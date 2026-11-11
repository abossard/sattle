// A ProjectionDefinition rendered as a node graph: operators, source bindings, output ontology, rules.
// The graph form is a plain Graph, so the same canvas engine and the same diff work on definitions.
import { Graph } from "../graph";
import { diffGraphs } from "../diff";
import { type ChangeKind, type ChangeSet, type Entity, type Rel, type Props } from "../model";
import type { ProjectionDefinition } from "../wire-types";
import { describeOperator } from "./describe";
import { portsOf, wireKey } from "./ports";

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

export const opNodeId = (opId: string) => `op:${opId}`;
export const BINDING = (name: string) => `in:${name}`;
export const ONTOLOGY_NODE = "ontology";

export function directionOf(def: ProjectionDefinition): Direction {
  const n = def.bidirectionalRules.length;
  return n === 0
    ? { mode: "forward-only", rules: 0, label: "forward only ⇢" }
    : { mode: "bidirectional", rules: n, label: `bidirectional ⇄ (${n} rule${n === 1 ? "" : "s"})` };
}

export function buildDefGraph(def: ProjectionDefinition): DefGraph {
  const nodes: DefNode[] = [];
  const edges: DefEdge[] = [];
  for (const b of def.inputs) {
    nodes.push({
      id: BINDING(b.name), kind: "binding", title: b.name, lines: [`${b.contract} v${b.version}`], inputs: [], outputs: ["out"],
      props: { contract: b.contract, version: b.version },
    });
  }
  for (const op of def.operators) {
    const d = describeOperator(op);
    const p = portsOf(op);
    nodes.push({ id: opNodeId(op.id.value), kind: "operator", opKind: op.kind, title: d.title, lines: d.lines, inputs: p.inputs, outputs: p.outputs, props: d.props });
  }
  nodes.push({
    id: ONTOLOGY_NODE, kind: "ontology", title: `${def.ontology.id} v${def.ontology.version}`, lines: ["output ontology", def.ontology.digest.value],
    inputs: ["in"], outputs: [], props: { digest: def.ontology.digest.value },
  });
  if (def.scaffold.entities.length || def.scaffold.relationships.length) {
    nodes.push({
      id: "scaffold", kind: "scaffold", title: "scaffold", lines: [`${def.scaffold.entities.length} entities`, `${def.scaffold.relationships.length} relationships`],
      inputs: [], outputs: ["out"], props: { entities: String(def.scaffold.entities.length), relationships: String(def.scaffold.relationships.length) },
    });
  }
  def.bidirectionalRules.forEach((r) => {
    nodes.push({
      id: `rule:${r.id}`, kind: "rule", title: r.id, lines: [
        `forward ${r.forward.operators.length} ops, backward ${r.backward.operators.length} ops`,
        `laws ${r.requiredLaws.join(", ") || "none"}`, `${r.correspondences.length} correspondence(s)`,
      ], inputs: [], outputs: [], props: {
        forward: String(r.forward.operators.length), backward: String(r.backward.operators.length), laws: r.requiredLaws.join(","),
        source: r.sourcePattern.nodes.map((n) => n.type.value).join(","), target: r.targetPattern.nodes.map((n) => n.type.value).join(","),
      },
    });
  });
  for (const w of def.wires) {
    const key = wireKey(w);
    edges.push({ id: `wire:${key}`, kind: "wire", from: opNodeId(w.from.value), fromPort: w.output, to: opNodeId(w.to.value), toPort: w.input, wireKey: key });
  }
  for (const op of def.operators) {
    if (op.kind === "input" || op.kind === "match") {
      if (def.inputs.some((i) => i.name === op.binding)) edges.push({ id: `binds:${op.binding}>${op.id.value}`, kind: "binds", from: BINDING(op.binding), fromPort: "out", to: opNodeId(op.id.value), toPort: "binding" });
    }
    if (op.kind === "emitEntity" || op.kind === "emitRelationship") {
      edges.push({ id: `emits:${op.id.value}`, kind: "emits", from: opNodeId(op.id.value), fromPort: "emit", to: ONTOLOGY_NODE, toPort: "in" });
    }
  }
  return { nodes, edges, direction: directionOf(def) };
}

