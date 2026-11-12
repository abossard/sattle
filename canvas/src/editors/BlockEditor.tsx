// A projection as blocks: every row set is a stack, every step a block in it, and the palette on the left holds the blocks the
// engine offers. A block is picked up (from the palette, or a step by its move button) and put down on one of the places that
// take it: only those are shown, so a stack that cannot work cannot be built. Dragging does the same pick up and put down;
// nothing needs a drag (click or Enter picks up and puts down, Alt+Arrow moves a step by one, Escape lets go).
import { Button, Caption1, Text, ToggleButton, makeStyles, mergeClasses, tokens } from "@fluentui/react-components";
import { ArrowMove20Regular } from "@fluentui/react-icons";
import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactElement } from "react";
import type { ProjectionDoc } from "../editing/doc";
import type { Gesture } from "../editing/gestures";
import { freeName } from "../editing/scope";
import type { Palette } from "../editor/palette";
import type { EditorProps } from "./host";
import { RowLine } from "./parts";
import { kindWord, rowsOf, type Row } from "./rows";
import { useViewSize } from "./view-size";
import { span } from "./window";

/** The block in the author's hand: a new one of a kind, or a step of the draft. */
export interface Held { kind: string; step?: { id: string; statement: string; index: number } }
/** A place the held block fits: the gap `gap` of a statement (0 is before its first step), or a row set of its own. */
export interface Place { statement: string | null; gap: number; label: string; gesture: Gesture }

