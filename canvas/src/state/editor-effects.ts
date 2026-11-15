// Effects of the editor actions: they turn commands into catalog edits (pure, editor/commands.ts), hand them to the draft session
// (which asks the engine and records undo), and report what the author must see as `editor/summary`.
import { Graph } from "../core/graph";
import type { Edit, Patch } from "../core/model";
import { removeEdits } from "../editor/patches";
import { addConstraintEdits, addEquivalenceEdits, addFieldEdits, addRowTypeEdits, addValueRoleEdits, newProjection, setValueRoleEdits, type Result } from "../editor/structures";
import { EditorService } from "../editor/service";
import { EditorSession, type Outcome } from "../editor/session";
import { runtime } from "./runtime";
import { ENGINE_URL } from "./sources";
import type { Action, Command, EditorState, ReleaseDiff, State } from "./types";
import type { Dispatch } from "./effects";

const summary = (s: EditorSession, busy = ""): Partial<EditorState> => ({
  status: "ready", draft: s.draft.draft, base: s.draft.baseRevision, revision: s.revision, version: s.version, diagnostics: s.diagnostics, busy,
  undo: s.undoStack.slice(-5).map((x) => x.label).reverse(), redo: s.redoStack.slice(-5).map((x) => x.label).reverse(), textVersion: s.version,
});

