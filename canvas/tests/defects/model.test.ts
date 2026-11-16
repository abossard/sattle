// Defects of the code review (paper/research/07-code-review-findings.md, section 3 and the canvas addendum) that a person can hit and that need no browser:
// each test states what should be true; each failed for the reason the review gives until its fix. Every title names the finding.
import { describe, expect, it } from "vitest";
import { TelemetryHub } from "../../src/app/telemetry-hub";
import { buildTree, severityCompare } from "../../src/app/tree-model";
import { Graph } from "../../src/core/graph";
import { Catalog } from "../../src/core/catalog";
import { TelemetryStore } from "../../src/core/telemetry";
import { resolvePatchTimes } from "../../src/core/time-axis";
import { parsePalette } from "../../src/editor/palette";
import { contains, ent, text } from "../../src/editor/patches";
import { initialState, reduce } from "../../src/state/reducer";

/** the workspace's own maps, filled by hand where a test builds a space without a source */
const histories = (ws: unknown) => (ws as { histories: Map<string, unknown> }).histories;
const hubs = (ws: unknown) => (ws as { hubs: Map<string, unknown> }).hubs;
import type { Meter, TelemetryBatch, TelemetryFeed } from "../../src/source/types";
import { addN, ent as plain, patch, rel, ver } from "../helpers";
import { treeGraph, treeHints, treeSpec } from "../fixtures/tree-graph";

describe("tree-model.ts:51 severity mixes two scales", () => {
  // the engine's severity is -1 unknown, 0 healthy, 1 degraded, 2 unhealthy; an element without the property falls back to a role scale 1..5
  it("an Unknown node never sorts above an Unhealthy one, and is never the driver of a roll-up", () => {
    const g = treeGraph();
    // rc is Unknown and has no severity of its own: it is read from its role
    const rc = g.entity("rc")!;
    const { severity: _s, ...props } = rc.props;
    g.putEntity({ ...rc, props });
    const m = buildTree(g, treeSpec, treeHints);
    const order = ["ra", "rb", "rc"].map((id) => m.nodes.get(id)!).sort(severityCompare).map((n) => n.id);
    expect(order[0], "Region A is Unhealthy and must come first").toBe("ra");
    expect(order.indexOf("rc")).toBeGreaterThan(order.indexOf("rb"));
  });
});

describe("time-axis.ts:24 more patches than windows", () => {
  it("revisions of a graph without commit times are not placed at seconds 1..n against telemetry windows in epoch seconds", () => {
    const windows = [1_800_000_000, 1_800_000_060];
    const { times } = resolvePatchTimes([NaN, NaN, NaN, NaN, NaN], windows);
    expect(Math.min(...times), "the first revision lies before every telemetry window").toBeGreaterThanOrEqual(windows[0]);
  });
});

