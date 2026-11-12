// Plain serializable state of the application and the named actions that change it.
// Large mutable data (graphs, histories, telemetry frames, GL scenes) lives in `runtime`, never in this state; components
// that read it select a version counter from the state, so a change to the data re-renders them exactly once.
import type { Patch } from "../core/model";
import type { Rejection } from "../core/validate";
import type { ConnectionStatus, EngineDiagnostic, TelemetryBatch } from "../source/types";
import type { IdentityEffect, RefactorRequest, Release } from "../editor/service";
import type { ProjectionSpec } from "../editor/structures";
import type { Gesture } from "../editing/gestures";

export type View = "data" | "space" | "editor" | "ontology";
export type ThemeName = "light" | "dark" | "contrast";
export type Dialog = "shortcuts" | "rejections" | null;
export interface Selection { graph: string; kind: "entity" | "relationship"; id: string }

export interface GraphSummary {
  id: string; role: "master" | "projection" | "catalog"; direction?: string; editable?: boolean;
  head: string; patches: number; nodes: number; edges: number;
  /** bumped by every action that changes the graph's data; components select it to re-render */
  version: number;
}

export interface FilterState { search: string; hiddenTypes: string[]; propKey: string; propOp: "equals" | "contains" | "not equals"; propValue: string; focusDepth: number; grouping: string; focusDim: boolean; layout: "force" | "layered"; hlMode: "step" | "since" | "off"; hlSince: number; /** what colors nodes and edges: "@default" (the ontology's presentation hint when it has one), "" (type colors) or a property id */ colorNodes: string; colorEdges: string; /** edge kinds left out (engine 3.17); null: the default of the place the graph is shown (a tile leaves out communication and support) */ hiddenKinds: import("../source/presentation").EdgeKind[] | null }
export const emptyFilter = (): FilterState => ({ search: "", hiddenTypes: [], propKey: "", propOp: "equals", propValue: "", focusDepth: 0, grouping: "@support", focusDim: true, layout: "force", hlMode: "step", hlSince: 0, colorNodes: "@default", colorEdges: "@default", hiddenKinds: null });

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
  status: "idle" | "opening" | "ready" | "error"; message: string;
  draft: string; base: string; revision: string;
  /** bumped by every change of the draft graph, so views re-read it from the runtime */
  version: number;
  module: string; projection: string;
  selected: string[];
  wiring: string | null;
  undo: string[]; redo: string[];
  diagnostics: EngineDiagnostic[];
  busy: string;
  notice: string;
  textVersion: number;
  releases: Release[];
  releaseDiff: ReleaseDiff | null;
  /** a refactoring the engine planned, waiting for the author to apply or drop it */
  pending: ReleaseDiff | null;
  preview: PreviewSummary | null;
  headRevision: string;
  committed: string;
}
export const emptyEditor = (): EditorState => ({ status: "idle", message: "", draft: "", base: "", revision: "", version: 0, module: "", projection: "", selected: [], wiring: null, undo: [], redo: [], diagnostics: [], busy: "", notice: "", textVersion: 0, releases: [], releaseDiff: null, pending: null, preview: null, headRevision: "", committed: "" });

/** Everything an author can do to the draft. Effects turn a command into catalog edits, the engine checks them, the session records them for undo. */
export type Command =
  | { t: "addOperator"; kind: string; name?: string }
  | { t: "delete"; ids: string[] }
  | { t: "wire"; from: string; to: string }
  | { t: "unwire"; key: string }
  | { t: "param"; id: string; name: string; value: string }
  | { t: "expr"; id: string; slot: string; src: string }
  | { t: "named"; id: string; slot: string; childType: string; name: string; src: string; extra?: Record<string, string> }
  | { t: "child"; id: string; slot: string; childType: string; key: string; params: Record<string, string> }
  | { t: "set"; id: string; slot?: string; property: string; src: string }
  | { t: "ofType"; id: string; type: string }
  | { t: "ref"; id: string; rel: string; target: string }
  | { t: "newProjection"; spec: ProjectionSpec }
  | { t: "valuesRow"; id: string; cells: { name: string; src: string }[] }
  | { t: "rowType"; module: string; name: string }
  | { t: "field"; rows: string; name: string; kind: string; optional: boolean }
  | { t: "constraint"; module: string; kind: string; target: string; properties: string[]; severity: string; attrs: Record<string, string> }
  | { t: "equivalence"; module: string; name: string; mode: string; threshold: string; between: string[] }
  | { t: "policy"; id: string; policy: string }
  | { t: "ruleProp"; id: string; name: string; value: string }
  | { t: "valueRole"; property: string; value: string; role: string }
  | { t: "setValueRole"; id: string; role: string }
  | { t: "exprAdd"; id: string; slot: string; src: string }
  | { t: "bindParam"; binding: string; value: string }
  | { t: "bindNew"; port: string; graph: string; value: string }
  | { t: "portDefault"; port: string; value: string }
  | { t: "createKey"; id: string; domain: string }
  | { t: "governs"; id: string; target: string; on: boolean }
  | { t: "ruleSet"; id: string; slot: string; property: string; src: string }
  | { t: "read"; id: string; slot?: string; path: string }
  | { t: "patternNode"; id: string; variable: string; type: string }
  | { t: "patternEdge"; id: string; variable: string; from: string; to: string; type: string }
  | { t: "rule"; name: string; cls: string; reason: string; governs: string[] }
  | { t: "ruleClass"; id: string; cls: string }
  | { t: "copy"; ids: string[] }
  | { t: "paste" }
  | { t: "text"; module: string; text: string }
  /** plan a refactoring: the engine answers a patch, shown to the author before it is applied */
  | { t: "refactor"; req: RefactorRequest }
  | { t: "applyRefactor" }
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
  | { type: "editor/summary"; patch: Partial<EditorState> }
  | { type: "editor/select"; ids: string[] }
  | { type: "editor/projection"; module: string; projection: string }
  | { type: "editor/wiring"; from: string | null }
  | { type: "editor/do"; cmd: Command }
  /** what an author did in one of the notations (editors/Workbench): composed into one patch and one undo step */
  | { type: "editor/gesture"; gesture: Gesture }
  | { type: "editor/undo" }
  | { type: "editor/redo" }
  | { type: "editor/commit"; version?: string }
  | { type: "editor/discard" }
  | { type: "editor/preview"; graphs?: string[] }
  | { type: "editor/releases"; module: string }
  | { type: "editor/diffRelease"; from: string; to: string }
  | { type: "editor/dismiss"; what: "preview" | "notice" | "releaseDiff" | "pending" }
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
