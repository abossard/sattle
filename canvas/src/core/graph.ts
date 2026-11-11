// Mutable indexed graph with slot-stable storage, so renderers and layouts can index by slot.
// Patch application is atomic: preconditions (before-states) are checked first, then edits are applied.
import {
  type ChangeSet, type Edit, type Entity, type Patch, type Rel, type Snapshot, type Version,
  emptyChangeSet, entityEquals, relEquals,
} from "./model";
import type { OntologyRef } from "./wire-types";

export interface Conflict { editIndex: number; key: string; message: string }
export type ApplyResult =
  | { ok: true; changes: ChangeSet; dirtyNodes: number[]; dirtyEdges: number[]; removedSlots: Map<string, number>; ontology: { before: OntologyRef; after: OntologyRef }[] }
  | { ok: false; conflicts: Conflict[] };

export class Graph {
  version: Version | null = null;
  readonly nodeSlot = new Map<string, number>();
  nodes: (Entity | undefined)[] = [];
  readonly edgeSlot = new Map<string, number>();
  edges: (Rel | undefined)[] = [];
  /** resolved endpoint slots per edge slot, -1 when the endpoint entity is absent */
  edgeSrc: number[] = [];
  edgeDst: number[] = [];
  out: number[][] = [];
  inn: number[][] = [];
  private freeNodes: number[] = [];
  private freeEdges: number[] = [];
  /** entity id -> edge slots waiting for that entity to exist */
  private pending = new Map<string, number[]>();
  /**
   * References: an endpoint that relationships name and the graph does not hold gets a stub node, so the edge can be drawn. A stub takes a node
   * slot but is not an entity: `nodeSlot`, `entities()`, the counts and patches never see it, and the entity that arrives takes over its slot.
   */
  readonly stubSlot = new Map<string, number>();
  private stubSlots = new Set<number>();
  /** stub slots created, changed or freed since the last `apply` took them: their nodes are dirty */
  private touchedStubs = new Set<number>();
  /** how a stub is named and typed; the workspace sets it from the engine's `references` */
  stubFor: (id: string) => Entity = (id) => ({ id, type: "reference", props: { name: { kind: "text", value: `? ${id.slice(0, 18)}` } } });
  static readonly MAX_STUBS = 5000;
  isStub(slot: number): boolean { return this.stubSlots.has(slot); }
  get stubCount(): number { return this.stubSlots.size; }
  *stubs(): Generator<Entity> { for (const s of this.stubSlots) { const n = this.nodes[s]; if (n) yield n; } }
  /** Names the stubs again (the engine told why endpoints are missing); returns their slots. */
  relabelStubs(): number[] {
    for (const [id, slot] of this.stubSlot) this.nodes[slot] = this.stubFor(id);
    return [...this.stubSlots];
  }

  get nodeCount(): number { return this.nodeSlot.size; }
  get edgeCount(): number { return this.edgeSlot.size; }
  get unresolvedEdgeCount(): number {
    let n = 0;
    for (const s of this.edgeSlot.values()) if (this.edgeSrc[s] < 0 || this.edgeDst[s] < 0) n++;
    return n;
  }
  get revision(): string | null { return this.version?.revision ?? null; }

  static fromSnapshot(s: Snapshot): Graph {
    const g = new Graph();
    g.version = s.version;
    for (const e of s.entities) g.putEntity(e);
    for (const r of s.relationships) g.putRel(r);
    return g;
  }

  entity(id: string): Entity | undefined { const s = this.nodeSlot.get(id); return s === undefined ? undefined : this.nodes[s]; }
  rel(id: string): Rel | undefined { const s = this.edgeSlot.get(id); return s === undefined ? undefined : this.edges[s]; }

  /** Live entities in slot order (deterministic for the same history). Stubs are not entities. */
  *entities(): Generator<Entity> { for (let i = 0; i < this.nodes.length; i++) { const n = this.nodes[i]; if (n && !this.stubSlots.has(i)) yield n; } }
  *relationships(): Generator<Rel> { for (const e of this.edges) if (e) yield e; }

