// What an editor is given, and the shell that gives it: the editors are views of a document and emit gestures; the shell
// performs a gesture on the draft and shows the document again. All five editors take the same props, so they can stand
// side by side on one draft.
import { useCallback, useMemo, useState } from "react";
import { Catalog } from "../core/catalog";
import type { ProjectionDoc } from "../editing/doc";
import { docOf, perform, type DraftPort } from "../editing/draft";
import type { Gesture } from "../editing/gestures";
import type { Palette } from "../editor/palette";

export interface EditorProps {
  doc: ProjectionDoc; palette: Palette; cat: Catalog;
  /** the element of the draft the author is on, shared by the editors */
  selected: string | null;
  busy: boolean;
  onSelect(id: string | null): void;
  onGesture(g: Gesture): void;
}

/** The props of an editor on `projection` of `draft`, and what the last gesture answered when it did not apply. */
export function useDraft(draft: DraftPort, projection: string): { props: EditorProps | null; notice: string } {
  const [version, setVersion] = useState(0);
  const [selected, setSelected] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState("");
  const onGesture = useCallback((g: Gesture) => {
    setBusy(true);
    void perform(draft, projection, g).then((r) => {
      setBusy(false);
      setNotice(r.ok ? "" : r.error);
      if (r.ok) { setVersion((v) => v + 1); if (r.select.length) setSelected(r.select[0]); }
    });
  }, [draft, projection]);
  // the draft graph changes in place: the version says when to read it again
  const cat = useMemo(() => new Catalog(draft.graph), [draft, version]);
  const doc = useMemo(() => docOf(draft, projection), [draft, projection, version]);
  return { props: doc && { doc, palette: draft.palette, cat, selected, busy, onSelect: setSelected, onGesture }, notice };
}
