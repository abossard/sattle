// Text forms for configuring operators. Each field formats a value to text and parses edited text back, with an error on bad input.
import type { Assignment, Expression, Field, FieldType, GraphPattern, Operator, PropertyAssignment } from "../wire-types";
import { formatExpr, parseExpr } from "./describe";

export type Applied = { ok: true; op: Operator } | { ok: false; error: string };
export interface FormField {
  key: string;
  label: string;
  kind: "text" | "area" | "select" | "number";
  options?: string[];
  hint?: string;
  value: string;
  apply(text: string): Applied;
}
export interface FormContext { bindings: string[]; entityTypes: string[]; relationshipTypes: string[] }

const id = (value: string) => ({ value });
const lines = (t: string): string[] => t.split("\n").map((l) => l.trim()).filter(Boolean);
const FIELD_TYPES: FieldType[] = ["text", "integer", "decimal", "boolean", "instant", "entity", "relationship"];

type R<T> = { ok: true; value: T } | { ok: false; error: string };
const fail = <T,>(error: string): R<T> => ({ ok: false, error });

export function parseAssignments(text: string): R<Assignment[]> {
  const out: Assignment[] = [];
  for (const l of lines(text)) {
    const m = /^([A-Za-z_][\w.\-]*)\s*=\s*(.+)$/.exec(l);
    if (!m) return fail(`expected "name = expression", got "${l}"`);
    const e = parseExpr(m[2]);
    if (!e.ok) return fail(`${m[1]}: ${e.error}`);
    out.push({ field: m[1], value: e.value });
  }
  return { ok: true, value: out };
}
export const formatAssignments = (a: Assignment[]): string => a.map((x) => `${x.field} = ${formatExpr(x.value)}`).join("\n");

export function parsePropertyAssignments(text: string): R<PropertyAssignment[]> {
  const r = parseAssignments(text);
  return r.ok ? { ok: true, value: r.value.map((a) => ({ property: id(a.field), value: a.value })) } : r;
}
const formatProps = (a: PropertyAssignment[]): string => a.map((x) => `${x.property.value} = ${formatExpr(x.value)}`).join("\n");

export function parseRow(text: string): R<Field[]> {
  const out: Field[] = [];
  for (const l of lines(text)) {
    const m = /^([A-Za-z_][\w.\-]*)\s*:\s*(\w+)(\?)?$/.exec(l);
    if (!m) return fail(`expected "name: type" (add ? for optional), got "${l}"`);
    if (!FIELD_TYPES.includes(m[2] as FieldType)) return fail(`unknown type "${m[2]}"; use ${FIELD_TYPES.join(", ")}`);
    out.push({ name: m[1], type: m[2] as FieldType, optional: !!m[3] });
  }
  return { ok: true, value: out };
}
const formatRow = (f: Field[]): string => f.map((x) => `${x.name}: ${x.type}${x.optional ? "?" : ""}`).join("\n");

export function parsePattern(nodesText: string, edgesText: string): R<GraphPattern> {
  const nodes = [], edges = [];
  for (const l of lines(nodesText)) {
    const m = /^([A-Za-z_]\w*)\s*:\s*([\w.\-]+)$/.exec(l);
    if (!m) return fail(`expected "variable: Type", got "${l}"`);
    nodes.push({ variable: m[1], type: id(m[2]) });
  }
  for (const l of lines(edgesText)) {
    const m = /^([A-Za-z_]\w*)\s*:\s*([A-Za-z_]\w*)\s*-\s*([\w.\-]+)\s*->\s*([A-Za-z_]\w*)$/.exec(l);
    if (!m) return fail(`expected "edge: source -Type-> target", got "${l}"`);
    edges.push({ variable: m[1], source: m[2], target: m[4], type: id(m[3]) });
  }
  return { ok: true, value: { nodes, edges } };
}
const formatNodes = (p: GraphPattern): string => p.nodes.map((n) => `${n.variable}: ${n.type.value}`).join("\n");
const formatEdges = (p: GraphPattern): string => p.edges.map((e) => `${e.variable}: ${e.source} -${e.type.value}-> ${e.target}`).join("\n");

