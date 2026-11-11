// The catalog graph: definitions as a graph typed by the kernel (modules, entity and relationship types, identity domains,
// projections, operators, ports, wires, rules with their direction class). This file reads that graph; it never writes it.
// Ids are paths: d:<module>/<slot>:<name>/..., children hang off `contains` edges with a `slot` property.
import { Graph } from "./graph";
import type { Entity, Rel } from "./model";
import { valueText } from "./model";
import type { DefEdge, DefGraph, DefLoop, DefNode } from "./projection/defgraph";

export type DirectionClass = "forward" | "readonly" | "bijective" | "lens" | "sync";
export const DIRECTION_CLASSES: DirectionClass[] = ["forward", "readonly", "bijective", "lens", "sync"];
/** glyph and meaning of each direction class: never color alone */
export const DIRECTION: Record<DirectionClass, { glyph: string; label: string; help: string }> = {
  forward: { glyph: "⇢", label: "forward", help: "Changes flow from the source graphs to this view." },
  readonly: { glyph: "⇥", label: "readonly", help: "Derived from observation; no edits flow back." },
  bijective: { glyph: "⇄", label: "bijective", help: "Edits to the view map back to the source one to one." },
  lens: { glyph: "⇋", label: "lens", help: "Edits map back through a lens with a complement." },
  sync: { glyph: "↻", label: "sync", help: "Both sides are edited and reconciled." },
};
export const isDirectionClass = (s: string): s is DirectionClass => (DIRECTION_CLASSES as string[]).includes(s);

interface Child { slot: string; ordinal: number; id: string }

export interface ModuleInfo { id: string; name: string; version: string; digest: string; description: string; imports: string[] }
export interface RuleInfo { id: string; name: string; direction: DirectionClass | string; reason: string; governs: string[] }
export interface ProjectionInfo {
  id: string; name: string; module: string; purpose: string; forward: boolean; retain: number; outputModule: string | null;
  rules: RuleInfo[]; direction: DirectionClass;
}

const text = (e: Entity | Rel | undefined, p: string): string => { const v = e?.props[p]; return v ? valueText(v) : ""; };
const segName = (id: string): string => { const i = id.lastIndexOf(":"); return i < 0 ? id : id.slice(i + 1); };
export const moduleOfId = (id: string): string => { const s = id.startsWith("d:") ? id.slice(2) : id; const c = s.search(/[/@|]/); return c < 0 ? s : s.slice(0, c); };

export class Catalog {
  private kids = new Map<string, Child[]>();
  private out = new Map<string, Map<string, string[]>>();
  private inn = new Map<string, Map<string, string[]>>();

  constructor(readonly g: Graph) {
    for (const r of g.relationships()) {
      const add = (m: Map<string, Map<string, string[]>>, k: string, v: string) => {
        let t = m.get(k); if (!t) m.set(k, (t = new Map()));
        const l = t.get(r.type); if (l) l.push(v); else t.set(r.type, [v]);
      };
      add(this.out, r.source, r.target);
      add(this.inn, r.target, r.source);
      if (r.type === "contains") {
        const c = this.kids.get(r.source) ?? [];
        c.push({ slot: text(r, "slot"), ordinal: Number(text(r, "ordinal") || "0"), id: r.target });
        this.kids.set(r.source, c);
      }
    }
    for (const c of this.kids.values()) c.sort((a, b) => a.ordinal - b.ordinal || a.id.localeCompare(b.id));
  }

  entity(id: string): Entity | undefined { return this.g.entity(id); }
  children(id: string, slot?: string): string[] { return (this.kids.get(id) ?? []).filter((c) => !slot || c.slot === slot).map((c) => c.id); }
  targets(id: string, rel: string): string[] { return this.out.get(id)?.get(rel) ?? []; }
  sources(id: string, rel: string): string[] { return this.inn.get(id)?.get(rel) ?? []; }
  name(id: string): string { return text(this.entity(id), "name") || segName(id); }
  typeOf(id: string): string { return this.entity(id)?.type ?? ""; }
  /** Name of a type declared in another module is qualified by that module's last segment. */
  typeLabel(id: string, from: string): string {
    const n = this.name(id);
    const m = moduleOfId(id);
    return m === from ? n : `${m.split(".").pop()}.${n}`;
  }

