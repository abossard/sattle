import { describe, expect, it } from "vitest";
import { StaticSource } from "../src/source/static";
import { SimulatedStream } from "../src/source/simulated";
import { Graph } from "../src/core/graph";
import type { Patch } from "../src/core/model";
import type { Rejection } from "../src/core/validate";
import { checkTelemetryLine, parseTelemetryNdjson } from "../src/source/telemetry-file";
import { TelemetryStore } from "../src/core/telemetry";

const files = import.meta.glob("/public/**/*.{json,ndjson,md}", { query: "?raw", import: "default", eager: true }) as Record<string, string>;
const fetcher = (overrides: Record<string, string> = {}) => async (url: string) => {
  const path = new URL(url).pathname;
  const text = overrides[path] ?? files[`/public${path}`];
  if (text === undefined) throw new Error(`no such file ${path}`);
  return text;
};
const patchesOf = (src: StaticSource, graph: string): Promise<Patch[]> => new Promise((resolve) => {
  const out: Patch[] = [];
  src.subscribe(graph, (e) => { if (e.type === "patch") out.push(e.patch); else if (e.type === "end") resolve(out); });
});

const url = "http://localhost/demo/estate/manifest.json";
const GRAPHS = ["master", "deploymentHealth", "deploymentReport", "serviceTree", "capabilities", "healthSignals", "healthTopology", "healthState", "health"];

describe("StaticSource: the engine's output on the estate fixture", () => {
  it("loads every graph without rejections and its patches apply in order", async () => {
    const src = new StaticSource("t", url, fetcher());
    const rejected: Rejection[] = [];
    src.onRejected((r) => rejected.push(r));
    const info = await src.open();
    expect(info.graphs.map((g) => g.id).sort()).toEqual([...GRAPHS].sort());
    expect(rejected).toEqual([]);
    for (const g of info.graphs) {
      const graph = Graph.fromSnapshot(await src.snapshot(g.id));
      for (const p of await patchesOf(src, g.id)) expect(graph.apply(p).ok, `${g.id} ${p.from.revision}`).toBe(true);
    }
  });
  it("every graph that should have edges has them once its patches are applied", async () => {
    const src = new StaticSource("t", url, fetcher());
    await src.open();
    const edges: Record<string, number> = {};
    for (const id of GRAPHS) {
      const graph = Graph.fromSnapshot(await src.snapshot(id));
      for (const p of await patchesOf(src, id)) graph.apply(p);
      edges[id] = graph.edgeCount;
    }
    expect(Object.entries(edges).filter(([id, n]) => n === 0 && !["deploymentReport", "healthSignals"].includes(id))).toEqual([]);
  });
});

describe("the estate space: inputs, lineage in both directions, documents", () => {
  const open = async () => { const src = new StaticSource("t", url, fetcher()); const info = await src.open(); return { src, info }; };
  it("capabilities read the service tree and the master", async () => {
    const { info } = await open();
    expect(info.graphs.find((g) => g.id === "capabilities")!.inputs.sort()).toEqual(["master", "serviceTree"]);
  });
  it("a lifted edge names the lower edge and nodes it stands for, and the master node names what it is part of", async () => {
    const { src } = await open();
    const graph = Graph.fromSnapshot(await src.snapshot("capabilities"));
    for (const p of await patchesOf(src, "capabilities")) graph.apply(p);
    const lifted = [...graph.relationships()].filter((r) => r.type.endsWith("DependsOn"));
    expect(lifted.length).toBeGreaterThan(0);
    const l = (await src.lineage("capabilities", lifted[0].id))!;
    expect(l.complete).toBe(true);
    expect(new Set(l.sources.map((s) => `${s.graph}:${s.kind}`))).toEqual(new Set(["serviceTree:entity", "serviceTree:relationship", "master:entity"]));
    const lower = l.sources.find((s) => s.graph === "serviceTree" && s.kind === "relationship")!;
    const lowerLineage = (await src.lineage("serviceTree", lower.element))!;
    expect(lowerLineage.sources.some((s) => s.graph === "master" && s.kind === "relationship")).toBe(true);
    const reverse = (await src.reverseLineage("serviceTree", lower.element))!;
    expect(reverse.partOf.some((p) => p.graph === "capabilities" && p.element === lifted[0].id)).toBe(true);
    expect(await src.lineage("capabilities", "e:nothing")).toBeNull();
  });
  it("the report is a document at its head only", async () => {
    const { src } = await open();
    const doc = (await src.document("deploymentReport"))!;
    expect([doc.markdown.startsWith("# "), doc.digest.startsWith("sha256:"), Number(doc.revision) > 0]).toEqual([true, true, true]);
    expect(await src.document("master")).toBeNull();
    await expect(src.document("deploymentReport", "0")).rejects.toThrow(/at revision/);
  });
});

