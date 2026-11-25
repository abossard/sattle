// @vitest-environment jsdom
// What an edge means: how the declared kinds are read on a workload (what restates others, which compositions stack, where the elements beside
// go, which part is to blame, the tree by dependency), how a pane draws and leaves them out, and how the legend, the filters and the tree show them.
// The WebGL view and the layout worker are replaced; the legend, the filters and the tree do not use them.
import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";

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
import { besidePositions, blamePath, causeLinks, kindAxes, restatingTypes } from "../src/app/edge-kinds";
import { defaultHiddenKinds } from "../src/app/graph-view-model";
import { History } from "../src/app/history";
import { buildTree, declaredHierarchyKind, hierarchyKinds, specForKind } from "../src/app/tree-model";
import { Graph } from "../src/core/graph";
import { EdgeKindCode, EdgeStyle, Scene } from "../src/render/scene";
import { darkTheme } from "../src/render/theme";
import { EDGE_KINDS, type EdgeKind, type TreeSpec } from "../src/source/presentation";
import type { GraphInfo } from "../src/source/types";
import { runtime } from "../src/state/runtime";
import { dispatch, useStore } from "../src/state/store";
import { Legend } from "../src/ui/DataView";
import { KIND_WORD, KindSample, kindsOf } from "../src/ui/EdgeKinds";
import { FiltersPanel } from "../src/ui/Filters";
import { TreeView } from "../src/ui/TreeView";
import { ont } from "./fixtures/engine-fake";
import { addE, addN, ent, fakeWorkspace, patch, rel, ver } from "./helpers";

afterEach(() => { cleanup(); runtime.ws = null; });
const KINDS = EDGE_KINDS;

// A small shop workload, with every kind the engine declares.
const shopKinds: Record<string, EdgeKind> = { Contains: "composition", HostedOn: "composition", MemberOf: "composition", BackedBy: "composition", ConnectedThrough: "composition", DependsOn: "dependency", Exposes: "dependency", Affects: "dependency", TalksTo: "communication", Measures: "support" };
const ends: Record<string, [string[], string[]]> = {
  Contains: [["Workload", "Service", "Deployment"], ["Service", "Deployment", "Pod"]], HostedOn: [["Pod"], ["Node"]], MemberOf: [["Node", "Pool"], ["Pool", "Cluster"]],
  BackedBy: [["Service"], ["Resource"]], ConnectedThrough: [["Resource"], ["Resource"]], DependsOn: [["Service"], ["Resource", "Service"]], Exposes: [["Resource"], ["Service"]],
  Affects: [["Service", "Pod", "Node"], ["Workload", "Service", "Pod"]], TalksTo: [["Service"], ["Service", "Resource"]], Measures: [["Signal"], ["Service"]],
};
const shopOntology = { ...ont("o"), relationshipTypes: Object.entries(ends).map(([id, [s, t]]) => ({ id: { value: id }, sourceTypes: s.map((value) => ({ value })), targetTypes: t.map((value) => ({ value })), properties: [] })) };
const shopRows = ["Workload", "Service", "Deployment", "Pod"];
const shopTree: TreeSpec = { hierarchy: { relationship: "Contains", childIsSource: false }, edgeRoles: {}, state: Object.fromEntries(shopRows.concat("Resource", "Node").map((t) => [t, { rolledUp: "state" }])), types: shopRows };
const shopInfo = { id: "w", role: "projection", descriptor: {}, ontology: shopOntology, inputs: [], tree: shopTree, edgeKinds: shopKinds, restating: ["Affects"], presentation: [{ property: "state", roles: { Healthy: "ok", Degraded: "warning", Unhealthy: "danger" } }] } as unknown as GraphInfo;
const shopTypes: Record<string, string> = { shop: "Workload", web: "Service", api: "Service", database: "Service", webd: "Deployment", pod: "Pod", node: "Node", pool: "Pool", cluster: "Cluster", cache: "Resource", pg: "Resource", pe: "Resource", vnet: "Resource", apim: "Resource", sig: "Signal" };
const shopEdges: [string, string, string][] = [
  ["Contains", "shop", "web"], ["Contains", "shop", "api"], ["Contains", "shop", "database"], ["Contains", "web", "webd"], ["Contains", "webd", "pod"],
  ["HostedOn", "pod", "node"], ["MemberOf", "node", "pool"], ["MemberOf", "pool", "cluster"],
  ["DependsOn", "web", "cache"], ["DependsOn", "web", "api"], ["DependsOn", "api", "database"], ["BackedBy", "database", "pg"], ["ConnectedThrough", "pg", "pe"], ["ConnectedThrough", "pe", "vnet"], ["Exposes", "apim", "api"],
  ["TalksTo", "web", "api"], ["Measures", "sig", "web"],
];
// the roll-up edges as the engine writes them: from the element whose state takes the other in (a workload its service, a service what it needs)
const affects: [string, string, string][] = [["Affects", "shop", "web"], ["Affects", "web", "api"], ["Affects", "pod", "node"]];
const workload = (states: Record<string, string> = {}, rollup = false) => Graph.fromSnapshot({
  version: ver("1"),
  entities: Object.entries(shopTypes).map(([id, t]) => ent(id, t, { name: id, ...(states[id] ? { state: states[id] } : {}) })),
  relationships: (rollup ? shopEdges.concat(affects) : shopEdges).map(([t, a, b], i) => rel(`${t}-${i}`, a, b, t)),
});

