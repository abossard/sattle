// A projection as cards and wires: data flows from left to right, every step is a card, every read a wire. The canvas lays itself
// out, so there is nothing to arrange. A wire is made by choosing an output and then an input (click, Enter, or a drag between the
// two); only the inputs that can take it stay enabled. The selected card is edited in the panel on the right, in the same lines
// as everywhere else. Only the cards and wires in view are in the page.
import { Badge, Button, Caption1, Select, Text, makeStyles, mergeClasses, tokens } from "@fluentui/react-components";
import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactElement } from "react";
import { stepHolding } from "../editing/doc";
import { freeName, upstream } from "../editing/scope";
import type { EditorProps } from "./host";
import { CARD_W, inView, layoutOf, neighbour, type Card, type CardPort, type Rect } from "./graph-layout";
import { RowLine, useParts } from "./parts";
import { kindWord, rowText, rowsOf, type Row } from "./rows";
import { StepCard } from "./StepCard";
import { useViewSize } from "./view-size";

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateColumns: "minmax(0, 1fr) minmax(260px, 340px)", gridTemplateRows: "auto minmax(0, 1fr)", columnGap: tokens.spacingHorizontalM, height: "100%", minHeight: 0 },
  bar: { gridColumn: "1 / 3", display: "flex", alignItems: "center", columnGap: tokens.spacingHorizontalS, minHeight: "32px" },
  scroll: { overflow: "auto", minHeight: 0, position: "relative", backgroundColor: tokens.colorNeutralBackground2, borderRadius: tokens.borderRadiusMedium },
  plane: { position: "relative" },
  wires: { position: "absolute", left: 0, top: 0, pointerEvents: "none" },
  wire: { fill: "none", stroke: tokens.colorNeutralStroke1, strokeWidth: "1.5px" },
  wireOn: { stroke: tokens.colorBrandStroke1, strokeWidth: "2.5px" },
  card: { position: "absolute", boxSizing: "border-box", backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke1}`, borderRadius: tokens.borderRadiusMedium, boxShadow: tokens.shadow2 },
  on: { outline: `2px solid ${tokens.colorBrandStroke1}`, outlineOffset: "1px" },
  head: { width: "100%", justifyContent: "flex-start", columnGap: tokens.spacingHorizontalXS, minHeight: "28px", fontFamily: tokens.fontFamilyMonospace },
  kind: { color: tokens.colorBrandForeground1, fontWeight: tokens.fontWeightSemibold },
  lines: { padding: `0 ${tokens.spacingHorizontalM} 0 18px`, fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200, lineHeight: "16px", color: tokens.colorNeutralForeground2 },
  line: { overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" },
  // a port is a 24px target around a 12px mark: hollow and dashed while open, filled when it reads something, ringed when it can take the wire in hand
  port: { position: "absolute", minWidth: "24px", width: "24px", height: "24px", padding: 0 },
  mark: { boxSizing: "border-box", width: "12px", height: "12px", borderRadius: "50%", backgroundColor: tokens.colorNeutralBackground1 },
  dot: { position: "absolute", width: "8px", height: "8px", borderRadius: "50%", backgroundColor: tokens.colorNeutralStroke1 },
  open: { border: `2px dashed ${tokens.colorNeutralStrokeAccessible}` },
  full: { border: `2px solid ${tokens.colorNeutralStrokeAccessible}`, backgroundColor: tokens.colorNeutralStrokeAccessible },
  takes: { width: "18px", height: "18px", border: `4px solid ${tokens.colorBrandStroke1}` },
  side: { overflow: "auto", minHeight: 0, display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalS },
});

export interface GraphProps extends EditorProps { /** size of the view, when the caller knows it (tests, measurements) */ viewport?: { width: number; height: number } }
const STEP = 120;
/** What a card's output is called where it is read: the row set, or the input of the projection. */
const what = (c: Card): string => c.statement ?? c.name;
/** A scroll position in steps, so that a scroll changes what is shown only now and then. */
const snap = (n: number): number => Math.floor(n / STEP) * STEP;

export function GraphEditor(p: GraphProps): ReactElement {
  const s = useStyles(), parts = useParts();
  const scroller = useRef<HTMLDivElement>(null);
  const [from, setFrom] = useState<string | null>(null);
  const [at, setAt] = useState({ left: 0, top: 0 });
  const size = useViewSize(scroller, p.viewport);
  const view = useMemo((): Rect => ({ ...at, width: size.width + STEP, height: size.height + STEP }), [at, size.width, size.height]);
  const [focus, setFocus] = useState<string | null>(null);
  const rows = useMemo(() => rowsOf(p.doc, p.palette, p.cat), [p.doc, p.palette, p.cat]);
  const byId = useMemo(() => new Map(rows.map((r) => [r.id, r])), [rows]);
  const byParent = useMemo(() => { const m = new Map<string, Row[]>(); for (const r of rows) if (r.parent) m.set(r.parent, [...(m.get(r.parent) ?? []), r]); return m; }, [rows]);
  const layout = useMemo(() => layoutOf(p.doc, kindWord, (id) => (byParent.get(id) ?? []).filter((r) => r.kind !== "hole" && r.kind !== "read").map(rowText)), [p.doc, byParent]);
  const shown = useMemo(() => inView(layout, view), [layout, view]);
  const step = stepHolding(p.doc, p.selected);
  const current = step?.id ?? (p.selected && layout.cards.some((c) => c.id === p.selected) ? p.selected : null);

  const measure = () => {
    const el = scroller.current;
    if (!el) return;
    const left = snap(el.scrollLeft), top = snap(el.scrollTop);
    setAt((v) => (v.left === left && v.top === top ? v : { left, top }));
  };
  // the keyboard moved to a card: bring it into view, then put the cursor on it
  useEffect(() => {
    if (!focus) return;
    const c = layout.cards.find((x) => x.id === focus), el = scroller.current;
    if (c && el && el.clientWidth) { el.scrollLeft = Math.max(0, Math.min(el.scrollLeft, c.x - 24), c.x + c.w + 24 - el.clientWidth); el.scrollTop = Math.max(0, Math.min(el.scrollTop, c.y - 24), c.y + c.h + 24 - el.clientHeight); measure(); }
    const head = el?.querySelector<HTMLElement>(`[data-card="${CSS.escape(focus)}"]`);
    if (head) { head.focus({ preventScroll: true }); setFocus(null); }
  }, [focus, shown]); // eslint-disable-line react-hooks/exhaustive-deps

  // what is being connected: the output chosen, what it carries and which row sets must not read it (the ones it reads itself)
  const source = from ? layout.cards.find((c) => c.out?.portId === from) : undefined;
  const blocked = useMemo(() => (source?.statement ? upstream(p.doc, source.statement) : new Set<string>()), [p.doc, source]);
  const takes = (c: Card, port: CardPort): boolean => !!source && !port.inChain && port.kind === source.out!.kind && (source.statement === null || (c.statement !== source.statement && !blocked.has(c.statement ?? "")));
  const connect = (to: string) => { setFrom(null); p.onGesture({ g: "connect", to, from }); };
  // a new step that reads the chosen output: the kinds with an input for what it carries
  const quick = source ? p.palette.operators.filter((o) => o.supported && o.ports.some((x) => x.direction === "in" && x.kind === source.out!.kind)) : [];
  const addReader = (kind: string) => {
    const o = quick.find((x) => x.kind === kind);
    if (!o || !from) return;
    setFrom(null);
    p.onGesture({ g: "addStep", kind, statement: freeName(p.doc, !o.ports.some((x) => x.direction === "out")), from: { [o.ports.find((x) => x.direction === "in" && x.kind === source!.out!.kind)!.name]: from } });
  };

  const onKeyDown = (e: KeyboardEvent) => {
    const t = e.target as HTMLElement;
    if (e.key === "Escape" && from) { e.stopPropagation(); setFrom(null); return; }
    if (t.dataset.card && (e.key === "ArrowLeft" || e.key === "ArrowRight" || e.key === "ArrowUp" || e.key === "ArrowDown")) {
      e.preventDefault();
      const next = neighbour(layout, t.dataset.card, e.key);
      if (next) { p.onSelect(next); setFocus(next); }
    }
    if (t.dataset.reads && (e.key === "Delete" || e.key === "Backspace") && !p.busy) { e.preventDefault(); p.onGesture({ g: "connect", to: t.dataset.port!, from: null }); }
  };

  const first = layout.cards[0]?.id;
  const head = step && byId.get(`stmt:${p.doc.statements.find((st) => st.steps.includes(step))!.name}`);
  const more = head && byParent.get(head.id)?.find((r) => r.kind === "addStep");
  const add = rows.find((r) => r.kind === "addStatement");
  return (
    <div className={s.root} onKeyDown={onKeyDown}>
      <div className={s.bar} role="group" aria-label="Wiring">
        <Text role="status" size={200}>{source ? `Connecting from ${what(source)}: choose an input, or add a step that reads it. Escape lets go.` : `${layout.cards.length} cards, ${layout.wires.length} wires`}</Text>
        {source && <>
          <Select size="small" aria-label={`add a step that reads ${what(source)}`} value="" disabled={p.busy} onChange={(_, d) => addReader(d.value)}>
            <option value="">add a step…</option>
            {quick.map((o) => <option key={o.kind} value={o.kind}>{kindWord(o.kind)}</option>)}
          </Select>
          <Button size="small" appearance="subtle" onClick={() => setFrom(null)}>Cancel</Button>
        </>}
      </div>
      <div className={s.scroll} ref={scroller} onScroll={measure} role="group" aria-label={`Graph of ${p.doc.name}`}>
        <div className={s.plane} style={{ width: layout.width, height: layout.height }}>
          <svg className={s.wires} width={layout.width} height={layout.height} aria-hidden="true">
            {shown.wires.map((w) => <path key={w.id} d={w.d} className={mergeClasses(s.wire, (w.fromCard === current || w.toCard === current) && s.wireOn)} />)}
          </svg>
          {shown.cards.map((c) => (
            <div key={c.id} className={mergeClasses(s.card, c.id === current && s.on)} style={{ left: c.x, top: c.y, width: c.w, height: c.h }} role="group" aria-label={`${c.title} ${c.name}`}>
              <Button className={s.head} size="small" appearance="transparent" data-card={c.id} tabIndex={c.id === (current ?? first) ? 0 : -1} aria-pressed={c.id === current} onClick={() => p.onSelect(c.id)}>
                <span className={s.kind}>{c.title}</span>{" "}<span>{c.name}</span>
                {c.problems > 0 && <>{" "}<Badge size="small" appearance="outline" color="danger">! {c.problems}<span className={parts.only}> to fix</span></Badge></>}
              </Button>
              <div className={s.lines}>
                {c.lines.map((l, i) => <div key={i} className={s.line} title={l}>{l}</div>)}
                {c.more > 0 && <div className={s.line}>and {c.more} more</div>}
              </div>
              {c.ins.map((port) => (port.inChain
                ? <span key={port.portId} className={s.dot} style={{ left: -4, top: port.y - c.y - 4 }} />
                : <Button key={port.portId} className={s.port} appearance="transparent" shape="circular" size="small" icon={<span className={mergeClasses(s.mark, port.from ? s.full : s.open, takes(c, port) && s.takes)} />} style={{ left: -12, top: port.y - c.y - 12 }}
                    data-port={port.portId} data-reads={port.from ?? undefined} disabled={p.busy || (!!source && !takes(c, port))}
                    aria-label={source && takes(c, port) ? `connect ${what(source)} to ${port.name} of ${c.title} ${c.name}` : `${port.name} of ${c.title} ${c.name}: ${port.from ? `reads ${port.fromName}` : "open"}`}
                    title={port.from ? `${port.name} reads ${port.fromName}. Delete disconnects.` : `${port.name} is open`}
                    onClick={() => (source ? connect(port.portId) : p.onSelect(c.id))} onDragOver={(e) => { if (takes(c, port)) e.preventDefault(); }} onDrop={(e) => { e.preventDefault(); connect(port.portId); }} />
              ))}
              {c.out && (c.out.readable
                ? <Button className={s.port} appearance="transparent" shape="circular" size="small" icon={<span className={mergeClasses(s.mark, s.full, from === c.out.portId && s.takes)} />} style={{ left: CARD_W - 13, top: c.out.y - c.y - 12 }} disabled={p.busy} aria-pressed={from === c.out.portId} aria-label={`connect from ${what(c)}`} title={`Connect from ${what(c)}`}
                    draggable onDragStart={() => setFrom(c.out!.portId)} onDragEnd={() => setFrom(null)} onClick={() => setFrom((f) => (f === c.out!.portId ? null : c.out!.portId))} />
                : <span className={s.dot} style={{ left: CARD_W - 5, top: c.out.y - c.y - 4 }} />)}
            </div>
          ))}
        </div>
      </div>
      <div className={s.side} role="group" aria-label="Selected step">
        {step && head
          ? <>
              <RowLine row={head} busy={p.busy} onGesture={p.onGesture} />
              <StepCard self={byId.get(step.id)!} inner={byParent.get(step.id) ?? []} label={`${kindWord(step.kind)} of ${head.label ?? ""}`} selected busy={p.busy} onGesture={p.onGesture} />
              {more && <RowLine row={more} busy={p.busy} onGesture={p.onGesture} />}
            </>
          : <Caption1>Choose a card to edit it here. Arrow keys move along the wires and the columns.</Caption1>}
        {add && <RowLine row={add} busy={p.busy} onGesture={p.onGesture} />}
      </div>
    </div>
  );
}
