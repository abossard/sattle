// Pure view calculations for data graphs: filters, search, neighbourhood focus, group collapse, flag layers.
import type { Graph } from "../core/graph";
import { type Entity, valueText } from "../core/model";
import type { OntologyDefinition } from "../core/wire-types";

export interface PropertyFilter { key: string; op: "equals" | "contains" | "not equals"; value: string }
export interface Filter { hiddenTypes: Set<string>; prop: PropertyFilter | null; search: string }
/** Room in pixels at the left and right of a fitted graph, so the label of a node at its edge is not cut off. Labels are drawn centred under their node. */
export function labelPadding(labels: string[]): number {
  const longest = labels.reduce((m, l) => Math.max(m, l.length), 0);
  return Math.min(120, Math.max(40, 8 + 3.4 * Math.min(longest, 32)));
}
export const emptyFilter = (): Filter => ({ hiddenTypes: new Set(), prop: null, search: "" });

export function entityMatchesProp(e: Entity, f: PropertyFilter): boolean {
  const v = e.props[f.key];
  const text = v ? valueText(v) : "";
  switch (f.op) {
    case "equals": return v !== undefined && text === f.value;
    case "not equals": return text !== f.value;
    case "contains": return text.toLowerCase().includes(f.value.toLowerCase());
  }
}

export function entityMatchesSearch(e: Entity, q: string): boolean {
  if (!q) return false;
  const s = q.toLowerCase();
  if (e.id.toLowerCase().includes(s) || e.type.toLowerCase().includes(s)) return true;
  for (const [k, v] of Object.entries(e.props)) if (k.toLowerCase().includes(s) || valueText(v).toLowerCase().includes(s)) return true;
  return false;
}

/** Node slots within `depth` hops of `root`, following edges in both directions. */
export function neighbourhood(g: Graph, root: number, depth: number): Set<number> {
  const seen = new Set<number>([root]);
  let frontier = [root];
  for (let d = 0; d < depth; d++) {
    const next: number[] = [];
    for (const v of frontier) {
      for (const es of g.out[v]) { const w = g.edgeDst[es]; if (w >= 0 && !seen.has(w)) { seen.add(w); next.push(w); } }
      for (const es of g.inn[v]) { const w = g.edgeSrc[es]; if (w >= 0 && !seen.has(w)) { seen.add(w); next.push(w); } }
    }
    frontier = next;
  }
  return seen;
}

export interface Grouping { type: string; /** true when edges point child -> parent */ childIsSource: boolean }

/** Which side of a relationship type is the parent: the side with fewer distinct nodes. */
export function detectGrouping(g: Graph, type: string, childIsSource?: boolean): Grouping {
  if (childIsSource !== undefined) return { type, childIsSource };
  const s = new Set<string>(), t = new Set<string>();
  for (const r of g.relationships()) if (r.type === type) { s.add(r.source); t.add(r.target); }
  return { type, childIsSource: t.size <= s.size };
}

/** The default of `FilterState.grouping`: collapse the supporting elements the ontology points out (see `supportRelation`). */
export const SUPPORT_GROUPING = "@support";

/**
 * The relationship type through which elements only support others, read from the ontology and its value roles alone: an end whose types
 * carry no property with roles (nothing says what state it is in) attached to an end whose types do. In the health roll-up that is the
 * evidence of a component: components and signals have states, evidence has none. Null when no relationship type has that shape, which is
 * every graph without value roles.
 */
export function supportRelation(ontology: OntologyDefinition, hinted: ReadonlySet<string>): { type: string; childIsSource: boolean } | null {
  const stateful = new Set(ontology.entityTypes.filter((t) => t.properties.some((p) => hinted.has(p.value))).map((t) => t.id.value));
  if (!stateful.size) return null;
  for (const r of ontology.relationshipTypes) {
    const src = r.sourceTypes.map((t) => t.value), dst = r.targetTypes.map((t) => t.value);
    if (!src.length || !dst.length) continue;
    if (src.every((t) => !stateful.has(t)) && dst.every((t) => stateful.has(t))) return { type: r.id.value, childIsSource: true };
    if (dst.every((t) => !stateful.has(t)) && src.every((t) => stateful.has(t))) return { type: r.id.value, childIsSource: false };
  }
  return null;
}

