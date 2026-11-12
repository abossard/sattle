// @vitest-environment jsdom
// Defects of the code review in the screens (section 3): each test says what should be true; each failed for the reason the review gives until its fix.
import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeAll, describe, expect, it } from "vitest";
import { History } from "../../src/app/history";
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
const seed = () => {
  const edits = [ent("d:m", "Module", { name: text("m") }), ent(P, "Projection", { name: text("p"), purpose: text("view"), forward: { kind: "boolean", value: true } }), ent(OP, "Filter", { name: text("f") }), contains(P, OP, "op")];
  return {
    entities: edits.filter((e) => e.kind === "entity").map((e) => (e as unknown as { after: never }).after),
    relationships: edits.filter((e) => e.kind === "relationship").map((e) => (e as unknown as { after: never }).after),
  };
};

/** The editor over an engine in memory, with the module `m`, one projection and one operator on screen. */
async function openEditor() {
  const eng = fakeEngine(seed());
  await openApp("tree-review", "data");
  // the catalog head the Text tab compares against
  (runtime.ws!.source as unknown as { catalogGraph: string }).catalogGraph = "catalog";
  runtime.ws!.histories.set("catalog", new History({ version: { tenant: "demo", graph: "catalog", revision: "1" }, entities: [], relationships: [] }));
  runtime.session = await EditorSession.open(eng.svc);
  dispatch({ type: "editor/summary", patch: { status: "ready", message: "", draft: "d1", base: "11", revision: "0", version: 1, module: "m", projection: P, selected: [OP], diagnostics: [] } });
  dispatch({ type: "ui/view", view: "editor" });
  await screen.findByRole("group", { name: /Projection graph/ });
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

describe("ui/EditorView.tsx:374-418 the root key handler", () => {
  it("Delete or Backspace on a focused button does not delete the selected operators", async () => {
    const user = userEvent.setup();
    const eng = await openEditor();
    const before = eng.calls.applyPatch;
    const zoom = await screen.findByRole("button", { name: "Zoom in" });
    act(() => zoom.focus());
    await user.keyboard("{Backspace}");
    await new Promise((r) => setTimeout(r, 200));
    expect(eng.calls.applyPatch, "a focused button swallowed the key and the operator went").toBe(before);
  }, 30000);

  it("Enter on a focused button presses it", async () => {
    const user = userEvent.setup();
    await openEditor();
    const add = await screen.findByRole("button", { name: /^Add/ });
    act(() => add.focus());
    await user.keyboard("{Enter}");
    expect(await screen.findByRole("dialog", {}, { timeout: 2000 }), "the handler took Enter for 'edit properties'").toBeTruthy();
  }, 30000);
});

describe("ui/editor/NewProjection.tsx:38 the graph a new projection produces", () => {
  it("is named like the projection, not by its first letter", async () => {
    const user = userEvent.setup();
    await openEditor();
    await user.click(await screen.findByRole("button", { name: /New projection/ }));
    const dialog = await screen.findByRole("dialog");
    await user.type(within(dialog).getByLabelText("Name"), "serviceView");
    expect((within(dialog).getByLabelText("Graph it produces in the space") as HTMLInputElement).value).toBe("serviceView");
  }, 30000);
});

describe("state/editor-effects.ts:39 the Editor tab on a source without the engine", () => {
  it("shows the reason and stops spinning", async () => {
    await openApp("tree-review", "data");
    runtime.session = null;
    dispatch({ type: "editor/open" });
    dispatch({ type: "ui/view", view: "editor" });
    await waitFor(() => expect(useStore.getState().editor.status).not.toBe("opening"), { timeout: 3000 });
    expect(useStore.getState().editor.message, "the error is overwritten and the tab spins forever").toMatch(/engine/i);
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
