// The text editor of a projection's KQL (note 11, stage a): CodeMirror 6 with errors, completion and hover from the engine. The text is the
// projection's `.kql` file as the space or the draft holds it; changes are checked as the author types and are not stored in the draft yet.
import { autocompletion, type CompletionContext, type CompletionResult } from "@codemirror/autocomplete";
import { defaultKeymap, history, historyKeymap } from "@codemirror/commands";
import { HighlightStyle, syntaxHighlighting } from "@codemirror/language";
import { linter, lintGutter, setDiagnostics, type Diagnostic } from "@codemirror/lint";
import { EditorState, type Extension } from "@codemirror/state";
import { EditorView, hoverTooltip, keymap, lineNumbers, type Tooltip } from "@codemirror/view";
import { Button, MessageBar, MessageBarBody, Spinner, Tab, TabList, Text, makeStyles, tokens } from "@fluentui/react-components";
import { tags } from "@lezer/highlight";
import { useEffect, useRef, useState } from "react";
import { completionOf, diagnosticsOf, digestOf, hoverLines, kqlLanguage, type Analysis, type Completion, type Depth, type Hover, type Place } from "../../editor/kql";
import { add, cardsOf, listsOf, type Change } from "../../editor/kql-edit";
import { matchOf } from "../../editor/kql-example";
import type { Graph } from "../../core/graph";
import { KqlOutline } from "./KqlOutline";
import { KqlSteps, type Reading } from "./KqlSteps";
import { KqlWires } from "./KqlWires";

/** What the editor asks of the engine: the KQL calls of the editor service. */
export interface KqlPort {
  kqlAnalyse(projection: string, text: string, depth: Depth): Promise<Analysis>;
  kqlComplete(projection: string, text: string, offset: number): Promise<Completion>;
  kqlHover(projection: string, text: string, offset: number): Promise<Hover>;
}

/** An answer counts only for the text it read: one whose digest is not the digest of the text sent is dropped. */
const current = async <T extends { digest: string }>(text: string, answer: Promise<T>): Promise<T | null> => {
  const [a, d] = await Promise.all([answer, digestOf(text)]);
  return a.digest === d ? a : null;
};

/** The three things the editor asks the engine, as CodeMirror sources. `onAnalysis` hears every analysis that counts (fast and full). */
export function kqlSources(port: KqlPort, projection: string, onAnalysis: (a: Analysis, text: string) => void = () => {}) {
  return {
    async lint(text: string, depth: Depth = "fast"): Promise<Diagnostic[]> {
      const a = await current(text, port.kqlAnalyse(projection, text, depth));
      if (!a) return [];
      onAnalysis(a, text);
      return diagnosticsOf(a);
    },
    async complete(ctx: CompletionContext): Promise<CompletionResult | null> {
      const text = ctx.state.doc.toString();
      const c = await current(text, port.kqlComplete(projection, text, ctx.pos));
      return c && c.items.length ? completionOf(c) : null;
    },
    async hover(text: string, pos: number): Promise<{ from: number; to: number; lines: string[] } | null> {
      const h = await current(text, port.kqlHover(projection, text, pos));
      const lines = h ? hoverLines(h) : [];
      return h && lines.length ? { from: h.place?.[0] ?? pos, to: h.place?.[1] ?? pos, lines } : null;
    },
  };
}

