// GraphView: canvas pair (WebGL + 2D label layer), camera, pointer and keyboard input, picking, level of detail.
import { GraphRenderer, type Camera, type RenderParams } from "./renderer";
import { Ex, Scene, Shape, St, WORDS_EDGE } from "./scene";
import { SpatialGrid, type PointSet } from "./spatial";
import type { Theme } from "./theme";

export type Pick = { kind: "node" | "edge"; slot: number };
export interface Ghost { x: number; y: number; r: number; color: string; label: string; shape: number; edge?: { x2: number; y2: number } }

export interface ViewHooks {
  nodeLabel?(slot: number): string | null;
  nodeGlyph?(slot: number): string | null;
  edgeLabel?(slot: number): string | null;
  drawCard?(ctx: CanvasRenderingContext2D, slot: number, cx: number, cy: number, hw: number, hh: number, theme: Theme): void;
  ghosts?(): Ghost[];
  onHover?(item: Pick | null): void;
  onSelect?(item: Pick | null, additive: boolean): void;
  onActivate?(item: Pick): void;
  onKey?(ev: KeyboardEvent): boolean;
  onCamera?(): void;
  /** Claim a drag that starts on an item (node editor: move a card, draw a wire). Return true to take it over from panning. */
  dragStart?(item: Pick, wx: number, wy: number): boolean;
  dragMove?(wx: number, wy: number, over: Pick | null): void;
  dragEnd?(wx: number, wy: number, over: Pick | null): void;
  /** Draw extra decorations (rubber band wire) above the labels. */
  overlayExtra?(ctx: CanvasRenderingContext2D, toScreen: (x: number, y: number) => [number, number]): void;
}

export interface ViewStats { frames: number; lastFrameMs: number; cpuMs: number; labelMs: number; lastInputLatencyMs: number; visibleLabels: number }

export class GraphView {
  readonly canvas = document.createElement("canvas");
  readonly overlay = document.createElement("canvas");
  readonly renderer: GraphRenderer;
  scene = new Scene();
  camera: Camera = { x: 0, y: 0, zoom: 1 };
  hooks: ViewHooks = {};
  theme!: Theme;
  /** show wires as bezier curves with ports (projection editor) */
  wires = false;
  dim = 1;
  /** label every visible edge with its type (small graphs such as ontologies) */
  allEdgeLabels = false;
  stats: ViewStats = { frames: 0, lastFrameMs: 0, cpuMs: 0, labelMs: 0, lastInputLatencyMs: 0, visibleLabels: 0 };
  readonly frameTimes: number[] = [];

  private ctx: CanvasRenderingContext2D;
  private width = 1; private height = 1; private dpr = 1;
  private raf = 0;
  private grid: SpatialGrid | null = null;
  private gridVersion = -1; private gridPosVersion = -1; private gridBuiltAt = 0;
  private labelOrder = new Int32Array(0); private labelOrderVersion = -1; private labelOrderAt = 0;
  private avgEdgeLen = 30;
  private fitZoom = 1;
  private pendingFit = false;
  private edgeSkip = 1;
  /** true while moving pulses are on screen; the view then redraws every frame */
  flowActive = false;
  /** estimated edges on screen, used to decide whether pulses are drawn */
  flowEstimate = 0;
  private learnedSkip = 1;
  private lastRaf = 0;
  private fastFrames = 0;
  private lastMove = 0;
  private idleTimer: ReturnType<typeof setTimeout> | null = null;
  private pointer: { x: number; y: number } | null = null;
  private drag: { id: number; sx: number; sy: number; cx: number; cy: number; moved: boolean; hook: boolean } | null = null;
  private touches = new Map<number, { x: number; y: number }>();
  private pinch: { dist: number; zoom: number } | null = null;
  private anim: { t0: number; dur: number; from: Camera; to: Camera } | null = null;
  private pendingInput = 0;
  private resizeObs: ResizeObserver;
  private measure = document.createElement("canvas").getContext("2d")!;
  private widthCache = new Map<string, number>();

