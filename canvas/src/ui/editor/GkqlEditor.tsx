import { Badge, Button, Field, Input, Menu, MenuGroup, MenuGroupHeader, MenuItem, MenuList, MenuPopover, MenuTrigger, MessageBar, MessageBarBody, Select, Tab, TabList, Text, Textarea, makeStyles, tokens } from "@fluentui/react-components";
import { Add20Regular, ArrowRedo20Regular, ArrowUndo20Regular } from "@fluentui/react-icons";
import { useEffect, useMemo, useReducer, useRef, useState, type KeyboardEvent } from "react";
import {
  appendVerb, authorableVerbs, canEditGkqlStructure, checkGkql, connect, focusOf, layoutGkql, portsFor, readGkqlJson, rewire, sourcesFor, spanOf, verbAt, verbFocus, verbStyle, wirePort,
  type GkqlDrawing, type GkqlProgram, type GkqlVerb, type GkqlWire,
} from "../../editor/gkql";
import { openModel, step } from "../../editor/gkql-editing";
import { GkqlGraph } from "./GkqlGraph";
import { GkqlOutline } from "./GkqlOutline";
import type { EditorProps } from "./languages";

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateRows: "auto auto minmax(0, 1fr)", height: "100%", minHeight: 0 },
  toolbar: { display: "flex", flexWrap: "wrap", alignItems: "center", gap: "8px", padding: "4px 12px", borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  name: { color: tokens.colorNeutralForeground2, overflowWrap: "anywhere", marginRight: "8px" },
  grow: { flexGrow: 1 },
  messages: { display: "flex", flexDirection: "column", gap: "4px", "&:not(:empty)": { padding: "4px 12px" } },
  workspace: {
    display: "grid", gridTemplateColumns: "minmax(160px, 200px) minmax(0, 1fr) minmax(260px, 32%)", minHeight: 0,
    "@media (max-width: 840px)": { gridTemplateColumns: "minmax(0, 1fr)", gridTemplateRows: "minmax(160px, 1fr) minmax(96px, auto) minmax(160px, 1fr)", overflow: "auto" },
  },
  sidebar: { display: "flex", flexDirection: "column", gap: "8px", overflow: "auto", minHeight: 0, padding: "8px 12px", borderLeft: `1px solid ${tokens.colorNeutralStroke2}` },
  problems: { display: "flex", flexDirection: "column", gap: "4px", paddingBottom: "8px", borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  problemList: { listStyle: "none", margin: 0, padding: 0 },
  problem: { justifyContent: "start", textAlign: "start", color: tokens.colorPaletteRedForeground1, minWidth: 0, fontWeight: tokens.fontWeightRegular },
  row: { display: "flex", flexWrap: "wrap", alignItems: "center", gap: "6px" },
  wireRow: { display: "grid", gridTemplateColumns: "auto minmax(0, 1fr)", alignItems: "center", gap: "6px" },
  patternItem: { display: "grid", gridTemplateColumns: "minmax(60px, .85fr) minmax(85px, 1fr) auto", alignItems: "center", gap: "6px" },
  code: { display: "flex", flexDirection: "column", minHeight: "240px", flexGrow: 1 },
  text: { flexGrow: 1, display: "flex" },
  mono: { fontFamily: tokens.fontFamilyMonospace, whiteSpace: "pre", fontSize: tokens.fontSizeBase200, overflowWrap: "normal", overflowX: "auto", maxHeight: "none", flexGrow: 1 },
  divider: { border: "none", borderTop: `1px solid ${tokens.colorNeutralStroke2}`, width: "100%", margin: 0 },
});

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

function Operation({ program, node, readOnly, change, remove }: { program: GkqlProgram; node: GkqlVerb; readOnly: boolean; change: (program: GkqlProgram) => void; remove: () => void }) {
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
      <Text as="h2" weight="semibold" size={400}>{node.id}</Text>
      <Badge size="small" appearance="outline" color="informative">{node.verb}</Badge>
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
      <Button appearance="subtle" size="small" disabled={readOnly} onClick={remove}>Remove operation</Button>
    </>
  );
}

/** The first offset where two texts differ. */
const firstDifference = (a: string, b: string) => { let i = 0; while (i < a.length && i < b.length && a[i] === b[i]) i++; return i; };

