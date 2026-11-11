// STAND-IN, to be removed: until the engine marks the hierarchy in the ontology (`relationshipTypes[].hierarchy`, engine 3.14, not in a build yet),
// the tree view of the committed engine output reads the health roll-up through this local table, keyed by the ontology (not by a graph name).
// A graph whose ontology carries the marker never uses it: `withStandIn` only fills in a missing `tree`.
import type { GraphInfo } from "./types";
import type { TreeSpec } from "./presentation";

export const STAND_IN: Record<string, TreeSpec> = {
  "acme.health": {
    hierarchy: { relationship: "acme.health.Contains", childIsSource: false },
    edgeRoles: { EvidenceFor: "support", Measures: "support", "acme.health.DependsOn": "cross" },
    state: { Component: { own: "own", rolledUp: "acme.health.state", severity: "acme.health.severity", kind: "acme.health.kind" } },
    types: ["Component"],
  },
};

/** The graph info with the stand-in hierarchy of its ontology when the ontology marks none. */
export function withStandIn(info: GraphInfo): GraphInfo {
  const spec = info.tree ? null : STAND_IN[info.ontology.reference?.id ?? ""];
  return spec ? { ...info, tree: spec } : info;
}