  constructor(readonly host: HTMLElement) {
    host.classList.add("graph-view");
    this.canvas.className = "gl"; this.overlay.className = "ov";
    this.overlay.setAttribute("aria-hidden", "true");
    host.append(this.canvas, this.overlay);
    host.tabIndex = 0;
    this.renderer = new GraphRenderer(this.canvas);
    this.ctx = this.overlay.getContext("2d")!;
    this.resizeObs = new ResizeObserver(() => {
      this.resize();
      if (this.pendingFit) { this.pendingFit = false; this.fit(false); }
      this.requestRender();
    });
    this.resizeObs.observe(host);
    this.resize();
    host.addEventListener("pointerdown", this.onDown);
    host.addEventListener("pointermove", this.onMove);
    host.addEventListener("pointerup", this.onUp);
    host.addEventListener("pointercancel", this.onUp);
    host.addEventListener("pointerleave", this.onLeave);
    host.addEventListener("wheel", this.onWheel, { passive: false });
    host.addEventListener("dblclick", this.onDbl);
    host.addEventListener("keydown", this.onKeyDown);
  }

  setScene(scene: Scene): void {
    this.scene = scene;
    this.renderer.setScene(scene);
    this.grid = null;
    this.labelOrderVersion = -1;
    this.requestRender();
  }

  setTheme(t: Theme): void { this.theme = t; this.requestRender(); }
  setHot(nodes: number[], edges: number[]): void { this.renderer.setHot(nodes, edges); this.requestRender(); }

  private resize(): void {
    const r = this.host.getBoundingClientRect();
    this.width = Math.max(1, Math.round(r.width)); this.height = Math.max(1, Math.round(r.height));
    this.dpr = Math.min(window.devicePixelRatio || 1, 2);
    this.overlay.width = Math.round(this.width * this.dpr); this.overlay.height = Math.round(this.height * this.dpr);
  }

  // ---- camera ----
  worldToScreen(x: number, y: number): [number, number] {
    return [(x - this.camera.x) * this.camera.zoom + this.width / 2, (y - this.camera.y) * this.camera.zoom + this.height / 2];
  }
  screenToWorld(sx: number, sy: number): [number, number] {
    return [(sx - this.width / 2) / this.camera.zoom + this.camera.x, (sy - this.height / 2) / this.camera.zoom + this.camera.y];
  }

  fit(animate = true, padding = 40, padX = padding): void {
    this.resize();
    const b = this.scene.bounds();
    if (!b) return;
    if (this.host.clientWidth < 20 || this.host.clientHeight < 20) { this.pendingFit = true; return; }
    const w = Math.max(b.maxX - b.minX, 1), h = Math.max(b.maxY - b.minY, 1);
    const sides = Math.min(padX, this.width * 0.18);
    const zoom = Math.max(Math.min((this.width - sides * 2) / w, (this.height - padding * 2) / h, 4), 1e-4);
    this.fitZoom = zoom;
    this.goTo({ x: (b.minX + b.maxX) / 2, y: (b.minY + b.maxY) / 2, zoom }, animate);
    this.computeEdgeLength();
  }

  goTo(to: Camera, animate = true): void {
    if (!animate || window.matchMedia("(prefers-reduced-motion: reduce)").matches) { this.camera = { ...to }; this.anim = null; this.cameraChanged(); return; }
    this.anim = { t0: performance.now(), dur: 320, from: { ...this.camera }, to };
    this.requestRender();
  }

  flyToSlot(slot: number, zoom?: number): void {
    const x = this.scene.positions[2 * slot], y = this.scene.positions[2 * slot + 1];
    if (!Number.isFinite(x)) return;
    this.goTo({ x, y, zoom: zoom ?? Math.max(this.camera.zoom, this.fitZoom * 6) });
  }

  zoomBy(factor: number, sx = this.width / 2, sy = this.height / 2): void {
    const [wx, wy] = this.screenToWorld(sx, sy);
    const z = Math.min(Math.max(this.camera.zoom * factor, this.fitZoom * 0.2), 60);
    this.camera = { zoom: z, x: wx - (sx - this.width / 2) / z, y: wy - (sy - this.height / 2) / z };
    this.anim = null;
    this.cameraChanged();
  }

  panBy(dxPx: number, dyPx: number): void {
    this.camera = { ...this.camera, x: this.camera.x - dxPx / this.camera.zoom, y: this.camera.y - dyPx / this.camera.zoom };
    this.anim = null;
    this.cameraChanged();
  }

  private cameraChanged(): void { this.lastMove = performance.now(); this.hooks.onCamera?.(); this.requestRender(); }

  private computeEdgeLength(): void {
    const s = this.scene;
    let sum = 0, n = 0;
    const step = Math.max(1, Math.floor(s.edgeCount / 2000));
    for (let e = 0; e < s.edgeCount; e += step) {
      if (!s.edgeVisible(e)) continue;
      const [a, b] = s.edgeEnds(e);
      if (a < 0 || b < 0) continue;
      const d = Math.hypot(s.positions[2 * a] - s.positions[2 * b], s.positions[2 * a + 1] - s.positions[2 * b + 1]);
      if (Number.isFinite(d)) { sum += d; n++; }
    }
    this.avgEdgeLen = n ? sum / n : 30;
  }

