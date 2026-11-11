// DataPane: one data graph on the canvas engine. Binds Graph (data) to Scene (render arrays), layout, selection and highlights.
import type { ApplyResult } from "../core/graph";
import { Graph } from "../core/graph";
import { type ChangeSet, type ElementKey, type Entity, type Rel } from "../core/model";
import { nameGraph } from "../core/naming";
import type { GraphValue } from "../core/wire-types";
import { placeIncremental } from "../layout/force";
import { LayoutClient } from "../layout/client";
import { Ex, EdgeStyle, Scene, St, parseColor } from "../render/scene";
import { type Theme, edgeColorForType, styleForType, type TypeStyle } from "../render/theme";
import { GraphView, type Ghost, type Pick } from "../render/view";
import {
  FlagLayer, type Filter, type Grouping, computeAnchors, detectGrouping, emptyFilter, entityMatchesProp, entityMatchesSearch, hiddenCounts, labelPadding, neighbourhood,
} from "./graph-view-model";
import type { History, StepResult } from "./history";
import { hasTreeRows } from "./tree-model";
import type { GraphInfo } from "../source/types";
import { EDGE_CHANNELS, NODE_CHANNELS, type TelemetryHub } from "./telemetry-hub";
import { ROLE_GLYPH, colorValueOf, entryOf, legendOf, tally, type Coloring, type LegendEntry, type Target } from "./color-by";
import { roleColors } from "./roles";

export type Selection = { graph: string; kind: "entity" | "relationship"; id: string };
export type LayoutKind = "force" | "layered";

export interface PaneEvents {
  select(sel: Selection | null, additive: boolean): void;
  hover(sel: Selection | null, x: number, y: number): void;
  activate(sel: Selection): void;
  changed(): void;
  announce(text: string): void;
  key(ev: KeyboardEvent): boolean;
}

export class DataPane {
  readonly scene = new Scene();
  readonly view: GraphView;
  readonly layoutClient = new LayoutClient();
  graph: Graph;
  filter: Filter = emptyFilter();
  layoutKind: LayoutKind = "force";
  layoutDirection: "down" | "right" = "down";
  layoutRunning = false;
  layoutProgress = 0;
  lastLayoutMs = 0;
  focusDepth = 0;                  // 0 = off
  grouping: Grouping | null = null;
  collapsed = new Set<number>();
  focusDim = true;
  /** what colors nodes and edges (null: the type colors) and the legend each was computed with */
  private coloring: Record<Target, { c: Coloring; legend: LegendEntry[] } | null> = { node: null, edge: null };
  /** a value appeared that the legend does not know: recompute it after the step */
  private legendStale = false;
  selection: Selection | null = null;
  hoverSel: Selection | null = null;
  highlight: ChangeSet | null = null;

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

  hub: TelemetryHub | null = null;
  /** smoothed cost of copying frames into the GPU channels, ms */
  telemetryApplyMs = 0;
  private hubOff: (() => void) | null = null;

  constructor(readonly info: GraphInfo, private readonly history: History, host: HTMLElement, private theme: Theme, private readonly ev: PaneEvents, hub: TelemetryHub | null = null) {
    this.graph = history.graph;
    nameGraph(this.graph, info.ontology);
    // derived trees and forests read best as layers; general graphs use the force layout
    const g0 = history.graph;
    if (g0.nodeCount > 2 && g0.edgeCount >= g0.nodeCount * 0.5 && g0.edgeCount <= g0.nodeCount) {
      // a tree or forest: most nodes take part in an edge; many isolated nodes would make a flat strip
      let connected = 0;
      for (let i = 0; i < g0.nodes.length; i++) if (g0.nodes[i] && g0.out[i].length + g0.inn[i].length > 0) connected++;
      if (connected >= g0.nodeCount * 0.8) this.layoutKind = "layered";
    }
    // a graph whose ontology declares a hierarchy is a tree: the root on top, whatever its size
    if (info.tree && hasTreeRows(g0, info.tree)) this.layoutKind = "layered";
    this.view = new GraphView(host);
    this.view.setTheme(theme);
    this.view.setScene(this.scene);
    this.view.hooks = {
      nodeLabel: (slot) => { const n = this.graph.nodes[slot]; if (!n) return null; const g = this.groupCounts.get(slot); let l = this.graph.label(n) + this.valueMark("node", n.props); return g ? `${l} (+${g})` : l; },
      nodeGlyph: (slot) => { const st = this.scene.nodeState(slot); return st & St.Added ? "+" : st & St.Changed ? "Δ" : null; },
      edgeLabel: (slot) => { const r = this.graph.edges[slot]; return r ? r.type + this.valueMark("edge", r.props) : null; },
      ghosts: () => this.ghostList,
      onHover: (item) => this.onHover(item),
      onSelect: (item, additive) => this.onPick(item, additive),
      onActivate: (item) => this.onActivate(item),
      onKey: (ev) => this.onKey(ev),
    };
    if (hub) this.attachTelemetry(hub);
    host.setAttribute("role", "application");
    host.setAttribute("aria-label", `Graph ${info.id}. Arrow keys move the selection along edges. Alt plus arrows pan. Plus and minus zoom. F fits the view.`);
  }

