// The node canvas of a projection: cards with ports, wires, drag to move, drag from an output to an input to wire (with live type checking),
// marquee selection and a full keyboard model. SVG, because projections are tens of nodes and cards carry text and ports.
import { makeStyles, tokens } from "@fluentui/react-components";
import { useMemo, useRef, useState } from "react";
import { DIRECTION, type DirectionClass } from "../../core/catalog";
import type { DefGraph, DefNode } from "../../core/def-graph";
import { NODE_W, ZONE, nodesInRect, partNumbers, portAnchor, rowH, type Pos } from "../../editor/layout";

const useBlock = makeStyles({
  block: { position: "absolute", right: "24px", bottom: "24px", width: "min(520px, calc(100% - 48px))", backgroundColor: tokens.colorNeutralBackground1, border: `1.5px solid ${tokens.colorNeutralForeground1}`, fontSize: tokens.fontSizeBase200, "@media (max-width: 600px)": { display: "none" } },
  table: { width: "100%", borderCollapse: "collapse", "& th, & td": { border: `0.75px solid ${tokens.colorNeutralForeground1}`, padding: `1px ${tokens.spacingHorizontalS}`, textAlign: "start", verticalAlign: "top" } },
  cap: { display: "block", fontSize: "9px", lineHeight: "11px", letterSpacing: "0.06em", textTransform: "uppercase", color: tokens.colorNeutralForeground3, fontWeight: tokens.fontWeightRegular },
  val: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase100, color: tokens.colorNeutralForeground1, overflowWrap: "anywhere" },
  step: { width: "3.5em", fontFamily: tokens.fontFamilyMonospace },
  where: { width: "9em" },
});

/** The title block of the sheet (three cells a row), pinned to the lower right corner of the view: what this drawing is, and the latest changes of the draft. */
export function TitleBlock({ rows, changes, total, base }: { rows: [string, string][]; changes: { label: string; parts: string }[]; total: number; base: string }) {
  const s = useBlock();
  const pairs = Array.from({ length: Math.ceil(rows.length / 3) }, (_, i) => rows.slice(i * 3, i * 3 + 3));
  return (
    <section className={s.block} aria-label="Title block">
      <table className={s.table} aria-label="Latest changes of the draft">
        <thead><tr><th className={s.step} scope="col"><span className={s.cap}>Step</span></th><th className={s.where} scope="col"><span className={s.cap}>Part · zone</span></th><th scope="col"><span className={s.cap}>Change in this draft, newest first</span></th></tr></thead>
        <tbody>{changes.length
          ? changes.map((c, i) => <tr key={i}><td className={s.step}>{total - i}</td><td className={`${s.val} ${s.where}`}>{c.parts}</td><td className={s.val}>{c.label}</td></tr>)
          : <tr><td className={s.step}>0</td><td className={`${s.val} ${s.where}`}>–</td><td className={s.val}>no changes since base {base}</td></tr>}</tbody>
      </table>
      <table className={s.table}><tbody>{pairs.map((pr) => <tr key={pr[0][0]}>{pr.map(([k, v]) => <td key={k}><span className={s.cap}>{k}</span><span className={s.val}>{v}</span></td>)}</tr>)}</tbody></table>
    </section>
  );
}

