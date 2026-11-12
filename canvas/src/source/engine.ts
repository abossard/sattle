// EngineSource: the GraphSource seam over the Sattle engine, protocol version 3 (engine/PROTOCOL.md).
// History is read over HTTP from immutable, revision-addressed resources (the browser caches them), then one WebSocket follows the
// ordered event stream with `subscribe {graphs, after: seq}`. The connection reconnects with backoff and resumes from the last
// sequence number seen; anything the stream skipped is read back over HTTP. Invalid records are reported, never dropped silently.
import type { Patch, Snapshot } from "../core/model";
import { checkOntology, type Rejection } from "../core/validate";
import { RejectionBus } from "./base";
import {
  ENGINE_ENDPOINTS, batchFromWire, instantSeconds, parseLineage, parseReferencePage, parseReverseLineage, parseMeters, parseModules, parseSpace, parseWsMessage, subscribeMessage,
  type SpaceDoc,
} from "./engine-protocol";
import { initialStream, transition, type Effect, type Event as StreamInput, type StreamState, type Why } from "./engine-stream";
import { readPresentation } from "./presentation";
import { parsePatch, parseRecords, parseSnapshot } from "./snapshot-file";
import type {
  ConnectionStatus, DefinitionService, DocumentText, GraphInfo, GraphSource, Lineage, Reference, ReverseLineage, ModuleText, SpaceInfo, StreamEvent, TelemetryBatch,
} from "./types";

export interface EngineOptions {
  fetch?: typeof fetch;
  WebSocket?: typeof WebSocket;
  /** reconnect delays in ms; the last one repeats */
  backoff?: number[];
}

type Sink = (e: StreamEvent) => void;
const num = (r: string): number => Number(r);

export class EngineSource implements GraphSource {
  readonly name: string;
  readonly bus = new RejectionBus();
  readonly status: ConnectionStatus = { state: "connecting", detail: "not connected", gapsFilled: 0, reconnects: 0 };
  catalogGraph: string | undefined;
  private readonly base: string;
  private doc: SpaceDoc | null = null;
  private info: SpaceInfo | null = null;
  private snaps = new Map<string, { snapshot: Snapshot; descriptor: GraphInfo["descriptor"] }>();
  private sinks = new Map<string, Set<Sink>>();
  /** head revision of each graph when the space was opened: history up to it is read from immutable URLs */
  private heads = new Map<string, string>();
  /** what the stream is: see engine-stream.ts */
  private stream: StreamState = initialStream([], undefined);
  private retryTimers = new Set<ReturnType<typeof setTimeout>>();
  /** graphs whose immutable history up to the opening head was read */
  private filled = new Set<string>();
  private ws: WebSocket | null = null;
  private closed = false;
  private retry = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private listeners = new Set<() => void>();

  /** `base` may be relative (a dev proxy such as /engine) or absolute. */
  constructor(base: string, private readonly opts: EngineOptions = {}) {
    this.base = new URL(base, globalThis.location?.href ?? "http://localhost/").href.replace(/\/$/, "");
    this.name = `Engine ${base}`;
  }

  get connection(): { status: ConnectionStatus; onChange(cb: () => void): () => void } {
    return { status: this.status, onChange: (cb) => { this.listeners.add(cb); return () => this.listeners.delete(cb); } };
  }
  get space(): SpaceDoc | null { return this.doc; }

  private f(url: string, init?: RequestInit): Promise<Response> { return (this.opts.fetch ?? fetch)(url, init); }
  private url(path: string): string { return this.base + path; }
  private reject(r: Rejection): void { this.bus.emit(r); }
  private setStatus(state: ConnectionStatus["state"], detail: string): void {
    this.status.state = state; this.status.detail = detail;
    for (const l of this.listeners) l();
  }

  private async get(path: string): Promise<Response> {
    try { return await this.f(this.url(path)); } catch (e) { throw new Error(`engine not reachable at ${this.base}: ${(e as Error).message}`); }
  }
  private async text(path: string): Promise<string> {
    const res = await this.get(path);
    if (!res.ok) throw new Error(`${path}: HTTP ${res.status} ${(await res.text()).slice(0, 160)}`);
    return res.text();
  }

