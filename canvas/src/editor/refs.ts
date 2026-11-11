// Relationships an operator holds to definitions elsewhere (the type an emit creates, the relationship a traversal follows, the equivalence
// policy of a link, the port that carries a fixpoint's step result). /palette describes slots and parameters but not these, so the table is
// here: one place, keyed by operator kind, read by the property panel and the incomplete-operator check.
import type { Graph } from "../core/graph";
import type { Edit } from "../core/model";
import { rel } from "./patches";
import type { Result } from "./commands";

export interface RefSpec { rel: string; label: string; to: "EntityType" | "RelationshipType" | "EquivalencePolicy" | "OutPort"; required: boolean }
export const OPERATOR_REFS: Record<string, RefSpec[]> = {
  EmitEntity: [{ rel: "ofType", label: "Entity type", to: "EntityType", required: true }],
  EmitRelationship: [{ rel: "ofType", label: "Relationship type", to: "RelationshipType", required: true }],
  Traverse: [{ rel: "ofType", label: "Relationship type", to: "RelationshipType", required: true }],
  Link: [{ rel: "policy", label: "Equivalence policy", to: "EquivalencePolicy", required: false }],
  // a lift: the relationship type it produces (in the output ontology), the lower relationship type it lifts, and the one that maps a lower node to its image
  Lift: [
    { rel: "ofType", label: "Relationship type lifted to (output)", to: "RelationshipType", required: true },
    { rel: "liftEdge", label: "Lower relationship type", to: "RelationshipType", required: true },
    { rel: "liftVia", label: "Mapping relationship type (node to image)", to: "RelationshipType", required: true },
  ],
  Fixpoint: [{ rel: "over", label: "Step result (the output port the step ends at)", to: "OutPort", required: true }],
};
export const refsOf = (kind: string): RefSpec[] => OPERATOR_REFS[kind] ?? [];

/** Point `rel` of `op` at `target`, replacing what it pointed at; an empty target removes it. */
export function setRefEdits(g: Graph, op: string, relType: string, target: string): Result<Edit[]> {
  if (target && !g.entity(target)) return { ok: false, reason: "unknown target" };
  const old = [...g.relationships()].filter((r) => r.source === op && r.type === relType).map((r): Edit => ({ kind: "relationship", id: r.id, before: r, after: null }));
  return { ok: true, value: [...old, ...(target ? [rel(op, relType, target)] : [])] };
}
