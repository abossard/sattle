// The canvas alternative: the same graph as a Fluent DataGrid, synced with the selection in both directions.
import { DataGrid, DataGridBody, DataGridCell, DataGridHeader, DataGridHeaderCell, DataGridRow, Field, SearchBox, Select, TableCellLayout, Text, createTableColumn, makeStyles, tokens, type TableColumnDefinition } from "@fluentui/react-components";
import { useMemo, useState } from "react";
import { entityMatchesSearch } from "../app/graph-view-model";
import { ROLE_GLYPH, ROLE_WORD, colorValueOf, coloringLabel, resolveColoring } from "../app/color-by";
import { emptyFilter } from "../state/types";
import type { Entity, Rel } from "../core/model";
import { dispatch, useStore } from "../state/store";
import { styleOf } from "../state/ws-ops";
import { TypeGlyph, useWorkspace } from "./common";

const useStyles = makeStyles({ col: { display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalS, padding: tokens.spacingHorizontalM, minHeight: 0, overflow: "auto" } });
const LIMIT = 300;
type Row = { id: string; type: string; name: string; detail: string; value: string };

export function ElementsPanel({ graph }: { graph: string }) {
  const s = useStyles();
  const ws = useWorkspace(graph);
  const sel = useStore((x) => x.selection);
  const [kind, setKind] = useState<"entity" | "relationship">("entity");
  const [q, setQ] = useState("");
  const g = ws?.histories.get(graph)?.graph;
  const filters = useStore((x) => x.filters[graph]) ?? emptyFilter();
  const info = ws?.graphInfo(graph);
  const coloring = info ? resolveColoring(info, kind === "entity" ? "node" : "edge", kind === "entity" ? filters.colorNodes : filters.colorEdges) : null;
  const prop = coloring?.property ?? "";
  /** the colored property's value with its role glyph and word: the grid shows what the color says */
  const valueOf = (props: Record<string, import("../core/wire-types").GraphValue>): string => { const v = coloring ? colorValueOf(coloring, props) : undefined; if (!v) return prop ? "(no value)" : ""; return `${v.text}${v.role ? ` ${ROLE_GLYPH[v.role]} ${ROLE_WORD[v.role]}` : ""}`; };
  const version = useStore((x) => x.graphs.find((y) => y.id === graph)?.version);
  const { rows, total } = useMemo(() => {
    if (!g) return { rows: [] as Row[], total: 0 };
    const out: Row[] = [];
    let n = 0;
    const lab = (id: string) => { const e = g.entity(id); return e ? g.label(e) : id; };
    const want = q.toLowerCase();
    if (kind === "entity") {
      for (const e of g.entities() as Iterable<Entity>) {
        if (want && !entityMatchesSearch(e, q)) continue;
        n++;
        if (out.length < LIMIT) out.push({ id: e.id, type: e.type, name: g.label(e), detail: e.id, value: valueOf(e.props) });
      }
    } else {
      for (const r of g.relationships() as Iterable<Rel>) {
        if (want && !`${r.type} ${r.id} ${lab(r.source)} ${lab(r.target)}`.toLowerCase().includes(want)) continue;
        n++;
        if (out.length < LIMIT) out.push({ id: r.id, type: r.type, name: `${lab(r.source)} → ${lab(r.target)}`, detail: r.id, value: valueOf(r.props) });
      }
    }
    // the selected row is always shown, so the list follows the canvas
    if (sel && sel.graph === graph && sel.kind === kind && !out.some((r) => r.id === sel.id)) {
      const e = kind === "entity" ? g.entity(sel.id) : g.rel(sel.id);
      if (e) out.unshift({ id: e.id, type: e.type, name: kind === "entity" ? g.label(e) : `${lab((e as Rel).source)} → ${lab((e as Rel).target)}`, detail: e.id, value: valueOf(e.props) });
    }
    return { rows: out, total: n };
  }, [g, q, kind, sel, version, prop]); // eslint-disable-line react-hooks/exhaustive-deps
  const columns: TableColumnDefinition<Row>[] = useMemo(() => [
    createTableColumn<Row>({ columnId: "type", compare: (a, b) => a.type.localeCompare(b.type), renderHeaderCell: () => "Type", renderCell: (r) => <TableCellLayout media={ws ? <TypeGlyph {...styleOf(ws, graph, r.type)} /> : undefined}>{r.type}</TableCellLayout> }),
    createTableColumn<Row>({ columnId: "name", compare: (a, b) => a.name.localeCompare(b.name), renderHeaderCell: () => (kind === "entity" ? "Name" : "Source → target"), renderCell: (r) => <TableCellLayout truncate>{r.name}</TableCellLayout> }),
    ...(prop ? [createTableColumn<Row>({ columnId: "value", compare: (a, b) => a.value.localeCompare(b.value), renderHeaderCell: () => (coloring ? coloringLabel(coloring) : prop), renderCell: (r) => <TableCellLayout truncate>{r.value}</TableCellLayout> })] : []),
  ], [ws, graph, kind, prop]);
  if (!ws || !g) return null;
  const selected = sel && sel.graph === graph && sel.kind === kind ? new Set([sel.id]) : new Set<string>();
  return (
    <div className={s.col}>
      <Field label="List"><Select value={kind} onChange={(_, d) => setKind(d.value as typeof kind)}><option value="entity">Nodes ({g.nodeCount.toLocaleString()})</option><option value="relationship">Edges ({g.edgeCount.toLocaleString()})</option></Select></Field>
      <SearchBox value={q} aria-label="Filter the list" placeholder="Filter the list" onChange={(_, d) => setQ(d.value)} />
      <Text size={200} role="status" aria-live="polite">{total > rows.length ? `Showing ${rows.length} of ${total.toLocaleString()}. Refine the filter to see others.` : `${total.toLocaleString()} shown`}</Text>
      <DataGrid items={rows} columns={columns} getRowId={(r) => r.id} size="small" sortable selectionMode="single" selectedItems={selected} focusMode="composite" aria-label={kind === "entity" ? "Nodes of the graph" : "Edges of the graph"}
        onSelectionChange={(_, d) => { const id = [...d.selectedItems][0] as string | undefined; if (id) { dispatch({ type: "select/element", selection: { graph, kind, id } }); dispatch({ type: "pane/command", graph, command: "focus" }); } }}>
        <DataGridHeader><DataGridRow selectionCell={{ checkboxIndicator: { "aria-label": "Select row" } }}>{({ renderHeaderCell }) => <DataGridHeaderCell>{renderHeaderCell()}</DataGridHeaderCell>}</DataGridRow></DataGridHeader>
        <DataGridBody<Row>>{({ item, rowId }) => <DataGridRow<Row> key={rowId} selectionCell={{ radioIndicator: { "aria-label": `Select ${item.name}` } }}>{({ renderCell }) => <DataGridCell>{renderCell(item)}</DataGridCell>}</DataGridRow>}</DataGridBody>
      </DataGrid>
    </div>
  );
}
