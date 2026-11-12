// One editing session: a draft on the engine, the browser's copy of its catalog graph, and the undo and redo stacks.
// Undo and redo are inverse edits applied to the draft as ordinary patches (PROTOCOL.md, drafts): nothing rewinds a log.
import { diffGraphs } from "../core/diff";
import { Catalog, moduleOfId } from "../core/catalog";
import { Graph } from "../core/graph";
import type { Edit, Patch } from "../core/model";
import type { EngineDiagnostic } from "../source/types";
import { coalesce, invertEdits } from "./patches";
import type { Palette } from "./palette";
import { EngineError, type Applied, type Draft, type EditorService, type Refactoring } from "./service";

export const MAX_UNDO = 99;

/** `consumed`: the placeholders this step replaced or changed; undoing the step makes them placeholders again */
export interface Step { label: string; edits: Edit[]; consumed?: string[] }
export type Outcome = { ok: true; applied: Applied } | { ok: false; error: string; code?: string; element?: string };

export class EditorSession {
  /**
   * The ids of the children that skeletons made only so that a new operator prints (engine 3.19), as far as they are still that: the first real
   * entry of the slot replaces exactly these. Nothing in the graph says it, so the session remembers; after a reload none are known.
   */
  readonly placeholders = new Set<string>();
  readonly undoStack: Step[] = [];
  readonly redoStack: Step[] = [];
  diagnostics: EngineDiagnostic[] = [];
  /** canonical text per module, as of the last answer */
  texts: Record<string, string> = {};
  /** bumped by every change of the graph, so views can tell */
  version = 0;
  private cat: { version: number; catalog: Catalog } | null = null;

  private constructor(readonly svc: EditorService, readonly palette: Palette, public draft: Draft, public graph: Graph, public revision: string) {}

  static async open(svc: EditorService, base?: string): Promise<EditorSession> {
    const [palette, draft] = await Promise.all([svc.palette(), svc.createDraft(base)]);
    const snap = await svc.snapshot(draft.draft);
    const s = new EditorSession(svc, palette, draft, Graph.fromSnapshot(snap), draft.revision);
    const checked = await svc.check(draft.draft);
    s.absorb(checked);
    return s;
  }

  get catalog(): Catalog {
    if (!this.cat || this.cat.version !== this.version) this.cat = { version: this.version, catalog: new Catalog(this.graph) };
    return this.cat.catalog;
  }
  get ok(): boolean { return !this.diagnostics.some((d) => d.severity === "error"); }

  private absorb(a: Pick<Applied, "diagnostics" | "modules">): void {
    this.diagnostics = a.diagnostics;
    for (const [m, v] of Object.entries(a.modules)) if (v.text !== undefined) this.texts[m] = v.text;
  }

  private patchOf(edits: Edit[]): Patch {
    const v = (rev: string) => ({ tenant: this.graph.version?.tenant ?? "", graph: this.graph.version?.graph ?? "catalog", revision: rev });
    return { from: v(this.revision), to: v(String(Number(this.revision) + 1)), projection: "catalog", profile: "extensionalFinite", edits, cause: { inputs: [], configurationVersion: "0" } };
  }

  /** Commands one after the other: two started together (a double click on Undo) read the stack and the revision one after the other, not both from the same state. */
  private tail: Promise<unknown> = Promise.resolve();
  private serial<T>(fn: () => Promise<T>): Promise<T> {
    const run = this.tail.then(fn, fn);
    this.tail = run.catch(() => {});
    return run;
  }

  /** Apply edits to the draft. `record` false is for undo and redo themselves. A rejected patch changes nothing, here or on the engine. */
  private async send(edits: Edit[]): Promise<Outcome> {
    if (edits.length === 0) return { ok: false, error: "nothing to change" };
    const patch = this.patchOf(edits);
    try {
      const applied = await this.svc.applyPatch(this.draft.draft, patch, this.revision);
      const res = this.graph.apply(patch);
      if (!res.ok) { await this.resyncNow(); return { ok: false, error: `the browser copy of the draft was out of step (${res.conflicts[0]?.message}); reloaded it` }; }
      this.revision = applied.revision;
      this.version++;
      this.absorb(applied);
      return { ok: true, applied };
    } catch (e) {
      if (e instanceof EngineError) { if (e.code === "StaleRevision" || e.code === "GuardFailed") await this.resyncNow().catch(() => {}); return { ok: false, error: e.message, code: e.code, element: e.element }; }
      return { ok: false, error: (e as Error).message };
    }
  }

