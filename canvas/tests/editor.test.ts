import { describe, expect, it } from "vitest";
import { Catalog } from "../src/core/catalog";
import { Graph, invertPatch } from "../src/core/graph";
import type { Edit, Entity, Rel } from "../src/core/model";
import { align, autoLayout } from "../src/editor/layout";
import { stackOf } from "../src/editor/stack";
import { addNamedEdits, addRuleEdits, canWire, paramEdits, pasteEdits, setExprEdits, statementNames, unwireEdits, wireEdits } from "../src/editor/commands";
import { canonicalFunctions, exprEdits, parseExpr } from "../src/editor/expr";
import { parsePalette } from "../src/editor/palette";
import { coalesce, contains, ent, invertEdits, rel, removeEdits, text } from "../src/editor/patches";

const P = "d:m/projection:p";
const port = (owner: string, name: string, dir: string, kind: string) => ent(`${owner}/port:${name}`, "Port", { name: text(name), direction: text(dir), kind: text(kind) });
const catalogGraph = () => {
  const edits: Edit[] = [
    ent(P, "Projection", { name: text("p") }),
    port(P, "master", "in", "graph"),
    ent(`${P}/op:m`, "Match", { name: text("m") }), port(`${P}/op:m`, "graph", "in", "graph"), port(`${P}/op:m`, "out", "out", "rows"),
    ent(`${P}/op:f`, "Filter", { name: text("f") }), port(`${P}/op:f`, "in", "in", "rows"), port(`${P}/op:f`, "out", "out", "rows"),
    ent(`${P}/op:e`, "EmitEntity", { name: text("e") }), port(`${P}/op:e`, "in", "in", "rows"),
  ];
  const ents: Entity[] = [], rels: Rel[] = [];
  const add = (es: Edit[]) => { for (const e of es) { if (e.kind === "entity" && e.after) ents.push(e.after); if (e.kind === "relationship" && e.after) rels.push(e.after); } };
  add(edits);
  for (const op of ["m", "f", "e"]) add([contains(P, `${P}/op:${op}`, "op")]);
  for (const [o, ports] of [["m", ["graph", "out"]], ["f", ["in", "out"]], ["e", ["in"]]] as const) for (const n of ports) add([contains(`${P}/op:${o}`, `${P}/op:${o}/port:${n}`, "port")]);
  add([contains(P, `${P}/port:master`, "port")]);
  return Graph.fromSnapshot({ version: { tenant: "t", graph: "catalog", revision: "0" }, entities: ents, relationships: rels });
};
const palette = parsePalette({ kernelDigest: "x", version: "3", operators: [], functions: [{ name: "Equal", signature: "", role: "scalar", supported: true }, { name: "And", signature: "", role: "scalar", supported: true }, { name: "Not", signature: "", role: "scalar", supported: true }, { name: "Add", signature: "", role: "scalar", supported: true }, { name: "Negate", signature: "", role: "scalar", supported: true }] });

describe("expression parser", () => {
  it.each([
    ["s.status", { k: "field", path: "s.status" }],
    ['"ok"', { k: "lit", kind: "text", value: "ok" }],
    ["42", { k: "lit", kind: "integer", value: "42" }],
    ["-1.5", { k: "lit", kind: "decimal", value: "-1.5" }],
    ["true", { k: "lit", kind: "boolean", value: "true" }],
    ["null", { k: "lit", kind: "null", value: "" }],
    ["s != t", { k: "call", fn: "NotEqual", args: [{ k: "field", path: "s" }, { k: "field", path: "t" }] }],
    ["a + b * 2", { k: "call", fn: "Add", args: [{ k: "field", path: "a" }, { k: "call", fn: "Multiply", args: [{ k: "field", path: "b" }, { k: "lit", kind: "integer", value: "2" }] }] }],
    ["!(a && b)", { k: "call", fn: "Not", args: [{ k: "call", fn: "And", args: [{ k: "field", path: "a" }, { k: "field", path: "b" }] }] }],
    ["Concat(a, \"x\")", { k: "call", fn: "Concat", args: [{ k: "field", path: "a" }, { k: "lit", kind: "text", value: "x" }] }],
  ])("%s", (src, expr) => expect(parseExpr(src)).toEqual({ ok: true, expr }));
  it.each([["a =="], ["(a"], ['"x'], ["a b"], ["f(a,"], [""]])("rejects %j with a position", (src) => {
    const r = parseExpr(src);
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.error).toMatch(/at \d+/);
  });
  it("builds the catalog subtree with ordered args and a function relationship", () => {
    const parsed = parseExpr("s != t");
    if (!parsed.ok) throw new Error();
    const edits = exprEdits(`${P}/op:f`, "predicate", 0, parsed.expr);
    const ids = edits.filter((e) => e.kind === "entity").map((e) => `${e.kind === "entity" ? e.after!.type : ""} ${e.id.replace(P, "")}`);
    expect(ids).toEqual(["Call /op:f/predicate:#0", "FieldRef /op:f/predicate:#0/arg:#0", "FieldRef /op:f/predicate:#0/arg:#1"]);
    const rels = edits.filter((e) => e.kind === "relationship").map((e) => e.id.replace(new RegExp(P, "g"), ""));
    expect(rels).toContain("/op:f/predicate:#0|function|d:sattle.kernel/builtin:NotEqual");
    expect(rels).toContain("/op:f/predicate:#0|contains|/op:f/predicate:#0/arg:#1");
  });
});