const AGG = ["countDistinct", "sum", "min", "max", "any", "all"] as const;
export function parseAggregates(text: string): R<{ name: string; function: (typeof AGG)[number]; value: Expression }[]> {
  const out = [];
  for (const l of lines(text)) {
    const m = /^([A-Za-z_][\w.\-]*)\s*=\s*(\w+)\((.*)\)$/.exec(l);
    if (!m || !(AGG as readonly string[]).includes(m[2])) return fail(`expected "name = ${AGG.join("|")}(expression)", got "${l}"`);
    const e = parseExpr(m[3]);
    if (!e.ok) return fail(`${m[1]}: ${e.error}`);
    out.push({ name: m[1], function: m[2] as (typeof AGG)[number], value: e.value });
  }
  return { ok: true, value: out };
}

export function formFor(op: Operator, ctx: FormContext): FormField[] {
  const f = (key: string, label: string, kind: FormField["kind"], value: string, apply: (t: string) => Applied, extra: Partial<FormField> = {}): FormField => ({ key, label, kind, value, apply, ...extra });
  const expr = (key: string, label: string, get: Expression, set: (e: Expression) => Operator): FormField =>
    f(key, label, "text", formatExpr(get), (t) => { const e = parseExpr(t); return e.ok ? { ok: true, op: set(e.value) } : { ok: false, error: e.error }; }, { hint: 'e.g. equal(health, "ok"). Fields bare, text in quotes, calls as fn(a, b).' });
  const num = (key: string, label: string, v: number, set: (n: number) => Operator): FormField =>
    f(key, label, "number", String(v), (t) => (Number.isFinite(Number(t)) && t.trim() !== "" ? { ok: true, op: set(Number(t)) } : { ok: false, error: "expected a number" }));
  const sel = (key: string, label: string, v: string, options: string[], set: (s: string) => Operator): FormField => f(key, label, "select", v, (t) => ({ ok: true, op: set(t) }), { options });
  const txt = (key: string, label: string, v: string, set: (s: string) => Operator, hint?: string, options?: string[]): FormField =>
    f(key, label, options ? "select" : "text", v, (t) => ({ ok: true, op: set(t.trim()) }), { hint, options });
  const bindingField = (cur: string, set: (s: string) => Operator) => sel("binding", "Source binding", cur, ["", ...ctx.bindings], set);

  switch (op.kind) {
    case "input": return [
      bindingField(op.binding, (s) => ({ ...op, binding: s })),
      f("row", "Row fields", "area", formatRow(op.row.fields), (t) => { const r = parseRow(t); return r.ok ? { ok: true, op: { ...op, row: { fields: r.value } } } : { ok: false, error: r.error }; }, { hint: "one per line: name: type, type? for optional" }),
    ];
    case "match": return [
      bindingField(op.binding, (s) => ({ ...op, binding: s })),
      f("nodes", "Node patterns", "area", formatNodes(op.pattern), (t) => { const r = parsePattern(t, formatEdges(op.pattern)); return r.ok ? { ok: true, op: { ...op, pattern: r.value } } : { ok: false, error: r.error }; }, { hint: "variable: Type" }),
      f("edges", "Edge patterns", "area", formatEdges(op.pattern), (t) => { const r = parsePattern(formatNodes(op.pattern), t); return r.ok ? { ok: true, op: { ...op, pattern: r.value } } : { ok: false, error: r.error }; }, { hint: "edge: source -Type-> target" }),
    ];
    case "filter": return [expr("predicate", "Predicate", op.predicate, (e) => ({ ...op, predicate: e }))];
    case "map": return [f("fields", "Fields", "area", formatAssignments(op.fields), (t) => { const r = parseAssignments(t); return r.ok ? { ok: true, op: { ...op, fields: r.value } } : { ok: false, error: r.error }; }, { hint: "name = expression, one per line" })];
    case "join": return [expr("on", "Join condition", op.on, (e) => ({ ...op, on: e }))];
    case "union": return [sel("conflict", "Conflict policy", op.conflict, ["requireEqual", "preferFirstPort", "preferLastPort", "preserveEvidence"], (s) => ({ ...op, conflict: s as typeof op.conflict }))];
    case "distinct": return [];
    case "group": return [
      f("keys", "Keys", "area", formatAssignments(op.keys), (t) => { const r = parseAssignments(t); return r.ok ? { ok: true, op: { ...op, keys: r.value } } : { ok: false, error: r.error }; }, { hint: "name = expression" }),
      f("aggregates", "Aggregates", "area", op.aggregates.map((a) => `${a.name} = ${a.function}(${formatExpr(a.value)})`).join("\n"), (t) => { const r = parseAggregates(t); return r.ok ? { ok: true, op: { ...op, aggregates: r.value } } : { ok: false, error: r.error }; }, { hint: "name = sum(expression)" }),
    ];
    case "traverse": return [
      txt("relationship", "Relationship type", op.relationship.value, (s) => ({ ...op, relationship: id(s) }), undefined, ["", ...ctx.relationshipTypes]),
      sel("direction", "Direction", op.direction, ["outgoing", "incoming", "either"], (s) => ({ ...op, direction: s as typeof op.direction })),
      num("minimumDepth", "Minimum depth", op.minimumDepth, (n) => ({ ...op, minimumDepth: n })), num("maximumDepth", "Maximum depth", op.maximumDepth, (n) => ({ ...op, maximumDepth: n })),
    ];
    case "window": return [
      txt("timestampField", "Timestamp field", op.timestampField, (s) => ({ ...op, timestampField: s })),
      num("widthNanoseconds", "Width (ns)", op.widthNanoseconds, (n) => ({ ...op, widthNanoseconds: n })),
      sel("latePolicy", "Late policy", op.latePolicy, ["ignore", "revise", "reject"], (s) => ({ ...op, latePolicy: s as typeof op.latePolicy })),
    ];
    case "emitEntity": return [
      txt("type", "Entity type", op.type.value, (s) => ({ ...op, type: id(s) }), "from the output ontology", ["", ...ctx.entityTypes]),
      txt("domain", "Identity domain", op.identity.domain.value, (s) => ({ ...op, identity: { ...op.identity, domain: id(s) } })),
      f("components", "Identity components", "area", formatAssignments(op.identity.components), (t) => { const r = parseAssignments(t); return r.ok ? { ok: true, op: { ...op, identity: { ...op.identity, components: r.value } } } : { ok: false, error: r.error }; }, { hint: "name = expression" }),
      f("properties", "Properties", "area", formatProps(op.properties), (t) => { const r = parsePropertyAssignments(t); return r.ok ? { ok: true, op: { ...op, properties: r.value } } : { ok: false, error: r.error }; }, { hint: "property = expression" }),
    ];
    case "emitRelationship": return [
      txt("type", "Relationship type", op.type.value, (s) => ({ ...op, type: id(s) }), "from the output ontology", ["", ...ctx.relationshipTypes]),
      expr("source", "Source entity", op.source, (e) => ({ ...op, source: e })), expr("target", "Target entity", op.target, (e) => ({ ...op, target: e })),
      txt("identityRule", "Identity rule", op.identityRule, (s) => ({ ...op, identityRule: s })),
      sel("retypeCollision", "Retype collision", op.retypeCollision, ["requireEqual", "preferFirstPort", "preferLastPort", "preserveEvidence"], (s) => ({ ...op, retypeCollision: s as typeof op.retypeCollision })),
      f("properties", "Properties", "area", formatProps(op.properties), (t) => { const r = parsePropertyAssignments(t); return r.ok ? { ok: true, op: { ...op, properties: r.value } } : { ok: false, error: r.error }; }, { hint: "property = expression" }),
    ];
    case "fixpoint": return [
      txt("relation", "Relation", op.relation, (s) => ({ ...op, relation: s })),
      bindingField(op.finiteDomainBinding, (s) => ({ ...op, finiteDomainBinding: s })),
      txt("result", "Result operator", op.result.value, (s) => ({ ...op, result: id(s) }), `body operators: ${op.body.map((b) => b.id.value).join(", ") || "none"} (the body is shown, not edited here)`),
      num("maximumFacts", "Maximum facts", op.maximumFacts, (n) => ({ ...op, maximumFacts: n })), num("maximumIterations", "Maximum iterations", op.maximumIterations, (n) => ({ ...op, maximumIterations: n })),
    ];
  }
}
