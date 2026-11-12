// Synthetic estate source: a master graph plus two derived projections (ownership tree, workload dependencies),
// with projection definitions, ontologies, correspondences and a deterministic patch stream. Used for load tests.
import { readPresentation } from "./presentation";
import type { Patch, Snapshot } from "../core/model";
import {
  ESTATE_SIZES, estateOntology, estatePatches, generateEstate, ownershipTree, workloadDependencies, type EstateOptions,
} from "../core/synth";
import type { GraphDescriptorWire, OntologyDefinition, OntologyRef, Operator, ProjectionDefinition } from "../core/wire-types";
import type { Rejection } from "../core/validate";
import { ESTATE_DEFAULTS, ESTATE_METERS, allocTick, fillTick, type TelemetrySpec, type TelemetryTick } from "../core/synth-telemetry";
import { RejectionBus } from "./base";
import type { Correspondence, GraphInfo, GraphSource, SpaceInfo, StreamEvent, TelemetryBatch, TelemetryFeed } from "./types";
import type { Snapshot as Snap } from "../core/model";

const id = (value: string) => ({ value });
const ref = (o: OntologyDefinition): OntologyRef => o.reference;

function derivedOntology(name: string, entityTypes: string[], rel: { id: string; source: string[]; target: string[] }): OntologyDefinition {
  const props = ["name", "health", "region"].map((p) => ({ id: id(p), valueKind: "text" as const, nullable: false, itemKind: null, unit: null, role: "graphData" as const }));
  return {
    schemaVersion: "1.0",
    reference: { id: name, version: "1", digest: id(`sha256:synthetic-${name}`) },
    noEntityType: id("NoType"), noRelationshipType: id("NoType"),
    properties: props,
    entityTypes: [{ id: id("NoType"), properties: [] }, ...entityTypes.map((t) => ({ id: id(t), properties: ["name", "health", "region"].map(id) }))],
    relationshipTypes: [
      { id: id("NoType"), sourceTypes: [], targetTypes: [], properties: [] },
      { id: id(rel.id), sourceTypes: rel.source.map(id), targetTypes: rel.target.map(id), properties: [] },
    ],
  };
}
export const ownershipOntology = derivedOntology("ownership", ["Subscription", "Cluster", "Namespace", "Workload"], { id: "Contains", source: ["Subscription", "Cluster", "Namespace"], target: ["Cluster", "Namespace", "Workload"] });
export const dependenciesOntology = derivedOntology("dependencies", ["Workload"], { id: "DependsOn", source: ["Workload"], target: ["Workload"] });

const field = (name: string) => ({ kind: "field" as const, field: name });
const base = (pid: string, ont: OntologyRef): Omit<ProjectionDefinition, "operators" | "wires" | "outputs"> => ({
  schemaVersion: "1.0", id: id(pid), version: "1", ontology: ont,
  inputs: [{ name: "estate", contract: "sattle.graph", version: "1" }],
  scaffold: { entities: [], relationships: [] }, retainedInputsPerElement: 8, bidirectionalRules: [],
});

export function ownershipDefinition(): ProjectionDefinition {
  const operators: Operator[] = [];
  const wires: ProjectionDefinition["wires"] = [];
  const outputs: ProjectionDefinition["outputs"] = [];
  const levels: [string, string, string][] = [["Cluster", "Subscription", "sub-cl"], ["Namespace", "Cluster", "cl-ns"], ["Workload", "Namespace", "ns-wl"]];
  for (const [child, parent, key] of levels) {
    const props = child === "Cluster" ? ["name", "region", "health"] : ["name"];
    operators.push({ kind: "match", id: id(`match-${key}`), binding: "estate", pattern: { nodes: [{ variable: "c", type: id(child) }, { variable: "p", type: id(parent) }], edges: [{ variable: "m", source: "c", target: "p", type: id("MemberOf") }] } });
    operators.push({
      kind: "emitEntity", id: id(`emit-${key}`), identity: { domain: id("estate-entity"), canonicalizationVersion: "1", components: [{ field: "id", value: field("c") }] }, type: id(child),
      properties: props.map((p) => ({ property: id(p), value: field(`c.${p}`) })),
    });
    operators.push({
      kind: "emitRelationship", id: id(`contains-${key}`), identityRule: "parent-child", source: field("p"), target: field("c"), type: id("Contains"), properties: [], retypeCollision: "requireEqual",
    });
    wires.push({ from: id(`match-${key}`), output: "out", to: id(`emit-${key}`), input: "in" }, { from: id(`match-${key}`), output: "out", to: id(`contains-${key}`), input: "in" });
    outputs.push(id(`emit-${key}`), id(`contains-${key}`));
  }
  operators.push({ kind: "match", id: id("match-sub"), binding: "estate", pattern: { nodes: [{ variable: "s", type: id("Subscription") }], edges: [] } });
  operators.push({
    kind: "emitEntity", id: id("emit-sub"), identity: { domain: id("estate-entity"), canonicalizationVersion: "1", components: [{ field: "id", value: field("s") }] }, type: id("Subscription"),
    properties: [{ property: id("name"), value: field("s.name") }],
  });
  wires.push({ from: id("match-sub"), output: "out", to: id("emit-sub"), input: "in" });
  outputs.push(id("emit-sub"));
  return {
    ...base("ownership", ref(ownershipOntology)), operators, wires, outputs,
    bidirectionalRules: [{
      id: "rename-workload",
      sourcePattern: { nodes: [{ variable: "w", type: id("Workload") }], edges: [] },
      targetPattern: { nodes: [{ variable: "w", type: id("Workload") }], edges: [] },
      correspondences: [{ name: "same-workload", sourceVariables: ["w"], targetVariables: ["w"], complement: [] }],
      forward: { operators: [], wires: [], outputs: [] }, backward: { operators: [], wires: [], outputs: [] }, requiredLaws: ["getPut", "putGet"],
    }],
  };
}

