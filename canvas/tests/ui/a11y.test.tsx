// @vitest-environment jsdom
// The one file that runs axe: a row per view and state worth checking. Rows of one source that a person reaches in turn share one start.
import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeAll, describe, expect, it } from "vitest";
import { Graph } from "../../src/core/graph";
import type { EdgeKind } from "../../src/source/presentation";
import { runtime } from "../../src/state/runtime";
import { SOURCES } from "../../src/state/sources";
import { dispatch, useStore } from "../../src/state/store";
import type { ThemeName, View } from "../../src/state/types";
import { Legend } from "../../src/ui/DataView";
import { DocumentView } from "../../src/ui/DocumentView";
import { IdentityEffectTable } from "../../src/ui/EditorView";
import { FiltersPanel } from "../../src/ui/Filters";
import { ont } from "../fixtures/engine-fake";
import { TreeSource } from "../fixtures/tree-source";
import { ent, fakeWorkspace, rel, ver } from "../helpers";
import { openApp, openDialog, violations } from "./helpers";

// the default source is static files of the engine's output: serve them to fetch from the public directory
const files = import.meta.glob("/public/demo/estate/*.{json,ndjson,md}", { query: "?raw", import: "default", eager: true }) as Record<string, string>;
beforeAll(() => {
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    const text = files[`/public${new URL(String(input), "http://localhost/").pathname}`];
    return text === undefined ? new Response("", { status: 404 }) : new Response(text);
  }) as typeof fetch;
  SOURCES.push({ id: "tree-test", label: "Tree test", make: () => new TreeSource() });
});

/** axe on each state in turn, by name: a failure names the state it was found in. */
async function each<T extends string>(states: readonly T[], go: (state: T) => Promise<void>) {
  const found: Record<string, string[]> = {};
  for (const s of states) { await go(s); found[s] = await violations(); }
  expect(found).toEqual(Object.fromEntries(states.map((s) => [s, []])));
}

describe("the application has no axe violations in any view, theme, dialog or panel a person reaches", () => {
  it("in each main view, switched in turn", async () => {
    await openApp("synth-small", "data");
    await each(["data", "space", "ontology", "editor"] as View[], async (view) => { dispatch({ type: "ui/view", view }); });
  });

  it("in the data view in each theme, switched in turn", async () => {
    await openApp("synth-small", "data");
    await each(["light", "dark", "contrast"] as ThemeName[], async (theme) => { dispatch({ type: "ui/theme", theme }); });
  });

  // a start each: a dialog opened after another finds the page behind it hidden by the first one's late aria-hidden pass (see editor.test.tsx, findDialog)
  it.each([["shortcuts"], ["rejections"]] as const)("with the %s dialog open", async (dialog) => {
    await openApp("synth-small", "data");
    openDialog(dialog);
    await waitFor(() => expect(screen.getByRole("dialog")).toBeTruthy());
    expect(await violations()).toEqual([]);
  });

  it("the element list and the inspector with a selection", async () => {
    const user = userEvent.setup();
    await openApp("synth-small", "data");
    await user.click(await screen.findByRole("tab", { name: "Elements" }));
    await screen.findByRole("grid", { name: /Nodes of the graph/ });
    const ws = runtime.ws!;
    const graph = useStore.getState().activeGraph;
    const id = ws.history(graph)!.graph.entities().next().value!.id;
    dispatch({ type: "select/element", selection: { graph, kind: "entity", id } });
    await screen.findByRole("heading", { level: 2, name: /./ });
    expect(await violations()).toEqual([]);
  }, 60000);   // a ceiling for a busy machine: axe over the whole page with a grid and the inspector is the slowest check here

  it.each<[string, string, View, () => Promise<unknown>]>([
    ["the color legend of nodes and edges", "synth-small", "data", async () => {
      await userEvent.setup().click(await screen.findByRole("button", { name: /Color by property/ }));
      await screen.findByRole("list", { name: /Legend of nodes: health/ });
      return screen.findByRole("list", { name: /Legend of edges: health/ });
    }],
    ["the space of the default source with a tile for every graph", "demo", "space", async () => {
      for (const g of runtime.ws!.info.graphs) await screen.findByRole("group", { name: `Graph ${g.id}` });
    }],
    ["the workload of the default source as a tree", "demo", "data", () => {
      dispatch({ type: "ui/activeGraph", graph: "workload" });
      return screen.findByRole("tree", { name: "Tree of workload" }, { timeout: 10000 });
    }],
    ["the tree view with rows", "tree-test", "data", () => screen.findByRole("tree", { name: "Tree of tree" })],
  ])("%s", async (_, source, view, show) => {
    await openApp(source, view);
    await show();
    expect(await violations()).toEqual([]);
  }, 30000);
});

const edgeKinds: Record<string, EdgeKind> = { Is: "composition", Needs: "dependency", Calls: "communication", Explains: "support" };
const kindsWorkspace = () => ({
  graphInfo: () => ({ id: "g", role: "projection", descriptor: {}, ontology: ont("o"), inputs: [], edgeKinds }),
  ...fakeWorkspace({ g: { graph: Graph.fromSnapshot({ version: ver("1"), entities: [ent("a"), ent("b")], relationships: Object.keys(edgeKinds).map((t) => rel(`r-${t}`, "a", "b", t)) }), length: 0 } }),
}) as never;
const wrap = (ui: React.ReactElement) => render(<FluentProvider theme={webLightTheme}>{ui}</FluentProvider>);
const c = (minted: string, aliased: string, merged: string, conflicts: string) => ({ minted, aliased, merged, conflicts });
const MD = "# Report\n\n- Items: 2\n\n| Name | Count |\n| --- | --- |\n| a | 1 |\n| b \\| c | 1 |\n";

describe("a screen shown on its own has no axe violations", () => {
  it.each<[string, () => Promise<unknown>]>([
    ["the document view with a document", () => {
      runtime.ws = {
        ...fakeWorkspace({ r: { cursor: 2, revisionAt: (i: number) => String(i) } }),
        source: { document: async (_g: string, rev?: string) => ({ revision: String(rev), digest: `sha256:${rev}`.padEnd(30, "0"), markdown: MD }) },
      } as never;
      dispatch({ type: "data/summary", graphs: [{ id: "r", role: "projection", head: "2", patches: 2, nodes: 0, edges: 0, version: 0 }], windows: {} });
      wrap(<DocumentView graph="r" />);
      return screen.findByText("Items: 2");
    }],
    ["the identity effect table", async () => {
      render(<IdentityEffectTable identity={{ preview: c("99", "4", "6", "0"), live: c("94", "4", "1", "0"), comparable: true, difference: c("5", "0", "5", "0") }} />);
    }],
    ["the legend of the edge kinds", async () => {
      runtime.ws = kindsWorkspace();
      wrap(<Legend graph="g" />);
      await userEvent.setup().click(screen.getByRole("button", { name: "Legend" }));
      return screen.findByLabelText("How to read the canvas");
    }],
    ["the edge kinds section of the filters", async () => {
      runtime.ws = kindsWorkspace();
      dispatch({ type: "data/summary", graphs: [], windows: {} });
      wrap(<FiltersPanel graph="g" />);
      await userEvent.setup().click(await screen.findByRole("button", { name: "Edge kinds" }));
    }],
  ])("%s", async (_, show) => {
    // a workspace made by hand has nothing to dispose of: it is let go here, before the shared clean up looks for one
    try {
      await show();
      expect(await violations()).toEqual([]);
    } finally { cleanup(); runtime.ws = null; }
  });
});
