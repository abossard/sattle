// Effects: the only code that touches the runtime. Each action is applied to the workspace in the order it is dispatched,
// then the pure reducer folds it into the serializable state. Frequent actions (patches, telemetry) only schedule a throttled summary.
import { Workspace } from "../app/workspace";
import type { TelemetryHub } from "../app/telemetry-hub";
import { lightTheme, darkTheme } from "../render/theme";
import { runtime } from "./runtime";
import { SOURCES } from "./sources";
import type { Action, GraphSummary, State, TelemetryState, ThemeName } from "./types";
import { run as runEditor } from "./editor-effects";
import { applyFilter, applyHighlight, seekTime } from "./ws-ops";

export type Dispatch = (a: Action) => void;
export const rendererTheme = (t: ThemeName) => (t === "light" ? lightTheme : darkTheme);

/** Summaries keep their version while nothing changed, so panels that select it do not re-render at the summary rate. */
export function summaries(ws: Workspace): GraphSummary[] {
  return ws.info.graphs.map((g) => {
    const h = ws.histories.get(g.id)!;
    const next = { id: g.id, role: g.role, direction: g.direction, editable: g.editable, head: h.revisionAt(h.length), patches: h.length, nodes: h.graph.nodeCount, edges: h.graph.edgeCount, cursor: h.cursor };
    const prev = runtime.versions.get(g.id);
    const same = prev && prev.head === next.head && prev.patches === next.patches && prev.nodes === next.nodes && prev.edges === next.edges && prev.cursor === next.cursor;
    const version = same ? prev.version : (prev?.version ?? 0) + 1;
    runtime.versions.set(g.id, { ...next, version });
    return { id: next.id, role: next.role, direction: next.direction, editable: next.editable, head: next.head, patches: next.patches, nodes: next.nodes, edges: next.edges, version };
  });
}

const telemetryOf = (hub: TelemetryHub): TelemetryState => ({
  hasData: hub.hasData, enabled: hub.enabled, flow: hub.flow,
  edge: { width: hub.edge.width ?? "", flow: hub.edge.flow ?? "", state: hub.edge.state ?? "" },
  node: { ring: hub.node.ring ?? "", size: hub.node.size ?? "", state: hub.node.state ?? "" },
  meters: [...hub.store.metersOf("node"), ...hub.store.metersOf("edge")].map((m) => ({ id: m.id, target: m.target, label: m.label, unit: m.unit, warn: m.warn, min: m.min, max: m.max })),
});
export const telemetrySummary = (ws: Workspace): Record<string, TelemetryState> => Object.fromEntries([...ws.hubs].map(([g, hub]) => [g, telemetryOf(hub)]));

function bump(graph: string): void { const v = runtime.versions.get(graph); if (v) v.version++; }

const timers = new Map<string, ReturnType<typeof setTimeout>>();
/** Run `fn` once per `ms` at most, trailing. */
function throttle(key: string, ms: number, fn: () => void): void {
  if (timers.has(key)) return;
  timers.set(key, setTimeout(() => { timers.delete(key); fn(); }, ms));
}

function reportTime(ws: Workspace, dispatch: Dispatch): void {
  const points = ws.axisPoints();
  const last = points[points.length - 1];
  if (ws.following) ws.time = last;
  dispatch({ type: "time/changed", now: ws.time, following: ws.following, first: points[0], last });
}
const reportData = (ws: Workspace, dispatch: Dispatch): void => dispatch({ type: "data/summary", graphs: summaries(ws), telemetry: telemetrySummary(ws) });

async function openSource(id: string, s: State, dispatch: Dispatch): Promise<void> {
  runtime.ws?.dispose();
  runtime.ws = null;
  runtime.versions.clear();
  const def = SOURCES.find((x) => x.id === id);
  if (!def) { dispatch({ type: "source/failed", message: `unknown source ${id}` }); return; }
  const ws = new Workspace(def.make(), rendererTheme(s.theme), dispatch);
  runtime.ws = ws;
  try { await ws.load(); } catch (e) { if (runtime.ws === ws) dispatch({ type: "source/failed", message: (e as Error).message }); return; }
  if (runtime.ws !== ws) return;
  const conn = ws.source.connection;
  if (conn) conn.onChange(() => dispatch({ type: "feed/connection", status: { ...conn.status } }));
  const graphs = summaries(ws);
  dispatch({ type: "source/opened", graphs, catalog: ws.source.catalogGraph ?? "", ms: ws.loadMs, telemetry: telemetrySummary(ws), active: (graphs.find((g) => g.role !== "catalog") ?? graphs[0]).id });
  if (conn) dispatch({ type: "feed/connection", status: { ...conn.status } });
  reportTime(ws, dispatch);
}

