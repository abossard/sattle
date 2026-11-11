import { describe, expect, it } from "vitest";
import { Graph } from "../src/core/graph";
import { FlagLayer, SUPPORT_GROUPING, computeAnchors, detectGrouping, edgelessNotice, resolveGrouping, supportRelation, entityMatchesProp, entityMatchesSearch, hiddenCounts, neighbourhood } from "../src/app/graph-view-model";
import type { OntologyDefinition } from "../src/core/wire-types";
import { History } from "../src/app/history";
import { addE, addN, delN, ent, patch, rel, setN, ver } from "./helpers";

// ns <- wl1,wl2 <- pods ; MemberOf points child -> parent
const g = () => Graph.fromSnapshot({
  version: ver("1"),
  entities: [ent("ns", "Namespace", { name: "team" }), ent("w1", "Workload"), ent("w2", "Workload"), ent("p1", "Pod", { health: "ok" }), ent("p2", "Pod"), ent("p3", "Pod"), ent("lone", "Service")],
  relationships: [rel("a", "w1", "ns", "MemberOf"), rel("b", "w2", "ns", "MemberOf"), rel("c", "p1", "w1", "MemberOf"), rel("d", "p2", "w1", "MemberOf"), rel("e", "p3", "w2", "MemberOf"), rel("f", "w1", "w2", "Calls")],
});

describe("grouping and collapse", () => {
  it("detects the parent side of a relationship type", () => {
    expect(detectGrouping(g(), "MemberOf").childIsSource).toBe(true);
  });
  it.each([
    ["nothing collapsed", [], { p1: "p1", w1: "w1", ns: "ns" }],
    ["workload collapsed", ["w1"], { p1: "w1", p2: "w1", w1: "w1", p3: "p3", ns: "ns" }],
    ["namespace collapsed hides everything below it", ["ns"], { p1: "ns", w2: "ns", p3: "ns", ns: "ns", lone: "lone" }],
    ["nested collapse uses the topmost", ["w1", "ns"], { p1: "ns", w1: "ns" }],
  ])("anchors: %s", (_n, collapsedIds, expected) => {
    const graph = g();
    const collapsed = new Set(collapsedIds.map((i) => graph.nodeSlot.get(i)!));
    const anchor = computeAnchors(graph, detectGrouping(graph, "MemberOf"), collapsed);
    for (const [id, anchorId] of Object.entries(expected)) expect(anchor[graph.nodeSlot.get(id)!]).toBe(graph.nodeSlot.get(anchorId));
  });
  it("counts hidden descendants", () => {
    const graph = g();
    const w1 = graph.nodeSlot.get("w1")!;
    const collapsed = new Set([w1]);
    const counts = hiddenCounts(computeAnchors(graph, detectGrouping(graph, "MemberOf"), collapsed), collapsed);
    expect(counts.get(w1)).toBe(2);
  });
});

describe("filters and focus", () => {
  it.each([
    ["equals", { key: "health", op: "equals" as const, value: "ok" }, true],
    ["equals miss", { key: "health", op: "equals" as const, value: "bad" }, false],
    ["contains", { key: "health", op: "contains" as const, value: "O" }, true],
    ["not equals on missing property", { key: "zzz", op: "not equals" as const, value: "x" }, true],
  ])("property filter %s", (_n, f, expected) => expect(entityMatchesProp(ent("p1", "Pod", { health: "ok" }), f)).toBe(expected));
  it.each([["p1", true], ["Pod", true], ["OK", true], ["zzz", false], ["", false]])("search %j", (q, expected) => {
    expect(entityMatchesSearch(ent("p1", "Pod", { health: "ok" }), q)).toBe(expected);
  });
  it.each([[0, 1], [1, 5], [2, 6]])("neighbourhood depth %i of w1 has %i nodes", (depth, size) => {
    const graph = g();
    expect(neighbourhood(graph, graph.nodeSlot.get("w1")!, depth).size).toBe(size);
  });
});

describe("FlagLayer", () => {
  it("reports only changed slots and keeps layers independent", () => {
    const f = new FlagLayer(4);
    expect(f.set(1, [0, 1]).sort()).toEqual([0, 1]);
    expect(f.set(2, [1]).sort()).toEqual([1]);
    expect(f.set(1, [1, 2]).sort()).toEqual([0, 2]);
    expect(Array.from(f.bits.subarray(0, 3))).toEqual([0, 3, 1]);
  });
});

