// A document graph (engine 3.4) as a page: the Markdown the engine renders at the revision the timeline shows, in Fluent components.
// A revision is immutable, so each is fetched once; the head moves with the event stream (the graph version changes, the view asks again).
import { Badge, MessageBar, MessageBarBody, Spinner, Subtitle1, Subtitle2, Body1Strong, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, Title1, Title2, Title3, makeStyles, tokens } from "@fluentui/react-components";
import { useEffect, useState } from "react";
import { parseDocument, type DocBlock } from "../core/markdown";
import type { DocumentText } from "../source/types";
import { useWorkspace } from "./common";

const useStyles = makeStyles({
  root: { overflow: "auto", minHeight: 0, padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalXL}`, display: "grid", alignContent: "start", rowGap: tokens.spacingVerticalM },
  page: { display: "grid", rowGap: tokens.spacingVerticalM, maxWidth: "960px" },
  list: { margin: 0, paddingLeft: tokens.spacingHorizontalXL, display: "grid", rowGap: tokens.spacingVerticalXS },
  meta: { display: "flex", alignItems: "center", columnGap: tokens.spacingHorizontalS, flexWrap: "wrap" },
});

const HEADINGS = [Title1, Title2, Title3, Subtitle1, Subtitle2, Body1Strong] as const;

function Block({ b }: { b: DocBlock }) {
  const s = useStyles();
  switch (b.kind) {
    case "heading": { const H = HEADINGS[Math.min(b.level, 6) - 1]; return <H as={`h${Math.min(b.level, 6)}` as "h1"}>{b.text}</H>; }
    case "paragraph": return <Text as="p">{b.text}</Text>;
    case "list": return <ul className={s.list}>{b.items.map((t, i) => <li key={i}><Text>{t}</Text></li>)}</ul>;
    case "table": {
      // the engine always writes four columns; a column that is empty in the header and every row is not shown
      const used = b.header.map((h, c) => !!h || b.rows.some((r) => !!r[c]));
      return (
        <Table aria-label={b.header.filter(Boolean).join(", ")} size="small">
          <TableHeader><TableRow>{b.header.map((h, c) => used[c] && <TableHeaderCell key={c}>{h}</TableHeaderCell>)}</TableRow></TableHeader>
          <TableBody>{b.rows.map((r, i) => <TableRow key={i}>{r.map((v, c) => used[c] && <TableCell key={c}>{v}</TableCell>)}</TableRow>)}</TableBody>
        </Table>
      );
    }
  }
}

export function DocumentView({ graph }: { graph: string }) {
  const s = useStyles();
  const ws = useWorkspace(graph);
  const hist = ws?.histories.get(graph);
  const revision = hist ? hist.revisionAt(hist.cursor) : undefined;
  const [doc, setDoc] = useState<DocumentText | null>(null);
  const [error, setError] = useState("");
  const source = ws?.source;
  useEffect(() => {
    if (!source?.document || revision === undefined) return;
    let current = true;
    source.document(graph, revision).then(
      (d) => { if (current) { setDoc(d); setError(d ? "" : `${graph} is not a document`); } },
      (e: Error) => { if (current) setError(e.message); },
    );
    return () => { current = false; };
  }, [source, graph, revision]);
  if (!source?.document) return <div className={s.root}><MessageBar intent="info"><MessageBarBody>This source does not render documents.</MessageBarBody></MessageBar></div>;
  const blocks = doc ? parseDocument(doc.markdown) : [];
  const shown = doc?.revision === revision;
  return (
    <section className={s.root} aria-label={`Document ${graph}`} aria-busy={!shown && !error}>
      <div className={s.meta}>
        <Badge appearance="outline">revision {revision}</Badge>
        {doc && <Badge appearance="outline" title={doc.digest}>{doc.digest.slice(0, 19)}…</Badge>}
        {!shown && !error && <Spinner size="tiny" label="Rendering" />}
      </div>
      {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
      {doc && !error && (blocks.length ? <article className={s.page}>{blocks.map((b, i) => <Block key={i} b={b} />)}</article> : <Text>The document is empty at revision {revision}.</Text>)}
    </section>
  );
}