const useStyles = makeStyles({
  wrap: { overflow: "auto", minHeight: 0, height: "100%", padding: tokens.spacingHorizontalM, boxSizing: "border-box", backgroundColor: tokens.colorNeutralBackground3, "&:focus-visible": { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: "-2px" } },
  svg: { display: "block", touchAction: "none", userSelect: "none", backgroundColor: tokens.colorNeutralBackground1 },
  frame: { fill: "none", stroke: tokens.colorNeutralForeground1, strokeWidth: 1.5 },
  inner: { fill: "none", stroke: tokens.colorNeutralForeground1, strokeWidth: 1 },
  tick: { stroke: tokens.colorNeutralForeground1, strokeWidth: 1 },
  zone: { fill: tokens.colorNeutralForeground3, fontSize: "11px", fontFamily: tokens.fontFamilyMonospace, textAnchor: "middle" },
  ref: { fill: tokens.colorNeutralBackground1, stroke: tokens.colorNeutralForeground1, strokeWidth: 1 },
  refSel: { stroke: tokens.colorBrandStroke1, strokeWidth: 2 },
  refText: { fill: tokens.colorNeutralForeground1, fontSize: "10px", fontFamily: tokens.fontFamilyMonospace, textAnchor: "middle" },
  card: { fill: tokens.colorNeutralBackground1, stroke: tokens.colorNeutralForeground1, strokeWidth: 1.5 },
  cardSel: { stroke: tokens.colorBrandStroke1, strokeWidth: 2.5 },
  cardErr: { stroke: tokens.colorPaletteRedBorder2, strokeDasharray: "6 3" },
  title: { fill: tokens.colorNeutralForeground1, fontSize: "13px", fontWeight: 600, fontFamily: tokens.fontFamilyBase },
  line: { fill: tokens.colorNeutralForeground2, fontSize: "11px", fontFamily: tokens.fontFamilyMonospace },
  wire: { fill: "none", stroke: tokens.colorNeutralForeground1, strokeWidth: 1 },
  wireSel: { stroke: tokens.colorBrandForeground1, strokeWidth: 2 },
  bind: { fill: "none", stroke: tokens.colorNeutralForeground3, strokeWidth: 1.2, strokeDasharray: "2 4" },
  port: { fill: tokens.colorNeutralBackground1, stroke: tokens.colorNeutralForeground1, strokeWidth: 1.25, cursor: "crosshair" },
  portOk: { fill: tokens.colorBrandBackground2, stroke: tokens.colorBrandStroke1, strokeWidth: 2.5 },
  portNo: { opacity: 0.25 },
  marquee: { fill: tokens.colorBrandBackground2, fillOpacity: 0.3, stroke: tokens.colorBrandStroke1, strokeDasharray: "4 3" },
  note: { fill: tokens.colorNeutralBackground1, stroke: tokens.colorNeutralForeground2, strokeWidth: 1, strokeDasharray: "3 2" },
  group: { fill: "none", stroke: tokens.colorNeutralForeground3, strokeDasharray: "8 3 2 3", strokeWidth: 1 },
});

export interface Note { id: string; x: number; y: number; text: string }
export interface Group { id: string; label: string; members: string[] }
export const portIdOf = (n: DefNode, name: string) => (n.kind === "binding" ? n.id : `${n.id}/port:${name}`);
const clip = (t: string, n = 32) => (t.length > n ? `${t.slice(0, n - 1)}…` : t);

interface Props {
  dg: DefGraph; positions: Map<string, Pos>; selected: string[]; flagged: Map<string, "error" | "warning">; notes: Note[]; groups: Group[];
  /** keyboard marquee in progress (corners in graph space) */
  keyMarquee: { a: Pos; b: Pos } | null;
  wiring: string | null; wireTarget: string | null; canWire: (from: string, to: string) => boolean;
  onSelect: (ids: string[]) => void; onMove: (ids: string[], dx: number, dy: number, final: boolean) => void; onWire: (from: string, to: string) => void;
  onNoteMove: (id: string, x: number, y: number) => void; onNoteEdit: (id: string, text: string) => void; zoom: number;
}

/** Margin of the sheet that carries the zone numbers. */
const M = 28;

