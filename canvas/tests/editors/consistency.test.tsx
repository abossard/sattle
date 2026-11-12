// @vitest-environment jsdom
// The same piece of work done in each notation leaves the same draft: the editors differ in how a change is reached, not in what
// it is. Each one is driven the way an author uses it; the result is compared with the gestures performed directly.
import { screen, waitFor, within } from "@testing-library/react";
import userEvent, { type UserEvent } from "@testing-library/user-event";
import type { ComponentType } from "react";
import { describe, expect, it } from "vitest";
import { docOf, perform } from "../../src/editing/draft";
import { BlockEditor } from "../../src/editors/BlockEditor";
import { GraphEditor } from "../../src/editors/GraphEditor";
import type { EditorProps } from "../../src/editors/host";
import { PipelineEditor } from "../../src/editors/PipelineEditor";
import { StructureEditor } from "../../src/editors/StructureEditor";
import { Workbench } from "../../src/editors/Workbench";
import { dangling, shape, type MemoryDraft } from "../editing/memory-draft";
import { freshDraft, mount, SVC, violations } from "./harness";

type Draft = MemoryDraft;
const statement = (d: Draft, name: string) => docOf(d, SVC)!.statements.find((s) => s.name === name);
const op = (name: string) => `${SVC}/op:${name}`;
const rowEl = (id: string) => document.querySelector<HTMLElement>(`[data-row-id="${CSS.escape(id)}"]`);

/** What an author did, counted: keys pressed and pointer clicks. */
class Hands {
  keys = 0; clicks = 0;
  constructor(readonly user: UserEvent) {}
  async press(keys: string) { this.keys += keys.replace(/\{[^}]+\}/g, "k").length; await this.user.keyboard(keys); }
  async type(el: Element, text: string) { this.keys += text.length; await this.user.type(el, text); }
  async click(el: Element) { this.clicks += 1; await this.user.click(el); }
  /** a choice from a list: one click to open it, one to choose */
  async choose(el: Element, option: string) { this.clicks += 2; await this.user.selectOptions(el, option); }
  /** Open the editor of a row: Enter on a row of a tree, the row's own button elsewhere. */
  async edit(id: string): Promise<HTMLElement> {
    const el = await waitFor(() => { const x = rowEl(id); if (!x) throw new Error(`no row ${id}`); return x; });
    if (el.getAttribute("role") === "treeitem") { el.focus(); await this.press("{Enter}"); } else await this.click(within(el).getByRole("button", { name: /, edit$/ }));
    return rowEl(id)!;
  }
  async text(id: string, value: string) { const el = await this.edit(id); const box = within(el).getByRole("textbox"); this.keys += 1; await this.user.clear(box); await this.type(box, value); await this.press("{Enter}"); }
  async pick(id: string, option: string) { const el = await this.edit(id); await this.choose(within(el).getByRole("combobox"), option); }
  async remove(id: string) {
    const el = rowEl(id)!;
    if (el.getAttribute("role") === "treeitem") { el.focus(); await this.press("{Delete}"); } else await this.click(within(el).getByRole("button", { name: /^remove / }));
  }
}

interface Way {
  Editor: ComponentType<EditorProps>;
  /** bring the rows of step `at` of a row set into the page */
  show(h: Hands, d: Draft, statement: string, at: number): Promise<void>;
  /** a new row set `name` of one step of `kind` that reads `reads` */
  start(h: Hands, d: Draft, name: string, kind: string, reads: string): Promise<void>;
  /** a step of `kind` at the end of `statement` */
  append(h: Hands, d: Draft, statement: string, kind: string): Promise<void>;
}

const viaForm = async (h: Hands, d: Draft, name: string, kind: string, reads: string) => {
  const el = await h.edit("#new");
  await h.type(within(el).getByRole("textbox", { name: "name" }), name);
  await h.choose(within(el).getByRole("combobox", { name: "starts with" }), kind);
  await h.press("{Enter}");
  await waitFor(() => expect(statement(d, name)).toBeTruthy());
  await h.pick(op(name), reads);
};
const viaRow = async (h: Hands, _d: Draft, st: string, kind: string) => h.pick(`stmt:${st}#add`, kind);
const none = async () => {};

const ways: Record<string, Way> = {
  structure: { Editor: StructureEditor, show: none, start: viaForm, append: viaRow },
  pipeline: {
    Editor: PipelineEditor, start: viaForm, append: viaRow,
    show: async (h, _d, st) => { const tab = screen.getAllByRole("tab").find((t) => t.textContent?.startsWith(st))!; if (tab.getAttribute("aria-selected") !== "true") await h.click(tab); },
  },
  blocks: {
    Editor: BlockEditor, show: none,
    start: async (h, d, name, kind, reads) => {
      await h.click(screen.getByRole("button", { name: kind }));
      await h.click(screen.getByRole("button", { name: / here: a new row set$/ }));
      await waitFor(() => expect(statement(d, "rows1")).toBeTruthy());
      await h.text("stmt:rows1", name);
      await waitFor(() => expect(statement(d, name)).toBeTruthy());
      await h.pick(op(name), reads);
    },
    append: async (h, d, st, kind) => {
      await h.click(screen.getByRole("button", { name: kind }));
      await h.click(screen.getByRole("button", { name: `${kind} here: ${st}, position ${statement(d, st)!.steps.length + 1}` }));
    },
  },
  graph: {
    Editor: GraphEditor, append: viaRow,
    show: async (h, d, st, at) => { const id = statement(d, st)!.steps[at].id; const head = await waitFor(() => { const x = document.querySelector<HTMLElement>(`[data-card="${CSS.escape(id)}"]`); if (!x) throw new Error(`no card ${id}`); return x; }); if (head.getAttribute("aria-pressed") !== "true") await h.click(head); },
    start: async (h, d, name, kind, reads) => {
      await h.click(screen.getByRole("button", { name: `connect from ${reads}` }));
      await h.choose(screen.getByRole("combobox", { name: `add a step that reads ${reads}` }), kind);
      await waitFor(() => expect(statement(d, "rows1")).toBeTruthy());
      await h.text("stmt:rows1", name);
    },
  },
};

