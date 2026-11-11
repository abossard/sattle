import { describe, expect, it } from "vitest";
import projectionFixture from "../../brainstorm/contracts/examples/graph-projection.json";
import ontologyFixture from "../../brainstorm/contracts/examples/ontology.json";
import { checkDefinition, checkNewWire, type Diagnostic } from "../src/core/projection/typecheck";
import { addOperator, addWire, defaultOperator, removeOperator, removeWire, replaceOperator } from "../src/core/projection/edit";
import { formatExpr, parseExpr } from "../src/core/projection/describe";
import { buildDefGraph, definitionGraph, directionOf } from "../src/core/projection/defgraph";
import { diffGraphs } from "../src/core/diff";
import { checkProjection } from "../src/core/validate";
import type { OntologyDefinition, ProjectionDefinition } from "../src/core/wire-types";

const proj = () => JSON.parse(JSON.stringify(projectionFixture)) as ProjectionDefinition;
const onto = () => JSON.parse(JSON.stringify(ontologyFixture)) as OntologyDefinition;
const clone = <T>(x: T): T => JSON.parse(JSON.stringify(x));
const messages = (d: Diagnostic[], sev: "error" | "warning" = "error") => d.filter((x) => x.severity === sev).map((x) => x.message);

describe("checkDefinition on the repo fixture", () => {
  it("accepts graph-projection.json against ontology.json", () => {
    expect(messages(checkDefinition(proj(), { output: onto() }).diagnostics)).toEqual([]);
  });

  const mutate = (f: (p: ProjectionDefinition) => void) => { const p = clone(proj()); f(p); return p; };
  it.each([
    ["unknown property", (p: ProjectionDefinition) => { (p.operators[1] as any).properties[0].property.value = "madeUp"; }, "madeUp"],
    ["property of wrong value kind", (p: ProjectionDefinition) => { (p.operators[1] as any).properties[0].value = { kind: "literal", value: { kind: "boolean", value: true } }; }, "is text, the assigned value is boolean"],
    ["missing input field", (p: ProjectionDefinition) => { (p.operators[1] as any).identity.components[0].value.field = "missing"; }, 'unknown field "missing"'],
    ["unknown entity type", (p: ProjectionDefinition) => { (p.operators[1] as any).type.value = "Nope"; }, 'entity type "Nope"'],
    ["wire from nonexistent operator", (p: ProjectionDefinition) => { p.wires[0].from.value = "ghost"; }, 'operator "ghost" does not exist'],
    ["missing wire", (p: ProjectionDefinition) => { p.wires = []; }, 'input port "in" is not connected'],
    ["wrong input port", (p: ProjectionDefinition) => { p.wires[0].input = "left"; }, 'no input port "left"'],
    ["wrong output port", (p: ProjectionDefinition) => { p.wires[0].output = "result"; }, 'no output port "result"'],
    ["ontology pin mismatch", (p: ProjectionDefinition) => { p.ontology.version = "2"; }, "does not match"],
    ["undeclared binding", (p: ProjectionDefinition) => { p.inputs = []; }, "not declared"],
    ["operator cycle", (p: ProjectionDefinition) => { p.operators.push(defaultOperator("distinct", "d1")); p.wires.push({ from: { value: "d1" }, output: "out", to: { value: "d1" }, input: "in" }); }, "cycle"],
  ])("rejects %s", (_n, f, expected) => {
    expect(messages(checkDefinition(mutate(f), { output: onto() }).diagnostics).join("\n")).toContain(expected);
  });

  it("puts errors at the offending operator or wire", () => {
    const p = mutate((x) => { (x.operators[1] as any).identity.components[0].value.field = "missing"; x.wires[0].output = "zzz"; });
    const targets = checkDefinition(p, { output: onto() }).diagnostics.filter((d) => d.severity === "error").map((d) => d.target);
    expect(targets).toContainEqual({ kind: "operator", id: "emit" });
    expect(targets).toContainEqual({ kind: "wire", key: "input.zzz>emit.in" });
  });
});

describe("expressions", () => {
  const withFilter = (predicate: any): ProjectionDefinition => {
    let p = proj();
    p = addOperator(p, { ...defaultOperator("filter", "f"), predicate } as any);
    p = addWire(p, { from: { value: "input" }, output: "out", to: { value: "f" }, input: "in" });
    return p;
  };
  const f = (n: string) => ({ kind: "field", field: n });
  const t = (v: string) => ({ kind: "literal", value: { kind: "text", value: v } });
  const call = (fn: string, ...a: any[]) => ({ kind: "call", function: fn, arguments: a });
  it.each([
    ["boolean equal", call("equal", f("health"), t("ok")), true],
    ["non-boolean predicate", f("health"), false],
    ["comparison of mismatched types", call("equal", f("health"), { kind: "literal", value: { kind: "integer", value: "1" } }), false],
    ["arithmetic on text", call("add", f("name"), f("health")), false],
    ["and of non-booleans", call("and", f("name"), t("x")), false],
    ["unknown field", call("equal", f("nope"), t("x")), false],
    ["wrong arity", call("not", f("a"), f("b")), false],
  ])("filter %s", (_n, predicate, ok) => {
    const errors = messages(checkDefinition(withFilter(predicate), { output: onto() }).diagnostics).filter((m) => !m.includes("not connected"));
    expect(errors.length === 0).toBe(ok);
  });

  it.each([
    ['equal(health, "ok")'], ['and(equal(a, 1), not(b))'], ['concat("x", name, "y")'], ["lessThan(count, 1.5)"], ["coalesce(a, null)"], ['equal(p.name, "a b")'],
  ])("parseExpr round-trips %s", (src) => {
    const r = parseExpr(src);
    expect(r.ok).toBe(true);
    if (r.ok) expect(formatExpr(r.value)).toBe(src);
  });
  it.each([["equal("], ["equal(a b)"], ["unknownFn(a)"], ['"open'], ["a b"], [""]])("parseExpr rejects %j", (src) => {
    expect(parseExpr(src).ok).toBe(false);
  });
});

