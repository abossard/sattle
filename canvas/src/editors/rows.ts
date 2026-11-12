// A projection as rows: what every editor lays out, each in its own notation. A row says what it shows, what an author can
// put in its place (an edit) and what removing it means, as data. The editors differ in how they draw and reach a row, not in
// what a row can become, so the same hole asks the same question in all of them.
import type { Catalog } from "../core/catalog";
import type { EntryDoc, ProjectionDoc, SlotDoc, StatementDoc, StepDoc } from "../editing/doc";
import type { Gesture } from "../editing/gestures";
import { candidates, referenceCandidates, sources, type Option } from "../editing/scope";
import { entryShape, type ShapeField } from "../editing/shapes";
import type { Palette } from "../editor/palette";

/** A piece of a row's text and what it is, so a notation can style it without parsing. */
export interface Token { text: string; as: "keyword" | "name" | "value" | "hole" | "plain" }
export interface FormField extends ShapeField { options?: Option[] }
/** What an author can enter in place of a row. `commit` gives the gesture, or null when the input changes nothing. The options of a choice are worked out when they are read, which is when the author opens it. */
export type EditSpec =
  | { kind: "text"; label: string; value: string; commit(value: string): Gesture | null }
  | { kind: "choice"; label: string; value: string; options: Option[]; commit(value: string): Gesture | null }
  | { kind: "form"; label: string; fields: FormField[]; commit(values: Record<string, string>): Gesture | null };

export type RowKind = "input" | "statement" | "step" | "read" | "field" | "ref" | "entry" | "hole" | "addStep" | "addStatement";
export interface Row {
  id: string; parent: string | null; level: number; kind: RowKind;
  tokens: Token[];
  /** what a screen reader says for the row; the tokens joined when absent */
  label?: string;
  edit?: EditSpec; remove?: Gesture;
  /** the element of the draft the row stands for: selection and diagnostics go by it */
  element?: string;
  problems: string[];
  hasChildren: boolean;
}

const kw = (text: string): Token => ({ text, as: "keyword" });
const nm = (text: string): Token => ({ text, as: "name" });
const val = (text: string): Token => ({ text, as: "value" });
const pl = (text: string): Token => ({ text, as: "plain" });
const hole = (text: string): Token => ({ text, as: "hole" });
/** `EmitEntity` as an author reads it: `emit entity`. */
export const kindWord = (kind: string): string => kind.replace(/([a-z])([A-Z])/g, "$1 $2").toLowerCase();
export const rowText = (r: Row): string => r.label ?? r.tokens.map((t) => t.text).join("");

interface Ctx { doc: ProjectionDoc; palette: Palette; cat: Catalog }

/** A row per parameter: its value, a hole that says it is needed, or for an optional one that is not set a place to add it. `of` says whose parameter it is when that is not the step. */
function fieldRows(c: Ctx, owner: { id: string; fields: StepDoc["fields"] }, parent: string, level: number, of = ""): Row[] {
  return owner.fields.map((f): Row => ({
    id: `${owner.id}#field:${f.name}`, parent, level, kind: f.value === "" && !f.required ? "hole" : "field", element: owner.id, hasChildren: false,
    tokens: f.value === "" && !f.required ? [hole(`+ ${f.name}${of && ` of ${of}`}`)] : [pl(`${of && `${of} `}${f.name}: `), f.value === "" ? hole("…") : val(f.value)],
    label: f.value === "" && !f.required ? `add ${f.name}${of && ` of ${of}`}` : undefined, problems: f.value === "" && f.required ? [`${f.name} is needed`] : [],
    edit: f.values
      ? { kind: "choice", label: f.name, value: f.value, options: f.values.map((v) => ({ value: v, label: v })), commit: (v) => (v === f.value ? null : { g: "do", cmd: { t: "param", id: owner.id, name: f.name, value: v } }) }
      : { kind: "text", label: f.name, value: f.value, commit: (v) => (v.trim() === f.value ? null : { g: "do", cmd: { t: "param", id: owner.id, name: f.name, value: v.trim() } }) },
  }));
}

