// Generates synthetic telemetry ticks off the main thread. Buffers are transferred to the page and recycled back.
import { allocTick, fillTick, type TelemetrySpec, type TelemetryTick } from "../core/synth-telemetry";

export type TelemetryRequest =
  | { type: "init"; spec: TelemetrySpec }
  | { type: "tick"; id: number; tick: number }
  | { type: "recycle"; tick: TelemetryTick };
export interface TelemetryResponse { id: number; tick: number; data: TelemetryTick }

const ctx = self as unknown as { postMessage(m: TelemetryResponse, t?: Transferable[]): void; onmessage: ((e: MessageEvent<TelemetryRequest>) => void) | null };
let spec: TelemetrySpec | null = null;
const pool: TelemetryTick[] = [];

ctx.onmessage = (e) => {
  const m = e.data;
  if (m.type === "init") { spec = m.spec; pool.length = 0; return; }
  if (m.type === "recycle") { if (pool.length < 4) pool.push(m.tick); return; }
  if (!spec) return;
  const out = pool.pop() ?? allocTick(spec);
  fillTick(spec, m.tick, out);
  ctx.postMessage({ id: m.id, tick: m.tick, data: out }, [out.requests.buffer, out.errorRate.buffer, out.latencyMs.buffer, out.cpu.buffer, out.memory.buffer]);
};
