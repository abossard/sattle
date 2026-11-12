// @vitest-environment jsdom
// What an edge means (engine 3.17): drawn by line pattern and head, left out by kind, a tile with fewer of them, a tree by the kind that nests.
import { describe, expect, it, vi } from "vitest";

vi.mock("../src/render/view", () => ({
  GraphView: class {
    constructor() {
      const own: Record<string | symbol, unknown> = {};
      return new Proxy(own, { get: (t, k) => (k in t ? t[k] : () => ({ x: 0, y: 0 })), set: (t, k, v) => { t[k] = v; return true; } });
    }
  },
}));
vi.mock("../src/layout/client", () => ({ LayoutClient: class { lastMs = 0; run() { return { done: new Promise(() => {}) }; } cancel() {} dispose() {} } }));

import { DataPane } from "../src/app/data-pane";
import { defaultHiddenKinds } from "../src/app/graph-view-model";
import { History } from "../src/app/history";
import { buildTree, declaredHierarchyKind, hierarchyKinds, specForKind } from "../src/app/tree-model";
import { EdgeKindCode, EdgeStyle, Scene } from "../src/render/scene";
import { darkTheme } from "../src/render/theme";
import { EDGE_KINDS, type EdgeKind, type TreeSpec } from "../src/source/presentation";
import type { GraphInfo } from "../src/source/types";
import { ont } from "./fixtures/engine-fake";
import { ent, rel, ver } from "./helpers";

const KINDS = EDGE_KINDS;
const events = { select() {}, hover() {}, activate() {}, changed() {}, announce() {}, key: () => false };
const scene = (p: DataPane) => (p as unknown as { scene: Scene }).scene;

describe("the kind of an edge in the scene", () => {
  it.each(KINDS)("%s survives a state change of the edge", (kind) => {
    const s = new Scene();
    s.setEdge(0, 0, 1, 0xffffffff, EdgeStyle.Solid, 0, 0, EdgeKindCode[kind]);
    expect(s.edgeKind(0)).toBe(EdgeKindCode[kind]);
    s.setEdgeState(0, 5);
    expect([s.edgeKind(0), s.edgeState(0), s.edgeStyle(0)]).toEqual([EdgeKindCode[kind], 5, EdgeStyle.Solid]);
  });
  it("the codes are different, and none is the code of an edge without a kind", () => { const codes = KINDS.map((k) => EdgeKindCode[k]); expect([new Set(codes).size, (codes as number[]).includes(0)]).toEqual([KINDS.length, false]); });
  it("an edge without a kind has code 0", () => { const s = new Scene(); s.setEdge(0, 0, 1, 1, EdgeStyle.Solid); expect(s.edgeKind(0)).toBe(0); });
});

/** One type of relationship per kind, one without; a hub with an edge of each, so every edge is between nodes the graph holds. */
const types: Record<string, EdgeKind | undefined> = { Is: "composition", Needs: "dependency", Calls: "communication", Explains: "support", Plain: undefined };
const info = (edgeKinds: Record<string, EdgeKind> | undefined): GraphInfo => ({ id: "g", role: "projection", descriptor: {}, ontology: ont("o"), inputs: [], edgeKinds } as unknown as GraphInfo);
const world = (relTypes = Object.keys(types)) => new History({ version: ver("1"), entities: [ent("hub"), ...relTypes.map((t) => ent(`n-${t}`))], relationships: relTypes.map((t) => rel(`r-${t}`, "hub", `n-${t}`, t)) });
const declared = Object.fromEntries(Object.entries(types).filter(([, k]) => k)) as Record<string, EdgeKind>;
const paneOf = (i: GraphInfo, h: History, tile = false) => { const p = new DataPane(i, h, document.createElement("div"), darkTheme, events as never); p.setTile(tile); p.refreshAll(); return p; };
const drawn = (p: DataPane, h: History) => Object.fromEntries(Object.keys(types).map((t) => { const slot = h.graph.edgeSlot.get(`r-${t}`)!; return [t, scene(p).edgeVisible(slot) ? scene(p).edgeKind(slot) : null]; }));

describe("a pane draws each edge with the kind its type declares", () => {
  it("every kind has its own code and an edge without a kind has none", () => {
    const h = world(); const p = paneOf(info(declared), h);
    expect(drawn(p, h)).toEqual({ Is: EdgeKindCode.composition, Needs: EdgeKindCode.dependency, Calls: EdgeKindCode.communication, Explains: EdgeKindCode.support, Plain: 0 });
  });
  it.each(KINDS)("leaving %s out hides its edges and only those", (kind) => {
    const h = world(); const p = paneOf(info(declared), h);
    p.setKindFilter([kind]);
    const shown = drawn(p, h);
    for (const [t, k] of Object.entries(types)) expect(shown[t] !== null, `${t} (${k ?? "no kind"})`).toBe(k !== kind);
  });
  it("a graph that declares no kind draws every edge, and has no kinds to leave out", () => {
    const h = world(); const p = paneOf(info(undefined), h);
    expect(Object.values(drawn(p, h))).toEqual([0, 0, 0, 0, 0]);
    expect(p.presentKinds().size).toBe(0);
  });
});

