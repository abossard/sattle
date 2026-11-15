// The Outline notation on the KQL outline (note 11, stage c): the reading as a tree of rows (Fluent FlatTree, only the rows in view are
// mounted), the graph line, inputs, parameters, functions, `let`s and steps, under each its operators and under those their parts. Arrow keys
// move, Right and Left open and close, Enter or F2 edits a row's text, Delete removes a row that is an item of a list. A pure view like Steps:
// a reading, the selected place and the busy mark in; a place to select and text changes out.
import { Button, FlatTree, FlatTreeItem, Input, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Text, TreeItemLayout, makeStyles, mergeClasses, tokens, useHeadlessFlatTree_unstable, type HeadlessFlatTreeItemProps } from "@fluentui/react-components";
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import type { Place } from "../../editor/kql";
import { add, listsOf, newOperatorAt, remove, set, starter, startersFor, type Column } from "../../editor/kql-edit";
import { outlineRows, type OutlineRow } from "../../editor/kql-outline";
import { useViewSize } from "../../editors/view-size";
import type { NotationProps } from "./KqlSteps";

const ROW_H = 28, OVERSCAN = 20;

const useStyles = makeStyles({
  scroll: { overflow: "auto", minHeight: 0, height: "100%", position: "relative" },
  busy: { opacity: 0.6 },
  row: { height: `${ROW_H}px`, minHeight: `${ROW_H}px` },
  selected: { backgroundColor: tokens.colorSubtleBackgroundSelected },
  line: { display: "flex", columnGap: tokens.spacingHorizontalS, alignItems: "baseline", minWidth: 0, whiteSpace: "nowrap" },
  kind: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, flexShrink: 0 },
  code: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200, overflow: "hidden", textOverflow: "ellipsis", minWidth: 0 },
});

const oneLine = (t: string) => t.replace(/\s+/g, " ").trim();
/** What a row is called to a screen reader and in its buttons. */
const nameOf = (r: OutlineRow) => `${r.kind}${r.label ? ` ${r.label}` : ""}`;

