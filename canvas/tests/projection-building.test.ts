import { describe, expect, it } from "vitest";
import { Catalog } from "../src/core/catalog";
import { Graph } from "../src/core/graph";
import type { Edit } from "../src/core/model";
import { addNamedEdits, addParamChildEdits, addRuleEdits, durationText, paramEdits, paramValue, wireEdits } from "../src/editor/commands";
import { parseExpr } from "../src/editor/expr";
import { incompleteOperators } from "../src/editor/incomplete";
import { aggregateAllowed, paramOf, parsePalette } from "../src/editor/palette";
import { contains, ent, rel, text } from "../src/editor/patches";
import { refsOf, setRefEdits } from "../src/editor/refs";
import { newProjectionEdits } from "../src/editor/structures";
import enginePalette from "./fixtures/palette.json";

const P = "d:m/projection:p";
const E = (e: Edit) => e as Extract<Edit, { kind: "entity" }>;
const build = (...edits: Edit[]) => Graph.fromSnapshot({
  version: { tenant: "t", graph: "catalog", revision: "0" },
  entities: edits.filter((e) => e.kind === "entity").map((e) => E(e).after!),
  relationships: edits.filter((e) => e.kind === "relationship").map((e) => (e as Extract<Edit, { kind: "relationship" }>).after!),
});
const port = (owner: string, name: string, dir: string, kind = "rows") => [ent(`${owner}/port:${name}`, "Port", { name: text(name), direction: text(dir), kind: text(kind) }), contains(owner, `${owner}/port:${name}`, "port")];
const op = (name: string, type: string, ports: [string, string][]) => [
  ent(`${P}/op:${name}`, type, { name: text(name) }), contains(P, `${P}/op:${name}`, "op"),
  ...ports.flatMap(([n, d]) => port(`${P}/op:${name}`, n, d)),
];

describe("expressions an author types for every operator", () => {
  it.each([
    ["a and b and c", "And", 3],
    ["a or b and c", "Or", 2],
    ["not a", "Not", 1],
    ["!(a && b)", "Not", 1],
    ["cpu > 90.0", "GreaterThan", 2],
  ])("%s is one %s call with %i arguments", (src, fn, n) => {
    const r = parseExpr(src);
    expect(r.ok && r.expr.k === "call" && [r.expr.fn, r.expr.args.length]).toEqual([fn, n]);
  });
  it("the identity of a domain is read from `domain(component: value)` without the id keyword", () => {
    const r = parseExpr('health.signal(component: c, name: "x")');
    expect(r.ok && r.expr).toMatchObject({ k: "idof", domain: "health.signal", components: [{ name: "component" }, { name: "name" }] });
  });
});

describe("relationships to definitions elsewhere", () => {
  const g = build(ent(P, "Projection"), ...op("e", "EmitEntity", [["in", "in"]]), ent("d:h/entity:A", "EntityType"), ent("d:h/entity:B", "EntityType"), rel(`${P}/op:e`, "ofType", "d:h/entity:A"));
  it.each([["EmitEntity", ["ofType"]], ["Traverse", ["ofType"]], ["Link", ["policy"]], ["Fixpoint", ["over"]], ["Lift", ["ofType", "liftEdge", "liftVia"]], ["Filter", []]])("%s holds %j", (kind, rels) => expect(refsOf(kind).map((r) => r.rel)).toEqual(rels));
  it("pointing it elsewhere replaces what it pointed at, an empty target removes it, an unknown target is refused", () => {
    const moved = setRefEdits(g, `${P}/op:e`, "ofType", "d:h/entity:B");
    expect(moved.ok && moved.value.map((e) => [e.kind, (e as { id: string }).id.split("|").pop(), (e as { after: unknown }).after === null])).toEqual([["relationship", "d:h/entity:A", true], ["relationship", "d:h/entity:B", false]]);
    const cleared = setRefEdits(g, `${P}/op:e`, "ofType", "");
    expect(cleared.ok && cleared.value).toHaveLength(1);
    expect(setRefEdits(g, `${P}/op:e`, "ofType", "d:h/entity:Nope").ok).toBe(false);
  });
});

