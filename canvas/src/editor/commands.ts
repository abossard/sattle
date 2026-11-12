// Editor commands as pure functions: graph and palette in, catalog edits (or a reason) out. Nothing here talks to the engine.
import { Catalog, DIRECTION_CLASSES } from "../core/catalog";
import type { Graph } from "../core/graph";
import type { Edit } from "../core/model";
import type { GraphValue } from "../core/wire-types";
import { canonicalFunctions, exprEdits, parseExpr, unknownComponents, unknownDomains, type Expr } from "./expr";
import { aggregateAllowed, scalarFunctions, slotOf, type Palette, type PaletteOperator, type PaletteParam } from "./palette";
import { contains, copyEdits, ent, rel, relId, removeEdits, setPropEdit, subtreeIds, text } from "./patches";

export type Result<T> = { ok: true; value: T } | { ok: false; reason: string };
const ok = <T,>(value: T): Result<T> => ({ ok: true, value });
const no = (reason: string): Result<never> => ({ ok: false, reason });

const prop = (g: Graph, id: string, name: string): string => { const v = g.entity(id)?.props[name]; return v && "value" in v ? String(v.value) : ""; };

// ---- wiring with live type checking ----
export function portInfo(g: Graph, port: string): { dir: string; kind: string; owner: string } | null {
  const e = g.entity(port);
  return e ? { dir: prop(g, port, "direction"), kind: prop(g, port, "kind"), owner: port.slice(0, port.lastIndexOf("/")) } : null;
}
const isProjectionPort = (id: string) => /^d:[^/]+\/projection:[^/]+\/port:[^/]+$/.test(id);

/** Whether `from` (an output, or a projection's input binding) can feed `to` (an input). The reason is shown while dragging. */
export function canWire(g: Graph, from: string, to: string): Result<true> {
  const a = portInfo(g, from), b = portInfo(g, to);
  if (!a || !b) return no("not a port");
  const source = isProjectionPort(from) ? a.dir === "in" : a.dir === "out";
  if (!source) return no("a wire starts at an output port");
  if (b.dir !== "in" || isProjectionPort(to)) return no("a wire ends at an input port of an operator");
  if (a.owner === b.owner) return no("an operator cannot feed itself");
  if (a.kind !== b.kind) return no(`${a.kind} cannot feed ${b.kind}`);
  const wired = [...g.relationships()].some((r) => r.type === "wire" && r.target === to);
  if (wired) return no("this input already has a wire");
  if ([...g.relationships()].some((r) => r.id === relId(from, "wire", to))) return no("already connected");
  // no cycles: walking from the target operator downstream must not reach the source operator
  const next = new Map<string, string[]>();
  for (const r of g.relationships()) if (r.type === "wire") { const o = r.source.slice(0, r.source.lastIndexOf("/")), t = r.target.slice(0, r.target.lastIndexOf("/")); next.set(o, [...(next.get(o) ?? []), t]); }
  const seen = new Set<string>(); const stack = [b.owner];
  while (stack.length) { const n = stack.pop()!; if (n === a.owner) return no("this would make a cycle"); if (seen.has(n)) continue; seen.add(n); stack.push(...(next.get(n) ?? [])); }
  return ok(true);
}
export const wireEdits = (from: string, to: string): Edit[] => [rel(from, "wire", to)];
export const unwireEdits = (g: Graph, key: string): Edit[] => { const r = g.rel(key); return r ? [{ kind: "relationship", id: key, before: r, after: null }] : []; };

