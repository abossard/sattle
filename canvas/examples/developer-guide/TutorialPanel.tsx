import { Badge, Button, Field, Link, Select, Text, makeStyles, tokens } from "@fluentui/react-components";
import { ArrowLeft16Regular, ArrowRight16Regular, Copy16Regular, Dismiss16Regular } from "@fluentui/react-icons";
import { useEffect, useMemo, useRef, useState } from "react";
import { valueText, type Entity } from "../../src/core/model";
import { printGkql, readGkql } from "../../src/editor/gkql";
import { LESSONS, lessonOf, sourceForLesson, type Lesson, type Step } from "../../src/tutorial/lessons";
import { masterLinks } from "./master-links";
import { projectionSources } from "./projection-sources";
import { exerciseProgram } from "../../src/tutorial/projection-exercises";
import { darkTheme, lightTheme } from "../../src/render/theme";
import connections from "../../connections.json";
import { runtime } from "../../src/state/runtime";
import { dispatch, useStore } from "../../src/state/store";
import type { View } from "../../src/state/types";
import { formatHash } from "../../src/state/url";
import { useRevision } from "../../src/ui/common";
import adapters from "../../../engine/src/Sattle.Runtime/Adapters.cs?raw";
import documentModule from "../../../engine/tutorial/sattle.doc.module.gkql.json?raw";
import health from "../../../engine/tutorial/tutorial.health.gkql.json?raw";
import ingest from "../../../engine/tutorial/tutorial.ingest.gkql.json?raw";
import inventory from "../../../engine/tutorial/tutorial.inventory.gkql.json?raw";
import tutorialModule from "../../../engine/tutorial/tutorial.module.gkql.json?raw";
import policy from "../../../engine/tutorial/tutorial.policy.gkql.json?raw";
import report from "../../../engine/tutorial/tutorial.report.gkql.json?raw";
import signals from "../../../engine/tutorial/tutorial.signals.gkql.json?raw";
import tree from "../../../engine/tutorial/tutorial.tree.gkql.json?raw";
import workloadHealth from "../../../engine/tutorial/tutorial.workloadHealth.gkql.json?raw";

const adapterStart = adapters.indexOf("public sealed class AzureResourceGraphAdapter");
const adapterEnd = adapters.indexOf("public sealed class ", adapterStart + 1);
const sources: Readonly<Record<string, string>> = {
  ...projectionSources,
  "Adapters.cs": adapters.slice(adapterStart, adapterEnd === -1 ? undefined : adapterEnd),
  "sattle.doc.module.gkql.json": documentModule,
  "tutorial.health.gkql.json": health,
  "tutorial.ingest.gkql.json": ingest,
  "tutorial.inventory.gkql.json": inventory,
  "tutorial.module.gkql.json": tutorialModule,
  "tutorial.policy.gkql.json": policy,
  "tutorial.report.gkql.json": report,
  "tutorial.signals.gkql.json": signals,
  "tutorial.tree.gkql.json": tree,
  "tutorial.workloadHealth.gkql.json": workloadHealth,
};

