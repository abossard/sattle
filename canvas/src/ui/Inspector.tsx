// Inspector of the selected element: change in this revision, properties, connections, shared identity and lineage, telemetry series.
import { Badge, Link, MessageBar, MessageBarBody, Subtitle2, Table, TableBody, TableCell, TableRow, Text, Tree, TreeItem, TreeItemLayout, makeStyles, tokens } from "@fluentui/react-components";
import { propDiff } from "../core/diff";
import { valueText, type Entity, type Rel } from "../core/model";
import { sparkPoints } from "../core/telemetry";
import { countAtOrBefore, formatTime } from "../core/time-axis";
import { formatValue } from "../app/telemetry-hub";
import type { Workspace } from "../app/workspace";
import { dispatch, useStore } from "../state/store";
import type { Selection } from "../state/types";
import { styleOf } from "../state/ws-ops";
import { roleColor, TypeGlyph, useWorkspace } from "./common";
import { WhyState } from "./WhyState";

const useStyles = makeStyles({ col: { display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalS, padding: tokens.spacingHorizontalM, overflow: "auto" }, mono: { fontFamily: tokens.fontFamilyMonospace, wordBreak: "break-all" } });
const isRel = (x: Entity | Rel): x is Rel => "source" in x;

const go = (graph: string, id: string, kind: Selection["kind"]) => {
  dispatch({ type: "select/element", selection: { graph, kind, id } });
  dispatch({ type: "pane/command", graph, command: "focus" });
};

const ElementLink = ({ ws, graph, id, edge = false }: { ws: Workspace; graph: string; id: string; edge?: boolean }) => {
  const g = ws.histories.get(graph)?.graph;
  const stub = g?.stubSlot.get(id);
  const el = edge ? g?.rel(id) : g?.entity(id) ?? (stub !== undefined ? g?.nodes[stub] : undefined);
  const text = el && g ? g.label(el) : id;
  return <Link as="button" title={id} onClick={() => go(graph, id, edge ? "relationship" : "entity")}>{text.length > 28 ? `${text.slice(0, 27)}…` : text}</Link>;
};

const KV = ({ rows }: { rows: [string, React.ReactNode][] }) => (
  <Table size="extra-small" aria-label="Properties"><TableBody>{rows.map(([k, v]) => <TableRow key={k}><TableCell><Text weight="semibold">{k}</Text></TableCell><TableCell>{v}</TableCell></TableRow>)}</TableBody></Table>
);

