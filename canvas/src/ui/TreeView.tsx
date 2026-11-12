// A hierarchical graph as a tree (Fluent FlatTree, only the rows in view are rendered): the model is src/app/tree-model.ts and knows nothing about what the
// graph is about. Each row says the element, its own and rolled-up state (glyph and word, never color alone), its children by state and how many pieces of
// support and cross relationships it has. The tree opens on the paths to what is wrong; the keyboard works on the whole list, not only on the rows in view.
import { Badge, Button, FlatTree, FlatTreeItem, MessageBar, MessageBarBody, SearchBox, Select, Switch, Text, TreeItemLayout, makeStyles, tokens, useHeadlessFlatTree_unstable, type HeadlessFlatTreeItemProps } from "@fluentui/react-components";
import { useEffect, useMemo, useRef, useState } from "react";
import { ROLE_GLYPH, ROLE_WORD } from "../app/color-by";
import type { EdgeKind } from "../source/presentation";
import { KIND_WORD } from "./EdgeKinds";
import { attentionPaths, buildTree, declaredHierarchyKind, defaultOpen, expandBelow, hierarchyKinds, isWrong, specForKind, typeAhead, visibleRows, type StateCell, type TreeNode, type TreeRow } from "../app/tree-model";
import { dispatch, useStore } from "../state/store";
import { useGraphSummary, useWorkspace } from "./common";

