// The Wires notation on the KQL outline (note 11, stage c): the inputs, `let`s and steps as cards, wired by what each statement reads, the
// steps of one type in one frame. A card selects its statement in the text; the statement at the cursor can be pointed at another `let` where
// it names the table it reads (its query, the right side of a join). A pure view like Steps: a reading, the selected place and the busy mark
// in; a place to select and text changes out.
import { Field, Select, Text, makeStyles, mergeClasses, tokens } from "@fluentui/react-components";
import { useMemo } from "react";
import { set } from "../../editor/kql-edit";
import { CARD_H, CARD_W, targetsOf, wiresOf } from "../../editor/kql-wires";
import { AddStatement, type NotationProps } from "./KqlSteps";

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateRows: "minmax(0, 1fr) auto auto", minHeight: 0, height: "100%", rowGap: tokens.spacingVerticalS },
  busy: { opacity: 0.6 },
  scroll: { overflow: "auto", minHeight: 0, border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground1 },
  sheet: { position: "relative" },
  svg: { position: "absolute", inset: 0, pointerEvents: "none" },
  wire: { fill: "none", stroke: tokens.colorNeutralForeground3, strokeWidth: 1 },
  wireOn: { stroke: tokens.colorBrandForeground1, strokeWidth: 2 },
  frame: { fill: "none", stroke: tokens.colorNeutralStroke1, strokeDasharray: "4 3" },
  frameLabel: { fill: tokens.colorNeutralForeground3, fontSize: "11px", fontFamily: tokens.fontFamilyBase },
  cards: { listStyle: "none", margin: 0, padding: 0 },
  card: {
    position: "absolute", width: `${CARD_W}px`, height: `${CARD_H}px`, boxSizing: "border-box", display: "flex", flexDirection: "column", justifyContent: "center", alignItems: "flex-start",
    padding: `0 ${tokens.spacingHorizontalS}`, border: `1.5px solid ${tokens.colorNeutralForeground1}`, backgroundColor: tokens.colorNeutralBackground1, color: tokens.colorNeutralForeground1,
    font: "inherit", textAlign: "start", cursor: "pointer", overflow: "hidden",
    "&:focus-visible": { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: "2px" },
  },
  input: { borderTopStyle: "dashed", borderRightStyle: "dashed", borderBottomStyle: "dashed", borderLeftStyle: "dashed" },
  on: { backgroundColor: tokens.colorBrandBackground2, borderTopColor: tokens.colorBrandStroke1, borderRightColor: tokens.colorBrandStroke1, borderBottomColor: tokens.colorBrandStroke1, borderLeftColor: tokens.colorBrandStroke1 },
  title: { fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase200, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis", maxWidth: "100%" },
  reads: { fontSize: tokens.fontSizeBase100, color: tokens.colorNeutralForeground3, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis", maxWidth: "100%" },
  panel: { display: "flex", flexWrap: "wrap", columnGap: tokens.spacingHorizontalM, rowGap: tokens.spacingVerticalXS, alignItems: "end" },
});

export function KqlWires({ reading, selected, busy, forms, onSelect, onChanges }: NotationProps) {
  const s = useStyles();
  const layout = useMemo(() => (reading ? wiresOf(reading.analysis) : null), [reading]);
  if (!reading || !layout) return <Text>Waiting for the engine's outline of the text…</Text>;
  const names = new Map(layout.cards.map((c) => [c.id, c.title]));
  const readBy = new Map<string, string[]>();
  for (const w of layout.wires) readBy.set(w.from, [...(readBy.get(w.from) ?? []), w.to]);
  const here = selected === null ? null : layout.cards.find((c) => c.id.startsWith("statements.") && c.place[0] <= selected && selected <= c.place[1]) ?? null;
  const targets = here ? targetsOf(reading.text, reading.analysis, Number(here.id.slice("statements.".length))) : [];
  const list = (ids: string[]) => ids.map((id) => names.get(id)).join(", ");
  return (
    <div className={mergeClasses(s.root, busy && s.busy)} aria-busy={busy}>
      <div className={s.scroll} role="region" aria-label="Wires of the projection">
        <div className={s.sheet} style={{ width: layout.width, height: layout.height }}>
          <svg className={s.svg} width={layout.width} height={layout.height} aria-hidden="true">
            {layout.groups.map((g) => <g key={`${g.type}${g.x}${g.y}`}><rect className={s.frame} x={g.x} y={g.y} width={g.w} height={g.h} /><text className={s.frameLabel} x={g.x + 6} y={g.y + 13}>{g.type}</text></g>)}
            {layout.wires.map((w) => <path key={`${w.from}>${w.to}`} d={w.d} className={mergeClasses(s.wire, !!here && (w.from === here.id || w.to === here.id) && s.wireOn)} />)}
          </svg>
          <ul className={s.cards} aria-label="Inputs, lets and steps, in columns by what they read">
            {layout.cards.map((c) => (
              <li key={c.id}>
                <button type="button" className={mergeClasses(s.card, c.kind === "input" && s.input, here?.id === c.id && s.on)} style={{ left: c.x, top: c.y }} aria-current={here?.id === c.id ? "true" : undefined} onClick={() => onSelect(c.place)}>
                  <span className={s.title}>{c.title}</span>
                  <span className={s.reads}>{c.reads.length ? `reads ${list(c.reads)}` : readBy.has(c.id) ? `read by ${readBy.get(c.id)!.length}` : "reads nothing"}</span>
                </button>
              </li>
            ))}
          </ul>
        </div>
      </div>
      {here && (
        <div className={s.panel} role="group" aria-label={`What ${here.title} reads`}>
          <Text size={200}>{here.title}: {here.reads.length ? `reads ${list(here.reads)}` : "reads no table"}{readBy.has(here.id) ? `; read by ${list(readBy.get(here.id)!)}` : ""}</Text>
          {targets.map((t) => (
            <Field key={t.place[0]} label={`${t.what}`} size="small">
              <Select size="small" value={t.name} disabled={busy} onChange={(_, d) => { const e = set([t.place], d.value); onChanges(e.changes, e.select); }}>
                {t.options.map((o) => <option key={o} value={o}>{o}</option>)}
              </Select>
            </Field>
          ))}
        </div>
      )}
      <div><AddStatement reading={reading} busy={busy} forms={forms} onChanges={onChanges} /></div>
    </div>
  );
}
