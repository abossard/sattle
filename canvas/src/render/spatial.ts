// Uniform grid over node positions for picking and viewport queries. Pure.
export interface PointSet {
  count: number;
  positions: Float32Array;   // x,y per slot
  radius: Float32Array;
  visible(slot: number): boolean;
}

export class SpatialGrid {
  private cell = 1;
  private minX = 0; private minY = 0;
  private cols = 1; private rows = 1;
  private start = new Int32Array(2);
  private items = new Int32Array(0);
  maxRadius = 0;

  static build(set: PointSet): SpatialGrid {
    const g = new SpatialGrid();
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity, n = 0, maxR = 0;
    for (let i = 0; i < set.count; i++) {
      if (!set.visible(i)) continue;
      const x = set.positions[2 * i], y = set.positions[2 * i + 1];
      if (!Number.isFinite(x) || !Number.isFinite(y)) continue;
      if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
      if (set.radius[i] > maxR) maxR = set.radius[i];
      n++;
    }
    g.maxRadius = maxR;
    if (n === 0) return g;
    const w = Math.max(maxX - minX, 1), h = Math.max(maxY - minY, 1);
    g.cell = Math.max(Math.sqrt((w * h) / n) * 2, 1e-3);
    // cap grid size
    while ((w / g.cell + 1) * (h / g.cell + 1) > 4 * n + 64) g.cell *= 1.5;
    g.minX = minX; g.minY = minY;
    g.cols = Math.floor(w / g.cell) + 1; g.rows = Math.floor(h / g.cell) + 1;
    const counts = new Int32Array(g.cols * g.rows + 1);
    const cellOf = (i: number) => Math.floor((set.positions[2 * i + 1] - minY) / g.cell) * g.cols + Math.floor((set.positions[2 * i] - minX) / g.cell);
    const keep = new Int32Array(n);
    let k = 0;
    for (let i = 0; i < set.count; i++) {
      if (!set.visible(i) || !Number.isFinite(set.positions[2 * i])) continue;
      keep[k++] = i;
      counts[cellOf(i) + 1]++;
    }
    for (let c = 0; c < g.cols * g.rows; c++) counts[c + 1] += counts[c];
    g.start = counts;
    g.items = new Int32Array(n);
    const fill = counts.slice(0, g.cols * g.rows);
    for (let j = 0; j < n; j++) { const i = keep[j]; g.items[fill[cellOf(i)]++] = i; }
    return g;
  }

  /** Visit slots whose cells overlap the rectangle. */
  forRect(x0: number, y0: number, x1: number, y1: number, visit: (slot: number) => void): void {
    if (this.items.length === 0) return;
    const c0 = Math.max(0, Math.floor((x0 - this.minX) / this.cell)), c1 = Math.min(this.cols - 1, Math.floor((x1 - this.minX) / this.cell));
    const r0 = Math.max(0, Math.floor((y0 - this.minY) / this.cell)), r1 = Math.min(this.rows - 1, Math.floor((y1 - this.minY) / this.cell));
    for (let r = r0; r <= r1; r++) for (let c = c0; c <= c1; c++) {
      const id = r * this.cols + c;
      for (let k = this.start[id]; k < this.start[id + 1]; k++) visit(this.items[k]);
    }
  }

  /** Number of nodes in cells overlapping a rectangle (upper bound on visible nodes). */
  countRect(x0: number, y0: number, x1: number, y1: number): number {
    if (this.items.length === 0) return 0;
    const c0 = Math.max(0, Math.floor((x0 - this.minX) / this.cell)), c1 = Math.min(this.cols - 1, Math.floor((x1 - this.minX) / this.cell));
    const r0 = Math.max(0, Math.floor((y0 - this.minY) / this.cell)), r1 = Math.min(this.rows - 1, Math.floor((y1 - this.minY) / this.cell));
    let n = 0;
    for (let r = r0; r <= r1; r++) n += this.start[r * this.cols + c1 + 1] - this.start[r * this.cols + c0];
    return n;
  }

  /** Slot whose disc (radius plus slack, world units) contains the point; the closest centre wins. */
  pick(x: number, y: number, slack: number, minRadius: number, set: PointSet): number {
    let best = -1, bestD = Infinity;
    const reach = this.maxRadius + slack + minRadius;
    this.forRect(x - reach, y - reach, x + reach, y + reach, (i) => {
      const dx = set.positions[2 * i] - x, dy = set.positions[2 * i + 1] - y;
      const d = Math.sqrt(dx * dx + dy * dy);
      const r = Math.max(set.radius[i], minRadius) + slack;
      if (d <= r && d < bestD) { bestD = d; best = i; }
    });
    return best;
  }
}