const useStyles = makeStyles({
  panel: { display: "grid", gridTemplateRows: "auto minmax(0, 1fr) auto", minHeight: 0, backgroundColor: tokens.colorNeutralBackground2, borderRight: `1px solid ${tokens.colorNeutralStroke2}`, "@media (max-width: 860px)": { borderRight: "none", borderBottom: `1px solid ${tokens.colorNeutralStroke2}` } },
  header: { display: "grid", gridTemplateColumns: "minmax(0, 1fr) auto", alignItems: "end", columnGap: tokens.spacingHorizontalXS, padding: tokens.spacingHorizontalM, borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  picker: { minWidth: 0, "& select": { width: "100%", minWidth: 0 } },
  body: { minWidth: 0, overflow: "auto", padding: tokens.spacingHorizontalM },
  title: { marginTop: 0, marginBottom: tokens.spacingVerticalS, fontSize: tokens.fontSizeBase500, lineHeight: tokens.lineHeightBase500, fontWeight: tokens.fontWeightSemibold },
  goal: { display: "block", marginTop: 0, marginBottom: tokens.spacingVerticalXS },
  flow: { listStyle: "none", display: "grid", alignItems: "center", justifyItems: "center", margin: `${tokens.spacingVerticalL} 0`, padding: tokens.spacingHorizontalM, border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground1 },
  flowPair: { display: "grid", gridTemplateColumns: "repeat(2, minmax(0, 1fr))", gap: tokens.spacingHorizontalXS },
  level: { display: "grid", justifyItems: "center", gap: tokens.spacingVerticalXXS, width: "100%" },
  nodes: { display: "flex", flexWrap: "wrap", justifyContent: "center", gap: tokens.spacingHorizontalXS, width: "100%" },
  node: { border: `1px solid ${tokens.colorNeutralStroke1}`, borderInlineStartWidth: "4px", padding: `${tokens.spacingVerticalXXS} ${tokens.spacingHorizontalS}`, backgroundColor: tokens.colorNeutralBackground2, fontWeight: tokens.fontWeightSemibold },
  arrow: { color: tokens.colorNeutralForeground2, lineHeight: tokens.lineHeightBase300 },
  buttons: { display: "flex", flexWrap: "wrap", gap: tokens.spacingHorizontalXS, margin: `${tokens.spacingVerticalS} 0` },
  networkLinks: { listStyle: "none", margin: `${tokens.spacingVerticalXS} 0`, padding: 0, display: "grid", gap: tokens.spacingVerticalXXS, "& button": { width: "100%", justifyContent: "space-between", textAlign: "start" } },
  details: { marginTop: tokens.spacingVerticalM, "& > summary": { cursor: "pointer", fontWeight: tokens.fontWeightSemibold, overflowWrap: "anywhere" } },
  exercise: { marginBlock: tokens.spacingVerticalM, padding: tokens.spacingHorizontalS, border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground1 },
  model: { listStyle: "none", display: "grid", gap: tokens.spacingVerticalXS, margin: `${tokens.spacingVerticalXS} 0`, padding: 0 },
  modelPart: { borderInlineStartWidth: "4px", borderInlineStartStyle: "solid", padding: `${tokens.spacingVerticalXXS} ${tokens.spacingHorizontalS}`, backgroundColor: tokens.colorNeutralBackground1 },
  recipe: { display: "grid", gap: tokens.spacingVerticalXS, paddingInlineStart: tokens.spacingHorizontalL, marginTop: tokens.spacingVerticalXS },
  codeGroup: { marginTop: tokens.spacingVerticalS },
  code: { margin: `${tokens.spacingVerticalXS} 0`, padding: tokens.spacingHorizontalS, overflow: "auto", maxHeight: "240px", backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke2}`, fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 },
  prompt: { margin: `${tokens.spacingVerticalXS} 0`, padding: tokens.spacingHorizontalS, backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke2}` },
  gates: { marginTop: 0, display: "grid", gridTemplateColumns: "repeat(2, minmax(0, 1fr))", gap: tokens.spacingHorizontalXS, "& > div": { padding: tokens.spacingHorizontalXS, border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground1 }, "& dd": { marginInlineStart: 0, marginTop: tokens.spacingVerticalXS, fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 } },
  healthSummary: { margin: 0, display: "grid", gap: tokens.spacingVerticalXXS, "& > div": { display: "flex", justifyContent: "space-between", alignItems: "center", gap: tokens.spacingHorizontalXS }, "& dd": { margin: 0 } },
  copyState: { marginInlineStart: tokens.spacingHorizontalXS },
  footer: { display: "grid", gridTemplateColumns: "1fr 1fr", justifyItems: "start", padding: tokens.spacingHorizontalM, borderTop: `1px solid ${tokens.colorNeutralStroke2}`, "& > :last-child": { justifySelf: "end" } },
});

const href = (lesson: Lesson, view = lesson.view) => formatHash({ guide: lesson.id, source: sourceForLesson(lesson), view });
const viewLabel: Record<View, string> = { data: "Open graph", space: "Open Graph Space", editor: "Open projection editor", ontology: "Open ontology" };

function showFullMaster() {
  dispatch({ type: "filter/set", graph: "master", filter: { search: "", hiddenTypes: [], propKey: "", propValue: "", focusDepth: 0, grouping: "", hiddenKinds: [], showRestated: true } });
  const latest = runtime.ws?.axisPoints().at(-1);
  if (latest !== undefined && runtime.ws?.timeline.k !== "live") dispatch({ type: "time/seek", t: latest });
}

