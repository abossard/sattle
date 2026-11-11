// Static checks of a ProjectionDefinition: wires, ports, row types, expressions, ontology references.
// Follows the reference validator brainstorm/contracts/tests/validate_schemas.py and extends it to all operator kinds.
// This is not the engine compiler; it never executes a projection.
import type { Expression, GraphPattern, OntologyDefinition, Operator, ProjectionDefinition, Wire } from "../wire-types";
import { portsOf, wireKey } from "./ports";

export type DiagnosticTarget =
  | { kind: "operator"; id: string }
  | { kind: "wire"; key: string }
  | { kind: "definition" };
export interface Diagnostic { severity: "error" | "warning"; target: DiagnosticTarget; message: string }

export type Row = Record<string, string>; // field name -> type ("text", "entity", ..., "unknown")
export interface RowInfo { row: Row; entityTypes: Record<string, string> }
export interface CheckResult { diagnostics: Diagnostic[]; rows: Map<string, RowInfo> }

/** marker field of rows whose property fields are unknown because the input ontology was not supplied */
const OPEN = "*";
const NUMERIC = new Set(["integer", "decimal"]);
const ORDERED = new Set(["integer", "decimal", "instant", "text"]);
const compatible = (a: string, b: string): boolean => a === "unknown" || b === "unknown" || a === b;

interface Ctx {
  row: Row;
  entityTypes: Record<string, string>;
  fail: (msg: string) => void;
}

/** Type of an expression against a row. Reports problems through ctx.fail and returns "unknown" after an error. */
export function inferType(e: Expression, ctx: Ctx): string {
  if (e.kind === "field") {
    const t = ctx.row[e.field];
    if (t === undefined && OPEN in ctx.row) return "unknown"; // row from a match without a known ontology
    if (t === undefined) { ctx.fail(`unknown field "${e.field}"; available: ${Object.keys(ctx.row).join(", ") || "none"}`); return "unknown"; }
    return t;
  }
  if (e.kind === "literal") return e.value.kind === "null" ? "unknown" : e.value.kind;
  const args = e.arguments.map((a) => inferType(a, ctx));
  const f = e.function;
  const arity = (min: number, max = min): boolean => {
    if (args.length < min || args.length > max) { ctx.fail(`${f} takes ${min === max ? min : `${min} to ${max}`} argument(s), got ${args.length}`); return false; }
    return true;
  };
  const same = (): string => {
    const known = args.filter((a) => a !== "unknown");
    if (known.some((a) => a !== known[0])) { ctx.fail(`${f} needs arguments of one type, got ${args.join(", ")}`); return "unknown"; }
    return known[0] ?? "unknown";
  };
  switch (f) {
    case "equal": case "notEqual": if (arity(2)) same(); return "boolean";
    case "and": case "or": if (arity(2, 8) && args.some((a) => !compatible(a, "boolean"))) ctx.fail(`${f} needs boolean arguments, got ${args.join(", ")}`); return "boolean";
    case "not": if (arity(1) && !compatible(args[0], "boolean")) ctx.fail(`not needs a boolean argument, got ${args[0]}`); return "boolean";
    case "add": case "subtract": case "multiply": case "divide": {
      if (!arity(2)) return "unknown";
      const t = same();
      if (t !== "unknown" && !NUMERIC.has(t)) { ctx.fail(`${f} needs numeric arguments, got ${t}`); return "unknown"; }
      return t;
    }
    case "lessThan": case "greaterThan": {
      if (arity(2)) { const t = same(); if (t !== "unknown" && !ORDERED.has(t)) ctx.fail(`${f} needs ordered arguments, got ${t}`); }
      return "boolean";
    }
    case "concat": if (arity(1, 8) && args.some((a) => !compatible(a, "text"))) ctx.fail(`concat needs text arguments, got ${args.join(", ")}`); return "text";
    case "coalesce": return arity(2, 8) ? same() : "unknown";
    case "lowerInvariant": if (arity(1) && !compatible(args[0], "text")) ctx.fail(`lowerInvariant needs a text argument, got ${args[0]}`); return "text";
  }
}

