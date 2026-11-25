import { describe, expect, it } from "vitest";
import { Graph } from "../src/core/graph";
import { WindowHub, formatValue } from "../src/app/window-hub";
import { WindowStore, dequantize, quantize, sparkPoints, warnByte } from "../src/core/windows";
import type { Meter, WindowBatch, WindowFeed } from "../src/source/types";
import { addN, delN, ent, patch, rel, ver } from "./helpers";

const lin: Meter = { id: "cpu", target: "node", label: "CPU", unit: "", min: 0, max: 1, warn: 0.8 };
const sq: Meter = { id: "requests", target: "edge", label: "Requests", unit: "1/s", min: 0, max: 1000, scale: "sqrt" };
const batch = (tick: number, values: number[], extra: Partial<WindowBatch> = {}): WindowBatch => ({ graph: "g", tick, t: tick, target: "node", property: "cpu", values: Float32Array.from(values), ...extra });
const ident = (_t: "node" | "edge", id: string) => Number(id);
const meters: Meter[] = [
  { id: "requests", target: "edge", label: "Requests", unit: "req/s", min: 0, max: 500, scale: "sqrt" },
  { id: "errorRate", target: "edge", label: "Error rate", unit: "", min: 0, max: 0.3, warn: 0.05 },
  { id: "cpu", target: "node", label: "CPU", unit: "", min: 0, max: 1, warn: 0.85 },
];
const feed: WindowFeed = {
  meters, dt: 1, ticks: 2, maxTicks: 2, fetch: async () => ({ batches: [] }),
  defaults: { edge: { width: "requests", flow: "requests", state: "errorRate" }, node: { ring: "cpu", size: "cpu", state: "cpu" } },
};
const graph = () => Graph.fromSnapshot({ version: ver("1"), entities: [ent("a"), ent("b"), ent("c")], relationships: [rel("r1", "a", "b"), rel("r2", "b", "c")] });
const dense = (tick: number, target: "node" | "edge", property: string, values: number[]): WindowBatch => ({ graph: "g", tick, t: tick, target, property, values: Float32Array.from(values) });

describe("a value is stored as one byte on the scale of its meter", () => {
  it.each([
    [lin, 0, 0], [lin, 1, 255], [lin, 0.5, 128], [lin, -3, 0], [lin, 9, 255], [lin, NaN, 0], [sq, 250, 128], [sq, 1000, 255],
  ])("quantize(%j, %f) = %i", (m, v, b) => expect(quantize(m, v)).toBe(b));
  it.each([[lin], [sq]])("round trips within one level (%j)", (m) => {
    for (let b = 0; b <= 255; b += 17) expect(quantize(m, dequantize(m, b))).toBe(b);
  });
  it("warn threshold in byte space", () => {
    expect(warnByte(lin)).toBe(204);
    expect(warnByte({ ...lin, warn: undefined })).toBe(256);
  });
});

describe("the store keeps one frame per window with a value per element slot", () => {
  it("dense batches write by index, sparse batches update on top of the previous tick", () => {
    const s = new WindowStore([lin]);
    s.ingest(batch(0, [0.1, 0.2, 0.3]), ident, 4);
    s.ingest(batch(1, [1], { ids: ["2"] }), ident, 4);
    const f1 = s.frameAt("node", "cpu", 1)!;
    expect(Array.from(f1.data)).toEqual([quantize(lin, 0.1), quantize(lin, 0.2), 255, 0]);
    expect(s.series("node", "cpu", 2).values.map((v) => +v.toFixed(2))).toEqual([0.3, 1]);
  });
  it("keeps only the window and reports first and last tick", () => {
    const s = new WindowStore([lin], 3);
    for (let t = 0; t < 7; t++) s.ingest(batch(t, [t / 10]), ident, 1);
    expect([s.firstTick, s.lastTick]).toEqual([4, 6]);
    expect(s.frameAt("node", "cpu", 0)!.tick).toBe(4); // before the window: oldest frame
    expect(s.frameAt("node", "cpu", 5)!.tick).toBe(5);
  });
  it("ignores unknown metrics and masks slots that no longer hold their snapshot element", () => {
    const s = new WindowStore([lin]);
    s.ingest(batch(0, [1], { property: "nope" }), ident, 1);
    expect(s.lastTick).toBe(-1);
    s.setValidity(Uint8Array.from([1, 0]), new Uint8Array(0));
    s.ingest(batch(0, [1, 1]), ident, 2);
    expect(Array.from(s.frameAt("node", "cpu", 0)!.data)).toEqual([255, 0]);
  });
  it("grows frames when capacity grows", () => {
    const s = new WindowStore([lin]);
    s.ingest(batch(0, [1]), ident, 1);
    s.ingest(batch(0, [1, 1], {}), ident, 3);
    expect(s.frameAt("node", "cpu", 0)!.data.length).toBe(3);
  });
});