/** Guided navigation uses the same selections as the normal graph and draft views. */
function useLessonTargets(lesson: Lesson) {
  const generation = useStore((s) => s.generation);
  const graph = useStore((s) => s.graphs.find((g) => g.id === lesson.graph));
  const loaded = useStore((s) => s.load.status);
  const draft = useStore((s) => s.editor.phase.t === "ready" ? s.editor.draft : "");
  const selectedLessonProjection = useRef(false);
  const source = useStore(s => s.sourceId);
  const available = loaded === "ready" && source === sourceForLesson(lesson) && runtime.ws?.info.id === (lesson.space ?? "azureTutorial");
  const graphExists = !!graph;

  useEffect(() => {
    if (!available || !graphExists) return;
    if (useStore.getState().selection?.graph !== lesson.graph) dispatch({ type: "select/element", selection: null });
    dispatch({ type: "ui/activeGraph", graph: lesson.graph });
    if (lesson.id === "explore") showFullMaster();
  }, [available, graphExists, generation, lesson.graph, lesson.id]);

  useEffect(() => {
    if (!draft || !available || !lesson.projection || selectedLessonProjection.current) return;
    const projections = runtime.session?.catalog.projections().filter((p) => p.module === (lesson.module ?? "tutorial"));
    const next = projections?.find((p) => p.name === lesson.build?.name)
      ?? projections?.find((p) => p.name === lesson.projection);
    if (!next) return;
    selectedLessonProjection.current = true;
    if (useStore.getState().editor.projection === next.id) return;
    dispatch({ type: "editor/projection", module: next.module, projection: next.id });
    dispatch({ type: "editor/releases", module: next.module });
  }, [available, draft, lesson.module, lesson.build?.name, lesson.projection]);

  return { graph: available ? graph : undefined, loaded };
}

function Copy({ code, label, buttonText = "Copy", disabled = false }: { code: string; label: string; buttonText?: string; disabled?: boolean }) {
  const s = useStyles();
  const [result, setResult] = useState("");
  const copy = async () => {
    try { await navigator.clipboard.writeText(code); setResult("Copied"); }
    catch { setResult("Clipboard unavailable"); }
  };
  return <><Button size="small" icon={<Copy16Regular />} disabled={disabled} onClick={() => void copy()} aria-label={`Copy ${label}`}>{buttonText}</Button><Text as="span" size={200} className={s.copyState} role="status">{result}</Text></>;
}

function Code({ code, label }: { code: string; label: string }) {
  const s = useStyles();
  return <div className={s.codeGroup}><pre className={s.code} tabIndex={0} aria-label={label}><code>{code}</code></pre><Copy code={code} label={label} /></div>;
}

function Source({ file }: { file: string }) {
  const s = useStyles();
  const text = sources[file];
  return text ? <details className={s.details}><summary>{file}</summary><Code code={text} label={file} /></details> : null;
}

function MirrorSource({ file, connected, base }: { file: string; connected: boolean; base: string }) {
  const s = useStyles();
  const [open, setOpen] = useState(false);
  const [code, setCode] = useState("");
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    if (!open || !connected) return;
    const controller = new AbortController();
    setFailed(false);
    void fetch(base + "/definitions/" + encodeURIComponent(file), { signal: controller.signal })
      .then(async res => {
        if (!res.ok) throw new Error(String(res.status));
        const body = await res.text();
        JSON.parse(body);
        if (!controller.signal.aborted) setCode(body);
      }).catch(() => { if (!controller.signal.aborted) setFailed(true); });
    return () => controller.abort();
  }, [open, connected, base, file]);
  return <details className={s.details} onToggle={e => setOpen(e.currentTarget.open)}>
    <summary>{file}</summary>{code ? <Code code={code} label={file} /> :
      <Text>{!connected ? "Start the CORE engine to load its GKQL." : failed ? "Reopen to retry loading the projection." : "Loading GKQL…"}</Text>}
  </details>;
}

function Flow({ levels, label }: { levels: Step[][]; label: string }) {
  const s = useStyles();
  const theme = useStore((state) => state.theme);
  const palette = theme === "light" ? lightTheme.hue : darkTheme.hue;
  return <ol className={s.flow} aria-label={label}>
    {levels.map((level, i) => <li key={i} className={s.level}>
      {i > 0 && <span className={s.arrow} aria-hidden="true">↓</span>}
      <div className={s.nodes}>{level.map(([label, role]) => <Text key={label} size={200} className={s.node} style={theme === "contrast" ? undefined : { borderInlineStartColor: palette[role] }}>{label}</Text>)}</div>
    </li>)}
  </ol>;
}

const modelParts = [
  { lesson: "adapter", title: "Adapters read facts", example: "Keep the Azure resource ID, metric value, and timestamp.", role: "input" },
  { lesson: "explore", title: "Identity finds the same object", example: "Use the Service’s cluster, namespace, and name across inputs.", role: "identity" },
  { lesson: "ontology", title: "You curate the ontology", example: "Define Service and the types a ServiceCalls edge can join.", role: "rule" },
  { lesson: "first-projection", title: "Projections run the rules", example: "Match an observed pod call and write a ServiceCalls edge.", role: "projection" },
] as const;

