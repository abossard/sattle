// Steps on the KQL outline (note 11, stage b): each statement as a card, each operator of its pipeline as a row with its rows count, beside
// the text. A notation is a pure view: a reading (the text and the engine's outline of it), the selected place and the busy mark in; a place
// to select and text changes out. It knows nothing of the engine or of the text editor.
import { Button, Input, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Text, Tooltip, makeStyles, mergeClasses, tokens } from "@fluentui/react-components";
import { Add16Regular, ArrowDown16Regular, ArrowUp16Regular, Delete16Regular, Edit16Regular } from "@fluentui/react-icons";
import { useState } from "react";
import type { Analysis, Place } from "../../editor/kql";
import { add, addBy, addStatement, addWhere, columnFor, listsOf, move, newOperatorAt, remove, set, starter, startersFor, type Change, type Column, type List } from "../../editor/kql-edit";

export interface Reading { text: string; analysis: Analysis }
/** What every notation is given, and its two ways out: a place to select, text changes. */
export interface NotationProps {
  reading: Reading | null; selected: number | null; busy: boolean;
  /** the forms a statement starts with (`nodes`, `let`, ...), as the engine completes them at a statement's start; absent until known */
  forms?: readonly string[];
  onSelect(place: Place): void; onChanges(changes: Change[], select: Place | null): void;
}
interface Props extends NotationProps {
  /** rows after each card, by card id (`cardsOf`), for the text as stored; absent until asked */
  rows: Map<string, string> | null;
}

