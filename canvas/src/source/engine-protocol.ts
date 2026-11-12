// The engine wire contract, protocol version 3: engine/PROTOCOL.md (authoritative). Every endpoint and message shape the canvas
// relies on is in this file, so a protocol change touches one place. Differences between the document and the running engine
// are listed in README.md ("Engine protocol notes").
import type { EngineDiagnostic, Lineage, Meter, ChannelDefaults, ModuleText, Reference, ReverseLineage } from "./types";

export const ENGINE_ENDPOINTS = {
  space: "/space",
  ws: "/ws",
  descriptor: (g: string) => `/graphs/${encodeURIComponent(g)}/descriptor`,
  /** `references=false` (engine 3.12) leaves `extent.references` out: they are read alone, by `references` */
  snapshot: (g: string, revision?: string) => `/graphs/${encodeURIComponent(g)}/snapshot?${revision === undefined ? "" : `revision=${encodeURIComponent(revision)}&`}references=false`,
  /** the endpoints the graph's relationships name and it does not hold, paged by id */
  references: (g: string, after?: string) => `/graphs/${encodeURIComponent(g)}/references?limit=1000${after ? `&after=${encodeURIComponent(after)}` : ""}`,
  ontology: (g: string) => `/graphs/${encodeURIComponent(g)}/ontology`,
  /** with an explicit `to` at or below the head the answer is immutable and the browser caches it for good */
  patches: (g: string, after: string, to?: string) => `/graphs/${encodeURIComponent(g)}/patches?after=${encodeURIComponent(after)}${to ? `&to=${encodeURIComponent(to)}` : ""}`,
  meters: (g: string) => `/graphs/${encodeURIComponent(g)}/meters`,
  /** window starts are RFC 3339 instants; `from` is inclusive */
  telemetry: (g: string, from?: string) => `/graphs/${encodeURIComponent(g)}/telemetry?format=batches${from ? `&from=${encodeURIComponent(from)}` : ""}`,
  /** Markdown of a document graph; with a revision the answer is immutable */
  document: (g: string, revision?: string) => `/graphs/${encodeURIComponent(g)}/document${revision === undefined ? "" : `?revision=${encodeURIComponent(revision)}`}`,
  lineage: (g: string, element: string, after?: string) => `/graphs/${encodeURIComponent(g)}/lineage?element=${encodeURIComponent(element)}&limit=500${after ? `&after=${encodeURIComponent(after)}` : ""}`,
  reverseLineage: (g: string, element: string, after?: string) => `/graphs/${encodeURIComponent(g)}/lineage/reverse?element=${encodeURIComponent(element)}&limit=500${after ? `&after=${encodeURIComponent(after)}` : ""}`,
  definitions: "/definitions",
  definition: (m: string) => `/definitions/${encodeURIComponent(m)}`,
  check: "/check",
};

export interface SpaceGraph {
  id: string; role: "master" | "projection" | "catalog"; head: string; ontology: string; inputs: string[]; direction: string; editable: boolean; projection: string | null; document: boolean;
  /** engine 3.17: the first sentence of the projection's `///` comment stands alone as a caption */
  description?: string;
}
export interface SpaceDoc { space: string; protocol: string; kernelDigest: string; /** event sequence the document is consistent with: the stream resumes after it */ seq: string | undefined; catalogRevision: string; graphs: SpaceGraph[]; input: { mode: string; logicalTime: string; rows: string } | null }

export function parseSpace(raw: unknown): SpaceDoc {
  const o = raw as Record<string, unknown> | null;
  const gs = o?.graphs;
  if (!o || !Array.isArray(gs)) throw new Error("GET /space: no graphs array");
  if (String(o.protocol) !== "3") throw new Error(`GET /space: protocol ${String(o.protocol)} is not supported, this canvas speaks protocol 3`);
  return {
    space: String(o.space), protocol: "3", kernelDigest: String(o.kernelDigest ?? ""), seq: o.seq === undefined ? undefined : String(o.seq), catalogRevision: String(o.catalogRevision ?? "0"),
    graphs: gs.map((g) => {
      const x = g as Record<string, unknown>;
      const role = x.role === "catalog" ? "catalog" : x.role === "master" ? "master" : "projection";
      return { id: String(x.id), role, head: String(x.head ?? "0"), ontology: String(x.ontology ?? ""), inputs: Array.isArray(x.inputs) ? x.inputs.map(String) : [], direction: String(x.direction ?? ""), editable: x.editable === true, document: x.document === true, projection: x.projection ? String(x.projection) : null, description: typeof x.description === "string" && x.description ? x.description : undefined };
    }),
    input: o.input ? { mode: String((o.input as Record<string, unknown>).mode), logicalTime: String((o.input as Record<string, unknown>).logicalTime ?? ""), rows: String((o.input as Record<string, unknown>).rows ?? "") } : null,
  };
}

