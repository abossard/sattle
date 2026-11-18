// @vitest-environment jsdom
// What a pane shows after patches or seeks equals what a fresh pane shows from the same graph: the incremental path and the full pass are one
// calculation. Over patches the scene is compared; over seeks of the demo workload, the layout. The WebGL view is replaced; the scene is the real one.
import { describe, expect, it, vi } from "vitest";

vi.mock("../src/render/view", () => ({
  GraphView: class {
    constructor() {
      const own: Record<string | symbol, unknown> = {};
      return new Proxy(own, { get: (t, k) => (k in t ? t[k] : () => ({ x: 0, y: 0 })), set: (t, k, v) => { t[k] = v; return true; } });
    }
  },
}));
// the worker's work, done on the spot; as in the worker, a new request cancels the one before it (it never resolves); a force layout never ends
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
import { emptyFilter } from "../src/app/graph-view-model";
import { History } from "../src/app/history";
import type { Edit, Patch, Snapshot } from "../src/core/model";
import { Ex, St, type Scene } from "../src/render/scene";
import { darkTheme } from "../src/render/theme";
import { StaticSource } from "../src/source/static";
import type { GraphInfo } from "../src/source/types";
import { addE, addN, delE, delN, ent, patch, rel, setN, ver } from "./helpers";
import { ont } from "./fixtures/engine-fake";

const info = { id: "g", role: "master", descriptor: {}, ontology: ont("o"), inputs: [] } as unknown as GraphInfo;
const events = { select() {}, hover() {}, activate() {}, changed() {}, announce() {}, key: () => false };
const scene = (p: DataPane) => (p as unknown as { scene: Scene }).scene;
const paneOf = (h: History, i = info) => new DataPane(i, h, document.createElement("div"), darkTheme, events as never);

/** What the scene shows, by element id (slots differ between a history and a fresh load), and how many nodes a search matches. */
function shown(p: DataPane, h: History) {
  const s = scene(p), g = h.graph;
  const nodes = Object.fromEntries([...g.nodeSlot].map(([id, slot]) => [id, [s.nodeVisible(slot), s.nodeVisible(slot) ? s.nodeExtra(slot) : 0]]));
  const stubs = Object.fromEntries([...g.stubSlot].map(([id, slot]) => [id, [s.nodeVisible(slot), s.nodeVisible(slot) ? s.nodeExtra(slot) : 0]]));
  const edges = Object.fromEntries([...g.edgeSlot].map(([id, slot]) => [id, s.edgeVisible(slot)]));
  return { nodes, stubs, edges, matches: p.matchCount };
}

/** Every slot of the scene, as the renderer reads it. */
function slots(p: DataPane, h: History) {
  const s = scene(p), g = h.graph;
  const nodes = Array.from({ length: g.nodes.length }, (_, i) => (s.nodeVisible(i) ? [s.nodeShape(i), s.nodeState(i), s.nodeExtra(i), s.nodeColor(i)] : null));
  const edges = Array.from({ length: g.edges.length }, (_, i) => (s.edgeVisible(i) ? [s.edgeStyle(i), s.edgeState(i)] : null));
  return { nodes, edges };
}

const A = ent("a", "Pod"), B = ent("b", "Pod"), C = ent("c", "Node"), R = rel("r", "a", "b", "Calls");
const start: Snapshot = { version: ver("1"), entities: [A, B, C], relationships: [R] };

/** Each case is a list of patches (each a list of edits) applied one after the other. */
const OPS: [string, Edit[][]][] = [
  ["a node is added", [[addN(ent("d", "Pod"))]]],
  ["a node and an edge to it are added", [[addN(ent("d", "Pod")), addE(rel("r2", "a", "d", "Calls"))]]],
  ["an edge is added between existing nodes", [[addE(rel("r2", "b", "c", "Calls"))]]],
  ["an edge names an entity the graph does not hold (a stub)", [[addE(rel("r2", "a", "missing", "Calls"))]]],
  ["a node with edges is removed (its slot becomes a stub)", [[delN(B)]]],
  ["a node without edges is removed", [[delN(C)]]],
  ["an edge is removed", [[delE(R)]]],
  ["a node changes type", [[setN(C, ent("c", "Pod"))]]],
  ["two operations in one patch: a node leaves and another arrives", [[delN(C), addN(ent("d", "Pod"))]]],
  ["two operations in one patch: an edge and its endpoint leave", [[delE(R), delN(B)]]],
  ["two operations in one patch: a node arrives with an edge while another changes type", [[addN(ent("d", "Pod")), addE(rel("r2", "d", "c", "Calls")), setN(C, ent("c", "Pod"))]]],
  ["a freed node slot is reused by the next patch", [[delN(C)], [addN(ent("d", "Node"))]]],
  ["a freed node slot is reused by a node with an edge", [[delN(C)], [addN(ent("d", "Pod")), addE(rel("r2", "a", "d", "Calls"))]]],
  ["a freed edge slot is reused by the next patch", [[delE(R)], [addE(rel("r3", "b", "a", "Calls"))]]],
  ["a stub is replaced by the entity it stood for", [[addE(rel("r2", "a", "m", "Calls"))], [addN(ent("m", "Pod"))]]],
  ["a node with edges leaves, then comes back", [[delN(B)], [addN(B)]]],
];
const FILTERS: [string, (p: DataPane) => void][] = [
  ["no filter", () => {}],
  ["a hidden type", (p) => p.setView({ filter: { ...emptyFilter(), hiddenTypes: new Set(["Node"]) } })],
  ["a search", (p) => p.setView({ filter: { ...emptyFilter(), search: "d" } })],
];