describe("fixpoints are loops of operators", () => {
  const wired = (pairs: [string, string][], over: string | null) => build(
    ent(P, "Projection"),
    ...op("seed", "Match", [["out", "out"]]), ...op("fp", "Fixpoint", [["seed", "in"], ["out", "out"]]),
    ...op("side", "Match", [["out", "out"]]), ...op("j", "Join", [["left", "in"], ["right", "in"], ["out", "out"]]),
    ...op("m", "Map", [["in", "in"], ["out", "out"]]), ...op("after", "Filter", [["in", "in"], ["out", "out"]]),
    ...pairs.flatMap(([a, b]) => wireEdits(`${P}/op:${a.split(".")[0]}/port:${a.split(".")[1]}`, `${P}/op:${b.split(".")[0]}/port:${b.split(".")[1]}`)),
    ...(over ? [rel(`${P}/op:fp`, "over", `${P}/op:${over}/port:out`)] : []),
  );
  const flow: [string, string][] = [["seed.out", "fp.seed"], ["fp.out", "j.left"], ["side.out", "j.right"], ["j.out", "m.in"], ["fp.out", "after.in"]];
  const short = (id: string) => id.slice(id.lastIndexOf(":") + 1);
  it("the step is every operator between the fixpoint and its step result; the seed, a side input and what follows are outside", () => {
    const [loop] = new Catalog(wired(flow, "m")).projectionGraph(P).loops!;
    expect([short(loop.fixpoint), loop.members.map(short).sort(), short(loop.result!)]).toEqual(["fp", ["j", "m"], "m"]);
  });
  it("an arc from the step result back to the fixpoint is drawn", () => {
    const dg = new Catalog(wired(flow, "m")).projectionGraph(P);
    expect(dg.edges.filter((e) => e.kind === "over").map((e) => [short(e.from), short(e.to)])).toEqual([["m", "fp"]]);
  });
  it("without a step result there is no loop members, only the fixpoint", () => {
    const [loop] = new Catalog(wired(flow, null)).projectionGraph(P).loops ?? [];
    expect(loop).toBeUndefined();
  });
});

describe("what an operator still needs", () => {
  const spec = (kind: string, slots: [string, string][], params: [string, boolean][] = []) => ({
    kind, supported: true, role: "linear", container: "", slot: "op", ports: [], strategy: null, sink: false, telemetry: false,
    params: params.map(([name, required]) => ({ name, kind: "Text", required })), slots: slots.map(([name, multiplicity]) => ({ name, children: [], multiplicity, ordered: false })),
  });
  const palette = parsePalette({ operators: [spec("Filter", [["predicate", "1"]]), spec("Group", [["key", "+"], ["agg", "+"]]), spec("Threshold", [], [["above", true], ["initial", false]]), spec("EmitEntity", [["id", "1"], ["set", "*"]])] });
  const wiredTo = (id: string) => [rel(`${P}/op:x/port:out`, "wire", id)];
  it.each([
    ["a filter without a predicate", [...op("f", "Filter", [["in", "in"]])], ["predicate", "input in is not connected"]],
    ["a group without key and aggregates", [...op("gr", "Group", [])], ["key (at least one)", "agg (at least one)"]],
    ["a threshold without a required parameter", [...op("t", "Threshold", [])], ["parameter above"]],
    ["an emit without its type", [...op("em", "EmitEntity", []), ent(`${P}/op:em/id:#0`, "IdOf"), contains(`${P}/op:em`, `${P}/op:em/id:#0`, "id")], ["entity type"]],
  ] as [string, Edit[], string[]][])("%s lists %j", (_, edits, missing) => {
    const g = build(ent(P, "Projection"), ...edits);
    expect(incompleteOperators(g, palette, P).flatMap((i) => i.missing)).toEqual(missing);
  });
  it("a finished operator is not listed", () => {
    const g = build(ent(P, "Projection"), ...op("f", "Filter", [["in", "in"]]), ent(`${P}/op:f/predicate:#0`, "Call"), contains(`${P}/op:f`, `${P}/op:f/predicate:#0`, "predicate"), ...wiredTo(`${P}/op:f/port:in`));
    expect(incompleteOperators(g, palette, P)).toEqual([]);
  });
});