/** RFC 3339 instant to seconds since the epoch. NaN when it does not parse. */
export const instantSeconds = (s: string): number => Date.parse(s) / 1000;

/** The telemetry batch of the wire (windowStart present) as the seam batch: `t` is the window start in epoch seconds. */
export function batchFromWire(raw: unknown): { ok: true; batch: import("./types").TelemetryBatch } | { ok: false; errors: string[] } {
  const o = raw as Record<string, unknown> | null;
  const e: string[] = [];
  if (!o || typeof o !== "object") return { ok: false, errors: ["not an object"] };
  if (typeof o.graph !== "string" || !o.graph) e.push("graph must be a non-empty string");
  if (typeof o.windowStart !== "string" || !Number.isFinite(instantSeconds(o.windowStart))) e.push("windowStart must be an RFC 3339 instant");
  if (o.target !== "node" && o.target !== "edge") e.push('target must be "node" or "edge"');
  if (typeof o.property !== "string" || !o.property) e.push("property must be a non-empty string");
  if (!Array.isArray(o.ids) || !o.ids.every((i) => typeof i === "string")) e.push("ids must be an array of element ids");
  if (!Array.isArray(o.values) || !o.values.every((v) => typeof v === "number")) e.push("values must be an array of numbers");
  else if (Array.isArray(o.ids) && o.ids.length !== o.values.length) e.push("ids and values differ in length");
  if (o.removed !== undefined && (!Array.isArray(o.removed) || !o.removed.every((i) => typeof i === "string"))) e.push("removed must be an array of element ids");
  if (e.length) return { ok: false, errors: e };
  return { ok: true, batch: { ...(Array.isArray(o.removed) && o.removed.length ? { removed: o.removed as string[] } : {}), graph: o.graph as string, tick: Number(o.tick ?? 0), t: instantSeconds(o.windowStart as string), target: o.target as "node" | "edge", property: o.property as string, ids: o.ids as string[], values: Float32Array.from(o.values as number[]) } };
}

export function parseMeters(raw: unknown): { meters: Meter[]; defaults: Partial<ChannelDefaults> } {
  const o = raw as { meters?: Record<string, unknown>[]; defaults?: Partial<ChannelDefaults> } | null;
  const meters: Meter[] = (o?.meters ?? []).map((m) => ({
    id: String(m.id), target: m.target === "edge" ? "edge" : "node", label: String(m.label ?? m.id), unit: String(m.unit ?? ""),
    min: Number(m.min ?? 0), max: Number(m.max ?? 1) || 1, warn: typeof m.warn === "number" ? m.warn : undefined,
    scale: m.scale === "sqrt" ? "sqrt" : "linear",
    // the engine reports the range observed so far; live values may exceed it
    auto: true,
  }));
  return { meters, defaults: o?.defaults ?? {} };
}

/** Every event except `time` carries `seq`, the server-wide sequence number (decimal string). */
export type WsMessage =
  | { type: "hello"; space: unknown }
  | { type: "snapshot"; graph: string; seq?: string }
  | { type: "patch"; graph: string; patch: unknown; seq?: string }
  | { type: "telemetry"; graph: string; batch: unknown; seq?: string }
  | { type: "gap"; firstSeq: string }
  | { type: "activation"; catalogRevision: string; graphs: { id: string; from: string; to: string }[]; seq?: string }
  | { type: "draft"; draft: string; revision: string; ok: boolean; diagnostics: unknown; seq?: string }
  | { type: "time"; logicalTime: string; rows: string }
  | { type: "end"; seq?: string }
  | { type: "error"; code: string; message: string }
  | { type: "other" };

export function parseWsMessage(raw: unknown): WsMessage {
  const m = raw as Record<string, unknown> | null;
  const seq = m && m.seq !== undefined ? String(m.seq) : undefined;
  switch (m?.type) {
    case "hello": return { type: "hello", space: m.space };
    case "snapshot": return { type: "snapshot", graph: String(m.graph), seq };
    case "patch": return { type: "patch", graph: String(m.graph), patch: m.patch, seq };
    case "telemetry": return { type: "telemetry", graph: String(m.graph), batch: m.batch, seq };
    case "gap": return { type: "gap", firstSeq: String(m.firstSeq) };
    case "activation": return { type: "activation", catalogRevision: String(m.catalogRevision), graphs: Array.isArray(m.graphs) ? (m.graphs as { id: string; from: string; to: string }[]) : [], seq };
    case "draft": return { type: "draft", draft: String(m.draft), revision: String(m.revision), ok: m.ok === true, diagnostics: m.diagnostics, seq };
    case "time": return { type: "time", logicalTime: String(m.logicalTime), rows: String(m.rows ?? "") };
    case "end": return { type: "end", seq };
    case "error": return { type: "error", code: String(m.code), message: String(m.message ?? "") };
    default: return { type: "other" };
  }
}