const ROW_H = 32;
const OVERSCAN = 30;

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateRows: "auto minmax(0, 1fr)", minHeight: 0, height: "100%" },
  bar: { display: "flex", alignItems: "center", columnGap: tokens.spacingHorizontalM, rowGap: tokens.spacingVerticalXS, flexWrap: "wrap", padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`, borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  scroll: { overflow: "auto", minHeight: 0, position: "relative" },
  cell: { display: "flex", alignItems: "center", columnGap: tokens.spacingHorizontalS, flexWrap: "nowrap", whiteSpace: "nowrap" },
  label: { overflow: "hidden", textOverflow: "ellipsis" },
  changed: { color: tokens.colorPaletteMarigoldForeground2 },
  grow: { flexGrow: 1 },
});

const useReader = makeStyles({ only: { position: "absolute", width: "1px", height: "1px", overflow: "hidden", clip: "rect(0 0 0 0)", whiteSpace: "nowrap" } });
const COLOR = { ok: "success", warning: "warning", danger: "danger", unknown: "informative", neutral: "subtle" } as const;
/**
 * A state as a badge: glyph (drawn, not read), the word of the value, and for a reader what it is and its role. The row takes its accessible name from
 * its content, so what is drawn and what is read cannot differ (WCAG 2.5.3), and color is never the only cue.
 */
export function StateBadge({ cell, label }: { cell: StateCell; label: string }) {
  const only = useReader().only;
  return (
    <Badge size="small" appearance="tint" color={COLOR[cell.role]} title={`${label}: ${cell.value}, ${ROLE_WORD[cell.role]}`}>
      <span aria-hidden="true">{ROLE_GLYPH[cell.role]} </span><span className={only}>{label} </span>{cell.value}<span className={only}>, {ROLE_WORD[cell.role]}</span>
    </Badge>
  );
}

/** The own state is a column of its own only when it means something (a value role) and says something the rolled-up state does not. */
export const showOwn = (n: TreeNode): boolean => !!n.own && n.own.role !== "neutral" && n.own.value !== n.rolled?.value;

const CHANGE_WORD = { added: "added", changed: "changed", removed: "removed" } as const;

export function TreeView({ graph }: { graph: string }) {
  const s = useStyles();
  const ws = useWorkspace(graph);
  const version = useGraphSummary(graph)?.version;
  const sel = useStore((x) => x.selection);
  const info = ws?.graphInfo(graph);
  const hist = ws?.histories.get(graph);
  // the kind of edge that is the hierarchy: what the ontology declares (composition when it says nothing); the others are links
  const [kindChoice, setKindChoice] = useState<EdgeKind | null>(null);
  const kinds = info ? hierarchyKinds(info) : [];
  const kind = kindChoice && kinds.includes(kindChoice) ? kindChoice : info ? declaredHierarchyKind(info) : "composition";
  const spec = useMemo(() => (info ? specForKind(info, kind) : undefined), [info, kind]);
  const model = useMemo(() => (hist && spec ? buildTree(hist.graph, spec, info?.presentation ?? []) : null), [hist, spec, info, version]); // eslint-disable-line react-hooks/exhaustive-deps
  const [expanded, setExpanded] = useState<Set<string> | null>(null);
  const [onlyWrong, setOnlyWrong] = useState(false);
  const [query, setQuery] = useState("");
  const [sort, setSort] = useState<"severity" | "label">("severity");
  const [showHidden, setShowHidden] = useState(false);
  const [top, setTop] = useState(0);
  const [height, setHeight] = useState(600);
  const [focusId, setFocusId] = useState<string | null>(null);
  const scroller = useRef<HTMLDivElement>(null);
  const typed = useRef({ text: "", at: 0 });

  // the tree opens on the paths to what is wrong, once per graph; after that what the reader opened and closed is theirs
  const opened = useRef<string>("");
  // a tree opens on the paths to where something is wrong, or else on its top levels (one row of a root says nothing)
  // so does a tree chosen by another kind of edge: its rows are other ones
  useEffect(() => { if (model && opened.current !== `${graph}|${kind}`) { opened.current = `${graph}|${kind}`; setExpanded(defaultOpen(model, 40)); } }, [model, graph, kind]);
  useEffect(() => {
    const el = scroller.current;
    if (!el) return;
    const measure = () => setHeight(el.clientHeight || 600);
    measure();
    if (typeof ResizeObserver === "undefined") return;
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const view = useMemo(() => ({ expanded: expanded ?? new Set<string>(), onlyWrong, query, sort, showHidden }), [expanded, onlyWrong, query, sort, showHidden]);
  const { rows, matches } = useMemo(() => (model ? visibleRows(model, view) : { rows: [] as TreeRow[], matches: new Set<string>() }), [model, view]);
  const change = useMemo(() => {
    if (!hist || hist.cursor === 0) return new Map<string, string>();
    const c = hist.changesBetween(hist.cursor - 1, hist.cursor);
    return new Map([...c.entities].map(([id, x]) => [id, CHANGE_WORD[x.kind]]));
  }, [hist, version, model]); // eslint-disable-line react-hooks/exhaustive-deps

  const items: HeadlessFlatTreeItemProps[] = useMemo(() => rows.map((r) => ({ value: r.node.id, parentValue: r.node.parent ?? undefined, itemType: r.hasChildren ? "branch" as const : "leaf" as const })), [rows]);
  const openItems = useMemo(() => new Set(rows.filter((r) => r.expanded).map((r) => r.node.id)), [rows]);
  const toggle = (id: string, open: boolean) => setExpanded((cur) => { const n = new Set(cur ?? []); if (open) n.add(id); else n.delete(id); return n; });
  const tree = useHeadlessFlatTree_unstable(items, { openItems, onOpenChange: (_, d) => toggle(String(d.value), d.open) });

  const start = Math.max(0, Math.floor(top / ROW_H) - OVERSCAN), end = Math.min(rows.length, Math.ceil((top + height) / ROW_H) + OVERSCAN);
  const visibleItems = useMemo(() => [...tree.items()].slice(start, end), [tree, start, end, openItems, rows]); // eslint-disable-line react-hooks/exhaustive-deps

  const indexOf = (id: string) => rows.findIndex((r) => r.node.id === id);
  const select = (id: string) => dispatch({ type: "select/element", selection: { graph, kind: "entity", id } });
  const goTo = (i: number) => {
    if (i < 0 || i >= rows.length) return;
    const el = scroller.current;
    if (el) { if (i * ROW_H < el.scrollTop) el.scrollTop = i * ROW_H; else if ((i + 1) * ROW_H > el.scrollTop + el.clientHeight) el.scrollTop = (i + 1) * ROW_H - el.clientHeight; setTop(el.scrollTop); }
    setFocusId(rows[i].node.id);
  };
  // focus the row the keyboard moved to, once it is rendered
  useEffect(() => {
    if (!focusId) return;
    const el = scroller.current?.querySelector<HTMLElement>(`[data-row-id="${CSS.escape(focusId)}"]`);
    if (el) { el.focus({ preventScroll: true }); setFocusId(null); }
  }, [focusId, visibleItems]);

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    const at = (e.target as HTMLElement).closest<HTMLElement>("[data-row-id]");
    if ((e.target as HTMLElement).closest("input, select, button") && !at) return;
    if (!at) return;
    const i = indexOf(at.dataset.rowId!);
    if (i < 0) return;
    const r = rows[i];
    const handled = (fn: () => void) => { e.preventDefault(); e.stopPropagation(); fn(); };
    switch (e.key) {
      case "ArrowDown": return handled(() => goTo(Math.min(rows.length - 1, i + 1)));
      case "ArrowUp": return handled(() => goTo(Math.max(0, i - 1)));
      case "Home": return handled(() => goTo(0));
      case "End": return handled(() => goTo(rows.length - 1));
      case "PageDown": return handled(() => goTo(Math.min(rows.length - 1, i + Math.max(1, Math.floor(height / ROW_H) - 1))));
      case "PageUp": return handled(() => goTo(Math.max(0, i - Math.max(1, Math.floor(height / ROW_H) - 1))));
      case "ArrowRight": return handled(() => { if (!r.hasChildren) return; if (!r.expanded) toggle(r.node.id, true); else goTo(i + 1); });
      case "ArrowLeft": return handled(() => { if (r.expanded) toggle(r.node.id, false); else if (r.node.parent) { const p = indexOf(r.node.parent); if (p >= 0) goTo(p); } });
      case "Enter": case " ": return handled(() => select(r.node.id));
      case "*": return handled(() => model && setExpanded((cur) => new Set([...(cur ?? []), ...expandBelow(model, r.node.id)])));
      default:
        if (e.key.length === 1 && /\S/.test(e.key)) {
          const now = Date.now();
          typed.current = { text: now - typed.current.at > 700 ? e.key : typed.current.text + e.key, at: now };
          const j = typeAhead(rows, i - (typed.current.text.length > 1 ? 1 : 0), typed.current.text);
          if (j >= 0) handled(() => goTo(j));
        }
    }
  };

  if (!model || !spec) return <div className={s.root}><MessageBar intent="info"><MessageBarBody>This graph has no hierarchy.</MessageBarBody></MessageBar></div>;
  const selected = sel && sel.graph === graph ? sel.id : null;
  const wrongCount = [...model.nodes.values()].filter(isWrong).length;
  const hiddenCount = [...model.nodes.values()].filter((x) => x.hidden).length;
  return (
    <div className={s.root}>
      <div className={s.bar} role="group" aria-label="Tree controls">
        <SearchBox aria-label="Find in the tree" placeholder="Find (opens the path)" value={query} onChange={(_, d) => setQuery(d.value)} />
        <Switch checked={onlyWrong} label="Only where something is wrong" onChange={(_, d) => setOnlyWrong(d.checked)} />
        {hiddenCount > 0 && <Switch checked={showHidden} label={`Show hidden (${hiddenCount})`} onChange={(_, d) => setShowHidden(d.checked)} />}
        {kinds.length > 1 && <Select aria-label="Hierarchy by" value={kind} onChange={(_, d) => setKindChoice(d.value as EdgeKind)}>{kinds.map((k) => <option key={k} value={k}>{KIND_WORD[k].word}</option>)}</Select>}
        <Select aria-label="Sort children" value={sort} onChange={(_, d) => setSort(d.value as "severity" | "label")}><option value="severity">Worst first</option><option value="label">By name</option></Select>
        <Button size="small" disabled={!selected} onClick={() => selected && setExpanded((cur) => new Set([...(cur ?? []), ...expandBelow(model, selected)]))}>Expand below selected</Button>
        <Button size="small" onClick={() => setExpanded(new Set())}>Collapse all</Button>
        <Button size="small" onClick={() => setExpanded(attentionPaths(model))}>Open paths to problems</Button>
        <span className={s.grow} />
        <Text size={200} role="status">{rows.length.toLocaleString()} rows · {model.nodes.size.toLocaleString()} nodes · {wrongCount.toLocaleString()} where something is wrong{query.trim() ? ` · ${matches.size.toLocaleString()} found` : ""}</Text>
      </div>
      <div className={s.scroll} ref={scroller} onScroll={(e) => setTop(e.currentTarget.scrollTop)} onKeyDownCapture={onKeyDown}>
        {rows.length === 0 ? <MessageBar intent="info"><MessageBarBody>{query.trim() || onlyWrong ? "Nothing matches." : "The graph has no elements."}</MessageBarBody></MessageBar> : (
          <div style={{ paddingTop: start * ROW_H, paddingBottom: Math.max(0, rows.length - end) * ROW_H }}>
            <FlatTree {...tree.getTreeProps()} aria-label={`Tree of ${graph}`} size="medium">
              {visibleItems.map((item) => {
                const r = rows[item.index];
                if (!r) return null;
                const n: TreeNode = r.node;
                const changed = change.get(n.id);
                return (
                  <FlatTreeItem {...item.getTreeItemProps()} key={n.id} data-row-id={n.id} aria-selected={selected === n.id} onClick={(e) => { e.preventDefault(); select(n.id); }}
                    style={{ height: ROW_H, backgroundColor: selected === n.id ? tokens.colorSubtleBackgroundSelected : undefined }}>
                    <TreeItemLayout aside={
                      <span className={s.cell}>
                        {n.counts.map((c) => <span key={c.value} title={`${c.n} ${c.value} (${ROLE_WORD[c.role]})`}><Text size={200}><span aria-hidden="true">{ROLE_GLYPH[c.role]}</span>{c.n} {c.value}</Text></span>)}
                        {n.supporting > 0 && <Badge size="small" appearance="outline" title={`${n.supporting} supporting elements`}>{n.supporting} supporting</Badge>}
                        {n.cross > 0 && <Badge size="small" appearance="outline" title={`${n.cross} cross relationships`}>{n.cross} {n.cross === 1 ? "link" : "links"}</Badge>}
                        {n.critical && <Badge size="small" appearance="filled" color="important" title="Marked critical">critical</Badge>}
                        {n.curatedBy && <Badge size="small" appearance="outline" title={`Curated by record ${n.curatedBy}`}><span aria-hidden="true">✎ </span>curated by {n.curatedBy}</Badge>}
                        {n.hidden && <Badge size="small" appearance="outline" title="Hidden by curation">hidden</Badge>}
                        {changed && <Badge size="small" appearance="outline" className={s.changed} title={`${changed} in this revision`}><span aria-hidden="true">Δ </span>{changed} in this revision</Badge>}
                        {showOwn(n) && <StateBadge cell={n.own!} label="own state" />}
                        {n.rolled && <StateBadge cell={n.rolled} label="state" />}
                      </span>}>
                      <span className={s.cell}><Text weight={r.match ? "bold" : "regular"} className={s.label} data-label="">{n.label}</Text>{" "}<Text size={200}>{n.type}</Text></span>
                    </TreeItemLayout>
                  </FlatTreeItem>
                );
              })}
            </FlatTree>
          </div>
        )}
      </div>
    </div>
  );
}
