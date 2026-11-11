// Telemetry store: 8-bit quantized frames per (target, metric) in a ring of recent ticks, indexed by graph slot.
// 8 bits is the GPU format (normalized unsigned byte attributes), so a frame is uploaded as is.
import type { Meter, TelemetryBatch } from "../source/types";

export const meterKey = (target: "node" | "edge", id: string): string => `${target}:${id}`;

export function quantize(m: Meter, v: number): number {
  if (!Number.isFinite(v)) return 0;
  const n = Math.min(1, Math.max(0, (v - m.min) / (m.max - m.min || 1)));
  return Math.round(255 * (m.scale === "sqrt" ? Math.sqrt(n) : n));
}
export function dequantize(m: Meter, b: number): number {
  const n = b / 255;
  return m.min + (m.scale === "sqrt" ? n * n : n) * (m.max - m.min);
}
export const warnByte = (m: Meter): number => (m.warn === undefined ? 256 : quantize(m, m.warn));

export interface Frame { tick: number; t: number; data: Uint8Array }

export interface IngestStats { ticks: number; lastMs: number; avgMs: number }

export class TelemetryStore {
  readonly meters = new Map<string, Meter>();
  private frames = new Map<string, Frame[]>();
  /** 1 while the slot still holds the element it held in the snapshot (dense batches are aligned to the snapshot) */
  validNode: Uint8Array<ArrayBufferLike> = new Uint8Array(0);
  validEdge: Uint8Array<ArrayBufferLike> = new Uint8Array(0);
  firstTick = 0;
  lastTick = -1;
  readonly stats: IngestStats = { ticks: 0, lastMs: 0, avgMs: 0 };

  constructor(meters: Meter[], readonly window = 60) { for (const m of meters) this.meters.set(meterKey(m.target, m.id), m); }

  meter(target: "node" | "edge", id: string): Meter | undefined { return this.meters.get(meterKey(target, id)); }

  addMeter(m: Meter): void { this.meters.set(meterKey(m.target, m.id), m); }

  /** Change the range of a metric and requantize its stored frames. Precision of old frames is limited to the old 8 bits. */
  rescale(m: Meter, max: number): void {
    const old: Meter = { ...m };
    m.max = max;
    for (const f of this.frames.get(meterKey(m.target, m.id)) ?? []) for (let i = 0; i < f.data.length; i++) if (f.data[i]) f.data[i] = quantize(m, dequantize(old, f.data[i]));
  }
  metersOf(target: "node" | "edge"): Meter[] { return [...this.meters.values()].filter((m) => m.target === target); }

  setValidity(node: Uint8Array<ArrayBufferLike>, edge: Uint8Array<ArrayBufferLike>): void { this.validNode = node; this.validEdge = edge; }

