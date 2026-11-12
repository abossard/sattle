import { describe, expect, it } from "vitest";
import { attentionPaths, buildTree, expandBelow, explainNode, hasTreeRows, isWrong, searchMatches, typeAhead, visibleRows, wrongNodes, type TreeView } from "../src/app/tree-model";
import { curated, curatedSpec, treeGraph, treeHints, treeSpec } from "./fixtures/tree-graph";

const model = () => buildTree(treeGraph(), treeSpec, treeHints);
const view = (over: Partial<TreeView> = {}): TreeView => ({ expanded: new Set(), onlyWrong: false, query: "", sort: "severity", ...over });
const ids = (v: TreeView) => visibleRows(model(), v).rows.map((r) => `${r.level}:${r.node.id}`);

describe("the tree of a hierarchical graph", () => {
  it("has the nodes of the tree types only, one root, children from the hierarchy relationship", () => {
    const m = model();
    expect([m.roots, [...m.nodes.keys()].sort(), m.nodes.get("ra")!.children, m.nodes.get("api")!.parent]).toEqual([["estate"], ["api", "cache", "db", "ra", "rb", "rc", "estate", "web"].sort(), ["api", "db"], "ra"]);
  });
  it.each([
    ["the direction of the hierarchy relationship decides who is the parent", { ...treeSpec, hierarchy: { relationship: "Contains", childIsSource: true } }, ["api"]],
  ])("%s", (_, spec, roots) => {
    expect(buildTree(treeGraph(), spec, treeHints).roots.sort()).not.toEqual(roots);
    expect(buildTree(treeGraph(), spec, treeHints).nodes.get("estate")!.parent).toBe("ra");
  });
  it("says own and rolled-up state with their roles, and counts the children by rolled-up state, worst first", () => {
    const m = model();
    expect([m.nodes.get("api")!.own, m.nodes.get("api")!.rolled]).toEqual([{ value: "Unhealthy", role: "danger" }, { value: "Unhealthy", role: "danger" }]);
    expect(m.nodes.get("estate")!.counts.map((c) => `${c.value}:${c.n}`)).toEqual(["Unhealthy:1", "Degraded:1", "Unknown:1"]);
    expect(m.nodes.get("ra")!.counts.map((c) => `${c.value}:${c.n}`)).toEqual(["Unhealthy:1", "Healthy:1"]);
  });
  it.each([
    ["api has two pieces of evidence and one dependency on it", "api", 2, 1],
    ["web depends on two nodes", "web", 0, 2],
    ["db is depended on once", "db", 0, 1],
    ["estate has neither", "estate", 0, 0],
  ])("%s", (_, id, supporting, cross) => expect([model().nodes.get(id)!.supporting, model().nodes.get(id)!.cross]).toEqual([supporting, cross]));
  it("orders states by the severity property, and by the role of the value when the type has none", () => {
    const noSeverity = { ...treeSpec, state: { Component: { own: "own", rolledUp: "state" } } };
    const m = buildTree(treeGraph(), noSeverity, treeHints);
    expect([m.nodes.get("api")!.severity, m.nodes.get("web")!.severity, m.nodes.get("db")!.severity, m.nodes.get("rc")!.severity]).toEqual([5, 4, 2, 3]);
  });
  it("a cycle in the hierarchy is cut and never followed", () => {
    const g = treeGraph();
    g.putRel({ id: "cx", source: "api", target: "estate", type: "Contains", props: {} });
    const m = buildTree(g, treeSpec, treeHints);
    expect(m.nodes.get("estate")!.parent).toBeNull();
    expect(visibleRows(m, view({ expanded: new Set(m.nodes.keys()) })).rows.length).toBe(m.nodes.size);
  });
});