// ---- parameters ----
/** Durations in nanoseconds for parameters whose unit is ns: `24h`, `90s`, `5m`, `500ms`, `1d` are read as such. */
const DURATION_NS: Record<string, number> = { ms: 1e6, s: 1e9, m: 60e9, h: 3600e9, d: 86400e9 };
/** A duration parameter: the palette says so (`quantity`), older engines by the unit `ns`. */
export const isDuration = (p: PaletteParam): boolean => p.quantity === "duration" || p.unit === "ns";
/** Nanoseconds as the shortest exact `<n><ms|s|m|h>`: 86400000000000 is `24h`. Anything else stays the number. */
export function durationText(ns: string): string {
  if (!/^\d+$/.test(ns) || ns === "0") return ns;
  const n = BigInt(ns);
  for (const [u, f] of [["h", 3600000000000n], ["m", 60000000000n], ["s", 1000000000n], ["ms", 1000000n]] as const) if (n % f === 0n) return `${n / f}${u}`;
  return ns;
}
export function paramValue(p: PaletteParam, raw0: string): Result<GraphValue> {
  const d = isDuration(p) ? /^(\d+(?:\.\d+)?)(ms|s|m|h|d)$/.exec(raw0.trim()) : null;
  const raw = d ? String(Math.round(Number(d[1]) * DURATION_NS[d[2]])) : raw0;
  switch (p.kind) {
    case "Integer": return /^-?\d+$/.test(raw.trim()) ? ok({ kind: "integer", value: raw.trim() }) : no(`${p.name} must be a whole number`);
    case "Decimal": return /^-?\d+(\.\d+)?$/.test(raw.trim()) ? ok({ kind: "decimal", value: raw.trim() }) : no(`${p.name} must be a number`);
    case "Boolean": return raw === "true" || raw === "false" ? ok({ kind: "boolean", value: raw === "true" }) : no(`${p.name} must be true or false`);
    default:
      if (p.values && !p.values.includes(raw)) return no(`${p.name} must be one of ${p.values.join(", ")}`);
      return ok(text(raw));
  }
}
export const paramEdits = (g: Graph, id: string, p: PaletteParam, raw: string): Result<Edit[]> => {
  // an optional parameter that is emptied is not set at all (`images` of a lift: absent means the mapping must be a function)
  if (raw.trim() === "" && !p.required) return ok(g.entity(id)?.props[p.name] ? [setPropEdit(g, id, p.name, null)] : []);
  const v = paramValue(p, raw);
  return v.ok ? ok([setPropEdit(g, id, p.name, v.value)]) : v;
};

// ---- slots: expressions, assignments, aggregates, patterns ----
const nameOk = (n: string) => /^[A-Za-z_][\w]*$/.test(n);
/** Whether the palette says the children of this slot are ordered (the columns of a `select`, the reads of a `series`): they then carry an ordinal. */
export function slotIsOrdered(g: Graph, palette: Palette, owner: string, slot: string): boolean {
  const type = g.entity(owner)?.type;
  const slots = type === "Rule" ? palette.rules?.slots : palette.operators.find((o) => o.kind === type)?.slots;
  return !!slots?.find((x) => x.name === slot)?.ordered;
}
/**
 * The children of a slot that a skeleton made only so that the new operator prints (an assignment `x`, an aggregate `n = count()`, a read `x.y`):
 * the first real child of the slot replaces them. Which they are is not in the graph (an author's `n = count()` is the same element), so the
 * caller says: `known` holds the ids the engine named in its skeleton answers (engine 3.19). A child that is not in it is the author's.
 */
export function placeholderChildren(g: Graph, owner: string, slot: string, known: ReadonlySet<string>): string[] {
  if (!known.size) return [];
  return new Catalog(g).children(owner, slot).filter((k) => known.has(k) && !!g.entity(k));
}

const nextOrdinal = (c: Catalog, parent: string, slot: string) => c.children(parent, slot).length;

/**
 * Identity domains of the catalog, resolved the way the text of the module that holds the element resolves them: `alias.name` by the alias the
 * module imports under (read from its `imports` relationships), a bare `name` in the module itself first, then by the name when only one module has it.
 * Two modules may declare a domain of the same name (`signal` of the health model and of the tree), so a name alone is not always enough.
 */
