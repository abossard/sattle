# Performance

Target: 100,000 nodes and 300,000 edges, smooth pan and zoom at 60 fps, hover and select under one frame, patches applied without a full re-layout, and telemetry updating every node and edge at 10 to 60 ticks per second while holding 60 fps.

## Data set

- Synthetic cloud estate, preset `100k` in `src/core/synth.ts`, seed 1.
- 99,495 nodes, 287,703 edges (subscription, cluster, VM node, namespace, workload, service, pod, with `MemberOf`, `HostedOn`, `Routes`, `Calls`).
- Plus a deterministic stream of patches for the replay test: 120 patches (322 edits) in the structural runs below, 60 patches in the telemetry runs (the source default).
- Telemetry (section below): per tick 3 metrics on all 287,968 edges and 2 metrics on all 99,485 nodes, 1,062,871 values per tick.

## Method

- `npm run build`, then `npx vite preview --port 4173`, then `npm run perf` (`scripts/perf-cdp.mjs`).
- The script starts headless Chrome with the GPU enabled (ANGLE Metal) over the DevTools protocol, opens the app on the 100k source, waits for the layout worker to finish, and runs the in-page harness `window.__perf` (`src/app/perf.ts`).
- Machine: Apple M1 Max, macOS, Google Chrome headless, viewport 1440x900 at device pixel ratio 2 (2880x1800 pixels). The machine was shared with other work sessions, so numbers vary between runs. Three runs are listed.
- Frame rate: `requestAnimationFrame` to `requestAnimationFrame` intervals while the camera is moved by code on every frame for 5 s. One frame is 16.7 ms at 60 Hz, so p50 = 16.7 means vsync-limited.
  - pan: sinusoidal pan at the fitted zoom, whole graph visible.
  - zoom: continuous zoom in and out over a 30x range around the centre.
  - panZoomDeep: pan and zoom together at about 12x, labels active.
- Pick cost: `pickAt` at 2,000 random screen positions (node grid, then an edge scan when no node is hit).
- Hover handler: time to dispatch a synthetic `pointermove` through the real handler (pick, flag update, hot buffers).
- Patch replay: reset to the snapshot, then apply the 120 patches one by one through the real pane path (history, graph, scene, incremental placement).

## Results (3 runs)

| Metric | Run 1 | Run 2 | Run 3 |
| --- | --- | --- | --- |
| Pan, fps (p50 / p99 frame) | 60.0 (16.7 / 16.8) | 44.7 (16.7 / 200) | 59.4 (16.7 / 16.8) |
| Zoom, fps (p50 / p99) | 59.2 (16.7 / 33.4) | 60.0 (16.7 / 16.8) | 55.4 (16.7 / 83.3) |
| Pan and zoom at 12x, fps | 60.0 | 59.8 | 60.0 |
| CPU time per frame, WebGL submit | 0.05 to 0.11 ms | 0.07 to 0.10 ms | 0.05 to 0.11 ms |
| CPU time per frame, label layer | 2.1 ms avg | 1.6 to 2.6 ms | 1.0 to 2.5 ms |
| Pick, avg / max | 1.41 / 8.3 ms | 1.34 / 10.0 ms | 1.36 / 7.8 ms |
| Hover handler, avg / max | 2.1 / 8.0 ms | 1.9 / 10 ms | 2.0 / 7.5 ms |
| Select (`ws.select`), sync avg / max | 0.06 / 0.4 ms | 0.08 / 0.4 ms | 0.08 / 0.4 ms |
| Patch apply, avg / max per patch | 0.07 / 0.3 ms | 0.09 / 0.2 ms | 0.09 / 0.3 ms |

- Run 2 pan contains a burst of slow frames (one 267 ms frame) while the CPU time of every frame stayed under 10 ms: that is contention on the shared machine, not the renderer. Run 3 zoom has one 62 ms label-layer spike (re-sorting 100k label priorities when the zoom crosses the density threshold; throttled to once per second).
- Hover and select: the handler work is 1 to 2 ms; the highlight appears in the next frame, so the visible latency is one frame (16.7 ms at 60 Hz) plus the handler. Event to end of the frame that shows it measured 19 to 22 ms p50 with a double rAF wait in the harness, which includes one full vsync.
- Patch replay: 0 re-layouts, 14 node positions changed across the 120 patches (the added nodes, placed next to their neighbours). Existing nodes did not move.
- Layout worker: 7.2 to 7.4 s for the full 100k force layout (60 iterations, Barnes-Hut theta 1.4, spiral seeded by breadth-first order); the first positions stream to the screen after about 0.3 s and the view is usable while it settles. Under heavy machine load it took up to 44 s. Layout runs off the main thread, the main thread stays at 60 fps while it runs.
- Load: generate 0.15 s, index into the graph 0.18 s, first scene 0.6 s. JS heap 160 to 230 MB with the full graph and 120 patches in memory.
- Multi graph view on the 100k source (master plus the 10k-node ownership tree plus the 9.6k-node dependency graph, three WebGL contexts): loaded and laid out; frame rate not measured separately.

## What made it fast

