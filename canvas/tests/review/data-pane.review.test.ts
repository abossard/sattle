// @vitest-environment jsdom
// Defects of the code review in the data pane (src/app/data-pane.ts): the WebGL view and the layout worker are replaced; the scene is the real one.
import { describe, expect, it, vi } from "vitest";

vi.mock("../../src/render/view", () => ({
  GraphView: class {
    constructor() {
      const own: Record<string | symbol, unknown> = {};
      return new Proxy(own, { get: (t, k) => (k in t ? t[k] : () => ({ x: 0, y: 0 })), set: (t, k, v) => { t[k] = v; return true; } });
    }
  },
}));
vi.mock("../../src/layout/client", () => ({ LayoutClient: class { lastMs = 0; run() { return { done: new Promise(() => {}) }; } cancel() {} dispose() {} } }));

import { DataPane } from "../../src/app/data-pane";
import { History } from "../../src/app/history";
import { Ex } from "../../src/render/scene";
import { darkTheme } from "../../src/render/theme";
import type { GraphInfo } from "../../src/source/types";
import { addE, addN, ent, patch, rel, ver } from "../helpers";

const info = { id: "g", role: "master", descriptor: {}, ontology: { types: [] }, inputs: [] } as unknown as GraphInfo;
const events = { select() {}, hover() {}, activate() {}, changed() {}, announce() {}, key: () => false };
const pane = (h: History) => { const p = new DataPane(info, h, document.createElement("div"), darkTheme, events as never); p.refreshAll(); return p; };
const scene = (p: DataPane) => (p as unknown as { scene: import("../../src/render/scene").Scene }).scene;

describe("data-pane.ts:328 (:201) a patch that grows the graph", () => {
  it("draws the new node and its edge", () => {
    const h = new History({ version: ver("1"), entities: [ent("a"), ent("b")], relationships: [rel("r", "a", "b")] });
    const p = pane(h);
    const step = h.append(patch("1", "2", [addN(ent("c")), addE(rel("r2", "b", "c"))]))!;
    p.applyStep(step);
    const slot = h.graph.nodeSlot.get("c")!;
    expect(scene(p).nodeVisible(slot), "the visibility array was sized at load: the write for the new slot is lost").toBe(true);
    expect(scene(p).edgeVisible(h.graph.edgeSlot.get("r2")!), "its edge is not drawn either").toBe(true);
  });

  it("search finds the new node", () => {
    const h = new History({ version: ver("1"), entities: [ent("a"), ent("b")], relationships: [] });
    const p = pane(h);
    p.setFilter({ hiddenTypes: new Set(), prop: null, search: "zebra" });
    p.applyStep(h.append(patch("1", "2", [addN(ent("zebra"))]))!);
    expect([...((p as unknown as { matches: Set<number> }).matches)].length, "search skips nodes the pane never showed").toBe(1);
  });
});

describe("data-pane.ts:417 flag writes drop the reference bit", () => {
  it("a stub (an endpoint the graph does not hold) is still drawn as a reference after a hover", () => {
    const h = new History({ version: ver("1"), entities: [ent("a")], relationships: [rel("r", "a", "missing")] });
    const p = pane(h);
    const slot = h.graph.stubSlot.get("missing")!;
    expect(scene(p).nodeExtra(slot) & Ex.Reference, "precondition: drawn as a reference before the hover").toBeTruthy();
    (p as unknown as { onHover(i: unknown): void }).onHover({ kind: "node", slot });
    expect(scene(p).nodeExtra(slot) & Ex.Reference, "the hover rewrote the flags without the reference bit").toBeTruthy();
  });
});
