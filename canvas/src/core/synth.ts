// Deterministic synthetic cloud estate for load tests: subscription > cluster > namespace > workload > pod,
// plus services, VM nodes and service-to-service flows. Same seed and options always give the same data.
import { Graph } from "./graph";
import type { Edit, Entity, Patch, Props, Rel, Snapshot, Version } from "./model";
import type { OntologyDefinition } from "./wire-types";

export interface EstateOptions {
  seed: number;
  subscriptions: number;
  clustersPerSubscription: number;
  namespacesPerCluster: number;
  workloadsPerNamespace: number;
  podsPerWorkload: number;      // mean
  nodesPerCluster: number;
  flowsPerService: number;      // mean outgoing Calls per service
}

export const ESTATE_SIZES: Record<string, EstateOptions> = {
  small: { seed: 1, subscriptions: 1, clustersPerSubscription: 2, namespacesPerCluster: 4, workloadsPerNamespace: 6, podsPerWorkload: 3, nodesPerCluster: 6, flowsPerService: 3 },
  medium: { seed: 1, subscriptions: 3, clustersPerSubscription: 3, namespacesPerCluster: 8, workloadsPerNamespace: 12, podsPerWorkload: 5, nodesPerCluster: 40, flowsPerService: 3 },
  // about 100k nodes and 300k edges
  "100k": { seed: 1, subscriptions: 8, clustersPerSubscription: 4, namespacesPerCluster: 12, workloadsPerNamespace: 25, podsPerWorkload: 8, nodesPerCluster: 90, flowsPerService: 12 },
};

