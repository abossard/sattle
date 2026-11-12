// A projection as a tree of rows (Fluent FlatTree, only the rows in view are rendered): the surface of the structure editor and the
// keyboard and screen reader form of the graph editor. Arrow keys move over the whole list, Enter or F2 edits the row, Delete removes it.
// Keys typed in a row's editor never reach the tree, and Delete on a button or a field deletes nothing.
import { FlatTree, FlatTreeItem, TreeItemLayout, makeStyles, mergeClasses, tokens, useHeadlessFlatTree_unstable, type HeadlessFlatTreeItemProps } from "@fluentui/react-components";
import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactElement } from "react";
import type { Gesture } from "../editing/gestures";
import type { EditorProps } from "./host";
import { Editing, Problems, Tokens } from "./parts";
import { rowText, rowsOf, type Row } from "./rows";
import { useViewSize } from "./view-size";

export const ROW_H = 28;
const OVERSCAN = 20;

const useStyles = makeStyles({
  scroll: { overflow: "auto", minHeight: 0, height: "100%", position: "relative" },
  row: { height: `${ROW_H}px`, minHeight: `${ROW_H}px` },
  selected: { backgroundColor: tokens.colorSubtleBackgroundSelected },
});

/** The rows an author sees: every row whose statement and step are open. */
export function visibleRows(rows: Row[], closed: ReadonlySet<string>): Row[] {
  const hidden = new Set<string>();
  return rows.filter((r) => { const gone = r.parent !== null && (hidden.has(r.parent) || closed.has(r.parent)); if (gone) hidden.add(r.id); return !gone; });
}

export interface OutlineProps extends EditorProps { label: string; /** rows in view, when the caller knows it (tests, measurements) */ window?: number }

