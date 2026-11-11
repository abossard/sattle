// Why a row of a tree has its state: its own and rolled-up state, the children that drive the roll-up, what supports it and what it depends on.
// Read from the graph and the ontology's tree marks alone; the lineage down to the master is the section that follows in the Inspector.
import { Link, Subtitle2, Text, makeStyles, tokens } from "@fluentui/react-components";
import { useMemo } from "react";
import { buildTree, explainNode } from "../app/tree-model";
import type { Workspace } from "../app/workspace";
import { dispatch } from "../state/store";
import { StateBadge } from "./TreeView";
import { useGraphSummary } from "./common";

const useStyles = makeStyles({ list: { listStyle: "none", margin: 0, padding: 0, display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalXS }, row: { display: "flex", columnGap: tokens.spacingHorizontalS, alignItems: "baseline", flexWrap: "wrap" } });
const go = (graph: string, id: string) => dispatch({ type: "select/element", selection: { graph, kind: "entity", id } });

export function WhyState({ ws, graph, id }: { ws: Workspace; graph: string; id: string }) {
  const s = useStyles();
  const version = useGraphSummary(graph)?.version;
  const info = ws.graphInfo(graph), hist = ws.histories.get(graph);
  const model = useMemo(() => (hist && info?.tree ? buildTree(hist.graph, info.tree, info.presentation ?? []) : null), [hist, info, version]); // eslint-disable-line react-hooks/exhaustive-deps
  const why = model && hist ? explainNode(hist.graph, model, id) : null;
  const n = model?.nodes.get(id);
  if (!model || !n || !why) return null;
  return (
    <>
      <Subtitle2 as="h3">Why this state</Subtitle2>
      <div className={s.row}>
        {n.rolled && <><Text>Rolled-up</Text><StateBadge cell={n.rolled} label="state" /></>}
        {n.own && <><Text>Own</Text><StateBadge cell={n.own} label="own state" /></>}
      </div>
      {(n.policy || n.curatedBy || n.critical) && (
        <ul className={s.list} aria-label="Roll-up policy and curation">
          {n.policy && <li><Text>Roll-up policy: {n.policy.mode}{n.policy.share ? ` (share ${n.policy.share})` : ""}</Text></li>}
          {n.critical && <li><Text>Marked critical</Text></li>}
          {n.curatedBy && <li><Text>Curated by record {n.curatedBy}</Text></li>}
        </ul>
      )}
      {n.children.length > 0 && (
        why.drivers.length > 0
          ? <><Text weight="semibold">Rolls up from the worst of its {n.children.length} {n.children.length === 1 ? "child" : "children"}</Text>
            <ul className={s.list} aria-label="Children that drive the roll-up">{why.drivers.map((d) => <li key={d.id} className={s.row}><Link as="button" onClick={() => go(graph, d.id)}>{d.label}</Link>{(d.rolled ?? d.own) && <StateBadge cell={(d.rolled ?? d.own)!} label="state" />}</li>)}</ul></>
          : <Text>Its own state is worse than any child's.</Text>
      )}
      {why.support.length > 0 && (
        <><Text weight="semibold">Supporting elements ({why.support.length})</Text>
          <ul className={s.list} aria-label="Supporting elements">{why.support.slice(0, 30).map((x) => (
            <li key={x.id}><div className={s.row}><Link as="button" onClick={() => go(graph, x.id)}>{x.label}</Link><Text size={200}>{x.type}</Text></div>
              <Text size={200}>{x.props.filter(([k]) => !/(^|\.)name$/.test(k)).map(([k, v]) => `${k.slice(k.lastIndexOf(".") + 1)}: ${v}`).join(", ")}</Text></li>
          ))}</ul>
          {why.support.length > 30 && <Text size={200}>and {why.support.length - 30} more</Text>}</>
      )}
      {why.cross.length > 0 && (
        <><Text weight="semibold">Links ({why.cross.length})</Text>
          <ul className={s.list} aria-label="Links to other nodes">{why.cross.slice(0, 30).map((x, i) => <li key={`${x.id}${i}`} className={s.row}><Text size={200}>{x.direction === "out" ? "→" : "←"} {x.relationship}</Text><Link as="button" onClick={() => go(graph, x.id)}>{x.label}</Link></li>)}</ul></>
      )}
    </>
  );
}
