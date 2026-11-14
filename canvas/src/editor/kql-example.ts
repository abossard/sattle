// By example (note 11, stage c): the elements an author picks in a data graph become the `graph-match` that finds their like, as KQL text.
// Each entity is a pattern node, each relationship an edge between the nodes of its ends (a picked relationship brings its two ends). The
// edges say their type (`labels(b) has "Backs"`), and so does a node no edge touches; each node is projected as its id, named after its type.
import type { Graph } from "../core/graph";
import type { Entity, Rel } from "../core/model";

/** `Pod` gives `p`; a second element of a type with the same letter gets a number. */
const variable = (type: string, taken: Set<string>): string => {
  const base = (type.match(/[A-Za-z]/)?.[0] ?? "x").toLowerCase();
  for (let n = 1; ; n++) { const v = n === 1 ? base : `${base}${n}`; if (!taken.has(v)) { taken.add(v); return v; } }
};
/** `Service` gives `service`, `NetworkEndpoint` `networkEndpoint`; a second one of a name gets a number. */
const column = (type: string, taken: Set<string>): string => {
  const base = type.charAt(0).toLowerCase() + type.slice(1);
  for (let n = 1; ; n++) { const c = n === 1 ? base : `${base}${n}`; if (!taken.has(c)) { taken.add(c); return c; } }
};

/**
 * The pipeline that finds the picked elements' like in graph `input`, starting at `graph("input")`, for the right side of a `let`. Null when
 * nothing that is in the graph is picked. `indent` starts each line after the first.
 */
export function matchOf(data: Graph, picked: readonly string[], input: string, indent = "    "): string | null {
  const rels = [...new Set(picked)].map((id) => data.rel(id)).filter((r): r is Rel => !!r);
  const ids = [...new Set([...picked.filter((id) => data.entity(id)), ...rels.flatMap((r) => [r.source, r.target])])];
  const nodes = ids.map((id) => data.entity(id)).filter((e): e is Entity => !!e);
  if (!nodes.length) return null;
  const vars = new Set<string>(), cols = new Set<string>();
  const v = new Map(nodes.map((e) => [e.id, variable(e.type, vars)]));
  const edges = rels.filter((r) => v.has(r.source) && v.has(r.target)).map((r) => ({ r, name: variable(r.type, vars) }));
  const touched = new Set(edges.flatMap(({ r }) => [r.source, r.target]));
  const patterns = [...edges.map(({ r, name }) => `(${v.get(r.source)})-[${name}]->(${v.get(r.target)})`), ...nodes.filter((e) => !touched.has(e.id)).map((e) => `(${v.get(e.id)})`)];
  const conditions = [...edges.map(({ r, name }) => `labels(${name}) has "${r.type}"`), ...nodes.filter((e) => !touched.has(e.id)).map((e) => `labels(${v.get(e.id)}) has "${e.type}"`)];
  const project = nodes.map((e) => `${column(e.type, cols)} = ${v.get(e.id)}.id`);
  return `graph("${input}")\n${indent}| graph-match ${patterns.join(", ")} where ${conditions.join(" and ")}\n${indent}  project ${project.join(", ")}`;
}
