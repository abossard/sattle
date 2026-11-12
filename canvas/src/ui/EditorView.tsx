// The projection editor: palette, node canvas, property panel, text, problems, versions and previews around one draft on the engine.
import {
  Badge, Breadcrumb, BreadcrumbButton, BreadcrumbDivider, BreadcrumbItem, Button, Combobox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, MessageBar, MessageBarActions, MessageBarBody, Option, Select,
  Spinner, Tab, TabList, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, Textarea, Toolbar, ToolbarButton, ToolbarDivider, Tree, TreeItem, TreeItemLayout, makeStyles, tokens,
} from "@fluentui/react-components";
import { Add20Regular, ArrowRedo20Regular, ArrowUndo20Regular, Comment20Regular, Copy20Regular, ClipboardPaste20Regular, Delete20Regular, Eye20Regular, Keyboard20Regular, Save20Regular, AlignLeft20Regular, FolderArrowRight20Regular } from "@fluentui/react-icons";
import { useEffect, useMemo, useRef, useState } from "react";
import { DataPane } from "../app/data-pane";
import { History } from "../app/history";
import { Graph } from "../core/graph";
import type { Entity } from "../core/model";
import { moduleOfId } from "../core/catalog";
import { canWire, statementNames } from "../editor/commands";
import { stackOf } from "../editor/stack";
import { align, autoLayout, nodesInRect, NODE_W, type Pos } from "../editor/layout";
import { ROLE_LABEL, ROLE_ORDER } from "../editor/palette";
import type { IdentityEffect, Release } from "../editor/service";
import { rendererTheme } from "../state/effects";
import { runtime } from "../state/runtime";
import { dispatch, useStore } from "../state/store";
import type { Command } from "../state/types";
import { webgl2Available } from "./CanvasHost";
import { DirectionBadge } from "./common";
import { EditorCanvas, portIdOf, type Group, type Note } from "./editor/EditorCanvas";
import { incompleteOperators } from "../editor/incomplete";
import { Definitions } from "./editor/Definitions";
import { NewProjectionDialog } from "./editor/NewProjection";
import { Properties } from "./editor/Properties";
import { Notations } from "../editors/Notations";

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateColumns: "minmax(200px, 260px) minmax(0, 1fr) minmax(260px, 340px)", minHeight: 0, height: "100%", "@media (max-width: 1000px)": { gridTemplateColumns: "1fr", height: "auto" } },
  side: { overflow: "auto", minHeight: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}` },
  right: { overflow: "auto", minHeight: 0, borderLeft: `1px solid ${tokens.colorNeutralStroke2}` },
  center: { display: "grid", gridTemplateRows: "auto auto auto minmax(0, 1fr) minmax(160px, 36%)", minHeight: 0, minWidth: 0, overflow: "hidden" },
  tools: { overflowX: "auto", minWidth: 0 },
  bar: { display: "flex", alignItems: "center", columnGap: tokens.spacingHorizontalS, flexWrap: "wrap", padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}` },
  pad: { padding: tokens.spacingHorizontalM, display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalS },
  bottom: { minHeight: 0, overflow: "auto", borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
  grow: { flexGrow: 1 },
  mono: { fontFamily: tokens.fontFamilyMonospace, width: "100%" },
});

const store = (key: string) => { try { return JSON.parse(localStorage.getItem(key) ?? "null"); } catch { return null; } };
const save = (key: string, v: unknown) => { try { localStorage.setItem(key, JSON.stringify(v)); } catch { /* storage unavailable */ } };
const run = (cmd: Command) => dispatch({ type: "editor/do", cmd });
const short = (d: string) => d.replace("sha256:", "").slice(0, 10);

// ---- palette ----
function PalettePanel() {
  const s = useStyles();
  const ed = useStore((x) => x.editor);
  const pal = runtime.session?.palette;
  if (!pal) return null;
  return (
    <div className={s.pad}>
      <Text>Choose an operator to add it to the projection. Operators the engine cannot evaluate yet are disabled.</Text>
      <Tree aria-label="Operators">
        {ROLE_ORDER.map((role) => (
          <TreeItem key={role} itemType="branch" value={role}>
            <TreeItemLayout>{ROLE_LABEL[role] ?? role}</TreeItemLayout>
            <Tree>{pal.operators.filter((o) => o.role === role).map((o) => (
              <TreeItem key={o.kind} itemType="leaf" value={o.kind} aria-disabled={!o.supported || undefined} onClick={() => o.supported && ed.projection && run({ t: "addOperator", kind: o.kind })}>
                <TreeItemLayout>{o.kind}{!o.supported && <Text size={200}> (not built: {o.unsupportedReason})</Text>}</TreeItemLayout>
              </TreeItem>
            ))}</Tree>
          </TreeItem>
        ))}
      </Tree>
    </div>
  );
}

