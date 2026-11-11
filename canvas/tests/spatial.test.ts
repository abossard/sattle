import { describe, expect, it } from "vitest";
import { SpatialGrid, type PointSet } from "../src/render/spatial";
import { mulberry32 } from "../src/core/synth";

const points = (n: number, seed = 3): PointSet => {
  const r = mulberry32(seed);
  const positions = new Float32Array(2 * n);
  for (let i = 0; i < 2 * n; i++) positions[i] = r() * 1000;
  return { count: n, positions, radius: new Float32Array(n).fill(4), visible: () => true };
};
const brute = (s: PointSet, x: number, y: number, slack: number, minR: number) => {
  let best = -1, bd = Infinity;
  for (let i = 0; i < s.count; i++) {
    const d = Math.hypot(s.positions[2 * i] - x, s.positions[2 * i + 1] - y);
    if (d <= Math.max(s.radius[i], minR) + slack && d < bd) { bd = d; best = i; }
  }
  return best;
};

describe("SpatialGrid.pick agrees with a brute-force scan", () => {
  it.each([[50], [2000], [20000]])("n=%i", (n) => {
    const s = points(n);
    const g = SpatialGrid.build(s);
    const r = mulberry32(9);
    for (let k = 0; k < 300; k++) {
      const x = r() * 1000, y = r() * 1000;
      expect(g.pick(x, y, 2, 1.5, s)).toBe(brute(s, x, y, 2, 1.5));
    }
  });
  it("ignores hidden and unplaced nodes", () => {
    const s = points(100);
    s.positions[0] = NaN;
    const hidden = new Set([1, 2, 3]);
    s.visible = (i) => !hidden.has(i);
    const g = SpatialGrid.build(s);
    for (const h of [0, 1, 2, 3]) expect(g.pick(s.positions[2 * h], s.positions[2 * h + 1], 0.01, 0, s)).not.toBe(h);
  });
  it("forRect visits every point inside the rectangle", () => {
    const s = points(3000);
    const g = SpatialGrid.build(s);
    const seen = new Set<number>();
    g.forRect(200, 200, 400, 500, (i) => seen.add(i));
    for (let i = 0; i < s.count; i++) {
      const x = s.positions[2 * i], y = s.positions[2 * i + 1];
      if (x >= 200 && x <= 400 && y >= 200 && y <= 500) expect(seen.has(i)).toBe(true);
    }
  });
});
