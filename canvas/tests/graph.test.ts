import { describe, expect, it } from "vitest";
import { Graph, invertPatch } from "../src/core/graph";
import { composeChanges, diffGraphs, propDiff } from "../src/core/diff";
import { addE, addN, delE, delN, ent, patch, rel, setN, ver } from "./helpers";

const base = () => Graph.fromSnapshot({
  version: ver("1"),
  entities: [ent("a", "Service", { health: "healthy" }), ent("b")],
  relationships: [rel("r1", "a", "b")],
});
const names = (g: Graph) => [[...g.entities()].map((e) => e.id).sort(), [...g.relationships()].map((r) => r.id).sort()];

describe("Graph.apply", () => {
  it.each([
    ["adds an entity", patch("1", "2", [addN(ent("c"))]), [["a", "b", "c"], ["r1"]]],
    ["removes an entity and leaves the edge unresolved", patch("1", "2", [delN(ent("b"))]), [["a"], ["r1"]]],
    ["adds a relationship", patch("1", "2", [addE(rel("r2", "b", "a"))]), [["a", "b"], ["r1", "r2"]]],
    ["removes a relationship", patch("1", "2", [delE(rel("r1", "a", "b"))]), [["a", "b"], []]],
    ["changes an entity", patch("1", "2", [setN(ent("a", "Service", { health: "healthy" }), ent("a", "Service", { health: "degraded" }))]), [["a", "b"], ["r1"]]],
  ])("%s", (_n, p, expected) => {
    const g = base();
    const r = g.apply(p);
    expect(r.ok).toBe(true);
    expect(names(g)).toEqual(expected);
    expect(g.revision).toBe("2");
  });

  it.each([
    ["wrong starting revision", patch("9", "10", [addN(ent("c"))]), "patch starts at revision 9"],
    ["add of existing entity", patch("1", "2", [addN(ent("a"))]), "expected the element to be absent"],
    ["remove of missing entity", patch("1", "2", [delN(ent("zz"))]), "expected the element to exist"],
    ["stale before-state", patch("1", "2", [setN(ent("a", "Service", { health: "old" }), ent("a", "Service", { health: "x" }))]), "before-state differs"],
  ])("rejects atomically: %s", (_n, p, msg) => {
    const g = base();
    const r = g.apply({ ...p, edits: [addN(ent("new")), ...p.edits] });
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.conflicts.map((c) => c.message).join("|")).toContain(msg);
    expect(g.entity("new")).toBeUndefined();
    expect(g.revision).toBe("1");
  });

  it("an edge whose endpoint is missing is drawn to a stub, and the entity that arrives takes the stub's slot", () => {
    const g = base();
    const slotB = g.nodeSlot.get("b")!;
    const res = g.apply(patch("1", "2", [delN(ent("b"))]));
    expect(res.ok && res.dirtyNodes).toContain(slotB);
    expect([g.unresolvedEdgeCount, g.stubCount, g.isStub(slotB), g.entity("b"), g.nodeCount]).toEqual([0, 1, true, undefined, 1]);
    expect([...g.entities()].map((e) => e.id)).toEqual(["a"]);
    expect(g.edgeDst[g.edgeSlot.get("r1")!]).toBe(slotB);
    g.apply(patch("2", "3", [addN(ent("b"))]));
    expect([g.stubCount, g.nodeSlot.get("b"), g.nodes[slotB]?.id, g.out[g.nodeSlot.get("a")!].length, g.inn[slotB].length]).toEqual([0, slotB, "b", 1, 1]);
  });

  it.each([
    ["an edge added with a missing end makes a stub", (g: Graph) => g.apply(patch("1", "2", [addE(rel("r9", "a", "ghost"))])), 1, 1],
    ["an edge to a missing end that is removed frees the stub", (g: Graph) => { g.apply(patch("1", "2", [addE(rel("r9", "a", "ghost"))])); return g.apply(patch("2", "3", [delE(rel("r9", "a", "ghost"))])); }, 0, 0],
    ["two edges to one missing end share one stub", (g: Graph) => g.apply(patch("1", "2", [addE(rel("r8", "a", "ghost")), addE(rel("r9", "b", "ghost"))])), 1, 2],
  ])("%s", (_, run, stubs, edgesToStub) => {
    const g = base();
    expect(run(g).ok).toBe(true);
    expect(g.stubCount).toBe(stubs);
    const slot = g.stubSlot.get("ghost");
    expect(slot === undefined ? 0 : g.inn[slot].length).toBe(edgesToStub);
    expect(g.nodeCount).toBe(2);
  });

  it("a stub is not an entity: snapshots and entity iteration leave it out, the stub has a node to draw", () => {
    const g = base();
    g.apply(patch("1", "2", [addE(rel("r9", "a", "ghost"))]));
    expect(g.toSnapshot().entities.map((e) => e.id)).toEqual(["a", "b"]);
    expect([...g.stubs()].map((e) => e.id)).toEqual(["ghost"]);
    expect(g.entity("ghost")).toBeUndefined();
  });

  it("round-trips through invertPatch", () => {
    const g = base();
    const before = JSON.stringify(g.toSnapshot());
    const p = patch("1", "2", [addN(ent("c")), addE(rel("r2", "c", "a")), delE(rel("r1", "a", "b")), delN(ent("b"))]);
    expect(g.apply(p).ok).toBe(true);
    expect(g.apply(invertPatch(p)).ok).toBe(true);
    expect(names(g)).toEqual(names(base()));
    expect(g.revision).toBe("1");
    expect(before).toContain('"a"');
  });

  it("reuses freed slots", () => {
    const g = base();
    const slotB = g.nodeSlot.get("b")!;
    g.apply(patch("1", "2", [delE(rel("r1", "a", "b")), delN(ent("b")), addN(ent("c"))]));
    expect(g.nodeSlot.get("c")).toBe(slotB);
  });
});

