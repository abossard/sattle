// The GraphSource seam. A source yields, for each graph of a graph space, an initial snapshot and then ordered patches.
// Implementations: static JSON files, simulated stream with timing controls, synthetic estate.
// A later engine WebSocket or NDJSON source implements the same interface; nothing above this file knows the transport.
import type { Patch, Snapshot } from "../core/model";
import type { Rejection } from "../core/validate";
import type { GraphDescriptorWire, OntologyDefinition, ProjectionDefinition } from "../core/wire-types";
import type { EdgeKind, PresentationHint, TreeSpec } from "./presentation";

export interface GraphInfo {
  id: string;
  role: "master" | "projection" | "catalog";
  descriptor: GraphDescriptorWire;
  ontology: OntologyDefinition;
  /** which property values mean ok, warning, danger or unknown (from the ontology), so a viewer can color by it */
  presentation?: PresentationHint[];
  /** engine 3.14: the ontology marks one relationship type as the hierarchy of this graph, so it can be read as a tree */
  tree?: TreeSpec;
  /** engine 3.17: what an edge of each relationship type means (communication, composition, dependency, support); a type that declares none is absent */
  edgeKinds?: Record<string, EdgeKind>;
  /** engine 3.17: per entity type, the property that names an element; a type that declares none is absent */
  labels?: Record<string, string>;
  /** definition of the projection that produces this graph (projection graphs only) */
  definition?: ProjectionDefinition;
  /** graphs this graph is derived from */
  inputs: string[];
  /** projection input binding name -> graph that feeds it */
  inputPorts?: Record<string, string>;
  /** engine: forward, lens, mixed or readonly, and whether some rule accepts edits */
  direction?: string;
  editable?: boolean;
  /** engine: name of the projection that produces this graph */
  projectionName?: string;
  /** one line on what the graph is, when the source knows (the projection's purpose) */
  description?: string;
  /** engine 3.4: the graph is a document (output ontology sattle.doc); the engine renders it to Markdown */
  document?: boolean;
}

/** What a graph says about itself that an activation can change. */
export type GraphMeta = Pick<GraphInfo, "ontology" | "presentation" | "tree" | "edgeKinds" | "labels">;

export interface SpaceInfo { id: string; graphs: GraphInfo[] }

/** Derived element -> the source elements it was derived from. Element keys are "n:<id>" / "e:<id>". */
export interface Correspondence { target: string; sources: { graph: string; key: string }[] }

/** A metric carried on nodes or edges. Telemetry is time-indexed, high rate and never creates revisions. */
export interface Meter {
  /** property id; matches an ontology property with role "telemetry" */
  id: string;
  target: "node" | "edge";
  label: string;
  unit: string;
  min: number;
  max: number;
  /** values at or above this are in the warning state */
  warn?: number;
  /** "sqrt" spreads heavy tailed rates over the 8-bit range */
  scale?: "linear" | "sqrt";
  /** the range is not declared: it starts from the first values seen and grows when a value exceeds it */
  auto?: boolean;
}

export interface ChannelDefaults {
  edge: { width: string; flow: string; state: string };
  node: { ring: string; size: string; state: string };
}

/**
 * One sample set for one metric at one tick. A tick is the ordinal of a telemetry window, not a revision (engine PROTOCOL.md, "Ticks are not revisions"); a simulated stream delivers tick k together with patch k.
 * Dense batches (ids omitted) carry one value per element of the initial snapshot, in snapshot order.
 * Sparse batches carry ids (entity ids for nodes, relationship ids for edges). The wire is columnar on purpose: 300k values per tick.
 */
export interface TelemetryBatch {
  graph: string;
  tick: number;
  /** seconds since the stream start */
  t: number;
  target: "node" | "edge";
  property: string;
  ids?: string[];
  /** engine 3.15: elements whose value of this window (property and window start) was retracted; the value is dropped */
  removed?: string[];
  values: Float32Array;
}

/** Pull access to telemetry, for sources that can regenerate or replay any tick (static files, the synthetic estate). */
export interface TelemetryFeed {
  meters: Meter[];
  defaults: ChannelDefaults;
  /** seconds per tick */
  dt: number;
  /** last tick available when loading without a live stream */
  ticks: number;
  /** ticks the feed can keep producing for a simulated stream */
  maxTicks: number;
  /** `release` lets the page hand buffers back for reuse once the batches are ingested */
  fetch(graph: string, tick: number): Promise<{ batches: TelemetryBatch[]; release?: () => void }>;
}

export type StreamEvent =
  /** `time` is the commit logical time in seconds when the source has one */
  | { type: "patch"; patch: Patch; time?: number }
  | { type: "telemetry"; batch: TelemetryBatch }
  | { type: "rejected"; rejection: Rejection }
  /** history delivered; a live source keeps going after this */
  | { type: "caught-up" }
  /** the source replaced the graph state (history gap): the page should reload the source */
  | { type: "resync"; reason: string }
  /** stored telemetry is stale (a definition edit re-activated the space); batches follow */
  | { type: "telemetry-reset" }
  /** the definition edit that re-activated the space can change the ontology and the value roles of a graph: they are read again */
  | { type: "meta"; meta: GraphMeta }
  | { type: "end" };

