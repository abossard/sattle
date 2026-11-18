// @vitest-environment jsdom
import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it } from "vitest";
import { Graph } from "../../src/core/graph";
import { runtime } from "../../src/state/runtime";
import { dispatch, useStore } from "../../src/state/store";
import { FiltersPanel } from "../../src/ui/Filters";
import { ont } from "../fixtures/engine-fake";
import { fakeWorkspace } from "../helpers";

afterEach(() => { cleanup(); runtime.ws = null; });

describe("Clear filters gives the layout back to the graph", () => {
  it("a tree laid out layered by default is not pinned to force", async () => {
    const graph = Graph.fromSnapshot({ version: { tenant: "demo", graph: "g", revision: "1" }, entities: [], relationships: [] });
    // the pane as the filters see it (jsdom has no WebGL, so no real pane): it records what it is told to show; every other call does nothing
    const views: { layout?: string }[] = [];
    const state = { info: { id: "g", ontology: ont("o") } };
    const pane = new Proxy(state as Record<string | symbol, unknown>, { get: (t, k) => (k in t ? t[k] : () => undefined), set: (t, k, v) => { t[k] = v; return true; } });
    const info = { id: "g", role: "master", descriptor: {}, ontology: ont("o"), inputs: [] };
    runtime.ws = { ...fakeWorkspace({ g: { graph, length: 0, cursor: 0, changesBetween: () => null } }), info: { graphs: [info] }, graphInfo: () => info, pane: () => pane,
      // what the pane is told: the layout, when the change names it
      applyFilter: (_: string, f: { layout: string }, changed: object) => { if ("layout" in changed) views.push({ layout: f.layout }); } } as never;
    const user = userEvent.setup();
    dispatch({ type: "filter/set", graph: "g", filter: { layout: "force" } });
    render(<FluentProvider theme={webLightTheme}><FiltersPanel graph="g" /></FluentProvider>);
    await user.click(await screen.findByRole("button", { name: "Property filter" }));
    await user.click(await screen.findByRole("button", { name: "Clear filters" }));
    expect([useStore.getState().filters.g.layout, views.at(-1)?.layout], "clearing the filters kept the layout the person had chosen").toEqual(["auto", "auto"]);
  });
});
