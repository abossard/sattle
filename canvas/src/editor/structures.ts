// Module-level declarations as catalog edits: row types with fields, constraints, equivalence policies, value roles, and a new projection's
// KQL file and place in the graph space. Pure: graph and palette in, edits (or a reason) out. The slots, types, parameters, constraint kinds, modes
// and value kinds all come from `GET /palette` (engine 3.2: declarations, rowTypes, equivalencePolicies, valueRoles); an engine
// that does not describe them gets a message, not a guess. Only the relationship names that connect the parts (`constrains`,
// `onProperty`, `between`, `valueKind`) are written here, because the palette names slots but not relationships.
import { Catalog } from "../core/catalog";
import type { Graph } from "../core/graph";
import type { Edit } from "../core/model";
import type { GraphValue } from "../core/wire-types";
import { contains, ent, rel, setPropEdit, text } from "./patches";
import type { Palette, PaletteDeclaration, PaletteParam } from "./palette";

/** A computed edit, or the reason it cannot be made. */
export type Result<T> = { ok: true; value: T } | { ok: false; reason: string };
const ok = <T,>(value: T): Result<T> => ({ ok: true, value });
const no = (reason: string): Result<never> => ({ ok: false, reason });
const nameOk = (n: string) => /^[A-Za-z_][\w]*$/.test(n);
const last = (id: string) => id.slice(id.lastIndexOf(":") + 1);
const KERNEL = "d:sattle.kernel/builtin:";

/** The declaration of a module slot, or the reason the engine's palette cannot say. */
function decl(palette: Palette, slot: string): Result<PaletteDeclaration> {
  const d = palette.declarations[slot];
  return d ? ok(d) : no(`the engine's palette does not describe ${slot} declarations (it needs protocol 3.2)`);
}
/** A parameter value as the palette types it (Boolean, Integer, Decimal, List of text, enumerations, Text). */
export function typedValue(p: PaletteParam, raw: string): Result<GraphValue> {
  const v = raw.trim();
  if (p.values && !p.values.includes(v)) return no(`${p.name} must be one of ${p.values.join(", ")}`);
  switch (p.kind) {
    case "Boolean": return v === "true" || v === "false" ? ok({ kind: "boolean", value: v === "true" }) : no(`${p.name} must be true or false`);
    case "Integer": return /^-?\d+$/.test(v) ? ok({ kind: "integer", value: v }) : no(`${p.name} must be a whole number`);
    case "Decimal": return /^-?\d+(\.\d+)?$/.test(v) ? ok({ kind: "decimal", value: v }) : no(`${p.name} must be a number`);
    case "List": return ok({ kind: "list", items: v.split(",").map((x) => x.trim()).filter(Boolean).map(text) });
    default: return ok(text(v));
  }
}
/** Properties of a declaration from the values given, checking required ones. `skip` are parameters the caller sets itself. */
function propsOf(d: PaletteParam[], given: Record<string, string>, skip: string[] = []): Result<Record<string, GraphValue>> {
  const out: Record<string, GraphValue> = {};
  for (const p of d) {
    if (skip.includes(p.name)) continue;
    const raw = (given[p.name] ?? "").trim();
    if (!raw) { if (p.required) return no(`${p.name} is required`); continue; }
    const v = typedValue(p, raw);
    if (!v.ok) return v;
    out[p.name] = v.value;
  }
  return ok(out);
}

// ---- row types ----
export function addRowTypeEdits(g: Graph, module: string, name: string, palette: Palette): Result<Edit[]> {
  const d = decl(palette, "rows");
  if (!d.ok) return d;
  if (!nameOk(name)) return no("a name is letters, digits and _");
  const id = `${module}/${d.value.slot}:${name}`;
  if (g.entity(id)) return no(`${name} already exists`);
  return ok([ent(id, d.value.type, { name: text(name) }), contains(module, id, d.value.slot)]);
}
export function addFieldEdits(g: Graph, rows: string, name: string, kind: string, optional: boolean, palette: Palette): Result<Edit[]> {
  const rt = palette.rowTypes;
  if (!rt) return no("the engine's palette does not describe row types (it needs protocol 3.2)");
  if (!nameOk(name)) return no("a name is letters, digits and _");
  if (!rt.field.valueKinds.includes(kind)) return no(`a field is one of ${rt.field.valueKinds.join(", ")}`);
  const id = `${rows}/${rt.fieldSlot}:${name}`;
  if (g.entity(id)) return no(`field ${name} already exists`);
  const n = new Catalog(g).children(rows, rt.fieldSlot).length;
  return ok([ent(id, rt.field.type, { name: text(name), optional: { kind: "boolean", value: optional } }), rel(id, "valueKind", `${KERNEL}${kind}`), contains(rows, id, rt.fieldSlot, n)]);
}

