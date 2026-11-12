import { describe, expect, it } from "vitest";
import { describeGraph, tileKind, typesHeld, purposesOf } from "../src/app/tile";
import { labelPadding } from "../src/app/graph-view-model";
import { parseDocument, previewBlocks } from "../src/core/markdown";
import { Graph } from "../src/core/graph";
import type { GraphInfo } from "../src/source/types";
import { ont } from "./fixtures/engine-fake";
import { ent, ver } from "./helpers";

const base = (over: Partial<GraphInfo>): GraphInfo => ({ id: "g", role: "projection", descriptor: {}, ontology: ont("o") as never, inputs: ["master"], ...over } as GraphInfo);

describe("what a tile shows", () => {
  it.each<[string, Partial<GraphInfo>, { nodes: number; edges: number }, boolean, string]>([
    ["a document graph shows the document", { document: true }, { nodes: 7, edges: 0 }, true, "document"],
    ["a document graph of a source that cannot render documents is a graph", { document: true }, { nodes: 7, edges: 0 }, false, "graph"],
    ["a projection with nodes and no edges says why", { ontology: { ...ont("o"), relationshipTypes: [{ id: { value: "Calls" }, sourceTypes: [], targetTypes: [], properties: [] }] } as never }, { nodes: 6, edges: 0 }, true, "notice"],
    ["a graph with edges is drawn", {}, { nodes: 6, edges: 5 }, true, "graph"],
    ["a small master is drawn", { role: "master" }, { nodes: 12, edges: 10 }, true, "graph"],
    ["a master of an estate is counted, not drawn", { role: "master" }, { nodes: 87, edges: 72 }, true, "summary"],
  ])("%s", (_, over, sum, canDoc, kind) => { expect(tileKind(base(over), sum, canDoc)).toBe(kind); });
});

describe("the line that says what a graph is", () => {
  it.each<[string, Partial<GraphInfo>, string[], string | undefined, string]>([
    ["only the first sentence of a description is the caption", { description: "Services and who calls whom. The rest says how the graph is derived, lifted from calls." }, ["Service"], undefined, "Services and who calls whom."],
    ["a description without a full stop is whole", { description: "Services and who calls whom" }, ["Service"], undefined, "Services and who calls whom"],
    ["the purpose the source gives wins", { description: "Which services depend on which" }, ["Service"], "from the catalog", "Which services depend on which"],
    ["else the purpose in the catalog", { projectionName: "serviceTree" }, ["Service"], "Services and their groups", "Services and their groups"],
    ["else the types it holds and where it comes from", {}, ["Service", "Group"], undefined, "Service, Group from master"],
    ["a document says it is a report", { document: true }, [], undefined, "A report from master, written as a document"],
    ["the master says what was ingested", { role: "master", inputs: [] }, ["Pod", "Node"], undefined, "Everything ingested: Pod, Node"],
    ["a hierarchy names the relationship without its module", { tree: { hierarchy: { relationship: "acme.health.Contains", childIsSource: false }, edgeRoles: {}, state: {} } }, ["Component"], undefined, "A hierarchy of Component over Contains from master"],
    ["many types are counted", {}, ["A", "B", "C", "D", "E", "F"], undefined, "A, B, C, D and 2 more from master"],
  ])("%s", (_, over, present, purpose, line) => expect(describeGraph(base(over), present, () => purpose)).toBe(line));

  it("the catalog gives the purpose of a projection by name", () => {
    const catalog = Graph.fromSnapshot({ version: ver("1"), entities: [ent("d:m/projection:p", "Projection", { name: "serviceTree", purpose: "Services by namespace" }), ent("d:m/projection:q", "Projection", { name: "other" })], relationships: [] });
    const purpose = purposesOf(catalog);
    expect([purpose("serviceTree"), purpose("other"), purpose("missing")]).toEqual(["Services by namespace", undefined, undefined]);
  });
  it("types are listed by how many there are", () => {
    const g = Graph.fromSnapshot({ version: ver("1"), entities: [ent("a", "Pod"), ent("b", "Node"), ent("c", "Pod")], relationships: [] });
    expect(typesHeld(g)).toEqual(["Pod", "Node"]);
  });
});

describe("a document in a tile", () => {
  const md = "# Report\n\n- a: 1\n- b: 2\n\n| Name | Count |\n| --- | --- |\n| a | 1 |\n| b | 2 |\n| c | 3 |\n| d | 4 |\n| e | 5 |\n| f | 6 |\n";
  it("keeps the title, the figures and the first rows of a table, and says that it is cut", () => {
    const { blocks, cut } = previewBlocks(parseDocument(md), { blocks: 8, tableRows: 3 });
    expect(blocks.map((b) => b.kind)).toEqual(["heading", "list", "table"]);
    expect(blocks[2].kind === "table" && blocks[2].rows.length).toBe(3);
    expect(cut).toBe(true);
  });
  it("a short document is shown whole", () => expect(previewBlocks(parseDocument("# T\n\ntext\n")).cut).toBe(false));
});

describe("the room a fitted graph leaves for the labels at its edges", () => {
  it.each([[[], 40], [["web"], 40], [["a service name of some length"], 107], [["x".repeat(200)], 117]])("%j is %d px", (labels, px) => expect(Math.round(labelPadding(labels))).toBe(px));
});

