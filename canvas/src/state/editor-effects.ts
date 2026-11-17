// Effects of the editor actions: they turn commands into catalog edits (pure, editor/commands.ts), hand them to the draft session
// (which asks the engine and records undo), and report what the author must see as `editor/summary`.
import { Graph } from "../core/graph";
import type { Edit, Patch } from "../core/model";
import type { Palette } from "../editor/palette";
import { removeEdits } from "../editor/patches";
import { addConstraintEdits, addEquivalenceEdits, addFieldEdits, addRowTypeEdits, addValueRoleEdits, newProjection, setValueRoleEdits, type Result } from "../editor/structures";
import { EditorService } from "../editor/service";
import { EditorSession, type Outcome } from "../editor/session";
import { runtime } from "./runtime";
import { ENGINE_URL } from "./sources";
import type { Action, Command, EditorState, ReleaseDiff, State } from "./types";
import type { Work } from "./editor-phase";
import type { Dispatch } from "./effects";

const summary = (s: EditorSession): Partial<EditorState> => ({
  draft: s.draft.draft, base: s.draft.baseRevision, revision: s.revision, version: s.version, diagnostics: [...s.diagnostics],
  undo: s.undoStack.slice(-5).map((x) => x.label).reverse(), redo: s.redoStack.slice(-5).map((x) => x.label).reverse(), textVersion: s.version,
});

/**
 * The generation rule (as in source/engine-stream.ts): an editor session ends with a discard, a commit, a new draft or a new source, and the
 * generation moves on. An answer to a call made in an older generation reaches neither the state nor the runtime.
 */
let generation = 0;
/** End the open session: what it still answers is dropped. */
export function endSession(): void { generation++; runtime.session = null; runtime.preview = null; }

/**
 * Work on the session's draft as the author sees it: the phase says what runs, and the summary and the notice follow when it ends, unless the
 * session ended meanwhile. `fn` answers the extra state to report (a preview), or throws the notice.
 */
async function work(s: EditorSession, w: Work, dispatch: Dispatch, fn: () => Promise<Partial<EditorState> | Outcome | null | void>): Promise<void> {
  const g = generation;
  dispatch({ type: "editor/event", event: { t: "start", work: w } });
  let extra: Partial<EditorState> = {};
  try {
    const r = await fn();
    if (r && "ok" in r) extra = { notice: !r.ok && r.error !== "nothing to change" ? r.error : "" };
    else if (r) extra = r;
    else extra = { notice: "" };
  } catch (e) { extra = { notice: (e as Error).message }; }
  if (g !== generation) return;
  dispatch({ type: "editor/summary", patch: { ...summary(s), ...extra } });
  dispatch({ type: "editor/event", event: { t: "done", work: w } });
}

/** Store a projection file's text in the draft (the text editor's "Store in draft"): the summary follows; the error when the engine refuses. */
export async function storeProjectionText(file: string, text: string, dispatch: Dispatch): Promise<string | null> {
  const s = runtime.session;
  if (!s) return "no draft is open";
  let error: string | null = null;
  await work(s, "store", dispatch, async () => { const o = await s.storeText(file, text); error = o.ok ? null : o.error; return o; });
  return error;
}

export function summarizePatch(label: string, from: string, to: string, p: Patch): ReleaseDiff {
  const items: ReleaseDiff["items"] = [];
  const count = { added: 0, removed: 0, changed: 0 };
  for (const e of p.edits) {
    if (e.kind !== "entity") continue;
    const kind = e.before === null ? "added" : e.after === null ? "removed" : "changed";
    count[kind]++;
    if (items.length < 200) items.push({ kind, id: e.id, type: (e.after ?? e.before)!.type });
  }
  return { label, from, to, ...count, items };
}

const headRevision = (): string => { const ws = runtime.ws; const id = ws?.source.catalogGraph; const h = id ? ws!.history(id) : undefined; return h ? h.revisionAt(h.length) : ""; };
const headGraph = (): Graph | null => { const ws = runtime.ws; const id = ws?.source.catalogGraph; return id ? ws!.history(id)!.graph : null; };

async function open(dispatch: Dispatch): Promise<void> {
  // effects run before the reducer: what is dispatched here, before the first await, would be overwritten by `editor/open` ("opening") that is still to be reduced
  const old = runtime.session;
  endSession();
  const g = generation;
  if (old) void old.discard();
  await Promise.resolve();
  const ws = runtime.ws;
  if (!ws?.source.catalogGraph) { if (g === generation) dispatch({ type: "editor/event", event: { t: "failed", message: "The projection editor needs the engine (it edits the catalog through drafts). Choose the Engine source." } }); return; }
  try {
    const session = await EditorSession.open(new EditorService(ENGINE_URL));
    // a newer draft or source took over while this one opened: it is nobody's
    if (g !== generation) { void session.discard(); return; }
    runtime.session = session;
    const first = session.catalog.projections().find((p) => p.module !== "sattle.kernel");
    dispatch({ type: "editor/summary", patch: { ...summary(session), headRevision: headRevision(), module: first?.module ?? "", projection: first?.id ?? "" } });
    dispatch({ type: "editor/event", event: { t: "opened" } });
    if (first) void run({ type: "editor/releases", module: first.module }, dispatch);
  } catch (e) { if (g === generation) dispatch({ type: "editor/event", event: { t: "failed", message: (e as Error).message } }); }
}

