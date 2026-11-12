// A row set from an example: the author picks elements of the data a projection reads, and the editor shows the pattern they are an
// example of, one line per element. The example grows along its relationships (the list of what is next to the picked elements),
// and a property becomes a condition only when the author asks for it. Nothing is written until the pattern is added as a row set.
import { Button, Caption1, Checkbox, Field, Input, MessageBar, MessageBarBody, Select, Subtitle2, Text, makeStyles, tokens } from "@fluentui/react-components";
import { Dismiss16Regular } from "@fluentui/react-icons";
import { useMemo, useState, type ReactElement } from "react";
import type { Graph } from "../core/graph";
import { around, conditions, edgeText, generalise, matchGesture, nodeText } from "../editing/example";
import { freeName } from "../editing/scope";
import type { EditorProps } from "./host";
import { useParts } from "./parts";

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateColumns: "minmax(0, 1fr) minmax(0, 1fr)", columnGap: tokens.spacingHorizontalL, height: "100%", minHeight: 0 },
  pane: { overflow: "auto", minHeight: 0, display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalS, paddingRight: tokens.spacingHorizontalS },
  list: { display: "flex", flexDirection: "column", margin: 0, padding: 0, listStyleType: "none" },
  row: { display: "flex", alignItems: "center", columnGap: tokens.spacingHorizontalS, minHeight: "28px" },
  grow: { flexGrow: 1 },
  bar: { display: "flex", alignItems: "flex-end", columnGap: tokens.spacingHorizontalS, flexWrap: "wrap" },
});

export interface ExampleProps extends EditorProps {
  /** the data graphs the projection reads, by the id of the graph input they are bound to */
  examples: Readonly<Record<string, Graph>>;
  /** elements the author picked elsewhere (on the data canvas) */
  picked?: readonly string[];
}
/** Rows of the list, and how many matches are looked at to choose them: the list is for finding a first example, not for reading a graph. */
const SHOWN = 50, SCANNED = 2000;
const order = new Intl.Collator("en", { numeric: true });

