// Application shell. Everything on screen is a Fluent component; the WebGL canvas sits behind CanvasHost.
import {
  Button, FluentProvider, Menu, MenuItemRadio, MenuList, MenuPopover, MenuTrigger, MessageBar, MessageBarActions, MessageBarBody, MessageBarTitle, Select, Spinner, Tab, TabList, Text,
  Toolbar, ToolbarButton, makeStyles, tokens,
} from "@fluentui/react-components";
import { Keyboard20Regular, WeatherMoon20Regular } from "@fluentui/react-icons";
import { Suspense, lazy, useEffect } from "react";
import { runtime } from "../state/runtime";
import { SOURCES } from "../state/sources";
import { dispatch, useStore } from "../state/store";
import type { ThemeName, View } from "../state/types";
import { formatHash } from "../state/url";
import { LESSONS, lessonOf, sourceForLesson, panelForLesson } from "../tutorial/lessons";
import { DataView, SpaceView } from "./DataView";
import { Dialogs } from "./Dialogs";

// The editor and the ontology load when first opened, or once the page is idle: the data graph the page opens on does not wait for them.
const loadEditor = () => import("./EditorView"), loadOntology = () => import("./OntologyView");
const EditorView = lazy(() => loadEditor().then((m) => ({ default: m.EditorView })));
const OntologyView = lazy(() => loadOntology().then((m) => ({ default: m.OntologyView })));
const whenIdle = (f: () => void) => (typeof requestIdleCallback === "function" ? requestIdleCallback(f, { timeout: 5000 }) : setTimeout(f, 2000));
import { THEMES, colorScheme } from "./theme";

const VIEWS: { id: View; label: string }[] = [{ id: "data", label: "Data graph" }, { id: "space", label: "Graph space" }, { id: "editor", label: "Projection editor" }, { id: "ontology", label: "Ontology" }];

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateRows: "auto auto minmax(0, 1fr) auto", height: "100dvh", backgroundColor: tokens.colorNeutralBackground1, color: tokens.colorNeutralForeground1 },
  head: { display: "flex", alignItems: "center", columnGap: tokens.spacingHorizontalL, flexWrap: "wrap", minHeight: "40px", padding: `0 ${tokens.spacingHorizontalM}`, backgroundColor: tokens.colorNeutralBackground1, borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  brand: { display: "flex", alignItems: "center", columnGap: tokens.spacingHorizontalS, margin: 0, fontSize: tokens.fontSizeBase300, fontWeight: tokens.fontWeightSemibold, letterSpacing: "0.01em" },
  grow: { flexGrow: 1 },
  views: { minWidth: 0, maxWidth: "100%", overflowX: "auto", scrollbarWidth: "thin", "@media (max-width: 700px)": { maskImage: "linear-gradient(to right, #000 calc(100% - 32px), transparent)" } },
  main: { minHeight: 0, overflow: "hidden", "&:focus": { outline: "none" } },
  guideMain: { display: "grid", gridTemplateColumns: "minmax(250px, 300px) minmax(0, 1fr)", "@media (max-width: 860px)": { gridTemplateColumns: "minmax(0, 1fr)", gridTemplateRows: "minmax(230px, 42%) minmax(360px, 1fr)", overflow: "auto" } },
  guideOffline: { display: "grid", gridTemplateColumns: "minmax(0, 600px)", gridTemplateRows: "minmax(0, 1fr)", justifyContent: "center", backgroundColor: tokens.colorNeutralBackground3, "& > aside": { borderLeft: `1px solid ${tokens.colorNeutralStroke2}` } },
  workspace: { minWidth: 0, minHeight: 0, overflow: "auto" },
  standardView: { height: "100%" },
  status: { display: "flex", columnGap: tokens.spacingHorizontalL, flexWrap: "wrap", padding: `${tokens.spacingVerticalXXS} ${tokens.spacingHorizontalM}`, borderTop: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground1, color: tokens.colorNeutralForeground2, "& b": { fontFamily: tokens.fontFamilyMonospace, fontWeight: tokens.fontWeightRegular, color: tokens.colorNeutralForeground1 } },
  skip: { position: "absolute", left: "-999px", ":focus": { left: tokens.spacingHorizontalM, top: tokens.spacingVerticalM, zIndex: 100, backgroundColor: tokens.colorNeutralBackground1, padding: tokens.spacingHorizontalM } },
  hidden: { position: "absolute", width: "1px", height: "1px", overflow: "hidden", clip: "rect(0 0 0 0)", whiteSpace: "nowrap" },
});

