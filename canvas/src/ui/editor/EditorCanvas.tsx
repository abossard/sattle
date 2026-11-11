// The node canvas of a projection: cards with ports, wires, drag to move, drag from an output to an input to wire (with live type checking),
// marquee selection and a full keyboard model. SVG, because projections are tens of nodes and cards carry text and ports.
import { makeStyles, tokens } from "@fluentui/react-components";
import { useMemo, useRef, useState } from "react";
import { DIRECTION, type DirectionClass } from "../../core/catalog";
import type { DefGraph, DefNode } from "../../core/projection/defgraph";
import { NODE_W, nodesInRect, portAnchor, rowH, type Pos } from "../../editor/layout";

const useStyles = makeStyles({
  wrap: { overflow: "auto", minHeight: 0, height: "100%", backgroundColor: tokens.colorNeutralBackground3, "&:focus-visible": { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: "-2px" } },
  svg: { display: "block", touchAction: "none", userSelect: "none" },
  card: { fill: tokens.colorNeutralBackground1, stroke: tokens.colorNeutralStroke1, strokeWidth: 1.5 },
  cardSel: { stroke: tokens.colorBrandStroke1, strokeWidth: 3 },
  cardErr: { stroke: tokens.colorPaletteRedBorder2, strokeDasharray: "6 3" },
  title: { fill: tokens.colorNeutralForeground1, fontSize: "13px", fontWeight: 600, fontFamily: tokens.fontFamilyBase },
  line: { fill: tokens.colorNeutralForeground2, fontSize: "11px", fontFamily: tokens.fontFamilyMonospace },
  wire: { fill: "none", stroke: tokens.colorNeutralForeground2, strokeWidth: 1.6 },
  wireSel: { stroke: tokens.colorBrandForeground1, strokeWidth: 3 },
  bind: { fill: "none", stroke: tokens.colorNeutralForeground3, strokeWidth: 1.2, strokeDasharray: "2 4" },
  port: { fill: tokens.colorNeutralBackground1, stroke: tokens.colorNeutralForeground1, strokeWidth: 1.5, cursor: "crosshair" },
  portOk: { fill: tokens.colorPaletteGreenBackground3, stroke: tokens.colorPaletteGreenForeground1, strokeWidth: 2.5 },
  portNo: { opacity: 0.25 },
  marquee: { fill: tokens.colorBrandBackground2, fillOpacity: 0.3, stroke: tokens.colorBrandStroke1, strokeDasharray: "4 3" },
  note: { fill: tokens.colorPaletteYellowBackground2, stroke: tokens.colorPaletteYellowBorder2 },
  loop: { fill: tokens.colorNeutralBackground2, fillOpacity: 0.5, stroke: tokens.colorBrandStroke1, strokeWidth: 1.5, strokeDasharray: "2 5" },
  overEdge: { fill: "none", stroke: tokens.colorBrandForeground1, strokeWidth: 2, strokeDasharray: "7 4" },
  group: { fill: "none", stroke: tokens.colorNeutralStroke2, strokeDasharray: "8 4", strokeWidth: 1.5 },
});

export interface Note { id: string; x: number; y: number; text: string }
export interface Group { id: string; label: string; members: string[] }
export const portIdOf = (n: DefNode, name: string) => (n.kind === "binding" ? n.id : `${n.id}/port:${name}`);
const glyph = (n: DefNode) => (n.kind === "binding" ? "⬓" : n.kind === "ontology" ? "◇" : n.kind === "rule" ? "" : "▭");
const clip = (t: string, n = 32) => (t.length > n ? `${t.slice(0, n - 1)}…` : t);

interface Props {
  dg: DefGraph; positions: Map<string, Pos>; selected: string[]; flagged: Map<string, "error" | "warning">; notes: Note[]; groups: Group[];
  /** keyboard marquee in progress (corners in graph space) */
  keyMarquee: { a: Pos; b: Pos } | null;
  wiring: string | null; wireTarget: string | null; canWire: (from: string, to: string) => boolean;
  onSelect: (ids: string[]) => void; onMove: (ids: string[], dx: number, dy: number, final: boolean) => void; onWire: (from: string, to: string) => void;
  onNoteMove: (id: string, x: number, y: number) => void; onNoteEdit: (id: string, text: string) => void; zoom: number;
}