export function dependenciesDefinition(): ProjectionDefinition {
  const operators: Operator[] = [
    {
      kind: "match", id: id("match-calls"), binding: "estate", pattern: {
        nodes: [{ variable: "a", type: id("Service") }, { variable: "b", type: id("Service") }, { variable: "wa", type: id("Workload") }, { variable: "wb", type: id("Workload") }],
        edges: [
          { variable: "ra", source: "a", target: "wa", type: id("Routes") }, { variable: "rb", source: "b", target: "wb", type: id("Routes") }, { variable: "call", source: "a", target: "b", type: id("Calls") },
        ],
      },
    },
    { kind: "distinct", id: id("distinct-pairs") },
    { kind: "emitEntity", id: id("emit-wl"), identity: { domain: id("estate-entity"), canonicalizationVersion: "1", components: [{ field: "id", value: field("wa") }] }, type: id("Workload"), properties: [{ property: id("name"), value: field("wa.name") }] },
    { kind: "emitRelationship", id: id("emit-dep"), identityRule: "workload-pair", source: field("wa"), target: field("wb"), type: id("DependsOn"), properties: [], retypeCollision: "requireEqual" },
  ];
  return {
    ...base("dependencies", ref(dependenciesOntology)), operators,
    wires: [
      { from: id("match-calls"), output: "out", to: id("distinct-pairs"), input: "in" },
      { from: id("distinct-pairs"), output: "out", to: id("emit-wl"), input: "in" },
      { from: id("distinct-pairs"), output: "out", to: id("emit-dep"), input: "in" },
    ],
    outputs: [id("emit-wl"), id("emit-dep")],
  };
}

const descriptor = (graph: string, projection: string, revision: string, ont: OntologyRef): GraphDescriptorWire => ({
  version: { tenant: id("synthetic"), graph: id(graph), revision: id(revision) }, projection: id(projection), ontology: ont, virtualRegions: [],
});

/** Entities of the first cluster: the blast radius of the synthetic incident, in every graph of the space. */
const inIncident = (id: string): boolean => /^(aks|vmss|vm|ns|wl|svc|pod)-0-0(-|$)/.test(id);

/** Telemetry spec of one graph of the estate: which edges carry flow, and which elements sit in the incident. */
export function telemetrySpec(g: Snap, seed: number): TelemetrySpec {
  const incidentNode = new Uint8Array(g.entities.length);
  g.entities.forEach((e, i) => { incidentNode[i] = inIncident(e.id) ? 1 : 0; });
  const edgeFlow = new Uint8Array(g.relationships.length), incidentEdge = new Uint8Array(g.relationships.length);
  g.relationships.forEach((r, i) => {
    edgeFlow[i] = r.type === "Calls" || r.type === "DependsOn" ? 1 : 0;
    incidentEdge[i] = inIncident(r.source) || inIncident(r.target) ? 1 : 0;
  });
  return { nodeCount: g.entities.length, edgeCount: g.relationships.length, edgeFlow, incidentNode, incidentEdge, seed };
}

/** Produces ticks in a worker in the browser and inline in tests; the page hands buffers back for reuse. */
export class TelemetryGenerator {
  private worker: Worker | null = null;
  private seq = 0;
  private waiting = new Map<number, (t: TelemetryTick) => void>();
  private inlineOut: TelemetryTick | null = null;
  constructor(private readonly spec: TelemetrySpec) {
    if (typeof Worker !== "undefined") {
      this.worker = new Worker(new URL("./telemetry-worker.ts", import.meta.url), { type: "module" });
      this.worker.postMessage({ type: "init", spec });
      this.worker.onmessage = (e: MessageEvent<{ id: number; data: TelemetryTick }>) => { const r = this.waiting.get(e.data.id); this.waiting.delete(e.data.id); r?.(e.data.data); };
    }
  }
  generate(tick: number): Promise<TelemetryTick> {
    if (!this.worker) {
      const out = this.inlineOut ?? allocTick(this.spec);
      fillTick(this.spec, tick, out);
      return Promise.resolve(out);
    }
    const id = ++this.seq;
    return new Promise((resolve) => { this.waiting.set(id, resolve); this.worker!.postMessage({ type: "tick", id, tick }); });
  }
  recycle(t: TelemetryTick): void { this.worker?.postMessage({ type: "recycle", tick: t }, [t.requests.buffer, t.errorRate.buffer, t.latencyMs.buffer, t.cpu.buffer, t.memory.buffer]); }
  dispose(): void { this.worker?.terminate(); }
}

