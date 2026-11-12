// The engine's draft session as the draft the editors work on: its graph, its palette, the engine's skeleton of an operator with
// the placeholders it names, and its one way to change the draft (a patch with an undo step).
import type { EditorSession } from "../editor/session";
import type { DraftPort } from "./draft";

export function sessionDraft(s: EditorSession): DraftPort {
  return {
    get graph() { return s.graph; },
    palette: s.palette,
    get diagnostics() { return s.diagnostics; },
    placeholders: s.placeholders,
    skeleton: async (kind, container, name, params) => { const p = await s.svc.skeleton(s.draft.draft, kind, container, name, params); return { edits: p.edits, placeholders: p.placeholders }; },
    apply: async (edits, label) => { const r = await s.apply(edits, label); return r.ok ? { ok: true } : { ok: false, error: r.error }; },
  };
}
