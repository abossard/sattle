// The only place state changes. Pure: (state, action) -> state, no clocks, no I/O, no mutation.
import type { Action, GraphSummary, State } from "./types";
import { emptyEditor, emptyFilter } from "./types";

export const initialState = (): State => ({
  sourceId: "", generation: 0, load: { status: "idle", message: "", ms: 0 }, graphs: [], catalog: "", activeGraph: "", view: "data", theme: "light", dialog: null,
  selection: null, hover: null, time: { now: 0, following: true, first: 0, last: 0 }, connection: null, freshness: "static", rejections: [], rejectedTotal: 0, filters: {}, telemetry: {},
  announcement: "", editor: emptyEditor(),
});

const patchGraph = (graphs: GraphSummary[], id: string, f: (g: GraphSummary) => GraphSummary): GraphSummary[] => graphs.map((g) => (g.id === id ? f(g) : g));

export function reduce(s: State, a: Action): State {
  switch (a.type) {
    case "source/opening": return { ...initialState(), theme: s.theme, view: s.view, sourceId: a.id, generation: s.generation + 1, load: { status: "loading", message: "", ms: 0 } };
    case "source/opened": return {
      ...s, graphs: a.graphs, catalog: a.catalog, activeGraph: a.active, telemetry: a.telemetry, load: { status: "ready", message: "", ms: a.ms },
      filters: Object.fromEntries(a.graphs.map((g) => [g.id, emptyFilter()])),
    };
    case "source/failed": return { ...s, load: { status: "error", message: a.message, ms: 0 } };
    // the data itself lives in the runtime; effects apply these in order and report summaries (data/summary), so the state never holds a graph
    case "feed/patch": case "feed/telemetry": return s;
    case "data/summary": return { ...s, graphs: a.graphs, telemetry: a.telemetry };
    case "data/touched": return s;
    case "feed/telemetryReset": return { ...s, graphs: patchGraph(s.graphs, a.graph, (g) => ({ ...g, version: g.version + 1 })) };
    case "feed/resync": return { ...s, freshness: "resyncing", announcement: `Resynchronizing: ${a.reason}` };
    case "feed/rejected": return { ...s, rejections: [...s.rejections.slice(-199), { ...a.rejection, at: a.at }], rejectedTotal: s.rejectedTotal + 1 };
    case "feed/connection": return { ...s, connection: a.status, freshness: a.status.state === "live" ? "live" : a.status.state === "ended" ? "static" : a.status.state === "closed" ? "static" : "stale" };
    case "ui/view": return { ...s, view: a.view };
    case "ui/theme": return { ...s, theme: a.theme };
    case "ui/dialog": return { ...s, dialog: a.dialog };
    case "ui/activeGraph": return { ...s, activeGraph: a.graph };
    case "ui/announce": return { ...s, announcement: a.text };
    case "select/element": return { ...s, selection: a.selection, announcement: a.selection ? `Selected ${a.selection.kind} ${a.selection.id} in ${a.selection.graph}` : "Selection cleared" };
    case "editor/summary": return { ...s, editor: { ...s.editor, ...a.patch } };
    case "editor/select": return { ...s, editor: { ...s.editor, selected: a.ids, wiring: null } };
    case "editor/projection": return { ...s, editor: { ...s.editor, module: a.module, projection: a.projection, selected: [], wiring: null, preview: null } };
    case "editor/wiring": return { ...s, editor: { ...s.editor, wiring: a.from } };
    case "editor/dismiss": return { ...s, editor: { ...s.editor, ...(a.what === "preview" ? { preview: null } : a.what === "notice" ? { notice: "" } : a.what === "pending" ? { pending: null } : { releaseDiff: null }) } };
    // the engine and the session answer these; effects report with editor/summary
    case "editor/open": return { ...s, editor: { ...emptyEditor(), status: "opening" } };
    case "editor/do": case "editor/undo": case "editor/redo": case "editor/commit": case "editor/discard": case "editor/preview": case "editor/releases": case "editor/diffRelease": return s;
    case "pane/command": return s;
    case "hover/element": return { ...s, hover: a.selection };
    case "time/seek": return { ...s, time: { ...s.time, now: a.t, following: a.t >= s.time.last } };
    case "time/changed": return { ...s, time: { now: a.now, following: a.following, first: a.first, last: a.last } };
    case "filter/set": return { ...s, filters: { ...s.filters, [a.graph]: { ...(s.filters[a.graph] ?? emptyFilter()), ...a.filter } } };
    case "telemetry/set": {
      const t = s.telemetry[a.graph];
      if (!t) return s;
      const meters = a.change.warn ? t.meters.map((m) => (m.id === a.change.warn!.id && m.target === a.change.warn!.target ? { ...m, warn: a.change.warn!.value } : m)) : t.meters;
      return { ...s, telemetry: { ...s.telemetry, [a.graph]: { ...t, enabled: a.change.enabled ?? t.enabled, flow: a.change.flow ?? t.flow, edge: { ...t.edge, ...a.change.edge }, node: { ...t.node, ...a.change.node }, meters } } };
    }
  }
}