/** Optional playback controls, offered by sources that replay recorded patches. */
export interface StreamControls {
  readonly state: "idle" | "playing" | "paused" | "ended";
  readonly speed: number;      // patches per second
  readonly delivered: number;
  readonly total: number;
  play(): void;
  pause(): void;
  step(): void;
  reset(): void;
  setSpeed(patchesPerSecond: number): void;
  onChange(cb: () => void): () => void;
}

export interface GraphSource {
  readonly name: string;
  /** Graph space structure, ontologies and descriptors. Rejected records are reported through onRejected. */
  open(): Promise<SpaceInfo>;
  snapshot(graph: string): Promise<Snapshot>;
  /** Ordered patches after the snapshot. The sink is called in order; a live source keeps calling it. */
  subscribe(graph: string, sink: (e: StreamEvent) => void): () => void;
  correspondences?(graph: string): Promise<Correspondence[]>;
  /** Re-run a projection definition. Sources without an engine answer "unavailable". */
  /** Records rejected by seam validation, as they occur. */
  onRejected(cb: (r: Rejection) => void): () => void;
  /** Release what the source holds open (a live connection); called when the workspace is closed or reloaded. */
  dispose?(): void;
  readonly controls?: StreamControls;
  /** Telemetry for the graphs of this space, when the source has any. A live source may also push `telemetry` stream events. */
  readonly telemetry?: TelemetryFeed;
  /** Live connection state, for sources that keep a connection open. */
  readonly connection?: { status: ConnectionStatus; onChange(cb: () => void): () => void };
  /** Definitions as text plus the catalog graph, for sources backed by the engine. */
  readonly definitions?: DefinitionService;
  /** id of the graph that holds the catalog (definitions typed by the kernel), when the source serves one */
  readonly catalogGraph?: string;
  /** Telemetry declarations of a graph (live sources), for the channel mapping. */
  meters?(graph: string): Promise<{ meters: Meter[]; defaults: Partial<ChannelDefaults> } | null>;
  /** The rendered Markdown of a document graph at `revision` (the head when absent); null when the graph is not a document. */
  document?(graph: string, revision?: string): Promise<DocumentText | null>;
  /** Where one derived element comes from, per the engine; null when the engine has none. */
  lineage?(graph: string, element: string): Promise<Lineage | null>;
  /** What an element is part of in other graphs, per the engine; null when it has none to give. */
  reverseLineage?(graph: string, element: string): Promise<ReverseLineage | null>;
  /** Endpoints that relationships of the graph name and the graph does not hold, read at the head. */
  references?(graph: string): Promise<Reference[]>;
}

/** A rendered document: the revision it is the state of, the digest of its bytes and the Markdown. */
export interface DocumentText { revision: string; digest: string; markdown: string }


/** Connection state of a live source, shown in the status bar. */
export interface ConnectionStatus {
  state: "connecting" | "live" | "reconnecting" | "closed" | "ended";
  detail: string;
  /** history gaps filled after a reconnect or a skipped revision */
  gapsFilled: number;
  reconnects: number;
}

/** Diagnostic from the engine's checker: positioned in the module text (line and col are 1-based, 0 when there is no position) and tied to a catalog element. */
export interface EngineDiagnostic { code: string; severity: "error" | "warning" | string; focus: string; message: string; line: number; col: number }

export interface ModuleText { module: string; text: string; version: string; digest: string; revision: string }

/** Where an element comes from, per the engine's lineage record. */
export interface Lineage {
  graph: string; element: string; kind: "entity" | "relationship"; projection: string | null; sink: string | null; rule: string | null; direction: string | null;
  operators: string[]; sources: { graph: string; element: string; kind: "entity" | "relationship"; role: string }[];
  sourceCount: string; complete: boolean; groupKey: unknown; inputs?: string[];
  /** engine 3.11: rows that support the element, and the cursor of the next page of `sources` (null on the last) */
  contributions?: string; next?: string | null;
}

/** Engine 3.11: the elements of other graphs that name an element of this graph as a source (what it is part of). */
export interface ReverseLineage {
  graph: string; element: string; kind: "entity" | "relationship";
  partOf: { graph: string; element: string; kind: "entity" | "relationship"; role: string; sink: string | null; rule: string | null }[];
  count: string; next: string | null;
  /** graphs that read this one through an aggregate: only group-key elements are known there */
  incompleteIn: string[];
}

/** Engine 3.11: an endpoint that relationships name and the graph does not hold, with why (`absent`, or `inOtherGraph` and which). */
export interface Reference {
  id: string; state: "absent" | "inOtherGraph"; graph: string | null; entityType: string;
  identity: { domain: string; key: Record<string, string> } | null;
  uses: { relationshipType: string; end: "source" | "target"; count: number }[];
}

/** Definitions held by the engine as text, with a catalog graph typed by the kernel. */
export interface DefinitionService {
  /** modules known to the engine with their canonical `.sattle` text */
  modules(): Promise<ModuleText[]>;
}