  // ---- space and snapshots ----
  async open(): Promise<SpaceInfo> {
    if (this.info) return this.info;
    this.doc = parseSpace(JSON.parse(await this.text(ENGINE_ENDPOINTS.space)));
    for (const g of this.doc.graphs) this.heads.set(g.id, g.head);
    const graphs: GraphInfo[] = [];
    const seed: { id: string; revision: string | undefined; telemetry: boolean }[] = [];
    for (const g of this.doc.graphs) {
      const { hints, tree, kinds, labels, ontology: rawOntology } = readPresentation(JSON.parse(await this.text(ENGINE_ENDPOINTS.ontology(g.id))));
      const ont = checkOntology(rawOntology);
      if (!ont.ok) {
        this.reject({ source: ENGINE_ENDPOINTS.ontology(g.id), where: `ontology of ${g.id}`, errors: ont.errors });
        // The kernel ontology uses valueKind "any", which OntologyDefinition.schema.json does not allow. The rejection stays
        // visible; the catalog graph is still loaded with the ontology as sent, because definitions are the point of this source.
        if (g.role !== "catalog") continue;
      }
      const snap = await this.genesis(g.id);
      if (!snap) continue;
      graphs.push({
        id: g.id, role: g.role, descriptor: snap.descriptor, ontology: (ont.ok ? ont.value : rawOntology) as GraphInfo["ontology"], presentation: hints.length ? hints : undefined, tree: tree ?? undefined, edgeKinds: Object.keys(kinds).length ? kinds : undefined, labels: Object.keys(labels).length ? labels : undefined, description: g.description, inputs: g.inputs, direction: g.direction, editable: g.editable, document: g.document || undefined,
        projectionName: g.projection ?? undefined,
      });
      seed.push({ id: g.id, revision: snap.snapshot.version.revision, telemetry: g.role !== "catalog" });
    }
    // the stream resumes after the sequence number of the space document; history read later overlaps it and duplicates are dropped by revision
    this.stream = initialStream(seed, this.doc.seq);
    this.catalogGraph = graphs.find((g) => g.role === "catalog")?.id;
    this.info = { id: this.doc.space, graphs };
    return this.info;
  }

  /** The empty state at revision 0, so the timeline starts at the beginning; the head when the log no longer reaches back. */
  private async genesis(graph: string) {
    const cached = this.snaps.get(graph);
    if (cached) return cached;
    let res = await this.get(ENGINE_ENDPOINTS.snapshot(graph, "0"));
    if (res.status === 410) res = await this.get(ENGINE_ENDPOINTS.snapshot(graph));
    if (!res.ok) { this.reject({ source: ENGINE_ENDPOINTS.snapshot(graph), where: `snapshot of ${graph}`, errors: [`HTTP ${res.status}`] }); return null; }
    const parsed = parseSnapshot(JSON.parse(await res.text()), ENGINE_ENDPOINTS.snapshot(graph), (r) => this.reject(r));
    if (parsed) this.snaps.set(graph, parsed);
    return parsed;
  }

  async snapshot(graph: string): Promise<Snapshot> {
    await this.open();
    const s = await this.genesis(graph);
    if (!s) throw new Error(`no snapshot for ${graph}`);
    return s.snapshot;
  }

  async meters(graph: string) {
    try { const m = parseMeters(JSON.parse(await this.text(ENGINE_ENDPOINTS.meters(graph)))); return m.meters.length ? m : null; } catch { return null; }
  }

  // ---- stream: the shell. What happens is `transition` (engine-stream.ts); this reads, sends and tells the sinks. ----
  subscribe(graph: string, sink: Sink): () => void {
    let set = this.sinks.get(graph);
    if (!set) this.sinks.set(graph, (set = new Set()));
    set.add(sink);
    this.step({ t: "subscribed", graph });
    return () => { set!.delete(sink); };
  }

  private emit(graph: string, e: StreamEvent): void { for (const s of this.sinks.get(graph) ?? []) s(e); }

