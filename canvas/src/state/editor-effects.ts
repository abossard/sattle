// Effects of the editor actions: they turn commands into catalog edits (pure, editor/commands.ts), hand them to the draft session
// (which asks the engine and records undo), and report what the author must see as `editor/summary`.
import { Graph } from "../core/graph";
import type { Edit, Patch } from "../core/model";
import { addParamChildEdits, addValuesRowEdits, addExprItemEdits, addNamedEdits, addSetEdits, setOfTypeEdits, addPatternEdgeEdits, addPatternNodeEdits, addReadEdits, addRuleEdits, paramEdits, pasteEdits, setExprEdits, setRuleClassEdits, unwireEdits, wireEdits, type Result } from "../editor/commands";
import { removeEdits } from "../editor/patches";
import { setRefEdits } from "../editor/refs";
import { addBindingEdits, setPortDefaultEdits, addValueRoleEdits, bindParamEdits, setValueRoleEdits, newProjectionEdits, addConstraintEdits, addEquivalenceEdits, addFieldEdits, addRowTypeEdits, addRuleSetEdits, governsEdits, setCreateKeyEdits, setPolicyEdits, setRulePropEdits } from "../editor/structures";
import { paramOf } from "../editor/palette";
import { EditorService } from "../editor/service";
import { EditorSession, type Outcome } from "../editor/session";
import { compose } from "../editing/gestures";
import { remember } from "../editing/draft";
import { sessionDraft } from "../editing/session-draft";
import { runtime } from "./runtime";
import { ENGINE_URL } from "./sources";
import type { Action, Command, EditorState, ReleaseDiff, State } from "./types";
import type { Dispatch } from "./effects";

const summary = (s: EditorSession, busy = ""): Partial<EditorState> => ({
  status: "ready", draft: s.draft.draft, base: s.draft.baseRevision, revision: s.revision, version: s.version, diagnostics: s.diagnostics, busy,
  undo: s.undoStack.slice(-5).map((x) => x.label).reverse(), redo: s.redoStack.slice(-5).map((x) => x.label).reverse(), textVersion: s.version,
});

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