const ctxOf = (row: Row, entityTypes: Record<string, string>, fail: (m: string) => void): Ctx => ({ row, entityTypes, fail });

export interface Ontologies {
  /** ontology of the projection output; used for emit operators and the pin check */
  output?: OntologyDefinition;
  /** ontologies per input binding name; used by match operators */
  inputs?: Record<string, OntologyDefinition>;
}

export function checkDefinition(def: ProjectionDefinition, ont: Ontologies = {}): CheckResult {
  const diagnostics: Diagnostic[] = [];
  const err = (target: DiagnosticTarget, message: string) => diagnostics.push({ severity: "error", target, message });
  const warn = (target: DiagnosticTarget, message: string) => diagnostics.push({ severity: "warning", target, message });

  // ---- definition level ----
  if (ont.output) {
    const r = ont.output.reference;
    if (r.id !== def.ontology.id || r.version !== def.ontology.version || r.digest.value !== def.ontology.digest.value) {
      err({ kind: "definition" }, `ontology pin ${def.ontology.id}@${def.ontology.version} does not match the supplied ontology ${r.id}@${r.version}`);
    }
  }
  const ops = new Map<string, Operator>();
  for (const op of def.operators) {
    if (ops.has(op.id.value)) err({ kind: "operator", id: op.id.value }, `duplicate operator id "${op.id.value}"`);
    ops.set(op.id.value, op);
  }
  const bindings = new Set(def.inputs.map((i) => i.name));
  for (const o of def.outputs) if (!ops.has(o.value)) err({ kind: "definition" }, `output "${o.value}" is not an operator`);

  // ---- wires ----
  const incoming = new Map<string, { wire: Wire; key: string }[]>();
  for (const op of def.operators) incoming.set(op.id.value, []);
  const seenKeys = new Set<string>();
  for (const w of def.wires) {
    const key = wireKey(w);
    const target: DiagnosticTarget = { kind: "wire", key };
    const a = ops.get(w.from.value), b = ops.get(w.to.value);
    if (seenKeys.has(key)) err(target, "duplicate wire");
    seenKeys.add(key);
    if (!a) { err(target, `source operator "${w.from.value}" does not exist`); continue; }
    if (!b) { err(target, `target operator "${w.to.value}" does not exist`); continue; }
    if (!portsOf(a).outputs.includes(w.output)) {
      err(target, portsOf(a).outputs.length ? `${a.kind} has no output port "${w.output}"` : `${a.kind} produces graph changes and has no output port`);
      continue;
    }
    if (!portsOf(b).inputs.includes(w.input)) { err(target, `${b.kind} has no input port "${w.input}" (ports: ${portsOf(b).inputs.join(", ") || "none"})`); continue; }
    incoming.get(b.id.value)!.push({ wire: w, key });
  }

  // ---- row propagation in dependency order ----
  const rows = new Map<string, RowInfo>();
  const visiting = new Set<string>();
  const empty: RowInfo = { row: {}, entityTypes: {} };

  const visit = (id: string): RowInfo => {
    const cached = rows.get(id);
    if (cached) return cached;
    const op = ops.get(id)!;
    const here: DiagnosticTarget = { kind: "operator", id };
    if (visiting.has(id)) { err(here, "ordinary operator cycle; recursion needs a fixpoint operator"); return empty; }
    visiting.add(id);
    const ins = incoming.get(id)!;
    const ports = portsOf(op).inputs;
    const byPort = new Map<string, { wire: Wire; key: string }[]>();
    for (const i of ins) byPort.set(i.wire.input, [...(byPort.get(i.wire.input) ?? []), i]);
    for (const p of ports) {
      const l = byPort.get(p) ?? [];
      if (l.length === 0) err(here, `input port "${p}" is not connected`);
      for (const extra of l.slice(1)) err({ kind: "wire", key: extra.key }, `input port "${p}" of ${id} already has a wire`);
    }
    const upstream = (port: string): RowInfo => {
      const first = byPort.get(port)?.[0];
      return first ? visit(first.wire.from.value) : empty;
    };
    const fail = (m: string) => err(here, m);
    let info: RowInfo = empty;

    switch (op.kind) {
      case "input": {
        if (!bindings.has(op.binding)) err(here, `binding "${op.binding}" is not declared in the projection inputs`);
        const row: Row = {};
        for (const f of op.row.fields) { if (row[f.name]) fail(`duplicate field "${f.name}"`); row[f.name] = f.type; }
        info = { row, entityTypes: {} };
        break;
      }
      case "match": {
        if (!bindings.has(op.binding)) err(here, `binding "${op.binding}" is not declared in the projection inputs`);
        info = matchRow(op.pattern, ont.inputs?.[op.binding], op.binding, fail, (m) => warn(here, m));
        break;
      }
      case "filter": {
        info = upstream("in");
        const t = inferType(op.predicate, ctxOf(info.row, info.entityTypes, fail));
        if (!compatible(t, "boolean")) fail(`filter predicate must be boolean, got ${t}`);
        break;
      }
      case "map": {
        const up = upstream("in");
        const row: Row = {};
        for (const a of op.fields) { if (row[a.field]) fail(`duplicate field "${a.field}"`); row[a.field] = inferType(a.value, ctxOf(up.row, up.entityTypes, fail)); }
        info = { row, entityTypes: {} };
        break;
      }
      case "distinct": case "traverse": info = upstream("in"); break;
      case "window": {
        info = upstream("in");
        const t = info.row[op.timestampField];
        if (t === undefined) fail(`unknown timestamp field "${op.timestampField}"`);
        else if (!compatible(t, "instant")) fail(`timestamp field "${op.timestampField}" must be instant, got ${t}`);
        if (!(op.widthNanoseconds > 0)) fail("window width must be positive");
        break;
      }
      case "join": {
        const l = upstream("left"), r = upstream("right");
        const row: Row = { ...l.row };
        for (const [k, t] of Object.entries(r.row)) {
          if (k in row && !compatible(row[k], t)) fail(`field "${k}" is ${row[k]} on the left and ${t} on the right`);
          row[k] = k in row ? row[k] : t;
        }
        info = { row, entityTypes: { ...l.entityTypes, ...r.entityTypes } };
        const t = inferType(op.on, ctxOf(row, info.entityTypes, fail));
        if (!compatible(t, "boolean")) fail(`join condition must be boolean, got ${t}`);
        break;
      }
      case "union": {
        const l = upstream("left");
        const rightWire = byPort.get("right")?.[0];
        const r = upstream("right");
        if (rightWire) {
          const diff = rowDifference(l.row, r.row);
          if (diff) err({ kind: "wire", key: rightWire.key }, `row type differs from the left input: ${diff}`);
        }
        info = l;
        break;
      }
      case "group": {
        const up = upstream("in");
        const row: Row = {};
        for (const k of op.keys) row[k.field] = inferType(k.value, ctxOf(up.row, up.entityTypes, fail));
        for (const a of op.aggregates) {
          const t = inferType(a.value, ctxOf(up.row, up.entityTypes, fail));
          if (row[a.name]) fail(`duplicate field "${a.name}"`);
          if ((a.function === "sum") && t !== "unknown" && !NUMERIC.has(t)) fail(`sum needs a numeric value, got ${t}`);
          row[a.name] = a.function === "countDistinct" ? "integer" : a.function === "any" || a.function === "all" ? "boolean" : t;
        }
        info = { row, entityTypes: {} };
        break;
      }
      case "emitEntity": {
        info = upstream("in");
        const c = ctxOf(info.row, info.entityTypes, fail);
        for (const comp of op.identity.components) inferType(comp.value, c);
        if (op.identity.components.length === 0) fail("identity has no components");
        const o = ont.output;
        if (o) {
          const t = o.entityTypes.find((x) => x.id.value === op.type.value);
          if (!t) fail(`entity type "${op.type.value}" is not declared in ontology ${o.reference.id}`);
          else checkProperties(op.properties, t.properties.map((p) => p.value), o, c, fail);
        }
        break;
      }
      case "emitRelationship": {
        info = upstream("in");
        const c = ctxOf(info.row, info.entityTypes, fail);
        const st = inferType(op.source, c), tt = inferType(op.target, c);
        if (!compatible(st, "entity")) fail(`relationship source must be an entity, got ${st}`);
        if (!compatible(tt, "entity")) fail(`relationship target must be an entity, got ${tt}`);
        const o = ont.output;
        if (o) {
          const t = o.relationshipTypes.find((x) => x.id.value === op.type.value);
          if (!t) fail(`relationship type "${op.type.value}" is not declared in ontology ${o.reference.id}`);
          else {
            checkProperties(op.properties, t.properties.map((p) => p.value), o, c, fail);
            const endType = (e: Expression) => (e.kind === "field" ? info.entityTypes[e.field] : undefined);
            const s = endType(op.source), g = endType(op.target);
            if (s && t.sourceTypes.length && !t.sourceTypes.some((x) => x.value === s)) fail(`${op.type.value} does not allow source type ${s} (allowed: ${t.sourceTypes.map((x) => x.value).join(", ")})`);
            if (g && t.targetTypes.length && !t.targetTypes.some((x) => x.value === g)) fail(`${op.type.value} does not allow target type ${g} (allowed: ${t.targetTypes.map((x) => x.value).join(", ")})`);
          }
        }
        break;
      }
      case "fixpoint": {
        const row: Row = {};
        for (const f of op.row.fields) row[f.name] = f.type;
        info = { row, entityTypes: {} };
        if (!op.body.some((b) => b.id.value === op.result.value)) fail(`result "${op.result.value}" is not a body operator`);
        if (!(op.maximumFacts >= 1) || !(op.maximumIterations >= 1)) fail("fixpoint needs positive fact and iteration budgets");
        if (!bindings.has(op.finiteDomainBinding)) fail(`finite domain binding "${op.finiteDomainBinding}" is not declared in the projection inputs`);
        break;
      }
    }
    if (op.kind === "traverse" && !(op.minimumDepth >= 0 && op.maximumDepth >= op.minimumDepth)) fail("traverse depths must satisfy 0 <= minimum <= maximum");
    visiting.delete(id);
    rows.set(id, info);
    return info;
  };
  for (const id of ops.keys()) visit(id);

  // ---- usage warnings ----
  const used = new Set(def.wires.map((w) => w.from.value));
  const outputs = new Set(def.outputs.map((o) => o.value));
  for (const op of def.operators) {
    const p = portsOf(op);
    if (p.outputs.length && !used.has(op.id.value)) warn({ kind: "operator", id: op.id.value }, "output is not connected to anything");
    if ((op.kind === "emitEntity" || op.kind === "emitRelationship") && !outputs.has(op.id.value)) warn({ kind: "operator", id: op.id.value }, "emitter is not listed in outputs");
  }
  const usedBindings = new Set(def.operators.flatMap((o) => (o.kind === "input" || o.kind === "match" ? [o.binding] : o.kind === "fixpoint" ? [o.finiteDomainBinding] : [])));
  for (const b of bindings) if (!usedBindings.has(b)) warn({ kind: "definition" }, `input binding "${b}" is not used by any operator`);
  return { diagnostics, rows };
}

