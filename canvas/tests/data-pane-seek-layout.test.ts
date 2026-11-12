// @vitest-environment jsdom
// The layout of a revision is a function of that revision and the pane: however the person got to revision r (in order, backwards, jumping, from
// a pane made on the empty first revision), every node stands where a pane opened fresh at r puts it.
import { describe, expect, it, vi } from "vitest";

vi.mock("../src/render/view", () => ({
  GraphView: class {
    constructor() {
      const own: Record<string | symbol, unknown> = {};
      return new Proxy(own, { get: (t, k) => (k in t ? t[k] : () => ({ x: 0, y: 0 })), set: (t, k, v) => { t[k] = v; return true; } });
    }
  },
}));
// the worker's work, done on the spot; as in the worker, a new request cancels the one before it (it never resolves)
vi.mock("../src/layout/client", async () => {
  const { layeredLayout, defaultLayered } = await import("../src/layout/layered");
  return {
    LayoutClient: class {
      lastMs = 0; seq = 0;
      run(req: { kind: string; n: number; src: Int32Array; dst: Int32Array; width?: Float32Array; height?: Float32Array; options?: object }) {
        const id = ++this.seq;
        const done = Promise.resolve().then(() => (id !== this.seq || req.kind !== "layered" ? new Promise<Float32Array>(() => {})
          : layeredLayout({ n: req.n, src: req.src, dst: req.dst, width: req.width, height: req.height }, { ...defaultLayered, ...req.options }).positions));
        return { id, done };
      }
      cancel() { this.seq++; }
      dispose() {}
    },
  };
});

import { DataPane } from "../src/app/data-pane";
import { History } from "../src/app/history";
import type { Patch } from "../src/core/model";
import { darkTheme } from "../src/render/theme";
import { StaticSource } from "../src/source/static";
import type { GraphInfo } from "../src/source/types";

const files = import.meta.glob("/public/demo/estate/*.{json,ndjson,md}", { query: "?raw", import: "default", eager: true }) as Record<string, string>;
const fetcher = async (url: string) => { const t = files[`/public${new URL(url).pathname}`]; if (t === undefined) throw new Error(url); return t; };
const events = { select() {}, hover() {}, activate() {}, changed() {}, announce() {}, key: () => false };
const flush = () => new Promise((r) => setTimeout(r, 0));

async function load(graph: string): Promise<{ info: GraphInfo; history: () => History }> {
  const src = new StaticSource("t", "http://localhost/demo/estate/manifest.json", fetcher);
  const info = (await src.open()).graphs.find((g) => g.id === graph)!;
  const snapshot = await src.snapshot(graph);
  const patches: Patch[] = await new Promise((resolve) => { const out: Patch[] = []; src.subscribe(graph, (e) => { if (e.type === "patch") out.push(e.patch); if (e.type === "end") resolve(out); }); });
  return { info, history: () => { const h = new History(snapshot); for (const p of patches) h.append(p); return h; } };
}

/** where each node of the pane stands, by entity id */
const positions = (pane: DataPane): Map<string, [number, number]> => {
  const out = new Map<string, [number, number]>();
  const g = pane.graph, p = pane.scene.positions;
  for (let i = 0; i < g.nodes.length; i++) { const e = g.nodes[i]; if (e && pane.scene.nodeVisible(i)) out.set(e.id, [p[2 * i], p[2 * i + 1]]); }
  return out;
};

async function fresh(info: GraphInfo, history: () => History, r: number): Promise<DataPane> {
  const h = history();
  h.seek(r);
  const pane = new DataPane(info, h, document.createElement("div"), darkTheme, events as never);
  pane.start();
  await flush();
  return pane;
}

describe("the layout of the workload graph after seeking", () => {
  it.each([
    ["in order", (n: number) => Array.from({ length: n + 1 }, (_, i) => i)],
    ["backwards", (n: number) => Array.from({ length: n + 1 }, (_, i) => n - i)],
    ["jumping", (n: number) => [n, 3, 10, 7, 0, 12, 1, 9, n - 1].filter((r) => r <= n)],
  ])("%s, every revision is laid out as a fresh load of it", async (_, order) => {
    const { info, history } = await load("workload");
    const h = history();
    const n = h.length;
    // the pane is made where the person starts: the first revision
    h.seek(0);
    const pane = new DataPane(info, h, document.createElement("div"), darkTheme, events as never);
    pane.start();
    await flush();
    let layered = 0;
    for (const r of order(n)) {
      for (const s of h.seek(r)) pane.applyStep(s);
      await flush();
      const want = await fresh(info, history, r);
      expect(pane.layoutKind, `layout at revision ${r}`).toBe(want.layoutKind);
      if (want.layoutKind !== "layered") continue;
      layered++;
      const got = positions(pane), exp = positions(want);
      expect([...got.keys()].sort(), `nodes at revision ${r}`).toEqual([...exp.keys()].sort());
      for (const [id, [x, y]] of exp) {
        expect(got.get(id)![0], `${id} x at revision ${r}`).toBeCloseTo(x, 3);
        expect(got.get(id)![1], `${id} y at revision ${r}`).toBeCloseTo(y, 3);
      }
    }
    expect(layered, "revisions laid out as a tree").toBeGreaterThan(0);
  });
});