describe("an edge type restates others when the ontology marks it rollup", () => {
  it.each<[string[] | undefined, string[]]>([[["Affects"], ["Affects"]], [undefined, []]])("the types the ontology marks rollup (%j), not read from the edges", (restating, want) => {
    expect([...restatingTypes({ restating })]).toEqual(want);
  });
});

describe("a workload stacks its hierarchy and stands its dependencies beside it", () => {
  it("the hierarchy and the compositions toward what nothing depends on stack; dependencies and the compositions toward what is depended on stand beside", () => {
    const { vertical, horizontal } = kindAxes(shopInfo);
    expect([[...vertical].sort(), [...horizontal].sort()]).toEqual([["Contains", "HostedOn", "MemberOf"], ["Affects", "BackedBy", "ConnectedThrough", "DependsOn", "Exposes"]]);
  });
  it("keeps a second declared hierarchy vertical even when its targets also have dependencies", () => {
    const info = { ...shopInfo, tree: { ...shopTree, hierarchy: { relationship: "Contains", childIsSource: false, also: ["BackedBy"] } } };
    const axes = kindAxes(info);
    expect(axes.vertical.has("BackedBy")).toBe(true);
    expect(axes.horizontal.has("BackedBy")).toBe(false);
    expect(specForKind(info, "dependency")!.hierarchy.parentRows).toContain("Resource");
  });
});

describe("the elements beside the parts stand right of what needs them and left of what they expose", () => {
  const g = workload();
  // the parts as the layered layout placed them: services in one row, then deployment, pod, node, pool, cluster
  const at: Record<string, [number, number]> = { shop: [100, 0], web: [0, 100], api: [100, 100], database: [200, 100], webd: [0, 200], pod: [0, 300], node: [0, 400], pool: [0, 500], cluster: [0, 600] };
  const placed = (slot: number) => at[g.nodes[slot]!.id] ?? null;
  const moves = besidePositions(g, shopInfo, placed, () => true, { column: 100, row: 30 });
  const pos = (id: string) => moves.get(g.nodeSlot.get(id)!);
  it.each<[string, string, [number, number]]>([
    ["what a service needs is right of everything, in the service's row", "cache", [300, 100]],
    ["a database server backing a service, one column right", "pg", [300, 130]],
    ["its private endpoint one column further", "pe", [400, 100]],
    ["its network further still", "vnet", [500, 100]],
    ["a gateway that exposes a service is to the left", "apim", [-100, 100]],
  ])("%s", (_, id, xy) => expect(pos(id)).toEqual(xy));
  it("the parts and what only communicates or supports are not moved", () => {
    for (const id of ["shop", "web", "api", "database", "webd", "pod", "node", "sig"]) expect(pos(id), id).toBeUndefined();
  });
  it("keeps each parent's checks together and reserves space for checks from the following row", () => {
    const owners = ["web", "api", "webd"];
    const checks = Array.from({ length: 18 }, (_, i) => ({ id: `check-${i}`, owner: owners[i % owners.length] }));
    const g = Graph.fromSnapshot({
      version: ver("1"),
      entities: [ent("shop", "Workload"), ...owners.map((id) => ent(id, shopTypes[id])), ...checks.map(({ id }) => ent(id, "Resource"))],
      relationships: [
        rel("web-flow", "shop", "web", "Contains"), rel("api-flow", "shop", "api", "Contains"), rel("web-part", "web", "webd", "Contains"),
        ...checks.map(({ id, owner }) => rel(`needs-${id}`, owner, id, "DependsOn")),
      ],
    });
    const moves = besidePositions(g, shopInfo, (s) => at[g.nodes[s]!.id] ?? null, () => true, { column: 100, row: 30 });
    expect(moves.size).toBe(checks.length);
    const ys = owners.flatMap((owner) => checks.filter((c) => c.owner === owner).map(({ id }) => moves.get(g.nodeSlot.get(id)!)![1]));
    expect(ys[0]).toBe(at.web[1]);
    expect(ys.slice(1).every((y, i) => y - ys[i] >= 30)).toBe(true);
  });
});

