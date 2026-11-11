// EngineSource: the GraphSource seam over the Sattle engine, protocol version 3 (engine/PROTOCOL.md).
// History is read over HTTP from immutable, revision-addressed resources (the browser caches them), then one WebSocket follows the
// ordered event stream with `subscribe {graphs, after: seq}`. The connection reconnects with backoff and resumes from the last
// sequence number seen; anything the stream skipped is read back over HTTP. Invalid records are reported, never dropped silently.
import type { Patch, Snapshot } from "../core/model";
import { checkOntology, type Rejection } from "../core/validate";
import { RejectionBus } from "./base";
import {
  ENGINE_ENDPOINTS, batchFromWire, instantSeconds, parseCheckResponse, parseCommitResponse, parseLineage, parseReferencePage, parseReverseLineage, parseMeters, parseModules, parseSpace, parseWsMessage, subscribeMessage,
  type SpaceDoc,
} from "./engine-protocol";
import { readPresentation } from "./presentation";
import { parsePatch, parseRecords, parseSnapshot } from "./snapshot-file";
import type {
  CheckResult, CommitResult, ConnectionStatus, DefinitionService, DocumentText, EvaluateResult, GraphInfo, GraphSource, Lineage, Reference, ReverseLineage, ModuleText, SpaceInfo, StreamEvent, TelemetryBatch,
} from "./types";
import { NOT_AVAILABLE } from "./types";

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
  /** engine logical time (epoch seconds) from the latest `time` message, used as the commit time of live patches */
  logicalTime = NaN;
  private readonly base: string;
  private doc: SpaceDoc | null = null;
  private info: SpaceInfo | null = null;
  private snaps = new Map<string, { snapshot: Snapshot; descriptor: GraphInfo["descriptor"] }>();
  private sinks = new Map<string, Set<Sink>>();
  private lastRevision = new Map<string, string>();
  /** head revision of each graph when the space was opened: history up to it is read from immutable URLs */
  private heads = new Map<string, string>();
  /** server-wide sequence number: where the stream resumes */
  private seq: string | undefined;
  private lastWindow = new Map<string, number>();
  private fills = new Map<string, Promise<void>>();
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
    // the stream resumes after the sequence number of the space document; history read later overlaps it and duplicates are dropped by revision
    this.seq = this.doc.seq;
    for (const g of this.doc.graphs) this.heads.set(g.id, g.head);
    const graphs: GraphInfo[] = [];
    for (const g of this.doc.graphs) {
      const { hints, tree, ontology: rawOntology } = readPresentation(JSON.parse(await this.text(ENGINE_ENDPOINTS.ontology(g.id))));
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
        id: g.id, role: g.role, descriptor: snap.descriptor, ontology: (ont.ok ? ont.value : rawOntology) as GraphInfo["ontology"], presentation: hints.length ? hints : undefined, tree: tree ?? undefined, inputs: g.inputs, direction: g.direction, editable: g.editable, document: g.document || undefined,
        projectionName: g.projection ?? undefined,
      });
      this.lastRevision.set(g.id, snap.snapshot.version.revision);
    }
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

  // ---- stream ----
  subscribe(graph: string, sink: Sink): () => void {
    let set = this.sinks.get(graph);
    if (!set) this.sinks.set(graph, (set = new Set()));
    set.add(sink);
    void this.history(graph).then(() => { sink({ type: "caught-up" }); this.ensureSocket(); })
      .catch((e) => { this.reject({ source: this.base, where: `history of ${graph}`, errors: [(e as Error).message] }); sink({ type: "caught-up" }); });
    return () => { set!.delete(sink); };
  }

  private emit(graph: string, e: StreamEvent): void { for (const s of this.sinks.get(graph) ?? []) s(e); }

  private async history(graph: string): Promise<void> {
    for (const p of await this.patchesAfter(graph)) this.emit(graph, { type: "patch", patch: p });
    if (this.hasTelemetry(graph)) for (const b of await this.telemetryFrom(graph)) this.emit(graph, { type: "telemetry", batch: b });
  }

  /** The catalog graph has no telemetry and the engine answers 404 for it. */
  private hasTelemetry(graph: string): boolean { return this.doc?.graphs.find((g) => g.id === graph)?.role !== "catalog"; }

  /** Patches after the last revision seen, in order. A patch that does not continue the log is reported, not applied. */
  private async patchesAfter(graph: string): Promise<Patch[]> {
    const after = this.lastRevision.get(graph) ?? "0";
    const head = this.heads.get(graph);
    // up to the head seen at open: immutable, so cached; later fills read the moving head
    const path = ENGINE_ENDPOINTS.patches(graph, after, head !== undefined && num(after) < num(head) && !this.filled.has(graph) ? head : undefined);
    this.filled.add(graph);
    const items = parseRecords(await this.text(path), path, true, (r) => this.reject(r));
    const out: Patch[] = [];
    for (const [i, raw] of items.entries()) {
      const p = parsePatch(raw, path, `patch #${i + 1}`, (r) => this.reject(r));
      if (!p) continue;
      const last = this.lastRevision.get(graph) ?? after;
      if (num(p.to.revision) <= num(last)) continue;
      out.push(p);
      this.lastRevision.set(graph, p.to.revision);
    }
    return out;
  }

  /** Telemetry windows from the last window seen (inclusive: a window may have been revised). */
  private async telemetryFrom(graph: string): Promise<TelemetryBatch[]> {
    const last = this.lastWindow.get(graph);
    const path = ENGINE_ENDPOINTS.telemetry(graph, last === undefined ? undefined : new Date(last * 1000).toISOString());
    const out: TelemetryBatch[] = [];
    for (const [i, raw] of parseRecords(await this.text(path), path, true, (r) => this.reject(r)).entries()) {
      const r = batchFromWire(raw);
      if (!r.ok) { this.reject({ source: path, where: `line ${i + 1}`, errors: r.errors }); continue; }
      out.push(r.batch);
      this.lastWindow.set(graph, Math.max(this.lastWindow.get(graph) ?? -Infinity, r.batch.t));
    }
    return out;
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
      this.setStatus("live", resumed ? "reconnected" : "connected");
      // resume after the last sequence number seen; the engine sends what was missed, or a gap notice when its history no longer reaches back
      ws.send(subscribeMessage([...this.sinks.keys()], this.seq));
      if (resumed) for (const g of this.sinks.keys()) void this.fill(g, "reconnect");
    };
    ws.onmessage = (ev) => { void this.onMessage(String(ev.data)); };
    ws.onclose = () => { this.ws = null; if (!this.closed && this.status.state !== "ended") this.scheduleReconnect("connection closed"); };
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

  private async onMessage(raw: string): Promise<void> {
    let m;
    try { m = parseWsMessage(JSON.parse(raw)); } catch (e) { this.reject({ source: "ws", where: "message", errors: [`not JSON: ${(e as Error).message}`] }); return; }
    if ("seq" in m && m.seq !== undefined && (this.seq === undefined || BigInt(m.seq) > BigInt(this.seq))) this.seq = m.seq;
    switch (m.type) {
      case "patch": {
        const p = parsePatch(m.patch, "ws", `patch of ${m.graph}`, (r) => this.reject(r));
        if (!p) return;
        const last = this.lastRevision.get(m.graph);
        if (last !== undefined && num(p.to.revision) <= num(last)) return; // already delivered by the HTTP history
        if (last !== undefined && p.from.revision !== last) await this.fill(m.graph, `expected a patch from revision ${last}, got ${p.from.revision}`);
        const now = this.lastRevision.get(m.graph);
        if (now !== undefined && num(p.to.revision) <= num(now)) return; // the fill delivered it
        this.lastRevision.set(m.graph, p.to.revision);
        this.emit(m.graph, { type: "patch", patch: p, time: Number.isFinite(this.logicalTime) ? this.logicalTime : undefined });
        return;
      }
      case "telemetry": {
        const r = batchFromWire(m.batch);
        if (!r.ok) { this.reject({ source: "ws", where: `telemetry of ${m.graph}`, errors: r.errors }); return; }
        // the stream replays events after the sequence number read before the history: windows older than the newest one are already ingested
        const newest = this.lastWindow.get(m.graph);
        if (newest !== undefined && r.batch.t < newest) return;
        this.lastWindow.set(m.graph, r.batch.t);
        this.emit(m.graph, { type: "telemetry", batch: r.batch });
        return;
      }
      case "time": { const t = instantSeconds(m.logicalTime); if (Number.isFinite(t)) this.logicalTime = t; return; }
      case "activation": {
        // a definition edit re-activated the space: stored telemetry may differ, read it again
        // a graph the space did not have before (a committed projection brought it): the view reloads to show it
        const known = new Set(this.info?.graphs.map((g) => g.id));
        if (m.graphs.some((g) => !known.has(g.id))) { for (const g of this.sinks.keys()) this.emit(g, { type: "resync", reason: "the space has a new graph" }); return; }
        for (const g of this.sinks.keys()) { this.lastWindow.delete(g); this.emit(g, { type: "telemetry-reset" }); void this.fill(g, `activation of catalog revision ${m.catalogRevision}`); }
        return;
      }
      case "gap": {
        this.reject({ source: "ws", where: "event stream", errors: [`the engine's retained events start at ${m.firstSeq}; resynchronizing from a snapshot`] });
        this.seq = undefined;
        for (const g of this.sinks.keys()) this.emit(g, { type: "resync", reason: "event gap" });
        return;
      }
      case "snapshot": case "draft": return; // snapshots follow a gap or a subscription without `after`, which the resync reload reads over HTTP; drafts are the editor's
      case "error": {
        this.reject({ source: "ws", where: m.code, errors: [m.message || m.code] });
        if (m.code === "SlowConsumer") this.ws?.close(); // the reconnect resumes with `after`
        if (m.code === "AheadOfHead") for (const g of this.sinks.keys()) this.emit(g, { type: "resync", reason: "this client is ahead of the engine" });
        return;
      }
      case "end": this.setStatus("ended", "the engine ended the stream"); for (const g of this.sinks.keys()) this.emit(g, { type: "end" }); return;
      case "hello": case "other": return;
    }
  }

  /** Read what the stream missed over HTTP. One fill per graph at a time. */
  private fill(graph: string, why: string): Promise<void> {
    const busy = this.fills.get(graph);
    if (busy) return busy;
    const p = (async () => {
      const patches = await this.patchesAfter(graph);
      for (const patch of patches) this.emit(graph, { type: "patch", patch });
      if (this.hasTelemetry(graph)) for (const b of await this.telemetryFrom(graph)) this.emit(graph, { type: "telemetry", batch: b });
      if (patches.length) { this.status.gapsFilled++; this.setStatus(this.status.state, `${why}: filled ${patches.length} revision(s)`); }
    })().catch((e) => this.reject({ source: this.base, where: `catch-up of ${graph}`, errors: [(e as Error).message] })).finally(() => this.fills.delete(graph));
    this.fills.set(graph, p);
    return p;
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

  evaluate(): Promise<EvaluateResult> { return Promise.resolve(NOT_AVAILABLE); }
  onRejected(cb: (r: Rejection) => void): () => void { return this.bus.on(cb); }

  // ---- definitions ----
  readonly definitions: DefinitionService = {
    modules: () => this.readModules(),
    check: (module, text) => this.check(module, text),
    commit: (module, text, expectedRevision) => this.commit(module, text, expectedRevision),
  };

  private async readModules(): Promise<ModuleText[]> {
    const list = parseModules(JSON.parse(await this.text(ENGINE_ENDPOINTS.definitions)));
    const out: ModuleText[] = [];
    for (const m of list) out.push({ ...m, text: await this.text(ENGINE_ENDPOINTS.definition(m.module)) });
    return out;
  }

  private async post(path: string, body: unknown): Promise<{ status: number; json: unknown }> {
    let res: Response;
    try { res = await this.f(this.url(path), { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) }); } catch (e) { throw new Error(`engine not reachable at ${this.base}: ${(e as Error).message}`); }
    const text = await res.text();
    let json: unknown = null;
    try { json = text ? JSON.parse(text) : null; } catch { throw new Error(`${path}: HTTP ${res.status}, not JSON: ${text.slice(0, 160)}`); }
    return { status: res.status, json };
  }

  private async check(module: string, text: string): Promise<CheckResult> {
    const { status, json } = await this.post(ENGINE_ENDPOINTS.check, { module, text });
    if (status !== 200) throw new Error(`POST /check: HTTP ${status} ${(json as { error?: { message?: string } })?.error?.message ?? ""}`);
    return parseCheckResponse(json, (r) => this.reject({ ...r, source: "/check" }));
  }

  private async commit(module: string, text: string, expectedRevision: string): Promise<CommitResult> {
    const { status, json } = await this.post(ENGINE_ENDPOINTS.definition(module), { text, expectedRevision });
    return parseCommitResponse(status, json);
  }

  dispose(): void {
    this.closed = true;
    if (this.timer) clearTimeout(this.timer);
    this.ws?.close();
    this.setStatus("closed", "closed");
  }
}