function plan(cmd: Command, s: EditorSession, st: State): { edits: Edit[]; label: string; select?: string[] } | null {
  const g = s.graph, pal = s.palette, proj = st.editor.projection;
  switch (cmd.t) {
    case "delete": return { edits: removeEdits(g, cmd.ids), label: `delete ${cmd.ids.length} element${cmd.ids.length === 1 ? "" : "s"}`, select: [] };
    case "wire": return { edits: wireEdits(cmd.from, cmd.to), label: "connect" };
    case "unwire": return { edits: unwireEdits(g, cmd.key), label: "disconnect" };
    case "param": { const p = paramOf(pal, g.entity(cmd.id)?.type ?? "", cmd.name); if (!p) throw new Error(`${cmd.name} is not a parameter of this element`); return { edits: need(paramEdits(g, cmd.id, p, cmd.value)), label: `set ${cmd.name}` }; }
    case "child": return { edits: need(addParamChildEdits(g, cmd.id, cmd.slot, cmd.childType, cmd.key, cmd.params, pal)), label: `add ${cmd.key}` };
    case "expr": return { edits: need(setExprEdits(g, cmd.id, cmd.slot, cmd.src, pal)), label: `set ${cmd.slot}` };
    case "named": return { edits: need(addNamedEdits(g, cmd.id, cmd.slot, cmd.childType, cmd.name, cmd.src, pal, cmd.extra, s.placeholders)), label: `add ${cmd.name}` };
    case "set": return { edits: need(addSetEdits(g, cmd.id, cmd.property, cmd.src, pal, cmd.slot ?? "set")), label: "set property" };
    case "ref": return { edits: need(setRefEdits(g, cmd.id, cmd.rel, cmd.target)), label: `set ${cmd.rel}` };
    case "ofType": return { edits: need(setOfTypeEdits(g, cmd.id, cmd.type)), label: "set type" };
    case "newProjection": return { edits: need(newProjectionEdits(g, cmd.spec, pal)), label: `new projection ${cmd.spec.name}` };
    case "valuesRow": return { edits: need(addValuesRowEdits(g, cmd.id, cmd.cells, pal)), label: "add row" };
    case "rowType": return { edits: need(addRowTypeEdits(g, `d:${cmd.module}`, cmd.name, pal)), label: `add rows ${cmd.name}` };
    case "field": return { edits: need(addFieldEdits(g, cmd.rows, cmd.name, cmd.kind, cmd.optional, pal)), label: `add field ${cmd.name}` };
    case "constraint": return { edits: need(addConstraintEdits(g, `d:${cmd.module}`, cmd.kind, cmd.target, cmd.properties, cmd.severity, cmd.attrs, pal)), label: `add ${cmd.kind} constraint` };
    case "equivalence": return { edits: need(addEquivalenceEdits(g, `d:${cmd.module}`, cmd.name, cmd.mode, cmd.threshold, cmd.between, pal)), label: `add equivalence ${cmd.name}` };
    case "policy": return { edits: setPolicyEdits(g, cmd.id, cmd.policy), label: "set policy" };
    case "ruleProp": return { edits: need(setRulePropEdits(g, cmd.id, cmd.name, cmd.value, pal)), label: `set ${cmd.name}` };
    case "createKey": return { edits: need(setCreateKeyEdits(g, cmd.id, cmd.domain)), label: "set create key" };
    case "governs": return { edits: need(governsEdits(g, cmd.id, cmd.target, cmd.on)), label: cmd.on ? "govern" : "stop governing" };
    case "ruleSet": return { edits: need(addRuleSetEdits(g, cmd.id, cmd.slot, cmd.property, cmd.src, pal)), label: `add ${cmd.slot}` };
    case "read": return { edits: need(addReadEdits(g, cmd.id, cmd.path, cmd.slot ?? "read", s.placeholders)), label: "add read" };
    case "exprAdd": return { edits: need(addExprItemEdits(g, cmd.id, cmd.slot, cmd.src, pal)), label: `add to ${cmd.slot}` };
    case "valueRole": return { edits: need(addValueRoleEdits(g, cmd.property, cmd.value, cmd.role, pal)), label: `role of ${cmd.value}` };
    case "setValueRole": return { edits: setValueRoleEdits(g, cmd.id, cmd.role), label: "change role" };
    case "bindNew": return { edits: need(addBindingEdits(g, cmd.port, cmd.graph, cmd.value)), label: "bind parameter" };
    case "portDefault": return { edits: need(setPortDefaultEdits(g, cmd.port, cmd.value)), label: "set default" };
    case "bindParam": return { edits: need(bindParamEdits(g, cmd.binding, cmd.value)), label: "bind parameter" };
    case "patternNode": return { edits: need(addPatternNodeEdits(g, cmd.id, cmd.variable, cmd.type)), label: `add node ${cmd.variable}` };
    case "patternEdge": return { edits: need(addPatternEdgeEdits(g, cmd.id, cmd.variable, cmd.from, cmd.to, cmd.type)), label: "add edge" };
    case "rule": return { edits: need(addRuleEdits(g, proj, cmd.name, cmd.cls, cmd.reason, cmd.governs)), label: `add rule ${cmd.name}`, select: [`${proj}/rule:${cmd.name}`] };
    case "ruleClass": return { edits: setRuleClassEdits(g, cmd.id, cmd.cls), label: "set rule class" };
    case "paste": { if (!runtime.clipboard.length) throw new Error("nothing copied"); return { edits: pasteEdits(g, proj, runtime.clipboard), label: "paste" }; }
    default: return null;
  }
}