describe("the part to blame is the wrong element the others are wrong through, and the way to it runs cause to effect", () => {
  it.each<[string, Record<string, string>, string[], string[]]>([
    ["the database server is down: everything above it through what needs it is on the way", { pg: "Unhealthy", database: "Unhealthy", api: "Unhealthy", web: "Unhealthy", shop: "Unhealthy", apim: "Unhealthy" }, ["pg"], ["api", "apim", "database", "pg", "shop", "web"]],
    ["one pod crash-loops and nothing above it is wrong", { pod: "Unhealthy" }, ["pod"], ["pod"]],
    ["a degraded cache under a degraded service", { cache: "Degraded", web: "Degraded", shop: "Degraded" }, ["cache"], ["cache", "shop", "web"]],
    ["nothing is wrong", {}, [], []],
  ])("%s", (_, states, blamed, path) => {
    const b = blamePath(workload(states), shopInfo);
    expect([[...b.blamed].sort(), [...b.nodes].sort()]).toEqual([blamed, path]);
  });
  it("the edges of the way are those between wrong elements, cause to effect", () => {
    const g = workload({ pg: "Unhealthy", database: "Unhealthy", api: "Unhealthy" });
    const b = blamePath(g, shopInfo);
    expect([...b.edges].map((id) => id.split("-")[0]).sort()).toEqual(["BackedBy", "DependsOn"]);
  });
  it("a graph that holds the roll-up edges is read along them alone: the web takes in no wrong element there, though it needs the database", () => {
    const g = workload({ web: "Unhealthy", shop: "Unhealthy", database: "Unhealthy" }, true);
    expect([...blamePath(g, shopInfo).blamed].sort()).toEqual(["database", "web"]);
  });
  it("a graph whose hierarchy declares no states blames nothing", () => {
    expect(blamePath(workload({ pg: "Unhealthy" }), { ...shopInfo, tree: { ...shopTree, state: {} } }).blamed.size).toBe(0);
  });
});

describe("the roll-up follows the roll-up edges where the graph holds them, and the hierarchy, compositions and dependencies otherwise", () => {
  const pairs = (g: Graph) => causeLinks(g, shopInfo).links.map((l) => `${l.parent}>${l.child}`).sort();
  it.each<[string, boolean, string[]]>([
    ["a graph that holds the roll-up edges: those alone", true, ["pod>node", "shop>web", "web>api"]],
    ["one that does not: the hierarchy, every composition and dependency, from the source", false, ["api>database", "apim>api", "database>pg", "node>pool", "pe>vnet", "pg>pe", "pod>node", "pool>cluster", "shop>api", "shop>database", "shop>web", "web>api", "web>cache", "web>webd", "webd>pod"]],
  ])("%s", (_, rollup, want) => expect(pairs(workload({}, rollup))).toEqual(want));
});

describe("the tree by dependency nests what each part needs under it", () => {
  it("nests what each part needs under it, what that is made of under that, and a gateway under what it exposes", () => {
    const m = buildTree(workload(), specForKind(shopInfo, "dependency")!, []);
    const parent = (id: string) => m.nodes.get(id)?.parent ?? null;
    expect(Object.fromEntries(["web", "api", "cache", "database", "apim", "pg", "pe", "vnet"].map((id) => [id, parent(id)]))).toEqual({ web: null, api: "web", cache: "web", database: "api", apim: "api", pg: "database", pe: "pg", vnet: "pe" });
  });
});