describe("core/telemetry.ts:67 a reused slot", () => {
  const cpu: Meter = { id: "cpu", target: "node", label: "CPU", unit: "", min: 0, max: 1 };
  const feed: TelemetryFeed = { meters: [cpu], dt: 1, ticks: 1, maxTicks: 1, fetch: async () => ({ batches: [] }), defaults: { node: { ring: "cpu", size: "cpu", state: "cpu" }, edge: { width: "", flow: "", state: "" } } };
  const dense = (t: number, v: number[]): TelemetryBatch => ({ graph: "g", tick: t, t, target: "node", property: "cpu", values: Float32Array.from(v) });
  const sparse = (t: number, ids: string[], v: number[]): TelemetryBatch => ({ graph: "g", tick: t, t, target: "node", property: "cpu", ids, values: Float32Array.from(v) });
  const world = () => Graph.fromSnapshot({ version: ver("1"), entities: [plain("a"), plain("b"), plain("c")], relationships: [] });

  it("a new element in the slot of a deleted one does not show the deleted element's value", () => {
    const g = world();
    const hub = new TelemetryHub(g, feed);
    hub.ingest(dense(0, [0.1, 0.2, 0.9]));                       // c is hot
    g.apply(patch("1", "2", [{ kind: "entity", id: "c", before: g.entity("c")!, after: null }, addN(plain("d"))]));
    hub.revalidate([g.nodeSlot.get("d")!], []);
    hub.ingest(sparse(1, ["a"], [0.3]));                          // a sparse window: d has no value in it
    const reading = hub.readings("node", g.nodeSlot.get("d")!).find((r) => r.meter.id === "cpu");
    expect(reading?.value, "d inherited c's 0.9 from the previous window").not.toBeCloseTo(0.9, 1);
  });

  it("an element that was never measured has no reading, rather than the minimum of the scale", () => {
    const g = world();
    const hub = new TelemetryHub(g, feed);
    hub.ingest(sparse(0, ["a"], [0.5]));
    expect(hub.readings("node", g.nodeSlot.get("b")!).find((r) => r.meter.id === "cpu"), "b has no data: it is unknown, not 0").toBeUndefined();
  });

  it("the canvas and the Inspector agree with the engine near the warning threshold", () => {
    const g = world();
    const warnMeter: Meter = { ...cpu, warn: 0.502 };
    const hub = new TelemetryHub(g, { ...feed, meters: [warnMeter] });
    hub.ingest(dense(0, [0.5, 0.1, 0.1]));                       // 0.5 is below the threshold 0.502: the engine says ok
    const slot = g.nodeSlot.get("a")!;
    const onGpu = hub.frame("node", 0 /* ring */)![slot] >= hub.warnByteOf("node");
    const inInspector = hub.readings("node", slot).find((r) => r.meter.id === "cpu")!.warn;
    expect({ gpu: onGpu, inspector: inInspector }, "8-bit quantisation decides on the GPU, the dequantised value in the Inspector").toEqual({ gpu: false, inspector: false });
  });
  void TelemetryStore;
});

describe("graph.ts:81-88 edges parked while the stub cap was reached", () => {
  it("an edge parked past the cap stays resolvable when a stub for the same endpoint is made later and the endpoint then arrives", () => {
    const g = new Graph();
    g.version = ver("1");
    const total = Graph.MAX_STUBS + 1;
    g.putEntity(plain("src"));
    for (let i = 0; i < total; i++) g.putRel(rel(`r${i}`, "src", `m${i}`, "Calls"));
    const last = `m${total - 1}`, parked = g.edgeSlot.get(`r${total - 1}`)!;
    g.dropRel("r0");                                              // a stub is freed: there is room again
    g.putRel(rel("late", "src", last, "Calls"));                  // a new edge to the same endpoint gets a stub
    expect(g.stubSlot.has(last)).toBe(true);
    g.putEntity(plain(last));                                     // the entity arrives and takes the stub's slot
    expect(g.edgeDst[parked], "the edge parked past the cap was forgotten").toBe(g.nodeSlot.get(last));
  });
  it("an edge that could not be drawn to a stub is resolved when its endpoint arrives", () => {
    const g = new Graph();
    g.version = ver("1");
    const total = Graph.MAX_STUBS + 1;
    g.putEntity(plain("src"));
    for (let i = 0; i < total; i++) g.putRel(rel(`r${i}`, "src", `m${i}`, "Calls"));
    expect(g.stubCount).toBe(Graph.MAX_STUBS);
    const last = `m${total - 1}`;
    expect(g.edgeDst[g.edgeSlot.get(`r${total - 1}`)!], "past the cap the edge is unresolved").toBe(-1);
    g.putEntity(plain(last));
    expect(g.edgeDst[g.edgeSlot.get(`r${total - 1}`)!], "the endpoint arrived: the edge must be drawn").toBe(g.nodeSlot.get(last));
  });
});

describe("state/reducer.ts:28 the rejected counter", () => {
  it("counts every rejection, not only the last 200", () => {
    let s = initialState();
    for (let i = 0; i < 250; i++) s = reduce(s, { type: "feed/rejected", rejection: { source: "x", where: `w${i}`, errors: ["e"] }, at: i });
    expect(s.rejectedTotal, "the header says Rejected: 200 after 250").toBe(250);
    expect(s.rejections.length, "the list itself stays bounded").toBeLessThanOrEqual(200);
  });
});