describe("History", () => {
  const base = () => new History({ version: ver("1"), entities: [ent("a")], relationships: [] });
  const p1 = patch("1", "2", [addN(ent("b"))]);
  const p2 = patch("2", "3", [addN(ent("c")), addE(rel("r", "b", "c"))]);
  const p3 = patch("3", "4", [setN(ent("a"), ent("a", "Service", { x: "1" })), delN(ent("c"))]);
  it("applies at head and scrubs both directions", () => {
    const h = base();
    expect(h.append(p1)).not.toBeNull();
    h.append(p2); h.append(p3);
    expect(h.cursor).toBe(3);
    const steps = h.seek(0);
    expect(steps.map((s) => s.direction)).toEqual([-1, -1, -1]);
    expect(h.graph.nodeCount).toBe(1);
    h.seek(2);
    expect([...h.graph.entities()].map((e) => e.id).sort()).toEqual(["a", "b", "c"]);
    expect(h.revisionAt(2)).toBe("3");
  });
  it.each([
    ["gap in revisions", patch("5", "6", [addN(ent("z"))])],
    ["failed precondition", patch("1", "2", [delN(ent("nope"))])],
  ])("records a problem and drops: %s", (_n, p) => {
    const h = base();
    expect(h.append(p)).toBeNull();
    expect(h.length).toBe(0);
    expect(h.problems.length).toBe(1);
  });
  it("keeps patches received while scrubbed back and applies them on seek", () => {
    const h = base();
    h.append(p1);
    h.seek(0);
    h.append(p2);
    expect(h.length).toBe(2);
    expect(h.cursor).toBe(0);
    h.seek(2);
    expect(h.graph.nodeCount).toBe(3);
  });
  it("changesBetween composes the net effect", () => {
    const h = base();
    h.append(p1); h.append(p2); h.append(p3);
    const c = h.changesBetween(0, 3);
    expect(Object.fromEntries([...c.entities].map(([k, v]) => [k, v.kind]))).toEqual({ a: "changed", b: "added" });
  });
});

describe("the notice of a projection without edges", () => {
  it.each([
    ["a projection with nodes, no edges and relationship types says so and names them", "projection", 6, 0, ["Calls", "Owns"], /has 6 nodes and no edges.*\(Calls, Owns\)/],
    ["one node reads in the singular", "projection", 1, 0, ["Calls"], /has 1 node and no edges/],
    ["more than three types are counted", "projection", 2, 0, ["A", "B", "C", "D", "E"], /\(A, B, C and 2 more\)/],
    ["a graph with edges has no notice", "projection", 6, 3, ["Calls"], null],
    ["an empty graph has no notice", "projection", 0, 0, ["Calls"], null],
    ["an ontology without relationship types has no notice", "projection", 6, 0, [], null],
    ["the engine's NoType placeholder is not a declared type", "projection", 7, 0, ["NoType"], null],
    ["the placeholder is not named next to real types", "projection", 7, 0, ["NoType", "Measures"], /\(Measures\)/],
    ["the master is not a projection", "master", 6, 0, ["Calls"], null],
  ] as const)("%s", (_, role, nodes, edges, types, want) => {
    const n = edgelessNotice(role, nodes, edges, [...types]);
    if (want) expect(n).toMatch(want); else expect(n).toBeNull();
  });
});

describe("supporting elements, read from the ontology and its value roles", () => {
  const ids = (...v: string[]) => v.map((value) => ({ value }));
  const ont = (rels: [string, string[], string[]][]) => ({
    entityTypes: [
      { id: { value: "Component" }, properties: ids("state", "name") }, { id: { value: "Signal" }, properties: ids("degraded") }, { id: { value: "Evidence" }, properties: ids("summary") },
    ],
    relationshipTypes: rels.map(([id, sourceTypes, targetTypes]) => ({ id: { value: id }, sourceTypes: ids(...sourceTypes), targetTypes: ids(...targetTypes), properties: [] })),
  }) as unknown as OntologyDefinition;
  const roles = new Set(["state", "degraded"]);
  it.each([
    ["evidence supports the component that has a state", [["EvidenceFor", ["Evidence"], ["Component"]], ["Measures", ["Signal"], ["Component"]]], roles, { type: "EvidenceFor", childIsSource: true }],
    ["the supporting end may be the target", [["Backs", ["Component"], ["Evidence"]]], roles, { type: "Backs", childIsSource: false }],
    ["a signal has a state, so it is not supporting", [["Measures", ["Signal"], ["Component"]]], roles, null],
    ["both ends with states are peers", [["DependsOn", ["Component"], ["Component"]]], roles, null],
    ["without value roles nothing is supporting", [["EvidenceFor", ["Evidence"], ["Component"]]], new Set<string>(), null],
    ["a relationship type with no declared ends says nothing", [["Any", [], []]], roles, null],
  ] as const)("%s", (_, rels, hinted, want) => expect(supportRelation(ont(rels as never), hinted)).toEqual(want));
  it.each([
    ["the default is the support relation", SUPPORT_GROUPING, { type: "EvidenceFor", childIsSource: true }],
    ["none", "", null],
    ["a chosen relationship type", "Measures", { type: "Measures" }],
  ])("a setting of %s resolves to %j", (_, setting, want) => expect(resolveGrouping({ ontology: ont([["EvidenceFor", ["Evidence"], ["Component"]]]), presentation: [{ property: "state" }] }, setting)).toEqual(want));
});
