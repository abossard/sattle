// Stacked projections: the graphs a projection reads are produced by other projections, and so on up to the source graphs of the space.
// The catalog says it with `Graph --producedBy--> Projection` and `Binding --bindsPort--> Port`, `Binding --boundFrom--> Graph`.
import type { Catalog } from "../core/catalog";

export interface Stack {
  /**
   * Every graph above this projection, by level from the roots down: a graph's level is the longest chain of producers above it, so each
   * graph shows once, after every graph it is computed from. Each entry is the graph and the projection that produces it (null for a source).
   */
  up: { graph: string; projection: string | null }[][];
  /** graphs this projection produces */
  outputs: string[];
  /** projections that read those graphs */
  down: string[];
}

export function stackOf(c: Catalog, projection: string): Stack {
  const entities = [...c.g.entities()];
  const bindings = entities.filter((e) => e.type === "Binding").map((e) => e.id);
  const graphs = entities.filter((e) => e.type === "Graph").map((e) => e.id);
  const inputsOf = (p: string) => [...new Set(bindings.filter((b) => c.targets(b, "bindsPort").some((port) => port.startsWith(`${p}/`))).flatMap((b) => c.targets(b, "boundFrom")))];
  const level = new Map<string, number>();
  // longest chain of producers above `g`; `path` guards a cycle in the space, which is cut where it closes
  const levelOf = (g: string, path: ReadonlySet<string>): number => {
    const known = level.get(g);
    if (known !== undefined) return known;
    if (path.has(g)) return -1;
    const producer = c.targets(g, "producedBy")[0];
    const l = producer ? Math.max(-1, ...inputsOf(producer).map((x) => levelOf(x, new Set(path).add(g)))) + 1 : 0;
    level.set(g, l);
    return l;
  };
  for (const g of inputsOf(projection)) levelOf(g, new Set());
  const up: Stack["up"] = [];
  for (const [graph, l] of level) (up[l] ??= []).push({ graph, projection: c.targets(graph, "producedBy")[0] ?? null });
  const outputs = graphs.filter((g) => c.targets(g, "producedBy").includes(projection));
  const down = bindings.filter((b) => c.targets(b, "boundFrom").some((g) => outputs.includes(g))).flatMap((b) => c.targets(b, "bindsPort").map((p) => p.slice(0, p.lastIndexOf("/"))));
  return { up, outputs, down: [...new Set(down)] };
}
