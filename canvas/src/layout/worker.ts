// Layout worker: runs force and layered layouts off the main thread and streams positions back.
import { ForceLayout, defaultForce, type ForceOptions } from "./force";
import { layeredLayout, defaultLayered, type LayeredOptions } from "./layered";

export type LayoutRequest =
  | { id: number; kind: "force"; n: number; src: Int32Array; dst: Int32Array; positions?: Float32Array; options?: Partial<ForceOptions> }
  | { id: number; kind: "layered"; n: number; src: Int32Array; dst: Int32Array; width?: Float32Array; height?: Float32Array; options?: Partial<LayeredOptions> }
  | { id: number; kind: "cancel" };
export type LayoutResponse =
  | { id: number; type: "progress"; positions: Float32Array; iteration: number; total: number }
  | { id: number; type: "done"; positions: Float32Array; ms: number };

const ctx = self as unknown as { postMessage(m: LayoutResponse, t?: Transferable[]): void; onmessage: ((e: MessageEvent<LayoutRequest>) => void) | null };
let current = 0;

ctx.onmessage = async (e) => {
  const req = e.data;
  if (req.kind === "cancel") { current = -1; return; }
  current = req.id;
  const t0 = performance.now();
  if (req.kind === "layered") {
    const r = layeredLayout({ n: req.n, src: req.src, dst: req.dst, width: req.width, height: req.height }, { ...defaultLayered, ...req.options });
    ctx.postMessage({ id: req.id, type: "done", positions: r.positions, ms: performance.now() - t0 }, [r.positions.buffer]);
    return;
  }
  const big = req.n > 50000;
  const iterations = req.options?.iterations ?? (big ? 60 : req.n > 20000 ? 90 : 220);
  const f = new ForceLayout({ n: req.n, src: req.src, dst: req.dst, positions: req.positions }, { ...defaultForce, theta: big ? 1.4 : req.n > 20000 ? 1.1 : 0.9, ...req.options, iterations });
  let lastPost = performance.now();
  while (!f.done) {
    f.step(req.n > 20000 ? 2 : 6);
    const now = performance.now();
    if (now - lastPost > 120) {
      ctx.postMessage({ id: req.id, type: "progress", positions: f.positions.slice(), iteration: f.iteration, total: iterations });
      lastPost = now;
      await new Promise((r) => setTimeout(r, 0)); // let a cancel message through
      if (current !== req.id) return;
    }
  }
  const out = f.positions.slice();
  ctx.postMessage({ id: req.id, type: "done", positions: out, ms: performance.now() - t0 }, [out.buffer]);
};