/** The work: a row set that reads `deps` and keeps some of its rows, extended, renamed, changed, and cut back. */
async function work(way: Way, h: Hands, d: Draft): Promise<void> {
  await way.start(h, d, "fresh", "filter", "deps");
  await waitFor(() => expect(statement(d, "fresh")?.steps[0].inputs[0].fromName).toBe("deps"));
  await way.show(h, d, "fresh", 0);
  await h.text(`${op("fresh")}#slot:predicate`, "s != t");
  await waitFor(() => expect(statement(d, "fresh")!.steps[0].slots[0].entries[0]?.expr).toBe("s != t"));
  await way.append(h, d, "fresh", "distinct");
  await waitFor(() => expect(statement(d, "fresh")!.steps.length).toBe(2));
  await way.show(h, d, "fresh", 0);
  await h.text("stmt:fresh", "pairs2");
  await waitFor(() => expect(statement(d, "pairs2")).toBeTruthy());
  await way.show(h, d, "pairs2", 0);
  await h.text(statement(d, "pairs2")!.steps[0].slots[0].entries[0].id, "s == t");
  await waitFor(() => expect(statement(d, "pairs2")!.steps[0].slots[0].entries[0].expr).toBe("s == t"));
  await way.append(h, d, "pairs2", "filter");
  await waitFor(() => expect(statement(d, "pairs2")!.steps.length).toBe(3));
  await way.show(h, d, "pairs2", 2);
  await h.remove(statement(d, "pairs2")!.steps[2].id);
  await waitFor(() => expect(statement(d, "pairs2")!.steps.length).toBe(2));
}

/** The same work as gestures, with no editor. */
async function direct(): Promise<Draft> {
  const d = freshDraft();
  const deps = statement(d, "deps")!.steps.at(-1)!.out!;
  const run = async (g: Parameters<typeof perform>[2]) => expect(await perform(d, SVC, g)).toMatchObject({ ok: true });
  await run({ g: "addStep", kind: "Filter", statement: "fresh", from: { in: deps } });
  await run({ g: "addEntry", step: op("fresh"), slot: "predicate", childType: "Expr", values: { src: "s != t" } });
  await run({ g: "addStep", kind: "Distinct", statement: "fresh" });
  await run({ g: "rename", statement: "fresh", to: "pairs2" });
  await run({ g: "do", cmd: { t: "expr", id: op("pairs2#0"), slot: "predicate", src: "s == t" } });
  return d;
}

describe("the same work in every notation", () => {
  const counts: Record<string, { keys: number; clicks: number; changes: number }> = {};
  it.each(Object.keys(ways))("%s: leaves the draft the gestures leave", async (name) => {
    const expected = shape((await direct()).graph);
    const h = new Hands(userEvent.setup());
    const { draft } = mount(ways[name].Editor);
    await work(ways[name], h, draft);
    expect(dangling(draft.graph)).toEqual([]);
    expect(shape(draft.graph)).toEqual(expected);
    expect(await violations()).toEqual([]);
    counts[name] = { keys: h.keys, clicks: h.clicks, changes: draft.log.length };
  }, 30_000);

  // a choice from a list counts as two clicks in every notation, also where the keyboard reaches it
  it("what it cost in each: keys, clicks and changes of the draft", () => {
    expect(counts).toMatchInlineSnapshot(`
      {
        "blocks": {
          "changes": 9,
          "clicks": 14,
          "keys": 31,
        },
        "graph": {
          "changes": 8,
          "clicks": 17,
          "keys": 31,
        },
        "pipeline": {
          "changes": 8,
          "clicks": 17,
          "keys": 30,
        },
        "structure": {
          "changes": 8,
          "clicks": 8,
          "keys": 38,
        },
      }
    `);
  });
});

describe("the workbench", () => {
  it("changes the notation and keeps the draft and the selection", async () => {
    const user = userEvent.setup();
    const { draft } = mount(Workbench as ComponentType<EditorProps>);
    const before = shape(draft.graph);
    expect(screen.getByRole("tree", { name: "Structure of serviceDeps" })).toBeTruthy();
    await user.click(screen.getByRole("tab", { name: "Wires" }));
    await user.click(screen.getByRole("button", { name: "threshold health" }));
    await user.click(screen.getByRole("tab", { name: "Steps" }));
    expect(screen.getByRole("tab", { name: /^health/ }).getAttribute("aria-selected")).toBe("true");
    await user.click(screen.getByRole("tab", { name: "Blocks" }));
    expect(await violations()).toEqual([]);
    await user.click(screen.getByRole("tab", { name: "By example" }));
    expect(screen.getByText(/No data is open/)).toBeTruthy();
    expect(shape(draft.graph)).toEqual(before);
    expect(draft.log.length).toBe(0);
  });
});