describe("children an operator needs to print are replaced, not added to", () => {
  const palette = parsePalette({
    operators: [{ kind: "Group", supported: true, role: "linear", container: "", slot: "op", ports: [], strategy: null, sink: false, telemetry: false, params: [], slots: [{ name: "key", children: ["Assign"], multiplicity: "+", ordered: true }, { name: "agg", children: ["Aggregate"], multiplicity: "+", ordered: false }] }],
    functions: [], aggregates: [{ name: "count", supported: true }, { name: "last", supported: true }],
  });
  const g = build(ent(P, "Projection"), ent(`${P}/op:g`, "Group", { name: text("g") }), contains(P, `${P}/op:g`, "op"), ent(`${P}/op:g/key:x`, "Assign", { target: text("x") }), contains(`${P}/op:g`, `${P}/op:g/key:x`, "key"), ent(`${P}/op:g/key:x/value:#0`, "Literal", { kind: text("text"), value: text("") }), contains(`${P}/op:g/key:x`, `${P}/op:g/key:x/value:#0`, "value"));
  it("the first key replaces the skeleton's placeholder `x`", () => {
    const r = addNamedEdits(g, `${P}/op:g`, "key", "Assign", "c", "r.id", palette);
    expect(r.ok && r.value.some((e) => e.kind === "entity" && e.before?.id === `${P}/op:g/key:x` && e.after === null)).toBe(true);
  });
  it.each([["count", ""], ["last", "t"]])("an aggregate %s with its second argument %j is built", (fn, second) => {
    const r = addNamedEdits(g, `${P}/op:g`, "agg", "Aggregate", "n", fn === "count" ? "" : "r.v", palette, { fn, second });
    expect(r.ok).toBe(true);
  });
});

describe("the new projection dialog", () => {
  const S = "d:sp/space:s";
  const base = build(ent("d:m", "Module"), ent("d:out", "Module"), ent("d:ops", "Module"), ent(S, "GraphSpace"), ent(`${S}/graph:master`, "Graph"), ent(`${S}/source:feed`, "Source"), ent("d:m/rows:Feed", "RowType"));
  const palette = parsePalette({ operators: [], declarations: [{ slot: "projection", type: "Projection", container: "Module", slots: [], params: [{ name: "purpose", kind: "Text", required: true, values: ["view", "ingestion"] }] }], valueKinds: ["Text", "Integer", "Decimal", "Boolean"] });
  const spec = { module: "m", name: "p", description: "", outputModule: "d:out", graphName: "p", inputs: [{ port: "master", graph: "master", ontology: "d:ops" }], params: [] as { port: string; kind: string; value: string; default?: string }[] };
  const rels = (r: { ok: boolean; value?: Edit[] }) => (r.value ?? []).filter((e) => e.kind === "relationship").map((e) => (e as { id: string }).id.replace(/d:m\/projection:p/g, "P").replace(/d:sp\/space:s/g, "S"));
  it("a rows input has a row type and a binding from a source", () => {
    const r = newProjectionEdits(base, { ...spec, rows: [{ port: "feed", rowType: "d:m/rows:Feed", source: `${S}/source:feed` }] }, palette);
    expect(rels(r)).toEqual(expect.arrayContaining(["P/port:feed|rowType|d:m/rows:Feed", "S/binding:p.feed|boundFrom|S/source:feed"]));
  });
  it.each([
    ["a default alone", { port: "n", kind: "Integer", value: "", default: "0" }, undefined, { kind: "integer", value: "0" }],
    ["a default and a bound value", { port: "n", kind: "Integer", value: "1", default: "0" }, { kind: "integer", value: "1" }, { kind: "integer", value: "0" }],
  ])("a parameter with %s", (_, p, bound, def) => {
    const r = newProjectionEdits(base, { ...spec, params: [p] }, palette);
    const port = (r.ok ? r.value : []).find((e) => e.kind === "entity" && e.id.endsWith("port:n")) as Extract<Edit, { kind: "entity" }>;
    const binding = (r.ok ? r.value : []).find((e) => e.kind === "entity" && e.id.endsWith("binding:p.n")) as Extract<Edit, { kind: "entity" }> | undefined;
    expect([port.after!.props.default, binding?.after!.props.value]).toEqual([def, bound]);
  });
  it.each([["neither a value nor a default", { port: "n", kind: "Integer", value: "" }], ["a default of the wrong kind", { port: "n", kind: "Integer", value: "", default: "x" }]])("refuses %s", (_, p) => expect(newProjectionEdits(base, { ...spec, params: [p] }, palette).ok).toBe(false));
  it.each([["ingestion", true], ["view", true], ["", true], ["dream", false]])("purpose %j is accepted: %s", (purpose, ok) => expect(newProjectionEdits(base, { ...spec, purpose }, palette).ok).toBe(ok));
  it("retained revisions are an integer property of the projection", () => {
    const r = newProjectionEdits(base, { ...spec, retain: "4" }, palette);
    expect(r.ok && E(r.value[0]).after!.props.retain).toEqual({ kind: "integer", value: "4" });
  });
});

