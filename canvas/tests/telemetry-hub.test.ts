import { describe, expect, it } from "vitest";
import { Graph } from "../src/core/graph";
import { TelemetryHub, formatValue } from "../src/app/telemetry-hub";
import { quantize } from "../src/core/telemetry";
import type { Meter, TelemetryFeed, TelemetryBatch } from "../src/source/types";
import { addN, delN, ent, patch, rel, ver } from "./helpers";

const meters: Meter[] = [
  { id: "requests", target: "edge", label: "Requests", unit: "req/s", min: 0, max: 500, scale: "sqrt" },
  { id: "errorRate", target: "edge", label: "Error rate", unit: "", min: 0, max: 0.3, warn: 0.05 },
  { id: "cpu", target: "node", label: "CPU", unit: "", min: 0, max: 1, warn: 0.85 },
];
const feed: TelemetryFeed = {
  meters, dt: 1, ticks: 2, maxTicks: 2, fetch: async () => ({ batches: [] }),
  defaults: { edge: { width: "requests", flow: "requests", state: "errorRate" }, node: { ring: "cpu", size: "cpu", state: "cpu" } },
};
const graph = () => Graph.fromSnapshot({ version: ver("1"), entities: [ent("a"), ent("b"), ent("c")], relationships: [rel("r1", "a", "b"), rel("r2", "b", "c")] });
const dense = (tick: number, target: "node" | "edge", property: string, values: number[]): TelemetryBatch => ({ graph: "g", tick, t: tick, target, property, values: Float32Array.from(values) });

describe("TelemetryHub", () => {
  it("dense ingest follows the newest tick and exposes channel frames for the GPU", () => {
    const hub = new TelemetryHub(graph(), feed);
    hub.ingest(dense(0, "edge", "requests", [100, 400]));
    hub.ingest(dense(1, "edge", "requests", [200, 300]));
    expect(hub.hasData && hub.time === 1 && hub.follow).toBe(true);
    const f = hub.frame("edge", 0)!;
    expect(f[0]).toBe(quantize(meters[0], 200));
    expect(hub.frame("edge", 1)![1]).toBe(quantize(meters[0], 300)); // flow channel uses the same metric by default
    hub.setTime(0);
    expect(hub.follow).toBe(false);
    expect(hub.frame("edge", 0)![0]).toBe(quantize(meters[0], 100));
  });
  it("channels can be switched off or remapped without touching the data", () => {
    const hub = new TelemetryHub(graph(), feed);
    hub.ingest(dense(0, "edge", "errorRate", [0.01, 0.2]));
    hub.edge.state = null;
    expect(hub.frame("edge", 2)).toBeNull();
    expect(hub.warnByteOf("edge")).toBe(256);
    hub.edge.state = "errorRate";
    expect(hub.warnByteOf("edge")).toBe(quantize(meters[1], 0.05));
  });
  it("readings flag values at or above the threshold", () => {
    const hub = new TelemetryHub(graph(), feed);
    hub.ingest(dense(0, "edge", "errorRate", [0.01, 0.2]));
    const [a, b] = [0, 1].map((slot) => hub.readings("edge", slot).find((r) => r.meter.id === "errorRate")!);
    expect([a.warn, b.warn]).toEqual([false, true]);
    expect(b.value).toBeCloseTo(0.2, 2);
  });
  it("a changed threshold takes effect immediately", () => {
    const hub = new TelemetryHub(graph(), feed);
    hub.ingest(dense(0, "edge", "errorRate", [0.01, 0.2]));
    hub.setMeterWarn("edge", "errorRate", 0.5);
    expect(hub.readings("edge", 1).find((r) => r.meter.id === "errorRate")!.warn).toBe(false);
  });
  it("dense values for slots that no longer hold their snapshot element are ignored after revalidation", () => {
    const g = graph();
    const hub = new TelemetryHub(g, feed);
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
    const hub = new TelemetryHub(g, feed);
    g.apply(patch("1", "2", [addN(ent("d"))]));
    hub.ingest({ graph: "g", tick: 0, t: 0, target: "node", property: "cpu", ids: ["d"], values: Float32Array.from([1]) });
    expect(hub.readings("node", g.nodeSlot.get("d")!)[0].value).toBeCloseTo(1, 2);
  });
});

describe("formatValue", () => {
  it.each([
    [meters[0], 123.4, "123 req/s"], [meters[0], 4.5, "4.50 req/s"], [meters[2], 0.929, "92.9%"], [meters[1], 0.052, "5.2%"],
  ])("%j %f -> %s", (m, v, s) => expect(formatValue(m, v)).toBe(s));
});
