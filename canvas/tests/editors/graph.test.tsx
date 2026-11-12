// @vitest-environment jsdom
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { allSteps } from "../../src/editing/doc";
import { docOf } from "../../src/editing/draft";
import { GraphEditor } from "../../src/editors/GraphEditor";
import { inView, layoutOf, neighbour } from "../../src/editors/graph-layout";
import type { EditorProps } from "../../src/editors/host";
import { freshDraft, mount, OPS, SVC, violations } from "./harness";

const reads = (draft: Parameters<typeof docOf>[0], statement: string) => docOf(draft, SVC)!.statements.find((s) => s.name === statement)!.steps[0].inputs.filter((i) => i.kind === "rows").map((i) => i.fromName);

describe.each([[SVC], [OPS]])("the layout of %s", (projection) => {
  const doc = docOf(freshDraft(), projection)!;
  const l = layoutOf(doc, (k) => k, () => ["a line"]);
  const card = new Map(l.cards.map((c) => [c.id, c]));
  it("has a card for every input and step and a wire for every read", () => {
    expect(l.cards.length).toBe(doc.inputs.length + allSteps(doc).length);
    expect(l.wires.length).toBe(allSteps(doc).flatMap((s) => s.inputs).filter((i) => i.from).length);
  });
  it("puts every card right of what it reads, and no card on another", () => {
    for (const w of l.wires) expect(card.get(w.toCard)!.x).toBeGreaterThan(card.get(w.fromCard)!.x);
    for (const a of l.cards) for (const b of l.cards) if (a !== b && a.column === b.column) expect(a.y + a.h <= b.y || b.y + b.h <= a.y).toBe(true);
    for (const c of l.cards) expect(c.x + c.w <= l.width && c.y + c.h <= l.height).toBe(true);
  });
  it("shows in a small view only what touches it", () => {
    const some = inView(l, { left: 0, top: 0, width: 300, height: 200 }, 0);
    expect(some.cards.length).toBeGreaterThan(0);
    expect(some.cards.length).toBeLessThan(l.cards.length);
    for (const c of l.cards) expect(some.cards.includes(c)).toBe(c.x < 300 && c.y < 200);
  });
  it("arrow keys follow the wires and the columns", () => {
    for (const w of l.wires.slice(0, 20)) expect(card.get(neighbour(l, w.toCard, "ArrowLeft")!)!.column).toBeLessThan(card.get(w.toCard)!.column);
    const two = l.cards.filter((c) => c.column === l.cards[0].column).sort((a, b) => a.y - b.y);
    if (two.length > 1) { expect(neighbour(l, two[0].id, "ArrowDown")).toBe(two[1].id); expect(neighbour(l, two[1].id, "ArrowUp")).toBe(two[0].id); }
    expect(neighbour(l, two[0].id, "ArrowUp")).toBeNull();
  });
});

describe("the graph editor", () => {
  it("shows every step and input as a card, with no accessibility violation", async () => {
    const { draft } = mount(GraphEditor);
    const doc = docOf(draft, SVC)!;
    const canvas = screen.getByRole("group", { name: "Graph of serviceDeps" });
    expect(within(canvas).getAllByRole("group").length).toBe(doc.inputs.length + allSteps(doc).length);
    expect(await violations()).toEqual([]);
  });

  it("makes a wire from the keyboard: an output, then an input that can take it", async () => {
    const user = userEvent.setup();
    const { draft } = mount(GraphEditor);
    expect(reads(draft, "health")).toEqual(["depTelemetry"]);
    screen.getByRole("button", { name: "connect from deps" }).focus();
    await user.keyboard("{Enter}");
    expect(screen.getAllByRole("status").some((x) => /Connecting from deps/.test(x.textContent ?? ""))).toBe(true);
    screen.getByRole("button", { name: "connect deps to in of threshold health" }).focus();
    await user.keyboard("{Enter}");
    await waitFor(() => expect(reads(draft, "health")).toEqual(["deps"]));
    expect(draft.log.length).toBe(1);
    expect(await violations()).toEqual([]);
  });

  it("does not offer an input that would close a circle or carries something else", async () => {
    const user = userEvent.setup();
    mount(GraphEditor);
    await user.click(screen.getByRole("button", { name: "connect from health" }));
    // depTelemetry feeds health, so it cannot read it; a graph input takes no rows
    expect((screen.getByRole("button", { name: /^in of map depTelemetry/ }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getAllByRole("button", { name: /^graph of match / }).every((b) => (b as HTMLButtonElement).disabled)).toBe(true);
    await user.keyboard("{Escape}");
    expect((screen.getByRole("button", { name: /^in of map depTelemetry/ }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("Delete on an input takes its wire away", async () => {
    const user = userEvent.setup();
    const { draft } = mount(GraphEditor);
    screen.getByRole("button", { name: "in of threshold health: reads depTelemetry" }).focus();
    await user.keyboard("{Delete}");
    await waitFor(() => expect(reads(draft, "health")).toEqual([""]));
    expect(screen.getByRole("button", { name: "in of threshold health: open" })).toBeTruthy();
  });

  it("adds a step that reads the chosen output", async () => {
    const user = userEvent.setup();
    const { draft } = mount(GraphEditor);
    await user.click(screen.getByRole("button", { name: "connect from deps" }));
    await user.selectOptions(screen.getByRole("combobox", { name: "add a step that reads deps" }), "filter");
    await waitFor(() => expect(reads(draft, "rows1")).toEqual(["deps"]));
    // the new card is the selected one, and its lines are in the panel
    await waitFor(() => expect(within(screen.getByRole("group", { name: "Selected step" })).getByRole("group", { name: /^filter of row set rows1/ })).toBeTruthy());
  });

  it("arrow keys move the cursor from card to card", async () => {
    const user = userEvent.setup();
    mount(GraphEditor);
    const first = screen.getByRole("button", { name: /^in master/ });
    first.focus();
    await user.keyboard("{ArrowRight}");
    await waitFor(() => expect(document.activeElement?.getAttribute("data-card")).toMatch(/\/op:/));
    expect(document.activeElement?.getAttribute("aria-pressed")).toBe("true");
  });

  it("keeps only the cards in view in the page", () => {
    const Small = (p: EditorProps) => <GraphEditor {...p} viewport={{ width: 400, height: 240 }} />;
    const { draft } = mount(Small);
    const doc = docOf(draft, SVC)!;
    expect(within(screen.getByRole("group", { name: "Graph of serviceDeps" })).getAllByRole("group").length).toBeLessThan(doc.inputs.length + allSteps(doc).length);
  });
});