describe("the scene keeps the kind of each edge apart from its state", () => {
  it("an edge of each kind keeps its kind through a state change", () => {
    for (const kind of KINDS) {
      const s = new Scene();
      s.setEdge(0, 0, 1, 0xffffffff, EdgeStyle.Solid, 0, 0, EdgeKindCode[kind]);
      expect(s.edgeKind(0), kind).toBe(EdgeKindCode[kind]);
      s.setEdgeState(0, 5);
      expect([s.edgeKind(0), s.edgeState(0), s.edgeStyle(0)], kind).toEqual([EdgeKindCode[kind], 5, EdgeStyle.Solid]);
    }
  });
  it("the codes are different, and none is the code of an edge without a kind", () => { const codes = KINDS.map((k) => EdgeKindCode[k]); expect([new Set(codes).size, (codes as number[]).includes(0)]).toEqual([KINDS.length, false]); });
  it("an edge without a kind has code 0", () => { const s = new Scene(); s.setEdge(0, 0, 1, 1, EdgeStyle.Solid); expect(s.edgeKind(0)).toBe(0); });
});

/** One type of relationship per kind, one without; a hub with an edge of each, so every edge is between nodes the graph holds. */
const hubTypes: Record<string, EdgeKind | undefined> = { Is: "composition", Needs: "dependency", Calls: "communication", Explains: "support", Plain: undefined };
const declared = Object.fromEntries(Object.entries(hubTypes).filter(([, k]) => k)) as Record<string, EdgeKind>;
const info = (edgeKinds: Record<string, EdgeKind> | undefined): GraphInfo => ({ id: "g", role: "projection", descriptor: {}, ontology: ont("o"), inputs: [], edgeKinds } as unknown as GraphInfo);
const world = (relTypes = Object.keys(hubTypes)) => new History({ version: ver("1"), entities: [ent("hub"), ...relTypes.map((t) => ent(`n-${t}`))], relationships: relTypes.map((t) => rel(`r-${t}`, "hub", `n-${t}`, t)) });
const events = { select() {}, hover() {}, activate() {}, changed() {}, announce() {}, key: () => false };
const scene = (p: DataPane) => (p as unknown as { scene: Scene }).scene;
const paneOf = (i: GraphInfo, h: History, tile = false) => { const p = new DataPane(i, h, document.createElement("div"), darkTheme, events as never); p.setView({ tile }); p.refreshAll(); return p; };
const drawn = (p: DataPane, h: History) => Object.fromEntries(Object.keys(hubTypes).map((t) => { const slot = h.graph.edgeSlot.get(`r-${t}`)!; return [t, scene(p).edgeVisible(slot) ? scene(p).edgeKind(slot) : null]; }));

describe("a pane draws each edge with the kind its type declares, and leaves out the kinds the person hides", () => {
  it("every kind has its own code and an edge without a kind has none", () => {
    const h = world(); const p = paneOf(info(declared), h);
    p.setView({ hiddenKinds: [] });                                              // every kind drawn
    expect(drawn(p, h)).toEqual({ Is: EdgeKindCode.composition, Needs: EdgeKindCode.dependency, Calls: EdgeKindCode.communication, Explains: EdgeKindCode.support, Plain: 0 });
  });
  // the pane hides by kind the same way for every kind; support is a row of its own, since an element that only supports goes with it
  it.each<EdgeKind>(["dependency", "support"])("leaving %s out hides its edges and only those", (kind) => {
    const h = world(); const p = paneOf(info(declared), h);
    p.setView({ hiddenKinds: [kind] });
    const shown = drawn(p, h);
    for (const [t, k] of Object.entries(hubTypes)) expect(shown[t] !== null, `${t} (${k ?? "no kind"})`).toBe(k !== kind);
  });
  it("a graph that declares no kind draws every edge, and has no kinds to leave out", () => {
    const h = world(); const p = paneOf(info(undefined), h);
    expect(Object.values(drawn(p, h))).toEqual([0, 0, 0, 0, 0]);
    expect(p.presentKinds().size).toBe(0);
  });
});