function ModelParts() {
  const s = useStyles();
  const theme = useStore((state) => state.theme);
  const palette = theme === "light" ? lightTheme.hue : darkTheme.hue;
  return <section aria-label="Parts of the model">
    <Text weight="semibold">What makes the model</Text>
    <ul className={s.model}>
      {modelParts.map(({ lesson, title, example, role }) => <li key={lesson} className={s.modelPart} style={{ borderInlineStartColor: theme === "contrast" ? tokens.colorNeutralStroke1 : palette[role] }}>
        <Link href={href(lessonOf(lesson)!)}>{title}</Link>
        <Text block size={200}>{example}</Text>
      </li>)}
    </ul>
    <Text block size={200}>As inputs change, the engine reevaluates the rules and updates downstream graphs.</Text>
  </section>;
}

interface Hysteresis { above: number; below: number; enterAfter: number; exitAfter: number; high: string; low: string }

function Thresholds() {
  const s = useStyles();
  const gate = readGkql(signals).verbs.find((verb) => verb.verb === "hysteresis")?.hysteresis as Hysteresis | undefined;
  if (!gate) return null;
  return <>
    <Text size={200}>Starter thresholds · consecutive one-minute CPU windows</Text>
    <dl className={s.gates} aria-label="CPU hysteresis in the starter projection">
      <div><dt><Badge appearance="outline" color="warning">{gate.high}</Badge></dt><dd>≥ {gate.above}% × {gate.enterAfter}</dd></div>
      <div><dt><Badge appearance="outline" color="success">{gate.low}</Badge></dt><dd>≤ {gate.below}% × {gate.exitAfter}</dd></div>
    </dl>
  </>;
}

function CpuReplay({ model }: { model: string }) {
  const s = useStyles();
  const revision = useRevision("signals");
  const healthRevision = useRevision(model);
  const time = useStore((state) => state.time.now);
  const example = useMemo(() => {
    if (!revision) return undefined;
    const times = revision.ws.patchTimes("signals").times;
    const stateBySignal = new Map<string, { healthy?: number; degraded?: number }>();
    for (const [i, patch] of revision.history.patches.entries()) {
      const at = times[i];
      if (at === undefined) continue;
      for (const edit of patch.edits) {
        if (edit.kind !== "entity" || !edit.after) continue;
        const state = edit.after.props.state;
        const previous = stateBySignal.get(edit.id) ?? {};
        if (state?.kind !== "text") continue;
        if (state.value === "Healthy") {
          if (previous.healthy !== undefined && previous.degraded !== undefined)
            return { id: edit.id, healthy: previous.healthy, degraded: previous.degraded, recovered: at };
          previous.healthy = at;
        }
        if (state.value === "Degraded" && previous.degraded === undefined) previous.degraded = at;
        stateBySignal.set(edit.id, previous);
      }
    }
    return undefined;
  }, [revision]);
  if (!example) return <Text block size={200}>Replay checkpoints appear when all three CPU states are available.</Text>;
  const nodes = [...(healthRevision?.graph.entities() ?? [])];
  const find = (role: string, scope: string) => nodes.find((node) => textProperty(node, "role") === role && textProperty(node, "scope") === scope);
  const states = [
    ["Measured API CPU", revision?.graph.entity(example.id)],
    ["Deploy to AKS", find("Flow", "deploy")],
    ["Serve traffic", find("Flow", "serve")],
    ["Workload", find("Workload", "tutorial")],
  ] as const;
  const checkpoints = [
    ["CPU healthy", example.healthy, "success"],
    ["CPU degraded", example.degraded, "warning"],
    ["CPU recovered", example.recovered, "success"],
  ] as const;
  return <section aria-label="Compare replay checkpoints" className={s.codeGroup}>
    <Text weight="semibold">Replay measured API CPU</Text>
    <div className={s.buttons}>
      {checkpoints.map(([label, at, color]) => <Button key={label} size="small" aria-pressed={time === at} onClick={() => {
        dispatch({ type: "time/seek", t: at });
        const deploy = [...(runtime.ws?.history(model)?.graph.entities() ?? [])]
          .find((node) => textProperty(node, "scope") === "deploy" && textProperty(node, "role") === "Flow");
        if (deploy) inspectElement(model, deploy.id, "entity");
      }}><Badge color={color} appearance="tint">{label}</Badge></Button>)}
    </div>
    <dl className={s.healthSummary} aria-label="States at this checkpoint">
      {states.map(([label, node]) => {
        const state = node && textProperty(node, "state");
        return state && <div key={label}><dt><Text size={200}>{label}</Text></dt><dd>
          <Badge appearance="tint" color={state === "Healthy" ? "success" : state === "Degraded" ? "warning" : state === "Unhealthy" ? "danger" : "informative"}>{state}</Badge>
        </dd></div>;
      })}
    </dl>
    <Text block size={200}>A flow can stay Unknown after CPU recovers if another required check has no data.</Text>
    <div className={s.buttons}>
      <Button size="small" onClick={() => inspectElement("signals", example.id, "entity")}>Inspect CPU state</Button>
      <Button size="small" onClick={() => inspectElement("master", example.id, "entity")}>Inspect Azure readings</Button>
      <Button as="a" size="small" href={href(lessonOf("document")!)}>Open report at this time</Button>
    </div>
  </section>;
}

