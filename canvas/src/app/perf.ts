// Performance harness, exposed as window.__perf. Used for the measurements recorded in PERF.md.
import type { Workspace } from "./workspace";
import { seekPane as seekPane0, seekTime as seekTime0 } from "../state/ws-ops";
import { useStore } from "../state/store";
const seekPane = (ws: Workspace, g: string, i: number) => seekPane0(ws, g, i, useStore.getState().filters[g]);
const seekTime = (ws: Workspace, t: number) => seekTime0(ws, t, useStore.getState().filters);

export interface FrameStats { cpuMs?: number; labelMs?: number; maxLabelMs?: number; maxCpuMs?: number; maxFrameMs?: number; frames: number; seconds: number; fps: number; p50: number; p95: number; p99: number; max: number; over20ms: number }

const pct = (a: number[], p: number): number => { const s = [...a].sort((x, y) => x - y); return s[Math.min(s.length - 1, Math.floor(p * s.length))] ?? 0; };

function summarize(deltas: number[]): FrameStats {
  const total = deltas.reduce((a, b) => a + b, 0);
  return {
    frames: deltas.length, seconds: +(total / 1000).toFixed(2), fps: +(deltas.length / (total / 1000)).toFixed(1),
    p50: +pct(deltas, 0.5).toFixed(2), p95: +pct(deltas, 0.95).toFixed(2), p99: +pct(deltas, 0.99).toFixed(2), max: +Math.max(...deltas).toFixed(2),
    over20ms: deltas.filter((d) => d > 20).length,
  };
}

/** Drive a callback every animation frame for `ms`, returning rAF-to-rAF intervals. */
let sampler: (() => void) | null = null;
function drive(ms: number, step: (i: number) => void): Promise<number[]> {
  return new Promise((resolve) => {
    const deltas: number[] = [];
    let last = 0, start = 0, i = 0;
    const tick = (t: number) => {
      if (!start) start = t;
      if (last) deltas.push(t - last);
      last = t;
      step(i++);
      sampler?.();
      if (t - start < ms) requestAnimationFrame(tick); else resolve(deltas);
    };
    requestAnimationFrame(tick);
  });
}

