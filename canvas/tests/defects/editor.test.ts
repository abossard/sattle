// Defects found while rebuilding the projections of the estate catalog from editor gestures (tests/editing/rebuild.test.ts).
import { describe, expect, it } from "vitest";
import { Graph } from "../../src/core/graph";
import type { Edit } from "../../src/core/model";
import { addNamedEdits, addPatternEdgeEdits, addReadEdits, placeholderChildren } from "../../src/editor/commands";
import { parsePalette } from "../../src/editor/palette";
import { coalesce, contains, ent, text } from "../../src/editor/patches";
import { parsePlaceholders, parseSkeleton } from "../../src/editor/service";
import { EditorSession } from "../../src/editor/session";
import { fakeEngine } from "../fixtures/editor-fake";
import { wirePatch } from "../fixtures/engine-fake";
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

// ---- placeholders (engine 3.19): a real `n = count()` and the skeleton's placeholder are the same element; only the skeleton answer says which ----

describe("the placeholders of a skeleton", () => {
  const R = "d:m/projection:p/op:r";
  const palette = parsePalette({ operators: [{ kind: "Rollup", supported: true, role: "linear", container: "", slot: "op", ports: [], strategy: null, sink: false, telemetry: false, params: [], slots: [{ name: "agg", children: ["Aggregate"], multiplicity: "+", ordered: true }, { name: "read", children: ["FieldRef"], multiplicity: "+", ordered: true }] }], functions: [], aggregates: [{ name: "count", supported: true }, { name: "sum", supported: true }] });
  const N = `${R}/agg:n`, READ = `${R}/read:#0`;
  const rollup = () => {
    const edits = [ent("d:m/projection:p", "Projection", {}), ent(R, "Rollup", { name: text("r") }), ent(N, "Aggregate", { target: text("n"), fn: text("count") }), contains(R, N, "agg", 0), ent(READ, "FieldRef", { path: text("x.y") }), contains(R, READ, "read", 0)];
    return { entities: edits.filter((e) => e.kind === "entity").map((e) => (e as unknown as { after: never }).after), relationships: edits.filter((e) => e.kind === "relationship").map((e) => (e as unknown as { after: never }).after) };
  };
  const graph = () => Graph.fromSnapshot({ version: ver("1", "catalog"), ...rollup() });
  const removes = (r: { ok: boolean; value?: Edit[] }, id: string) => !!r.ok && r.value!.some((e) => e.kind === "entity" && e.id === id && e.after === null);

  it.each<[string, unknown, string[]]>([
    ["the ids the engine names", { edits: [], placeholders: [{ id: { value: "a" }, slot: "agg" }, { id: { value: "b" }, slot: "read" }] }, ["a", "b"]],
    ["an engine before 3.19 names none", { edits: [] }, []],
    ["entries that are not ids are left out", { placeholders: [{ id: "a" }, null, { id: { value: "" } }, { id: { value: "c" } }] }, ["c"]],
  ])("the skeleton answer: %s", (_, raw, ids) => expect(parsePlaceholders(raw)).toEqual(ids));

  // the answer as the engine sends it: a patch of the wire contract with `placeholders` beside `edits`; the patch is checked against the schema, which allows no extra key
  it.each<[string, Record<string, unknown>, string[]]>([
    ["an answer of engine 3.19", { placeholders: [{ id: { value: "p2" }, slot: "key" }] }, ["p2"]],
    ["an answer of an engine before 3.19", {}, []],
  ])("%s is read through the contract schema: the patch and the placeholders", (_, extra, ids) => {
    const r = parseSkeleton({ ...wirePatch(1, 2), ...extra });
    expect([r.edits.length, r.to.revision, r.placeholders]).toEqual([1, "2", ids]);
  });
  it("an answer that is no patch is still refused", () => expect(() => parseSkeleton({ placeholders: [], edits: "none" })).toThrow(/skeleton/));

  it.each<[string, Set<string>, string[]]>([
    ["a child the engine named is a placeholder", new Set([N]), [N]],
    ["the same child not named is the author's", new Set(), []],
    ["an id that is named and is not in the graph is nothing", new Set([`${R}/agg:gone`]), []],
    ["an id of another slot is not a placeholder of this one", new Set([READ]), []],
  ])("%s", (_, known, want) => expect(placeholderChildren(graph(), R, "agg", known)).toEqual(want));

  it("an author's `n = count()` stays when the next aggregate is added (the defect), a named placeholder goes", () => {
    const g = graph();
    expect(removes(addNamedEdits(g, R, "agg", "Aggregate", "total", "x.v", palette, { fn: "sum" }), N)).toBe(false);
    expect(removes(addNamedEdits(g, R, "agg", "Aggregate", "total", "x.v", palette, { fn: "sum" }, new Set([N])), N)).toBe(true);
  });
  it("a read: the placeholder path goes only when it was named", () => {
    expect(removes(addReadEdits(graph(), R, "a.b"), READ)).toBe(false);
    expect(removes(addReadEdits(graph(), R, "a.b", "read", new Set([READ])), READ)).toBe(true);
  });

  it("the session forgets a placeholder that an edit replaced, and knows it again when that edit is undone", async () => {
    const eng = fakeEngine(rollup());
    const s = await EditorSession.open(eng.svc);
    s.placeholders.add(N);
    const first = addNamedEdits(s.graph, R, "agg", "Aggregate", "total", "x.v", palette, { fn: "sum" }, s.placeholders);
    expect(first.ok && (await s.apply(first.value, "add total")).ok).toBe(true);
    expect([s.placeholders.has(N), !!s.graph.entity(N)]).toEqual([false, false]);
    // the author now writes a real n = count(): it is theirs, the next aggregate leaves it alone
    const real = addNamedEdits(s.graph, R, "agg", "Aggregate", "n", "", palette, { fn: "count" }, s.placeholders);
    expect(real.ok && (await s.apply(real.value, "add n")).ok).toBe(true);
    expect(removes(addNamedEdits(s.graph, R, "agg", "Aggregate", "more", "x.v", palette, { fn: "sum" }, s.placeholders), N)).toBe(false);
    await s.undo();   // the real n is gone
    await s.undo();   // total is gone, the placeholder n is back
    expect([s.placeholders.has(N), !!s.graph.entity(N)]).toEqual([true, true]);
    await s.redo();
    expect(s.placeholders.has(N)).toBe(false);
  });
  it("an author who writes exactly what the placeholder is makes it theirs: no patch is sent, and the next aggregate leaves it alone", async () => {
    const eng = fakeEngine(rollup());
    const s = await EditorSession.open(eng.svc);
    s.placeholders.add(N);
    const same = addNamedEdits(s.graph, R, "agg", "Aggregate", "n", "", palette, { fn: "count" }, s.placeholders);
    const sent = eng.calls.applyPatch;
    expect(same.ok && (await s.apply(same.value, "add n")).ok).toBe(true);
    expect([eng.calls.applyPatch - sent, s.placeholders.has(N), !!s.graph.entity(N)]).toEqual([0, false, true]);
    expect(removes(addNamedEdits(s.graph, R, "agg", "Aggregate", "total", "x.v", palette, { fn: "sum" }, s.placeholders), N)).toBe(false);
  });
  it("creating an element does not consume a placeholder of the same id that is remembered first", async () => {
    const eng = fakeEngine();
    const s = await EditorSession.open(eng.svc);
    s.placeholders.add("d:m/entity:E1");
    await s.apply([ent("d:m/entity:E1", "Entity", {})], "the skeleton itself");
    expect(s.placeholders.has("d:m/entity:E1")).toBe(true);
  });
});