  /** Fold one event into the stream state and run what the transition asks for. */
  private step(e: StreamInput): void {
    const r = transition(this.stream, e);
    this.stream = r.state;
    for (const f of r.effects) this.run(f);
  }

  private run(f: Effect): void {
    switch (f.k) {
      case "emit": this.emit(f.graph, f.event); return;
      case "status": if (f.gapFilled) this.status.gapsFilled++; this.setStatus(f.state ?? this.status.state, f.detail); return;
      case "reject": this.reject({ source: this.base, where: f.where, errors: f.errors }); return;
      case "openSocket": this.ensureSocket(); return;
      case "closeSocket": this.ws?.close(); return;
      case "retry": {
        const delays = this.opts.backoff ?? [500, 1000, 2000, 5000];
        const t = setTimeout(() => { this.retryTimers.delete(t); this.step({ t: "retry", graph: f.graph, gen: f.gen }); }, delays[Math.min(f.attempt - 1, delays.length - 1)]);
        this.retryTimers.add(t);
        return;
      }
      case "readPatches": void this.readPatches(f.graph, f.gen, f.why, f.after); return;
      case "readTelemetry": void this.readTelemetry(f.graph, f.gen, f.why, f.from); return;
      case "readMeta": void this.readMeta(f.graph, f.gen); return;
    }
  }

  /** Patches after a revision, parsed; one that does not continue the log is for the transition to judge, a record that is not a patch is reported here. */
  private async readPatches(graph: string, gen: number, why: Why, after: string): Promise<void> {
    try {
      const head = this.heads.get(graph);
      // up to the head seen at open: immutable, so cached; later reads use the moving head
      const path = ENGINE_ENDPOINTS.patches(graph, after, head !== undefined && num(after) < num(head) && !this.filled.has(graph) ? head : undefined);
      this.filled.add(graph);
      const patches: Patch[] = [];
      for (const [i, raw] of parseRecords(await this.text(path), path, true, (r) => this.reject(r)).entries()) {
        const p = parsePatch(raw, path, `patch #${i + 1}`, (r) => this.reject(r));
        if (p) patches.push(p);
      }
      this.step({ t: "patches", graph, gen, why, patches });
    } catch (e) { this.step({ t: "readFailed", graph, gen, why, error: (e as Error).message }); }
  }

  /** Telemetry windows from the last window seen (inclusive: a window may have been revised). */
  private async readTelemetry(graph: string, gen: number, why: Why, from: number | undefined): Promise<void> {
    try {
      const path = ENGINE_ENDPOINTS.telemetry(graph, from === undefined ? undefined : new Date(from * 1000).toISOString());
      const batches: TelemetryBatch[] = [];
      for (const [i, raw] of parseRecords(await this.text(path), path, true, (r) => this.reject(r)).entries()) {
        const r = batchFromWire(raw);
        if (!r.ok) { this.reject({ source: path, where: `line ${i + 1}`, errors: r.errors }); continue; }
        batches.push(r.batch);
      }
      this.step({ t: "windows", graph, gen, why, batches });
    } catch (e) { this.step({ t: "readFailed", graph, gen, why, error: (e as Error).message }); }
  }

  /** An activation can change the ontology of a graph and the roles of its values: read them again and tell the workspace. */
  private async readMeta(graph: string, gen: number): Promise<void> {
    try {
      const { hints, tree, kinds, labels, ontology: raw } = readPresentation(JSON.parse(await this.text(ENGINE_ENDPOINTS.ontology(graph))));
      const ont = checkOntology(raw);
      if (!ont.ok) { this.reject({ source: ENGINE_ENDPOINTS.ontology(graph), where: `ontology of ${graph}`, errors: ont.errors }); return; }
      this.step({ t: "meta", graph, gen, meta: { ontology: ont.value as GraphInfo["ontology"], presentation: hints.length ? hints : undefined, tree: tree ?? undefined, edgeKinds: Object.keys(kinds).length ? kinds : undefined, labels: Object.keys(labels).length ? labels : undefined } });
    } catch (e) { this.reject({ source: this.base, where: `ontology of ${graph}`, errors: [(e as Error).message] }); }
  }

