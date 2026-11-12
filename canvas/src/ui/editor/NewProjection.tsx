// A new projection: name, output ontology, graph inputs, parameters and the graph it produces in the space. Operators are added afterwards.
import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, MessageBar, MessageBarBody, Select, Text, makeStyles, tokens } from "@fluentui/react-components";
import { useState } from "react";
import type { ProjectionSpec } from "../../editor/structures";
import { runtime } from "../../state/runtime";
import { dispatch, useStore } from "../../state/store";

const useStyles = makeStyles({ row: { display: "flex", columnGap: tokens.spacingHorizontalS, alignItems: "end", flexWrap: "wrap" }, col: { display: "flex", flexDirection: "column", rowGap: tokens.spacingVerticalS } });

export function NewProjectionDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
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
  const [purpose, setPurpose] = useState("view");
  const [retain, setRetain] = useState("");
  if (!session) return null;
  const g = session.graph;
  const modules = [...g.entities()].filter((e) => e.type === "Module" && e.id !== "d:sattle.kernel").map((e) => ({ id: e.id, name: e.id.slice(2) }));
  const spaceGraphs = [...g.entities()].filter((e) => e.type === "Graph").map((e) => e.id.slice(e.id.lastIndexOf(":") + 1));
  const rowTypes = [...g.entities()].filter((e) => e.type === "RowType").map((e) => ({ id: e.id, name: e.id.slice(2) }));
  const sources = [...g.entities()].filter((e) => e.type === "Source").map((e) => ({ id: e.id, name: e.id.slice(e.id.lastIndexOf(":") + 1) }));
  const purposes = session.palette.declarations.projection?.params.find((p) => p.name === "purpose")?.values ?? ["view"];
  const kinds = session.palette.projectionPorts.find((x) => x.kind === "param")?.text.match(/<([^>]+)>/)?.[1].split("|") ?? ["Text", "Integer", "Decimal", "Boolean"];
  const graphName = typedGraph ?? name;
  const upd = <T,>(list: T[], i: number, patch: Partial<T>, set: (l: T[]) => void) => set(list.map((x, n) => (n === i ? { ...x, ...patch } : x)));
  return (
    <Dialog open={open} onOpenChange={(_, d) => { if (!d.open) onClose(); }}>
      <DialogSurface><DialogBody>
        <DialogTitle>New projection in {ed.module}</DialogTitle>
        <DialogContent className={s.col}>
          <div className={s.row}>
            <Field label="Name"><Input value={name} onChange={(_, d) => setName(d.value)} /></Field>
            <Field label="Graph it produces in the space"><Input value={graphName} onChange={(_, d) => setTypedGraph(d.value)} /></Field>
            <Field label="Purpose"><Select value={purpose} onChange={(_, d) => setPurpose(d.value)}>{purposes.map((p) => <option key={p}>{p}</option>)}</Select></Field>
            <Field label="Retain (revisions)"><Input value={retain} onChange={(_, d) => setRetain(d.value)} /></Field>
            <Field label="Output ontology"><Select value={output} onChange={(_, d) => setOutput(d.value)}><option value="">(choose)</option>{modules.map((m) => <option key={m.id} value={m.id}>{m.name}</option>)}</Select></Field>
          </div>
          <Field label="Description (optional)"><Input value={description} onChange={(_, d) => setDescription(d.value)} /></Field>
          <Text weight="semibold">Graph inputs</Text>
          {inputs.map((i, n) => (
            <div key={n} className={s.row}>
              <Field label="Port"><Input value={i.port} onChange={(_, d) => upd(inputs, n, { port: d.value }, setInputs)} /></Field>
              <Field label="Fed by graph"><Select value={i.graph} onChange={(_, d) => upd(inputs, n, { graph: d.value }, setInputs)}>{spaceGraphs.map((x) => <option key={x}>{x}</option>)}</Select></Field>
              <Field label="Expects ontology"><Select value={i.ontology} onChange={(_, d) => upd(inputs, n, { ontology: d.value }, setInputs)}><option value="">(choose)</option>{modules.map((m) => <option key={m.id} value={m.id}>{m.name}</option>)}</Select></Field>
              <Button onClick={() => setInputs(inputs.filter((_, k) => k !== n))}>Remove</Button>
            </div>
          ))}
          <Button onClick={() => setInputs([...inputs, { port: "", graph: spaceGraphs[0] ?? "", ontology: "" }])}>Add graph input</Button>
          <Text weight="semibold">Rows inputs (fed by a source of the space)</Text>
          {rows.map((r, n) => (
            <div key={n} className={s.row}>
              <Field label="Port"><Input value={r.port} onChange={(_, d) => upd(rows, n, { port: d.value }, setRows)} /></Field>
              <Field label="Row type"><Select value={r.rowType} onChange={(_, d) => upd(rows, n, { rowType: d.value }, setRows)}><option value="">(choose)</option>{rowTypes.map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}</Select></Field>
              <Field label="Fed by source"><Select value={r.source} onChange={(_, d) => upd(rows, n, { source: d.value }, setRows)}><option value="">(choose)</option>{sources.map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}</Select></Field>
              <Button onClick={() => setRows(rows.filter((_, k) => k !== n))}>Remove</Button>
            </div>
          ))}
          <Button onClick={() => setRows([...rows, { port: "", rowType: "", source: "" }])}>Add rows input</Button>
          <Text weight="semibold">Parameters (a default, a value bound in the space, or both; usable by name in expressions)</Text>
          {params.map((p, n) => (
            <div key={n} className={s.row}>
              <Field label="Name"><Input value={p.port} onChange={(_, d) => upd(params, n, { port: d.value }, setParams)} /></Field>
              <Field label="Kind"><Select value={p.kind} onChange={(_, d) => upd(params, n, { kind: d.value }, setParams)}>{kinds.map((k) => <option key={k}>{k}</option>)}</Select></Field>
              <Field label="Default"><Input value={p.default ?? ""} onChange={(_, d) => upd(params, n, { default: d.value }, setParams)} /></Field>
              <Field label="Value bound in the space"><Input value={p.value} onChange={(_, d) => upd(params, n, { value: d.value }, setParams)} /></Field>
              <Button onClick={() => setParams(params.filter((_, k) => k !== n))}>Remove</Button>
            </div>
          ))}
          <Button onClick={() => setParams([...params, { port: "", kind: "Integer", value: "" }])}>Add parameter</Button>
          {ed.notice && <MessageBar intent="error"><MessageBarBody>{ed.notice}</MessageBarBody></MessageBar>}
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose}>Cancel</Button>
          <Button appearance="primary" onClick={() => { dispatch({ type: "editor/do", cmd: { t: "newProjection", spec: { module: ed.module, name, description, outputModule: output, graphName, inputs, params, purpose, rows, retain } } }); onClose(); }}>Create</Button>
        </DialogActions>
      </DialogBody></DialogSurface>
    </Dialog>
  );
}