function matchRow(p: GraphPattern, source: OntologyDefinition | undefined, binding: string, fail: (m: string) => void, warn: (m: string) => void): RowInfo {
  const row: Row = {};
  const entityTypes: Record<string, string> = {};
  if (!source) { warn(`no ontology supplied for binding "${binding}"; pattern types and properties are not checked`); row[OPEN] = "open"; }
  const propKind = new Map(source?.properties.map((x) => [x.id.value, x.valueKind]));
  for (const n of p.nodes) {
    if (row[n.variable]) fail(`duplicate pattern variable "${n.variable}"`);
    row[n.variable] = "entity";
    entityTypes[n.variable] = n.type.value;
    if (source) {
      const t = source.entityTypes.find((x) => x.id.value === n.type.value);
      if (!t) fail(`entity type "${n.type.value}" is not declared in ontology ${source.reference.id}`);
      else for (const pr of t.properties) row[`${n.variable}.${pr.value}`] = propKind.get(pr.value) ?? "unknown";
    }
  }
  for (const e of p.edges) {
    const s = p.nodes.find((n) => n.variable === e.source), t = p.nodes.find((n) => n.variable === e.target);
    if (!s || !t) fail(`edge "${e.variable}" refers to an unknown node variable`);
    row[e.variable] = "relationship";
    if (source) {
      const rt = source.relationshipTypes.find((x) => x.id.value === e.type.value);
      if (!rt) fail(`relationship type "${e.type.value}" is not declared in ontology ${source.reference.id}`);
      else {
        if (s && rt.sourceTypes.length && !rt.sourceTypes.some((x) => x.value === s.type.value)) fail(`${e.type.value} does not allow source type ${s.type.value}`);
        if (t && rt.targetTypes.length && !rt.targetTypes.some((x) => x.value === t.type.value)) fail(`${e.type.value} does not allow target type ${t.type.value}`);
      }
    }
  }
  return { row, entityTypes };
}

