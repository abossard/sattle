// The scratch copy a change is composed on, and the names and ids of the definition graph that composing needs: operators of a
// projection, the pieces of a row set, the ports of an operator. Shared by the gestures and by the statement kinds.
import { Catalog } from "../core/catalog";
import { Graph } from "../core/graph";
import type { Edit, Entity, Rel } from "../core/model";
import type { Palette, PaletteOperator } from "../editor/palette";
import { coalesce, relId, text } from "../editor/patches";
import { statementOf } from "./doc";

/** The engine's skeleton of a new operator: its node, ports and the placeholders of its required slots. */
export type Skeleton = (kind: string, container: string, name: string, params: Record<string, string>) => Promise<Edit[]>;

/** A scratch copy of the draft that edits are applied to as they are composed, so each sees the result of the ones before. */
export class Work {
  readonly edits: Edit[] = [];
  select: string[] = [];
  constructor(readonly g: Graph, readonly palette: Palette, readonly projection: string, readonly skeleton: Skeleton) {}
  private read: { at: number; cat: Catalog } | null = null;
  /** The scratch graph as a catalog, indexed again only after it changed. */
  get cat(): Catalog { if (this.read?.at !== this.edits.length) this.read = { at: this.edits.length, cat: new Catalog(this.g) }; return this.read.cat; }
  push(edits: Edit[]): void {
    const net = coalesce(edits);
    if (!net.length) return;
    const v = this.g.version ?? { tenant: "", graph: "catalog", revision: "0" };
    const r = this.g.apply({ from: v, to: { ...v, revision: String(Number(v.revision) + 1) }, projection: "catalog", profile: "extensionalFinite", edits: net, cause: { inputs: [], configurationVersion: "0" } });
    if (!r.ok) throw new Error(r.conflicts[0]?.message ?? "the edit does not apply");
    this.edits.push(...net);
  }
}

export const opId = (projection: string, name: string): string => `${projection}/op:${name}`;
export const nameOfOp = (id: string): string => id.slice(id.lastIndexOf("/op:") + 4);
export const under = (id: string, root: string): boolean => id === root || id.startsWith(`${root}/`);
export const spec = (palette: Palette, kind: string): PaletteOperator | undefined => palette.operators.find((o) => o.kind === kind);
/** The input a chain feeds: the first rows input of the operator kind. */
export const chainIn = (s: PaletteOperator | undefined): string | null => s?.ports.find((p) => p.direction === "in" && p.kind === "rows")?.name ?? null;
export const outOf = (s: PaletteOperator | undefined): string | null => s?.ports.find((p) => p.direction === "out")?.name ?? null;
export const port = (op: string, name: string): string => `${op}/port:${name}`;

/** The operators of a statement in chain order: `name#0`, `name#1`, ..., `name`. */
export function chain(w: Work, statement: string, container: string = w.projection): string[] {
  const piece = (n: string) => { const i = n.indexOf("#"); return i < 0 ? Number.MAX_SAFE_INTEGER : Number(n.slice(i + 1)); };
  return w.cat.children(container, "op").filter((o) => statementOf(nameOfOp(o)) === statement).sort((a, b) => piece(nameOfOp(a)) - piece(nameOfOp(b)));
}
export const pieceName = (statement: string, i: number, n: number): string => (i === n - 1 ? statement : `${statement}#${i}`);

/** Edits that move operators to new ids, with everything below them and every relationship that touches them. Removals first, so a name that is freed and taken in one change is one edit. */
export function renameEdits(g: Graph, moves: ReadonlyMap<string, string>): Edit[] {
  const map = (id: string): string => { for (const [a, b] of moves) if (under(id, a)) return b + id.slice(a.length); return id; };
  const gone: Edit[] = [], made: Edit[] = [];
  for (const e of [...g.entities()]) {
    const to = map(e.id);
    if (to === e.id) continue;
    const after: Entity = moves.has(e.id) ? { ...e, id: to, props: { ...e.props, name: text(nameOfOp(to)) } } : { ...e, id: to };
    gone.push({ kind: "entity", id: e.id, before: e, after: null });
    made.push({ kind: "entity", id: to, before: null, after });
  }
  for (const r of [...g.relationships()]) {
    const s = map(r.source), t = map(r.target);
    if (s === r.source && t === r.target) continue;
    const after: Rel = { ...r, id: relId(s, r.type, t), source: s, target: t };
    gone.push({ kind: "relationship", id: r.id, before: r, after: null });
    made.push({ kind: "relationship", id: after.id, before: null, after });
  }
  return [...gone, ...made];
}

export const relsFrom = (g: Graph, source: string): Rel[] => [...g.relationships()].filter((r) => r.source === source);
export const relsTo = (g: Graph, target: string): Rel[] => [...g.relationships()].filter((r) => r.target === target && r.type !== "contains");
/** An edit that creates a relationship the graph does not hold yet. */
export const fresh = (g: Graph, e: Edit): boolean => e.kind !== "relationship" || !g.rel(e.id);
export const drop = (r: Rel): Edit => ({ kind: "relationship", id: r.id, before: r, after: null });
export const add = (r: Rel, source: string, target: string): Edit => ({ kind: "relationship", id: relId(source, r.type, target), before: null, after: { ...r, id: relId(source, r.type, target), source, target } });
export const outPort = (w: Work, op: string): string | null => { const n = outOf(spec(w.palette, w.g.entity(op)?.type ?? "")); return n ? port(op, n) : null; };
export const inPort = (w: Work, op: string): string | null => { const n = chainIn(spec(w.palette, w.g.entity(op)?.type ?? "")); return n ? port(op, n) : null; };

