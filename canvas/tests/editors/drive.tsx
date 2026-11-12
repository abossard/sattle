// An author's hands on a notation, for tests: keys and clicks counted, a row reached by the element it stands for and edited the way
// the notation offers (Enter on a row of a tree, the row's own button elsewhere). On top of it, `build` makes a whole projection
// through a notation's interface from the document of another one: every step, read, parameter, reference and entry is put in
// by the same actions an author would use.
import { screen, waitFor, within } from "@testing-library/react";
import type { UserEvent } from "@testing-library/user-event";
import type { ComponentType } from "react";
import type { EntryDoc, ProjectionDoc, SlotDoc, StatementDoc, StepDoc } from "../../src/editing/doc";
import { docOf, type DraftPort } from "../../src/editing/draft";
import { entryShape } from "../../src/editing/shapes";
import { BlockEditor } from "../../src/editors/BlockEditor";
import { GraphEditor } from "../../src/editors/GraphEditor";
import type { EditorProps } from "../../src/editors/host";
import { PipelineEditor } from "../../src/editors/PipelineEditor";
import { kindWord } from "../../src/editors/rows";
import { StructureEditor } from "../../src/editors/StructureEditor";

export const rowEl = (id: string) => document.querySelector<HTMLElement>(`[data-row-id="${CSS.escape(id)}"]`);
/** user-event reads `{` and `[` as the start of a key name */
const literal = (text: string): string => text.replace(/[{[]/g, (c) => c + c);

/** What an author did, counted: keys pressed and pointer clicks. A choice from a list counts as two clicks. */
export class Hands {
  keys = 0; clicks = 0;
  constructor(readonly user: UserEvent) {}
  async press(keys: string) { this.keys += keys.replace(/\{[^}]+\}/g, "k").length; await this.user.keyboard(keys); }
  async type(el: Element, text: string) { this.keys += text.length; await this.user.type(el, literal(text)); }
  async click(el: Element) { this.clicks += 1; await this.user.click(el); }
  async choose(el: Element, option: string) { this.clicks += 2; await this.user.selectOptions(el, option); }
  /** Open the editor of a row: Enter on a row of a tree, the row's own button elsewhere. */
  async edit(id: string): Promise<HTMLElement> {
    const el = await waitFor(() => { const x = rowEl(id); if (!x) throw new Error(`no row ${id}`); return x; });
    if (el.getAttribute("role") === "treeitem") { el.focus(); await this.press("{Enter}"); } else await this.click(within(el).getByRole("button", { name: /, edit$/ }));
    return rowEl(id)!;
  }
  async text(id: string, value: string) { const el = await this.edit(id); const box = within(el).getByRole("textbox"); this.keys += 1; await this.user.clear(box); await this.type(box, value); await this.press("{Enter}"); }
  async pick(id: string, option: string) { const el = await this.edit(id); await this.choose(within(el).getByRole("combobox"), option); }
  /** Fill the form a row opens: a text or a choice per field, by the label of the field, then Enter. */
  async fill(id: string, fields: { label: string; value: string; choice: boolean }[]) {
    const el = await this.edit(id);
    for (const f of fields) {
      if (f.choice) await this.choose(within(el).getByRole("combobox", { name: f.label }), f.value);
      else await this.type(within(el).getByRole("textbox", { name: f.label }), f.value);
    }
    await this.press("{Enter}");
  }
  async remove(id: string) {
    const el = rowEl(id)!;
    if (el.getAttribute("role") === "treeitem") { el.focus(); await this.press("{Delete}"); } else await this.click(within(el).getByRole("button", { name: /^remove / }));
  }
}

const statement = (d: DraftPort, projection: string, name: string): StatementDoc | undefined => docOf(d, projection)!.statements.find((s) => s.name === name);

/** How one notation starts a row set, continues it, and brings a step's rows into the page. Everything else is the rows, the same in all. */
export interface Way {
  Editor: ComponentType<EditorProps>;
  /** Returns the name the row set has: the one asked for, or for a sink in the blocks the next number of its kind (a sink is named by its position, never by the author). */
  start(h: Hands, d: DraftPort, projection: string, name: string, kind: string, sink: boolean): Promise<string>;
  append(h: Hands, d: DraftPort, projection: string, statement: string, kind: string): Promise<void>;
  show(h: Hands, d: DraftPort, projection: string, statement: string, at: number): Promise<void>;
}

/** The form every notation but the blocks has for a new row set: a name and what it starts with. */
const byForm: Way["start"] = async (h, _d, _p, name, kind) => {
  const el = await h.edit("#new");
  await h.type(within(el).getByRole("textbox", { name: "name" }), name);
  await h.choose(within(el).getByRole("combobox", { name: "starts with" }), kind);
  await h.press("{Enter}");
  return name;
};
const byRow: Way["append"] = (h, _d, _p, st, kind) => h.pick(`stmt:${st}#add`, kind);

export const WAYS: Record<string, Way> = {
  structure: { Editor: StructureEditor, start: byForm, append: byRow, show: async () => {} },
  pipeline: {
    Editor: PipelineEditor, start: byForm, append: byRow,
    show: async (h, _d, _p, st) => { const tab = await waitFor(() => { const t = document.querySelector<HTMLElement>(`[role=tab][data-row-set="${CSS.escape(st)}"]`); if (!t) throw new Error(`no tab ${st}`); return t; }); if (tab.getAttribute("aria-selected") !== "true") await h.click(tab); },
  },
  blocks: {
    Editor: BlockEditor, show: async () => {},
    // a block from the palette, put down as a row set of its own; a row set is then given its name, a sink has the next name of its kind
    start: async (h, d, p, name, kind, sink) => {
      const before = new Set(docOf(d, p)!.statements.map((s) => s.name));
      await h.click(within(screen.getByRole("group", { name: "Blocks" })).getByRole("button", { name: kindWord(kind) }));
      await h.click(screen.getByRole("button", { name: `${kindWord(kind)} here: a new ${sink ? "sink" : "row set"}` }));
      const made = await waitFor(() => { const s = docOf(d, p)!.statements.find((x) => !before.has(x.name)); if (!s) throw new Error("no new row set"); return s.name; });
      if (made === name || sink) return made;
      await h.text(`stmt:${made}`, name);
      return name;
    },
    append: async (h, d, p, st, kind) => {
      await h.click(within(screen.getByRole("group", { name: "Blocks" })).getByRole("button", { name: kindWord(kind) }));
      await h.click(screen.getByRole("button", { name: `${kindWord(kind)} here: ${st}, position ${statement(d, p, st)!.steps.length + 1}` }));
    },
  },
  graph: {
    Editor: GraphEditor, start: byForm, append: byRow,
    // the card of the step: its rows are in the side panel
    show: async (h, d, p, st, at) => {
      const id = statement(d, p, st)!.steps[at].id;
      const head = await waitFor(() => { const x = document.querySelector<HTMLElement>(`[data-card="${CSS.escape(id)}"]`); if (!x) throw new Error(`no card ${id}`); return x; });
      if (head.getAttribute("aria-pressed") !== "true") await h.click(head);
    },
  },
};

const part = (e: EntryDoc, slot?: string): string => (slot ? e.parts.find((p) => p.slot === slot) : e.parts[0])?.entries[0]?.expr ?? "";
const field = (e: EntryDoc, name: string): string => e.fields.find((f) => f.name === name)?.value ?? "";
const target = (e: EntryDoc, rel: string) => e.refs.find((r) => r.rel === rel);

/** What an author fills in to add the entry `e`, by the keys of the shape of its type (editing/shapes.ts). */
function valuesOf(e: EntryDoc): Record<string, string> {
  if (e.expr !== null) return { src: e.expr, path: e.expr };
  switch (e.type) {
    case "Assign": return { name: e.key, src: part(e) };
    case "Aggregate": return { name: e.key, fn: field(e, "fn"), src: part(e, "value"), second: e.parts.filter((p) => p.slot !== "value")[0]?.entries[0]?.expr ?? "" };
    case "SetProperty": return { property: target(e, "property")?.target ?? "", src: part(e) };
    case "PatternNode": return { variable: e.key, type: target(e, "ofType")?.target ?? "" };
    case "PatternEdge": return { from: target(e, "from")?.targetName ?? "", to: target(e, "to")?.targetName ?? "", type: target(e, "ofType")?.target ?? "", variable: field(e, "var") };
    default: return Object.fromEntries(e.fields.map((f) => [f.name, f.value]));
  }
}

/** The shape of the form that adds the entry `e`: an expression takes the shape of what the slot holds, anything else the shape of its own type. */
const shapeOf = (palette: DraftPort["palette"], kind: string, sl: SlotDoc, e: EntryDoc) =>
  entryShape(palette, kind, sl.name, e.expr !== null ? sl.children.find((t) => entryShape(palette, kind, sl.name, t)) ?? "" : e.type);

/** The entries of `doc` that no notation can add: their type has no shape (the body of a fixpoint, a constant row). Empty when the whole projection can be built. */
export function unbuildable(doc: ProjectionDoc, palette: DraftPort["palette"]): string[] {
  return [...new Set(doc.statements.flatMap((st) => st.steps.flatMap((s) => s.slots.flatMap((sl) => sl.entries.filter((e) => !shapeOf(palette, s.kind, sl, e)).map((e) => `${s.kind}.${sl.name}: ${e.type}`)))))];
}

/**
 * Build what `like` says in the projection `projection` of `d`, through the notation mounted in the page: row set by row set, first
 * its steps, then what each reads, its parameters, its references and its entries. `like` may be another projection: its ids are
 * read as ids of `projection`.
 */
export async function build(way: Way, h: Hands, d: DraftPort, projection: string, like: ProjectionDoc): Promise<void> {
  // the names the row sets got here (a sink may have another number) and the ids their steps got (the pieces of a row set are named by
  // their position here; a projection the engine lowered may name them otherwise), and with them every id inside `like`
  const named = new Map<string, string>(), ids = new Map<string, string>();
  const here = (id: string): string => {
    for (const [from, to] of ids) if (id === from || id.startsWith(`${from}/`)) return to + id.slice(from.length);
    return id.startsWith(`${like.id}/`) ? projection + id.slice(like.id.length) : id;
  };
  const now = (stepId: string): StepDoc => docOf(d, projection)!.statements.flatMap((s) => s.steps).find((s) => s.id === stepId)!;
  const until = (what: string, done: () => boolean) => waitFor(() => { if (!done()) throw new Error(`waiting for ${what}`); }, { timeout: 5000 });
  const add = async (step: StepDoc, sl: SlotDoc, e: EntryDoc) => {
    const id = here(step.id), row = `${id}#slot:${sl.name}`, shape = shapeOf(d.palette, step.kind, sl, e)!;
    const values = valuesOf(e), count = now(id).slots.find((s) => s.name === sl.name)!.entries.length;
    const filled = shape.fields.filter((f) => (values[f.key] ?? "") !== "");
    if (shape.fields.length === 1 && shape.fields[0].kind !== "ref" && shape.fields[0].kind !== "choice") await h.text(row, values[shape.fields[0].key]);
    else await h.fill(row, filled.map((f) => ({ label: f.label, value: values[f.key], choice: f.kind === "ref" || f.kind === "choice" })));
    await until(`${sl.name} of ${step.name}: ${e.text}`, () => now(id).slots.find((s) => s.name === sl.name)!.entries.length === count + 1);
    // what an expression entry has besides its text (the `fill` of a field a series reads), in the rows under it
    const made = now(id).slots.find((s) => s.name === sl.name)!.entries[count];
    for (const f of e.expr !== null ? e.fields : []) {
      if (f.value === "") continue;
      await h.text(`${made.id}#field:${f.name}`, f.value);
      await until(`${f.name} of ${e.text}`, () => now(id).slots.find((s) => s.name === sl.name)!.entries[count].fields.find((x) => x.name === f.name)?.value === f.value);
    }
  };
  for (const st of like.statements) {
    for (const [i, s] of st.steps.entries()) {
      if (i === 0) named.set(st.name, await way.start(h, d, projection, st.name, s.kind, st.sink));
      else { await way.show(h, d, projection, named.get(st.name)!, i - 1); await way.append(h, d, projection, named.get(st.name)!, s.kind); }
      await until(`step ${i + 1} of ${st.name}`, () => statement(d, projection, named.get(st.name)!)?.steps.length === i + 1);
    }
    statement(d, projection, named.get(st.name)!)!.steps.forEach((made, i) => ids.set(st.steps[i].id, made.id));
    for (const [i, s] of st.steps.entries()) {
      const id = here(s.id);
      await way.show(h, d, projection, named.get(st.name)!, i);
      // what it reads: the source of a row set is chosen in the row of its first step, every other input in a row of its own
      const rowsIn = s.inputs.filter((x) => x.kind === "rows");
      for (const x of s.inputs) {
        if (!x.from || (i > 0 && x.inChain)) continue;
        const from = here(x.from);
        if (now(id).inputs.find((y) => y.port === x.port)?.from === from) continue;
        await h.pick(i === 0 && rowsIn.length === 1 && rowsIn[0] === x ? id : `${id}#in:${x.port}`, from);
        await until(`${s.name} reads ${x.fromName}`, () => now(id).inputs.find((y) => y.port === x.port)?.from === from);
      }
      for (const f of s.fields) {
        if (f.value === "" || now(id).fields.find((y) => y.name === f.name)?.value === f.value) continue;
        if (f.values) await h.pick(`${id}#field:${f.name}`, f.value); else await h.text(`${id}#field:${f.name}`, f.value);
        await until(`${s.name} ${f.name}`, () => now(id).fields.find((y) => y.name === f.name)?.value === f.value);
      }
      for (const r of s.refs) {
        const to = r.target && here(r.target);
        if (!to || now(id).refs.find((y) => y.rel === r.rel)?.target === to) continue;
        await h.pick(`${id}#ref:${r.rel}`, to);
        await until(`${s.name} ${r.rel}`, () => now(id).refs.find((y) => y.rel === r.rel)?.target === to);
      }
      for (const sl of s.slots) for (const e of sl.entries) await add(s, sl, e);
    }
  }
}
