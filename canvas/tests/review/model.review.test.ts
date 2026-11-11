// Defects of the code review (paper/research/07-code-review-findings.md, section 3 and the canvas addendum) that a person can hit and that need no browser:
// each test states what should be true and fails today for the reason the review gives. Every title names the finding.
import { describe, expect, it } from "vitest";
import { TelemetryHub } from "../../src/app/telemetry-hub";
import { buildTree, severityCompare } from "../../src/app/tree-model";
import { Graph } from "../../src/core/graph";
import { Catalog } from "../../src/core/catalog";
import { TelemetryStore } from "../../src/core/telemetry";
import { resolvePatchTimes } from "../../src/core/time-axis";
import { addValuesRowEdits } from "../../src/editor/commands";
import { parseExpr } from "../../src/editor/expr";
import { parsePalette } from "../../src/editor/palette";
import { contains, ent, text } from "../../src/editor/patches";
import { initialState, reduce } from "../../src/state/reducer";
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

describe("commands.ts:199 child ids come from the child count", () => {
  it("a row added after a middle row was deleted gets an id that no row has", () => {
    const op = "d:m/projection:p/op:v";
    const rows = [0, 2].flatMap((n) => [ent(`${op}/row:#${n}`, "RowValue", {}), contains(op, `${op}/row:#${n}`, "row", n)]);
    const g = Graph.fromSnapshot({
      version: ver("1", "catalog"),
      entities: [ent("d:m/projection:p", "Projection", {}), ent(op, "Values", { name: text("v") }), ...rows.filter((e) => e.kind === "entity")].map((e) => (e as unknown as { after: never }).after),
      relationships: rows.filter((e) => e.kind === "relationship").map((e) => (e as unknown as { after: never }).after),
    });
    const r = addValuesRowEdits(g, op, [{ name: "a", src: "1" }], parsePalette({ operators: [], functions: [] }));
    expect(r.ok).toBe(true);
    const ids = r.ok ? r.value.filter((e) => e.kind === "entity").map((e) => (e as { id: string }).id) : [];
    expect(ids.filter((id) => g.entity(id)), "an id that already exists would be rejected by the engine").toEqual([]);
    void Catalog;
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

describe("editor/expr.ts:14-38 the editor's parser rejects syntax the engine prints", () => {
  it.each([
    ["text concatenation", 'a ++ "x"'],
    ["a duration literal", "t > 5m"],
    ["a duration literal in hours", "age < 24h"],
  ])("%s", (_, src) => expect(parseExpr(src).ok, src).toBe(true));
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
    const { seekTime } = await import("../../src/state/ws-ops");
    const { darkTheme } = await import("../../src/render/theme");
    const ws = new Workspace({} as never, darkTheme, () => {});
    (ws as unknown as { info: unknown }).info = { graphs: [{ id: "a", role: "master" }, { id: "b", role: "master" }] };
    const a = new History({ version: ver("1", "a"), entities: [], relationships: [] });
    const b = new History({ version: ver("1", "b"), entities: [], relationships: [] });
    ws.histories.set("a", a); ws.histories.set("b", b);
    a.append(patch("1", "2", [addN(plain("x"))]), 10);
    a.append(patch("2", "3", [addN(plain("y"))]), 20);
    seekTime(ws, 10, {});                       // the user scrubs back to second 10: a shows its first patch, b its snapshot
    expect(a.cursor).toBe(1);
    ws.applyPatch("b", patch("1", "2", [addN(plain("z"))]), 30);   // live: b gets a patch from second 30
    expect(b.cursor, "b jumped to now while the timeline shows second 10").toBe(ws.cursorAt("b", ws.time));
  });
});