import { countsByType, isolatedNodes, tileThinsIsolated, TREE_TILE_NODES } from "../src/app/tile";
import { buildTree, hasHierarchyEdges, tileRows, treeRowCount } from "../src/app/tree-model";
import { shortLabel } from "../src/app/data-pane";
import type { TreeSpec } from "../src/source/presentation";
import { rel } from "./helpers";

describe("a graph with a hierarchy is a list in a tile when it is too big to draw", () => {
  const tree = { hierarchy: { relationship: "Contains", childIsSource: false }, edgeRoles: {}, state: {}, types: ["Row"] } as TreeSpec;
  it.each<[string, number, number, string]>([
    ["a small tree is drawn", 3, 10, "graph"],
    ["a big graph whose tree has rows is a list", 5, TREE_TILE_NODES, "tree"],
    ["a big graph whose tree has almost no rows is drawn", 2, 80, "graph"],
  ])("%s", (_, rows, nodes, kind) => expect(tileKind(base({ tree }), { nodes, edges: nodes }, true, rows)).toBe(kind));
  it("a graph that marks a hierarchy and has none of its edges has no tree to list", () => {
    const g = Graph.fromSnapshot({ version: ver("1"), entities: [ent("a", "Row"), ent("b", "Row")], relationships: [rel("r", "a", "b", "Rolls")] });
    expect([hasHierarchyEdges(g, tree), treeRowCount(g, tree)]).toEqual([false, 2]);
  });
});

describe("what a tile shows of a tree", () => {
  const tree = { hierarchy: { relationship: "Contains", childIsSource: false }, edgeRoles: {}, state: { Row: { rolledUp: "state" } }, types: ["Row"] } as TreeSpec;
  const hints = [{ property: "state", roles: { Healthy: "ok", Unhealthy: "danger" } }] as never;
  const world = (states: Record<string, string>) => {
    const ids = Object.keys(states);
    return Graph.fromSnapshot({ version: ver("1"), entities: ids.map((id) => ent(id, "Row", { name: id, state: states[id] })), relationships: ids.slice(1).map((id, i) => rel(`c${i}`, ids[Math.floor(i / 2)], id, "Contains")) });
  };
  it("shows the paths to where something is wrong, the worst first", () => {
    const m = buildTree(world({ a: "Healthy", b: "Healthy", c: "Healthy", d: "Healthy", e: "Unhealthy", f: "Healthy", g: "Healthy" }), tree, hints);
    const { rows, more } = tileRows(m);
    expect(rows.map((r) => r.node.id)).toContain("e");
    expect([rows[0].node.id, more]).toEqual(["a", 2]);   // the two healthy leaves that are on no path to the problem
    expect(rows.find((r) => r.node.id === "g")).toBeUndefined();            // a healthy leaf elsewhere is not on a path to a problem
  });
  it("without a problem shows the top of the tree, level by level, cut at the limit", () => {
    const states = Object.fromEntries(Array.from({ length: 30 }, (_, i) => [`n${String(i).padStart(2, "0")}`, "Healthy"]));
    const m = buildTree(world(states), tree, hints);
    const { rows, more } = tileRows(m, 8);
    expect([rows.length, more, rows[0].level]).toEqual([8, 22, 1]);
  });
});

describe("a tile leaves out what no edge reaches", () => {
  const g = (entities: string[], edges: [string, string][]) => Graph.fromSnapshot({ version: ver("1"), entities: entities.map((id) => ent(id, "T")), relationships: edges.map(([a, b], i) => rel(`r${i}`, a, b, "R")) });
  it.each<[string, Graph, number, boolean]>([
    ["some of the nodes have no edge: they are left out", g(["a", "b", "c", "d"], [["a", "b"]]), 2, true],
    ["every node has one: nothing is left out", g(["a", "b"], [["a", "b"]]), 0, false],
    ["no node has one: the picture is all there is", g(["a", "b"], []), 2, false],
  ])("%s", (_, graph, isolated, thins) => expect([isolatedNodes(graph), tileThinsIsolated(graph)]).toEqual([isolated, thins]));
  it("counts what a graph holds by type, the most numerous first", () => {
    const graph = Graph.fromSnapshot({ version: ver("1"), entities: [ent("a", "Pod"), ent("b", "Pod"), ent("c", "Node")], relationships: [rel("r1", "a", "c", "RunsOn"), rel("r2", "b", "c", "RunsOn")] });
    expect(countsByType(graph)).toEqual({ entities: [["Pod", 2], ["Node", 1]], relationships: [["RunsOn", 2]] });
  });
});

describe("a label that is a path", () => {
  it.each<[string, string, string]>([
    ["is its last segment", "/subscriptions/x/resourcegroups/rg-shop/providers/microsoft.sql/servers/pg-shop", "pg-shop"],
    ["a trailing slash does not make it empty", "rg/sub-0001/rg-shop/", "rg-shop"],
    ["a name is left alone", "aks-synth", "aks-synth"],
    ["a long name is cut", "x".repeat(40), `${"x".repeat(29)}…`],
  ])("%s", (_, label, want) => expect(shortLabel(label)).toBe(want));
});
