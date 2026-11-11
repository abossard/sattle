// A reference is an endpoint that relationships name and the graph does not hold (engine 3.11). The canvas draws it as a stub node;
// this builds what the stub says: a label from the identity key, the type with "(reference)", and why the entity is missing.
import type { Reference } from "../source/types";
import type { Entity } from "./model";
import type { GraphValue } from "./wire-types";

const text = (value: string): GraphValue => ({ kind: "text", value });
export const isReferenceType = (type: string): boolean => type === "reference" || type.endsWith(" (reference)");

/** The last segment of a path-like key value (`/subscriptions/x/…/aks-prod` is `aks-prod`), shortened. */
const short = (v: string): string => { const last = v.slice(v.lastIndexOf("/") + 1) || v; return last.length > 28 ? `${last.slice(0, 27)}…` : last; };

/** What the stub of `id` shows; without information from the engine only the id is known. */
export function stubEntity(id: string, ref?: Reference): Entity {
  if (!ref) return { id, type: "reference", props: { name: text("◌ unresolved reference"), reason: text("The graph's relationships name this entity, but it is not in the graph.") } };
  const key = ref.identity ? Object.values(ref.identity.key).map(short).join(" / ") : "";
  const type = `${ref.entityType || "entity"} (reference)`;
  const where = ref.state === "inOtherGraph" && ref.graph ? `in graph ${ref.graph}` : "in no graph";
  const props: Record<string, GraphValue> = {
    name: text(`◌ ${key || `unresolved ${ref.entityType || "entity"}`}`),
    reason: text(ref.state === "inOtherGraph" && ref.graph ? `This entity is ${where}, not in this one.` : "No graph holds this entity: relationships name it, nothing created it."),
    state: text(ref.state),
  };
  if (ref.graph) props.graph = text(ref.graph);
  if (ref.identity) { props.identityDomain = text(ref.identity.domain); for (const [k, v] of Object.entries(ref.identity.key)) props[`key.${k}`] = text(v); }
  if (ref.uses.length) props.usedBy = text(ref.uses.map((u) => `${u.relationshipType} (${u.end}) × ${u.count}`).join(", "));
  return { id, type, props };
}