function checkProperties(
  assigns: { property: { value: string }; value: Expression }[], owned: string[], o: OntologyDefinition, c: Ctx, fail: (m: string) => void,
): void {
  for (const a of assigns) {
    const name = a.property.value;
    const t = inferType(a.value, c);
    const def = o.properties.find((x) => x.id.value === name);
    if (!def) { fail(`property "${name}" is not declared in ontology ${o.reference.id}`); continue; }
    if (!owned.includes(name)) { fail(`property "${name}" is not a property of this type`); continue; }
    if (def.role !== "graphData") fail(`property "${name}" is telemetry and cannot be emitted as graph data`);
    if (!compatible(t, def.valueKind)) fail(`property "${name}" is ${def.valueKind}, the assigned value is ${t}`);
  }
}

function rowDifference(a: Row, b: Row): string | null {
  const diffs: string[] = [];
  for (const k of Object.keys(a)) if (!(k in b)) diffs.push(`missing "${k}"`); else if (!compatible(a[k], b[k])) diffs.push(`"${k}" is ${a[k]} vs ${b[k]}`);
  for (const k of Object.keys(b)) if (!(k in a)) diffs.push(`extra "${k}"`);
  return diffs.length ? diffs.slice(0, 3).join(", ") : null;
}

/** Diagnostics a single wire would cause if added to `def`; empty means it is accepted. Pure. */
export function checkNewWire(def: ProjectionDefinition, wire: Wire, ont: Ontologies = {}): Diagnostic[] {
  const key = wireKey(wire);
  if (def.wires.some((w) => wireKey(w) === key)) return [{ severity: "error", target: { kind: "wire", key }, message: "this wire already exists" }];
  const before = checkDefinition(def, ont).diagnostics.map(diagKey);
  const after = checkDefinition({ ...def, wires: [...def.wires, wire] }, ont).diagnostics;
  return after.filter((d) => d.severity === "error" && !before.includes(diagKey(d)));
}
const diagKey = (d: Diagnostic): string => `${d.severity}|${JSON.stringify(d.target)}|${d.message}`;
