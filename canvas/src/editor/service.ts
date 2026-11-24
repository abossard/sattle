// The editor side of the engine protocol (v3): palette, projection files, drafts, releases and preview. HTTP only.
// Responses are parsed here, so the rest of the editor sees typed values; engine errors become EngineError with the code and element.
import type { Patch, Snapshot } from "../core/model";
import { patchToWire } from "../core/wire";
import { parseDiagnostics } from "../source/engine-protocol";
import { parsePatch, parseRecords, parseSnapshot } from "../source/snapshot-file";
import type { EngineDiagnostic } from "../source/types";
import { parsePalette, type Palette } from "./palette";

export class EngineError extends Error {
  constructor(readonly code: string, message: string, readonly status: number, readonly element?: string, readonly diagnostics: EngineDiagnostic[] = []) { super(`${code}: ${message}`); }
}

export interface Draft { draft: string; baseRevision: string; revision: string }
/** engine 3.50: a projection in a file of its own, and the language the engine reads that file in; the language picks the editor (ui/editor/languages.tsx) */
export interface ProjectionFile { projection: string; file: string; language: string }
/** engine 3.21: a column of the rows after an operator. `kind` is `Entity`, `Relationship` or the value kind; `unit` a unit other than 1; `item` the item kind of a list. */
export interface Column { name: string; kind: string; type: string | null; unit: string | null; item: string | null; required: boolean; role: "data" | "telemetry"; temporality: string | null; period: string | null }
/** engine 3.21: the columns after each typed operator of a projection, in order, from a draft check or a draft patch */
export interface OperatorSchema { op: string; name: string; kind: string; window: string | null; columns: Column[] }
/** engine 3.21: the rows a physical operator holds after the replay of a preview (`include: ["rows"]`), with the first `rowLimit` of them */
export interface OperatorRows { op: string; name: string; kind: string; columns: Column[]; rows: string; weight: string; /** a cell without a value has no key */ sample: { weight: string; cells: Record<string, string> }[]; truncated: boolean }

const str = (v: unknown): string | null => (v === null || v === undefined ? null : String(v));
/** Columns as a check names them (objects), or as a preview with `include: ["rows"]` does (the names alone, as the live engine sends them): a name alone has no kind. */
export function parseColumns(list: unknown): Column[] {
  return (Array.isArray(list) ? list : []).map((x): Column => {
    if (typeof x === "string") return { name: x, kind: "", type: null, unit: null, item: null, required: true, role: "data", temporality: null, period: null };
    const c = (x ?? {}) as Record<string, unknown>;
    return { name: String(c.name ?? ""), kind: String(c.kind ?? ""), type: str(c.type), unit: str(c.unit), item: str(c.item), required: c.required === true, role: c.role === "telemetry" ? "telemetry" : "data", temporality: str(c.temporality), period: str(c.period) };
  });
}
/** The `schemas` of a check or patch answer, by projection id; absent from an older engine. */
export function parseSchemas(raw: unknown): Record<string, OperatorSchema[]> | undefined {
  if (!raw || typeof raw !== "object") return undefined;
  return Object.fromEntries(Object.entries(raw as Record<string, unknown>).map(([p, ops]) => [p, (Array.isArray(ops) ? ops : []).map((x) => {
    const o = (x ?? {}) as Record<string, unknown>;
    return { op: String(o.op ?? ""), name: String(o.name ?? ""), kind: String(o.kind ?? ""), window: str(o.window), columns: parseColumns(o.columns) };
  })]));
}
export function parseOperatorRows(list: unknown): OperatorRows[] {
  return (Array.isArray(list) ? list : []).map((x) => {
    const o = (x ?? {}) as Record<string, unknown>;
    const sample = (Array.isArray(o.sample) ? o.sample : []).map((r) => { const s = (r ?? {}) as Record<string, unknown>; return { weight: String(s.weight ?? ""), cells: Object.fromEntries(Object.entries((s.cells ?? {}) as Record<string, unknown>).filter(([, v]) => v != null).map(([k, v]) => [k, String(v)])) }; });
    return { op: String(o.op ?? ""), name: String(o.name ?? ""), kind: String(o.kind ?? ""), columns: parseColumns(o.columns), rows: String(o.rows ?? "0"), weight: String(o.weight ?? "0"), sample, truncated: o.truncated === true };
  });
}
export interface Applied {
  /** restore: the patch the engine applied */
  patch?: Patch; revision: string; ok: boolean; diagnostics: EngineDiagnostic[]; modules: Record<string, { text: string | null; changed?: boolean }>;
  /** engine 3.21: the columns after each operator, by projection id */
  schemas?: Record<string, OperatorSchema[]> }