describe("workspace.ts:113 a live patch while the shared timeline is scrubbed back", () => {
  it("a graph that has nothing newer than the scrubbed time stays where the timeline says", async () => {
    const { Workspace } = await import("../../src/app/workspace");
    const { History } = await import("../../src/app/history");
    const { darkTheme } = await import("../../src/render/theme");
    const ws = new Workspace({} as never, darkTheme, () => {});
    (ws as unknown as { info: unknown }).info = { graphs: [{ id: "a", role: "master" }, { id: "b", role: "master" }] };
    const a = new History({ version: ver("1", "a"), entities: [], relationships: [] });
    const b = new History({ version: ver("1", "b"), entities: [], relationships: [] });
    histories(ws).set("a", a); histories(ws).set("b", b);
    a.append(patch("1", "2", [addN(plain("x"))]), 10);
    a.append(patch("2", "3", [addN(plain("y"))]), 20);
    ws.seek(10, {});                       // the user scrubs back to second 10: a shows its first patch, b its snapshot
    expect(a.cursor).toBe(1);
    ws.applyPatch("b", patch("1", "2", [addN(plain("z"))]), 30);   // live: b gets a patch from second 30
    expect(b.cursor, "b jumped to now while the timeline shows second 10").toBe(ws.cursorAt("b", 10));
  });
});

// Code review 2 (paper/research/12-code-review-2.md, defect 28), the canvas view.
describe("core/ontology-graph.ts:22 a relationship type with an empty source or target list", () => {
  it("is drawn from and to the node 'any type', as the engine reads an empty list as every type", async () => {
    const { ontologyGraph } = await import("../../src/core/ontology-graph");
    const { ont } = await import("../fixtures/engine-fake");
    const o = { ...ont("o"), relationshipTypes: [...ont("o").relationshipTypes, { id: { value: "Mentions" }, sourceTypes: [], targetTypes: [{ value: "Pod" }], properties: [] }, { id: { value: "Links" }, sourceTypes: [], targetTypes: [], properties: [] }] } as never;
    const g = ontologyGraph(o, false);
    const ends = (id: string) => String((g.entity(id)?.props.name as { value: string } | undefined)?.value);
    expect([...g.relationships()].map((r) => `${r.type}: ${ends(r.source)} -> ${ends(r.target)}`).sort(), "a type without a source or target list draws nothing").toEqual(["Links: any type -> any type", "Mentions: any type -> Pod"]);
  });
});

describe("editor/service.ts:43 an absent cell in the rows after an operator", () => {
  it("stays absent instead of becoming the text \"null\"", async () => {
    const { parseOperatorRows } = await import("../../src/editor/service");
    const [o] = parseOperatorRows([{ op: "o", columns: ["a", "b"], rows: "1", sample: [{ weight: "1", cells: { a: null, b: "null" } }] }]);
    expect(o.sample[0].cells, "an absent value reads like the text null").toEqual({ b: "null" });
  });
});

