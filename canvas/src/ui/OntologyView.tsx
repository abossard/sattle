// Ontology browser: types as a Fluent Tree beside the ontology drawn as a graph; for engine sources the module text sits next to it.
import { Badge, Field, Select, Subtitle2, Text, Textarea, Tree, TreeItem, TreeItemLayout, makeStyles, tokens } from "@fluentui/react-components";
import { useEffect, useMemo, useRef, useState } from "react";
import { DataPane } from "../app/data-pane";
import { History } from "../app/history";
import { Catalog, moduleOntologyGraph } from "../core/catalog";
import type { Graph } from "../core/graph";
import { ontologyGraph } from "../core/ontology-graph";
import { runtime } from "../state/runtime";
import { rendererTheme } from "../state/effects";
import { dispatch, useStore } from "../state/store";
import { webgl2Available } from "./CanvasHost";
import { useWorkspace } from "./common";

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateColumns: "minmax(240px, 320px) minmax(0, 1fr)", minHeight: 0, height: "100%", "@media (max-width: 800px)": { gridTemplateColumns: "1fr", gridTemplateRows: "auto minmax(300px, 1fr)" } },
  side: { padding: tokens.spacingHorizontalM, overflow: "auto", display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalS },
  main: { display: "grid", gridTemplateRows: "minmax(0, 1fr) auto", minHeight: 0 },
  stage: { minHeight: 0, backgroundColor: tokens.colorNeutralBackground3, "& > div": { height: "100%" } },
});

interface Item { id: string; label: string; detail: string }
interface Content { graph: Graph; entities: Item[]; rels: Item[]; summary: string }

function fromOntology(ws: NonNullable<typeof runtime.ws>, graphId: string): Content {
  const o = ws.graphInfo(graphId)!.ontology;
  const g = ontologyGraph(o);
  return {
    graph: g,
    entities: o.entityTypes.filter((t) => t.id.value !== o.noEntityType.value).map((t) => ({ id: `type:${t.id.value}`, label: t.id.value, detail: t.properties.map((p) => p.value).join(", ") })),
    rels: o.relationshipTypes.filter((t) => t.id.value !== o.noRelationshipType.value).map((t) => ({ id: t.id.value, label: t.id.value, detail: `${t.sourceTypes.map((x) => x.value).join(", ")} → ${t.targetTypes.map((x) => x.value).join(", ")}` })),
    summary: `${o.reference.id}@${o.reference.version}`,
  };
}

function fromModule(c: Catalog, moduleId: string, name: string): Content {
  const o = c.ontology(moduleId);
  return {
    graph: moduleOntologyGraph(c, moduleId),
    entities: o.entityTypes.map((t) => ({ id: t, label: c.name(t), detail: c.targets(t, "hasProperty").map((p) => c.name(p)).join(", ") })),
    rels: o.relationshipTypes.map((r) => ({ id: r, label: c.name(r), detail: `${c.targets(r, "sourceType").map((x) => c.typeLabel(x, name)).join(", ")} → ${c.targets(r, "targetType").map((x) => c.typeLabel(x, name)).join(", ")}` })),
    summary: `${o.entityTypes.length} entity types, ${o.relationshipTypes.length} relationship types, ${o.propertyTypes.length} properties, ${o.identityDomains.length} identity domains`,
  };
}

