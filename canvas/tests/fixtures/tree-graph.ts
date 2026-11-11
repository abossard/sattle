// A small hand-made hierarchical graph for the tree view, in the shape an ontology marks up: a hierarchy relationship (`Contains`, parent to child),
// support relationships (evidence for a node), cross relationships (a dependency between nodes), and states with value roles. Nothing in it is about health.
import { Graph } from "../../src/core/graph";
import type { TreeSpec } from "../../src/app/tree-model";
import type { PresentationHint } from "../../src/source/presentation";
import { ent, rel, ver } from "../helpers";

const node = (id: string, name: string, own: string, state: string, severity: number) => ent(id, "Component", { name, own, state, severity: String(severity) });
/** Estate > Region A (api, db), Region B (web, cache), Region C (empty). api is degraded by evidence; web depends on api and db. */
export const treeGraph = (): Graph => {
  const g = Graph.fromSnapshot({
    version: ver("1", "tree"),
    entities: [
      node("estate", "Estate", "Healthy", "Unhealthy", 2),
      node("ra", "Region A", "Healthy", "Unhealthy", 2), node("rb", "Region B", "Healthy", "Degraded", 1), node("rc", "Region C", "Unknown", "Unknown", 0),
      node("api", "api", "Unhealthy", "Unhealthy", 2), node("db", "db", "Healthy", "Healthy", 0),
      node("web", "web", "Degraded", "Degraded", 1), node("cache", "cache", "Healthy", "Healthy", 0),
      ent("e1", "Evidence", { name: "restart" }), ent("e2", "Evidence", { name: "oom" }),
    ].map((e) => ({ ...e, props: Object.fromEntries(Object.entries(e.props).map(([k, v]) => [k, k === "severity" ? { kind: "integer" as const, value: (v as { value: string }).value } : v])) })),
    relationships: [
      rel("c1", "estate", "ra", "Contains"), rel("c2", "estate", "rb", "Contains"), rel("c3", "estate", "rc", "Contains"),
      rel("c4", "ra", "api", "Contains"), rel("c5", "ra", "db", "Contains"), rel("c6", "rb", "web", "Contains"), rel("c7", "rb", "cache", "Contains"),
      rel("s1", "e1", "api", "EvidenceFor"), rel("s2", "e2", "api", "EvidenceFor"),
      rel("d1", "web", "api", "DependsOn"), rel("d2", "web", "db", "DependsOn"),
    ],
  });
  return g;
};
export const treeSpec: TreeSpec = {
  hierarchy: { relationship: "Contains", childIsSource: false },
  edgeRoles: { EvidenceFor: "support", DependsOn: "cross" },
  state: { Component: { own: "own", rolledUp: "state", severity: "severity" } },
  types: ["Component"],
};
const roles = { Healthy: "ok", Degraded: "warning", Unhealthy: "danger", Unknown: "unknown" } as const;
export const treeHints: PresentationHint[] = [{ property: "own", roles: { ...roles } }, { property: "state", roles: { ...roles } }];

/** The same graph after authored curation: api is curated by a record and rolls up as a share, db is critical, cache is hidden. */
export const curated = (g: Graph): Graph => {
  const put = (id: string, props: Record<string, { kind: "text"; value: string } | { kind: "boolean"; value: boolean }>) => { const e = g.entity(id)!; g.putEntity({ ...e, props: { ...e.props, ...props } }); };
  put("api", { curatedBy: { kind: "text", value: "cur-7" }, mode: { kind: "text", value: "share" }, share: { kind: "text", value: "0.5" } });
  put("db", { critical: { kind: "boolean", value: true } });
  put("cache", { hidden: { kind: "boolean", value: true } });
  return g;
};
export const curatedSpec: TreeSpec = { ...treeSpec, state: { Component: { ...treeSpec.state.Component, curatedBy: "curatedBy", critical: "critical", hidden: "hidden", mode: "mode", share: "share" } } };