  // ---- telemetry ----
  attachTelemetry(hub: TelemetryHub): void {
    this.hubOff?.();
    this.hub = hub;
    hub.revalidateAll();
    this.hubOff = hub.onChange(() => this.applyTelemetry());
    this.applyTelemetry();
  }

  /** Copy the frames of the chosen metrics into the GPU channels. No layout work and no instance rebuild. */
  applyTelemetry(): void {
    const t0 = performance.now();
    const hub = this.hub, sc = this.scene;
    if (!hub || !hub.hasData) { sc.telemetry.on = false; return; }
    sc.telemetry.on = hub.enabled;
    sc.telemetry.flow = hub.flow;
    sc.telemetry.warnNode = hub.warnByteOf("node");
    sc.telemetry.warnEdge = hub.warnByteOf("edge");
    if (hub.enabled) {
      for (let c = 0; c < NODE_CHANNELS.length; c++) sc.setChannel("node", c, hub.frame("node", c));
      for (let c = 0; c < EDGE_CHANNELS.length; c++) sc.setChannel("edge", c, hub.frame("edge", c));
    }
    this.view.requestRender();
    this.telemetryApplyMs += (performance.now() - t0 - this.telemetryApplyMs) * 0.1;
    this.ev.changed();
  }

  // ---- styling ----
  private styleOf(type: string): TypeStyle {
    let s = this.styles.get(type);
    if (!s) { s = styleForType(type, this.theme); this.styles.set(type, s); }
    return s;
  }
  typeStyles(): Map<string, TypeStyle> { return this.styles; }

  setTheme(t: Theme): void { this.theme = t; this.styles.clear(); this.view.setTheme(t); this.refreshAll(); this.recolor(); }

  // ---- loading and layout ----
  start(): void {
    this.refreshAll();
    // a large tree is unreadable leaf by leaf: group by its single relationship type and collapse the lowest level
    const types = this.relationshipTypes();
    if (this.layoutKind === "layered" && this.graph.nodeCount > 300 && types.length === 1) {
      this.grouping = detectGrouping(this.graph, types[0]);
      this.collapseAllAtDepth();
      return;
    }
    this.runLayout(this.layoutKind, true);
  }

  private relayoutIfLayered(): void { if (this.layoutKind === "layered") this.runLayout("layered", true); }