  apply(all: Edit[], label: string): Promise<Outcome> { return this.serial(() => this.applyNow(all, label)); }
  private async applyNow(all: Edit[], label: string): Promise<Outcome> {
    // a placeholder that an edit removes or changes is no placeholder any more: an element of the same id that comes later is the author's.
    // Read before the edits are coalesced: an author who writes exactly what the placeholder is (`n = count()`) removes it and creates it
    // again, which is no change of the graph and still makes the element theirs.
    const consumed = [...new Set(all.flatMap((e) => (e.kind === "entity" && e.before !== null && this.placeholders.has(e.id) ? [e.id] : [])))];
    const edits = coalesce(all);
    if (!edits.length && consumed.length) {
      for (const id of consumed) this.placeholders.delete(id);
      return { ok: true, applied: { revision: this.revision, ok: this.ok, diagnostics: this.diagnostics, modules: {} } };
    }
    const r = await this.send(edits);
    if (r.ok) {
      for (const id of consumed) this.placeholders.delete(id);
      this.undoStack.push({ label, edits, ...(consumed.length ? { consumed } : {}) });
      if (this.undoStack.length > MAX_UNDO) this.undoStack.shift();
      this.redoStack.length = 0;
    }
    return r;
  }
  undo(): Promise<Outcome | null> { return this.serial(() => this.undoNow()); }
  private async undoNow(): Promise<Outcome | null> {
    const step = this.undoStack[this.undoStack.length - 1];
    if (!step) return null;
    const r = await this.send(invertEdits(step.edits));
    if (r.ok) { this.undoStack.pop(); this.redoStack.push(step); for (const id of step.consumed ?? []) this.placeholders.add(id); }
    return r;
  }
  redo(): Promise<Outcome | null> { return this.serial(() => this.redoNow()); }
  private async redoNow(): Promise<Outcome | null> {
    const step = this.redoStack[this.redoStack.length - 1];
    if (!step) return null;
    const r = await this.send(step.edits);
    if (r.ok) { this.redoStack.pop(); this.undoStack.push(step); for (const id of step.consumed ?? []) this.placeholders.delete(id); }
    return r;
  }

  /** A change the engine computed (refactoring, restore): the patch is applied to the draft like any other, so it can be undone. */
  applyRefactoring(r: Refactoring, label: string): Promise<Outcome> { return this.apply(r.patch.edits, label); }
  /** The engine applied a restore itself and answered the patch: it joins the draft like any other, so it can be undone. An empty patch is no step. */
  absorbRestore(applied: Applied & { patch: Patch }, label: string): Promise<void> { return this.serial(() => this.absorbRestoreNow(applied, label)); }
  private async absorbRestoreNow(applied: Applied & { patch: Patch }, label: string): Promise<void> {
    const edits = applied.patch.edits;
    // an empty patch still moves the revision: the browser copy follows it, or the next edit starts from a revision the engine has left
    const r = this.graph.apply(applied.patch);
    if (!r.ok) await this.resyncNow();
    if (edits.length) {
      this.undoStack.push({ label, edits });
      if (this.undoStack.length > MAX_UNDO) this.undoStack.shift();
      this.redoStack.length = 0;
    }
    this.revision = applied.revision;
    this.version++;
    this.absorb(applied);
  }

  resync(): Promise<void> { return this.serial(() => this.resyncNow()); }
  private async resyncNow(): Promise<void> {
    const snap = await this.svc.snapshot(this.draft.draft);
    this.graph = Graph.fromSnapshot(snap);
    this.revision = snap.version.revision;
    this.version++;
    this.absorb(await this.svc.check(this.draft.draft));
  }

  /**
   * Text to graph: the engine parses the module text; the draft is edited by the difference between its graph and the parsed one
   * (restricted to that module's elements). One step on the undo stack.
   */
  applyText(module: string, text: string, head: Graph): Promise<Outcome> { return this.serial(() => this.applyTextNow(module, text, head)); }
  private async applyTextNow(module: string, text: string, head: Graph): Promise<Outcome> {
    const checked = await this.svc.checkText(module, text);
    if (!checked.ok || !checked.patch) { this.diagnostics = checked.diagnostics; return { ok: false, error: checked.diagnostics[0]?.message ?? "the engine rejects this text" }; }
    const parsed = Graph.fromSnapshot(head.toSnapshot());
    const r = parsed.apply(checked.patch);
    if (!r.ok) return { ok: false, error: "the checked patch does not apply to the catalog head" };
    const change = diffGraphs(this.graph, parsed);
    // the edited module is replaced by what its text says. The modules the text affects (their import pins are rewritten) are not: `parsed`
    // comes from the committed catalog, so everything the draft added there (the graphs and bindings of a new projection live in the space
    // module) would read as removed. Of those modules only the check patch's own edits are taken, rebased on the draft's current state.
    const touched = new Set([module, ...checked.affectedModules]);
    const own = (id: string) => moduleOfId(id) === module;
    const rels = [...change.relationships].filter(([id]) => own(id)).map(([id, c]): Edit => ({ kind: "relationship", id, before: c.before, after: c.after }));
    const ents = [...change.entities].filter(([id]) => own(id)).map(([id, c]): Edit => ({ kind: "entity", id, before: c.before, after: c.after }));
    for (const e of checked.patch.edits) {
      if (e.kind !== "entity" && e.kind !== "relationship") continue;
      if (own(e.id) || !touched.has(moduleOfId(e.id))) continue;
      const before = e.kind === "entity" ? this.graph.entity(e.id) ?? null : this.graph.rel(e.id) ?? null;
      if (!before && !e.after) continue;
      (e.kind === "entity" ? ents : rels).push({ ...e, before } as Edit);
    }
    const gone = (e: Edit) => (e.kind === "entity" || e.kind === "relationship") && e.after === null;
    const edits: Edit[] = [...rels.filter(gone), ...ents.filter(gone), ...ents.filter((e) => !gone(e)), ...rels.filter((e) => !gone(e))];
    return edits.length ? this.applyNow(edits, "edit text") : { ok: false, error: "the text equals the draft" };
  }
}