  // ---- modules ----
  modules(): ModuleInfo[] {
    return [...this.g.entities()].filter((e) => e.type === "Module").map((m) => {
      const rel = this.sources(m.id, "releaseOf")[0];
      const imports = this.targets(m.id, "imports").map((r) => this.targets(r, "releaseOf")[0]).filter(Boolean).map((x) => this.name(x));
      return { id: m.id, name: text(m, "name"), version: text(this.entity(rel), "version"), digest: text(this.entity(rel), "digest"), description: text(m, "description"), imports };
    }).sort((a, b) => a.name.localeCompare(b.name));
  }

  // ---- projections and rules ----
  projections(): ProjectionInfo[] {
    return [...this.g.entities()].filter((e) => e.type === "Projection").map((p) => {
      const module = moduleOfId(p.id);
      const rules = this.children(p.id, "rule").map((r) => this.rule(r));
      const forward = text(p, "forward") === "true";
      const classes = rules.map((r) => r.direction).filter(isDirectionClass);
      // the projection's direction class is the strongest rule class: sync > lens > bijective > readonly > forward
      const rank = (c: DirectionClass) => DIRECTION_CLASSES.indexOf(c);
      const direction = classes.length ? classes.reduce((a, b) => (rank(b) > rank(a) ? b : a)) : forward ? "forward" : "readonly";
      const out = this.targets(p.id, "outputOntology")[0];
      return { id: p.id, name: text(p, "name"), module, purpose: text(p, "purpose"), forward, retain: Number(text(p, "retain") || "0"), outputModule: out ? this.name(out) : null, rules, direction };
    }).sort((a, b) => a.module.localeCompare(b.module) || a.name.localeCompare(b.name));
  }

  rule(id: string): RuleInfo {
    const r = this.entity(id);
    return { id, name: text(r, "name"), direction: text(r, "class") || "forward", reason: text(r, "reason"), governs: this.targets(id, "governs") };
  }

  // ---- expressions and operators ----
  expr(id: string): string {
    const e = this.entity(id);
    if (!e) return "?";
    switch (e.type) {
      case "FieldRef": return text(e, "path");
      case "Literal": { const v = e.props.value; return v ? (v.kind === "text" ? JSON.stringify(v.value) : valueText(v)) : "null"; }
      case "Call": {
        const fn = this.targets(id, "function")[0];
        const args = this.children(id, "arg").map((a) => this.expr(a));
        return `${fn ? this.name(fn) : "call"}(${args.join(", ")})`;
      }
      case "IdOf": {
        const dom = this.targets(id, "domain")[0];
        const comps = this.children(id, "component").map((c) => `${this.name(c)}=${this.children(c, "value").map((v) => this.expr(v)).join("")}`);
        return `id ${dom ? this.name(dom) : "?"}(${comps.join(", ")})`;
      }
      default: return this.name(id);
    }
  }