- Instanced rendering: one draw per pass (edges, arrowheads, nodes, plus a small "hot" pass for selection and neighbours). Positions live in a float texture indexed by node slot, so layout progress updates one texture, not 288k edges.
- SDF shaders for node shapes and state rings; node quads padded by 2 px unless a state ring is set. Before this change (7 px padding for all) zooming ran at 22 to 33 fps because 100k quads cost about 110M fragments per frame.
- Edge level of detail: edges with both ends beyond one viewport side are culled in the vertex shader. While the camera moves, a feedback loop thins edges (hash sampled, up to 1 in 48) when frames exceed 22 ms, and restores full density 220 ms after the camera stops. Before this change, zooming in 10x cost 44 ms per frame, almost all of it edge fill.
- Labels: priority-ordered, collision checked on a 64 px hash, capped at 320, from the spatial grid when few nodes are in view.
- Picking: uniform grid over node positions (nodes), linear scan with bounding-box rejection (edges, 1.4 ms at 288k).

## Not measured

- Safari, Firefox, integrated or older GPUs, device pixel ratio 1, touch devices.
- The in-app browser pane of the desktop client was hidden during the session, which throttles animation frames to 1 per second, so no numbers were taken there.
- Force layout quality at 100k: the pod layer bridges workloads and VM nodes, so the full graph settles into a dense disc. Zoomed in it is readable; the overview is not informative.
- A stream of patches arriving while the user pans (only sequential replay was timed).

## Telemetry

Target: new values for every node and edge, every tick, at a stated rate, with the camera moving, at 60 fps.

### Method

- Source `synth-100k-stream` (simulated stream over the synthetic estate). Ticks are produced by a Web Worker (a pure function of element and tick, five `Float32Array`s per tick, 4.2 MB, buffers recycled), transferred to the page, quantized to 8 bits per metric, copied into three GPU byte channels per target, and uploaded.
- `__perf.telemetry(hz, ms, mode)` plays the stream at `hz` ticks per second for 5 s and measures rAF intervals.
  - `idle`: camera still, whole graph visible.
  - `pan`: camera panning, whole graph visible.
  - `zoomed`: camera at about 14x, panning, pulses animating (about 8k edges on screen).
- Same machine, Chrome and viewport as above. Per-tick costs are main-thread times measured in the page.

### Results (3 runs)

| Mode and rate | Run 1 fps (p99 frame) | Run 2 fps (p99) | Run 3 fps (p99) |
| --- | --- | --- | --- |
| idle, 10 ticks/s | 60.0 (16.8) | 60.0 (16.8) | 60.0 (16.8) |
| pan, 10 ticks/s | 57.0 (50.1) | 59.8 (16.8) | 60.0 (16.8) |
| pan, 30 ticks/s | 59.6 (16.8) | 59.4 (33.3) | 60.0 (16.8) |
| pan, 60 ticks/s | 53.4 (66.8) | 59.8 (16.8) | 59.4 (33.3) |
| zoomed with pulses, 10 ticks/s | 59.8 (16.8) | 59.6 (16.8) | 59.6 (16.8) |
| zoomed with pulses, 60 ticks/s | 59.8 (16.8) | 60.0 (16.8) | 59.4 (33.2) |

- Achieved rate matched the requested rate (9.8 to 10, 30.2, 61 to 62 ticks per second): the page kept up with 60 ticks per second, which is about 64 million values per second.
- Main-thread cost per tick: ingest (quantize) 0.5 to 0.7 ms per metric batch, so about 2.7 ms for the five batches of a tick; copy into the GPU channels 0.03 to 0.06 ms; upload and render CPU 0.05 to 0.12 ms per frame. Generation is in the worker. At 60 ticks per second the main thread spends about 16% of its time on telemetry.
- Run 1 pan at 10 and 60 ticks/s and run 1 static pan have a few slow frames (p99 50 to 67 ms, max 183 ms) while per-frame CPU stayed under 1 ms: contention on the shared machine, as in the structural runs.
- Static source with the telemetry window loaded (61 ticks) and telemetry drawn: pan 59 to 60 fps, zoom 58.6 to 59.2, pan and zoom at 12x 56.4 to 60 (one 217 ms hiccup in run 2), timeline scrub through the stored window while panning 55.5 to 60 fps (every frame changes the tick and re-uploads three edge and three node channels). Hover handler 0.9 to 1.1 ms, select 0.07 ms, patch apply 0.09 to 0.10 ms, 0 re-layouts.
- JS heap 260 to 420 MB with 61 ticks of 3 edge and 2 node metrics stored (about 65 MB of quantized frames) plus the graph.

### What keeps it fast

- Telemetry changes one byte per element per channel and never touches instance data, positions or the layout. Three byte buffers per target are replaced per tick (about 1.2 MB).
- 8-bit quantization halves GPU memory traffic against 16-bit and lets frames be stored and uploaded as they are; the ring keeps the last 61 ticks for scrubbing.
- Pulses animate only when edges are at least 30 px long on screen and fewer than about 40k are visible; otherwise the view redraws only on ticks. Without that limit, idle at the overview with pulses ran at 17 to 29 fps (full-density edges every frame). The edge thinning feedback loop also applies while pulses animate.
- Node ring gauges and padding are only drawn above 3 px radius, so the 100k overview pays no extra fragment cost.

