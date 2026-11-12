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
  skeleton: Skeleton;
  /** send edits as one patch; the label names the step for undo */
  apply(edits: Edit[], label: string): Promise<Applied>;
}

/** Do a gesture on the draft: compose it against the draft graph, send it as one patch. Returns what to select, or why nothing changed. */
export async function perform(draft: DraftPort, projection: string, gesture: Gesture): Promise<{ ok: true; select: string[] } | { ok: false; error: string }> {
  const c = await compose(draft.graph, draft.palette, projection, gesture, draft.skeleton);
  if (!c.ok) return { ok: false, error: c.reason };
  if (!c.value.edits.length) return { ok: true, select: c.value.select };
  const a = await draft.apply(c.value.edits, c.value.label);
  return a.ok ? { ok: true, select: c.value.select } : a;
}

/** The projection as the editors show it. */
export const docOf = (draft: DraftPort, projection: string): ProjectionDoc | null => readProjection(new Catalog(draft.graph), draft.palette, projection, draft.diagnostics);
