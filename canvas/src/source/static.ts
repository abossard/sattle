// Static JSON source. A manifest points at a GraphSpaceDefinition plus, per graph, an ontology, a snapshot and patches.
// Every record is validated against the contract schemas; rejected records are reported, never dropped silently.
import type { Patch, Snapshot } from "../core/model";
import { entityFromWire, patchFromWire, relFromWire, versionFromWire } from "../core/wire";
import {
  checkDescriptor, checkEntityState, checkGraphSpace, checkOntology, checkPatch, checkProjection, checkRelationshipState, type Rejection,
} from "../core/validate";
import type {
  EntityState, GraphDescriptorWire, GraphExtentWire, GraphPatchWire, GraphSpaceDefinition, OntologyDefinition, ProjectionDefinition, RelationshipState,
} from "../core/wire-types";
import { RejectionBus, evaluateUnavailable } from "./base";
import { parseTelemetryNdjson } from "./telemetry-file";
import { parseLineage, parseMeters, parseReferences, parseReverseLineage } from "./engine-protocol";
import { readPresentation, type PresentationHint, type TreeSpec } from "./presentation";
import { parsePatch, parseRecords, parseSnapshot } from "./snapshot-file";
import type { ChannelDefaults, Correspondence, DocumentText, EvaluateResult, GraphInfo, GraphSource, Lineage, Meter, Reference, ReverseLineage, SpaceInfo, StreamEvent, TelemetryBatch, TelemetryFeed } from "./types";

export interface Manifest {
  /** GraphSpaceDefinition file, relative to the manifest */
  space: string;
  graphs: Record<string, {
    ontology: string; snapshot: string; patches?: string; correspondences?: string;
    /** engine 3.11 `replay --out`: forward and reverse lineage (NDJSON, one record per element), and for a document graph its Markdown and the JSON with revision and digest */
    lineage?: string; lineageReverse?: string; document?: string; documentJson?: string;
    /** one line on what the graph is (the projection's purpose), when the engine wrote it */
    description?: string;
    /** engine 3.12 `replay --out`: the references (NDJSON), and the telemetry declarations (the body of `GET /graphs/{g}/meters`); the telemetry file is `NAME.telemetry.ndjson` next to them */
    references?: string; meters?: string; telemetry?: string;
  }>;
  /** optional telemetry: meters and a sparse NDJSON file, see telemetry-file.ts */
  telemetry?: { meters: Meter[]; defaults: ChannelDefaults; dt: number; file: string };
}

interface SnapshotFile { descriptor: GraphDescriptorWire; extent: GraphExtentWire }

export class StaticSource implements GraphSource {
  readonly bus = new RejectionBus();
  private manifest: Manifest | null = null;
  private info: SpaceInfo | null = null;
  private snapshots = new Map<string, Snapshot>();
  private patches = new Map<string, Patch[]>();
  telemetry: TelemetryFeed | undefined;

  constructor(readonly name: string, private readonly manifestUrl: string, private readonly fetcher: (url: string) => Promise<string> = defaultFetch) {}

  private url(rel: string): string { return new URL(rel, new URL(this.manifestUrl, globalThis.location?.href ?? "http://localhost/")).href; }
  private async json(rel: string): Promise<unknown> { return JSON.parse(await this.fetcher(this.url(rel))); }
  private reject(source: string, where: string, errors: string[]): void { this.bus.emit({ source, where, errors } satisfies Rejection); }