export function installPerf(getWs: () => Workspace | null, graph: () => string): void {
  const pane = () => getWs()!.paneFor(graph());
  const sampled = async (run: () => Promise<number[]>): Promise<FrameStats> => {
    const v = pane().view; const cpu: number[] = [], lab: number[] = [];
    sampler = () => { cpu.push(v.stats.cpuMs); lab.push(v.stats.labelMs); };
    const d = await run();
    sampler = null;
    const avg = (a: number[]) => +(a.reduce((x, y) => x + y, 0) / Math.max(1, a.length)).toFixed(2);
    return { ...summarize(d), cpuMs: avg(cpu), labelMs: avg(lab), maxLabelMs: +Math.max(...lab).toFixed(1), maxCpuMs: +Math.max(...cpu).toFixed(1), maxFrameMs: +Math.max(...v.frameTimes.slice(-d.length)).toFixed(1) };
  };
  (window as unknown as { __perf: unknown }).__perf = {
    async pan(ms = 5000): Promise<FrameStats> {
      const v = pane().view; v.fit(false);
      return sampled(() => drive(ms, (i) => v.panBy(Math.sin(i / 40) * 14, Math.cos(i / 55) * 9)));
    },
    async zoom(ms = 5000): Promise<FrameStats> {
      const v = pane().view; v.fit(false);
      const base = v.camera.zoom;
      return sampled(() => drive(ms, (i) => { const target = base * Math.pow(30, (Math.sin(i / 90) + 1) / 2); v.zoomBy(target / v.camera.zoom, v.viewport.w / 2, v.viewport.h / 2); }));
    },
    async panZoomDeep(ms = 5000): Promise<FrameStats> {
      const v = pane().view; v.fit(false);
      const base = v.camera.zoom;
      v.zoomBy(12);
      void base;
      return sampled(() => drive(ms, (i) => { v.panBy(Math.sin(i / 30) * 10, Math.cos(i / 41) * 10); v.zoomBy(1 + Math.sin(i / 70) * 0.01); }));
    },
    /** time of pickAt alone, over random screen positions */
    pickCost(n = 2000): { n: number; avgMs: number; maxMs: number; hitRate: number } {
      const v = pane().view; const { w, h } = v.viewport;
      let sum = 0, max = 0, hits = 0;
      for (let i = 0; i < n; i++) {
        const x = Math.random() * w, y = Math.random() * h;
        const t = performance.now();
        const p = v.pickAt(x, y);
        const d = performance.now() - t;
        sum += d; if (d > max) max = d; if (p) hits++;
      }
      return { n, avgMs: +(sum / n).toFixed(4), maxMs: +max.toFixed(3), hitRate: +(hits / n).toFixed(3) };
    },
    /** pointermove to end of the frame that shows the hover state, in ms */
    async hoverLatency(n = 200): Promise<{ n: number; handlerAvgMs: number; handlerMaxMs: number; p50: number; p95: number; max: number }> {
      const v = pane().view; const r = v.host.getBoundingClientRect();
      const lat: number[] = [], handler: number[] = [];
      for (let i = 0; i < n; i++) {
        const x = r.left + 20 + Math.random() * (r.width - 40), y = r.top + 20 + Math.random() * (r.height - 40);
        const t0 = performance.now();
        v.host.dispatchEvent(new PointerEvent("pointermove", { clientX: x, clientY: y, bubbles: true, pointerId: 1 }));
        handler.push(performance.now() - t0);
        await new Promise<void>((res) => requestAnimationFrame(() => requestAnimationFrame(() => res())));
        lat.push(v.stats.lastInputLatencyMs || performance.now() - t0);
        void t0;
      }
      return { n, handlerAvgMs: +(handler.reduce((a, b) => a + b, 0) / n).toFixed(3), handlerMaxMs: +Math.max(...handler).toFixed(2), p50: +pct(lat, 0.5).toFixed(2), p95: +pct(lat, 0.95).toFixed(2), max: +Math.max(...lat).toFixed(2) };
    },
    /** select a node: synchronous cost of ws.select plus the following frame */
    async selectLatency(n = 100): Promise<{ n: number; syncAvgMs: number; syncMaxMs: number; frameAvgMs: number }> {
      const ws = getWs()!; const g = graph(); const p = pane();
      const ids = [...p.graph.entities()].filter((_, i) => i % Math.max(1, Math.floor(p.graph.nodeCount / n)) === 0).slice(0, n).map((e) => e.id);
      let sync = 0, smax = 0, frame = 0;
      for (const id of ids) {
        const t0 = performance.now();
        ws.select({ graph: g, kind: "entity", id });
        const t1 = performance.now();
        await new Promise<void>((res) => requestAnimationFrame(() => res()));
        const t2 = performance.now();
        sync += t1 - t0; smax = Math.max(smax, t1 - t0); frame += t2 - t1;
      }
      ws.select(null);
      return { n: ids.length, syncAvgMs: +(sync / ids.length).toFixed(3), syncMaxMs: +smax.toFixed(3), frameAvgMs: +(frame / ids.length).toFixed(2) };
    },
    /** replay all patches forward from revision 0, timing incremental application */
    async patchReplay(): Promise<{ patches: number; edits: number; applyAvgMs: number; applyMaxMs: number; relayouts: number; positionsChanged: number }> {
      const ws = getWs()!; const g = graph(); const hist = ws.histories.get(g)!; const p = pane();
      seekPane(ws, g, 0);
      await new Promise((r) => setTimeout(r, 50));
      const before = new Float32Array(p.scene.positions.subarray(0, p.graph.nodes.length * 2));
      const jobs = p.layoutClient.busy;
      const times: number[] = [];
      let edits = 0;
      for (let i = 1; i <= hist.length; i++) {
        const t = performance.now();
        seekPane(ws, g, i);
        times.push(performance.now() - t);
        edits += hist.patches[i - 1].edits.length;
        await new Promise<void>((r) => requestAnimationFrame(() => r()));
      }
      let moved = 0;
      for (let i = 0; i < before.length; i += 2) if (Number.isFinite(before[i]) && (p.scene.positions[i] !== before[i] || p.scene.positions[i + 1] !== before[i + 1])) moved++;
      void jobs;
      return { patches: hist.length, edits, applyAvgMs: +(times.reduce((a, b) => a + b, 0) / times.length).toFixed(3), applyMaxMs: +Math.max(...times).toFixed(3), relayouts: p.layoutClient.busy ? 1 : 0, positionsChanged: moved };
    },
    /**
     * Telemetry at `hz` ticks per second on every node and edge, with the camera idle, panning, or zoomed in with flow pulses on.
     * Needs the synth-100k-stream source (simulated stream). Reports frame statistics and the per-tick costs.
     */
    async telemetry(hz = 10, ms = 6000, mode: "idle" | "pan" | "zoomed" = "pan"): Promise<Record<string, unknown>> {
      const ws = getWs()!; const p = pane(); const v = p.view; const hub = ws.hubs.get(graph())!;
      const ctl = ws.source.controls!;
      v.fit(false);
      if (mode === "zoomed") { v.zoomBy(14); }
      await new Promise((r) => setTimeout(r, 400));
      const t0 = hub.lastTime, c0 = hub.store.stats.ticks;
      ctl.setSpeed(hz); ctl.play();
      const cpu: number[] = [], lab: number[] = [];
      sampler = () => { cpu.push(v.stats.cpuMs); lab.push(v.stats.labelMs); };
      const d = await drive(ms, (i) => { if (mode !== "idle") v.panBy(Math.sin(i / 40) * 10, Math.cos(i / 55) * 7); });
      sampler = null;
      ctl.pause();
      const ticks = Math.round((hub.lastTime - t0) / (hub.store.frameTimes().length > 1 ? hub.store.frameTimes()[1] - hub.store.frameTimes()[0] : 1));
      const avg = (a: number[]) => +(a.reduce((x, y) => x + y, 0) / Math.max(1, a.length)).toFixed(2);
      return {
        mode, requestedHz: hz, achievedHz: +(ticks / (ms / 1000)).toFixed(1), ticks, ingestedBatches: hub.store.stats.ticks - c0,
        ...summarize(d), cpuMs: avg(cpu), labelMs: avg(lab), maxCpuMs: +Math.max(...cpu).toFixed(1),
        ingestAvgMs: +hub.store.stats.avgMs.toFixed(2), applyAvgMs: +p.telemetryApplyMs.toFixed(2), flowActive: v.flowActive,
        edges: p.graph.edgeCount, nodes: p.graph.nodeCount, valuesPerTick: p.graph.edgeCount * 3 + p.graph.nodeCount * 2,
      };
    },
    /** scrub through the stored telemetry window while panning; measures frame time with a tick change every frame */
    async scrub(ms = 4000): Promise<FrameStats> {
      const ws = getWs()!; const g = graph(); const hub = ws.hubs.get(g)!; const v = pane().view;
      v.fit(false);
      const times = hub.store.frameTimes();
      return sampled(() => drive(ms, (i) => { seekTime(ws, times[Math.floor((Math.sin(i / 25) + 1) / 2 * (times.length - 1))]); v.panBy(Math.sin(i / 40) * 8, 0); }));
    },
    /** frame interval at fixed zoom levels, with passes disabled in turn (diagnostic) */
    async probe(): Promise<Record<string, number>> {
      const v = pane().view; v.fit(false);
      const s = v.scene; const base = v.camera.zoom; const c0 = { ...v.camera };
      const out: Record<string, number> = {};
      const measure = async () => { const d = await drive(700, (i) => v.panBy(i % 2 ? 1 : -1, 0)); return +(d.reduce((a, b) => a + b, 0) / d.length).toFixed(1); };
      const nodes = s.nodeCount, edges = s.edgeCount;
      for (const k of [1, 3, 10, 30]) {
        v.camera = { x: c0.x, y: c0.y, zoom: base * k };
        out[`all x${k}`] = await measure();
        s.edgeCount = 0; out[`nodes only x${k}`] = await measure(); s.edgeCount = edges;
        s.nodeCount = 0; out[`edges only x${k}`] = await measure(); s.nodeCount = nodes;
      }
      return out;
    },
    memory(): number | null { return (performance as unknown as { memory?: { usedJSHeapSize: number } }).memory?.usedJSHeapSize ?? null; },
  };
}
