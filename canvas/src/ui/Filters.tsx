// Search, type filter, property filter, focus, layout, groups and telemetry channels of one graph. Every control dispatches an action.
import { Accordion, AccordionHeader, AccordionItem, AccordionPanel, Button, Checkbox, Field, Input, Select, SearchBox, SpinButton, Switch, Text, makeStyles, tokens } from "@fluentui/react-components";
import { useEffect, useMemo, useState } from "react";
import { dispatch, useStore } from "../state/store";
import { emptyFilter, type FilterState } from "../state/types";
import type { EdgeKind } from "../source/presentation";
import { KIND_WORD, KindSample, kindsOf } from "./EdgeKinds";
import { DEFAULT_COLORING, ROLE_GLYPH, ROLE_WORD, coloringLabel, legendOf, propertiesOf, resolveColoring, type Target } from "../app/color-by";
import { roleCss } from "../app/roles";
import { SUPPORT_GROUPING, resolveGrouping } from "../app/graph-view-model";
import { typeCountsOf, styleOf, valueCountsOf } from "../state/ws-ops";
import { TypeGlyph, useGraphSummary, useWorkspace } from "./common";

const useStyles = makeStyles({ col: { display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalS }, row: { display: "flex", columnGap: tokens.spacingHorizontalS, flexWrap: "wrap" } });
const set = (graph: string, filter: Partial<FilterState>) => dispatch({ type: "filter/set", graph, filter });

