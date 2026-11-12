// The tree of a graph with a roll-up answers where a problem starts: by default it nests along the relation the state was computed from, marks
// the origins (own state as bad as the rolled-up one) and opens the paths to them; an element reached by two paths is under both. Read on the
// demo workload: the cache overloaded (revision 7), the database server down and one pod crash-looping (revision 10).
import { describe, expect, it } from "vitest";
import { History } from "../src/app/history";
import { blamePath } from "../src/app/edge-kinds";
import { defaultOpen, treeOf, visibleRows, type TreeModel } from "../src/app/tree-model";
import type { Patch } from "../src/core/model";
import { StaticSource } from "../src/source/static";

const files = import.meta.glob("/public/demo/estate/*.{json,ndjson,md}", { query: "?raw", import: "default", eager: true }) as Record<string, string>;
const fetcher = async (url: string) => { const t = files[`/public${new URL(url).pathname}`]; if (t === undefined) throw new Error(url); return t; };

async function load(graph: string) {
  const src = new StaticSource("t", "http://localhost/demo/estate/manifest.json", fetcher);
  const info = (await src.open()).graphs.find((g) => g.id === graph)!;
  const h = new History(await src.snapshot(graph));
  const patches: Patch[] = await new Promise((resolve) => { const out: Patch[] = []; src.subscribe(graph, (e) => { if (e.type === "patch") out.push(e.patch); if (e.type === "end") resolve(out); }); });
  for (const p of patches) h.append(p);
  return { info, h };
}

/** the rows the tree opens on, each as the labels of its path from the root, with its state, and a mark for an origin */
const opened = (m: TreeModel): string[] => visibleRows(m, { expanded: defaultOpen(m, 40), onlyWrong: false, query: "", sort: "severity" }).rows
  .map((r) => `${r.key.split("\u001f").map((id) => m.nodes.get(id)!.label).join(" > ")} ${r.node.rolled?.value}${r.node.origin ? " origin" : ""}`);
const byLabel = (m: TreeModel, label: string, type?: string) => [...m.nodes.values()].find((n) => n.label === label && (!type || n.type === type))!;
const labels = (m: TreeModel, ids: Iterable<string>) => [...ids].map((id) => m.nodes.get(id)!.label).sort();

describe("the workload tree by cause", () => {
  it("is the default, for the tile and the Tree tab alike", async () => {
    const { info, h } = await load("workload");
    expect(treeOf(h.graph, info)?.choice).toBe("cause");
  });

  it("the cache overloaded (revision 7): the cache is the origin, the web and the shop are degraded through it, the api is not", async () => {
    const { info, h } = await load("workload");
    h.seek(7);
    const m = treeOf(h.graph, info)!.model;
    expect(labels(m, [...m.nodes.values()].filter((n) => n.origin).map((n) => n.id))).toEqual(["shopcache"]);
    expect(labels(m, byLabel(m, "shop").through)).toEqual(["web"]);
    expect(labels(m, byLabel(m, "web", "service").through)).toEqual(["shopcache"]);
    expect(byLabel(m, "api", "service").rolled?.value).toBe("Healthy");
    expect(opened(m)).toContain("shop > web > shopcache Degraded origin");
  });

  it("the database server down (revision 10): the server is the origin under the database, under everything that needs it; the pod is a second origin under the web", async () => {
    const { info, h } = await load("workload");
    h.seek(10);
    const m = treeOf(h.graph, info)!.model;
    expect(labels(m, [...m.nodes.values()].filter((n) => n.origin).map((n) => n.id))).toEqual(["pg-shop", "web-7d4-3"]);
    expect(labels(m, byLabel(m, "database").through)).toEqual(["pg-shop"]);
    expect(labels(m, byLabel(m, "api", "service").through)).toEqual(["database"]);
    // the web needs the api, which needs the database: it is unhealthy through the api; its crash-looping pod does not make it so
    expect(labels(m, byLabel(m, "web", "service").through)).toEqual(["api"]);
    // reached by two paths and more, under each, the same element
    expect([byLabel(m, "database").parents.length, byLabel(m, "api", "service").parents.length]).toEqual([2, 3]);
    expect(opened(m)).toEqual(expect.arrayContaining([
      "shop > database > pg-shop Unhealthy origin",
      "shop > api > database > pg-shop Unhealthy origin",
      "shop > web > api > database > pg-shop Unhealthy origin",
      "shop > web > web > web-7d4-3 Unhealthy origin",
      "apim-shop > api > database > pg-shop Unhealthy origin",
    ]));
  });

  it("the graph's flag marks the same elements as the tree's origins, at every revision", async () => {
    const { info, h } = await load("workload");
    for (let r = 0; r <= h.length; r++) {
      h.seek(r);
      const m = treeOf(h.graph, info)!.model;
      expect([...blamePath(h.graph, info).blamed].sort(), `revision ${r}`).toEqual([...m.nodes.values()].filter((n) => n.origin).map((n) => n.id).sort());
    }
  });
});