/** One subscription per connection (v3): the graphs to follow and where to resume. Without `after` the server sends a snapshot per graph first. */
export const subscribeMessage = (graphs: string[], after?: string, drafts = false): string => JSON.stringify({ op: "subscribe", graphs, drafts, ...(after !== undefined ? { after } : {}) });

export function parseDiagnostics(list: unknown): EngineDiagnostic[] {
  return (Array.isArray(list) ? list : []).map((d) => {
    const x = d as Record<string, unknown>;
    return { code: String(x.code ?? ""), severity: String(x.severity ?? "error"), focus: x.element ? String(x.element) : "", message: String(x.message ?? ""), line: Number(x.line ?? 0), col: Number(x.column ?? 0) };
  });
}

export function parseModules(raw: unknown): Omit<ModuleText, "text">[] {
  return (Array.isArray(raw) ? raw : []).map((m) => { const x = m as Record<string, unknown>; return { module: String(x.name), version: String(x.version ?? ""), digest: String(x.digest ?? ""), revision: String(x.revision ?? "") }; });
}

export function parseLineage(raw: unknown): Lineage | null {
  const o = raw as Record<string, unknown> | null;
  if (!o || typeof o.element !== "string") return null;
  const kind = (k: unknown): "entity" | "relationship" => (k === "relationship" ? "relationship" : "entity");
  return {
    graph: String(o.graph), element: o.element, kind: kind(o.kind), projection: o.projection ? String(o.projection) : null, sink: o.sink ? String(o.sink) : null,
    rule: o.rule ? String(o.rule) : null, direction: o.direction ? String(o.direction) : null, operators: Array.isArray(o.operators) ? o.operators.map(String) : [],
    sources: (Array.isArray(o.sources) ? o.sources : []).map((s) => { const x = s as Record<string, unknown>; return { graph: String(x.graph), element: String(x.element), kind: kind(x.kind), role: String(x.role ?? "") }; }),
    sourceCount: String(o.sourceCount ?? "0"), complete: o.complete === true, groupKey: o.groupKey ?? null, inputs: Array.isArray(o.inputs) ? o.inputs.map(String) : undefined,
    contributions: o.contributions === undefined ? undefined : String(o.contributions), next: o.next ? String(o.next) : null,
  };
}

const kindOf = (k: unknown): "entity" | "relationship" => (k === "relationship" ? "relationship" : "entity");
export function parseReverseLineage(raw: unknown): ReverseLineage | null {
  const o = raw as Record<string, unknown> | null;
  if (!o || typeof o.element !== "string") return null;
  return {
    graph: String(o.graph), element: o.element, kind: kindOf(o.kind),
    partOf: (Array.isArray(o.partOf) ? o.partOf : []).map((p) => { const x = p as Record<string, unknown>; return { graph: String(x.graph), element: String(x.element), kind: kindOf(x.kind), role: String(x.role ?? ""), sink: x.sink ? String(x.sink) : null, rule: x.rule ? String(x.rule) : null }; }),
    count: String(o.count ?? "0"), next: o.next ? String(o.next) : null, incompleteIn: Array.isArray(o.incompleteIn) ? o.incompleteIn.map(String) : [],
  };
}

/** One page of `GET /graphs/{g}/references` (engine 3.12): the entries and the id to continue from. */
export function parseReferencePage(raw: unknown): { references: Reference[]; next: string | null } {
  const o = raw as { references?: unknown; next?: unknown } | null;
  return { references: parseReferences(o?.references), next: typeof o?.next === "string" && o.next ? o.next : null };
}

/** Reference entries (engine 3.11, the shape of `extent.references`); entries that do not have the shape are left out. */
export function parseReferences(raw: unknown): Reference[] {
  const out: Reference[] = [];
  for (const r of Array.isArray(raw) ? raw : []) {
    const x = r as Record<string, unknown>;
    // ids are `{ value }` on the wire, as everywhere in a snapshot
    const id = typeof x?.id === "string" ? x.id : typeof (x?.id as { value?: unknown } | undefined)?.value === "string" ? (x.id as { value: string }).value : null;
    if (id === null) continue;
    const ident = x.identity as { domain?: unknown; key?: Record<string, unknown> } | null | undefined;
    out.push({
      id, state: x.state === "inOtherGraph" ? "inOtherGraph" : "absent", graph: typeof x.graph === "string" ? x.graph : null, entityType: String(x.entityType ?? ""),
      identity: ident && typeof ident === "object" ? { domain: String(ident.domain ?? ""), key: Object.fromEntries(Object.entries(ident.key ?? {}).map(([k, v]) => [k, String(v)])) } : null,
      uses: (Array.isArray(x.uses) ? x.uses : []).map((u) => { const y = u as Record<string, unknown>; return { relationshipType: String(y.relationshipType ?? ""), end: y.end === "target" ? "target" as const : "source" as const, count: Number(y.count ?? 0) }; }),
    });
  }
  return out;
}
