// DataPane: one data graph on the canvas engine. Binds Graph (data) to Scene (render arrays), layout, selection and highlights.
import type { ApplyResult } from "../core/graph";
import { Graph } from "../core/graph";
import type { EdgeKind } from "../source/presentation";
import { type ChangeSet, type ElementKey, type Entity, type Rel } from "../core/model";
import { nameGraph } from "../core/naming";
import type { GraphValue } from "../core/wire-types";
import { placeIncremental } from "../layout/force";
import { LayoutClient } from "../layout/client";
import { Ex, EdgeKindCode, EdgeStyle, Scene, St, parseColor } from "../render/scene";
import { KIND_EDGE_COLOR, type Theme, edgeColorForType, styleForType, type TypeStyle } from "../render/theme";
import { GraphView, type Ghost, type Pick } from "../render/view";
import {
  FlagLayer, SUPPORT_GROUPING, resolveGrouping, type Grouping, type LayoutKind, type PaneModel, type PaneView, computeAnchors, detectGrouping, emptyPaneView, entityMatchesProp, entityMatchesSearch, hiddenCounts, labelPadding, neighbourhood, paneModel, sameDrawing,
} from "./graph-view-model";
import type { History, StepResult } from "./history";
import { hasHierarchyEdges, hasTreeRows, inHierarchy } from "./tree-model";
import { besidePositions, blamePath, hasDependencyBranches, kindAxes, restatingTypes } from "./edge-kinds";
import { tileThinsIsolated } from "./tile";
import type { GraphInfo } from "../source/types";
import { EDGE_CHANNELS, NODE_CHANNELS, type WindowHub } from "./window-hub";
import { ROLE_GLYPH, colorValueOf, entryOf, legendOf, resolveColoring, tally, type Coloring, type LegendEntry, type Target } from "./color-by";
import { roleColors } from "./roles";
import type { FilterState } from "../state/types";

export type Selection = { graph: string; kind: "entity" | "relationship"; id: string };
export type { LayoutKind } from "./graph-view-model";

export interface PaneEvents {
  select(sel: Selection | null, additive: boolean): void;
  hover(sel: Selection | null, x: number, y: number): void;
  activate(sel: Selection): void;
  changed(): void;
  announce(text: string): void;
  key(ev: KeyboardEvent): boolean;
}

/** A label that is a path (a resource id) is its last segment, and a long one is cut: the whole text is in the Inspector. */
export function shortLabel(l: string, max = 30): string {
  const t = l.includes("/") ? l.slice(l.lastIndexOf("/", l.length - 2) + 1).replace(/\/$/, "") || l : l;
  return t.length > max ? `${t.slice(0, max - 1)}…` : t;
}

export class DataPane {
  readonly scene = new Scene();
  readonly view: GraphView;
  readonly #layoutClient = new LayoutClient();
  #graph: Graph;
  /** what the person chose (filters, focus, kinds, layout, tile), and what the pane draws by as of the last full pass */
  private shown: PaneView = emptyPaneView();
  private model: PaneModel;
  /** the layout drawn now; `shown.layout` decides it */
  #layoutKind: LayoutKind = "force";
  #layoutDirection: "down" | "right" = "down";
  #layoutRunning = false;
  #layoutProgress = 0;
  #lastLayoutMs = 0;
  #grouping: Grouping | null = null;
  #collapsed = new Set<number>();
  /** what colors nodes and edges (null: the type colors) and the legend each was computed with */
  /** `none`: no element carries a value of the property, so the type colors stay (a graph of structure does not turn grey for a state it does not hold) */
  private coloring: Record<Target, { c: Coloring; legend: LegendEntry[]; none: boolean } | null> = { node: null, edge: null };
  /** a value appeared that the legend does not know: recompute it after the step */
  private legendStale = false;
  #selection: Selection | null = null;
  #hoverSel: Selection | null = null;
  #highlight: ChangeSet | null = null;

  private styles = new Map<string, TypeStyle>();
  private nodeFlags = new FlagLayer();
  private nodeExtra = new FlagLayer();
  private edgeFlags = new FlagLayer();
  private vis = new Uint8Array(0);
  private anchor: Int32Array | null = null;
  private groupCounts = new Map<number, number>();
  private lastPos = new Map<string, [number, number]>();
  private ghostList: Ghost[] = [];
  private matches = new Set<number>();
  private layoutSlots: Int32Array = new Int32Array(0);
  private disposed = false;
  private refreshSeq = 0;