const highlight = HighlightStyle.define([
  { tag: tags.keyword, fontWeight: "600" },
  { tag: tags.macroName, fontWeight: "700", textDecoration: "underline", textUnderlineOffset: "3px" },
  { tag: tags.string, color: "var(--colorNeutralForeground2)" },
  { tag: tags.comment, color: "var(--colorNeutralForeground3)", fontStyle: "italic" },
  { tag: tags.operator, color: "var(--colorNeutralForeground3)" },
]);
const sheet = EditorView.theme({
  "&": { height: "100%", backgroundColor: "var(--colorNeutralBackground1)", color: "var(--colorNeutralForeground1)", fontSize: "13px" },
  ".cm-scroller": { fontFamily: "var(--fontFamilyMonospace)", lineHeight: "1.55" },
  ".cm-content": { caretColor: "var(--colorBrandForeground1)" },
  "&.cm-focused": { outline: "2px solid var(--colorStrokeFocus2)", outlineOffset: "-2px" },
  "&.cm-focused .cm-selectionBackground, .cm-selectionBackground, ::selection": { backgroundColor: "var(--colorBrandBackground2)" },
  ".cm-gutters": { backgroundColor: "var(--colorNeutralBackground2)", color: "var(--colorNeutralForeground3)", borderRight: "1px solid var(--colorNeutralStroke2)" },
  ".cm-activeLine, .cm-activeLineGutter": { backgroundColor: "transparent" },
  ".cm-tooltip": { backgroundColor: "var(--colorNeutralBackground1)", color: "var(--colorNeutralForeground1)", border: "1px solid var(--colorNeutralForeground1)", borderRadius: "2px" },
  ".cm-tooltip-hover": { padding: "4px 8px", maxWidth: "60ch", fontSize: "12px" },
  ".cm-tooltip-hover .kql-kusto": { fontFamily: "var(--fontFamilyMonospace)" },
  ".cm-tooltip-autocomplete ul li[aria-selected]": { backgroundColor: "var(--colorBrandBackground2)", color: "var(--colorNeutralForeground1)" },
});

/** The editor's extensions: highlighting, undo, errors (fast after a pause, full once idle), completion and hover from the engine. */
export function kqlExtensions(port: KqlPort, projection: string, onAnalysis?: (a: Analysis, text: string) => void): Extension[] {
  const src = kqlSources(port, projection, onAnalysis);
  let idle: ReturnType<typeof setTimeout> | undefined;
  return [
    lineNumbers(), history(), keymap.of([...defaultKeymap, ...historyKeymap]), kqlLanguage, syntaxHighlighting(highlight), sheet,
    // the content is editable and so focusable; the explicit tab stop tells checkers that the scrolling region holds a focusable element
    EditorView.contentAttributes.of({ "aria-label": `KQL text of ${projection}`, tabindex: "0" }),
    lintGutter(),
    linter(async (view) => {
      const text = view.state.doc.toString();
      const out = await src.lint(text, "fast");
      // the full analysis (the engine's own checker and column types, 1 to 2 s) once the author stops typing; dropped if the text changed
      clearTimeout(idle);
      idle = setTimeout(async () => {
        if (view.state.doc.toString() !== text) return;
        const full = await src.lint(text, "full");
        if (view.state.doc.toString() === text) view.dispatch(setDiagnostics(view.state, full));
      }, 1500);
      return out;
    }, { delay: 500 }),
    autocompletion({ override: [(ctx) => src.complete(ctx)] }),
    hoverTooltip(async (view, pos): Promise<Tooltip | null> => {
      const h = await src.hover(view.state.doc.toString(), pos);
      if (!h) return null;
      return {
        pos: h.from, end: h.to, above: true,
        create: () => {
          const dom = document.createElement("div");
          h.lines.forEach((l, i) => { const line = document.createElement("div"); line.textContent = l; if (i === 0) line.className = "kql-kusto"; dom.appendChild(line); });
          return { dom };
        },
      };
    }),
  ];
}

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateRows: "auto minmax(0, 1fr)", height: "100%", minHeight: 0, backgroundColor: tokens.colorNeutralBackground3, padding: tokens.spacingHorizontalM, boxSizing: "border-box", rowGap: tokens.spacingVerticalS },
  head: { display: "flex", alignItems: "center", columnGap: tokens.spacingHorizontalM, flexWrap: "wrap" },
  file: { fontFamily: tokens.fontFamilyMonospace },
  grow: { flexGrow: 1 },
  body: { display: "grid", gridTemplateColumns: "minmax(0, 1.3fr) minmax(300px, 1fr)", columnGap: tokens.spacingHorizontalM, minHeight: 0, "@media (max-width: 1000px)": { gridTemplateColumns: "minmax(0, 1fr)", gridTemplateRows: "minmax(50vh, 1fr) auto" } },
  host: { minHeight: 0, border: `1.5px solid ${tokens.colorNeutralForeground1}`, backgroundColor: tokens.colorNeutralBackground1, overflow: "hidden" },
  side: { display: "grid", gridTemplateRows: "auto minmax(0, 1fr)", minHeight: 0, rowGap: tokens.spacingVerticalXS },
  pane: { display: "flex", flexDirection: "column", minHeight: 0 },
});

