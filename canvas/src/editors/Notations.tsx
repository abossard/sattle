// The five notations in the application's editor view, on the engine's draft: the open projection as a document, a gesture
// sent as an editor action (composed and applied by the editor effects, one undo step each), the selection shared with the
// properties panel, and for "By example" the data of the graphs the projection reads, as the workspace holds and draws them.
import { useCallback, useMemo, useState, type ReactElement } from "react";
import { allSteps, readProjection, stepHolding } from "../editing/doc";
import { columnsByStep, rowsByStep, type StepRows } from "../editing/results";
import { rendererTheme } from "../state/effects";
import { runtime } from "../state/runtime";
import { dispatch, useStore } from "../state/store";
import { dataDrawing } from "./data-drawing";
import type { ExampleData } from "./ExampleEditor";
import type { StepResults } from "./PipelineEditor";
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
  // the rows after each step: a preview of the graphs the projection produces, on demand (it runs the projection on the space's data)
  const [rows, setRows] = useState<{ projection: string; version: number; byStep: Map<string, StepRows>; notice: string } | null>(null);
  const [loading, setLoading] = useState(false);
  const load = useCallback(() => {
    if (!session || !doc) return;
    const cat = session.catalog, version = ed.version, projection = doc.id;
    const graphs = cat.sources(projection, "producedBy").map((g) => cat.name(g));
    setLoading(true);
    void session.svc.preview(session.draft.draft, { graphs, include: ["rows"], rowLimit: "20" }).then((pv) => {
      const operators = pv.graphs.flatMap((g) => g.operators ?? []);
      const errors = pv.diagnostics.filter((d) => d.severity === "error");
      const notice = operators.length ? "" : errors.length ? `The draft does not run: ${errors[0].message}` : "The engine answered no rows for this projection.";
      // what a cell holds: the kinds of the columns from the draft's check, the names of elements from the graphs the workspace holds
      const schema = session.schemas[projection] ?? [];
      const kinds = (op: string) => new Map((schema.find((o) => o.op === op)?.columns ?? []).map((c) => [c.name, c.kind]));
      const graphsHeld = runtime.ws ? [...runtime.ws.histories.values()].map((h) => h.graph) : [];
      const label = (id: string) => { for (const g of graphsHeld) { const e = g.entity(id) ?? g.rel(id); if (e) return g.label(e); } return undefined; };
      setRows({ projection, version, byStep: rowsByStep(operators, allSteps(doc).map((s) => s.id), (op) => ({ kinds: kinds(op), label })), notice });
    }, (e: Error) => setRows({ projection, version, byStep: new Map(), notice: e.message })).finally(() => setLoading(false));
  }, [session, doc, ed.version]);
  const columns = useMemo(() => (session && doc ? columnsByStep(session.schemas[doc.id] ?? [], allSteps(doc).map((s) => s.id), (id) => session.catalog.typeLabel(id, doc.module)) : new Map<string, string[]>()), [session, doc, ed.version]); // eslint-disable-line react-hooks/exhaustive-deps
  const mine = rows?.projection === doc?.id ? rows : null;
  const results: StepResults = {
    rowsAfter: (id) => mine?.byStep.get(id), columnsAfter: (id) => columns.get(id),
    load, loading, notice: mine?.notice ?? "", stale: !!mine && mine.version !== ed.version,
  };
  if (!session || !doc) return null;
  return (
    // keys typed in a notation are its own; the shortcuts of the view around it that use Ctrl or Cmd (undo, redo) pass
    <div style={{ height: "100%", minHeight: 0 }} onKeyDown={(e) => { if (!e.ctrlKey && !e.metaKey) e.stopPropagation(); }}>
      <Workbench doc={doc} palette={session.palette} cat={session.catalog} selected={ed.selected[0] ?? null} busy={!!ed.busy} examples={examples} results={results}
        // the properties panel works on operators: a row inside a step selects its step
        onSelect={(id) => dispatch({ type: "editor/select", ids: id ? [stepHolding(doc, id)?.id ?? id] : [] })}
        onGesture={(gesture) => dispatch({ type: "editor/gesture", gesture })} />
    </div>
  );
}