describe("a tile leaves out what does not make the graph readable at its size", () => {
  it.each<[string, Set<EdgeKind>, boolean, EdgeKind[]]>([
    ["in the view only support is left out (it is counted on what it supports)", new Set<EdgeKind>(["composition", "communication", "support"]), false, ["support"]],
    ["a view of a graph that is all support draws it", new Set<EdgeKind>(["support"]), false, []],
    ["a tile with composition leaves out communication and support", new Set<EdgeKind>(["composition", "communication", "support"]), true, ["communication", "support"]],
    ["a tile with dependency does too", new Set<EdgeKind>(["dependency", "communication"]), true, ["communication", "support"]],
    ["a tile of a graph that is all communication draws it, without support", new Set<EdgeKind>(["communication", "support"]), true, ["support"]],
    ["a tile of a graph with no kinds draws what it has", new Set<EdgeKind>(), true, []],
  ])("%s", (_, present, tile, hidden) => expect(defaultHiddenKinds(tile, present)).toEqual(hidden));

  it("the pane of a tile draws composition and dependency, the person's choice overrides, and opening the graph draws all", () => {
    const h = world(); const p = paneOf(info(declared), h, true);
    expect(Object.fromEntries(Object.entries(drawn(p, h)).map(([t, k]) => [t, k !== null]))).toEqual({ Is: true, Needs: true, Calls: false, Explains: false, Plain: true });
    p.setView({ hiddenKinds: [] });                                              // the person turned them all on
    expect(Object.values(drawn(p, h)).every((k) => k !== null)).toBe(true);
    p.setView({ hiddenKinds: null }); p.setView({ tile: false });                          // opened: the default of the view leaves out only support
    expect(Object.fromEntries(Object.entries(drawn(p, h)).map(([t, k]) => [t, k !== null]))).toEqual({ Is: true, Needs: true, Calls: true, Explains: false, Plain: true });
  });
});

// A step that brings a kind decides the hidden kinds again: the pane model after the step differs from the last one.
describe("a kind that a step brings changes what a tile leaves out from that step on", () => {
  it("a tile that gains a dependency leaves communication out from that step on", () => {
    const h = world(["Calls"]);
    const p = paneOf(info(declared), h, true);
    const calls = () => scene(p).edgeVisible(p.graph.edgeSlot.get("r-Calls")!);
    expect(calls(), "a tile with only communication draws it").toBe(true);
    const step = h.append(patch("1", "2", [addN(ent("n-Needs")), addE(rel("r-Needs", "hub", "n-Needs", "Needs"))]))!;
    p.applyStep(step);
    expect([[...p.hiddenKinds()].sort(), calls()], "the kinds hidden before the dependency stay hidden after it").toEqual([["communication", "support"], false]);
  });
});

describe("an element that only supports others is counted on what it supports until support is turned on", () => {
  // hub supports nothing; s1 and s2 only explain hub (support); c is a part of hub (composition)
  const world2 = () => new History({ version: ver("1"), entities: [ent("hub"), ent("c"), ent("s1", "Sig"), ent("s2", "Sig")], relationships: [rel("p", "hub", "c", "Is"), rel("e1", "s1", "hub", "Explains"), rel("e2", "s2", "hub", "Explains")] });
  const nodeShown = (p: DataPane, h: History, id: string) => scene(p).nodeVisible(h.graph.nodeSlot.get(id)!);
  it("is left out with its edges by default, and what it supports counts it", () => {
    const h = world2(); const p = paneOf(info(declared), h);
    expect(["hub", "c", "s1", "s2"].map((id) => nodeShown(p, h, id))).toEqual([true, true, false, false]);
    expect(p.labelsShown()).toContain("hub (+2)");
  });
  it("comes back as a node when support is turned on", () => {
    const h = world2(); const p = paneOf(info(declared), h);
    p.setView({ hiddenKinds: [] });
    expect(["s1", "s2"].map((id) => nodeShown(p, h, id))).toEqual([true, true]);
    expect(p.labelsShown()).toContain("hub");
  });
});

