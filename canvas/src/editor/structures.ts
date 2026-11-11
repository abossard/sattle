// Module-level declarations and rule programs as catalog edits: row types with fields, constraints, equivalence policies, value roles,
// and the parts of a rule. Pure: graph and palette in, edits (or a reason) out. The slots, types, parameters, constraint kinds, modes
// and value kinds all come from `GET /palette` (engine 3.2: declarations, rowTypes, equivalencePolicies, rules, valueRoles); an engine
// that does not describe them gets a message, not a guess. Only the relationship names that connect the parts (`constrains`,
// `onProperty`, `between`, `createKey`, `governs`, `valueKind`) are written here, because the palette names slots but not relationships.
import { Catalog } from "../core/catalog";
import type { Graph } from "../core/graph";
import type { Edit } from "../core/model";
import type { GraphValue } from "../core/wire-types";
import { addSetEdits, type Result } from "./commands";
import { contains, ent, rel, setPropEdit, text } from "./patches";
import type { Palette, PaletteDeclaration, PaletteParam } from "./palette";

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
/** An operator applies an equivalence policy (`policy` relationship); replaces the current one, or removes it with an empty id. */
export function setPolicyEdits(g: Graph, op: string, policy: string): Edit[] {
  const old = [...g.relationships()].filter((r) => r.source === op && r.type === "policy").map((r): Edit => ({ kind: "relationship", id: r.id, before: r, after: null }));
  return [...old, ...(policy ? [rel(op, "policy", policy)] : [])];
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

// ---- rule programs ----
/** Rule properties the class defines through its policies (`on delete` is the property `onDelete`). */
export const policyProp = (policy: string): string => policy.replace(/\s+(\w)/g, (_, c: string) => c.toUpperCase());
/** Set a rule property typed by the palette's rule parameters; an empty value removes it. */
export function setRulePropEdits(g: Graph, rule: string, name: string, raw: string, palette: Palette): Result<Edit[]> {
  const p = palette.rules?.params.find((x) => x.name === name);
  if (!p) return no(`the engine's palette has no rule parameter ${name}`);
  if (!raw.trim()) return ok([setPropEdit(g, rule, name, null)]);
  const v = typedValue(p, raw);
  return v.ok ? ok([setPropEdit(g, rule, name, v.value)]) : v;
}
export function setCreateKeyEdits(g: Graph, rule: string, domain: string): Result<Edit[]> {
  if (domain && g.entity(domain)?.type !== "IdentityDomain") return no("choose an identity domain");
  const old = [...g.relationships()].filter((r) => r.source === rule && r.type === "createKey").map((r): Edit => ({ kind: "relationship", id: r.id, before: r, after: null }));
  return ok([...old, ...(domain ? [rel(rule, "createKey", domain)] : [])]);
}
/** A property set by a lens program (the rule's `SetProperty` slots: `createDefault`, `deleteSet`). */
export const addRuleSetEdits = (g: Graph, rule: string, slot: string, property: string, src: string, palette: Palette): Result<Edit[]> => addSetEdits(g, rule, property, src, palette, slot);

/** A rule governs (or stops governing) an operator or projection port. */
export function governsEdits(g: Graph, rule: string, target: string, on: boolean): Result<Edit[]> {
  if (on) return g.entity(target) ? ok([rel(rule, "governs", target)]) : no("unknown target");
  const r = [...g.relationships()].find((x) => x.source === rule && x.type === "governs" && x.target === target);
  return r ? ok([{ kind: "relationship", id: r.id, before: r, after: null }]) : no("the rule does not govern this");
}

// ---- a new projection and its place in the graph space ----
export interface ProjectionSpec {
  module: string; name: string; description: string; outputModule: string; graphName: string;
  /** `purpose` of the projection (from the palette: ingestion, view, ...); a view when empty */
  purpose?: string;
  /** revisions the projection retains (a required parameter of the declaration); left out when empty */
  retain?: string;
  /** rows inputs: the port, the row type it reads and the source of the space that feeds it */
  rows?: { port: string; rowType: string; source: string }[];
  /** graph inputs: the port name, the space graph that feeds it and the ontology module the port expects */
  inputs: { port: string; graph: string; ontology: string }[];
  /** parameters: the port name, its value kind, the literal the space binds (may be empty when there is a default) and the default */
  params: { port: string; kind: string; value: string; default?: string }[];
}

/**
 * A projection with its input ports (graphs and parameters), its output ontology, and the graph of the space that it produces with one
 * binding per port (a graph port is bound from a space graph, a parameter port to a literal). The operators are added afterwards.
 */
export function newProjectionEdits(g: Graph, spec: ProjectionSpec, palette: Palette): Result<Edit[]> {
  const mod = `d:${spec.module}`, space = [...g.entities()].find((e) => e.type === "GraphSpace")?.id;
  if (!space) return no("the catalog has no graph space");
  if (!nameOk(spec.name) || !nameOk(spec.graphName)) return no("names are letters, digits and _");
  const proj = `${mod}/projection:${spec.name}`, graph = `${space}/graph:${spec.graphName}`;
  if (g.entity(proj)) return no(`projection ${spec.name} already exists`);
  if (g.entity(graph)) return no(`graph ${spec.graphName} already exists in the space`);
  if (!g.entity(spec.outputModule)) return no("choose the output ontology");
  const purposes = palette.declarations.projection?.params.find((p) => p.name === "purpose")?.values;
  const purpose = spec.purpose || "view";
  if (purposes && !purposes.includes(purpose)) return no(`purpose must be one of ${purposes.join(", ")}`);
  const rows = spec.rows ?? [];
  const ports = [...spec.inputs.map((i) => i.port), ...rows.map((r) => r.port), ...spec.params.map((p) => p.port)];
  if (ports.some((p) => !nameOk(p)) || new Set(ports).size !== ports.length) return no("port names are distinct names of letters, digits and _");
  const edits: Edit[] = [
    ent(proj, "Projection", { name: text(spec.name), purpose: text(purpose), forward: { kind: "boolean", value: true }, ...(spec.retain?.trim() ? { retain: { kind: "integer", value: spec.retain.trim() } } : {}), ...(spec.description ? { description: text(spec.description) } : {}) }),
    rel(proj, "outputOntology", spec.outputModule), contains(mod, proj, "projection"),
    ent(graph, "Graph", { name: text(spec.graphName) }), contains(space, graph, "graph"), rel(graph, "producedBy", proj),
  ];
  const bind = (port: string, props: Record<string, GraphValue>, extra: Edit[]) => {
    const b = `${space}/binding:${spec.graphName}.${port}`;
    edits.push(ent(b, "Binding", props), contains(space, b, "binding"), rel(b, "bindsPort", `${proj}/port:${port}`), ...extra);
  };
  for (const i of spec.inputs) {
    const from = `${space}/graph:${i.graph}`;
    if (!g.entity(from)) return no(`unknown graph ${i.graph}`);
    if (!g.entity(i.ontology)) return no(`choose the ontology that ${i.port} expects`);
    const port = `${proj}/port:${i.port}`;
    edits.push(ent(port, "Port", { name: text(i.port), direction: text("in"), kind: text("graph") }), rel(port, "expects", i.ontology), contains(proj, port, "port"));
    bind(i.port, {}, [rel(`${space}/binding:${spec.graphName}.${i.port}`, "boundFrom", from)]);
  }
  for (const r of rows) {
    if (g.entity(r.rowType)?.type !== "RowType") return no(`choose the row type of ${r.port}`);
    if (g.entity(r.source)?.type !== "Source") return no(`choose the source that feeds ${r.port}`);
    const port = `${proj}/port:${r.port}`;
    edits.push(ent(port, "Port", { name: text(r.port), direction: text("in"), kind: text("rows") }), rel(port, "rowType", r.rowType), contains(proj, port, "port"));
    bind(r.port, {}, [rel(`${space}/binding:${spec.graphName}.${r.port}`, "boundFrom", r.source)]);
  }
  for (const p of spec.params) {
    if (!palette.valueKinds.includes(p.kind)) return no(`unknown value kind ${p.kind}`);
    const check = (raw: string, what: string): Result<GraphValue> => typedValue({ name: what, kind: p.kind, required: true }, raw);
    const def = p.default?.trim() ? check(p.default, `${p.port} default`) : null;
    if (def && !def.ok) return def;
    const val = p.value.trim() ? check(p.value, p.port) : null;
    if (val && !val.ok) return val;
    if (!val && !def) return no(`${p.port} needs a value bound in the space or a default`);
    const port = `${proj}/port:${p.port}`;
    edits.push(ent(port, "Port", { name: text(p.port), direction: text("in"), kind: text("param"), ...(def ? { default: def.value } : {}) }), rel(port, "valueKind", `${KERNEL}${p.kind}`), contains(proj, port, "port"));
    if (val) bind(p.port, { value: val.value }, []);
  }
  return ok(edits);
}

/** The literal a parameter binding of a space holds, typed by the parameter port's value kind (`Binding.value`). */
export function bindParamEdits(g: Graph, binding: string, raw: string): Result<Edit[]> {
  const b = g.entity(binding);
  if (!b || b.type !== "Binding") return no("not a binding");
  const port = [...g.relationships()].find((r) => r.source === binding && r.type === "bindsPort")?.target;
  const kind = last([...g.relationships()].find((r) => r.source === port && r.type === "valueKind")?.target ?? "Text");
  const v = typedValue({ name: "value", kind, required: true }, raw);
  return v.ok ? ok([setPropEdit(g, binding, "value", v.value)]) : v;
}

/** A graph of the space starts binding a parameter port that had only its default: the binding holds the literal. */
export function addBindingEdits(g: Graph, port: string, graph: string, raw: string): Result<Edit[]> {
  if (g.entity(port)?.type !== "Port") return no("not a port");
  if (g.entity(graph)?.type !== "Graph") return no("not a graph of the space");
  const space = graph.slice(0, graph.indexOf("/graph:"));
  const b = `${space}/binding:${last(graph)}.${last(port)}`;
  if (g.entity(b)) return no("this graph already binds the parameter");
  const kind = last([...g.relationships()].find((r) => r.source === port && r.type === "valueKind")?.target ?? "Text");
  const v = typedValue({ name: "value", kind, required: true }, raw);
  if (!v.ok) return v;
  return ok([ent(b, "Binding", { value: v.value }), contains(space, b, "binding"), rel(b, "bindsPort", port)]);
}
/** The default of a parameter port, typed by its value kind; empty removes it. */
export function setPortDefaultEdits(g: Graph, port: string, raw: string): Result<Edit[]> {
  if (g.entity(port)?.type !== "Port") return no("not a port");
  if (!raw.trim()) return ok([setPropEdit(g, port, "default", null)]);
  const kind = last([...g.relationships()].find((r) => r.source === port && r.type === "valueKind")?.target ?? "Text");
  const v = typedValue({ name: "default", kind, required: true }, raw);
  return v.ok ? ok([setPropEdit(g, port, "default", v.value)]) : v;
}