  async open(): Promise<SpaceInfo> {
    if (this.info) return this.info;
    const manifest = JSON.parse(await this.fetcher(this.url(""))) as Manifest;
    this.manifest = manifest;
    const spaceRaw = await this.json(manifest.space);
    const space = checkGraphSpace(spaceRaw);
    if (!space.ok) { this.reject(manifest.space, "graph space", space.errors); throw new Error(`graph space rejected: ${space.errors.join("; ")}`); }
    const def: GraphSpaceDefinition = space.value;
    const graphs: GraphInfo[] = [];
    const projectionGraph = new Map(def.projections.map((p) => [p.projection.id.value, p.outputGraph.value]));
    const entries: { id: string; role: "master" | "projection"; definition: ProjectionDefinition; inputs: string[]; ports?: Record<string, string> }[] = [
      { id: def.master.graph.value, role: "master", definition: def.master.ingestionProjection, inputs: [] },
      ...def.projections.map((p) => ({
        id: p.outputGraph.value, role: "projection" as const, definition: p.projection,
        inputs: p.graphInputs.map((i) => (i.source.kind === "master" ? def.master.graph.value : projectionGraph.get(i.source.projection.value) ?? i.source.projection.value)),
        ports: Object.fromEntries(p.graphInputs.map((i) => [i.port, i.source.kind === "master" ? def.master.graph.value : projectionGraph.get(i.source.projection.value) ?? i.source.projection.value])),
      })),
    ];
    for (const e of entries) {
      const files = manifest.graphs[e.id];
      if (!files) { this.reject(this.manifestUrl, `graph ${e.id}`, ["graph is in the space definition but has no data files in the manifest"]); continue; }
      const loaded = await this.loadOntology(files.ontology);
      const ontology = loaded?.ontology ?? null;
      const snap = await this.loadSnapshot(files.snapshot);
      if (!ontology || !snap) continue;
      this.snapshots.set(e.id, snap.snapshot);
      graphs.push({ id: e.id, role: e.role, descriptor: snap.descriptor, ontology, presentation: loaded!.hints.length ? loaded!.hints : undefined, tree: loaded!.tree ?? undefined, definition: e.definition, inputs: e.inputs, inputPorts: e.ports, description: files.description || undefined, document: files.document && files.documentJson ? true : undefined });
      if (files.patches) this.patches.set(e.id, await this.loadPatches(files.patches));
    }
    if (manifest.telemetry) {
      const t = manifest.telemetry;
      const batches = parseTelemetryNdjson(await this.fetcher(this.url(t.file)), t.file, (r) => this.bus.emit(r));
      const byTick = new Map<string, TelemetryBatch[]>();
      let last = 0;
      for (const b of batches) { const k = `${b.graph}|${b.tick}`; byTick.set(k, [...(byTick.get(k) ?? []), b]); last = Math.max(last, b.tick); }
      this.telemetry = { meters: t.meters, defaults: t.defaults, dt: t.dt, ticks: last, maxTicks: last, fetch: async (graph, tick) => ({ batches: byTick.get(`${graph}|${tick}`) ?? [] }) };
    }
    this.info = { id: def.id, graphs };
    return this.info;
  }

  /** The ontology and its value roles (engine 3.2): the roles are read out first, the contract schema forbids them in the ontology. */
  private async loadOntology(rel: string): Promise<{ ontology: OntologyDefinition; hints: PresentationHint[]; tree: TreeSpec | null } | null> {
    const { hints, tree, ontology } = readPresentation(await this.json(rel));
    const r = checkOntology(ontology);
    if (!r.ok) { this.reject(rel, "ontology", r.errors); return null; }
    return { ontology: r.value, hints, tree };
  }

  private async loadSnapshot(rel: string): Promise<{ snapshot: Snapshot; descriptor: GraphDescriptorWire } | null> {
    return parseSnapshot(await this.json(rel), rel, (r) => this.bus.emit(r));
  }

  private async loadPatches(rel: string): Promise<Patch[]> {
    const items = parseRecords(await this.fetcher(this.url(rel)), rel, rel.endsWith(".ndjson"), (r) => this.bus.emit(r));
    const out: Patch[] = [];
    items.forEach((raw, i) => { const p = parsePatch(raw, rel, `patch #${i + 1}`, (r) => this.bus.emit(r)); if (p) out.push(p); });
    return out;
  }

  async snapshot(graph: string): Promise<Snapshot> {
    await this.open();
    const s = this.snapshots.get(graph);
    if (!s) throw new Error(`unknown graph ${graph}`);
    return s;
  }

  subscribe(graph: string, sink: (e: StreamEvent) => void): () => void {
    let live = true;
    queueMicrotask(async () => {
      await this.open();
      for (const patch of this.patches.get(graph) ?? []) { if (!live) return; sink({ type: "patch", patch }); }
      // the telemetry windows of the graph, as the engine source delivers them: after the history, as stream events
      const file = this.telemetryFile(graph);
      if (file && live) for (const batch of parseTelemetryNdjson(await this.fetcher(this.url(file)), file, (r) => this.bus.emit(r))) { if (!live) return; sink({ type: "telemetry", batch }); }
      if (live) sink({ type: "end" });
    });
    return () => { live = false; };
  }