/** The product mark: a sheet with its zone ticks. */
const Mark = () => (
  <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true" focusable="false"><path d="M1.5 1.5h13v13h-13z M1.5 5.5h2 M1.5 10.5h2 M5.5 1.5v2 M10.5 1.5v2 M9 10h5.5v4.5" fill="none" stroke="currentColor" strokeWidth="1.2" /></svg>
);

function Status() {
  const s = useStyles();
  const st = { load: useStore((x) => x.load), conn: useStore((x) => x.connection), active: useStore((x) => x.activeGraph), graphs: useStore((x) => x.graphs) };
  useStore((x) => x.time.now);
  const ws = runtime.ws;
  const pane = ws?.pane(st.active);
  const hub = ws?.hub(st.active);
  const frames = pane?.view.frameTimes.slice(-60) ?? [];
  const avg = frames.length ? frames.reduce((a, b) => a + b, 0) / frames.length : 0;
  const g = st.graphs.find((x) => x.id === st.active);
  return (
    <div className={s.status} role="contentinfo">
      {g && <Text size={200}>Graph <b>{g.id}</b>: <b>{g.nodes.toLocaleString()}</b> nodes · <b>{g.edges.toLocaleString()}</b> edges</Text>}
      {pane && pane.graph.unresolvedEdgeCount > 0 && <Text size={200}><b>{pane.graph.unresolvedEdgeCount}</b> edges with a missing endpoint (not drawn)</Text>}
      {pane && <Text size={200}>{pane.layoutRunning ? `layout ${Math.round(pane.layoutProgress * 100)}%` : pane.lastLayoutMs ? `layout ${Math.round(pane.lastLayoutMs)} ms` : "layout idle"} · frame <b>{avg.toFixed(1)}</b> ms CPU · labels {pane.view.stats.visibleLabels}</Text>}
      {hub?.hasData && <Text size={200}>telemetry · ingest <b>{hub.store.stats.avgMs.toFixed(1)}</b> ms</Text>}
      {st.conn && <Text size={200} title={st.conn.detail}>engine <b>{st.conn.state}</b> · reconnects {st.conn.reconnects} · gaps filled {st.conn.gapsFilled}</Text>}
      {ws && <Text size={200}>source: {ws.source.name} · loaded in {Math.round(st.load.ms)} ms</Text>}
    </div>
  );
}

function Banners({ guideEngine }: { guideEngine: boolean }) {
  const load = useStore((x) => x.load);
  const fresh = useStore((x) => x.freshness);
  const conn = useStore((x) => x.connection);
  const source = useStore((x) => x.sourceId);
  return (
    <div role="status">
      {load.status === "loading" && <MessageBar intent="info"><MessageBarBody><Spinner size="tiny" label={`Loading ${SOURCES.find((x) => x.id === source)?.label ?? source}…`} labelPosition="after" /></MessageBarBody></MessageBar>}
      {load.status === "error" && !guideEngine && (
        <MessageBar intent="error"><MessageBarBody><MessageBarTitle>Could not load the source</MessageBarTitle>{load.message}</MessageBarBody><MessageBarActions><Button onClick={() => dispatch({ type: "source/opening", id: source })}>Retry</Button></MessageBarActions></MessageBar>
      )}
      {fresh === "stale" && <MessageBar intent="warning"><MessageBarBody><MessageBarTitle>Showing stale data</MessageBarTitle>{conn?.detail}. The view resumes from the last revision it has when the connection returns.</MessageBarBody></MessageBar>}
      {fresh === "resyncing" && <MessageBar intent="info"><MessageBarBody><Spinner size="tiny" label="Resynchronizing from a snapshot…" labelPosition="after" /></MessageBarBody></MessageBar>}
    </div>
  );
}

