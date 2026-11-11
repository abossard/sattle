// Stacked projections: the graph a projection reads is produced by another projection, and so on up to the first graph of the space.
// The catalog says it with `Graph --producedBy--> Projection` and `Binding --bindsPort--> Port`, `Binding --boundFrom--> Graph`.
import type { Catalog } from "../core/catalog";

export interface Stack {
  /** from the root of the stack down to the graph this projection reads: the graph and the projection that produces it (null for a source graph) */
  up: { graph: string; projection: string | null }[];
  /** graphs this projection produces */
  outputs: string[];
  /** projections that read those graphs */
  down: string[];
}

export function stackOf(c: Catalog, projection: string): Stack {
  const entities = [...c.g.entities()];
  const bindings = entities.filter((e) => e.type === "Binding").map((e) => e.id);
  const graphs = entities.filter((e) => e.type === "Graph").map((e) => e.id);
  const inputsOf = (p: string) => bindings.filter((b) => c.targets(b, "bindsPort").some((port) => port.startsWith(`${p}/`))).flatMap((b) => c.targets(b, "boundFrom"));
  const up: Stack["up"] = [];
  const seen = new Set<string>();
  let cur = projection;
  for (;;) {
    const g = inputsOf(cur)[0];
    if (!g || seen.has(g)) break;
    seen.add(g);
    const producer = c.targets(g, "producedBy")[0] ?? null;
    up.unshift({ graph: g, projection: producer });
    if (!producer) break;
    cur = producer;
  }
  const outputs = graphs.filter((g) => c.targets(g, "producedBy").includes(projection));
  const down = bindings.filter((b) => c.targets(b, "boundFrom").some((g) => outputs.includes(g))).flatMap((b) => c.targets(b, "bindsPort").map((p) => p.slice(0, p.lastIndexOf("/"))));
  return { up, outputs, down: [...new Set(down)] };
}
