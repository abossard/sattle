// What the declared kinds of edge (engine 3.17) say about how to read a graph: which edges restate others, which compositions stack the graph from
// top to bottom and which belong to the side of what a part depends on, and where the elements on that side go. Pure: graph and ontology in, data out.
import type { Graph } from "../core/graph";
import type { EdgeKind, TreeSpec } from "../source/presentation";
import type { GraphInfo } from "../source/types";

type Kinds = Readonly<Record<string, EdgeKind>>;

/**
 * Relationship types that restate others: every edge of the type runs beside an edge of another type between the same two elements, and it does so
 * beside three or more other types (a roll-up edge such as `Affects` repeats the containment, the dependencies and the memberships it follows). They
 * are left out by default; nothing is lost, the edges they restate are drawn. The engine declares no such mark; this is read from the edges.
 */
export function restatingTypes(g: Graph): Set<string> {
  const between = new Map<string, Set<string>>();
  const pair = (a: string, b: string) => (a < b ? `${a}\u0000${b}` : `${b}\u0000${a}`);
  for (const r of g.relationships()) { const k = pair(r.source, r.target); const s = between.get(k); if (s) s.add(r.type); else between.set(k, new Set([r.type])); }
  const covered = new Map<string, { all: boolean; by: Set<string> }>();
  for (const r of g.relationships()) {
    const c = covered.get(r.type) ?? { all: true, by: new Set<string>() };
    const others = [...between.get(pair(r.source, r.target))!].filter((t) => t !== r.type);
    if (!others.length) c.all = false; else for (const t of others) c.by.add(t);
    covered.set(r.type, c);
  }
  return new Set([...covered].filter(([, c]) => c.all && c.by.size >= 3).map(([t]) => t));
}

/**
 * The two axes of a graph with declared kinds. `vertical`: the hierarchy and every composition whose target is no element something depends on
 * (a pod on its node, a node in its pool, a pool in its cluster): the part is drawn below the whole. `horizontal`: the dependencies and the
 * compositions whose target is something that is depended on (a service backed by a database server, a server connected through a private endpoint):
 * drawn beside, to the right of what needs them. Communication and support take no part.
 */
export function kindAxes(info: Pick<GraphInfo, "tree" | "edgeKinds" | "ontology">): { vertical: Set<string>; horizontal: Set<string> } {
  const kinds: Kinds = info.edgeKinds ?? {};
  const depended = new Set<string>();
  for (const r of info.ontology.relationshipTypes) if (kinds[r.id.value] === "dependency") for (const t of r.targetTypes) depended.add(t.value);
  const vertical = new Set<string>(), horizontal = new Set<string>();
  for (const r of info.ontology.relationshipTypes) {
    const id = r.id.value, kind = kinds[id];
    if (id === info.tree?.hierarchy.relationship) vertical.add(id);
    else if (kind === "dependency") horizontal.add(id);
    else if (kind === "composition") (r.targetTypes.length && r.targetTypes.every((t) => depended.has(t.value)) ? horizontal : vertical).add(id);
  }
  return { vertical, horizontal };
}

/** The entity types of the rows of a hierarchy: the ends of its relationship type. */
const rowTypes = (spec: TreeSpec | undefined, info: Pick<GraphInfo, "ontology">): Set<string> => {
  const out = new Set<string>(spec?.types ?? []);
  const h = spec ? info.ontology.relationshipTypes.find((r) => r.id.value === spec.hierarchy.relationship) : undefined;
  for (const t of [...(h?.sourceTypes ?? []), ...(h?.targetTypes ?? [])]) out.add(t.value);
  return out;
};

/**
 * Where the elements on the side of the dependencies go, once the parts are placed. Each is placed in the row of the part it hangs from, in a column
 * to the right of everything already placed, one column further for each step away from the part (a database server, then its private endpoint, then
 * its network); what an element exposes a part through (a gateway: a dependency that points at the part) goes to the left. Elements in one row and
 * column are stacked. Returns the new positions by slot; elements it cannot reach from a placed part keep theirs.
 */
