// Port model of operators. Mirrors the reference semantic validator in brainstorm/contracts/tests/validate_schemas.py:
// every operator output is "out"; join and union take "left" and "right"; emitters have no output; others take "in".
import type { Operator, OperatorKind, Wire } from "../wire-types";

export interface Ports { inputs: string[]; outputs: string[] }

export function portsOfKind(kind: OperatorKind): Ports {
  switch (kind) {
    case "input": case "match": case "fixpoint": return { inputs: [], outputs: ["out"] };
    case "join": case "union": return { inputs: ["left", "right"], outputs: ["out"] };
    case "emitEntity": case "emitRelationship": return { inputs: ["in"], outputs: [] };
    default: return { inputs: ["in"], outputs: ["out"] };
  }
}
export const portsOf = (op: Operator): Ports => portsOfKind(op.kind);

export const wireKey = (w: Wire): string => `${w.from.value}.${w.output}>${w.to.value}.${w.input}`;

export const OPERATOR_KINDS: OperatorKind[] = [
  "input", "match", "filter", "map", "join", "union", "distinct", "group", "traverse", "window", "emitEntity", "emitRelationship", "fixpoint",
];
