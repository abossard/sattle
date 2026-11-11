// Validation of wire records against the real contract schemas. Used at the GraphSource seam and on export.
import Ajv2020 from "ajv/dist/2020.js";
import type { ValidateFunction } from "ajv/dist/2020.js";
import patchSchema from "../../../brainstorm/contracts/schemas/GraphPatch.schema.json";
import descriptorSchema from "../../../brainstorm/contracts/schemas/GraphDescriptor.schema.json";
import ontologySchema from "../../../brainstorm/contracts/schemas/OntologyDefinition.schema.json";
import projectionSchema from "../../../brainstorm/contracts/schemas/ProjectionDefinition.schema.json";
import spaceSchema from "../../../brainstorm/contracts/schemas/GraphSpaceDefinition.schema.json";
import type {
  EntityState, GraphDescriptorWire, GraphPatchWire, GraphSpaceDefinition, OntologyDefinition, ProjectionDefinition, RelationshipState,
} from "./wire-types";

export type Checked<T> = { ok: true; value: T } | { ok: false; errors: string[] };

const ajv = new Ajv2020({ strict: false, validateFormats: false, allErrors: false });
ajv.addSchema(patchSchema, "patch");
ajv.addSchema(descriptorSchema, "descriptor");
ajv.addSchema(ontologySchema, "ontology");
ajv.addSchema(projectionSchema, "projection");
ajv.addSchema(spaceSchema, "space");

function checker<T>(ref: string): (x: unknown) => Checked<T> {
  let fn: ValidateFunction | undefined;
  return (x) => {
    fn ??= ajv.getSchema(ref);
    if (!fn) throw new Error(`schema ${ref} not found`);
    if (fn(x)) return { ok: true, value: x as T };
    const seen = new Set<string>();
    const errors: string[] = [];
    for (const e of fn.errors ?? []) {
      const extra = (e.params as { additionalProperty?: string }).additionalProperty;
      const line = `${e.instancePath || "/"} ${e.message ?? "invalid"}${extra ? ` "${extra}"` : ""}`;
      if (!seen.has(line)) { seen.add(line); errors.push(line); }
      if (errors.length >= 4) break;
    }
    return { ok: false, errors };
  };
}

export const checkPatch = checker<GraphPatchWire>("patch");
export const checkDescriptor = checker<GraphDescriptorWire>("descriptor");
export const checkOntology = checker<OntologyDefinition>("ontology");
export const checkProjection = checker<ProjectionDefinition>("projection");
export const checkGraphSpace = checker<GraphSpaceDefinition>("space");
export const checkEntityState = checker<EntityState>("patch#/$defs/EntityState");
export const checkRelationshipState = checker<RelationshipState>("patch#/$defs/RelationshipState");

/** A record rejected at a seam. Surfaced in the UI, never dropped silently. */
export interface Rejection {
  source: string;   // which input (file name, stream, generator)
  where: string;    // "patch 3", "entity a", "line 7"
  errors: string[];
}
