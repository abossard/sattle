// Conversions between wire shapes (brainstorm/contracts/schemas) and the flat model.
import type { Edit, Entity, Patch, Props, Rel, Snapshot, Version } from "./model";
import type {
  EntityState, GraphDescriptorWire, GraphEditWire, GraphExtentWire, GraphPatchWire, GraphVersionWire,
  PropertyValue, RelationshipState,
} from "./wire-types";

const propsFromWire = (ps: PropertyValue[]): Props => {
  const o: Props = {};
  for (const p of ps) o[p.property.value] = p.value;
  return o;
};
const propsToWire = (p: Props): PropertyValue[] =>
  Object.keys(p).map((k) => ({ property: { value: k }, value: p[k] }));

export const entityFromWire = (e: EntityState): Entity => ({ id: e.id.value, type: e.type.value, props: propsFromWire(e.properties) });
export const entityToWire = (e: Entity): EntityState => ({ id: { value: e.id }, type: { value: e.type }, properties: propsToWire(e.props) });
export const relFromWire = (r: RelationshipState): Rel => ({
  id: r.id.value, source: r.source.value, target: r.target.value, type: r.type.value, props: propsFromWire(r.properties),
});
export const relToWire = (r: Rel): RelationshipState => ({
  id: { value: r.id }, source: { value: r.source }, target: { value: r.target }, type: { value: r.type }, properties: propsToWire(r.props),
});
export const versionFromWire = (v: GraphVersionWire): Version => ({ tenant: v.tenant.value, graph: v.graph.value, revision: v.revision.value });
export const versionToWire = (v: Version): GraphVersionWire => ({ tenant: { value: v.tenant }, graph: { value: v.graph }, revision: { value: v.revision } });

function editFromWire(e: GraphEditWire): Edit {
  switch (e.kind) {
    case "entity": return { kind: "entity", id: e.id.value, before: e.before && entityFromWire(e.before), after: e.after && entityFromWire(e.after) };
    case "relationship": return { kind: "relationship", id: e.id.value, before: e.before && relFromWire(e.before), after: e.after && relFromWire(e.after) };
    default: return e;
  }
}
function editToWire(e: Edit): GraphEditWire {
  switch (e.kind) {
    case "entity": return { kind: "entity", id: { value: e.id }, before: e.before && entityToWire(e.before), after: e.after && entityToWire(e.after) };
    case "relationship": return { kind: "relationship", id: { value: e.id }, before: e.before && relToWire(e.before), after: e.after && relToWire(e.after) };
    default: return e;
  }
}

export const patchFromWire = (p: GraphPatchWire): Patch => ({
  from: versionFromWire(p.from), to: versionFromWire(p.to), projection: p.projection.value, profile: p.profile,
  edits: p.edits.map(editFromWire),
  cause: { inputs: p.cause.inputs.map((i) => i.value), configurationVersion: p.cause.configurationVersion },
});
export const patchToWire = (p: Patch): GraphPatchWire => ({
  schemaVersion: "1.0", from: versionToWire(p.from), to: versionToWire(p.to), projection: { value: p.projection }, profile: p.profile,
  edits: p.edits.map(editToWire),
  cause: { inputs: p.cause.inputs.map((value) => ({ value })), configurationVersion: p.cause.configurationVersion },
});

export const snapshotFromWire = (descriptor: GraphDescriptorWire, extent: GraphExtentWire): Snapshot => ({
  version: versionFromWire(descriptor.version),
  entities: extent.entities.map(entityFromWire),
  relationships: extent.relationships.map(relFromWire),
});