describe("aggregates per context, from the palette's declaration", () => {
  const ctx = (operator: string, requires: string | null = null) => ({ operator, inputs: "", output: "", requires });
  const palette = parsePalette({
    operators: [{ kind: "Rollup", supported: true, role: "telemetry", container: "", slot: "op", ports: [], strategy: null, sink: false, telemetry: true, params: [], slots: [{ name: "agg", children: ["Aggregate"], multiplicity: "+", ordered: false }] }],
    aggregates: [
      { name: "count", supported: true, contexts: [ctx("Group"), ctx("Rollup")] }, { name: "last", supported: true, contexts: [ctx("Group")] },
      { name: "mean", supported: true, contexts: [ctx("Group"), ctx("Rollup", "gauge")] }, { name: "future", supported: false, contexts: [] },
      { name: "old", supported: true },
    ],
  });
  it.each([
    ["count", "Group", true], ["count", "Rollup", true], ["last", "Group", true], ["last", "Rollup", false], ["mean", "Rollup", true],
    ["future", "Group", false], ["nothing", "Group", false], ["old", "Rollup", true],
  ])("%s in a %s: %s", (aggregate, operator, allowed) => expect(aggregateAllowed(palette, aggregate, operator).ok).toBe(allowed));
  it("says what the context requires, and where an aggregate is allowed instead", () => {
    expect(aggregateAllowed(palette, "mean", "Rollup")).toEqual({ ok: true, requires: "gauge" });
    expect(aggregateAllowed(palette, "last", "Rollup")).toEqual({ ok: false, reason: "last is allowed only in Group" });
  });
  it("an aggregate outside its context is refused when it is added, before the engine is asked", () => {
    const g = build(ent(P, "Projection"), ent(`${P}/op:r`, "Rollup", { name: text("r") }), contains(P, `${P}/op:r`, "op"));
    expect(addNamedEdits(g, `${P}/op:r`, "agg", "Aggregate", "l", "x", palette, { fn: "last", second: "t" })).toEqual({ ok: false, reason: "last is allowed only in Group" });
    expect(addNamedEdits(g, `${P}/op:r`, "agg", "Aggregate", "m", "x", palette, { fn: "mean" }).ok).toBe(true);
  });
});

describe("durations in parameters", () => {
  const age = (unit?: string, quantity?: string) => ({ name: "age", kind: "Integer", required: true, ...(unit ? { unit } : {}), ...(quantity ? { quantity } : {}) });
  it.each([["24h", "86400000000000"], ["90s", "90000000000"], ["500ms", "500000000"], ["1.5m", "90000000000"], ["86400000000000", "86400000000000"]])("%s with the unit ns is %s", (raw, ns) => {
    const v = paramValue(age("ns"), raw);
    expect(v.ok && v.value).toEqual({ kind: "integer", value: ns });
  });
  it.each([["24h", "86400000000000"], ["5m", "300000000000"]])("a parameter the palette declares a duration takes %s (engine 3.6, Retain.age)", (raw, ns) => {
    const v = paramValue(age("ns", "duration"), raw);
    expect(v.ok && v.value).toEqual({ kind: "integer", value: ns });
  });
  it.each([["86400000000000", "24h"], ["90000000000", "90s"], ["60000000000", "1m"], ["1500000", "1500000"], ["1000000", "1ms"], ["7", "7"], ["0", "0"], ["x", "x"]])("%s nanoseconds are shown as %s", (ns, shown) => expect(durationText(ns)).toBe(shown));
  it("a parameter the palette gives no unit takes the number only, as Retain.age does until the palette declares one", () => {
    expect(paramValue(age(), "24h").ok).toBe(false);
    expect(paramValue(age(), "86400000000000").ok).toBe(true);
  });
});