  async correspondences(graph: string): Promise<Correspondence[]> {
    await this.open();
    const rel = this.manifest?.graphs[graph]?.correspondences;
    if (!rel) return [];
    const raw = await this.json(rel);
    if (!Array.isArray(raw)) { this.reject(rel, "correspondences", ["expected an array"]); return []; }
    const ok: Correspondence[] = [];
    raw.forEach((c: unknown, i) => {
      const x = c as Correspondence;
      if (x && typeof x.target === "string" && Array.isArray(x.sources) && x.sources.every((s) => typeof s?.graph === "string" && typeof s?.key === "string")) ok.push(x);
      else this.reject(rel, `correspondence #${i}`, ["expected {target, sources: [{graph, key}]}"]);
    });
    return ok;
  }

  private telemetryFile(graph: string): string | undefined {
    const f = this.manifest?.graphs[graph];
    return f?.telemetry ?? f?.meters?.replace(/\.meters\.json$/, ".telemetry.ndjson");
  }
  /** The telemetry declarations of a graph (ranges, units, thresholds) when the output has them. */
  async meters(graph: string): Promise<{ meters: Meter[]; defaults: Partial<ChannelDefaults> } | null> {
    await this.open();
    const rel = this.manifest?.graphs[graph]?.meters;
    if (!rel) return null;
    const m = parseMeters(await this.json(rel));
    return m.meters.length ? m : null;
  }
  /** The endpoints the graph's relationships name and it does not hold, from the file the engine wrote. */
  async references(graph: string): Promise<Reference[]> {
    await this.open();
    const rel = this.manifest?.graphs[graph]?.references;
    if (!rel) return [];
    return parseReferences(parseRecords(await this.fetcher(this.url(rel)), rel, true, (r) => this.bus.emit(r)));
  }

  /** Lineage records by element, read once per graph from the NDJSON the engine wrote. */
  private lineages = new Map<string, Map<string, Lineage>>();
  private reverses = new Map<string, Map<string, ReverseLineage>>();
  private async records<T>(cache: Map<string, Map<string, T>>, graph: string, rel: string | undefined, parse: (raw: unknown) => T | null, key: (r: T) => string): Promise<Map<string, T>> {
    await this.open();
    let m = cache.get(graph);
    if (m) return m;
    m = new Map();
    cache.set(graph, m);
    if (!rel) return m;
    for (const raw of parseRecords(await this.fetcher(this.url(rel)), rel, true, (r) => this.bus.emit(r))) { const r = parse(raw); if (r) m.set(key(r), r); }
    return m;
  }
  async lineage(graph: string, element: string): Promise<Lineage | null> {
    return (await this.records(this.lineages, graph, this.manifest?.graphs[graph]?.lineage, parseLineage, (l) => l.element)).get(element) ?? null;
  }
  async reverseLineage(graph: string, element: string): Promise<ReverseLineage | null> {
    return (await this.records(this.reverses, graph, this.manifest?.graphs[graph]?.lineageReverse, parseReverseLineage, (l) => l.element)).get(element) ?? null;
  }

  /** The rendered document of a document graph: the file holds the state at the head only, so an earlier revision is refused rather than shown as another. */
  async document(graph: string, revision?: string): Promise<DocumentText | null> {
    await this.open();
    const files = this.manifest?.graphs[graph];
    if (!files?.document || !files.documentJson) return null;
    const meta = (await this.json(files.documentJson)) as { revision?: unknown; digest?: unknown };
    const head = String(meta.revision ?? "");
    if (revision !== undefined && revision !== head) throw new Error(`this source holds the document of ${graph} at revision ${head} only`);
    return { revision: head, digest: String(meta.digest ?? ""), markdown: await this.fetcher(this.url(files.document)) };
  }

  evaluate(_def: ProjectionDefinition): Promise<EvaluateResult> { return evaluateUnavailable(); }
  onRejected(cb: (r: Rejection) => void): () => void { return this.bus.on(cb); }
}

async function defaultFetch(url: string): Promise<string> {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`${url}: HTTP ${res.status}`);
  return res.text();
}
