// @vitest-environment jsdom
// How a pane lays out its graph: a graph whose ontology declares a hierarchy is a tree (root on top, parent ranked above child), whatever its size.
import { describe, expect, it, vi } from "vitest";

const requests: { kind: string; src: Int32Array; dst: Int32Array }[] = [];
vi.mock("../src/render/view", () => ({
  GraphView: class {
    constructor() {
      const own: Record<string | symbol, unknown> = {};
      return new Proxy(own, { get: (t, k) => (k in t ? t[k] : () => ({ x: 0, y: 0 })), set: (t, k, v) => { t[k] = v; return true; } });
    }
  },
}));
vi.mock("../src/layout/client", () => ({ LayoutClient: class { lastMs = 0; run(req: { kind: string; src: Int32Array; dst: Int32Array }) { requests.push(req); return { done: new Promise(() => {}) }; } cancel() {} dispose() {} } }));

import { DataPane } from "../src/app/data-pane";
import { History } from "../src/app/history";
import { hasTreeRows } from "../src/app/tree-model";
import { darkTheme } from "../src/render/theme";
import { StaticSource } from "../src/source/static";
import type { GraphInfo } from "../src/source/types";
import type { TreeSpec } from "../src/source/presentation";
import { ent, rel, ver } from "./helpers";
import { ont } from "./fixtures/engine-fake";

const events = { select() {}, hover() {}, activate() {}, changed() {}, announce() {}, key: () => false };
const paneOf = (info: GraphInfo, h: History) => new DataPane(info, h, document.createElement("div"), darkTheme, events as never);
const spec = (childIsSource: boolean): TreeSpec => ({ hierarchy: { relationship: "Contains", childIsSource }, edgeRoles: { Evidence: "support", DependsOn: "cross" }, state: {}, types: ["Node"] });
const info = (tree?: TreeSpec): GraphInfo => ({ id: "g", role: "projection", descriptor: {}, ontology: ont("o"), inputs: [], tree } as unknown as GraphInfo);
const world = (childIsSource = true) => new History({
  version: ver("1"),
  entities: [ent("p", "Node"), ent("c", "Node"), ent("s", "Sig")],
  relationships: [childIsSource ? rel("r0", "c", "p", "Contains") : rel("r0", "p", "c", "Contains"), rel("r1", "s", "p", "Evidence"), rel("r2", "c", "p", "DependsOn")],
});

describe("a graph with a declared hierarchy", () => {
  it.each([[true, "the child is the edge's source"], [false, "the parent is the edge's source"]])("is laid out in layers, the parent above the child, what explains a row below it, dependencies not ranking (%s: %s)", (childIsSource) => {
    requests.length = 0;
    const pane = paneOf(info(spec(childIsSource)), world(childIsSource));
    expect(pane.layoutKind).toBe("layered");
    pane.start();
    const r = requests.at(-1)!;
    expect(r.kind).toBe("layered");
    // dense slots: p 0, c 1, s 2
    expect([[r.src[0], r.dst[0]], [r.src[1], r.dst[1]], [r.src[2], r.dst[2]]]).toEqual([[0, 1], [0, 2], [-1, -1]]);
  });

  it("a graph without one is not, however few its nodes", () => {
    expect(paneOf(info(), new History({ version: ver("1"), entities: [ent("a"), ent("b"), ent("c")], relationships: [rel("r", "a", "b")] })).layoutKind).toBe("force");
  });
});

const files = import.meta.glob("/public/demo/estate/*.{json,ndjson,md}", { query: "?raw", import: "default", eager: true }) as Record<string, string>;
const fetcher = async (url: string) => { const t = files[`/public${new URL(url).pathname}`]; if (t === undefined) throw new Error(url); return t; };

describe("the graphs of the default source", () => {
  it("every graph that declares a hierarchy and has rows in it is laid out as a tree", async () => {
    const src = new StaticSource("t", "http://localhost/demo/estate/manifest.json", fetcher);
    const opened = await src.open();
    const trees = opened.graphs.filter((g) => g.tree);
    expect(trees.length, "the default source has graphs that declare a hierarchy").toBeGreaterThan(0);
    for (const g of opened.graphs) {
      const h = new History(await src.snapshot(g.id));
      if (g.tree && hasTreeRows(h.graph, g.tree)) expect(paneOf(g, h).layoutKind, g.id).toBe("layered");
    }
  });
});