describe("a lift built from the palette's own description", () => {
  const palette = parsePalette(enginePalette);
  const LIFT = `${P}/op:l`;
  const g = () => build(ent(P, "Projection"), ...op("l", "Lift", [["edges", "in"], ["map", "in"], ["out", "out"]]), ent(LIFT, "Lift", { name: text("l"), within: { kind: "integer", value: "86400000000000" }, selfLoops: text("drop") }));
  const ids = (r: { ok: boolean; value?: Edit[] }) => (r.value ?? []).map((e) => (e as { id: string }).id.split("|")[0]);

  it("the palette gives within a default of 24h and no default to images", () => {
    const l = palette.operators.find((o) => o.kind === "Lift")!;
    expect([l.role, l.params.find((x) => x.name === "within")?.default, l.params.find((x) => x.name === "images")?.default, l.params.find((x) => x.name === "selfLoops")?.default]).toEqual(["lift", "86400000000000", undefined, "drop"]);
    expect(l.slots.find((x) => x.name === "state")?.childTypes?.[0].params.map((x) => x.name)).toEqual(["target", "of", "above", "enterAfter", "high", "below", "exitAfter", "low", "initial"]);
  });

  it.each([
    ["carry takes a lower property", "carry", "callFlows", "flows", true],
    ["carry refuses an expression", "carry", "x", "flows + 1", false],
    ["keep takes src. or dst. of the image", "keep", "cluster", "src.cluster", true],
    ["keep refuses a field that does not start with src or dst", "keep", "cluster", "cluster", false],
    ["derive takes an expression", "derive", "ratio", "if(flows > 0, drops / flows, 0.0)", true],
  ])("%s", (_, slot, name, src, accepted) => {
    const r = addNamedEdits(g(), LIFT, slot, "Assign", name, src, palette);
    expect(r.ok).toBe(accepted);
    if (r.ok) expect(ids(r)).toContain(`${LIFT}/${slot}:${name}`);
  });

  it("a state is a LiftState node with a property for every parameter, typed by the palette", () => {
    const r = addParamChildEdits(g(), LIFT, "state", "LiftState", "status", { of: "dropRatio", above: "0.05", enterAfter: "3", high: "degraded", below: "0.02", exitAfter: "3", low: "ok", initial: "ok" }, palette);
    expect(r.ok).toBe(true);
    const st = r.ok ? (r.value.find((e) => e.kind === "entity") as Extract<Edit, { kind: "entity" }>).after! : null;
    expect([st?.type, st?.id, st?.props.target, st?.props.above, st?.props.enterAfter]).toEqual(["LiftState", `${LIFT}/state:status`, text("status"), { kind: "decimal", value: "0.05" }, { kind: "integer", value: "3" }]);
  });
  it.each([
    ["a missing parameter", { of: "dropRatio" }, /above is needed/],
    ["a measure that is not a name is still text, a number must be one", { of: "x", above: "high", enterAfter: "3", high: "a", below: "1", exitAfter: "3", low: "b", initial: "c" }, /above must be a number/],
  ])("a state with %s is refused", (_, values, why) => {
    const r = addParamChildEdits(g(), LIFT, "state", "LiftState", "s", values, palette);
    expect(!r.ok && r.reason).toMatch(why);
  });

  it("parameters of a child are found by the node type, and an emptied optional parameter is removed", () => {
    expect(paramOf(palette, "LiftState", "enterAfter")?.quantity).toBe("count");
    expect(paramOf(palette, "Lift", "nothing")).toBeUndefined();
    const withImages = build(ent(P, "Projection"), ent(LIFT, "Lift", { name: text("l"), images: text("each") }));
    const images = paramOf(palette, "Lift", "images")!;
    const cleared = paramEdits(withImages, LIFT, images, "");
    expect(cleared.ok && (cleared.value[0] as Extract<Edit, { kind: "entity" }>).after!.props.images).toBeUndefined();
    const within = paramOf(palette, "Lift", "within")!;
    expect(paramEdits(withImages, LIFT, within, "").ok).toBe(false);
  });

  it("a lift says what it still needs: its three relationship types and its two graph inputs", () => {
    const missing = incompleteOperators(g(), palette, P)[0]?.missing ?? [];
    expect(missing).toEqual(expect.arrayContaining(["relationship type lifted to", "lower relationship type", "mapping relationship type", "input edges is not connected", "input map is not connected"]));
  });

  it("the card of a lift names the relationship types and shows only what differs from the defaults", () => {
    const base = g();
    const withTypes = build(ent(P, "Projection"), ent(LIFT, "Lift", { name: text("l"), within: { kind: "integer", value: "3600000000000" }, images: text("each"), selfLoops: text("drop") }), contains(P, LIFT, "op"),
      ent("d:m/rel:Up", "RelationshipType", { name: text("Up") }), ent("d:m/rel:Calls", "RelationshipType", { name: text("Calls") }), ent("d:m/rel:Backs", "RelationshipType", { name: text("Backs") }),
      rel(LIFT, "ofType", "d:m/rel:Up"), rel(LIFT, "liftEdge", "d:m/rel:Calls"), rel(LIFT, "liftVia", "d:m/rel:Backs"));
    void base;
    expect(new Catalog(withTypes).describeOp(LIFT)).toEqual(["Up from Calls via Backs", "images each, within 1h"]);
  });
});

