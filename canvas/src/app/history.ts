// Revision history of one graph: a live Graph at a cursor, plus the ordered patches. Scrubbing applies or inverts patches.
import { Graph, invertPatch, type ApplyResult, type Conflict } from "../core/graph";
import { composeChanges } from "../core/diff";
import type { ChangeSet, Patch, Snapshot } from "../core/model";

export interface StepResult { result: Extract<ApplyResult, { ok: true }>; direction: 1 | -1; patchIndex: number }
export interface HistoryProblem { patch: number; reason: string; conflicts?: Conflict[] }

export class History {
  readonly graph: Graph;
  readonly patches: Patch[] = [];
  /** commit time of each patch as given by the source; NaN when the source gave none */
  readonly sourceTimes: number[] = [];
  /** number of patches applied to the snapshot; 0 is the snapshot itself */
  cursor = 0;
  readonly problems: HistoryProblem[] = [];
  private tailRevision: string;

  constructor(readonly snapshot: Snapshot) {
    this.graph = Graph.fromSnapshot(snapshot);
    this.tailRevision = snapshot.version.revision;
  }

  get length(): number { return this.patches.length; }
  get atHead(): boolean { return this.cursor === this.patches.length; }
  revisionAt(i: number): string { return i === 0 ? this.snapshot.version.revision : this.patches[i - 1].to.revision; }

  /** Add a patch received from a source. When the cursor is at the head the patch is applied immediately. */
  append(patch: Patch, time?: number): StepResult | null {
    if (patch.from.revision !== this.tailRevision) {
      this.problems.push({ patch: this.patches.length + 1, reason: `patch starts at revision ${patch.from.revision}, expected ${this.tailRevision}` });
      return null;
    }
    if (this.atHead) {
      const res = this.graph.apply(patch);
      if (!res.ok) { this.problems.push({ patch: this.patches.length + 1, reason: "preconditions failed", conflicts: res.conflicts }); return null; }
      this.patches.push(patch);
      this.sourceTimes.push(time ?? patch.time ?? NaN);
      this.tailRevision = patch.to.revision;
      this.cursor = this.patches.length;
      return { result: res, direction: 1, patchIndex: this.cursor };
    }
    this.patches.push(patch);
    this.sourceTimes.push(time ?? patch.time ?? NaN);
    this.tailRevision = patch.to.revision;
    return null;
  }

  /** Move to `target` patches applied. Stops early and records a problem when a patch does not apply. */
  seek(target: number): StepResult[] {
    const steps: StepResult[] = [];
    target = Math.max(0, Math.min(target, this.patches.length));
    while (this.cursor < target) {
      const p = this.patches[this.cursor];
      const res = this.graph.apply(p);
      if (!res.ok) { this.problems.push({ patch: this.cursor + 1, reason: "preconditions failed while scrubbing forward", conflicts: res.conflicts }); break; }
      this.cursor++;
      steps.push({ result: res, direction: 1, patchIndex: this.cursor });
    }
    while (this.cursor > target) {
      const res = this.graph.apply(invertPatch(this.patches[this.cursor - 1]));
      if (!res.ok) { this.problems.push({ patch: this.cursor, reason: "inverse patch does not apply", conflicts: res.conflicts }); break; }
      steps.push({ result: res, direction: -1, patchIndex: this.cursor });
      this.cursor--;
    }
    return steps;
  }

  /** The graph after the first k patches, as an independent copy. */
  graphAt(k: number): Graph {
    const g = Graph.fromSnapshot(this.snapshot);
    for (const p of this.patches.slice(0, Math.max(0, Math.min(k, this.patches.length)))) g.apply(p);
    return g;
  }

  /** Net change of patches (from, to], i.e. what happened between revision `from` and revision `to`. */
  changesBetween(from: number, to: number): ChangeSet {
    return composeChanges(this.patches.slice(Math.min(from, to), Math.max(from, to)));
  }
}
