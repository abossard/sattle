// Node positions of a projection graph: layered left to right by data flow, plus what the author moved.
import type { DefGraph, DefNode } from "../core/def-graph";

export interface Pos { x: number; y: number }
export const NODE_W = 220;
export const rowH = (n: DefNode) => 34 + Math.max(0, Math.min(n.lines.length, 4)) * 15 + Math.max(n.inputs.length, n.outputs.length) * 0;
export const GAP_X = 90;
export const GAP_Y = 26;

/** Columns by longest path from the sources; rules and the output ontology on the right. Stable for the same graph. */
export function autoLayout(dg: DefGraph): Map<string, Pos> {
  const depth = new Map<string, number>();
  const incoming = new Map<string, string[]>();
  for (const e of dg.edges) if (e.kind === "wire") incoming.set(e.to, [...(incoming.get(e.to) ?? []), e.from]);
  const visit = (id: string, trail: Set<string>): number => {
    const known = depth.get(id);
    if (known !== undefined) return known;
    if (trail.has(id)) return 0;
    trail.add(id);
    const d = Math.max(-1, ...(incoming.get(id) ?? []).map((p) => visit(p, trail))) + 1;
    trail.delete(id);
    depth.set(id, d);
    return d;
  };
  const flow = dg.nodes.filter((n) => n.kind === "binding" || n.kind === "operator");
  for (const n of flow) visit(n.id, new Set());
  const cols = Math.max(0, ...flow.map((n) => depth.get(n.id) ?? 0)) + 1;
  const pos = new Map<string, Pos>();
  const place = (nodes: DefNode[], col: number) => { let y = 20; for (const n of nodes) { pos.set(n.id, { x: 20 + col * (NODE_W + GAP_X), y }); y += rowH(n) + GAP_Y; } };
  for (let c = 0; c < cols; c++) place(flow.filter((n) => (depth.get(n.id) ?? 0) === c), c);
  place(dg.nodes.filter((n) => n.kind === "ontology"), cols);
  place(dg.nodes.filter((n) => n.kind === "rule"), cols + 1);
  return pos;
}

/** Port anchor in graph space: inputs on the left edge, outputs on the right edge, spread over the card height. */
export function portAnchor(n: DefNode, at: Pos, port: string, side: "in" | "out"): Pos {
  const list = side === "in" ? n.inputs : n.outputs;
  const i = Math.max(0, list.indexOf(port));
  const h = rowH(n);
  return { x: at.x + (side === "in" ? 0 : NODE_W), y: at.y + (h / (list.length + 1)) * (i + 1) };
}

export function align(sel: string[], pos: Map<string, Pos>, how: "left" | "top" | "column" | "row"): Map<string, Pos> {
  const out = new Map(pos);
  const ps = sel.map((id) => pos.get(id)).filter((p): p is Pos => !!p);
  if (ps.length < 2) return out;
  const left = Math.min(...ps.map((p) => p.x)), top = Math.min(...ps.map((p) => p.y));
  let y = top;
  for (const id of [...sel].sort((a, b) => (pos.get(a)!.y - pos.get(b)!.y) || (pos.get(a)!.x - pos.get(b)!.x))) {
    const p = pos.get(id);
    if (!p) continue;
    if (how === "left") out.set(id, { x: left, y: p.y });
    else if (how === "top") out.set(id, { x: p.x, y: top });
    else if (how === "column") { out.set(id, { x: left, y }); y += 120; }
    else { out.set(id, { x: left + (y - top), y: top }); y += 260; }
  }
  return out;
}

/** Ids of the nodes whose card touches the rectangle (corners in any order): what pointer and keyboard marquee select. */
export function nodesInRect(dg: DefGraph, pos: Map<string, Pos>, a: Pos, b: Pos): string[] {
  const x0 = Math.min(a.x, b.x), x1 = Math.max(a.x, b.x), y0 = Math.min(a.y, b.y), y1 = Math.max(a.y, b.y);
  return dg.nodes.filter((n) => { const at = pos.get(n.id); return !!at && at.x < x1 && at.x + NODE_W > x0 && at.y < y1 && at.y + rowH(n) > y0; }).map((n) => n.id);
}
