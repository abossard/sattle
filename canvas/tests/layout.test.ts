import { describe, expect, it } from "vitest";
import { ForceLayout, defaultForce, placeIncremental } from "../src/layout/force";
import { layeredLayout } from "../src/layout/layered";

const edges = (pairs: [number, number][]) => ({ src: Int32Array.from(pairs.map((p) => p[0])), dst: Int32Array.from(pairs.map((p) => p[1])) });
const tree = (depth: number, fan: number) => {
  const pairs: [number, number][] = [];
  let n = 1, frontier = [0];
  for (let d = 0; d < depth; d++) { const next: number[] = []; for (const f of frontier) for (let k = 0; k < fan; k++) { pairs.push([f, n]); next.push(n++); } frontier = next; }
  return { n, ...edges(pairs) };
};
const ring = (n: number) => ({ n, ...edges(Array.from({ length: n }, (_, i) => [i, (i + 1) % n] as [number, number])) });
const grid = (w: number) => {
  const pairs: [number, number][] = [];
  for (let y = 0; y < w; y++) for (let x = 0; x < w; x++) { if (x + 1 < w) pairs.push([y * w + x, y * w + x + 1]); if (y + 1 < w) pairs.push([y * w + x, (y + 1) * w + x]); }
  return { n: w * w, ...edges(pairs) };
};
const run = (g: { n: number; src: Int32Array; dst: Int32Array }, iterations = 60) => {
  const f = new ForceLayout(g, { ...defaultForce, iterations });
  f.step(iterations);
  return f.positions;
};

describe("determinism", () => {
  const cases: [string, { n: number; src: Int32Array; dst: Int32Array }][] = [["tree", tree(4, 3)], ["ring", ring(40)], ["grid", grid(10)]];
  it.each(cases)("force layout of %s is identical on repeat runs", (_name, g) => {
    expect(Array.from(run(g))).toEqual(Array.from(run(g)));
  });
  it.each(cases)("layered layout of %s is identical on repeat runs", (_name, g) => {
    expect(Array.from(layeredLayout(g).positions)).toEqual(Array.from(layeredLayout(g).positions));
  });
  it("force layout produces finite coordinates", () => {
    for (const [, g] of cases) expect(Array.from(run(g)).every(Number.isFinite)).toBe(true);
  });
});

describe("layered layout", () => {
  it("puts parents above children in a tree", () => {
    const g = tree(3, 2);
    const { positions } = layeredLayout(g);
    for (let e = 0; e < g.src.length; e++) expect(positions[2 * g.dst[e] + 1]).toBeGreaterThan(positions[2 * g.src[e] + 1]);
  });
  it("puts the root on top when edges point child to parent", () => {
    const t = tree(3, 2);
    const g = { n: t.n, src: t.dst, dst: t.src };
    const { positions } = layeredLayout(g);
    const rootY = positions[1];
    for (let i = 1; i < g.n; i++) expect(positions[2 * i + 1]).toBeGreaterThan(rootY);
  });
  it("does not overlap nodes inside a layer", () => {
    const g = tree(4, 3);
    const { positions, layers } = layeredLayout(g, { direction: "down", layerGap: 90, nodeGap: 30, roots: "auto" });
    const byLayer = new Map<number, number[]>();
    for (let i = 0; i < g.n; i++) byLayer.set(layers[i], [...(byLayer.get(layers[i]) ?? []), positions[2 * i]]);
    for (const xs of byLayer.values()) { xs.sort((a, b) => a - b); for (let i = 1; i < xs.length; i++) expect(xs[i] - xs[i - 1]).toBeGreaterThanOrEqual(29.99); }
  });
  it.each([["cycle", ring(6)], ["self loop and parallel edges", { n: 3, ...edges([[0, 0], [0, 1], [0, 1], [1, 2]]) }]])("handles %s", (_n, g) => {
    const r = layeredLayout(g);
    expect(Array.from(r.positions).every(Number.isFinite)).toBe(true);
  });
  it("supports left-to-right", () => {
    const g = tree(2, 2);
    const { positions } = layeredLayout(g, { direction: "right", layerGap: 100, nodeGap: 20, roots: "auto" });
    expect(positions[2 * 1]).toBeGreaterThan(positions[0]);
  });
});

describe("force layout quality", () => {
  it("keeps connected nodes closer than unconnected ones in a ring", () => {
    const g = ring(30);
    const p = run(g, 150);
    const d = (a: number, b: number) => Math.hypot(p[2 * a] - p[2 * b], p[2 * a + 1] - p[2 * b + 1]);
    let adj = 0, far = 0;
    for (let i = 0; i < 30; i++) { adj += d(i, (i + 1) % 30); far += d(i, (i + 15) % 30); }
    expect(adj).toBeLessThan(far / 2);
  });
});

describe("placeIncremental", () => {
  it("places a new node near its placed neighbours and leaves others untouched", () => {
    const pos = new Float32Array([0, 0, 100, 0, NaN, NaN]);
    placeIncremental(pos, [2], () => [0, 1], [0, 0], 10);
    expect(pos[0]).toBe(0);
    expect(pos[2]).toBe(100);
    expect(Math.hypot(pos[4] - 50, pos[5])).toBeLessThan(10);
  });
  it("is deterministic and falls back without neighbours", () => {
    const a = new Float32Array([NaN, NaN]), b = new Float32Array([NaN, NaN]);
    placeIncremental(a, [0], () => [], [5, 5], 4);
    placeIncremental(b, [0], () => [], [5, 5], 4);
    expect(Array.from(a)).toEqual(Array.from(b));
    expect(Math.hypot(a[0] - 5, a[1] - 5)).toBeLessThan(5);
  });
});
