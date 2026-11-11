// The editor side of the engine protocol (v3): palette, drafts, releases, preview and refactoring. HTTP only.
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
export interface Applied {
  /** restore: the patch the engine applied */
  patch?: Patch; revision: string; ok: boolean; diagnostics: EngineDiagnostic[]; modules: Record<string, { text: string; changed?: boolean }> }
export interface Release { module: string; version: string; digest: string; catalogRevision: string; supersedes: string | null }
export interface PreviewGraph { id: string; removed?: boolean; counts: { entities: string; relationships: string; telemetryWindows: string }; digest?: string; diff: Patch | null; snapshot?: Snapshot; truncated?: boolean }
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
export interface Refactoring { patch: Patch; modules: Record<string, { text: string }>; preserves: unknown }
export type RefactorRequest =
  | { kind: "extract"; module: string; projection: string; select: string[]; name: string }
  | { kind: "inline"; module: string; projection: string; into: string }
  | { kind: "moveRule"; module: string; from: string; to: string; rule: string };

const throwing = (where: string) => (r: { source: string; where: string; errors: string[] }) => { throw new Error(`${where}: ${r.where}: ${r.errors.join("; ")}`); };

export class EditorService {
  private readonly base: string;
  constructor(base: string, private readonly f: typeof fetch = (...a) => fetch(...a)) { this.base = new URL(base, globalThis.location?.href ?? "http://localhost/").href.replace(/\/$/, ""); }

  private async call(method: string, path: string, body?: unknown): Promise<unknown> {
    let res: Response;
    try { res = await this.f(this.base + path, body === undefined ? { method } : { method, headers: { "content-type": "application/json" }, body: JSON.stringify(body) }); } catch (e) { throw new EngineError("Unreachable", (e as Error).message, 0); }
    const raw = await res.text();
    let json: unknown = raw;
    if ((res.headers.get("content-type") ?? "").includes("json") || /^[{[]/.test(raw)) { try { json = JSON.parse(raw); } catch { /* text */ } }
    if (!res.ok) {
      const e = ((json as { error?: Record<string, unknown> })?.error ?? {}) as Record<string, unknown>;
      throw new EngineError(String(e.code ?? `HTTP${res.status}`), String(e.message ?? raw.slice(0, 120)), res.status, e.element ? String(e.element) : undefined, parseDiagnostics(e.diagnostics));
    }
    return json;
  }
  async palette(): Promise<Palette> { return parsePalette(await this.call("GET", "/palette")); }
  /** The patch that creates an operator with its ports and placeholders for required slots, starting at the draft's revision. */
  async skeleton(draft: string, kind: string, container: string, name: string, params: Record<string, string> = {}): Promise<Patch> {
    const p = parsePatch(await this.call("POST", `/drafts/${draft}/skeleton`, { kind, container, name, params }), "skeleton", "skeleton", throwing("skeleton"));
    return p!;
  }

  async createDraft(base?: string): Promise<Draft> { const d = (await this.call("POST", "/drafts", base ? { base } : {})) as Draft; return { draft: String(d.draft), baseRevision: String(d.baseRevision), revision: String(d.revision) }; }
  async discard(id: string): Promise<void> { await this.call("DELETE", `/drafts/${id}`); }
  async snapshot(id: string): Promise<Snapshot> {
    const s = parseSnapshot(await this.call("GET", `/drafts/${id}/snapshot`), `/drafts/${id}/snapshot`, throwing("draft snapshot"));
    if (!s) throw new Error("draft snapshot is not valid");
    return s.snapshot;
  }
  private applied(raw: unknown): Applied {
    const o = raw as Record<string, unknown>;
    return { revision: String(o.revision), ok: o.ok === true, diagnostics: parseDiagnostics(o.diagnostics), modules: (o.modules ?? {}) as Applied["modules"] };
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
  async draftText(id: string, module: string): Promise<string> { return String(await this.call("GET", `/drafts/${id}/text/${encodeURIComponent(module)}`)); }

  async preview(id: string, body: { graphs?: string[]; include?: string[]; limit?: string; scope?: { maxRows: string } }): Promise<Preview> {
    const o = (await this.call("POST", `/drafts/${id}/preview`, body)) as Record<string, unknown>;
    const reject = throwing("preview");
    const graphs = ((o.graphs ?? []) as Record<string, unknown>[]).map((g): PreviewGraph => ({
      id: String(g.id), removed: g.removed === true, counts: g.counts as PreviewGraph["counts"], digest: g.digest ? String(g.digest) : undefined, truncated: g.truncated === true,
      diff: g.diff ? parsePatch(g.diff, "preview", `diff of ${g.id}`, reject) : null,
      snapshot: g.snapshot ? parseSnapshot(g.snapshot, "preview", reject)?.snapshot : undefined,
    }));
    return { scope: o.scope as Preview["scope"], diagnostics: parseDiagnostics(o.diagnostics), graphs, identity: parseIdentity(o.identity), milliseconds: String(o.milliseconds ?? "") };
  }
  async refactor(id: string, req: RefactorRequest): Promise<Refactoring> {
    const o = (await this.call("POST", `/drafts/${id}/refactor`, req)) as Record<string, unknown>;
    return { patch: parsePatch(o.patch, "refactor", "refactoring patch", throwing("refactor"))!, modules: (o.modules ?? {}) as Refactoring["modules"], preserves: o.preserves };
  }
  /** Text check of a module (v2 endpoint, unchanged in v3): the engine parses the text and answers the catalog patch it stands for. */
  async checkText(module: string, text: string): Promise<{ ok: boolean; patch: Patch | null; diagnostics: EngineDiagnostic[]; affectedModules: string[] }> {
    const o = (await this.call("POST", "/check", { module, text })) as Record<string, unknown>;
    return { ok: o.ok === true, patch: o.patch ? parsePatch(o.patch, "/check", "check patch", throwing("check")) : null, diagnostics: parseDiagnostics(o.diagnostics), affectedModules: ((o.affected as { modules?: string[] } | undefined)?.modules ?? []).map(String) };
  }
}
