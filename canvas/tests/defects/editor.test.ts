// Defects found while rebuilding the projections of the estate catalog from editor gestures (tests/editing/rebuild.test.ts).
import { describe, expect, it } from "vitest";
import { Graph } from "../../src/core/graph";
import type { Edit } from "../../src/core/model";
import { addPatternEdgeEdits } from "../../src/editor/commands";
import { coalesce, contains, ent, text } from "../../src/editor/patches";
import { ver } from "../helpers";

const OP = "d:m/projection:p/op:match";
const pattern = () => {
  const edits = [ent("d:m/projection:p", "Projection", {}), ent(OP, "Match", {}), ent(`${OP}/node:a`, "PatternNode", {}), ent(`${OP}/node:b`, "PatternNode", {}), ent("d:m/relationship:Calls", "RelationshipType", {}), contains(OP, `${OP}/node:a`, "node", 0), contains(OP, `${OP}/node:b`, "node", 1)];
  return Graph.fromSnapshot({ version: ver("1", "catalog"), entities: edits.filter((e) => e.kind === "entity").map((e) => (e as unknown as { after: never }).after), relationships: edits.filter((e) => e.kind === "relationship").map((e) => (e as unknown as { after: never }).after) });
};
const created = (r: ReturnType<typeof addPatternEdgeEdits>) => (r.ok ? r.value.filter((e): e is Extract<Edit, { kind: "entity" }> => e.kind === "entity") : []);

describe("the edge of a pattern is named as the engine names it", () => {
  it.each<[string, string, string, boolean]>([
    ["a named edge by its variable, with the variable as a property", "e", `${OP}/edge:e`, true],
    ["an unnamed edge by its index, with no variable property", "", `${OP}/edge:#0`, false],
  ])("%s", (_, variable, id, hasVar) => {
    const [edge] = created(addPatternEdgeEdits(pattern(), OP, variable, "a", "b", "d:m/relationship:Calls"));
    expect([edge.id, "var" in (edge.after?.props ?? {})]).toEqual([id, hasVar]);
  });
  it("a second edge with the same variable is refused", () => {
    const g = pattern();
    const first = addPatternEdgeEdits(g, OP, "e", "a", "b", "d:m/relationship:Calls");
    expect(first.ok).toBe(true);
    if (first.ok) for (const e of first.value) g.apply({ from: ver("1", "catalog"), to: ver("2", "catalog"), projection: "catalog", profile: "extensionalFinite", edits: [e], cause: { inputs: [], configurationVersion: "0" } });
    expect(addPatternEdgeEdits(g, OP, "e", "a", "b", "d:m/relationship:Calls").ok).toBe(false);
  });
});

describe("edits that leave an element as it was are no edits, whatever the order of its properties", () => {
  const a = ent("x", "T", { slot: text("s"), ordinal: text("1") });
  const b = ent("x", "T", { ordinal: text("1"), slot: text("s") });
  it.each<[string, Edit[], number]>([
    ["the same properties in another key order", [{ kind: "entity", id: "x", before: a.after, after: b.after } as Edit], 0],
    ["a property that really changed", [{ kind: "entity", id: "x", before: a.after, after: ent("x", "T", { slot: text("s"), ordinal: text("2") }).after } as Edit], 1],
  ])("%s", (_, edits, kept) => expect(coalesce(edits).length).toBe(kept));
});