export interface Release { module: string; version: string; digest: string; catalogRevision: string; supersedes: string | null }
export interface PreviewGraph { id: string; removed?: boolean; counts: { entities: string; relationships: string; telemetryWindows: string }; digest?: string; diff: Patch | null; snapshot?: Snapshot; truncated?: boolean;
  /** engine 3.21, with `include: ["rows"]`: the rows after each operator of the graph's plan */
  operators?: OperatorRows[] }
/** Engine 3.7: what identity resolution did in the previewed space (counters are integers as strings). `difference` is preview minus live, present when `comparable`. */
export interface IdentityCounts { minted: string; aliased: string; merged: string; conflicts: string }
export interface IdentityEffect { preview: IdentityCounts; live: IdentityCounts; comparable: boolean; difference?: IdentityCounts }
const KEYS = ["minted", "aliased", "merged", "conflicts"] as const;
const counts = (v: unknown): IdentityCounts | null => {
  const o = v as Record<string, unknown> | null;
  return o && KEYS.every((k) => typeof o[k] === "string" && /^-?\d+$/.test(o[k] as string)) ? { minted: o.minted, aliased: o.aliased, merged: o.merged, conflicts: o.conflicts } as IdentityCounts : null;
};
/** The identity effect of a preview answer; absent (an older engine) or malformed answers give undefined rather than a guess. */
export function parseIdentity(v: unknown): IdentityEffect | undefined {
  const o = v as Record<string, unknown> | null | undefined, preview = counts(o?.preview), live = counts(o?.live);
  if (!o || !preview || !live) return undefined;
  const difference = counts(o.difference);
  return { preview, live, comparable: o.comparable === true && !!difference, ...(difference ? { difference } : {}) };
}
export interface Preview { scope: { rows: string; totalRows: string; complete: boolean; logicalTime: string }; diagnostics: EngineDiagnostic[]; graphs: PreviewGraph[]; identity?: IdentityEffect; milliseconds: string }

const throwing = (where: string) => (r: { source: string; where: string; errors: string[] }) => { throw new Error(`${where}: ${r.where}: ${r.errors.join("; ")}`); };

export class EditorService {
  private readonly base: string;
  constructor(base: string, private readonly f: typeof fetch = (...a) => fetch(...a)) { this.base = new URL(base, globalThis.location?.href ?? "http://localhost/").href.replace(/\/$/, ""); }