  // ---- picking ----
  private pointSet(): PointSet {
    const s = this.scene;
    return { count: s.nodeCount, positions: s.positions, radius: s.radius, visible: (i) => s.nodeVisible(i) };
  }

  private ensureGrid(): SpatialGrid {
    const s = this.scene;
    const now = performance.now();
    const stale = !this.grid || this.gridVersion !== s.version || this.gridPosVersion !== s.positionsVersion;
    // while a layout is streaming positions, rebuild at most every 200 ms
    if (stale && (!this.grid || now - this.gridBuiltAt > 200 || this.gridVersion !== s.version)) {
      this.grid = SpatialGrid.build(this.pointSet());
      this.gridVersion = s.version; this.gridPosVersion = s.positionsVersion; this.gridBuiltAt = now;
    }
    return this.grid!;
  }

  pickAt(px: number, py: number): Pick | null {
    const s = this.scene;
    const z = this.camera.zoom;
    const [wx, wy] = this.screenToWorld(px, py);
    const slack = 3 / z;
    let node = -1;
    if (s.nodeCount < 3000 || this.wires) {
      let port = -1, card = -1, round = -1, bestD = Infinity;
      for (let i = s.nodeCount - 1; i >= 0; i--) {
        if (!s.nodeVisible(i)) continue;
        const cx = s.positions[2 * i], cy = s.positions[2 * i + 1];
        if (!Number.isFinite(cx)) continue;
        const o = i * 5;
        const hw = s.nodeF32[o + 1], hh = s.nodeF32[o + 2];
        if (s.nodeExtra(i) & Ex.Port) { if (Math.hypot(wx - cx, wy - cy) <= Math.max(hw, 6 / z)) port = i; }
        else if (s.nodeShape(i) === Shape.Card) { if (card < 0 && Math.abs(wx - cx) <= hw && Math.abs(wy - cy) <= hh) card = i; }
        else {
          const d = Math.hypot(wx - cx, wy - cy);
          if (d <= Math.max(hw, 3 / z) + slack && d < bestD) { bestD = d; round = i; }
        }
      }
      node = port >= 0 ? port : round >= 0 ? round : card;
    } else {
      node = this.ensureGrid().pick(wx, wy, slack, 2.2 / z, this.pointSet());
    }
    if (node >= 0) return { kind: "node", slot: node };
    const e = this.pickEdge(wx, wy, 5 / z);
    return e >= 0 ? { kind: "edge", slot: e } : null;
  }

  private pickEdge(wx: number, wy: number, tol: number): number {
    const s = this.scene;
    const P = s.positions;
    let best = -1, bestD = tol;
    for (let e = 0; e < s.edgeCount; e++) {
      const o = e * WORDS_EDGE;
      if ((s.edgeU32[o + 3] & 255) === 0) continue;
      const a = s.edgeI32[o], b = s.edgeI32[o + 1];
      if (a < 0 || b < 0) continue;
      const ax = P[2 * a], ay = P[2 * a + 1], bx = P[2 * b], by = P[2 * b + 1];
      if (wx < Math.min(ax, bx) - tol || wx > Math.max(ax, bx) + tol || wy < Math.min(ay, by) - tol || wy > Math.max(ay, by) + tol) continue;
      let d: number;
      if (this.wires) d = bezierDistance(ax, ay, bx, by, wx, wy, this.camera.zoom);
      else {
        const vx = bx - ax, vy = by - ay;
        const l2 = vx * vx + vy * vy;
        const t = l2 > 0 ? Math.max(0, Math.min(1, ((wx - ax) * vx + (wy - ay) * vy) / l2)) : 0;
        d = Math.hypot(wx - (ax + t * vx), wy - (ay + t * vy));
      }
      if (d < bestD) { bestD = d; best = e; }
    }
    return best;
  }

