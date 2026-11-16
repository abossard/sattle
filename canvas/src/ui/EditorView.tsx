// The projection editor: the projection's KQL text with its notations, and the draft's problems, module texts, definitions, versions and
// previews around one draft on the engine.
import {
  Badge, Breadcrumb, BreadcrumbButton, BreadcrumbDivider, BreadcrumbItem, Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, MessageBar, MessageBarActions, MessageBarBody, Select, Spinner, Tab, TabList, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, Textarea, Toolbar, ToolbarButton, ToolbarDivider, makeStyles, tokens,
} from "@fluentui/react-components";
import { Checkmark12Regular, ArrowRedo20Regular, ArrowUndo20Regular, Eye20Regular, Keyboard20Regular, Save20Regular } from "@fluentui/react-icons";
import { Suspense, lazy, useEffect, useEffectEvent, useMemo, useRef, useState } from "react";
import { DataPane } from "../app/data-pane";
import { History } from "../app/history";
import { Graph } from "../core/graph";
import { moduleOfId } from "../core/catalog";
import { stackOf } from "../editor/stack";
import type { IdentityEffect, Release } from "../editor/service";
import { rendererTheme } from "../state/effects";
import { storeKqlText } from "../state/editor-effects";
import { runtime } from "../state/runtime";
import { dispatch, useStore } from "../state/store";
import type { Command, EditorState } from "../state/types";
import { WORK_LABEL } from "../state/editor-phase";
import { webgl2Available } from "./CanvasHost";
import { DirectionBadge } from "./common";
import { Definitions } from "./editor/Definitions";
import { NewProjectionDialog } from "./editor/NewProjection";

