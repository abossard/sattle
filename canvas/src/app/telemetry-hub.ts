// Telemetry state of one graph: the quantized store, the displayed tick, and the metric chosen for each visual channel.
// Tick k is aligned with revision k, so one tick drives both the telemetry frame and the structural revision.
import type { Graph } from "../core/graph";
import { TelemetryStore, dequantize, warnByte } from "../core/telemetry";
import type { Meter, TelemetryBatch, TelemetryFeed } from "../source/types";

export type EdgeChannel = "width" | "flow" | "state";
export type NodeChannel = "ring" | "size" | "state";
/** channel order in the GPU attributes */
export const EDGE_CHANNELS: EdgeChannel[] = ["width", "flow", "state"];
export const NODE_CHANNELS: NodeChannel[] = ["ring", "size", "state"];

export interface Reading { meter: Meter; value: number; warn: boolean }

export class TelemetryHub {
  readonly store: TelemetryStore;
  edge: Record<EdgeChannel, string | null>;
  node: Record<NodeChannel, string | null>;
  enabled = true;
  flow = true;
  /** displayed time in seconds */
  time = 0;
  /** true while the displayed time follows the newest window */
  follow = true;
  hasData = false;
  private snapNodeIds: (string | undefined)[];
  private snapEdgeIds: (string | undefined)[];
  private listeners = new Set<() => void>();

  constructor(private readonly graph: Graph, feed: Pick<TelemetryFeed, "meters"> & { defaults: Partial<TelemetryFeed["defaults"]> }, window = 61) {
    this.store = new TelemetryStore(feed.meters.map((m) => ({ ...m })), window);
    const nz = <T extends Record<string, string>>(o: T) => Object.fromEntries(Object.entries(o).map(([k, v]) => [k, v || null])) as { [K in keyof T]: string | null };
    this.edge = nz(feed.defaults.edge ?? {}) as Record<EdgeChannel, string | null>;
    this.node = nz(feed.defaults.node ?? {}) as Record<NodeChannel, string | null>;
    this.fillDefaults();
    this.snapNodeIds = graph.nodes.map((n) => n?.id);
    this.snapEdgeIds = graph.edges.map((e) => e?.id);
    this.store.setValidity(new Uint8Array(this.snapNodeIds.length).fill(1), new Uint8Array(this.snapEdgeIds.length).fill(1));
  }

  onChange(cb: () => void): () => void { this.listeners.add(cb); return () => this.listeners.delete(cb); }
  private emit(): void { for (const l of this.listeners) l(); }

  get lastTime(): number { return this.store.lastTime; }
  get firstTime(): number { return this.store.firstTime; }

  /** Channels the source left open get the first metric of the right target; state channels get the first metric with a threshold. */
  fillDefaults(): void {
    const e = this.store.metersOf("edge"), n = this.store.metersOf("node");
    if (e.length) { this.edge.width ??= e[0].id; this.edge.flow ??= e[0].id; this.edge.state ??= e.find((m) => m.warn !== undefined)?.id ?? null; }
    if (n.length) { this.node.ring ??= n[0].id; this.node.size ??= n[0].id; this.node.state ??= n.find((m) => m.warn !== undefined)?.id ?? null; }
  }

  /** Create a metric the source did not declare (live engine): range from the values seen, default channels from the first metrics. */
  private ensureMeter(b: TelemetryBatch): void {
    if (this.store.meter(b.target, b.property)) return;
    let mx = 0;
    for (const v of b.values) if (v > mx) mx = v;
    this.store.addMeter({ id: b.property, target: b.target, label: b.property.replace(/^.*\./, ""), unit: "", min: 0, max: mx > 0 ? mx * 1.25 : 1, auto: true });
    if (b.target === "edge") { this.edge.width ??= b.property; this.edge.flow ??= b.property; }
    else { this.node.ring ??= b.property; this.node.size ??= b.property; }
  }

  ingest(b: TelemetryBatch): void {
    this.ensureMeter(b);
    const g = this.graph;
    this.store.ingest(b, (target, id) => (target === "node" ? g.nodeSlot.get(id) : g.edgeSlot.get(id)), Math.max(g.nodes.length, g.edges.length) + 16);
    this.hasData = true;
    if (this.follow && b.t > this.time) this.time = b.t;
    this.emit();
  }

  setTime(t: number): void {
    this.time = t;
    this.follow = t >= this.store.lastTime;
    this.emit();
  }

  /** Mark slots whose element is no longer the snapshot element they held; their dense values are ignored. */
  revalidate(nodeSlots: Iterable<number>, edgeSlots: Iterable<number>): void {
    const g = this.graph;
    const vn = this.store.validNode as Uint8Array, ve = this.store.validEdge as Uint8Array;
    for (const s of nodeSlots) if (s < vn.length) vn[s] = g.nodes[s] && !g.isStub(s) && g.nodes[s]!.id === this.snapNodeIds[s] ? 1 : 0;
    for (const s of edgeSlots) if (s < ve.length) ve[s] = g.edges[s] && g.edges[s]!.id === this.snapEdgeIds[s] ? 1 : 0;
  }
  revalidateAll(): void {
    this.revalidate(this.snapNodeIds.keys(), this.snapEdgeIds.keys());
  }

  setMeterWarn(target: "node" | "edge", id: string, warn: number | undefined): void {
    const m = this.store.meter(target, id);
    if (m) { m.warn = warn; this.emit(); }
  }

  poke(): void { this.emit(); }

  /** Drop stored windows after a re-activation; declarations and channel choices stay. */
  reset(): void { this.store.clear(); this.hasData = false; this.emit(); }

  /** Quantized frame for a channel at the displayed tick, or null when the channel is off. */
  frame(target: "node" | "edge", channel: number): Uint8Array | null {
    const id = target === "edge" ? this.edge[EDGE_CHANNELS[channel]] : this.node[NODE_CHANNELS[channel]];
    return id ? this.store.frameAtTime(target, id, this.time)?.data ?? null : null;
  }
  warnByteOf(target: "node" | "edge"): number {
    const id = target === "edge" ? this.edge.state : this.node.state;
    const m = id ? this.store.meter(target, id) : undefined;
    return m ? warnByte(m) : 256;
  }

  /** Values of every metric of the target kind for one slot at the displayed tick. */
  readings(target: "node" | "edge", slot: number): Reading[] {
    const out: Reading[] = [];
    for (const m of this.store.metersOf(target)) {
      const f = this.store.frameAtTime(target, m.id, this.time);
      if (!f || slot >= f.data.length) continue;
      const v = dequantize(m, f.data[slot]);
      out.push({ meter: m, value: v, warn: m.warn !== undefined && v >= m.warn });
    }
    return out;
  }
}

export function formatValue(m: Meter, v: number): string {
  const digits = Math.abs(v) >= 100 ? 0 : Math.abs(v) >= 10 ? 1 : Math.abs(v) >= 1 ? 2 : 3;
  const base = m.unit === "ms" || m.unit === "req/s" ? v.toFixed(digits) : m.max <= 1 ? `${(v * 100).toFixed(1)}%` : v.toFixed(digits);
  return m.unit && m.max > 1 ? `${base} ${m.unit}` : base;
}
