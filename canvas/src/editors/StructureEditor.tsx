// The projection as text that is edited as structure: every line is a statement, a step or a slot, and what is missing is a hole.
// An author moves with the arrow keys, presses Enter on a line to change it, and types expressions as text. Nothing invalid can be
// typed between the lines, because there is no between: a line is put there by the palette or not at all.
import type { ReactElement } from "react";
import type { EditorProps } from "./host";
import { Outline } from "./Outline";

export function StructureEditor(p: EditorProps & { window?: number }): ReactElement {
  return <Outline {...p} label={`Structure of ${p.doc.name}`} />;
}
