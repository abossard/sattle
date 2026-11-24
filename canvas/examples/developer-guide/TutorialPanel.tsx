import { Badge, Button, Field, Link, Select, Text, makeStyles, tokens } from "@fluentui/react-components";
import { ArrowLeft16Regular, ArrowRight16Regular, Copy16Regular, Dismiss16Regular } from "@fluentui/react-icons";
import { useEffect, useMemo, useRef, useState, type CSSProperties } from "react";
import { labelOf, valueText, type Entity } from "../../src/core/model";
import { field } from "../../src/editor/coverage-review";
import { printGkql, readGkql } from "../../src/editor/gkql";
import { LESSONS, lessonOf, sourceForLesson, type Lesson, type Step } from "../../src/tutorial/lessons";
import { colorValueOf } from "../../src/app/color-by";
import type { Role } from "../../src/source/presentation";
import { CheckpointComparison } from "./CheckpointComparison";
import { useCapturedKusto } from "./useCapturedKusto";
import { masterLinks } from "./master-links";
import { projectionSources } from "./projection-sources";
import { exerciseProgram } from "../../src/tutorial/projection-exercises";
import { darkTheme, lightTheme } from "../../src/render/theme";
import connections from "../../connections.json";
import { runtime } from "../../src/state/runtime";
import { dispatch, useStore } from "../../src/state/store";
import type { View } from "../../src/state/types";
import { formatHash } from "../../src/state/url";
import { useRevision, type GraphRevision } from "../../src/ui/common";
import projectFlowOntology from "../../../lab/health-mirror/projects/ahm-model-demo/flows.json?raw";
import metricCatalog from "../../../lab/health-mirror/projects/ahm-model-demo/metrics.json?raw";
import recordAdapter from "../../../engine/src/Sattle.Runtime/RecordAdapter.cs?raw";
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

