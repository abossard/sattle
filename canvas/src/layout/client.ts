import type { LayoutRequest, LayoutResponse } from "./worker";

type Job<T> = T extends { kind: "cancel" } ? never : T extends { id: number } ? Omit<T, "id"> : never;
export type LayoutJobRequest = Job<LayoutRequest>;

export interface LayoutJob { id: number; done: Promise<Float32Array> }

/** One worker per client; a new request cancels the previous one. */
export class LayoutClient {
  private worker = new Worker(new URL("./worker.ts", import.meta.url), { type: "module" });
  private seq = 0;
  private active: { id: number; onProgress: (p: Float32Array, it: number, total: number) => void; resolve: (p: Float32Array) => void; started: number } | null = null;
  lastMs = 0;

  constructor() {
    this.worker.onmessage = (e: MessageEvent<LayoutResponse>) => {
      const m = e.data, a = this.active;
      if (!a || m.id !== a.id) return;
      if (m.type === "progress") a.onProgress(m.positions, m.iteration, m.total);
      else { this.lastMs = m.ms; this.active = null; a.resolve(m.positions); }
    };
  }

  run(req: LayoutJobRequest, onProgress: (p: Float32Array, it: number, total: number) => void = () => {}): LayoutJob {
    this.cancel();
    const id = ++this.seq;
    const done = new Promise<Float32Array>((resolve) => { this.active = { id, onProgress, resolve, started: performance.now() }; });
    const transfer: Transferable[] = [];
    this.worker.postMessage({ ...req, id } as LayoutRequest, transfer);
    return { id, done };
  }

  cancel(): void {
    if (this.active) { this.worker.postMessage({ id: this.active.id, kind: "cancel" } satisfies LayoutRequest); this.active = null; }
  }

  get busy(): boolean { return this.active !== null; }
  dispose(): void { this.worker.terminate(); }
}