// ---- constraints ----
export interface ConstraintKind { kind: string; target: string; uses: string[]; text: string }
/** The constraint kinds the engine offers, with what each one constrains and which parts it uses. */
export const constraintKinds = (p: Palette): ConstraintKind[] => p.declarations.constraint?.kinds ?? [];
export const constraintParams = (p: Palette): PaletteParam[] => p.declarations.constraint?.params ?? [];

/**
 * A constraint of any kind the palette lists: `target` is the constrained type (for the kinds that constrain a property, the type that
 * has it), `properties` the property ids it uses (`onProperty`, ordered), `attrs` the values of the other parts (`values`, `min`, `max`,
 * `pattern`, `side`), typed by the palette's parameters.
 */
export function addConstraintEdits(g: Graph, module: string, kind: string, target: string, properties: string[], severity: string, attrs: Record<string, string>, palette: Palette): Result<Edit[]> {
  const d = decl(palette, "constraint");
  if (!d.ok) return d;
  const k = constraintKinds(palette).find((x) => x.kind === kind);
  if (!k) return no(`the engine has no constraint kind ${kind}`);
  if (!g.entity(target)) return no("choose what the constraint applies to");
  if (k.uses.includes("onProperty")) {
    if (!properties.length || properties.some((x) => !g.entity(x))) return no("choose the property");
    if (kind !== "key" && properties.length > 1) return no("this constraint uses one property");
  }
  // a part the kind uses is required even where the parameter is optional for other kinds (`min` of a cardinality)
  const props = propsOf(d.value.params.filter((p) => k.uses.includes(p.name)).map((p) => ({ ...p, required: true })), attrs);
  if (!props.ok) return props;
  const sev = typedValue(d.value.params.find((p) => p.name === "severity") ?? { name: "severity", kind: "Text", required: true }, severity || "error");
  if (!sev.ok) return sev;
  // the engine's default name, which its printer leaves out: kind and type, plus the property when exactly one is constrained (a key has several)
  const name = `${kind}-${last(target)}${k.uses.includes("onProperty") && properties.length === 1 ? `.${last(properties[0])}` : ""}`;
  const id = `${module}/${d.value.slot}:${name}`;
  if (g.entity(id)) return no(`${name} already exists`);
  return ok([ent(id, d.value.type, { name: text(name), kind: text(kind), severity: sev.value, ...props.value }), rel(id, "constrains", target),
    ...(k.uses.includes("onProperty") ? properties.map((x, i) => rel(id, "onProperty", x, { ordinal: { kind: "integer", value: String(i) } })) : []), contains(module, id, d.value.slot)]);
}