  toSnapshot(): Snapshot {
    return { version: this.version!, entities: [...this.entities()], relationships: [...this.relationships()] };
  }

  // ---- primitive mutations; return the slot touched ----
  putEntity(e: Entity): number {
    const existing = this.nodeSlot.get(e.id);
    if (existing !== undefined) { this.nodes[existing] = e; return existing; }
    const stub = this.stubSlot.get(e.id);
    if (stub !== undefined) {
      // the entity the stub stood for has arrived: it takes the slot, and the edges that pointed at the stub already point at it
      this.stubSlot.delete(e.id); this.stubSlots.delete(stub); this.touchedStubs.add(stub);
      this.nodes[stub] = e;
      this.nodeSlot.set(e.id, stub);
      this.pending.delete(e.id);
      return stub;
    }
    const slot = this.freeNodes.pop() ?? this.nodes.length;
    this.nodes[slot] = e;
    this.out[slot] = [];
    this.inn[slot] = [];
    this.nodeSlot.set(e.id, slot);
    const waiting = this.pending.get(e.id);
    if (waiting) {
      this.pending.delete(e.id);
      for (const es of waiting) {
        const r = this.edges[es];
        if (!r) continue;
        if (r.source === e.id && this.edgeSrc[es] < 0) { this.edgeSrc[es] = slot; this.out[slot].push(es); }
        if (r.target === e.id && this.edgeDst[es] < 0) { this.edgeDst[es] = slot; this.inn[slot].push(es); }
      }
    }
    return slot;
  }

  /** Remove an entity; incident edges stay and become unresolved. Returns incident edge slots. */
  dropEntity(id: string): { slot: number; incident: number[] } | undefined {
    const slot = this.nodeSlot.get(id);
    if (slot === undefined) return undefined;
    const incident = [...this.out[slot], ...this.inn[slot]];
    this.nodeSlot.delete(id);
    if (incident.length && this.stubSlots.size < Graph.MAX_STUBS) {
      // edges stay and now name a missing entity: the slot becomes its stub
      for (const es of this.out[slot]) this.park(id, es);
      for (const es of this.inn[slot]) this.park(id, es);
      this.nodes[slot] = this.stubFor(id);
      this.stubSlot.set(id, slot); this.stubSlots.add(slot); this.touchedStubs.add(slot);
      return { slot, incident };
    }
    for (const es of this.out[slot]) { this.edgeSrc[es] = -1; this.park(id, es); }
    for (const es of this.inn[slot]) { this.edgeDst[es] = -1; this.park(id, es); }
    this.nodes[slot] = undefined;
    this.out[slot] = [];
    this.inn[slot] = [];
    this.freeNodes.push(slot);
    return { slot, incident };
  }

  putRel(r: Rel): number {
    const existing = this.edgeSlot.get(r.id);
    if (existing !== undefined) {
      const old = this.edges[existing]!;
      if (old.source === r.source && old.target === r.target) { this.edges[existing] = r; return existing; }
      this.dropRel(r.id);
    }
    const slot = this.freeEdges.pop() ?? this.edges.length;
    this.edges[slot] = r;
    this.edgeSlot.set(r.id, slot);
    const s = this.nodeSlot.get(r.source) ?? this.stubFor_(r.source);
    const t = this.nodeSlot.get(r.target) ?? this.stubFor_(r.target);
    this.edgeSrc[slot] = s;
    this.edgeDst[slot] = t;
    if (s >= 0) this.out[s].push(slot);
    if (t >= 0) this.inn[t].push(slot);
    if (this.nodeSlot.get(r.source) === undefined) this.park(r.source, slot);
    if (this.nodeSlot.get(r.target) === undefined) this.park(r.target, slot);
    return slot;
  }