  /** `text`: an answer that succeeds is the text as sent, never parsed (a definition file may start like JSON). */
  private async call(method: string, path: string, body?: unknown, text = false): Promise<unknown> {
    let res: Response;
    try { res = await this.f(this.base + path, body === undefined ? { method } : { method, headers: { "content-type": "application/json" }, body: JSON.stringify(body) }); } catch (e) { throw new EngineError("Unreachable", (e as Error).message, 0); }
    const raw = await res.text();
    let json: unknown = raw;
    if ((!text || !res.ok) && ((res.headers.get("content-type") ?? "").includes("json") || /^[{[]/.test(raw))) { try { json = JSON.parse(raw); } catch { /* text */ } }
    if (!res.ok) {
      const e = ((json as { error?: Record<string, unknown> })?.error ?? {}) as Record<string, unknown>;
      throw new EngineError(String(e.code ?? `HTTP${res.status}`), String(e.message ?? raw.slice(0, 120)), res.status, e.element ? String(e.element) : undefined, parseDiagnostics(e.diagnostics));
    }
    return json;
  }
  async palette(): Promise<Palette> { return parsePalette(await this.call("GET", "/palette")); }
  /** Every projection file of the space with its language (PROTOCOL 3.50), from the modules' `projections`. */
  async projectionFiles(): Promise<ProjectionFile[]> {
    return ((await this.call("GET", "/definitions")) as { projections?: Record<string, unknown>[] }[]).flatMap((m) => (m.projections ?? []).map((p) => ({ projection: String(p.projection), file: String(p.file), language: String(p.language) })));
  }
  /** A definition file's text as the space holds it, whatever its language. */
  async definitionText(file: string): Promise<string> { return String(await this.call("GET", `/definitions/${encodeURIComponent(file)}`, undefined, true)); }

  /** Store a projection file's text in the draft (`file` as `GET /definitions` names it); check, preview and commit include it from then on. */
  async setDraftText(id: string, file: string, text: string | null, expectedRevision: string): Promise<Applied> { return this.applied(await this.call("PUT", `/drafts/${id}/text/${encodeURIComponent(file)}`, { text, expectedRevision })); }

  async createDraft(base?: string): Promise<Draft> { const d = (await this.call("POST", "/drafts", base ? { base } : {})) as Draft; return { draft: String(d.draft), baseRevision: String(d.baseRevision), revision: String(d.revision) }; }
  async discard(id: string): Promise<void> { await this.call("DELETE", `/drafts/${id}`); }
  async snapshot(id: string): Promise<Snapshot> {
    const s = parseSnapshot(await this.call("GET", `/drafts/${id}/snapshot`), `/drafts/${id}/snapshot`, throwing("draft snapshot"));
    if (!s) throw new Error("draft snapshot is not valid");
    return s.snapshot;
  }
  private applied(raw: unknown): Applied {
    const o = raw as Record<string, unknown>;
    const schemas = parseSchemas(o.schemas);
    return { revision: String(o.revision), ok: o.ok === true, diagnostics: parseDiagnostics(o.diagnostics), modules: (o.modules ?? {}) as Applied["modules"], ...(schemas ? { schemas } : {}) };
  }
  async applyPatch(id: string, patch: Patch, expectedRevision: string): Promise<Applied> { return this.applied(await this.call("POST", `/drafts/${id}/patches`, { patch: patchToWire(patch), expectedRevision })); }
  async check(id: string): Promise<Applied> { return this.applied(await this.call("POST", `/drafts/${id}/check`)); }
  async commit(id: string, expectedCatalogRevision: string): Promise<{ catalogRevision: string; modules: string[] }> {
    const o = (await this.call("POST", `/drafts/${id}/commit`, { expectedCatalogRevision })) as Record<string, unknown>;
    return { catalogRevision: String(o.catalogRevision), modules: Array.isArray(o.modules) ? o.modules.map(String) : [] };
  }
  async restore(id: string, digest: string): Promise<Applied & { patch: Patch }> {
    const raw = (await this.call("POST", `/drafts/${id}/restore`, { digest })) as Record<string, unknown>;
    return { ...this.applied(raw), patch: parsePatch(raw.patch, "restore", "restore patch", throwing("restore"))! };
  }

  async releases(module?: string): Promise<Release[]> { return (await this.call("GET", `/releases${module ? `?module=${encodeURIComponent(module)}` : ""}`)) as Release[]; }
  async releaseText(digest: string): Promise<string> { return String(await this.call("GET", `/releases/${encodeURIComponent(digest)}/text`)); }
  async releaseDiff(from: string, to: string): Promise<Patch> {
    return parsePatch(await this.call("GET", `/releases/diff?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`), "/releases/diff", "release diff", throwing("release diff"))!;
  }

  /** `rowLimit` (engine 3.21): how many rows of each operator a preview with `include: ["rows"]` samples (the engine's default is 20, at most 1000) */
  async preview(id: string, body: { graphs?: string[]; include?: string[]; limit?: string; rowLimit?: string; scope?: { maxRows: string } }): Promise<Preview> {
    const o = (await this.call("POST", `/drafts/${id}/preview`, body)) as Record<string, unknown>;
    const reject = throwing("preview");
    const graphs = ((o.graphs ?? []) as Record<string, unknown>[]).map((g): PreviewGraph => ({
      id: String(g.id), removed: g.removed === true, counts: g.counts as PreviewGraph["counts"], digest: g.digest ? String(g.digest) : undefined, truncated: g.truncated === true,
      diff: g.diff ? parsePatch(g.diff, "preview", `diff of ${g.id}`, reject) : null,
      snapshot: g.snapshot ? parseSnapshot(g.snapshot, "preview", reject)?.snapshot : undefined,
      ...(Array.isArray(g.operators) ? { operators: parseOperatorRows(g.operators) } : {}),
    }));
    return { scope: o.scope as Preview["scope"], diagnostics: parseDiagnostics(o.diagnostics), graphs, identity: parseIdentity(o.identity), milliseconds: String(o.milliseconds ?? "") };
  }
  /** Text check of a module (v2 endpoint, unchanged in v3): the engine parses the text and answers the catalog patch it stands for. */
  async checkText(module: string, text: string): Promise<{ ok: boolean; patch: Patch | null; diagnostics: EngineDiagnostic[]; affectedModules: string[] }> {
    const o = (await this.call("POST", "/check", { module, text })) as Record<string, unknown>;
    return { ok: o.ok === true, patch: o.patch ? parsePatch(o.patch, "/check", "check patch", throwing("check")) : null, diagnostics: parseDiagnostics(o.diagnostics), affectedModules: ((o.affected as { modules?: string[] } | undefined)?.modules ?? []).map(String) };
  }
}