  /** Visible node slots inside a world rectangle. */
  nodesInRect(x0: number, y0: number, x1: number, y1: number): number[] {
    const out: number[] = [];
    const s = this.scene;
    if (s.nodeCount < 3000) {
      for (let i = 0; i < s.nodeCount; i++) {
        if (!s.nodeVisible(i)) continue;
        const x = s.positions[2 * i], y = s.positions[2 * i + 1];
        if (x >= x0 && x <= x1 && y >= y0 && y <= y1) out.push(i);
      }
    } else this.ensureGrid().forRect(x0, y0, x1, y1, (i) => { const x = s.positions[2 * i], y = s.positions[2 * i + 1]; if (x >= x0 && x <= x1 && y >= y0 && y <= y1) out.push(i); });
    return out;
  }

  get viewport(): { w: number; h: number } { return { w: this.width, h: this.height }; }

  // ---- input ----
  private local(ev: PointerEvent | WheelEvent | MouseEvent): { x: number; y: number } {
    const r = this.host.getBoundingClientRect();
    return { x: ev.clientX - r.left, y: ev.clientY - r.top };
  }

  private onDown = (ev: PointerEvent): void => {
    this.host.focus({ preventScroll: true });
    const p = this.local(ev);
    this.touches.set(ev.pointerId, p);
    if (this.touches.size === 2) {
      const [a, b] = [...this.touches.values()];
      this.pinch = { dist: Math.hypot(a.x - b.x, a.y - b.y), zoom: this.camera.zoom };
      this.drag = null;
      return;
    }
    try { this.host.setPointerCapture(ev.pointerId); } catch { /* synthetic pointer */ }
    let hook = false;
    if (this.hooks.dragStart) {
      const item = this.pickAt(p.x, p.y);
      const [wx, wy] = this.screenToWorld(p.x, p.y);
      if (item) hook = this.hooks.dragStart(item, wx, wy);
    }
    this.drag = { id: ev.pointerId, sx: p.x, sy: p.y, cx: this.camera.x, cy: this.camera.y, moved: false, hook };
  };

  private onMove = (ev: PointerEvent): void => {
    const p = this.local(ev);
    this.pointer = p;
    this.pendingInput = ev.timeStamp || performance.now();
    if (this.touches.has(ev.pointerId)) this.touches.set(ev.pointerId, p);
    if (this.pinch && this.touches.size === 2) {
      const [a, b] = [...this.touches.values()];
      const d = Math.hypot(a.x - b.x, a.y - b.y);
      this.zoomBy((this.pinch.zoom * d) / this.pinch.dist / this.camera.zoom, (a.x + b.x) / 2, (a.y + b.y) / 2);
      return;
    }
    if (this.drag && this.drag.id === ev.pointerId) {
      const dx = p.x - this.drag.sx, dy = p.y - this.drag.sy;
      if (!this.drag.moved && Math.hypot(dx, dy) > 4) this.drag.moved = true;
      if (this.drag.moved && this.drag.hook) {
        const [wx, wy] = this.screenToWorld(p.x, p.y);
        this.hooks.dragMove?.(wx, wy, this.pickAt(p.x, p.y));
        this.requestRender();
        return;
      }
      if (this.drag.moved) {
        this.camera = { ...this.camera, x: this.drag.cx - dx / this.camera.zoom, y: this.drag.cy - dy / this.camera.zoom };
        this.anim = null;
        this.host.style.cursor = "grabbing";
        this.cameraChanged();
        return;
      }
    }
    this.hooks.onHover?.(this.pickAt(p.x, p.y));
    this.requestRender();
  };

  private onUp = (ev: PointerEvent): void => {
    this.touches.delete(ev.pointerId);
    if (this.touches.size < 2) this.pinch = null;
    const d = this.drag;
    this.drag = null;
    this.host.style.cursor = "";
    if (d?.hook && d.moved && ev.type === "pointerup") {
      const p = this.local(ev);
      const [wx, wy] = this.screenToWorld(p.x, p.y);
      this.hooks.dragEnd?.(wx, wy, this.pickAt(p.x, p.y));
      this.requestRender();
      return;
    }
    if (d && !d.moved && d.id === ev.pointerId && ev.type === "pointerup") {
      const p = this.local(ev);
      this.hooks.onSelect?.(this.pickAt(p.x, p.y), ev.shiftKey || ev.metaKey || ev.ctrlKey);
    }
  };

  private onLeave = (): void => { this.pointer = null; this.hooks.onHover?.(null); this.requestRender(); };

  private onWheel = (ev: WheelEvent): void => {
    ev.preventDefault();
    const p = this.local(ev);
    const unit = ev.deltaMode === 1 ? 16 : 1;
    const k = ev.ctrlKey ? 0.01 : 0.0022;
    this.zoomBy(Math.exp(-ev.deltaY * unit * k), p.x, p.y);
  };