describe("wiring with type checking", () => {
  const g = catalogGraph();
  it.each([
    ["master to Match graph", `${P}/port:master`, `${P}/op:m/port:graph`, true],
    ["Match to Filter", `${P}/op:m/port:out`, `${P}/op:f/port:in`, true],
    ["Filter to Emit", `${P}/op:f/port:out`, `${P}/op:e/port:in`, true],
    ["rows into a graph port", `${P}/op:m/port:out`, `${P}/op:m/port:graph`, false],
    ["graph into a rows port", `${P}/port:master`, `${P}/op:f/port:in`, false],
    ["input to input", `${P}/op:f/port:in`, `${P}/op:e/port:in`, false],
    ["output to output", `${P}/op:m/port:out`, `${P}/op:f/port:out`, false],
    ["an operator to itself", `${P}/op:f/port:out`, `${P}/op:f/port:in`, false],
    ["not a port", `${P}/op:f`, `${P}/op:e/port:in`, false],
  ])("%s", (_, from, to, ok) => expect(canWire(g, from, to).ok).toBe(ok));

  it("an input takes one wire, and a wire may not close a cycle", () => {
    const w = (a: string, b: string) => Graph.fromSnapshot({ ...g.toSnapshot(), relationships: [...g.toSnapshot().relationships, ...wireEdits(a, b).map((e) => (e.kind === "relationship" ? e.after! : null)).filter((r): r is Rel => !!r)] });
    const once = w(`${P}/op:m/port:out`, `${P}/op:f/port:in`);
    const second = canWire(once, `${P}/op:m/port:out`, `${P}/op:f/port:in`);
    expect(second).toEqual({ ok: false, reason: "this input already has a wire" });
    const chain = Graph.fromSnapshot({ ...once.toSnapshot(), relationships: [...once.toSnapshot().relationships, ...wireEdits(`${P}/op:f/port:out`, `${P}/op:e/port:in`).map((e) => (e.kind === "relationship" ? e.after! : null)).filter((r): r is Rel => !!r)] });
    expect(canWire(chain, `${P}/op:e/port:in`, `${P}/op:m/port:graph`).ok).toBe(false);
    expect(unwireEdits(chain, `${P}/op:f/port:out|wire|${P}/op:e/port:in`).length).toBe(1);
  });
});