  /** Short description lines of an operator, from its subtree. */
  describeOp(id: string): string[] {
    const e = this.entity(id);
    if (!e) return [];
    const mod = moduleOfId(id);
    const t = (x: string) => this.typeLabel(x, mod);
    const ofType = this.targets(id, "ofType")[0];
    const sets = () => this.children(id, "set").map((s) => `${this.name(this.targets(s, "property")[0] ?? s)} = ${this.children(s, "value").map((v) => this.expr(v)).join("")}`);
    const assigns = (slot: string, prefix = "") => this.children(id, slot).map((a) => `${prefix}${text(this.entity(a), "target")} = ${this.children(a, "value").map((v) => this.expr(v)).join("")}`);
    switch (e.type) {
      case "Match": {
        const nodes = this.children(id, "node").map((n) => `${text(this.entity(n), "var")}: ${this.targets(n, "ofType")[0] ? t(this.targets(n, "ofType")[0]) : "?"}`);
        const edges = this.children(id, "edge").map((x) => {
          const f = this.targets(x, "from")[0], to = this.targets(x, "to")[0];
          return `${f ? text(this.entity(f), "var") : "?"} -[${text(this.entity(x), "var")}${this.targets(x, "ofType")[0] ? `:${t(this.targets(x, "ofType")[0])}` : ""}]-> ${to ? text(this.entity(to), "var") : "?"}`;
        });
        return [...nodes, ...edges];
      }
      case "Filter": return this.children(id, "predicate").map((p) => this.expr(p));
      case "Map": return assigns("assign");
      case "Group": case "Rollup": {
        const aggs = this.children(id, "agg").map((a) => `${text(this.entity(a), "target")} = ${text(this.entity(a), "fn") || "agg"}(${this.children(a, "value").map((v) => this.expr(v)).join("")})`);
        return [...assigns("key", "key "), ...aggs];
      }
      case "Join": return [`on ${this.children(id, "on").map((x) => this.expr(x)).join("")}`];
      case "Union": return [`union, conflict ${text(e, "conflict")}`];
      case "Distinct": return ["distinct rows"];
      case "Traverse": return [`via ${ofType ? this.name(ofType) : "?"} ${text(e, "direction")} depth ${text(e, "minDepth")} .. ${text(e, "maxDepth")}`, `from ${text(e, "start")} as ${text(e, "target")}`];
      case "Fixpoint": return [`repeat the step, at most ${text(e, "maxIterations")} rounds`];
      case "Values": return [`${this.children(id, "row").length} constant rows`];
      case "Window": return [`on ${text(e, "timestampField")}`, `width ${durationText(text(e, "width"))}, late ${text(e, "late")}`];
      case "Series": return [`read ${this.children(id, "read").map((r) => this.expr(r)).join(", ")}`, `within ${durationText(text(e, "within"))}`];
      case "Threshold": return [`${text(e, "target")} above ${text(e, "above")} for ${text(e, "enterAfter")} then ${text(e, "high")}`, `below ${text(e, "below")} for ${text(e, "exitAfter")} then ${text(e, "low")}`];
      case "EmitEntity": return [`entity ${ofType ? t(ofType) : "?"} (${text(e, "mode")})`, ...this.children(id, "id").map((i) => this.expr(i)), ...sets()];
      case "EmitRelationship": return [`relationship ${ofType ? t(ofType) : "?"} (${text(e, "mode")}, key ${text(e, "key")})`, `${this.children(id, "source").map((i) => this.expr(i)).join("")} → ${this.children(id, "target").map((i) => this.expr(i)).join("")}`, ...sets()];
      case "Lift": {
        const via = this.targets(id, "liftVia")[0], from = this.targets(id, "liftEdge")[0];
        const states = this.children(id, "state").map((x) => { const st = this.entity(x); return `state ${text(st, "target")} of ${text(st, "of")} above ${text(st, "above")} for ${text(st, "enterAfter")} then ${JSON.stringify(text(st, "high"))} below ${text(st, "below")} for ${text(st, "exitAfter")} then ${JSON.stringify(text(st, "low"))}`; });
        const when = this.children(id, "when").map((w) => `when ${this.expr(w)}`);
        const flags = [text(e, "images") ? `images ${text(e, "images")}` : "", text(e, "selfLoops") && text(e, "selfLoops") !== "drop" ? `selfloops ${text(e, "selfLoops")}` : "", text(e, "within") && text(e, "within") !== "86400000000000" ? `within ${durationText(text(e, "within"))}` : ""].filter(Boolean);
        return [`${ofType ? t(ofType) : "?"} from ${from ? t(from) : "?"} via ${via ? t(via) : "?"}`, ...when, ...assigns("carry", "carry "), ...assigns("keep", "keep "), ...assigns("derive", "derive "), ...states, ...(flags.length ? [flags.join(", ")] : [])];
      }
      case "Link": return [`${this.children(id, "left").map((i) => this.expr(i)).join("")} ≡ ${this.children(id, "right").map((i) => this.expr(i)).join("")}`];
      default: return [];
    }
  }