export class SyntheticSource implements GraphSource {
  readonly name: string;
  readonly bus = new RejectionBus();
  private built: { info: SpaceInfo; snaps: Map<string, Snapshot>; patches: Map<string, Patch[]>; corr: Map<string, Correspondence[]> } | null = null;
  /** generation time in ms, filled by open() */
  generateMs = 0;
  telemetry: TelemetryFeed | undefined;
  private generators = new Map<string, TelemetryGenerator>();

  constructor(private readonly sizeName: string, private readonly patchCount = 200, private readonly opts: EstateOptions = ESTATE_SIZES[sizeName]) {
    this.name = `Synthetic estate (${sizeName})`;
  }

  async open(): Promise<SpaceInfo> {
    if (!this.built) {
      const t0 = performance.now();
      const estate = generateEstate(this.opts);
      const tree = ownershipTree(estate.snapshot);
      const deps = workloadDependencies(estate.snapshot, estate.serviceOf);
      const master = estate.snapshot;
      // a presentation hint in the engine's shape (PROTOCOL.md, version 3.2: value roles): the values of `health` mean ok, warning, danger
      const { hints } = readPresentation({ properties: [{ id: { value: "health" }, presentation: [{ value: { kind: "text", value: "healthy" }, role: "ok" }, { value: { kind: "text", value: "degraded" }, role: "warning" }, { value: { kind: "text", value: "unhealthy" }, role: "danger" }] }] });
      const graphs: GraphInfo[] = [
        { id: "master", role: "master", descriptor: descriptor("master", "ingestion", "1", ref(estateOntology)), ontology: estateOntology, inputs: [] },
        { id: "ownership", role: "projection", descriptor: descriptor("ownership", "ownership", "1", ref(ownershipOntology)), ontology: ownershipOntology, definition: ownershipDefinition(), inputs: ["master"], inputPorts: { estate: "master" } },
        { id: "dependencies", role: "projection", descriptor: descriptor("dependencies", "dependencies", "1", ref(dependenciesOntology)), ontology: dependenciesOntology, definition: dependenciesDefinition(), inputs: ["master"], inputPorts: { estate: "master" } },
      ].map((g) => ({ ...g, presentation: hints })) as GraphInfo[];
      const corr = new Map<string, Correspondence[]>();
      // ownership: each Contains edge comes from the MemberOf edge it mirrors; entities share ids with the master
      corr.set("ownership", tree.relationships.map((r) => ({ target: `e:${r.id}`, sources: [{ graph: "master", key: `e:MemberOf:${r.target}>${r.source}` }] })));
      corr.set("dependencies", [...deps.sources].map(([depId, calls]) => ({ target: `e:${depId}`, sources: calls.slice(0, 20).map((c) => ({ graph: "master", key: `e:${c}` })) })));
      this.built = {
        info: { id: `estate-${this.sizeName}`, graphs },
        snaps: new Map([["master", master], ["ownership", tree], ["dependencies", deps.snapshot]]),
        patches: new Map([["master", [...estatePatches(master, this.patchCount, 7)]], ["ownership", []], ["dependencies", []]]),
        corr,
      };
      this.generateMs = performance.now() - t0;
      for (const [id, snap] of this.built.snaps) this.generators.set(id, new TelemetryGenerator(telemetrySpec(snap, this.opts.seed)));
      const dt = 1;
      this.telemetry = {
        meters: ESTATE_METERS, defaults: ESTATE_DEFAULTS, dt, ticks: this.patchCount, maxTicks: 100000,
        fetch: async (graph, tick) => {
          const gen = this.generators.get(graph);
          if (!gen) return { batches: [] };
          const d = await gen.generate(tick);
          const mk = (target: "node" | "edge", property: keyof TelemetryTick): TelemetryBatch => ({ graph, tick, t: tick * dt, target, property, values: d[property] });
          return { batches: [mk("edge", "requests"), mk("edge", "errorRate"), mk("edge", "latencyMs"), mk("node", "cpu"), mk("node", "memory")], release: () => gen.recycle(d) };
        },
      };
    }
    return this.built.info;
  }

  async snapshot(graph: string): Promise<Snapshot> { await this.open(); return this.built!.snaps.get(graph)!; }

  subscribe(graph: string, sink: (e: StreamEvent) => void): () => void {
    let live = true;
    queueMicrotask(async () => {
      await this.open();
      for (const patch of this.built!.patches.get(graph) ?? []) { if (!live) return; sink({ type: "patch", patch }); }
      if (live) sink({ type: "end" });
    });
    return () => { live = false; };
  }

  async correspondences(graph: string): Promise<Correspondence[]> { await this.open(); return this.built!.corr.get(graph) ?? []; }
  onRejected(cb: (r: Rejection) => void): () => void { return this.bus.on(cb); }
}