// ---- outline: the same projection as a tree, for keyboard and screen reader use ----
function Outline() {
  const ed = useStore((x) => x.editor);
  const s = runtime.session;
  if (!s) return null;
  const c = s.catalog, g = s.graph;
  const dg = c.projectionGraph(ed.projection);
  return (
    <Tree aria-label="Operators of the projection">
      {dg.nodes.map((n) => (
        <TreeItem key={n.id} itemType="leaf" value={n.id} onClick={() => dispatch({ type: "editor/select", ids: [n.id] })} aria-current={ed.selected.includes(n.id) || undefined}>
          <TreeItemLayout>{ed.selected.includes(n.id) ? <b>{n.opKind ?? n.kind} {n.title}</b> : `${n.opKind ?? n.kind} ${n.title}`} <Text size={200}>{dg.edges.filter((e) => e.kind === "wire" && e.from === n.id).map((e) => `→ ${g.entity(e.to) ? c.name(e.to) : e.to}`).join(" ")}</Text></TreeItemLayout>
        </TreeItem>
      ))}
    </Tree>
  );
}

// ---- text ----
function TextPanel() {
  const s = useStyles();
  const ed = useStore((x) => x.editor);
  const session = runtime.session;
  const mods = useMemo(() => Object.keys(session?.texts ?? {}).filter((m) => !m.startsWith("sattle.kernel")), [ed.textVersion]); // eslint-disable-line react-hooks/exhaustive-deps
  const [module, setModule] = useState(ed.module);
  const [text, setText] = useState("");
  const focused = useRef(false);
  useEffect(() => setModule(ed.module), [ed.module]);
  useEffect(() => { if (!focused.current) setText(session?.texts[module] ?? ""); }, [ed.textVersion, module]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!focused.current || text === (session?.texts[module] ?? "")) return;
    const t = setTimeout(() => run({ t: "text", module, text }), 700);
    return () => clearTimeout(t);
  }, [text]); // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <div className={s.pad}>
      <div className={s.bar}>
        <Field label="Module" orientation="horizontal"><Select value={module} onChange={(_, d) => { setModule(d.value); setText(session?.texts[d.value] ?? ""); }}>{mods.map((m) => <option key={m}>{m}</option>)}</Select></Field>
        <Text size={200}>The text is the draft printed canonically. Edits are checked by the engine and become one undo step.</Text>
      </div>
      <Textarea className={s.mono} resize="vertical" rows={12} aria-label={`Text of module ${module}`} value={text} onFocus={() => { focused.current = true; }} onBlur={() => { focused.current = false; if (text !== (session?.texts[module] ?? "")) run({ t: "text", module, text }); /* the pending edit is sent now, not lost with the timer */ else setText(session?.texts[module] ?? text); }} onChange={(_, d) => setText(d.value)} textarea={{ style: { fontFamily: tokens.fontFamilyMonospace, whiteSpace: "pre" } }} />
    </div>
  );
}