// the KQL text editor brings CodeMirror: it loads when its tab is first opened
const KqlText = lazy(() => import("./editor/KqlText"));

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateColumns: "minmax(0, 1fr)", minHeight: 0, height: "100%" },
  center: { display: "grid", gridTemplateColumns: "minmax(0, 1fr)", gridTemplateRows: "auto auto auto minmax(0, 1fr) minmax(140px, 28%)", minHeight: 0, minWidth: 0, overflow: "hidden" },
  tools: { display: "flex", alignItems: "center", flexWrap: "wrap", minWidth: 0, whiteSpace: "nowrap", borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, "& .fui-Toolbar > *": { flexShrink: 0 } },
  // the last 32px fade out, so a cut label reads as "scroll for more"
  scroll: { overflowX: "auto", minWidth: 0, flex: "1 1 240px", maskImage: "linear-gradient(to right, #000 calc(100% - 32px), transparent)" },
  fixed: { flex: "0 0 auto", borderLeft: `1px solid ${tokens.colorNeutralStroke2}`, "@media (max-width: 1000px)": { flexBasis: "100%", borderLeft: "none", borderTop: `1px solid ${tokens.colorNeutralStroke2}`, justifyContent: "flex-end" } },
  bar: { display: "flex", alignItems: "center", columnGap: tokens.spacingHorizontalS, flexWrap: "wrap", padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`, "@media (min-width: 1001px)": { flexWrap: "nowrap", "& > .fui-Badge, & > .fui-TabList": { flexShrink: 0 } } },
  pad: { padding: tokens.spacingHorizontalM, display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalS },
  bottom: { minHeight: 0, overflow: "auto", borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
  grow: { flexGrow: 1 },
  mono: { fontFamily: tokens.fontFamilyMonospace, width: "100%" },
  pick: { display: "grid", gridTemplateColumns: "auto minmax(0, 1fr)", alignItems: "center", columnGap: tokens.spacingHorizontalS, width: "min(360px, 38%)", minWidth: "160px", flex: "0 1 auto", "& .fui-Select": { minWidth: 0 }, "@media (max-width: 1000px)": { width: "100%" } },
});

const store = (key: string) => { try { return JSON.parse(localStorage.getItem(key) ?? "null"); } catch { return null; } };
const save = (key: string, v: unknown) => { try { localStorage.setItem(key, JSON.stringify(v)); } catch { /* storage unavailable */ } };
const run = (cmd: Command) => dispatch({ type: "editor/do", cmd });
const short = (d: string) => d.replace("sha256:", "").slice(0, 10);
const NO_TEXTS: Readonly<Record<string, string>> = {};
const working = (ed: EditorState) => (ed.phase.t === "ready" ? ed.phase.work : null);



// ---- text ----
function TextPanel() {
  const s = useStyles();
  const ed = useStore((x) => x.editor);
  // the session's texts are a new object whenever one changed (the store's textVersion moves with them and renders this again)
  const texts = runtime.session?.texts ?? NO_TEXTS;
  const mods = useMemo(() => Object.keys(texts).filter((m) => !m.startsWith("sattle.kernel")), [texts]);
  const [module, setModule] = useState(ed.module);
  const [text, setText] = useState("");
  const focused = useRef(false);
  useEffect(() => setModule(ed.module), [ed.module]);
  useEffect(() => { if (!focused.current) setText(texts[module] ?? ""); }, [texts, module]);
  // what the author typed is sent 700 ms after the last key; an answer that arrives meanwhile does not send it again
  const typedDiffers = useEffectEvent((typed: string) => typed !== (texts[module] ?? ""));
  useEffect(() => {
    if (!focused.current || !typedDiffers(text)) return;
    const t = setTimeout(() => run({ t: "text", module, text }), 700);
    return () => clearTimeout(t);
  }, [text, module]);
  return (
    <div className={s.pad}>
      <div className={s.bar}>
        <Field label="Module" orientation="horizontal"><Select value={module} onChange={(_, d) => { setModule(d.value); setText(texts[d.value] ?? ""); }}>{mods.map((m) => <option key={m}>{m}</option>)}</Select></Field>
        <Text size={200}>The text is the draft printed canonically. Edits are checked by the engine and become one undo step.</Text>
      </div>
      <Textarea className={s.mono} resize="vertical" rows={12} aria-label={`Text of module ${module}`} value={text} onFocus={() => { focused.current = true; }} onBlur={() => { focused.current = false; if (text !== (texts[module] ?? "")) run({ t: "text", module, text }); /* the pending edit is sent now, not lost with the timer */ else setText(texts[module] ?? text); }} onChange={(_, d) => setText(d.value)} textarea={{ style: { fontFamily: tokens.fontFamilyMonospace, whiteSpace: "pre" } }} />
    </div>
  );
}

function Problems() {
  const s = useStyles();
  const ed = useStore((x) => x.editor);
  if (!ed.diagnostics.length) return <div className={s.pad}><Badge color="informative" appearance="outline" shape="square" icon={<Checkmark12Regular />}>no problems</Badge><Text>The engine accepts the draft as it is.</Text></div>;
  return (
    <Table size="small" aria-label="Problems in the draft">
      <TableHeader><TableRow><TableHeaderCell>Severity</TableHeaderCell><TableHeaderCell>Problem</TableHeaderCell><TableHeaderCell>Where</TableHeaderCell></TableRow></TableHeader>
      <TableBody>{ed.diagnostics.map((d, i) => (
        <TableRow key={i}>
          <TableCell>{d.severity === "error" ? "✖ error" : "⚠ warning"}</TableCell><TableCell>{d.code}: {d.message}</TableCell><TableCell><Text size={200}>{d.focus.replace(/^d:/, "")}{d.line ? ` · line ${d.line}` : ""}</Text></TableCell>
        </TableRow>
      ))}</TableBody>
    </Table>
  );
}

// ---- versions and previews ----
function PreviewCanvas({ graph }: { graph: Graph }) {
  const host = useRef<HTMLDivElement>(null);
  const theme = useStore((x) => x.theme);
  useEffect(() => {
    const ws = runtime.ws;
    if (!ws || !host.current || !webgl2Available()) return;
    const info = ws.graphInfo(ws.info.graphs.find((g) => g.role !== "catalog")!.id)!;
    const el = document.createElement("div");
    host.current.replaceChildren(el);
    const pane = new DataPane({ ...info, id: "preview", inputs: [] }, new History(graph.toSnapshot()), el, rendererTheme(theme), { select: () => {}, hover: () => {}, activate: () => {}, changed: () => {}, announce: () => {}, key: () => false });
    pane.start();
    return () => pane.dispose();
  }, [graph, theme]);
  return webgl2Available() ? <div ref={host} style={{ height: 320 }} /> : <Text>WebGL2 is not available; the counts above describe the preview.</Text>;
}

function Projects() {
  const s = useStyles();
  const ed = useStore((x) => x.editor);
  const [pick, setPick] = useState<string[]>([]);
  const [shown, setShown] = useState<{ title: string; text: string } | null>(null);
  const [previewGraph, setPreviewGraph] = useState<string>("");
  const session = runtime.session;
  const mine = ed.releases.filter((r: Release) => r.module === ed.module);
  const pv = ed.preview, full = runtime.preview;
  const snap = full?.graphs.find((g) => g.id === previewGraph)?.snapshot;
  return (
    <div className={s.pad}>
      <div className={s.bar}>
        <Text weight="semibold" as="h2">Versions of {ed.module}</Text><span className={s.grow} />
        <Button size="small" disabled={pick.length !== 2} onClick={() => dispatch({ type: "editor/diffRelease", from: pick[0], to: pick[1] })}>Compare two versions</Button>
      </div>
      <Table size="small" aria-label="Releases of the module">
        <TableHeader><TableRow><TableHeaderCell>Pick</TableHeaderCell><TableHeaderCell>Version</TableHeaderCell><TableHeaderCell>Digest</TableHeaderCell><TableHeaderCell>Catalog revision</TableHeaderCell><TableHeaderCell>Actions</TableHeaderCell></TableRow></TableHeader>
        <TableBody>{mine.map((r) => (
          <TableRow key={r.digest}>
            <TableCell><input type="checkbox" aria-label={`Pick version ${r.version} for comparison`} checked={pick.includes(r.digest)} onChange={(e) => setPick(e.target.checked ? [...pick, r.digest].slice(-2) : pick.filter((x) => x !== r.digest))} /></TableCell>
            <TableCell>{r.version}</TableCell><TableCell><code>{short(r.digest)}</code></TableCell><TableCell>{r.catalogRevision}</TableCell>
            <TableCell>
              <Button size="small" onClick={() => void session?.svc.releaseText(r.digest).then((text) => setShown({ title: `${r.module} ${r.version}`, text }))}>Open</Button>{" "}
              <Button size="small" onClick={() => run({ t: "restore", digest: r.digest })}>Restore into draft</Button>
            </TableCell>
          </TableRow>
        ))}</TableBody>
      </Table>
      {ed.releaseDiff && (
        <MessageBar intent="info"><MessageBarBody>Between the versions: {ed.releaseDiff.added} added, {ed.releaseDiff.removed} removed, {ed.releaseDiff.changed} changed elements. {ed.releaseDiff.items.slice(0, 8).map((i) => `${i.kind === "added" ? "+" : i.kind === "removed" ? "−" : "Δ"} ${i.type} ${i.id.slice(i.id.lastIndexOf(":") + 1)}`).join("; ")}</MessageBarBody><MessageBarActions><Button size="small" onClick={() => dispatch({ type: "editor/dismiss", what: "releaseDiff" })}>Dismiss</Button></MessageBarActions></MessageBar>
      )}
      <div className={s.bar}>
        <Text weight="semibold" as="h2">Preview of the draft</Text><span className={s.grow} />
        <Button size="small" icon={working(ed) === "preview" ? <Spinner size="tiny" /> : <Eye20Regular />} disabled={working(ed) === "preview"} onClick={() => dispatch({ type: "editor/preview" })}>Preview output graphs</Button>
      </div>
      {pv && (
        <>
          <Text>Evaluated {pv.scope.rows} of {pv.scope.totalRows} input rows ({pv.scope.complete ? "complete: the diff against the live graphs is exact" : "partial: no diff against the live graphs"}) as of {pv.scope.logicalTime} in {pv.milliseconds} ms. The live engine was not touched.</Text>
          <Table size="small" aria-label="Output graphs of the preview">
            <TableHeader><TableRow><TableHeaderCell>Graph</TableHeaderCell><TableHeaderCell>Entities</TableHeaderCell><TableHeaderCell>Relationships</TableHeaderCell><TableHeaderCell>Against the live graph</TableHeaderCell><TableHeaderCell>Show</TableHeaderCell></TableRow></TableHeader>
            <TableBody>{pv.graphs.map((g) => (
              <TableRow key={g.id}><TableCell>{g.id}{g.removed ? " (removed)" : ""}</TableCell><TableCell>{g.entities}</TableCell><TableCell>{g.relationships}</TableCell><TableCell>{pv.scope.complete ? `+${g.added} −${g.removed_} Δ${g.changed}` : "n/a"}</TableCell>
                <TableCell>{g.hasSnapshot && <Button size="small" onClick={() => setPreviewGraph(g.id)}>Show graph</Button>}{g.truncated && " (truncated)"}</TableCell></TableRow>
            ))}</TableBody>
          </Table>
          {pv.identity && <IdentityEffectTable identity={pv.identity} />}
          {pv.diagnostics.length > 0 && <MessageBar intent="warning"><MessageBarBody>{pv.diagnostics.map((d) => d.message).join("; ")}</MessageBarBody></MessageBar>}
          {snap && <PreviewCanvas graph={Graph.fromSnapshot(snap)} />}
        </>
      )}
      <Dialog open={!!shown} onOpenChange={(_, d) => { if (!d.open) setShown(null); }}>
        <DialogSurface><DialogBody><DialogTitle>{shown?.title}</DialogTitle><DialogContent><Textarea readOnly rows={20} value={shown?.text ?? ""} aria-label="Release text" textarea={{ style: { fontFamily: tokens.fontFamilyMonospace, whiteSpace: "pre", width: "100%" } }} /></DialogContent><DialogActions><Button appearance="primary" onClick={() => setShown(null)}>Close</Button></DialogActions></DialogBody></DialogSurface>
      </Dialog>
    </div>
  );
}

/** Identity resolution in the previewed space against the live one: a Link has no element in an output graph, its effect is here. */
const IDENTITY_ROWS = [["minted", "New identities"], ["aliased", "Keys bound to an identity by a Link"], ["merged", "Identities joined by a Link or late evidence"], ["conflicts", "Links refused (different entity types)"]] as const;
const signed = (n: string) => (Number(n) > 0 ? `+${n}` : n);
export function IdentityEffectTable({ identity }: { identity: IdentityEffect }) {
  return (
    <Table size="small" aria-label="Identity effect of the preview">
      <TableHeader><TableRow><TableHeaderCell>Identity</TableHeaderCell><TableHeaderCell>Preview</TableHeaderCell><TableHeaderCell>Live</TableHeaderCell><TableHeaderCell>{identity.comparable ? "Preview minus live" : "Preview minus live (not comparable: partial input)"}</TableHeaderCell></TableRow></TableHeader>
      <TableBody>{IDENTITY_ROWS.map(([k, label]) => (
        <TableRow key={k}><TableCell>{label}</TableCell><TableCell>{identity.preview[k]}</TableCell><TableCell>{identity.live[k]}</TableCell><TableCell>{identity.comparable && identity.difference ? signed(identity.difference[k]) : "n/a"}</TableCell></TableRow>
      ))}</TableBody>
    </Table>
  );
}

// ---- dialogs ----

function CommitDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const ed = useStore((x) => x.editor);
  const header = /^module\s+\S+\s*@\s*(\S+?)\s*;/m.exec(runtime.session?.texts[ed.module] ?? "")?.[1] ?? "";
  const [version, setVersion] = useState(header);
  useEffect(() => setVersion(header), [header, open]);
  const errors = ed.diagnostics.filter((d) => d.severity === "error").length;
  return (
    <Dialog open={open} onOpenChange={(_, d) => { if (!d.open) onClose(); }}>
      <DialogSurface><DialogBody>
        <DialogTitle>Commit the draft</DialogTitle>
        <DialogContent>
          <Field label={`Version of ${ed.module}`} hint="Changing it before the commit names a new version; the engine checks the result."><Input value={version} onChange={(_, d) => setVersion(d.value)} /></Field>
          <Text block>Draft base revision {ed.base}, now at {ed.revision}. {errors ? `${errors} problem${errors === 1 ? "" : "s"} must be fixed first.` : "The engine accepts the draft."} Committing activates the definitions: the engine replays the retained inputs and every output graph receives one patch.</Text>
        </DialogContent>
        <DialogActions><Button onClick={onClose}>Cancel</Button><Button appearance="primary" disabled={errors > 0} onClick={() => { dispatch({ type: "editor/commit", version: version !== header ? version : undefined }); onClose(); }}>Commit</Button></DialogActions>
      </DialogBody></DialogSurface>
    </Dialog>
  );
}


// ---- the view ----
export function EditorView() {
  const s = useStyles();
  const ed = useStore((x) => x.editor);
  const session = runtime.session;
  const [bottom, setBottom] = useState("problems");
  const [dialog, setDialog] = useState<"commit" | "new" | null>(null);
  const picked = useStore((x) => x.selection);
  useEffect(() => { if (ed.phase.t === "closed") dispatch({ type: "editor/open" }); }, [ed.phase.t]);
  // restoring a release can remove the projection being edited: move to a projection of the module that still exists
  useEffect(() => {
    if (!session || ed.phase.t !== "ready" || !ed.projection || session.graph.entity(ed.projection)) return;
    const next = session.catalog.projections().find((p) => p.module === ed.module) ?? session.catalog.projections().find((p) => p.module !== "sattle.kernel");
    if (next) dispatch({ type: "editor/projection", module: next.module, projection: next.id });
  }, [session, ed.phase.t, ed.projection, ed.module, ed.version]);
  if (ed.phase.t === "failed") return <div className={s.pad}><MessageBar intent="warning"><MessageBarBody>{ed.phase.message}</MessageBarBody><MessageBarActions><Button onClick={() => dispatch({ type: "editor/open" })}>Try again</Button></MessageBarActions></MessageBar></div>;
  if (!session || ed.phase.t !== "ready") return <div className={s.pad}><Spinner label="Opening a draft on the engine…" /></div>;

  const c = session.catalog;
  const stack = stackOf(c, ed.projection);
  const openProjection = (id: string) => { dispatch({ type: "editor/projection", module: moduleOfId(id), projection: id }); dispatch({ type: "editor/releases", module: moduleOfId(id) }); };
  const projections = c.projections().filter((p) => p.module !== "sattle.kernel");
  const p = projections.find((x) => x.id === ed.projection);
  const onKeyDown = (e: React.KeyboardEvent) => { if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s") { e.preventDefault(); setDialog("commit"); } };

  return (
    <div className={s.root} onKeyDown={onKeyDown}>
      <section className={s.center} aria-label="Projection editor">
        <div className={s.bar}>
          <Field label="Projection" orientation="horizontal" className={s.pick}><Select value={ed.projection} onChange={(_, d) => { const q = projections.find((x) => x.id === d.value)!; dispatch({ type: "editor/projection", module: q.module, projection: q.id }); dispatch({ type: "editor/releases", module: q.module }); }}>
            {projections.map((q) => <option key={q.id} value={q.id}>{q.module}: {q.name}</option>)}</Select></Field>
          {p && <DirectionBadge direction={p.direction} />}
          <Badge appearance="outline" shape="square" color="informative">draft {ed.draft} · rev {ed.revision} · base {ed.base}</Badge>
          {ed.phase.work && <Spinner size="tiny" label={WORK_LABEL[ed.phase.work]} labelPosition="after" />}
        </div>
        <div className={s.bar}>
          <Breadcrumb aria-label="Stacked projections" size="small">
            {stack.up.flatMap((u) => [
              <BreadcrumbItem key={u.graph}>{u.projection ? <BreadcrumbButton onClick={() => openProjection(u.projection!)}>{c.name(u.graph)} ({c.name(u.projection)})</BreadcrumbButton> : <BreadcrumbButton disabled>{c.name(u.graph)}</BreadcrumbButton>}</BreadcrumbItem>,
              <BreadcrumbDivider key={`${u.graph}>`} />,
            ])}
            <BreadcrumbItem><BreadcrumbButton current>{c.name(ed.projection)}</BreadcrumbButton></BreadcrumbItem>
            {stack.down.length > 0 && <BreadcrumbDivider />}
            {stack.down.map((d, i) => <BreadcrumbItem key={d}><BreadcrumbButton onClick={() => openProjection(d)}>{i === 0 ? "feeds " : ""}{c.name(d)}</BreadcrumbButton></BreadcrumbItem>)}
          </Breadcrumb>
        </div>
        <div className={s.tools}><div className={s.scroll}><Toolbar size="small" aria-label="Editor commands">
          <Tooltip2 label="Undo a change of the draft's modules"><ToolbarButton aria-label={`Undo${ed.undo[0] ? ` ${ed.undo[0]}` : ""}`} icon={<ArrowUndo20Regular />} disabled={!ed.undo.length} onClick={() => dispatch({ type: "editor/undo" })}>Undo</ToolbarButton></Tooltip2>
          <ToolbarButton aria-label={`Redo${ed.redo[0] ? ` ${ed.redo[0]}` : ""}`} icon={<ArrowRedo20Regular />} disabled={!ed.redo.length} onClick={() => dispatch({ type: "editor/redo" })}>Redo</ToolbarButton>
          <ToolbarDivider />
          <ToolbarButton onClick={() => setDialog("new")}>New projection</ToolbarButton>
          <ToolbarButton icon={<Eye20Regular />} onClick={() => { setBottom("versions"); dispatch({ type: "editor/preview" }); }}>Preview</ToolbarButton>
          <ToolbarButton aria-label="Keyboard shortcuts" icon={<Keyboard20Regular />} onClick={() => dispatch({ type: "ui/dialog", dialog: "shortcuts" })} />
        </Toolbar></div>
          <Toolbar size="small" aria-label="Draft" className={s.fixed}>
            <ToolbarButton onClick={() => dispatch({ type: "editor/discard" })}>Discard draft</ToolbarButton>
            <ToolbarButton icon={<Save20Regular />} appearance="primary" onClick={() => setDialog("commit")}>Commit…</ToolbarButton>
          </Toolbar>
        </div>
        <div style={{ position: "relative", minHeight: 0 }}>
          {p && <Suspense fallback={<Spinner label="Loading the text editor…" />}><KqlText port={session.svc} projection={`${p.module}.${p.name}`} draft={ed.draft || undefined} rows={ed.draft ? () => session.preview({ include: ["rows"], rowLimit: "1" }).then((pv) => pv.graphs.flatMap((g) => g.operators ?? [])) : undefined} store={(text) => storeKqlText(`${p.module}.${p.name}.kql`, text, dispatch)}
            picked={picked && runtime.ws?.history(picked.graph) ? { graph: picked.graph, data: runtime.ws.history(picked.graph)!.graph, ids: [picked.id] } : null} /></Suspense>}
          {ed.notice && <div style={{ position: "absolute", left: 36, right: 36, top: 36 }}><MessageBar intent="warning"><MessageBarBody>{ed.notice}</MessageBarBody><MessageBarActions><Button size="small" onClick={() => dispatch({ type: "editor/dismiss", what: "notice" })}>Dismiss</Button></MessageBarActions></MessageBar></div>}
          {ed.committed && <div style={{ position: "absolute", left: 8, right: 8, top: 8 }}><MessageBar intent="success"><MessageBarBody>{ed.committed}</MessageBarBody></MessageBar></div>}
        </div>
        <div className={s.bottom}>
          <TabList size="small" selectedValue={bottom} onTabSelect={(_, d) => setBottom(String(d.value))} aria-label="Draft details">
            <Tab value="problems">Problems{ed.diagnostics.length ? ` (${ed.diagnostics.length})` : ""}</Tab><Tab value="text">Text</Tab><Tab value="definitions">Definitions</Tab><Tab value="versions">Versions and preview</Tab>
          </TabList>
          {bottom === "problems" ? <Problems /> : bottom === "text" ? <TextPanel /> : bottom === "definitions" ? <Definitions session={session} module={ed.module} /> : <Projects />}
        </div>
      </section>
      <CommitDialog open={dialog === "commit"} onClose={() => setDialog(null)} />
      <NewProjectionDialog open={dialog === "new"} onClose={() => setDialog(null)} />
    </div>
  );
}

import { Tooltip } from "@fluentui/react-components";
const Tooltip2 = ({ label, children }: { label: string; children: React.ReactElement }) => <Tooltip content={label} relationship="description">{children}</Tooltip>;
