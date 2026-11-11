// An OntologyDefinition as a graph: entity types are nodes, relationship types are edges between allowed
// source and target types, properties are nodes attached to the types that carry them.
import { Graph } from "./graph";
import type { Entity, Props, Rel } from "./model";
import type { OntologyDefinition } from "./wire-types";

const text = (value: string) => ({ kind: "text" as const, value });

export function ontologyGraph(o: OntologyDefinition, withProperties = true): Graph {
  const entities: Entity[] = [];
  const rels: Rel[] = [];
  const propDef = new Map(o.properties.map((p) => [p.id.value, p]));
  for (const t of o.entityTypes) {
    const props: Props = { name: text(t.id.value), properties: text(t.properties.map((p) => p.value).join(", ")) };
    entities.push({ id: `type:${t.id.value}`, type: "EntityType", props });
  }
  for (const rt of o.relationshipTypes) {
    const props: Props = { name: text(rt.id.value), properties: text(rt.properties.map((p) => p.value).join(", ")) };
    for (const s of rt.sourceTypes) for (const t of rt.targetTypes) {
      rels.push({ id: `rel:${rt.id.value}:${s.value}>${t.value}`, source: `type:${s.value}`, target: `type:${t.value}`, type: rt.id.value, props });
    }
  }
  if (withProperties) {
    for (const p of o.properties) {
      entities.push({
        id: `prop:${p.id.value}`, type: "Property",
        props: { name: text(p.id.value), valueKind: text(p.valueKind), role: text(p.role), unit: text(p.unit ?? ""), nullable: text(String(p.nullable)) },
      });
    }
    for (const t of [...o.entityTypes, ...o.relationshipTypes.map((r) => ({ id: r.id, properties: r.properties }))]) {
      for (const p of t.properties) {
        if (!propDef.has(p.value)) continue;
        // relationship-type properties attach to the relationship type name, shown on its first source node
        const owner = o.entityTypes.some((e) => e.id.value === t.id.value) ? `type:${t.id.value}` : null;
        if (owner) rels.push({ id: `has:${t.id.value}.${p.value}`, source: owner, target: `prop:${p.value}`, type: "has", props: {} });
      }
    }
  }
  return Graph.fromSnapshot({ version: { tenant: "ontology", graph: o.reference.id, revision: o.reference.version }, entities, relationships: rels });
}
