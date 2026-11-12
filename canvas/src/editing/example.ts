// A pattern from an example: the author picks elements of a data graph, and each entity becomes a pattern node of its type, each
// relationship a pattern edge between the nodes of its ends. A calculation over the picked elements and the catalog; what it
// leaves out (which properties matter) the author adds on request, as conditions on the values the example has.
import type { Catalog } from "../core/catalog";
import type { Graph } from "../core/graph";
import type { Entity, Rel } from "../core/model";
import type { Gesture } from "./gestures";
import { typeOfData } from "./scope";

export interface PatternNode { variable: string; type: string | null; typeName: string; example: Entity }
export interface PatternEdge { from: string; to: string; type: string | null; typeName: string; example: Rel }
export interface Pattern { nodes: PatternNode[]; edges: PatternEdge[]; /** type names of the example that the ontology of the input does not declare */ unknown: string[] }
/** A condition an author can ask for: a property of a pattern variable equal to the value the example has. */
export interface Condition { text: string; variable: string; property: string }

/** `Pod` gives `p`; the second pod of a pattern `p2`. */
function variable(type: string, taken: Set<string>): string {
  const base = (type.match(/[A-Za-z]/)?.[0] ?? "x").toLowerCase();
  for (let n = 1; ; n++) { const v = n === 1 ? base : `${base}${n}`; if (!taken.has(v)) { taken.add(v); return v; } }
}

/** The pattern the picked elements of `data` are an example of. `input` is the graph input of the projection the data belongs to. A picked relationship brings its two ends. */
export function generalise(cat: Catalog, input: string, data: Graph, picked: readonly string[]): Pattern {
  const rels = picked.map((id) => data.rel(id)).filter((r): r is Rel => !!r);
  const ids = [...new Set([...picked.filter((id) => data.entity(id)), ...rels.flatMap((r) => [r.source, r.target])])];
  const taken = new Set<string>(), unknown = new Set<string>();
  const known = (slot: "entity" | "rel", name: string): string | null => { const t = typeOfData(cat, input, slot, name); if (!t) unknown.add(name); return t; };
  const nodes = ids.map((id) => data.entity(id)).filter((e): e is Entity => !!e).map((e): PatternNode => ({ variable: variable(e.type, taken), type: known("entity", e.type), typeName: e.type, example: e }));
  const of = new Map(nodes.map((n) => [n.example.id, n.variable]));
  const seen = new Set<string>();
  const edges = rels.flatMap((r): PatternEdge[] => {
    const from = of.get(r.source), to = of.get(r.target), key = `${from}>${r.type}>${to}`;
    if (!from || !to || seen.has(key)) return [];
    seen.add(key);
    return [{ from, to, type: known("rel", r.type), typeName: r.type, example: r }];
  });
  return { nodes, edges, unknown: [...unknown] };
}

/** The pattern as an author reads it, one element per line: `(p: Pod)`, `(p)-[:Calls]->(p2)`. */
export const nodeText = (n: PatternNode): string => `(${n.variable}: ${n.typeName})`;
export const edgeText = (e: PatternEdge): string => `(${e.from})-[:${e.typeName}]->(${e.to})`;

/** The conditions the example offers: every text, number and truth value its entities have, by pattern variable. */
export function conditions(p: Pattern): Condition[] {
  return p.nodes.flatMap((n) => Object.entries(n.example.props).flatMap(([property, v]): Condition[] => {
    // a property is read as `variable.property`, so its name has to be one word
    if (!/^[A-Za-z_]\w*$/.test(property)) return [];
    const value = v.kind === "text" ? `"${v.value.replace(/[\\"]/g, (c) => `\\${c}`)}"` : v.kind === "integer" || v.kind === "decimal" || v.kind === "boolean" ? String(v.value) : null;
    return value === null ? [] : [{ variable: n.variable, property, text: `${n.variable}.${property} == ${value}` }];
  }));
}

/** The gesture that adds the pattern as a row set, or null while it cannot be one. */
export function matchGesture(p: Pattern, input: string, statement: string, where: readonly string[]): Gesture | null {
  if (!p.nodes.length || p.unknown.length || !statement.trim()) return null;
  return {
    g: "match", statement: statement.trim(), graph: input,
    nodes: p.nodes.map((n) => ({ variable: n.variable, type: n.type! })), edges: p.edges.map((e) => ({ from: e.from, to: e.to, type: e.type! })),
    where: where.length ? where.join(" and ") : undefined,
  };
}

/** The elements next to the picked ones: what the example can grow by. Each is a relationship and the entity at its other end. */
export function around(data: Graph, picked: readonly string[], limit = 40): { rel: Rel; other: Entity; outward: boolean }[] {
  const has = new Set(picked);
  const mine = new Set([...picked.filter((id) => data.entity(id)), ...picked.flatMap((id) => { const r = data.rel(id); return r ? [r.source, r.target] : []; })]);
  const out: { rel: Rel; other: Entity; outward: boolean }[] = [];
  for (const id of mine) {
    const slot = data.nodeSlot.get(id);
    if (slot === undefined) continue;
    for (const [edges, outward] of [[data.out[slot] ?? [], true], [data.inn[slot] ?? [], false]] as const) for (const es of edges) {
      const r = data.edges[es];
      if (out.length >= limit) return out;
      if (!r || has.has(r.id) || out.some((x) => x.rel.id === r.id)) continue;
      const other = data.entity(outward ? r.target : r.source);
      if (other) out.push({ rel: r, other, outward });
    }
  }
  return out;
}
