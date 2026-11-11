import { describe, expect, it } from "vitest";
import { ESTATE_METERS, allocTick, fillTick, hash01, incident, type TelemetrySpec } from "../src/core/synth-telemetry";
import { estateOntology } from "../src/core/synth";
import { checkOntology } from "../src/core/validate";

const spec = (): TelemetrySpec => ({
  nodeCount: 50, edgeCount: 80, seed: 3,
  edgeFlow: Uint8Array.from({ length: 80 }, (_, i) => (i % 2 ? 1 : 0)),
  incidentNode: Uint8Array.from({ length: 50 }, (_, i) => (i < 10 ? 1 : 0)),
  incidentEdge: Uint8Array.from({ length: 80 }, (_, i) => (i < 20 ? 1 : 0)),
});
const run = (tick: number) => { const s = spec(), o = allocTick(s); fillTick(s, tick, o); return o; };

describe("synthetic telemetry", () => {
  it("is deterministic and tick dependent", () => {
    expect(Array.from(run(7).requests)).toEqual(Array.from(run(7).requests));
    expect(Array.from(run(7).requests)).not.toEqual(Array.from(run(8).requests));
  });
  it.each([[0, 0], [10, 0], [15, 0.5], [25, 1], [45, 0.5], [60, 0]])("incident envelope at tick %i is %f", (t, v) => expect(incident(t)).toBeCloseTo(v));
  it("errors rise inside the incident and stay low outside it", () => {
    const before = run(0), during = run(25);
    const avg = (a: Float32Array, from: number, to: number) => { let s = 0, n = 0; for (let i = from; i < to; i++) if (i % 2) { s += a[i]; n++; } return s / n; };
    expect(avg(during.errorRate, 0, 20)).toBeGreaterThan(avg(before.errorRate, 0, 20) * 10);
    expect(avg(during.errorRate, 20, 80)).toBeLessThan(0.02);
  });
  it.each(ESTATE_METERS)("$id stays inside its declared range", (m) => {
    for (const tick of [0, 25, 100, 1000]) {
      const o = run(tick);
      const a = (o as unknown as Record<string, Float32Array>)[m.id];
      for (const v of a) { expect(v).toBeGreaterThanOrEqual(m.min); expect(v).toBeLessThanOrEqual(m.max); }
    }
  });
  it("hash01 is in [0,1)", () => { for (let i = 0; i < 1000; i++) { const h = hash01(i, 3); expect(h >= 0 && h < 1).toBe(true); } });
  it("the estate ontology declares every metric with role telemetry and validates", () => {
    expect(checkOntology(estateOntology).ok).toBe(true);
    for (const m of ESTATE_METERS) expect(estateOntology.properties.find((p) => p.id.value === m.id)?.role).toBe("telemetry");
  });
});