  /** The projection as a node graph: bindings, operators with ports, wires, output ontology and rules with direction class. */
  projectionGraph(projectionId: string): DefGraph {
    const p = this.entity(projectionId);
    const nodes: DefNode[] = [];
    const edges: DefEdge[] = [];
    if (!p) return { nodes, edges, direction: { mode: "forward-only", rules: 0, label: "" } };
    const info = this.projections().find((x) => x.id === projectionId)!;
    const portOwner = new Map<string, { node: string; name: string }>(); // port id -> card and port name
    const ports = (owner: string) => this.children(owner, "port").map((pid) => ({ id: pid, name: text(this.entity(pid), "name"), dir: text(this.entity(pid), "direction"), kind: text(this.entity(pid), "kind") }));
    for (const port of ports(projectionId)) {
      const exp = this.targets(port.id, "expects")[0];
      const vk = this.targets(port.id, "valueKind")[0], rt = this.targets(port.id, "rowType")[0];
      const def = text(this.entity(port.id), "default");
      const what = port.kind === "param" ? `${vk ? this.name(vk) : ""}${def ? ` = ${def}` : ""}` : port.kind === "rows" ? (rt ? this.name(rt) : "") : exp ? this.name(exp) : "";
      nodes.push({ id: port.id, kind: "binding", title: port.name, lines: [`${port.kind} ${what}`.trim()], inputs: [], outputs: ["out"], props: { kind: port.kind, direction: port.dir }, ref: port.id } as DefNode);
      portOwner.set(port.id, { node: port.id, name: "out" });
    }
    for (const opId of this.children(projectionId, "op")) {
      const op = this.entity(opId)!;
      const ps = ports(opId);
      nodes.push({
        id: opId, kind: "operator", opKind: op.type, title: text(op, "name") || segName(opId), lines: this.describeOp(opId),
        inputs: ps.filter((x) => x.dir === "in").map((x) => x.name), outputs: ps.filter((x) => x.dir === "out").map((x) => x.name), props: { kind: op.type, ...(op.type === "Fixpoint" ? { maxIterations: text(op, "maxIterations") } : {}) }, ref: opId,
      } as DefNode);
      for (const x of ps) portOwner.set(x.id, { node: opId, name: x.name });
      // inner operators (name#0) are pieces of a let binding; they are listed as operators too
    }
    const ontology = this.targets(projectionId, "outputOntology")[0];
    if (ontology) {
      const ontId = `ontology:${ontology}`;
      nodes.push({ id: ontId, kind: "ontology", title: this.name(ontology), lines: ["output ontology"], inputs: ["in"], outputs: [], props: {}, ref: ontology } as DefNode);
      for (const n of nodes) if (n.kind === "operator" && (n.opKind === "EmitEntity" || n.opKind === "EmitRelationship")) edges.push({ id: `emits:${n.id}`, kind: "emits", from: n.id, fromPort: "emit", to: ontId, toPort: "in" });
    }
    for (const r of info.rules) {
      const dir = isDirectionClass(r.direction) ? r.direction : "forward";
      nodes.push({ id: r.id, kind: "rule", title: r.name, lines: [`${DIRECTION[dir].glyph} ${r.direction}`, ...(r.reason ? [r.reason] : [])], inputs: [], outputs: [], props: { direction: r.direction }, direction: dir, ref: r.id } as DefNode);
      for (const g of r.governs) if (nodes.some((n) => n.id === g)) edges.push({ id: `governs:${r.id}>${g}`, kind: "binds", from: r.id, fromPort: "governs", to: g, toPort: "binding" });
    }
    // wires connect ports; a wire from a projection port feeds the operator that reads it
    for (const [pid, own] of portOwner) {
      for (const target of this.targets(pid, "wire")) {
        const to = portOwner.get(target);
        if (!to) continue;
        edges.push({ id: `wire:${pid}>${target}`, kind: "wire", from: own.node, fromPort: own.name, to: to.node, toPort: to.name, wireKey: `${pid}>${target}` });
      }
    }
    // fixpoints: the `over` relationship points at the port that carries the step result; the step is every operator on a wire path from the
    // fixpoint's out port to that operator, which the canvas draws as a loop region
    const loops: DefLoop[] = [];
    for (const n of nodes) {
      if (n.kind !== "operator") continue;
      const overPort = this.targets(n.id, "over")[0];
      if (!overPort) continue;
      const to = portOwner.get(overPort);
      if (to) edges.push({ id: `over:${n.id}>${overPort}`, kind: "over", from: to.node, fromPort: to.name, to: n.id, toPort: "out" });
      const next = new Map<string, string[]>(), prev = new Map<string, string[]>();
      for (const e of edges) if (e.kind === "wire") { next.set(e.from, [...(next.get(e.from) ?? []), e.to]); prev.set(e.to, [...(prev.get(e.to) ?? []), e.from]); }
      const reach = (start: string, adj: Map<string, string[]>) => { const seen = new Set<string>(); const st = [start]; while (st.length) { const x = st.pop()!; for (const y of adj.get(x) ?? []) if (!seen.has(y) && y !== n.id) { seen.add(y); st.push(y); } } return seen; };
      const down = reach(n.id, next), up = to ? reach(to.node, prev) : new Set<string>();
      const members = [...down].filter((m) => m === to?.node || up.has(m));
      loops.push({ fixpoint: n.id, members, result: to?.node ?? null, resultPort: overPort });
    }
    return { loops, nodes, edges, direction: { mode: info.direction === "forward" || info.direction === "readonly" ? "forward-only" : "bidirectional", rules: info.rules.length, label: `${DIRECTION[info.direction].glyph} ${info.direction}` } };
  }

