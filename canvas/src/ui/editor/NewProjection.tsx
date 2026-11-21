// Create a projection and bind its inputs to graphs and sources in the space.
import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, MessageBar, MessageBarBody, Select, Text, makeStyles, tokens } from "@fluentui/react-components";
import { useState, type FormEvent } from "react";
import { newProjection, PARAM_KINDS, type ProjectionSpec } from "../../editor/structures";
import { runtime } from "../../state/runtime";
import { dispatch, useStore } from "../../state/store";

const useStyles = makeStyles({ row: { display: "flex", columnGap: tokens.spacingHorizontalS, alignItems: "end", flexWrap: "wrap" }, col: { display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalS },
  group: { display: "flex", flexDirection: "column", alignItems: "start", rowGap: tokens.spacingVerticalS, border: "none", margin: 0, padding: 0 } });

export function NewProjectionDialog({ open, onClose, native = false }: { open: boolean; onClose: () => void; native?: boolean }) {
  const s = useStyles();
  const ed = useStore((x) => x.editor);
  const session = runtime.session;
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [output, setOutput] = useState("");
  // the graph is named like the projection until the person names it otherwise
  const [typedGraph, setTypedGraph] = useState<string | null>(null);
  const [inputs, setInputs] = useState<ProjectionSpec["inputs"]>([{ port: "master", graph: "master", ontology: "" }]);
  const [params, setParams] = useState<ProjectionSpec["params"]>([]);
  const [rows, setRows] = useState<NonNullable<ProjectionSpec["rows"]>>([]);
  const [history, setHistory] = useState("");
  // why the projection cannot be made, shown when Create is pressed and cleared by the next change
  const [reason, setReason] = useState("");
  if (!session) return null;
  const g = session.graph;
  const modules = [...g.entities()].filter((e) => e.type === "Module" && e.id !== "d:sattle.kernel").map((e) => ({ id: e.id, name: e.id.slice(2) }));
  const spaceGraphs = [...g.entities()].filter((e) => e.type === "Graph").map((e) => e.id.slice(e.id.lastIndexOf(":") + 1));
  const rowTypes = [...g.entities()].filter((e) => e.type === "RowType").map((e) => ({ id: e.id, name: e.id.slice(2) }));
  const sources = [...g.entities()].filter((e) => e.type === "Source").map((e) => ({ id: e.id, name: e.id.slice(e.id.lastIndexOf(":") + 1) }));
  const kinds = Object.keys(PARAM_KINDS);
  const graphName = typedGraph ?? name;
  const upd = <T,>(list: T[], i: number, patch: Partial<T>, set: (l: T[]) => void) => set(list.map((x, n) => (n === i ? { ...x, ...patch } : x)));
  const create = (e: FormEvent) => {
    e.preventDefault();
    const parameters = native ? params.map((p) => ({ ...p, value: "" })) : params;
    const spec: ProjectionSpec = { module: ed.module, name, description, outputModule: output, graphName, inputs, params: parameters, rows, history, language: native ? "gkql" : "kql" };
    const r = newProjection(g, spec, session.texts[ed.module]);
    if (!r.ok) { setReason(r.reason); return; }
    dispatch({ type: "editor/do", cmd: { t: "newProjection", spec } });
    onClose();
  };
  return (
    <Dialog open={open} onOpenChange={(_, d) => { if (!d.open) onClose(); }}>
      <DialogSurface><form onSubmit={create} onChange={() => setReason("")}><DialogBody>
        <DialogTitle>New projection in {ed.module}</DialogTitle>
        <DialogContent className={s.col}>
          <div className={s.row}>
            <Field label="Name" required><Input name="name" value={name} onChange={(_, d) => setName(d.value)} autoComplete="off" spellCheck={false} /></Field>
            <Field label="Graph it produces in the space" required><Input name="graph" value={graphName} onChange={(_, d) => setTypedGraph(d.value)} autoComplete="off" spellCheck={false} /></Field>
            <Field label="History (inputs kept per element)" hint="Optional, a whole number"><Input name="history" inputMode="numeric" value={history} onChange={(_, d) => setHistory(d.value)} autoComplete="off" /></Field>
            <Field label="Output ontology" required><Select name="output" value={output} onChange={(_, d) => setOutput(d.value)}><option value="">(choose)</option>{modules.map((m) => <option key={m.id} value={m.id}>{m.name}</option>)}</Select></Field>
          </div>
          <Field label="Description (optional)"><Input value={description} onChange={(_, d) => setDescription(d.value)} /></Field>
          <fieldset className={s.group}><legend><Text weight="semibold">Graph inputs</Text></legend>
          {inputs.map((i, n) => (
            <div key={n} className={s.row}>
              <Field label="Port"><Input value={i.port} onChange={(_, d) => upd(inputs, n, { port: d.value }, setInputs)} /></Field>
              <Field label="Fed by graph"><Select value={i.graph} onChange={(_, d) => upd(inputs, n, { graph: d.value }, setInputs)}>{spaceGraphs.map((x) => <option key={x}>{x}</option>)}</Select></Field>
              <Field label="Expects ontology"><Select value={i.ontology} onChange={(_, d) => upd(inputs, n, { ontology: d.value }, setInputs)}><option value="">(choose)</option>{modules.map((m) => <option key={m.id} value={m.id}>{m.name}</option>)}</Select></Field>
              <Button type="button" onClick={() => setInputs(inputs.filter((_, k) => k !== n))}>Remove</Button>
            </div>
          ))}
          <Button type="button" onClick={() => setInputs([...inputs, { port: "", graph: spaceGraphs[0] ?? "", ontology: "" }])}>Add graph input</Button>
          </fieldset>
          <fieldset className={s.group}><legend><Text weight="semibold">Rows inputs (fed by a source of the space)</Text></legend>
          {rows.map((r, n) => (
            <div key={n} className={s.row}>
              <Field label="Port"><Input value={r.port} onChange={(_, d) => upd(rows, n, { port: d.value }, setRows)} /></Field>
              <Field label="Row type"><Select value={r.rowType} onChange={(_, d) => upd(rows, n, { rowType: d.value }, setRows)}><option value="">(choose)</option>{rowTypes.map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}</Select></Field>
              <Field label="Fed by source"><Select value={r.source} onChange={(_, d) => upd(rows, n, { source: d.value }, setRows)}><option value="">(choose)</option>{sources.map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}</Select></Field>
              <Button type="button" onClick={() => setRows(rows.filter((_, k) => k !== n))}>Remove</Button>
            </div>
          ))}
          <Button type="button" onClick={() => setRows([...rows, { port: "", rowType: "", source: "" }])}>Add rows input</Button>
          </fieldset>
          <fieldset className={s.group}><legend><Text weight="semibold">{native ? "Parameters" : "Parameters (a default, a value bound in the space, or both; usable by name in expressions)"}</Text></legend>
          {params.map((p, n) => (
            <div key={n} className={s.row}>
              <Field label="Parameter"><Input value={p.port} onChange={(_, d) => upd(params, n, { port: d.value }, setParams)} /></Field>
              <Field label="Type"><Select value={p.kind} onChange={(_, d) => upd(params, n, { kind: d.value }, setParams)}>{kinds.map((k) => <option key={k} value={k}>{native ? PARAM_KINDS[k] : k}</option>)}</Select></Field>
              <Field label="Default"><Input value={p.default ?? ""} onChange={(_, d) => upd(params, n, { default: d.value }, setParams)} /></Field>
              {!native && <Field label="Value bound in the space"><Input value={p.value} onChange={(_, d) => upd(params, n, { value: d.value }, setParams)} /></Field>}
              <Button type="button" onClick={() => setParams(params.filter((_, k) => k !== n))}>Remove</Button>
            </div>
          ))}
          <Button type="button" onClick={() => setParams([...params, { port: "", kind: "long", value: "" }])}>Add parameter</Button>
          </fieldset>
          <div aria-live="polite">{reason && <MessageBar intent="error"><MessageBarBody>{reason}</MessageBarBody></MessageBar>}</div>
        </DialogContent>
        <DialogActions>
          <Button type="button" onClick={onClose}>Cancel</Button>
          <Button type="submit" appearance="primary">Create projection</Button>
        </DialogActions>
      </DialogBody></form></DialogSurface>
    </Dialog>
  );
}