class DomainMap extends Map<string, string> {
  private bare = new Map<string, string[]>();
  constructor(private readonly ownerModule: string, private readonly aliases: ReadonlyMap<string, string>) { super(); }
  add(id: string): void { const n = id.slice(id.lastIndexOf(":") + 1); this.set(id, id); this.bare.set(n, [...(this.bare.get(n) ?? []), id]); }
  override get(key: string): string | undefined {
    const direct = super.get(key);
    if (direct !== undefined) return direct;
    const dot = key.lastIndexOf("."), name = key.slice(dot + 1), all = this.bare.get(name) ?? [];
    if (dot > 0) { const mod = this.aliases.get(key.slice(0, dot)); const hit = mod && all.find((id) => id.startsWith(`${mod}/identity:`)); if (hit) return hit; }
    const own = all.find((id) => id.startsWith(`${this.ownerModule}/identity:`));
    if (dot < 0 && own) return own;
    return all.length === 1 ? all[0] : undefined;
  }
  override has(key: string): boolean { return this.get(key) !== undefined; }
}
/** The module of a definition id (`d:acme.svc/projection:x/op:y` is `d:acme.svc`). */
const moduleOf = (id: string): string => id.slice(0, id.indexOf("/") < 0 ? undefined : id.indexOf("/"));
const domainsOf = (g: Graph, owner = ""): Map<string, string> => {
  const module = moduleOf(owner);
  const aliases = new Map<string, string>();
  if (module) for (const r of g.relationships()) if (r.type === "imports" && r.source === module) { const alias = r.props.alias; if (alias && "value" in alias) aliases.set(String(alias.value), r.target.replace(/@[^@/]*$/, "")); }
  const m = new DomainMap(module, aliases);
  for (const e of g.entities()) if (e.type === "IdentityDomain") m.add(e.id);
  return m;
};

/** Replace the expression in an expression slot (predicate, value, id, source, target...) by the parsed text. */
export function setExprEdits(g: Graph, parent: string, slot: string, src: string, palette: Palette): Result<Edit[]> {
  const parsed = parseExpr(src);
  if (!parsed.ok) return no(parsed.error);
  const canon = canonicalFunctions(parsed.expr, scalarFunctions(palette));
  if (canon.unknown.length) return no(`unknown function ${canon.unknown.join(", ")}`);
  const expr: Expr = canon.expr;
  const domains = domainsOf(g, parent);
  const dom = unknownDomains(expr, domains);
  if (dom.length) return no(`unknown identity domain ${dom.join(", ")}`);
  const comp = unknownComponents(expr, domains, (id) => !!g.entity(id));
  if (comp.length) return no(`the identity domain has no component ${comp.join(", ")}`);
  const c = new Catalog(g);
  const old = c.children(parent, slot);
  return ok([...(old.length ? removeEdits(g, old) : []), ...exprEdits(parent, slot, 0, expr, false, domains)]);
}

/** Append an expression to an ordered slot of several (a rule's `equate`). */
export function addExprItemEdits(g: Graph, parent: string, slot: string, src: string, palette: Palette): Result<Edit[]> {
  const parsed = parseExpr(src);
  if (!parsed.ok) return no(parsed.error);
  const canon = canonicalFunctions(parsed.expr, scalarFunctions(palette));
  if (canon.unknown.length) return no(`unknown function ${canon.unknown.join(", ")}`);
  return ok(exprEdits(parent, slot, nextOrdinal(new Catalog(g), parent, slot), canon.expr, true, domainsOf(g, parent)));
}

/** Add a named child with an expression: `assign:<name>` (target), `key:<name>`, `agg:<name>` (target and fn), `set:<name>`. */
/** The slot of an aggregate that holds its second argument: `last(value, order)` and `weightedMean(value, weight)`. The palette does not describe an aggregate's own slots. */
export const AGGREGATE_SECOND: Record<string, string> = { last: "order", weightedMean: "weight" };

