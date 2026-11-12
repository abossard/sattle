// Operations on the workspace that effects run: time seeking, change highlighting, pane filter application.
import type { DataPane } from "../app/data-pane";
import type { Workspace } from "../app/workspace";
import { resolveColoring, tally, type Coloring, type Target } from "../app/color-by";
import { SUPPORT_GROUPING, resolveGrouping } from "../app/graph-view-model";
import type { FilterState } from "./types";

export function seekPane(ws: Workspace, graph: string, target: number, fs: FilterState | undefined): void {
  const hist = ws.histories.get(graph)!;
  const pane = ws.panes.get(graph);
  const steps = hist.seek(target);
  if (pane) { for (const s of steps) pane.applyStep(s); applyHighlight(ws, graph, fs); }
}

/** Move the whole space to time t: every graph shows the revision and the telemetry frame valid at that instant. */
export function seekTime(ws: Workspace, t: number, filters: Record<string, FilterState>): void {
  const points = ws.axisPoints();
  ws.time = t;
  ws.following = t >= points[points.length - 1];
  for (const hub of ws.hubs.values()) if (hub.hasData) hub.setTime(t);
  for (const g of ws.info.graphs) {
    if (g.role === "catalog") continue; // definitions are configuration, not scrubbed by data time
    const hist = ws.histories.get(g.id)!;
    const target = ws.cursorAt(g.id, t);
    if (hist.cursor !== target) seekPane(ws, g.id, target, filters[g.id]);
  }
}

export function applyHighlight(ws: Workspace, graph: string, fs: FilterState | undefined): void {
  const hist = ws.histories.get(graph)!;
  const pane = ws.panes.get(graph);
  if (!pane) return;
  const mode = fs?.hlMode ?? "step";
  if (mode === "off" || (hist.cursor === 0 && mode === "step")) pane.setHighlight(null);
  else if (mode === "since") pane.setHighlight(hist.changesBetween(Math.min(fs!.hlSince, hist.cursor), hist.cursor));
  else pane.setHighlight(hist.changesBetween(hist.cursor - 1, hist.cursor));
}

/** Apply the parts of the filter state that changed to the pane. */
export function applyFilter(pane: DataPane, f: FilterState, changed: Partial<FilterState>): void {
  const has = (k: keyof FilterState) => k in changed;
  if (has("search") || has("hiddenTypes") || has("propKey") || has("propOp") || has("propValue")) {
    pane.setFilter({ hiddenTypes: new Set(f.hiddenTypes), prop: f.propKey ? { key: f.propKey, op: f.propOp, value: f.propValue } : null, search: f.search });
  }
  if (has("focusDepth")) { pane.focusDepth = f.focusDepth; pane.refreshAll(); }
  if (has("hiddenKinds")) pane.setKindFilter(f.hiddenKinds);
  if (has("focusDim")) { pane.focusDim = f.focusDim; pane.setSelection(pane.selection); }
  if (has("layout") && pane.layoutKind !== f.layout) pane.runLayout(f.layout, true);
  if (has("grouping")) {
    const g = resolveGrouping(pane.info, f.grouping);
    // a pane that already groups this way keeps what the user collapsed or expanded
    if ((g?.type ?? null) !== (pane.grouping?.type ?? null)) {
      pane.setGrouping(g?.type ?? null, g?.childIsSource);
      // the supporting elements are collapsed onto what they support, so the elements with a state are what one sees first
      if (g && f.grouping === SUPPORT_GROUPING) { pane.collapseAllAtDepth(); pane.runLayout(pane.layoutKind, true); }
    }
  }
  if (has("colorNodes")) pane.setColoring("node", resolveColoring(pane.info, "node", f.colorNodes));
  if (has("colorEdges")) pane.setColoring("edge", resolveColoring(pane.info, "edge", f.colorEdges));
}

/** Count of entities per type in a graph at its cursor. */
export function typeCountsOf(ws: Workspace, graph: string): Map<string, number> {
  const m = new Map<string, number>();
  const g = ws.histories.get(graph)!.graph;
  for (const e of g.entities()) m.set(e.type, (m.get(e.type) ?? 0) + 1);
  for (const e of g.stubs()) m.set(e.type, (m.get(e.type) ?? 0) + 1);
  return m;
}

/** Shape and color of a type as the canvas draws it (a neutral circle when there is no pane). */
export function styleOf(ws: Workspace, graph: string, type: string): { shape: string; color: string } {
  const pane = ws.panes.get(graph);
  return pane ? { shape: pane.shapeName(type), color: pane.colorCss(type) } : { shape: "circle", color: "#8ec8ff" };
}

/** Counts per colored value over the nodes or the edges (see `tally`), how many have none, and the hint that maps the values to roles. */
export function valueCountsOf(ws: Workspace, graph: string, c: Coloring, target: Target = "node"): ReturnType<typeof tally> {
  const g = ws.histories.get(graph)!.graph;
  return tally(c, target === "node" ? g.entities() : g.relationships());
}
