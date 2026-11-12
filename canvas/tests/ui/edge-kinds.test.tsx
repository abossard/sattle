// @vitest-environment jsdom
// The edge kinds on screen: a sample of each that tells them apart without color, the legend, the filter that leaves a kind out, the tree that nests by a kind.
import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it } from "vitest";
import { Graph } from "../../src/core/graph";
import { EDGE_KINDS, type EdgeKind, type TreeSpec } from "../../src/source/presentation";
import { runtime } from "../../src/state/runtime";
import { dispatch, useStore } from "../../src/state/store";
import { Legend } from "../../src/ui/DataView";
import { KIND_WORD, KindSample, kindsOf } from "../../src/ui/EdgeKinds";
import { FiltersPanel } from "../../src/ui/Filters";
import { TreeView } from "../../src/ui/TreeView";
import { ont } from "../fixtures/engine-fake";
import { ent, rel, ver } from "../helpers";
import { violations } from "./helpers";

afterEach(() => { cleanup(); runtime.ws = null; });
const edgeKinds: Record<string, EdgeKind> = { Is: "composition", Needs: "dependency", Calls: "communication", Explains: "support" };
const KINDS = EDGE_KINDS;
const wrap = (ui: React.ReactElement) => render(<FluentProvider theme={webLightTheme}>{ui}</FluentProvider>);

