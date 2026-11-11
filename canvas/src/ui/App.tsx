// Application shell. Everything on screen is a Fluent component; the WebGL canvas sits behind CanvasHost.
import {
  Button, FluentProvider, Menu, MenuItemRadio, MenuList, MenuPopover, MenuTrigger, MessageBar, MessageBarActions, MessageBarBody, MessageBarTitle, Select, Spinner, Tab, TabList, Text,
  Toolbar, ToolbarButton, makeStyles, teamsHighContrastTheme, tokens, webDarkTheme, webLightTheme,
} from "@fluentui/react-components";
import { Keyboard20Regular, WeatherMoon20Regular } from "@fluentui/react-icons";
import { useEffect } from "react";
import { runtime } from "../state/runtime";
import { SOURCES } from "../state/sources";
import { dispatch, useStore } from "../state/store";
import type { ThemeName, View } from "../state/types";
import { DataView, SpaceView } from "./DataView";
import { Dialogs } from "./Dialogs";
import { EditorView } from "./EditorView";
import { OntologyView } from "./OntologyView";

const themes = { light: webLightTheme, dark: webDarkTheme, contrast: teamsHighContrastTheme };
const VIEWS: { id: View; label: string }[] = [{ id: "data", label: "Data graph" }, { id: "space", label: "Graph space" }, { id: "editor", label: "Projection editor" }, { id: "ontology", label: "Ontology" }];

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateRows: "auto auto minmax(0, 1fr) auto", height: "100vh", backgroundColor: tokens.colorNeutralBackground1, color: tokens.colorNeutralForeground1 },
  head: { display: "flex", alignItems: "center", columnGap: tokens.spacingHorizontalM, flexWrap: "wrap", padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`, borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  grow: { flexGrow: 1 },
  main: { minHeight: 0, overflow: "hidden", "&:focus": { outline: "none" } },
  status: { display: "flex", columnGap: tokens.spacingHorizontalL, flexWrap: "wrap", padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`, borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
  skip: { position: "absolute", left: "-999px", ":focus": { left: tokens.spacingHorizontalM, top: tokens.spacingVerticalM, zIndex: 100, backgroundColor: tokens.colorNeutralBackground1, padding: tokens.spacingHorizontalM } },
  hidden: { position: "absolute", width: "1px", height: "1px", overflow: "hidden", clip: "rect(0 0 0 0)", whiteSpace: "nowrap" },
});

function Status() {
  const s = useStyles();
  const st = { load: useStore((x) => x.load), conn: useStore((x) => x.connection), active: useStore((x) => x.activeGraph), graphs: useStore((x) => x.graphs) };
  useStore((x) => x.time.now);
  const ws = runtime.ws;
  const pane = ws?.panes.get(st.active);
  const hub = ws?.hubs.get(st.active);
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

function Banners() {
  const load = useStore((x) => x.load);
  const fresh = useStore((x) => x.freshness);
  const conn = useStore((x) => x.connection);
  const source = useStore((x) => x.sourceId);
  return (
    <div role="status">
      {load.status === "loading" && <MessageBar intent="info"><MessageBarBody><Spinner size="tiny" label={`Loading ${SOURCES.find((x) => x.id === source)?.label ?? source}…`} labelPosition="after" /></MessageBarBody></MessageBar>}
      {load.status === "error" && (
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
  const status = useStore((x) => x.load.status);
  const rejected = useStore((x) => x.rejectedTotal);
  const announcement = useStore((x) => x.announcement);
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
    <FluentProvider theme={themes[theme]}>
      <div className={s.root}>
      <a className={s.skip} href="#main">Skip to the main view</a>
      <header className={s.head}>
        <Text as="h1" size={500} weight="semibold">◈ Sattle canvas</Text>
        <TabList selectedValue={view} onTabSelect={(_, d) => dispatch({ type: "ui/view", view: d.value as View })} aria-label="Views">{VIEWS.map((v) => <Tab key={v.id} value={v.id}>{v.label}</Tab>)}</TabList>
        <span className={s.grow} />
        <Select aria-label="Data source" value={source} onChange={(_, d) => dispatch({ type: "source/opening", id: d.value })}>{SOURCES.map((x) => <option key={x.id} value={x.id}>{x.label}</option>)}</Select>
        <Toolbar aria-label="Application">
          <ToolbarButton onClick={() => dispatch({ type: "ui/dialog", dialog: "rejections" })}>Rejected: {rejected}</ToolbarButton>
          <ToolbarButton aria-label="Keyboard shortcuts" icon={<Keyboard20Regular />} onClick={() => dispatch({ type: "ui/dialog", dialog: "shortcuts" })} />
          <Menu checkedValues={{ theme: [theme] }} onCheckedValueChange={(_, d) => dispatch({ type: "ui/theme", theme: d.checkedItems[0] as ThemeName })}>
            <MenuTrigger disableButtonEnhancement><ToolbarButton aria-label="Theme" icon={<WeatherMoon20Regular />}>Theme</ToolbarButton></MenuTrigger>
            <MenuPopover><MenuList><MenuItemRadio name="theme" value="light">Light</MenuItemRadio><MenuItemRadio name="theme" value="dark">Dark</MenuItemRadio><MenuItemRadio name="theme" value="contrast">High contrast</MenuItemRadio></MenuList></MenuPopover>
          </Menu>
        </Toolbar>
      </header>
      <Banners />
      <main id="main" tabIndex={-1} className={s.main}>
        {status === "ready" && (view === "data" ? <DataView /> : view === "space" ? <SpaceView /> : view === "editor" ? <EditorView /> : <OntologyView />)}
      </main>
      <Status />
      <Dialogs />
      <div role="status" aria-live="polite" className={s.hidden}>{announcement}</div>
      </div>
    </FluentProvider>
  );
}