describe("the estate output: telemetry and references", () => {
  it("serves the declared meters of a graph, with their ranges and the warning threshold", async () => {
    const src = new StaticSource("t", url, fetcher());
    const m = (await src.meters("serviceTree"))!;
    const drop = m.meters.find((x) => x.id === "dropRatio")!;
    expect([drop.target, drop.warn, drop.scale]).toEqual(["edge", 0.05, "sqrt"]);
    expect((await src.meters("capabilities"))!.meters.map((x) => x.id).sort()).toEqual(["callDropRatio", "callDrops", "callFlows"]);
  });
  it("delivers the telemetry windows after the patches and before the end, for elements that exist in the graph", async () => {
    const src = new StaticSource("t", url, fetcher());
    await src.open();
    const events: string[] = [];
    const ids = new Set<string>();
    await new Promise<void>((resolve) => src.subscribe("serviceTree", (e) => {
      events.push(e.type);
      if (e.type === "telemetry") for (const id of e.batch.ids ?? []) ids.add(id);
      if (e.type === "end") resolve();
    }));
    const firstTelemetry = events.indexOf("telemetry");
    expect([firstTelemetry > events.lastIndexOf("patch"), events[events.length - 1]]).toEqual([true, "end"]);
    const graph = Graph.fromSnapshot(await src.snapshot("serviceTree"));
    for (const p of await patchesOf(src, "serviceTree")) graph.apply(p);
    expect([...ids].every((id) => graph.nodeSlot.has(id) || graph.edgeSlot.has(id))).toBe(true);
  });
  it("reads the references of a graph from the file the manifest names, empty when the graph has none", async () => {
    const entry = { id: { value: "e:x" }, state: "absent", graph: null, entityType: "Pod", identity: { domain: "d", key: { name: "web" } }, uses: [{ relationshipType: "Calls", end: "target", count: 2 }] };
    const src = new StaticSource("t", url, fetcher({ "/demo/estate/master.references.ndjson": JSON.stringify(entry) + "\n" }));
    expect((await src.references("master")).map((r) => [r.id, r.state, r.entityType])).toEqual([["e:x", "absent", "Pod"]]);
    expect(await new StaticSource("t", url, fetcher()).references("health")).toEqual([]);
  });
});

describe("rejections are reported, never silent", () => {
  const base = JSON.parse(files["/public/demo/estate/master.patches.json"]);
  const cases: [string, Record<string, string>, string][] = [
    ["invalid patch", { "/demo/estate/master.patches.json": JSON.stringify([{ ...base[0], unexpected: true }, ...base.slice(1)]) }, "master.patches.json"],
    ["invalid entity in snapshot", { "/demo/estate/master.snapshot.json": JSON.stringify((() => { const s = JSON.parse(files["/public/demo/estate/master.snapshot.json"]); s.extent.entities = [{ type: {} }]; return s; })()) }, "entity #0"],
    ["malformed ontology", { "/demo/estate/health.ontology.json": JSON.stringify({ nope: 1 }) }, "ontology"],
  ];
  it.each(cases)("%s", async (_n, overrides, fragment) => {
    const src = new StaticSource("t", url, fetcher(overrides));
    const rejected: Rejection[] = [];
    src.onRejected((r) => rejected.push(r));
    await src.open().catch(() => {});
    await patchesOf(src, "master").catch(() => []);
    expect(rejected.length).toBeGreaterThan(0);
    expect(rejected.map((r) => `${r.source} ${r.where}`).join("\n")).toContain(fragment);
    expect(rejected.every((r) => r.errors.length > 0)).toBe(true);
  });
});

describe("SimulatedStream", () => {
  it("delivers patches only under control and counts them", async () => {
    const inner = new StaticSource("t", url, fetcher());
    const sim = new SimulatedStream(inner);
    await sim.open();
    const got: string[] = [];
    sim.subscribe("master", (e) => { if (e.type === "patch") got.push(e.patch.to.revision); });
    await new Promise((r) => setTimeout(r, 20));
    expect(got).toEqual([]);
    expect(sim.total).toBeGreaterThan(5);
    sim.step(); sim.step();
    const first = JSON.parse(files["/public/demo/estate/master.patches.json"]).slice(0, 2).map((p: { to: { revision: { value: string } } }) => p.to.revision.value);
    expect(got).toEqual(first);
    expect(sim.state).toBe("paused");
    sim.reset();
    expect(sim.delivered).toBe(0);
    expect(sim.state).toBe("idle");
  });
  it("lineage, reverse lineage and documents pass through to the source it plays", async () => {
    const sim = new SimulatedStream(new StaticSource("t", url, fetcher()));
    await sim.open();
    expect((await sim.document("deploymentReport"))?.markdown).toBeTruthy();
    expect(await sim.lineage("master", "e:nothing")).toBeNull();
    expect(await sim.reverseLineage("master", "e:nothing")).toBeNull();
  });
});

describe("telemetry through the seam", () => {
  it.each([
    [{ graph: "g", tick: 1, t: 1, target: "edge", property: "p", ids: ["a"], values: [1] }, true],
    [{ graph: "g", tick: -1, t: 1, target: "edge", property: "p", ids: ["a"], values: [1] }, false],
    [{ graph: "g", tick: 1, t: 1, target: "wire", property: "p", ids: ["a"], values: [1] }, false],
    [{ graph: "g", tick: 1, t: 1, target: "node", property: "p", ids: ["a", "b"], values: [1] }, false],
    [{ graph: "g", tick: 1, t: 1, target: "node", property: "p", ids: ["a"], values: ["x"] }, false],
    [null, false],
  ])("checkTelemetryLine %j", (line, ok) => expect(checkTelemetryLine(line).ok).toBe(ok));
  it("rejected telemetry lines are reported with their line number", () => {
    const rejected: Rejection[] = [];
    const batches = parseTelemetryNdjson('{"graph":"g","tick":0,"t":0,"target":"node","property":"cpu","ids":["a"],"values":[1]}\nnot json\n{"graph":"g"}\n', "f.ndjson", (r) => rejected.push(r));
    expect(batches.length).toBe(1);
    expect(rejected.map((r) => r.where)).toEqual(["line 2", "line 3"]);
  });
});