const ADD_GROUPS = [["input", "Read"], ["transform", "Transform"], ["aggregate", "Combine"], ["output", "Write"]] as const;
const ARROWS: Record<string, [number, number]> = { ArrowLeft: [-16, 0], ArrowRight: [16, 0], ArrowUp: [0, -16], ArrowDown: [0, 16] };

/** A selected wire: where it runs, and its removal. */
function WirePanel({ focus, readOnly, remove }: { focus: string; readOnly: boolean; remove: () => void }) {
  const [, from, to, port] = /^wire:(.+)>(.+)\.([^.]+)$/.exec(focus) ?? [];
  return (
    <>
      <Text as="h2" weight="semibold" size={400}>Wire</Text>
      <Text>{from} feeds {port === "in" ? to : `the ${port} input of ${to}`}.</Text>
      <Button appearance="subtle" size="small" disabled={readOnly} onClick={remove}>Remove wire</Button>
    </>
  );
}

/**
 * The editor of a GKQL projection: the outline (steps), the graph and a side panel with the selected element and the program text, all
 * showing one model (`gkql-editing.ts`). Problems from the editor's own check and from the engine's last check sit on the element they name.
 */
export default function GkqlEditor({ file, source, initialTab = "wires", starter, recoveredText, diagnostics, store, onEdit }: EditorProps) {
  const s = useStyles();
  const [model, dispatch] = useReducer(step, recoveredText ?? source ?? "", openModel);
  const [side, setSide] = useState<string>(initialTab === "source" ? "source" : "operation");
  const [status, setStatus] = useState<{ phase: "idle" | "checking" | "checked" } | { phase: "error"; message: string }>({ phase: "idle" });
  const textArea = useRef<HTMLTextAreaElement>(null);
  const fromText = useRef(false);
  const loading = useRef(false);
  const noted = useRef(model.text);
  const seen = useRef(source);
  const root = useRef<HTMLDivElement>(null);
  const caretAt = useRef<number | null>(null);
  // a removal unmounts the control that had focus: focus moves to the graph
  const removeAndFocus = (focus: string) => { dispatch({ t: "remove", focus }); root.current?.querySelector<HTMLElement>("[aria-label='GKQL operations and wires']")?.focus(); };
  const { text, program } = model;
  const changed = text !== (source ?? "");
  const checking = status.phase === "checking";
  const readOnly = !store || checking;
  const structureEditable = canEditGkqlStructure(text);
  const frozen = readOnly || !structureEditable || model.error !== null;
  const projectionId = `d:${file.projection.slice(0, file.projection.lastIndexOf("."))}/projection:${file.projection.split(".").at(-1)}`;

  // a source that arrives while nothing was edited becomes the text; every other change of the text is the author's
  useEffect(() => {
    const before = seen.current;
    seen.current = source;
    if (source !== undefined && source !== before && model.text === (before ?? "")) { loading.current = true; dispatch({ t: "load", text: source }); }
  }, [source]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (model.text === noted.current) return;
    noted.current = model.text;
    if (loading.current) loading.current = false;
    else { onEdit?.(model.text); setStatus({ phase: "idle" }); }
  }, [model.text, onEdit]);

  const problems = useMemo(() => {
    if (!program) return [];
    const engine = changed ? [] : (diagnostics ?? []).filter((d) => d.file === file.file || d.focus === projectionId || d.focus.startsWith(projectionId + "/"));
    return [...checkGkql(program), ...engine.map((d) => ({ focus: focusOf(program, projectionId, d.focus), message: d.message }))];
  }, [program, changed, diagnostics, file.file, projectionId]);
  const byFocus = useMemo(() => {
    const map = new Map<string, string[]>();
    for (const p of problems) if (p.focus) map.set(p.focus, [...(map.get(p.focus) ?? []), p.message]);
    return map;
  }, [problems]);

  // undo and redo in the text keep the caret where the text changed
  useEffect(() => {
    if (caretAt.current === null || !textArea.current) return;
    textArea.current.setSelectionRange(caretAt.current, caretAt.current);
    caretAt.current = null;
  }, [model.text]);
  // the verb selected elsewhere shows in the text: the caret moves to its object, scrolled into view
  useEffect(() => {
    if (fromText.current) { fromText.current = false; return; }
    const area = textArea.current;
    if (side !== "source" || !area || !model.selected?.startsWith("verb:")) return;
    const span = spanOf(text, model.selected.slice(5));
    if (!span) return;
    area.setSelectionRange(span[0], span[0]);
    area.scrollTop = Math.max(0, (text.slice(0, span[0]).split("\n").length - 2) * (parseFloat(getComputedStyle(area).lineHeight) || 16));
  }, [model.selected, side]); // eslint-disable-line react-hooks/exhaustive-deps

  async function checkAndStore() {
    if (!store) return;
    setStatus({ phase: "checking" });
    try {
      const error = await store(text);
      setStatus(error ? { phase: "error", message: error } : { phase: "checked" });
    } catch (error) { setStatus({ phase: "error", message: (error as Error).message }); }
  }
  const add = (verb: string) => {
    if (!program) return;
    let next = appendVerb(program, verb);
    const made = next.verbs.at(-1)!.id;
    const feeding = model.selected?.startsWith("verb:") ? model.selected.slice(5) : null;
    const wired = feeding ? connect(next, feeding, made) : null;
    if (wired && typeof wired !== "string") next = wired;
    dispatch({ t: "change", program: next, select: verbFocus(made) });
  };
  const caret = () => {
    const area = textArea.current;
    if (!program || !area) return;
    const id = verbAt(text, program, area.selectionStart);
    if (id && verbFocus(id) !== model.selected) { fromText.current = true; dispatch({ t: "select", focus: verbFocus(id) }); }
  };
  const key = (e: KeyboardEvent<HTMLDivElement>) => {
    const target = e.target as HTMLElement;
    const field = target.closest("input, textarea, select, [contenteditable], [role=menu]");
    const mod = e.ctrlKey || e.metaKey, k = e.key.toLowerCase();
    if (mod && (k === "z" || k === "y") && (!field || target === textArea.current)) {
      e.preventDefault();
      if (readOnly) return;
      const redo = k === "y" || e.shiftKey;
      const next = redo ? model.future[0] : model.past.at(-1);
      if (next !== undefined && target === textArea.current) caretAt.current = firstDifference(text, next);
      dispatch({ t: redo ? "redo" : "undo" });
      return;
    }
    // Delete and Alt+arrows act on the focused card or step (or the selection when the graph itself has focus), never from other controls
    const view = target.closest("[aria-label='GKQL operations and wires'], [role=listbox]");
    if (field || mod || !view) return;
    const owner = target.closest<HTMLElement>("[data-verb], [data-step]");
    const focused = owner ? verbFocus(owner.dataset.verb ?? owner.dataset.step!) : model.selected;
    if (e.key === "Escape" && model.gesture.t === "connecting") { e.preventDefault(); dispatch({ t: "connect-cancel" }); }
    else if ((e.key === "Delete" || e.key === "Backspace") && focused && !frozen) {
      e.preventDefault();
      dispatch({ t: "remove", focus: focused });
      // the removed element had focus: keep it in the view it was removed from
      (view as HTMLElement).focus();
    }
    else if (e.altKey && ARROWS[e.key] && program && focused?.startsWith("verb:")) {
      const card = layoutGkql(program, model.moved).cards.find((c) => verbFocus(c.verb.id) === focused);
      if (!card) return;
      e.preventDefault();
      dispatch({ t: "move", id: card.verb.id, to: { x: card.x + ARROWS[e.key][0], y: card.y + ARROWS[e.key][1] } });
    }
  };

  const selectedVerb = model.selected?.startsWith("verb:") ? program?.verbs.find((v) => verbFocus(v.id) === model.selected) : undefined;
  const error = status.phase === "error" ? status.message : null;
  const sourceView = (
    <div className={s.code}>
      <Textarea resize="none" aria-label="GKQL source" value={text} readOnly={readOnly} className={s.text}
        textarea={{ ref: textArea, className: s.mono, spellCheck: false }} onChange={(_, d) => dispatch({ t: "type", text: d.value })} onSelect={caret} />
    </div>
  );
  return (
    <div ref={root} className={s.root} onKeyDown={key}>
      <div className={s.toolbar}>
        <Text font="monospace" size={200} className={s.name}>{program?.graph ?? file.projection}</Text>
        <Button size="small" appearance="subtle" icon={<ArrowUndo20Regular />} disabled={readOnly || !model.past.length} onClick={() => dispatch({ t: "undo" })} aria-label="Undo edit" title="Undo the last edit of this projection (Ctrl+Z)" />
        <Button size="small" appearance="subtle" icon={<ArrowRedo20Regular />} disabled={readOnly || !model.future.length} onClick={() => dispatch({ t: "redo" })} aria-label="Redo edit" title="Redo (Ctrl+Shift+Z)" />
        {program && <Menu>
          <MenuTrigger disableButtonEnhancement><Button size="small" icon={<Add20Regular />} disabled={frozen}>Add operation</Button></MenuTrigger>
          <MenuPopover><MenuList>
            {ADD_GROUPS.map(([style, label]) => <MenuGroup key={style}><MenuGroupHeader>{label}</MenuGroupHeader>
              {authorableVerbs.filter((v) => verbStyle(v) === style).map((v) => <MenuItem key={v} onClick={() => add(v)}>{v}</MenuItem>)}
            </MenuGroup>)}
          </MenuList></MenuPopover>
        </Menu>}
        {starter && !readOnly && !changed && program?.verbs.length === 0 && <Button size="small" onClick={() => { dispatch({ t: "replace", text: starter }); setSide("source"); }}>Load starter GKQL</Button>}
        <span className={s.grow} />
        {changed && <Text size={200}>Local changes</Text>}
        {status.phase === "checked" && !problems.length && <Badge color="success" appearance="tint">Projection checked</Badge>}
        {store && <Button size="small" appearance="primary" disabled={!program || !changed || checking || model.error !== null} onClick={() => void checkAndStore()}>{checking ? "Checking…" : "Check & store"}</Button>}
      </div>
      <div aria-live="polite" className={s.messages}>
        {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
        {model.error && <MessageBar intent="error"><MessageBarBody>The text is not a GKQL program: {model.error}{program ? " The graph shows the last good program; it can be edited again once the text is a program." : ""}</MessageBarBody></MessageBar>}
        {model.notice && <MessageBar intent="warning"><MessageBarBody>{model.notice}</MessageBarBody></MessageBar>}
        {model.gesture.t === "connecting" && <MessageBar intent="info"><MessageBarBody>Connecting from {model.gesture.from}: choose the operation it feeds. Escape cancels.</MessageBarBody></MessageBar>}
        {!structureEditable && <MessageBar intent="info"><MessageBarBody>Use the GKQL text to preserve exact numbers in this browser.</MessageBarBody></MessageBar>}
      </div>
      {!program ? sourceView : (
        <div className={s.workspace}>
          <GkqlOutline model={model} problems={byFocus} dispatch={dispatch} />
          <GkqlGraph model={model} problems={byFocus} readOnly={frozen} dispatch={dispatch} />
          <section className={s.sidebar} aria-label="Selection and text">
            {problems.length > 0 && (
              <div className={s.problems}>
                <Text as="h2" weight="semibold" size={300}>Problems ({problems.length})</Text>
                <ul className={s.problemList}>
                  {problems.map((p, i) => <li key={i}>
                    <Button size="small" appearance="transparent" className={s.problem} disabled={!p.focus || p.focus.startsWith("port:")} onClick={() => { dispatch({ t: "select", focus: p.focus }); setSide("operation"); }}>
                      {p.focus?.startsWith("verb:") ? `${p.focus.slice(5)}: ` : ""}{p.message}
                    </Button>
                  </li>)}
                </ul>
              </div>
            )}
            <TabList size="small" aria-label="GKQL views" selectedValue={side} onTabSelect={(_, { value }) => setSide(String(value))}><Tab value="operation">Operation</Tab><Tab value="source">GKQL</Tab></TabList>
            {side === "source" ? sourceView : selectedVerb ? (
              <Operation key={selectedVerb.id} program={program} node={selectedVerb} readOnly={frozen} change={(next) => dispatch({ t: "change", program: next })} remove={() => removeAndFocus(verbFocus(selectedVerb.id))} />
            ) : model.selected?.startsWith("wire:") ? (
              <WirePanel focus={model.selected} readOnly={frozen} remove={() => removeAndFocus(model.selected!)} />
            ) : (
              <>
                <Text as="h2" weight="semibold" size={400}>Inputs</Text>
                {program.inputs.length ? <div className={s.row}>{program.inputs.map((input) => <Badge key={input.name} color="informative" appearance="outline">{input.name}{input.rows ? ` · rows ${input.rows}` : input.ontology ? ` · ${input.ontology}` : ""}</Badge>)}</div> : <Text>No inputs.</Text>}
                <Text size={200}>Select a step or a card to edit it.</Text>
              </>
            )}
          </section>
        </div>
      )}
    </div>
  );
}