describe("edits", () => {
  it("removing an operator removes its ports, its wires and the contains edge of its parent", () => {
    const g = catalogGraph();
    const edits = removeEdits(g, [`${P}/op:f`]);
    const ids = edits.map((e) => (e as { id: string }).id.replace(new RegExp(P, "g"), ""));
    expect(ids).toEqual(expect.arrayContaining(["/op:f", "/op:f/port:in", "/op:f/port:out"]));
    expect(ids.some((i) => i.endsWith("|contains|/op:f"))).toBe(true);
    const after = Graph.fromSnapshot(g.toSnapshot());
    const res = after.apply({ from: { tenant: "t", graph: "catalog", revision: "0" }, to: { tenant: "t", graph: "catalog", revision: "1" }, projection: "catalog", profile: "extensionalFinite", edits, cause: { inputs: [], configurationVersion: "0" } });
    expect(res.ok).toBe(true);
    expect(after.entity(`${P}/op:f`)).toBeUndefined();
    expect(after.entity(`${P}/op:m`)).toBeDefined();
  });

  it("every edit list inverts to the original graph (the basis of undo)", () => {
    const g = catalogGraph();
    const before = JSON.stringify(g.toSnapshot().entities.map((e) => e.id).sort());
    const sets: Edit[][] = [removeEdits(g, [`${P}/op:f`]), wireEdits(`${P}/op:m/port:out`, `${P}/op:f/port:in`), pasteEdits(g, P, [`${P}/op:f`])];
    for (const edits of sets) {
      const h = Graph.fromSnapshot(g.toSnapshot());
      const mk = (e: Edit[], from: string, to: string) => ({ from: { tenant: "t", graph: "catalog", revision: from }, to: { tenant: "t", graph: "catalog", revision: to }, projection: "catalog", profile: "extensionalFinite" as const, edits: e, cause: { inputs: [], configurationVersion: "0" } });
      expect(h.apply(mk(edits, "0", "1")).ok).toBe(true);
      expect(h.apply(mk(invertEdits(edits), "1", "2")).ok).toBe(true);
      expect(JSON.stringify(h.toSnapshot().entities.map((e) => e.id).sort())).toBe(before);
      expect(h.toSnapshot().relationships.length).toBe(g.toSnapshot().relationships.length);
    }
    expect(invertPatch).toBeTypeOf("function");
  });

  it("pasting copies the operator with a new name and its ports, and links it to the projection", () => {
    const g = catalogGraph();
    const edits = pasteEdits(g, P, [`${P}/op:f`]);
    const names = edits.filter((e) => e.kind === "entity").map((e) => e.id.replace(P, ""));
    expect(names).toEqual(["/op:f2", "/op:f2/port:in", "/op:f2/port:out"]);
    expect(edits.some((e) => e.kind === "relationship" && e.id === `${P}|contains|${P}/op:f2`)).toBe(true);
    const e0 = edits.find((e) => e.kind === "entity" && e.id.endsWith("op:f2")) as Extract<Edit, { kind: "entity" }>;
    expect(e0.after!.props.name).toEqual(text("f2"));
  });

  it("parameters are typed and constrained by the palette", () => {
    const g = catalogGraph();
    const ok = paramEdits(g, `${P}/op:f`, { name: "width", kind: "Integer", required: true }, "60");
    expect(ok.ok).toBe(true);
    expect(paramEdits(g, `${P}/op:f`, { name: "width", kind: "Integer", required: true }, "6x").ok).toBe(false);
    expect(paramEdits(g, `${P}/op:f`, { name: "late", kind: "Text", required: true, values: ["ignore", "revise"] }, "later")).toEqual({ ok: false, reason: "late must be one of ignore, revise" });
    expect(paramEdits(g, `${P}/op:f`, { name: "on", kind: "Boolean", required: true }, "true").ok).toBe(true);
  });

  it("expression slots: replace the predicate, reject unknown functions and bad syntax", () => {
    const g = catalogGraph();
    const r = setExprEdits(g, `${P}/op:f`, "predicate", "s != t", { ...palette, functions: [...palette.functions, { name: "NotEqual", signature: "", role: "scalar", supported: true }] });
    expect(r.ok && r.value.some((e) => (e as { id: string }).id === `${P}/op:f/predicate:#0`)).toBe(true);
    expect(setExprEdits(g, `${P}/op:f`, "predicate", "Nope(1)", palette)).toEqual({ ok: false, reason: "unknown function Nope" });
    expect(setExprEdits(g, `${P}/op:f`, "predicate", "a ==", palette).ok).toBe(false);
    const named = addNamedEdits(g, `${P}/op:f`, "assign", "Assign", "total", "a + 1", palette);
    expect(named.ok && named.value.filter((e) => e.kind === "entity").map((e) => e.id.replace(P, ""))).toEqual(["/op:f/assign:total", "/op:f/assign:total/value:#0", "/op:f/assign:total/value:#0/arg:#0", "/op:f/assign:total/value:#0/arg:#1"]);
    expect(addNamedEdits(g, `${P}/op:f`, "assign", "Assign", "bad name", "1", palette).ok).toBe(false);
  });

  it("rules need a known class and a fresh name", () => {
    const g = catalogGraph();
    expect(addRuleEdits(g, P, "r1", "readonly", "why", [`${P}/op:e`]).ok).toBe(true);
    expect(addRuleEdits(g, P, "r1", "nonsense", "", []).ok).toBe(false);
  });

  it("statement names for refactoring come from operator and rule ids", () => {
    expect(statementNames(catalogGraph(), [`${P}/op:m`, `${P}/op:backed#0`, `${P}/rule:caps`, `${P}/op:e`])).toEqual(["m", "backed", "caps", "e"]);
  });
});

