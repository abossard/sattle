// The pieces every editor is built from: a row's text, the inline editor of a row, its problems. One look and one behaviour
// for the same thing in all of them: Enter commits, Escape cancels, leaving a text field commits what was typed.
import { Badge, Button, Input, Select, makeStyles, tokens } from "@fluentui/react-components";
import { Dismiss16Regular } from "@fluentui/react-icons";
import { useEffect, useRef, useState, type KeyboardEvent, type ReactElement, type ReactNode } from "react";
import type { Gesture } from "../editing/gestures";
import { rowText, type EditSpec, type FormField, type Row, type Token } from "./rows";

export const useParts = makeStyles({
  keyword: { color: tokens.colorBrandForeground1, fontWeight: tokens.fontWeightSemibold },
  name: { fontWeight: tokens.fontWeightSemibold },
  value: { color: tokens.colorNeutralForeground1 },
  plain: { color: tokens.colorNeutralForeground3 },
  hole: { color: tokens.colorNeutralForeground3, fontStyle: "italic", borderBottom: `1px dashed ${tokens.colorNeutralStroke1}` },
  mono: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase300, whiteSpace: "pre" },
  form: { display: "inline-flex", alignItems: "center", columnGap: tokens.spacingHorizontalXS, flexWrap: "wrap" },
  line: { display: "flex", alignItems: "center", columnGap: tokens.spacingHorizontalXS, minHeight: "28px" },
  grow: { flexGrow: 1 },
  only: { position: "absolute", width: "1px", height: "1px", overflow: "hidden", clip: "rect(0 0 0 0)", whiteSpace: "nowrap" },
});

/** The text of a row, each piece styled by what it is. A hole is drawn dashed and italic, never by colour alone. */
export function Tokens({ tokens: ts }: { tokens: Token[] }): ReactElement {
  const s = useParts();
  return <span className={s.mono}>{ts.map((t, i) => <span key={i} className={s[t.as]}>{t.text}</span>)}</span>;
}

/** Problems of a row as a count with the first one in words; the rest is in the title and read out in full. */
export function Problems({ list }: { list: string[] }): ReactElement | null {
  const s = useParts();
  if (!list.length) return null;
  return <Badge size="small" appearance="outline" color="danger" title={list.join("\n")}><span aria-hidden="true">! </span>{list[0]}{list.length > 1 ? ` (+${list.length - 1})` : ""}<span className={s.only}>{list.slice(1).join(", ")}</span></Badge>;
}

const stop = (e: KeyboardEvent) => { if (e.key !== "Escape" && e.key !== "Enter") e.stopPropagation(); };

function Field({ f, value, set, autoFocus }: { f: FormField; value: string; set(v: string): void; autoFocus: boolean }): ReactElement {
  if (f.options) return (
    <Select size="small" aria-label={f.label} value={value} autoFocus={autoFocus} onChange={(_, d) => set(d.value)}>
      <option value="">{f.label}…</option>
      {f.options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
    </Select>
  );
  return <Input size="small" aria-label={f.label} placeholder={f.optional ? `${f.label} (optional)` : f.label} value={value} autoFocus={autoFocus} onChange={(_, d) => set(d.value)} />;
}

/**
 * The inline editor of a row. `done` gets the gesture to perform, or null when the author left without a change.
 * Keys typed here stay here (the tree or canvas around it does not see them), apart from Enter and Escape, which end the edit.
 */
export function Editing({ spec, done }: { spec: EditSpec; done(g: Gesture | null): void }): ReactElement {
  const s = useParts();
  const [text, setText] = useState(spec.kind === "form" ? "" : spec.value);
  const [values, setValues] = useState<Record<string, string>>({});
  const ended = useRef(false);
  const end = (g: Gesture | null) => { if (!ended.current) { ended.current = true; done(g); } };
  const first = useRef<HTMLSpanElement>(null);
  useEffect(() => { first.current?.querySelector<HTMLElement>("input, select")?.focus(); }, []);
  // a click in the editor is not a click on the row around it (the tree would take the focus back)
  const own = { onClick: (e: { stopPropagation(): void }) => e.stopPropagation(), onDoubleClick: (e: { stopPropagation(): void }) => e.stopPropagation() };
  const keys = (commit: () => void) => (e: KeyboardEvent) => {
    stop(e);
    if (e.key === "Enter") { e.preventDefault(); e.stopPropagation(); commit(); }
    if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); end(null); }
  };
  if (spec.kind === "text") return (
    <span ref={first} className={s.form} {...own} onKeyDown={keys(() => end(spec.commit(text)))}>
      <Input size="small" aria-label={spec.label} value={text} onChange={(_, d) => setText(d.value)} onBlur={() => end(spec.commit(text))} />
    </span>
  );
  if (spec.kind === "choice") return (
    <span ref={first} className={s.form} {...own} onKeyDown={keys(() => end(spec.commit(text)))}>
      <Select size="small" aria-label={spec.label} value={text} onChange={(_, d) => { setText(d.value); end(spec.commit(d.value)); }} onBlur={() => end(null)}>
        <option value="">{spec.label}…</option>
        {spec.options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
      </Select>
    </span>
  );
  const commit = () => { const g = spec.commit(values); if (g) end(g); };
  return (
    <span ref={first} className={s.form} role="group" aria-label={spec.label} {...own} onKeyDown={keys(commit)}>
      {spec.fields.map((f, i) => <Field key={f.key} f={f} value={values[f.key] ?? ""} set={(v) => setValues((x) => ({ ...x, [f.key]: v }))} autoFocus={i === 0} />)}
      <Button size="small" appearance="primary" onClick={commit} disabled={spec.commit(values) === null}>Add</Button>
      <Button size="small" appearance="subtle" onClick={() => end(null)}>Cancel</Button>
    </span>
  );
}

/**
 * A row outside a tree (in a card, a block, a panel): its text is a button that opens the row's editor, and a row that can be
 * removed has a button for it. Every change has a click and a key (the buttons), so nothing here needs a drag.
 */
export function RowLine({ row, busy, onGesture, extra }: { row: Row; busy: boolean; onGesture(g: Gesture): void; /** what a notation adds to the row's actions */ extra?: ReactNode }): ReactElement {
  const s = useParts();
  const [editing, setEditing] = useState(false);
  const text = rowText(row);
  return (
    <div className={s.line} data-row-id={row.id} data-kind={row.kind}>
      {editing && row.edit
        ? <Editing spec={row.edit} done={(g) => { setEditing(false); if (g) onGesture(g); }} />
        : row.edit
          ? <Button size="small" appearance="subtle" aria-label={`${text}, edit`} onClick={() => setEditing(true)}><Tokens tokens={row.tokens} /></Button>
          : <Tokens tokens={row.tokens} />}
      <span className={s.grow} />
      <Problems list={row.problems} />
      {extra}
      {row.remove && <Button size="small" appearance="subtle" icon={<Dismiss16Regular />} aria-label={`remove ${text}`} disabled={busy} onClick={() => onGesture(row.remove!)} />}
    </div>
  );
}