export function addNamedEdits(g: Graph, op: string, slot: string, childType: string, name: string, src: string, palette: Palette, extra: Record<string, string> = {}, placeholders: ReadonlySet<string> = new Set()): Result<Edit[]> {
  if (!nameOk(name)) return no("a name is letters, digits and _");
  if (childType === "Aggregate" && extra.fn) {
    const allowed = aggregateAllowed(palette, extra.fn, g.entity(op)?.type ?? "");
    if (!allowed.ok) return no(allowed.reason);
  }
  const id = `${op}/${slot}:${name}`;
  if (g.entity(id) && !placeholderChildren(g, op, slot, placeholders).includes(id)) return no(`${name} already exists in ${slot}`);
  // a slot whose children hold a path (the carried properties of a lift, the values it keeps) takes a field, not an expression
  const value = slotOf(palette, g.entity(op)?.type ?? "", slot)?.value;
  if (value?.kind === "fieldRef") {
    const parsed = parseExpr(src);
    if (!parsed.ok || parsed.expr.k !== "field") return no(`${slot} takes a field${value.roots.length ? ` (${value.roots.map((r) => `${r}.name`).join(" or ")})` : ""}, not an expression`);
    if (value.roots.length && !value.roots.includes(parsed.expr.path.split(".")[0])) return no(`a field of ${slot} starts with ${value.roots.join(" or ")}, for example ${value.roots[0]}.name`);
  }
  const domains = domainsOf(g, op);
  const fns = scalarFunctions(palette);
  // `count()` has no argument; every other expression is parsed, spelled the palette's way and checked against the identity domains
  const parts: { slot: string; src: string }[] = [];
  if (!(childType === "Aggregate" && extra.fn === "count" && !src.trim())) parts.push({ slot: "value", src });
  if (childType === "Aggregate") {
    const second = AGGREGATE_SECOND[extra.fn ?? ""];
    if (second) { if (!extra.second?.trim()) return no(`${extra.fn} needs its ${second} expression`); parts.push({ slot: second, src: extra.second }); }
    else if (extra.second?.trim()) return no(`${extra.fn || "this aggregate"} takes one argument`);
  }
  const built: Edit[] = [];
  for (const part of parts) {
    const parsed = parseExpr(part.src);
    if (!parsed.ok) return no(parsed.error);
    const canon = canonicalFunctions(parsed.expr, fns);
    if (canon.unknown.length) return no(`unknown function ${canon.unknown.join(", ")}`);
    const expr: Expr = canon.expr;
    const dom = unknownDomains(expr, domains);
    if (dom.length) return no(`unknown identity domain ${dom.join(", ")}`);
    const comp = unknownComponents(expr, domains, (cid) => !!g.entity(cid));
    if (comp.length) return no(`the identity domain has no component ${comp.join(", ")}`);
    built.push(...exprEdits(id, part.slot, 0, expr, false, domains));
  }
  const props = Object.fromEntries(Object.entries({ target: name, ...(extra.fn ? { fn: extra.fn } : {}) }).map(([k, v]) => [k, text(v)]));
  const old = placeholderChildren(g, op, slot, placeholders);
  const ordered = slotIsOrdered(g, palette, op, slot);
  return ok([...(old.length ? removeEdits(g, old) : []), ent(id, childType, props), contains(op, id, slot, ordered ? nextOrdinal(new Catalog(g), op, slot) - old.length : undefined), ...built]);
}

/** The rows of a `values` operator: each row is a `RowValue` with one assignment per column. */
export function addValuesRowEdits(g: Graph, op: string, cells: { name: string; src: string }[], palette: Palette): Result<Edit[]> {
  if (!cells.length) return no("a row needs at least one column");
  if (new Set(cells.map((c) => c.name)).size !== cells.length) return no("column names must differ");
  const c = new Catalog(g);
  // the next index no row has: after a middle row was deleted the child count is the index of a row that still exists
  let n = c.children(op, "row").length;
  while (g.entity(`${op}/row:#${n}`)) n++;
  const row = `${op}/row:#${n}`;
  const domains = domainsOf(g, op);
  const edits: Edit[] = [ent(row, "RowValue", {}), contains(op, row, "row", n)];
  for (const cell of cells) {
    if (!nameOk(cell.name)) return no("a column name is letters, digits and _");
    const parsed = parseExpr(cell.src);
    if (!parsed.ok) return no(parsed.error);
    const canon = canonicalFunctions(parsed.expr, scalarFunctions(palette));
    if (canon.unknown.length) return no(`unknown function ${canon.unknown.join(", ")}`);
    const a = `${row}/assign:${cell.name}`;
    edits.push(ent(a, "Assign", { target: text(cell.name) }), contains(row, a, "assign"), ...exprEdits(a, "value", 0, canon.expr, false, domains));
  }
  return ok(edits);
}