describe("composeChanges / diffGraphs", () => {
  const a = ent("a"), a2 = ent("a", "Service", { health: "x" });
  it.each([
    ["added", [patch("1", "2", [addN(a)])], { a: "added" }],
    ["removed", [patch("1", "2", [delN(a)])], { a: "removed" }],
    ["changed", [patch("1", "2", [setN(a, a2)])], { a: "changed" }],
    ["add then remove vanishes", [patch("1", "2", [addN(a)]), patch("2", "3", [delN(a)])], {}],
    ["remove then re-add unchanged vanishes", [patch("1", "2", [delN(a)]), patch("2", "3", [addN(a)])], {}],
    ["add then change stays added", [patch("1", "2", [addN(a)]), patch("2", "3", [setN(a, a2)])], { a: "added" }],
  ])("%s", (_n, patches, expected) => {
    const c = composeChanges(patches);
    expect(Object.fromEntries([...c.entities].map(([k, v]) => [k, v.kind]))).toEqual(expected);
  });

  it("diffGraphs finds added, removed and changed elements", () => {
    const x = base();
    const y = base();
    y.apply(patch("1", "2", [addN(ent("c")), delN(ent("b")), setN(ent("a", "Service", { health: "healthy" }), ent("a", "Service", { health: "bad" }))]));
    const d = diffGraphs(x, y);
    expect(Object.fromEntries([...d.entities].map(([k, v]) => [k, v.kind]))).toEqual({ a: "changed", b: "removed", c: "added" });
  });

  it.each([
    [ent("a", "T", { p: "1" }), ent("a", "T", { p: "2" }), [{ property: "p", before: "1", after: "2" }]],
    [ent("a", "T"), ent("a", "U"), [{ property: "type", before: "T", after: "U" }]],
    [null, ent("a", "T", { p: "1" }), [{ property: "p", before: null, after: "1" }]],
  ])("propDiff %#", (b, a, expected) => expect(propDiff(b, a)).toEqual(expected));
});