export function KqlOutline({ reading, selected, busy, onSelect, onChanges, window: given }: NotationProps & { /** rows in view, when the caller knows it */ window?: number }) {
  const s = useStyles();
  const all = useMemo(() => (reading ? outlineRows(reading.analysis) : []), [reading]);
  // operators start closed: the tree first shows the statements and their pipelines
  const [closed, setClosed] = useState<ReadonlySet<string> | null>(null);
  const shut = useMemo(() => closed ?? new Set(all.filter((r) => r.hasChildren && r.level > 1).map((r) => r.id)), [closed, all]);
  const rows = useMemo(() => { const hidden = new Set<string>(); return all.filter((r) => { const gone = r.parent !== null && (hidden.has(r.parent) || shut.has(r.parent)); if (gone) hidden.add(r.id); return !gone; }); }, [all, shut]);
  const [editing, setEditing] = useState<{ id: string; value: string } | null>(null);
  const [focusId, setFocusId] = useState<string | null>(null);
  const [top, setTop] = useState(0);
  const scroller = useRef<HTMLDivElement>(null);
  const toggle = (id: string, open: boolean) => setClosed(() => { const n = new Set(shut); if (open) n.delete(id); else n.add(id); return n; });
  const items: HeadlessFlatTreeItemProps[] = useMemo(() => rows.map((r) => ({ value: r.id, parentValue: r.parent ?? undefined, itemType: r.hasChildren ? "branch" as const : "leaf" as const })), [rows]);
  const openItems = useMemo(() => new Set(rows.filter((r) => r.hasChildren && !shut.has(r.id)).map((r) => r.id)), [rows, shut]);
  const tree = useHeadlessFlatTree_unstable(items, { openItems, onOpenChange: (_, d) => toggle(String(d.value), d.open) });
  const count = Math.ceil(useViewSize(scroller, given ? { height: given * ROW_H } : undefined).height / ROW_H);
  const start = Math.max(0, Math.floor(top / ROW_H) - OVERSCAN), end = Math.min(rows.length, Math.floor(top / ROW_H) + count + OVERSCAN);
  const visible = useMemo(() => [...tree.items()].slice(start, end), [tree, start, end]);
  // the row of the cursor: the deepest row whose place holds it (children follow their parents)
  const here = useMemo(() => (selected === null ? null : [...all].reverse().find((r) => r.kind !== "add" && r.place[0] <= selected && selected <= r.place[1])?.id ?? null), [all, selected]);

  const goTo = (i: number) => {
    if (i < 0 || i >= rows.length) return;
    const el = scroller.current;
    if (el && el.clientHeight) { if (i * ROW_H < el.scrollTop) el.scrollTop = i * ROW_H; else if ((i + 1) * ROW_H > el.scrollTop + el.clientHeight) el.scrollTop = (i + 1) * ROW_H - el.clientHeight; setTop(el.scrollTop); }
    setFocusId(rows[i].id);
  };
  useEffect(() => {
    if (!focusId || editing) return;
    const el = scroller.current?.querySelector<HTMLElement>(`[data-row-id="${CSS.escape(focusId)}"]`);
    if (el) { el.focus({ preventScroll: true }); setFocusId(null); }
  }, [focusId, visible, editing]);

  if (!reading) return <Text>Waiting for the engine's outline of the text…</Text>;
  const { text, analysis } = reading;
  const statements = analysis.statements as { kind?: string; operators?: { kind?: string; columns?: Column[] }[] }[];
  const offer = (i: number) => { const st = statements[i], k = newOperatorAt(st), cols = st.operators?.[k - 1]?.columns ?? []; return startersFor(st).flatMap((kind) => { const op = starter(kind, cols); return op ? [op] : []; }); };
  const addTo = (i: number, op: string) => { const ops = listsOf(analysis).find((l) => l.id === `statements.${i}.operators`); if (ops) { const e = add(text, ops, newOperatorAt(statements[i]), op); onChanges(e.changes, e.select); } };
  const commit = (r: OutlineRow, value: string) => { setEditing(null); setFocusId(r.id); const e = set([r.place], value); onChanges(e.changes, e.select); };

  const onKeyDown = (e: KeyboardEvent) => {
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    const target = e.target as HTMLElement;
    if (target.closest("input, button") || !target.matches("[data-row-id]")) return;
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
      case "ArrowRight": return handled(() => { if (!r.hasChildren) return; if (shut.has(r.id)) toggle(r.id, true); else goTo(i + 1); });
      case "ArrowLeft": return handled(() => { if (r.hasChildren && !shut.has(r.id)) toggle(r.id, false); else if (r.parent) goTo(rows.findIndex((x) => x.id === r.parent)); });
      case "Enter": case "F2": return handled(() => { if (r.kind === "add") target.querySelector<HTMLElement>("button")?.click(); else if (!busy) setEditing({ id: r.id, value: text.slice(r.place[0], r.place[1]) }); });
      case "Delete": case "Backspace": return handled(() => { if (r.item && r.item.list.items.length > 1 && !busy) { setFocusId(rows[Math.max(0, i - 1)].id); onChanges(remove(text, r.item.list, r.item.i).changes, null); } });
    }
  };

  return (
    <div className={mergeClasses(s.scroll, busy && s.busy)} ref={scroller} aria-busy={busy} onScroll={(e) => setTop(e.currentTarget.scrollTop)} onKeyDownCapture={onKeyDown}>
      <div style={{ paddingTop: start * ROW_H, paddingBottom: Math.max(0, rows.length - end) * ROW_H }}>
        <FlatTree {...tree.getTreeProps()} aria-label="Outline of the projection" size="small">
          {visible.map((item) => {
            const r = rows[item.index];
            if (!r) return null;
            const code = oneLine(text.slice(r.place[0], r.place[1]));
            return (
              <FlatTreeItem {...item.getTreeItemProps()} key={r.id} data-row-id={r.id} aria-selected={r.id === here} aria-label={r.kind === "add" ? `Add an operator to ${nameOf(all.find((x) => x.id === r.parent)!)}` : undefined}
                className={mergeClasses(s.row, r.id === here && s.selected)} onClick={() => { if (r.kind !== "add") onSelect(r.place as Place); }}>
                <TreeItemLayout>
                  {editing?.id === r.id
                    ? <Input size="small" aria-label={`Text of ${nameOf(r)}`} autoFocus value={editing.value} onChange={(_, d) => setEditing({ id: r.id, value: d.value })}
                        onKeyDown={(e) => { e.stopPropagation(); if (e.key === "Enter") commit(r, editing.value); else if (e.key === "Escape") { setEditing(null); setFocusId(r.id); } }} onBlur={() => setEditing(null)} />
                    : r.kind === "add"
                      ? <Menu>
                          <MenuTrigger disableButtonEnhancement><Button size="small" appearance="subtle" tabIndex={-1} disabled={busy}>+ operator</Button></MenuTrigger>
                          <MenuPopover><MenuList>{offer(r.adds!).map((op) => <MenuItem key={op} onClick={() => addTo(r.adds!, op)}>{op}</MenuItem>)}</MenuList></MenuPopover>
                        </Menu>
                      : <span className={s.line}><span className={s.kind}>{nameOf(r)}</span><span className={s.code}>{code}</span></span>}
                </TreeItemLayout>
              </FlatTreeItem>
            );
          })}
        </FlatTree>
      </div>
    </div>
  );
}
