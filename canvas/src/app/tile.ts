// What a tile of the graph space shows for a graph, and the line that says what the graph is. Pure: the view picks the component.
import type { Graph } from "../core/graph";
import type { GraphInfo } from "../source/types";
import { edgelessNotice } from "./graph-view-model";

export type TileKind = "document" | "tree" | "summary" | "notice" | "graph";

/** The master of an estate has this many nodes or more: as a tile it is a count of what was observed, not a picture of all of it. */
export const SUMMARY_NODES = 40;

/** A graph with a hierarchy and this many nodes is read as a list of its top rows: drawn as a graph it is too dense to read at the size of a tile. */
export const TREE_TILE_NODES = 24;

/** A document graph shows the document (the blocks drawn as nodes are nothing to look at); a projection with nodes and no edges says why; the rest is drawn. */
export function tileKind(info: GraphInfo, sum: { nodes: number; edges: number }, canRenderDocument: boolean, treeRows = 0): TileKind {
  if (info.document && canRenderDocument) return "document";
  if (info.tree && treeRows >= 3 && sum.nodes >= TREE_TILE_NODES) return "tree";
  if (info.role === "master" && sum.nodes >= SUMMARY_NODES) return "summary";
  if (edgelessNotice(info.role, sum.nodes, sum.edges, info.ontology.relationshipTypes.map((t) => t.id.value))) return "notice";
  return "graph";
}

/** The first sentence of a description stands alone as a caption (engine 3.17: the rest says how the graph is derived). */
export function firstSentence(text: string): string {
  const t = text.trim();
  const m = /^(.+?[.!?])(\s|$)/s.exec(t);
  return (m ? m[1] : t).replace(/\s+/g, " ");
}

const list = (xs: string[], max = 4): string => (xs.length <= max ? xs.join(", ") : `${xs.slice(0, max).join(", ")} and ${xs.length - max} more`);
/** `acme.health.Contains` is `Contains`: the module is for the Inspector. */
const plain = (qualified: string): string => qualified.slice(qualified.lastIndexOf(".") + 1);

/**
 * One line that says what the graph is: the projection's purpose from the catalog when the source has one, else what the inputs and the types
 * it holds say. `present` are the entity types the graph holds now; `purposeOf` reads the catalog (projection name to purpose).
 */
export function describeGraph(info: GraphInfo, present: string[], purposeOf: (projection: string) => string | undefined = () => undefined): string {
  const given = info.description ?? (info.projectionName ? purposeOf(info.projectionName) : undefined);
  if (given) return firstSentence(given);
  const from = info.inputs.length ? ` from ${list(info.inputs)}` : "";
  if (info.document) return `A report${from}, written as a document`;
  if (info.role === "master") return `Everything ingested: ${list(present)}`;
  if (info.tree) return `A hierarchy of ${list(present, 3)} over ${plain(info.tree.hierarchy.relationship)}${from}`;
  return `${list(present)}${from}`;
}

/** The purpose of each projection of a catalog graph, by projection name. */
export function purposesOf(catalog: Graph | undefined): (projection: string) => string | undefined {
  const by = new Map<string, string>();
  if (catalog) for (const e of catalog.entities()) {
    if (e.type !== "Projection") continue;
    const name = e.props.name, purpose = e.props.purpose;
    if (name?.kind === "text" && purpose?.kind === "text" && purpose.value) by.set(name.value, purpose.value);
  }
  return (p) => by.get(p);
}

/** The entity types a graph holds, the most numerous first. */
export function typesHeld(g: Graph): string[] {
  const n = new Map<string, number>();
  for (const e of g.entities()) n.set(e.type, (n.get(e.type) ?? 0) + 1);
  return [...n].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0])).map(([t]) => t);
}

/** The nodes of a graph that no edge reaches. A tile leaves them out when the graph has others: a ring of unconnected dots says nothing. */
export function isolatedNodes(g: Graph): number {
  let n = 0;
  for (const slot of g.nodeSlot.values()) if (!g.isStub(slot) && g.out[slot].length + g.inn[slot].length === 0) n++;
  return n;
}
/** Whether a tile of this graph leaves out the nodes without edges: only when there are some, and others that have them. */
export const tileThinsIsolated = (g: Graph): boolean => { const iso = isolatedNodes(g); return iso > 0 && iso < g.nodeCount; };

/** What a graph holds, by type: entities and relationships with their counts, the most numerous first. */
export function countsByType(g: Graph): { entities: [string, number][]; relationships: [string, number][] } {
  const e = new Map<string, number>(), r = new Map<string, number>();
  for (const x of g.entities()) e.set(x.type, (e.get(x.type) ?? 0) + 1);
  for (const x of g.relationships()) r.set(x.type, (r.get(x.type) ?? 0) + 1);
  const order = (m: Map<string, number>): [string, number][] => [...m].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));
  return { entities: order(e), relationships: order(r) };
}