  /** Nearest catalog element id that is a card or wire of a graph, for a diagnostic focus that points deeper into the tree. */
  nearest(id: string, has: (id: string) => boolean): string | null {
    let cur = id;
    while (cur) {
      if (has(cur)) return cur;
      const cut = Math.max(cur.lastIndexOf("/"), cur.lastIndexOf("|"));
      if (cut <= 1) return null;
      cur = cur.slice(0, cut);
    }
    return null;
  }

  // ---- ontologies of a module ----
  ontology(moduleId: string): { entityTypes: string[]; relationshipTypes: string[]; propertyTypes: string[]; identityDomains: string[]; constraints: string[] } {
    const pick = (slot: string) => this.children(moduleId, slot);
    return { entityTypes: pick("entity"), relationshipTypes: pick("rel"), propertyTypes: pick("prop"), identityDomains: pick("identity"), constraints: pick("constraint") };
  }
}

const txt = (v: string) => ({ kind: "text" as const, value: v });

/**
 * The ontology of one module as a graph: entity types are nodes, relationship types are edges from each source type to each
 * target type, `extends` links types, identity domains attach to the type they identify. Types of imported modules that the
 * module refers to appear as nodes of their own module.
 */
export function moduleOntologyGraph(c: Catalog, moduleId: string): Graph {
  const mod = moduleOfId(moduleId);
  const entities = new Map<string, Entity>();
  const rels: Rel[] = [];
  const typeNode = (id: string): string => {
    if (!entities.has(id)) {
      const e = c.entity(id);
      const props = c.targets(id, "hasProperty").map((p) => `${c.name(p)}${text(c.entity(p), "role") === "telemetry" ? " (telemetry)" : ""}`);
      entities.set(id, { id, type: "EntityType", props: { name: txt(c.typeLabel(id, mod)), module: txt(moduleOfId(id)), properties: txt(props.join(", ")), abstract: txt(text(e, "abstract")) } });
    }
    return id;
  };
  const o = c.ontology(moduleId);
  for (const t of o.entityTypes) typeNode(t);
  for (const r of o.relationshipTypes) {
    const name = c.name(r);
    const props = c.targets(r, "hasProperty").map((p) => c.name(p)).join(", ");
    for (const s of c.targets(r, "sourceType")) for (const t of c.targets(r, "targetType")) {
      rels.push({ id: `${r}|${s}>${t}`, source: typeNode(s), target: typeNode(t), type: name, props: { properties: txt(props) } });
    }
    for (const sup of c.targets(r, "extends")) void sup; // relationship type inheritance is shown in the text
  }
  for (const t of o.entityTypes) for (const sup of c.targets(t, "extends")) rels.push({ id: `${t}|extends|${sup}`, source: typeNode(t), target: typeNode(sup), type: "extends", props: {} });
  for (const d of o.identityDomains) {
    const id = d;
    entities.set(id, { id, type: "IdentityDomain", props: { name: txt(c.name(d)), module: txt(mod), canonicalization: txt(text(c.entity(d), "canonicalizationVersion")) } });
    for (const t of c.targets(d, "identifies")) rels.push({ id: `${d}|identifies|${t}`, source: id, target: typeNode(t), type: "identifies", props: {} });
  }
  return Graph.fromSnapshot({ version: { tenant: "catalog", graph: moduleId, revision: "1" }, entities: [...entities.values()], relationships: rels });
}

export function durationText(ns: string): string {
  const n = Number(ns);
  if (!Number.isFinite(n) || n <= 0) return ns;
  const s = n / 1e9;
  if (s % 86400 === 0) return `${s / 86400}d`;
  if (s % 3600 === 0) return `${s / 3600}h`;
  if (s % 60 === 0) return `${s / 60}m`;
  return `${s}s`;
}
