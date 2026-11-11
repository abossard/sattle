// Force-directed layout (Fruchterman-Reingold forces, Barnes-Hut repulsion, cooling schedule).
// Deterministic: seeding is a breadth-first spiral and every loop runs in index order, so equal input gives equal output.

export interface ForceInput {
  n: number;
  src: Int32Array;
  dst: Int32Array;
  /** optional starting positions (x,y per node); NaN entries are seeded */
  positions?: Float32Array;
  /** nodes that must not move */
  pinned?: Uint8Array;
}
export interface ForceOptions {
  /** ideal edge length in world units */
  length: number;
  theta: number;
  gravity: number;
  iterations: number;
}
export const defaultForce: ForceOptions = { length: 30, theta: 0.9, gravity: 0.02, iterations: 220 };

/** Seed: breadth-first order from the best connected nodes, laid on a Vogel spiral so neighbours start close. */
export function seedPositions(n: number, src: Int32Array, dst: Int32Array, length: number, existing?: Float32Array): Float32Array {
  const pos = existing ? existing.slice() : new Float32Array(n * 2).fill(NaN);
  const start = new Int32Array(n + 1);
  for (let e = 0; e < src.length; e++) { if (src[e] < 0 || dst[e] < 0) continue; start[src[e] + 1]++; start[dst[e] + 1]++; }
  for (let i = 0; i < n; i++) start[i + 1] += start[i];
  const adj = new Int32Array(start[n]);
  const fill = start.slice(0, n);
  for (let e = 0; e < src.length; e++) { const s = src[e], t = dst[e]; if (s < 0 || t < 0) continue; adj[fill[s]++] = t; adj[fill[t]++] = s; }
  const deg = (i: number) => start[i + 1] - start[i];
  const roots = Array.from({ length: n }, (_, i) => i).filter((i) => Number.isNaN(pos[2 * i]));
  roots.sort((a, b) => deg(b) - deg(a) || a - b);
  const seen = new Uint8Array(n);
  const queue = new Int32Array(n);
  const GOLD = Math.PI * (3 - Math.sqrt(5));
  let placed = 0;
  const radius = length * 0.6;
  for (const r of roots) {
    if (seen[r]) continue;
    let qh = 0, qt = 0;
    queue[qt++] = r; seen[r] = 1;
    while (qh < qt) {
      const v = queue[qh++];
      const a = placed * GOLD, rad = radius * Math.sqrt(placed + 1);
      pos[2 * v] = Math.cos(a) * rad;
      pos[2 * v + 1] = Math.sin(a) * rad;
      placed++;
      for (let k = start[v]; k < start[v + 1]; k++) { const w = adj[k]; if (!seen[w] && Number.isNaN(pos[2 * w])) { seen[w] = 1; queue[qt++] = w; } }
    }
  }
  return pos;
}

export class ForceLayout {
  readonly n: number;
  readonly positions: Float32Array;
  private readonly src: Int32Array;
  private readonly dst: Int32Array;
  private readonly pinned?: Uint8Array;
  private readonly opt: ForceOptions;
  private readonly dx: Float64Array;
  private readonly dy: Float64Array;
  // Barnes-Hut quadtree in structure-of-arrays form
  private cap: number;
  private gx!: Float64Array; private gy!: Float64Array; private tsize!: Float64Array; private tmass!: Float64Array;
  private sx!: Float64Array; private sy!: Float64Array; private tchild!: Int32Array; private tbody!: Int32Array;
  private tcount = 0;
  private stack: Int32Array<ArrayBufferLike> = new Int32Array(1024);
  iteration = 0;

  constructor(input: ForceInput, opt: ForceOptions = defaultForce) {
    this.n = input.n; this.src = input.src; this.dst = input.dst; this.opt = opt; this.pinned = input.pinned;
    this.positions = seedPositions(input.n, input.src, input.dst, opt.length, input.positions);
    this.dx = new Float64Array(input.n); this.dy = new Float64Array(input.n);
    this.cap = 0;
    this.alloc(3 * input.n + 64);
  }

  get done(): boolean { return this.iteration >= this.opt.iterations; }

  /** Run up to `count` iterations; returns the maximum node displacement of the last one. */
  step(count: number): number {
    let last = 0;
    for (let c = 0; c < count && !this.done; c++) { last = this.iterate(); this.iteration++; }
    return last;
  }

  private alloc(cap: number): void {
    const f = (old: Float64Array | undefined) => { const a = new Float64Array(cap); if (old) a.set(old); return a; };
    this.gx = f(this.gx); this.gy = f(this.gy); this.tsize = f(this.tsize); this.tmass = f(this.tmass); this.sx = f(this.sx); this.sy = f(this.sy);
    const ch = new Int32Array(cap * 4); if (this.tchild) ch.set(this.tchild); this.tchild = ch;
    const bd = new Int32Array(cap); if (this.tbody) bd.set(this.tbody); this.tbody = bd;
    this.cap = cap;
  }

  private cell(cx: number, cy: number, size: number): number {
    if (this.tcount >= this.cap) this.alloc(this.cap * 2);
    const id = this.tcount++;
    this.gx[id] = cx; this.gy[id] = cy; this.tsize[id] = size; this.tmass[id] = 0; this.sx[id] = 0; this.sy[id] = 0; this.tbody[id] = -1;
    const c = id * 4;
    this.tchild[c] = this.tchild[c + 1] = this.tchild[c + 2] = this.tchild[c + 3] = -1;
    return id;
  }

