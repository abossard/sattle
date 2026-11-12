// Scene: typed-array render data shared by the WebGL renderer, picking and labels.
// Node instance = 5 words [slot, halfW(f32), halfH(f32), colorRGBA, meta]; edge instance = 4 words [src, dst, colorRGBA, meta].
// meta byte 0 = shape/style (0 = hidden), byte 1 = state flags, byte 2 = extra flags.

export const Shape = { Hidden: 0, Circle: 1, Square: 2, Diamond: 3, Triangle: 4, Hexagon: 5, Card: 6 } as const;
export const EdgeStyle = { Hidden: 0, Solid: 1, Dashed: 2 } as const;
/** What an edge means (engine 3.17), drawn by line pattern and arrowhead so it never rests on color: composition solid with a diamond, dependency dash-dot with a spike, communication dotted with a triangle, support thin and faint with none. */
export const EdgeKindCode = { none: 0, composition: 1, dependency: 2, communication: 3, support: 4 } as const;

/** state flags (meta byte 1) */
export const St = {
  Selected: 1, Hover: 2, Related: 4, Match: 8, Added: 16, Removed: 32, Changed: 64, Identity: 128,
} as const;
/** extra flags (meta byte 2) */
/** `Blame`: on the way from the top to the part that is to blame (nodes: a thick coral ring; edges: thick and coral) */
export const Ex = { Source: 1, Aggregate: 2, Bidirectional: 4, Error: 8, Warning: 16, Port: 32, Reference: 64, Blame: 128 } as const;

export const WORDS_NODE = 5;
export const WORDS_EDGE = 4;

export const pack = (r: number, g: number, b: number, a = 255): number => ((a << 24) | (b << 16) | (g << 8) | r) >>> 0;
export function parseColor(hex: string, alpha = 255): number {
  const h = hex.replace("#", "");
  return pack(parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16), alpha);
}
export const withAlpha = (c: number, a: number): number => ((c & 0x00ffffff) | (a << 24)) >>> 0;

export interface Range { lo: number; hi: number }

/** Telemetry visual state: three 8-bit channels per target, indexed by slot, uploaded as normalized GPU attributes. */
export interface TelemetryParams {
  on: boolean;
  /** state threshold in byte units, 256 = never */
  warnNode: number;
  warnEdge: number;
  flow: boolean;
}
export const CHANNELS = 3;

export class Scene {
  nodeCap = 0;
  edgeCap = 0;
  nodeCount = 0;  // high-water mark; hidden instances have shape 0
  edgeCount = 0;
  nodeBuf: ArrayBuffer = new ArrayBuffer(0);
  nodeU32 = new Uint32Array(0);
  nodeF32 = new Float32Array(0);
  edgeU32 = new Uint32Array(0);
  edgeI32 = new Int32Array(0);
  /** world positions, x,y per node slot (laid out by workers or by hand) */
  positions = new Float32Array(0);
  /** world radius used for arrow clipping and picking */
  radius = new Float32Array(0);
  /** label priority per node slot; higher draws first */
  priority = new Float32Array(0);
  /** node channels: ring gauge, size, state. edge channels: width, flow speed, state. */
  nodeT: Uint8Array[] = [new Uint8Array(0), new Uint8Array(0), new Uint8Array(0)];
  edgeT: Uint8Array[] = [new Uint8Array(0), new Uint8Array(0), new Uint8Array(0)];
  telemetry: TelemetryParams = { on: false, warnNode: 256, warnEdge: 256, flow: true };
  telemetryDirty = false;
  telemetryVersion = 0;
  nodeDirty: Range | null = null;
  edgeDirty: Range | null = null;
  positionsDirty = true;
  /** bumped whenever instance data changes, so pickers and label layers rebuild */
  version = 0;
  positionsVersion = 0;