describe("the store keys windows by time: a revised window replaces its frame, a late one is inserted in order", () => {
  it("a revised window updates its frame in place and a late window is inserted in time order", () => {
    const s = new WindowStore([lin]);
    const at = (t: number, v: number, ids?: string[]): WindowBatch => ({ graph: "g", tick: t, t, target: "node", property: "cpu", ids, values: Float32Array.from([v]) });
    s.ingest(at(60, 0.1, ["0"]), ident, 2);
    s.ingest(at(180, 0.3, ["0"]), ident, 2);
    s.ingest(at(120, 0.2, ["0"]), ident, 2);        // late window between the two
    s.ingest(at(60, 0.9, ["0"]), ident, 2);         // revision of the first window
    expect(s.frameTimes()).toEqual([60, 120, 180]);
    expect(s.series("node", "cpu", 0).values.map((v) => +v.toFixed(1))).toEqual([0.9, 0.2, 0.3]);
    expect(s.frameAtTime("node", "cpu", 130)!.t).toBe(120);
    expect(s.frameAtTime("node", "cpu", 10)!.t).toBe(60);
  });
  it("auto-range metrics rescale stored frames when a value exceeds the range", () => {
    const m: Meter = { id: "rate", target: "node", label: "r", unit: "", min: 0, max: 10, auto: true };
    const s = new WindowStore([m]);
    const b = (t: number, v: number): WindowBatch => ({ graph: "g", tick: t, t, target: "node", property: "rate", ids: ["0"], values: Float32Array.from([v]) });
    s.ingest(b(0, 8), ident, 1);
    s.ingest(b(1, 40), ident, 1);
    expect(m.max).toBe(50);
    const [first, second] = s.series("node", "rate", 0).values;
    expect(first).toBeCloseTo(8, 0);   // old frame requantized to the new range
    expect(second).toBeCloseTo(40, 0);
  });
  it("clear forgets frames but keeps meters", () => {
    const s = new WindowStore([lin]);
    s.ingest(batch(0, [0.5]), ident, 1);
    s.clear();
    expect(s.frameTimes()).toEqual([]);
    expect(s.meter("node", "cpu")).toBeDefined();
  });
});

describe("a sparkline scales the series into its box", () => {
  it.each([
    [[], ""], [[0.5], "5.0,5.0"], [[0, 1], "0.0,10.0 10.0,0.0"], [[2, -1], "0.0,0.0 10.0,10.0"],
  ])("%j", (v, expected) => expect(sparkPoints(v, 10, 10, 0, 1)).toBe(expected));
});

