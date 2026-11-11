import { describe, expect, it } from "vitest";
import { TelemetryStore, dequantize, quantize, sparkPoints, warnByte } from "../src/core/telemetry";
import type { Meter, TelemetryBatch } from "../src/source/types";

const lin: Meter = { id: "cpu", target: "node", label: "CPU", unit: "", min: 0, max: 1, warn: 0.8 };
const sq: Meter = { id: "requests", target: "edge", label: "Requests", unit: "1/s", min: 0, max: 1000, scale: "sqrt" };
const batch = (tick: number, values: number[], extra: Partial<TelemetryBatch> = {}): TelemetryBatch => ({ graph: "g", tick, t: tick, target: "node", property: "cpu", values: Float32Array.from(values), ...extra });
const ident = (_t: "node" | "edge", id: string) => Number(id);

describe("quantization", () => {
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

describe("TelemetryStore", () => {
  it("dense batches write by index, sparse batches update on top of the previous tick", () => {
    const s = new TelemetryStore([lin]);
    s.ingest(batch(0, [0.1, 0.2, 0.3]), ident, 4);
    s.ingest(batch(1, [1], { ids: ["2"] }), ident, 4);
    const f1 = s.frameAt("node", "cpu", 1)!;
    expect(Array.from(f1.data)).toEqual([quantize(lin, 0.1), quantize(lin, 0.2), 255, 0]);
    expect(s.series("node", "cpu", 2).values.map((v) => +v.toFixed(2))).toEqual([0.3, 1]);
  });
  it("keeps only the window and reports first and last tick", () => {
    const s = new TelemetryStore([lin], 3);
    for (let t = 0; t < 7; t++) s.ingest(batch(t, [t / 10]), ident, 1);
    expect([s.firstTick, s.lastTick]).toEqual([4, 6]);
    expect(s.frameAt("node", "cpu", 0)!.tick).toBe(4); // before the window: oldest frame
    expect(s.frameAt("node", "cpu", 5)!.tick).toBe(5);
  });
  it("ignores unknown metrics and masks slots that no longer hold their snapshot element", () => {
    const s = new TelemetryStore([lin]);
    s.ingest(batch(0, [1], { property: "nope" }), ident, 1);
    expect(s.lastTick).toBe(-1);
    s.setValidity(Uint8Array.from([1, 0]), new Uint8Array(0));
    s.ingest(batch(0, [1, 1]), ident, 2);
    expect(Array.from(s.frameAt("node", "cpu", 0)!.data)).toEqual([255, 0]);
  });
  it("grows frames when capacity grows", () => {
    const s = new TelemetryStore([lin]);
    s.ingest(batch(0, [1]), ident, 1);
    s.ingest(batch(0, [1, 1], {}), ident, 3);
    expect(s.frameAt("node", "cpu", 0)!.data.length).toBe(3);
  });
});

describe("TelemetryStore windows keyed by time", () => {
  it("a revised window updates its frame in place and a late window is inserted in time order", () => {
    const s = new TelemetryStore([lin]);
    const at = (t: number, v: number, ids?: string[]): TelemetryBatch => ({ graph: "g", tick: t, t, target: "node", property: "cpu", ids, values: Float32Array.from([v]) });
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
    const s = new TelemetryStore([m]);
    const b = (t: number, v: number): TelemetryBatch => ({ graph: "g", tick: t, t, target: "node", property: "rate", ids: ["0"], values: Float32Array.from([v]) });
    s.ingest(b(0, 8), ident, 1);
    s.ingest(b(1, 40), ident, 1);
    expect(m.max).toBe(50);
    const [first, second] = s.series("node", "rate", 0).values;
    expect(first).toBeCloseTo(8, 0);   // old frame requantized to the new range
    expect(second).toBeCloseTo(40, 0);
  });
  it("clear forgets frames but keeps meters", () => {
    const s = new TelemetryStore([lin]);
    s.ingest(batch(0, [0.5]), ident, 1);
    s.clear();
    expect(s.frameTimes()).toEqual([]);
    expect(s.meter("node", "cpu")).toBeDefined();
  });
});

describe("sparkPoints", () => {
  it.each([
    [[], ""], [[0.5], "5.0,5.0"], [[0, 1], "0.0,10.0 10.0,0.0"], [[2, -1], "0.0,0.0 10.0,10.0"],
  ])("%j", (v, expected) => expect(sparkPoints(v, 10, 10, 0, 1)).toBe(expected));
});

describe("retracted windows (engine 3.15)", () => {
  const sparse = (t: number, ids: string[], values: number[], removed?: string[]): TelemetryBatch => ({ ...batch(t, values, { ids }), t, ...(removed ? { removed } : {}) });
  it("a retraction drops the value of that element in that window and leaves the others", () => {
    const s = new TelemetryStore([lin]);
    s.ingest(sparse(1, ["0", "1"], [0.4, 0.6]), ident, 4);
    s.ingest(sparse(1, [], [], ["1"]), ident, 4);
    expect(Array.from(s.frameAt("node", "cpu", 1)!.data)).toEqual([quantize(lin, 0.4), 0, 0, 0]);
  });
  it("a batch can retract and carry values together", () => {
    const s = new TelemetryStore([lin]);
    s.ingest(sparse(1, ["0", "1"], [0.4, 0.6]), ident, 4);
    s.ingest(sparse(1, ["2"], [0.9], ["0"]), ident, 4);
    expect(Array.from(s.frameAt("node", "cpu", 1)!.data)).toEqual([0, quantize(lin, 0.6), quantize(lin, Math.fround(0.9)), 0]);
  });
  it.each([
    ["a window that is not held", sparse(7, [], [], ["0"])],
    ["an element that is not in the graph", sparse(1, [], [], ["99"])],
  ])("retracting %s changes nothing and adds no frame", (_, retraction) => {
    const s = new TelemetryStore([lin]);
    s.ingest(sparse(1, ["0"], [0.4]), ident, 4);
    const before = Array.from(s.frameAt("node", "cpu", 1)!.data);
    s.ingest(retraction, (_t, id) => (id === "99" ? undefined : Number(id)), 4);
    expect([Array.from(s.frameAt("node", "cpu", 1)!.data), s.frameTimes()]).toEqual([before, [1]]);
  });
});