export function Inspector() {
  const s = useStyles();
  const sel = useStore((x) => x.selection);
  const ws = useWorkspace(sel?.graph);
  useStore((x) => x.time.now);
  if (!ws) return null;
  if (!sel) return <div className={s.col}><Subtitle2 as="h2">Inspector</Subtitle2><Text>Select a node or edge. The same entity is highlighted in every graph of the space.</Text></div>;
  const hist = ws.histories.get(sel.graph);
  if (!hist) return null;
  const pane = ws.panes.get(sel.graph);
  const stubSlot = sel.kind === "entity" ? hist.graph.stubSlot.get(sel.id) : undefined;
  const el = sel.kind === "entity" ? hist.graph.entity(sel.id) ?? (stubSlot !== undefined ? hist.graph.nodes[stubSlot] : undefined) : hist.graph.rel(sel.id);
  const reference = stubSlot !== undefined ? ws.references.get(sel.graph)?.get(sel.id) : undefined;
  const change = pane?.highlight ? (sel.kind === "entity" ? pane.highlight.entities.get(sel.id) : pane.highlight.relationships.get(sel.id)) : undefined;
  const subject = el ?? change?.before ?? null;
  const role = ws.graphInfo(sel.graph)?.role ?? "";
  const rel = ws.related(sel);
  const li = ws.lineageInfo.get(`${sel.graph}|${sel.kind}|${sel.id}`);
  const partial = ws.partialOf(sel);
  const g = hist.graph;
  const slot = stubSlot ?? (el && !isRel(el) ? g.nodeSlot.get(el.id) : undefined);
  const groups = new Map<string, { dir: string; other: string }[]>();
  if (slot !== undefined) {
    for (const es of g.out[slot]) { const r = g.edges[es]!; groups.set(r.type, [...(groups.get(r.type) ?? []), { dir: "→", other: r.target }]); }
    for (const es of g.inn[slot]) { const r = g.edges[es]!; groups.set(r.type, [...(groups.get(r.type) ?? []), { dir: "←", other: r.source }]); }
  }
  return (
    <div className={s.col}>
      <Subtitle2 as="h2">{subject ? <><TypeGlyph {...styleOf(ws, sel.graph, subject.type)} /> {g.label(subject)}</> : sel.id}</Subtitle2>
      <div><Badge color={roleColor(role)} appearance="outline">{sel.graph}</Badge> <code className={s.mono}>{sel.id}</code></div>
      {!el && change?.kind === "removed" && <Text>Removed in this revision. Showing the state before the change.</Text>}
      {el && isRel(el) && <Text><ElementLink ws={ws} graph={sel.graph} id={el.source} /> → <ElementLink ws={ws} graph={sel.graph} id={el.target} /></Text>}
      {change && (
        <>
          <Subtitle2 as="h3">{change.kind === "added" ? "+ added" : change.kind === "removed" ? "− removed" : "Δ changed"} in this revision</Subtitle2>
          {propDiff(change.before, change.after).length === 0 ? <Text>No property differences.</Text> : (
            <KV rows={propDiff(change.before, change.after).map((r) => [r.property, <>{r.before !== null && <del title="before">{r.before}</del>}{r.before !== null && r.after !== null && " → "}{r.after !== null && <ins title="after">{r.after}</ins>}</>])} />
          )}
        </>
      )}
      {stubSlot !== undefined && (
        <>
          <Subtitle2 as="h3">Reference: not in this graph</Subtitle2>
          <Text>{reference?.state === "inOtherGraph" && reference.graph ? `The entity is in graph ${reference.graph}.` : reference ? "No graph holds this entity: relationships name it, nothing created it." : "A relationship names this entity and the graph does not hold it."}</Text>
          {reference?.state === "inOtherGraph" && reference.graph && <Link as="button" onClick={() => { dispatch({ type: "ui/activeGraph", graph: reference.graph! }); go(reference.graph!, sel.id, "entity"); }}>Open it in {reference.graph}</Link>}
        </>
      )}
      {el && <><Subtitle2 as="h3">Properties</Subtitle2>{Object.keys(el.props).length ? <KV rows={Object.entries(el.props).map(([k, v]) => [k, valueText(v)])} /> : <Text>No properties.</Text>}</>}
      {slot !== undefined && (
        <>
          <Subtitle2 as="h3">Connections ({g.out[slot].length + g.inn[slot].length})</Subtitle2>
          <Tree aria-label="Connections by relationship type">
            {[...groups].sort().map(([type, list]) => (
              <TreeItem key={type} itemType="branch" value={type}>
                <TreeItemLayout>{type} ({list.length})</TreeItemLayout>
                <Tree>{list.slice(0, 50).map((x, i) => <TreeItem key={i} itemType="leaf" value={`${type}${i}`} onClick={() => go(sel.graph, x.other, "entity")}><TreeItemLayout>{x.dir} {g.label(g.entity(x.other) ?? { id: x.other, props: {} })}</TreeItemLayout></TreeItem>)}</Tree>
              </TreeItem>
            ))}
          </Tree>
        </>
      )}
      {sel.kind === "entity" && ws.graphInfo(sel.graph)?.tree && <WhyState ws={ws} graph={sel.graph} id={sel.id} />}
      <Subtitle2 as="h3">Shared identity and lineage</Subtitle2>
      {[...rel.identity].map(([graph, keys]) => <Text key={graph}><Badge appearance="outline" color={roleColor(ws.graphInfo(graph)?.role ?? "")}>{graph}</Badge> {[...keys].slice(0, 6).map((k) => <ElementLink key={k} ws={ws} graph={graph} id={k.slice(2)} edge={k.startsWith("e:")} />)}</Text>)}
      {(partial.below.length > 0 || partial.aboveIn.length > 0 || partial.cutOff) && (
        <MessageBar intent="warning"><MessageBarBody>
          The highlight is partial.{" "}
          {partial.below.length > 0 && `What ${partial.below.length === 1 ? "one element" : `${partial.below.length} elements`} below ${partial.below.length === 1 ? "is" : "are"} made of is known only by its group key (${[...new Set(partial.below.map((x) => x.graph))].join(", ")}). `}
          {partial.aboveIn.length > 0 && `${partial.aboveIn.join(", ")} read${partial.aboveIn.length === 1 ? "s" : ""} this through an aggregate, so what it is part of there is not listed. `}
          {partial.cutOff && "The lookups stopped at the limit for one selection. "}
        </MessageBarBody></MessageBar>
      )}
      {rel.sourceList.length > 0 && <><Text>Derived from:</Text>{rel.sourceList.slice(0, 20).map((x) => <Text key={x.graph + x.key}><Badge appearance="outline" color={roleColor(ws.graphInfo(x.graph)?.role ?? "")}>{x.graph}</Badge> <ElementLink ws={ws} graph={x.graph} id={x.key.slice(2)} edge={x.key.startsWith("e:")} /></Text>)}</>}
      {li && (
        <KV rows={[["projection", li.projection ?? ""], ["emitted by", li.sink ?? ""], ...(li.rule ? [["rule", `${li.rule}${li.direction ? ` (${li.direction})` : ""}`] as [string, string]] : []), ["operators", li.operators.join(" → ")], ["sources", `${li.sources.length} of ${li.sourceCount}${li.complete ? "" : " (not complete)"}`], ...(li.groupKey ? [["group key", JSON.stringify(li.groupKey)] as [string, string]] : [])]} />
      )}
      {!rel.identity.size && !rel.sourceList.length && <Text>{ws.corr.size ? "This element has no counterpart in another graph." : "This source provides no correspondence data. Only entities with the same id are linked."}</Text>}
      <Series ws={ws} sel={sel} />
    </div>
  );
}