describe("a retraction removes the value of one element in one window and nothing else", () => {
  const sparse = (t: number, ids: string[], values: number[], removed?: string[]): WindowBatch => ({ ...batch(t, values, { ids }), t, ...(removed ? { removed } : {}) });
  it("a retraction drops the value of that element in that window and leaves the others", () => {
    const s = new WindowStore([lin]);
    s.ingest(sparse(1, ["0", "1"], [0.4, 0.6]), ident, 4);
    s.ingest(sparse(1, [], [], ["1"]), ident, 4);
    expect(Array.from(s.frameAt("node", "cpu", 1)!.data)).toEqual([quantize(lin, 0.4), 0, 0, 0]);
  });
  it("a batch can retract and carry values together", () => {
    const s = new WindowStore([lin]);
    s.ingest(sparse(1, ["0", "1"], [0.4, 0.6]), ident, 4);
    s.ingest(sparse(1, ["2"], [0.9], ["0"]), ident, 4);
    expect(Array.from(s.frameAt("node", "cpu", 1)!.data)).toEqual([0, quantize(lin, 0.6), quantize(lin, Math.fround(0.9)), 0]);
  });
  it.each([
    ["a window that is not held", sparse(7, [], [], ["0"])],
    ["an element that is not in the graph", sparse(1, [], [], ["99"])],
  ])("retracting %s changes nothing and adds no frame", (_, retraction) => {
    const s = new WindowStore([lin]);
    s.ingest(sparse(1, ["0"], [0.4]), ident, 4);
    const before = Array.from(s.frameAt("node", "cpu", 1)!.data);
    s.ingest(retraction, (_t, id) => (id === "99" ? undefined : Number(id)), 4);
    expect([Array.from(s.frameAt("node", "cpu", 1)!.data), s.frameTimes()]).toEqual([before, [1]]);
  });
});

describe("the hub serves the frames of the GPU channels and the readings of the Inspector", () => {
  it("dense ingest follows the newest tick and exposes channel frames for the GPU", () => {
    const hub = new WindowHub(graph(), feed);
    hub.ingest(dense(0, "edge", "requests", [100, 400]));
    hub.ingest(dense(1, "edge", "requests", [200, 300]));
    expect(hub.hasData && hub.time === 1 && hub.timeline.k === "live").toBe(true);
    const f = hub.frame("edge", 0)!;
    expect(f[0]).toBe(quantize(meters[0], 200));
    expect(hub.frame("edge", 1)![1]).toBe(quantize(meters[0], 300)); // flow channel uses the same metric by default
    hub.show({ k: "at", t: 0 });
    expect(hub.timeline.k).toBe("at");
    expect(hub.frame("edge", 0)![0]).toBe(quantize(meters[0], 100));
  });
  it("channels can be switched off or remapped without touching the data", () => {
    const hub = new WindowHub(graph(), feed);
    hub.ingest(dense(0, "edge", "errorRate", [0.01, 0.2]));
    hub.edge.state = null;
    expect(hub.frame("edge", 2)).toBeNull();
    expect(hub.warnByteOf("edge")).toBe(256);
    hub.edge.state = "errorRate";
    expect(hub.warnByteOf("edge")).toBe(quantize(meters[1], 0.05));
  });
  it("readings flag values at or above the threshold", () => {
    const hub = new WindowHub(graph(), feed);
    hub.ingest(dense(0, "edge", "errorRate", [0.01, 0.2]));
    const [a, b] = [0, 1].map((slot) => hub.readings("edge", slot).find((r) => r.meter.id === "errorRate")!);
    expect([a.warn, b.warn]).toEqual([false, true]);
    expect(b.value).toBeCloseTo(0.2, 2);
  });
  it("a changed threshold takes effect immediately", () => {
    const hub = new WindowHub(graph(), feed);
    hub.ingest(dense(0, "edge", "errorRate", [0.01, 0.2]));
    hub.setMeterWarn("edge", "errorRate", 0.5);
    expect(hub.readings("edge", 1).find((r) => r.meter.id === "errorRate")!.warn).toBe(false);
  });
  it("dense values for slots that no longer hold their snapshot element are ignored after revalidation", () => {
    const g = graph();
    const hub = new WindowHub(g, feed);
    const res = g.apply(patch("1", "2", [delN(ent("c")), addN(ent("d"))]));
    expect(res.ok).toBe(true);
    if (res.ok) hub.revalidate(res.dirtyNodes, res.dirtyEdges);
    hub.ingest(dense(0, "node", "cpu", [0.5, 0.5, 0.9]));
    const f = hub.frame("node", 0)!;
    expect([f[0], f[1]].every((v) => v > 0)).toBe(true);
    expect(f[2]).toBe(0); // slot 2 now holds "d", not the snapshot element "c"
  });
  it("sparse batches reach elements by id, including ones added after the snapshot", () => {
    const g = graph();
    const hub = new WindowHub(g, feed);
    g.apply(patch("1", "2", [addN(ent("d"))]));
    hub.ingest({ graph: "g", tick: 0, t: 0, target: "node", property: "cpu", ids: ["d"], values: Float32Array.from([1]) });
    expect(hub.readings("node", g.nodeSlot.get("d")!)[0].value).toBeCloseTo(1, 2);
  });
});