function Problems() {
  const s = useStyles();
  const ed = useStore((x) => x.editor);
  const session = runtime.session;
  const incomplete = session ? incompleteOperators(session.graph, session.palette, ed.projection) : [];
  const projectionOnly = ed.diagnostics.filter((d) => d.focus === ed.projection);
  const lead = incomplete.length > 0 && (
    <MessageBar intent="warning" role="region" aria-label="Incomplete operators"><MessageBarBody>
      {projectionOnly.length > 0 ? `The engine reports the projection as incomplete: ${projectionOnly[0].message}. ` : ""}The editor finds {incomplete.length} operator{incomplete.length === 1 ? "" : "s"} that still need something:
      <ul>{incomplete.map((i) => <li key={i.op}><Button appearance="transparent" size="small" onClick={() => dispatch({ type: "editor/select", ids: [i.op] })}>{i.kind} {i.name}</Button>: {i.missing.join(", ")}</li>)}</ul>
    </MessageBarBody></MessageBar>
  );
  if (!ed.diagnostics.length && !incomplete.length) return <div className={s.pad}><Badge color="success" appearance="outline">✓ no problems</Badge><Text>The engine accepts the draft as it is.</Text></div>;
  return (
    <>
    {lead}
    <Table size="small" aria-label="Problems in the draft">
      <TableHeader><TableRow><TableHeaderCell>Severity</TableHeaderCell><TableHeaderCell>Problem</TableHeaderCell><TableHeaderCell>Where</TableHeaderCell></TableRow></TableHeader>
      <TableBody>{ed.diagnostics.map((d, i) => (
        <TableRow key={i} tabIndex={0} onClick={() => { const dg = runtime.session!.catalog.projectionGraph(ed.projection); const hit = runtime.session!.catalog.nearest(d.focus, (id) => dg.nodes.some((n) => n.id === id)); if (hit) dispatch({ type: "editor/select", ids: [hit] }); }}>
          <TableCell>{d.severity === "error" ? "✖ error" : "⚠ warning"}</TableCell><TableCell>{d.code}: {d.message}</TableCell><TableCell><Text size={200}>{d.focus.replace(/^d:/, "")}{d.line ? ` · line ${d.line}` : ""}</Text></TableCell>
        </TableRow>
      ))}</TableBody>
    </Table>
    </>
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
        <Button size="small" icon={ed.busy === "preview" ? <Spinner size="tiny" /> : <Eye20Regular />} disabled={ed.busy === "preview"} onClick={() => dispatch({ type: "editor/preview" })}>Preview output graphs</Button>
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
function QuickAdd({ open, onClose }: { open: boolean; onClose: () => void }) {
  const pal = runtime.session?.palette;
  const [text, setText] = useState("");
  return (
    <Dialog open={open} onOpenChange={(_, d) => { if (!d.open) onClose(); }}>
      <DialogSurface><DialogBody>
        <DialogTitle>Add an operator</DialogTitle>
        <DialogContent>
          <Field label="Operator (type to filter, Enter to add)">
            <Combobox freeform value={text} onChange={(e) => setText(e.target.value)} onOptionSelect={(_, d) => { if (d.optionValue) { run({ t: "addOperator", kind: d.optionValue }); setText(""); onClose(); } }}>
              {pal?.operators.filter((o) => o.kind.toLowerCase().includes(text.toLowerCase())).map((o) => <Option key={o.kind} value={o.kind} disabled={!o.supported} text={o.kind}>{o.kind} <Text size={200}>{o.supported ? ROLE_LABEL[o.role] : `not built: ${o.unsupportedReason}`}</Text></Option>)}
            </Combobox>
          </Field>
        </DialogContent>
        <DialogActions><Button onClick={onClose}>Cancel</Button></DialogActions>
      </DialogBody></DialogSurface>
    </Dialog>
  );
}

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

function RefactorDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const ed = useStore((x) => x.editor);
  const session = runtime.session;
  const [kind, setKind] = useState<"extract" | "inline" | "moveRule">("extract");
  const [name, setName] = useState("");
  const [other, setOther] = useState("");
  const c = session?.catalog;
  const projections = c?.projections().filter((p) => p.module === ed.module) ?? [];
  const cur = c?.name(ed.projection) ?? "";
  const rule = ed.selected.find((id) => id.includes("/rule:"));
  const req = () => {
    const module = ed.module;
    if (kind === "extract") return { kind, module, projection: cur, select: statementNames(session!.graph, ed.selected), name } as const;
    if (kind === "inline") return { kind, module, projection: cur, into: other } as const;
    return { kind, module, from: cur, to: other, rule: (rule ?? "").slice((rule ?? "").lastIndexOf(":") + 1) } as const;
  };
  return (
    <Dialog open={open} onOpenChange={(_, d) => { if (!d.open) { dispatch({ type: "editor/dismiss", what: "pending" }); onClose(); } }}>
      <DialogSurface><DialogBody>
        <DialogTitle>Refactor projections</DialogTitle>
        <DialogContent>
          <Field label="Refactoring"><Select value={kind} onChange={(_, d) => setKind(d.value as typeof kind)}>
            <option value="extract">Move the selection into a new projection</option><option value="inline">Inline this projection into another</option><option value="moveRule">Move the selected rule to another projection</option>
          </Select></Field>
          {kind === "extract" && <Field label="Name of the new projection" hint={`Statements: ${statementNames(session?.graph as Graph, ed.selected).join(", ") || "select operators or rules first"}`}><Input value={name} onChange={(_, d) => setName(d.value)} /></Field>}
          {kind !== "extract" && <Field label={kind === "inline" ? "Into" : "Move to"}><Select value={other} onChange={(_, d) => setOther(d.value)}><option value="">(choose)</option>{projections.filter((p) => p.id !== ed.projection).map((p) => <option key={p.id} value={p.name}>{p.name}</option>)}</Select></Field>}
          {ed.notice && <MessageBar intent="error"><MessageBarBody>{ed.notice}</MessageBarBody></MessageBar>}
          {ed.pending && (
            <MessageBar intent="info"><MessageBarBody>
              The engine plans this patch: {ed.pending.added} elements added, {ed.pending.removed} removed, {ed.pending.changed} changed. The union of the output graphs stays the same. {ed.pending.items.slice(0, 10).map((i) => `${i.kind === "added" ? "+" : i.kind === "removed" ? "−" : "Δ"} ${i.type} ${i.id.slice(i.id.lastIndexOf("/") + 1)}`).join("; ")}
            </MessageBarBody></MessageBar>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={() => { dispatch({ type: "editor/dismiss", what: "pending" }); onClose(); }}>Close</Button>
          <Button onClick={() => run({ t: "refactor", req: req() })}>Show the patch</Button>
          <Button appearance="primary" disabled={!ed.pending} onClick={() => { run({ t: "applyRefactor" }); onClose(); }}>Apply to the draft</Button>
        </DialogActions>
      </DialogBody></DialogSurface>
    </Dialog>
  );
}

// ---- the view ----
export function EditorView() {
  const s = useStyles();
  const ed = useStore((x) => x.editor);
  const session = runtime.session;
  const [left, setLeft] = useState("palette");
  const [bottom, setBottom] = useState("problems");
  const [dialog, setDialog] = useState<"add" | "commit" | "refactor" | "new" | null>(null);
  const [manual, setManual] = useState<Record<string, Pos>>({});
  const [notes, setNotes] = useState<Note[]>([]);
  const [groups, setGroups] = useState<Group[]>([]);
  const [wireIdx, setWireIdx] = useState(0);
  const [marquee, setMarquee] = useState<{ a: Pos; b: Pos } | null>(null);
  const [zoom, setZoom] = useState(1);
  // how the projection is shown in the centre: the node canvas of this view, or the five notations of editors/Workbench on the same draft
  const [shown, setShown] = useState<"canvas" | "notations">("canvas");
  useEffect(() => { if (ed.status === "idle") dispatch({ type: "editor/open" }); }, [ed.status]);
  const key = `sattle-canvas-editor:${ed.projection}`;
  useEffect(() => { const v = store(key); setManual(v?.manual ?? {}); setNotes(v?.notes ?? []); setGroups(v?.groups ?? []); }, [key]);
  const persist = (m: Record<string, Pos>, n: Note[], g: Group[]) => { setManual(m); setNotes(n); setGroups(g); save(key, { manual: m, notes: n, groups: g }); };

  const dg = useMemo(() => (session && ed.projection ? session.catalog.projectionGraph(ed.projection) : null), [session, ed.projection, ed.version]); // eslint-disable-line react-hooks/exhaustive-deps
  const positions = useMemo(() => { if (!dg) return new Map<string, Pos>(); const auto = autoLayout(dg); for (const [id, p] of Object.entries(manual)) if (auto.has(id)) auto.set(id, p); return auto; }, [dg, manual]);
  const flagged = useMemo(() => {
    const m = new Map<string, "error" | "warning">();
    if (!dg || !session) return m;
    const ids = new Set(dg.nodes.map((n) => n.id));
    for (const i of incompleteOperators(session.graph, session.palette, ed.projection)) if (ids.has(i.op) && !m.has(i.op)) m.set(i.op, "warning");
    for (const d of ed.diagnostics) { const hit = session.catalog.nearest(d.focus, (id) => ids.has(id)); if (hit && m.get(hit) !== "error") m.set(hit, d.severity === "error" ? "error" : "warning"); }
    return m;
  }, [dg, ed.diagnostics, session, ed.version, ed.projection]);

  // a refactoring can remove the projection being edited (inline): move to a projection of the module that still exists
  useEffect(() => {
    if (!session || ed.status !== "ready" || !ed.projection || session.graph.entity(ed.projection)) return;
    const next = session.catalog.projections().find((p) => p.module === ed.module) ?? session.catalog.projections().find((p) => p.module !== "sattle.kernel");
    if (next) dispatch({ type: "editor/projection", module: next.module, projection: next.id });
  }, [session, ed.status, ed.projection, ed.module, ed.version]);
  if (ed.status === "error") return <div className={s.pad}><MessageBar intent="warning"><MessageBarBody>{ed.message}</MessageBarBody><MessageBarActions><Button onClick={() => dispatch({ type: "editor/open" })}>Try again</Button></MessageBarActions></MessageBar></div>;
  if (!session || ed.status !== "ready" || !dg) return <div className={s.pad}><Spinner label="Opening a draft on the engine…" /></div>;

  const g = session.graph, c = session.catalog;
  const stack = stackOf(c, ed.projection);
  const openProjection = (id: string) => { dispatch({ type: "editor/projection", module: moduleOfId(id), projection: id }); dispatch({ type: "editor/releases", module: moduleOfId(id) }); };
  const projections = c.projections().filter((p) => p.module !== "sattle.kernel");
  const deletable = ed.selected.filter((id) => { const e = g.entity(id); return e && e.type !== "Port" && e.type !== "Projection"; });
  const nodeById = new Map(dg.nodes.map((n) => [n.id, n]));
  const outPortOf = (id: string) => { const n = nodeById.get(id); return n && n.outputs.length ? portIdOf(n, n.outputs[0]) : null; };
  const targetsFor = (from: string) => [...g.entities()].filter((e: Entity) => e.type === "Port" && e.id.startsWith(`${ed.projection}/op:`) && canWire(g, from, e.id).ok).map((e) => e.id);
  const targets = ed.wiring ? targetsFor(ed.wiring) : [];

  const move = (ids: string[], dx: number, dy: number) => { const m = { ...manual }; for (const id of ids) { const p = positions.get(id); if (p) m[id] = { x: Math.max(0, p.x + dx), y: Math.max(0, p.y + dy) }; } persist(m, notes, groups); };
  const alignSel = (how: "left" | "top" | "column" | "row") => { const next = align(ed.selected, positions, how); const m = { ...manual }; for (const id of ed.selected) { const p = next.get(id); if (p) m[id] = p; } persist(m, notes, groups); };
  const announce = (text: string) => dispatch({ type: "ui/announce", text });

  const onKeyDown = (e: React.KeyboardEvent) => {
    const t = e.target as HTMLElement;
    // a control that has focus keeps its own keys: Enter presses a button, Delete is not "delete the selected operators" (dialogs are portals, but their events reach this handler)
    if (t.closest("input, textarea, select, button, a[href], [role=combobox], [role=button], [role=tab], [role=menuitem], [role=option], [role=dialog], [role=spinbutton], [role=switch], [role=checkbox]")) return;
    const mod = e.ctrlKey || e.metaKey;
    const k = e.key.toLowerCase();
    const stop = () => { e.preventDefault(); e.stopPropagation(); };
    if (marquee) {
      const step = e.shiftKey ? 20 : 80;
      const dx = k === "arrowright" ? step : k === "arrowleft" ? -step : 0, dy = k === "arrowdown" ? step : k === "arrowup" ? -step : 0;
      if (dx || dy) { stop(); const b = { x: Math.max(0, marquee.b.x + dx), y: Math.max(0, marquee.b.y + dy) }; setMarquee({ ...marquee, b }); announce(`${nodesInRect(dg, positions, marquee.a, b).length} operators in the region`); }
      else if (k === "enter") { stop(); const ids = nodesInRect(dg, positions, marquee.a, marquee.b); dispatch({ type: "editor/select", ids }); setMarquee(null); announce(`${ids.length} operators selected`); }
      else if (k === "escape") { stop(); setMarquee(null); announce("Region selection cancelled"); }
      return;
    }
    if (k === "l" && !mod) {
      stop();
      const loop = (dg.loops ?? []).find((l) => l.fixpoint === ed.selected[0] || l.members.includes(ed.selected[0]));
      if (loop) { dispatch({ type: "editor/select", ids: [loop.fixpoint, ...loop.members] }); announce(`Loop selected: the fixpoint and ${loop.members.length} step operators`); }
      else announce("Select a fixpoint or one of its step operators first");
      return;
    }
    if (k === "m" && !mod) {
      stop();
      const at = positions.get(ed.selected[0] ?? "") ?? { x: 20, y: 20 };
      setMarquee({ a: at, b: { x: at.x + NODE_W, y: at.y + 60 } });
      announce("Region selection. Arrow keys resize the region, Enter selects what it touches, Escape cancels.");
      return;
    }
    if (ed.wiring) {
      if (k === "escape") { stop(); dispatch({ type: "editor/wiring", from: null }); announce("Connecting cancelled"); }
      else if (k === "arrowdown" || k === "arrowright" || k === "arrowup" || k === "arrowleft") { stop(); const n = targets.length; if (n) { const i = (wireIdx + (k === "arrowdown" || k === "arrowright" ? 1 : -1) + n) % n; setWireIdx(i); announce(`Connect to ${c.name(targets[i].slice(0, targets[i].lastIndexOf("/")))} input ${targets[i].slice(targets[i].lastIndexOf(":") + 1)}`); } }
      else if (k === "enter") { stop(); if (targets[wireIdx]) run({ t: "wire", from: ed.wiring, to: targets[wireIdx] }); dispatch({ type: "editor/wiring", from: null }); }
      return;
    }
    if (mod && k === "z") { stop(); dispatch({ type: e.shiftKey ? "editor/redo" : "editor/undo" }); }
    else if (mod && k === "y") { stop(); dispatch({ type: "editor/redo" }); }
    else if (mod && k === "c") { stop(); run({ t: "copy", ids: ed.selected.filter((id) => g.entity(id)?.type !== "Port" && g.entity(id)?.type !== "Rule") }); }
    else if (mod && k === "v") { stop(); run({ t: "paste" }); }
    else if (mod && k === "d") { stop(); run({ t: "copy", ids: ed.selected }); run({ t: "paste" }); }
    else if (mod && k === "a") { stop(); dispatch({ type: "editor/select", ids: dg.nodes.map((n) => n.id) }); }
    else if (mod && k === "s") { stop(); setDialog("commit"); }
    else if (mod && k === "g") { stop(); setDialog("refactor"); }
    else if (k === "a" && !mod) { stop(); setDialog("add"); }
    else if (k === "w" && !mod) { const from = ed.selected[0] ? outPortOf(ed.selected[0]) : null; stop(); if (from) { dispatch({ type: "editor/wiring", from }); setWireIdx(0); const n = targetsFor(from).length; announce(n ? `Connecting. ${n} possible inputs. Arrow keys choose, Enter connects, Escape cancels.` : "No input can take this output"); } else announce("Select an operator with an output first"); }
    else if (k === "delete" || k === "backspace") { stop(); if (deletable.length) run({ t: "delete", ids: deletable }); }
    else if (k === "escape") { stop(); dispatch({ type: "editor/select", ids: [] }); }
    else if (k === "enter") { stop(); document.getElementById("editor-props")?.focus(); }
    else if (k.startsWith("arrow")) {
      stop();
      const dx = k === "arrowright" ? 1 : k === "arrowleft" ? -1 : 0, dy = k === "arrowdown" ? 1 : k === "arrowup" ? -1 : 0;
      if (e.shiftKey && ed.selected.length) { const step = e.altKey ? 1 : 10; move(ed.selected, dx * step, dy * step); return; }
      const from = positions.get(ed.selected[0] ?? "") ?? { x: -1, y: -1 };
      let best: string | null = null, bd = Infinity;
      for (const n of dg.nodes) {
        if (n.id === ed.selected[0]) continue;
        const p = positions.get(n.id)!;
        const ax = (p.x - from.x) * dx + (p.y - from.y) * dy, off = Math.abs((p.x - from.x) * dy) + Math.abs((p.y - from.y) * dx);
        if (ax > 0 && ax + off * 2 < bd) { bd = ax + off * 2; best = n.id; }
      }
      if (!ed.selected.length) best = dg.nodes[0]?.id ?? null;
      if (best) { dispatch({ type: "editor/select", ids: [best] }); announce(`${nodeById.get(best)!.opKind ?? nodeById.get(best)!.kind} ${nodeById.get(best)!.title}`); setTimeout(() => (document.querySelector(`[data-node="${CSS.escape(best!)}"]`) as HTMLElement | null)?.focus(), 0); }
    }
  };

  return (
    <div className={s.root} onKeyDown={onKeyDown}>
      <aside className={s.side} aria-label="Operators and versions">
        <TabList size="small" selectedValue={left} onTabSelect={(_, d) => setLeft(String(d.value))} aria-label="Left panel"><Tab value="palette">Palette</Tab><Tab value="outline">Outline</Tab></TabList>
        {left === "palette" ? <PalettePanel /> : <Outline />}
      </aside>
      <section className={s.center} aria-label="Projection editor">
        <div className={s.bar}>
          <Field label="Projection" orientation="horizontal"><Select value={ed.projection} onChange={(_, d) => { const p = projections.find((x) => x.id === d.value)!; dispatch({ type: "editor/projection", module: p.module, projection: p.id }); dispatch({ type: "editor/releases", module: p.module }); }}>
            {projections.map((p) => <option key={p.id} value={p.id}>{p.module}: {p.name}</option>)}</Select></Field>
          {(() => { const p = projections.find((x) => x.id === ed.projection); return p ? <DirectionBadge direction={p.direction} /> : null; })()}
          <Badge appearance="outline">draft {ed.draft} · rev {ed.revision} · base {ed.base}</Badge>
          {ed.busy && <Spinner size="tiny" label={ed.busy} labelPosition="after" />}
          <span className={s.grow} />
          <TabList size="small" selectedValue={shown} onTabSelect={(_, d) => setShown(d.value as "canvas" | "notations")} aria-label="How the projection is shown"><Tab value="canvas">Canvas</Tab><Tab value="notations">Notations</Tab></TabList>
          <Button size="small" onClick={() => setZoom(Math.max(0.5, zoom - 0.1))} aria-label="Zoom out">−</Button><Text size={200}>{Math.round(zoom * 100)}%</Text><Button size="small" onClick={() => setZoom(Math.min(2, zoom + 0.1))} aria-label="Zoom in">+</Button>
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
        <div className={s.tools}><Toolbar size="small" aria-label="Editor commands">
          <Tooltip2 label="Undo (Ctrl+Z)"><ToolbarButton aria-label={`Undo${ed.undo[0] ? ` ${ed.undo[0]}` : ""}`} icon={<ArrowUndo20Regular />} disabled={!ed.undo.length} onClick={() => dispatch({ type: "editor/undo" })}>Undo</ToolbarButton></Tooltip2>
          <ToolbarButton aria-label={`Redo${ed.redo[0] ? ` ${ed.redo[0]}` : ""}`} icon={<ArrowRedo20Regular />} disabled={!ed.redo.length} onClick={() => dispatch({ type: "editor/redo" })}>Redo</ToolbarButton>
          <ToolbarDivider />
          <ToolbarButton icon={<Add20Regular />} onClick={() => setDialog("add")}>Add</ToolbarButton>
          <ToolbarButton onClick={() => setDialog("new")}>New projection</ToolbarButton>
          <ToolbarButton icon={<Delete20Regular />} disabled={!deletable.length} onClick={() => run({ t: "delete", ids: deletable })}>Delete</ToolbarButton>
          <ToolbarButton icon={<Copy20Regular />} disabled={!ed.selected.length} onClick={() => run({ t: "copy", ids: ed.selected })}>Copy</ToolbarButton>
          <ToolbarButton icon={<ClipboardPaste20Regular />} onClick={() => run({ t: "paste" })}>Paste</ToolbarButton>
          <Menu><MenuTrigger disableButtonEnhancement><ToolbarButton icon={<AlignLeft20Regular />} disabled={ed.selected.length < 2}>Align</ToolbarButton></MenuTrigger><MenuPopover><MenuList>
            <MenuItem onClick={() => alignSel("left")}>Left edges</MenuItem><MenuItem onClick={() => alignSel("top")}>Top edges</MenuItem><MenuItem onClick={() => alignSel("column")}>Stack in a column</MenuItem><MenuItem onClick={() => alignSel("row")}>Spread in a row</MenuItem></MenuList></MenuPopover></Menu>
          <ToolbarButton icon={<Comment20Regular />} onClick={() => persist(manual, [...notes, { id: `n${Date.now()}`, x: 20, y: 20, text: "Comment" }], groups)}>Comment</ToolbarButton>
          <ToolbarButton disabled={ed.selected.length < 2} onClick={() => persist(manual, notes, [...groups, { id: `g${Date.now()}`, label: `Group ${groups.length + 1}`, members: ed.selected }])}>Group</ToolbarButton>
          <ToolbarDivider />
          <ToolbarButton icon={<FolderArrowRight20Regular />} onClick={() => setDialog("refactor")}>Refactor</ToolbarButton>
          <ToolbarButton icon={<Eye20Regular />} onClick={() => { setBottom("versions"); dispatch({ type: "editor/preview" }); }}>Preview</ToolbarButton>
          <ToolbarButton icon={<Save20Regular />} appearance="primary" onClick={() => setDialog("commit")}>Commit…</ToolbarButton>
          <ToolbarButton onClick={() => dispatch({ type: "editor/discard" })}>Discard draft</ToolbarButton>
          <ToolbarButton aria-label="Keyboard shortcuts" icon={<Keyboard20Regular />} onClick={() => dispatch({ type: "ui/dialog", dialog: "shortcuts" })} />
        </Toolbar></div>
        <div style={{ position: "relative", minHeight: 0 }}>
          {shown === "notations" ? <Notations /> : <EditorCanvas dg={dg} positions={positions} selected={ed.selected} flagged={flagged} notes={notes} groups={groups} zoom={zoom}
            keyMarquee={marquee} wiring={ed.wiring} wireTarget={ed.wiring ? targets[wireIdx] ?? null : null} canWire={(a, b) => canWire(g, a, b).ok}
            onSelect={(ids) => dispatch({ type: "editor/select", ids })} onMove={(ids, dx, dy) => move(ids, dx, dy)} onWire={(from, to) => run({ t: "wire", from, to })}
            onNoteMove={(id, x, y) => persist(manual, notes.map((n) => (n.id === id ? { ...n, x, y } : n)), groups)} onNoteEdit={(id, text) => persist(manual, notes.map((n) => (n.id === id ? { ...n, text } : n)), groups)} />}
          {ed.notice && <div style={{ position: "absolute", left: 8, right: 8, bottom: 8 }}><MessageBar intent="warning"><MessageBarBody>{ed.notice}</MessageBarBody><MessageBarActions><Button size="small" onClick={() => dispatch({ type: "editor/dismiss", what: "notice" })}>Dismiss</Button></MessageBarActions></MessageBar></div>}
          {ed.committed && <div style={{ position: "absolute", left: 8, right: 8, top: 8 }}><MessageBar intent="success"><MessageBarBody>{ed.committed}</MessageBarBody></MessageBar></div>}
        </div>
        <div className={s.bottom}>
          <TabList size="small" selectedValue={bottom} onTabSelect={(_, d) => setBottom(String(d.value))} aria-label="Draft details">
            <Tab value="problems">Problems{ed.diagnostics.length ? ` (${ed.diagnostics.length})` : ""}</Tab><Tab value="text">Text</Tab><Tab value="definitions">Definitions</Tab><Tab value="versions">Versions and preview</Tab>
          </TabList>
          {bottom === "problems" ? <Problems /> : bottom === "text" ? <TextPanel /> : bottom === "definitions" ? <Definitions session={session} module={ed.module} /> : <Projects />}
        </div>
      </section>
      <aside className={s.right} aria-label="Properties"><Properties session={session} selected={ed.selected} projection={ed.projection} diagnostics={ed.diagnostics} /></aside>
      <QuickAdd open={dialog === "add"} onClose={() => setDialog(null)} />
      <CommitDialog open={dialog === "commit"} onClose={() => setDialog(null)} />
      <RefactorDialog open={dialog === "refactor"} onClose={() => setDialog(null)} />
      <NewProjectionDialog open={dialog === "new"} onClose={() => setDialog(null)} />
    </div>
  );
}

import { Tooltip } from "@fluentui/react-components";
const Tooltip2 = ({ label, children }: { label: string; children: React.ReactElement }) => <Tooltip content={label} relationship="description">{children}</Tooltip>;
