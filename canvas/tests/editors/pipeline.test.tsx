// @vitest-environment jsdom
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { docOf } from "../../src/editing/draft";
import type { EditorProps } from "../../src/editors/host";
import { PipelineEditor, type StepResults } from "../../src/editors/PipelineEditor";
import { freshDraft, mount, SVC, violations } from "./harness";

describe("the pipeline editor", () => {
  it("lists the row sets as tabs and shows the steps of one, with no accessibility violation", async () => {
    const { draft } = mount(PipelineEditor);
    const doc = docOf(draft, SVC)!;
    expect(within(screen.getByRole("tablist", { name: "Row sets" })).getAllByRole("tab").length).toBe(doc.statements.length);
    expect(screen.getAllByRole("group", { name: /^Step \d+: / }).length).toBe(doc.statements[0].steps.length);
    expect(screen.getByText(/come from the engine/)).toBeTruthy();
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

  it("asks for the rows after each step, and shows them under each step", async () => {
    const user = userEvent.setup();
    const draft = freshDraft();
    const first = docOf(draft, SVC)!.statements[0].steps[0].id;
    let asked = 0;
    const results = (loaded: boolean): StepResults => ({
      rowsAfter: (id) => (loaded && id === first ? { columns: ["s", "n"], rows: [["web", "2"]], total: 3, complete: false } : loaded ? { columns: [], rows: [], total: 0, complete: true } : undefined),
      load: () => { asked++; }, loading: false, notice: "", stale: false,
      columnsAfter: (id) => (id === first ? ["s: Service", "n: Integer"] : undefined),
    });
    const Before = (p: EditorProps) => <PipelineEditor {...p} results={results(false)} />;
    const page = mount(Before, SVC, draft);
    await user.click(screen.getByRole("button", { name: "Show the rows after each step" }));
    expect(asked).toBe(1);
    page.unmount();
    const After = (p: EditorProps) => <PipelineEditor {...p} results={results(true)} />;
    mount(After, SVC, draft);
    expect(screen.getByRole("table", { name: "3 rows after this step" })).toBeTruthy();
    expect(screen.getByText("The first 1 of 3 rows after this step")).toBeTruthy();
    expect(screen.getByText("gives s: Service, n: Integer")).toBeTruthy();
    expect(screen.getAllByText("No rows after this step.").length).toBeGreaterThan(0);
    expect(await violations()).toEqual([]);
  });

  it("says where the rows would come from when no engine answers", () => {
    mount(PipelineEditor);
    expect(screen.getByText(/come from the engine/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Show the rows after each step" })).toBeNull();
  });
});