/** One target (nodes or edges): which property colors it, and the legend with counts, roles and glyphs. */
export function ColorControl({ graph, target, setting }: { graph: string; target: Target; setting: string }) {
  const ws = useWorkspace(graph);
  const version = useGraphSummary(graph)?.version;
  const info = ws?.graphInfo(graph);
  const coloring = info ? resolveColoring(info, target, setting) : null;
  const legend = useMemo(() => {
    if (!ws || !coloring) return [];
    const { counts, missing, hint } = valueCountsOf(ws, graph, coloring, target);
    return legendOf(counts, missing, hint, roleCss);
  }, [ws, graph, target, coloring?.property, coloring?.hint, version]); // eslint-disable-line react-hooks/exhaustive-deps
  if (!ws || !info) return null;
  const props = [...propertiesOf(info.ontology, target)].sort();
  const hinted = resolveColoring(info, target, DEFAULT_COLORING);
  const label = target === "node" ? "Nodes" : "Edges";
  const key = target === "node" ? "colorNodes" : "colorEdges";
  return (
    <div style={{ display: "flex", flexDirection: "column", rowGap: 6 }}>
      <Field label={`${label} colored by`} hint={setting === DEFAULT_COLORING && hinted ? `The ontology says what ${coloringLabel(hinted)} mean: ok, warning, danger.` : "Labels also carry the value, so color is never the only cue."}>
        <Select value={setting} onChange={(_, d) => set(graph, { [key]: d.value })}>
          <option value={DEFAULT_COLORING}>{hinted ? `Ontology hint (${coloringLabel(hinted)})` : "Ontology hint (none for this graph)"}</option>
          <option value="">Type colors</option>
          {props.map((k) => <option key={k} value={k}>{k}</option>)}
        </Select>
      </Field>
      {legend.length > 0 && (
        <ul aria-label={`Legend of ${label.toLowerCase()}: ${coloring ? coloringLabel(coloring) : ""}`} style={{ listStyle: "none", margin: 0, padding: 0, display: "flex", flexDirection: "column", rowGap: 4 }}>
          {legend.map((l) => (
            <li key={l.value} style={{ display: "flex", alignItems: "center", columnGap: 8 }}>
              <span aria-hidden="true" style={{ width: 14, height: 14, borderRadius: target === "node" ? 7 : 2, background: l.color, display: "inline-block", border: "1px solid currentColor" }} />
              <Text>{l.role ? `${ROLE_GLYPH[l.role]} ` : ""}{l.value}</Text>
              <Text size={200}>({l.count}){l.role ? `, ${ROLE_WORD[l.role]}` : ""}</Text>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** A number typed by a person: a decimal comma is a decimal point. Empty or not a number is no number (the threshold is cleared), never NaN. */
export function parseDecimal(typed: string | undefined): number | undefined {
  const t = (typed ?? "").trim().replace(/^(-?\d+),(\d+)$/, "$1.$2");
  const n = t === "" ? NaN : Number(t);
  return Number.isFinite(n) ? n : undefined;
}

export function FiltersPanel({ graph }: { graph: string }) {
  const s = useStyles();
  const ws = useWorkspace(graph);
  const f = useStore((x) => x.filters[graph]) ?? emptyFilter();
  const tel = useStore((x) => x.telemetry[graph]);
  const info = ws?.graphInfo(graph);
  const [search, setSearch] = useState(f.search);
  useEffect(() => { const t = setTimeout(() => { if (search !== f.search) set(graph, { search }); }, 160); return () => clearTimeout(t); }, [search]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => setSearch(f.search), [graph, f.search]);
  const counts = useMemo(() => (ws ? typeCountsOf(ws, graph) : new Map<string, number>()), [ws, graph, ws?.histories.get(graph)?.length]); // eslint-disable-line react-hooks/exhaustive-deps
  if (!ws || !info) return null;
  const pane = ws.panes.get(graph);
  const relTypes = [...new Set([...ws.histories.get(graph)!.graph.relationships()].map((r) => r.type))].sort();
  const propKeys = [...new Set(info.ontology.properties.map((p) => p.id.value))];
  const support = resolveGrouping(info, SUPPORT_GROUPING);
  const grouped = !!resolveGrouping(info, f.grouping);
  const kinds = kindsOf(info.edgeKinds);
  const hiddenKinds: ReadonlySet<EdgeKind> = new Set(f.hiddenKinds ?? pane?.hiddenKinds() ?? []);
  const restated = [...(pane?.restatedTypes() ?? [])].sort();
  const meters = (target: "node" | "edge") => tel?.meters.filter((m) => m.target === target) ?? [];
  const channel = (label: string, target: "node" | "edge", key: string, value: string) => (
    <Field label={label} key={`${target}-${key}`}>
      <Select value={value} onChange={(_, d) => dispatch({ type: "telemetry/set", graph, change: { [target]: { [key]: d.value } } })}>
        <option value="">(off)</option>
        {meters(target).map((m) => <option key={m.id} value={m.id}>{m.label}{m.unit ? ` (${m.unit})` : ""}</option>)}
      </Select>
    </Field>
  );
  return (
    <Accordion multiple collapsible defaultOpenItems={["search", "types"]}>
      <AccordionItem value="search">
        <AccordionHeader>Search</AccordionHeader>
        <AccordionPanel className={s.col}>
          <SearchBox value={search} placeholder="id, type or property value" aria-label="Search elements" onChange={(_, d) => setSearch(d.value)}
            onKeyDown={(e) => { if (e.key === "Enter") dispatch({ type: "pane/command", graph, command: "firstMatch" }); }} />
          <Text size={200} role="status" aria-live="polite">{f.search ? `${pane?.matchCount ?? 0} matches` : ""}</Text>
        </AccordionPanel>
      </AccordionItem>
      <AccordionItem value="types">
        <AccordionHeader>Types</AccordionHeader>
        <AccordionPanel className={s.col}>
          {[...counts].sort((a, b) => b[1] - a[1]).map(([t, n]) => (
            <Checkbox key={t} checked={!f.hiddenTypes.includes(t)} onChange={(_, d) => set(graph, { hiddenTypes: d.checked ? f.hiddenTypes.filter((x) => x !== t) : [...f.hiddenTypes, t] })}
              label={<span style={{ display: "inline-flex", alignItems: "center", columnGap: 6 }}><TypeGlyph {...styleOf(ws, graph, t)} />{t} <Text size={200}>({n})</Text></span>} />
          ))}
        </AccordionPanel>
      </AccordionItem>
      {kinds.length > 0 && (
        <AccordionItem value="kinds">
          <AccordionHeader>Edge kinds</AccordionHeader>
          <AccordionPanel className={s.col}>
            {kinds.map(({ kind, types }) => (
              <Checkbox key={kind} checked={!hiddenKinds.has(kind)} onChange={(_, d) => set(graph, { hiddenKinds: d.checked ? [...hiddenKinds].filter((x) => x !== kind) : [...hiddenKinds, kind] })}
                label={<span style={{ display: "inline-flex", alignItems: "center", columnGap: 8 }}><KindSample kind={kind} />{KIND_WORD[kind].word} <Text size={200}>({types.map((t) => t.slice(t.lastIndexOf(".") + 1)).join(", ")})</Text></span>} />
            ))}
            {restated.length > 0 && (
              <Checkbox checked={f.showRestated} onChange={(_, d) => set(graph, { showRestated: !!d.checked })}
                label={<span>Edges that restate others <Text size={200}>({restated.map((t) => t.slice(t.lastIndexOf(".") + 1)).join(", ")}: every one runs beside an edge of another type)</Text></span>} />
            )}
          </AccordionPanel>
        </AccordionItem>
      )}
      <AccordionItem value="color">
        <AccordionHeader>Color by property</AccordionHeader>
        <AccordionPanel className={s.col}>
          {(["node", "edge"] as const).map((target) => (
            <ColorControl key={target} graph={graph} target={target} setting={target === "node" ? f.colorNodes : f.colorEdges} />
          ))}
        </AccordionPanel>
      </AccordionItem>
      <AccordionItem value="prop">
        <AccordionHeader>Property filter</AccordionHeader>
        <AccordionPanel className={s.col}>
          <Field label="Property"><Select value={f.propKey} onChange={(_, d) => set(graph, { propKey: d.value })}><option value="">(none)</option>{propKeys.map((k) => <option key={k}>{k}</option>)}</Select></Field>
          <Field label="Operator"><Select value={f.propOp} onChange={(_, d) => set(graph, { propOp: d.value as FilterState["propOp"] })}><option>equals</option><option>contains</option><option>not equals</option></Select></Field>
          <Field label="Value"><Input value={f.propValue} onChange={(_, d) => set(graph, { propValue: d.value })} /></Field>
          <Button onClick={() => { set(graph, emptyFilter()); }}>Clear filters</Button>
        </AccordionPanel>
      </AccordionItem>
      <AccordionItem value="focus">
        <AccordionHeader>Focus and layout</AccordionHeader>
        <AccordionPanel className={s.col}>
          <Field label="Neighbourhood of the selection" hint="Enter on the canvas toggles one hop.">
            <Select value={String(f.focusDepth)} onChange={(_, d) => set(graph, { focusDepth: Number(d.value) })}>{[0, 1, 2, 3].map((d) => <option key={d} value={d}>{d === 0 ? "Off" : `${d} hop${d === 1 ? "" : "s"}`}</option>)}</Select>
          </Field>
          <Switch checked={f.focusDim} label="Dim everything else" onChange={(_, d) => set(graph, { focusDim: d.checked })} />
          <Field label="Layout"><Select value={f.layout} onChange={(_, d) => set(graph, { layout: d.value as FilterState["layout"] })}><option value="force">Force directed</option><option value="layered">Layered (trees, DAGs)</option></Select></Field>
          <div className={s.row}><Button onClick={() => dispatch({ type: "pane/command", graph, command: "relayout" })}>Re-layout</Button><Button onClick={() => dispatch({ type: "pane/command", graph, command: "fit" })}>Fit</Button></div>
        </AccordionPanel>
      </AccordionItem>
      <AccordionItem value="groups">
        <AccordionHeader>Groups</AccordionHeader>
        <AccordionPanel className={s.col}>
          <Field label="Group by relationship" hint="Double-click a group, or press Space, to collapse or expand.">
            <Select value={f.grouping} onChange={(_, d) => set(graph, { grouping: d.value })}>
              <option value={SUPPORT_GROUPING}>{support ? `Supporting elements (${support.type})` : "Supporting elements (none for this graph)"}</option>
              <option value="">(none)</option>{relTypes.map((r) => <option key={r}>{r}</option>)}
            </Select>
          </Field>
          <div className={s.row}>
            <Button disabled={!grouped} onClick={() => dispatch({ type: "pane/command", graph, command: "collapse" })}>Collapse lowest level</Button>
            <Button disabled={!grouped} onClick={() => dispatch({ type: "pane/command", graph, command: "expand" })}>Expand all</Button>
          </div>
        </AccordionPanel>
      </AccordionItem>
      {tel?.hasData && (
        <AccordionItem value="telemetry">
          <AccordionHeader>Telemetry</AccordionHeader>
          <AccordionPanel className={s.col}>
            <Switch checked={tel.enabled} label="Show telemetry on the graph" onChange={(_, d) => dispatch({ type: "telemetry/set", graph, change: { enabled: d.checked } })} />
            {channel("Edge width", "edge", "width", tel.edge.width)}
            {channel("Edge flow speed", "edge", "flow", tel.edge.flow)}
            {channel("Edge state (dashed at threshold)", "edge", "state", tel.edge.state)}
            {channel("Node ring gauge", "node", "ring", tel.node.ring)}
            {channel("Node size", "node", "size", tel.node.size)}
            {channel("Node state (segmented ring and ! at threshold)", "node", "state", tel.node.state)}
            {([["edge", tel.edge.state], ["node", tel.node.state]] as const).map(([target, id]) => {
              const m = id ? tel.meters.find((x) => x.id === id && x.target === target) : undefined;
              return m ? (
                <Field key={target} label={`${m.label} warning threshold`}>
                  <SpinButton value={m.warn ?? null} min={m.min} max={m.max} step={m.max <= 1 ? 0.01 : 1} onChange={(_, d) => dispatch({ type: "telemetry/set", graph, change: { warn: { id: m.id, target, value: d.value ?? parseDecimal(d.displayValue) } } })} />
                </Field>
              ) : null;
            })}
            <Switch checked={tel.flow} label="Animate flow" onChange={(_, d) => dispatch({ type: "telemetry/set", graph, change: { flow: d.checked } })} />
          </AccordionPanel>
        </AccordionItem>
      )}
    </Accordion>
  );
}