function BuildSteps({ lesson, build, available }: { lesson: Lesson; build: NonNullable<Lesson["build"]>; available: boolean }) {
  const s = useStyles();
  const editor = useStore((state) => state.editor);
  const view = useStore((state) => state.view);
  const program = useMemo(() => exerciseProgram(build), [build]);
  const code = printGkql(program);
  const projection = `d:tutorial/projection:${build.name}`;
  const session = runtime.session;
  const exists = Boolean(session?.graph.entity(projection));
  const space = session && [...session.graph.entities()].find((node) => node.type === "GraphSpace");
  const inputs = build.inputs.map((port) => {
    const bound = space && session?.catalog.targets(`${space.id}/binding:${build.name}.${port}`, "boundFrom")[0];
    if (bound) return { port, graph: session!.catalog.name(bound) };
    const upstream = LESSONS.find((item) => item.graph === port)?.build?.name;
    const checked = upstream && session?.texts[`tutorial.${upstream}.gkql.json`];
    return { port, graph: upstream && checked && readGkql(checked).verbs.length ? upstream : port };
  });
  const ready = available && editor.phase.t === "ready" && !editor.phase.work;
  const active = exists && editor.projection === projection && view === "editor";
  const creating = !active && editor.phase.t === "ready" && editor.phase.work === "command";
  const create = () => {
    if (exists) {
      dispatch({ type: "editor/projection", module: "tutorial", projection });
      return;
    }
    dispatch({ type: "editor/do", cmd: { t: "newProjection", spec: {
      module: "tutorial", name: build.name, description: "", outputModule: `d:${program.ontology}`, graphName: build.name,
      language: "gkql", params: [], inputs: inputs.map((input) => ({ ...input, ontology: `d:${program.inputs.find(({ name }) => name === input.port)?.ontology ?? "tutorial"}` })),
    } } });
  };
  return <section className={s.exercise} aria-label="Build the draft projection">
    <Text block size={200}>Read <strong>{inputs.map(({ graph }) => graph).join(" + ")}</strong> → Write <strong>{build.name}</strong></Text>
    <ol className={s.recipe}>
      <li>{active ? <Badge appearance="tint" color="success">Draft open</Badge>
        : view !== "editor" ? <Button as="a" href={href(lesson, "editor")} size="small">Open projection editor</Button>
          : <Button appearance="primary" size="small" disabled={!ready} onClick={create}>{creating ? "Creating draft…" : exists ? "Open exercise draft" : "Create exercise draft"}</Button>}</li>
      <li>Click <strong>Load starter GKQL</strong>, or <Copy label="GKQL" buttonText="Copy GKQL" code={code} disabled={available && !active} /> and paste over the editor text.</li>
      <li>Click <strong>Check &amp; store</strong>. When checked, click <strong>Preview draft</strong>.</li>
    </ol>
    {lesson.id === "first-projection" && <details className={s.details}>
      <summary>Try removing Service calls</summary>
      <Text block size={200}>In <strong>Graph</strong>, select <code>serviceCalls</code> and click <strong>Remove operation</strong>.</Text>
      <Text block size={200}>Check and preview again. Expect 17 Deployment calls and zero Service calls.</Text>
    </details>}
    {lesson.id === "second-projection" && active && inputs.some(({ graph }) => graph === "firstWorkloads") &&
      <details className={s.details}>
        <summary>Test a change in the upstream draft</summary>
        <ol className={s.recipe}>
          <li>Preview placement and note the relationship count.</li>
          <li>Open <Link href={href(lessonOf("first-projection")!)}>your workloads draft</Link>. In <strong>Graph</strong>, remove <code>scheduling</code> and check it.</li>
          <li>Return here and preview again. The placement links disappear.</li>
          <li>To restore the starter, click <strong>Copy GKQL</strong> in the workloads lesson. Paste over the source and check again.</li>
        </ol>
      </details>}
  </section>;
}