export function besidePositions(g: Graph, info: Pick<GraphInfo, "tree" | "edgeKinds" | "ontology">, placed: (slot: number) => [number, number] | null,
  visible: (slot: number) => boolean, step = { column: 120, row: 34 }): Map<number, [number, number]> {
  const { vertical, horizontal } = kindAxes(info);
  const rows = rowTypes(info.tree, info);
  const onVertical = new Set<number>();
  for (let e = 0; e < g.edges.length; e++) { const r = g.edges[e]; if (r && vertical.has(r.type) && g.edgeSrc[e] >= 0 && g.edgeDst[e] >= 0) { onVertical.add(g.edgeSrc[e]); onVertical.add(g.edgeDst[e]); } }
  let minX = Infinity, maxX = -Infinity;
  for (const s of onVertical) { const p = placed(s); if (p && visible(s)) { minX = Math.min(minX, p[0]); maxX = Math.max(maxX, p[0]); } }
  const out = new Map<number, [number, number]>();
  if (!Number.isFinite(minX)) return out;
  // breadth first from the parts over the horizontal edges: side (+1 right, -1 left) and distance in steps
  const seen = new Map<number, { side: number; hops: number; y: number }>();
  let frontier: number[] = [];
  for (const s of onVertical) if (visible(s)) { const p = placed(s); if (p) { seen.set(s, { side: 0, hops: 0, y: p[1] }); frontier.push(s); } }
  while (frontier.length) {
    const next: number[] = [];
    for (const v of frontier) {
      const at = seen.get(v)!;
      for (const e of [...g.out[v], ...g.inn[v]]) {
        const r = g.edges[e];
        if (!r || !horizontal.has(r.type)) continue;
        const other = g.edgeSrc[e] === v ? g.edgeDst[e] : g.edgeSrc[e];
        if (other < 0 || seen.has(other) || onVertical.has(other) || !visible(other)) continue;
        // from a part: what it needs (it is the source) goes right, what points at it (a gateway that exposes it) goes left; further out the side stays
        const outward = g.edgeSrc[e] === v;
        const side = at.side !== 0 ? at.side : rows.has(g.nodes[v]?.type ?? "") && !outward ? -1 : 1;
        seen.set(other, { side, hops: at.hops + 1, y: at.y });
        next.push(other);
      }
    }
    frontier = next;
  }
  const stacks = new Map<string, number>();
  const sorted = [...seen].filter(([s, a]) => a.hops > 0 && !onVertical.has(s)).sort((a, b) => a[1].y - b[1].y || a[1].hops - b[1].hops || a[0] - b[0]);
  for (const [s, a] of sorted) {
    const key = `${a.side}|${a.hops}|${Math.round(a.y)}`;
    const k = stacks.get(key) ?? 0;
    stacks.set(key, k + 1);
    const x = a.side > 0 ? maxX + a.hops * step.column : minX - a.hops * step.column;
    out.set(s, [x, a.y + k * step.row]);
  }
  return out;
}

/** The role of an element's state: the rolled-up state its type's `treeState` names, read through the value roles. */
function stateRole(e: { type: string; props: Record<string, unknown> }, spec: TreeSpec, hints: readonly { property: string; roles: Record<string, string> }[]): string | null {
  const prop = spec.state[e.type]?.rolledUp ?? spec.state[e.type]?.own;
  if (!prop) return null;
  const v = e.props[prop] as { kind?: string; value?: unknown } | undefined;
  if (!v || v.value === undefined || v.value === null) return null;
  return hints.find((h) => h.property === prop)?.roles[String(v.value)] ?? null;
}

/**
 * Which part is to blame, and the way from the top to it. An element whose state is a warning or a danger is caused by what it is made of (its children
 * in the hierarchy) and by what it needs (the targets of its other compositions and of its dependencies) when those are wrong too; one with no wrong
 * cause is to blame. The path is every wrong element above a blamed one through those edges, and the edges between them. `skip`: types that restate
 * others (see `restatingTypes`), which are no causes.
 */
export function blamePath(g: Graph, info: Pick<GraphInfo, "tree" | "edgeKinds" | "ontology" | "presentation">, skip: ReadonlySet<string> = new Set()): { blamed: Set<string>; nodes: Set<string>; edges: Set<string> } {
  const out = { blamed: new Set<string>(), nodes: new Set<string>(), edges: new Set<string>() };
  const spec = info.tree;
  if (!spec || !Object.keys(spec.state).length) return out;
  const kinds: Kinds = info.edgeKinds ?? {};
  const hints = (info.presentation ?? []) as { property: string; roles: Record<string, string> }[];
  const wrong = new Set<string>();
  for (const e of g.entities()) { const r = stateRole(e, spec, hints); if (r === "warning" || r === "danger") wrong.add(e.id); }
  if (!wrong.size) return out;
  // cause edges: parent -> child of the hierarchy; source -> target of every other composition and dependency
  const causes = new Map<string, { id: string; edge: string }[]>();
  for (const r of g.relationships()) {
    const hier = r.type === spec.hierarchy.relationship || !!spec.hierarchy.also?.includes(r.type);
    const k = kinds[r.type];
    // a type that restates others (a roll-up edge) names no cause of its own
    if (skip.has(r.type) || (!hier && k !== "composition" && k !== "dependency")) continue;
    const [from, to] = hier && spec.hierarchy.childIsSource ? [r.target, r.source] : [r.source, r.target];
    const l = causes.get(from); if (l) l.push({ id: to, edge: r.id }); else causes.set(from, [{ id: to, edge: r.id }]);
  }
  for (const id of wrong) if (!(causes.get(id) ?? []).some((c) => wrong.has(c.id))) out.blamed.add(id);
  // walk up from each blamed element through the wrong elements whose cause it is
  const caused = new Map<string, { id: string; edge: string }[]>();
  for (const [from, list] of causes) for (const c of list) { const l = caused.get(c.id); if (l) l.push({ id: from, edge: c.edge }); else caused.set(c.id, [{ id: from, edge: c.edge }]); }
  const todo = [...out.blamed];
  for (const id of todo) out.nodes.add(id);
  while (todo.length) {
    const id = todo.pop()!;
    for (const up of caused.get(id) ?? []) {
      if (!wrong.has(up.id)) continue;
      out.edges.add(up.edge);
      if (!out.nodes.has(up.id)) { out.nodes.add(up.id); todo.push(up.id); }
    }
  }
  return out;
}