describe("a tree nests by the kind chosen, and the other edges are links", () => {
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

const wrap = (ui: React.ReactElement) => render(<FluentProvider theme={webLightTheme}>{ui}</FluentProvider>);
const fakeWs = (graph: Graph, kinds: Record<string, EdgeKind>, tree?: TreeSpec) => ({
  graphInfo: () => ({ id: "g", role: "projection", descriptor: {}, ontology: ont("o"), inputs: [], edgeKinds: kinds, tree }),
  ...fakeWorkspace({ g: { graph, length: 0 } }),
}) as never;
const hubGraph = () => Graph.fromSnapshot({ version: ver("1"), entities: [ent("a"), ent("b")], relationships: Object.keys(declared).map((t) => rel(`r-${t}`, "a", "b", t)) });

describe("each kind has a sample that tells it apart without color, a name and a meaning", () => {
  const svg = (k: EdgeKind) => render(<KindSample kind={k} />).container.innerHTML;
  it("no two kinds look the same, and the difference is not a color", () => {
    // the color repeats the kind; without it the pattern and the head still differ
    const looks = KINDS.map((k) => svg(k).replace(/ style="[^"]*"/, ""));
    expect(new Set(looks).size).toBe(KINDS.length);
    for (const l of looks) expect(l).not.toMatch(/#[0-9a-f]{3,6}|rgb\(/i);
  });
  it("every kind has a name and says what it means", () => {
    for (const k of KINDS) expect([KIND_WORD[k].word.length > 3, KIND_WORD[k].help.length > 20], k).toEqual([true, true]);
  });
  it("lists the kinds a graph declares with their types, in a fixed order", () => {
    expect(kindsOf({ B: "support", A: "composition", C: "composition" })).toEqual([{ kind: "composition", types: ["A", "C"] }, { kind: "support", types: ["B"] }]);
    expect(kindsOf(undefined)).toEqual([]);
  });
});

describe("the legend names the kinds the graph declares", () => {
  it("names each kind the graph declares with its relationship types, and shows a sample", async () => {
    runtime.ws = fakeWs(hubGraph(), declared);
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

describe("the filters leave out exactly the kind the person unticks", () => {
  // every kind's box is made by the same code; one kind proves the round trip through the store
  it("unticking composition leaves exactly it out, ticking it again brings it back", async () => {
    runtime.ws = fakeWs(hubGraph(), declared);
    dispatch({ type: "data/summary", graphs: [], windows: {} });
    wrap(<FiltersPanel graph="g" />);
    await userEvent.setup().click(await screen.findByRole("button", { name: "Edge kinds" }));
    const box = await screen.findByRole("checkbox", { name: new RegExp(`^${KIND_WORD.composition.word}`) });
    expect((box as HTMLInputElement).checked).toBe(true);
    await userEvent.setup().click(box);
    expect(useStore.getState().filters.g?.hiddenKinds).toEqual(["composition"]);
    await userEvent.setup().click(box);
    expect(useStore.getState().filters.g?.hiddenKinds).toEqual([]);
  });
  it("the section is not there for a graph without kinds", async () => {
    runtime.ws = fakeWs(hubGraph(), {});
    wrap(<FiltersPanel graph="g" />);
    await screen.findByRole("button", { name: "Types" });
    expect(screen.queryByRole("button", { name: "Edge kinds" })).toBeNull();
  });
});

describe("the tree lets the person choose which kind is the hierarchy", () => {
  const kinds: Record<string, EdgeKind> = { Contains: "composition", Needs: "dependency" };
  const tree: TreeSpec = { hierarchy: { relationship: "Contains", childIsSource: false }, edgeRoles: { Needs: "cross" }, state: {}, types: ["Row"] };
  const g = Graph.fromSnapshot({
    version: ver("1"),
    entities: ["a", "b", "c"].map((id) => ent(id, "Row", { name: `row ${id}` })),
    relationships: [rel("c1", "a", "b", "Contains"), rel("n1", "c", "a", "Needs")],
  });
  const ws = () => ({
    graphInfo: () => ({ id: "g", role: "projection", descriptor: {}, ontology: { ...ont("o"), relationshipTypes: ["Contains", "Needs"].map((t) => ({ id: { value: t }, sourceTypes: [{ value: "Row" }], targetTypes: [{ value: "Row" }], properties: [] })) }, inputs: [], edgeKinds: kinds, tree }),
    ...fakeWorkspace({ g: { graph: g, length: 0, cursor: 0, patches: [] } }), select() {},
  }) as never;
  const rows = () => screen.getAllByRole("treeitem").map((r) => r.textContent ?? "");
  it("opens on composition and offers the other kinds; choosing dependency nests by it", async () => {
    runtime.ws = ws();
    dispatch({ type: "data/summary", graphs: [{ id: "g", role: "projection", head: "1", patches: 0, nodes: 3, edges: 2, version: 0 }], windows: {} });
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
    dispatch({ type: "data/summary", graphs: [{ id: "g", role: "projection", head: "1", patches: 0, nodes: 3, edges: 2, version: 0 }], windows: {} });
    wrap(<TreeView graph="g" />);
    await screen.findByRole("tree");
    expect(screen.queryByRole("combobox", { name: "Hierarchy by" })).toBeNull();
  });
});
