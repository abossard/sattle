// The only React component that touches the WebGL view: it mounts the pane's host element and gets out of the way.
import { makeStyles, MessageBar, MessageBarBody, tokens } from "@fluentui/react-components";
import { useEffect, useRef } from "react";
import { runtime } from "../state/runtime";
import { useStore } from "../state/store";
import { emptyFilter, type FilterState } from "../state/types";
import { applyFilter } from "../state/ws-ops";
import { edgelessNotice } from "../app/graph-view-model";
import { useGraphSummary, useWorkspace } from "./common";
import "./canvas.css";

const useStyles = makeStyles({
  stage: { position: "relative", minHeight: 0, height: "100%", backgroundColor: tokens.colorNeutralBackground3, "& > div": { height: "100%" } },
  box: { position: "relative", minHeight: 0, height: "100%" },
  notice: { position: "absolute", left: tokens.spacingHorizontalS, right: tokens.spacingHorizontalS, bottom: tokens.spacingVerticalS, zIndex: 2 },
});

let glOk: boolean | undefined;
export function webgl2Available(): boolean {
  if (glOk === undefined) { try { glOk = !!document.createElement("canvas").getContext("webgl2"); } catch { glOk = false; } }
  return glOk;
}

export function CanvasHost({ graph, focus = false }: { graph: string; focus?: boolean }) {
  const ref = useRef<HTMLDivElement>(null);
  const s = useStyles();
  const gen = useStore((x) => x.generation);
  const ready = useStore((x) => x.load.status === "ready");
  const gl = webgl2Available();
  useEffect(() => {
    const ws = runtime.ws;
    if (!ws || !ready || !gl || !ref.current) return;
    const pane = ws.paneFor(graph);
    const host = pane.view.host;
    ref.current.append(host);
    // a pane that is shown again gets the filters the user set; defaults are left alone
    const f = useStore.getState().filters[graph];
    const empty = emptyFilter();
    const changed = f ? Object.fromEntries(Object.entries(f).filter(([k, v]) => JSON.stringify(v) !== JSON.stringify(empty[k as keyof FilterState]))) : {};
    // the coloring and the grouping always apply: their defaults come from the ontology, which a default filter state does not mention
    if (f) applyFilter(pane, f, { ...changed, colorNodes: f.colorNodes, colorEdges: f.colorEdges, grouping: f.grouping });
    pane.view.fit(false);
    if (focus) host.focus({ preventScroll: true });
    return () => host.remove();
  }, [graph, gen, ready, gl, focus]);
  const sum = useGraphSummary(graph);
  const info = useWorkspace(graph)?.graphInfo(graph);
  const notice = sum && info ? edgelessNotice(info.role, sum.nodes, sum.edges, info.ontology.relationshipTypes.map((t) => t.id.value)) : null;
  return (
    <div className={s.box}>
      {gl ? <div ref={ref} className={s.stage} /> : <MessageBar intent="warning"><MessageBarBody>WebGL2 is not available in this browser. The Elements and Relationships lists show the same graph.</MessageBarBody></MessageBar>}
      {notice && <MessageBar intent="info" className={s.notice}><MessageBarBody>{notice}</MessageBarBody></MessageBar>}
    </div>
  );
}