/** Set a property of the emitted element: `set:<property>` with a `property` relationship and a value expression. */
export function addSetEdits(g: Graph, op: string, propId: string, src: string, palette: Palette, slot = "set"): Result<Edit[]> {
  const prop = g.entity(propId);
  if (!prop || prop.type !== "PropertyType") return no("unknown property");
  const name = propId.slice(propId.lastIndexOf(":") + 1);
  const id = `${op}/${slot}:${name}`;
  if (g.entity(id)) return no(`${name} is already set`);
  const parsed = parseExpr(src);
  if (!parsed.ok) return no(parsed.error);
  const canon = canonicalFunctions(parsed.expr, scalarFunctions(palette));
  if (canon.unknown.length) return no(`unknown function ${canon.unknown.join(", ")}`);
  const expr: Expr = canon.expr;
  const domains = domainsOf(g, op);
  return ok([ent(id, "SetProperty", {}), rel(id, "property", propId), contains(op, id, slot, slotIsOrdered(g, palette, op, slot) ? nextOrdinal(new Catalog(g), op, slot) : undefined), ...exprEdits(id, "value", 0, expr, false, domains)]);
}

/** The type an emit operator creates (`ofType`): replaces the current one. */
export function setOfTypeEdits(g: Graph, op: string, typeId: string): Result<Edit[]> {
  if (!g.entity(typeId)) return no("unknown type");
  const old = [...g.relationships()].filter((r) => r.source === op && r.type === "ofType");
  return ok([...old.map((r): Edit => ({ kind: "relationship", id: r.id, before: r, after: null })), rel(op, "ofType", typeId)]);
}

export function addReadEdits(g: Graph, op: string, path: string, slot = "read", placeholders: ReadonlySet<string> = new Set()): Result<Edit[]> {
  if (!/^[A-Za-z_][\w.]*$/.test(path)) return no("a field path is names separated by dots");
  const c = new Catalog(g);
  const old = placeholderChildren(g, op, slot, placeholders);
  const n = nextOrdinal(c, op, slot) - old.length;
  const id = `${op}/${slot}:#${n}`;
  return ok([...(old.length ? removeEdits(g, old) : []), ent(id, "FieldRef", { path: text(path) }), contains(op, id, slot, n)]);
}

export function addPatternNodeEdits(g: Graph, op: string, variable: string, type: string): Result<Edit[]> {
  if (!nameOk(variable)) return no("a variable is letters, digits and _");
  const id = `${op}/node:${variable}`;
  if (g.entity(id)) return no(`${variable} already exists`);
  if (!g.entity(type)) return no("unknown entity type");
  return ok([ent(id, "PatternNode", { var: text(variable) }), rel(id, "ofType", type), contains(op, id, "node")]);
}
export function addPatternEdgeEdits(g: Graph, op: string, variable: string, from: string, to: string, type: string): Result<Edit[]> {
  if (variable && !nameOk(variable)) return no("a variable is letters, digits and _");
  const f = `${op}/node:${from}`, t = `${op}/node:${to}`;
  if (!g.entity(f) || !g.entity(t)) return no("an edge joins two nodes of this pattern");
  if (!g.entity(type)) return no("unknown relationship type");
  const n = nextOrdinal(new Catalog(g), op, "edge");
  // as the engine names them: a named edge by its variable and with a `var` property, an unnamed one by its index and with none
  const id = variable ? `${op}/edge:${variable}` : `${op}/edge:#${n}`;
  if (g.entity(id)) return no(variable ? `an edge ${variable} already exists in this pattern` : "an edge with this index already exists");
  return ok([ent(id, "PatternEdge", variable ? { var: text(variable) } : {}), rel(id, "from", f), rel(id, "to", t), rel(id, "ofType", type), contains(op, id, "edge", n)]);
}

