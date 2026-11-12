// @vitest-environment jsdom
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { docOf, perform } from "../../src/editing/draft";
import { BlockEditor, places, type Held } from "../../src/editors/BlockEditor";
import { dangling, palette, shape } from "../editing/memory-draft";
import { freshDraft, mount, OPS, SVC, violations } from "./harness";

const steps = (draft: Parameters<typeof docOf>[0], name: string, projection = SVC) => docOf(draft, projection)!.statements.find((s) => s.name === name)?.steps ?? [];
const long = (draft: Parameters<typeof docOf>[0]) => docOf(draft, SVC)!.statements.find((s) => !s.sink && s.steps.length > 1)!;
/** A row set whose first step reads rows, so its steps can change places. */
const movable = (draft: Parameters<typeof docOf>[0]) => docOf(draft, SVC)!.statements.find((s) => !s.sink && s.steps.length > 1 && s.steps[0].inputs.some((i) => i.kind === "rows"))!;

describe("the places a block fits", () => {
  const kinds = palette.operators.filter((o) => o.supported).map((o) => o.kind);
  const cases = [SVC, OPS].flatMap((p) => kinds.map((k) => [p, k] as [string, string]));
  it.each(cases)("%s: every place offered for a new %s gives a whole draft with the block where the place says", async (projection, kind) => {
    const offered = places(docOf(freshDraft(), projection)!, palette, { kind });
    expect(offered.length).toBeGreaterThan(0);
    for (const pl of offered) {
      const draft = freshDraft();
      const done = await perform(draft, projection, pl.gesture);
      expect(done).toMatchObject({ ok: true });
      expect(dangling(draft.graph)).toEqual([]);
      const doc = docOf(draft, projection)!;
      const st = pl.statement ? doc.statements.find((s) => s.name === pl.statement)! : doc.statements.find((s) => s.steps.some((x) => done.ok && x.id === done.select[0]))!;
      expect(st.steps[pl.gap].kind).toBe(kind);
      // the stack still reads from top to bottom
      st.steps.slice(1).forEach((x) => expect(x.inputs.some((i) => i.inChain)).toBe(true));
    }
  });

  it.each(kinds.filter((k) => !palette.operators.find((o) => o.kind === k)!.ports.some((p) => p.direction === "in" && p.kind === "rows")))("a %s reads no rows, so it only starts a row set of its own", (kind) => {
    expect(places(docOf(freshDraft(), SVC)!, palette, { kind }).map((p) => p.statement)).toEqual([null]);
  });

  it("a step is not offered the place it is in", () => {
    const draft = freshDraft();
    const st = long(draft);
    const held: Held = { kind: st.steps[0].kind, step: { id: st.steps[0].id, statement: st.name, index: 0 } };
    expect(places(docOf(draft, SVC)!, palette, held).filter((p) => p.statement === st.name).map((p) => p.gap)).not.toContain(0);
  });
});

describe("the block editor", () => {
  it("shows every row set as a stack of blocks and the palette by role, with no accessibility violation", async () => {
    const { draft } = mount(BlockEditor);
    const doc = docOf(draft, SVC)!;
    for (const st of doc.statements) expect(within(screen.getByRole("group", { name: `${st.sink ? "sink" : "row set"} ${st.name}` })).getAllByRole("group").length).toBe(st.steps.length);
    expect(within(screen.getByRole("group", { name: "Blocks" })).getAllByRole("button").length).toBe(palette.operators.length);
    expect(await violations()).toEqual([]);
  });

  it("puts a block from the palette into a stack from the keyboard alone", async () => {
    const user = userEvent.setup();
    const { draft } = mount(BlockEditor);
    const st = long(draft);
    screen.getByRole("button", { name: "filter" }).focus();
    await user.keyboard("{Enter}");
    expect(screen.getAllByRole("status").some((x) => /Holding filter/.test(x.textContent ?? ""))).toBe(true);
    screen.getByRole("button", { name: `filter here: ${st.name}, position 2` }).focus();
    await user.keyboard("{Enter}");
    await waitFor(() => expect(steps(draft, st.name).map((s) => s.kind)).toEqual([st.steps[0].kind, "Filter", ...st.steps.slice(1).map((s) => s.kind)]));
    expect(draft.log.length).toBe(1);
    expect(screen.queryByRole("button", { name: / here: / })).toBeNull();
    await waitFor(() => expect(document.activeElement?.getAttribute("aria-label")).toBe(`move filter, step 2 of ${st.name}`));
    expect(await violations()).toEqual([]);
  });

  it("moves a step by Alt+Down and back by Alt+Up, and the draft is as it was", async () => {
    const user = userEvent.setup();
    const { draft } = mount(BlockEditor);
    const before = shape(draft.graph);
    const st = movable(draft);
    const kinds = st.steps.map((s) => s.kind);
    screen.getByRole("button", { name: new RegExp(`^move .*, step 1 of ${st.name}$`) }).focus();
    await user.keyboard("{Alt>}{ArrowDown}{/Alt}");
    await waitFor(() => expect(steps(draft, st.name).map((s) => s.kind)).toEqual([kinds[1], kinds[0], ...kinds.slice(2)]));
    await waitFor(() => expect(document.activeElement?.getAttribute("aria-label")).toMatch(new RegExp(`step 2 of ${st.name}$`)));
    await user.keyboard("{Alt>}{ArrowUp}{/Alt}");
    await waitFor(() => expect(shape(draft.graph)).toEqual(before));
  });

  it("picks a step up with its move button and puts it down in a row set of its own; Escape lets go", async () => {
    const user = userEvent.setup();
    const { draft } = mount(BlockEditor);
    const st = long(draft);
    const move = screen.getByRole("button", { name: new RegExp(`^move .*, step ${st.steps.length} of ${st.name}$`) });
    await user.click(move);
    expect(screen.getAllByRole("button", { name: / here: / }).length).toBeGreaterThan(1);
    await user.keyboard("{Escape}");
    expect(screen.queryByRole("button", { name: / here: / })).toBeNull();
    expect(draft.log.length).toBe(0);
    await user.click(screen.getByRole("button", { name: new RegExp(`^move .*, step ${st.steps.length} of ${st.name}$`) }));
    await user.click(screen.getByRole("button", { name: / here: a new row set$/ }));
    await waitFor(() => expect(steps(draft, "rows1").map((s) => s.kind)).toEqual([st.steps[st.steps.length - 1].kind]));
    expect(steps(draft, st.name).length).toBe(st.steps.length - 1);
  });

  it("a drag is the same pick up and put down", async () => {
    const { draft } = mount(BlockEditor);
    const st = long(draft);
    fireEvent.dragStart(screen.getByRole("button", { name: "distinct" }));
    fireEvent.drop(await screen.findByRole("button", { name: `distinct here: ${st.name}, position ${st.steps.length + 1}` }));
    await waitFor(() => expect(steps(draft, st.name).at(-1)!.kind).toBe("Distinct"));
  });
});
