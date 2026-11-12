// @vitest-environment jsdom
// The scene a pane holds after patches equals the scene a fresh pane draws from the resulting graph: the incremental path and the full pass are one calculation.
import { describe, expect, it, vi } from "vitest";

vi.mock("../src/render/view", () => ({
  GraphView: class {
    constructor() {
      const own: Record<string | symbol, unknown> = {};
      return new Proxy(own, { get: (t, k) => (k in t ? t[k] : () => ({ x: 0, y: 0 })), set: (t, k, v) => { t[k] = v; return true; } });
    }
  },
}));
vi.mock("../src/layout/client", () => ({ LayoutClient: class { lastMs = 0; run() { return { done: new Promise(() => {}) }; } cancel() {} dispose() {} } }));

import { DataPane } from "../src/app/data-pane";
import { emptyFilter } from "../src/app/graph-view-model";
import { History } from "../src/app/history";
import type { Edit, Snapshot } from "../src/core/model";
import type { Scene } from "../src/render/scene";
import { darkTheme } from "../src/render/theme";
import type { GraphInfo } from "../src/source/types";
import { addE, addN, delE, delN, ent, patch, rel, setN, ver } from "./helpers";
import { ont } from "./fixtures/engine-fake";

const info = { id: "g", role: "master", descriptor: {}, ontology: ont("o"), inputs: [] } as unknown as GraphInfo;
const events = { select() {}, hover() {}, activate() {}, changed() {}, announce() {}, key: () => false };
const scene = (p: DataPane) => (p as unknown as { scene: Scene }).scene;
const paneOf = (h: History) => new DataPane(info, h, document.createElement("div"), darkTheme, events as never);

/** What the scene shows, by element id (slots differ between a history and a fresh load). */
function shown(p: DataPane, h: History) {
  const s = scene(p), g = h.graph;
  const nodes = Object.fromEntries([...g.nodeSlot].map(([id, slot]) => [id, [s.nodeVisible(slot), s.nodeVisible(slot) ? s.nodeExtra(slot) : 0]]));
  const stubs = Object.fromEntries([...g.stubSlot].map(([id, slot]) => [id, [s.nodeVisible(slot), s.nodeVisible(slot) ? s.nodeExtra(slot) : 0]]));
  const edges = Object.fromEntries([...g.edgeSlot].map(([id, slot]) => [id, s.edgeVisible(slot)]));
  return { nodes, stubs, edges };
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
  ["a hidden type", (p) => p.setFilter({ ...emptyFilter(), hiddenTypes: new Set(["Node"]) })],
  ["a search", (p) => p.setFilter({ ...emptyFilter(), search: "d" })],
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
});
