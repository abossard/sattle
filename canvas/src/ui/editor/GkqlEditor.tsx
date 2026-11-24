import { Badge, Button, Field, Input, MessageBar, MessageBarBody, Select, Tab, TabList, Text, Textarea, makeStyles, mergeClasses, tokens } from "@fluentui/react-components";
import { useId, useMemo, useState } from "react";
import {
  appendVerb, authorableVerbs, canEditGkqlStructure, layoutGkql, portsFor, printGkql, readGkql, readGkqlJson, removeVerb, rewire, sourcesFor, verbStyle, VERB_WIDTH, wirePort,
  type GkqlDrawing, type GkqlProgram, type GkqlVerb, type GkqlWire,
} from "../../editor/gkql";
import type { EditorProps } from "./languages";

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateRows: "auto auto minmax(0, 1fr)", height: "100%", minHeight: 0 },
  toolbar: { display: "flex", flexWrap: "wrap", alignItems: "center", gap: "8px", padding: "6px 12px", borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  name: { color: tokens.colorNeutralForeground2, overflowWrap: "anywhere" },
  workspace: { display: "grid", gridTemplateColumns: "minmax(0, 1fr) minmax(220px, 30%)", minHeight: 0, "@media (max-width: 840px)": { gridTemplateColumns: "minmax(0, 1fr)", gridTemplateRows: "minmax(130px, .8fr) minmax(120px, 1fr)" } },
  stage: { overflow: "auto", minHeight: 0, backgroundColor: tokens.colorNeutralBackground2, backgroundImage: `radial-gradient(${tokens.colorNeutralStroke2} 1px, transparent 1px)`, backgroundSize: "16px 16px" },
  stageContents: { position: "relative" },
  empty: { margin: "18px", display: "block" },
  svg: { position: "absolute", inset: 0, pointerEvents: "none" },
  wire: { fill: "none", stroke: tokens.colorNeutralForeground3, strokeWidth: 1.5 },
  wireActive: { stroke: tokens.colorBrandForeground1, strokeWidth: 2.5 },
  cards: { listStyle: "none", margin: 0, padding: 0 },
  card: {
    position: "absolute", height: "58px", boxSizing: "border-box", textAlign: "start", display: "flex", flexDirection: "column",
    justifyContent: "center", gap: "2px", padding: "0 9px", border: `1px solid ${tokens.colorNeutralStroke1}`, borderRadius: "7px",
    backgroundColor: tokens.colorNeutralBackground1, color: tokens.colorNeutralForeground1, cursor: "pointer",
    "&:focus-visible": { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: "2px" },
  },
  active: { boxShadow: `0 0 0 2px ${tokens.colorBrandStroke1}` },
  clip: { maxWidth: "100%", textOverflow: "ellipsis", overflow: "hidden", whiteSpace: "nowrap" },
  sidebar: { display: "flex", flexDirection: "column", gap: "10px", overflow: "auto", padding: "12px", borderLeft: `1px solid ${tokens.colorNeutralStroke2}` },
  row: { display: "flex", flexWrap: "wrap", alignItems: "center", gap: "6px" },
  wireRow: { display: "grid", gridTemplateColumns: "auto minmax(0, 1fr)", alignItems: "center", gap: "6px" },
  patternItem: { display: "grid", gridTemplateColumns: "minmax(60px, .85fr) minmax(85px, 1fr) auto", alignItems: "center", gap: "6px" },
  code: { overflow: "auto", minHeight: 0, padding: "12px", display: "flex", flexDirection: "column", gap: "8px" },
  mono: { fontFamily: tokens.fontFamilyMonospace, whiteSpace: "pre", fontSize: tokens.fontSizeBase200 },
  divider: { border: "none", borderTop: `1px solid ${tokens.colorNeutralStroke2}`, width: "100%", margin: 0 },
});

const badgeColor = { input: "informative", transform: "brand", aggregate: "warning", output: "success" } as const;