export function EditorCanvas(p: Props) {
  const s = useStyles();
  const [drag, setDrag] = useState<{ kind: "note"; id: string; at: Pos; dx: number; dy: number } | { kind: "move"; ids: string[]; at: Pos; dx: number; dy: number } | { kind: "wire"; from: string; at: Pos; to: Pos } | { kind: "marquee"; a: Pos; b: Pos } | null>(null);
  const svg = useRef<SVGSVGElement>(null);
  const nodes = useMemo(() => new Map(p.dg.nodes.map((n) => [n.id, n])), [p.dg]);
  const pos = (id: string): Pos => {
    const base = p.positions.get(id) ?? { x: 0, y: 0 };
    return drag?.kind === "move" && drag.ids.includes(id) ? { x: base.x + drag.dx, y: base.y + drag.dy } : base;
  };
  const point = (e: React.PointerEvent): Pos => { const r = svg.current!.getBoundingClientRect(); return { x: (e.clientX - r.left) / p.zoom, y: (e.clientY - r.top) / p.zoom }; };
  const size = useMemo(() => {
    let w = 400, h = 300;
    for (const [id, at] of p.positions) { const n = nodes.get(id); if (n) { w = Math.max(w, at.x + NODE_W + 80); h = Math.max(h, at.y + rowH(n) + 80); } }
    for (const n of p.notes) { w = Math.max(w, n.x + 200); h = Math.max(h, n.y + 80); }
    return { w, h };
  }, [p.positions, p.notes, nodes]);

  const portPos = (n: DefNode, name: string, side: "in" | "out"): Pos => portAnchor(n, pos(n.id), name, side);
  const wirePath = (a: Pos, b: Pos) => { const d = Math.max(40, Math.abs(b.x - a.x) / 2); return `M${a.x},${a.y} C${a.x + d},${a.y} ${b.x - d},${b.y} ${b.x},${b.y}`; };

  const onNodeDown = (e: React.PointerEvent, n: DefNode) => {
    if ((e.target as Element).getAttribute("data-port")) return;
    e.stopPropagation();
    const sel = e.shiftKey ? (p.selected.includes(n.id) ? p.selected.filter((x) => x !== n.id) : [...p.selected, n.id]) : p.selected.includes(n.id) ? p.selected : [n.id];
    p.onSelect(sel);
    (e.currentTarget as Element).setPointerCapture(e.pointerId);
    setDrag({ kind: "move", ids: sel, at: point(e), dx: 0, dy: 0 });
  };
  const onMoveEv = (e: React.PointerEvent) => {
    if (!drag) return;
    const q = point(e);
    if (drag.kind === "move" || drag.kind === "note") setDrag({ ...drag, dx: q.x - drag.at.x, dy: q.y - drag.at.y });
    else if (drag.kind === "wire") setDrag({ ...drag, to: q });
    else setDrag({ ...drag, b: q });
  };
  const onUp = (e: React.PointerEvent) => {
    if (!drag) return;
    if (drag.kind === "note") { const n = p.notes.find((x) => x.id === drag.id); if (n) p.onNoteMove(n.id, Math.max(0, n.x + drag.dx), Math.max(0, n.y + drag.dy)); }
    else if (drag.kind === "move") { if (Math.abs(drag.dx) + Math.abs(drag.dy) > 2) p.onMove(drag.ids, drag.dx, drag.dy, true); }
    else if (drag.kind === "wire") {
      const target = (document.elementFromPoint(e.clientX, e.clientY) as Element | null)?.closest("[data-port]")?.getAttribute("data-port");
      if (target && p.canWire(drag.from, target)) p.onWire(drag.from, target);
    } else {
      p.onSelect(nodesInRect(p.dg, p.positions, drag.a, drag.b));
    }
    setDrag(null);
  };
  const lines: { id: string; d: string; kind: "wire" | "bind" | "over"; sel: boolean }[] = [];
  for (const e of p.dg.edges) {
    const a = nodes.get(e.from), b = nodes.get(e.to);
    if (!a || !b) continue;
    if (e.kind === "over") {
      // the step result goes back to the fixpoint: a dashed arc under the loop
      const from = portPos(a, e.fromPort, "out"), to = portPos(b, "out", "out");
      const low = Math.max(from.y, to.y) + 70;
      lines.push({ id: e.id, d: `M${from.x},${from.y} C${from.x + 60},${low} ${to.x + 60},${low} ${to.x},${to.y}`, kind: "over", sel: false });
      continue;
    }
    const from = e.kind === "wire" ? portPos(a, e.fromPort, "out") : { x: pos(a.id).x + NODE_W / 2, y: pos(a.id).y + rowH(a) };
    const to = e.kind === "wire" ? portPos(b, e.toPort, "in") : { x: pos(b.id).x + NODE_W / 2, y: pos(b.id).y };
    lines.push({ id: e.id, d: e.kind === "wire" ? wirePath(from, to) : `M${from.x},${from.y} L${to.x},${to.y}`, kind: e.kind === "wire" ? "wire" : "bind", sel: e.kind === "wire" && (p.selected.includes(a.id) || p.selected.includes(b.id)) });
  }
  return (
    <div className={s.wrap} tabIndex={0} role="group" aria-label="Projection graph. Arrow keys select the next operator, Shift with arrows moves it, Enter edits its properties, W connects its output, A adds an operator, Delete removes it."
      >
      <svg ref={svg} className={s.svg} width={size.w * p.zoom} height={size.h * p.zoom} viewBox={`0 0 ${size.w} ${size.h}`} onPointerMove={onMoveEv} onPointerUp={onUp}
        onPointerDown={(e) => { if (e.target === e.currentTarget) { const q = point(e); p.onSelect([]); setDrag({ kind: "marquee", a: q, b: q }); (e.currentTarget as Element).setPointerCapture(e.pointerId); } }}>
        {p.groups.map((g) => {
          const ats = g.members.map((m) => ({ at: pos(m), n: nodes.get(m) })).filter((x) => x.n);
          if (!ats.length) return null;
          const x0 = Math.min(...ats.map((a) => a.at.x)) - 14, y0 = Math.min(...ats.map((a) => a.at.y)) - 26, x1 = Math.max(...ats.map((a) => a.at.x + NODE_W)) + 14, y1 = Math.max(...ats.map((a) => a.at.y + rowH(a.n!))) + 14;
          return <g key={g.id}><rect className={s.group} x={x0} y={y0} width={x1 - x0} height={y1 - y0} rx={8} /><text className={s.line} x={x0 + 8} y={y0 + 16}>Group: {g.label}</text></g>;
        })}
        {(p.dg.loops ?? []).map((lp) => {
          const ms = [lp.fixpoint, ...lp.members].map((m) => ({ at: pos(m), n: nodes.get(m) })).filter((x) => x.n);
          if (!ms.length) return null;
          const x0 = Math.max(2, Math.min(...ms.map((a) => a.at.x)) - 16), y0 = Math.max(2, Math.min(...ms.map((a) => a.at.y)) - 34), x1 = Math.max(...ms.map((a) => a.at.x + NODE_W)) + 16, y1 = Math.max(...ms.map((a) => a.at.y + rowH(a.n!))) + 86;
          const fp = nodes.get(lp.fixpoint)!;
          return <g key={lp.fixpoint} role="group" aria-label={`Loop of ${fp.title}: ${lp.members.length} step operators repeat until nothing new is derived`}><rect className={s.loop} x={x0} y={y0} width={x1 - x0} height={y1 - y0} rx={12} /><text className={s.line} x={x0 + 10} y={y0 + 14}>{`↻ loop of ${fp.title}${fp.props.maxIterations ? `, at most ${fp.props.maxIterations} rounds` : ""} `}</text></g>;
        })}
        {lines.map((l) => <path key={l.id} d={l.d} className={`${l.kind === "wire" ? s.wire : l.kind === "over" ? s.overEdge : s.bind} ${l.sel ? s.wireSel : ""}`} />)}
        {p.dg.nodes.map((n, idx) => {
          const at = pos(n.id), h = rowH(n), sel = p.selected.includes(n.id), flag = p.flagged.get(n.id);
          // input ports with names (a join's left and right) are labelled inside the card, so the text starts to the right of the labels
          const gutter = n.inputs.length > 1 ? 40 : 0;
          const dir = n.kind === "rule" && n.direction ? DIRECTION[n.direction as DirectionClass] : null;
          const tab = p.selected.length ? p.selected[0] === n.id : idx === 0;
          const titleText = `${flag ? (flag === "error" ? "✖ " : "⚠ ") : ""}${glyph(n)} ${dir ? `${dir.glyph} ` : ""}${clip(`${n.opKind ? `${n.opKind} ` : ""}${n.title}`, 26 - Math.round(gutter / 7))}`;
          const shown = n.lines.length > 4 ? [...n.lines.slice(0, 3), `… ${n.lines.length - 3} more lines`] : n.lines;
          const bodyLines = shown.map((l) => clip(l, 32 - Math.round(gutter / 6.5)));
          const portNames = [...(n.inputs.length > 1 ? n.inputs : []), ...(n.outputs.length > 1 ? n.outputs : [])];
          return (
            <g key={n.id} transform={`translate(${at.x},${at.y})`} role="button" aria-pressed={sel} tabIndex={tab ? 0 : -1} data-node={n.id}
              aria-label={`${titleText} ${bodyLines.join(" ")} ${portNames.join(" ")}${flag ? `, ${flag === "error" ? "has a problem" : "has a warning"}` : ""}`.replace(/\s+/g, " ").trim()}
              onPointerDown={(e) => onNodeDown(e, n)}>
              <rect className={`${s.card} ${sel ? s.cardSel : ""} ${flag === "error" ? s.cardErr : ""}`} width={NODE_W} height={h} rx={6} />
              <text className={s.title} x={10 + gutter} y={20}>{`${titleText} `}</text>
              {bodyLines.map((l, i) => <text key={i} className={s.line} x={10 + gutter} y={38 + i * 15}>{`${l} `}</text>)}
              {n.inputs.map((name) => {
                const pid = portIdOf(n, name), a = portAnchor(n, { x: 0, y: 0 }, name, "in");
                const hit = drag?.kind === "wire" ? (p.canWire(drag.from, pid) ? s.portOk : s.portNo) : p.wireTarget === pid ? s.portOk : "";
                return <g key={`i${name}`}><circle className={`${s.port} ${hit}`} cx={a.x} cy={a.y} r={6} data-port={pid}></circle>{n.inputs.length > 1 && <text className={s.line} x={a.x + 9} y={a.y + 4} fontSize={9}>{`${name} `}</text>}</g>;
              })}
              {n.outputs.map((name) => {
                const pid = portIdOf(n, name), a = portAnchor(n, { x: 0, y: 0 }, name, "out");
                return <g key={`o${name}`}><circle className={`${s.port} ${p.wiring === pid ? s.portOk : ""}`} cx={a.x} cy={a.y} r={6} data-port={pid}
                  onPointerDown={(e) => { e.stopPropagation(); (svg.current as Element).setPointerCapture(e.pointerId); setDrag({ kind: "wire", from: pid, at: point(e), to: point(e) }); }}></circle>
                  {n.outputs.length > 1 && <text className={s.line} x={a.x - 9} y={a.y + 4} fontSize={9} textAnchor="end">{`${name} `}</text>}</g>;
              })}
            </g>
          );
        })}
        {drag?.kind === "wire" && (() => { const n = [...nodes.values()].find((x) => x.outputs.some((o) => portIdOf(x, o) === drag.from)); const a = n ? portAnchor(n, pos(n.id), n.outputs.find((o) => portIdOf(n, o) === drag.from)!, "out") : drag.at; return <path d={wirePath(a, drag.to)} className={s.wireSel} fill="none" />; })()}
        {p.keyMarquee && <rect className={s.marquee} x={Math.min(p.keyMarquee.a.x, p.keyMarquee.b.x)} y={Math.min(p.keyMarquee.a.y, p.keyMarquee.b.y)} width={Math.abs(p.keyMarquee.a.x - p.keyMarquee.b.x)} height={Math.abs(p.keyMarquee.a.y - p.keyMarquee.b.y)} />}
        {drag?.kind === "marquee" && <rect className={s.marquee} x={Math.min(drag.a.x, drag.b.x)} y={Math.min(drag.a.y, drag.b.y)} width={Math.abs(drag.a.x - drag.b.x)} height={Math.abs(drag.a.y - drag.b.y)} />}
        {p.notes.map((n) => (
          <g key={n.id} transform={`translate(${n.x + (drag?.kind === "note" && drag.id === n.id ? drag.dx : 0)},${n.y + (drag?.kind === "note" && drag.id === n.id ? drag.dy : 0)})`} role="note" aria-label={`Comment: ${n.text}`}>
            <rect className={s.note} width={190} height={56} rx={4} onPointerDown={(e) => { e.stopPropagation(); (svg.current as Element).setPointerCapture(e.pointerId); setDrag({ kind: "note", id: n.id, at: point(e), dx: 0, dy: 0 }); }} style={{ cursor: "move" }} />
            <foreignObject y={8} width={190} height={48}><textarea aria-label="Comment text" defaultValue={n.text} onBlur={(e) => p.onNoteEdit(n.id, e.target.value)} style={{ width: "100%", height: "100%", boxSizing: "border-box", background: "transparent", border: "none", resize: "none", font: "inherit", color: "inherit" }} /></foreignObject>
          </g>
        ))}
      </svg>
    </div>
  );
}
