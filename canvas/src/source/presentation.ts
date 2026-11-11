// Value roles (engine/PROTOCOL.md, version 3.2): an entry of `properties` in the ontology may carry
// `"presentation": [ { "value": <value as in snapshots>, "role": "ok" } ]`. The hint is removed before the ontology is validated
// against the contract schema, which forbids extra fields.
import { valueText } from "../core/model";
import type { GraphValue } from "../core/wire-types";

export type Role = "ok" | "warning" | "danger" | "unknown" | "neutral";
export const ROLES: Role[] = ["ok", "warning", "danger", "unknown", "neutral"];
export interface PresentationHint { property: string; roles: Record<string, Role> }

/** The properties of an entity type that carry what a tree shows. `curatedBy`, `critical`, `hidden`, `mode` (worst, share or ignore) and `share` say how an authored record changed the element and how it rolls up. */
export interface TreeState { own?: string; rolledUp?: string; severity?: string; kind?: string; counts?: Record<string, string>; curatedBy?: string; critical?: string; hidden?: string; mode?: string; share?: string }

/** What the ontology says about a hierarchy (engine 3.14); absent from a graph that is not one. */
export interface TreeSpec {
  /** the hierarchy relationship type (the one the ontology marks `hierarchy: true`), and whether its edges point child to parent (the engine's point parent to child) */
  hierarchy: { relationship: string; childIsSource: boolean };
  /** relationship types that are not the hierarchy, read from the structure of the ontology: `support` has an end outside the tree types (evidence, signals) and explains a node, `cross` joins two types of the tree (a dependency) */
  edgeRoles: Record<string, "support" | "cross">;
  /** per entity type: the properties of its own state, of its rolled-up state, of the integer that orders states (higher is worse), of its kind (what the row says it is), and of the count of descendants per state value */
  state: Record<string, TreeState>;
  /** the entity types that are rows of the tree (the ends of the hierarchy relationship) */
  types?: string[];
}

/**
 * The hierarchy of an ontology: the relationship type marked `hierarchy: true` (parent is the source, child the target), and per entity type
 * `treeState { own, rolledUp, severity, kind, counts }` naming the properties that hold its states. Like the value roles they are data of the
 * ontology and are removed before it is validated against the contract schema.
 */
export function readTree(raw: unknown): { tree: TreeSpec | null; ontology: unknown } {
  const o = raw as Record<string, unknown> | null;
  if (!o || typeof o !== "object") return { tree: null, ontology: raw };
  type Id = { value?: string };
  type Rel = { id?: Id; sourceTypes?: Id[]; targetTypes?: Id[]; hierarchy?: boolean } & Record<string, unknown>;
  const state: TreeSpec["state"] = {};
  const entityTypes = Array.isArray(o.entityTypes) ? (o.entityTypes as Record<string, unknown>[]).map((t) => {
    if (!("treeState" in t)) return t;
    const { treeState, ...others } = t as { treeState?: TreeState; id?: Id } & Record<string, unknown>;
    const id = (t as { id?: Id }).id?.value;
    if (id && treeState) state[id] = { ...treeState };
    return others;
  }) : null;
  const rels = Array.isArray(o.relationshipTypes) ? (o.relationshipTypes as Rel[]) : null;
  const hierarchyRel = rels?.find((r) => r.hierarchy === true);
  const relationshipTypes = rels?.map((r) => { if (!("hierarchy" in r)) return r; const { hierarchy: _h, ...others } = r; return others; });
  const ontology = { ...o, ...(entityTypes ? { entityTypes } : {}), ...(relationshipTypes ? { relationshipTypes } : {}) };
  if (!hierarchyRel?.id?.value) return { tree: null, ontology };
  const ends = (r: Rel) => [...(r.sourceTypes ?? []), ...(r.targetTypes ?? [])].map((t) => t.value).filter((x): x is string => !!x);
  const types = [...new Set(ends(hierarchyRel))];
  const edgeRoles: TreeSpec["edgeRoles"] = {};
  for (const r of rels ?? []) {
    if (r === hierarchyRel || !r.id?.value) continue;
    const e = ends(r);
    if (!e.length || !types.length) continue;
    const inside = e.filter((t) => types.includes(t)).length;
    if (inside === e.length) edgeRoles[r.id.value] = "cross"; else if (inside > 0) edgeRoles[r.id.value] = "support";
  }
  return { ontology, tree: { hierarchy: { relationship: hierarchyRel.id.value, childIsSource: false }, edgeRoles, state, ...(types.length ? { types } : {}) } };
}

export function readPresentation(raw0: unknown): { hints: PresentationHint[]; ontology: unknown; tree: TreeSpec | null } {
  const { tree, ontology: raw } = readTree(raw0);
  const r = readHints(raw);
  return { ...r, tree };
}

function readHints(raw: unknown): { hints: PresentationHint[]; ontology: unknown } {
  const o = raw as { properties?: { id?: { value?: string }; presentation?: { value: GraphValue; role: string }[] }[] } | null;
  if (!o || !Array.isArray(o.properties)) return { hints: [], ontology: raw };
  const hints: PresentationHint[] = [];
  const properties = o.properties.map((p) => {
    if (!p || !("presentation" in p)) return p;
    const { presentation, ...rest } = p;
    const roles: Record<string, Role> = {};
    for (const e of Array.isArray(presentation) ? presentation : []) if (e?.value && (ROLES as string[]).includes(e.role)) roles[valueText(e.value)] = e.role as Role;
    if (p.id?.value && Object.keys(roles).length) hints.push({ property: p.id.value, roles });
    return rest;
  });
  return { hints, ontology: { ...o, properties } };
}
