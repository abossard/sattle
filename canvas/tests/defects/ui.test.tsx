// @vitest-environment jsdom
// Defects of the code review in the screens (section 3): each test says what should be true; each failed for the reason the review gives until its fix.
import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
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
import { ont } from "../fixtures/engine-fake";
import { TreeSource } from "../fixtures/tree-source";
import { openApp } from "../ui/helpers";

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

/** The editor over an engine in memory, with the module `m` and one projection. */
/**
 * The dialog that the click opened. It is looked for with `hidden: true`: the modal management of the component library (tabster) hides the
 * other parts of the page from assistive technology with `aria-hidden` and does it after a delay, and on a busy machine a dialog opened right
 * after another test's dialog finds its own portal hidden by that late pass. The dialog is there (a condition that is met), and what the test
 * is about is what it holds, not whether the library has finished hiding the page behind it.
 */
const findDialog = () => screen.findByRole("dialog", { hidden: true });

async function openEditor() {
  const eng = fakeEngine(seed());
  await openApp("tree-review", "data");
  // the catalog head the Text tab compares against
  (runtime.ws!.source as unknown as { catalogGraph: string }).catalogGraph = "catalog";
  runtime.ws!.histories.set("catalog", new History({ version: { tenant: "demo", graph: "catalog", revision: "1" }, entities: [], relationships: [] }));
  runtime.session = await EditorSession.open(eng.svc);
  dispatch({ type: "editor/event", event: { t: "open" } });
  dispatch({ type: "editor/event", event: { t: "opened" } });
  dispatch({ type: "editor/summary", patch: { draft: "d1", base: "11", revision: "0", version: 1, module: "m", projection: P, diagnostics: [] } });
  dispatch({ type: "ui/view", view: "editor" });
  await screen.findByRole("tablist", { name: "Draft details" });
  return eng;
}

