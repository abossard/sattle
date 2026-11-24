// The projection editor: the projection's text in the editor of its language, and the draft's problems, module texts, definitions, versions and
// previews around one draft on the engine.
import {
  Badge, Breadcrumb, BreadcrumbButton, BreadcrumbDivider, BreadcrumbItem, Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, MessageBar, MessageBarActions, MessageBarBody, Select, Spinner, Tab, TabList, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, Textarea, Toolbar, ToolbarButton, ToolbarDivider, makeStyles, tokens,
} from "@fluentui/react-components";
import { Checkmark12Regular, ArrowRedo20Regular, ArrowUndo20Regular, Eye20Regular, Keyboard20Regular, Save20Regular } from "@fluentui/react-icons";
import { Suspense, useCallback, useEffect, useEffectEvent, useMemo, useRef, useState } from "react";
import { DataPane } from "../app/data-pane";
import { History } from "../app/history";
import { Graph } from "../core/graph";
import { moduleOfId } from "../core/catalog";
import { printGkql } from "../editor/gkql";
import { stackOf } from "../editor/stack";
import type { IdentityEffect, Release } from "../editor/service";
import { rendererTheme } from "../state/effects";
import { recoverExpiredDraft, storeProjectionText } from "../state/editor-effects";
import { runtime } from "../state/runtime";
import { dispatch, useStore } from "../state/store";
import type { Command, EditorState } from "../state/types";
import { WORK_LABEL } from "../state/editor-phase";
import { lessonOf } from "../tutorial/lessons";
import { exerciseProgram } from "../tutorial/projection-exercises";
import { webgl2Available } from "./CanvasHost";
import { DirectionBadge, useRevision } from "./common";
import { Definitions } from "./editor/Definitions";
import { editorFor } from "./editor/languages";
import { NewProjectionDialog } from "./editor/NewProjection";

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateColumns: "minmax(0, 1fr)", minHeight: 0, height: "100%" },
  center: { display: "grid", gridTemplateColumns: "minmax(0, 1fr)", gridTemplateRows: "auto auto auto minmax(0, 1fr) minmax(140px, 28%)", minHeight: 0, minWidth: 0, overflow: "hidden" },
  tools: { display: "flex", alignItems: "center", flexWrap: "wrap", minWidth: 0, whiteSpace: "nowrap", borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, "& .fui-Toolbar > *": { flexShrink: 0 } },
  // the last 32px fade out, so a cut label reads as "scroll for more"
  scroll: { overflowX: "auto", minWidth: 0, flex: "1 1 240px", maskImage: "linear-gradient(to right, #000 calc(100% - 32px), transparent)" },
  fixed: { flex: "0 0 auto", borderLeft: `1px solid ${tokens.colorNeutralStroke2}`, "@media (max-width: 1000px)": { flexBasis: "100%", borderLeft: "none", borderTop: `1px solid ${tokens.colorNeutralStroke2}`, justifyContent: "flex-end" } },
  bar: { display: "flex", alignItems: "center", columnGap: tokens.spacingHorizontalS, flexWrap: "wrap", padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`, "@media (min-width: 1001px)": { flexWrap: "nowrap", "& > .fui-Badge, & > .fui-TabList": { flexShrink: 0 } } },
  pad: { padding: tokens.spacingHorizontalM, display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalS },
  draftGraphDialog: { width: "min(92vw, 880px)", maxWidth: "880px" },
  typeCounts: { display: "flex", flexWrap: "wrap", gap: tokens.spacingHorizontalXS, marginBottom: tokens.spacingVerticalS },
  draftRelationship: { display: "grid", gap: tokens.spacingVerticalS, marginBottom: tokens.spacingVerticalM, "& .fui-Select": { minWidth: 0 }, "& button": { justifySelf: "start" } },
  draftEndpoints: { display: "flex", flexWrap: "wrap", gap: tokens.spacingHorizontalL, margin: 0, "& > div": { flex: "1 1 144px", minWidth: 0, overflowWrap: "anywhere" }, "& dd": { margin: 0 } },
  bottom: { minHeight: 0, overflow: "auto", borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
  grow: { flexGrow: 1 },
  mono: { fontFamily: tokens.fontFamilyMonospace, width: "100%" },
  /** the graphs of one level of the stack: side by side, no divider between them (none is computed from another) */
  level: { display: "flex", flexWrap: "wrap", columnGap: tokens.spacingHorizontalXS },
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

function ModuleGkql({ module, source, recoveredText }: { module: string; source: string; recoveredText?: string }) {
  const s = useStyles();
  const diagnostics = useStore((x) => x.editor.diagnostics);
  const [edited, setEdited] = useState<string | null>(recoveredText ?? null);
  const [saving, setSaving] = useState(false);
  const [checked, setChecked] = useState(false);
  const [error, setError] = useState("");
  const text = edited ?? source;
  const problem = diagnostics.find((d) => d.file === module || d.focus === `d:${module}` || d.focus.startsWith(`d:${module}/`));
  const storeModule = async () => {
    setSaving(true);
    setError("");
    try {
      const error = await storeProjectionText(module, text, dispatch);
      if (error) setError(error);
      else { setEdited(null); setChecked(true); }
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setSaving(false);
    }
  };
  return (
    <div className={s.pad}>
      <div className={s.bar}>
        <Text font="monospace">{module}</Text>
        <Button size="small" appearance="primary" disabled={text === source || saving} onClick={() => void storeModule()}>{saving ? "Checking…" : "Check & store module"}</Button>
        <div aria-live="polite">
          {(error || problem) && <Text>{error || problem?.message}</Text>}
          {checked && !edited && !error && !problem && <Badge color="success" appearance="tint">Module checked</Badge>}
        </div>
      </div>
      <Textarea className={s.mono} resize="vertical" rows={14} aria-label={`GKQL module ${module}`} value={text} readOnly={saving} onChange={(_, d) => { setEdited(d.value); runtime.session?.noteLocalText(module, d.value); setChecked(false); setError(""); }} textarea={{ style: { fontFamily: tokens.fontFamilyMonospace, whiteSpace: "pre" } }} />
    </div>
  );
}

function ProblemTable({ diagnostics, label }: { diagnostics: EditorState["diagnostics"]; label: string }) {
  return (
    <Table size="small" aria-label={label}>
      <TableHeader><TableRow><TableHeaderCell>Severity</TableHeaderCell><TableHeaderCell>Problem</TableHeaderCell><TableHeaderCell>Where</TableHeaderCell></TableRow></TableHeader>
      <TableBody>{diagnostics.map((d, i) => (
        <TableRow key={i}>
          <TableCell>{d.severity === "error" ? "✖ error" : "⚠ warning"}</TableCell><TableCell>{d.code}: {d.message}</TableCell><TableCell><Text size={200}>{d.focus.replace(/^d:/, "")}{d.line ? ` · line ${d.line}` : ""}</Text></TableCell>
        </TableRow>
      ))}</TableBody>
    </Table>
  );
}

function Problems({ diagnostics, other }: { diagnostics: EditorState["diagnostics"]; other?: EditorState["diagnostics"] }) {
  const s = useStyles();
  if (!other) {
    return diagnostics.length ? <ProblemTable diagnostics={diagnostics} label="Problems in the draft" />
      : <div className={s.pad}><Badge color="informative" appearance="outline" shape="square" icon={<Checkmark12Regular />}>no problems</Badge><Text>The engine accepts the draft as it is.</Text></div>;
  }
  return <div className={s.pad}>
    {diagnostics.length > 0 ? <ProblemTable diagnostics={diagnostics} label="Problems in this projection" />
      : <div><Badge color="informative" appearance="outline" shape="square" icon={<Checkmark12Regular />}>No problems in this projection</Badge></div>}
    {other.length > 0 && <details open={other.some((d) => d.severity === "error") || undefined}>
      <summary>Other draft diagnostics ({other.length})</summary>
      <ProblemTable diagnostics={other} label="Other draft diagnostics" />
    </details>}
  </div>;
}

// ---- versions and previews ----
function PreviewCanvas({ graph, id, relationshipId, onRelationshipSelect }: { graph: Graph; id: string; relationshipId?: string; onRelationshipSelect?: (id: string) => void }) {
  const host = useRef<HTMLDivElement>(null);
  const activePane = useRef<DataPane>(null);
  const theme = useStore((x) => x.theme);
  const pickRelationship = useEffectEvent((edge: string) => onRelationshipSelect?.(edge));
  useEffect(() => {
    const ws = runtime.ws;
    if (!ws || !host.current || !webgl2Available()) return;
    const info = ws.graphInfo(id) ?? ws.info.graphs.find((g) => g.role !== "catalog");
    if (!info) return;
    const el = document.createElement("div");
    host.current.replaceChildren(el);
    const pane = new DataPane({ ...info, id, inputs: [] }, new History(graph.toSnapshot()), el, rendererTheme(theme), {
      select: (selection) => { if (selection?.kind === "relationship") pickRelationship(selection.id); },
      hover: () => {}, activate: () => {}, changed: () => {}, announce: () => {}, key: () => false,
    });
    activePane.current = pane;
    pane.start();
    return () => { activePane.current = null; pane.dispose(); };
  }, [graph, id, theme]);
  useEffect(() => {
    activePane.current?.setSelection(relationshipId ? { graph: id, kind: "relationship", id: relationshipId } : null);
  }, [graph, id, relationshipId, theme]);
  return webgl2Available() ? <div ref={host} style={{ height: 320 }} /> : <Text>WebGL2 is not available for the diagram.</Text>;
}

function GuidedPreview({ graphId, inScope }: { graphId: string; inScope: (diagnostic: EditorState["diagnostics"][number]) => boolean }) {
  const s = useStyles();
  const ed = useStore((x) => x.editor);
  const lesson = lessonOf(useStore((x) => x.guide));
  const reference = useRevision(lesson?.graph ?? "");
  const [showGraph, setShowGraph] = useState(false);
  const [relationshipId, setRelationshipId] = useState("");
  const preview = ed.preview;
  const relevant = preview?.diagnostics.filter((d) => inScope(d) || d.severity === "error") ?? [];
  const other = preview?.diagnostics.filter((d) => !relevant.includes(d)) ?? [];
  const result = preview?.graphs.find((item) => item.id === graphId);
  const snapshot = result?.hasSnapshot ? runtime.preview?.graphs.find((item) => item.id === graphId)?.snapshot : undefined;
  const graph = useMemo(() => snapshot && Graph.fromSnapshot(snapshot), [snapshot]);
  const types = useMemo(() => {
    const counts = new Map<string, number>();
    for (const edge of snapshot?.relationships ?? []) counts.set(edge.type, (counts.get(edge.type) ?? 0) + 1);
    return [...counts].sort(([a], [b]) => a.localeCompare(b));
  }, [snapshot]);
  const relationships = snapshot?.relationships ?? [];
  const relationship = graph?.rel(relationshipId)
    ?? relationships.find((edge) => edge.type === lesson?.example?.type)
    ?? relationships[0];
  const sourceType = relationship && graph?.entity(relationship.source)?.type;
  const targetType = relationship && graph?.entity(relationship.target)?.type;
  const nodeLabel = (id: string) => {
    const entity = graph?.entity(id);
    return entity ? graph!.label(entity) : id;
  };
  const referenceEdge = useMemo(() => {
    if (!reference || !relationship) return undefined;
    const source = reference.graph.nodeSlot.get(relationship.source);
    if (source === undefined) return undefined;
    const matches = reference.graph.out[source].flatMap((slot) => {
      const edge = reference.graph.edges[slot];
      return edge?.type === relationship.type && edge.target === relationship.target ? [edge] : [];
    });
    return matches.find((edge) => edge.id === relationship.id) ?? (matches.length === 1 ? matches[0] : undefined);
  }, [reference, relationship]);
  const inspectReferenceEdge = () => {
    if (!reference || !referenceEdge) return;
    setShowGraph(false);
    dispatch({ type: "ui/view", view: "data" });
    dispatch({ type: "ui/activeGraph", graph: reference.id });
    dispatch({ type: "select/element", selection: { graph: reference.id, kind: "relationship", id: referenceEdge.id } });
  };

  return <section className={s.pad} aria-label="Draft preview">
    <div className={s.bar}>
      <div role="status">
        {result ? <Text as="h2" weight="semibold"><code>{graphId}</code> · {result.entities} nodes · {result.relationships} relationships</Text>
          : <Text weight="semibold">{working(ed) === "preview" ? `Evaluating ${graphId}…` : `Preview ${graphId} to see its output.`}</Text>}
      </div>
      <span className={s.grow} />
      {graph && <Button size="small" onClick={() => setShowGraph(true)}>Show draft graph</Button>}
    </div>
    {preview && !preview.scope.complete && <Text size={200}>Sampled {preview.scope.rows} of {preview.scope.totalRows} input rows.</Text>}
    {relevant.length > 0 && <MessageBar intent={relevant.some((d) => d.severity === "error") ? "error" : "warning"}><MessageBarBody>{relevant.map((d) => d.message).join(". ")}</MessageBarBody></MessageBar>}
    {other.length > 0 && <details>
      <summary>Other preview diagnostics ({other.length})</summary>
      <ProblemTable diagnostics={other} label="Other preview diagnostics" />
    </details>}
    <Dialog open={showGraph && !!graph} onOpenChange={(_, event) => setShowGraph(event.open)}>
      <DialogSurface className={s.draftGraphDialog}><DialogBody>
        <DialogTitle>Draft graph <code>{graphId}</code></DialogTitle>
        <DialogContent>
          <div className={s.typeCounts} role="group" aria-label="Relationships by type">
            {types.map(([type, count]) => <Badge key={type} appearance="outline" color="informative">{type} · {count}</Badge>)}
          </div>
          {result?.truncated && <Text size={200}>The diagram shows a snapshot sample.</Text>}
          {relationship && <div className={s.draftRelationship}>
            <Field label="Draft relationship">
              <Select value={relationship.id} onChange={(_, data) => setRelationshipId(data.value)}>
                {relationships.map((edge) => <option key={edge.id} value={edge.id}>{edge.type} · {nodeLabel(edge.source)} → {nodeLabel(edge.target)}</option>)}
              </Select>
            </Field>
            <dl className={s.draftEndpoints}>
              <div><dt><Text size={200} weight="semibold">From</Text></dt><dd>{nodeLabel(relationship.source)}{sourceType && <Text size={200}> · {sourceType}</Text>}</dd></div>
              <div><dt><Text size={200} weight="semibold">Type</Text></dt><dd>{relationship.type}</dd></div>
              <div><dt><Text size={200} weight="semibold">To</Text></dt><dd>{nodeLabel(relationship.target)}{targetType && <Text size={200}> · {targetType}</Text>}</dd></div>
            </dl>
            {referenceEdge && <Button size="small" onClick={inspectReferenceEdge}>Trace matching edge in reference graph</Button>}
          </div>}
          {graph && <PreviewCanvas graph={graph} id={graphId} relationshipId={relationship?.id} onRelationshipSelect={setRelationshipId} />}
        </DialogContent>
        <DialogActions><Button appearance="primary" onClick={() => setShowGraph(false)}>Close</Button></DialogActions>
      </DialogBody></DialogSurface>
    </Dialog>
  </section>;
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
          {snap && <PreviewCanvas graph={Graph.fromSnapshot(snap)} id={previewGraph} />}
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

function CommitDialog({ open, onClose, native }: { open: boolean; onClose: () => void; native: boolean }) {
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
          {!native && <Field label={`Version of ${ed.module}`} hint="Changing it before the commit names a new version; the engine checks the result."><Input value={version} onChange={(_, d) => setVersion(d.value)} /></Field>}
          <Text block>Draft base revision {ed.base}, now at {ed.revision}. {errors ? `${errors} problem${errors === 1 ? "" : "s"} must be fixed first.` : "The engine accepts the draft."} Committing activates the definitions: the engine replays the retained inputs and every output graph receives one patch.</Text>
        </DialogContent>
        <DialogActions><Button onClick={onClose}>Cancel</Button><Button appearance="primary" disabled={errors > 0} onClick={() => { dispatch({ type: "editor/commit", version: !native && version !== header ? version : undefined }); onClose(); }}>Commit</Button></DialogActions>
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
  const guide = useStore((x) => x.guide);
  const lesson = lessonOf(guide);
  const projections = session?.catalog.projections().filter((p) => p.module !== "sattle.kernel") ?? [];
  const p = projections.find((x) => x.id === ed.projection);
  const file = p && session?.fileOf(`${p.module}.${p.name}`);
  const fileName = file?.file;
  const noteProjectionText = useCallback((text: string) => { if (fileName) session?.noteLocalText(fileName, text); }, [session, fileName]);
  const onDraftError = useCallback((error: unknown) => { if (session) recoverExpiredDraft(session, error, dispatch); }, [session]);
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
  const native = file?.language === "gkql";
  const exercise = native && lesson?.build && file.projection === `${lesson.module}.${lesson.build.name}` ? lesson.build : undefined;
  const exerciseGraph = exercise?.name;
  const reference = native && lesson?.build && file.projection === `${lesson.module}.${lesson.projection}`;
  const targets = [ed.projection, ...stack.outputs];
  const inScope = (d: EditorState["diagnostics"][number]) => d.file === fileName || targets.some((id) => d.focus === id || d.focus.startsWith(`${id}/`));
  const problems = exercise ? ed.diagnostics.filter(inScope) : ed.diagnostics;
  const otherProblems = exercise ? ed.diagnostics.filter((d) => !problems.includes(d)) : undefined;
  const details = bottom === "draft-preview" && !exerciseGraph ? "problems" : bottom;
  const Editor = file && editorFor(file.language);
  const onKeyDown = (e: React.KeyboardEvent) => { if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s") { e.preventDefault(); setDialog("commit"); } };

  return (
    <div className={s.root} onKeyDown={onKeyDown}>
      <section className={s.center} aria-label="Projection editor">
        <div className={s.bar}>
          <Field label="Projection" orientation="horizontal" className={s.pick}><Select value={ed.projection} onChange={(_, d) => { const q = projections.find((x) => x.id === d.value)!; dispatch({ type: "editor/projection", module: q.module, projection: q.id }); dispatch({ type: "editor/releases", module: q.module }); }}>
            {projections.map((q) => <option key={q.id} value={q.id}>{q.module}: {q.name}</option>)}</Select></Field>
          {exerciseGraph && <Badge appearance="tint" color="informative">Exercise draft</Badge>}
          {reference && <Badge appearance="outline" color="informative">Reference projection</Badge>}
          {p && <DirectionBadge direction={p.direction} />}
          <Badge appearance="outline" shape="square" color="informative">draft {ed.draft} · rev {ed.revision} · base {ed.base}</Badge>
          {ed.phase.work && <Spinner size="tiny" label={WORK_LABEL[ed.phase.work]} labelPosition="after" />}
        </div>
        <div className={s.bar}>
          <Breadcrumb aria-label="Stacked projections" size="small">
            {stack.up.flatMap((level, i) => [
              <BreadcrumbItem key={i} className={s.level}>{level.map((u) => u.projection ? <BreadcrumbButton key={u.graph} onClick={() => openProjection(u.projection!)}>{c.name(u.graph)} ({c.name(u.projection)})</BreadcrumbButton> : <BreadcrumbButton key={u.graph} disabled>{c.name(u.graph)}</BreadcrumbButton>)}</BreadcrumbItem>,
              <BreadcrumbDivider key={`${i}>`} />,
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
          <ToolbarButton icon={<Eye20Regular />} disabled={!!exerciseGraph && !!working(ed)} onClick={() => {
            setBottom(exerciseGraph ? "draft-preview" : "versions");
            dispatch({ type: "editor/preview", graphs: exerciseGraph ? [exerciseGraph] : undefined });
          }}>{exerciseGraph ? "Preview draft" : "Preview"}</ToolbarButton>
          <ToolbarButton aria-label="Keyboard shortcuts" icon={<Keyboard20Regular />} onClick={() => dispatch({ type: "ui/dialog", dialog: "shortcuts" })} />
        </Toolbar></div>
          <Toolbar size="small" aria-label="Draft" className={s.fixed}>
            <ToolbarButton onClick={() => dispatch({ type: "editor/discard" })}>Discard draft</ToolbarButton>
            <ToolbarButton icon={<Save20Regular />} appearance="primary" onClick={() => setDialog("commit")}>Commit…</ToolbarButton>
          </Toolbar>
        </div>
        <div style={{ position: "relative", minHeight: 0 }}>
          {file && Editor && <Suspense fallback={<Spinner label="Loading the projection editor…" />}><Editor key={`${ed.draft}/${file.file}`} port={session.svc} file={file} draft={ed.draft || undefined} source={session.texts[file.file]} initialTab={exerciseGraph ? "source" : undefined} starter={exercise ? printGkql(exerciseProgram(exercise)) : undefined} recoveredText={ed.recoveredText[file.file]} onEdit={noteProjectionText} onDraftError={onDraftError} diagnostics={ed.diagnostics} rows={ed.draft ? () => session.preview({ include: ["rows"], rowLimit: "1" }).then((pv) => pv.graphs.flatMap((g) => g.operators ?? []), (error) => { onDraftError(error); throw error; }) : undefined} store={(text) => storeProjectionText(file.file, text, dispatch)}
            picked={picked && runtime.ws?.history(picked.graph) ? { graph: picked.graph, data: runtime.ws.history(picked.graph)!.graph, ids: [picked.id] } : null} /></Suspense>}
          {ed.notice && <div style={{ position: "absolute", left: 36, right: 36, top: 36 }}><MessageBar intent="warning"><MessageBarBody>{ed.notice}</MessageBarBody><MessageBarActions><Button size="small" onClick={() => dispatch({ type: "editor/dismiss", what: "notice" })}>Dismiss</Button></MessageBarActions></MessageBar></div>}
          {ed.committed && <div style={{ position: "absolute", left: 8, right: 8, top: 8 }}><MessageBar intent="success"><MessageBarBody>{ed.committed}</MessageBarBody></MessageBar></div>}
        </div>
        <div className={s.bottom}>
          <TabList size="small" selectedValue={details} onTabSelect={(_, d) => setBottom(String(d.value))} aria-label="Draft details">
            <Tab value="problems">Problems{problems.length ? ` (${problems.length})` : ""}</Tab><Tab value="text">{native ? "Module GKQL" : "Text"}</Tab>{!native && <Tab value="definitions">Definitions</Tab>}{exerciseGraph && <Tab value="draft-preview">Draft preview</Tab>}<Tab value="versions">Versions and preview</Tab>
          </TabList>
          {details === "problems" || (details === "definitions" && native) ? <Problems diagnostics={problems} other={otherProblems} /> : details === "text"
            ? native ? <ModuleGkql key={`${ed.draft}/${ed.module}`} module={ed.module} source={session.texts[ed.module] ?? ""} recoveredText={ed.recoveredText[ed.module]} /> : <TextPanel />
            : details === "draft-preview" && exerciseGraph ? <GuidedPreview key={exerciseGraph} graphId={exerciseGraph} inScope={inScope} />
            : details === "definitions" ? <Definitions session={session} module={ed.module} /> : <Projects />}
        </div>
      </section>
      <CommitDialog open={dialog === "commit"} onClose={() => setDialog(null)} native={native} />
      <NewProjectionDialog open={dialog === "new"} onClose={() => setDialog(null)} native={native} />
    </div>
  );
}

import { Tooltip } from "@fluentui/react-components";
const Tooltip2 = ({ label, children }: { label: string; children: React.ReactElement }) => <Tooltip content={label} relationship="description">{children}</Tooltip>;
