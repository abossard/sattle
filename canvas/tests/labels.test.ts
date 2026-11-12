// @vitest-environment jsdom
// Node labels: none over another label or over a node, none cut off at the edge of the pane, and short in a tile.
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
import { History } from "../src/app/history";
import { LabelPlacer, keepInside } from "../src/render/labels";
import { darkTheme } from "../src/render/theme";
import type { GraphInfo } from "../src/source/types";
import { ont } from "./fixtures/engine-fake";
import { ent, ver } from "./helpers";

describe("where a label may go", () => {
  it.each<[string, [number, number, number, number], boolean]>([
    ["on a label that is there", [100, 100, 60, 14], false],
    ["overlapping its right end", [150, 100, 60, 14], false],
    ["beside it", [161, 100, 60, 14], true],
    ["a line below", [100, 115, 60, 14], true],
    ["far away, in another cell", [900, 700, 60, 14], true],
  ])("%s", (_, box, free) => {
    const p = new LabelPlacer();
    expect(p.place(100, 100, 60, 14)).toBe(true);
    expect(p.place(...box)).toBe(free);
  });
  it("a node takes its box: no label is placed over it, and a box that spans cells is found from each of them", () => {
    const p = new LabelPlacer(64);
    p.take(64, 64, 40, 40);                               // a node across four cells
    expect([p.free(50, 50, 10, 10), p.free(80, 80, 10, 10), p.free(120, 64, 20, 14)]).toEqual([false, false, true]);
  });
  it("a label that was refused takes nothing", () => {
    const p = new LabelPlacer();
    p.place(0, 0, 50, 14);
    expect(p.place(10, 0, 50, 14)).toBe(false);
    expect(p.place(60, 0, 40, 14)).toBe(true);            // free: the refused one left no box
  });
});

describe("a label at the edge of the pane", () => {
  it.each<[string, number, number, number, number]>([
    ["in the middle stays", 200, 80, 400, 200],
    ["at the left edge is moved in", 10, 80, 400, 42],
    ["at the right edge is moved in", 395, 80, 400, 358],
    ["wider than the pane is centred", 10, 500, 400, 200],
  ])("%s", (_, x, w, pane, want) => expect(keepInside(x, w, pane)).toBe(want));
});

describe("the label of a node with a colored state", () => {
  const info = { id: "g", role: "projection", descriptor: {}, ontology: ont("o"), inputs: [], presentation: [{ property: "state", roles: { Healthy: "ok", Degraded: "warning", Plain: "neutral" } }] } as unknown as GraphInfo;
  const events = { select() {}, hover() {}, activate() {}, changed() {}, announce() {}, key: () => false };
  const pane = (tile: boolean) => {
    const h = new History({ version: ver("1"), entities: [ent("a", "Pod", { name: "api", state: "Degraded" }), ent("b", "Pod", { name: "web", state: "Healthy" }), ent("c", "Pod", { name: "db", state: "Plain" }), ent("d", "Pod", { name: "cache" })], relationships: [] });
    const p = new DataPane(info, h, document.createElement("div"), darkTheme, events as never);
    p.setColoring("node", { property: "state", hint: info.presentation![0] } as never);
    p.setTile(tile);
    p.refreshAll();
    return p.labelsShown();
  };
  it("in the view is the name and the glyph of the role, as in a tile (the value is in the Inspector)", () => expect(pane(false)).toEqual(["api ⚠", "web ✓", "db · Plain ○", "cache"]));
  it("in a tile is the name and the glyph of the role; a value without a role is still written out", () => expect(pane(true)).toEqual(["api ⚠", "web ✓", "db · Plain ○", "cache"]));
});