/** The places that take the held block, by what the palette says its kind reads and gives. */
export function places(doc: ProjectionDoc, palette: Palette, held: Held): Place[] {
  const spec = (kind: string) => palette.operators.find((o) => o.kind === kind);
  const continues = (kind: string) => !!spec(kind)?.ports.some((p) => p.direction === "in" && p.kind === "rows");
  const sink = !spec(held.kind)?.ports.some((p) => p.direction === "out");
  const word = kindWord(held.kind);
  const put = (statement: string, at: number | undefined): Gesture => (held.step ? { g: "moveStep", id: held.step.id, statement, at } : { g: "addStep", kind: held.kind, statement, at });
  const out: Place[] = [];
  if (continues(held.kind) && !sink) for (const st of doc.statements) {
    if (st.sink) continue;
    const own = held.step?.statement === st.name ? held.step.index : -1;
    for (let gap = 0; gap <= st.steps.length; gap++) {
      // before a step that reads no rows nothing can stand; the gaps around the held step are where it is
      if ((gap === 0 && !continues(st.steps[0].kind)) || (own >= 0 && (gap === own || gap === own + 1))) continue;
      out.push({ statement: st.name, gap, label: `${word} to ${st.name}, position ${gap + 1}`, gesture: put(st.name, own >= 0 && gap > own ? gap - 1 : gap) });
    }
  }
  const alone = held.step && doc.statements.find((s) => s.name === held.step!.statement)?.steps.length === 1;
  if (!alone) out.push({ statement: null, gap: 0, label: `${word} as a new ${sink ? "sink" : "row set"}`, gesture: put(freeName(doc, sink), undefined) });
  return out;
}

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateColumns: "minmax(140px, 200px) minmax(0, 1fr)", columnGap: tokens.spacingHorizontalL, height: "100%", minHeight: 0 },
  palette: { overflow: "auto", minHeight: 0, display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalXXS, paddingRight: tokens.spacingHorizontalS, borderRight: `1px solid ${tokens.colorNeutralStroke2}` },
  kinds: { display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalXXS },
  role: { marginTop: tokens.spacingVerticalS, color: tokens.colorNeutralForeground3 },
  kind: { justifyContent: "flex-start", borderLeftWidth: "4px" },
  work: { overflow: "auto", minHeight: 0, display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalM, padding: tokens.spacingHorizontalS },
  stack: { display: "flex", flexDirection: "column", rowGap: "2px", padding: tokens.spacingHorizontalS, borderRadius: tokens.borderRadiusLarge, backgroundColor: tokens.colorNeutralBackground2, border: `1px solid ${tokens.colorNeutralStroke2}`, maxWidth: "720px" },
  block: { borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke1}`, borderLeftWidth: "4px", padding: `0 ${tokens.spacingHorizontalS}` },
  inner: { paddingLeft: tokens.spacingHorizontalL },
  selected: { outline: `2px solid ${tokens.colorBrandStroke1}`, outlineOffset: "1px" },
  lifted: { opacity: 0.5 },
  place: { justifyContent: "flex-start", border: `1px dashed ${tokens.colorBrandStroke1}`, maxWidth: "720px" },
  source: { borderLeftColor: tokens.colorPaletteBlueBorderActive },
  linear: { borderLeftColor: tokens.colorPaletteTealBorderActive },
  telemetry: { borderLeftColor: tokens.colorPalettePurpleBorderActive },
  lift: { borderLeftColor: tokens.colorPaletteMarigoldBorderActive },
  sink: { borderLeftColor: tokens.colorPaletteGreenBorderActive },
});
type RoleClass = "source" | "linear" | "telemetry" | "lift" | "sink";
const ROLES: readonly string[] = ["source", "linear", "telemetry", "lift", "sink"];
const STEP_PAD = 10, STACK_PAD = 60, LINE = 28;

export interface BlockProps extends EditorProps { /** height of the view, when the caller knows it (tests, measurements) */ viewport?: number }

export function BlockEditor(p: BlockProps): ReactElement {
  const s = useStyles();
  const [held, setHeld] = useState<Held | null>(null);
  const [top, setTop] = useState(0);
  const work = useRef<HTMLDivElement>(null);
  const rows = useMemo(() => rowsOf(p.doc, p.palette, p.cat), [p.doc, p.palette, p.cat]);
  const byId = useMemo(() => new Map(rows.map((r) => [r.id, r])), [rows]);
  const byParent = useMemo(() => { const m = new Map<string, Row[]>(); for (const r of rows) if (r.parent) m.set(r.parent, [...(m.get(r.parent) ?? []), r]); return m; }, [rows]);
  const open = useMemo(() => (held ? places(p.doc, p.palette, held) : []), [held, p.doc, p.palette]);
  const roleOf = (kind: string): string => p.palette.operators.find((o) => o.kind === kind)?.role ?? "";
  const tint = (kind: string): string | undefined => { const r = roleOf(kind); return ROLES.includes(r) ? s[r as RoleClass] : undefined; };
  const put = (pl: Place) => { setHeld(null); p.onGesture(pl.gesture); };
  // after a block was put down from the keyboard, the cursor is on it
  const follow = useRef(false);
  useEffect(() => {
    if (!follow.current || !p.selected) return;
    follow.current = false;
    work.current?.querySelector<HTMLElement>(`[data-step="${CSS.escape(p.selected)}"] [data-move]`)?.focus();
  }, [p.selected, p.doc]);

  const heights = useMemo(() => p.doc.statements.map((st) => STACK_PAD + st.steps.reduce((h, x) => h + STEP_PAD + LINE * (1 + (byParent.get(x.id)?.length ?? 0)), 0)), [p.doc, byParent]);
  const view = span(heights, top, useViewSize(work, { height: p.viewport }).height);

  // a place the held block fits, as a button where it would go (an element, not a component: it keeps the cursor when the view scrolls)
  const target = (statement: string | null, gap: number): ReactElement | null => {
    const pl = open.find((x) => x.statement === statement && x.gap === gap);
    if (!pl) return null;
    return <Button key={`put:${statement}:${gap}`} className={s.place} size="small" appearance="outline" aria-label={`put ${pl.label}`} onClick={() => { follow.current = true; put(pl); }} onDragOver={(e) => e.preventDefault()} onDrop={(e) => { e.preventDefault(); put(pl); }}>{kindWord(held!.kind)} here</Button>;
  };
  const byOne = (e: KeyboardEvent, h: Held) => {
    if (!e.altKey || (e.key !== "ArrowUp" && e.key !== "ArrowDown")) return;
    e.preventDefault();
    const pl = places(p.doc, p.palette, h).find((x) => x.statement === h.step!.statement && x.gap === h.step!.index + (e.key === "ArrowUp" ? -1 : 2));
    if (pl) { follow.current = true; put(pl); }
  };
  const groups = [...new Set(p.palette.operators.map((o) => o.role))].sort((a, b) => ROLES.indexOf(a) - ROLES.indexOf(b));

  return (
    <div className={s.root} onKeyDown={(e) => { if (e.key === "Escape" && held) { e.stopPropagation(); setHeld(null); } }}>
      <div className={s.palette} role="group" aria-label="Blocks">
        {groups.map((role) => (
          <div key={role} className={s.kinds} role="group" aria-label={`${role} blocks`}>
            <Caption1 className={s.role}>{role}</Caption1>
            {p.palette.operators.filter((o) => o.role === role).map((o) => (
              <ToggleButton key={o.kind} size="small" className={mergeClasses(s.kind, tint(o.kind))} checked={held?.kind === o.kind && !held.step} disabled={!o.supported || p.busy} title={o.supported ? undefined : o.unsupportedReason}
                draggable={o.supported} onDragStart={() => setHeld({ kind: o.kind })} onDragEnd={() => setHeld(null)}
                onClick={() => setHeld((h) => (h?.kind === o.kind && !h.step ? null : { kind: o.kind }))}>{kindWord(o.kind)}</ToggleButton>
            ))}
          </div>
        ))}
      </div>
      <div className={s.work} ref={work} onScroll={(e) => setTop(e.currentTarget.scrollTop)}>
        <Text role="status" size={200}>{held ? `Holding ${kindWord(held.kind)}: ${open.length} place${open.length === 1 ? "" : "s"} take it. Escape lets go.` : ""}</Text>
        <div style={{ height: view.before }} />
        {p.doc.statements.slice(view.start, view.end).map((st) => {
          const head = byId.get(`stmt:${st.name}`)!;
          return (
            <div key={st.name} className={s.stack} role="group" aria-label={`${st.sink ? "sink" : "row set"} ${st.name}`}>
              <RowLine row={head} busy={p.busy} onGesture={p.onGesture} extra={st.readers.length ? <Caption1>read by {st.readers.join(", ")}</Caption1> : undefined} />
              {st.steps.map((step, i) => {
                const me: Held = { kind: step.kind, step: { id: step.id, statement: st.name, index: i } };
                const lifted = held?.step?.id === step.id;
                const word = kindWord(step.kind);
                return (
                  <div key={step.id} style={{ display: "contents" }}>
                    {target(st.name, i)}
                    <div className={mergeClasses(s.block, tint(step.kind), p.selected === step.id && s.selected, lifted && s.lifted)} role="group" aria-label={`${word} block, ${i + 1} of ${st.steps.length} in ${st.name}`} data-step={step.id}
                      draggable={!st.sink} onDragStart={() => setHeld(me)} onDragEnd={() => setHeld(null)} onClick={() => p.onSelect(step.id)}>
                      <RowLine row={byId.get(step.id)!} busy={p.busy} onGesture={p.onGesture} extra={st.sink ? undefined : (
                        <ToggleButton size="small" appearance="subtle" icon={<ArrowMove20Regular />} data-move checked={lifted} disabled={p.busy} aria-label={`move ${word}, step ${i + 1} of ${st.name}`} title="Move: pick up, then choose a place. Alt+Up and Alt+Down move by one."
                          onClick={(e) => { e.stopPropagation(); setHeld(lifted ? null : me); }} onKeyDown={(e) => byOne(e, me)} />
                      )} />
                      <div className={s.inner}>{(byParent.get(step.id) ?? []).map((r) => <RowLine key={r.id} row={r} busy={p.busy} onGesture={p.onGesture} />)}</div>
                    </div>
                  </div>
                );
              })}
              {target(st.name, st.steps.length)}
            </div>
          );
        })}
        <div style={{ height: view.after }} />
        {target(null, 0)}
      </div>
    </div>
  );
}