describe("tree-model.ts:228 the tree by cause with everything open", () => {
  it("has a row per element under each parent, not one per simple path (8 services that all depend on each other)", async () => {
    const { visibleRows } = await import("../../src/app/tree-model");
    const services = Array.from({ length: 8 }, (_, i) => `s${i}`);
    const node = (id: string, children: string[], parents: string[]) => ({ id, type: "Service", label: id, parent: parents[0] ?? null, parents, children, own: null, rolled: null, severity: 0, supporting: 0, cross: 0, curatedBy: null, critical: false, hidden: false, policy: null, counts: [], origin: false, through: [] });
    const nodes = new Map([["shop", node("shop", services, [])], ...services.map((s) => [s, node(s, services.filter((x) => x !== s), ["shop", ...services.filter((x) => x !== s)])] as const)]);
    const m = { nodes, roots: ["shop"], spec: treeSpec } as never;
    const { rows } = visibleRows(m, { expanded: new Set(nodes.keys()), onlyWrong: false, query: "", sort: "label" });
    // 8 under the shop, the first service shows its 7, the next one down its 6 and so on to s7, whose children are all on its path there; s7
    // opens at its next row (under s5) with the one child left: 1 + 8 + 7 + ... + 1 + 1 rows for 64 edges (109,601 rows before)
    expect(rows.length, `${rows.length} rows for ${nodes.size} elements`).toBe(1 + 8 + 7 + 6 + 5 + 4 + 3 + 2 + 1 + 1);
    // the same rows each time, and every element shows its children once
    expect(visibleRows(m, { expanded: new Set(nodes.keys()), onlyWrong: false, query: "", sort: "label" }).rows.map((r) => r.key)).toEqual(rows.map((r) => r.key));
    expect(rows.filter((r) => r.expanded).map((r) => r.node.id).sort()).toEqual([...nodes.keys()].sort());
    for (const r of rows.filter((x) => x.shownAbove)) expect(rows.find((x) => x.key === r.shownAbove)?.node.id).toBe(r.node.id);
  });
});

describe("core/catalog.ts:250 the ontology of a module in the editor, a relationship type with an empty source or target list", () => {
  it("is drawn from and to the node 'any type', as the ontology picture draws it", async () => {
    const { Catalog, moduleOntologyGraph } = await import("../../src/core/catalog");
    const { coalesce } = await import("../../src/editor/patches");
    const m = "d:m", pod = "d:m/entity:Pod", links = "d:m/rel:Links", mentions = "d:m/rel:Mentions";
    const edits = coalesce([ent(m, "Module", { name: text("m") }), ent(pod, "EntityType", { name: text("Pod") }), ent(links, "RelationshipType", { name: text("Links") }), ent(mentions, "RelationshipType", { name: text("Mentions") }),
      contains(m, pod, "entity"), contains(m, links, "rel"), contains(m, mentions, "rel"), { kind: "relationship", id: `${mentions}|targetType|${pod}`, before: null, after: { id: `${mentions}|targetType|${pod}`, source: mentions, target: pod, type: "targetType", props: {} } } as never]);
    const g = Graph.fromSnapshot({ version: ver("1", "catalog"), entities: edits.filter((e) => e.kind === "entity").map((e) => e.after as never), relationships: edits.filter((e) => e.kind === "relationship").map((e) => e.after as never) });
    const o = moduleOntologyGraph(new Catalog(g), m);
    const name = (id: string) => String((o.entity(id)?.props.name as { value: string } | undefined)?.value);
    expect([...o.relationships()].map((r) => `${r.type}: ${name(r.source)} -> ${name(r.target)}`).sort(), "a type without a source or target list draws nothing").toEqual(["Links: any type -> any type", "Mentions: any type -> Pod"]);
  });
});