export function mulberry32(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const text = (value: string) => ({ kind: "text" as const, value });
const HEALTH = ["healthy", "healthy", "healthy", "healthy", "healthy", "healthy", "healthy", "degraded", "unhealthy"];
const REGIONS = ["westeurope", "northeurope", "eastus", "swedencentral", "uksouth", "japaneast"];

export interface Estate {
  snapshot: Snapshot;
  /** workload entity id -> pod ids; service entity id -> workload id (used for derived projections) */
  podsOf: Map<string, string[]>;
  serviceOf: Map<string, string>;
}

export function generateEstate(o: EstateOptions): Estate {
  const rnd = mulberry32(o.seed);
  const pick = <T,>(a: T[]): T => a[Math.floor(rnd() * a.length)];
  const entities: Entity[] = [];
  const rels: Rel[] = [];
  const podsOf = new Map<string, string[]>();
  const serviceOf = new Map<string, string>();
  const ent = (id: string, type: string, props: Props) => { entities.push({ id, type, props }); };
  const link = (type: string, s: string, t: string, props: Props = {}) => { rels.push({ id: `${type}:${s}>${t}`, source: s, target: t, type, props }); };

  const services: string[] = [];
  const nsServices: string[][] = [];
  const clusterServices: string[][] = [];
  for (let s = 0; s < o.subscriptions; s++) {
    const sub = `sub-${s}`;
    ent(sub, "Subscription", { name: text(`subscription-${s}`) });
    for (let c = 0; c < o.clustersPerSubscription; c++) {
      const cl = `aks-${s}-${c}`;
      ent(cl, "Cluster", { name: text(`aks-${s}-${c}`), region: text(REGIONS[(s + c) % REGIONS.length]), health: text(pick(HEALTH)) });
      link("MemberOf", cl, sub);
      const vms: string[] = [];
      for (let v = 0; v < o.nodesPerCluster; v++) {
        const vm = `vm-${s}-${c}-${v}`;
        ent(vm, "Node", { name: text(`vmss-${s}-${c}-${v}`), health: text(pick(HEALTH)) });
        link("MemberOf", vm, cl);
        vms.push(vm);
      }
      const cs: string[] = [];
      clusterServices.push(cs);
      for (let n = 0; n < o.namespacesPerCluster; n++) {
        const ns = `ns-${s}-${c}-${n}`;
        ent(ns, "Namespace", { name: text(`team-${n}`) });
        link("MemberOf", ns, cl);
        const nsl: string[] = [];
        for (let w = 0; w < o.workloadsPerNamespace; w++) {
          const wl = `wl-${s}-${c}-${n}-${w}`;
          const svc = `svc-${s}-${c}-${n}-${w}`;
          ent(wl, "Workload", { name: text(`app-${n}-${w}`) });
          link("MemberOf", wl, ns);
          ent(svc, "Service", { name: text(`svc-${n}-${w}`), health: text(pick(HEALTH)) });
          link("MemberOf", svc, ns);
          link("Routes", svc, wl);
          serviceOf.set(svc, wl);
          const podCount = Math.max(1, Math.round(o.podsPerWorkload * (0.4 + rnd() * 1.2)));
          const pods: string[] = [];
          for (let p = 0; p < podCount; p++) {
            const pod = `pod-${s}-${c}-${n}-${w}-${p}`;
            ent(pod, "Pod", { name: text(`app-${n}-${w}-${p}`), health: text(pick(HEALTH)) });
            link("MemberOf", pod, wl);
            link("HostedOn", pod, pick(vms));
            pods.push(pod);
          }
          podsOf.set(wl, pods);
          nsl.push(svc);
          cs.push(svc);
          services.push(svc);
        }
        nsServices.push(nsl);
      }
    }
  }
  // flows: mostly inside the namespace, some inside the cluster, a few anywhere
  const seen = new Set<string>();
  for (const svc of services) {
    const parts = svc.split("-"); // svc-s-c-n-w
    const outgoing = Math.max(0, Math.round(o.flowsPerService * (0.3 + rnd() * 1.4)));
    for (let i = 0; i < outgoing; i++) {
      const r = rnd();
      const cluster = clusterServices[Number(parts[1]) * o.clustersPerSubscription + Number(parts[2])];
      const target = r < 0.6
        ? nsServices[(Number(parts[1]) * o.clustersPerSubscription + Number(parts[2])) * o.namespacesPerCluster + Number(parts[3])][Math.floor(rnd() * o.workloadsPerNamespace)]
        : r < 0.92 ? pick(cluster) : pick(services);
      if (target === svc) continue;
      const id = `Calls:${svc}>${target}`;
      if (seen.has(id)) continue;
      seen.add(id);
      link("Calls", svc, target, { health: text(pick(HEALTH)) });
    }
  }
  return {
    snapshot: { version: { tenant: "synthetic", graph: "master", revision: "1" }, entities, relationships: rels },
    podsOf,
    serviceOf,
  };
}

export const estateOntology: OntologyDefinition = (() => {
  const prop = (id: string, valueKind: "text" | "integer" | "decimal", role: "graphData" | "telemetry" = "graphData", unit: string | null = null) =>
    ({ id: { value: id }, valueKind, nullable: false, itemKind: null, unit, role });
  const ids = (...a: string[]) => a.map((value) => ({ value }));
  return {
    schemaVersion: "1.0",
    reference: { id: "estate", version: "1", digest: { value: "sha256:synthetic-estate" } },
    noEntityType: { value: "NoType" },
    noRelationshipType: { value: "NoType" },
    properties: [
      prop("name", "text"), prop("health", "text"), prop("region", "text"),
      prop("requests", "decimal", "telemetry", "1/s"), prop("errorRate", "decimal", "telemetry"), prop("latencyMs", "decimal", "telemetry", "ms"),
      prop("cpu", "decimal", "telemetry"), prop("memory", "decimal", "telemetry"),
    ],
    entityTypes: [
      { id: { value: "NoType" }, properties: [] },
      { id: { value: "Subscription" }, properties: ids("name") },
      { id: { value: "Cluster" }, properties: ids("name", "region", "health") },
      { id: { value: "Node" }, properties: ids("name", "health", "cpu", "memory") },
      { id: { value: "Namespace" }, properties: ids("name") },
      { id: { value: "Workload" }, properties: ids("name") },
      { id: { value: "Service" }, properties: ids("name", "health", "cpu", "memory") },
      { id: { value: "Pod" }, properties: ids("name", "health", "cpu", "memory") },
    ],
    relationshipTypes: [
      { id: { value: "NoType" }, sourceTypes: [], targetTypes: [], properties: [] },
      { id: { value: "MemberOf" }, sourceTypes: ids("Cluster", "Node", "Namespace", "Workload", "Service", "Pod"), targetTypes: ids("Subscription", "Cluster", "Namespace", "Workload"), properties: [] },
      { id: { value: "HostedOn" }, sourceTypes: ids("Pod"), targetTypes: ids("Node"), properties: [] },
      { id: { value: "Routes" }, sourceTypes: ids("Service"), targetTypes: ids("Workload"), properties: [] },
      { id: { value: "Calls" }, sourceTypes: ids("Service"), targetTypes: ids("Service"), properties: ids("health", "requests", "errorRate", "latencyMs") },
    ],
  };
})();

/** Ownership tree projection: Subscription > Cluster > Namespace > Workload, same entity ids as the master. */
export function ownershipTree(master: Snapshot): Snapshot {
  const keep = new Set(["Subscription", "Cluster", "Namespace", "Workload"]);
  const entities = master.entities.filter((e) => keep.has(e.type));
  const ids = new Set(entities.map((e) => e.id));
  const relationships: Rel[] = [];
  for (const r of master.relationships) {
    if (r.type === "MemberOf" && ids.has(r.source) && ids.has(r.target)) relationships.push({ ...r, id: `Contains:${r.target}>${r.source}`, source: r.target, target: r.source, type: "Contains" });
  }
  return { version: { tenant: "synthetic", graph: "ownership", revision: "1" }, entities, relationships };
}

/** Workload dependency projection: Calls between services aggregated onto their workloads (derived, DAG-like). */
export function workloadDependencies(master: Snapshot, serviceOf: Map<string, string>): { snapshot: Snapshot; sources: Map<string, string[]> } {
  const wl = new Map(master.entities.filter((e) => e.type === "Workload").map((e) => [e.id, e]));
  const deps = new Map<string, Rel>();
  const sources = new Map<string, string[]>();
  for (const r of master.relationships) {
    if (r.type !== "Calls") continue;
    const a = serviceOf.get(r.source), b = serviceOf.get(r.target);
    if (!a || !b || a === b) continue;
    const id = `DependsOn:${a}>${b}`;
    if (!deps.has(id)) { deps.set(id, { id, source: a, target: b, type: "DependsOn", props: {} }); sources.set(id, []); }
    sources.get(id)!.push(r.id);
  }
  const used = new Set<string>();
  for (const d of deps.values()) { used.add(d.source); used.add(d.target); }
  const entities = [...used].sort().map((id) => wl.get(id)!);
  return { snapshot: { version: { tenant: "synthetic", graph: "dependencies", revision: "1" }, entities, relationships: [...deps.values()] }, sources };
}

/** A deterministic stream of valid patches (health flips, pods added and removed, flows added and removed). */
export function* estatePatches(master: Snapshot, count: number, seed: number): Generator<Patch> {
  const rnd = mulberry32(seed);
  const g = Graph.fromSnapshot(master);
  const pods = master.entities.filter((e) => e.type === "Pod").map((e) => e.id);
  const services = master.entities.filter((e) => e.type === "Service").map((e) => e.id);
  const nodes = master.entities.filter((e) => e.type === "Node").map((e) => e.id);
  const workloads = master.entities.filter((e) => e.type === "Workload").map((e) => e.id);
  const pick = <T,>(a: T[]): T => a[Math.floor(rnd() * a.length)];
  let rev = Number(master.version.revision);
  let serial = 0;
  for (let i = 0; i < count; i++) {
    const edits: Edit[] = [];
    const r = rnd();
    if (r < 0.5) {
      for (let j = 0; j < 3; j++) {
        const id = pick(rnd() < 0.5 ? pods : services);
        const e = g.entity(id);
        if (!e || edits.some((x) => x.kind === "entity" && x.id === id)) continue;
        const cur = e.props.health?.kind === "text" ? e.props.health.value : "healthy";
        const next = cur === "healthy" ? pick(["degraded", "unhealthy"]) : "healthy";
        edits.push({ kind: "entity", id, before: e, after: { ...e, props: { ...e.props, health: text(next) } } });
      }
    } else if (r < 0.7) {
      const wl = pick(workloads);
      const id = `pod-new-${seed}-${serial++}`;
      const pod: Entity = { id, type: "Pod", props: { name: text(id), health: text("healthy") } };
      edits.push({ kind: "entity", id, before: null, after: pod });
      const host = pick(nodes);
      for (const rel of [
        { id: `MemberOf:${id}>${wl}`, source: id, target: wl, type: "MemberOf", props: {} },
        { id: `HostedOn:${id}>${host}`, source: id, target: host, type: "HostedOn", props: {} },
      ]) edits.push({ kind: "relationship", id: rel.id, before: null, after: rel });
    } else if (r < 0.85) {
      const id = pick(pods);
      const e = g.entity(id);
      if (e) {
        const slot = g.nodeSlot.get(id)!;
        for (const es of [...g.out[slot], ...g.inn[slot]]) { const rr = g.edges[es]!; edits.push({ kind: "relationship", id: rr.id, before: rr, after: null }); }
        edits.push({ kind: "entity", id, before: e, after: null });
      }
    } else {
      const a = pick(services), b = pick(services);
      const id = `Calls:${a}>${b}`;
      const ex = g.rel(id);
      if (a !== b && !ex) edits.push({ kind: "relationship", id, before: null, after: { id, source: a, target: b, type: "Calls", props: { health: text("healthy") } } });
      else if (ex) edits.push({ kind: "relationship", id, before: ex, after: null });
    }
    if (edits.length === 0) continue;
    const from: Version = { tenant: "synthetic", graph: "master", revision: String(rev) };
    const to: Version = { ...from, revision: String(rev + 1) };
    const patch: Patch = { from, to, projection: "ingestion", profile: "extensionalFinite", edits, cause: { inputs: [`input-${i}`], configurationVersion: "1" } };
    const res = g.apply(patch);
    if (!res.ok) continue;
    rev++;
    yield patch;
  }
}
