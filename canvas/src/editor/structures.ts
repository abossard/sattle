// Module-level declarations as catalog edits: row types with fields, constraints, equivalence policies, value roles, and a new projection's
// GKQL program and its binding in the module's graph space. Pure: graph and palette in, edits (or a reason) out. The slots, types, parameters, constraint kinds, modes
// and value kinds all come from `GET /palette` (engine 3.2: declarations, rowTypes, equivalencePolicies, valueRoles), and so do the
// relationships that connect the parts (engine 3.55: `references` of Constraint, EquivalencePolicy, Field and PropertyType); an engine
// that does not describe them gets a message, not a guess.
import { Catalog } from "../core/catalog";
import type { Graph } from "../core/graph";
import type { Edit } from "../core/model";
import type { GraphValue } from "../core/wire-types";
import { canEditGkqlStructure, printGkql, readGkqlJson, type GkqlProgram } from "./gkql";
import { contains, ent, rel, setPropEdit, text } from "./patches";
import type { Palette, PaletteDeclaration, PaletteParam, PaletteReference } from "./palette";
import type { ProjectionFile } from "./service";

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
/** The reference from a node of type `owner` to one of type `target` (its relationship name and the types it may point at), or the reason the palette cannot say. */
function refTo(palette: Palette, owner: string, target: string): Result<PaletteReference> {
  const r = palette.references?.[owner]?.find((x) => x.targets.includes(target));
  return r ? ok(r) : no(`the engine's palette does not say how a ${owner} names a ${target} (it needs protocol 3.55)`);
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
  const valueKind = refTo(palette, rt.field.type, "ValueKind");
  if (!valueKind.ok) return valueKind;
  const id = `${rows}/${rt.fieldSlot}:${name}`;
  if (g.entity(id)) return no(`field ${name} already exists`);
  const n = new Catalog(g).children(rows, rt.fieldSlot).length;
  return ok([ent(id, rt.field.type, { name: text(name), optional: { kind: "boolean", value: optional } }), rel(id, valueKind.value.name, `${KERNEL}${kind}`), contains(rows, id, rt.fieldSlot, n)]);
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
  const t = g.entity(target);
  if (!t) return no("choose what the constraint applies to");
  const constrains = refTo(palette, d.value.type, "EntityType");
  if (!constrains.ok) return constrains;
  if (!constrains.value.targets.includes(t.type)) return no(`a constraint applies to one of: ${constrains.value.targets.join(", ")}`);
  const onProperty = refTo(palette, d.value.type, "PropertyType");
  if (!onProperty.ok) return onProperty;
  if (k.uses.includes(onProperty.value.name)) {
    if (!properties.length || properties.some((x) => !g.entity(x))) return no("choose the property");
    if (kind !== "key" && properties.length > 1) return no("this constraint uses one property");
  }
  // a part the kind uses is required even where the parameter is optional for other kinds (`min` of a cardinality)
  const props = propsOf(d.value.params.filter((p) => k.uses.includes(p.name)).map((p) => ({ ...p, required: true })), attrs);
  if (!props.ok) return props;
  const sev = typedValue(d.value.params.find((p) => p.name === "severity") ?? { name: "severity", kind: "Text", required: true }, severity || "error");
  if (!sev.ok) return sev;
  // the engine's default name, which its printer leaves out: kind and type, plus the property when exactly one is constrained (a key has several)
  const name = `${kind}-${last(target)}${k.uses.includes(onProperty.value.name) && properties.length === 1 ? `.${last(properties[0])}` : ""}`;
  const id = `${module}/${d.value.slot}:${name}`;
  if (g.entity(id)) return no(`${name} already exists`);
  return ok([ent(id, d.value.type, { name: text(name), kind: text(kind), severity: sev.value, ...props.value }), rel(id, constrains.value.name, target),
    ...(k.uses.includes(onProperty.value.name) ? properties.map((x, i) => rel(id, onProperty.value.name, x, { ordinal: { kind: "integer", value: String(i) } })) : []), contains(module, id, d.value.slot)]);
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
  const by = refTo(palette, d.value.type, "IdentityDomain");
  if (!by.ok) return by;
  return ok([ent(id, d.value.type, { name: text(name), ...props.value }), ...between.map((b, i) => rel(id, by.value.name, b, { ordinal: { kind: "integer", value: String(i) } })), contains(module, id, d.value.slot)]);
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
  const valueKind = refTo(palette, slot.container, "ValueKind");
  if (!valueKind.ok) return valueKind;
  // the property's value kind decides how the text is read: "1" is an integer for an Integer property, "true" a boolean for a Boolean one
  const kind = last([...g.relationships()].find((r) => r.source === property && r.type === valueKind.value.name)?.target ?? "Text");
  const t = typedValue({ name: "value", kind: kind === "Entity" || kind === "List" ? "Text" : kind, required: true }, raw);
  if (!t.ok) return t;
  const id = `${property}/${slot.slot}:${valueSegment(t.value)}`;
  if (g.entity(id)) return no(`${raw.trim()} already has a role`);
  return ok([ent(id, slot.type, { value: t.value, role: text(role) }), contains(property, id, slot.slot)]);
}
export const setValueRoleEdits = (g: Graph, id: string, role: string): Edit[] => [setPropEdit(g, id, "role", text(role))];

