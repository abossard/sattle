// The GKQL program drawn as a sheet: verbs as square cards, wires between them. A card selects its verb; dragging a card moves it (layout
// only); a card's handle starts a wire, by drag onto the card it feeds or by Enter and then Enter on that card. A pure view of the editor's
// model: it sends events and keeps only the pointer of a gesture in progress.
import { Badge, Text, makeStyles, mergeClasses, tokens } from "@fluentui/react-components";
import { useId, useMemo, useRef, useState, type PointerEvent } from "react";
import { layoutGkql, verbFocus, verbStyle, VERB_HEIGHT, VERB_WIDTH, wireFocus } from "../../editor/gkql";
import type { GkqlEvent, GkqlModel } from "../../editor/gkql-editing";

const useStyles = makeStyles({
  stage: { position: "relative", overflow: "auto", minHeight: 0, backgroundColor: tokens.colorNeutralBackground2, backgroundImage: `radial-gradient(${tokens.colorNeutralStroke2} 1px, transparent 1px)`, backgroundSize: "16px 16px", "&:focus-visible": { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: "-2px" } },
  contents: { position: "relative" },
  empty: { margin: "16px", display: "block" },
  svg: { position: "absolute", inset: 0, pointerEvents: "none", overflow: "visible" },
  wire: { fill: "none", stroke: tokens.colorNeutralForeground3, strokeWidth: 1 },
  wireSelected: { stroke: tokens.colorBrandStroke1, strokeWidth: 2 },
  wireError: { stroke: tokens.colorPaletteRedBorder2, strokeDasharray: "4 3" },
  hit: { fill: "none", stroke: "transparent", strokeWidth: 10, pointerEvents: "stroke", cursor: "pointer" },
  ghost: { fill: "none", stroke: tokens.colorBrandStroke1, strokeWidth: 2, strokeDasharray: "4 3" },
  cards: { listStyle: "none", margin: 0, padding: 0 },
  card: {
    position: "absolute", height: `${VERB_HEIGHT}px`, boxSizing: "border-box", textAlign: "start", display: "flex", flexDirection: "column",
    justifyContent: "center", gap: "2px", padding: "0 20px 0 8px", border: `1.5px solid ${tokens.colorNeutralForeground1}`, borderRadius: 0,
    backgroundColor: tokens.colorNeutralBackground1, color: tokens.colorNeutralForeground1, cursor: "pointer", touchAction: "none", font: "inherit",
    "&:focus-visible": { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: "2px" },
  },
  input: { border: `1.5px dashed ${tokens.colorNeutralForeground1}` },
  selected: { backgroundColor: tokens.colorBrandBackground2, border: `2px solid ${tokens.colorBrandStroke1}` },
  error: { border: `2px dashed ${tokens.colorPaletteRedBorder2}` },
  target: { outline: `2px dashed ${tokens.colorBrandStroke1}`, outlineOffset: "2px" },
  clip: { maxWidth: "100%", textOverflow: "ellipsis", overflow: "hidden", whiteSpace: "nowrap" },
  handle: {
    position: "absolute", width: "16px", height: "16px", padding: 0, borderRadius: 0, cursor: "crosshair", touchAction: "none",
    border: `1.5px solid ${tokens.colorNeutralForeground1}`, backgroundColor: tokens.colorNeutralBackground1,
    "&:focus-visible": { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: "2px" },
  },
  handleActive: { backgroundColor: tokens.colorBrandBackground, border: `1.5px solid ${tokens.colorBrandStroke1}` },
  hidden: { position: "absolute", width: "1px", height: "1px", overflow: "hidden", clipPath: "inset(50%)", whiteSpace: "nowrap" },
});

type Drag = { id: string; pointer: number; x0: number; y0: number; from: { x: number; y: number }; moved: boolean };