  ensureNodes(n: number): void {
    if (n <= this.nodeCap) return;
    const cap = Math.max(n, Math.ceil(this.nodeCap * 1.5), 1024);
    const buf = new ArrayBuffer(cap * WORDS_NODE * 4);
    new Uint8Array(buf).set(new Uint8Array(this.nodeBuf));
    this.nodeBuf = buf; this.nodeU32 = new Uint32Array(buf); this.nodeF32 = new Float32Array(buf);
    const pos = new Float32Array(cap * 2).fill(NaN); pos.set(this.positions); this.positions = pos;
    const rad = new Float32Array(cap); rad.set(this.radius); this.radius = rad;
    const pr = new Float32Array(cap); pr.set(this.priority); this.priority = pr;
    this.nodeT = this.nodeT.map((a) => { const n = new Uint8Array(cap); n.set(a); return n; });
    this.nodeCap = cap;
    this.telemetryDirty = true;
    this.positionsDirty = true;
    this.nodeDirty = { lo: 0, hi: this.nodeCount };
  }

  ensureEdges(n: number): void {
    if (n <= this.edgeCap) return;
    const cap = Math.max(n, Math.ceil(this.edgeCap * 1.5), 1024);
    const u = new Uint32Array(cap * WORDS_EDGE); u.set(this.edgeU32);
    this.edgeU32 = u; this.edgeI32 = new Int32Array(u.buffer);
    this.edgeT = this.edgeT.map((a) => { const n = new Uint8Array(cap); n.set(a); return n; });
    this.edgeCap = cap;
    this.telemetryDirty = true;
    this.edgeDirty = { lo: 0, hi: this.edgeCount };
  }

  setNode(slot: number, shape: number, halfW: number, halfH: number, color: number, state = 0, extra = 0): void {
    this.ensureNodes(slot + 1);
    const o = slot * WORDS_NODE;
    this.nodeU32[o] = slot;
    this.nodeF32[o + 1] = halfW; this.nodeF32[o + 2] = halfH;
    this.nodeU32[o + 3] = color;
    this.nodeU32[o + 4] = (shape | (state << 8) | (extra << 16)) >>> 0;
    this.radius[slot] = Math.max(halfW, halfH);
    if (slot >= this.nodeCount) this.nodeCount = slot + 1;
    this.markNode(slot);
  }

  hideNode(slot: number): void {
    if (slot >= this.nodeCount) return;
    this.nodeU32[slot * WORDS_NODE + 4] = 0;
    this.markNode(slot);
  }

  setNodeMeta(slot: number, shape: number, state: number, extra: number): void {
    this.nodeU32[slot * WORDS_NODE + 4] = (shape | (state << 8) | (extra << 16)) >>> 0;
    this.markNode(slot);
  }
  nodeShape(slot: number): number { return this.nodeU32[slot * WORDS_NODE + 4] & 255; }
  nodeState(slot: number): number { return (this.nodeU32[slot * WORDS_NODE + 4] >>> 8) & 255; }
  nodeExtra(slot: number): number { return (this.nodeU32[slot * WORDS_NODE + 4] >>> 16) & 255; }
  nodeColor(slot: number): number { return this.nodeU32[slot * WORDS_NODE + 3]; }
  setNodeColor(slot: number, c: number): void { this.nodeU32[slot * WORDS_NODE + 3] = c; this.markNode(slot); }
  setNodeState(slot: number, state: number): void {
    const o = slot * WORDS_NODE + 4;
    this.nodeU32[o] = ((this.nodeU32[o] & ~0xff00) | (state << 8)) >>> 0;
    this.markNode(slot);
  }
  nodeVisible(slot: number): boolean { return slot < this.nodeCount && (this.nodeU32[slot * WORDS_NODE + 4] & 255) !== 0; }