// ---- equivalence policies ----
export function addEquivalenceEdits(g: Graph, module: string, name: string, mode: string, threshold: string, between: string[], palette: Palette): Result<Edit[]> {
  const d = decl(palette, "equivalence");
  if (!d.ok) return d;
  if (!nameOk(name)) return no("a name is letters, digits and _");
  if (between.length !== 2 || between.some((b) => !g.entity(b))) return no("choose two identity domains");
  if (between[0] === between[1]) return no("the two identity domains must differ");
  const id = `${module}/${d.value.slot}:${name}`;
  if (g.entity(id)) return no(`${name} already exists`);
  const props = propsOf(d.value.params, { mode, threshold }, ["name", "description"]);
  if (!props.ok) return props;
  return ok([ent(id, d.value.type, { name: text(name), ...props.value }), ...between.map((b, i) => rel(id, "between", b, { ordinal: { kind: "integer", value: String(i) } })), contains(module, id, d.value.slot)]);
}
// ---- value roles ----
/** The last id segment of a value, as the engine writes it (`Ids.ValueSegment`): text escaped, numbers and booleans tagged. */
export function valueSegment(v: GraphValue): string {
  switch (v.kind) {
    case "text": return encodeURIComponent(v.value).replace(/[!'()*]/g, (c) => `%${c.charCodeAt(0).toString(16).toUpperCase()}`).replace(/~/g, "%7E").replace(/:/g, "%3A");
    case "integer": return `~i${v.value}`;
    case "decimal": return `~d${v.value.includes(".") ? v.value.replace(/0+$/, "").replace(/\.$/, "") : v.value}`;
    case "boolean": return v.value ? "~btrue" : "~bfalse";
    default: return "~null";
  }
}
/** A value of a property means a role (`ok`, `warning`, ...): the node `<property>/presentation:<value>` in the property's presentation slot. */
export function addValueRoleEdits(g: Graph, property: string, raw: string, role: string, palette: Palette): Result<Edit[]> {
  const slot = palette.valueRoleSlot;
  if (!slot) return no("the engine's palette does not describe value roles (it needs protocol 3.2)");
  if (g.entity(property)?.type !== slot.container) return no("choose a property");
  if (!palette.valueRoles.includes(role)) return no(`a role is one of ${palette.valueRoles.join(", ")}`);
  if (!raw.trim()) return no("a value is required");
  // the property's value kind decides how the text is read: "1" is an integer for an Integer property, "true" a boolean for a Boolean one
  const kind = last([...g.relationships()].find((r) => r.source === property && r.type === "valueKind")?.target ?? "Text");
  const t = typedValue({ name: "value", kind: kind === "Entity" || kind === "List" ? "Text" : kind, required: true }, raw);
  if (!t.ok) return t;
  const id = `${property}/${slot.slot}:${valueSegment(t.value)}`;
  if (g.entity(id)) return no(`${raw.trim()} already has a role`);
  return ok([ent(id, slot.type, { value: t.value, role: text(role) }), contains(property, id, slot.slot)]);
}
export const setValueRoleEdits = (g: Graph, id: string, role: string): Edit[] => [setPropEdit(g, id, "role", text(role))];

// ---- a new projection: its KQL file and its place in the graph space ----
/** The parameter types of `declare query_parameters` and the value kind of each. */
export const PARAM_KINDS: Record<string, string> = { string: "Text", long: "Integer", real: "Decimal", bool: "Boolean" };

export interface ProjectionSpec {
  module: string; name: string; description: string; outputModule: string; graphName: string;
  /** inputs the graph keeps per element (`history` of the graph line); left out when empty */
  history?: string;
  /** rows inputs: the port, the row type it reads and the source of the space that feeds it */
  rows?: { port: string; rowType: string; source: string }[];
  /** graph inputs: the port name, the space graph that feeds it and the ontology module the port expects */
  inputs: { port: string; graph: string; ontology: string }[];
  /** parameters: the name, its type (a key of PARAM_KINDS), the literal the space binds (may be empty when there is a default) and the default */
  params: { port: string; kind: string; value: string; default?: string }[];
}

/**
 * The graph of the space that projection `name` of `module` produces, with one binding per port: `from` gives the space graph or source
 * that feeds a graph or rows port, `values` the literal bound to a parameter. No edits when the space has that graph already.
 */
export function bindingEdits(g: Graph, module: string, name: string, graphName: string, from: Record<string, string>, values: Record<string, GraphValue>): Edit[] {
  const space = [...g.entities()].find((e) => e.type === "GraphSpace")?.id;
  if (!space) return [];
  const graph = `${space}/graph:${graphName}`, proj = `d:${module}/projection:${name}`;
  if (g.entity(graph)) return [];
  const edits: Edit[] = [ent(graph, "Graph", { name: text(graphName) }), contains(space, graph, "graph"), rel(graph, "producedBy", proj)];
  const bind = (port: string, props: Record<string, GraphValue>, extra: (b: string) => Edit[]) => {
    const b = `${space}/binding:${graphName}.${port}`;
    edits.push(ent(b, "Binding", props), contains(space, b, "binding"), rel(b, "bindsPort", `${proj}/port:${port}`), ...extra(b));
  };
  for (const [port, f] of Object.entries(from)) bind(port, {}, (b) => [rel(b, "boundFrom", f)]);
  for (const [port, v] of Object.entries(values)) bind(port, { value: v }, () => []);
  return edits;
}

/**
 * A new projection the KQL way: the text of its file (the graph line with its settings, one `input` line per input and the parameters'
 * declaration) and the edits that place its graph in the space. The text is stored first, then the edits applied. Operators come afterwards.
 */
export function newProjection(g: Graph, spec: ProjectionSpec): Result<{ file: string; text: string; edits: Edit[] }> {
  const space = [...g.entities()].find((e) => e.type === "GraphSpace")?.id;
  if (!space) return no("the catalog has no graph space");
  if (!nameOk(spec.name) || !nameOk(spec.graphName)) return no("names are letters, digits and _");
  if (g.entity(`d:${spec.module}/projection:${spec.name}`)) return no(`projection ${spec.name} already exists`);
  if (g.entity(`${space}/graph:${spec.graphName}`)) return no(`graph ${spec.graphName} already exists in the space`);
  if (g.entity(spec.outputModule)?.type !== "Module") return no("choose the output ontology");
  const history = spec.history?.trim() ?? "";
  if (history && !/^\d+$/.test(history)) return no("history is a whole number: how many inputs the graph keeps per element");
  const rows = spec.rows ?? [];
  const ports = [...spec.inputs.map((i) => i.port), ...rows.map((r) => r.port), ...spec.params.map((p) => p.port)];
  if (ports.some((p) => !nameOk(p)) || new Set(ports).size !== ports.length) return no("input and parameter names are distinct names of letters, digits and _");
  const mod = (id: string) => id.slice(2, id.indexOf("/") < 0 ? undefined : id.indexOf("/"));
  const lines: string[] = [], from: Record<string, string> = {}, values: Record<string, GraphValue> = {}, declared: string[] = [];
  for (const i of spec.inputs) {
    const f = `${space}/graph:${i.graph}`;
    if (!g.entity(f)) return no(`unknown graph ${i.graph}`);
    if (g.entity(i.ontology)?.type !== "Module") return no(`choose the ontology that ${i.port} expects`);
    lines.push(`input ${i.port} : ${mod(i.ontology)};`);
    from[i.port] = f;
  }
  for (const r of rows) {
    if (g.entity(r.rowType)?.type !== "RowType") return no(`choose the row type of ${r.port}`);
    if (g.entity(r.source)?.type !== "Source") return no(`choose the source that feeds ${r.port}`);
    const m = mod(r.rowType);
    lines.push(`input ${r.port} : ${m === spec.module ? "" : `${m}.`}${last(r.rowType)};`);
    from[r.port] = r.source;
  }
  for (const p of spec.params) {
    const kind = PARAM_KINDS[p.kind];
    if (!kind) return no(`a parameter is ${Object.keys(PARAM_KINDS).join(", ")}`);
    const check = (raw: string, what: string): Result<GraphValue> => typedValue({ name: what, kind, required: true }, raw);
    const def = p.default?.trim() ? check(p.default, `${p.port} default`) : null;
    if (def && !def.ok) return def;
    const val = p.value.trim() ? check(p.value, p.port) : null;
    if (val && !val.ok) return val;
    if (!val && !def) return no(`${p.port} needs a value bound in the space or a default`);
    declared.push(`${p.port}:${p.kind}${def ? ` = ${p.kind === "string" ? JSON.stringify(p.default!.trim()) : p.default!.trim()}` : ""}`);
    if (val) values[p.port] = val.value;
  }
  const settings = [...(spec.description.trim() ? [`description = ${JSON.stringify(spec.description.trim())}`] : []), ...(history ? [`history = ${history}`] : [])];
  const text = [`graph ${spec.name} : ${mod(spec.outputModule)}${settings.length ? ` with (${settings.join(", ")})` : ""};`, ...lines,
    ...(declared.length ? [`declare query_parameters(${declared.join(", ")});`] : [])].join("\n") + "\n";
  return ok({ file: `${spec.module}.${spec.name}.kql`, text, edits: bindingEdits(g, spec.module, spec.name, spec.graphName, from, values) });
}