### Not measured (telemetry)

- Telemetry on derived graphs (none is produced).
- Sparse batches at this volume (the 100k path is dense; sparse is for small deltas and late additions).
- Rates above 60 ticks per second, and a real engine transport (WebSocket decode cost).
- The 17 to 29 fps case above (pulses at the overview) is avoided by the visibility limit, not fixed.

## Re-measurement after the engine source and the shared time axis

Same method, machine and data (`synth-100k`, `synth-100k-stream`), two runs, after adding the shared time axis, telemetry windows keyed by time, derived-graph telemetry (the ownership tree and the dependency graph now also receive 5 metrics per tick from their own workers) and the engine source.

| Metric | Run 1 | Run 2 |
| --- | --- | --- |
| Pan, fps (p99 frame) | 60.0 (16.8) | 57.6 (33.4) |
| Zoom, fps (p99) | 54.6 (50.0) | 58.6 (50.0) |
| Pan and zoom at 12x, fps | 60.0 | 60.0 |
| Scrub the stored window while panning (every frame moves the shared time), fps | 59.5 | 57.5 |
| Telemetry 10 ticks/s on all nodes and edges, pan, fps | 60.0 | 59.0 |
| Telemetry 60 ticks/s, pan, fps | 57.2 | 60.0 |
| Telemetry 60 ticks/s, zoomed with pulses, fps | 59.6 | 60.0 |
| Achieved telemetry rate at 60 requested | 58.0 | 61.2 |
| Ingest per metric batch, apply to GPU channels | 0.5 to 0.6 ms, 0.03 to 0.05 ms | 0.5 to 0.6 ms, 0.04 to 0.05 ms |
| Hover handler, avg | 0.94 ms | 1.07 ms |
| Patch apply, avg / max | 0.07 / 0.2 ms | 0.09 / 0.2 ms |
| JS heap | 310 to 457 MB | 302 to 441 MB |

- No regression against the earlier runs. The occasional slow frames (zoom p99 50 ms, one 250 ms frame in run 1) have per-frame CPU under 1 ms, as before: contention on the shared machine.
- The heap is higher than before (260 to 420 MB) because three telemetry rings are kept (master 61 windows, the two derived graphs 241 windows each, about 10k elements).
- Time axis cost: building the axis points (merge of all patch and window times) runs on every timeline update, which is once per animation frame while telemetry streams; it is a merge of a few hundred numbers and did not show in the frame CPU.

## Engine source

Measured against the engine as run on the lab fixtures and on the newest real capture (243,457 records, master 146 revisions, 434 nodes, serviceTree 44 revisions, 61 and 9 telemetry windows), in the in-app browser through the dev proxy: first view in 100 to 160 ms after the engine answered (HTTP history, 3 graphs including the 885-node catalog, no live traffic). Live arrival over the WebSocket (patches, windows, logical time) was observed on a paced replay. Throughput of the live socket at scale was not measured: the engine's lab data produces a few messages per second.

## Re-measurement with the React shell

Same method, machine and data. The shell is now React 19, Fluent UI and Zustand; the WebGL view, workers and telemetry stores are unchanged and mounted by `CanvasHost`. One run after the change below (the machine was shared, as before).

| Metric | Run |
| --- | --- |
| Pan, fps (p99 frame) | 60.0 (16.8) |
| Zoom, fps (p99) | 59.4 (33.3) |
| Pan and zoom at 12x, fps | 60.0 |
| Scrub the stored window while panning, fps | 57.5 |
| Telemetry 10 ticks/s on all nodes and edges, idle and pan, fps | 57.6, 58.6 |
| Telemetry 60 ticks/s, pan, fps (achieved rate) | 59.8 (61.4 ticks/s) |
| Telemetry 60 ticks/s, zoomed with pulses, fps (achieved rate) | 59.4 (60.8 ticks/s) |
| Ingest per metric batch, apply to GPU channels | 0.52 ms, 0.04 ms |
| Hover handler, avg | 1.7 ms |
| Patch apply, avg / max | 0.11 / 0.3 ms |
| JS heap | 308 to 470 MB |

- Finding: a first run with the shell reached only 33 ticks/s at 60 requested. Cause: every telemetry apply made the pane report a change, which became a store action per frame and re-rendered the panels that select the graph version. Fix: panes report changes through `data/touched`, which effects coalesce to at most 4 summaries per second, and patches and telemetry frames do not change the state at all (`feed/patch` and `feed/telemetry` are applied to the runtime by effects; summaries are throttled to 100 and 250 ms). After the fix the rates are back to the earlier numbers (above), so telemetry frames do not render React components.
- The first view of the 100k estate takes about 10 s to lay out in the worker, as before; the shell itself shows after a few hundred ms.
- Editor: the node canvas is SVG and holds tens of nodes (the health-model projections have 20 to 40 cards); it was not measured at larger sizes.