describe("ui/EditorView.tsx:113 the Text tab", () => {
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

describe("ui/editor/NewProjection.tsx:38 the graph a new projection produces", () => {
  it("is named like the projection, not by its first letter", async () => {
    const user = userEvent.setup();
    await openEditor();
    await user.click(await screen.findByRole("button", { name: /New projection/ }));
    const dialog = await findDialog();
    await user.type(within(dialog).getByLabelText(/^Name/), "serviceView");
    expect((within(dialog).getByLabelText(/^Graph it produces in the space/) as HTMLInputElement).value).toBe("serviceView");
  }, 30000);
});

describe("ui/editor/NewProjection.tsx a new projection the KQL way", () => {
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

  it("says why it cannot be made and stores nothing", async () => {
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

describe("state/editor-effects.ts:39 the Editor tab on a source without the engine", () => {
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

void fireEvent;

describe("ui/DocumentView.tsx:42-56 switching between two document graphs at the same revision", () => {
  it("does not show the first graph's document under the second while the second loads", async () => {
    const { FluentProvider, webLightTheme } = await import("@fluentui/react-components");
    const { render, cleanup } = await import("@testing-library/react");
    const { DocumentView } = await import("../../src/ui/DocumentView");
    const hist = { cursor: 1, revisionAt: (i: number) => String(i) };
    runtime.ws = {
      histories: new Map([["r", hist], ["s", hist]]),
      source: { document: (g: string, rev?: string) => g === "s" ? new Promise(() => {}) : Promise.resolve({ revision: String(rev), digest: "sha256:".padEnd(30, "0"), markdown: "# Report of r\n" }) },
    } as never;
    const ui = (g: string) => <FluentProvider theme={webLightTheme}><DocumentView graph={g} /></FluentProvider>;
    try {
      const view = render(ui("r"));
      await screen.findByText("Report of r");
      view.rerender(ui("s"));
      expect(screen.queryByText("Report of r"), "the document of r stays on screen under s: only the revision number is compared").toBeNull();
    } finally { cleanup(); runtime.ws = null; }
  });
});

describe("ui/Elements.tsx:40 the list is cut at 300 rows before it is sorted", () => {
  it("sorting by name puts the first name of all elements on top, wherever its slot is", async () => {
    const { FluentProvider, webLightTheme } = await import("@fluentui/react-components");
    const { render, cleanup } = await import("@testing-library/react");
    const { ElementsPanel } = await import("../../src/ui/Elements");
    const { Graph } = await import("../../src/core/graph");
    const { ent: plain } = await import("../helpers");
    const g = Graph.fromSnapshot({
      version: { tenant: "demo", graph: "g", revision: "1" },
      entities: [...Array.from({ length: 400 }, (_, i) => plain(`n${i}`, "Service", { name: `m${String(i).padStart(3, "0")}` })), plain("last", "Service", { name: "a-first-by-name" })],
      relationships: [],
    });
    runtime.ws = { histories: new Map([["g", { graph: g }]]), panes: new Map(), graphInfo: () => undefined } as never;
    try {
      const user = userEvent.setup();
      render(<FluentProvider theme={webLightTheme}><ElementsPanel graph="g" /></FluentProvider>);
      await user.click(await screen.findByRole("button", { name: /^Name/ }));
      const first = screen.getAllByRole("row")[1];
      expect(first.textContent, "the 401st element sorts first by name but was cut before the grid saw it").toMatch(/a-first-by-name/);
    } finally { cleanup(); runtime.ws = null; }
  });
});

describe("ui/Filters.tsx:157 a warning threshold typed with a decimal comma", () => {
  it("never becomes NaN (which draws every element at warning)", async () => {
    const { FluentProvider, webLightTheme } = await import("@fluentui/react-components");
    const { render, cleanup } = await import("@testing-library/react");
    const { FiltersPanel } = await import("../../src/ui/Filters");
    const { Graph } = await import("../../src/core/graph");
    const graph = Graph.fromSnapshot({ version: { tenant: "demo", graph: "g", revision: "1" }, entities: [], relationships: [] });
    runtime.ws = { graphInfo: () => ({ id: "g", role: "master", descriptor: {}, ontology: ont("o"), inputs: [] }), histories: new Map([["g", { graph, length: 0 }]]), panes: new Map(), hubs: new Map() } as never;
    dispatch({ type: "data/summary", graphs: [], telemetry: { g: { hasData: true, enabled: true, flow: false, edge: { width: "", flow: "", state: "" }, node: { ring: "cpu", size: "cpu", state: "cpu" }, meters: [{ id: "cpu", target: "node", label: "CPU", unit: "", min: 0, max: 1, warn: 0.5 }] } } });
    try {
      const user = userEvent.setup();
      render(<FluentProvider theme={webLightTheme}><FiltersPanel graph="g" /></FluentProvider>);
      await user.click(await screen.findByRole("button", { name: "Telemetry" }));
      const box = await screen.findByRole("spinbutton", { name: /warning threshold/ });
      await user.clear(box);
      await user.type(box, "0,7");
      await user.tab();
      expect(Number.isFinite(useStore.getState().telemetry.g.meters[0].warn), `'0,7' reached the store as ${useStore.getState().telemetry.g.meters[0].warn}`).toBe(true);
    } finally { cleanup(); runtime.ws = null; }
  });
});

// Code review 2 (paper/research/12-code-review-2.md, defect 28), the canvas view.
describe("ui/Filters.tsx:140 Clear filters on a tree", () => {
  it("gives the layout back to the graph: a tree laid out layered by default is not pinned to force", async () => {
    const { FluentProvider, webLightTheme } = await import("@fluentui/react-components");
    const { render, cleanup } = await import("@testing-library/react");
    const { FiltersPanel } = await import("../../src/ui/Filters");
    const { Graph } = await import("../../src/core/graph");
    const graph = Graph.fromSnapshot({ version: { tenant: "demo", graph: "g", revision: "1" }, entities: [], relationships: [] });
    // the pane as the filters see it (jsdom has no WebGL, so no real pane): it records what it is told to show; every other call does nothing
    const views: { layout?: string }[] = [];
    const state = { info: { id: "g", ontology: ont("o") }, setView(v: { layout?: string }) { views.push(v); } };
    const pane = new Proxy(state as Record<string | symbol, unknown>, { get: (t, k) => (k in t ? t[k] : () => undefined), set: (t, k, v) => { t[k] = v; return true; } });
    runtime.ws = { graphInfo: () => ({ id: "g", role: "master", descriptor: {}, ontology: ont("o"), inputs: [] }), histories: new Map([["g", { graph, length: 0, cursor: 0, changesBetween: () => null }]]), panes: new Map([["g", pane]]), hubs: new Map() } as never;
    try {
      const user = userEvent.setup();
      dispatch({ type: "filter/set", graph: "g", filter: { layout: "force" } });
      render(<FluentProvider theme={webLightTheme}><FiltersPanel graph="g" /></FluentProvider>);
      await user.click(await screen.findByRole("button", { name: "Property filter" }));
      await user.click(await screen.findByRole("button", { name: "Clear filters" }));
      expect([useStore.getState().filters.g.layout, views.at(-1)?.layout], "clearing the filters kept the layout the person had chosen").toEqual(["auto", "auto"]);
    } finally { cleanup(); runtime.ws = null; }
  });
});