function Series({ ws, sel }: { ws: Workspace; sel: Selection }) {
  const hub = ws.hubs.get(sel.graph);
  if (!hub?.hasData) return null;
  const g = ws.histories.get(sel.graph)!.graph;
  const target = sel.kind === "entity" ? "node" : "edge";
  const slot = target === "node" ? g.nodeSlot.get(sel.id) : g.edgeSlot.get(sel.id);
  const meters = hub.store.metersOf(target);
  if (slot === undefined || meters.length === 0) return <><Subtitle2 as="h3">Telemetry</Subtitle2><Text>No telemetry for this {target}.</Text></>;
  const readings = new Map(hub.readings(target, slot).map((r) => [r.meter.id, r]));
  const W = 190, H = 30;
  return (
    <>
      <Subtitle2 as="h3">Telemetry</Subtitle2>
      {meters.map((m) => {
        const rd = readings.get(m.id);
        const series = hub.store.series(target, m.id, slot);
        const i = Math.max(0, countAtOrBefore(series.times, hub.time) - 1);
        const wy = m.warn !== undefined ? H - Math.min(1, (m.warn - m.min) / (m.max - m.min)) * H : null;
        return (
          <div key={m.id}>
            <Text weight="semibold">{m.label}</Text> {rd ? <Badge color={rd.warn ? "danger" : "informative"}>{rd.warn ? "! " : ""}{formatValue(m, rd.value)}</Badge> : <Text size={200}>no value</Text>}
            <svg viewBox={`0 0 ${W} ${H}`} width={W} height={H} role="img" aria-label={`${m.label} over ${series.values.length} ticks${rd ? `, now ${formatValue(m, rd.value)}` : ""}`}>
              {wy !== null && <line x1="0" x2={W} y1={wy} y2={wy} stroke={tokens.colorPaletteRedForeground1} strokeDasharray="3 3" />}
              <polyline points={sparkPoints(series.values, W, H, m.min, m.max)} fill="none" stroke={tokens.colorBrandForeground1} strokeWidth="1.5" />
              {series.ticks.length > 1 && <line x1={(i / (series.ticks.length - 1)) * W} x2={(i / (series.ticks.length - 1)) * W} y1="0" y2={H} stroke="currentColor" />}
            </svg>
            <Text size={200} block>{m.warn !== undefined ? `dashed line: threshold ${formatValue(m, m.warn)}` : "no threshold"}</Text>
          </div>
        );
      })}
      <Text size={200}>8-bit quantized: error under 0.4% of each metric range. Window {formatTime(hub.firstTime)} to {formatTime(hub.lastTime)}.</Text>
    </>
  );
}