/** Plain-graph view of a definition, for diffing and for rendering on the data-graph engine. */
export function defGraphToGraph(dg: DefGraph, revision: string): Graph {
  const ents: Entity[] = dg.nodes.map((n) => ({
    id: n.id, type: n.kind === "operator" ? `operator:${n.opKind}` : n.kind,
    props: Object.fromEntries(Object.entries({ name: n.title, ...n.props }).map(([k, v]) => [k, { kind: "text", value: v } as const])) as Props,
  }));
  const rels: Rel[] = dg.edges.map((e) => ({ id: e.id, source: e.from, target: e.to, type: e.kind, props: {} }));
  return Graph.fromSnapshot({ version: { tenant: "definition", graph: "definition", revision }, entities: ents, relationships: rels });
}

export const definitionGraph = (def: ProjectionDefinition): Graph => defGraphToGraph(buildDefGraph(def), def.version);

export interface DefinitionDiff {
  /** union of both versions: removed elements stay in, flagged */
  dg: DefGraph;
  nodes: Map<string, ChangeKind>;
  edges: Map<string, ChangeKind>;
  changes: ChangeSet;
  direction: { before: Direction; after: Direction; changed: boolean };
}

/** Graph diff of two versions of a projection definition (operators, bindings, ontology pin, rules, wires). */
export function diffDefinitions(before: ProjectionDefinition, after: ProjectionDefinition): DefinitionDiff {
  const a = buildDefGraph(before), b = buildDefGraph(after);
  const changes = diffGraphs(defGraphToGraph(a, before.version), defGraphToGraph(b, after.version));
  const nodes = new Map([...changes.entities].map(([id, c]) => [id, c.kind] as const));
  const edges = new Map([...changes.relationships].map(([id, c]) => [id, c.kind] as const));
  const seenN = new Set(b.nodes.map((n) => n.id)), seenE = new Set(b.edges.map((e) => e.id));
  const dg: DefGraph = {
    nodes: [...b.nodes, ...a.nodes.filter((n) => !seenN.has(n.id))],
    edges: [...b.edges, ...a.edges.filter((e) => !seenE.has(e.id))],
    direction: b.direction,
  };
  return { dg, nodes, edges, changes, direction: { before: a.direction, after: b.direction, changed: a.direction.mode !== b.direction.mode || a.direction.rules !== b.direction.rules } };
}

export interface DefGraphDiff { dg: DefGraph; nodes: Map<string, ChangeKind>; edges: Map<string, ChangeKind> }

const nodeSig = (n: DefNode): string => JSON.stringify([n.kind, n.opKind, n.title, n.lines, n.inputs, n.outputs, n.direction]);

/** Diff of two node graphs (for example the same projection at two catalog revisions). The result holds the union, removed elements included. */
export function diffDefGraphs(a: DefGraph, b: DefGraph): DefGraphDiff {
  const nodes = new Map<string, ChangeKind>(), edges = new Map<string, ChangeKind>();
  const an = new Map(a.nodes.map((n) => [n.id, n])), bn = new Map(b.nodes.map((n) => [n.id, n]));
  for (const [id, n] of bn) { const o = an.get(id); if (!o) nodes.set(id, "added"); else if (nodeSig(o) !== nodeSig(n)) nodes.set(id, "changed"); }
  for (const id of an.keys()) if (!bn.has(id)) nodes.set(id, "removed");
  const ae = new Set(a.edges.map((e) => e.id)), be = new Set(b.edges.map((e) => e.id));
  for (const id of be) if (!ae.has(id)) edges.set(id, "added");
  for (const id of ae) if (!be.has(id)) edges.set(id, "removed");
  const dg: DefGraph = { nodes: [...b.nodes, ...a.nodes.filter((n) => !bn.has(n.id))], edges: [...b.edges, ...a.edges.filter((e) => !be.has(e.id))], direction: b.direction };
  return { dg, nodes, edges };
}
