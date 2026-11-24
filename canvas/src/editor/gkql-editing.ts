// The GKQL editor as one state machine: the program text and the last good program, undo and redo over every edit, the selected element
// (a focus as the engine names it: `verb:ID` or `wire:FROM>TO.PORT`), the cards the author moved and the connect gesture. The graph, the
// outline and the text only send events; `step` is pure, so each view shows the same program and the same selection.
import { connect, printGkql, readGkql, removeFocus, verbFocus, wireFocus, type GkqlProgram } from "./gkql";

export type Position = { x: number; y: number };

export interface GkqlModel {
  text: string;
  /** the program of the last text that read as one; the graph and the outline show it */
  program: GkqlProgram | null;
  /** why `text` is not a program, or null */
  error: string | null;
  past: readonly string[];
  future: readonly string[];
  /** the last step was typing: the next keystroke joins its undo step */
  typing: boolean;
  selected: string | null;
  /** cards placed by the author: layout only, never in the program text */
  moved: Readonly<Record<string, Position>>;
  gesture: { t: "idle" } | { t: "connecting"; from: string };
  /** why the last gesture or edit did nothing, or null */
  notice: string | null;
}

export type GkqlEvent =
  | { t: "load"; text: string }
  | { t: "type"; text: string }
  | { t: "replace"; text: string }
  | { t: "change"; program: GkqlProgram; select?: string }
  | { t: "remove"; focus: string }
  | { t: "undo" } | { t: "redo" }
  | { t: "select"; focus: string | null }
  | { t: "move"; id: string; to: Position }
  | { t: "connect-start"; from: string }
  | { t: "connect-end"; to: string }
  | { t: "connect-cancel" };

const read = (text: string): { program: GkqlProgram } | { error: string } => {
  try { return { program: readGkql(text) }; } catch (e) { return { error: (e as Error).message }; }
};

/** A model of `text` with empty history. */
export function openModel(text: string): GkqlModel {
  const r = read(text);
  return { text, program: "program" in r ? r.program : null, error: "error" in r ? r.error : null, past: [], future: [], typing: false, selected: null, moved: {}, gesture: { t: "idle" }, notice: null };
}

/** The selection, if it still names an element of `program`. */
const keep = (program: GkqlProgram | null, focus: string | null) =>
  focus && program && (program.verbs.some((v) => verbFocus(v.id) === focus) || program.wires.some((w) => wireFocus(w) === focus)) ? focus : null;

/** `text` as the new current text; a text that is not a program keeps the last good program. */
function withText(m: GkqlModel, text: string): GkqlModel {
  const r = read(text);
  const program = "program" in r ? r.program : m.program;
  return { ...m, text, program, error: "error" in r ? r.error : null, selected: keep(program, m.selected), notice: null };
}

/** A new undo step: the current text goes on the past, the future is cleared. */
const edit = (m: GkqlModel, text: string): GkqlModel => text === m.text ? m : { ...withText(m, text), past: [...m.past, m.text], future: [], typing: false };

export function step(m: GkqlModel, e: GkqlEvent): GkqlModel {
  switch (e.t) {
    case "load": return { ...openModel(e.text), moved: m.moved };
    case "type": {
      if (e.text === m.text) return m;
      const next = withText(m, e.text);
      return m.typing ? { ...next, future: [], typing: true } : { ...next, past: [...m.past, m.text], future: [], typing: true };
    }
    case "replace": return edit(m, e.text);
    case "change": case "remove": case "connect-end":
      // an edit of the graph prints the last good program: it would drop what the author typed
      if (m.error !== null) return { ...m, gesture: { t: "idle" }, notice: "the text is not a program: fix it in the GKQL tab, or undo, before editing the graph" };
      return structural(m, e);
    case "undo": {
      const text = m.past.at(-1);
      return text === undefined ? m : { ...withText(m, text), past: m.past.slice(0, -1), future: [m.text, ...m.future], typing: false };
    }
    case "redo": {
      const text = m.future[0];
      return text === undefined ? m : { ...withText(m, text), past: [...m.past, m.text], future: m.future.slice(1), typing: false };
    }
    case "select": return { ...m, selected: keep(m.program, e.focus), typing: false, notice: null };
    case "move": return { ...m, moved: { ...m.moved, [e.id]: { x: Math.max(0, Math.round(e.to.x)), y: Math.max(0, Math.round(e.to.y)) } } };
    case "connect-start": return m.program?.verbs.some((v) => v.id === e.from) ? { ...m, gesture: { t: "connecting", from: e.from }, notice: null } : m;
    case "connect-cancel": return { ...m, gesture: { t: "idle" } };
  }
}

/** The edits of the program's structure, made on the last good program. */
function structural(m: GkqlModel, e: Extract<GkqlEvent, { t: "change" | "remove" | "connect-end" }>): GkqlModel {
  switch (e.t) {
    case "change": {
      const next = edit(m, printGkql(e.program));
      return e.select === undefined ? next : { ...next, selected: keep(next.program, e.select) };
    }
    case "remove": return m.program ? { ...edit(m, printGkql(removeFocus(m.program, e.focus))), gesture: { t: "idle" } } : m;
    case "connect-end": {
      if (m.gesture.t !== "connecting" || !m.program) return m;
      const from = m.gesture.from;
      const idle = { ...m, gesture: { t: "idle" } as const };
      if (e.to === from) return idle;
      const r = connect(m.program, from, e.to);
      return typeof r === "string" ? { ...idle, notice: r } : { ...edit(idle, printGkql(r)), selected: verbFocus(e.to) };
    }
  }
}