export function GkqlGraph({ model, problems, readOnly, dispatch }: {
  model: GkqlModel; problems: ReadonlyMap<string, string[]>; readOnly: boolean; dispatch: (e: GkqlEvent) => void;
}) {
  const s = useStyles();
  const ids = useId();
  const program = model.program!;
  const layout = useMemo(() => layoutGkql(program, model.moved), [program, model.moved]);
  const contents = useRef<HTMLDivElement>(null);
  const drag = useRef<Drag | null>(null);
  const wiring = useRef<Drag | null>(null);
  const clickAfterDrag = useRef(false);
  const [ghost, setGhost] = useState<{ x: number; y: number } | null>(null);
  const connecting = model.gesture.t === "connecting" ? model.gesture.from : null;
  const local = (e: PointerEvent) => { const r = contents.current!.getBoundingClientRect(); return { x: e.clientX - r.left, y: e.clientY - r.top }; };

  const cardDown = (e: PointerEvent<HTMLButtonElement>, id: string, at: { x: number; y: number }) => {
    clickAfterDrag.current = false;
    if (readOnly || e.button !== 0) return;
    e.currentTarget.setPointerCapture?.(e.pointerId);
    drag.current = { id, pointer: e.pointerId, x0: e.clientX, y0: e.clientY, from: at, moved: false };
  };
  const cardMove = (e: PointerEvent) => {
    const d = drag.current;
    if (!d || d.pointer !== e.pointerId) return;
    const dx = e.clientX - d.x0, dy = e.clientY - d.y0;
    if (!d.moved && Math.hypot(dx, dy) < 4) return;
    d.moved = true;
    dispatch({ t: "move", id: d.id, to: { x: d.from.x + dx, y: d.from.y + dy } });
  };
  const cardUp = () => { if (drag.current?.moved) clickAfterDrag.current = true; drag.current = null; };
  const cardClick = (id: string) => {
    if (clickAfterDrag.current) { clickAfterDrag.current = false; return; }
    if (connecting) dispatch({ t: "connect-end", to: id });
    else dispatch({ t: "select", focus: verbFocus(id) });
  };

  const handleDown = (e: PointerEvent<HTMLButtonElement>, id: string) => {
    clickAfterDrag.current = false;
    if (readOnly || e.button !== 0) return;
    e.currentTarget.setPointerCapture?.(e.pointerId);
    wiring.current = { id, pointer: e.pointerId, x0: e.clientX, y0: e.clientY, from: { x: 0, y: 0 }, moved: false };
  };
  const handleMove = (e: PointerEvent) => {
    const w = wiring.current;
    if (!w || w.pointer !== e.pointerId) return;
    if (!w.moved && Math.hypot(e.clientX - w.x0, e.clientY - w.y0) < 4) return;
    if (!w.moved) dispatch({ t: "connect-start", from: w.id });
    w.moved = true;
    setGhost(local(e));
  };
  const handleUp = (e: PointerEvent) => {
    const w = wiring.current;
    wiring.current = null;
    if (!w?.moved) return;
    setGhost(null);
    clickAfterDrag.current = true;
    const over = document.elementFromPoint?.(e.clientX, e.clientY)?.closest<HTMLElement>("[data-verb]")?.dataset.verb;
    dispatch(over ? { t: "connect-end", to: over } : { t: "connect-cancel" });
  };
  const handleClick = (id: string) => {
    if (clickAfterDrag.current) { clickAfterDrag.current = false; return; }
    dispatch(connecting === id ? { t: "connect-cancel" } : { t: "connect-start", from: id });
  };

  if (!layout.cards.length) return <div className={s.stage} role="region" aria-label="GKQL operations and wires"><Text className={s.empty}>Add an operation to start: a graph match reads an input graph.</Text></div>;
  const from = connecting ? layout.cards.find((c) => c.verb.id === connecting) : null;
  return (
    <div className={s.stage} role="region" aria-label="GKQL operations and wires" aria-describedby={`${ids}help`} tabIndex={0}>
      <span id={`${ids}help`} className={s.hidden}>Drag a card to move it. Drag a card's handle onto the operation it feeds, or press Enter on the handle and then on that operation. Alt and an arrow key move the focused card. Delete removes the focused card.</span>
      <div ref={contents} className={s.contents} style={{ width: layout.width + 24, height: layout.height }} onPointerMove={(e) => { cardMove(e); handleMove(e); }}>
        <svg className={s.svg} width={layout.width + 24} height={layout.height} aria-hidden="true">
          <defs>
            <marker id={`${ids}arrow`} orient="auto" markerWidth="6" markerHeight="6" refX="5" refY="3"><path d="M0 0 6 3 0 6Z" fill={tokens.colorNeutralForeground3} /></marker>
          </defs>
          {layout.wires.map(({ wire, d }, index) => {
            const focus = wireFocus(wire);
            return (
              <g key={index}>
                <path d={d} markerEnd={`url(#${ids}arrow)`} className={mergeClasses(s.wire, model.selected === focus && s.wireSelected, problems.has(focus) && s.wireError)} />
                <path d={d} className={s.hit} onClick={() => dispatch({ t: "select", focus })} />
              </g>
            );
          })}
          {from && ghost && <path className={s.ghost} d={`M${from.x + VERB_WIDTH},${from.y + VERB_HEIGHT / 2} L${ghost.x},${ghost.y}`} />}
        </svg>
        <ul className={s.cards}>
          {layout.cards.map(({ verb, x, y }, index) => {
            const key = layout.cards.findIndex((c) => c.verb.id === verb.id) === index ? verb.id : `${verb.id}/${index}`;
            const focus = verbFocus(verb.id), errors = problems.get(focus) ?? [];
            const describe = errors.length ? `${ids}e${index}` : undefined;
            return (
              <li key={key}>
                <button type="button" data-verb={verb.id} aria-pressed={model.selected === focus} aria-describedby={describe}
                  className={mergeClasses(s.card, verbStyle(verb.verb) === "input" && s.input, model.selected === focus && s.selected, errors.length > 0 && s.error, connecting !== null && connecting !== verb.id && s.target)}
                  style={{ left: x, top: y, width: VERB_WIDTH }}
                  onPointerDown={(e) => cardDown(e, verb.id, { x, y })} onPointerUp={cardUp} onPointerCancel={() => { drag.current = null; }} onClick={() => cardClick(verb.id)}>
                  <Badge size="small" appearance="outline" color="informative">{verb.verb}</Badge>
                  <Text weight="semibold" className={s.clip} title={verb.id}>{verb.id}</Text>
                </button>
                {describe && <span id={describe} className={s.hidden}>{errors.join(". ")}</span>}
                {!readOnly && <button type="button" aria-label={`Connect from ${verb.id}`} aria-pressed={connecting === verb.id}
                  className={mergeClasses(s.handle, connecting === verb.id && s.handleActive)} style={{ left: x + VERB_WIDTH - 8, top: y + VERB_HEIGHT / 2 - 8 }}
                  onPointerDown={(e) => handleDown(e, verb.id)} onPointerUp={handleUp} onPointerCancel={() => { if (wiring.current?.moved) dispatch({ t: "connect-cancel" }); wiring.current = null; setGhost(null); }} onClick={() => handleClick(verb.id)} />}
              </li>
            );
          })}
        </ul>
      </div>
    </div>
  );
}
