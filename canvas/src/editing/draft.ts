// The draft an editor works on, as the editors need it: the definition graph, the palette, and one way to change it.
// The engine session (editor/session.ts) is one implementation; tests use one that lives in memory.
import { Catalog } from "../core/catalog";
import type { Graph } from "../core/graph";
import type { Edit } from "../core/model";
import type { Palette } from "../editor/palette";
import type { EngineDiagnostic } from "../source/types";
import { readProjection, type ProjectionDoc } from "./doc";
import { compose, type Gesture, type Skeleton } from "./gestures";

export type Applied = { ok: true } | { ok: false; error: string };
export interface DraftPort {
  readonly graph: Graph;
  readonly palette: Palette;
  readonly diagnostics: EngineDiagnostic[];
  /** the placeholders the draft knows: children a skeleton made only so that an operator prints. The draft forgets one when an edit removes or changes it. */
  readonly placeholders: Set<string>;
  skeleton: Skeleton;
  /** send edits as one patch; the label names the step for undo */
  apply(edits: Edit[], label: string): Promise<Applied>;
}

/** Do a gesture on the draft: compose it against the draft graph, send it as one patch. Returns what to select, or why nothing changed. */
export async function perform(draft: DraftPort, projection: string, gesture: Gesture): Promise<{ ok: true; select: string[] } | { ok: false; error: string }> {
  const c = await compose(draft.graph, draft.palette, projection, gesture, draft.skeleton, draft.placeholders);
  if (!c.ok) return { ok: false, error: c.reason };
  const a = c.value.edits.length ? await draft.apply(c.value.edits, c.value.label) : { ok: true as const };
  if (!a.ok) return a;
  remember(draft.placeholders, c.value.placeholders);
  return { ok: true, select: c.value.select };
}

/** Make `known` what a composed change says the placeholders are now: its skeletons' added, the replaced ones gone, the renamed ones under their new ids. */
export function remember(known: Set<string>, now: readonly string[]): void {
  known.clear();
  for (const id of now) known.add(id);
}

/** The projection as the editors show it. */
export const docOf = (draft: DraftPort, projection: string): ProjectionDoc | null => readProjection(new Catalog(draft.graph), draft.palette, projection, draft.diagnostics, draft.placeholders);
