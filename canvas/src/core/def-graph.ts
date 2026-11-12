// The operator graph of a projection definition as the editor draws it: cards (operators, bindings, the output ontology, rules) and the wires between them.
export type DefNodeKind = "binding" | "operator" | "ontology" | "rule" | "scaffold";
export interface DefNode {
  id: string; kind: DefNodeKind; opKind?: string; title: string; lines: string[];
  inputs: string[]; outputs: string[]; props: Record<string, string>;
  /** direction class of a rule (catalog definitions) */
  direction?: string;
  /** id of the catalog element this card stands for */
  ref?: string;
}
export interface DefEdge { id: string; kind: "wire" | "binds" | "emits" | "over"; from: string; fromPort: string; to: string; toPort: string; wireKey?: string }
/** A fixpoint and its step: the operators wired from its `out` port that lead to the operator whose output carries the step result (`over`). */
export interface DefLoop { fixpoint: string; members: string[]; result: string | null; resultPort: string | null }
export interface DefGraph { nodes: DefNode[]; edges: DefEdge[]; direction: Direction; loops?: DefLoop[] }
export interface Direction { mode: "forward-only" | "bidirectional"; rules: number; label: string }