describe("layout", () => {
  it("columns follow the data flow and alignment keeps the others in place", () => {
    const base = catalogGraph().toSnapshot();
    const wires = [[`${P}/port:master`, `${P}/op:m/port:graph`], [`${P}/op:m/port:out`, `${P}/op:f/port:in`], [`${P}/op:f/port:out`, `${P}/op:e/port:in`]]
      .flatMap(([a, b]) => wireEdits(a, b)).map((e) => (e.kind === "relationship" ? e.after! : null)).filter((r): r is Rel => !!r);
    const dg = new Catalog(Graph.fromSnapshot({ ...base, relationships: [...base.relationships, ...wires] })).projectionGraph(P);
    const pos = autoLayout(dg);
    expect(pos.get(`${P}/port:master`)!.x).toBeLessThan(pos.get(`${P}/op:m`)!.x);
    expect(pos.get(`${P}/op:m`)!.x).toBeLessThan(pos.get(`${P}/op:f`)!.x);
    expect(pos.get(`${P}/op:f`)!.x).toBeLessThan(pos.get(`${P}/op:e`)!.x);
    const a = align([`${P}/op:m`, `${P}/op:f`], new Map([[`${P}/op:m`, { x: 10, y: 5 }], [`${P}/op:f`, { x: 300, y: 80 }], [`${P}/op:e`, { x: 9, y: 9 }]]), "left");
    expect(a.get(`${P}/op:f`)).toEqual({ x: 10, y: 80 });
    expect(a.get(`${P}/op:e`)).toEqual({ x: 9, y: 9 });
  });
});

describe("stacked projections", () => {
  const S = "d:m/space:s";
  const mk = () => {
    const ents = [ent("d:m/projection:a", "Projection"), ent("d:m/projection:b", "Projection"), ent("d:m/projection:c", "Projection"),
      ent(`${S}/graph:master`, "Graph"), ent(`${S}/graph:mid`, "Graph"), ent(`${S}/graph:top`, "Graph"),
      ent(`${S}/binding:mid.in`, "Binding"), ent(`${S}/binding:top.in`, "Binding"), ent("d:m/projection:b/port:in", "Port"), ent("d:m/projection:c/port:in", "Port")];
    const rels = [rel(`${S}/graph:master`, "producedBy", "d:m/projection:a"), rel(`${S}/graph:mid`, "producedBy", "d:m/projection:b"), rel(`${S}/graph:top`, "producedBy", "d:m/projection:c"),
      rel(`${S}/binding:mid.in`, "bindsPort", "d:m/projection:b/port:in"), rel(`${S}/binding:mid.in`, "boundFrom", `${S}/graph:master`),
      rel(`${S}/binding:top.in`, "bindsPort", "d:m/projection:c/port:in"), rel(`${S}/binding:top.in`, "boundFrom", `${S}/graph:mid`)];
    const E: Entity[] = [], R: Rel[] = [];
    for (const e of [...ents, ...rels]) { if (e.kind === "entity" && e.after) E.push(e.after); if (e.kind === "relationship" && e.after) R.push(e.after); }
    return new Catalog(Graph.fromSnapshot({ version: { tenant: "t", graph: "catalog", revision: "0" }, entities: E, relationships: R }));
  };
  const M = "d:m/projection:";
  it.each([
    ["the top of a stack of three", `${M}c`, [`${S}/graph:master`, `${S}/graph:mid`], [`${M}a`, `${M}b`], []],
    ["the middle", `${M}b`, [`${S}/graph:master`], [`${M}a`], [`${M}c`]],
    ["the first, reading no graph produced by another projection", `${M}a`, [], [], [`${M}b`]],
  ])("%s", (_, p, graphs, producers, down) => {
    const st = stackOf(mk(), p);
    expect(st.up.map((u) => u.graph)).toEqual(graphs);
    expect(st.up.map((u) => u.projection)).toEqual(producers);
    expect(st.down).toEqual(down);
  });
});