/**
 * Anchor of every node: the topmost collapsed ancestor, or the node itself.
 * Parents come from the grouping relationship; each node uses its first parent. Cycles are cut.
 */
export function computeAnchors(g: Graph, grouping: Grouping, collapsed: ReadonlySet<number>): Int32Array {
  const n = g.nodes.length;
  const parent = new Int32Array(n).fill(-1);
  for (let es = 0; es < g.edges.length; es++) {
    const r = g.edges[es];
    if (!r || r.type !== grouping.type) continue;
    const a = g.edgeSrc[es], b = g.edgeDst[es];
    if (a < 0 || b < 0) continue;
    const child = grouping.childIsSource ? a : b, par = grouping.childIsSource ? b : a;
    if (parent[child] < 0) parent[child] = par;
  }
  const anchor = new Int32Array(n);
  for (let i = 0; i < n; i++) {
    let v = i, top = i, steps = 0;
    while (parent[v] >= 0 && steps++ < 64) { v = parent[v]; if (collapsed.has(v)) top = v; }
    anchor[i] = top;
  }
  return anchor;
}

/** Number of descendants hidden under each collapsed group. */
export function hiddenCounts(anchor: Int32Array, collapsed: ReadonlySet<number>): Map<number, number> {
  const m = new Map<number, number>();
  for (let i = 0; i < anchor.length; i++) if (anchor[i] !== i && collapsed.has(anchor[i])) m.set(anchor[i], (m.get(anchor[i]) ?? 0) + 1);
  return m;
}

/** Per-slot bit flags with named layers; each layer owns one bit and reports the slots it changed. */
export class FlagLayer {
  bits: Uint8Array;
  private members = new Map<number, Set<number>>();
  constructor(size = 0) { this.bits = new Uint8Array(size); }
  ensure(size: number): void { if (size > this.bits.length) { const b = new Uint8Array(Math.max(size, this.bits.length * 2)); b.set(this.bits); this.bits = b; } }
  /** Replace the members of a layer; returns slots whose flags changed. */
  set(bit: number, slots: Iterable<number>): number[] {
    const next = new Set(slots);
    const prev = this.members.get(bit) ?? new Set<number>();
    const changed: number[] = [];
    for (const s of prev) if (!next.has(s)) { this.ensure(s + 1); this.bits[s] &= ~bit; changed.push(s); }
    for (const s of next) if (!prev.has(s)) { this.ensure(s + 1); this.bits[s] |= bit; changed.push(s); }
    this.members.set(bit, next);
    return changed;
  }
  members_(bit: number): ReadonlySet<number> { return this.members.get(bit) ?? new Set(); }
}

/**
 * A projection graph with nodes, no edges and relationship types in its ontology: the graph is not broken to draw, it holds no edges.
 * Says so (and which relationship types the ontology declares), so the missing lines are never mistaken for a rendering fault.
 */
export function edgelessNotice(role: string, nodes: number, edges: number, declared: string[]): string | null {
  // the engine lists the placeholder `NoType` in every ontology; it is not a relationship type an author declared
  const relationshipTypes = declared.filter((t) => t !== "NoType");
  if (role !== "projection" || nodes === 0 || edges > 0 || relationshipTypes.length === 0) return null;
  const shown = relationshipTypes.slice(0, 3).join(", ") + (relationshipTypes.length > 3 ? ` and ${relationshipTypes.length - 3} more` : "");
  return `This graph has ${nodes.toLocaleString("en")} ${nodes === 1 ? "node" : "nodes"} and no edges. Its ontology declares relationship types (${shown}), but the projection has not produced any, so nothing is missing from the drawing.`;
}

/** What `FilterState.grouping` stands for on a graph: the supporting-elements default, a relationship type, or nothing. */
export function resolveGrouping(info: { ontology: OntologyDefinition; presentation?: { property: string }[] }, setting: string): { type: string; childIsSource?: boolean } | null {
  if (setting === SUPPORT_GROUPING) return supportRelation(info.ontology, new Set((info.presentation ?? []).map((h) => h.property)));
  return setting ? { type: setting } : null;
}