describe("a sample of each kind", () => {
  const svg = (k: EdgeKind) => render(<KindSample kind={k} />).container.innerHTML;
  it("no two kinds look the same, and the difference is not a color", () => {
    // the color repeats the kind; without it the pattern and the head still differ
    const looks = KINDS.map((k) => svg(k).replace(/ style="[^"]*"/, ""));
    expect(new Set(looks).size).toBe(KINDS.length);
    for (const l of looks) expect(l).not.toMatch(/#[0-9a-f]{3,6}|rgb\(/i);
  });
  it.each(KINDS)("%s has a name and says what it means", (k) => expect([KIND_WORD[k].word.length > 3, KIND_WORD[k].help.length > 20]).toEqual([true, true]));
  it("lists the kinds a graph declares with their types, in a fixed order", () => {
    expect(kindsOf({ B: "support", A: "composition", C: "composition" })).toEqual([{ kind: "composition", types: ["A", "C"] }, { kind: "support", types: ["B"] }]);
    expect(kindsOf(undefined)).toEqual([]);
  });
});

const fakeWs = (graph: Graph, kinds: Record<string, EdgeKind>, tree?: TreeSpec) => ({
  graphInfo: () => ({ id: "g", role: "projection", descriptor: {}, ontology: ont("o"), inputs: [], edgeKinds: kinds, tree }),
  histories: new Map([["g", { graph, length: 0 }]]), panes: new Map(), hubs: new Map(),
}) as never;
const hubGraph = () => Graph.fromSnapshot({ version: ver("1"), entities: [ent("a"), ent("b")], relationships: Object.keys(edgeKinds).map((t) => rel(`r-${t}`, "a", "b", t)) });

describe("the legend", () => {
  it("names each kind the graph declares with its relationship types, and shows a sample", async () => {
    runtime.ws = fakeWs(hubGraph(), edgeKinds);
    wrap(<Legend graph="g" />);
    await userEvent.setup().click(screen.getByRole("button", { name: "Legend" }));
    const popup = await screen.findByLabelText("How to read the canvas");
    for (const k of KINDS) expect(within(popup).getByText(new RegExp(`^${KIND_WORD[k].word}:`))).toBeTruthy();
    expect(popup.querySelectorAll("svg").length).toBe(KINDS.length);
  });
  it("lists no kinds for a graph that declares none", async () => {
    runtime.ws = fakeWs(hubGraph(), {});
    wrap(<Legend graph="g" />);
    await userEvent.setup().click(screen.getByRole("button", { name: "Legend" }));
    expect((await screen.findByLabelText("How to read the canvas")).querySelectorAll("svg").length).toBe(0);
  });
});

describe("leaving a kind out in the filters", () => {
  const open = async () => {
    runtime.ws = fakeWs(hubGraph(), edgeKinds);
    dispatch({ type: "data/summary", graphs: [], telemetry: {} });
    wrap(<FiltersPanel graph="g" />);
    await userEvent.setup().click(await screen.findByRole("button", { name: "Edge kinds" }));
  };
  it.each(KINDS)("unticking %s leaves exactly it out", async (kind) => {
    await open();
    const box = await screen.findByRole("checkbox", { name: new RegExp(`^${KIND_WORD[kind].word}`) });
    expect((box as HTMLInputElement).checked).toBe(true);
    await userEvent.setup().click(box);
    expect(useStore.getState().filters.g?.hiddenKinds).toEqual([kind]);
    await userEvent.setup().click(box);
    expect(useStore.getState().filters.g?.hiddenKinds).toEqual([]);
  });
  it("the section is not there for a graph without kinds", async () => {
    runtime.ws = fakeWs(hubGraph(), {});
    wrap(<FiltersPanel graph="g" />);
    await screen.findByRole("button", { name: "Types" });
    expect(screen.queryByRole("button", { name: "Edge kinds" })).toBeNull();
  });
  it("has no axe violations", async () => {
    await open();
    expect(await violations()).toEqual([]);
  });
});

describe("the tree chooses which kind is the hierarchy", () => {
  const kinds: Record<string, EdgeKind> = { Contains: "composition", Needs: "dependency" };
  const tree: TreeSpec = { hierarchy: { relationship: "Contains", childIsSource: false }, edgeRoles: { Needs: "cross" }, state: {}, types: ["Row"] };
  const g = Graph.fromSnapshot({
    version: ver("1"),
    entities: ["a", "b", "c"].map((id) => ent(id, "Row", { name: `row ${id}` })),
    relationships: [rel("c1", "a", "b", "Contains"), rel("n1", "c", "a", "Needs")],
  });
  const ws = () => ({
    graphInfo: () => ({ id: "g", role: "projection", descriptor: {}, ontology: { ...ont("o"), relationshipTypes: ["Contains", "Needs"].map((t) => ({ id: { value: t }, sourceTypes: [{ value: "Row" }], targetTypes: [{ value: "Row" }], properties: [] })) }, inputs: [], edgeKinds: kinds, tree }),
    histories: new Map([["g", { graph: g, length: 0, cursor: 0, patches: [] }]]), panes: new Map(), hubs: new Map(), select() {},
  }) as never;
  const rows = () => screen.getAllByRole("treeitem").map((r) => r.textContent ?? "");
  it("opens on composition and offers the other kinds; choosing dependency nests by it", async () => {
    runtime.ws = ws();
    dispatch({ type: "data/summary", graphs: [{ id: "g", role: "projection", head: "1", patches: 0, nodes: 3, edges: 2, version: 0 }], telemetry: {} });
    wrap(<TreeView graph="g" />);
    const choose = await screen.findByRole("combobox", { name: "Hierarchy by" });
    expect((choose as HTMLSelectElement).value).toBe("composition");
    // by composition: "row c" is a root of its own, "row a" holds "row b"
    expect(rows().some((r) => /row a/.test(r))).toBe(true);
    await userEvent.setup().selectOptions(choose, "dependency");
    // by dependency "row c" needs "row a": "row a" is nested under "row c" (the new tree opens on its top levels)
    const level = (name: RegExp) => screen.getAllByRole("treeitem").find((r) => name.test(r.textContent ?? ""))?.getAttribute("aria-level");
    expect([level(/row c/), level(/row a/)]).toEqual(["1", "2"]);
  });
  it("offers no choice when the graph has one kind", async () => {
    runtime.ws = { ...(ws() as object), graphInfo: () => ({ ...(ws() as { graphInfo(): object }).graphInfo(), edgeKinds: { Contains: "composition" } }) } as never;
    dispatch({ type: "data/summary", graphs: [{ id: "g", role: "projection", head: "1", patches: 0, nodes: 3, edges: 2, version: 0 }], telemetry: {} });
    wrap(<TreeView graph="g" />);
    await screen.findByRole("tree");
    expect(screen.queryByRole("combobox", { name: "Hierarchy by" })).toBeNull();
  });
});
