// A hierarchical graph as a tree: pure, nothing here knows what the graph is about. The ontology names the relationship type that is the hierarchy,
// the types of the other relationships that only support a node (evidence) or cross the tree (dependencies), and per entity type the properties that
// hold its own state and its rolled-up state; value roles (engine 3.2) say what each value means. Everything below reads those and the graph.
import type { Graph } from "../core/graph";
import type { Entity } from "../core/model";
import { valueText } from "../core/model";
import type { PresentationHint, Role, TreeSpec } from "../source/presentation";

export type { TreeSpec };

export interface StateCell { value: string; role: Role }
export interface TreeNode {
  id: string; type: string; label: string; parent: string | null; children: string[];
  own: StateCell | null; rolled: StateCell | null;
  /** how bad the node is: the severity property when the type has one, otherwise the role of the rolled-up (or own) value */
  severity: number;
  /** support relationships and cross relationships incident to the node */
  supporting: number; cross: number;
  /** authored curation: the record that changed the element, whether it is marked critical, whether it is hidden, and how it rolls up its children */
  curatedBy: string | null; critical: boolean; hidden: boolean; policy: { mode: string; share: string | null } | null;
  /** the descendants by state (the engine's counts) or, without them, the direct children by rolled-up state, worst first */
  counts: { value: string; role: Role; n: number }[];
}
export interface TreeModel { nodes: Map<string, TreeNode>; roots: string[]; spec: TreeSpec }

/** Roles from the worst to the mildest: what a severity falls back to, and the order the counts are shown in. */
const WORST_FIRST: Role[] = ["danger", "warning", "unknown", "ok", "neutral"];
export const roleSeverity = (r: Role): number => WORST_FIRST.length - WORST_FIRST.indexOf(r);
/** The same fallback on the engine's own scale (-1 unknown, 0 healthy, 1 degraded, 2 unhealthy), for a graph whose severity property is declared: one scale per tree. */
const ENGINE_SEVERITY: Record<Role, number> = { danger: 2, warning: 1, ok: 0, neutral: 0, unknown: -1 };
/** A node where something is wrong: its rolled-up state (which includes its own) is a warning or a danger. */
export const isWrong = (n: TreeNode): boolean => (n.rolled ?? n.own)?.role === "warning" || (n.rolled ?? n.own)?.role === "danger";


const cellOf = (e: Entity, property: string | undefined, hints: ReadonlyMap<string, Record<string, Role>>): StateCell | null => {
  const v = property ? e.props[property] : undefined;
  if (!v) return null;
  const value = valueText(v);
  return { value, role: hints.get(property!)?.[value] ?? "neutral" };
};