  // ---- socket with reconnect ----
  private ensureSocket(): void {
    if (this.closed || this.ws) return;
    const WS = this.opts.WebSocket ?? globalThis.WebSocket;
    if (!WS) { this.setStatus("closed", "no WebSocket in this environment; history only"); return; }
    this.setStatus(this.retry ? "reconnecting" : "connecting", this.retry ? `reconnecting, attempt ${this.retry}` : "connecting");
    let ws: WebSocket;
    try { ws = new WS(this.url(ENGINE_ENDPOINTS.ws).replace(/^http/, "ws")); } catch (e) { this.scheduleReconnect((e as Error).message); return; }
    this.ws = ws;
    ws.onopen = () => {
      const resumed = this.retry > 0;
      this.retry = 0;
      // resume after the last sequence number seen; the engine sends what was missed, or a gap notice when its history no longer reaches back
      ws.send(subscribeMessage([...this.sinks.keys()], this.stream.seq));
      this.step({ t: "socketOpened", resumed });
    };
    ws.onmessage = (ev) => this.onMessage(String(ev.data));
    ws.onclose = () => { this.ws = null; this.step({ t: "socketClosed" }); if (!this.closed && this.status.state !== "ended") this.scheduleReconnect("connection closed"); };
    ws.onerror = () => { /* onclose follows */ };
  }

  private scheduleReconnect(why: string): void {
    if (this.closed) return;
    const delays = this.opts.backoff ?? [500, 1000, 2000, 5000];
    const d = delays[Math.min(this.retry, delays.length - 1)];
    this.retry++;
    this.status.reconnects++;
    this.setStatus("reconnecting", `${why}; retry in ${d} ms`);
    this.timer = setTimeout(() => { this.timer = null; this.ensureSocket(); }, d);
  }

  /** A socket message becomes an event of the stream; what does not parse is reported and dropped. */
  private onMessage(raw: string): void {
    let m;
    try { m = parseWsMessage(JSON.parse(raw)); } catch (e) { this.reject({ source: "ws", where: "message", errors: [`not JSON: ${(e as Error).message}`] }); return; }
    if ("seq" in m && m.seq !== undefined) this.step({ t: "seq", seq: m.seq });
    switch (m.type) {
      case "patch": { const p = parsePatch(m.patch, "ws", `patch of ${m.graph}`, (r) => this.reject(r)); if (p) this.step({ t: "wsPatch", graph: m.graph, patch: p, seq: m.seq }); return; }
      case "telemetry": {
        const r = batchFromWire(m.batch);
        if (!r.ok) { this.reject({ source: "ws", where: `telemetry of ${m.graph}`, errors: r.errors }); return; }
        this.step({ t: "wsTelemetry", graph: m.graph, batch: r.batch, seq: m.seq });
        return;
      }
      case "time": { const t = instantSeconds(m.logicalTime); if (Number.isFinite(t)) this.step({ t: "wsTime", logicalTime: t }); return; }
      case "activation": this.step({ t: "wsActivation", catalogRevision: m.catalogRevision, graphs: m.graphs.map((g) => g.id), seq: m.seq }); return;
      case "gap": this.step({ t: "wsGap", firstSeq: m.firstSeq }); return;
      case "error": this.step({ t: "wsError", code: m.code, message: m.message }); return;
      case "end": this.step({ t: "wsEnd", seq: m.seq }); return;
      case "snapshot": case "draft": return; // snapshots follow a gap or a subscription without `after`, which the resync reload reads over HTTP; drafts are the editor's
      case "hello": case "other": return;
    }
  }