// Code review 2 (paper/research/12-code-review-2.md, design: Live | At(t)): whether the space follows now or shows a time is one value.
describe("app/timeline.ts the shared timeline scrubbed back", () => {
  const space = async () => {
    const { Workspace } = await import("../../src/app/workspace");
    const { History } = await import("../../src/app/history");
    const { darkTheme } = await import("../../src/render/theme");
    const ws = new Workspace({} as never, darkTheme, () => {});
    (ws as unknown as { info: unknown }).info = { graphs: [{ id: "a", role: "master" }, { id: "b", role: "master" }] };
    const a = new History({ version: ver("1", "a"), entities: [], relationships: [] });
    const b = new History({ version: ver("1", "b"), entities: [], relationships: [] });
    histories(ws).set("a", a); histories(ws).set("b", b);
    a.append(patch("1", "2", [addN(plain("x"))]), 10);
    a.append(patch("2", "3", [addN(plain("y"))]), 20);
    return { ws, a, b };
  };
  it("a live patch is kept for later and never applied to the graph and taken back", async () => {
    const { ws, b } = await space();
    ws.seek(10, {});
    let applied = 0;
    const apply = b.graph.apply.bind(b.graph);
    b.graph.apply = (p) => { applied++; return apply(p); };
    ws.applyPatch("b", patch("1", "2", [addN(plain("z"))]), 30);
    expect([applied, b.cursor, b.length], "the patch was applied and then inverted").toEqual([0, 0, 1]);
    ws.seek(30, {});
    expect(b.graph.entity("z"), "at the newest time it shows").toBeTruthy();
  });
  it("telemetry that arrives while the space shows a time does not move the time of the values", async () => {
    const { TelemetryHub } = await import("../../src/app/telemetry-hub");
    const { ws, a } = await space();
    const hub = new TelemetryHub(a.graph, { meters: [{ id: "cpu", target: "node", label: "CPU", unit: "", min: 0, max: 1 }], dt: 1, ticks: 1, maxTicks: 1, fetch: async () => ({ batches: [] }), defaults: { edge: { width: "", flow: "", state: "" }, node: { ring: "cpu", size: "cpu", state: "cpu" } } } as never);
    hubs(ws).set("a", hub);
    hub.ingest({ graph: "a", tick: 5, t: 5, target: "node", property: "cpu", values: Float32Array.of(0.5) });
    ws.seek(10, {});                       // the graphs show second 10 of 20; the values' newest window is second 5
    hub.ingest({ graph: "a", tick: 15, t: 15, target: "node", property: "cpu", values: Float32Array.of(0.9) });
    expect(hub.time, "the values followed the newest window while the graphs show second 10").toBe(10);
  });
});

describe("app/timeline.ts the catalog and new values while the space shows a time (batch 2 review)", () => {
  it("a catalog patch is applied at once: definitions are not scrubbed by data time, and later catalog patches still apply", async () => {
    const { Workspace } = await import("../../src/app/workspace");
    const { History } = await import("../../src/app/history");
    const { darkTheme } = await import("../../src/render/theme");
    const ws = new Workspace({} as never, darkTheme, () => {});
    (ws as unknown as { info: unknown }).info = { graphs: [{ id: "catalog", role: "catalog" }, { id: "a", role: "master" }] };
    const cat = new History({ version: ver("1", "catalog"), entities: [], relationships: [] });
    const a = new History({ version: ver("1", "a"), entities: [], relationships: [] });
    histories(ws).set("catalog", cat); histories(ws).set("a", a);
    a.append(patch("1", "2", [addN(plain("x"))]), 10);
    a.append(patch("2", "3", [addN(plain("y"))]), 20);
    ws.seek(10, {});
    ws.applyPatch("catalog", patch("1", "2", [addN(plain("m"))]), 30);
    ws.seek(20, {});
    ws.applyPatch("catalog", patch("2", "3", [addN(plain("n"))]), 40);
    expect([cat.cursor, cat.atHead, !!cat.graph.entity("m"), !!cat.graph.entity("n")], "the catalog patch kept while scrubbed back is never applied").toEqual([2, true, true, true]);
  });
  it("values whose hub is made while the space shows a time show that time, not the newest window", async () => {
    const { Workspace } = await import("../../src/app/workspace");
    const { History } = await import("../../src/app/history");
    const { darkTheme } = await import("../../src/render/theme");
    const ws = new Workspace({} as never, darkTheme, () => {});
    (ws as unknown as { info: unknown }).info = { graphs: [{ id: "a", role: "master" }] };
    const a = new History({ version: ver("1", "a"), entities: [], relationships: [] });
    histories(ws).set("a", a);
    a.append(patch("1", "2", [addN(plain("x"))]), 10);
    a.append(patch("2", "3", [addN(plain("y"))]), 20);
    ws.seek(10, {});
    const hub = ws.hubFor("a");
    hub.ingest({ graph: "a", tick: 15, t: 15, target: "node", property: "cpu", values: Float32Array.of(0.9) });
    expect(hub.time, "a hub made while scrubbed back followed the newest window").toBe(10);
  });
});