// ---- a new projection: its GKQL program and its binding in the module's graph space ----
/** The parameter types the dialog offers and the value kind of each. */
export const PARAM_KINDS: Record<string, string> = { string: "Text", long: "Integer", real: "Decimal", bool: "Boolean" };

export interface ProjectionSpec {
  module: string; name: string; description: string; outputModule: string; graphName: string;
  /** inputs the graph keeps per element; left out when empty */
  history?: string;
  /** rows inputs: the port, the row type it reads and the source of the space that feeds it */
  rows?: { port: string; rowType: string; source: string }[];
  /** graph inputs: the port name, the space graph that feeds it and the ontology module the port expects */
  inputs: { port: string; graph: string; ontology: string }[];
  /** parameters: the name, its type (a key of PARAM_KINDS) and its default */
  params: { port: string; kind: string; default?: string }[];
}

/**
 * A new projection: its GKQL program (inputs and parameters, no verbs yet) and the module text with the graph's binding added to its space.
 * The program is stored first, then the module. Verbs come afterwards.
 */
export function newProjection(g: Graph, spec: ProjectionSpec, moduleText?: string): Result<{ file: ProjectionFile; text: string; module: { name: string; text: string } }> {
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
  const parameters: GkqlProgram["parameters"] = [];
  const from: Record<string, string> = {};
  for (const i of spec.inputs) {
    const f = `${space}/graph:${i.graph}`;
    if (!g.entity(f)) return no(`unknown graph ${i.graph}`);
    if (g.entity(i.ontology)?.type !== "Module") return no(`choose the ontology that ${i.port} expects`);
    from[i.port] = f;
  }
  for (const r of rows) {
    if (g.entity(r.rowType)?.type !== "RowType") return no(`choose the row type of ${r.port}`);
    if (g.entity(r.source)?.type !== "Source") return no(`choose the source that feeds ${r.port}`);
    from[r.port] = r.source;
  }
  for (const p of spec.params) {
    const kind = PARAM_KINDS[p.kind];
    if (!kind) return no(`a parameter is ${Object.keys(PARAM_KINDS).join(", ")}`);
    if (!p.default?.trim()) return no(`${p.port} needs a default in the GKQL program`);
    const def = typedValue({ name: `${p.port} default`, kind, required: true }, p.default);
    if (!def.ok) return def;
    const raw = p.default.trim().replace(/^(-?)0+(?=\d)/, "$1");
    const literal = kind === "Decimal" && !raw.includes(".") ? raw + ".0" : raw;
    if ((kind === "Integer" || kind === "Decimal") && !canEditGkqlStructure(literal)) return no(`${p.port} needs exact numeric support in the browser`);
    parameters.push({ name: p.port, kind, default: kind === "Text" ? p.default.trim() : readGkqlJson(literal) });
  }
  const retention = Number(history);
  if (!Number.isSafeInteger(retention)) return no("history must be a safe whole number");
  let document: { module?: string; space?: { graphs?: unknown[] } };
  try { document = readGkqlJson(moduleText ?? "") as typeof document; } catch { return no("check the GKQL module before adding a projection"); }
  if (document?.module !== spec.module || !Array.isArray(document.space?.graphs)) return no("the GKQL module must declare this graph space");
  if (!canEditGkqlStructure(moduleText ?? "")) return no("edit the graph binding in Module GKQL to preserve exact numbers");
  const binding = { name: spec.graphName, projection: spec.name, inputs: Object.fromEntries(Object.entries(from).map(([port, id]) => [port, last(id)])) };
  const program: GkqlProgram = {
    graph: spec.name, ontology: mod(spec.outputModule), history: retention,
    ...(spec.description.trim() ? { description: spec.description.trim() } : {}),
    inputs: [...spec.inputs.map((i) => ({ name: i.port, ontology: mod(i.ontology) })), ...rows.map((r) => ({ name: r.port, rows: `${mod(r.rowType)}.${last(r.rowType)}` }))],
    parameters, verbs: [], wires: [],
  };
  return ok({
    file: { projection: `${spec.module}.${spec.name}`, file: `${spec.module}.${spec.name}.gkql.json`, language: "gkql" },
    text: printGkql(program),
    module: { name: spec.module, text: JSON.stringify({ ...document, space: { ...document.space, graphs: [...document.space.graphs, binding] } }, null, 2) + "\n" },
  });
}
