// @vitest-environment jsdom
// The notations inside the application's editor view, on an engine in memory: a gesture goes through the editor's own state
// (`editor/gesture`, composed and applied by the editor effects) to the draft session, so it is one patch and one step of undo.
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeAll, describe, expect, it } from "vitest";
import { History } from "../../src/app/history";
import { docOf } from "../../src/editing/draft";
import { sessionDraft } from "../../src/editing/session-draft";
import { EditorSession } from "../../src/editor/session";
import { runtime } from "../../src/state/runtime";
import { SOURCES } from "../../src/state/sources";
import { dispatch, useStore } from "../../src/state/store";
import { demoCatalog, palette, skeletonOf } from "../editing/memory-draft";
import { fakeEngine } from "../fixtures/editor-fake";
import { TreeSource } from "../fixtures/tree-source";
import { openApp } from "../ui/helpers";

beforeAll(() => { SOURCES.push({ id: "tree-notations", label: "Tree", make: () => new TreeSource() }); });

const SVC = "d:acme.svc/projection:serviceDeps";

/** The editor view on the demo catalog, held by an engine in memory that has the palette and makes skeletons from it. */
async function openEditor() {
  const eng = fakeEngine(demoCatalog().toSnapshot());
  const svc = eng.svc as unknown as Record<string, unknown>;
  svc.palette = async () => palette;
  svc.skeleton = async (_draft: string, kind: string, container: string, name: string, params: Record<string, string>) => skeletonOf(palette)(kind, container, name, params);
  await openApp("tree-notations", "data");
  (runtime.ws!.source as unknown as { catalogGraph: string }).catalogGraph = "catalog";
  runtime.ws!.histories.set("catalog", new History({ version: { tenant: "demo", graph: "catalog", revision: "1" }, entities: [], relationships: [] }));
  runtime.session = await EditorSession.open(eng.svc);
  dispatch({ type: "editor/summary", patch: { status: "ready", message: "", draft: "d1", base: "11", revision: "0", version: 1, module: "acme.svc", projection: SVC, selected: [], diagnostics: [] } });
  dispatch({ type: "ui/view", view: "editor" });
  await screen.findByRole("group", { name: /Projection graph/ });
  return eng;
}
const predicate = () => docOf(sessionDraft(runtime.session!), SVC)!.statements.find((s) => s.name === "deps")!.steps[1].slots[0].entries[0];
const row = (id: string) => document.querySelector<HTMLElement>(`[data-row-id="${CSS.escape(id)}"]`)!;

describe("the notations in the editor view", () => {
  it("the canvas stays the first thing shown, and the notations are one tab away on the same draft", async () => {
    const user = userEvent.setup();
    await openEditor();
    await user.click(screen.getByRole("tab", { name: "Notations" }));
    expect(await screen.findByRole("tree", { name: "Structure of serviceDeps" })).toBeTruthy();
    expect(screen.queryByRole("group", { name: /Projection graph/ })).toBeNull();
    await user.click(screen.getByRole("tab", { name: "Canvas" }));
    expect(await screen.findByRole("group", { name: /Projection graph/ })).toBeTruthy();
  }, 30_000);

  it("an edit in a notation is one patch on the engine and one step of undo, and Undo takes it back", async () => {
    const user = userEvent.setup();
    const eng = await openEditor();
    await user.click(screen.getByRole("tab", { name: "Notations" }));
    await screen.findByRole("tree", { name: "Structure of serviceDeps" });
    const before = predicate();
    row(before.id).focus();
    await user.keyboard("{Enter}");
    const box = within(row(before.id)).getByRole("textbox");
    await user.clear(box);
    await user.type(box, "s == t{Enter}");
    await waitFor(() => expect(predicate().expr).toBe("s == t"));
    expect(eng.calls.applyPatch).toBe(1);
    await waitFor(() => expect(useStore.getState().editor.undo).toEqual(["set predicate"]));
    // the letters typed in the row did not reach the shortcuts of the canvas (`m` starts a region there)
    expect(screen.queryByText(/Region selection/)).toBeNull();
    await user.click(screen.getByRole("button", { name: "Undo set predicate" }));
    await waitFor(() => expect(predicate().expr).toBe(before.expr));
    expect(eng.calls.applyPatch).toBe(2);
  }, 30_000);

  it("a step added from the blocks palette is selected, so the properties panel shows it", async () => {
    const user = userEvent.setup();
    await openEditor();
    await user.click(screen.getByRole("tab", { name: "Notations" }));
    await user.click(await screen.findByRole("tab", { name: "Blocks" }));
    await user.click(within(screen.getByRole("group", { name: "Blocks" })).getByRole("button", { name: "distinct" }));
    await user.click(screen.getByRole("button", { name: "distinct here: deps, position 3" }));
    await waitFor(() => expect(docOf(sessionDraft(runtime.session!), SVC)!.statements.find((s) => s.name === "deps")!.steps.map((s) => s.kind)).toEqual(["Match", "Filter", "Distinct"]));
    expect(useStore.getState().editor.selected).toEqual([`${SVC}/op:deps`]);
    expect(useStore.getState().editor.undo[0]).toBe("add Distinct");
  }, 30_000);

  it("a gesture the core refuses is said in the editor's notice, and nothing is sent", async () => {
    const user = userEvent.setup();
    const eng = await openEditor();
    await user.click(screen.getByRole("tab", { name: "Notations" }));
    await screen.findByRole("tree", { name: "Structure of serviceDeps" });
    row("stmt:deps").focus();
    await user.keyboard("{Enter}");
    const box = within(row("stmt:deps")).getByRole("textbox");
    await user.clear(box);
    await user.type(box, "not a name{Enter}");
    await waitFor(() => expect(useStore.getState().editor.notice).toMatch(/letters, digits/));
    expect(eng.calls.applyPatch).toBe(0);
  }, 30_000);
});
