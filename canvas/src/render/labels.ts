// Where node labels may go: no label over another label or over a node, none cut off at the edge of the pane. Pure: boxes in screen pixels.

/** Boxes taken so far, in a grid of cells so a test looks at its neighbours only. */
export class LabelPlacer {
  private cells = new Map<number, number[]>();
  constructor(private readonly cell = 64) {}

  private span(x0: number, y0: number, w: number, h: number): [number, number, number, number] {
    return [Math.floor(x0 / this.cell), Math.floor((x0 + w) / this.cell), Math.floor(y0 / this.cell), Math.floor((y0 + h) / this.cell)];
  }
  /** Whether a box centred at (x, y) is free. */
  free(x: number, y: number, w: number, h: number): boolean {
    const x0 = x - w / 2, y0 = y - h / 2;
    const [cx0, cx1, cy0, cy1] = this.span(x0, y0, w, h);
    for (let cy = cy0; cy <= cy1; cy++) for (let cx = cx0; cx <= cx1; cx++) {
      const l = this.cells.get(cy * 100003 + cx);
      if (!l) continue;
      for (let k = 0; k < l.length; k += 4) if (x0 < l[k] + l[k + 2] && x0 + w > l[k] && y0 < l[k + 1] + l[k + 3] && y0 + h > l[k + 1]) return false;
    }
    return true;
  }
  /** Take a box centred at (x, y): a label that was drawn, or a node no label may cover. */
  take(x: number, y: number, w: number, h: number): void {
    const x0 = x - w / 2, y0 = y - h / 2;
    const [cx0, cx1, cy0, cy1] = this.span(x0, y0, w, h);
    for (let cy = cy0; cy <= cy1; cy++) for (let cx = cx0; cx <= cx1; cx++) {
      const key = cy * 100003 + cx;
      const l = this.cells.get(key);
      if (l) l.push(x0, y0, w, h); else this.cells.set(key, [x0, y0, w, h]);
    }
  }
  /** Take the box when it is free. */
  place(x: number, y: number, w: number, h: number): boolean {
    if (!this.free(x, y, w, h)) return false;
    this.take(x, y, w, h);
    return true;
  }
}

/** The centre of a label of width `w` moved so that the whole label is inside a pane `paneWidth` wide; a label wider than the pane is centred in it. */
export function keepInside(x: number, w: number, paneWidth: number, margin = 2): number {
  if (w + 2 * margin >= paneWidth) return paneWidth / 2;
  return Math.min(Math.max(x, w / 2 + margin), paneWidth - w / 2 - margin);
}