const sources: Readonly<Record<string, string>> = {
  ...projectionSources,
  "RecordAdapter.cs": recordAdapter,
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
  ...Object.fromEntries(Object.entries(import.meta.glob<string>([
    "../../../engine/standard/flows/definitions/sattle.flow.discover.gkql.json",
    "../../../engine/standard/coverage/sattle.coverage.coverage.gkql.json",
    "../../../engine/examples/kiln/workshop.{rating,impact,progress}.gkql.json",
  ], { eager: true, query: "?raw", import: "default" })).map(([path, code]) => [path.split("/").at(-1), code])),
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
  node: { display: "inline-flex", alignItems: "center", gap: tokens.spacingHorizontalXS, "::before": { content: '""', width: "8px", height: "8px", flexShrink: 0, backgroundColor: `var(--role, ${tokens.colorNeutralForeground1})` }, border: `1px solid ${tokens.colorNeutralStroke1}`, padding: `${tokens.spacingVerticalXXS} ${tokens.spacingHorizontalS}`, backgroundColor: tokens.colorNeutralBackground2, fontWeight: tokens.fontWeightSemibold },
  arrow: { color: tokens.colorNeutralForeground2, lineHeight: tokens.lineHeightBase300 },
  buttons: { display: "flex", flexWrap: "wrap", gap: tokens.spacingHorizontalXS, margin: `${tokens.spacingVerticalS} 0` },
  networkLinks: { listStyle: "none", margin: `${tokens.spacingVerticalXS} 0`, padding: 0, display: "grid", gap: tokens.spacingVerticalXXS, "& button": { width: "100%", justifyContent: "space-between", textAlign: "start" } },
  details: { marginTop: tokens.spacingVerticalM, "& > summary": { cursor: "pointer", fontWeight: tokens.fontWeightSemibold, overflowWrap: "anywhere" } },
  exercise: { marginBlock: tokens.spacingVerticalM, padding: tokens.spacingHorizontalS, border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground1 },
  model: { listStyle: "none", display: "grid", gap: tokens.spacingVerticalXS, margin: `${tokens.spacingVerticalXS} 0`, padding: 0 },
  modelPart: { display: "grid", gridTemplateColumns: "8px minmax(0, 1fr)", columnGap: tokens.spacingHorizontalS, alignItems: "center", "::before": { content: '""', width: "8px", height: "8px", flexShrink: 0, backgroundColor: `var(--role, ${tokens.colorNeutralForeground1})`, gridRow: "1" }, "& > *": { gridColumn: "2" } },
  recipe: { display: "grid", gap: tokens.spacingVerticalXS, paddingInlineStart: tokens.spacingHorizontalL, marginTop: tokens.spacingVerticalXS },
  codeGroup: { marginTop: tokens.spacingVerticalS },
  code: { margin: `${tokens.spacingVerticalXS} 0`, padding: tokens.spacingHorizontalS, overflow: "auto", maxHeight: "240px", backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke2}`, fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 },
  prompt: { margin: `${tokens.spacingVerticalXS} 0`, padding: tokens.spacingHorizontalS, backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke2}` },
  gates: { marginTop: 0, display: "grid", gridTemplateColumns: "repeat(2, minmax(0, 1fr))", gap: tokens.spacingHorizontalXS, "& > div": { padding: tokens.spacingHorizontalXS, border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground1 }, "& dd": { marginInlineStart: 0, marginTop: tokens.spacingVerticalXS, fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 } },
  healthSummary: { margin: 0, display: "grid", gap: tokens.spacingVerticalXXS, "& > div": { display: "flex", justifyContent: "space-between", alignItems: "center", gap: tokens.spacingHorizontalXS }, "& dd": { margin: 0 } },
  evidenceTable: { width: "100%", textAlign: "left", fontSize: tokens.fontSizeBase200, fontVariantNumeric: "tabular-nums", "& th, & td": { paddingBlock: tokens.spacingVerticalXXS } },
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
      <div className={s.nodes}>{level.map(([label, role]) => <Text key={label} size={200} className={s.node} style={theme === "contrast" ? undefined : { "--role": palette[role] } as CSSProperties}>{label}</Text>)}</div>
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
      {modelParts.map(({ lesson, title, example, role }) => <li key={lesson} className={s.modelPart} style={theme === "contrast" ? undefined : { "--role": palette[role] } as CSSProperties}>
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
        const role = stateRole("signals", edit.after);
        const previous = stateBySignal.get(edit.id) ?? {};
        if (role === "ok") {
          if (previous.healthy !== undefined && previous.degraded !== undefined)
            return { id: edit.id, healthy: previous.healthy, degraded: previous.degraded, recovered: at };
          previous.healthy = at;
        }
        if (role === "warning" && previous.degraded === undefined) previous.degraded = at;
        stateBySignal.set(edit.id, previous);
      }
    }
    return undefined;
  }, [revision]);
  if (!example) return <Text block size={200}>Replay checkpoints appear when all three CPU states are available.</Text>;
  const nodes = [...(healthRevision?.graph.entities() ?? [])];
  const find = (role: string, scope: string) => nodes.find((node) => textProperty(node, "role") === role && textProperty(node, "scope") === scope);
  const states = [
    ["Measured API CPU", "signals", revision?.graph.entity(example.id)],
    ["Deploy to AKS", model, find("Flow", "deploy")],
    ["Serve traffic", model, find("Flow", "serve")],
    ["Workload", model, find("Workload", "tutorial")],
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
      {states.map(([label, graph, node]) => {
        const state = node && textProperty(node, "state");
        return state && <div key={label}><dt><Text size={200}>{label}</Text></dt><dd>
          <Badge appearance="tint" color={ROLE_BADGE[stateRole(graph, node)]}>{state}</Badge>
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
      params: [], inputs: inputs.map((input) => ({ ...input, ontology: `d:${program.inputs.find(({ name }) => name === input.port)?.ontology ?? "tutorial"}` })),
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
      <Text block size={200}>On the graph, select <code>serviceCalls</code> and press <kbd>Delete</kbd>, or click <strong>Remove operation</strong>.</Text>
      <Text block size={200}>Check and preview again. Expect 17 Deployment calls and zero Service calls.</Text>
    </details>}
    {lesson.id === "second-projection" && active && inputs.some(({ graph }) => graph === "firstWorkloads") &&
      <details className={s.details}>
        <summary>Test a change in the upstream draft</summary>
        <ol className={s.recipe}>
          <li>Preview placement and note the relationship count.</li>
          <li>Open <Link href={href(lessonOf("first-projection")!)}>your workloads draft</Link>. On the graph, select <code>scheduling</code>, press <kbd>Delete</kbd> and check it.</li>
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

function SubmissionEvidence({ graph }: { graph: string }) {
  const s = useStyles();
  const revision = useRevision(graph);
  if (!revision) return null;
  if (graph === "flows") {
    const handoffs = [...revision.graph.relationships()].filter(edge => edge.type.split(".").at(-1) === "Handoff");
    return <div className={s.buttons}>{handoffs.map(edge => {
      const from = revision.graph.entity(edge.source), to = revision.graph.entity(edge.target);
      return from && to && <Button key={edge.id} size="small" onClick={() => inspectEdge(graph, edge.id)}>
        {textProperty(from, "operation") ?? from.id} → {textProperty(to, "operation") ?? to.id}
      </Button>;
    })}</div>;
  }
  const nodes = [...revision.graph.entities()];
  const findings = nodes.filter(n => n.type.split(".").at(-1) === "Finding" && field(n, "item") === "submit-42");
  if (graph === "conflicts") {
    const conflict = findings.find(n => field(n, "role") === "receiver");
    return conflict ? <Button size="small" onClick={() => inspectElement(graph, conflict.id, "entity")}>Inspect receiver conflict</Button>
      : <div><Text block size={200}>This revision has no receiver conflict.</Text><Link href={href(lessonOf("inspect-coverage")!)}>Check the receiver in coverage</Link></div>;
  }
  const scope = nodes.find(n => n.type.split(".").at(-1) === "Scope" && field(n, "role") === "receiver");
  return <div className={s.buttons}>
    {scope && <Button size="small" onClick={() => inspectElement(graph, scope.id, "entity")}>
      Receiver scope · {field(scope, "eligible")} eligible · {field(scope, "availability")}
    </Button>}
    {findings.map(n => <Button key={n.id} size="small" onClick={() => inspectElement(graph, n.id, "entity")}>
      {field(n, "direction") === "observation" ? "Observed" : "Declared"} {field(n, "role")} · {field(n, "status")}
    </Button>)}
  </div>;
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
  const value = node.props[property] ?? Object.entries(node.props).find(([name]) => name.endsWith("." + property))?.[1];
  return value?.kind === "text" ? value.value : undefined;
};
/** The role the graph's presentation metadata gives a node's state: state words are data of the model, never of the canvas. */
const stateRole = (graph: string, node: Entity): Role => {
  const hint = runtime.ws?.graphInfo(graph)?.presentation?.find((h) => (h.property === "state" || h.property.endsWith(".state")) && node.props[h.property]);
  return (hint && colorValueOf({ property: hint.property, hint }, node.props)?.role) ?? "neutral";
};
const ROLE_BADGE: Record<Role, "success" | "warning" | "danger" | "informative"> = { ok: "success", warning: "warning", danger: "danger", unknown: "informative", neutral: "informative" };

function Coverage({ graph }: { graph: string }) {
  const inventory = useRevision("containerApps");
  const s = useStyles();
  if (!inventory) return null;
  const apps = [...inventory.graph.entities()].filter(node => node.type.split(".").at(-1) === "ContainerApp");
  if (!apps.length) return <Text size={200}>No apps observed at this checkpoint.</Text>;
  const matches = [...inventory.graph.relationships()].filter(edge => edge.type.split(".").at(-1) === "MatchedRule");
  const classified = apps.filter(node => textProperty(node, "ontologyStatus") === "Mapped").length;
  return <section aria-label="App name coverage">
    <Badge appearance="outline" color={classified === apps.length ? "success" : "warning"}>{classified} of {apps.length} apps mapped</Badge>
    <ul className={s.networkLinks}>{apps.map(app => {
      const status = textProperty(app, "ontologyStatus") ?? "Unknown";
      const rules = matches.filter(e => e.source === app.id).map(e => inventory.graph.entity(e.target)).filter((r): r is Entity => !!r);
      return <li key={app.id}><Button size="small" onClick={() => inspectElement(status === "Mapped" ? "containerApps" : graph, app.id, "entity")}>{textProperty(app, "name")}</Button>
        <Text block size={200}>{rules.length ? "Naming rule · " : ""}{rules.length ? rules.map(rule => [textProperty(rule, "pattern"), textProperty(rule, "className")].join(" → ")).join(" · ") : status}</Text></li>;
    })}</ul>
  </section>;
}

function ProjectFlows({ revision }: { revision: GraphRevision }) {
  const s = useStyles();
  const [chosen, setChosen] = useState("");
  const flows = [...revision.graph.entities()].filter(n => n.type.split(".").at(-1) === "UserFlow")
    .sort((a, b) => labelOf(a).localeCompare(labelOf(b)));
  const flow = flows.find(node => node.id === chosen) ?? flows.find(node => labelOf(node) === "Run a Task") ?? flows[0];
  const edges = [...revision.graph.relationships()];
  const steps = edges.filter(edge => edge.source === flow?.id && edge.type.split(".").at(-1) === "DeclaresStep");
  const inspect = (id: string) => inspectElement(revision.id, id, "entity");
  return <section aria-label="Project flow trace">
    {flow ? <>
      <Field label="User flow"><Select value={flow.id} onChange={(_, data) => { setChosen(data.value); inspect(data.value); }}>
        {flows.map(node => <option key={node.id} value={node.id}>{labelOf(node)}</option>)}
      </Select></Field>
      <Text block size={200}>Flow health · {textProperty(flow, "state") ?? "Unknown"}</Text>
      <Text block size={200}>{textProperty(flow, "healthBasis")}</Text>
      <Button size="small" appearance="subtle" onClick={() => inspect(flow.id)}>Inspect this flow</Button>
      <ul className={s.model}>{steps.map(link => {
        const step = revision.graph.entity(link.target);
        if (!step) return null;
        const matches = edges.filter(edge => edge.source === step.id && edge.type.split(".").at(-1) === "RunsOnApp");
        return <li key={link.id} className={s.modelPart} style={{ borderInlineStartColor: tokens.colorBrandStroke1 }}>
          <Button appearance="subtle" size="small" onClick={() => inspect(step.id)}>{labelOf(step)}</Button>
          {matches.map(edge => {
            const app = revision.graph.entity(edge.target);
            if (!app) return null;
            const role = textProperty(edge, "className");
            return <div key={edge.id}><Text size={200}>{role} → </Text>
              <Button size="small" onClick={() => inspect(app.id)}>{labelOf(app)}</Button></div>;
          })}
          {!matches.length && <Text block size={200}>No deployed app matches this step.</Text>}
          <Text block size={200}>{textProperty(step, "healthBasis")}</Text>
        </li>;
      })}</ul>
    </> : <Text block size={200}>No user flows are declared at this checkpoint.</Text>}
    <details className={s.details}><summary>Project flow ontology · flows.json</summary>
      <Code code={projectFlowOntology.trim()} label="project flow ontology" />
    </details>
  </section>;
}

function AppTelemetry({ revision }: { revision: GraphRevision }) {
  const s = useStyles();
  const [chosen, setChosen] = useState("");
  const nodes = [...revision.graph.entities()];
  const edges = [...revision.graph.relationships()];
  const apps = nodes.filter(n => n.type.split(".").at(-1) === "ContainerApp").sort((a, b) => labelOf(a).localeCompare(labelOf(b)));
  const app = apps.find(n => n.id === chosen) ?? apps.find(n => labelOf(n).endsWith("-web")) ?? apps[0];
  const linked = (id: string, type: string) => edges.filter(e => e.source === id && e.type.split(".").at(-1) === type)
    .map(edge => revision.graph.entity(edge.target)).filter((n): n is Entity => !!n);
  const metrics = app ? linked(app.id, "HasPlatformMetric").sort((a, b) => labelOf(a).localeCompare(labelOf(b))) : [];
  const scope = app && textProperty(app, "groupId")?.toLowerCase();
  const receipt = scope && nodes.find(n => n.type.split(".").at(-1) === "OperationWindow"
    && textProperty(n, "scopeId")?.toLowerCase() === scope);
  const queries = useCapturedKusto(receipt ? textProperty(receipt, "componentId") : undefined,
    receipt ? textProperty(receipt, "windowStart") : undefined, receipt ? textProperty(receipt, "windowEnd") : undefined);
  const steps = app ? edges.filter(e => e.target === app.id && e.type.split(".").at(-1) === "RunsOnApp")
    .map(e => revision.graph.entity(e.source)).filter((n): n is Entity => !!n) : [];
  const prop = (n: Entity, key: string) => {
    const value = n.props[key] ?? n.props[`mirror.${key}`];
    return value && value.kind !== "null" ? valueText(value) : "—";
  };
  const inspect = (n: Entity) => <Button size="small" appearance="subtle" onClick={() => inspectElement(revision.id, n.id, "entity")}>{labelOf(n)}</Button>;
  return <section aria-label="Measured app telemetry">
    {app && <Field label="Deployed app"><Select value={app.id} onChange={(_, data) => setChosen(data.value)}>
      {apps.map(n => <option key={n.id} value={n.id}>{labelOf(n)}</option>)}
    </Select></Field>}
    {metrics.length > 0 ? <table className={s.evidenceTable}><caption>Azure Monitor metrics</caption>
      <thead><tr><th>Metric</th><th>Value</th><th>Unit</th></tr></thead>
      <tbody>{metrics.map(n => <tr key={n.id}><td>{inspect(n)}</td><td>{prop(n, "value")}</td><td>{prop(n, "unit")}</td></tr>)}</tbody>
    </table> : <Text block size={200}>No platform readings for this app at this checkpoint.</Text>}
    <ul className={s.model}>{steps.map(step => {
      const readings = linked(step.id, "ObservedOperation");
      return <li key={step.id} className={s.modelPart} style={{ borderInlineStartColor: tokens.colorBrandStroke1 }}>
        {inspect(step)}
        {readings.length ? readings.map(row => <div key={row.id}>
          <Button size="small" appearance="subtle" onClick={() => inspectElement(revision.id, row.id, "entity")}>{textProperty(row, "target") || textProperty(row, "operation")}</Button>
          <Text block size={200}>{prop(row, "source")} · {prop(row, "calls")} calls · {prop(row, "failed")} failed</Text>
        </div>) : <Text block size={200}>No matching operation in the captured query results.</Text>}
      </li>;
    })}</ul>
    <details className={s.details}><summary>Metric names and aggregations</summary><Code label="metric catalog" code={metricCatalog.trim()} /></details>
    {queries ? <>
      <div className={s.buttons}><Copy code={queries.routes} label="request KQL" buttonText="Copy request KQL" />
        <Copy code={queries.dependencies} label="dependency KQL" buttonText="Copy dependency KQL" /></div>
      <details className={s.details}><summary>Kusto · requests by route</summary>
        <pre className={s.code} tabIndex={0}><code>{queries.routes}</code></pre></details>
      <details className={s.details}><summary>Kusto · dependencies inside a request</summary>
        <pre className={s.code} tabIndex={0}><code>{queries.dependencies}</code></pre></details>
    </> : <details className={s.details}><summary>Print Kusto for a local capture</summary>
      <Text size={200}>Run these commands in the checkout that holds the captured request and dependency results.</Text>
      <Code label="request KQL command" code="./scripts/health-mirror.sh curate-flows --print-captured-kql routes" />
      <Code label="dependency KQL command" code="./scripts/health-mirror.sh curate-flows --print-captured-kql dependencies" />
    </details>}
  </section>;
}

function CoreExample({ lesson }: { lesson: Lesson }) {
  const s = useStyles();
  const rev = useRevision(lesson.graph);
  if (!rev) return null;
  if (lesson.graph === "ontologyGaps") return <Coverage graph={lesson.graph} />;
  if (lesson.graph === "workloadFlows") return <ProjectFlows revision={rev} />;
  if (lesson.graph === "appTelemetry") return <AppTelemetry revision={rev} />;
  if (lesson.graph === "projectHealth") {
    const nodes = [...rev.graph.entities()];
    const metrics = nodes.filter(n => n.type.split(".").at(-1) === "PlatformMetric");
    const apps = nodes.filter(n => n.type.split(".").at(-1) === "ContainerApp");
    return <section aria-label="Project health checks">
      <ul className={s.model}>{apps.map(app => {
        const targets = new Set([...rev.graph.relationships()].filter(e => e.source === app.id && e.type.endsWith("HasPlatformMetric")).map(e => e.target));
        const checks = metrics.filter(n => targets.has(n.id));
        return <li key={app.id} className={s.modelPart} style={{ borderInlineStartColor: tokens.colorBrandStroke1 }}>
          <Button size="small" onClick={() => inspectElement(lesson.graph, app.id, "entity")}>{labelOf(app)}</Button>
          <Text block size={200}>Operational health · {textProperty(app, "state")}</Text>
          {checks.map(check => {
            const reading = check.props.value ?? check.props["mirror.value"];
            return <Button key={check.id} appearance="subtle" size="small" onClick={() => inspectElement(lesson.graph, check.id, "entity")}>
              {labelOf(check)} · {reading && reading.kind !== "null" ? valueText(reading) + "%" : "Unknown"}
            </Button>;
          })}
        </li>;
      })}</ul>
    </section>;
  }
  if (lesson.graph === "signals") return <SignalReading revision={rev} />;
  if (lesson.graph === "containerApps") {
    const nodes = [...rev.graph.entities()];
    const picks = nodes.filter(n => n.type.split(".").at(-1) === "ContainerApp");
    return <div className={s.buttons}>{picks.map(n => <Button key={n.id} size="small" onClick={() => inspectElement(lesson.graph, n.id, "entity")}>{textProperty(n, "name") ?? n.id}</Button>)}</div>;
  }
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
  const keys = lesson.graph === "master" ? ["flow-run-agent-tasks"]
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

function SignalReading({ revision }: { revision: GraphRevision }) {
  const s = useStyles();
  const master = useRevision("master");
  const [chosen, setChosen] = useState("");
  const signals = [...revision.graph.entities()].filter(n => n.type.split(".").at(-1) === "EvaluatedSignal")
    .sort((a, b) => (textProperty(a, "key") ?? "").localeCompare(textProperty(b, "key") ?? ""));
  const signal = signals.find(n => n.id === chosen) ?? signals.find(n => textProperty(n, "key") === "session-storage/azureResource/availability") ?? signals[0];
  if (!signal || !master) return <Text block size={200}>Select a checkpoint with signal readings.</Text>;
  function config(type: string, key?: string): Record<string, unknown> {
    const node = [...master!.graph.entities()].find(n => n.type.split(".").at(-1) === type && textProperty(n, "key") === key);
    try { return node ? JSON.parse(textProperty(node, "configuration") ?? "{}") : {}; } catch { return {}; }
  }
  const reading = config("Observation", textProperty(signal, "key"));
  const definition = config("SignalDefinition", textProperty(signal, "definitionKey"));
  return <section aria-label="Configured signal reading">
    <Field label="Signal binding"><Select value={signal.id} onChange={(_, data) => { setChosen(data.value); inspectElement(revision.id, data.value, "entity"); }}>
      {signals.map(n => <option key={n.id} value={n.id}>{textProperty(n, "key")}</option>)}
    </Select></Field>
    <dl className={s.healthSummary}>
      <div><dt>Source</dt><dd>{String(reading.source ?? "Unknown")}</dd></div>
      <div><dt>Reading</dt><dd>{String(reading.value ?? reading.availabilityState ?? reading.status ?? "Unknown")}</dd></div>
      <div><dt>State</dt><dd>{textProperty(signal, "state")}</dd></div>
      <div><dt>Window end</dt><dd>{String(reading.windowEnd ?? "Unknown")}</dd></div>
    </dl>
    {typeof definition.metricName === "string" && <Text block size={200}>{definition.metricName} · {String(definition.aggregationType)} · {String(definition.timeGrain)}</Text>}
    {typeof definition.queryText === "string" && <details className={s.details}><summary>Kusto query for this signal</summary><Code label="configured signal KQL" code={definition.queryText} /></details>}
    <Button size="small" onClick={() => inspectElement(revision.id, signal.id, "entity")}>Inspect evaluated signal</Button>
  </section>;
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
        <Button size="small" title={type} onClick={() => inspectEdge("master", edge.id)}>{label}<Badge size="small" appearance="outline" color="informative">{count.toLocaleString()}</Badge></Button>
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
  const examples = [
    { space: undefined, name: "Azure and Kubernetes" }, { space: "healthMirror", name: "CORE deployment" },
    { space: "flowReview", name: "Submission review" }, { space: "kiln", name: "Kiln health policy" },
  ];
  const localExercise = lesson.space === "flowReview" || lesson.space === "kiln";
  const startup = localExercise && !graph ? chapters[0].commands?.[0] : undefined;
  const commands = startup && !lesson.commands?.includes(startup) ? [startup, ...(lesson.commands ?? [])] : lesson.commands;
  useEffect(() => { heading.current?.focus(); }, []);
  const go = (id: string) => { const target = lessonOf(id); if (target) location.hash = href(target); };
  const close = () => {
    dispatch({ type: "ui/guide", lesson: null });
    requestAnimationFrame(() => document.getElementById("open-developer-guide")?.focus());
  };

  return <aside className={s.panel} aria-label="Developer guide">
    <div className={s.header}>
      <Field label="Developer guide"><Select className={s.picker} value={lesson.id} onChange={(_, data) => go(data.value)}>
        {examples.map(group => <optgroup key={group.name} label={group.name}>
          {LESSONS.filter(item => item.space === group.space).map(item => <option key={item.id} value={item.id}>{item.title}</option>)}
        </optgroup>)}
      </Select></Field>
      <Button appearance="subtle" icon={<Dismiss16Regular />} aria-label="Close developer guide" onClick={close} />
    </div>
    <div className={s.body}>
      <h2 ref={heading} tabIndex={-1} className={s.title}>{lesson.title}</h2>
      <Text as="p" className={s.goal}><strong>Goal</strong> {lesson.goal}</Text>
      {replayReady ? <Button as="a" appearance="primary" size="small" href={href(lessonOf("explore")!)}>Explore the master graph</Button> : <Text>{lesson.action}</Text>}
      {lesson.space === "healthMirror" && graph && <CoreExample lesson={lesson} />}
      {lesson.space === "flowReview" && graph && <SubmissionEvidence graph={lesson.graph} />}
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
      {lesson.id === "core-health" && <CheckpointComparison />}
      {lesson.id === "rollup" && graph && <PolicyTrace />}
      {lesson.id === "azure-health" && graph && <><CpuReplay model={lesson.graph} /><HealthEvidence graph={lesson.graph} /></>}
      {lesson.companionFlow ? <div className={s.flowPair}><Flow levels={lesson.flow} label={lesson.flowLabel ?? "Data flow"} /><Flow levels={lesson.companionFlow.levels} label={lesson.companionFlow.label} /></div> : <Flow levels={lesson.flow} label={lesson.flowLabel ?? "Data flow"} />}
      {lesson.id === "start" && <ModelParts />}
      {lesson.id === "azure-health" && <Thresholds />}
      <Text block size={200}><strong>Check</strong> {lesson.check}</Text>
      {graph ? <Text size={200}><strong>{replayReady ? "Replay ready" : lesson.build ? `Example ${graph.id}` : graph.id}</strong> · {graph.nodes.toLocaleString()} nodes · {graph.edges.toLocaleString()} links</Text>
        : <Text size={200}>{loaded === "loading" ? "Connecting to the engine…" : startup ? "Start this replay from the repository root." : lesson.id === "start" ? "Waiting for the tutorial engine." : <Link href={href(chapters[0])}>{chapters[0].title}</Link>}</Text>}
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
      {commands?.map(({ label, code }) => <details key={label} className={s.details} open={label === startup?.label || (lesson.id === "start" && !replayReady) || (lesson.id === "core-import" && !graph && label === "Open the local replay") || undefined}><summary>{label}</summary><Code label={label} code={code} /></details>)}
      {lesson.files?.map((file) => lesson.space === "healthMirror" ? <MirrorSource key={file} file={file} connected={Boolean(graph)} base={connections.find(item => item.id === lesson.source)!.url} /> : <Source key={file} file={file} />)}
      <details className={s.details}><summary>Ask your AI assistant</summary><p className={s.prompt}>{lesson.prompt}</p><Copy label="AI prompt" code={lesson.prompt} /></details>
      {lesson.id === "core-portable-flows" && <Link href={href(lessonOf("trace-submission")!)}>Follow an individual request in the local trace sample</Link>}
      {lesson.id === "core-ontology-gaps" && <Link href={href(lessonOf("inspect-coverage")!)}>Inspect role coverage in the local trace sample</Link>}
      {lesson.id === "core-project-health" && <Link href={href(lessonOf("kiln-policy")!)}>Try a recovery and freshness policy on the local kiln sample</Link>}
    </div>
    <nav className={s.footer} aria-label="Lessons">
      {previous ? <Button as="a" size="small" appearance="subtle" href={href(previous)} icon={<ArrowLeft16Regular />}>Back</Button> : <span />}
      {next ? <Button as="a" size="small" href={href(next)} icon={<ArrowRight16Regular />} iconPosition="after">Next</Button> : <span />}
    </nav>
  </aside>;
}