function entryRow(c: Ctx, step: StepDoc, slot: SlotDoc, e: EntryDoc, parent: string, level: number): Row {
  const single = slot.multiplicity === "1" || slot.multiplicity === "?";
  // an expression is edited as text where it stands; a named entry is edited in its value
  const part = e.parts.find((p) => p.entries.length === 1 && p.entries[0].expr !== null);
  const edit: EditSpec | undefined = e.expr !== null && single
    ? { kind: "text", label: slot.name, value: e.expr, commit: (v) => (v.trim() === e.expr || !v.trim() ? null : { g: "do", cmd: { t: "expr", id: step.id, slot: slot.name, src: v } }) }
    : part
      ? { kind: "text", label: `${e.key || slot.name} ${part.slot}`, value: part.entries[0].expr!, commit: (v) => (v.trim() === part.entries[0].expr || !v.trim() ? null : { g: "do", cmd: { t: "expr", id: e.id, slot: part.slot, src: v } }) }
      : undefined;
  const text = e.text || "…";
  const eq = text.indexOf(" = ");
  return {
    id: e.id, parent, level, kind: "entry", element: e.id, hasChildren: false, problems: [],
    tokens: [pl(`${slot.name}: `), ...(e.expr !== null ? [val(e.expr)] : eq < 0 ? [val(text)] : [nm(text.slice(0, eq)), pl(" = "), val(text.slice(eq + 3))])],
    edit, remove: single && slot.multiplicity === "1" ? undefined : { g: "do", cmd: { t: "delete", ids: [e.id] } },
  };
}

function holeRow(c: Ctx, step: StepDoc, slot: SlotDoc, parent: string, level: number): Row | null {
  const many = slot.multiplicity === "*" || slot.multiplicity === "+";
  if (!many && slot.entries.length) return null;
  const childType = slot.children.find((t) => entryShape(c.palette, step.kind, slot.name, t));
  const shape = childType && entryShape(c.palette, step.kind, slot.name, childType);
  if (!childType || !shape) return null;
  const needed = (slot.multiplicity === "1" || slot.multiplicity === "+") && slot.entries.length === 0;
  const fields = shape.fields.map((f): FormField => (f.kind === "ref" && f.refTo ? { ...f, get options() { return candidates(c.cat, c.doc, f.refTo!, step); } } : f.kind === "choice" ? { ...f, options: (f.values ?? []).map((v) => ({ value: v, label: v })) } : f));
  const commit = (values: Record<string, string>): Gesture | null =>
    fields.some((f) => !f.optional && !values[f.key]?.trim()) ? null : { g: "addEntry", step: step.id, slot: slot.name, childType, values };
  return {
    id: `${step.id}#slot:${slot.name}`, parent, level, kind: "hole", element: step.id, hasChildren: false,
    tokens: [hole(`+ ${slot.name}`)], label: `add ${slot.name}`, problems: needed ? [`${slot.name} is needed`] : [],
    edit: fields.length === 1 && fields[0].kind !== "ref" && fields[0].kind !== "choice"
      ? { kind: "text", label: slot.name, value: "", commit: (v) => commit({ [fields[0].key]: v }) }
      : { kind: "form", label: `add ${slot.name}`, fields, commit },
  };
}

function stepRows(c: Ctx, st: StatementDoc, step: StepDoc, index: number, parent: string, level: number): Row[] {
  // a step with one rows input continues what it reads (`source |> step`); the first step of a statement names that source in its own row
  const rowsIn = step.inputs.filter((i) => i.kind === "rows");
  const piped = rowsIn.length === 1 ? rowsIn[0] : null;
  const source = index === 0 && piped ? piped : null;
  const reads = step.inputs.filter((i) => i !== piped || (index > 0 && !i.inChain));
  const self: Row = {
    id: step.id, parent, level, kind: "step", element: step.id, hasChildren: true,
    tokens: [...(source ? [source.from ? nm(source.fromName) : hole("…"), pl(" ")] : []), pl(piped ? "|> " : ""), kw(kindWord(step.kind))],
    label: `${kindWord(step.kind)} step of ${st.name}${source?.from ? `, reads ${source.fromName}` : ""}`,
    problems: [...step.missing, ...step.diagnostics.map((d) => d.message)], remove: { g: "removeStep", id: step.id },
    edit: source ? { kind: "choice", label: `${st.name} reads`, value: source.from ?? "", get options() { return sources(c.doc, "rows", st.name); }, commit: (v) => (v === (source.from ?? "") ? null : { g: "connect", to: source.portId, from: v || null }) } : undefined,
  };
  const rows: Row[] = [self];
  const inner = level + 1;
  for (const i of reads) {
    rows.push({
      id: `${step.id}#in:${i.port}`, parent: step.id, level: inner, kind: "read", element: step.id, hasChildren: false,
      tokens: [pl(`${i.port} from `), i.from ? nm(i.fromName) : hole("…")], problems: i.from ? [] : [`input ${i.port} is not connected`],
      edit: { kind: "choice", label: `${i.port} reads`, value: i.from ?? "", get options() { return sources(c.doc, i.kind, st.name); }, commit: (v) => (v === (i.from ?? "") ? null : { g: "connect", to: i.portId, from: v || null }) },
    });
  }
  for (const r of step.refs) {
    rows.push({
      id: `${step.id}#ref:${r.rel}`, parent: step.id, level: inner, kind: "ref", element: step.id, hasChildren: false,
      tokens: [pl(`${r.label.replace(/ \(.*/, "").toLowerCase()}: `), r.target ? nm(r.targetName) : hole("…")], problems: r.required && !r.target ? [`${r.label.replace(/ \(.*/, "").toLowerCase()} is needed`] : [],
      edit: { kind: "choice", label: r.label, value: r.target ?? "", get options() { return referenceCandidates(c.cat, c.doc, r, step); }, commit: (v) => (v === (r.target ?? "") ? null : { g: "do", cmd: { t: "ref", id: step.id, rel: r.rel, target: v } }) },
    });
  }
  rows.push(...fieldRows(c, step, step.id, inner));
  for (const slot of step.slots) {
    for (const e of slot.entries) {
      rows.push(entryRow(c, step, slot, e, step.id, inner));
      // what an expression entry has besides its text (the `fill` of a field a series reads)
      if (e.expr !== null) rows.push(...fieldRows(c, e, step.id, inner, e.expr));
    }
    const h = holeRow(c, step, slot, step.id, inner);
    if (h) rows.push(h);
  }
  return rows;
}