  private onDbl = (ev: MouseEvent): void => {
    const p = this.local(ev);
    const item = this.pickAt(p.x, p.y);
    if (item) this.hooks.onActivate?.(item);
    else this.zoomBy(1.8, p.x, p.y);
  };

  private onKeyDown = (ev: KeyboardEvent): void => {
    if (this.hooks.onKey?.(ev)) { ev.preventDefault(); return; }
    const step = ev.shiftKey ? 240 : 80;
    switch (ev.key) {
      case "+": case "=": this.zoomBy(1.3); break;
      case "-": case "_": this.zoomBy(1 / 1.3); break;
      case "0": case "f": this.fit(); break;
      case "ArrowLeft": if (ev.altKey) this.panBy(step, 0); else return; break;
      case "ArrowRight": if (ev.altKey) this.panBy(-step, 0); else return; break;
      case "ArrowUp": if (ev.altKey) this.panBy(0, step); else return; break;
      case "ArrowDown": if (ev.altKey) this.panBy(0, -step); else return; break;
      default: return;
    }
    ev.preventDefault();
  };

  // ---- render loop ----
  requestRender(): void {
    if (this.raf) return;
    this.raf = requestAnimationFrame((t) => { this.raf = 0; this.frame(t); });
  }

  /** Adapt edge density to the measured frame interval while the camera moves; full density when idle. */
  private adaptEdgeSkip(now: number): void {
    const delta = this.lastRaf ? now - this.lastRaf : 16.7;
    this.lastRaf = now;
    const moving = performance.now() - this.lastMove < 200 || this.anim !== null || this.flowActive;
    if (this.scene.edgeCount < 30000 || this.wires) { this.edgeSkip = 1; return; }
    if (!moving) { this.edgeSkip = 1; return; }
    if (delta > 22 && delta < 400) { this.learnedSkip = Math.min(48, this.learnedSkip * 1.3 + 0.5); this.fastFrames = 0; }
    else if (delta < 18 && ++this.fastFrames > 45) { this.learnedSkip = Math.max(1, this.learnedSkip * 0.85); this.fastFrames = 0; }
    this.edgeSkip = this.learnedSkip;
    if (this.idleTimer) clearTimeout(this.idleTimer);
    this.idleTimer = setTimeout(() => { this.idleTimer = null; this.lastRaf = 0; this.requestRender(); }, 220);
  }

  private frame(now: number): void {
    const t0 = performance.now();
    this.adaptEdgeSkip(now);
    if (this.anim) {
      const a = this.anim;
      const k = Math.min(1, (now - a.t0) / a.dur);
      const e = k < 0.5 ? 2 * k * k : 1 - Math.pow(-2 * k + 2, 2) / 2;
      const lz = Math.log(a.from.zoom) + (Math.log(a.to.zoom) - Math.log(a.from.zoom)) * e;
      this.camera = { x: a.from.x + (a.to.x - a.from.x) * e, y: a.from.y + (a.to.y - a.from.y) * e, zoom: Math.exp(lz) };
      if (k >= 1) this.anim = null; else this.requestRender();
      this.hooks.onCamera?.();
    }
    const th = this.theme;
    const s = this.scene;
    const rel = this.camera.zoom / Math.max(this.fitZoom, 1e-6);
    const dense = s.edgeCount > 20000;
    const edgeAlpha = this.wires ? 0.9 : dense ? Math.min(0.75, 0.1 + 0.16 * Math.sqrt(rel)) : Math.min(0.85, 0.45 + 0.2 * Math.sqrt(rel));
    // pulses are only visible when edges are long enough on screen; otherwise skip the continuous redraw
    let flowActive = s.telemetry.on && s.telemetry.flow && this.avgEdgeLen * this.camera.zoom >= 30 && !this.wires;
    if (flowActive && s.nodeCount > 3000) {
      // estimate the edges on screen from the node grid; pulses over a very dense view are not readable and would force full redraws
      const [wx0, wy0] = this.screenToWorld(0, 0), [wx1, wy1] = this.screenToWorld(this.width, this.height);
      this.flowEstimate = this.ensureGrid().countRect(wx0, wy0, wx1, wy1) * (s.edgeCount / Math.max(1, s.nodeCount));
      flowActive = this.flowEstimate < 40000;
    }
    this.flowActive = flowActive;
    const params: RenderParams = {
      width: this.width, height: this.height, dpr: this.dpr, camera: this.camera, dim: this.dim,
      edgeAlpha, edgeWidth: dense && rel < 2 ? 1 : 1.4, arrows: this.wires ? false : this.avgEdgeLen * this.camera.zoom > 26,
      wires: this.wires, background: th.bgRgb, minNodePx: dense ? 1.4 : 2.2, edgeSkip: Math.round(this.edgeSkip),
      telemetry: { on: s.telemetry.on, warnNode: s.telemetry.warnNode, warnEdge: s.telemetry.warnEdge, flow: flowActive, time: (now / 1000) % 1000, flowColor: th.source },
      colors: { selected: th.selected, added: th.added, removed: th.removed, changed: th.changed, identity: th.identity, source: th.source },
    };
    this.renderer.render(params);
    const t1 = performance.now();
    this.drawOverlay();
    const t2 = performance.now();
    this.stats.frames++;
    this.stats.cpuMs = this.renderer.lastCpuMs;
    this.stats.labelMs = t2 - t1;
    this.stats.lastFrameMs = t2 - t0;
    if (this.pendingInput) { this.stats.lastInputLatencyMs = t2 - this.pendingInput; this.pendingInput = 0; }
    this.frameTimes.push(t2 - t0);
    if (this.frameTimes.length > 600) this.frameTimes.shift();
    if (flowActive) this.requestRender();
  }