function ExampleConnection({ graph, example }: { graph: string; example: NonNullable<Lesson["example"]> }) {
  const s = useStyles();
  const revision = useRevision(graph);
  const edge = useMemo(() => revision && [...revision.graph.relationships()].find((link) => link.type.split(".").at(-1) === example.type), [revision, example.type]);
  if (!edge) return null;
  return <div className={s.codeGroup}>
    <Button size="small" onClick={() => inspectEdge(graph, edge.id)}>{example.label}</Button>
    <Text block size={200}>{example.explanation}</Text>
  </div>;
}

function inspectEdge(graph: string, id: string) {
  inspectElement(graph, id, "relationship");
}

function inspectElement(graph: string, id: string, kind: "entity" | "relationship") {
  dispatch({ type: "ui/view", view: "data" });
  dispatch({ type: "ui/activeGraph", graph });
  dispatch({ type: "select/element", selection: { graph, kind, id } });
}

function SharedService() {
  const s = useStyles();
  const master = useRevision("master");
  const workloads = useRevision("workloads");
  const service = useMemo(() => {
    if (!master || !workloads) return undefined;
    for (const node of workloads.graph.entities()) {
      if (node.type.split(".").at(-1) === "Service" && master.graph.entity(node.id)) return node;
    }
    return undefined;
  }, [master, workloads]);
  if (!service) return null;
  return <details className={s.details}>
    <summary>Follow the same Service across graphs</summary>
    <Text block size={200}>Its identity uses <code>cluster + namespace + name</code>.</Text>
    <div className={s.buttons}>
      <Button size="small" onClick={() => inspectElement("master", service.id, "entity")}>Service in master</Button>
      <Button size="small" onClick={() => inspectElement("workloads", service.id, "entity")}>Service in workloads</Button>
    </div>
    <Text block size={200}>Compare the IDs in Inspector. Workloads uses the captured Service as a call endpoint.</Text>
  </details>;
}

function PolicyTrace() {
  const s = useStyles();
  const tree = useRevision("tree");
  const policy = useRevision("policy");
  const path = useMemo(() => {
    if (!tree || !policy) return undefined;
    for (const edge of tree.graph.relationships()) {
      if (edge.type.split(".").at(-1) !== "Contains") continue;
      const parent = tree.graph.entity(edge.source);
      const child = policy.graph.entity(edge.target);
      if (parent?.props.policy && child?.props.policy) return { parent, child };
    }
    return undefined;
  }, [tree, policy]);
  if (!path) return null;
  return <div className={s.buttons}>
    <Button size="small" onClick={() => inspectElement("tree", path.parent.id, "entity")}>Group policy · {valueText(path.parent.props.policy)}</Button>
    <Button size="small" onClick={() => inspectElement("policy", path.child.id, "entity")}>Cluster policy · {valueText(path.child.props.policy)}</Button>
  </div>;
}

const textProperty = (node: Entity, property: string) => {
  const value = node.props[property] ?? node.props[`tutorial.${property}`] ?? node.props[`mirror.${property}`];
  return value?.kind === "text" ? value.value : undefined;
};

function CoreExample({ lesson }: { lesson: Lesson }) {
  const s = useStyles();
  const rev = useRevision(lesson.graph);
  if (!rev) return null;
  if (lesson.graph === "network") {
    const paths = [...rev.graph.relationships()];
    const blob = paths.find(edge => {
      const target = rev.graph.entity(edge.target);
      return edge.type.endsWith("CalledEndpoint") && target && textProperty(target, "destination")?.includes(".blob.");
    });
    const edge = blob ?? paths.find(item => item.type.endsWith("CalledEndpoint") || item.type.endsWith("ObservedTraffic"));
    const model = blob && paths.find(item => {
      const target = rev.graph.entity(item.target);
      return item.type.endsWith("EndpointOf") && item.source === blob.target && target && textProperty(target, "key") === "session-storage";
    });
    return edge ? <div className={s.buttons}>
      <Button size="small" onClick={() => inspectEdge(lesson.graph, edge.id)}>{blob ? "Inspect calls to Blob Storage" : "Inspect an observed connection"}</Button>
      {model && <Button size="small" onClick={() => inspectEdge(lesson.graph, model.id)}>Trace the host to CORE storage</Button>}
    </div> : <Text size={200}>This capture has no network observations for this checkpoint.</Text>;
  }
  const keys = lesson.graph === "signals" ? ["session-storage/azureResource/availability"]
    : lesson.graph === "master" ? ["flow-run-agent-tasks"]
    : ["flow-run-agent-tasks", "system-session-store", "system-agent-runtime"];
  const nodes = [...rev.graph.entities()];
  const task = nodes.find(n => textProperty(n, "key") === "flow-run-agent-tasks");
  const storage = nodes.find(n => textProperty(n, "key") === "system-session-store");
  const link = task && storage && [...rev.graph.relationships()].find(e => e.type.endsWith("DependsOn") && e.source === task.id && e.target === storage.id);
  return <div className={s.buttons}>{link && (lesson.graph === "structure" || lesson.graph === "health") &&
    <Button size="small" onClick={() => inspectEdge(lesson.graph, link.id)}>Inspect storage dependency</Button>}{keys.map(key => {
    const node = nodes.find(n => textProperty(n, "key") === key);
    return node && <Button size="small" key={key} onClick={() => inspectElement(lesson.graph, node.id, "entity")}>{textProperty(node, "name") ?? key}</Button>;
  })}</div>;
}

