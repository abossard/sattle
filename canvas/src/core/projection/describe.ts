// Human-readable descriptions of operators and expressions. Pure.
import { valueText } from "../model";
import type { Expression, Operator, GraphPattern } from "../wire-types";

/** Expression text syntax: field names bare, "text" literals, 12 / 1.5 / true / null, calls as fn(a, b). Round-trips with parseExpr. */
export function formatExpr(e: Expression): string {
  switch (e.kind) {
    case "field": return e.field;
    case "literal": return e.value.kind === "text" ? JSON.stringify(e.value.value) : valueText(e.value);
    case "call": return `${e.function}(${e.arguments.map(formatExpr).join(", ")})`;
  }
}

const FUNCTIONS = new Set(["equal", "notEqual", "and", "or", "not", "add", "subtract", "multiply", "divide", "lessThan", "greaterThan", "concat", "coalesce", "lowerInvariant"]);

export function parseExpr(src: string): { ok: true; value: Expression } | { ok: false; error: string } {
  let i = 0;
  const ws = () => { while (i < src.length && /\s/.test(src[i])) i++; };
  const fail = (m: string): never => { throw new Error(`${m} at position ${i}`); };
  const expr = (): Expression => {
    ws();
    const c = src[i];
    if (c === undefined) return fail("expected an expression");
    if (c === '"') {
      let j = i + 1;
      while (j < src.length && (src[j] !== '"' || src[j - 1] === "\\")) j++;
      if (j >= src.length) return fail("unterminated string");
      const text = JSON.parse(src.slice(i, j + 1)) as string;
      i = j + 1;
      return { kind: "literal", value: { kind: "text", value: text } };
    }
    const m = /^-?\d+(\.\d+)?/.exec(src.slice(i));
    if (m) { i += m[0].length; return { kind: "literal", value: m[1] ? { kind: "decimal", value: m[0] } : { kind: "integer", value: m[0] } }; }
    const id = /^[A-Za-z_][A-Za-z0-9_.\-]*/.exec(src.slice(i));
    if (!id) return fail(`unexpected "${c}"`);
    i += id[0].length;
    const name = id[0];
    ws();
    if (src[i] === "(") {
      if (!FUNCTIONS.has(name)) return fail(`unknown function "${name}"`);
      i++;
      const args: Expression[] = [];
      ws();
      if (src[i] === ")") { i++; return { kind: "call", function: name as never, arguments: args }; }
      for (;;) {
        args.push(expr());
        ws();
        if (src[i] === ",") { i++; continue; }
        if (src[i] === ")") { i++; break; }
        return fail("expected , or )");
      }
      return { kind: "call", function: name as never, arguments: args };
    }
    if (name === "true" || name === "false") return { kind: "literal", value: { kind: "boolean", value: name === "true" } };
    if (name === "null") return { kind: "literal", value: { kind: "null" } };
    return { kind: "field", field: name };
  };
  try {
    const v = expr();
    ws();
    if (i < src.length) fail("unexpected trailing text");
    return { ok: true, value: v };
  } catch (e) {
    return { ok: false, error: (e as Error).message };
  }
}

const formatPattern = (p: GraphPattern): string[] => [
  ...p.nodes.map((n) => `${n.variable}: ${n.type.value}`),
  ...p.edges.map((x) => `${x.source} -[${x.variable}: ${x.type.value}]-> ${x.target}`),
];

export interface OperatorDescription { title: string; lines: string[]; props: Record<string, string> }

export function describeOperator(op: Operator): OperatorDescription {
  const id = op.id.value;
  switch (op.kind) {
    case "input": return {
      title: id, lines: [`binding ${op.binding}`, ...op.row.fields.map((f) => `${f.name}: ${f.type}${f.optional ? "?" : ""}`)],
      props: { binding: op.binding, row: op.row.fields.map((f) => `${f.name}:${f.type}`).join(", ") },
    };
    case "match": return { title: id, lines: [`binding ${op.binding}`, ...formatPattern(op.pattern)], props: { binding: op.binding, pattern: formatPattern(op.pattern).join("; ") } };
    case "filter": return { title: id, lines: [formatExpr(op.predicate)], props: { predicate: formatExpr(op.predicate) } };
    case "map": return { title: id, lines: op.fields.map((f) => `${f.field} = ${formatExpr(f.value)}`), props: { fields: op.fields.map((f) => `${f.field} = ${formatExpr(f.value)}`).join("; ") } };
    case "join": return { title: id, lines: [`on ${formatExpr(op.on)}`], props: { on: formatExpr(op.on) } };
    case "union": return { title: id, lines: [`conflict ${op.conflict}`], props: { conflict: op.conflict } };
    case "distinct": return { title: id, lines: [], props: {} };
    case "group": {
      const k = op.keys.map((f) => `${f.field} = ${formatExpr(f.value)}`);
      const a = op.aggregates.map((f) => `${f.name} = ${f.function}(${formatExpr(f.value)})`);
      return { title: id, lines: [...k.map((x) => `key ${x}`), ...a], props: { keys: k.join("; "), aggregates: a.join("; ") } };
    }
    case "traverse": return {
      title: id, lines: [`${op.relationship.value} ${op.direction}`, `depth ${op.minimumDepth}..${op.maximumDepth}`],
      props: { relationship: op.relationship.value, direction: op.direction, depth: `${op.minimumDepth}..${op.maximumDepth}` },
    };
    case "window": return {
      title: id, lines: [`on ${op.timestampField}`, `${op.widthNanoseconds} ns, late ${op.latePolicy}`],
      props: { timestampField: op.timestampField, widthNanoseconds: String(op.widthNanoseconds), latePolicy: op.latePolicy },
    };
    case "emitEntity": {
      const ps = op.properties.map((p) => `${p.property.value} = ${formatExpr(p.value)}`);
      const ident = op.identity.components.map((c) => `${c.field} = ${formatExpr(c.value)}`).join(", ");
      return {
        title: id, lines: [`type ${op.type.value}`, `identity ${op.identity.domain.value}(${ident})`, ...ps],
        props: { type: op.type.value, identity: `${op.identity.domain.value}(${ident})`, properties: ps.join("; ") },
      };
    }
    case "emitRelationship": {
      const ps = op.properties.map((p) => `${p.property.value} = ${formatExpr(p.value)}`);
      return {
        title: id, lines: [`type ${op.type.value}`, `${formatExpr(op.source)} → ${formatExpr(op.target)}`, ...ps],
        props: { type: op.type.value, source: formatExpr(op.source), target: formatExpr(op.target), identityRule: op.identityRule, retypeCollision: op.retypeCollision, properties: ps.join("; ") },
      };
    }
    case "fixpoint": return {
      title: id, lines: [`relation ${op.relation}`, `domain ${op.finiteDomainBinding}`, `${op.body.length} operators, result ${op.result.value}`, `max ${op.maximumFacts} facts, ${op.maximumIterations} iterations`],
      props: { relation: op.relation, finiteDomainBinding: op.finiteDomainBinding, result: op.result.value, body: op.body.map((o) => o.id.value).join(", "), maximumFacts: String(op.maximumFacts), maximumIterations: String(op.maximumIterations) },
    };
  }
}
