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
    ["the master is drawn", { role: "master" }, { nodes: 47, edges: 48 }, true, "graph"],
  ])("%s", (_, over, sum, canDoc, kind) => { expect(tileKind(base(over), sum, canDoc)).toBe(kind); });
});

describe("the line that says what a graph is", () => {
  it.each<[string, Partial<GraphInfo>, string[], string | undefined, string]>([
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