  setEdge(slot: number, src: number, dst: number, color: number, style: number, state = 0, extra = 0, kind = 0): void {
    this.ensureEdges(slot + 1);
    const o = slot * WORDS_EDGE;
    this.edgeI32[o] = src; this.edgeI32[o + 1] = dst;
    this.edgeU32[o + 2] = color;
    this.edgeU32[o + 3] = (style | (state << 8) | (extra << 16) | (kind << 24)) >>> 0;
    if (slot >= this.edgeCount) this.edgeCount = slot + 1;
    this.markEdge(slot);
  }
  hideEdge(slot: number): void {
    if (slot >= this.edgeCount) return;
    this.edgeU32[slot * WORDS_EDGE + 3] = 0;
    this.markEdge(slot);
  }
  edgeStyle(slot: number): number { return this.edgeU32[slot * WORDS_EDGE + 3] & 255; }
  edgeState(slot: number): number { return (this.edgeU32[slot * WORDS_EDGE + 3] >>> 8) & 255; }
  edgeKind(slot: number): number { return (this.edgeU32[slot * WORDS_EDGE + 3] >>> 24) & 255; }
  edgeExtra(slot: number): number { return (this.edgeU32[slot * WORDS_EDGE + 3] >>> 16) & 255; }
  setEdgeState(slot: number, state: number): void {
    const o = slot * WORDS_EDGE + 3;
    this.edgeU32[o] = ((this.edgeU32[o] & ~0xff00) | (state << 8)) >>> 0;
    this.markEdge(slot);
  }
  edgeEnds(slot: number): [number, number] { return [this.edgeI32[slot * WORDS_EDGE], this.edgeI32[slot * WORDS_EDGE + 1]]; }
  edgeVisible(slot: number): boolean { return slot < this.edgeCount && (this.edgeU32[slot * WORDS_EDGE + 3] & 255) !== 0; }

  /** Copy a quantized frame into a channel. Frames shorter than the capacity leave the rest zero. */
  setChannel(target: "node" | "edge", channel: number, frame: Uint8Array | null): void {
    const dst = (target === "node" ? this.nodeT : this.edgeT)[channel];
    if (!dst) return;
    if (!frame) dst.fill(0);
    else { const n = Math.min(frame.length, dst.length); dst.set(frame.length === n ? frame : frame.subarray(0, n)); if (n < dst.length) dst.fill(0, n); }
    this.telemetryDirty = true; this.telemetryVersion++;
  }

  setPosition(slot: number, x: number, y: number): void {
    this.ensureNodes(slot + 1);
    this.positions[2 * slot] = x; this.positions[2 * slot + 1] = y;
    this.positionsDirty = true; this.positionsVersion++;
  }

  /** Overwrite many positions (layout progress). `p` has x,y per slot. */
  setPositions(p: Float32Array): void {
    this.ensureNodes(p.length / 2);
    this.positions.set(p.subarray(0, Math.min(p.length, this.positions.length)));
    this.positionsDirty = true; this.positionsVersion++;
  }

  private markNode(slot: number): void {
    const d = this.nodeDirty;
    if (!d) this.nodeDirty = { lo: slot, hi: slot + 1 };
    else { if (slot < d.lo) d.lo = slot; if (slot + 1 > d.hi) d.hi = slot + 1; }
    this.version++;
  }
  private markEdge(slot: number): void {
    const d = this.edgeDirty;
    if (!d) this.edgeDirty = { lo: slot, hi: slot + 1 };
    else { if (slot < d.lo) d.lo = slot; if (slot + 1 > d.hi) d.hi = slot + 1; }
    this.version++;
  }

  /** Bounding box of visible nodes with finite positions. */
  bounds(): { minX: number; minY: number; maxX: number; maxY: number } | null {
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (let i = 0; i < this.nodeCount; i++) {
      if (!this.nodeVisible(i)) continue;
      const x = this.positions[2 * i], y = this.positions[2 * i + 1];
      if (!Number.isFinite(x)) continue;
      const hw = this.nodeF32[i * WORDS_NODE + 1], hh = this.nodeF32[i * WORDS_NODE + 2];
      if (x - hw < minX) minX = x - hw; if (x + hw > maxX) maxX = x + hw; if (y - hh < minY) minY = y - hh; if (y + hh > maxY) maxY = y + hh;
    }
    return minX === Infinity ? null : { minX, minY, maxX, maxY };
  }
}