describe("what the tree shows", () => {
  it.each([
    ["collapsed: only the roots", view(), ["1:estate"]],
    ["expanded, worst first", view({ expanded: new Set(["estate"]) }), ["1:estate", "2:ra", "2:rb", "2:rc"]],
    ["sorted by name", view({ expanded: new Set(["estate"]), sort: "label" }), ["1:estate", "2:ra", "2:rb", "2:rc"]],
    ["two levels", view({ expanded: new Set(["estate", "rb"]) }), ["1:estate", "2:ra", "2:rb", "3:web", "3:cache", "2:rc"]],
    ["rows of a node whose children are all filtered out are not expandable rows", view({ expanded: new Set(["estate", "ra"]), onlyWrong: true }), ["1:estate", "2:ra", "3:api", "2:rb"]],
  ])("%s", (_, v, want) => expect(ids(v)).toEqual(want));

  it("opens by default the paths to the nodes where something is wrong, and nothing else", () => {
    const m = model();
    expect([wrongNodes(m).sort(), [...attentionPaths(m)].sort()]).toEqual([["api", "estate", "ra", "rb", "web"], ["estate", "ra", "rb"]]);
    expect(visibleRows(m, view({ expanded: attentionPaths(m) })).rows.map((r) => r.node.id)).toEqual(["estate", "ra", "api", "db", "rb", "web", "cache", "rc"]);
  });
  it("only where something is wrong keeps the path to a wrong node and drops healthy and unknown branches", () => {
    expect(visibleRows(model(), view({ onlyWrong: true, expanded: attentionPaths(model()) })).rows.map((r) => r.node.id)).toEqual(["estate", "ra", "api", "rb", "web"]);
  });
  it("a search reveals the path to a match and shows only paths to matches, without expanding anything by hand", () => {
    const r = visibleRows(model(), view({ query: "cache" }));
    expect(r.rows.map((x) => `${x.level}:${x.node.id}:${x.match}`)).toEqual(["1:estate:false", "2:rb:false", "3:cache:true"]);
  });
  it.each([
    ["a match that is wrong keeps its path", "web", ["estate", "rb", "web"]],
    ["a match that is healthy is not shown at all", "db", []],
  ])("a search and the filter both apply: %s", (_, query, want) => expect(visibleRows(model(), view({ query, onlyWrong: true })).rows.map((x) => x.node.id)).toEqual(want));
  it("positions among the visible siblings are for the screen reader", () => {
    const rows = visibleRows(model(), view({ expanded: new Set(["estate"]) })).rows;
    expect(rows.map((r) => `${r.node.id} ${r.posInSet}/${r.setSize}`)).toEqual(["estate 1/1", "ra 1/3", "rb 2/3", "rc 3/3"]);
  });
  it("expanding below a node opens every branch under it", () => {
    expect([...expandBelow(model(), "estate")].sort()).toEqual(["estate", "ra", "rb"]);
    expect([...expandBelow(model(), "api")]).toEqual([]);
  });
  it.each([["cache", ["cache"]], ["APi", ["api"]], ["component", ["estate", "ra", "rb", "rc", "api", "db", "web", "cache"]], ["", []], ["nothing", []]])("a search for %j matches %j", (q, want) => expect(searchMatches(model(), q).sort()).toEqual([...want].sort()));
  it.each([["a", 0, 2], ["d", 1, 3], ["w", 0, 5], ["zz", 0, -1], ["", 0, -1]])("type-ahead %j from row %i goes to row %i", (typed, from, want) => {
    const rows = visibleRows(model(), view({ expanded: attentionPaths(model()) })).rows;
    expect(typeAhead(rows, from, typed)).toBe(want);
  });
  it("only a warning or a danger is wrong", () => {
    const m = model();
    expect(["api", "web", "rb"].every((i) => isWrong(m.nodes.get(i)!)) && ["db", "rc", "cache"].every((i) => !isWrong(m.nodes.get(i)!))).toBe(true);
  });
});

describe("why a node has its state", () => {
  const why = (id: string) => explainNode(treeGraph(), model(), id)!;
  it.each([
    ["a region rolls up from the worst of its children", "ra", ["api"]],
    ["the estate rolls up from the region that is worst", "estate", ["ra"]],
    ["a node whose own state is the worst has no driving child", "api", []],
    ["a leaf has none", "db", []],
    ["a region that is only as bad as its worst child names that child", "rb", ["web"]],
  ])("%s", (_, id, drivers) => expect(why(id).drivers.map((d) => d.id)).toEqual(drivers));
  it("lists the supporting elements with their properties and the links in both directions", () => {
    expect(why("api").support.map((x) => [x.label, x.relationship, x.props.find(([k]) => k === "name")?.[1]])).toEqual([["restart", "EvidenceFor", "restart"], ["oom", "EvidenceFor", "oom"]]);
    expect(why("api").cross.map((x) => `${x.direction} ${x.relationship} ${x.label}`)).toEqual(["in DependsOn web"]);
    expect(why("web").cross.map((x) => `${x.direction} ${x.label}`).sort()).toEqual(["out api", "out db"]);
  });
  it("a node that is not in the tree has no explanation", () => expect(explainNode(treeGraph(), model(), "e1")).toBeNull());
});

describe("a tree whose ontology names the kind and the counts of descendants", () => {
  const spec = { ...treeSpec, state: { Component: { own: "own", rolledUp: "state", severity: "severity", kind: "kind", counts: { Healthy: "healthy", Degraded: "degraded", Unhealthy: "unhealthy" } } } };
  const g = () => {
    const x = treeGraph();
    const put = (id: string, props: Record<string, string>) => { const e = x.entity(id)!; x.putEntity({ ...e, props: { ...e.props, ...Object.fromEntries(Object.entries(props).map(([k, v]) => [k, /^\d+$/.test(v) && k !== "kind" ? { kind: "integer" as const, value: v } : { kind: "text" as const, value: v }])) } }); };
    put("estate", { kind: "subscription", healthy: "5", degraded: "1", unhealthy: "2" });
    put("api", { kind: "workload" });
    return x;
  };
  it("the row says what the element is by its kind, not by the entity type everything shares", () => {
    const m = buildTree(g(), spec, treeHints);
    expect([m.nodes.get("estate")!.type, m.nodes.get("api")!.type, m.nodes.get("db")!.type]).toEqual(["subscription", "workload", "Component"]);
  });
  it("the counts are the engine's descendants by state with the roles of the rolled-up property, worst first, zeros left out", () => {
    const m = buildTree(g(), spec, treeHints);
    expect(m.nodes.get("estate")!.counts).toEqual([{ value: "Unhealthy", role: "danger", n: 2 }, { value: "Degraded", role: "warning", n: 1 }, { value: "Healthy", role: "ok", n: 5 }]);
    expect(m.nodes.get("ra")!.counts).toEqual([]);
  });
});