export async function run(a: Action, dispatch: Dispatch, st?: State): Promise<void> {
  const s = runtime.session;
  const report = (o: Outcome | null, select?: string[]) => {
    if (!s) return;
    dispatch({ type: "editor/summary", patch: { ...summary(s), notice: o && !o.ok && o.error !== "nothing to change" ? o.error : "", ...(select ? { selected: select } : {}) } });
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
      case "editor/gesture": {
        // a gesture of one of the notations: composed on a copy of the draft (editing/gestures.ts), sent as one patch, one undo step
        if (!s || !st) return;
        dispatch({ type: "editor/summary", patch: { busy: "edit" } });
        const composed = await compose(s.graph, s.palette, st.editor.projection, a.gesture, sessionDraft(s).skeleton, s.placeholders);
        if (!composed.ok) { dispatch({ type: "editor/summary", patch: { ...summary(s), notice: composed.reason } }); return; }
        const applied = composed.value.edits.length ? await s.apply(composed.value.edits, composed.value.label) : null;
        if (!applied || applied.ok) remember(s.placeholders, composed.value.placeholders);
        report(applied, composed.value.select.length ? composed.value.select : undefined);
        return;
      }
      case "editor/do": {
        if (!s || !st) return;
        const cmd = a.cmd;
        dispatch({ type: "editor/summary", patch: { busy: cmd.t } });
        switch (cmd.t) {
          case "copy": runtime.clipboard = cmd.ids; dispatch({ type: "editor/summary", patch: { busy: "", notice: `Copied ${cmd.ids.length} operator${cmd.ids.length === 1 ? "" : "s"}` } }); return;
          case "addOperator": {
            const base = cmd.name || cmd.kind.toLowerCase();
            let name = base;
            for (let i = 2; s.graph.entity(`${st.editor.projection}/op:${name}`); i++) name = `${base}${i}`;
            // parameters the palette gives a default (a lift's `within`) start at it, not at the skeleton's placeholder
            const defaults = Object.fromEntries((s.palette.operators.find((o) => o.kind === cmd.kind)?.params ?? []).filter((x) => x.default !== undefined).map((x) => [x.name, x.default!]));
            const p = await s.svc.skeleton(s.draft.draft, cmd.kind, st.editor.projection, name, defaults);
            const added = await s.apply(p.edits, `add ${cmd.kind}`);
            if (added.ok) for (const id of p.placeholders) s.placeholders.add(id); // what the next real entry of a slot replaces
            report(added, [`${st.editor.projection}/op:${name}`]);
            return;
          }
          case "text": { const head = headGraph(); report(head ? await s.applyText(cmd.module, cmd.text, head) : null); return; }
          case "restore": {
            const applied = await s.svc.restore(s.draft.draft, cmd.digest);
            await s.absorbRestore(applied, "restore release");
            report(null);
            return;
          }
          case "refactor": {
            const r = await s.svc.refactor(s.draft.draft, cmd.req);
            runtime.refactoring = { plan: r, label: cmd.req.kind };
            dispatch({ type: "editor/summary", patch: { busy: "", pending: summarizePatch(cmd.req.kind, "", "", r.patch) } });
            return;
          }
          case "applyRefactor": {
            const pending = runtime.refactoring;
            if (!pending) return;
            runtime.refactoring = null;
            const o = await s.applyRefactoring(pending.plan, pending.label);
            dispatch({ type: "editor/summary", patch: { pending: null } });
            report(o, []);
            return;
          }
          default: {
            const p = plan(cmd, s, st);
            if (!p) return;
            const o = await s.apply(p.edits, p.label);
            report(o, p.select);
            if (cmd.t === "newProjection" && o.ok) dispatch({ type: "editor/projection", module: cmd.spec.module, projection: `d:${cmd.spec.module}/projection:${cmd.spec.name}` });
            if (cmd.t === "paste") {
              // select the pasted operators
              const added = p.edits.filter((e) => e.kind === "entity" && e.before === null && /\/op:[^/]+$/.test((e as { id: string }).id) && !(e as { id: string }).id.includes("#")).map((e) => (e as { id: string }).id);
              dispatch({ type: "editor/select", ids: added });
            }
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