export function runEffects(a: Action, s: State, dispatch: Dispatch): void {
  const ws = runtime.ws;
  if (a.type.startsWith("editor/") && a.type !== "editor/summary" && a.type !== "editor/select" && a.type !== "editor/projection" && a.type !== "editor/wiring" && a.type !== "editor/dismiss") { void runEditor(a, dispatch, s); return; }
  switch (a.type) {
    case "source/opening": runtime.session = null; void openSource(a.id, s, dispatch); break;
    case "feed/patch":
      ws?.applyPatch(a.graph, a.patch, a.time);
      if (ws) applyHighlight(ws, a.graph, s.filters[a.graph]); // the highlight mode the user chose, not always the last step
      throttle("data", 100, () => { if (runtime.ws === ws && ws) { reportData(ws, dispatch); reportTime(ws, dispatch); } });
      break;
    case "feed/telemetry":
      ws?.hubFor(a.graph).ingest(a.batch);
      throttle("time", 250, () => { if (runtime.ws === ws && ws) { reportTime(ws, dispatch); reportData(ws, dispatch); } });
      break;
    case "feed/telemetryReset": ws?.hubs.get(a.graph)?.reset(); bump(a.graph); break;
    // panes report changes (layout, telemetry applied, lineage loaded) far more often than panels need to look: at most 4 per second
    case "data/touched": throttle(`touched:${a.graph}`, 250, () => { if (runtime.ws === ws && ws) { bump(a.graph); reportData(ws, dispatch); } }); break;
    case "feed/resync": if (s.sourceId) setTimeout(() => dispatch({ type: "source/opening", id: s.sourceId }), 0); break;
    case "ui/dialog":
      if (a.dialog && !s.dialog) runtime.opener = document.activeElement as HTMLElement | null;
      else if (!a.dialog && s.dialog) setTimeout(() => runtime.opener?.focus(), 50);
      break;
    case "ui/theme": ws?.setTheme(rendererTheme(a.theme)); break;
    case "select/element": ws?.select(a.selection); break;
    case "pane/command": {
      const pane = ws?.panes.get(a.graph);
      if (!pane) break;
      if (a.command === "fit") pane.fit();
      else if (a.command === "relayout") pane.runLayout(pane.layoutKind, pane.layoutKind === "layered");
      else if (a.command === "focus") pane.focusSelection();
      else if (a.command === "collapse") pane.collapseAllAtDepth();
      else if (a.command === "expand") pane.expandAll();
      else if (a.command === "firstMatch") {
        const m = pane.matchSlots();
        const n = m.length ? pane.graph.nodes[m[0]] : null;
        if (n) { ws!.select({ graph: a.graph, kind: "entity", id: n.id }); pane.focusSlot(m[0]); dispatch({ type: "select/element", selection: { graph: a.graph, kind: "entity", id: n.id } }); }
      }
      break;
    }
    case "time/seek": if (ws) { seekTime(ws, a.t, s.filters); reportTime(ws, dispatch); reportData(ws, dispatch); } break;
    case "filter/set": {
      const pane = ws?.panes.get(a.graph);
      if (ws && pane) {
        const f = { ...s.filters[a.graph], ...a.filter };
        applyFilter(pane, f, a.filter);
        if ("hlMode" in a.filter || "hlSince" in a.filter) applyHighlight(ws, a.graph, f);
      }
      break;
    }
    case "telemetry/set": {
      const hub = ws?.hubs.get(a.graph);
      if (!hub) break;
      const c = a.change;
      if (c.enabled !== undefined) hub.enabled = c.enabled;
      if (c.flow !== undefined) hub.flow = c.flow;
      for (const [k, v] of Object.entries(c.edge ?? {})) (hub.edge as Record<string, string | null>)[k] = v || null;
      for (const [k, v] of Object.entries(c.node ?? {})) (hub.node as Record<string, string | null>)[k] = v || null;
      if (c.warn) hub.setMeterWarn(c.warn.target, c.warn.id, c.warn.value);
      hub.poke();
      break;
    }
  }
}