export function Outline(p: OutlineProps): ReactElement {
  const s = useStyles();
  const [closed, setClosed] = useState<ReadonlySet<string>>(new Set());
  const [editing, setEditing] = useState<string | null>(null);
  const [focusId, setFocusId] = useState<string | null>(null);
  const [top, setTop] = useState(0);
  const scroller = useRef<HTMLDivElement>(null);
  const all = useMemo(() => rowsOf(p.doc, p.palette, p.cat), [p.doc, p.palette, p.cat]);
  const rows = useMemo(() => visibleRows(all, closed), [all, closed]);
  const items: HeadlessFlatTreeItemProps[] = useMemo(() => rows.map((r) => ({ value: r.id, parentValue: r.parent ?? undefined, itemType: r.hasChildren ? "branch" as const : "leaf" as const })), [rows]);
  const openItems = useMemo(() => new Set(rows.filter((r) => r.hasChildren && !closed.has(r.id)).map((r) => r.id)), [rows, closed]);
  const toggle = (id: string, open: boolean) => setClosed((cur) => { const n = new Set(cur); if (open) n.delete(id); else n.add(id); return n; });
  const tree = useHeadlessFlatTree_unstable(items, { openItems, onOpenChange: (_, d) => toggle(String(d.value), d.open) });

  const count = Math.ceil(useViewSize(scroller, p.window ? { height: p.window * ROW_H } : undefined).height / ROW_H);
  const start = Math.max(0, Math.floor(top / ROW_H) - OVERSCAN), end = Math.min(rows.length, Math.floor(top / ROW_H) + count + OVERSCAN);
  const visible = useMemo(() => [...tree.items()].slice(start, end), [tree, start, end, openItems, rows]); // eslint-disable-line react-hooks/exhaustive-deps

  const goTo = (i: number) => {
    if (i < 0 || i >= rows.length) return;
    const el = scroller.current;
    if (el && el.clientHeight) { if (i * ROW_H < el.scrollTop) el.scrollTop = i * ROW_H; else if ((i + 1) * ROW_H > el.scrollTop + el.clientHeight) el.scrollTop = (i + 1) * ROW_H - el.clientHeight; setTop(el.scrollTop); }
    setFocusId(rows[i].id);
  };
  // focus the row the keyboard moved to, once it is rendered
  useEffect(() => {
    if (!focusId || editing) return;
    const el = scroller.current?.querySelector<HTMLElement>(`[data-row-id="${CSS.escape(focusId)}"]`);
    if (el) { el.focus({ preventScroll: true }); setFocusId(null); }
  }, [focusId, visible, editing]);
  // a gesture selected a new element (a step just added): go to its row when the author is working in this view
  const lastSelected = useRef(p.selected);
  useEffect(() => {
    if (p.selected === lastSelected.current) return;
    lastSelected.current = p.selected;
    if (p.selected && scroller.current?.contains(document.activeElement)) { const i = rows.findIndex((r) => r.id === p.selected); if (i >= 0) goTo(i); }
  }, [p.selected, rows]); // eslint-disable-line react-hooks/exhaustive-deps

  const done = (row: Row, g: Gesture | null) => { setEditing(null); setFocusId(row.id); if (g) p.onGesture(g); };
  const onKeyDown = (e: KeyboardEvent) => {
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    const target = e.target as HTMLElement;
    // only keys on a row itself: not in its editor, not on a button inside it
    if (target.closest("input, select, textarea, button") || !target.matches("[data-row-id]")) return;
    const i = rows.findIndex((r) => r.id === target.dataset.rowId);
    if (i < 0) return;
    const r = rows[i];
    const handled = (fn: () => void) => { e.preventDefault(); e.stopPropagation(); fn(); };
    const page = Math.max(1, count - 1);
    switch (e.key) {
      case "ArrowDown": return handled(() => goTo(Math.min(rows.length - 1, i + 1)));
      case "ArrowUp": return handled(() => goTo(Math.max(0, i - 1)));
      case "Home": return handled(() => goTo(0));
      case "End": return handled(() => goTo(rows.length - 1));
      case "PageDown": return handled(() => goTo(Math.min(rows.length - 1, i + page)));
      case "PageUp": return handled(() => goTo(Math.max(0, i - page)));
      case "ArrowRight": return handled(() => { if (!r.hasChildren) return; if (closed.has(r.id)) toggle(r.id, true); else goTo(i + 1); });
      case "ArrowLeft": return handled(() => { if (r.hasChildren && !closed.has(r.id)) toggle(r.id, false); else if (r.parent) goTo(rows.findIndex((x) => x.id === r.parent)); });
      case "Enter": case "F2": return handled(() => { if (r.edit) setEditing(r.id); else if (r.hasChildren) toggle(r.id, closed.has(r.id)); });
      case "Delete": case "Backspace": return handled(() => { if (r.remove && !p.busy) { setFocusId(rows[Math.max(0, i - 1)].id); p.onGesture(r.remove); } });
    }
  };

  return (
    <div className={s.scroll} ref={scroller} onScroll={(e) => setTop(e.currentTarget.scrollTop)} onKeyDownCapture={onKeyDown}>
      <div style={{ paddingTop: start * ROW_H, paddingBottom: Math.max(0, rows.length - end) * ROW_H }}>
        <FlatTree {...tree.getTreeProps()} aria-label={p.label} size="small">
          {visible.map((item) => {
            const r = rows[item.index];
            if (!r) return null;
            const isSelected = !!p.selected && r.id === p.selected;
            return (
              <FlatTreeItem {...item.getTreeItemProps()} key={r.id} data-row-id={r.id} data-kind={r.kind} aria-selected={isSelected} aria-label={editing === r.id ? rowText(r) : undefined}
                className={mergeClasses(s.row, isSelected && s.selected)}
                onClick={() => { if (r.element) p.onSelect(r.element); }} onDoubleClick={() => { if (r.edit) setEditing(r.id); }}>
                <TreeItemLayout aside={<Problems list={r.problems} />}>
                  {editing === r.id && r.edit ? <Editing spec={r.edit} done={(g) => done(r, g)} /> : <Tokens tokens={r.tokens} />}
                </TreeItemLayout>
              </FlatTreeItem>
            );
          })}
        </FlatTree>
      </div>
    </div>
  );
}