export function ExampleEditor(p: ExampleProps): ReactElement {
  const s = useStyles(), parts = useParts();
  const inputs = p.doc.inputs.filter((i) => i.kind === "graph" && p.examples[i.id]);
  const [inputId, setInputId] = useState("");
  const input = inputs.find((i) => i.id === inputId) ?? inputs[0];
  const data = input && p.examples[input.id];
  const [picked, setPicked] = useState<readonly string[]>(p.picked ?? []);
  const [query, setQuery] = useState("");
  const [where, setWhere] = useState<readonly string[]>([]);
  const [name, setName] = useState("");
  const pattern = useMemo(() => (data && input ? generalise(p.cat, input.id, data, picked) : null), [p.cat, input, data, picked]);
  const next = useMemo(() => (data && picked.length ? around(data, picked) : []), [data, picked]);
  const found = useMemo(() => {
    if (!data) return { rows: [], more: 0 };
    const q = query.trim().toLowerCase(), has = new Set(picked), hits: { text: string; id: string; type: string; label: string }[] = [];
    let total = 0;
    for (const e of data.entities()) {
      const label = data.label(e), text = `${e.type} ${label}`;
      if (has.has(e.id) || (q && !text.toLowerCase().includes(q))) continue;
      total++;
      if (hits.length < SCANNED) hits.push({ text, id: e.id, type: e.type, label });
    }
    const rows = hits.sort((a, b) => order.compare(a.text, b.text)).slice(0, SHOWN);
    return { rows, more: total - rows.length };
  }, [data, query, picked]);

  if (!data || !input || !pattern) return <MessageBar intent="info"><MessageBarBody>No data is open for a graph this projection reads, so there is nothing to pick an example from.</MessageBarBody></MessageBar>;
  const pick = (...ids: string[]) => setPicked((cur) => [...cur, ...ids.filter((id) => !cur.includes(id))]);
  const unpick = (...ids: string[]) => { setPicked((cur) => cur.filter((id) => !ids.includes(id))); setWhere([]); };
  const offered = conditions(pattern).filter((c) => !where.includes(c.text));
  const statement = name || freeName(p.doc, false);
  const gesture = matchGesture(pattern, input.id, statement, where);
  const add = () => { if (!gesture) return; p.onGesture(gesture); setPicked([]); setWhere([]); setName(""); };
  // an entity that a picked relationship brought in is taken out by taking the relationship out
  const held = (id: string) => pattern.edges.filter((e) => e.example.source === id || e.example.target === id).map((e) => e.example.id);

  return (
    <div className={s.root}>
      <section className={s.pane} aria-label="Data">
        <div className={s.bar}>
          {inputs.length > 1 && <Field label="Graph" size="small"><Select size="small" value={input.id} onChange={(_, d) => { setInputId(d.value); setPicked([]); setWhere([]); }}>{inputs.map((i) => <option key={i.id} value={i.id}>{i.name}</option>)}</Select></Field>}
          <Field label={`Find in ${input.name}`} size="small" className={s.grow}><Input size="small" value={query} onChange={(_, d) => setQuery(d.value)} placeholder="type or name" /></Field>
        </div>
        {next.length > 0 && <>
          <Subtitle2 as="h3">Next to the example</Subtitle2>
          <ul className={s.list} aria-label="Next to the example">
            {next.map(({ rel, other, outward }) => (
              <li key={rel.id} className={s.row}>
                <Checkbox checked={false} onChange={() => pick(rel.id)} label={<span className={parts.mono}>{outward ? `-[:${rel.type}]->` : `<-[:${rel.type}]-`} {other.type} {data.label(other)}</span>} />
              </li>
            ))}
          </ul>
        </>}
        <Subtitle2 as="h3">{query ? "Found" : `In ${input.name}`}</Subtitle2>
        <ul className={s.list} aria-label="Entities">
          {found.rows.map((e) => <li key={e.id} className={s.row}><Checkbox checked={false} onChange={() => pick(e.id)} label={<span className={parts.mono}><span className={parts.plain}>{e.type}</span>{" "}{e.label}</span>} /></li>)}
        </ul>
        {found.more > 0 && <Caption1>and {found.more} more: type to narrow the list</Caption1>}
      </section>
      <section className={s.pane} aria-label="Pattern">
        <Subtitle2 as="h3">The pattern of this example</Subtitle2>
        {!pattern.nodes.length && <Text>Pick an entity on the left. Each pick becomes one line here.</Text>}
        <ul className={s.list} aria-label="Pattern elements">
          {pattern.nodes.map((n) => (
            <li key={n.example.id} className={s.row}>
              <span className={parts.mono}><span className={parts.keyword}>{nodeText(n)}</span></span><Caption1>like {data.label(n.example)}</Caption1><span className={s.grow} />
              <Button size="small" appearance="subtle" icon={<Dismiss16Regular />} aria-label={`remove ${nodeText(n)}`} onClick={() => unpick(n.example.id, ...held(n.example.id))} />
            </li>
          ))}
          {pattern.edges.map((e) => (
            <li key={e.example.id} className={s.row}>
              <span className={parts.mono}>{edgeText(e)}</span><span className={s.grow} />
              <Button size="small" appearance="subtle" icon={<Dismiss16Regular />} aria-label={`remove ${edgeText(e)}`} onClick={() => unpick(e.example.id)} />
            </li>
          ))}
          {where.map((w) => (
            <li key={w} className={s.row}>
              <span className={parts.mono}><span className={parts.plain}>where </span>{w}</span><span className={s.grow} />
              <Button size="small" appearance="subtle" icon={<Dismiss16Regular />} aria-label={`remove where ${w}`} onClick={() => setWhere((cur) => cur.filter((x) => x !== w))} />
            </li>
          ))}
        </ul>
        {pattern.unknown.length > 0 && <MessageBar intent="warning"><MessageBarBody>The ontology of {input.name} does not declare {pattern.unknown.join(", ")}, so this example cannot be a pattern.</MessageBarBody></MessageBar>}
        {pattern.nodes.length > 0 && (
          <div className={s.bar}>
            <Field label="Keep only rows where" size="small">
              <Select size="small" value="" disabled={!offered.length} onChange={(_, d) => { if (d.value) setWhere((cur) => [...cur, d.value]); }}>
                <option value="">{offered.length ? "a value of the example…" : "nothing more to add"}</option>
                {offered.map((c) => <option key={c.text} value={c.text}>{c.text}</option>)}
              </Select>
            </Field>
            <Field label="Name of the row set" size="small"><Input size="small" value={name} placeholder={statement} onChange={(_, d) => setName(d.value)} onKeyDown={(e) => { if (e.key === "Enter") add(); }} /></Field>
            <Button size="small" appearance="primary" disabled={!gesture || p.busy} onClick={add}>Add as row set</Button>
          </div>
        )}
      </section>
    </div>
  );
}