describe("coalesce", () => {
  const e = (id: string, v: string) => ent(id, "Literal", { value: text(v) });
  const as = (x: Edit) => x as Extract<Edit, { kind: "entity" }>;
  it("an element removed and created again is one change, and one that ends as it began is none", () => {
    const old = as(e("a", "1")).after!, neu = as(e("a", "2")).after!;
    const out = coalesce([{ kind: "entity", id: "a", before: old, after: null }, { kind: "entity", id: "b", before: null, after: neu }, { kind: "entity", id: "a", before: null, after: neu }]);
    expect(out.map((x) => [(x as { id: string }).id, as(x).before?.props.value, as(x).after?.props.value])).toEqual([["a", text("1"), text("2")], ["b", undefined, text("2")]]);
    expect(coalesce([{ kind: "entity", id: "a", before: old, after: null }, { kind: "entity", id: "a", before: null, after: old }])).toEqual([]);
  });
  it("replacing an expression by another with the same root id patches the root instead of removing it", () => {
    const g = catalogGraph();
    const first = setExprEdits(g, `${P}/op:f`, "predicate", "1", palette);
    if (!first.ok) throw new Error("first");
    const h = Graph.fromSnapshot({ ...g.toSnapshot(), entities: [...g.toSnapshot().entities, ...first.value.filter((x) => x.kind === "entity").map((x) => as(x).after!)], relationships: [...g.toSnapshot().relationships, ...first.value.filter((x) => x.kind === "relationship").map((x) => (x as Extract<Edit, { kind: "relationship" }>).after!)] });
    const second = setExprEdits(h, `${P}/op:f`, "predicate", "2", palette);
    if (!second.ok) throw new Error("second");
    const ids = coalesce(second.value).map((x) => (x as { id: string }).id);
    expect(ids.length).toBe(new Set(ids).size);
  });
});

describe("identities in expressions", () => {
  const withDomain = () => {
    const base = catalogGraph().toSnapshot();
    const dom = ent("d:m/identity:thing", "IdentityDomain", { name: text("thing") }), comp = ent("d:m/identity:thing/component:id", "KeyComponent", { name: text("id") });
    return Graph.fromSnapshot({ ...base, entities: [...base.entities, (dom as Extract<Edit, { kind: "entity" }>).after!, (comp as Extract<Edit, { kind: "entity" }>).after!] });
  };
  it("builds IdOf with a domain relationship and SetComponent children that point at the domain's component", () => {
    const r = setExprEdits(withDomain(), `${P}/op:e`, "id", "id thing(id: a.b)", palette);
    if (!r.ok) throw new Error(r.reason);
    const rels = r.value.filter((x) => x.kind === "relationship").map((x) => (x as { id: string }).id.replace(new RegExp(P, "g"), ""));
    expect(rels).toContain("/op:e/id:#0|domain|d:m/identity:thing");
    expect(rels).toContain("/op:e/id:#0/component:id|component|d:m/identity:thing/component:id");
    const comp = r.value.find((x) => x.kind === "entity" && x.id.endsWith("component:id")) as Extract<Edit, { kind: "entity" }>;
    expect([comp.after!.type, comp.after!.props]).toEqual(["SetComponent", {}]);
  });
  it.each([["id nope(id: a)", "unknown identity domain nope"], ["id thing(other: a)", "the identity domain has no component thing.other"]])("%s is refused: %s", (src, reason) => expect(setExprEdits(withDomain(), `${P}/op:e`, "id", src, palette)).toEqual({ ok: false, reason }));
});

describe("function spelling", () => {
  it.each([["concat(a, b)", "Concat"], ["lowerInvariant(a)", "LowerInvariant"], ["if(a == 1, 2, 3)", "If"], ["isPresent(a)", "IsPresent"]])("%s is the palette's %s", (src, want) => {
    const p = parseExpr(src);
    if (!p.ok) throw new Error(p.error);
    const r = canonicalFunctions(p.expr, ["Concat", "LowerInvariant", "If", "IsPresent", "Equal"]);
    expect(r.unknown).toEqual([]);
    expect(JSON.stringify(r.expr)).toContain(`"fn":"${want}"`);
  });
  it("lists names the palette does not have", () => {
    const p = parseExpr("nope(concat(a))");
    if (!p.ok) throw new Error();
    expect(canonicalFunctions(p.expr, ["Concat"]).unknown).toEqual(["nope"]);
  });
});
