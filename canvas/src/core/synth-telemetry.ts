// Deterministic synthetic telemetry for the estate: request, error and latency series on edges, cpu and memory on nodes.
// A pure function of (element index, tick, seed), so any tick can be regenerated and a worker can produce it off the main thread.
import type { Meter } from "../source/types";

export const ESTATE_METERS: Meter[] = [
  { id: "requests", target: "edge", label: "Requests", unit: "req/s", min: 0, max: 500, scale: "sqrt" },
  { id: "errorRate", target: "edge", label: "Error rate", unit: "", min: 0, max: 0.3, warn: 0.05 },
  { id: "latencyMs", target: "edge", label: "Latency", unit: "ms", min: 0, max: 300, scale: "sqrt", warn: 120 },
  { id: "cpu", target: "node", label: "CPU", unit: "", min: 0, max: 1, warn: 0.85 },
  { id: "memory", target: "node", label: "Memory", unit: "", min: 0, max: 1, warn: 0.9 },
];
export const ESTATE_DEFAULTS = {
  edge: { width: "requests", flow: "requests", state: "errorRate" },
  node: { ring: "cpu", size: "memory", state: "cpu" },
};

export interface TelemetrySpec {
  nodeCount: number;
  edgeCount: number;
  /** 1 for flow edges (Calls), 0 for structural edges */
  edgeFlow: Uint8Array;
  /** elements inside the incident blast radius */
  incidentNode: Uint8Array;
  incidentEdge: Uint8Array;
  seed: number;
}
export interface TelemetryTick {
  requests: Float32Array; errorRate: Float32Array; latencyMs: Float32Array;
  cpu: Float32Array; memory: Float32Array;
}
export const allocTick = (spec: TelemetrySpec): TelemetryTick => ({
  requests: new Float32Array(spec.edgeCount), errorRate: new Float32Array(spec.edgeCount), latencyMs: new Float32Array(spec.edgeCount),
  cpu: new Float32Array(spec.nodeCount), memory: new Float32Array(spec.nodeCount),
});

/** uniform [0, 1) from two integers */
export function hash01(a: number, b: number): number {
  let h = Math.imul(a ^ 0x9e3779b9, 0x85ebca6b) ^ Math.imul(b + 0x7f4a7c15, 0xc2b2ae35);
  h ^= h >>> 15; h = Math.imul(h, 0x2c1b3c6d); h ^= h >>> 12; h = Math.imul(h, 0x297a2d39); h ^= h >>> 15;
  return (h >>> 0) / 4294967296;
}

/** 0 before tick 10, ramps to 1 by tick 20, holds, ramps down 40 to 50: the incident envelope */
export const incident = (tick: number): number => Math.min(1, Math.max(0, (tick - 10) / 10)) * Math.min(1, Math.max(0, (50 - tick) / 10));

export function fillTick(spec: TelemetrySpec, tick: number, out: TelemetryTick): void {
  const inc = incident(tick), w = Math.PI * 2;
  for (let i = 0; i < spec.edgeCount; i++) {
    const base = hash01(i, spec.seed), phase = hash01(i, spec.seed + 1), noise = hash01(i, tick * 7919 + spec.seed + 2);
    const flow = spec.edgeFlow[i] === 1;
    const wave = 0.7 + 0.3 * Math.sin(w * (tick / 24 + phase));
    out.requests[i] = (flow ? 20 + base * 380 : 2 + base * 20) * wave * (1 + 0.2 * (noise - 0.5));
    const hit = spec.incidentEdge[i] === 1 && flow ? inc : 0;
    out.errorRate[i] = flow ? 0.002 + 0.01 * noise * base + hit * (0.1 + 0.18 * hash01(i, 5)) : 0;
    out.latencyMs[i] = flow ? (6 + base * 50) * wave * (1 + hit * 4) * (0.9 + 0.2 * noise) : 0;
  }
  for (let i = 0; i < spec.nodeCount; i++) {
    const base = hash01(i, spec.seed + 3), phase = hash01(i, spec.seed + 4), noise = hash01(i, tick * 104729 + spec.seed + 5);
    const hit = spec.incidentNode[i] === 1 ? inc : 0;
    out.cpu[i] = Math.min(1, 0.12 + 0.45 * base * (0.7 + 0.3 * Math.sin(w * (tick / 30 + phase))) + 0.06 * noise + hit * 0.4);
    out.memory[i] = Math.min(1, 0.25 + 0.4 * base + 0.0015 * tick + 0.03 * noise);
  }
}