describe("what authored curation does to the tree", () => {
  const m = () => buildTree(curated(treeGraph()), curatedSpec, treeHints);
  it("says which rows are curated by which record, critical, hidden, and how they roll up", () => {
    const x = m();
    expect([x.nodes.get("api")!.curatedBy, x.nodes.get("api")!.policy, x.nodes.get("db")!.critical, x.nodes.get("cache")!.hidden, x.nodes.get("web")!.curatedBy, x.nodes.get("web")!.policy]).toEqual(["cur-7", { mode: "share", share: "0.5" }, true, true, null, null]);
  });
  it.each([
    ["a hidden element is left out", false, ["estate", "ra", "api", "db", "rb", "web", "rc"]],
    ["unless the reader asks for it", true, ["estate", "ra", "api", "db", "rb", "web", "cache", "rc"]],
  ])("%s", (_, showHidden, want) => expect(visibleRows(m(), view({ expanded: attentionPaths(m()), showHidden })).rows.map((r) => r.node.id)).toEqual(want));
  it("an element hidden with children takes them with it", () => {
    const g = curated(treeGraph());
    const rb = g.entity("rb")!;
    g.putEntity({ ...rb, props: { ...rb.props, hidden: { kind: "boolean", value: true } } });
    const x = buildTree(g, curatedSpec, treeHints);
    expect(visibleRows(x, view({ expanded: new Set(x.nodes.keys()) })).rows.map((r) => r.node.id)).toEqual(["estate", "ra", "api", "db", "rc"]);
  });
});

describe("the label of a row", () => {
  it.each([
    ["the usual name", { name: "api" }, "api"],
    ["a property that ends in name", { nodeName: "rg-synth" }, "rg-synth"],
    ["a qualified property that ends in name", { "acme.tree.nodeName": "pod-1" }, "pod-1"],
    ["the id when nothing names it", { kind: "x" }, "e1"],
  ])("%s", (_, props, want) => {
    const g = treeGraph();
    g.putEntity({ id: "e1", type: "Component", props: Object.fromEntries(Object.entries(props).map(([k, v]) => [k, { kind: "text" as const, value: v }])) });
    expect(buildTree(g, treeSpec, treeHints).nodes.get("e1")!.label).toBe(want);
  });
});

describe("whether a graph has anything to show as a tree", () => {
  it.each([
    ["a graph with entities of the tree types", treeSpec, true],
    ["a graph that shares the ontology but holds only other types", { ...treeSpec, types: ["Signal"] }, false],
    ["no types listed: any entity is a row", { ...treeSpec, types: undefined }, true],
  ])("%s", (_, spec, want) => expect(hasTreeRows(treeGraph(), spec)).toBe(want));
});

describe("a tree by cause", () => {
  // the hierarchy and the dependencies: web needs api and db, so both are under their region and under web
  const links = (extra: [string, string][] = []) => {
    const g = treeGraph();
    const ls = [...g.relationships()].filter((r) => r.type === "Contains" || r.type === "DependsOn").map((r) => ({ parent: r.source, child: r.target, edge: r.id }));
    return { types: new Set(["Contains", "DependsOn"]), links: ls.concat(extra.map(([parent, child], i) => ({ parent, child, edge: `x${i}` }))) };
  };
  const all = (m: ReturnType<typeof model>) => new Set(m.nodes.keys());
  it("puts an element under every element whose state takes it in, each row in its own place", () => {
    const m = buildTree(treeGraph(), treeSpec, treeHints, links());
    const rows = visibleRows(m, view({ expanded: all(m) })).rows;
    expect([rows.filter((r) => r.node.id === "api").length, m.nodes.get("api")!.parents, new Set(rows.map((r) => r.key)).size]).toEqual([2, ["ra", "web"], rows.length]);
  });
  it.each<[string, [string, string][], string[]]>([
    ["the origins are where the own state is as bad as the rolled-up one, and the paths to them open", [], ["api", "web"]],
    ["a cycle is walked once per path and leaves no element without a row", [["api", "web"]], ["api", "web"]],
  ])("%s", (_, extra, origins) => {
    const m = buildTree(treeGraph(), treeSpec, treeHints, links(extra));
    expect([...m.nodes.values()].filter((n) => n.origin).map((n) => n.id).sort()).toEqual(origins);
    expect([...attentionPaths(m)].sort()).toEqual(extra.length ? ["api", "estate", "ra", "rb", "web"] : ["estate", "ra", "rb", "web"]);
    expect(new Set(visibleRows(m, view({ expanded: expandBelow(m, "estate") })).rows.map((r) => r.node.id))).toEqual(all(m));
  });
});