export function App() {
  const s = useStyles();
  const theme = useStore((x) => x.theme);
  const view = useStore((x) => x.view);
  const source = useStore((x) => x.sourceId);
  const guide = useStore((x) => x.guide);
  const lesson = lessonOf(guide);
  const firstLesson = LESSONS[0];
  const TutorialPanel = lesson && panelForLesson(lesson.id);
  const status = useStore((x) => x.load.status);
  const guideLayout = lesson ? status === "ready" ? s.guideMain : s.guideOffline : "";
  const rejected = useStore((x) => x.rejectedTotal);
  const announcement = useStore((x) => x.announcement);
  useEffect(() => { if (status === "ready") whenIdle(() => { void loadEditor(); void loadOntology(); }); }, [status]);
  // the root element carries the scheme, so the viewport's own scrollbars and canvas follow the theme too
  useEffect(() => { document.documentElement.style.colorScheme = colorScheme(theme); }, [theme]);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement;
      if (t.closest("input, textarea, select, [contenteditable], [role=combobox]") || e.ctrlKey || e.metaKey || e.altKey) return;
      if (e.key === "?") { e.preventDefault(); dispatch({ type: "ui/dialog", dialog: "shortcuts" }); }
      else if (e.key === "/" && (view === "data" || view === "space")) { e.preventDefault(); (document.querySelector("input[aria-label='Search elements']") as HTMLInputElement | null)?.focus(); }
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [view]);
  return (
    <FluentProvider theme={THEMES[theme]}>
      <div className={s.root}>
      <a className={s.skip} href="#main" onClick={(event) => { event.preventDefault(); document.getElementById("main")?.focus(); }}>Skip to the main view</a>
      <header className={s.head}>
        <h1 className={s.brand}><Mark />Sattle canvas</h1>
        <TabList className={s.views} selectedValue={view} onTabSelect={(_, d) => dispatch({ type: "ui/view", view: d.value as View })} aria-label="Views">{VIEWS.map((v) => <Tab key={v.id} value={v.id}>{v.label}</Tab>)}</TabList>
        <span className={s.grow} />
        <Select aria-label="Data source" value={source} onChange={(_, d) => dispatch({ type: "source/opening", id: d.value })}>{SOURCES.map((x) => <option key={x.id} value={x.id}>{x.label}</option>)}</Select>
        <Toolbar aria-label="Application">
          {!lesson && firstLesson && <Button as="a" id="open-developer-guide" href={formatHash({ source: sourceForLesson(firstLesson), view: firstLesson.view, guide: firstLesson.id })} appearance="subtle">Developer guide</Button>}
          <ToolbarButton onClick={() => dispatch({ type: "ui/dialog", dialog: "rejections" })}>Rejected: {rejected}</ToolbarButton>
          <ToolbarButton aria-label="Keyboard shortcuts" icon={<Keyboard20Regular />} onClick={() => dispatch({ type: "ui/dialog", dialog: "shortcuts" })} />
          <Menu checkedValues={{ theme: [theme] }} onCheckedValueChange={(_, d) => dispatch({ type: "ui/theme", theme: d.checkedItems[0] as ThemeName })}>
            <MenuTrigger disableButtonEnhancement><ToolbarButton aria-label="Theme" icon={<WeatherMoon20Regular />}>Theme</ToolbarButton></MenuTrigger>
            <MenuPopover><MenuList><MenuItemRadio name="theme" value="light">Light</MenuItemRadio><MenuItemRadio name="theme" value="dark">Dark</MenuItemRadio><MenuItemRadio name="theme" value="contrast">High contrast</MenuItemRadio></MenuList></MenuPopover>
          </Menu>
        </Toolbar>
      </header>
      <Banners guideEngine={!!lesson && source === "engine"} />
      <main id="main" tabIndex={-1} className={`${s.main} ${guideLayout}`}>
        {lesson && TutorialPanel && <Suspense fallback={<Spinner label="Loading the guide…" />}><TutorialPanel key={lesson.id} lesson={lesson} /></Suspense>}
        {status === "ready" && <div className={lesson ? s.workspace : s.standardView}>
          <Suspense fallback={<Spinner label="Loading the view…" />}>
            {view === "data" ? <DataView /> : view === "space" ? <SpaceView /> : view === "editor" ? <EditorView /> : <OntologyView key={lesson?.module ?? "ontology"} initialModule={lesson?.module} />}
          </Suspense>
        </div>}
      </main>
      <Status />
      <Dialogs />
      <div role="status" aria-live="polite" className={s.hidden}>{announcement}</div>
      </div>
    </FluentProvider>
  );
}