  #hub: WindowHub | null = null;
  /** smoothed cost of copying frames into the GPU channels, ms */
  #windowsApplyMs = 0;
  /** the graph at the history's cursor */
  get graph(): Graph { return this.#graph; }
  get visibleReferenceCount(): number {
    let count = 0;
    for (const slot of this.#graph.stubSlot.values()) if (this.scene.nodeVisible(slot)) count++;
    return count;
  }
  /** the layout drawn now; the view's `layout` decides it */
  get layoutKind(): LayoutKind { return this.#layoutKind; }
  get layoutRunning(): boolean { return this.#layoutRunning; }
  get layoutProgress(): number { return this.#layoutProgress; }
  get lastLayoutMs(): number { return this.#lastLayoutMs; }
  /** a layout job is queued or running in the worker */
  get layoutBusy(): boolean { return this.#layoutClient.busy; }
  get selection(): Selection | null { return this.#selection; }
  /** the changes the pane marks (the last step, or since a revision) */
  get highlight(): ChangeSet | null { return this.#highlight; }
  /** smoothed cost of copying frames into the GPU channels, ms */
  get windowsApplyMs(): number { return this.#windowsApplyMs; }
  private hubOff: (() => void) | null = null;

  constructor(readonly info: GraphInfo, private readonly history: History, host: HTMLElement, private theme: Theme, private readonly ev: PaneEvents, hub: WindowHub | null = null) {
    this.#graph = history.graph;
    nameGraph(this.#graph, info.ontology, info.labels);
    this.model = this.modelNow();
    this.#layoutKind = this.model.layout;
    this.view = new GraphView(host);
    this.view.setTheme(theme);
    this.view.setScene(this.scene);
    this.view.hooks = {
      nodeLabel: (slot) => { const n = this.#graph.nodes[slot]; if (!n) return null; const g = this.groupCounts.get(slot); let l = (this.blame.blamed.has(n.id) ? "⚑ " : "") + shortLabel(this.#graph.label(n)); if (!this.#graph.isStub(slot)) l += this.valueMark("node", n.props); const sup = this.supportCounts.get(slot); if (sup) l += ` (+${sup})`; return g ? `${l} (+${g})` : l; },
      nodeGlyph: (slot) => { const st = this.scene.nodeState(slot); return st & St.Added ? "+" : st & St.Changed ? "Δ" : null; },
      edgeLabel: (slot) => { const r = this.#graph.edges[slot]; return r ? r.type + this.valueMark("edge", r.props) : null; },
      ghosts: () => this.ghostList,
      onHover: (item) => this.onHover(item),
      onSelect: (item, additive) => this.onPick(item, additive),
      onActivate: (item) => this.onActivate(item),
      onKey: (ev) => this.onKey(ev),
    };
    if (hub) this.attachWindows(hub);
    host.setAttribute("role", "application");
    host.setAttribute("aria-label", `Graph ${info.id}. Arrow keys move the selection along edges. Alt plus arrows pan. Plus and minus zoom. F fits the view.`);
  }

  // ---- windows ----
  attachWindows(hub: WindowHub): void {
    this.hubOff?.();
    this.#hub = hub;
    hub.revalidateAll();
    this.hubOff = hub.onChange(() => this.applyWindows());
    this.applyWindows();
  }

  /** Copy the frames of the chosen metrics into the GPU channels. No layout work and no instance rebuild. */
  private applyWindows(): void {
    const t0 = performance.now();
    const hub = this.#hub, sc = this.scene;
    if (!hub || !hub.hasData) { sc.windows.on = false; return; }
    sc.windows.on = hub.enabled;
    sc.windows.flow = hub.flow;
    sc.windows.warnNode = hub.warnByteOf("node");
    sc.windows.warnEdge = hub.warnByteOf("edge");
    if (hub.enabled) {
      for (let c = 0; c < NODE_CHANNELS.length; c++) sc.setChannel("node", c, hub.frame("node", c));
      for (let c = 0; c < EDGE_CHANNELS.length; c++) sc.setChannel("edge", c, hub.frame("edge", c));
    }
    this.view.requestRender();
    this.#windowsApplyMs += (performance.now() - t0 - this.#windowsApplyMs) * 0.1;
    this.ev.changed();
  }

  // ---- styling ----
  private styleOf(type: string): TypeStyle {
    let s = this.styles.get(type);
    if (!s) { s = styleForType(type, this.theme); this.styles.set(type, s); }
    return s;
  }

  setTheme(t: Theme): void { this.theme = t; this.styles.clear(); this.view.setTheme(t); this.refreshAll(); this.recolor(); }

  // ---- loading and layout ----
  /** `view`: what the pane shows from the start (the ontology view lays out layered, say) */
  start(view: Partial<PaneView> = {}, direction: "down" | "right" = "down"): void {
    this.#layoutDirection = direction;
    this.shown = { ...this.shown, ...view };
    this.refreshAll();
    this.#layoutKind = this.model.layout;
    // a large tree is unreadable leaf by leaf: group by its single relationship type and collapse the lowest level
    const types = this.relationshipTypes();
    if (this.#layoutKind === "layered" && this.#graph.nodeCount > 300 && types.length === 1) {
      this.#grouping = detectGrouping(this.#graph, types[0]);
      this.collapseAllAtDepth();
      return;
    }
    this.runLayout(this.#layoutKind, true);
  }

  private relayoutIfLayered(): void { if (this.#layoutKind === "layered") this.runLayout("layered", true); }

  /**
   * The layout the graph as it is now reads best in: a graph whose ontology declares a hierarchy and that has rows and edges of it is a tree (the root on
   * top, whatever its size); a tree or forest by its shape is layered too; the rest is drawn by force. A function of the graph alone, so a pane made
   * on an empty revision chooses again when the graph has grown.
   */
  private autoLayoutKind(): LayoutKind {
    const g = this.#graph, info = this.info;
    if (info.tree && hasTreeRows(g, info.tree) && hasHierarchyEdges(g, info.tree)) return "layered";
    if (g.nodeCount > 2 && g.edgeCount >= g.nodeCount * 0.5 && g.edgeCount <= g.nodeCount) {
      // a tree or forest: most nodes take part in an edge; many isolated nodes would make a flat strip
      let connected = 0;
      for (let i = 0; i < g.nodes.length; i++) if (g.nodes[i] && g.out[i].length + g.inn[i].length > 0) connected++;
      if (connected >= g.nodeCount * 0.8) return "layered";
    }
    return "force";
  }
  runLayout(kind: LayoutKind = this.#layoutKind, fitWhenDone = false): void {
    this.#layoutKind = kind;
    const g = this.#graph, sc = this.scene;
    // lay out what is visible: collapsed groups and filtered nodes do not take space
    // nodes and edges go to the layout in the order of their ids, not of their slots: the slots of a graph depend on how it got to this revision
    // (a scrub back and forth frees and reuses them), its ids do not, so a layered layout is a function of the revision alone
    const byId = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);
    const slots: number[] = [];
    for (let i = 0; i < g.nodes.length; i++) if (g.nodes[i] && (sc.nodeVisible(i) || i >= sc.nodeCount)) slots.push(i);
    slots.sort((a, b) => byId(g.nodes[a]!.id, g.nodes[b]!.id));
    const dense = new Int32Array(g.nodes.length).fill(-1);
    slots.forEach((s, i) => { dense[s] = i; });
    const n = slots.length;
    this.layoutSlots = Int32Array.from(slots);
    const edgeSlots: number[] = [];
    for (let e = 0; e < g.edges.length; e++) if (g.edges[e]) edgeSlots.push(e);
    edgeSlots.sort((a, b) => byId(g.edges[a]!.id, g.edges[b]!.id));
    // `src`/`dst` are indexed by the edge's place in id order; `edgeAt` maps it back to the slot
    const edgeAt = Int32Array.from(edgeSlots);
    const src = new Int32Array(edgeAt.length).fill(-1), dst = new Int32Array(edgeAt.length).fill(-1);
    for (let k = 0; k < edgeAt.length; k++) {
      const e = edgeAt[k];
      if (!sc.edgeVisible(e)) continue;
      const [a, b] = sc.edgeEnds(e);
      if (a < 0 || b < 0 || dense[a] < 0 || dense[b] < 0) continue;
      src[k] = dense[a]; dst[k] = dense[b];
    }
    // a declared hierarchy ranks the layers: parent above child, what explains a row below it; dependencies between rows do not rank
    const tree = kind === "layered" && this.info.tree && hasHierarchyEdges(g, this.info.tree) ? this.info.tree : undefined;
    const branches = !!(tree && this.info.edgeKinds && hasDependencyBranches(g, tree, this.info.edgeKinds, (e) => sc.edgeVisible(e)));
    if (tree) {
      // what explains an element (a signal, evidence) hangs below it: the end of a support edge that takes part in nothing else is the child
      const isSupport = (type: string) => (this.kindOf(type) ?? tree.edgeRoles[type]) === "support";
      const structural = new Uint8Array(g.nodes.length);
      for (let e = 0; e < g.edges.length; e++) { const r = g.edges[e]; if (r && !isSupport(r.type)) { if (g.edgeSrc[e] >= 0) structural[g.edgeSrc[e]] = 1; if (g.edgeDst[e] >= 0) structural[g.edgeDst[e]] = 1; } }
      // with declared kinds the parts stack below their wholes along every vertical composition (a pod above its node, a node above its pool);
      // what a part depends on is placed beside it once the layers are done, and communication takes no part
      const axes = this.info.edgeKinds ? kindAxes(this.info) : null;
      for (let k = 0; k < edgeAt.length; k++) {
        const e = edgeAt[k], r = g.edges[e];
        if (src[k] < 0 || !r) continue;
        if (inHierarchy(tree, r.type)) { if (tree.hierarchy.childIsSource) [src[k], dst[k]] = [dst[k], src[k]]; }
        else if (branches && this.kindOf(r.type) === "dependency") { /* rank each dependency with its parent */ }
        else if (axes?.vertical.has(r.type)) { /* the source (the part) above the target */ }
        else if (isSupport(r.type)) { if (!structural[g.edgeSrc[e]] && structural[g.edgeDst[e]]) [src[k], dst[k]] = [dst[k], src[k]]; }
        else { src[k] = -1; dst[k] = -1; }
      }
    }
    this.#layoutRunning = true; this.#layoutProgress = 0;
    this.view.branchLeaves = branches ? new Set(slots) : null;
    if (this.view.branchLeaves) for (const parent of src) if (parent >= 0) this.view.branchLeaves.delete(slots[parent]);
    const scatter = (p: Float32Array) => {
      const pos = this.scene.positions;
      this.scene.ensureNodes(g.nodes.length);
      for (let i = 0; i < n; i++) { const s = this.layoutSlots[i]; this.scene.positions[2 * s] = p[2 * i]; this.scene.positions[2 * s + 1] = p[2 * i + 1]; }
      void pos;
      this.scene.positionsDirty = true; this.scene.positionsVersion++;
      this.view.requestRender();
    };
    const first = fitWhenDone;
    if (kind === "layered") {
      const w = new Float32Array(n), h = new Float32Array(n);
      for (let i = 0; i < n; i++) { const r = this.styleOf(g.nodes[slots[i]]!.type).radius; w[i] = r * 2; h[i] = r * 2; }
      const options = branches
        ? { direction: "right" as const, roots: "sources" as const, layerGap: 190, nodeGap: 26 }
        : { direction: this.#layoutDirection, layerGap: 70, nodeGap: this.info.edgeKinds ? 40 : 14 };
      const job = this.#layoutClient.run({ kind: "layered", n, src, dst, width: w, height: h, options });
      job.done.then((p) => {
        if (this.disposed) return;
        scatter(p);
        if (tree && this.info.edgeKinds && !branches) this.placeBeside();
        this.#layoutRunning = false; this.#lastLayoutMs = this.#layoutClient.lastMs; this.fit(!first); this.ev.changed();
      });
    } else {
      // keep existing positions as the starting point so a re-run refines instead of scrambling
      const start = new Float32Array(n * 2).fill(NaN);
      if (!first) for (let i = 0; i < n; i++) { const s = slots[i]; if (Number.isFinite(this.scene.positions[2 * s])) { start[2 * i] = this.scene.positions[2 * s]; start[2 * i + 1] = this.scene.positions[2 * s + 1]; } }
      const job = this.#layoutClient.run({ kind: "force", n, src, dst, positions: first ? undefined : start, options: { length: 30 } }, (p, it, total) => {
        this.#layoutProgress = it / total; scatter(p);
        if (first && it === 0) this.fit(false);
        this.ev.changed();
      });
      job.done.then((p) => { if (this.disposed) return; scatter(p); this.#layoutRunning = false; this.#layoutProgress = 1; this.#lastLayoutMs = this.#layoutClient.lastMs; if (first || n < 100) this.fit(!first); this.ev.changed(); });
    }
  }

  /** The elements on the side of the dependencies, to the right of the parts that need them (see `besidePositions`). */
  private placeBeside(): void {
    const s = this.scene;
    const moves = besidePositions(this.#graph, this.info, (slot) => (Number.isFinite(s.positions[2 * slot]) ? [s.positions[2 * slot], s.positions[2 * slot + 1]] : null), (slot) => !!this.vis[slot]);
    for (const [slot, [x, y]] of moves) { s.positions[2 * slot] = x; s.positions[2 * slot + 1] = y; }
    if (moves.size) { s.positionsDirty = true; s.positionsVersion++; this.view.requestRender(); }
  }

  /** What the canvas can write next to the nodes of the scene: the labels of the visible ones. */
  labelsShown(): string[] {
    const out: string[] = [];
    for (let i = 0; i < this.#graph.nodes.length; i++) if (this.scene.nodeVisible(i)) { const l = this.view.hooks.nodeLabel?.(i); if (l) out.push(l); }
    return out;
  }

  /** Fit the whole graph in view with room at the sides for the labels of the nodes at its edges. */
  fit(animate = true): void {
    const n = this.#graph.nodeCount;
    const labels: string[] = [];
    if (n <= 200) for (let i = 0; i < this.#graph.nodes.length && labels.length < 200; i++) { const l = this.view.hooks.nodeLabel?.(i); if (l) labels.push(l); }
    this.view.fit(animate, 40, labelPadding(labels));
  }

  // ---- scene construction ----
  /** Every per-slot array at the size of the graph: called first by everything that reads or writes them, so a slot a patch brought is never past an end. */
  private ensureSized(): void {
    const g = this.#graph, n = g.nodes.length;
    if (this.vis.length < n) { const v = new Uint8Array(Math.max(n, this.vis.length * 2)); v.set(this.vis); this.vis = v; }
    this.nodeFlags.ensure(n); this.nodeExtra.ensure(n); this.edgeFlags.ensure(g.edges.length);
    this.scene.ensureNodes(n);
  }

  /** The extra bits of a node in the scene: the layers, and what the graph says (an aggregate of a collapsed group, a reference stub). The only place they are put together. */
  private extraOf(slot: number): number {
    const n = this.#graph.nodes[slot];
    return (this.nodeExtra.bits[slot] ?? 0) | (this.groupCounts.has(slot) ? Ex.Aggregate : 0) | (this.#graph.isStub(slot) ? Ex.Reference : 0) | (n && this.blame.nodes.has(n.id) ? Ex.Blame : 0);
  }

  private computeVisibility(): void {
    const g = this.#graph, n = g.nodes.length;
    this.ensureSized();
    const vis = this.vis;
    const f = this.shown.filter, depth = this.shown.focusDepth;
    const focusSet = depth > 0 && this.#selection?.kind === "entity" && g.nodeSlot.has(this.#selection.id)
      ? neighbourhood(g, g.nodeSlot.get(this.#selection.id)!, depth) : null;
    // a tile leaves out the nodes no edge reaches when the graph has others: a ring of unconnected dots says nothing at that size
    const thin = this.model.thinIsolated;
    // with support left out, an element that only supports others (a signal, evidence) is left out too; what it supports counts it
    const supportOnly = this.model.hiddenKinds.has("support") ? this.supportOnlyNodes() : null;
    this.supportCounts = new Map();
    if (supportOnly) for (const supported of supportOnly.values()) for (const t of supported) this.supportCounts.set(t, (this.supportCounts.get(t) ?? 0) + 1);
    for (let i = 0; i < n; i++) {
      const e = g.nodes[i];
      let v = e ? 1 : 0;
      if (e) {
        if (f.hiddenTypes.has(e.type)) v = 0;
        else if (f.prop && !entityMatchesProp(e, f.prop)) v = 0;
        else if (focusSet && !focusSet.has(i)) v = 0;
        else if (thin && !g.isStub(i) && g.out[i].length + g.inn[i].length === 0) v = 0;
        else if (supportOnly?.has(i)) v = 0;
      }
      vis[i] = v;
    }
    if (this.#grouping && this.#collapsed.size) {
      // groups removed by deletion are dropped
      for (const c of [...this.#collapsed]) if (!g.nodes[c]) this.#collapsed.delete(c);
      this.anchor = computeAnchors(g, this.#grouping, this.#collapsed);
      this.groupCounts = hiddenCounts(this.anchor, this.#collapsed);
      for (let i = 0; i < n; i++) if (vis[i] && this.anchor[i] !== i) vis[i] = 0;
    } else { this.anchor = null; this.groupCounts = new Map(); }
  }

  /** Rebuild every instance from the graph. Used on load and when filters, grouping or theme change. */
  refreshAll(): void {
    const g = this.#graph, s = this.scene;
    this.kindCounts = new Map();
    for (const r of g.relationships()) this.countKind(r.type, 1);
    this.model = this.modelNow();
    this.restated = restatingTypes(this.info);
    this.blame = blamePath(g, this.info, this.restated);
    this.computeVisibility();
    for (let i = 0; i < g.nodes.length; i++) this.writeNode(i);
    s.nodeCount = Math.max(s.nodeCount, g.nodes.length);
    this.writeEdges(0, g.edges.length);
    this.edgeFlags.ensure(g.edges.length);
    this.updateMatches();
    this.updateHot();
    this.view.requestRender();
    this.ev.changed();
  }

  private writeNode(slot: number): void {
    const s = this.scene, n = this.#graph.nodes[slot];
    if (!n || !this.vis[slot]) { s.hideNode(slot); if (slot >= s.nodeCount && n === undefined) return; return; }
    const st = this.styleOf(n.type);
    const stub = this.#graph.isStub(slot);
    const color = stub ? parseColor("#8a94a3") : this.coloring.node && !this.coloring.node.none ? parseColor(this.entry("node", n.props)?.color ?? "#66758a") : st.color;
    const deg = this.#graph.out[slot].length + this.#graph.inn[slot].length;
    const group = this.groupCounts.has(slot);
    const r = st.radius * (group ? 1.35 : 1);
    s.setNode(slot, st.shape, r, r, color, this.nodeFlags.bits[slot] ?? 0, this.extraOf(slot));
    s.priority[slot] = (stub ? 0 : st.radius * 40) + Math.log2(1 + deg) * 12 + (group ? 500 : 0);
  }

  /** The nodes whose every edge is a support edge, with the nodes they support. */
  private supportOnlyNodes(): Map<number, Set<number>> {
    const g = this.#graph, out = new Map<number, Set<number>>();
    for (let i = 0; i < g.nodes.length; i++) {
      if (!g.nodes[i]) continue;
      const edges = [...g.out[i], ...g.inn[i]];
      if (!edges.length || !edges.every((e) => g.edges[e] && this.kindOf(g.edges[e]!.type) === "support")) continue;
      out.set(i, new Set(edges.map((e) => (g.edgeSrc[e] === i ? g.edgeDst[e] : g.edgeSrc[e])).filter((x) => x >= 0)));
    }
    return out;
  }
  /** per supported node: how many elements that only support it are left out (shown as a count on its label) */
  private supportCounts = new Map<number, number>();

  /** What an edge of a type means, as the ontology declares it; no kind when it declares none. */
  private kindOf(type: string): EdgeKind | undefined { return this.info.edgeKinds?.[type]; }

  /** The kinds that have at least one edge now. */
  presentKinds(): Set<EdgeKind> { return new Set([...this.kindCounts].filter(([, n]) => n > 0).map(([k]) => k)); }
  /** edges per kind: counted on a full pass and moved by each step, so the kinds hidden by default follow a patch without a pass over the edges */
  private kindCounts = new Map<EdgeKind, number>();
  private countKind(type: string | undefined, by: number): void { const k = type === undefined ? undefined : this.kindOf(type); if (k) this.kindCounts.set(k, (this.kindCounts.get(k) ?? 0) + by); }
  /** The kinds drawn without: the person's choice, or the default of the place. */
  hiddenKinds(): ReadonlySet<EdgeKind> { return this.modelNow().hiddenKinds; }
  /** The pane model of the view and the graph as they are now (see `paneModel`). */
  private modelNow(): PaneModel {
    const g = this.#graph;
    return paneModel(this.shown, { presentKinds: this.presentKinds(), tileThins: this.shown.tile && tileThinsIsolated(g), collapsed: !!this.#grouping && this.#collapsed.size > 0, treeState: !!this.info.tree && Object.keys(this.info.tree.state).length > 0, autoLayout: this.autoLayoutKind() });
  }
  /** relationship types that restate others (see `restatingTypes`), as of the last full pass, and whether the person asked to see them */
  private restated: ReadonlySet<string> = new Set();
  /** the parts to blame and the way to them from the top (see `blamePath`), as of the last full pass */
  private blame: ReturnType<typeof blamePath> = { blamed: new Set(), nodes: new Set(), edges: new Set() };
  restatedTypes(): ReadonlySet<string> { return this.restated; }
  /**
   * Show the graph as the person chose: the one way to change a filter, the focus, the edge kinds, the restated types, the layout or whether the pane
   * is a tile (a tile of the graph space draws fewer kinds than the view of the graph until the person chooses). Only what the change reaches is redone.
   */
  /** What a person set in the filter panel: the parts in `changed` are applied, the rest of `f` is what they stay with. */
  applyFilter(f: FilterState, changed: Partial<FilterState>): void {
    const has = (k: keyof FilterState) => k in changed;
    this.setView({
      ...(has("search") || has("hiddenTypes") || has("propKey") || has("propOp") || has("propValue")
        ? { filter: { hiddenTypes: new Set(f.hiddenTypes), prop: f.propKey ? { key: f.propKey, op: f.propOp, value: f.propValue } : null, search: f.search } } : {}),
      ...(has("focusDepth") ? { focusDepth: f.focusDepth } : {}),
      ...(has("hiddenKinds") ? { hiddenKinds: f.hiddenKinds } : {}),
      ...(has("showRestated") ? { showRestated: f.showRestated } : {}),
      ...(has("focusDim") ? { focusDim: f.focusDim } : {}),
      ...(has("layout") ? { layout: f.layout } : {}),
    });
    if (has("grouping")) {
      const g = resolveGrouping(this.info, f.grouping);
      // a pane that already groups this way keeps what the user collapsed or expanded
      if ((g?.type ?? null) !== (this.#grouping?.type ?? null)) {
        this.setGrouping(g?.type ?? null, g?.childIsSource);
        // the supporting elements are collapsed onto what they support, so the elements with a state are what one sees first
        if (g && f.grouping === SUPPORT_GROUPING) { this.collapseAllAtDepth(); this.runLayout(this.#layoutKind, true); }
      }
    }
    if (has("colorNodes")) this.setColoring("node", resolveColoring(this.info, "node", f.colorNodes));
    if (has("colorEdges")) this.setColoring("edge", resolveColoring(this.info, "edge", f.colorEdges));
  }

  setView(change: Partial<PaneView>): void {
    const was = this.shown, next = { ...was, ...change };
    const supportWasHidden = this.model.hiddenKinds.has("support");
    this.shown = next;
    const same = <K extends keyof PaneView>(k: K) => JSON.stringify(was[k]) === JSON.stringify(next[k]);
    const sameTypes = was.filter.hiddenTypes.size === next.filter.hiddenTypes.size && [...next.filter.hiddenTypes].every((t) => was.filter.hiddenTypes.has(t));
    const visible = sameTypes && JSON.stringify(was.filter.prop) === JSON.stringify(next.filter.prop) && same("focusDepth") && same("hiddenKinds") && same("showRestated") && same("tile");
    if (!visible) this.refreshAll();
    else if (was.filter.search !== next.filter.search) { this.updateMatches(); this.view.requestRender(); }
    if (!same("focusDim")) this.setSelection(this.#selection);
    const supportToggled = supportWasHidden !== this.model.hiddenKinds.has("support") && (this.kindCounts.get("support") ?? 0) > 0;
    if (supportToggled || !same("layout")) {
      const want = this.modelNow().layout;
      if (supportToggled || want !== this.#layoutKind) this.runLayout(want, true);
    }
  }

  private writeEdges(lo: number, hi: number): void {
    const g = this.#graph, s = this.scene;
    const hidden = this.model.hiddenKinds;
    const seen = this.anchor ? new Set<string>() : null;
    for (let e = lo; e < hi; e++) {
      const r = g.edges[e];
      const a = g.edgeSrc[e], b = g.edgeDst[e];
      const kind = this.kindOf(r?.type ?? "");
      if (!r || a < 0 || b < 0 || (kind && hidden.has(kind)) || (!this.shown.showRestated && this.restated.has(r.type))) { s.hideEdge(e); continue; }
      let ea = a, eb = b, agg = false;
      if (this.anchor) { ea = this.anchor[a]; eb = this.anchor[b]; agg = ea !== a || eb !== b; }
      if (!this.vis[ea] || !this.vis[eb] || ea === eb) { s.hideEdge(e); continue; }
      if (agg && seen) { const key = `${ea}>${eb}>${r.type}`; if (seen.has(key)) { s.hideEdge(e); continue; } seen.add(key); }
      const ent = this.coloring.edge && !this.coloring.edge.none ? this.entry("edge", r.props) : undefined;
      s.setEdge(e, ea, eb, ent ? parseColor(ent.color) : kind ? parseColor(KIND_EDGE_COLOR[kind]) : edgeColorForType(r.type, this.theme), agg || ent?.role === "danger" || g.isStub(g.edgeSrc[e]) || g.isStub(g.edgeDst[e]) ? EdgeStyle.Dashed : EdgeStyle.Solid, this.edgeFlags.bits[e] ?? 0, (agg ? Ex.Aggregate : 0) | (this.blame.edges.has(r.id) ? Ex.Blame : 0), kind ? EdgeKindCode[kind] : 0);
    }
    if (hi > s.edgeCount) s.edgeCount = hi;
  }

  private entry(target: Target, props: Record<string, GraphValue>): LegendEntry | undefined {
    const st = this.coloring[target];
    if (!st) return undefined;
    const text = colorValueOf(st.c, props)?.text;
    const e = entryOf(st.legend, text);
    // a value the legend has not seen (a patch brought it): colored as other for now, the legend is rebuilt after the step
    if (text !== undefined && !st.legend.some((l) => l.value === text)) this.legendStale = true;
    return e;
  }
  /** " · value" and a role glyph: the cues that do not depend on color. Empty when the element type has no such coloring. */
  private valueMark(target: Target, props: Record<string, GraphValue>): string {
    const st = this.coloring[target];
    const v = st && colorValueOf(st.c, props);
    if (!v) return "";
    // the glyph of the role says the state (the color repeats it); the name of the property and its value on every label would bury the names.
    // The value is in the Inspector and the element list
    if (v.role && v.role !== "neutral") return ` ${ROLE_GLYPH[v.role]}`;
    return ` · ${v.text}${v.role ? ` ${ROLE_GLYPH[v.role]}` : ""}`;
  }

  /** Color nodes or edges by the value of a property (null to go back to type colors); with a hint the values map to roles. */
  setColoring(target: Target, c: Coloring | null): void {
    if (!c) { this.coloring[target] = null; this.refreshAll(); return; }
    const { counts, missing, hint } = tally(c, target === "node" ? this.#graph.entities() : this.#graph.relationships());
    this.coloring[target] = { c, legend: legendOf(counts, missing, hint, roleColors(this.theme.name)), none: counts.size === 0 };
    this.legendStale = false;
    this.refreshAll();
  }
  /** Current legend of a target (entries with their counts), for panels that show it. */
  legendOfTarget(target: Target): LegendEntry[] { return this.coloring[target]?.legend ?? []; }
  private recolor(): void { for (const t of ["node", "edge"] as Target[]) { const st = this.coloring[t]; if (st) this.setColoring(t, st.c); } }

  // ---- patches ----
  /** Update the scene after the history applied a step. */
  applyStep(step: StepResult): void { this.applySteps([step]); }
  /**
   * Update the scene after the history applied steps (a seek over many revisions is one batch). Each step writes the elements it touched; what
   * depends on the whole graph (the full refresh when filters or groups are active, the legend, search matches, the layout decision) runs once.
   */
  applySteps(steps: StepResult[]): void {
    if (!steps.length) return;
    const g = this.#graph, s = this.scene;
    this.ensureSized();
    // drawn element by element only when the last full pass needed no pass over the whole graph; the model after the steps decides again below
    const full = this.model.whole;
    let structural = false;
    for (const step of steps) {
      const res: Extract<ApplyResult, { ok: true }> = step.result;
      for (const c of res.changes.relationships.values()) { this.countKind(c.before?.type, -1); this.countKind(c.after?.type, 1); }
      for (const [id, slot] of res.removedSlots) this.lastPos.set(id, [s.positions[2 * slot], s.positions[2 * slot + 1]]);
      const added: number[] = [];
      for (const [id, c] of res.changes.entities) {
        if (c.kind !== "added") continue;
        const slot = g.nodeSlot.get(id);
        if (slot === undefined) continue;
        const known = this.lastPos.get(id);
        if (known) { s.positions[2 * slot] = known[0]; s.positions[2 * slot + 1] = known[1]; this.lastPos.delete(id); }
        else added.push(slot);
      }
      // a stub that a patch's edges brought has no position yet; one an entity replaced keeps its place
      for (const slot of res.dirtyNodes) if (g.isStub(slot) && !added.includes(slot)) added.push(slot);
      if (added.length) {
        const neighbours = (v: number) => { const out: number[] = []; for (const es of g.out[v]) out.push(g.edgeDst[es]); for (const es of g.inn[v]) out.push(g.edgeSrc[es]); return out.filter((x) => x >= 0); };
        const b = s.bounds();
        placeIncremental(s.positions, added, neighbours, b ? [(b.minX + b.maxX) / 2, (b.minY + b.maxY) / 2] : [0, 0], 22);
      }
      this.#hub?.revalidate(res.dirtyNodes, res.dirtyEdges);
      if (!full) {
        for (const slot of res.dirtyNodes) { this.vis[slot] = g.nodes[slot] ? 1 : 0; this.writeNode(slot); }
        // degree changed for neighbours of touched edges, keep priorities current
        for (const e of res.dirtyEdges) {
          const a = g.edgeSrc[e], b = g.edgeDst[e];
          for (const v of [a, b]) if (v >= 0 && g.nodes[v]) this.writeNode(v);
        }
        for (const e of res.dirtyEdges) this.writeEdges(e, e + 1);
      }
      structural ||= [...res.changes.entities.values(), ...res.changes.relationships.values()].some((c) => c.kind !== "changed");
    }
    s.positionsDirty = true; s.positionsVersion++;
    // the selection is held by id: a slot it flagged may now hold another element
    if (this.#selection && structural) this.flagSelection();
    const now = this.modelNow();
    if (full || !sameDrawing(this.model, now)) this.refreshAll();
    else { s.nodeCount = Math.max(s.nodeCount, g.nodes.length); if (this.#selection && structural) this.updateHot(); }
    if (this.legendStale) this.recolor();
    this.updateMatches();
    this.view.requestRender();
    // the layout of a revision is a function of that revision: when the graph now reads best in another layout (a pane made on an empty revision,
    // a scrub that brought the tree), or a layered graph changed its nodes or edges, it is laid out again
    const want = now.layout;
    if (want !== this.#layoutKind || (want === "layered" && structural && g.nodeCount <= 5000)) this.runLayout(want, want !== this.#layoutKind);
  }

  /** Highlight the elements of a change set (added, changed on the graph; removed as ghosts). */
  setHighlight(changes: ChangeSet | null): void {
    this.#highlight = changes;
    const g = this.#graph;
    const nA: number[] = [], nC: number[] = [], eA: number[] = [], eC: number[] = [], eR: number[] = [];
    this.ghostList = [];
    if (changes) {
      for (const [id, c] of changes.entities) {
        const slot = g.nodeSlot.get(id);
        if (c.kind === "added" && slot !== undefined) nA.push(slot);
        else if (c.kind === "changed" && slot !== undefined) nC.push(slot);
        else if (c.kind === "removed" && c.before) {
          const p = this.lastPos.get(id) ?? this.guessPosition(c.before, changes);
          if (p) this.ghostList.push({ x: p[0], y: p[1], r: this.styleOf(c.before.type).radius, color: this.cssColor("change"), label: this.#graph.label(c.before), shape: 1 });
        }
      }
      for (const [id, c] of changes.relationships) {
        const slot = g.edgeSlot.get(id);
        if (c.kind === "added" && slot !== undefined) eA.push(slot);
        else if (c.kind === "changed" && slot !== undefined) eC.push(slot);
        else if (c.kind === "removed" && c.before) {
          const a = this.posOfEntity(c.before.source), b = this.posOfEntity(c.before.target);
          if (a && b) this.ghostList.push({ x: a[0], y: a[1], r: 0, color: this.cssColor("change"), label: c.before.type, shape: 0, edge: { x2: b[0], y2: b[1] } });
        }
      }
    }
    this.flushNodeLayer(St.Added, nA); this.flushNodeLayer(St.Changed, nC); this.flushNodeLayer(St.Removed, []);
    this.flushEdgeLayer(St.Added, eA); this.flushEdgeLayer(St.Changed, eC); this.flushEdgeLayer(St.Removed, eR);
    this.view.changedNodes = [...nA, ...nC];
    this.view.requestRender();
  }

  private cssColor(kind: "change"): string {
    const c = this.theme[kind];
    return `rgb(${Math.round(c[0] * 255)}, ${Math.round(c[1] * 255)}, ${Math.round(c[2] * 255)})`;
  }
  private posOfEntity(id: string): [number, number] | null {
    const slot = this.#graph.nodeSlot.get(id);
    if (slot !== undefined && Number.isFinite(this.scene.positions[2 * slot])) return [this.scene.positions[2 * slot], this.scene.positions[2 * slot + 1]];
    return this.lastPos.get(id) ?? null;
  }
  private guessPosition(e: Entity, changes: ChangeSet): [number, number] | null {
    let sx = 0, sy = 0, c = 0;
    for (const r of changes.relationships.values()) {
      const rel = r.before ?? r.after;
      if (!rel) continue;
      const other = rel.source === e.id ? rel.target : rel.target === e.id ? rel.source : null;
      if (!other) continue;
      const p = this.posOfEntity(other);
      if (p) { sx += p[0]; sy += p[1]; c++; }
    }
    if (c) return [sx / c, sy / c];
    const b = this.scene.bounds();
    return b ? [(b.minX + b.maxX) / 2, (b.minY + b.maxY) / 2] : null;
  }

  // ---- flags ----
  private flushNodeLayer(bit: number, slots: number[]): void {
    this.ensureSized();
    const changed = this.nodeFlags.set(bit, slots);
    this.writeFlags(changed);
  }
  private flushExtraLayer(bit: number, slots: number[]): void {
    this.ensureSized();
    this.writeFlags(this.nodeExtra.set(bit, slots));
  }
  private flushEdgeLayer(bit: number, slots: number[]): void {
    this.ensureSized();
    for (const e of this.edgeFlags.set(bit, slots)) if (this.scene.edgeVisible(e)) this.scene.setEdgeState(e, this.edgeFlags.bits[e]);
  }
  private writeFlags(slots: number[]): void {
    const s = this.scene;
    for (const slot of slots) {
      if (!s.nodeVisible(slot)) continue;
      s.setNodeMeta(slot, s.nodeShape(slot), this.nodeFlags.bits[slot] ?? 0, this.extraOf(slot));
    }
  }

  // ---- selection, hover, search ----
  setSelection(sel: Selection | null): void {
    this.#selection = sel && sel.graph === this.info.id ? sel : null;
    this.flagSelection();
    if (this.shown.focusDepth > 0) this.refreshAll(); else this.updateHot();
  }

  /** The flags of the selection and what it relates to, found by id. */
  private flagSelection(): void {
    const g = this.#graph;
    const nodeSel: number[] = [], related: number[] = [], edgeSel: number[] = [], edgeRel: number[] = [];
    if (this.#selection?.kind === "entity") {
      const slot = g.nodeSlot.get(this.#selection.id);
      if (slot !== undefined) {
        nodeSel.push(slot);
        for (const es of g.out[slot]) { edgeRel.push(es); if (g.edgeDst[es] >= 0) related.push(g.edgeDst[es]); }
        for (const es of g.inn[slot]) { edgeRel.push(es); if (g.edgeSrc[es] >= 0) related.push(g.edgeSrc[es]); }
      }
    } else if (this.#selection?.kind === "relationship") {
      const es = g.edgeSlot.get(this.#selection.id);
      if (es !== undefined) { edgeSel.push(es); if (g.edgeSrc[es] >= 0) related.push(g.edgeSrc[es]); if (g.edgeDst[es] >= 0) related.push(g.edgeDst[es]); }
    }
    this.flushNodeLayer(St.Selected, nodeSel);
    this.flushNodeLayer(St.Related, related.filter((x) => !nodeSel.includes(x)).slice(0, 4000));
    this.flushEdgeLayer(St.Selected, edgeSel);
    this.flushEdgeLayer(St.Related, edgeRel.slice(0, 6000));
  }

  private onHover(item: Pick | null): void {
    const g = this.#graph;
    let sel: Selection | null = null;
    if (item?.kind === "node") { const n = g.nodes[item.slot]; if (n) sel = { graph: this.info.id, kind: "entity", id: n.id }; }
    else if (item?.kind === "edge") { const r = g.edges[item.slot]; if (r) sel = { graph: this.info.id, kind: "relationship", id: r.id }; }
    if (sel?.id === this.#hoverSel?.id && sel?.kind === this.#hoverSel?.kind) return;
    this.#hoverSel = sel;
    this.flushNodeLayer(St.Hover, item?.kind === "node" ? [item.slot] : []);
    this.flushEdgeLayer(St.Hover, item?.kind === "edge" ? [item.slot] : []);
    this.updateHot();
    const p = this.view.screenToWorld(0, 0);
    void p;
    this.ev.hover(sel, 0, 0);
  }

  private onPick(item: Pick | null, additive: boolean): void {
    const g = this.#graph;
    if (!item) { this.ev.select(null, additive); return; }
    if (item.kind === "node") { const n = g.nodes[item.slot]; if (n) this.ev.select({ graph: this.info.id, kind: "entity", id: n.id }, additive); }
    else { const r = g.edges[item.slot]; if (r) this.ev.select({ graph: this.info.id, kind: "relationship", id: r.id }, additive); }
  }

  private onActivate(item: Pick): void {
    const g = this.#graph;
    if (item.kind === "node") {
      if (this.groupCounts.has(item.slot) || (this.#grouping && this.hasChildren(item.slot))) { this.toggleCollapse(item.slot); return; }
      const n = g.nodes[item.slot];
      if (n) this.ev.activate({ graph: this.info.id, kind: "entity", id: n.id });
    }
  }

  private hasChildren(slot: number): boolean {
    if (!this.#grouping) return false;
    const list = this.#grouping.childIsSource ? this.#graph.inn[slot] : this.#graph.out[slot];
    return list.some((es) => this.#graph.edges[es]?.type === this.#grouping!.type);
  }

  private setGrouping(type: string | null, childIsSource?: boolean): void {
    this.#collapsed.clear();
    this.#grouping = type ? detectGrouping(this.#graph, type, childIsSource) : null;
    this.refreshAll();
    this.relayoutIfLayered();
  }

  private toggleCollapse(slot: number): void {
    if (!this.#grouping || !this.hasChildren(slot)) return;
    if (this.#collapsed.has(slot)) this.#collapsed.delete(slot); else this.#collapsed.add(slot);
    this.refreshAll();
    this.relayoutIfLayered();
    this.ev.announce(`${this.#collapsed.has(slot) ? "Collapsed" : "Expanded"} ${this.#graph.label(this.#graph.nodes[slot]!)}`);
  }

  collapseAllAtDepth(): void {
    if (!this.#grouping) return;
    this.#collapsed.clear();
    for (let i = 0; i < this.#graph.nodes.length; i++) if (this.#graph.nodes[i] && this.hasChildren(i)) {
      // collapse groups whose members are all leaves (the lowest grouping level)
      const kids = (this.#grouping.childIsSource ? this.#graph.inn[i] : this.#graph.out[i]).filter((es) => this.#graph.edges[es]?.type === this.#grouping!.type);
      const leafy = kids.every((es) => { const c = this.#grouping!.childIsSource ? this.#graph.edgeSrc[es] : this.#graph.edgeDst[es]; return c >= 0 && !this.hasChildren(c); });
      if (leafy) this.#collapsed.add(i);
    }
    this.refreshAll();
    this.relayoutIfLayered();
  }

  expandAll(): void { this.#collapsed.clear(); this.refreshAll(); this.relayoutIfLayered(); }

  private updateMatches(): void {
    const q = this.shown.filter.search.trim();
    this.matches = new Set<number>();
    if (q) {
      const g = this.#graph;
      for (let i = 0; i < g.nodes.length; i++) { const e = g.nodes[i]; if (e && this.vis[i] && entityMatchesSearch(e, q)) this.matches.add(i); }
    }
    this.flushNodeLayer(St.Match, [...this.matches].slice(0, 5000));
    this.updateHot();
  }

  get matchCount(): number { return this.matches.size; }
  matchSlots(): number[] { return [...this.matches].sort((a, b) => this.scene.priority[b] - this.scene.priority[a] || a - b); }

  /**
   * Cross-graph highlighting: `above` are the elements in this graph that the selection is part of (and the same entity elsewhere),
   * `below` the ones it stands for. Nodes get a ring in the selection color (solid for above, dashed for below); edges of either are drawn in it.
   */
  setCross(above: { nodes: ReadonlySet<string>; edges: ReadonlySet<string> }, below: { nodes: ReadonlySet<string>; edges: ReadonlySet<string> }): void {
    const g = this.#graph;
    const idSlots: number[] = [], srcSlots: number[] = [], edgeSlots: number[] = [];
    for (const id of above.nodes) { const s = g.nodeSlot.get(id); if (s !== undefined) idSlots.push(s); }
    for (const id of below.nodes) { const s = g.nodeSlot.get(id); if (s !== undefined) srcSlots.push(s); }
    for (const id of [...above.edges, ...below.edges]) { const s = g.edgeSlot.get(id); if (s !== undefined) edgeSlots.push(s); }
    this.flushNodeLayer(St.Identity, idSlots);
    this.flushExtraLayer(Ex.Source, srcSlots);
    this.flushEdgeLayer(St.Identity, edgeSlots);
    this.updateHot();
  }

  private updateHot(): void {
    const hotNodes = new Set<number>();
    for (const bit of [St.Selected, St.Related, St.Hover, St.Match, St.Identity]) for (const s of this.nodeFlags.members_(bit)) hotNodes.add(s);
    for (const s of this.nodeExtra.members_(Ex.Source)) hotNodes.add(s);
    const hotEdges = new Set<number>();
    for (const bit of [St.Selected, St.Related, St.Hover, St.Identity]) for (const e of this.edgeFlags.members_(bit)) hotEdges.add(e);
    const nodes = [...hotNodes].filter((s) => this.scene.nodeVisible(s)).slice(0, 6000);
    const edges = [...hotEdges].filter((e) => this.scene.edgeVisible(e)).slice(0, 8000);
    this.view.hotNodes = [...[...this.blame.nodes].map((id) => this.#graph.nodeSlot.get(id)).filter((x): x is number => x !== undefined), ...this.nodeFlags.members_(St.Selected), ...this.nodeFlags.members_(St.Hover), ...this.nodeFlags.members_(St.Identity), ...this.nodeExtra.members_(Ex.Source), ...this.nodeFlags.members_(St.Related)].filter((s) => this.scene.nodeVisible(s)).slice(0, 200);
    this.view.hotEdges = [...this.edgeFlags.members_(St.Selected), ...this.edgeFlags.members_(St.Hover), ...this.edgeFlags.members_(St.Identity)].filter((e) => this.scene.edgeVisible(e));
    this.view.setHot(nodes, edges);
    const focused = this.nodeFlags.members_(St.Selected).size + this.edgeFlags.members_(St.Selected).size > 0 || this.matches.size > 0 || this.nodeFlags.members_(St.Identity).size > 0 || this.nodeExtra.members_(Ex.Source).size > 0 || this.edgeFlags.members_(St.Identity).size > 0;
    this.view.dim = focused && this.shown.focusDim ? 0.2 : 1;
  }

  focusSelection(): void {
    if (this.#selection?.kind === "entity") { const s = this.#graph.nodeSlot.get(this.#selection.id); if (s !== undefined) this.view.flyToSlot(s); }
  }
  focusSlot(slot: number): void { this.view.flyToSlot(slot); }

  // ---- keyboard ----
  private onKey(ev: KeyboardEvent): boolean {
    if (this.ev.key(ev)) return true;
    const g = this.#graph;
    if (ev.altKey || ev.ctrlKey || ev.metaKey) return false;
    const dirs: Record<string, [number, number]> = { ArrowUp: [0, -1], ArrowDown: [0, 1], ArrowLeft: [-1, 0], ArrowRight: [1, 0] };
    if (ev.key in dirs) {
      const [dx, dy] = dirs[ev.key];
      const cur = this.#selection?.kind === "entity" ? g.nodeSlot.get(this.#selection.id) : undefined;
      const P = this.scene.positions;
      let target = -1;
      if (cur === undefined) {
        const cam = this.view.camera;
        let best = Infinity;
        const vp = this.view.viewport;
        for (const i of this.view.nodesInRect(cam.x - vp.w / 2 / cam.zoom, cam.y - vp.h / 2 / cam.zoom, cam.x + vp.w / 2 / cam.zoom, cam.y + vp.h / 2 / cam.zoom)) {
          const d = Math.hypot(P[2 * i] - cam.x, P[2 * i + 1] - cam.y);
          if (d < best) { best = d; target = i; }
        }
      } else {
        const candidates = new Set<number>();
        for (const es of g.out[cur]) if (g.edgeDst[es] >= 0) candidates.add(g.edgeDst[es]);
        for (const es of g.inn[cur]) if (g.edgeSrc[es] >= 0) candidates.add(g.edgeSrc[es]);
        const pick = (set: Iterable<number>): number => {
          let best = -Infinity, found = -1;
          for (const i of set) {
            if (i === cur || !this.scene.nodeVisible(i)) continue;
            const vx = P[2 * i] - P[2 * cur], vy = P[2 * i + 1] - P[2 * cur + 1];
            const len = Math.hypot(vx, vy) || 1;
            const align = (vx * dx + vy * dy) / len;
            if (align < 0.35) continue;
            const score = align * 2 - len / 2000;
            if (score > best) { best = score; found = i; }
          }
          return found;
        };
        target = pick(candidates);
        if (target < 0) {
          const cam = this.view.camera, r = 200 / cam.zoom;
          target = pick(this.view.nodesInRect(P[2 * cur] - r, P[2 * cur + 1] - r, P[2 * cur] + r, P[2 * cur + 1] + r));
        }
      }
      if (target >= 0) {
        const n = g.nodes[target]!;
        this.ev.select({ graph: this.info.id, kind: "entity", id: n.id }, false);
        const [sx, sy] = this.view.worldToScreen(P[2 * target], P[2 * target + 1]);
        const vp = this.view.viewport;
        if (sx < 40 || sy < 40 || sx > vp.w - 40 || sy > vp.h - 40) this.view.flyToSlot(target, this.view.camera.zoom);
        this.ev.announce(`${n.type} ${this.#graph.label(n)}, ${g.out[target].length + g.inn[target].length} connections`);
      }
      return true;
    }
    if (ev.key === "Enter" && this.#selection?.kind === "entity") { this.setView({ focusDepth: this.shown.focusDepth ? 0 : 1 }); this.ev.announce(this.shown.focusDepth ? "Neighbourhood focus on" : "Neighbourhood focus off"); return true; }
    if (ev.key === " " && this.#selection?.kind === "entity") { const s = g.nodeSlot.get(this.#selection.id); if (s !== undefined) this.toggleCollapse(s); return true; }
    if (ev.key === "Escape") { this.shown = { ...this.shown, focusDepth: 0 }; this.ev.select(null, false); return true; }
    return false;
  }

  // ---- stats for UI ----
  relationshipTypes(): string[] { const s = new Set<string>(); for (const r of this.#graph.relationships()) s.add(r.type); return [...s].sort(); }
  colorCss(type: string): string { return this.styleOf(type).hue; }
  shapeName(type: string): string { return this.styleOf(type).shapeName; }

  dispose(): void {
    this.disposed = true;
    this.hubOff?.();
    this.#layoutClient.dispose();
    this.view.dispose();
  }
}