export function OntologyView() {
  const s = useStyles();
  const ws = useWorkspace();
  const theme = useStore((x) => x.theme);
  const catalogId = useStore((x) => x.catalog);
  const active = useStore((x) => x.activeGraph);
  const catVersion = useStore((x) => x.graphs.find((g) => g.id === x.catalog)?.version);
  const [graphId, setGraphId] = useState(active);
  const [moduleName, setModuleName] = useState("");
  const [texts, setTexts] = useState(new Map<string, string>());
  const [picked, setPicked] = useState<string | null>(null);
  const stage = useRef<HTMLDivElement>(null);
  const paneRef = useRef<DataPane | null>(null);
  const catalog = useMemo(() => (ws && catalogId ? new Catalog(ws.histories.get(catalogId)!.graph) : null), [ws, catalogId, catVersion]); // eslint-disable-line react-hooks/exhaustive-deps
  const modules = useMemo(() => catalog?.modules().filter((m) => m.name !== "sattle.kernel") ?? [], [catalog]);
  const module = modules.find((m) => m.name === moduleName) ?? modules.find((m) => m.name === "acme.ops") ?? modules[0];
  useEffect(() => { void ws?.source.definitions?.modules().then((ms) => setTexts(new Map(ms.map((m) => [m.module, m.text])))).catch(() => {}); }, [ws, catVersion]);
  const content = useMemo(() => (!ws ? null : catalog && module ? fromModule(catalog, module.id, module.name) : fromOntology(ws, ws.graphInfo(graphId) ? graphId : active)), [ws, catalog, module, graphId, active]);
  useEffect(() => {
    if (!ws || !content || !stage.current || !webgl2Available()) return;
    const info = ws.graphInfo(catalog ? catalogId : graphId)!;
    const host = document.createElement("div");
    stage.current.replaceChildren(host);
    const pane = new DataPane({ ...info, id: `ontology ${module?.name ?? graphId}`, inputs: [] }, new History(content.graph.toSnapshot()), host, rendererTheme(theme),
      { select: (sel) => { pane.setSelection(sel); setPicked(sel?.id ?? null); }, hover: () => {}, activate: () => {}, changed: () => {}, announce: (text) => dispatch({ type: "ui/announce", text }), key: () => false });
    pane.layoutKind = "layered"; pane.layoutDirection = "right"; pane.view.allEdgeLabels = true; pane.start();
    paneRef.current = pane;
    return () => { pane.dispose(); paneRef.current = null; };
  }, [ws, content, theme]); // eslint-disable-line react-hooks/exhaustive-deps
  if (!ws || !content) return null;
  const pick = (id: string) => { setPicked(id); const p = paneRef.current; if (p) { p.setSelection({ graph: p.info.id, kind: "entity", id }); p.focusSelection(); } };
  return (
    <div className={s.root}>
      <div className={s.side}>
        {catalog ? (
          <Field label="Module"><Select value={module?.name} onChange={(_, d) => setModuleName(d.value)}>{modules.map((m) => <option key={m.id} value={m.name}>{m.name} v{m.version}</option>)}</Select></Field>
        ) : (
          <Field label="Ontology of graph"><Select value={graphId} onChange={(_, d) => setGraphId(d.value)}>{ws.info.graphs.map((g) => <option key={g.id} value={g.id}>{g.id} ({g.ontology.reference.id}@{g.ontology.reference.version})</option>)}</Select></Field>
        )}
        <Text size={200}>{content.summary}</Text>
        <Subtitle2 as="h2">Entity types</Subtitle2>
        <Tree aria-label="Entity types">
          {content.entities.map((t) => <TreeItem key={t.id} itemType="leaf" value={t.id} aria-current={picked === t.id || undefined} onClick={() => pick(t.id)}><TreeItemLayout>{picked === t.id ? <b>{t.label}</b> : t.label} <Text size={200}>{t.detail}</Text></TreeItemLayout></TreeItem>)}
        </Tree>
        <Subtitle2 as="h2">Relationship types</Subtitle2>
        <Tree aria-label="Relationship types">
          {content.rels.map((r) => <TreeItem key={r.id} itemType="leaf" value={r.id}><TreeItemLayout>{r.label} <Badge appearance="outline">{r.detail}</Badge></TreeItemLayout></TreeItem>)}
        </Tree>
      </div>
      <div className={s.main}>
        <div className={s.stage} ref={stage} />
        {catalog && module && <Field label="Module text (read only here; edit it in the projection editor)"><Textarea readOnly rows={10} resize="vertical" value={texts.get(module.name) ?? ""} /></Field>}
      </div>
    </div>
  );
}
