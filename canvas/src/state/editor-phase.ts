// The phase of the projection editor: a closed set of states and a pure transition. Effects send the events, the reducer applies them.
// A draft session is open only in `ready`; `work` is what the session's queue is doing for the author.

export type Work = "undo" | "redo" | "preview" | "commit" | "store" | "command";
export type EditorPhase =
  | { t: "closed" }
  | { t: "opening" }
  | { t: "failed"; message: string }
  | { t: "ready"; work: Work | null };
export type EditorEvent =
  | { t: "open" }
  | { t: "opened" }
  | { t: "failed"; message: string }
  | { t: "start"; work: Work }
  | { t: "done"; work: Work };

/** An event that does not fit the phase changes nothing: a late `opened` after a failure, a `done` of work that another start replaced. */
export function editorTransition(p: EditorPhase, e: EditorEvent): EditorPhase {
  switch (e.t) {
    case "open": return { t: "opening" };
    case "opened": return p.t === "opening" ? { t: "ready", work: null } : p;
    case "failed": return p.t === "opening" ? { t: "failed", message: e.message } : p;
    case "start": return p.t === "ready" ? { t: "ready", work: e.work } : p;
    case "done": return p.t === "ready" && p.work === e.work ? { t: "ready", work: null } : p;
  }
}

/** What the editor says while the work runs. */
export const WORK_LABEL: Record<Work, string> = { undo: "Undoing", redo: "Redoing", preview: "Previewing", commit: "Committing", store: "Storing the text", command: "Changing the draft" };