describe("a tile leaves out what does not make the graph readable at its size", () => {
  it.each<[string, Set<EdgeKind>, boolean, EdgeKind[]]>([
    ["in the view nothing is left out", new Set<EdgeKind>(["composition", "communication"]), false, []],
    ["a tile with composition leaves out communication and support", new Set<EdgeKind>(["composition", "communication", "support"]), true, ["communication", "support"]],
    ["a tile with dependency does too", new Set<EdgeKind>(["dependency", "communication"]), true, ["communication", "support"]],
    ["a tile of a graph that is all communication draws what it has", new Set<EdgeKind>(["communication"]), true, []],
    ["a tile of a graph with no kinds draws what it has", new Set<EdgeKind>(), true, []],
  ])("%s", (_, present, tile, hidden) => expect(defaultHiddenKinds(tile, present)).toEqual(hidden));

  it("the pane of a tile draws composition and dependency, the person's choice overrides, and opening the graph draws all", () => {
    const h = world(); const p = paneOf(info(declared), h, true);
    expect(Object.fromEntries(Object.entries(drawn(p, h)).map(([t, k]) => [t, k !== null]))).toEqual({ Is: true, Needs: true, Calls: false, Explains: false, Plain: true });
    p.setKindFilter([]);                                              // the person turned them all on
    expect(Object.values(drawn(p, h)).every((k) => k !== null)).toBe(true);
    p.setKindFilter(null); p.setTile(false);                          // opened: the default of the view
    expect(Object.values(drawn(p, h)).every((k) => k !== null)).toBe(true);
  });
  it("the tile of a graph whose edges are all communication still draws them", () => {
    const h = world(["Calls"]); const p = paneOf(info({ Calls: "communication" }), h, true);
    expect(scene(p).edgeVisible(h.graph.edgeSlot.get("r-Calls")!)).toBe(true);
  });
});

describe("a tree by the kind that nests", () => {
  // a service tree: Contains (composition, the declared hierarchy) nests the rows; DependsOn (dependency) and Calls (communication) are links; Evidence supports
  const edgeKinds: Record<string, EdgeKind> = { Contains: "composition", Needs: "dependency", Calls: "communication", Evidence: "support" };
  const tree: TreeSpec = { hierarchy: { relationship: "Contains", childIsSource: false }, edgeRoles: { Needs: "cross", Calls: "cross", Evidence: "support" }, state: {}, types: ["Row"] };
  const ontology = { ...ont("o"), relationshipTypes: [
    { id: { value: "Contains" }, sourceTypes: [{ value: "Row" }], targetTypes: [{ value: "Row" }], properties: [] },
    { id: { value: "Needs" }, sourceTypes: [{ value: "Row" }], targetTypes: [{ value: "Row" }], properties: [] },
    { id: { value: "Calls" }, sourceTypes: [{ value: "Row" }], targetTypes: [{ value: "Row" }], properties: [] },
  ] };
  const i = { tree, edgeKinds, ontology } as unknown as GraphInfo;
  const g = new History({
    version: ver("1"),
    entities: ["a", "b", "c", "d"].map((id) => ent(id, "Row", { name: id })),
    relationships: [rel("c1", "a", "b", "Contains"), rel("c2", "a", "c", "Contains"), rel("n1", "b", "d", "Needs"), rel("n2", "c", "d", "Needs"), rel("k1", "d", "a", "Calls")],
  }).graph;

  it("composition is the default, as the ontology declares it", () => {
    expect([declaredHierarchyKind(i), specForKind(i, "composition")]).toEqual(["composition", tree]);
    expect(hierarchyKinds(i)).toEqual(["composition", "dependency", "communication"]);
  });
  it("a graph that declares no kind has composition", () => expect([declaredHierarchyKind({ tree } as GraphInfo), hierarchyKinds({ tree } as GraphInfo)]).toEqual(["composition", ["composition"]]));
  it.each<[EdgeKind, Record<string, string | null>]>([
    ["composition", { a: null, b: "a", c: "a", d: null }],
    ["dependency", { a: null, b: null, c: null, d: "b" }],       // d is needed by b and c: it nests under the first, the other need is a link
    ["communication", { a: "d", b: null, c: null, d: null }],
  ])("%s nests its own edges; the others are links", (kind, parents) => {
    const spec = specForKind(i, kind)!;
    const m = buildTree(g, spec, []);
    expect(Object.fromEntries([...m.nodes].map(([id, n]) => [id, n.parent]))).toEqual(parents);
    // every edge that does not nest is a link on both of its ends
    const links = [...m.nodes.values()].reduce((n, x) => n + x.cross, 0);
    const nesting = [...m.nodes.values()].filter((x) => x.parent !== null).length;
    expect(links, `${kind}: 5 edges, ${nesting} nest`).toBe((5 - nesting) * 2);
  });
});