/** Store a projection's KQL text in the draft (the text editor's "Store in draft"): the summary follows; the error when the engine refuses. */
export async function storeKqlText(file: string, text: string, dispatch: Dispatch): Promise<string | null> {
  const s = runtime.session;
  if (!s) return "no draft is open";
  dispatch({ type: "editor/summary", patch: { busy: `storing ${file}` } });
  const o = await s.storeKql(file, text);
  dispatch({ type: "editor/summary", patch: { ...summary(s), notice: o.ok ? "" : o.error } });
  return o.ok ? null : o.error;
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

const headRevision = (): string => { const ws = runtime.ws; const id = ws?.source.catalogGraph; const h = id ? ws!.histories.get(id) : undefined; return h ? h.revisionAt(h.length) : ""; };
const headGraph = (): Graph | null => { const ws = runtime.ws; const id = ws?.source.catalogGraph; return id ? ws!.histories.get(id)!.graph : null; };

async function open(dispatch: Dispatch): Promise<void> {
  // effects run before the reducer: what is dispatched here, before the first await, would be overwritten by `editor/open` ("opening") that is still to be reduced
  await Promise.resolve();
  const ws = runtime.ws;
  if (!ws?.source.catalogGraph) { dispatch({ type: "editor/summary", patch: { status: "error", message: "The projection editor needs the engine (it edits the catalog through drafts). Choose the Engine source." } }); return; }
  try {
    const session = await EditorSession.open(new EditorService(ENGINE_URL));
    runtime.session = session;
    const first = session.catalog.projections().find((p) => p.module !== "sattle.kernel");
    dispatch({ type: "editor/summary", patch: { ...summary(session), headRevision: headRevision(), module: first?.module ?? "", projection: first?.id ?? "" } });
    if (first) void run({ type: "editor/releases", module: first.module }, dispatch);
  } catch (e) { dispatch({ type: "editor/summary", patch: { status: "error", message: (e as Error).message } }); }
}

const need = <T,>(r: Result<T>): T => { if (!r.ok) throw new Error(r.reason); return r.value; };

function plan(cmd: Command, s: EditorSession): { edits: Edit[]; label: string } | null {
  const g = s.graph, pal = s.palette;
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

export async function run(a: Action, dispatch: Dispatch, st?: State): Promise<void> {
  const s = runtime.session;
  const report = (o: Outcome | null) => {
    if (!s) return;
    dispatch({ type: "editor/summary", patch: { ...summary(s), notice: o && !o.ok && o.error !== "nothing to change" ? o.error : "" } });
  };
  try {
    switch (a.type) {
      case "editor/open": await open(dispatch); return;
      case "editor/undo": if (s) { dispatch({ type: "editor/summary", patch: { busy: "undo" } }); report(await s.undo()); } return;
      case "editor/redo": if (s) { dispatch({ type: "editor/summary", patch: { busy: "redo" } }); report(await s.redo()); } return;
      case "editor/discard": {
        if (s) await s.svc.discard(s.draft.draft).catch(() => {});
        runtime.session = null;
        dispatch({ type: "editor/open" });
        return;
      }
      case "editor/releases": { if (s) dispatch({ type: "editor/summary", patch: { releases: await s.svc.releases(a.module) } }); return; }
      case "editor/diffRelease": {
        if (!s) return;
        const p = await s.svc.releaseDiff(a.from, a.to);
        dispatch({ type: "editor/summary", patch: { releaseDiff: summarizePatch("release diff", a.from, a.to, p) } });
        return;
      }
      case "editor/preview": {
        if (!s) return;
        dispatch({ type: "editor/summary", patch: { busy: "preview", preview: null } });
        const pv = await s.svc.preview(s.draft.draft, { graphs: a.graphs, include: ["counts", "diff", "snapshots"], limit: "3000" });
        runtime.preview = pv;
        dispatch({ type: "editor/summary", patch: {
          busy: "", preview: {
            scope: pv.scope, milliseconds: pv.milliseconds, diagnostics: pv.diagnostics, identity: pv.identity,
            graphs: pv.graphs.map((g) => { const d = g.diff ? summarizePatch("", "", "", g.diff) : null; return { id: g.id, removed: g.removed === true, entities: g.counts?.entities ?? "0", relationships: g.counts?.relationships ?? "0", added: d?.added ?? 0, removed_: d?.removed ?? 0, changed: d?.changed ?? 0, truncated: g.truncated === true, hasSnapshot: !!g.snapshot }; }),
          },
        } });
        return;
      }
      case "editor/commit": {
        if (!s || !st) return;
        dispatch({ type: "editor/summary", patch: { busy: "commit" } });
        const module = st.editor.module;
        if (a.version) {
          const t = s.texts[module] ?? "";
          const re = /^(module\s+\S+\s*@\s*)\S+?(\s*;)/m;
          if (!re.test(t)) throw new Error("the module header was not found in the text");
          const next = t.replace(re, `$1${a.version}$2`);
          const head = headGraph();
          if (next !== t && head) { const r = await s.applyText(module, next, head); if (!r.ok) throw new Error(r.error); }
        }
        const done = await s.svc.commit(s.draft.draft, headRevision());
        runtime.session = null;
        dispatch({ type: "editor/open" });
        dispatch({ type: "editor/summary", patch: { committed: `Committed catalog revision ${done.catalogRevision}${done.modules.length ? ` (${done.modules.join(", ")})` : ""}` } });
        return;
      }
      case "editor/do": {
        if (!s || !st) return;
        const cmd = a.cmd;
        dispatch({ type: "editor/summary", patch: { busy: cmd.t } });
        switch (cmd.t) {
          case "text": { const head = headGraph(); report(head ? await s.applyText(cmd.module, cmd.text, head) : null); return; }
          case "restore": {
            const applied = await s.svc.restore(s.draft.draft, cmd.digest);
            await s.absorbRestore(applied, "restore release");
            report(null);
            return;
          }
          case "newProjection": {
            // the file first (the projection and its ports follow from the text), then its graph and bindings in the space
            const p = need(newProjection(s.graph, cmd.spec));
            const stored = await s.storeKql(p.file, p.text);
            if (!stored.ok) { report(stored); return; }
            const o = p.edits.length ? await s.apply(p.edits, `bind ${cmd.spec.name}`) : null;
            report(o);
            if (!o || o.ok) dispatch({ type: "editor/projection", module: cmd.spec.module, projection: `d:${cmd.spec.module}/projection:${cmd.spec.name}` });
            return;
          }
          default: {
            const p = plan(cmd, s);
            if (!p) return;
            report(await s.apply(p.edits, p.label));
            return;
          }
        }
      }
    }
  } catch (e) {
    if (s) dispatch({ type: "editor/summary", patch: { ...summary(s), notice: (e as Error).message } });
    else dispatch({ type: "editor/summary", patch: { status: "error", message: (e as Error).message } });
  }
}
