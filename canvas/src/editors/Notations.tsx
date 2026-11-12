// The five notations in the application's editor view, on the engine's draft: the open projection as a document, a gesture
// sent as an editor action (composed and applied by the editor effects, one undo step each), the selection shared with the
// properties panel, and for "By example" the data of the graphs the projection reads, as the workspace holds and draws them.
import { useMemo, type ReactElement } from "react";
import { readProjection, stepHolding } from "../editing/doc";
import { rendererTheme } from "../state/effects";
import { runtime } from "../state/runtime";
import { dispatch, useStore } from "../state/store";
import { dataDrawing } from "./data-drawing";
import type { ExampleData } from "./ExampleEditor";
import { Workbench } from "./Workbench";

export function Notations(): ReactElement | null {
  const ed = useStore((x) => x.editor);
  const theme = useStore((x) => x.theme);
  const generation = useStore((x) => x.generation);
  const session = runtime.session;
  const doc = useMemo(() => (session && ed.projection ? readProjection(session.catalog, session.palette, ed.projection, ed.diagnostics, session.placeholders) : null), [session, ed.projection, ed.version, ed.diagnostics]); // eslint-disable-line react-hooks/exhaustive-deps
  // a graph input is bound to a graph of the space (`Binding --bindsPort--> Port`, `Binding --boundFrom--> Graph`); the workspace holds that graph when it is open
  const examples = useMemo(() => {
    const out: Record<string, ExampleData> = {};
    const ws = runtime.ws;
    if (!session || !doc || !ws) return out;
    const cat = session.catalog;
    for (const input of doc.inputs.filter((i) => i.kind === "graph")) {
      const bound = cat.sources(input.id, "bindsPort").flatMap((b) => cat.targets(b, "boundFrom")).map((g) => cat.name(g)).find((name) => ws.histories.has(name));
      const graph = bound ? ws.histories.get(bound)!.graph : undefined, info = bound ? ws.graphInfo(bound) : undefined;
      if (graph && info) out[input.id] = { graph, draw: dataDrawing(info, graph, rendererTheme(theme)) };
    }
    return out;
  }, [session, doc?.id, theme, generation]); // eslint-disable-line react-hooks/exhaustive-deps
  if (!session || !doc) return null;
  return (
    // keys typed in a notation are its own; the shortcuts of the view around it that use Ctrl or Cmd (undo, redo) pass
    <div style={{ height: "100%", minHeight: 0 }} onKeyDown={(e) => { if (!e.ctrlKey && !e.metaKey) e.stopPropagation(); }}>
      <Workbench doc={doc} palette={session.palette} cat={session.catalog} selected={ed.selected[0] ?? null} busy={!!ed.busy} examples={examples}
        // the properties panel works on operators: a row inside a step selects its step
        onSelect={(id) => dispatch({ type: "editor/select", ids: id ? [stepHolding(doc, id)?.id ?? id] : [] })}
        onGesture={(gesture) => dispatch({ type: "editor/gesture", gesture })} />
    </div>
  );
}
