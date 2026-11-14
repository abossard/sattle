// @vitest-environment jsdom
// By example (note 11, stage c): picking a pod, a service and its Backs edge in the master graph writes the graph-match of `backing` in the
// service tree, exactly as the shipped acme.svc.serviceDeps.kql has it; picking the edge alone brings its two ends.
// @ts-expect-error the project has no typings for node; the tests run in it
import { readFileSync } from "node:fs";
// @ts-expect-error as above
import { gunzipSync } from "node:zlib";
import { describe, expect, it } from "vitest";
import { History } from "../../src/app/history";
import type { Graph } from "../../src/core/graph";
import type { Patch } from "../../src/core/model";
import { matchOf } from "../../src/editor/kql-example";
import { StaticSource } from "../../src/source/static";

const files = import.meta.glob("/public/demo/estate/*.{json,ndjson,md}", { query: "?raw", import: "default", eager: true }) as Record<string, string>;
const fetcher = async (url: string) => { const t = files[`/public${new URL(url).pathname}`]; if (t === undefined) throw new Error(url); return t; };
const rec = JSON.parse(String(gunzipSync(readFileSync("tests/fixtures/kql.json.gz")))) as { files: { projection: string; text: string }[] };
const shipped = rec.files.find((f) => f.projection === "acme.svc.serviceDeps")!.text;
const backing = shipped.slice(shipped.indexOf("let backing = ") + "let backing = ".length, shipped.indexOf(";", shipped.indexOf("let backing = ")));

/** The master graph at its last revision: its snapshot with every patch applied. */
const master = async (): Promise<Graph> => {
  const src = new StaticSource("t", "http://localhost/demo/estate/manifest.json", fetcher);
  await src.open();
  const h = new History(await src.snapshot("master"));
  const patches: Patch[] = await new Promise((resolve) => { const out: Patch[] = []; src.subscribe("master", (e) => { if (e.type === "patch") out.push(e.patch); if (e.type === "end") resolve(out); }); });
  for (const p of patches) h.append(p);
  h.seek(h.length);
  return h.graph;
};

describe("a pattern by example", () => {
  it.each([
    ["the pod, the service and the Backs edge", (p: string, s: string, b: string) => [p, s, b]],
    ["the Backs edge alone", (_p: string, _s: string, b: string) => [b]],
    ["the edge first, then its ends", (p: string, s: string, b: string) => [b, p, s]],
  ])("picking %s writes the graph-match of backing", async (_what, picks) => {
    const g = await master();
    const b = [...g.relationships()].find((r) => r.type === "Backs")!;
    expect(b).toBeTruthy();
    expect(matchOf(g, picks(b.source, b.target, b.id), "master")).toBe(backing);
  });

  it("a node no edge touches says its type, and two of a kind get numbered names", async () => {
    const g = await master();
    const pods = [...g.entities()].filter((e) => e.type === "Pod").slice(0, 2).map((e) => e.id);
    expect(matchOf(g, pods, "master")).toBe('graph("master")\n    | graph-match (p), (p2) where labels(p) has "Pod" and labels(p2) has "Pod"\n      project pod = p.id, pod2 = p2.id');
  });

  it("nothing in the graph picked gives no pattern", async () => {
    expect(matchOf(await master(), ["d:nowhere"], "master")).toBeNull();
  });
});
