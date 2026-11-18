// @vitest-environment jsdom
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeAll, describe, expect, it } from "vitest";
import { History } from "../../src/app/history";
import type { Patch } from "../../src/core/model";
import { EditorSession } from "../../src/editor/session";
import { ent, text, contains } from "../../src/editor/patches";
import { runtime } from "../../src/state/runtime";
import { SOURCES } from "../../src/state/sources";
import { dispatch, useStore } from "../../src/state/store";
import { fakeEngine } from "../fixtures/editor-fake";
import { TreeSource } from "../fixtures/tree-source";
import { openApp } from "./helpers";

beforeAll(() => { SOURCES.push({ id: "tree-review", label: "Tree", make: () => new TreeSource() }); });

const P = "d:m/projection:p", OP = `${P}/op:f`;
const SPACE = "d:sp/space:s";
/** The module `m` with one projection, and a graph space holding the graph `master`. */
const seed = () => {
  const edits = [ent("d:m", "Module", { name: text("m") }), ent(P, "Projection", { name: text("p"), purpose: text("view"), forward: { kind: "boolean", value: true } }), ent(OP, "Filter", { name: text("f") }), contains(P, OP, "op"),
    ent(SPACE, "GraphSpace"), ent(`${SPACE}/graph:master`, "Graph", { name: text("master") }), contains(SPACE, `${SPACE}/graph:master`, "graph")];
  return {
    entities: edits.filter((e) => e.kind === "entity").map((e) => (e as unknown as { after: never }).after),
    relationships: edits.filter((e) => e.kind === "relationship").map((e) => (e as unknown as { after: never }).after),
  };
};

/**
 * The dialog that the click opened. It is looked for with `hidden: true`: the modal management of the component library (tabster) hides the
 * other parts of the page from assistive technology with `aria-hidden` and does it after a delay, and on a busy machine a dialog opened right
 * after another test's dialog finds its own portal hidden by that late pass. The dialog is there (a condition that is met), and what the test
 * is about is what it holds, not whether the library has finished hiding the page behind it.
 */
const findDialog = () => screen.findByRole("dialog", { hidden: true });

/** The editor over an engine in memory, with the module `m` and one projection. */
async function openEditor() {
  const eng = fakeEngine(seed());
  await openApp("tree-review", "data");
  // the catalog head the Text tab compares against
  (runtime.ws!.source as unknown as { catalogGraph: string }).catalogGraph = "catalog";
  (runtime.ws as unknown as { histories: Map<string, unknown> }).histories.set("catalog", new History({ version: { tenant: "demo", graph: "catalog", revision: "1" }, entities: [], relationships: [] }));
  runtime.session = await EditorSession.open(eng.svc);
  dispatch({ type: "editor/event", event: { t: "open" } });
  dispatch({ type: "editor/event", event: { t: "opened" } });
  dispatch({ type: "editor/summary", patch: { draft: "d1", base: "11", revision: "0", version: 1, module: "m", projection: P, diagnostics: [] } });
  dispatch({ type: "ui/view", view: "editor" });
  await screen.findByRole("tablist", { name: "Draft details" });
  return eng;
}

describe("the Text tab sends what is typed to the engine", () => {
  it("an edit typed and followed by a click within 0.7 s is not lost", async () => {
    const user = userEvent.setup();
    const eng = await openEditor();
    await user.click(await screen.findByRole("tab", { name: "Text" }));
    const box = await screen.findByLabelText("Text of module m");
    await user.click(box);
    await user.type(box, "x");
    await user.click(screen.getByRole("tab", { name: "Problems" }));          // blur, well inside the debounce
    await new Promise((r) => setTimeout(r, 900));
    expect(eng.calls.checkText, "the blur cancelled the pending check: the edit never reached the engine").toBeGreaterThan(0);
  }, 30000);
});