  /** The slot of the stub for a missing endpoint (made when there is none), or -1 once there are too many stubs to draw. */
  private stubFor_(id: string): number {
    const have = this.stubSlot.get(id);
    if (have !== undefined) return have;
    if (this.stubSlots.size >= Graph.MAX_STUBS) return -1;
    const slot = this.freeNodes.pop() ?? this.nodes.length;
    this.nodes[slot] = this.stubFor(id);
    this.out[slot] = [];
    this.inn[slot] = [];
    this.stubSlot.set(id, slot); this.stubSlots.add(slot); this.touchedStubs.add(slot);
    return slot;
  }
  /** A stub nothing points at any more is freed. */
  private releaseStub(slot: number): void {
    if (!this.stubSlots.has(slot) || this.out[slot].length || this.inn[slot].length) return;
    const id = this.nodes[slot]?.id;
    if (id !== undefined) this.stubSlot.delete(id);
    this.stubSlots.delete(slot); this.touchedStubs.add(slot);
    this.nodes[slot] = undefined;
    this.freeNodes.push(slot);
  }

  dropRel(id: string): number | undefined {
    const slot = this.edgeSlot.get(id);
    if (slot === undefined) return undefined;
    const r = this.edges[slot]!;
    const s = this.edgeSrc[slot];
    const t = this.edgeDst[slot];
    if (s >= 0) swapRemove(this.out[s], slot);
    if (t >= 0) swapRemove(this.inn[t], slot);
    if (s < 0 || this.stubSlots.has(s)) this.unpark(r.source, slot);
    if (t < 0 || this.stubSlots.has(t)) this.unpark(r.target, slot);
    if (s >= 0) this.releaseStub(s);
    if (t >= 0) this.releaseStub(t);
    this.edges[slot] = undefined;
    this.edgeSrc[slot] = -1;
    this.edgeDst[slot] = -1;
    this.edgeSlot.delete(id);
    this.freeEdges.push(slot);
    return slot;
  }

  private park(entityId: string, edgeSlot: number): void {
    const l = this.pending.get(entityId);
    if (l) l.push(edgeSlot); else this.pending.set(entityId, [edgeSlot]);
  }
  private unpark(entityId: string, edgeSlot: number): void {
    const l = this.pending.get(entityId);
    if (!l) return;
    swapRemove(l, edgeSlot);
    if (l.length === 0) this.pending.delete(entityId);
  }

  // ---- patches ----
  /** Preconditions of a patch against the current state. Empty array means applicable. */
  check(patch: Patch): Conflict[] {
    const conflicts: Conflict[] = [];
    if (this.version && patch.from.revision !== this.version.revision) {
      conflicts.push({ editIndex: -1, key: "version", message: `patch starts at revision ${patch.from.revision}, graph is at ${this.version.revision}` });
    }
    const overlayN = new Map<string, Entity | null>();
    const overlayE = new Map<string, Rel | null>();
    patch.edits.forEach((edit, i) => {
      if (edit.kind === "entity") {
        const cur = overlayN.has(edit.id) ? overlayN.get(edit.id)! : this.entity(edit.id) ?? null;
        const m = mismatch(edit.before, cur, entityEquals);
        if (m) conflicts.push({ editIndex: i, key: `entity ${edit.id}`, message: m });
        if (edit.after && edit.after.id !== edit.id) conflicts.push({ editIndex: i, key: `entity ${edit.id}`, message: `after-state id ${edit.after.id} differs from edit id` });
        overlayN.set(edit.id, edit.after);
      } else if (edit.kind === "relationship") {
        const cur = overlayE.has(edit.id) ? overlayE.get(edit.id)! : this.rel(edit.id) ?? null;
        const m = mismatch(edit.before, cur, relEquals);
        if (m) conflicts.push({ editIndex: i, key: `relationship ${edit.id}`, message: m });
        if (edit.after && edit.after.id !== edit.id) conflicts.push({ editIndex: i, key: `relationship ${edit.id}`, message: `after-state id ${edit.after.id} differs from edit id` });
        overlayE.set(edit.id, edit.after);
      }
    });
    return conflicts;
  }