  // ---- lineage, evaluate, rejections ----
  /** Every page of the supporting elements of one element (a lifted edge names all the lower edges it stands for), up to a cap. */
  async lineage(graph: string, element: string): Promise<Lineage | null> {
    let first: Lineage | null = null, after: string | undefined;
    for (let page = 0; page < 20; page++) {
      const path = ENGINE_ENDPOINTS.lineage(graph, element, after);
      const res = await this.get(path);
      if (res.status === 404) return null;
      if (!res.ok) { this.reject({ source: path, where: "lineage", errors: [`HTTP ${res.status}`] }); return first; }
      const l = parseLineage(JSON.parse(await res.text()));
      if (!l) return first;
      if (!first) first = l; else first.sources.push(...l.sources);
      if (!l.next) break;
      after = l.next;
    }
    return first && { ...first, next: null };
  }

  async reverseLineage(graph: string, element: string): Promise<ReverseLineage | null> {
    let first: ReverseLineage | null = null, after: string | undefined;
    for (let page = 0; page < 20; page++) {
      const path = ENGINE_ENDPOINTS.reverseLineage(graph, element, after);
      const res = await this.get(path);
      if (res.status === 404) return null;
      if (!res.ok) { this.reject({ source: path, where: "reverse lineage", errors: [`HTTP ${res.status}`] }); return first; }
      const l = parseReverseLineage(JSON.parse(await res.text()));
      if (!l) return first;
      if (!first) first = l; else first.partOf.push(...l.partOf);
      if (!l.next) break;
      after = l.next;
    }
    return first && { ...first, next: null };
  }

  /** The endpoints the graph's relationships name and it does not hold, with why: read alone from the references route, page by page (a snapshot would carry the whole graph). */
  async references(graph: string): Promise<Reference[]> {
    const out: Reference[] = [];
    let after: string | undefined;
    for (let page = 0; page < 100; page++) {
      const res = await this.get(ENGINE_ENDPOINTS.references(graph, after));
      if (!res.ok) { if (res.status !== 404) this.reject({ source: ENGINE_ENDPOINTS.references(graph), where: `references of ${graph}`, errors: [`HTTP ${res.status}`] }); return out; }
      const p = parseReferencePage(JSON.parse(await res.text()));
      out.push(...p.references);
      if (!p.next) break;
      after = p.next;
    }
    return out;
  }

  /** Rendered documents by URL with the ETag they came with (a content digest, engine 3.9). Every request asks again with If-None-Match, the head and a revision alike: a revision number is unique only within one run of the engine. */
  private documents = new Map<string, { etag: string; text: DocumentText }>();
  async document(graph: string, revision?: string): Promise<DocumentText | null> {
    const path = ENGINE_ENDPOINTS.document(graph, revision);
    const have = this.documents.get(path);
    let res: Response;
    try { res = await this.f(this.url(path), have ? { headers: { "If-None-Match": have.etag } } : undefined); } catch (e) { throw new Error(`engine not reachable at ${this.base}: ${(e as Error).message}`); }
    if (res.status === 304 && have) return have.text;
    if (res.status === 404) return null;
    if (!res.ok) throw new Error(`${path}: HTTP ${res.status} ${(await res.text()).slice(0, 160)}`);
    const text: DocumentText = { revision: res.headers.get("X-Revision") ?? revision ?? "", digest: res.headers.get("X-Document-Digest") ?? "", markdown: await res.text() };
    const etag = res.headers.get("ETag");
    if (etag) this.documents.set(path, { etag, text });
    return text;
  }

  onRejected(cb: (r: Rejection) => void): () => void { return this.bus.on(cb); }

  // ---- definitions ----
  readonly definitions: DefinitionService = {
    modules: () => this.readModules(),
  };

  private async readModules(): Promise<ModuleText[]> {
    const list = parseModules(JSON.parse(await this.text(ENGINE_ENDPOINTS.definitions)));
    const out: ModuleText[] = [];
    for (const m of list) out.push({ ...m, text: await this.text(ENGINE_ENDPOINTS.definition(m.module)) });
    return out;
  }

  dispose(): void {
    this.closed = true;
    for (const t of this.retryTimers) clearTimeout(t);
    this.retryTimers.clear();
    if (this.timer) clearTimeout(this.timer);
    this.step({ t: "dispose" }); // the generations move on (what was in flight delivers nothing), the socket closes, the status says closed
  }
}