// ---- rules ----
export function addRuleEdits(g: Graph, projection: string, name: string, cls: string, reason: string, governs: string[]): Result<Edit[]> {
  if (!nameOk(name)) return no("a name is letters, digits and _");
  const id = `${projection}/rule:${name}`;
  if (g.entity(id)) return no(`rule ${name} already exists`);
  if (!DIRECTION_CLASSES.includes(cls as never) && cls !== "forward") return no(`unknown rule class ${cls}`);
  // a forward projection has no rules (the engine's check P3): the first rule makes it a projection with rules
  const leaveForward = prop(g, projection, "forward") === "true" ? [setPropEdit(g, projection, "forward", { kind: "boolean", value: false })] : [];
  return ok([...leaveForward, ent(id, "Rule", { name: text(name), class: text(cls), ...(reason ? { reason: text(reason) } : {}) }), contains(projection, id, "rule"), ...governs.map((x) => rel(id, "governs", x))]);
}
export const setRuleClassEdits = (g: Graph, rule: string, cls: string): Edit[] => [setPropEdit(g, rule, "class", text(cls))];

// ---- paste, extract ----
export const uniqueName = (g: Graph, base: string, exists: (n: string) => boolean): string => { for (let i = 2; ; i++) { const n = `${base}${i}`; if (!exists(n) && !g.entity(n)) return n; } };

/** Paste operators next to the originals under new names; wires among the copied operators stay, wires to others are dropped. */
export function pasteEdits(g: Graph, projection: string, ops: string[]): Edit[] {
  const names = new Map<string, string>();
  const taken = (n: string) => !!g.entity(`${projection}/op:${n}`);
  for (const op of ops) { const base = op.slice(op.lastIndexOf(":") + 1).replace(/#\d+$/, ""); names.set(op, uniqueName(g, base, taken)); }
  const rename = (id: string) => {
    for (const [op, n] of names) if (id === op || id.startsWith(`${op}/`) || id.startsWith(`${op}#`)) return `${projection}/op:${n}${id.slice(op.length)}`;
    return id;
  };
  return [...copyEdits(g, ops, rename), ...ops.map((op) => contains(projection, rename(op), "op"))];
}

export const operatorKind = (g: Graph, id: string): string => g.entity(id)?.type ?? "";
export const paletteOperator = (p: Palette, kind: string): PaletteOperator | undefined => p.operators.find((o) => o.kind === kind);

/** Names of the statements a selection of operators stands for, for the refactoring endpoint: `op:name#n` pieces belong to their let `name`. */
export function statementNames(g: Graph, ids: string[]): string[] {
  const out = new Set<string>();
  for (const id of ids) {
    const seg = id.slice(id.lastIndexOf("/") + 1);
    if (seg.startsWith("op:")) out.add(seg.slice(3).replace(/#\d+$/, ""));
    else if (seg.startsWith("rule:")) out.add(seg.slice(5));
  }
  void g;
  return [...out];
}
export { subtreeIds };

/**
 * A child that is made of parameters (a `LiftState`): a node `<op>/<slot>:<key>` typed `childType` with a property per parameter the palette lists for it,
 * contained in the slot. `key` is the child's target (the property the state sets). Every required parameter is needed, and each is typed by the palette.
 */
export function addParamChildEdits(g: Graph, op: string, slot: string, childType: string, key: string, values: Record<string, string>, palette: Palette): Result<Edit[]> {
  if (!nameOk(key)) return no("a name is letters, digits and _");
  const sl = slotOf(palette, g.entity(op)?.type ?? "", slot);
  const ct = sl?.childTypes?.find((c) => c.type === childType);
  if (!ct) return no(`${slot} has no ${childType}`);
  const id = `${op}/${slot}:${key}`;
  if (g.entity(id)) return no(`${key} already exists in ${slot}`);
  const props: Record<string, GraphValue> = {};
  for (const p of ct.params) {
    const raw = p.name === "target" ? key : (values[p.name] ?? p.default ?? "").trim();
    if (raw === "") { if (p.required) return no(`${p.name} is needed`); continue; }
    const v = paramValue(p, raw);
    if (!v.ok) return v;
    props[p.name] = v.value;
  }
  return ok([ent(id, childType, props), contains(op, id, slot, sl?.ordered ? nextOrdinal(new Catalog(g), op, slot) : undefined)]);
}