function HealthEvidence({ graph }: { graph: string }) {
  const s = useStyles();
  const revision = useRevision(graph);
  const choices = useMemo(() => {
    if (!revision) return [];
    const nodes = [...revision.graph.entities()];
    const serve = nodes.find((node) => textProperty(node, "role") === "Flow" && textProperty(node, "scope") === "serve");
    const probe = nodes.find((node) => textProperty(node, "role") === "Azure ingress probe");
    const unknown = nodes.find((node) => textProperty(node, "ownState") === "Unknown");
    return [["Serving flow", serve], ["Azure probe check", probe], ["Missing evidence", unknown]] as const;
  }, [revision]);
  return <>
    <div className={s.buttons}>{choices.map(([label, node]) => node && <Button key={label} size="small" onClick={() => inspectElement(graph, node.id, "entity")}>{label}</Button>)}</div>
    <details className={s.details}><summary>Read the health model</summary>
      <Text block size={200}>A check has its own state. A flow inherits the worst state from the checks it requires.</Text>
      <Text block size={200}>Solid lines join the workload to its flows. Dashed lines lead from each flow to the checks it needs.</Text>
      <Text block size={200}>In Graph, choose Show evidence to see the Pods, CPU signal and Azure probe meter behind the checks.</Text>
      <Text block size={200}>Unknown means evidence is missing. A measured zero is evidence.</Text>
      <Text block size={200}>Pod readiness and the Azure probe cover parts of serving traffic. Request success needs a request check.</Text>
    </details>
  </>;
}

function CapturedNetworkLinks() {
  const s = useStyles();
  const revision = useRevision("master");
  const links = useMemo(() => revision ? masterLinks(revision.graph.relationships()) : [], [revision]);
  if (links.length === 0) return null;
  return <section aria-label="Captured network links">
    <Text weight="semibold">Captured network links</Text>
    <ul className={s.networkLinks}>
      {links.map(({ edge, label, count, type }) => <li key={type}>
        <Button size="small" title={type} onClick={() => inspectEdge("master", edge.id)}>{label}<Badge size="small" appearance="outline">{count.toLocaleString()}</Badge></Button>
      </li>)}
    </ul>
  </section>;
}

