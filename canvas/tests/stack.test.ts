import { describe, expect, it } from "vitest";
import { Catalog } from "../src/core/catalog";
import { Graph } from "../src/core/graph";
import type { Edit } from "../src/core/model";
import { ent, rel } from "../src/editor/patches";
import { stackOf } from "../src/editor/stack";

const S = "d:sp/space:s";
/** A space: `produced` maps a graph to the projection that produces it, `reads` a projection to the graphs it reads (one port each). */
function space(produced: Record<string, string>, reads: Record<string, string[]>): Catalog {
  const es: Edit[] = [];
  const graphs = new Set([...Object.keys(produced), ...Object.values(reads).flat()]);
  for (const g of graphs) es.push(ent(`${S}/graph:${g}`, "Graph"));
  for (const [g, p] of Object.entries(produced)) es.push(rel(`${S}/graph:${g}`, "producedBy", `d:m/projection:${p}`));
  for (const [p, gs] of Object.entries(reads)) gs.forEach((g, i) => es.push(ent(`${S}/binding:${p}.${i}`, "Binding"), rel(`${S}/binding:${p}.${i}`, "bindsPort", `d:m/projection:${p}/port:in${i}`), rel(`${S}/binding:${p}.${i}`, "boundFrom", `${S}/graph:${g}`)));
  return new Catalog(Graph.fromSnapshot({ version: { tenant: "t", graph: "catalog", revision: "0" }, entities: es.flatMap((e) => (e.kind === "entity" ? [e.after!] : [])), relationships: es.flatMap((e) => (e.kind === "relationship" ? [e.after!] : [])) }));
}
const levels = (c: Catalog, p: string) => stackOf(c, `d:m/projection:${p}`).up.map((l) => l.map((u) => u.graph.slice(u.graph.lastIndexOf(":") + 1)));

describe("the stack above a projection follows every input", () => {
  // the shipped workload space: master from the source, workloadStructure from master, workloadHealth from master and workloadStructure
  const workload = space({ master: "ingest", workloadStructure: "structure", workloadHealth: "health" }, { ingest: ["raw"], structure: ["master"], health: ["master", "workloadStructure"] });
  it.each<[string, Catalog, string, string[][]]>([
    ["a projection with two inputs shows both, each after what it is computed from", workload, "health", [["raw"], ["master"], ["workloadStructure"]]],
    ["a projection with one input shows its chain", workload, "structure", [["raw"], ["master"]]],
    ["two inputs of one level stand side by side", space({ a: "pa", b: "pb" }, { pa: ["raw"], pb: ["raw"], top: ["a", "b"] }), "top", [["raw"], ["a", "b"]]],
    ["a cycle in the space is cut where it closes", space({ a: "pa", b: "pb" }, { pa: ["b"], pb: ["a"] }), "pa", [["a"], ["b"]]],
    ["a projection that reads nothing has none", workload, "nothing", []],
  ])("%s", (_, c, p, want) => expect(levels(c, p)).toEqual(want));
});