  private childFor(c: number, x: number, y: number): number {
    const q = (x >= this.gx[c] ? 1 : 0) + (y >= this.gy[c] ? 2 : 0);
    let ch = this.tchild[c * 4 + q];
    if (ch < 0) {
      const half = this.tsize[c] / 2;
      ch = this.cell(this.gx[c] + (q & 1 ? half / 2 : -half / 2), this.gy[c] + (q & 2 ? half / 2 : -half / 2), half);
      this.tchild[c * 4 + q] = ch;
    }
    return ch;
  }

  private build(): void {
    const { n, positions: p } = this;
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (let i = 0; i < n; i++) {
      const x = p[2 * i], y = p[2 * i + 1];
      if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
    }
    const size = Math.max(maxX - minX, maxY - minY, 1) * 1.001;
    this.tcount = 0;
    const root = this.cell(minX + size / 2, minY + size / 2, size);
    for (let i = 0; i < n; i++) {
      const x = p[2 * i], y = p[2 * i + 1];
      let c = root;
      for (let depth = 0; ; depth++) {
        if (this.tmass[c] === 0) { this.tbody[c] = i; this.tmass[c] = 1; this.sx[c] = x; this.sy[c] = y; break; }
        if (this.tbody[c] >= 0) {
          if (depth >= 40) { this.tmass[c]++; this.sx[c] += x; this.sy[c] += y; break; } // coincident points
          const b = this.tbody[c];
          this.tbody[c] = -1;
          const ch = this.childFor(c, p[2 * b], p[2 * b + 1]);
          this.tbody[ch] = b; this.tmass[ch] = 1; this.sx[ch] = p[2 * b]; this.sy[ch] = p[2 * b + 1];
        }
        this.tmass[c]++; this.sx[c] += x; this.sy[c] += y;
        c = this.childFor(c, x, y);
      }
    }
  }

  private iterate(): number {
    const { n, positions: p, dx, dy, opt } = this;
    const k = opt.length, k2 = k * k;
    this.build();
    dx.fill(0); dy.fill(0);
    const theta2 = opt.theta * opt.theta;
    const { tmass, sx, sy, tsize, tbody, tchild } = this;
    let stack = this.stack;
    for (let i = 0; i < n; i++) {
      const x = p[2 * i], y = p[2 * i + 1];
      let fx = 0, fy = 0, sp = 0;
      stack[sp++] = 0;
      while (sp > 0) {
        const c = stack[--sp];
        const m = tmass[c];
        if (m === 0) continue;
        const body = tbody[c];
        if (body === i && m === 1) continue;
        const ddx = x - sx[c] / m, ddy = y - sy[c] / m;
        const d2 = ddx * ddx + ddy * ddy + 0.01;
        if (body >= 0 || tsize[c] * tsize[c] < theta2 * d2) {
          const f = (k2 * m) / d2;
          fx += ddx * f; fy += ddy * f;
          continue;
        }
        if (sp + 4 > stack.length) { stack = growStack(stack); this.stack = stack; }
        const b = c * 4;
        for (let q = 0; q < 4; q++) { const ch = tchild[b + q]; if (ch >= 0) stack[sp++] = ch; }
      }
      dx[i] = fx; dy[i] = fy;
    }
    // attraction along edges: F = d^2 / k, expressed per unit direction as d / k
    for (let e = 0; e < this.src.length; e++) {
      const a = this.src[e], b = this.dst[e];
      if (a < 0 || b < 0 || a === b) continue;
      const ex = p[2 * a] - p[2 * b], ey = p[2 * a + 1] - p[2 * b + 1];
      const f = Math.sqrt(ex * ex + ey * ey) / k;
      dx[a] -= ex * f; dy[a] -= ey * f; dx[b] += ex * f; dy[b] += ey * f;
    }
    // gravity and cooling
    const t = k * 2 * (1 - this.iteration / opt.iterations) + 0.05;
    let maxMove = 0;
    for (let i = 0; i < n; i++) {
      dx[i] -= p[2 * i] * opt.gravity * k; dy[i] -= p[2 * i + 1] * opt.gravity * k;
      if (this.pinned?.[i]) continue;
      const len = Math.sqrt(dx[i] * dx[i] + dy[i] * dy[i]) + 1e-9;
      const move = Math.min(len, t);
      p[2 * i] += (dx[i] / len) * move;
      p[2 * i + 1] += (dy[i] / len) * move;
      if (move > maxMove) maxMove = move;
    }
    return maxMove;
  }
}

function growStack(s: Int32Array<ArrayBufferLike>): Int32Array<ArrayBuffer> { const g = new Int32Array(s.length * 2); g.set(s); return g; }

/**
 * Place new nodes without a full layout: centroid of already placed neighbours plus a deterministic offset.
 * Nodes without placed neighbours go to the given fallback point. Returns the indices that were placed.
 */
export function placeIncremental(
  positions: Float32Array, newNodes: readonly number[], neighbours: (node: number) => readonly number[], fallback: [number, number], spread: number,
): void {
  const placed = new Set<number>();
  for (const v of newNodes) {
    let sx = 0, sy = 0, c = 0;
    for (const w of neighbours(v)) {
      if (w === v || Number.isNaN(positions[2 * w]) || (newNodes.includes(w) && !placed.has(w))) continue;
      sx += positions[2 * w]; sy += positions[2 * w + 1]; c++;
    }
    const a = (v * 2.399963) % (2 * Math.PI);
    const r = spread * (0.6 + ((v * 7919) % 100) / 200);
    const cx = c ? sx / c : fallback[0], cy = c ? sy / c : fallback[1];
    positions[2 * v] = cx + Math.cos(a) * r;
    positions[2 * v + 1] = cy + Math.sin(a) * r;
    placed.add(v);
  }
}