export function TutorialPanel({ lesson }: { lesson: Lesson }) {
  const s = useStyles();
  const heading = useRef<HTMLHeadingElement>(null);
  const view = useStore((state) => state.view);
  const { graph, loaded } = useLessonTargets(lesson);
  const [reset, setReset] = useState(false);
  const replayReady = lesson.id === "start" && Boolean(graph?.nodes);
  const chapters = LESSONS.filter(item => item.space === lesson.space);
  const index = chapters.indexOf(lesson);
  const previous = chapters[index - 1], next = chapters[index + 1];
  useEffect(() => { heading.current?.focus(); }, []);
  const go = (id: string) => { const target = lessonOf(id); if (target) location.hash = href(target); };
  const close = () => {
    dispatch({ type: "ui/guide", lesson: null });
    requestAnimationFrame(() => document.getElementById("open-developer-guide")?.focus());
  };

  return <aside className={s.panel} aria-label="Developer guide">
    <div className={s.header}>
      <Field label="Developer guide"><Select className={s.picker} value={lesson.id} onChange={(_, data) => go(data.value)}>
        {([["CORE deployment", true], ["Azure and Kubernetes", false]] as const).map(([label, core]) => <optgroup key={label} label={label}>{LESSONS.filter(item => Boolean(item.space) === core).map(item => <option key={item.id} value={item.id}>{item.title}</option>)}</optgroup>)}
      </Select></Field>
      <Button appearance="subtle" icon={<Dismiss16Regular />} aria-label="Close developer guide" onClick={close} />
    </div>
    <div className={s.body}>
      <h2 ref={heading} tabIndex={-1} className={s.title}>{lesson.title}</h2>
      <Text as="p" className={s.goal}><strong>Goal</strong> {lesson.goal}</Text>
      {replayReady ? <Button as="a" appearance="primary" size="small" href={href(lessonOf("explore")!)}>Explore the master graph</Button> : <Text>{lesson.action}</Text>}
      {lesson.space && graph && <CoreExample lesson={lesson} />}
      {lesson.build && <BuildSteps lesson={lesson} build={lesson.build} available={Boolean(graph)} />}
      {lesson.id === "explore" && graph && <><CapturedNetworkLinks /><SharedService /></>}
      {lesson.id === "explore" && graph && <details className={s.details}>
        <summary>Find any node or edge</summary>
        <ol className={s.recipe}>
          <li>Open <strong>Elements</strong> and pick <strong>Nodes</strong> or <strong>Edges</strong>.</li>
          <li>Search for an ID, name, or type in <strong>Filter the list</strong>.</li>
          <li>Pick a row to see it on the graph and in Inspector.</li>
        </ol>
      </details>}
      {lesson.id === "rollup" && graph && <PolicyTrace />}
      {lesson.id === "azure-health" && graph && <><CpuReplay model={lesson.graph} /><HealthEvidence graph={lesson.graph} /></>}
      {lesson.companionFlow ? <div className={s.flowPair}><Flow levels={lesson.flow} label={lesson.flowLabel ?? "Data flow"} /><Flow levels={lesson.companionFlow.levels} label={lesson.companionFlow.label} /></div> : <Flow levels={lesson.flow} label={lesson.flowLabel ?? "Data flow"} />}
      {lesson.id === "start" && <ModelParts />}
      {lesson.id === "azure-health" && <Thresholds />}
      <Text block size={200}><strong>Check</strong> {lesson.check}</Text>
      {graph ? <Text size={200}><strong>{replayReady ? "Replay ready" : lesson.build ? `Example ${graph.id}` : graph.id}</strong> · {graph.nodes.toLocaleString()} nodes · {graph.edges.toLocaleString()} links</Text>
        : <Text size={200}>{loaded === "loading" ? "Connecting to the engine…" : lesson.id === "start" ? "Waiting for the tutorial engine." : <Link href={href(chapters[0])}>{lesson.space ? "Open the CORE replay" : "Start the tutorial engine"}</Link>}</Text>}
      <div className={s.buttons}>
        {loaded === "error" && <Button size="small" onClick={() => dispatch({ type: "source/opening", id: sourceForLesson(lesson) })}>Retry connection</Button>}
        {lesson.id === "explore" && graph && <><Button size="small" onClick={() => {
          dispatch({ type: "ui/view", view: "data" });
          dispatch({ type: "ui/activeGraph", graph: "master" });
          dispatch({ type: "select/element", selection: null });
          showFullMaster();
          setReset(true);
        }}>Reset master view</Button>{reset && <Text role="status" size={200}>Showing the latest master graph.</Text>}</>}
        {view !== lesson.view && <Button as="a" size="small" href={href(lesson)}>{viewLabel[lesson.view]}</Button>}
        {lesson.projection && view !== "editor" && lesson.view !== "editor" && <Button as="a" size="small" href={href(lesson, "editor")}>Open projection editor</Button>}
        {view !== "data" && lesson.view !== "data" && <Button as="a" size="small" href={href(lesson, "data")}>{lesson.build ? "Open example graph" : "Open graph"}</Button>}
      </div>
      {lesson.example && graph && <ExampleConnection graph={lesson.graph} example={lesson.example} />}
      {lesson.commands?.map(({ label, code }) => <details key={label} className={s.details} open={(lesson.id === "start" && !replayReady) || (lesson.exercise && !graph) || (lesson.id === "core-import" && !graph && label === "Open the local replay") || undefined}><summary>{label}</summary><Code label={label} code={code} /></details>)}
      {lesson.files?.map((file) => lesson.space ? <MirrorSource key={file} file={file} connected={Boolean(graph)} base={connections.find(item => item.id === lesson.source)!.url} /> : <Source key={file} file={file} />)}
      <details className={s.details}><summary>Ask your AI assistant</summary><p className={s.prompt}>{lesson.prompt}</p><Copy label="AI prompt" code={lesson.prompt} /></details>
    </div>
    <nav className={s.footer} aria-label="Lessons">
      {previous ? <Button as="a" size="small" appearance="subtle" href={href(previous)} icon={<ArrowLeft16Regular />}>Back</Button> : <span />}
      {next ? <Button as="a" size="small" href={href(next)} icon={<ArrowRight16Regular />} iconPosition="after">Next</Button> : <span />}
    </nav>
  </aside>;
}
