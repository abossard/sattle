// Statement kinds in the editor: make one from a row set, give it parameters, open its template, expand a use. The kinds are held
// by the local stand-in (editing/statements.ts), and this panel says so.
import { Button, Caption1, Field, Input, MessageBar, MessageBarBody, Select, Subtitle2, makeStyles, tokens } from "@fluentui/react-components";
import { useMemo, useState, type ReactElement } from "react";
import { allSteps, readProjection } from "../editing/doc";
import { declarations, type Decl } from "../editing/statements";
import type { EditorProps } from "./host";
import { useParts } from "./parts";
import { kindWord } from "./rows";

const useStyles = makeStyles({
  root: { display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalS, overflow: "auto", minHeight: 0 },
  bar: { display: "flex", alignItems: "flex-end", columnGap: tokens.spacingHorizontalS, flexWrap: "wrap" },
  kind: { display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalXS, padding: tokens.spacingHorizontalS, borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}` },
});

export interface KindsProps extends EditorProps { /** open another document: the template of a kind */ onOpen(projection: string): void }

function Kind({ d, p }: { d: Decl; p: KindsProps }): ReactElement {
  const s = useStyles(), parts = useParts();
  const [place, setPlace] = useState(""), [name, setName] = useState("");
  // the places of the template that can be a parameter: an expression that stands alone in its slot, and every parameter of a step
  const open = useMemo(() => {
    const t = readProjection(p.cat, p.palette, d.template);
    if (!t) return [];
    const used = new Set(d.params.map((x) => `${x.step}|${x.slot}|${x.field}`));
    return allSteps(t).flatMap((x) => [
      ...x.slots.filter((sl) => sl.entries.length === 1 && sl.entries[0].expr !== null && sl.multiplicity !== "*" && sl.multiplicity !== "+").map((sl) => ({ key: `${x.id}|${sl.name}|`, label: `${kindWord(x.kind)} ${sl.name}: ${sl.entries[0].expr}` })),
      ...x.fields.map((f) => ({ key: `${x.id}||${f.name}`, label: `${kindWord(x.kind)} ${f.name}: ${f.value}` })),
    ]).filter((o) => !used.has(o.key));
  }, [p.cat, p.palette, d]);
  const add = () => {
    const [step, slot, field] = place.split("|");
    if (!step || !name.trim()) return;
    p.onGesture({ g: "param", decl: d.id, name: name.trim(), step, slot: slot || undefined, field: field || undefined });
    setPlace(""); setName("");
  };
  return (
    <div className={s.kind} role="group" aria-label={`statement kind ${d.name}`}>
      <div className={s.bar}>
        <span className={parts.mono}><span className={parts.keyword}>{d.name}</span>({d.params.map((x) => x.name).join(", ")})</span>
        <Button size="small" onClick={() => p.onOpen(d.template)}>Open template</Button>
      </div>
      <div className={s.bar}>
        <Field label="A place of the template" size="small"><Select size="small" value={place} onChange={(_, v) => setPlace(v.value)}><option value="">choose…</option>{open.map((o) => <option key={o.key} value={o.key}>{o.label}</option>)}</Select></Field>
        <Field label="as the parameter" size="small"><Input size="small" value={name} onChange={(_, v) => setName(v.value)} onKeyDown={(e) => { if (e.key === "Enter") add(); }} /></Field>
        <Button size="small" disabled={!place || !name.trim() || p.busy} onClick={add}>Add parameter</Button>
      </div>
    </div>
  );
}

export function StatementKinds(p: KindsProps): ReactElement {
  const s = useStyles();
  const [from, setFrom] = useState(""), [name, setName] = useState("");
  const kinds = useMemo(() => declarations(p.cat), [p.cat]);
  const uses = allSteps(p.doc).filter((x) => kinds.some((k) => k.name === x.kind));
  const make = () => { if (from && name.trim()) { p.onGesture({ g: "declare", statement: from, name: name.trim() }); setFrom(""); setName(""); } };
  return (
    <section className={s.root} aria-label="Statement kinds">
      <Subtitle2 as="h3">Statement kinds</Subtitle2>
      <MessageBar intent="warning"><MessageBarBody>Kinds made here are kept in this browser only. The engine has no statement declarations yet: it receives the operators a use stands for.</MessageBarBody></MessageBar>
      <div className={s.bar}>
        <Field label="Make a kind from the row set" size="small"><Select size="small" value={from} onChange={(_, v) => setFrom(v.value)}><option value="">choose…</option>{p.doc.statements.filter((st) => !st.sink).map((st) => <option key={st.name} value={st.name}>{st.name}</option>)}</Select></Field>
        <Field label="named" size="small"><Input size="small" value={name} onChange={(_, v) => setName(v.value)} onKeyDown={(e) => { if (e.key === "Enter") make(); }} /></Field>
        <Button size="small" appearance="primary" disabled={!from || !name.trim() || p.busy} onClick={make}>Make kind</Button>
      </div>
      {kinds.map((d) => <Kind key={d.id} d={d} p={p} />)}
      {uses.length > 0 && <div className={s.bar}><Caption1>Uses in {p.doc.name}:</Caption1>{uses.map((u) => <Button key={u.id} size="small" disabled={p.busy} onClick={() => p.onGesture({ g: "expand", id: u.id })}>Expand {u.name}</Button>)}</div>}
    </section>
  );
}