type Port = KqlPort & { kqlText(projection: string, draft?: string): Promise<{ text: string }> };

/**
 * The `.kql` file of a projection, editable, with the engine's answers, and Steps beside it. `port` also reads the text (in the draft when one
 * is given); `rows`, when given, previews the stored text and answers the rows after each engine operator.
 */
export default function KqlText({ port, projection, draft, rows, store, picked }: { port: Port; projection: string; draft?: string; rows?: () => Promise<{ op: string; rows: string }[]>;
  /** the element picked in a data graph (an edge brings its ends): by example, a `let` with the graph-match that finds its like */
  picked?: { graph: string; data: Graph; ids: string[] } | null;
  /** store the text in the draft; the engine's refusal, or null */
  store?: (text: string) => Promise<string | null> }) {
  const s = useStyles();
  const host = useRef<HTMLDivElement>(null);
  const view = useRef<EditorView | null>(null);
  const [state, setState] = useState<{ status: "loading" } | { status: "error"; message: string } | { status: "ready"; stored: string }>({ status: "loading" });
  const [reading, setReading] = useState<Reading | null>(null);
  const [doc, setDoc] = useState("");
  const [cursor, setCursor] = useState<number | null>(null);
  const [notation, setNotation] = useState<"steps" | "outline" | "wires">("steps");
  const [rowsBy, setRowsBy] = useState<{ status: "idle" | "busy" } | { status: "ready"; by: Map<string, string> } | { status: "error"; message: string }>({ status: "idle" });
  const [forms, setForms] = useState<string[] | undefined>(undefined);
  useEffect(() => {
    let gone = false;
    setState({ status: "loading" }); setReading(null); setRowsBy({ status: "idle" }); setForms(undefined);
    port.kqlText(projection, draft).then(({ text }) => {
      if (gone || !host.current) return;
      const follow = EditorView.updateListener.of((u) => { if (u.docChanged) setDoc(u.state.doc.toString()); if (u.selectionSet || u.docChanged) setCursor(u.state.selection.main.head); });
      view.current = new EditorView({ parent: host.current, state: EditorState.create({ doc: text, extensions: [...kqlExtensions(port, projection, (a, t) => setReading({ text: t, analysis: a })), follow] }) });
      setDoc(text);
      setState({ status: "ready", stored: text });
    }, (e: Error) => !gone && setState({ status: "error", message: e.message }));
    return () => { gone = true; view.current?.destroy(); view.current = null; };
  }, [port, projection, draft]);
  // the forms a new statement starts with: what the engine completes at the start of a statement after the last one
  const hasReading = !!reading;
  useEffect(() => {
    if (!hasReading || forms || !view.current) return;
    let gone = false;
    const before = view.current.state.doc.toString().trimEnd(), at = before.endsWith(";") ? `${before}\n` : `${before};\n`;
    port.kqlComplete(projection, at, at.length).then((c) => { if (!gone) setForms(c.items.filter((i) => i.kind === "step").map((i) => i.insert ?? i.text)); }, () => {});
    return () => { gone = true; };
  }, [hasReading, forms, port, projection]);
  const problems = reading?.analysis.diagnostics.length ?? null;
  // a visual edit goes in like typing: one transaction on the text, so typing and Steps share one undo history
  const change = (changes: Change[], select: Place | null) => {
    const v = view.current;
    if (!v || !reading || reading.text !== v.state.doc.toString()) return; // computed on an older reading: dropped, the next reading comes
    v.dispatch({ changes: changes.map((c) => ({ from: c.from, to: c.to, insert: c.insert })), ...(select ? { selection: { anchor: select[0] } } : {}), scrollIntoView: true });
  };
  const select = (p: Place) => { const v = view.current; if (v) { v.dispatch({ selection: { anchor: p[0], head: p[0] }, scrollIntoView: true }); v.focus(); } };
  const askRows = () => {
    if (!rows || !reading) return;
    setRowsBy({ status: "busy" });
    rows().then((ops) => {
      const cards = cardsOf(reading.analysis), by = new Map<string, string>();
      for (const o of ops) { const c = cards.get(o.op); if (c) by.set(c, o.rows); }
      setRowsBy({ status: "ready", by });
    }, (e: Error) => setRowsBy({ status: "error", message: e.message }));
  };
  const stored = state.status === "ready" && doc === state.stored;
  // by example: the picked element of a graph this projection reads, as a new `let` after the last one
  const reads = new Set(((reading?.analysis.inputs as { name: string; kind: string }[] | undefined) ?? []).filter((i) => i.kind === "graph").map((i) => i.name));
  const example = picked && reads.has(picked.graph) ? matchOf(picked.data, picked.ids, picked.graph) : null;
  const addExample = () => {
    if (!reading || !example) return;
    const lists = listsOf(reading.analysis), statements = lists.find((l) => l.id === "statements");
    const stmts = (reading.analysis.statements as { kind: string; name?: string | null; place: Place }[] | undefined) ?? [];
    if (!statements) return;
    const names = new Set(stmts.map((x) => x.name).filter(Boolean));
    let name = "example";
    for (let n = 2; names.has(name); n++) name = `example${n}`;
    const lastLet = [...stmts].reverse().find((x) => x.kind === "let");
    const k = lastLet ? statements.items.findIndex((p) => p[0] === lastLet.place[0]) + 1 : statements.items.length;
    const e = add(reading.text, statements, k, `let ${name} = ${example}`, ";\n");
    change(e.changes, e.select);
  };
  const [storing, setStoring] = useState<{ busy: boolean; error: string | null }>({ busy: false, error: null });
  const keep = () => {
    if (!store || state.status !== "ready") return;
    const text = doc;
    setStoring({ busy: true, error: null });
    store(text).then((error) => { setStoring({ busy: false, error }); if (!error) { setState({ status: "ready", stored: text }); setRowsBy({ status: "idle" }); } });
  };
  return (
    <div className={s.root}>
      <div className={s.head}>
        <Text className={s.file}>{projection}.kql</Text>
        <Text size={200} role="status">{problems === null ? "" : problems ? `${problems} problem${problems === 1 ? "" : "s"} found by the engine` : "the engine reads the text without problems"}</Text>
        <Text size={200}>{store ? (stored ? "The draft holds this text." : "Changed here, not yet in the draft.") : "Changes are checked as you type; they are not stored in the draft."}</Text>
        <span className={s.grow} />
        {picked && <Button size="small" disabled={!example} onClick={addExample} title={example ? undefined : `pick an element of a graph this projection reads (${[...reads].join(", ")})`}>Add a let from the picked element</Button>}
        {store && <Button size="small" appearance="primary" disabled={stored || storing.busy} onClick={keep}>{storing.busy ? "Storing…" : "Store in draft"}</Button>}
        {storing.error && <Text size={200} role="alert">{storing.error}</Text>}
        {rows && <Button size="small" disabled={!stored || rowsBy.status === "busy"} onClick={askRows}>{rowsBy.status === "busy" ? "Counting rows…" : "Show rows"}</Button>}
        {rowsBy.status === "error" && <Text size={200}>{rowsBy.message}</Text>}
      </div>
      {state.status === "loading" && <Spinner size="small" label="Reading the projection's text…" />}
      {state.status === "error" && <MessageBar intent="warning"><MessageBarBody>{state.message}</MessageBarBody></MessageBar>}
      <div className={s.body} hidden={state.status !== "ready"}>
        <div ref={host} className={s.host} />
        <div className={s.side}>
          <TabList size="small" selectedValue={notation} onTabSelect={(_, d) => setNotation(d.value as typeof notation)} aria-label="Notation beside the text">
            <Tab value="steps">Steps</Tab><Tab value="outline">Outline</Tab><Tab value="wires">Wires</Tab>
          </TabList>
          <div className={s.pane} role="tabpanel" aria-label={notation}>
            {notation === "steps" && <KqlSteps reading={reading} forms={forms} selected={cursor} busy={!reading || reading.text !== doc} rows={rowsBy.status === "ready" && stored ? rowsBy.by : null} onSelect={select} onChanges={change} />}
            {notation === "outline" && <KqlOutline reading={reading} forms={forms} selected={cursor} busy={!reading || reading.text !== doc} onSelect={select} onChanges={change} />}
            {notation === "wires" && <KqlWires reading={reading} forms={forms} selected={cursor} busy={!reading || reading.text !== doc} onSelect={select} onChanges={change} />}
          </div>
        </div>
      </div>
    </div>
  );
}
