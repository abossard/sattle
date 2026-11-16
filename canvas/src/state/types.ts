// Plain serializable state of the application and the named actions that change it.
// Large mutable data (graphs, histories, telemetry frames, GL scenes) lives in `runtime`, never in this state; components
// that read it select a version counter from the state, so a change to the data re-renders them exactly once.
import type { Patch } from "../core/model";
import type { Rejection } from "../core/validate";
import type { ConnectionStatus, EngineDiagnostic, TelemetryBatch } from "../source/types";
import type { IdentityEffect, Release } from "../editor/service";
import type { ProjectionSpec } from "../editor/structures";
import type { EditorEvent, EditorPhase } from "./editor-phase";

export type View = "data" | "space" | "editor" | "ontology";
export type ThemeName = "light" | "dark" | "contrast";
export type Dialog = "shortcuts" | "rejections" | null;
export interface Selection { graph: string; kind: "entity" | "relationship"; id: string }

export interface GraphSummary {
  id: string; role: "master" | "input" | "projection" | "catalog"; direction?: string; editable?: boolean;
  head: string; patches: number; nodes: number; edges: number;
  /** bumped by every action that changes the graph's data; components select it to re-render */
  version: number;
}

export interface FilterState { search: string; hiddenTypes: string[]; propKey: string; propOp: "equals" | "contains" | "not equals"; propValue: string; focusDepth: number; grouping: string; focusDim: boolean; /** "auto": the layout the graph reads best in, decided again as it changes; a kind: the person chose it */ layout: "auto" | "force" | "layered"; hlMode: "step" | "since" | "off"; hlSince: number; /** what colors nodes and edges: "@default" (the ontology's presentation hint when it has one), "" (type colors) or a property id */ colorNodes: string; colorEdges: string; /** edge kinds left out (engine 3.17); null: the default of the place the graph is shown (a tile leaves out communication and support) */ hiddenKinds: import("../source/presentation").EdgeKind[] | null; /** draw the relationship types that restate others (a roll-up edge beside what it rolls up); left out by default */ showRestated: boolean }
export const emptyFilter = (): FilterState => ({ search: "", hiddenTypes: [], propKey: "", propOp: "equals", propValue: "", focusDepth: 0, grouping: "@support", focusDim: true, layout: "auto", hlMode: "step", hlSince: 0, colorNodes: "@default", colorEdges: "@default", hiddenKinds: null, showRestated: false });

export interface TelemetryState {
  hasData: boolean; enabled: boolean; flow: boolean;
  edge: { width: string; flow: string; state: string }; node: { ring: string; size: string; state: string };
  meters: { id: string; target: "node" | "edge"; label: string; unit: string; warn?: number; min: number; max: number }[];
}

export interface PreviewSummary {
  scope: { rows: string; totalRows: string; complete: boolean; logicalTime: string }; milliseconds: string; diagnostics: EngineDiagnostic[];
  /** what identity resolution did in the previewed space, against the live one (a Link shows here, not in a graph) */
  identity?: IdentityEffect;
  graphs: { id: string; removed: boolean; entities: string; relationships: string; added: number; removed_: number; changed: number; truncated: boolean; hasSnapshot: boolean }[];
}
export interface ReleaseDiff { label: string; from: string; to: string; added: number; removed: number; changed: number; items: { kind: "added" | "removed" | "changed"; id: string; type: string }[] }

export interface EditorState {
  phase: EditorPhase;
  draft: string; base: string; revision: string;
  /** bumped by every change of the draft graph, so views re-read it from the runtime */
  version: number;
  module: string; projection: string;
  undo: string[]; redo: string[];
  diagnostics: EngineDiagnostic[];
  /** a message to the author that outlives a phase change (a refused edit, a failed preview) */
  notice: string;
  textVersion: number;
  releases: Release[];
  releaseDiff: ReleaseDiff | null;
  preview: PreviewSummary | null;
  headRevision: string;
  /** the last commit, said after the session it ended */
  committed: string;
}
export const emptyEditor = (): EditorState => ({ phase: { t: "closed" }, draft: "", base: "", revision: "", version: 0, module: "", projection: "", undo: [], redo: [], diagnostics: [], notice: "", textVersion: 0, releases: [], releaseDiff: null, preview: null, headRevision: "", committed: "" });

