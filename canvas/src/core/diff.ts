// Pure diff calculations: patch -> change set, composition of patches, graph vs graph, property diffs.
import type { Graph } from "./graph";
import {
  type ChangeSet, type ElementChange, type Entity, type Patch, type Props, type Rel,
  emptyChangeSet, entityEquals, relEquals, valueEquals, valueText,
} from "./model";

function classify<T>(before: T | null, after: T | null, eq: (a: T, b: T) => boolean): ElementChange<T> | null {
  if (before === null && after === null) return null;
  if (before === null) return { kind: "added", before, after };
  if (after === null) return { kind: "removed", before, after };
  return eq(before, after) ? null : { kind: "changed", before, after };
}

/** Net effect of an ordered list of patches. An element added then removed vanishes; removed then re-added becomes changed or nothing. */
export function composeChanges(patches: readonly Patch[]): ChangeSet {
  const firstN = new Map<string, Entity | null>(), lastN = new Map<string, Entity | null>();
  const firstE = new Map<string, Rel | null>(), lastE = new Map<string, Rel | null>();
  for (const p of patches) {
    for (const e of p.edits) {
      if (e.kind === "entity") { if (!firstN.has(e.id)) firstN.set(e.id, e.before); lastN.set(e.id, e.after); }
      else if (e.kind === "relationship") { if (!firstE.has(e.id)) firstE.set(e.id, e.before); lastE.set(e.id, e.after); }
    }
  }
  const out = emptyChangeSet();
  for (const [id, b] of firstN) { const c = classify(b, lastN.get(id)!, entityEquals); if (c) out.entities.set(id, c); }
  for (const [id, b] of firstE) { const c = classify(b, lastE.get(id)!, relEquals); if (c) out.relationships.set(id, c); }
  return out;
}

export const changesOfPatch = (p: Patch): ChangeSet => composeChanges([p]);

/** Difference between two graphs by element id. */
export function diffGraphs(a: Graph, b: Graph): ChangeSet {
  const out = emptyChangeSet();
  for (const e of a.entities()) { const c = classify(e, b.entity(e.id) ?? null, entityEquals); if (c) out.entities.set(e.id, c); }
  for (const e of b.entities()) if (!a.entity(e.id)) out.entities.set(e.id, { kind: "added", before: null, after: e });
  for (const r of a.relationships()) { const c = classify(r, b.rel(r.id) ?? null, relEquals); if (c) out.relationships.set(r.id, c); }
  for (const r of b.relationships()) if (!a.rel(r.id)) out.relationships.set(r.id, { kind: "added", before: null, after: r });
  return out;
}

export interface PropDiff { property: string; before: string | null; after: string | null }

/** Per-property difference, including type and endpoint changes as pseudo-properties. */
export function propDiff(before: Entity | Rel | null, after: Entity | Rel | null): PropDiff[] {
  const out: PropDiff[] = [];
  const bp: Props = before?.props ?? {};
  const ap: Props = after?.props ?? {};
  const keys = [...new Set([...Object.keys(bp), ...Object.keys(ap)])].sort();
  if (before && after && before.type !== after.type) out.push({ property: "type", before: before.type, after: after.type });
  if (before && after && "source" in before && "source" in after) {
    if (before.source !== after.source) out.push({ property: "source", before: before.source, after: after.source });
    if (before.target !== after.target) out.push({ property: "target", before: before.target, after: after.target });
  }
  for (const k of keys) {
    const x = bp[k], y = ap[k];
    if (x && y && valueEquals(x, y)) continue;
    out.push({ property: k, before: x ? valueText(x) : null, after: y ? valueText(y) : null });
  }
  return out;
}