describe("the first rule of a forward projection", () => {
  it.each([
    ["makes it a projection with rules (the engine refuses rules on a forward one)", true, 3],
    ["leaves a projection that is not forward alone", false, 2],
  ])("%s", (_, forward, edits) => {
    const g = build(ent(P, "Projection", { name: text("p"), forward: { kind: "boolean", value: forward } }));
    const r = addRuleEdits(g, P, "r", "lens", "", []);
    expect(r.ok && r.value.length).toBe(edits);
    if (r.ok && forward) expect((r.value[0] as Extract<Edit, { kind: "entity" }>).after!.props.forward).toEqual({ kind: "boolean", value: false });
  });
});

describe("identity domains are resolved the way the module's text resolves them", () => {
  const domain = (module: string, name: string) => [ent(`d:${module}/identity:${name}`, "IdentityDomain", { name: text(name) }), ent(`d:${module}/identity:${name}/component:component`, "IdentityComponent", {})];
  const imports = (from: string, to: string, alias: string): Edit => ({ kind: "relationship", id: `${from}|imports|${to}@1.0.0`, before: null, after: { id: `${from}|imports|${to}@1.0.0`, source: from, target: `${to}@1.0.0`, type: "imports", props: { alias: text(alias) } } });
  const OP = "d:app/projection:p/op:e";
  const g = () => build(
    ...domain("acme.health", "signal"), ...domain("acme.tree", "signal"), ...domain("app", "group"), ...domain("acme.health", "group"),
    ent("d:app", "Module"), ent("d:app/projection:p", "Projection"), ent(OP, "EmitEntity"),
    imports("d:app", "d:acme.health", "health"), imports("d:app", "d:acme.tree", "tree"),
  );
  const keyOf = (src: string) => { const r = addNamedEdits(g(), OP, "set", "Assign", "x", src, parsePalette({ operators: [], functions: [] })); return r.ok ? (JSON.stringify(r.value).match(/d:[\w.]+\/identity:\w+/)?.[0] ?? "no domain") : r.reason; };
  it.each([
    ["an alias picks the module it imports under, though two modules have a domain of that name", 'health.signal(component: "a")', "d:acme.health/identity:signal"],
    ["another alias picks the other one", 'tree.signal(component: "a")', "d:acme.tree/identity:signal"],
    ["a bare name is the module's own domain first", 'group(component: "a")', "d:app/identity:group"],
  ])("%s", (_, src, want) => expect(keyOf(src)).toContain(want));
  it("a bare name two other modules share is not guessed", () => expect(keyOf('signal(component: "a")')).toMatch(/unknown identity domain/));
});