/** Everything an author can do to the draft. Effects turn a command into catalog edits, the engine checks them, the session records them for undo. */
export type Command =
  | { t: "delete"; ids: string[] }
  | { t: "newProjection"; spec: ProjectionSpec }
  | { t: "rowType"; module: string; name: string }
  | { t: "field"; rows: string; name: string; kind: string; optional: boolean }
  | { t: "constraint"; module: string; kind: string; target: string; properties: string[]; severity: string; attrs: Record<string, string> }
  | { t: "equivalence"; module: string; name: string; mode: string; threshold: string; between: string[] }
  | { t: "valueRole"; property: string; value: string; role: string }
  | { t: "setValueRole"; id: string; role: string }
  | { t: "text"; module: string; text: string }
  | { t: "restore"; digest: string };

export interface State {
  sourceId: string;
  /** incremented when a source is opened: components that hold runtime objects key on it */
  generation: number;
  load: { status: "idle" | "loading" | "ready" | "error"; message: string; ms: number };
  graphs: GraphSummary[];
  catalog: string;
  activeGraph: string;
  view: View;
  theme: ThemeName;
  dialog: Dialog;
  selection: Selection | null;
  hover: Selection | null;
  time: { now: number; following: boolean; first: number; last: number };
  connection: ConnectionStatus | null;
  /** "live", "stale" (the feed is not connected), or "resyncing" */
  freshness: "live" | "stale" | "resyncing" | "static";
  /** the last 200 rejections, and how many there have been in all */
  rejections: (Rejection & { at: number })[];
  rejectedTotal: number;
  filters: Record<string, FilterState>;
  telemetry: Record<string, TelemetryState>;
  announcement: string;
  editor: EditorState;
}

export type Action =
  // lifecycle
  /** `resync`: the same source is read again after a gap; the author's editor session and draft stay */
  | { type: "source/opening"; id: string; resync?: boolean }
  | { type: "source/opened"; graphs: GraphSummary[]; catalog: string; ms: number; telemetry: Record<string, TelemetryState>; active: string }
  | { type: "source/failed"; message: string }
  // server push, dispatched in the order the source delivers it
  | { type: "feed/patch"; graph: string; patch: Patch; time?: number }
  | { type: "data/summary"; graphs: GraphSummary[]; telemetry: Record<string, TelemetryState> }
  | { type: "data/touched"; graph: string }
  | { type: "feed/telemetry"; graph: string; batch: TelemetryBatch }
  | { type: "feed/telemetryReset"; graph: string }
  | { type: "feed/meta"; graph: string; meta: import("../source/types").GraphMeta }
  | { type: "feed/resync"; reason: string }
  | { type: "feed/rejected"; rejection: Rejection; at: number }
  | { type: "feed/connection"; status: ConnectionStatus }
  // editor
  | { type: "editor/open" }
  | { type: "editor/summary"; patch: Partial<Omit<EditorState, "phase">> }
  | { type: "editor/event"; event: EditorEvent }
  | { type: "editor/projection"; module: string; projection: string }
  | { type: "editor/do"; cmd: Command }
  /** what an author did in one of the notations (editors/Workbench): composed into one patch and one undo step */
  | { type: "editor/undo" }
  | { type: "editor/redo" }
  | { type: "editor/commit"; version?: string }
  | { type: "editor/discard" }
  | { type: "editor/preview"; graphs?: string[] }
  | { type: "editor/releases"; module: string }
  | { type: "editor/diffRelease"; from: string; to: string }
  | { type: "editor/dismiss"; what: "preview" | "notice" | "releaseDiff" }
  // interaction
  | { type: "ui/view"; view: View }
  | { type: "ui/theme"; theme: ThemeName }
  | { type: "ui/dialog"; dialog: Dialog }
  | { type: "ui/activeGraph"; graph: string }
  | { type: "ui/announce"; text: string }
  | { type: "select/element"; selection: Selection | null }
  | { type: "hover/element"; selection: Selection | null }
  | { type: "pane/command"; graph: string; command: "fit" | "relayout" | "focus" | "collapse" | "expand" | "firstMatch" }
  | { type: "time/seek"; t: number }
  | { type: "time/changed"; now: number; following: boolean; first: number; last: number }
  | { type: "filter/set"; graph: string; filter: Partial<FilterState> }
  | { type: "telemetry/set"; graph: string; change: Partial<Pick<TelemetryState, "enabled" | "flow">> & { edge?: Partial<TelemetryState["edge"]>; node?: Partial<TelemetryState["node"]>; warn?: { id: string; target: "node" | "edge"; value: number | undefined } } }