describe("the new projection dialog writes the projection's KQL file and binds its graph in the space, or says why it cannot", () => {
  it("the graph it produces is named like the projection, not by its first letter", async () => {
    const user = userEvent.setup();
    await openEditor();
    await user.click(await screen.findByRole("button", { name: /New projection/ }));
    const dialog = await findDialog();
    await user.type(within(dialog).getByLabelText(/^Name/), "serviceView");
    expect((within(dialog).getByLabelText(/^Graph it produces in the space/) as HTMLInputElement).value).toBe("serviceView");
  }, 30000);

  it("stores the projection's file in the draft, then binds its graph in the space", async () => {
    const user = userEvent.setup();
    const eng = await openEditor();
    // this engine keeps a text as sent; the projection and the ports a real engine lowers from it are added here at the text's revision, so the bindings find them
    const keep = eng.svc.setDraftText.bind(eng.svc), pr = "d:m/projection:serviceView";
    eng.svc.setDraftText = async (...a) => {
      const done = await keep(...a);
      eng.g.apply({ from: eng.g.version, to: { ...eng.g.version!, revision: String(eng.rev) }, edits: [ent(pr, "Projection"), ...["master", "minReady"].map((p) => ent(`${pr}/port:${p}`, "Port"))] } as Patch);
      return done;
    };
    await user.click(await screen.findByRole("button", { name: /New projection/ }));
    const dialog = await findDialog();
    await user.type(within(dialog).getByLabelText(/^Name/), "serviceView");
    await user.type(within(dialog).getByLabelText(/^History/), "4");
    await user.selectOptions(within(dialog).getByLabelText(/^Output ontology/), "d:m");
    await user.selectOptions(within(dialog).getByLabelText("Expects ontology"), "d:m");
    await user.click(within(dialog).getByRole("button", { name: "Add parameter" }));
    await user.type(within(dialog).getByLabelText("Parameter"), "minReady");
    await user.type(within(dialog).getByLabelText("Default"), "0");
    await user.click(within(dialog).getByRole("button", { name: "Create projection" }));
    await waitFor(() => expect(eng.g.entity(`${SPACE}/binding:serviceView.master`)).toBeDefined());
    expect(eng.files.get("m.serviceView.kql")).toBe("graph serviceView : m with (history = 4);\ninput master : m;\ndeclare query_parameters(minReady:long = 0);\n");
    expect(eng.g.entity(`${SPACE}/graph:serviceView`)?.type).toBe("Graph");
    expect(eng.g.entity(`${SPACE}/binding:serviceView.minReady`), "a parameter with only a default is not bound").toBeUndefined();
    expect(useStore.getState().editor.projection).toBe("d:m/projection:serviceView");
  }, 30000);

  it("a graph name the space has already says why it cannot be made and stores nothing", async () => {
    const user = userEvent.setup();
    const eng = await openEditor();
    await user.click(await screen.findByRole("button", { name: /New projection/ }));
    const dialog = await findDialog();
    await user.type(within(dialog).getByLabelText(/^Name/), "master");
    await user.selectOptions(within(dialog).getByLabelText(/^Output ontology/), "d:m");
    await user.selectOptions(within(dialog).getByLabelText("Expects ontology"), "d:m");
    await user.click(within(dialog).getByRole("button", { name: "Create projection" }));
    expect(await within(dialog).findByText("graph master already exists in the space")).toBeDefined();
    expect(eng.files.size).toBe(0);
  }, 30000);
});

describe("the Editor tab on a source without the engine says why it cannot open", () => {
  it("shows the reason and stops spinning", async () => {
    await openApp("tree-review", "data");
    runtime.session = null;
    dispatch({ type: "editor/open" });
    dispatch({ type: "ui/view", view: "editor" });
    await waitFor(() => expect(useStore.getState().editor.phase.t).not.toBe("opening"), { timeout: 3000 });
    const phase = useStore.getState().editor.phase;
    expect(phase.t === "failed" ? phase.message : phase.t, "the error is overwritten and the tab spins forever").toMatch(/engine/i);
  }, 30000);
});
