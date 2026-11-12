// The data graph view and the graph space view: filters and elements on the left, canvases in the middle, inspector on the right.
import { Badge, Button, Field, Popover, PopoverSurface, PopoverTrigger, Select, Tab, TabList, Text, makeStyles, tokens } from "@fluentui/react-components";
import { useState } from "react";
import { dispatch, useStore } from "../state/store";
import { CanvasHost } from "./CanvasHost";
import { DocumentView } from "./DocumentView";
import { TreeView } from "./TreeView";
import { hasTreeRows } from "../app/tree-model";
import { edgelessNotice } from "../app/graph-view-model";
import { describeGraph, purposesOf, tileKind, typesHeld } from "../app/tile";
import { DirectionBadge, roleColor, useGraphSummary, useWorkspace } from "./common";
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
    <div className={s.notice}>
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
  const kindOf = (g: { id: string; nodes: number; edges: number }) => { const info = ws.graphInfo(g.id); return info ? tileKind(info, g, !!ws.source.document) : "graph"; };
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
                <Text size={200} className={s.caption} title={subtitle(g.id)}>{subtitle(g.id)}</Text>
              </div>
              <div className={s.tileBody}>
                {kindOf(g) === "document" ? <><DocumentView graph={g.id} compact /><div className={s.fade} /></> : kindOf(g) === "notice" ? <NoticeTile graph={g.id} /> : <CanvasHost graph={g.id} tile />}
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