  /**
   * Add a batch. `slotOf` resolves an element id to its slot (sparse batches); dense batches write by index.
   * `cap` is the current slot capacity so frames can be indexed by slot directly.
   */
  ingest(b: TelemetryBatch, slotOf: (target: "node" | "edge", id: string) => number | undefined, cap: number): void {
    const t0 = performance.now();
    const m = this.meter(b.target, b.property);
    if (!m) return;
    const key = meterKey(b.target, b.property);
    // engine 3.15: a window of an element that was retracted has no value any more; a window that is not held is nothing to drop
    if (b.removed?.length) {
      const held = this.frames.get(key)?.find((x) => x.t === b.t);
      if (held) for (const id of b.removed) { const s = slotOf(b.target, id); if (s !== undefined && s < held.data.length) held.data[s] = 0; }
      if (!b.values.length) return;
    }
    if (m.auto) { let mx = 0; for (let i = 0; i < b.values.length; i++) if (b.values[i] > mx) mx = b.values[i]; if (mx > m.max) this.rescale(m, mx * 1.25); }
    let list = this.frames.get(key);
    if (!list) { list = []; this.frames.set(key, list); }
    // frames are keyed by window start: a revised window updates its frame, a new one is inserted in time order
    let at = list.length;
    while (at > 0 && list[at - 1].t > b.t) at--;
    let f = at > 0 && list[at - 1].t === b.t ? list[at - 1] : undefined;
    if (!f) {
      const prev = at > 0 ? list[at - 1].data : undefined;
      f = { tick: b.tick, t: b.t, data: new Uint8Array(cap) };
      if (prev) f.data.set(prev.subarray(0, Math.min(prev.length, cap))); // sparse batches update on top of the previous window
      list.splice(at, 0, f);
      while (list.length > this.window) list.shift();
    } else if (f.data.length < cap) { const d = new Uint8Array(cap); d.set(f.data); f.data = d; }
    const d = f.data;
    const v = b.values;
    if (b.ids) {
      for (let i = 0; i < b.ids.length; i++) { const s = slotOf(b.target, b.ids[i]); if (s !== undefined && s < d.length) d[s] = quantize(m, v[i]); }
    } else {
      const valid = b.target === "node" ? this.validNode : this.validEdge;
      const n = Math.min(v.length, d.length);
      const lo = m.min, span = m.max - m.min || 1, sq = m.scale === "sqrt";
      for (let i = 0; i < n; i++) {
        if (valid.length && !valid[i]) { d[i] = 0; continue; }
        let x = (v[i] - lo) / span;
        x = x < 0 ? 0 : x > 1 ? 1 : x;
        d[i] = (sq ? Math.sqrt(x) : x) * 255 + 0.5;
      }
    }
    if (b.tick > this.lastTick) this.lastTick = b.tick;
    this.firstTick = this.oldestTick();
    const ms = performance.now() - t0;
    this.stats.ticks++; this.stats.lastMs = ms; this.stats.avgMs += (ms - this.stats.avgMs) / Math.min(this.stats.ticks, 50);
  }

  private oldestTick(): number {
    let o = Infinity;
    for (const l of this.frames.values()) if (l.length) o = Math.min(o, l[0].tick);
    return Number.isFinite(o) ? o : 0;
  }

  /** Frame at or before `tick` (the oldest frame when scrubbing before the window). */
  frameAt(target: "node" | "edge", id: string, tick: number): Frame | undefined {
    const l = this.frames.get(meterKey(target, id));
    if (!l?.length) return undefined;
    let best = l[0];
    for (const f of l) { if (f.tick <= tick) best = f; else break; }
    return best;
  }

  /** Frame valid at time t (seconds): the last frame starting at or before t, or the oldest when t precedes the window. */
  frameAtTime(target: "node" | "edge", id: string, t: number): Frame | undefined {
    const l = this.frames.get(meterKey(target, id));
    if (!l?.length) return undefined;
    let best = l[0];
    for (const f of l) { if (f.t <= t) best = f; else break; }
    return best;
  }

  /** Window start times of the stored frames, ascending (taken from the metric with the most frames). */
  frameTimes(): number[] {
    let best: Frame[] = [];
    for (const l of this.frames.values()) if (l.length > best.length) best = l;
    return best.map((f) => f.t);
  }

  get firstTime(): number { return this.frameTimes()[0] ?? 0; }
  get lastTime(): number { const t = this.frameTimes(); return t.length ? t[t.length - 1] : 0; }

  /** Dequantized history of one slot over the window, oldest first. */
  series(target: "node" | "edge", id: string, slot: number): { ticks: number[]; times: number[]; values: number[] } {
    const m = this.meter(target, id);
    const l = this.frames.get(meterKey(target, id)) ?? [];
    const out = { ticks: [] as number[], times: [] as number[], values: [] as number[] };
    if (!m) return out;
    for (const f of l) { out.ticks.push(f.tick); out.times.push(f.t); out.values.push(slot < f.data.length ? dequantize(m, f.data[slot]) : 0); }
    return out;
  }

  get tickCount(): number { return this.lastTick + 1; }

  /** Forget every stored frame (the engine re-activated the space and its windows may differ). Meters stay. */
  clear(): void { this.frames.clear(); this.firstTick = 0; this.lastTick = -1; }
}

/** Polyline points for a sparkline in a w by h box (y down). Pure. */
export function sparkPoints(values: readonly number[], w: number, h: number, min: number, max: number): string {
  if (values.length === 0) return "";
  const span = max - min || 1;
  return values.map((v, i) => {
    const x = values.length === 1 ? w / 2 : (i / (values.length - 1)) * w;
    const y = h - Math.min(1, Math.max(0, (v - min) / span)) * h;
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(" ");
}