describe("a pane after a patch draws what a fresh pane draws from the same graph", () => {
  it.each(OPS.flatMap(([op, edits]) => FILTERS.map(([filter, apply]) => [op, filter, edits, apply] as const)))("%s, with %s", (_op, _filter, edits, apply) => {
    const h = new History(start);
    const p = paneOf(h);
    p.refreshAll();
    apply(p);
    edits.forEach((one, n) => {
      const step = h.append(patch(String(n + 1), String(n + 2), one));
      expect(step, `patch ${n + 1} applies`).not.toBeNull();
      p.applyStep(step!);
    });

    const fresh = new History(h.graph.toSnapshot());
    const q = paneOf(fresh);
    q.refreshAll();
    apply(q);
    expect(shown(p, h)).toEqual(shown(q, fresh));
    // and slot by slot: the same pane drawing everything again from its graph changes nothing
    const incremental = slots(p, h);
    p.refreshAll();
    expect(incremental).toEqual(slots(p, h));
  });

  it("a patch that brings the first edges of a kind leaves a support edge out, as a full pass does", () => {
    const kinded = { ...info, edgeKinds: { Calls: "communication", Measures: "support" } } as unknown as GraphInfo;
    const h = new History({ version: ver("1"), entities: [ent("a"), ent("b")], relationships: [rel("r", "a", "b", "Owns")] });
    const p = paneOf(h, kinded); p.refreshAll();
    p.applyStep(h.append(patch("1", "2", [addN(ent("s")), addE(rel("m", "s", "a", "Measures")), addE(rel("c", "a", "b", "Calls"))]))!);
    expect([...p.hiddenKinds()], "precondition: the default now leaves out support").toEqual(["support"]);
    expect(scene(p).edgeVisible(h.graph.edgeSlot.get("m")!), "the incremental path drew it by the kinds of the last full pass").toBe(false);
  });

  it("a hover keeps a stub (an endpoint the graph does not hold) drawn as a reference", () => {
    const h = new History({ version: ver("1"), entities: [ent("a")], relationships: [rel("r", "a", "missing")] });
    const p = paneOf(h); p.refreshAll();
    const slot = h.graph.stubSlot.get("missing")!;
    expect(scene(p).nodeExtra(slot) & Ex.Reference, "precondition: drawn as a reference before the hover").toBeTruthy();
    (p as unknown as { onHover(i: unknown): void }).onHover({ kind: "node", slot });
    expect(scene(p).nodeExtra(slot) & Ex.Reference, "the hover rewrote the flags without the reference bit").toBeTruthy();
  });

  it("the selection does not stay on a slot that a removed element left and another took", () => {
    const h = new History({ version: ver("1"), entities: [ent("a"), ent("b")], relationships: [] });
    const p = paneOf(h); p.refreshAll();
    p.setSelection({ kind: "entity", graph: "g", id: "a" });
    const slot = h.graph.nodeSlot.get("a")!;
    p.applyStep(h.append(patch("1", "2", [delN(ent("a"))]))!);
    p.applyStep(h.append(patch("2", "3", [addN(ent("c"))]))!);
    expect(h.graph.nodeSlot.get("c"), "precondition: c took the slot a left").toBe(slot);
    const selected = (p as unknown as { nodeFlags: { members_(bit: number): Set<number> } }).nodeFlags.members_(St.Selected);
    expect([...selected], "c is drawn selected").toEqual([]);
  });
});

const files = import.meta.glob("/public/demo/estate/*.{json,ndjson,md}", { query: "?raw", import: "default", eager: true }) as Record<string, string>;
const fetcher = async (url: string) => { const t = files[`/public${new URL(url).pathname}`]; if (t === undefined) throw new Error(url); return t; };
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
  const pane = paneOf(h, info);
  pane.start();
  await flush();
  return pane;
}

// The layout of a revision is a function of that revision and the pane: however the person got to revision r (in order, backwards, jumping, from
// a pane made on the empty first revision), every node stands where a pane opened fresh at r puts it.
describe("a pane after seeking lays out each revision of the workload as a fresh pane at that revision", () => {
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
    const pane = paneOf(h, info);
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

  // A seek over many revisions is one batch (`applySteps`): the full refresh, the legend and the layout decision run once. The scene it leaves is
  // the one that applying the steps one at a time leaves, with and without the full refresh (a tile hides the supporting kinds, which takes it).
  it.each([["the view of the graph", false], ["a tile of the space", true]])("a seek in one batch, in %s, leaves the scene the steps one at a time leave", async (_, tile) => {
    const { info, history } = await load("workload");
    const panes = [history(), history()].map((h) => { h.seek(0); const p = paneOf(h, info); p.setView({ tile }); p.start(); return { h, p }; });
    await flush();
    const [batch, single] = panes;
    batch.p.applySteps(batch.h.seek(batch.h.length));
    for (const s of single.h.seek(single.h.length)) single.p.applyStep(s);
    await flush();
    const scene = (p: DataPane) => ({ nodes: Array.from(p.scene.nodeU32), edges: Array.from(p.scene.edgeU32), nodeCount: p.scene.nodeCount, edgeCount: p.scene.edgeCount, positions: positions(p) });
    expect(scene(batch.p)).toEqual(scene(single.p));
  });
});
