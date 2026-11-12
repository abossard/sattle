// Where the cards and wires of a projection go: columns by data flow, left to right, every card after what it reads. A calculation
// over the document, the same for the same document, so the canvas has no positions to store and nothing to tidy by hand.
import type { ProjectionDoc } from "../editing/doc";

export interface CardPort { portId: string; name: string; kind: string; y: number; from: string | null; fromName: string; /** it reads the step before it in its row set: the stack holds that wire, not the author */ inChain: boolean }
export interface Card {
  id: string; statement: string | null;
  title: string; name: string; lines: string[]; more: number; problems: number;
  x: number; y: number; w: number; h: number; column: number;
  ins: CardPort[];
  /** the output port, null for a sink. `readable` when others can read it: the end of a row set or an input of the projection */
  out: { portId: string; kind: string; y: number; readable: boolean } | null;
}
export interface Wire { id: string; from: string; to: string; fromCard: string; toCard: string; d: string; x1: number; y1: number; x2: number; y2: number }
export interface Layout { cards: Card[]; wires: Wire[]; width: number; height: number }
export interface Rect { left: number; top: number; width: number; height: number }

export const CARD_W = 232, HEAD = 30, LINE = 16, PORT = 26, GAP_X = 72, GAP_Y = 16, PAD = 16, MAX_LINES = 4;
const ownerOf = (port: string): string => port.slice(0, port.lastIndexOf("/port:"));

/** Cards and wires of `doc`. `lines` gives what a step's card says about it. */
export function layoutOf(doc: ProjectionDoc, title: (kind: string) => string, lines: (stepId: string) => string[]): Layout {
  const cards = new Map<string, Card>();
  const bare = (id: string, statement: string | null, t: string, name: string, ls: string[], problems: number): Card => {
    const shown = ls.slice(0, MAX_LINES);
    const c: Card = { id, statement, title: t, name, lines: shown, more: ls.length - shown.length, problems, x: 0, y: 0, w: CARD_W, h: 0, column: 0, ins: [], out: null };
    cards.set(id, c);
    return c;
  };
  for (const i of doc.inputs) bare(i.id, null, "in", i.name, [`${i.kind} ${i.detail}`.trim()], 0).out = { portId: i.id, kind: i.kind, y: 0, readable: true };
  for (const st of doc.statements) st.steps.forEach((s, n) => {
    const c = bare(s.id, st.name, title(s.kind), st.steps.length > 1 ? `${st.name} ${n + 1}/${st.steps.length}` : st.name, lines(s.id), s.missing.length + s.diagnostics.length);
    c.ins = s.inputs.map((i) => ({ portId: i.portId, name: i.port, kind: i.kind, y: 0, from: i.from, fromName: i.fromName, inChain: i.inChain }));
    if (s.out) c.out = { portId: s.out, kind: "rows", y: 0, readable: n === st.steps.length - 1 };
  });
  const cardOfPort = (port: string): Card | undefined => cards.get(port) ?? cards.get(ownerOf(port));
  // columns: one more than the furthest thing a card reads
  const depth = (c: Card, trail: Set<string>): number => {
    if (c.column || trail.has(c.id) || !c.ins.length) return c.column;
    trail.add(c.id);
    c.column = 1 + Math.max(0, ...c.ins.map((i) => { const s = i.from ? cardOfPort(i.from) : undefined; return s ? depth(s, trail) : 0; }));
    return c.column;
  };
  for (const c of cards.values()) depth(c, new Set());
  // rows: a card stands near what it reads (the mean row of its sources), in document order otherwise
  const columns: Card[][] = [];
  for (const c of cards.values()) (columns[c.column] ??= []).push(c);
  const rank = new Map<string, number>();
  let height = 0;
  columns.forEach((col, n) => {
    const order = new Map(col.map((c, i) => [c.id, i]));
    const mean = (c: Card): number => { const rs = c.ins.flatMap((i) => { const s = i.from ? cardOfPort(i.from) : undefined; const r = s && rank.get(s.id); return r === undefined ? [] : [r]; }); return rs.length ? rs.reduce((a, b) => a + b, 0) / rs.length : Number.MAX_SAFE_INTEGER; };
    if (n > 0) { const m = new Map(col.map((c) => [c.id, mean(c)])); col.sort((a, b) => m.get(a.id)! - m.get(b.id)! || order.get(a.id)! - order.get(b.id)!); }
    let y = PAD;
    for (const c of col) {
      c.x = PAD + n * (CARD_W + GAP_X); c.y = y;
      c.h = HEAD + Math.max((c.lines.length + (c.more ? 1 : 0)) * LINE, c.ins.length * PORT) + 8;
      c.ins.forEach((p, i) => { p.y = c.y + HEAD + i * PORT + PORT / 2; });
      if (c.out) c.out.y = c.y + HEAD / 2;
      rank.set(c.id, y + c.h / 2);
      y += c.h + GAP_Y;
    }
    height = Math.max(height, y);
  });
  const wires: Wire[] = [];
  for (const c of cards.values()) for (const p of c.ins) {
    const s = p.from ? cardOfPort(p.from) : undefined;
    if (!s || !p.from) continue;
    const x1 = s.x + s.w, y1 = s.out?.portId === p.from ? s.out.y : s.y + HEAD / 2, x2 = c.x, y2 = p.y;
    const bend = Math.max(36, Math.abs(x2 - x1) / 2);
    wires.push({ id: `${p.from}>${p.portId}`, from: p.from, to: p.portId, fromCard: s.id, toCard: c.id, x1, y1, x2, y2, d: `M ${x1} ${y1} C ${x1 + bend} ${y1}, ${x2 - bend} ${y2}, ${x2} ${y2}` });
  }
  return { cards: [...cards.values()], wires, width: PAD * 2 + Math.max(1, columns.length) * (CARD_W + GAP_X) - GAP_X, height: height + PAD };
}

/** The cards and wires a view shows: those that touch `view` grown by `margin`. */
export function inView(l: Layout, view: Rect, margin = 240): { cards: Card[]; wires: Wire[] } {
  const x0 = view.left - margin, x1 = view.left + view.width + margin, y0 = view.top - margin, y1 = view.top + view.height + margin;
  return {
    cards: l.cards.filter((c) => c.x < x1 && c.x + c.w > x0 && c.y < y1 && c.y + c.h > y0),
    wires: l.wires.filter((w) => Math.min(w.x1, w.x2) < x1 && Math.max(w.x1, w.x2) > x0 && Math.min(w.y1, w.y2) < y1 && Math.max(w.y1, w.y2) > y0),
  };
}

/** The card next to `id` in a direction: along a wire to the left and right, along the column up and down. */
export function neighbour(l: Layout, id: string, key: "ArrowLeft" | "ArrowRight" | "ArrowUp" | "ArrowDown"): string | null {
  const c = l.cards.find((x) => x.id === id);
  if (!c) return null;
  if (key === "ArrowLeft") return l.wires.find((w) => w.toCard === id)?.fromCard ?? null;
  if (key === "ArrowRight") return l.wires.find((w) => w.fromCard === id)?.toCard ?? null;
  const col = l.cards.filter((x) => x.column === c.column).sort((a, b) => a.y - b.y);
  return col[col.indexOf(c) + (key === "ArrowUp" ? -1 : 1)]?.id ?? null;
}
