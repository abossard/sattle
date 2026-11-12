// @vitest-environment jsdom
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { docOf } from "../../src/editing/draft";
import { StructureEditor } from "../../src/editors/StructureEditor";
import { mount, SVC, violations } from "./harness";

const row = (id: string) => document.querySelector<HTMLElement>(`[data-row-id="${CSS.escape(id)}"]`)!;
const statement = (draft: Parameters<typeof docOf>[0], name: string) => docOf(draft, SVC)!.statements.find((s) => s.name === name);

describe("the structure editor", () => {
  it("is a tree of the projection's statements, steps and slots with no accessibility violation", async () => {
    const { draft } = mount(StructureEditor);
    const doc = docOf(draft, SVC)!;
    const tree = screen.getByRole("tree", { name: "Structure of serviceDeps" });
    expect(within(tree).getAllByRole("treeitem").length).toBeGreaterThan(doc.statements.length * 2);
    expect(await violations()).toEqual([]);
  });

  it("adds a statement from the keyboard alone: End, Enter, a name, a kind, Enter", async () => {
    const user = userEvent.setup();
    const { draft } = mount(StructureEditor);
    screen.getAllByRole("treeitem")[0].focus();
    await user.keyboard("{End}{Enter}");
    await user.type(screen.getByRole("textbox", { name: "name" }), "fresh");
    await user.selectOptions(screen.getByRole("combobox", { name: "starts with" }), "filter");
    await user.keyboard("{Enter}");
    await waitFor(() => expect(statement(draft, "fresh")?.steps.map((s) => s.kind)).toEqual(["Filter"]));
    expect(draft.log.length).toBe(1);
  });

  it("edits an expression where it stands, and Escape leaves it alone", async () => {
    const user = userEvent.setup();
    const { draft } = mount(StructureEditor);
    const entry = docOf(draft, SVC)!.statements.flatMap((s) => s.steps).flatMap((s) => s.slots).flatMap((s) => s.entries).find((e) => e.expr !== null && e.expr.includes("!="))!;
    row(entry.id).focus();
    await user.keyboard("{Enter}");
    const box = within(row(entry.id)).getByRole("textbox");
    await user.clear(box);
    await user.type(box, "s == t{Escape}");
    expect(draft.log.length).toBe(0);
    row(entry.id).focus();
    await user.keyboard("{F2}");
    const again = within(row(entry.id)).getByRole("textbox");
    await user.clear(again);
    await user.type(again, "s == t{Enter}");
    await waitFor(() => expect(draft.log.length).toBe(1));
    expect(docOf(draft, SVC)!.statements.flatMap((s) => s.steps).flatMap((s) => s.slots).flatMap((s) => s.entries).some((e) => e.expr === "s == t")).toBe(true);
  });

  it("Delete removes the step under the cursor, and typing Backspace in a field removes nothing", async () => {
    const user = userEvent.setup();
    const { draft } = mount(StructureEditor);
    const st = docOf(draft, SVC)!.statements.find((s) => !s.sink && s.steps.length > 1)!;
    row(`stmt:${st.name}`).focus();
    await user.keyboard("{Enter}");
    await user.keyboard("x{Backspace}{Escape}");
    expect(draft.log.length).toBe(0);
    row(st.steps[0].id).focus();
    await user.keyboard("{Delete}");
    await waitFor(() => expect(statement(draft, st.name)!.steps.length).toBe(st.steps.length - 1));
  });

  it("says why a gesture was refused", async () => {
    const user = userEvent.setup();
    mount(StructureEditor);
    const st = docOf(mount(StructureEditor).draft, SVC)!.statements.find((s) => !s.sink)!;
    row(`stmt:${st.name}`).focus();
    await user.keyboard("{Enter}");
    const box = within(row(`stmt:${st.name}`)).getByRole("textbox");
    await user.clear(box);
    await user.type(box, "not a name{Enter}");
    await waitFor(() => expect(screen.getAllByRole("status").some((x) => /letters, digits/.test(x.textContent ?? ""))).toBe(true));
  });
});