const need = <T,>(r: Result<T>): T => { if (!r.ok) throw new Error(r.reason); return r.value; };

function plan(cmd: Command, g: Graph, pal: Palette): { edits: Edit[]; label: string } | null {
  switch (cmd.t) {
    case "delete": return { edits: removeEdits(g, cmd.ids), label: `delete ${cmd.ids.length} element${cmd.ids.length === 1 ? "" : "s"}` };
    case "rowType": return { edits: need(addRowTypeEdits(g, `d:${cmd.module}`, cmd.name, pal)), label: `add rows ${cmd.name}` };
    case "field": return { edits: need(addFieldEdits(g, cmd.rows, cmd.name, cmd.kind, cmd.optional, pal)), label: `add field ${cmd.name}` };
    case "constraint": return { edits: need(addConstraintEdits(g, `d:${cmd.module}`, cmd.kind, cmd.target, cmd.properties, cmd.severity, cmd.attrs, pal)), label: `add ${cmd.kind} constraint` };
    case "equivalence": return { edits: need(addEquivalenceEdits(g, `d:${cmd.module}`, cmd.name, cmd.mode, cmd.threshold, cmd.between, pal)), label: `add equivalence ${cmd.name}` };
    case "valueRole": return { edits: need(addValueRoleEdits(g, cmd.property, cmd.value, cmd.role, pal)), label: `role of ${cmd.value}` };
    case "setValueRole": return { edits: setValueRoleEdits(g, cmd.id, cmd.role), label: "change role" };
    default: return null;
  }
}

/** A command on the draft: its edits are composed from the session's graph when the queue reaches it, so they start from the revision they apply to. */
async function perform(cmd: Command, s: EditorSession, dispatch: Dispatch): Promise<Outcome | null | void> {
  switch (cmd.t) {
    case "text": { const head = headGraph(); return head ? s.applyText(cmd.module, cmd.text, head) : null; }
    case "restore": return s.restore(cmd.digest);
    case "newProjection": {
      const { spec } = cmd;
      const o = await s.change((g) => { const p = need(newProjection(g, spec)); return { edits: p.edits, label: `bind ${spec.name}`, file: { ...p.file, text: p.text } }; });
      if (o?.ok && runtime.session === s) dispatch({ type: "editor/projection", module: spec.module, projection: `d:${spec.module}/projection:${spec.name}` });
      return o;
    }
    default: return s.change((g) => plan(cmd, g, s.palette));
  }
}

export async function run(a: Action, dispatch: Dispatch, st?: State): Promise<void> {
  if (a.type === "editor/open") return open(dispatch);
  const s = runtime.session;
  if (!s) return;
  const g = generation;
  switch (a.type) {
    case "editor/undo": return work(s, "undo", dispatch, () => s.undo());
    case "editor/redo": return work(s, "redo", dispatch, () => s.redo());
    case "editor/do": return work(s, "command", dispatch, () => perform(a.cmd, s, dispatch));
    case "editor/discard": { dispatch({ type: "editor/open" }); return; }
    // read only: no queue, but an answer of an older generation is dropped
    case "editor/releases": {
      try {
        const releases = await s.svc.releases(a.module);
        if (g === generation) dispatch({ type: "editor/summary", patch: { releases } });
      } catch (e) { if (g === generation) dispatch({ type: "editor/summary", patch: { notice: (e as Error).message } }); }
      return;
    }
    case "editor/diffRelease": {
      try {
        const p = await s.svc.releaseDiff(a.from, a.to);
        if (g === generation) dispatch({ type: "editor/summary", patch: { releaseDiff: summarizePatch("release diff", a.from, a.to, p) } });
      } catch (e) { if (g === generation) dispatch({ type: "editor/summary", patch: { notice: (e as Error).message } }); }
      return;
    }
    case "editor/preview": {
      dispatch({ type: "editor/summary", patch: { preview: null } });
      return work(s, "preview", dispatch, async () => {
        const pv = await s.preview({ graphs: a.graphs, include: ["counts", "diff", "snapshots"], limit: "3000" });
        if (g !== generation) return;
        runtime.preview = pv;
        return { notice: "", preview: {
          scope: pv.scope, milliseconds: pv.milliseconds, diagnostics: pv.diagnostics, identity: pv.identity,
          graphs: pv.graphs.map((x) => { const d = x.diff ? summarizePatch("", "", "", x.diff) : null; return { id: x.id, removed: x.removed === true, entities: x.counts?.entities ?? "0", relationships: x.counts?.relationships ?? "0", added: d?.added ?? 0, removed_: d?.removed ?? 0, changed: d?.changed ?? 0, truncated: x.truncated === true, hasSnapshot: !!x.snapshot }; }),
        } };
      });
    }
    case "editor/commit": {
      if (!st) return;
      const head = headGraph();
      return work(s, "commit", dispatch, async () => {
        if (!head) throw new Error("the catalog head is not loaded");
        const done = await s.commit(st.editor.module, a.version, head, headRevision());
        if (g !== generation) return;
        // the session ends with the commit (the engine has no draft left to discard); the next draft opens and the commit is said there
        endSession();
        dispatch({ type: "editor/open" });
        dispatch({ type: "editor/summary", patch: { committed: `Committed catalog revision ${done.catalogRevision}${done.modules.length ? ` (${done.modules.join(", ")})` : ""}` } });
      });
    }
  }
}