  // ---- labels and decorations ----
  private textWidth(text: string): number {
    let w = this.widthCache.get(text);
    if (w === undefined) {
      this.measure.font = "12px system-ui, sans-serif";
      w = this.measure.measureText(text).width;
      if (this.widthCache.size > 5000) this.widthCache.clear();
      this.widthCache.set(text, w);
    }
    return w;
  }

  private ensureLabelOrder(): void {
    const s = this.scene;
    const now = performance.now();
    if (this.labelOrderVersion === s.version || (this.labelOrder.length && now - this.labelOrderAt < 1000)) return;
    const slots: number[] = [];
    for (let i = 0; i < s.nodeCount; i++) if (s.nodeVisible(i)) slots.push(i);
    slots.sort((a, b) => s.priority[b] - s.priority[a] || a - b);
    this.labelOrder = Int32Array.from(slots.slice(0, 40000));
    this.labelOrderVersion = s.version; this.labelOrderAt = now;
  }

  private drawOverlay(): void {
    const ctx = this.ctx, th = this.theme, s = this.scene, z = this.camera.zoom;
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    ctx.clearRect(0, 0, this.width, this.height);
    const W = this.width, H = this.height;
    const toS = (x: number, y: number): [number, number] => [(x - this.camera.x) * z + W / 2, (y - this.camera.y) * z + H / 2];
    const inView = (x: number, y: number, m: number) => x > -m && x < W + m && y > -m && y < H + m;
    ctx.textBaseline = "middle";

    // ghosts: removed elements of the highlighted change
    const ghosts = this.hooks.ghosts?.() ?? [];
    for (const g of ghosts) {
      const [x, y] = toS(g.x, g.y);
      if (g.edge) {
        const [x2, y2] = toS(g.edge.x2, g.edge.y2);
        ctx.strokeStyle = g.color; ctx.setLineDash([5, 4]); ctx.lineWidth = 1.4; ctx.beginPath(); ctx.moveTo(x, y); ctx.lineTo(x2, y2); ctx.stroke(); ctx.setLineDash([]);
        continue;
      }
      if (!inView(x, y, 40)) continue;
      const r = Math.max(g.r * z, 4);
      ctx.strokeStyle = g.color; ctx.lineWidth = 1.6; ctx.setLineDash([4, 3]);
      ctx.beginPath(); ctx.arc(x, y, r + 1, 0, Math.PI * 2); ctx.stroke(); ctx.setLineDash([]);
      ctx.fillStyle = g.color; ctx.font = "bold 11px system-ui, sans-serif"; ctx.textAlign = "center"; ctx.fillText("−", x, y);
      if (r > 6) { ctx.font = "12px system-ui, sans-serif"; ctx.fillText(g.label, x, y + r + 10); }
    }

    // cards (projection editor): drawn by the pane
    if (this.hooks.drawCard) {
      for (let i = 0; i < s.nodeCount; i++) {
        if (s.nodeShape(i) !== Shape.Card) continue;
        const cx = s.positions[2 * i], cy = s.positions[2 * i + 1];
        const hw = s.nodeF32[i * 5 + 1] * z, hh = s.nodeF32[i * 5 + 2] * z;
        const [sx, sy] = toS(cx, cy);
        if (sx + hw < 0 || sx - hw > W || sy + hh < 0 || sy - hh > H) continue;
        this.hooks.drawCard(ctx, i, sx, sy, hw, hh, th);
      }
    }

    // node labels
    ctx.font = "12px system-ui, sans-serif";
    ctx.textAlign = "center";
    const boxes = new Map<number, number[]>();
    const tryPlace = (x: number, y: number, w: number, h: number): boolean => {
      const x0 = x - w / 2, y0 = y - h / 2;
      const cx0 = Math.floor(x0 / 64), cx1 = Math.floor((x0 + w) / 64), cy0 = Math.floor(y0 / 64), cy1 = Math.floor((y0 + h) / 64);
      for (let cy = cy0; cy <= cy1; cy++) for (let cx = cx0; cx <= cx1; cx++) {
        const l = boxes.get(cy * 100003 + cx);
        if (!l) continue;
        for (let k = 0; k < l.length; k += 4) if (x0 < l[k] + l[k + 2] && x0 + w > l[k] && y0 < l[k + 1] + l[k + 3] && y0 + h > l[k + 1]) return false;
      }
      for (let cy = cy0; cy <= cy1; cy++) for (let cx = cx0; cx <= cx1; cx++) {
        const key = cy * 100003 + cx;
        const l = boxes.get(key);
        if (l) l.push(x0, y0, w, h); else boxes.set(key, [x0, y0, w, h]);
      }
      return true;
    };
    let drawn = 0;
    const MAX_LABELS = 320;
    const labelNode = (i: number, force: boolean): void => {
      const x0 = s.positions[2 * i], y0 = s.positions[2 * i + 1];
      if (!Number.isFinite(x0)) return;
      const [x, y] = toS(x0, y0);
      if (!inView(x, y, 30)) return;
      const r = Math.max(s.radius[i] * z, 1.4);
      const st = s.nodeState(i);
      const special = (st & (St.Selected | St.Hover | St.Match | St.Added | St.Changed | St.Removed | St.Identity)) !== 0 || (s.nodeExtra(i) & Ex.Source) !== 0;
      if (!force && !special && r < 2.4) return;
      const text = this.hooks.nodeLabel?.(i);
      if (!text) return;
      const w = this.textWidth(text) + 6;
      const ly = y + r + 8;
      if (!special && !tryPlace(x, ly, w, 14)) return;
      if (special) tryPlace(x, ly, w, 14);
      ctx.lineWidth = 3; ctx.strokeStyle = th.bg; ctx.strokeText(text, x, ly);
      ctx.fillStyle = special ? th.text : th.muted; ctx.fillText(text, x, ly);
      drawn++;
    };
    // special nodes first so they always win
    const hot = this.hotNodes;
    for (const i of hot) labelNode(i, true);
    if (this.hooks.nodeLabel && !this.wires) {
      const [wx0, wy0] = this.screenToWorld(0, 0), [wx1, wy1] = this.screenToWorld(W, H);
      const approx = s.nodeCount > 3000 ? this.ensureGrid().countRect(wx0, wy0, wx1, wy1) : s.nodeCount;
      if (approx < 4000) {
        const cands: number[] = [];
        const push = (i: number) => cands.push(i);
        if (s.nodeCount > 3000) this.ensureGrid().forRect(wx0, wy0, wx1, wy1, push);
        else for (let i = 0; i < s.nodeCount; i++) if (s.nodeVisible(i)) cands.push(i);
        cands.sort((a, b) => s.priority[b] - s.priority[a] || a - b);
        for (const i of cands) { if (drawn >= MAX_LABELS) break; labelNode(i, false); }
      } else {
        this.ensureLabelOrder();
        let scanned = 0;
        for (const i of this.labelOrder) { if (drawn >= MAX_LABELS || ++scanned > 20000) break; labelNode(i, false); }
      }
    }
    this.stats.visibleLabels = drawn;

    // telemetry warning glyph: a non-color cue for nodes whose state channel is at or above its threshold
    if (s.telemetry.on && s.telemetry.warnNode < 256 && this.hooks.nodeLabel) {
      ctx.font = "bold 12px system-ui, sans-serif"; ctx.textAlign = "center";
      const [wx0, wy0] = this.screenToWorld(0, 0), [wx1, wy1] = this.screenToWorld(W, H);
      const cand = s.nodeCount > 3000 ? this.nodesInRect(wx0, wy0, wx1, wy1) : Array.from({ length: s.nodeCount }, (_, i) => i);
      let marks = 0;
      for (const i of cand) {
        if (marks > 400) break;
        if (s.nodeT[2][i] < s.telemetry.warnNode || !s.nodeVisible(i)) continue;
        const [x, y] = toS(s.positions[2 * i], s.positions[2 * i + 1]);
        const r = Math.max(s.radius[i] * z, 1.4) * 1.5;
        if (r < 5 || !inView(x, y, 20)) continue;
        ctx.lineWidth = 3; ctx.strokeStyle = th.bg; ctx.strokeText("!", x - r - 4, y - r); ctx.fillStyle = `rgb(${th.removed.map((v) => Math.round(v * 255)).join(",")})`; ctx.fillText("!", x - r - 4, y - r);
        marks++;
      }
    }

    // diff glyphs: a non-color cue for added / removed / changed nodes
    ctx.font = "bold 11px system-ui, sans-serif";
    const glyphNode = (i: number): void => {
      const g = this.hooks.nodeGlyph?.(i);
      if (!g) return;
      const x0 = s.positions[2 * i], y0 = s.positions[2 * i + 1];
      const [x, y] = toS(x0, y0);
      if (!inView(x, y, 20)) return;
      const r = Math.max(s.radius[i] * z, 3);
      ctx.fillStyle = th.text; ctx.strokeStyle = th.bg; ctx.lineWidth = 3;
      ctx.strokeText(g, x + r + 6, y - r - 4); ctx.fillText(g, x + r + 6, y - r - 4);
    };
    if (this.hooks.nodeGlyph) {
      if (s.nodeCount < 6000) { for (let i = 0; i < s.nodeCount; i++) if (s.nodeState(i) & (St.Added | St.Changed | St.Removed) && s.nodeVisible(i)) glyphNode(i); }
      else for (const i of this.changedNodes) glyphNode(i);
    }

    this.hooks.overlayExtra?.(ctx, toS);

    // edge labels for hot edges
    if (this.hooks.edgeLabel) {
      ctx.font = "11px system-ui, sans-serif";
      const labelEdges = this.allEdgeLabels && s.edgeCount < 400 ? Array.from({ length: s.edgeCount }, (_, i) => i).filter((i) => s.edgeVisible(i)) : this.hotEdges;
      for (const e of labelEdges) {
        const [a, b] = s.edgeEnds(e);
        if (a < 0 || b < 0) continue;
        const text = this.hooks.edgeLabel(e);
        if (!text) continue;
        const [x1, y1] = toS(s.positions[2 * a], s.positions[2 * a + 1]);
        const [x2, y2] = toS(s.positions[2 * b], s.positions[2 * b + 1]);
        const x = (x1 + x2) / 2, y = (y1 + y2) / 2;
        if (!inView(x, y, 20)) continue;
        ctx.lineWidth = 3; ctx.strokeStyle = th.bg; ctx.strokeText(text, x, y - 6);
        ctx.fillStyle = th.text; ctx.fillText(text, x, y - 6);
      }
    }
  }

