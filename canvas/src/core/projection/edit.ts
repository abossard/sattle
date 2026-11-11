// Pure edit operations on a ProjectionDefinition. Positions are not part of the definition and live in the editor.
import type { Operator, OperatorKind, ProjectionDefinition, Wire } from "../wire-types";
import { wireKey } from "./ports";

const id = (value: string) => ({ value });
const lit = (value: string) => ({ kind: "literal" as const, value: { kind: "text" as const, value } });

export function defaultOperator(kind: OperatorKind, opId: string): Operator {
  const base = { id: id(opId) };
  switch (kind) {
    case "input": return { ...base, kind, binding: "", row: { fields: [] } };
    case "match": return { ...base, kind, binding: "", pattern: { nodes: [], edges: [] } };
    case "filter": return { ...base, kind, predicate: { kind: "literal", value: { kind: "boolean", value: true } } };
    case "map": return { ...base, kind, fields: [] };
    case "join": return { ...base, kind, on: { kind: "literal", value: { kind: "boolean", value: true } } };
    case "union": return { ...base, kind, conflict: "requireEqual" };
    case "distinct": return { ...base, kind };
    case "group": return { ...base, kind, keys: [], aggregates: [] };
    case "traverse": return { ...base, kind, relationship: id(""), direction: "outgoing", minimumDepth: 1, maximumDepth: 1 };
    case "window": return { ...base, kind, timestampField: "", widthNanoseconds: 1_000_000_000, latePolicy: "ignore" };
    case "emitEntity": return { ...base, kind, identity: { domain: id("domain"), canonicalizationVersion: "1", components: [] }, type: id(""), properties: [] };
    case "emitRelationship": return { ...base, kind, identityRule: "", source: lit(""), target: lit(""), type: id(""), properties: [], retypeCollision: "requireEqual" };
    case "fixpoint": return {
      ...base, kind, relation: "", row: { fields: [] }, body: [], wires: [], result: id(""), finiteDomainBinding: "", maximumFacts: 1000, maximumIterations: 100,
    };
  }
}

export function uniqueOperatorId(def: ProjectionDefinition, base: string): string {
  const taken = new Set(def.operators.map((o) => o.id.value));
  if (!taken.has(base)) return base;
  for (let n = 2; ; n++) if (!taken.has(`${base}${n}`)) return `${base}${n}`;
}

export const addOperator = (def: ProjectionDefinition, op: Operator): ProjectionDefinition => ({ ...def, operators: [...def.operators, op] });

export const removeOperator = (def: ProjectionDefinition, opId: string): ProjectionDefinition => ({
  ...def,
  operators: def.operators.filter((o) => o.id.value !== opId),
  wires: def.wires.filter((w) => w.from.value !== opId && w.to.value !== opId),
  outputs: def.outputs.filter((o) => o.value !== opId),
});

/** Replace an operator, renaming references in wires and outputs when its id changed. */
export function replaceOperator(def: ProjectionDefinition, oldId: string, op: Operator): ProjectionDefinition {
  const rename = (x: { value: string }) => (x.value === oldId ? id(op.id.value) : x);
  return {
    ...def,
    operators: def.operators.map((o) => (o.id.value === oldId ? op : o)),
    wires: def.wires.map((w) => ({ ...w, from: rename(w.from), to: rename(w.to) })),
    outputs: def.outputs.map(rename),
  };
}

export const addWire = (def: ProjectionDefinition, w: Wire): ProjectionDefinition => ({ ...def, wires: [...def.wires, w] });
export const removeWire = (def: ProjectionDefinition, key: string): ProjectionDefinition => ({ ...def, wires: def.wires.filter((w) => wireKey(w) !== key) });
export const toggleOutput = (def: ProjectionDefinition, opId: string): ProjectionDefinition => ({
  ...def,
  outputs: def.outputs.some((o) => o.value === opId) ? def.outputs.filter((o) => o.value !== opId) : [...def.outputs, id(opId)],
});
export const addBinding = (def: ProjectionDefinition, name: string, contract = "sattle.graph", version = "1"): ProjectionDefinition => ({
  ...def, inputs: [...def.inputs, { name, contract, version }],
});
export const removeBinding = (def: ProjectionDefinition, name: string): ProjectionDefinition => ({ ...def, inputs: def.inputs.filter((i) => i.name !== name) });
