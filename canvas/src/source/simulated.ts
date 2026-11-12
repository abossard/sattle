// Simulated stream: replays the recorded patches of an inner source under timing controls (play, pause, step, speed, reset).
// Each tick delivers the next patch of every graph that still has one.
import type { Patch, Snapshot } from "../core/model";
import type { Rejection } from "../core/validate";
import type { Correspondence, GraphSource, DocumentText, Lineage, Reference, ReverseLineage, SpaceInfo, StreamControls, StreamEvent, TelemetryFeed } from "./types";

export class SimulatedStream implements GraphSource, StreamControls {
  readonly name: string;
  private queues = new Map<string, { patches: Patch[]; next: number; sinks: Set<(e: StreamEvent) => void> }>();
  private timer: ReturnType<typeof setInterval> | null = null;
  private tick_ = 0;
  private listeners = new Set<() => void>();
  state: StreamControls["state"] = "idle";
  speed = 2;
  delivered = 0;
  total = 0;
  /** last tick delivered; tick k is delivered together with patch k */
  tick = 0;
  readonly controls = this;

  constructor(private readonly inner: GraphSource) { this.name = `${inner.name} (simulated stream)`; }

  /** Telemetry of the inner source; only tick 0 is loaded up front, later ticks arrive as stream events. */
  get telemetry(): TelemetryFeed | undefined {
    const t = this.inner.telemetry;
    return t ? { ...t, ticks: 0 } : undefined;
  }

  open(): Promise<SpaceInfo> { return this.inner.open(); }
  snapshot(graph: string): Promise<Snapshot> { return this.inner.snapshot(graph); }
  correspondences(graph: string): Promise<Correspondence[]> { return this.inner.correspondences?.(graph) ?? Promise.resolve([]); }
  onRejected(cb: (r: Rejection) => void): () => void { return this.inner.onRejected(cb); }
  meters(graph: string) { return this.inner.meters?.(graph) ?? Promise.resolve(null); }
  references(graph: string): Promise<Reference[]> { return this.inner.references?.(graph) ?? Promise.resolve([]); }
  lineage(graph: string, element: string): Promise<Lineage | null> { return this.inner.lineage?.(graph, element) ?? Promise.resolve(null); }
  reverseLineage(graph: string, element: string): Promise<ReverseLineage | null> { return this.inner.reverseLineage?.(graph, element) ?? Promise.resolve(null); }
  document(graph: string, revision?: string): Promise<DocumentText | null> { return this.inner.document?.(graph, revision) ?? Promise.resolve(null); }

  subscribe(graph: string, sink: (e: StreamEvent) => void): () => void {
    let q = this.queues.get(graph);
    if (!q) {
      const queue = { patches: [] as Patch[], next: 0, sinks: new Set<(e: StreamEvent) => void>() };
      q = queue;
      this.queues.set(graph, queue);
      // load the recorded patches from the inner source; delivery is ours
      this.inner.subscribe(graph, (e) => {
        if (e.type === "patch") { queue.patches.push(e.patch); this.total++; this.changed(); }
        // telemetry the inner source has by windows (not by tick) is all there from the start
        else if (e.type === "telemetry") for (const s of queue.sinks) s(e);
      });
    }
    q.sinks.add(sink);
    return () => q!.sinks.delete(sink);
  }

  private advance(): void {
    let any = false;
    const feed = this.inner.telemetry;
    const nextTick = this.tick_ + 1;
    if (feed && nextTick <= feed.maxTicks) {
      any = true;
      this.tick_ = nextTick;
      this.tick = nextTick;
      for (const [graph, q] of this.queues) {
        if (q.sinks.size === 0) continue;
        void feed.fetch(graph, nextTick).then(({ batches, release }) => {
          for (const batch of batches) for (const sink of q.sinks) sink({ type: "telemetry", batch });
          release?.();
        });
      }
    }
    for (const q of this.queues.values()) {
      if (q.next >= q.patches.length) continue;
      const patch = q.patches[q.next++];
      this.delivered++;
      any = true;
      for (const s of q.sinks) s({ type: "patch", patch });
    }
    if (!any) {
      this.state = "ended";
      this.stop();
      for (const q of this.queues.values()) for (const s of q.sinks) s({ type: "end" });
    }
    this.changed();
  }

  /** The workspace is gone: no more ticks, and the inner source (workers) is released. */
  dispose(): void { this.stop(); this.listeners.clear(); this.inner.dispose?.(); }
  private stop(): void { if (this.timer) { clearInterval(this.timer); this.timer = null; } }
  private start(): void { this.stop(); this.timer = setInterval(() => this.advance(), Math.max(10, 1000 / this.speed)); }
  private changed(): void { for (const l of this.listeners) l(); }

  play(): void { if (this.state === "ended") return; this.state = "playing"; this.start(); this.changed(); }
  pause(): void { this.state = "paused"; this.stop(); this.changed(); }
  step(): void { if (this.state === "playing") this.pause(); this.advance(); if (this.state !== "ended") this.state = "paused"; this.changed(); }
  reset(): void {
    this.stop();
    for (const q of this.queues.values()) q.next = 0;
    this.delivered = 0;
    this.tick_ = 0; this.tick = 0;
    this.state = "idle";
    this.changed();
  }
  setSpeed(patchesPerSecond: number): void {
    this.speed = Math.max(0.1, Math.min(60, patchesPerSecond));
    if (this.state === "playing") this.start();
    this.changed();
  }
  onChange(cb: () => void): () => void { this.listeners.add(cb); return () => this.listeners.delete(cb); }
}