export function EditorCanvas(p: Props) {
  const s = useStyles();
  const [drag, setDrag] = useState<{ kind: "note"; id: string; at: Pos; dx: number; dy: number } | { kind: "move"; ids: string[]; at: Pos; dx: number; dy: number } | { kind: "wire"; from: string; at: Pos; to: Pos } | { kind: "marquee"; a: Pos; b: Pos } | null>(null);
  const svg = useRef<SVGSVGElement>(null);
  const nodes = useMemo(() => new Map(p.dg.nodes.map((n) => [n.id, n])), [p.dg]);
  const pos = (id: string): Pos => {
    const base = p.positions.get(id) ?? { x: 0, y: 0 };
    return drag?.kind === "move" && drag.ids.includes(id) ? { x: base.x + drag.dx, y: base.y + drag.dy } : base;
  };
  const point = (e: React.PointerEvent): Pos => { const r = svg.current!.getBoundingClientRect(); return { x: (e.clientX - r.left) / p.zoom - M, y: (e.clientY - r.top) / p.zoom - M }; };
  const size = useMemo(() => {
    let w = 400, h = 300;
    for (const [id, at] of p.positions) { const n = nodes.get(id); if (n) { w = Math.max(w, at.x + NODE_W + 80); h = Math.max(h, at.y + rowH(n) + 80); } }
    for (const n of p.notes) { w = Math.max(w, n.x + 200); h = Math.max(h, n.y + 80); }
    return { w, h };
  }, [p.positions, p.notes, nodes]);
  // room under the parts for the title block, so the last row scrolls clear of it
  const sheet = { w: size.w, h: size.h + 170 };
  const cols = Math.ceil(sheet.w / ZONE.w), rows = Math.ceil(sheet.h / ZONE.h);
  const W = sheet.w + 2 * M, H = sheet.h + 2 * M;
  const ref = useMemo(() => partNumbers(p.dg, p.positions), [p.dg, p.positions]);

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
  const lines: { id: string; d: string; kind: "wire" | "bind"; sel: boolean }[] = [];
  for (const e of p.dg.edges) {
    const a = nodes.get(e.from), b = nodes.get(e.to);
    if (!a || !b) continue;
    const from = e.kind === "wire" ? portPos(a, e.fromPort, "out") : { x: pos(a.id).x + NODE_W / 2, y: pos(a.id).y + rowH(a) };
    const to = e.kind === "wire" ? portPos(b, e.toPort, "in") : { x: pos(b.id).x + NODE_W / 2, y: pos(b.id).y };
    lines.push({ id: e.id, d: e.kind === "wire" ? wirePath(from, to) : `M${from.x},${from.y} L${to.x},${to.y}`, kind: e.kind === "wire" ? "wire" : "bind", sel: e.kind === "wire" && (p.selected.includes(a.id) || p.selected.includes(b.id)) });
  }
  return (
    <div className={s.wrap} tabIndex={0} role="group" aria-label="Projection graph. Arrow keys select the next operator, Shift with arrows moves it, Enter edits its properties, W connects its output, A adds an operator, Delete removes it."
      >
      <svg ref={svg} className={s.svg} width={W * p.zoom} height={H * p.zoom} viewBox={`0 0 ${W} ${H}`} onPointerMove={onMoveEv} onPointerUp={onUp}
        onPointerDown={(e) => { if (e.target === e.currentTarget || (e.target as Element).hasAttribute("data-sheet")) { const q = point(e); p.onSelect([]); setDrag({ kind: "marquee", a: q, b: q }); (svg.current as Element).setPointerCapture(e.pointerId); } }}>
        <g aria-hidden="true">
          <rect className={s.frame} x={4} y={4} width={W - 8} height={H - 8} />
          <rect className={s.inner} x={M} y={M} width={sheet.w} height={sheet.h} data-sheet="" fill="transparent" />
          {Array.from({ length: cols }, (_, i) => { const x0 = M + i * ZONE.w, x1 = M + Math.min(sheet.w, (i + 1) * ZONE.w); return <g key={`c${i}`}>{i > 0 && <><line className={s.tick} x1={x0} y1={4} x2={x0} y2={M} /><line className={s.tick} x1={x0} y1={H - M} x2={x0} y2={H - 4} /></>}<text className={s.zone} x={(x0 + x1) / 2} y={M - 9}>{i + 1}</text><text className={s.zone} x={(x0 + x1) / 2} y={H - 11}>{i + 1}</text></g>; })}
          {Array.from({ length: rows }, (_, i) => { const y0 = M + i * ZONE.h, y1 = M + Math.min(sheet.h, (i + 1) * ZONE.h), L = String.fromCharCode(65 + Math.min(25, i)); return <g key={`r${i}`}>{i > 0 && <><line className={s.tick} x1={4} y1={y0} x2={M} y2={y0} /><line className={s.tick} x1={W - M} y1={y0} x2={W - 4} y2={y0} /></>}<text className={s.zone} x={M / 2 + 2} y={(y0 + y1) / 2 + 4}>{L}</text><text className={s.zone} x={W - M / 2 - 2} y={(y0 + y1) / 2 + 4}>{L}</text></g>; })}
        </g>
        <g transform={`translate(${M},${M})`}>
        {p.groups.map((g) => {
          const ats = g.members.map((m) => ({ at: pos(m), n: nodes.get(m) })).filter((x) => x.n);
          if (!ats.length) return null;
          const x0 = Math.min(...ats.map((a) => a.at.x)) - 14, y0 = Math.min(...ats.map((a) => a.at.y)) - 26, x1 = Math.max(...ats.map((a) => a.at.x + NODE_W)) + 14, y1 = Math.max(...ats.map((a) => a.at.y + rowH(a.n!))) + 14;
          return <g key={g.id}><rect className={s.group} x={x0} y={y0} width={x1 - x0} height={y1 - y0} /><text className={s.line} x={x0 + 8} y={y0 + 16}>Group: {g.label}</text></g>;
        })}
        {lines.map((l) => <path key={l.id} d={l.d} className={`${l.kind === "wire" ? s.wire : s.bind} ${l.sel ? s.wireSel : ""}`} />)}
        {p.dg.nodes.map((n, idx) => {
          const at = pos(n.id), h = rowH(n), sel = p.selected.includes(n.id), flag = p.flagged.get(n.id);
          // input ports with names (a join's left and right) are labelled inside the card, so the text starts to the right of the labels
          const gutter = n.inputs.length > 1 ? 40 : 0;
          const dir = n.kind === "rule" && n.direction ? DIRECTION[n.direction as DirectionClass] : null;
          const tab = p.selected.length ? p.selected[0] === n.id : idx === 0;
          const titleText = `${flag ? (flag === "error" ? "✖ " : "⚠ ") : ""}${dir ? `${dir.glyph} ` : ""}${clip(`${n.opKind ? `${n.opKind} ` : ""}${n.title}`, 26 - Math.round(gutter / 7))}`;
          const shown = n.lines.length > 4 ? [...n.lines.slice(0, 3), `… ${n.lines.length - 3} more lines`] : n.lines;
          const bodyLines = shown.map((l) => clip(l, 32 - Math.round(gutter / 6.5)));
          const portNames = [...(n.inputs.length > 1 ? n.inputs : []), ...(n.outputs.length > 1 ? n.outputs : [])];
          return (
            <g key={n.id} transform={`translate(${at.x},${at.y})`} role="button" aria-pressed={sel} tabIndex={tab ? 0 : -1} data-node={n.id}
              aria-label={`${titleText} ${bodyLines.join(" ")} ${portNames.join(" ")}${flag ? `, ${flag === "error" ? "has a problem" : "has a warning"}` : ""}`.replace(/\s+/g, " ").trim()}
              onPointerDown={(e) => onNodeDown(e, n)}>
              <rect className={`${s.card} ${sel ? s.cardSel : ""} ${flag === "error" ? s.cardErr : ""}`} width={NODE_W} height={h} />
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
        <g aria-hidden="true" pointerEvents="none">{p.dg.nodes.map((n) => { const at = pos(n.id), sel = p.selected.includes(n.id); return <g key={n.id} transform={`translate(${at.x + NODE_W},${at.y})`}><circle className={`${s.ref} ${sel ? s.refSel : ""}`} r={10} /><text className={s.refText} y={3.5}>{ref.get(n.id)}</text></g>; })}</g>
        {drag?.kind === "wire" && (() => { const n = [...nodes.values()].find((x) => x.outputs.some((o) => portIdOf(x, o) === drag.from)); const a = n ? portAnchor(n, pos(n.id), n.outputs.find((o) => portIdOf(n, o) === drag.from)!, "out") : drag.at; return <path d={wirePath(a, drag.to)} className={s.wireSel} fill="none" />; })()}
        {p.keyMarquee && <rect className={s.marquee} x={Math.min(p.keyMarquee.a.x, p.keyMarquee.b.x)} y={Math.min(p.keyMarquee.a.y, p.keyMarquee.b.y)} width={Math.abs(p.keyMarquee.a.x - p.keyMarquee.b.x)} height={Math.abs(p.keyMarquee.a.y - p.keyMarquee.b.y)} />}
        {drag?.kind === "marquee" && <rect className={s.marquee} x={Math.min(drag.a.x, drag.b.x)} y={Math.min(drag.a.y, drag.b.y)} width={Math.abs(drag.a.x - drag.b.x)} height={Math.abs(drag.a.y - drag.b.y)} />}
        {p.notes.map((n) => (
          <g key={n.id} transform={`translate(${n.x + (drag?.kind === "note" && drag.id === n.id ? drag.dx : 0)},${n.y + (drag?.kind === "note" && drag.id === n.id ? drag.dy : 0)})`} role="note" aria-label={`Comment: ${n.text}`}>
            <rect className={s.note} width={190} height={56} onPointerDown={(e) => { e.stopPropagation(); (svg.current as Element).setPointerCapture(e.pointerId); setDrag({ kind: "note", id: n.id, at: point(e), dx: 0, dy: 0 }); }} style={{ cursor: "move" }} />
            <foreignObject y={8} width={190} height={48}><textarea aria-label="Comment text" defaultValue={n.text} onBlur={(e) => p.onNoteEdit(n.id, e.target.value)} style={{ width: "100%", height: "100%", boxSizing: "border-box", background: "transparent", border: "none", resize: "none", font: "inherit", color: "inherit" }} /></foreignObject>
          </g>
        ))}
        </g>
      </svg>
    </div>
  );
}