  apply(patch: Patch): ApplyResult {
    const conflicts = this.check(patch);
    if (conflicts.length) return { ok: false, conflicts };
    const changes = emptyChangeSet();
    const dirtyNodes = new Set<number>();
    const dirtyEdges = new Set<number>();
    const removedSlots = new Map<string, number>();
    const ontology: { before: OntologyRef; after: OntologyRef }[] = [];
    for (const edit of patch.edits) {
      if (edit.kind === "entity") {
        if (edit.after) dirtyNodes.add(this.putEntity(edit.after));
        else {
          const d = this.dropEntity(edit.id);
          if (d) { dirtyNodes.add(d.slot); removedSlots.set(edit.id, d.slot); for (const es of d.incident) dirtyEdges.add(es); }
        }
        record(changes.entities, edit.id, edit.before, edit.after);
      } else if (edit.kind === "relationship") {
        if (edit.after) {
          const before = this.edgeSlot.get(edit.id);
          if (before !== undefined) dirtyEdges.add(before);
          dirtyEdges.add(this.putRel(edit.after));
        } else {
          const s = this.dropRel(edit.id);
          if (s !== undefined) dirtyEdges.add(s);
        }
        record(changes.relationships, edit.id, edit.before, edit.after);
      } else if (edit.kind === "ontology") {
        ontology.push({ before: edit.before, after: edit.after });
      }
    }
    // edges incident to added nodes may have been resolved
    for (const c of changes.entities.values()) {
      const slot = this.nodeSlot.get((c.after ?? c.before)!.id);
      if (slot !== undefined) for (const es of [...this.out[slot], ...this.inn[slot]]) dirtyEdges.add(es);
    }
    for (const sl of this.touchedStubs) dirtyNodes.add(sl);
    this.touchedStubs.clear();
    this.version = patch.to;
    return { ok: true, changes, dirtyNodes: [...dirtyNodes], dirtyEdges: [...dirtyEdges], removedSlots, ontology };
  }
}

function swapRemove(a: number[], v: number): void {
  const i = a.indexOf(v);
  if (i < 0) return;
  a[i] = a[a.length - 1];
  a.pop();
}

function mismatch<T>(expected: T | null, actual: T | null, eq: (a: T, b: T) => boolean): string | null {
  if (expected === null && actual === null) return null;
  if (expected === null) return "expected the element to be absent, but it exists";
  if (actual === null) return "expected the element to exist, but it is absent";
  return eq(expected, actual) ? null : "before-state differs from the current state";
}

function record<T>(m: Map<string, { kind: "added" | "removed" | "changed"; before: T | null; after: T | null }>, id: string, before: T | null, after: T | null): void {
  const prev = m.get(id);
  const b = prev ? prev.before : before;
  const kind = b === null ? (after === null ? null : "added") : after === null ? "removed" : "changed";
  if (kind === null) m.delete(id);
  else m.set(id, { kind, before: b, after });
}

/** The patch that undoes `p`. Pure. */
export function invertPatch(p: Patch): Patch {
  const edits: Edit[] = [];
  for (let i = p.edits.length - 1; i >= 0; i--) {
    const e = p.edits[i];
    if (e.kind === "ontology") edits.push({ kind: "ontology", before: e.after, after: e.before });
    else if (e.kind === "entity") edits.push({ kind: "entity", id: e.id, before: e.after, after: e.before });
    else if (e.kind === "relationship") edits.push({ kind: "relationship", id: e.id, before: e.after, after: e.before });
    else edits.push({ kind: "virtualRegion", id: e.id, before: e.after, after: e.before });
  }
  return { ...p, from: p.to, to: p.from, edits };
}