  /** Slots the pane marks as hot (selected, hovered, neighbours) so labels and glyphs can find them cheaply. */
  hotNodes: number[] = [];
  hotEdges: number[] = [];
  changedNodes: number[] = [];

  dispose(): void {
    cancelAnimationFrame(this.raf);
    this.resizeObs.disconnect();
    this.renderer.dispose();
    this.host.replaceChildren();
  }
}

function bezierDistance(ax: number, ay: number, bx: number, by: number, px: number, py: number, zoom: number): number {
  const dx = Math.min(Math.max(Math.abs(bx - ax) * 0.5, 24 / zoom), 260 / zoom);
  const c1x = ax + dx, c2x = bx - dx;
  let best = Infinity, lx = ax, ly = ay;
  for (let i = 1; i <= 16; i++) {
    const t = i / 16, u = 1 - t;
    const x = u * u * u * ax + 3 * u * u * t * c1x + 3 * u * t * t * c2x + t * t * t * bx;
    const y = u * u * u * ay + 3 * u * u * t * ay + 3 * u * t * t * by + t * t * t * by;
    const vx = x - lx, vy = y - ly, l2 = vx * vx + vy * vy;
    const k = l2 > 0 ? Math.max(0, Math.min(1, ((px - lx) * vx + (py - ly) * vy) / l2)) : 0;
    best = Math.min(best, Math.hypot(px - (lx + k * vx), py - (ly + k * vy)));
    lx = x; ly = y;
  }
  return best;
}