export function buildTree(g: Graph, spec: TreeSpec, hints: readonly PresentationHint[]): TreeModel {
  const roles = new Map(hints.map((h) => [h.property, h.roles]));
  const nodes = new Map<string, TreeNode>();
  const inTree = (e: Entity) => (spec.types ? spec.types.includes(e.type) : true);
  // a node without the severity property is ranked on the scale the others use: the engine's when any type declares one, else the role scale
  const declared = Object.values(spec.state).some((s) => s.severity);
  for (const e of g.entities()) {
    if (!inTree(e)) continue;
    const st = spec.state[e.type] ?? {};
    const own = cellOf(e, st.own, roles), rolled = cellOf(e, st.rolledUp, roles);
    const kind = st.kind && e.props[st.kind] ? valueText(e.props[st.kind]) : e.type;
    const sev = st.severity ? e.props[st.severity] : undefined;
    nodes.set(e.id, {
      id: e.id, type: kind, label: g.label(e), parent: null, children: [], own, rolled,
      severity: sev && /^-?\d+$/.test(valueText(sev)) ? Number(valueText(sev)) : declared ? ENGINE_SEVERITY[(rolled ?? own)?.role ?? "neutral"] : roleSeverity((rolled ?? own)?.role ?? "neutral"),
      supporting: 0, cross: 0, counts: [],
      curatedBy: st.curatedBy && e.props[st.curatedBy] ? valueText(e.props[st.curatedBy]) || null : null,
      critical: !!st.critical && valueText(e.props[st.critical] ?? { kind: "boolean", value: false }) === "true",
      hidden: !!st.hidden && valueText(e.props[st.hidden] ?? { kind: "boolean", value: false }) === "true",
      policy: st.mode && e.props[st.mode] ? { mode: valueText(e.props[st.mode]), share: st.share && e.props[st.share] ? valueText(e.props[st.share]) : null } : null,
    });
  }
  for (const r of g.relationships()) {
    if (r.type === spec.hierarchy.relationship) {
      const child = spec.hierarchy.childIsSource ? r.source : r.target, parent = spec.hierarchy.childIsSource ? r.target : r.source;
      const c = nodes.get(child), p = nodes.get(parent);
      // each node has one parent (the first), and a cycle is never followed
      if (c && p && c.parent === null && child !== parent) { c.parent = parent; p.children.push(child); }
      continue;
    }
    const role = spec.edgeRoles[r.type];
    if (!role) continue;
    for (const end of [r.source, r.target]) { const n = nodes.get(end); if (n) { if (role === "support") n.supporting++; else n.cross++; } }
  }
  // a cycle (a parent chain that never reaches a root) is cut at the node that closes it
  for (const n of nodes.values()) {
    const seen = new Set<string>([n.id]);
    let p = n.parent;
    while (p !== null) {
      if (seen.has(p)) { const par = nodes.get(n.parent!); if (par) par.children = par.children.filter((x) => x !== n.id); n.parent = null; break; }
      seen.add(p); p = nodes.get(p)?.parent ?? null;
    }
  }
  for (const n of nodes.values()) {
    // the engine's count of descendants per state, when the ontology names the properties; otherwise the direct children counted here
    const given = spec.state[g.entity(n.id)?.type ?? ""]?.counts;
    if (given) {
      const e = g.entity(n.id)!;
      n.counts = Object.entries(given).map(([value, property]) => ({ value, role: roles.get(spec.state[e.type]?.rolledUp ?? "")?.[value] ?? "neutral" as Role, n: Number(valueText(e.props[property] ?? { kind: "integer", value: "0" })) || 0 })).filter((c) => c.n > 0).sort((a, b) => ROLES_WORST(a.role) - ROLES_WORST(b.role) || a.value.localeCompare(b.value));
      continue;
    }
    const by = new Map<string, { value: string; role: Role; n: number }>();
    for (const c of n.children) { const s = nodes.get(c)?.rolled ?? nodes.get(c)?.own; if (!s) continue; const k = by.get(s.value) ?? { ...s, n: 0 }; k.n++; by.set(s.value, k); }
    n.counts = [...by.values()].sort((a, b) => ROLES_WORST(a.role) - ROLES_WORST(b.role) || a.value.localeCompare(b.value));
  }
  const roots = [...nodes.values()].filter((n) => n.parent === null).map((n) => n.id);
  return { nodes, roots, spec };
}
const ROLES_WORST = (r: Role): number => WORST_FIRST.indexOf(r);

export const labelCompare = (a: TreeNode, b: TreeNode): number => a.label.localeCompare(b.label, undefined, { numeric: true }) || a.id.localeCompare(b.id);
/** Worst first, then by name: what a person reading a health tree wants at the top. */
export const severityCompare = (a: TreeNode, b: TreeNode): number => b.severity - a.severity || labelCompare(a, b);

export interface TreeView { expanded: ReadonlySet<string>; onlyWrong: boolean; query: string; sort: "severity" | "label"; showHidden?: boolean }
export interface TreeRow { node: TreeNode; level: number; expanded: boolean; hasChildren: boolean; posInSet: number; setSize: number; match: boolean }

/** The node and every ancestor of it, for each given node. */
export function ancestorsOrSelf(m: TreeModel, ids: Iterable<string>): Set<string> {
  const out = new Set<string>();
  for (const id of ids) for (let x: string | null = id; x !== null && !out.has(x); x = m.nodes.get(x)?.parent ?? null) out.add(x);
  return out;
}
/** Only the ancestors: the path to each node, not the node itself. */
export function ancestorsOf(m: TreeModel, ids: Iterable<string>): Set<string> {
  const out = new Set<string>();
  for (const id of ids) for (let x = m.nodes.get(id)?.parent ?? null; x !== null && !out.has(x); x = m.nodes.get(x)?.parent ?? null) out.add(x);
  return out;
}
export const wrongNodes = (m: TreeModel): string[] => [...m.nodes.values()].filter(isWrong).map((n) => n.id);
/** What opens by default: the paths to the nodes where something is wrong, so the tree starts on them and everything else is collapsed. */
export const attentionPaths = (m: TreeModel): Set<string> => ancestorsOf(m, wrongNodes(m));
/** The ids that match a search by label, type or id. */
export function searchMatches(m: TreeModel, query: string): string[] {
  const q = query.trim().toLowerCase();
  if (!q) return [];
  return [...m.nodes.values()].filter((n) => n.label.toLowerCase().includes(q) || n.type.toLowerCase().includes(q) || n.id.toLowerCase().includes(q)).map((n) => n.id);
}
/** `id` and everything below it that has children. */
export function expandBelow(m: TreeModel, id: string): Set<string> {
  const out = new Set<string>();
  const stack = [id];
  while (stack.length) { const x = stack.pop()!; const n = m.nodes.get(x); if (n?.children.length) { out.add(x); stack.push(...n.children); } }
  return out;
}