/** Operator kinds a new step can be: `head` ones start a statement (they need no rows before them), the others continue one. */
export function stepKinds(palette: Palette, where: "head" | "next"): Option[] {
  const rowsIn = (k: Palette["operators"][number]) => k.ports.some((p) => p.direction === "in" && p.kind === "rows");
  return palette.operators.filter((o) => o.supported && (where === "next" ? rowsIn(o) : true)).map((o) => ({ value: o.kind, label: kindWord(o.kind) })).sort((a, b) => a.label.localeCompare(b.label));
}

/** Every row of the projection, in reading order. `open` holds the ids of the statements and steps whose rows are shown; null shows all. */
export function rowsOf(doc: ProjectionDoc, palette: Palette, cat: Catalog, open: ReadonlySet<string> | null = null): Row[] {
  const c: Ctx = { doc, palette, cat };
  const shown = (id: string) => open === null || open.has(id);
  const rows: Row[] = doc.inputs.map((i): Row => ({ id: i.id, parent: null, level: 1, kind: "input", element: i.id, hasChildren: false, problems: [], tokens: [kw("in "), nm(i.name), pl(`: ${i.kind} ${i.detail}`.trimEnd())] }));
  for (const st of doc.statements) {
    const sid = `stmt:${st.name}`;
    rows.push({
      id: sid, parent: null, level: 1, kind: "statement", hasChildren: true, element: st.steps[st.steps.length - 1].id,
      tokens: st.sink ? [nm(st.name)] : [kw("let "), nm(st.name), pl(" =")], label: `${st.sink ? "sink" : "row set"} ${st.name}`,
      problems: [], edit: st.sink ? undefined : { kind: "text", label: "name", value: st.name, commit: (v) => (v.trim() === st.name || !v.trim() ? null : { g: "rename", statement: st.name, to: v.trim() }) },
    });
    if (!shown(sid)) continue;
    st.steps.forEach((step, i) => { const rs = stepRows(c, st, step, i, sid, 2); rows.push(...(shown(step.id) ? rs : rs.slice(0, 1))); });
    if (!st.sink) rows.push({
      id: `${sid}#add`, parent: sid, level: 2, kind: "addStep", hasChildren: false, problems: [], tokens: [hole("+ step")], label: `add a step to ${st.name}`,
      edit: { kind: "choice", label: "step", value: "", options: stepKinds(palette, "next"), commit: (v) => (v ? { g: "addStep", kind: v, statement: st.name } : null) },
    });
  }
  rows.push({
    id: "#new", parent: null, level: 1, kind: "addStatement", hasChildren: false, problems: [], tokens: [hole("+ row set")], label: "add a row set",
    edit: {
      kind: "form", label: "new statement",
      fields: [{ key: "name", label: "name", kind: "name" }, { key: "kind", label: "starts with", kind: "choice", options: stepKinds(palette, "head") }],
      commit: (v) => (v.name?.trim() && v.kind ? { g: "addStep", kind: v.kind, statement: v.name.trim() } : null),
    },
  });
  return rows;
}