  runLayout(kind: LayoutKind = this.layoutKind, fitWhenDone = false): void {
    this.layoutKind = kind;
    const g = this.graph, sc = this.scene;
    // lay out what is visible: collapsed groups and filtered nodes do not take space
    const slots: number[] = [];
    const dense = new Int32Array(g.nodes.length).fill(-1);
    for (let i = 0; i < g.nodes.length; i++) if (g.nodes[i] && (sc.nodeVisible(i) || i >= sc.nodeCount)) { dense[i] = slots.length; slots.push(i); }
    const n = slots.length;
    this.layoutSlots = Int32Array.from(slots);
    const src = new Int32Array(g.edges.length).fill(-1), dst = new Int32Array(g.edges.length).fill(-1);
    for (let e = 0; e < g.edges.length; e++) {
      if (!g.edges[e] || !sc.edgeVisible(e)) continue;
      const [a, b] = sc.edgeEnds(e);
      if (a < 0 || b < 0 || dense[a] < 0 || dense[b] < 0) continue;
      src[e] = dense[a]; dst[e] = dense[b];
    }
    // a declared hierarchy ranks the layers: parent above child, what explains a row below it; dependencies between rows do not rank
    const tree = kind === "layered" ? this.info.tree : undefined;
    if (tree) {
      for (let e = 0; e < g.edges.length; e++) {
        const r = g.edges[e];
        if (src[e] < 0 || !r) continue;
        if (r.type === tree.hierarchy.relationship) { if (tree.hierarchy.childIsSource) [src[e], dst[e]] = [dst[e], src[e]]; }
        else if (tree.edgeRoles[r.type] === "support") { if (!tree.types || !tree.types.includes(g.nodes[g.edgeSrc[e]]?.type ?? "")) [src[e], dst[e]] = [dst[e], src[e]]; }
        else { src[e] = -1; dst[e] = -1; }
      }
    }
    this.layoutRunning = true; this.layoutProgress = 0;
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
      const job = this.layoutClient.run({ kind: "layered", n, src, dst, width: w, height: h, options: { direction: this.layoutDirection, layerGap: 70, nodeGap: 14 } });
      job.done.then((p) => { if (this.disposed) return; scatter(p); this.layoutRunning = false; this.lastLayoutMs = this.layoutClient.lastMs; this.fit(!first); this.ev.changed(); });
    } else {
      // keep existing positions as the starting point so a re-run refines instead of scrambling
      const start = new Float32Array(n * 2).fill(NaN);
      if (!first) for (let i = 0; i < n; i++) { const s = slots[i]; if (Number.isFinite(this.scene.positions[2 * s])) { start[2 * i] = this.scene.positions[2 * s]; start[2 * i + 1] = this.scene.positions[2 * s + 1]; } }
      const job = this.layoutClient.run({ kind: "force", n, src, dst, positions: first ? undefined : start, options: { length: 30 } }, (p, it, total) => {
        this.layoutProgress = it / total; scatter(p);
        if (first && it === 0) this.fit(false);
        this.ev.changed();
      });
      job.done.then((p) => { if (this.disposed) return; scatter(p); this.layoutRunning = false; this.layoutProgress = 1; this.lastLayoutMs = this.layoutClient.lastMs; if (first || n < 100) this.fit(!first); this.ev.changed(); });
    }
  }

  /** What the canvas can write next to the nodes of the scene: the labels of the visible ones. */
  labelsShown(): string[] {
    const out: string[] = [];
    for (let i = 0; i < this.graph.nodes.length; i++) if (this.scene.nodeVisible(i)) { const l = this.view.hooks.nodeLabel?.(i); if (l) out.push(l); }
    return out;
  }

  /** Fit the whole graph in view with room at the sides for the labels of the nodes at its edges. */
  fit(animate = true): void {
    const n = this.graph.nodeCount;
    const labels: string[] = [];
    if (n <= 200) for (let i = 0; i < this.graph.nodes.length && labels.length < 200; i++) { const l = this.view.hooks.nodeLabel?.(i); if (l) labels.push(l); }
    this.view.fit(animate, 40, labelPadding(labels));
  }

  // ---- scene construction ----
  private computeVisibility(): void {
    const g = this.graph, n = g.nodes.length;
    if (this.vis.length < n) this.vis = new Uint8Array(Math.max(n, this.vis.length * 2));
    const vis = this.vis;
    const f = this.filter;
    const focusSet = this.focusDepth > 0 && this.selection?.kind === "entity" && g.nodeSlot.has(this.selection.id)
      ? neighbourhood(g, g.nodeSlot.get(this.selection.id)!, this.focusDepth) : null;
    for (let i = 0; i < n; i++) {
      const e = g.nodes[i];
      let v = e ? 1 : 0;
      if (e) {
        if (f.hiddenTypes.has(e.type)) v = 0;
        else if (f.prop && !entityMatchesProp(e, f.prop)) v = 0;
        else if (focusSet && !focusSet.has(i)) v = 0;
      }
      vis[i] = v;
    }
    if (this.grouping && this.collapsed.size) {
      // groups removed by deletion are dropped
      for (const c of [...this.collapsed]) if (!g.nodes[c]) this.collapsed.delete(c);
      this.anchor = computeAnchors(g, this.grouping, this.collapsed);
      this.groupCounts = hiddenCounts(this.anchor, this.collapsed);
      for (let i = 0; i < n; i++) if (vis[i] && this.anchor[i] !== i) vis[i] = 0;
    } else { this.anchor = null; this.groupCounts = new Map(); }
  }

  /** Rebuild every instance from the graph. Used on load and when filters, grouping or theme change. */
  refreshAll(): void {
    const g = this.graph, s = this.scene;
    this.computeVisibility();
    s.ensureNodes(g.nodes.length);
    this.nodeFlags.ensure(g.nodes.length); this.nodeExtra.ensure(g.nodes.length);
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
    const s = this.scene, n = this.graph.nodes[slot];
    if (!n || !this.vis[slot]) { s.hideNode(slot); if (slot >= s.nodeCount && n === undefined) return; return; }
    const st = this.styleOf(n.type);
    const stub = this.graph.isStub(slot);
    const color = stub ? parseColor("#8a94a3") : this.coloring.node ? parseColor(this.entry("node", n.props)?.color ?? "#66758a") : st.color;
    const deg = this.graph.out[slot].length + this.graph.inn[slot].length;
    const group = this.groupCounts.has(slot);
    const r = st.radius * (group ? 1.35 : 1);
    s.setNode(slot, st.shape, r, r, color, this.nodeFlags.bits[slot] ?? 0, (this.nodeExtra.bits[slot] ?? 0) | (group ? Ex.Aggregate : 0) | (stub ? Ex.Reference : 0));
    s.priority[slot] = (stub ? 0 : st.radius * 40) + Math.log2(1 + deg) * 12 + (group ? 500 : 0);
  }

  private writeEdges(lo: number, hi: number): void {
    const g = this.graph, s = this.scene;
    const seen = this.anchor ? new Set<string>() : null;
    for (let e = lo; e < hi; e++) {
      const r = g.edges[e];
      const a = g.edgeSrc[e], b = g.edgeDst[e];
      if (!r || a < 0 || b < 0) { s.hideEdge(e); continue; }
      let ea = a, eb = b, agg = false;
      if (this.anchor) { ea = this.anchor[a]; eb = this.anchor[b]; agg = ea !== a || eb !== b; }
      if (!this.vis[ea] || !this.vis[eb] || ea === eb) { s.hideEdge(e); continue; }
      if (agg && seen) { const key = `${ea}>${eb}>${r.type}`; if (seen.has(key)) { s.hideEdge(e); continue; } seen.add(key); }
      const ent = this.coloring.edge ? this.entry("edge", r.props) : undefined;
      s.setEdge(e, ea, eb, ent ? parseColor(ent.color) : edgeColorForType(r.type, this.theme), agg || ent?.role === "danger" || g.isStub(g.edgeSrc[e]) || g.isStub(g.edgeDst[e]) ? EdgeStyle.Dashed : EdgeStyle.Solid, this.edgeFlags.bits[e] ?? 0, agg ? Ex.Aggregate : 0);
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
    return ` · ${v.text}${v.role ? ` ${ROLE_GLYPH[v.role]}` : ""}`;
  }

  /** Color nodes or edges by the value of a property (null to go back to type colors); with a hint the values map to roles. */
  setColoring(target: Target, c: Coloring | null): void {
    if (!c) { this.coloring[target] = null; this.refreshAll(); return; }
    const { counts, missing, hint } = tally(c, target === "node" ? this.graph.entities() : this.graph.relationships());
    this.coloring[target] = { c, legend: legendOf(counts, missing, hint, roleColors(this.theme.name)) };
    this.legendStale = false;
    this.refreshAll();
  }
  /** Current legend of a target (entries with their counts), for panels that show it. */
  legendOfTarget(target: Target): LegendEntry[] { return this.coloring[target]?.legend ?? []; }
  private recolor(): void { for (const t of ["node", "edge"] as Target[]) { const st = this.coloring[t]; if (st) this.setColoring(t, st.c); } }

  // ---- patches ----
  /** Update the scene after the history applied a step. Falls back to a full refresh when filters or groups are active. */
  applyStep(step: StepResult): void {
    const res: Extract<ApplyResult, { ok: true }> = step.result;
    const g = this.graph, s = this.scene;
    s.ensureNodes(g.nodes.length);
    this.nodeFlags.ensure(g.nodes.length); this.nodeExtra.ensure(g.nodes.length); this.edgeFlags.ensure(g.edges.length);
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
    this.hub?.revalidate(res.dirtyNodes, res.dirtyEdges);
    s.positionsDirty = true; s.positionsVersion++;
    if (this.filter.hiddenTypes.size || this.filter.prop || this.grouping && this.collapsed.size || this.focusDepth) this.refreshAll();
    else {
      for (const slot of res.dirtyNodes) { this.vis[slot] = g.nodes[slot] ? 1 : 0; this.writeNode(slot); }
      // degree changed for neighbours of touched edges, keep priorities current
      for (const e of res.dirtyEdges) {
        const a = g.edgeSrc[e], b = g.edgeDst[e];
        for (const v of [a, b]) if (v >= 0 && g.nodes[v]) this.writeNode(v);
      }
      for (const e of res.dirtyEdges) this.writeEdges(e, e + 1);
      s.nodeCount = Math.max(s.nodeCount, g.nodes.length);
    }
    if (this.legendStale) this.recolor();
    this.updateMatches();
    this.view.requestRender();
  }

  /** Highlight the elements of a change set (added, changed on the graph; removed as ghosts). */
  setHighlight(changes: ChangeSet | null): void {
    this.highlight = changes;
    const g = this.graph;
    const nA: number[] = [], nC: number[] = [], eA: number[] = [], eC: number[] = [], eR: number[] = [];
    this.ghostList = [];
    if (changes) {
      for (const [id, c] of changes.entities) {
        const slot = g.nodeSlot.get(id);
        if (c.kind === "added" && slot !== undefined) nA.push(slot);
        else if (c.kind === "changed" && slot !== undefined) nC.push(slot);
        else if (c.kind === "removed" && c.before) {
          const p = this.lastPos.get(id) ?? this.guessPosition(c.before, changes);
          if (p) this.ghostList.push({ x: p[0], y: p[1], r: this.styleOf(c.before.type).radius, color: this.cssColor("removed"), label: this.graph.label(c.before), shape: 1 });
        }
      }
      for (const [id, c] of changes.relationships) {
        const slot = g.edgeSlot.get(id);
        if (c.kind === "added" && slot !== undefined) eA.push(slot);
        else if (c.kind === "changed" && slot !== undefined) eC.push(slot);
        else if (c.kind === "removed" && c.before) {
          const a = this.posOfEntity(c.before.source), b = this.posOfEntity(c.before.target);
          if (a && b) this.ghostList.push({ x: a[0], y: a[1], r: 0, color: this.cssColor("removed"), label: c.before.type, shape: 0, edge: { x2: b[0], y2: b[1] } });
        }
      }
    }
    this.flushNodeLayer(St.Added, nA); this.flushNodeLayer(St.Changed, nC); this.flushNodeLayer(St.Removed, []);
    this.flushEdgeLayer(St.Added, eA); this.flushEdgeLayer(St.Changed, eC); this.flushEdgeLayer(St.Removed, eR);
    this.view.changedNodes = [...nA, ...nC];
    this.view.requestRender();
  }

  private cssColor(kind: "removed"): string {
    const c = this.theme.removed;
    void kind;
    return `rgb(${Math.round(c[0] * 255)}, ${Math.round(c[1] * 255)}, ${Math.round(c[2] * 255)})`;
  }
  private posOfEntity(id: string): [number, number] | null {
    const slot = this.graph.nodeSlot.get(id);
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
    this.nodeFlags.ensure(this.graph.nodes.length);
    const changed = this.nodeFlags.set(bit, slots);
    this.writeFlags(changed);
  }
  private flushExtraLayer(bit: number, slots: number[]): void {
    this.nodeExtra.ensure(this.graph.nodes.length);
    this.writeFlags(this.nodeExtra.set(bit, slots));
  }
  private flushEdgeLayer(bit: number, slots: number[]): void {
    this.edgeFlags.ensure(this.graph.edges.length);
    for (const e of this.edgeFlags.set(bit, slots)) if (this.scene.edgeVisible(e)) this.scene.setEdgeState(e, this.edgeFlags.bits[e]);
  }
  private writeFlags(slots: number[]): void {
    const s = this.scene;
    for (const slot of slots) {
      if (!s.nodeVisible(slot)) continue;
      s.setNodeMeta(slot, s.nodeShape(slot), this.nodeFlags.bits[slot] ?? 0, (this.nodeExtra.bits[slot] ?? 0) | (this.groupCounts.has(slot) ? Ex.Aggregate : 0));
    }
  }

  // ---- selection, hover, search ----
  setSelection(sel: Selection | null): void {
    this.selection = sel && sel.graph === this.info.id ? sel : null;
    const g = this.graph;
    const nodeSel: number[] = [], related: number[] = [], edgeSel: number[] = [], edgeRel: number[] = [];
    if (this.selection?.kind === "entity") {
      const slot = g.nodeSlot.get(this.selection.id);
      if (slot !== undefined) {
        nodeSel.push(slot);
        for (const es of g.out[slot]) { edgeRel.push(es); if (g.edgeDst[es] >= 0) related.push(g.edgeDst[es]); }
        for (const es of g.inn[slot]) { edgeRel.push(es); if (g.edgeSrc[es] >= 0) related.push(g.edgeSrc[es]); }
      }
    } else if (this.selection?.kind === "relationship") {
      const es = g.edgeSlot.get(this.selection.id);
      if (es !== undefined) { edgeSel.push(es); if (g.edgeSrc[es] >= 0) related.push(g.edgeSrc[es]); if (g.edgeDst[es] >= 0) related.push(g.edgeDst[es]); }
    }
    this.flushNodeLayer(St.Selected, nodeSel);
    this.flushNodeLayer(St.Related, related.filter((x) => !nodeSel.includes(x)).slice(0, 4000));
    this.flushEdgeLayer(St.Selected, edgeSel);
    this.flushEdgeLayer(St.Related, edgeRel.slice(0, 6000));
    if (this.focusDepth > 0) this.refreshAll(); else this.updateHot();
  }

  private onHover(item: Pick | null): void {
    const g = this.graph;
    let sel: Selection | null = null;
    if (item?.kind === "node") { const n = g.nodes[item.slot]; if (n) sel = { graph: this.info.id, kind: "entity", id: n.id }; }
    else if (item?.kind === "edge") { const r = g.edges[item.slot]; if (r) sel = { graph: this.info.id, kind: "relationship", id: r.id }; }
    if (sel?.id === this.hoverSel?.id && sel?.kind === this.hoverSel?.kind) return;
    this.hoverSel = sel;
    this.flushNodeLayer(St.Hover, item?.kind === "node" ? [item.slot] : []);
    this.flushEdgeLayer(St.Hover, item?.kind === "edge" ? [item.slot] : []);
    this.updateHot();
    const p = this.view.screenToWorld(0, 0);
    void p;
    this.ev.hover(sel, 0, 0);
  }

  private onPick(item: Pick | null, additive: boolean): void {
    const g = this.graph;
    if (!item) { this.ev.select(null, additive); return; }
    if (item.kind === "node") { const n = g.nodes[item.slot]; if (n) this.ev.select({ graph: this.info.id, kind: "entity", id: n.id }, additive); }
    else { const r = g.edges[item.slot]; if (r) this.ev.select({ graph: this.info.id, kind: "relationship", id: r.id }, additive); }
  }

  private onActivate(item: Pick): void {
    const g = this.graph;
    if (item.kind === "node") {
      if (this.groupCounts.has(item.slot) || (this.grouping && this.hasChildren(item.slot))) { this.toggleCollapse(item.slot); return; }
      const n = g.nodes[item.slot];
      if (n) this.ev.activate({ graph: this.info.id, kind: "entity", id: n.id });
    }
  }

  private hasChildren(slot: number): boolean {
    if (!this.grouping) return false;
    const list = this.grouping.childIsSource ? this.graph.inn[slot] : this.graph.out[slot];
    return list.some((es) => this.graph.edges[es]?.type === this.grouping!.type);
  }

  setGrouping(type: string | null, childIsSource?: boolean): void {
    this.collapsed.clear();
    this.grouping = type ? detectGrouping(this.graph, type, childIsSource) : null;
    this.refreshAll();
    this.relayoutIfLayered();
  }

  toggleCollapse(slot: number): void {
    if (!this.grouping || !this.hasChildren(slot)) return;
    if (this.collapsed.has(slot)) this.collapsed.delete(slot); else this.collapsed.add(slot);
    this.refreshAll();
    this.relayoutIfLayered();
    this.ev.announce(`${this.collapsed.has(slot) ? "Collapsed" : "Expanded"} ${this.graph.label(this.graph.nodes[slot]!)}`);
  }

  collapseAllAtDepth(): void {
    if (!this.grouping) return;
    this.collapsed.clear();
    for (let i = 0; i < this.graph.nodes.length; i++) if (this.graph.nodes[i] && this.hasChildren(i)) {
      // collapse groups whose members are all leaves (the lowest grouping level)
      const kids = (this.grouping.childIsSource ? this.graph.inn[i] : this.graph.out[i]).filter((es) => this.graph.edges[es]?.type === this.grouping!.type);
      const leafy = kids.every((es) => { const c = this.grouping!.childIsSource ? this.graph.edgeSrc[es] : this.graph.edgeDst[es]; return c >= 0 && !this.hasChildren(c); });
      if (leafy) this.collapsed.add(i);
    }
    this.refreshAll();
    this.relayoutIfLayered();
  }

  expandAll(): void { this.collapsed.clear(); this.refreshAll(); this.relayoutIfLayered(); }

  setFilter(f: Filter): void {
    this.filter = f;
    this.refreshAll();
  }

  private updateMatches(): void {
    const q = this.filter.search.trim();
    this.matches = new Set<number>();
    if (q) {
      const g = this.graph;
      for (let i = 0; i < g.nodes.length; i++) { const e = g.nodes[i]; if (e && this.vis[i] && entityMatchesSearch(e, q)) this.matches.add(i); }
    }
    this.flushNodeLayer(St.Match, [...this.matches].slice(0, 5000));
    this.updateHot();
  }

  get matchCount(): number { return this.matches.size; }
  matchSlots(): number[] { return [...this.matches].sort((a, b) => this.scene.priority[b] - this.scene.priority[a] || a - b); }

  /**
   * Cross-graph highlighting: `above` are the elements in this graph that the selection is part of (and the same entity elsewhere),
   * `below` the ones it stands for. Nodes get a ring (pink for above, dashed for below); edges of either are drawn in the identity color.
   */
  setCross(above: { nodes: ReadonlySet<string>; edges: ReadonlySet<string> }, below: { nodes: ReadonlySet<string>; edges: ReadonlySet<string> }): void {
    const g = this.graph;
    const idSlots: number[] = [], srcSlots: number[] = [], edgeSlots: number[] = [];
    for (const id of above.nodes) { const s = g.nodeSlot.get(id); if (s !== undefined) idSlots.push(s); }
    for (const id of below.nodes) { const s = g.nodeSlot.get(id); if (s !== undefined) srcSlots.push(s); }
    for (const id of [...above.edges, ...below.edges]) { const s = g.edgeSlot.get(id); if (s !== undefined) edgeSlots.push(s); }
    this.flushNodeLayer(St.Identity, idSlots);
    this.flushExtraLayer(Ex.Source, srcSlots);
    this.flushEdgeLayer(St.Identity, edgeSlots);
    this.updateHot();
  }
  crossCount(): number { return this.nodeFlags.members_(St.Identity).size + this.nodeExtra.members_(Ex.Source).size + this.edgeFlags.members_(St.Identity).size; }

  private updateHot(): void {
    const hotNodes = new Set<number>();
    for (const bit of [St.Selected, St.Related, St.Hover, St.Match, St.Identity]) for (const s of this.nodeFlags.members_(bit)) hotNodes.add(s);
    for (const s of this.nodeExtra.members_(Ex.Source)) hotNodes.add(s);
    const hotEdges = new Set<number>();
    for (const bit of [St.Selected, St.Related, St.Hover, St.Identity]) for (const e of this.edgeFlags.members_(bit)) hotEdges.add(e);
    const nodes = [...hotNodes].filter((s) => this.scene.nodeVisible(s)).slice(0, 6000);
    const edges = [...hotEdges].filter((e) => this.scene.edgeVisible(e)).slice(0, 8000);
    this.view.hotNodes = [...this.nodeFlags.members_(St.Selected), ...this.nodeFlags.members_(St.Hover), ...this.nodeFlags.members_(St.Identity), ...this.nodeExtra.members_(Ex.Source), ...this.nodeFlags.members_(St.Related)].filter((s) => this.scene.nodeVisible(s)).slice(0, 200);
    this.view.hotEdges = [...this.edgeFlags.members_(St.Selected), ...this.edgeFlags.members_(St.Hover), ...this.edgeFlags.members_(St.Identity)].filter((e) => this.scene.edgeVisible(e));
    this.view.setHot(nodes, edges);
    const focused = this.nodeFlags.members_(St.Selected).size + this.edgeFlags.members_(St.Selected).size > 0 || this.matches.size > 0 || this.nodeFlags.members_(St.Identity).size > 0 || this.nodeExtra.members_(Ex.Source).size > 0 || this.edgeFlags.members_(St.Identity).size > 0;
    this.view.dim = focused && this.focusDim ? 0.2 : 1;
  }

  focusSelection(): void {
    if (this.selection?.kind === "entity") { const s = this.graph.nodeSlot.get(this.selection.id); if (s !== undefined) this.view.flyToSlot(s); }
  }
  focusSlot(slot: number): void { this.view.flyToSlot(slot); }

  // ---- keyboard ----
  private onKey(ev: KeyboardEvent): boolean {
    if (this.ev.key(ev)) return true;
    const g = this.graph;
    if (ev.altKey || ev.ctrlKey || ev.metaKey) return false;
    const dirs: Record<string, [number, number]> = { ArrowUp: [0, -1], ArrowDown: [0, 1], ArrowLeft: [-1, 0], ArrowRight: [1, 0] };
    if (ev.key in dirs) {
      const [dx, dy] = dirs[ev.key];
      const cur = this.selection?.kind === "entity" ? g.nodeSlot.get(this.selection.id) : undefined;
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
        this.ev.announce(`${n.type} ${this.graph.label(n)}, ${g.out[target].length + g.inn[target].length} connections`);
      }
      return true;
    }
    if (ev.key === "Enter" && this.selection?.kind === "entity") { this.focusDepth = this.focusDepth ? 0 : 1; this.refreshAll(); this.ev.announce(this.focusDepth ? "Neighbourhood focus on" : "Neighbourhood focus off"); return true; }
    if (ev.key === " " && this.selection?.kind === "entity") { const s = g.nodeSlot.get(this.selection.id); if (s !== undefined) this.toggleCollapse(s); return true; }
    if (ev.key === "Escape") { this.focusDepth = 0; this.ev.select(null, false); return true; }
    return false;
  }

  // ---- stats for UI ----
  typeCounts(): Map<string, number> {
    const m = new Map<string, number>();
    for (const e of this.graph.entities()) m.set(e.type, (m.get(e.type) ?? 0) + 1);
    return m;
  }
  relationshipTypes(): string[] { const s = new Set<string>(); for (const r of this.graph.relationships()) s.add(r.type); return [...s].sort(); }
  elementKey(sel: Selection): ElementKey { return (sel.kind === "entity" ? "n:" : "e:") + sel.id; }
  entityOf(id: string): Entity | undefined { return this.graph.entity(id); }
  relOf(id: string): Rel | undefined { return this.graph.rel(id); }
  hasEntity(id: string): boolean { return this.graph.nodeSlot.has(id); }
  colorCss(type: string): string { return this.styleOf(type).hue; }
  shapeName(type: string): string { return this.styleOf(type).shapeName; }
  parse = parseColor;

  dispose(): void {
    this.disposed = true;
    this.hubOff?.();
    this.layoutClient.dispose();
    this.view.dispose();
  }
}