describe("a value is shown in the unit of its meter", () => {
  it.each([
    [meters[0], 123.4, "123 req/s"], [meters[0], 4.5, "4.50 req/s"], [meters[2], 0.929, "92.9%"], [meters[1], 0.052, "5.2%"],
  ])("%j %f -> %s", (m, v, s) => expect(formatValue(m, v)).toBe(s));
});

describe("a reading shows only a value measured for that element, and the warning on the canvas and in the Inspector agree", () => {
  const cpu: Meter = { id: "cpu", target: "node", label: "CPU", unit: "", min: 0, max: 1 };
  const feed: WindowFeed = { meters: [cpu], dt: 1, ticks: 1, maxTicks: 1, fetch: async () => ({ batches: [] }), defaults: { node: { ring: "cpu", size: "cpu", state: "cpu" }, edge: { width: "", flow: "", state: "" } } };
  const dense = (t: number, v: number[]): WindowBatch => ({ graph: "g", tick: t, t, target: "node", property: "cpu", values: Float32Array.from(v) });
  const sparse = (t: number, ids: string[], v: number[]): WindowBatch => ({ graph: "g", tick: t, t, target: "node", property: "cpu", ids, values: Float32Array.from(v) });
  const world = () => Graph.fromSnapshot({ version: ver("1"), entities: [ent("a"), ent("b"), ent("c")], relationships: [] });

  it("a new element in the slot of a deleted one does not show the deleted element's value", () => {
    const g = world();
    const hub = new WindowHub(g, feed);
    hub.ingest(dense(0, [0.1, 0.2, 0.9]));                       // c is hot
    g.apply(patch("1", "2", [{ kind: "entity", id: "c", before: g.entity("c")!, after: null }, addN(ent("d"))]));
    hub.revalidate([g.nodeSlot.get("d")!], []);
    hub.ingest(sparse(1, ["a"], [0.3]));                          // a sparse window: d has no value in it
    const reading = hub.readings("node", g.nodeSlot.get("d")!).find((r) => r.meter.id === "cpu");
    expect(reading?.value, "d inherited c's 0.9 from the previous window").not.toBeCloseTo(0.9, 1);
  });

  it("an element that was never measured has no reading, rather than the minimum of the scale", () => {
    const g = world();
    const hub = new WindowHub(g, feed);
    hub.ingest(sparse(0, ["a"], [0.5]));
    expect(hub.readings("node", g.nodeSlot.get("b")!).find((r) => r.meter.id === "cpu"), "b has no data: it is unknown, not 0").toBeUndefined();
  });

  it("the canvas and the Inspector agree with the engine near the warning threshold", () => {
    const g = world();
    const warnMeter: Meter = { ...cpu, warn: 0.502 };
    const hub = new WindowHub(g, { ...feed, meters: [warnMeter] });
    hub.ingest(dense(0, [0.5, 0.1, 0.1]));                       // 0.5 is below the threshold 0.502: the engine says ok
    const slot = g.nodeSlot.get("a")!;
    const onGpu = hub.frame("node", 0 /* ring */)![slot] >= hub.warnByteOf("node");
    const inInspector = hub.readings("node", slot).find((r) => r.meter.id === "cpu")!.warn;
    expect({ gpu: onGpu, inspector: inInspector }, "8-bit quantisation decides on the GPU, the dequantised value in the Inspector").toEqual({ gpu: false, inspector: false });
  });
});