describe("union and join rows", () => {
  it("reports a row type mismatch on the wire", () => {
    let p = proj();
    p = addOperator(p, { ...defaultOperator("input", "other"), binding: "observations", row: { fields: [{ name: "uid", type: "integer", optional: false }] } } as any);
    p = addOperator(p, defaultOperator("union", "u"));
    p = addWire(p, { from: { value: "input" }, output: "out", to: { value: "u" }, input: "left" });
    p = addWire(p, { from: { value: "other" }, output: "out", to: { value: "u" }, input: "right" });
    const d = checkDefinition(p, { output: onto() }).diagnostics.filter((x) => x.target.kind === "wire");
    expect(d.map((x) => x.message).join()).toContain("row type differs");
    expect(d[0].target).toEqual({ kind: "wire", key: "other.out>u.right" });
  });
});

describe("edit operations", () => {
  it("removeOperator drops its wires and outputs", () => {
    const p = removeOperator(proj(), "emit");
    expect(p.wires).toEqual([]);
    expect(p.outputs).toEqual([]);
  });
  it("replaceOperator renames references", () => {
    const p = replaceOperator(proj(), "emit", { ...proj().operators[1], id: { value: "emit2" } } as any);
    expect(p.wires[0].to.value).toBe("emit2");
    expect(p.outputs[0].value).toBe("emit2");
  });
  it("addWire/removeWire", () => {
    const p = removeWire(proj(), "input.out>emit.in");
    expect(p.wires).toEqual([]);
    expect(addWire(p, { from: { value: "input" }, output: "out", to: { value: "emit" }, input: "in" }).wires.length).toBe(1);
  });
  it.each([
    ["valid wire", { from: { value: "input" }, output: "out", to: { value: "f" }, input: "in" }, 0],
    ["wire into an input operator", { from: { value: "input" }, output: "out", to: { value: "input" }, input: "in" }, 1],
    ["wire out of an emitter", { from: { value: "emit" }, output: "out", to: { value: "f" }, input: "in" }, 1],
    ["duplicate wire", { from: { value: "input" }, output: "out", to: { value: "emit" }, input: "in" }, 1],
  ])("checkNewWire %s", (_n, wire, errors) => {
    const p = addOperator(proj(), defaultOperator("filter", "f"));
    expect(checkNewWire(p, wire, { output: onto() }).length).toBe(errors);
  });
  it("every edit result stays schema valid", () => {
    let p = addOperator(proj(), defaultOperator("filter", "f"));
    p = addWire(p, { from: { value: "input" }, output: "out", to: { value: "f" }, input: "in" });
    expect(checkProjection(p).ok).toBe(true);
  });
});

describe("definition graph and direction", () => {
  it("renders bindings, operators, ontology and wires", () => {
    const g = buildDefGraph(proj());
    expect(g.nodes.map((n) => n.id).sort()).toEqual(["in:observations", "ontology", "op:emit", "op:input"]);
    expect(g.edges.map((e) => e.kind).sort()).toEqual(["binds", "emits", "wire"]);
  });
  it("marks forward-only and bidirectional", () => {
    expect(directionOf(proj()).mode).toBe("forward-only");
    const p = proj();
    p.bidirectionalRules = [{ id: "r", sourcePattern: { nodes: [], edges: [] }, targetPattern: { nodes: [], edges: [] }, correspondences: [], forward: { operators: [], wires: [], outputs: [] }, backward: { operators: [], wires: [], outputs: [] }, requiredLaws: ["getPut"] }];
    expect(directionOf(p)).toMatchObject({ mode: "bidirectional", rules: 1 });
  });
  it("diffs two definition versions as graphs", () => {
    const a = proj();
    let b = addOperator(clone(a), defaultOperator("filter", "f"));
    b = addWire(b, { from: { value: "input" }, output: "out", to: { value: "f" }, input: "in" });
    (b.operators[1] as any).properties[1].value = { kind: "literal", value: { kind: "text", value: "x" } };
    const d = diffGraphs(definitionGraph(a), definitionGraph(b));
    expect([...d.entities].map(([k, v]) => `${k}:${v.kind}`).sort()).toEqual(["op:emit:changed", "op:f:added"]);
    expect([...d.relationships].map(([, v]) => v.kind)).toEqual(["added"]);
  });
});
