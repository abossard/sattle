// Edit builders for catalog patches: entities and relationships by id, with the id scheme of the engine
// (`d:<module>/<slot>:<name>/...`, relationship id `<source>|<edge>|<target>`).
import type { Edit, Entity, Props, Rel } from "../core/model";
import type { GraphValue } from "../core/wire-types";

export const text = (value: string): GraphValue => ({ kind: "text", value });
export const relId = (source: string, type: string, target: string) => `${source}|${type}|${target}`;

export const ent = (id: string, type: string, props: Props = {}): Edit => ({ kind: "entity", id, before: null, after: { id, type, props } });
export const rel = (source: string, type: string, target: string, props: Props = {}): Edit => {
  const id = relId(source, type, target);
  return { kind: "relationship", id, before: null, after: { id, source, target, type, props } };
};
export const contains = (parent: string, child: string, slot: string, ordinal?: number): Edit =>
  rel(parent, "contains", child, ordinal === undefined ? { slot: text(slot) } : { slot: text(slot), ordinal: { kind: "integer", value: String(ordinal) } });

/** Undo of a list of edits: each edit swapped, in reverse order. */
export const invertEdits = (edits: Edit[]): Edit[] =>
  [...edits].reverse().map((e) => (e.kind === "entity" || e.kind === "relationship" ? ({ ...e, before: e.after, after: e.before } as Edit) : e));

type G = { entity(id: string): Entity | undefined; rel(id: string): Rel | undefined; entities(): Iterable<Entity>; relationships(): Iterable<Rel> };

/** Ids of an element and everything below it: `id/...` children and `id#n` pieces of a let chain. */
export function subtreeIds(g: G, id: string): string[] {
  const out: string[] = [];
  for (const e of g.entities()) if (e.id === id || e.id.startsWith(`${id}/`) || e.id.startsWith(`${id}#`)) out.push(e.id);
  return out;
}

/** Remove elements and every relationship that touches them (wires, governs, contains from the parent). */
export function removeEdits(g: G, ids: string[]): Edit[] {
  const gone = new Set(ids.flatMap((id) => subtreeIds(g, id)));
  const edits: Edit[] = [];
  for (const r of g.relationships()) if (gone.has(r.source) || gone.has(r.target)) edits.push({ kind: "relationship", id: r.id, before: r, after: null });
  for (const id of gone) { const e = g.entity(id)!; edits.push({ kind: "entity", id, before: e, after: null }); }
  return edits;
}

export function setPropEdit(g: G, id: string, name: string, value: GraphValue | null): Edit {
  const before = g.entity(id)!;
  const props = { ...before.props };
  if (value === null) delete props[name]; else props[name] = value;
  return { kind: "entity", id, before, after: { ...before, props } };
}

/** Copy elements (with their subtrees and the relationships among them) under new ids: `from` is replaced by `to` in every id. */
export function copyEdits(g: G, ids: string[], rename: (id: string) => string): Edit[] {
  const set = new Set(ids.flatMap((id) => subtreeIds(g, id)));
  const map = (id: string) => (set.has(id) ? rename(id) : id);
  const edits: Edit[] = [];
  for (const id of set) { const e = g.entity(id)!; const n = map(id); edits.push({ kind: "entity", id: n, before: null, after: { ...e, id: n, props: { ...e.props, ...(e.props.name ? { name: text(n.slice(n.lastIndexOf(":") + 1)) } : {}) } } }); }
  for (const r of g.relationships()) {
    if (!set.has(r.source) || !set.has(r.target)) continue; // wires to elements outside the copy are dropped
    const s = map(r.source), t = map(r.target), n = relId(s, r.type, t);
    edits.push({ kind: "relationship", id: n, before: null, after: { ...r, id: n, source: s, target: t } });
  }
  return edits;
}

/**
 * Edits that touch the same element more than once become one: first before-state, last after-state (an element removed and created
 * again by one edit is a change; one that ends as it began is no edit). A patch must name an element once.
 */
export function coalesce(edits: Edit[]): Edit[] {
  const order: string[] = [];
  const byKey = new Map<string, Edit>();
  for (const e of edits) {
    if (e.kind !== "entity" && e.kind !== "relationship") { order.push(`x${order.length}`); byKey.set(order[order.length - 1], e); continue; }
    const k = `${e.kind}:${e.id}`;
    const prev = byKey.get(k) as Extract<Edit, { kind: "entity" | "relationship" }> | undefined;
    if (!prev) { order.push(k); byKey.set(k, e); } else byKey.set(k, { ...e, before: prev.before } as Edit);
  }
  return order.map((k) => byKey.get(k)!).filter((e) => !((e.kind === "entity" || e.kind === "relationship") && JSON.stringify(e.before) === JSON.stringify(e.after)));
}