function Wires({ program, selected, select }: { program: GkqlProgram; selected?: string; select: (id: string) => void }) {
  const s = useStyles();
  const layout = useMemo(() => layoutGkql(program), [program]);
  const arrowId = useId();
  return (
    <div className={s.stage} role="region" aria-label="GKQL operations and wires" tabIndex={0}>
      {!layout.cards.length ? <Text className={s.empty}>Add a graph match to start.</Text> : (
        <div className={s.stageContents} style={{ width: layout.width, height: layout.height }}>
          <svg className={s.svg} width={layout.width} height={layout.height} aria-hidden="true">
            <defs>
              <marker id={arrowId} orient="auto" markerWidth="6" markerHeight="6" refX="5" refY="3">
                <path d="M0 0 6 3 0 6Z" fill={tokens.colorNeutralForeground3} />
              </marker>
            </defs>
            {layout.wires.map(({ wire, d }, index) => <path key={index} d={d} markerEnd={`url(#${arrowId})`} className={mergeClasses(s.wire, (wire.from === selected || wire.to === selected) && s.wireActive)} />)}
          </svg>
          <ul className={s.cards}>
            {layout.cards.map(({ verb, x, y }, index) => (
              <li key={`${verb.id}/${index}`}>
                <button type="button" className={mergeClasses(s.card, verb.id === selected && s.active)} style={{ left: x, top: y, width: VERB_WIDTH }} aria-pressed={verb.id === selected} onClick={() => select(verb.id)}>
                  <Badge size="small" appearance="tint" color={badgeColor[verbStyle(verb.verb)]}>{verb.verb}</Badge>
                  <Text weight="semibold" className={s.clip} title={verb.id}>{verb.id}</Text>
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}

function Drawing({ value, target, disabled, update }: { value: GkqlDrawing; target: boolean; disabled: boolean; update: (value: GkqlDrawing) => void }) {
  const s = useStyles();
  const nodes = value.nodes ?? [];
  const edges = value.edges ?? [];
  const name = (prefix: string) => {
    const used = new Set([...nodes, ...edges].map((item) => item.var));
    let index = 1;
    while (used.has(prefix + index)) index++;
    return prefix + index;
  };
  return (
    <>
      {nodes.map((node, index) => (
        <div key={index}>
          <div className={s.patternItem}>
          <Field label="Node variable" size="small"><Input disabled={disabled} value={node.var} onChange={(_, { value: variable }) => update({
            ...value,
            nodes: nodes.map((n, i) => i === index ? { ...n, var: variable } : n),
            ...(value.edges ? { edges: edges.map((edge) => ({ ...edge, from: edge.from === node.var ? variable : edge.from, to: edge.to === node.var ? variable : edge.to })) } : {}),
          })} /></Field>
          <Field label="Node type" size="small"><Input disabled={disabled} value={node.label} onChange={(_, { value: label }) => update({ ...value, nodes: nodes.map((n, i) => i === index ? { ...n, label } : n) })} /></Field>
          <Button size="small" appearance="subtle" disabled={disabled} aria-label={`Remove node ${node.var}`} onClick={() => update({
            ...value, nodes: nodes.filter((_, i) => i !== index),
            ...(value.edges ? { edges: edges.filter((edge) => edge.from !== node.var && edge.to !== node.var) } : {}),
          })}>Remove</Button>
          </div>
          {target && (node.id == null || typeof (node.id as { ref?: unknown })?.ref === "string") && (
            <Field label="Identity reference" size="small"><Input disabled={disabled} value={(node.id as { ref?: string } | undefined)?.ref ?? ""} onChange={(_, d) => update({
              ...value, nodes: nodes.map((n, i) => i === index ? { ...n, id: d.value ? { ref: d.value } : undefined } : n),
            })} /></Field>
          )}
        </div>
      ))}
      {edges.map((edge, index) => {
        const setEdge = (patch: Partial<typeof edge>) => update({ ...value, edges: edges.map((item, i) => i === index ? { ...item, ...patch } : item) });
        return (
          <div className={s.row} key={index}>
            <Badge appearance="outline" color="informative">{edge.var}</Badge>
            <Field label="From" size="small"><Select disabled={disabled} value={edge.from} onChange={(_, data) => setEdge({ from: data.value })}>{nodes.map((node, i) => <option key={i}>{node.var}</option>)}</Select></Field>
            <Field label="Relationship type" size="small"><Input disabled={disabled} value={edge.label} onChange={(_, data) => setEdge({ label: data.value })} /></Field>
            <Field label="To" size="small"><Select disabled={disabled} value={edge.to} onChange={(_, data) => setEdge({ to: data.value })}>{nodes.map((node, i) => <option key={i}>{node.var}</option>)}</Select></Field>
            <Button size="small" appearance="subtle" disabled={disabled} aria-label={`Remove relationship ${edge.var}`} onClick={() => update({ ...value, edges: edges.filter((_, i) => i !== index) })}>Remove</Button>
          </div>
        );
      })}
      <div className={s.row}>
        <Button size="small" disabled={disabled} onClick={() => update({ ...value, nodes: [...nodes, { var: name("n"), label: "" }] })}>Add node</Button>
        <Button size="small" disabled={disabled || nodes.length < 2} onClick={() => update({ ...value, edges: [...edges, { var: name("r"), label: "", from: nodes[0].var, to: nodes[1].var }] })}>Add relationship</Button>
      </div>
    </>
  );
}

function Settings({ node, disabled, update }: { node: GkqlVerb; disabled: boolean; update: (next: GkqlVerb) => void }) {
  const s = useStyles();
  const { id, verb, ...fields } = node;
  const initial = JSON.stringify(fields, null, 2);
  const [text, setText] = useState(initial);
  const [error, setError] = useState("");
  return (
    <details>
      <summary>Operation JSON</summary>
      <Textarea size="small" aria-label="Operation JSON" rows={8} value={text} onChange={(_, d) => setText(d.value)} readOnly={disabled} textarea={{ className: s.mono }} />
      <Button size="small" disabled={disabled || text === initial} onClick={() => {
        try {
          const settings = readGkqlJson(text);
          if (!settings || typeof settings !== "object" || Array.isArray(settings)) throw new Error("Expected an object.");
          update({ ...settings, id, verb });
          setError("");
        } catch (e) { setError((e as Error).message); }
      }}>Apply operation</Button>
      {error && <Text role="alert">{error}</Text>}
    </details>
  );
}

const thresholdFields = {
  above: "Enter at or above",
  enterAfter: "Enter after windows",
  below: "Exit at or below",
  exitAfter: "Exit after windows",
};

function ThresholdControls({ node, readOnly, update }: { node: GkqlVerb; readOnly: boolean; update: (node: GkqlVerb) => void }) {
  const s = useStyles();
  const thresholds = node.hysteresis as Record<string, unknown> | undefined;
  const initial = Object.fromEntries(Object.keys(thresholdFields).map((field) => [field, JSON.stringify(thresholds?.[field]) ?? ""]));
  const [values, setValues] = useState(initial);
  if (!thresholds) return null;
  const valid = Object.values(values).every((value) => /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:e[+-]?\d+)?$/i.test(value) && canEditGkqlStructure(value))
    && [values.enterAfter, values.exitAfter].every((value) => /^[1-9]\d*$/.test(value));
  return (
    <>
      <div className={s.row}>{Object.entries(thresholdFields).map(([field, label]) => <Field label={label} size="small" key={field}>
        <Input disabled={readOnly} inputMode={field.endsWith("After") ? "numeric" : "decimal"} value={values[field]} onChange={(_, d) => setValues({ ...values, [field]: d.value })} />
      </Field>)}</div>
      <Button size="small" disabled={readOnly || !valid || JSON.stringify(values) === JSON.stringify(initial)} onClick={() => update({
        ...node, hysteresis: { ...thresholds, ...Object.fromEntries(Object.entries(values).map(([field, value]) => [field, readGkqlJson(value)])) },
      })}>Apply thresholds</Button>
    </>
  );
}

function Operation({ program, node, readOnly, change }: { program: GkqlProgram; node: GkqlVerb; readOnly: boolean; change: (program: GkqlProgram) => void }) {
  const s = useStyles();
  const update = (next: GkqlVerb) => change({ ...program, verbs: program.verbs.map((verb) => verb.id === node.id ? next : verb) });
  const available = sourcesFor(program, node.id);
  const incoming = program.wires.filter((wire) => wire.to === node.id);
  const connect = (port: string, previous?: GkqlWire, index = 0) => (
    <Field key={`${port}/${index}`} label={port === "in" ? "Input operation" : `${port === "left" ? "Left" : port === "right" ? "Right" : port} input`} size="small" orientation="horizontal" className={s.wireRow}>
      <Select disabled={readOnly} value={previous?.from ?? ""} onChange={(_, d) => change(rewire(program, node.id, port, d.value, previous))}>
        <option value="">Unconnected</option>
        {previous && !available.some((verb) => verb.id === previous.from) && <option value={previous.from}>{previous.from}</option>}
        {available.map((verb, i) => <option key={i} value={verb.id}>{verb.id} · {verb.verb}</option>)}
      </Select>
    </Field>
  );
  const kind = node.pattern ? "pattern" : node.target ? "target" : null;
  const ports = [...new Set([...portsFor(node.verb), ...incoming.map(wirePort)])];
  const condition = node.verb === "where" ? node.condition as { fn?: string; args?: [{ ref?: string }, { lit?: unknown }] } | null | undefined : null;
  const [reference, value] = Array.isArray(condition?.args) && condition.args.length === 2 ? condition.args : [];
  return (
    <>
      <Text as="h3" weight="semibold" size={400}>{node.id}</Text>
      <Badge size="small" appearance="tint" color={badgeColor[verbStyle(node.verb)]}>{node.verb}</Badge>
      {typeof node.graph === "string" && (
        <Field label="Source graph" size="small"><Select disabled={readOnly} value={node.graph} onChange={(_, d) => update({ ...node, graph: d.value })}>
          <option value="">Choose an input</option>{program.inputs.filter((input) => input.ontology).map((input) => <option key={input.name} value={input.name}>{input.name}</option>)}
        </Select></Field>
      )}
      {typeof node.source === "string" && (
        <Field label="Source rows" size="small"><Select disabled={readOnly} value={node.source} onChange={(_, d) => update({ ...node, source: d.value })}>
          <option value="">Choose an input</option>{program.inputs.filter((input) => input.rows).map((input) => <option key={input.name} value={input.name}>{input.name}</option>)}
        </Select></Field>
      )}
      {ports.map((port) => {
        const onPort = incoming.filter((wire) => wirePort(wire) === port);
        return onPort.length === 0 ? connect(port) : onPort.map((wire, index) => connect(port, wire, index));
      })}
      {node.verb === "union" && incoming.length > 0 && connect("in", undefined, incoming.length)}
      {typeof reference?.ref === "string" && typeof value?.lit === "string" && (
        <Field label="Filter value" hint={`${reference.ref} ${condition?.fn ?? ""}`} size="small"><Input disabled={readOnly} value={value.lit} onChange={(_, d) => update({
          ...node, condition: { ...condition, args: [reference, { ...value, lit: d.value }] },
        })} /></Field>
      )}
      {kind && (
        <>
          <hr className={s.divider} />
          <Text weight="semibold">{kind === "pattern" ? "Match pattern" : "Build output"}</Text>
          <Drawing value={node[kind]!} target={kind === "target"} disabled={readOnly} update={(next) => update({ ...node, [kind]: next })} />
        </>
      )}
      {node.verb === "hysteresis" && <ThresholdControls key={JSON.stringify(node.hysteresis)} node={node} readOnly={readOnly} update={update} />}
      <Settings key={JSON.stringify(node)} node={node} disabled={readOnly} update={update} />
      <Button appearance="subtle" size="small" disabled={readOnly} onClick={() => change(removeVerb(program, node.id))}>Remove operation</Button>
    </>
  );
}

export default function GkqlEditor({ file, source, initialTab = "wires", starter, recoveredText, diagnostics, store, onEdit }: EditorProps) {
  const s = useStyles();
  const [edited, setEdited] = useState<string | null>(recoveredText ?? null);
  const [selected, select] = useState<string>();
  const [view, setView] = useState<string>(initialTab);
  const [newVerb, chooseVerb] = useState("graph-match");
  const [status, setStatus] = useState<{ phase: "idle" | "checking" | "checked" } | { phase: "error"; message: string }>({ phase: "idle" });
  const text = edited ?? source ?? "";
  const parsed = useMemo(() => {
    if (!text) return null;
    try { return { program: readGkql(text) }; }
    catch (error) { return { error: (error as Error).message }; }
  }, [text]);
  const program = parsed?.program;
  const node = program?.verbs.find((v) => v.id === selected) ?? program?.verbs[0];
  const changed = edited !== null && text !== source;
  const structureEditable = canEditGkqlStructure(text);
  const checking = status.phase === "checking";
  const readOnly = !store || checking;
  const projectionId = `d:${file.projection.slice(0, file.projection.lastIndexOf("."))}/projection:${file.projection.split(".").at(-1)}`;
  const problems = diagnostics?.filter((d) => d.file === file.file || d.focus === projectionId || d.focus.startsWith(projectionId + "/")) ?? [];
  const edit = (value: string) => { setEdited(value); onEdit?.(value); setStatus({ phase: "idle" }); };
  const change = (next: GkqlProgram) => edit(printGkql(next));

  async function checkAndStore() {
    if (!store) return;
    setStatus({ phase: "checking" });
    try {
      const error = await store(text);
      if (error) { setStatus({ phase: "error", message: error }); return; }
      setEdited(null);
      setStatus({ phase: "checked" });
    } catch (error) { setStatus({ phase: "error", message: (error as Error).message }); }
  }
  const error = (status.phase === "error" ? status.message : "") || parsed?.error;

  return (
    <div className={s.root}>
      <div className={s.toolbar}>
        <TabList size="small" aria-label="GKQL views" selectedValue={view} onTabSelect={(_, { value }) => setView(String(value))}><Tab value="wires">Graph</Tab><Tab value="source">GKQL</Tab></TabList>
        <Text font="monospace" size={200} className={s.name}>{program?.graph ?? file.projection}</Text>
        {program && view === "wires" && <><Field label="Operation" size="small" orientation="horizontal"><Select disabled={readOnly || !structureEditable} value={newVerb} onChange={(_, d) => chooseVerb(d.value)}>{authorableVerbs.map((verb) => <option key={verb}>{verb}</option>)}</Select></Field>
          <Button size="small" disabled={readOnly || !structureEditable} onClick={() => { const next = appendVerb(program, newVerb); change(next); select(next.verbs.at(-1)?.id); }}>Add operation</Button></>}
        {starter && !readOnly && !changed && program?.verbs.length === 0 && <Button size="small" onClick={() => { edit(starter); setView("source"); }}>Load starter GKQL</Button>}
        {store && <Button size="small" appearance="primary" disabled={!program || !changed || checking} onClick={() => void checkAndStore()}>{checking ? "Checking…" : "Check & store"}</Button>}
        {changed && <Text size={200}>Local changes</Text>}
        {status.phase === "checked" && problems.length === 0 && <Badge color="success" appearance="tint">Projection checked</Badge>}
      </div>
      <div aria-live="polite">
        {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
        {!changed && problems.length > 0 && <MessageBar intent="warning"><MessageBarBody>{problems[0].message}</MessageBarBody></MessageBar>}
        {!structureEditable && view === "wires" && <MessageBar intent="info"><MessageBarBody>Use the GKQL tab to preserve exact numbers in this browser.</MessageBarBody></MessageBar>}
      </div>
      {view === "source" || !program ? (
        <div className={s.code}><Textarea resize="vertical" rows={16} aria-label="GKQL source" value={text} readOnly={readOnly} onChange={(_, d) => edit(d.value)} textarea={{ className: s.mono }} /></div>
      ) : (
        <div className={s.workspace}>
          <Wires program={program} selected={node?.id} select={select} />
          <section className={s.sidebar} aria-label={node ? `Edit ${node.id}` : "Projection inputs"}>
            {node ? <Operation program={program} node={node} change={change} readOnly={readOnly || !structureEditable} /> : <Text weight="semibold">Graph inputs</Text>}
            {program.inputs.length > 0 && <div className={s.row}>{program.inputs.map((input) => <Badge key={input.name} color="informative" appearance="outline">{input.name}</Badge>)}</div>}
          </section>
        </div>
      )}
    </div>
  );
}