const useStyles = makeStyles({
  root: { overflow: "auto", minHeight: 0, display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalS, paddingRight: tokens.spacingHorizontalXS },
  busy: { opacity: 0.6 },
  card: { border: `1.5px solid ${tokens.colorNeutralForeground1}`, backgroundColor: tokens.colorNeutralBackground1, margin: 0, padding: 0 },
  sel: { outline: `2px solid ${tokens.colorBrandStroke1}`, outlineOffset: "-1px" },
  head: { display: "flex", alignItems: "center", columnGap: tokens.spacingHorizontalS, padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`, borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  ops: { listStyle: "none", margin: 0, padding: 0 },
  op: { display: "grid", gridTemplateColumns: "6.5em minmax(0, 1fr) auto auto", alignItems: "center", columnGap: tokens.spacingHorizontalS, padding: `2px ${tokens.spacingHorizontalS}`, borderTop: `1px solid ${tokens.colorNeutralStroke3}`, cursor: "pointer", "&:first-child": { borderTop: "none" } },
  opSel: { backgroundColor: tokens.colorBrandBackground2 },
  kind: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  code: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" },
  rows: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground2, textAlign: "end", whiteSpace: "nowrap" },
  acts: { display: "flex", columnGap: "2px" },
  grow: { flexGrow: 1 },
  // a plain button: the text it shows is the item; the focus ring is the sheet's
  pick: { backgroundColor: "transparent", border: "none", padding: 0, margin: 0, font: "inherit", color: "inherit", textAlign: "start", cursor: "pointer", minWidth: 0, "&:focus-visible": { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: "1px" } },
});

const oneLine = (t: string) => t.replace(/\s+/g, " ").trim();
const inside = (p: Place, at: number | null) => at !== null && p[0] <= at && at <= p[1];

/** Move up, move down, remove, and edit in place for item i of a list: each gives text changes on the reading's text. */
function Actions({ text, list, i, what, onChanges, onEdit }: { text: string; list: List; i: number; what: string; onChanges: Props["onChanges"]; onEdit?: () => void }) {
  const s = useStyles();
  const n = list.items.length;
  return (
    <span className={s.acts} onClick={(e) => e.stopPropagation()}>
      {onEdit && <Tooltip content={`Edit ${what}`} relationship="label"><Button size="small" appearance="subtle" icon={<Edit16Regular />} onClick={onEdit} /></Tooltip>}
      <Tooltip content={`Move ${what} up`} relationship="label"><Button size="small" appearance="subtle" icon={<ArrowUp16Regular />} disabled={i === 0} onClick={() => { const m = move(text, list, i, i - 1); onChanges(m.changes, m.select); }} /></Tooltip>
      <Tooltip content={`Move ${what} down`} relationship="label"><Button size="small" appearance="subtle" icon={<ArrowDown16Regular />} disabled={i === n - 1} onClick={() => { const m = move(text, list, i, i + 1); onChanges(m.changes, m.select); }} /></Tooltip>
      <Tooltip content={`Remove ${what}`} relationship="label"><Button size="small" appearance="subtle" icon={<Delete16Regular />} disabled={n < 2} onClick={() => onChanges(remove(text, list, i).changes, null)} /></Tooltip>
    </span>
  );
}

/** A new statement at the end of the file: pick the form it starts with, write the rest, Enter adds it. Every notation shows this. */
export function AddStatement({ reading, busy, forms, onChanges }: Pick<NotationProps, "reading" | "busy" | "forms" | "onChanges">) {
  const [value, setValue] = useState<string | null>(null);
  const statements = reading ? listsOf(reading.analysis).find((l) => l.id === "statements") : undefined;
  if (!reading || !statements || !forms?.length) return null;
  const done = () => { const e = addStatement(reading.text, statements, value!.trim()); setValue(null); onChanges(e.changes, e.select); };
  return value === null
    ? <Menu>
        <MenuTrigger disableButtonEnhancement><Button size="small" appearance="subtle" icon={<Add16Regular />} disabled={busy}>Add a statement</Button></MenuTrigger>
        <MenuPopover><MenuList>{forms.map((f) => <MenuItem key={f} onClick={() => setValue(`${f} `)}>{f}</MenuItem>)}</MenuList></MenuPopover>
      </Menu>
    : <Input size="small" aria-label="Text of the new statement" autoFocus value={value} onChange={(_, d) => setValue(d.value)}
        onKeyDown={(e) => { e.stopPropagation(); if (e.key === "Enter" && value.trim() && !busy) done(); else if (e.key === "Escape") setValue(null); }} onBlur={() => setValue(null)} />;
}

export function KqlSteps({ reading, selected, busy, forms, rows, onSelect, onChanges }: Props) {
  const s = useStyles();
  const [editing, setEditing] = useState<{ place: Place; value: string } | null>(null);
  if (!reading) return <Text>Waiting for the engine's outline of the text…</Text>;
  const { text, analysis } = reading;
  const lists = listsOf(analysis);
  const statements = lists.find((l) => l.id === "statements")!;
  const stmts = (analysis.statements as { kind: string; name?: string | null; type?: string | null; place: Place }[] | undefined) ?? [];
  const opsOf = (i: number) => lists.find((l) => l.id === `statements.${i}.operators`);
  type Op = { kind: string; columns?: Column[]; parts?: { by?: unknown[]; where?: unknown; pattern?: unknown[] } };
  const kinds = (analysis.statements as { kind: string; operators?: Op[] }[]) ?? [];
  const field = (place: Place, what: string) => editing && editing.place === place
    ? <Input size="small" aria-label={`Text of ${what}`} autoFocus value={editing.value} onClick={(e) => e.stopPropagation()} onChange={(_, d) => setEditing({ place, value: d.value })}
        onKeyDown={(e) => { if (e.key === "Enter") { const ed = set([place], editing.value); onChanges(ed.changes, ed.select); setEditing(null); } else if (e.key === "Escape") setEditing(null); }}
        onBlur={() => setEditing(null)} />
    : <button type="button" className={mergeClasses(s.pick, s.code)} title={text.slice(place[0], place[1])} onClick={(e) => { e.stopPropagation(); onSelect(place); }}>{oneLine(text.slice(place[0], place[1]))}</button>;
  return (
    <div className={mergeClasses(s.root, busy && s.busy)} aria-busy={busy} aria-label="Steps of the projection" role="region">
      {stmts.map((st, i) => {
        const index = statements.items.findIndex((p) => p[0] === st.place[0]);
        const ops = opsOf(i);
        const what = `${st.kind}${st.name ? ` ${st.name}` : st.type ? ` ${st.type}` : ""}`;
        return (
          <div key={i} role="group" className={mergeClasses(s.card, inside(st.place, selected) && s.sel)} aria-label={what}>
            <div className={s.head} onClick={() => onSelect(st.place)}>
              <button type="button" className={s.pick} onClick={(e) => { e.stopPropagation(); onSelect(st.place); }}><Text weight="semibold">{what}</Text></button><span className={s.grow} />
              {rows?.get(`statements.${i}`) && <span className={s.rows}>{rows.get(`statements.${i}`)} rows</span>}
              {ops && (() => {
                const k = newOperatorAt(kinds[i]);
                const cols = kinds[i]?.operators?.[k - 1]?.columns ?? [];
                const offer = startersFor(kinds[i]).flatMap((kind) => { const op = starter(kind, cols); return op ? [[kind, op] as const] : []; });
                return offer.length > 0 && (
                  <Menu>
                    <MenuTrigger disableButtonEnhancement><Tooltip content={`Add an operator to ${what}`} relationship="label"><Button size="small" appearance="subtle" icon={<Add16Regular />} onClick={(e) => e.stopPropagation()} /></Tooltip></MenuTrigger>
                    <MenuPopover><MenuList>{offer.map(([kind, op]) => <MenuItem key={kind} onClick={() => { const e = add(text, ops, k, op); onChanges(e.changes, e.select); }}>{op}</MenuItem>)}</MenuList></MenuPopover>
                  </Menu>
                );
              })()}
              {index >= 0 && <Actions text={text} list={statements} i={index} what={what} onChanges={onChanges} />}
            </div>
            {ops && (
              <ol className={s.ops} aria-label={`Operators of ${what}`}>
                {ops.items.map((p, j) => {
                  const kind = kinds[i]?.operators?.[j]?.kind ?? "";
                  const id = `statements.${i}.operators.${j}`;
                  return (
                    <li key={j} className={mergeClasses(s.op, inside(p, selected) && s.opSel)} onClick={() => onSelect(p)}>
                      <span className={s.kind}>{kind}</span>
                      {field(p, `${kind} of ${what}`)}
                      <span className={s.rows}>{rows?.get(id) ? `${rows.get(id)} rows` : ""}
                        {kind === "summarize" && !kinds[i]?.operators?.[j]?.parts?.by?.length && columnFor(kinds[i]?.operators?.[j - 1]?.columns ?? []) && <Button size="small" appearance="subtle" onClick={(e) => { e.stopPropagation(); const ed = addBy((analysis.statements as never[])[i]["operators"][j], columnFor(kinds[i]?.operators?.[j - 1]?.columns ?? [])!); if (ed) onChanges(ed.changes, ed.select); }}>Add by</Button>}
                        {kind === "graph-match" && !kinds[i]?.operators?.[j]?.parts?.where && <Button size="small" appearance="subtle" onClick={(e) => { e.stopPropagation(); const ed = addWhere((analysis.statements as never[])[i]["operators"][j], "true"); if (ed) onChanges(ed.changes, ed.select); }}>Add where</Button>}
                      </span>
                      <Actions text={text} list={ops} i={j} what={`${kind} of ${what}`} onChanges={onChanges} onEdit={() => setEditing({ place: p, value: text.slice(p[0], p[1]) })} />
                    </li>
                  );
                })}
              </ol>
            )}
          </div>
        );
      })}
      <AddStatement reading={reading} busy={busy} forms={forms} onChanges={onChanges} />
    </div>
  );
}
