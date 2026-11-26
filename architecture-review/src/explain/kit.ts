// Shared parts of every explainer: the contract a page section needs, and pure SVG string builders.
// An explainer is data (frames from a pure simulator, one note per frame) plus a pure draw(frame) -> SVG.

export type Swatch = "add" | "remove" | "touched" | "dim" | "accent" | "warn";

export interface Explainer<F> {
  id: string;
  /** The theme id in data/week-source.json this explainer sits next to. */
  theme: string;
  title: string;
  lede: string;
  frames: readonly F[];
  /** One plain sentence per frame. */
  notes: readonly string[];
  legend: readonly { swatch: Swatch; label: string }[];
  draw(frame: F, index: number): string;
}

export const WIDTH = 960;
export const CHIP = { width: 168, height: 28 };

const escape = (value: string) => value.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll('"', "&quot;");

export function svg(height: number, label: string, body: string) {
  return `<svg class="lanes" viewBox="0 0 ${WIDTH} ${height}" width="${WIDTH}" height="${height}" role="img" aria-label="${escape(label)}">
    <defs><marker id="kit-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path class="kit-head" d="M0 0L10 5L0 10z"/></marker></defs>
    ${body}
  </svg>`;
}

/** A vertical lane with its title at the top. */
export function lane(x: number, width: number, height: number, title: string, tone: "" | "calc" | "state" = "") {
  return `<g class="lane ${tone}"><rect x="${x}" y="4" width="${width}" height="${height - 8}" rx="10"/><text class="lane-title" x="${x + 12}" y="26">${escape(title)}</text></g>`;
}

export function arrow(x1: number, y1: number, x2: number, y2 = y1, dashed = false) {
  return `<path class="kit-edge${dashed ? " dashed" : ""}" d="M${x1} ${y1}L${x2} ${y2}" marker-end="url(#kit-arrow)"/>`;
}

export function text(x: number, y: number, value: string, className = "", anchor: "start" | "middle" | "end" = "start") {
  return `<text class="halo ${className}" x="${x}" y="${y}" text-anchor="${anchor}">${escape(value)}</text>`;
}

/**
 * A row chip at its end position. `from` is where it travels from (drawn there first, then moved by CSS);
 * with reduced motion it simply sits at the end position.
 */
export function chip(options: { x: number; y: number; swatch: Swatch; label: string; from?: { x: number; y: number }; delay?: number; width?: number; struck?: boolean }) {
  const width = options.width ?? CHIP.width;
  const dx = options.from ? options.from.x - options.x : 0;
  const dy = options.from ? options.from.y - options.y : 0;
  const travel = options.from ? " travel" : " appear";
  return `<g class="kit-chip ${options.swatch}${travel}${options.struck ? " struck" : ""}" style="--dx:${dx}px;--dy:${dy}px;--delay:${options.delay ?? 0}ms">
    <rect x="${options.x}" y="${options.y}" width="${width}" height="${CHIP.height}" rx="6"/>
    <text x="${options.x + 10}" y="${options.y + 18}">${escape(options.label)}</text>
  </g>`;
}

/** A state box for one key: its name small, its value large. Touched boxes are highlighted, others dim. */
export function box(options: { x: number; y: number; width: number; key: string; value: string; swatch: Swatch; delay?: number }) {
  return `<g class="kit-box ${options.swatch}" style="--delay:${options.delay ?? 0}ms">
    <rect x="${options.x}" y="${options.y}" width="${options.width}" height="52" rx="8"/>
    <text class="box-key" x="${options.x + 10}" y="${options.y + 18}">${escape(options.key)}</text>
    <text class="box-value" x="${options.x + 10}" y="${options.y + 42}">${escape(options.value)}</text>
  </g>`;
}

export const weightLabel = (weight: number, value: string) => `${weight > 0 ? "+1" : "−1"}  ${value}`;
