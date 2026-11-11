// The data graph view and the graph space view: filters and elements on the left, canvases in the middle, inspector on the right.
import { Badge, Button, Field, Popover, PopoverSurface, PopoverTrigger, Select, Tab, TabList, Text, makeStyles, tokens } from "@fluentui/react-components";
import { useState } from "react";
import { dispatch, useStore } from "../state/store";
import { CanvasHost } from "./CanvasHost";
import { DocumentView } from "./DocumentView";
import { TreeView } from "./TreeView";
import { hasTreeRows } from "../app/tree-model";
import { DirectionBadge, roleColor, useGraphSummary, useWorkspace } from "./common";
import { ElementsPanel } from "./Elements";
import { FiltersPanel } from "./Filters";
import { Inspector } from "./Inspector";
import { Timeline } from "./Timeline";

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateColumns: "minmax(220px, 300px) minmax(0, 1fr) minmax(240px, 340px)", minHeight: 0, height: "100%", "@media (max-width: 1000px)": { gridTemplateColumns: "1fr", gridTemplateRows: "auto minmax(360px, 1fr) auto", height: "auto" } },
  side: { overflow: "auto", minHeight: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}` },
  right: { overflow: "auto", minHeight: 0, borderLeft: `1px solid ${tokens.colorNeutralStroke2}` },
  center: { display: "grid", gridTemplateRows: "auto minmax(0, 1fr) auto", minHeight: 0 },
  bar: { display: "flex", alignItems: "center", columnGap: tokens.spacingHorizontalS, flexWrap: "wrap", padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}` },
  grow: { flexGrow: 1 },
  grid: { display: "grid", gap: "6px", minHeight: 0, overflow: "auto", padding: "6px", alignContent: "start" },
  pane: { display: "grid", gridTemplateRows: "auto minmax(260px, 1fr) auto", minHeight: "340px", border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium },
  activePane: { border: `2px solid ${tokens.colorBrandStroke1}` },
});

const Legend = () => (
  <Popover><PopoverTrigger disableButtonEnhancement><Button size="small">Legend</Button></PopoverTrigger>
    <PopoverSurface aria-label="How to read the canvas"><div style={{ display: "grid", rowGap: 4 }}>
      <Text>+ added: solid ring</Text><Text>Δ changed: double ring</Text><Text>− removed: dashed ghost</Text><Text>Same entity elsewhere: pink ring</Text><Text>Source element: dashed ring</Text><Text>Shape and label give the type; color repeats it</Text>
    </div></PopoverSurface></Popover>
);

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
          {mode === "graph" && <><Button size="small" onClick={() => dispatch({ type: "pane/command", graph: active, command: "fit" })}>Fit</Button><Legend /></>}
        </div>
        {mode === "document" ? <DocumentView graph={active} /> : mode === "tree" ? <TreeView graph={active} /> : <CanvasHost graph={active} />}
        <Timeline graph={active} />
      </section>
      <aside className={s.right} aria-label="Inspector"><Inspector /></aside>
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
  return (
    <div className={s.root}>
      <aside className={s.side} aria-label="Search, filters and element list of the active graph"><SidePanel graph={active} /></aside>
      <section className={s.center} aria-label="Graph space">
        <div className={s.bar}>
          <Text weight="semibold">Graph space {ws.info.id}</Text><Badge appearance="outline">{graphs.length} graphs</Badge><span className={s.grow} />
          <Button size="small" onClick={() => setRows(!rows)}>{rows ? "Side by side" : "Stack vertically"}</Button>
          <Button size="small" onClick={() => graphs.forEach((g) => dispatch({ type: "pane/command", graph: g.id, command: "fit" }))}>Fit all</Button><Legend />
        </div>
        <div className={s.grid} style={{ gridTemplateColumns: rows ? "1fr" : "repeat(auto-fill, minmax(340px, 1fr))" }}>
          {graphs.map((g) => (
            <div key={g.id} role="group" aria-label={`Graph ${g.id}`} className={`${s.pane} ${g.id === active ? s.activePane : ""}`} onPointerDownCapture={() => g.id !== active && dispatch({ type: "ui/activeGraph", graph: g.id })}>
              <div className={s.bar}>
                <Text weight="semibold">{g.id}</Text><Badge color={roleColor(g.role)} appearance="outline">{g.role}</Badge>{g.direction && <DirectionBadge direction={g.direction} />}
                <Text size={200}>{g.nodes.toLocaleString()} nodes · {g.edges.toLocaleString()} edges</Text><span className={s.grow} />
                <Button size="small" aria-pressed={g.id === active} onClick={() => dispatch({ type: "ui/activeGraph", graph: g.id })}>Use for filters</Button>
              </div>
              <CanvasHost graph={g.id} />
            </div>
          ))}
        </div>
        <Timeline graph={active} />
      </section>
      <aside className={s.right} aria-label="Inspector"><Inspector /></aside>
    </div>
  );
}
