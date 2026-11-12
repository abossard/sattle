// The data graph view and the graph space view: filters and elements on the left, canvases in the middle, inspector on the right.
import { Badge, Button, Field, Popover, PopoverSurface, PopoverTrigger, Select, Tab, TabList, Text, makeStyles, tokens } from "@fluentui/react-components";
import { useMemo, useState } from "react";
import { dispatch, useStore } from "../state/store";
import { CanvasHost } from "./CanvasHost";
import { DocumentView } from "./DocumentView";
import { TreeView } from "./TreeView";
import { hasHierarchyEdges, hasTreeRows, tileRows, treeOf, treeRowCount } from "../app/tree-model";
import { ROLE_GLYPH } from "../app/color-by";
import { edgelessNotice } from "../app/graph-view-model";
import { countsByType, describeGraph, isolatedNodes, purposesOf, tileKind, tileThinsIsolated, typesHeld } from "../app/tile";
import { DirectionBadge, TypeGlyph, roleColor, useGraphSummary, useWorkspace } from "./common";
import { styleOf } from "../state/ws-ops";
import { ElementsPanel } from "./Elements";
import { FiltersPanel } from "./Filters";
import { Inspector } from "./Inspector";
import { Timeline } from "./Timeline";
import { KIND_WORD, KindSample, kindsOf } from "./EdgeKinds";

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateColumns: "minmax(220px, 300px) minmax(0, 1fr) minmax(240px, 340px)", minHeight: 0, height: "100%", "@media (max-width: 1000px)": { gridTemplateColumns: "1fr", gridTemplateRows: "auto minmax(360px, 1fr) auto", height: "auto" } },
  side: { overflow: "auto", minHeight: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}` },
  right: { overflow: "auto", minHeight: 0, borderLeft: `1px solid ${tokens.colorNeutralStroke2}` },
  center: { display: "grid", gridTemplateRows: "auto minmax(0, 1fr) auto", minHeight: 0 },
  bar: { display: "flex", alignItems: "center", columnGap: tokens.spacingHorizontalS, flexWrap: "wrap", padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}` },
  grow: { flexGrow: 1 },
  grid: { display: "grid", gap: "6px", minHeight: 0, overflow: "auto", padding: "6px", alignContent: "start", gridAutoRows: "440px" },
  pane: { display: "grid", gridTemplateRows: "auto minmax(0, 1fr)", minHeight: 0, overflow: "hidden", border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium },
  activePane: { border: `2px solid ${tokens.colorBrandStroke1}` },
  caption: { display: "-webkit-box", WebkitLineClamp: 3, WebkitBoxOrient: "vertical", overflow: "hidden" },
  head: { display: "grid", rowGap: "2px", padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}` },
  headRow: { display: "flex", alignItems: "center", columnGap: tokens.spacingHorizontalS, flexWrap: "wrap" },
  tileBody: { minHeight: 0, overflow: "hidden", position: "relative", backgroundColor: tokens.colorNeutralBackground1 },
  fade: { position: "absolute", left: 0, right: 0, bottom: 0, height: "28px", pointerEvents: "none", backgroundImage: `linear-gradient(transparent, ${tokens.colorNeutralBackground1})` },
  note: { margin: 0, padding: tokens.spacingHorizontalM, borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground3, border: `1px solid ${tokens.colorNeutralStroke2}` },
  list: { margin: 0, paddingLeft: tokens.spacingHorizontalXL, display: "grid", rowGap: "2px" },
  notice: { display: "grid", gridTemplateColumns: "minmax(0, 1fr)", alignContent: "start", rowGap: tokens.spacingVerticalS, padding: tokens.spacingHorizontalM, overflow: "auto", height: "100%", boxSizing: "border-box" },
});

/** How to read the canvas; the edge kinds are the ones the graph's ontology declares, each with the relationship types that have it. */
export function Legend({ graph }: { graph: string }) {
  const ws = useWorkspace();
  const kinds = kindsOf(ws?.graphInfo(graph)?.edgeKinds);
  return (
    <Popover><PopoverTrigger disableButtonEnhancement><Button size="small">Legend</Button></PopoverTrigger>
      <PopoverSurface aria-label="How to read the canvas"><div style={{ display: "grid", rowGap: 4 }}>
        <Text>+ added: solid ring</Text><Text>Δ changed: double ring</Text><Text>− removed: dashed ghost</Text><Text>Same entity elsewhere: pink ring</Text><Text>Source element: dashed ring</Text><Text>Shape and label give the type; color repeats it</Text>
        {kinds.length > 0 && <Text weight="semibold">Edges</Text>}
        {kinds.map(({ kind, types }) => (
          <div key={kind} style={{ display: "flex", alignItems: "center", columnGap: 8 }}><KindSample kind={kind} /><Text>{KIND_WORD[kind].word}: {types.map((t) => t.slice(t.lastIndexOf(".") + 1)).join(", ")}. {KIND_WORD[kind].help}</Text></div>
        ))}
      </div></PopoverSurface>
    </Popover>
  );
}

function SidePanel({ graph }: { graph: string }) {
  const [tab, setTab] = useState("filters");
  return (
    <>
      <TabList size="small" selectedValue={tab} onTabSelect={(_, d) => setTab(String(d.value))} aria-label="Left panel"><Tab value="filters">Filters</Tab><Tab value="elements">Elements</Tab></TabList>
      {tab === "filters" ? <FiltersPanel graph={graph} /> : <ElementsPanel graph={graph} />}
    </>
  );
}

export function DataView() {
  const s = useStyles();
  const ws = useWorkspace();
  const graphs = useStore((x) => x.graphs);
  const active = useStore((x) => x.activeGraph);
  const info = ws?.graphInfo(active);
  const sum = useGraphSummary(active);
  const [shown, setShown] = useState<Record<string, string>>({});
  if (!ws || !info) return null;
  const isDoc = info.document === true && !!ws.source.document;
  // a graph whose ontology marks a hierarchy opens as a tree, a document graph as a document; the graph is the other tab
  const hist = ws.histories.get(active);
  const isTree = !isDoc && !!info.tree && !!hist && hasTreeRows(hist.graph, info.tree);
  const first = isDoc ? "document" : "tree";
  const mode = isDoc || isTree ? shown[active] ?? first : "graph";
  return (
    <div className={s.root}>
      <aside className={s.side} aria-label="Search, filters and element list"><SidePanel graph={active} /></aside>
      <section className={s.center} aria-label="Graph">
        <div className={s.bar}>
          <Field label="Graph" orientation="horizontal"><Select style={{ width: 220 }} value={active} onChange={(_, d) => dispatch({ type: "ui/activeGraph", graph: d.value })}>{graphs.map((g) => <option key={g.id} value={g.id}>{g.id} ({g.role})</option>)}</Select></Field>
          <Badge color={roleColor(info.role)} appearance="outline">{info.role}</Badge>
          {info.direction && <DirectionBadge direction={info.direction} />}
          <Badge appearance="outline">ontology {info.ontology.reference.id}@{info.ontology.reference.version}</Badge>
          {sum && <Text size={200}>{sum.nodes.toLocaleString()} nodes · {sum.edges.toLocaleString()} edges</Text>}
          <span className={s.grow} />
          {(isDoc || isTree) && <TabList size="small" selectedValue={mode} onTabSelect={(_, d) => setShown({ ...shown, [active]: String(d.value) })} aria-label="How to show this graph"><Tab value={first}>{isDoc ? "Document" : "Tree"}</Tab><Tab value="graph">Graph</Tab></TabList>}
          {mode === "graph" && <><Button size="small" onClick={() => dispatch({ type: "pane/command", graph: active, command: "fit" })}>Fit</Button><Legend graph={active} /></>}
        </div>
        {mode === "document" ? <DocumentView graph={active} /> : mode === "tree" ? <TreeView graph={active} /> : <CanvasHost graph={active} />}
        <Timeline graph={active} />
      </section>
      <aside className={s.right} aria-label="Inspector"><Inspector /></aside>
    </div>
  );
}

/**
 * A hierarchy as the rows a tile has room for: where something is wrong, worst first, else the top of the tree. Each row has its state as a word and
 * a glyph, and how many links to other parts it has; the rest is behind "Open".
 */
function TreeTile({ graph }: { graph: string }) {
  const s = useStyles();
  const ws = useWorkspace(graph);
  const version = useGraphSummary(graph)?.version;
  const info = ws?.graphInfo(graph);
  const g = ws?.histories.get(graph)?.graph;
  // the tree the Tree tab opens on: by cause when the graph has a roll-up
  const { rows, more, wrong, total } = useMemo(() => {
    const m = g && info ? treeOf(g, info)?.model : undefined;
    if (!m) return { rows: [], more: 0, wrong: 0, total: 0 };
    return { ...tileRows(m), wrong: [...m.nodes.values()].filter((n) => (n.rolled ?? n.own)?.role === "warning" || (n.rolled ?? n.own)?.role === "danger").length, total: m.nodes.size };
  }, [g, info, version]); // eslint-disable-line react-hooks/exhaustive-deps
  if (!rows.length) return null;
  return (
    <div className={s.notice} tabIndex={0} role="region" aria-label={`Top of the hierarchy of ${graph}`}>
      <Text size={200} role="status">{wrong ? `${wrong} of ${total} where something is wrong, worst first` : `${total} elements`}</Text>
      <ul className={s.list} style={{ listStyle: "none", paddingLeft: 0 }} aria-label={`The top of the hierarchy of ${graph}`}>
        {rows.map((r) => {
          const st = r.node.rolled ?? r.node.own;
          return (
            <li key={r.key} style={{ paddingLeft: (r.level - 1) * 14, display: "flex", columnGap: 6, alignItems: "baseline" }}>
              <Text size={200} weight={st && (st.role === "danger" || st.role === "warning") ? "semibold" : "regular"}>{r.node.origin ? "⚑ " : ""}{r.node.label}</Text>
              <Text size={100} style={{ color: tokens.colorNeutralForeground3 }}>{r.node.type}</Text>
              {st && <Text size={200}>{ROLE_GLYPH[st.role]} {st.value}</Text>}
              {r.node.cross + r.node.supporting > 0 && <Text size={100} style={{ color: tokens.colorNeutralForeground3 }}>{r.node.cross + r.node.supporting} {r.node.cross + r.node.supporting === 1 ? "link" : "links"}</Text>}
            </li>
          );
        })}
      </ul>
      {more > 0 && <Text size={200}>and {more} more</Text>}
    </div>
  );
}

/** The master as a count of what was observed: its types with how many of each, and what relates them. */
function SummaryTile({ graph }: { graph: string }) {
  const s = useStyles();
  const ws = useWorkspace(graph);
  const version = useGraphSummary(graph)?.version;
  const g = ws?.histories.get(graph)?.graph;
  const counts = useMemo(() => (g ? countsByType(g) : null), [g, version]); // eslint-disable-line react-hooks/exhaustive-deps
  if (!ws || !counts) return null;
  const word = (t: string) => t.slice(t.lastIndexOf(".") + 1);
  const part = (list: [string, number][], max: number) => ({ shown: list.slice(0, max), more: list.length - Math.min(list.length, max) });
  const ents = part(counts.entities, 12), rels = part(counts.relationships, 8);
  return (
    <div className={s.notice} tabIndex={0} role="region" aria-label={`What ${graph} holds`}>
      <Text size={200} weight="semibold">What was observed</Text>
      <ul className={s.list} style={{ listStyle: "none", paddingLeft: 0 }} aria-label={`Elements of ${graph} by type`}>
        {ents.shown.map(([t, n]) => <li key={t} style={{ display: "flex", columnGap: 8, alignItems: "center" }}><TypeGlyph {...styleOf(ws, graph, t)} /><Text size={200}>{word(t)}</Text><Text size={200} style={{ marginLeft: "auto" }}>{n}</Text></li>)}
      </ul>
      {ents.more > 0 && <Text size={200}>and {ents.more} more types</Text>}
      <Text size={200} weight="semibold">Related by</Text>
      <Text size={200}>{rels.shown.map(([t, n]) => `${word(t)} ${n}`).join(" · ")}{rels.more > 0 ? ` · and ${rels.more} more` : ""}</Text>
    </div>
  );
}

/** A graph with nodes and no edges: what the engine says about it and what it holds, instead of a ring of unconnected dots. */
function NoticeTile({ graph }: { graph: string }) {
  const s = useStyles();
  const ws = useWorkspace(graph);
  const sum = useGraphSummary(graph);
  const info = ws?.graphInfo(graph);
  const g = ws?.histories.get(graph)?.graph;
  if (!ws || !info || !sum || !g) return null;
  const notice = edgelessNotice(info.role, sum.nodes, sum.edges, info.ontology.relationshipTypes.map((t) => t.id.value));
  const held = [...g.entities()].slice(0, 12).map((e) => g.label(e));
  return (
    <div className={s.notice} tabIndex={0} role="region" aria-label={`Why ${graph} has no edges`}>
      {notice && <Text as="p" role="note" className={s.note}>{notice}</Text>}
      <Text size={200}>It holds:</Text>
      <ul className={s.list}>{held.map((l, i) => <li key={i}><Text size={200}>{l}</Text></li>)}</ul>
      {sum.nodes > held.length && <Text size={200}>and {sum.nodes - held.length} more</Text>}
    </div>
  );
}

export function SpaceView() {
  const s = useStyles();
  const ws = useWorkspace();
  const graphs = useStore((x) => x.graphs).filter((g) => g.role !== "catalog");
  const active = useStore((x) => x.activeGraph);
  const [rows, setRows] = useState(false);
  if (!ws) return null;
  const catalog = ws.info.graphs.find((x) => x.role === "catalog");
  const purpose = purposesOf(catalog ? ws.histories.get(catalog.id)?.graph : undefined);
  const subtitle = (id: string): string => { const info = ws.graphInfo(id), g = ws.histories.get(id)?.graph; return info && g ? describeGraph(info, typesHeld(g), purpose) : ""; };
  const kindOf = (g: { id: string; nodes: number; edges: number }) => {
    const info = ws.graphInfo(g.id), graph = ws.histories.get(g.id)?.graph;
    const rows = info?.tree && graph && hasTreeRows(graph, info.tree) && hasHierarchyEdges(graph, info.tree) ? treeRowCount(graph, info.tree) : 0;
    return info ? tileKind(info, g, !!ws.source.document, rows) : "graph";
  };
  /** what the picture leaves out: nodes no edge reaches are not drawn in a tile of a graph that has others */
  const leftOut = (id: string): string => { const graph = ws.histories.get(id)?.graph; return graph && kindOf(graphs.find((x) => x.id === id)!) === "graph" && tileThinsIsolated(graph) ? ` ${isolatedNodes(graph)} ${isolatedNodes(graph) === 1 ? "element without relationships is" : "elements without relationships are"} not drawn.` : ""; };
  return (
    <div className={s.root}>
      <aside className={s.side} aria-label="Search, filters and element list of the active graph"><SidePanel graph={active} /></aside>
      <section className={s.center} aria-label="Graph space">
        <div className={s.bar}>
          <Text weight="semibold">Graph space {ws.info.id}</Text><Badge appearance="outline">{graphs.length} graphs</Badge><span className={s.grow} />
          <Button size="small" onClick={() => setRows(!rows)}>{rows ? "Side by side" : "Stack vertically"}</Button>
          <Button size="small" onClick={() => graphs.forEach((g) => dispatch({ type: "pane/command", graph: g.id, command: "fit" }))}>Fit all</Button><Legend graph={active} />
        </div>
        <div className={s.grid} style={{ gridTemplateColumns: rows ? "1fr" : "repeat(auto-fill, minmax(340px, 1fr))" }}>
          {graphs.map((g) => (
            <div key={g.id} role="group" aria-label={`Graph ${g.id}`} className={`${s.pane} ${g.id === active ? s.activePane : ""}`} onPointerDownCapture={() => g.id !== active && dispatch({ type: "ui/activeGraph", graph: g.id })}>
              <div className={s.head}>
                <div className={s.headRow}>
                  <Text weight="semibold">{g.id}</Text><Badge color={roleColor(g.role)} appearance="outline">{g.role}</Badge>{g.direction && <DirectionBadge direction={g.direction} />}
                  <Text size={200}>{g.nodes.toLocaleString()} nodes · {g.edges.toLocaleString()} edges</Text><span className={s.grow} />
                  <Button size="small" aria-pressed={g.id === active} onClick={() => dispatch({ type: "ui/activeGraph", graph: g.id })}>Use for filters</Button>
                  <Button size="small" onClick={() => { dispatch({ type: "ui/activeGraph", graph: g.id }); dispatch({ type: "ui/view", view: "data" }); }}>Open</Button>
                </div>
                <Text size={200} className={s.caption} title={subtitle(g.id)}>{subtitle(g.id)}{leftOut(g.id)}</Text>
              </div>
              <div className={s.tileBody}>
                {kindOf(g) === "document" ? <><DocumentView graph={g.id} compact /><div className={s.fade} /></> : kindOf(g) === "notice" ? <NoticeTile graph={g.id} /> : kindOf(g) === "tree" ? <TreeTile graph={g.id} /> : kindOf(g) === "summary" ? <SummaryTile graph={g.id} /> : <CanvasHost graph={g.id} tile />}
              </div>
            </div>
          ))}
        </div>
        <Timeline graph={active} />
      </section>
      <aside className={s.right} aria-label="Inspector"><Inspector /></aside>
    </div>
  );
}