/**
 * The rows to show, in order. With a filter ("only where something is wrong") or a search, a node is in when it satisfies all of them or has a
 * descendant that does, so the path to it stays; an expanded node shows the children that are in. A search also opens the paths to its matches.
 */
export function visibleRows(m: TreeModel, v: TreeView): { rows: TreeRow[]; matches: Set<string> } {
  const matchIds = v.query.trim() ? searchMatches(m, v.query) : [];
  const matches = new Set(matchIds);
  const wrong = new Set(wrongNodes(m));
  const satisfy = (id: string) => (!v.onlyWrong || wrong.has(id)) && (!v.query.trim() || matches.has(id));
  const filtered = v.onlyWrong || !!v.query.trim();
  const include = filtered ? ancestorsOrSelf(m, [...m.nodes.keys()].filter(satisfy)) : null;
  const reveal = v.query.trim() ? ancestorsOf(m, [...matches].filter((id) => !include || include.has(id))) : null;
  const compare = v.sort === "label" ? labelCompare : severityCompare;
  const rows: TreeRow[] = [];
  // an element curated to be hidden is left out with everything below it, unless asked for
  const shownNode = (n: TreeNode) => v.showHidden || !n.hidden;
  const walk = (ids: string[], level: number) => {
    const kids = ids.map((id) => m.nodes.get(id)!).filter((n) => shownNode(n) && (!include || include.has(n.id))).sort(compare);
    kids.forEach((n, i) => {
      const shown = n.children.filter((c) => shownNode(m.nodes.get(c)!) && (!include || include.has(c))).length;
      const open = shown > 0 && (v.expanded.has(n.id) || !!reveal?.has(n.id));
      rows.push({ node: n, level, expanded: open, hasChildren: n.children.length > 0, posInSet: i + 1, setSize: kids.length, match: matches.has(n.id) });
      if (open) walk(n.children, level + 1);
    });
  };
  walk(m.roots, 1);
  return { rows, matches };
}

/** The type-ahead target: the first row after `from` (wrapping) whose label starts with `typed`. */
export function typeAhead(rows: readonly TreeRow[], from: number, typed: string): number {
  const q = typed.toLowerCase();
  if (!q) return -1;
  for (let k = 1; k <= rows.length; k++) { const i = (from + k) % rows.length; if (rows[i].node.label.toLowerCase().startsWith(q)) return i; }
  return -1;
}

/** What explains a node's state: the children that drive its roll-up (the worst ones), and the elements that support it with their properties. */
export interface Explanation {
  drivers: TreeNode[];
  support: { id: string; type: string; label: string; relationship: string; props: [string, string][] }[];
  cross: { id: string; label: string; relationship: string; direction: "out" | "in" }[];
}
export function explainNode(g: Graph, m: TreeModel, id: string): Explanation | null {
  const n = m.nodes.get(id);
  if (!n) return null;
  const kids = n.children.map((c) => m.nodes.get(c)!).filter(Boolean);
  const worst = kids.length ? Math.max(...kids.map((k) => k.severity)) : -Infinity;
  // a child drives the roll-up when it is as bad as the worst child and at least as bad as the node itself
  const drivers = kids.filter((k) => k.severity === worst && k.severity >= n.severity).sort(labelCompare);
  const slot = g.nodeSlot.get(id);
  const support: Explanation["support"] = [], cross: Explanation["cross"] = [];
  if (slot !== undefined) {
    for (const es of [...g.out[slot], ...g.inn[slot]]) {
      const r = g.edges[es];
      if (!r) continue;
      const role = m.spec.edgeRoles[r.type];
      if (!role) continue;
      const otherId = r.source === id ? r.target : r.source;
      const other = g.entity(otherId);
      if (role === "support" && other) support.push({ id: otherId, type: other.type, label: g.label(other), relationship: r.type, props: Object.entries(other.props).map(([k, v]) => [k, valueText(v)] as [string, string]) });
      else if (role === "cross") cross.push({ id: otherId, label: other ? g.label(other) : otherId, relationship: r.type, direction: r.source === id ? "out" : "in" });
    }
  }
  return { drivers, support, cross };
}

/** Whether the graph has anything to show as a tree: an entity of a type of the tree (a graph that shares the ontology but holds only other types has not). */
export function hasTreeRows(g: Graph, spec: TreeSpec): boolean {
  for (const e of g.entities()) if (!spec.types || spec.types.includes(e.type)) return true;
  return false;
}
