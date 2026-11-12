// @vitest-environment jsdom
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { docOf } from "../../src/editing/draft";
import type { EditorProps } from "../../src/editors/host";
import { PipelineEditor } from "../../src/editors/PipelineEditor";
import { freshDraft, mount, SVC, violations } from "./harness";

describe("the pipeline editor", () => {
  it("lists the row sets as tabs and shows the steps of one, with no accessibility violation", async () => {
    const { draft } = mount(PipelineEditor);
    const doc = docOf(draft, SVC)!;
    expect(within(screen.getByRole("tablist", { name: "Row sets" })).getAllByRole("tab").length).toBe(doc.statements.length);
    expect(screen.getAllByRole("group", { name: /^Step \d+: / }).length).toBe(doc.statements[0].steps.length);
    expect(screen.getByText(/does not return the rows after a step/)).toBeTruthy();
    expect(await violations()).toEqual([]);
  });

  it("shows another row set when its tab is chosen, and adds a step to it", async () => {
    const user = userEvent.setup();
    const { draft } = mount(PipelineEditor);
    const st = docOf(draft, SVC)!.statements.find((s) => !s.sink && s.steps.length > 1)!;
    await user.click(screen.getByRole("tab", { name: new RegExp(`^${st.name}`) }));
    expect(screen.getAllByRole("group", { name: /^Step \d+: / }).length).toBe(st.steps.length);
    await user.click(screen.getByRole("button", { name: "+ step, edit" }));
    await user.selectOptions(screen.getByRole("combobox", { name: "step" }), "filter");
    await waitFor(() => expect(docOf(draft, SVC)!.statements.find((s) => s.name === st.name)!.steps.length).toBe(st.steps.length + 1));
    await waitFor(() => expect(screen.getAllByRole("group", { name: /^Step \d+: / }).length).toBe(st.steps.length + 1));
  });

  it("removes a step with its button", async () => {
    const user = userEvent.setup();
    const { draft } = mount(PipelineEditor);
    const st = docOf(draft, SVC)!.statements[0];
    await user.click(within(screen.getAllByRole("group", { name: /^Step 1: / })[0]).getAllByRole("button", { name: /^remove / })[0]);
    await waitFor(() => expect(docOf(draft, SVC)!.statements.find((s) => s.name === st.name)?.steps.length ?? 0).toBe(st.steps.length - 1));
  });

  it("shows the rows after a step when the engine returns them", () => {
    const draft = freshDraft();
    const first = docOf(draft, SVC)!.statements[0].steps[0].id;
    const WithRows = (p: EditorProps) => <PipelineEditor {...p} rowsAfter={(id) => (id === first ? { columns: ["a", "b"], rows: [["1", "2"]], total: 1 } : undefined)} />;
    mount(WithRows, SVC, draft);
    expect(screen.getByRole("table", { name: "1 rows after this step" })).toBeTruthy();
    expect(screen.queryByText(/does not return the rows after a step/)).toBeNull();
  });
});
