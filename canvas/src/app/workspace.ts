// Workspace: one opened GraphSource with a History and a lazily created DataPane per graph.
// Owns selection and cross-graph highlighting (shared identity and correspondences).
import { LIVE, timelineAt, type Timeline } from "./timeline";
import { nameGraph } from "../core/naming";
import type { Correspondence, GraphInfo, GraphSource, Reference, SpaceInfo } from "../source/types";
import { stubEntity } from "../core/reference";
import { DataPane, type PaneEvents, type Selection } from "./data-pane";
import type { Action, FilterState } from "../state/types";
import { History } from "./history";
import { TelemetryHub } from "./telemetry-hub";
import { mergeTimes, resolvePatchTimes, revisionCursorAt } from "../core/time-axis";
import type { Theme } from "../render/theme";
import { tally, type Coloring, type Target } from "./color-by";

export class Workspace {
  info!: SpaceInfo;
  private readonly histories = new Map<string, History>();
  private readonly panes = new Map<string, DataPane>();
  private readonly corr = new Map<string, Correspondence[]>();
  private readonly hubs = new Map<string, TelemetryHub>();
  /** sourceKey "graph|key" -> derived targets */
  private derivedOf = new Map<string, { graph: string; key: string }[]>();
  private targetSources = new Map<string, { graph: string; key: string }[]>();
  /** per graph: why its dangling endpoints are missing, as the engine says (read at the head once the graphs are loaded) */
  private readonly references = new Map<string, Map<string, Reference>>();
  private selected: Selection | null = null;
  /** whether the space follows the newest data or shows a time on the shared axis (seconds): see timeline.ts */
  private shownAt: Timeline = LIVE;
  private unsub: (() => void)[] = [];
  loadMs = 0;

  /** `dispatch` receives every event the source pushes, in order; the effects of the store apply them back through `apply*`. */
  constructor(readonly source: GraphSource, public theme: Theme, private readonly dispatch: (a: Action) => void) {}

  private disposed = false;

  async load(): Promise<void> {
    const t0 = performance.now();
    this.unsub.push(this.source.onRejected((r) => this.dispatch({ type: "feed/rejected", rejection: r, at: Date.now() })));
    const opened = await this.source.open();
    if (this.disposed) return; // the source was replaced while it was loading: nothing of this load reaches the store
    this.info = { ...opened, graphs: [...opened.graphs] };
    for (const g of this.info.graphs) {
      const snap = await this.source.snapshot(g.id);
      if (this.disposed) return;
      const hist = new History(snap);
      nameGraph(hist.graph, g.ontology, g.labels);
      this.histories.set(g.id, hist);
      const c = await this.source.correspondences?.(g.id);
      if (this.disposed) return;
      if (c?.length) this.addCorrespondences(g.id, c);
    }
    // telemetry: pull the ticks a non-streaming source can give up front; streamed ticks arrive as events
    const feed = this.source.telemetry;
    if (feed) {
      for (const g of this.info.graphs) {
        const h = this.histories.get(g.id)!;
        const total = h.graph.nodes.length + h.graph.edges.length;
        this.hubs.set(g.id, new TelemetryHub(h.graph, feed, total > 150000 ? 61 : 241));
      }
      for (let t = 0; t <= feed.ticks; t++) {
        for (const g of this.info.graphs) {
          const { batches, release } = await feed.fetch(g.id, t);
          if (this.disposed) return;
          const hub = this.hubs.get(g.id)!;
          for (const b of batches) hub.ingest(b);
          release?.();
        }
      }
    }
    // live sources declare their meters per graph (ranges, units, thresholds)
    if (this.source.meters) {
      for (const g of this.info.graphs) {
        const d = await this.source.meters(g.id);
        if (this.disposed) return;
        if (d) this.hubs.set(g.id, new TelemetryHub(this.histories.get(g.id)!.graph, d, 241));
      }
    }
    if (this.disposed) return;
    // subscribe; static sources deliver everything at once, simulated ones over time
    await Promise.all(this.info.graphs.map((g) => new Promise<void>((resolve) => {
      let ended = false;
      const done = () => { if (!ended) { ended = true; resolve(); } };
      this.unsub.push(this.source.subscribe(g.id, (e) => {
        if (this.disposed) return;
        if (e.type === "patch") this.dispatch({ type: "feed/patch", graph: g.id, patch: e.patch, time: e.time });
        else if (e.type === "telemetry") this.dispatch({ type: "feed/telemetry", graph: g.id, batch: e.batch });
        else if (e.type === "telemetry-reset") this.dispatch({ type: "feed/telemetryReset", graph: g.id });
        else if (e.type === "meta") this.dispatch({ type: "feed/meta", graph: g.id, meta: e.meta });
        else if (e.type === "resync") this.dispatch({ type: "feed/resync", reason: e.reason });
        else if (e.type === "rejected") this.dispatch({ type: "feed/rejected", rejection: e.rejection, at: Date.now() });
        else if (e.type === "caught-up" || e.type === "end") done();
      }));
      if (this.source.controls) setTimeout(done, 0); // simulated streams do not end until played
    })));
    this.loadMs = performance.now() - t0;
  }

  private addCorrespondences(graph: string, list: Correspondence[]): void {
    this.corr.set(graph, [...(this.corr.get(graph) ?? []), ...list]);
    for (const c of list) {
      this.targetSources.set(`${graph}|${c.target}`, c.sources);
      for (const s of c.sources) {
        const k = `${s.graph}|${s.key}`;
        const arr = this.derivedOf.get(k);
        if (arr) arr.push({ graph, key: c.target }); else this.derivedOf.set(k, [{ graph, key: c.target }]);
      }
    }
  }

  /** Effect of `feed/patch`: append to the history and update the pane. Problems become rejections. */
  applyPatch(graph: string, patch: import("../core/model").Patch, time?: number): void {
    const h = this.histories.get(graph);
    if (!h) return;
    const before = h.problems.length;
    // definitions are configuration, not scrubbed by data time: a catalog patch always applies
    const step = h.append(patch, time, this.shownAt.k === "live" || this.graphInfo(graph)?.role === "catalog");
    for (let i = before; i < h.problems.length; i++) {
      const p = h.problems[i];
      this.dispatch({ type: "feed/rejected", at: Date.now(), rejection: { source: graph, where: `patch ${p.patch}`, errors: [p.reason, ...(p.conflicts ?? []).slice(0, 3).map((c) => `${c.key}: ${c.message}`)] } });
    }
    const pane = this.panes.get(graph);
    if (step) pane?.applyStep(step);
    // the space shows a time: the patch is kept, and the graph shows what that time holds (the patch too, if it is from before it)
    else if (this.shownAt.k === "at") {
      const target = this.cursorAt(graph, this.shownAt.t);
      if (h.cursor !== target) pane?.applySteps(h.seek(target));
    }
  }

  /**
   * The engine says why the endpoints that relationships name are missing; the stubs for them are named from that. Asked once per graph, when it
   * is first shown (the history is in by then), and only when the graph has stubs.
   */
  private referencesAsked = new Set<string>();
  private async loadReferences(graph: string): Promise<void> {
    const g = this.histories.get(graph)?.graph;
    if (!g || !g.stubCount || !this.source.references || this.referencesAsked.has(graph)) return;
    this.referencesAsked.add(graph);
    try {
      const refs = new Map((await this.source.references(graph)).map((r) => [r.id, r]));
      this.references.set(graph, refs);
      g.stubFor = (id) => stubEntity(id, refs.get(id));
      g.relabelStubs();
      this.panes.get(graph)?.refreshAll();
      this.dispatch({ type: "data/touched", graph });
    } catch { /* the stubs keep their generic name */ }
  }

  paneFor(graph: string): DataPane {
    let p = this.panes.get(graph);
    if (p) return p;
    void this.loadReferences(graph);
    const info = this.info.graphs.find((g) => g.id === graph)!;
    const host = document.createElement("div");
    const events: PaneEvents = {
      select: (sel) => this.dispatch({ type: "select/element", selection: sel }),
      hover: (sel) => this.dispatch({ type: "hover/element", selection: sel }),
      activate: () => {},
      changed: () => this.dispatch({ type: "data/touched", graph }),
      announce: (text) => this.dispatch({ type: "ui/announce", text }),
      key: () => false,
    };
    p = new DataPane(info, this.histories.get(graph)!, host, this.theme, events, this.hubs.get(graph) ?? null);
    this.panes.set(graph, p);
    p.start();
    return p;
  }

  // ---- shared time axis ----
  /** Commit time of every patch of a graph: the source's times when all are present, else placed by order on the telemetry windows. */
  patchTimes(graph: string): { times: number[]; source: "source" | "ordinal" | "mixed" } {
    const h = this.histories.get(graph)!;
    const own = this.hubs.get(graph)?.hasData ? this.hubs.get(graph)!.store.frameTimes() : [];
    let windows = own;
    if (!windows.length) for (const hub of this.hubs.values()) if (hub.hasData) { windows = hub.store.frameTimes(); break; }
    return resolvePatchTimes(h.sourceTimes, windows);
  }

  /** All points on the axis: commit times of every revision and window starts of every telemetry frame. */
  axisPoints(): number[] {
    const lists: number[][] = [[0]];
    for (const g of this.info.graphs) if (g.role !== "catalog") lists.push(this.patchTimes(g.id).times);
    for (const hub of this.hubs.values()) if (hub.hasData) lists.push(hub.store.frameTimes());
    return mergeTimes(lists);
  }

  /** Revision cursor of a graph at the shared time. */
  cursorAt(graph: string, t: number): number { return revisionCursorAt(this.patchTimes(graph).times, t); }

  /** Hub of a graph; created on the first telemetry batch for sources that stream telemetry without declaring it. */
  hubFor(graph: string): TelemetryHub {
    let hub = this.hubs.get(graph);
    if (!hub) {
      const h = this.histories.get(graph)!;
      hub = new TelemetryHub(h.graph, { meters: [], defaults: {} }, 241);
      hub.show(this.shownAt);
      this.hubs.set(graph, hub);
      this.panes.get(graph)?.attachTelemetry(hub);
    }
    return hub;
  }

  graphInfo(id: string): GraphInfo | undefined { return this.info.graphs.find((g) => g.id === id); }

  // ---- what the views read ----
  /** the history of a graph: its graph at the cursor and its patches */
  history(graph: string): History | undefined { return this.histories.get(graph); }
  /** the pane of a graph once it is shown */
  pane(graph: string): DataPane | undefined { return this.panes.get(graph); }
  hub(graph: string): TelemetryHub | undefined { return this.hubs.get(graph); }
  get timeline(): Timeline { return this.shownAt; }
  get selection(): Selection | null { return this.selected; }
  /** why a dangling endpoint is missing, as the engine says */
  reference(graph: string, id: string): Reference | undefined { return this.references.get(graph)?.get(id); }
  lineageOf(sel: Selection): import("../source/types").Lineage | undefined { return this.lineageInfo.get(`${sel.graph}|${sel.kind}|${sel.id}`); }
  get hasCorrespondences(): boolean { return this.corr.size > 0; }

  /** Count of entities per type in a graph at its cursor. */
  typeCounts(graph: string): Map<string, number> {
    const m = new Map<string, number>();
    const g = this.histories.get(graph)!.graph;
    for (const e of g.entities()) m.set(e.type, (m.get(e.type) ?? 0) + 1);
    for (const e of g.stubs()) m.set(e.type, (m.get(e.type) ?? 0) + 1);
    return m;
  }

  /** Shape and color of a type as the canvas draws it (a neutral circle when there is no pane). */
  styleOf(graph: string, type: string): { shape: string; color: string } {
    const pane = this.panes.get(graph);
    return pane ? { shape: pane.shapeName(type), color: pane.colorCss(type) } : { shape: "circle", color: "#8ec8ff" };
  }

  /** Counts per colored value over the nodes or the edges (see `tally`), how many have none, and the hint that maps the values to roles. */
  valueCounts(graph: string, c: Coloring, target: Target = "node"): ReturnType<typeof tally> {
    const g = this.histories.get(graph)!.graph;
    return tally(c, target === "node" ? g.entities() : g.relationships());
  }

  // ---- time and what the panes show ----
  /** Move one graph to `target` patches applied. */
  seekGraph(graph: string, target: number, fs: FilterState | undefined): void {
    const steps = this.histories.get(graph)!.seek(target);
    const pane = this.panes.get(graph);
    if (pane) { pane.applySteps(steps); this.highlight(graph, fs); }
  }

  /** Move the whole space to time t: every graph shows the revision and the telemetry frame valid at that instant. */
  seek(t: number, filters: Record<string, FilterState>): void {
    this.shownAt = timelineAt(this.axisPoints(), t);
    for (const hub of this.hubs.values()) if (hub.hasData) hub.show(this.shownAt);
    for (const g of this.info.graphs) {
      if (g.role === "catalog") continue; // definitions are configuration, not scrubbed by data time
      const target = this.cursorAt(g.id, t);
      if (this.histories.get(g.id)!.cursor !== target) this.seekGraph(g.id, target, filters[g.id]);
    }
  }

  /** The change highlight a graph's filter asks for: the last step, everything since a revision, or none. */
  highlight(graph: string, fs: FilterState | undefined): void {
    const hist = this.histories.get(graph);
    const pane = this.panes.get(graph);
    if (!hist || !pane) return;
    const mode = fs?.hlMode ?? "step";
    if (mode === "off" || (hist.cursor === 0 && mode === "step")) pane.setHighlight(null);
    else if (mode === "since") pane.setHighlight(hist.changesBetween(Math.min(fs!.hlSince, hist.cursor), hist.cursor));
    else pane.setHighlight(hist.changesBetween(hist.cursor - 1, hist.cursor));
  }

  /** Apply the parts of a graph's filter state that changed to its pane. */
  applyFilter(graph: string, f: FilterState, changed: Partial<FilterState>): void {
    this.panes.get(graph)?.applyFilter(f, changed);
  }

  setTheme(t: Theme): void { this.theme = t; for (const p of this.panes.values()) p.setTheme(t); }

  // ---- selection ----
  select(sel: Selection | null): void {
    this.selected = sel;
    for (const [id, pane] of this.panes) pane.setSelection(sel && sel.graph === id ? sel : null);
    this.updateCross();
    if (sel) void this.loadAbstraction(sel);
  }

  /** Elements related to the selection: same entity id elsewhere, derived elements, and source elements. */
  related(sel: Selection): { identity: Map<string, Set<string>>; sources: Map<string, Set<string>>; derived: { graph: string; key: string }[]; sourceList: { graph: string; key: string }[] } {
    const key = (sel.kind === "entity" ? "n:" : "e:") + sel.id;
    const identity = new Map<string, Set<string>>();
    const sources = new Map<string, Set<string>>();
    const add = (m: Map<string, Set<string>>, graph: string, k: string) => { const s = m.get(graph) ?? new Set<string>(); s.add(k); m.set(graph, s); };
    if (sel.kind === "entity") for (const g of this.info.graphs) if (g.id !== sel.graph && this.histories.get(g.id)!.graph.nodeSlot.has(sel.id)) add(identity, g.id, `n:${sel.id}`);
    const derived = this.derivedOf.get(`${sel.graph}|${key}`) ?? [];
    for (const d of derived) add(identity, d.graph, d.key);
    const sourceList = this.targetSources.get(`${sel.graph}|${key}`) ?? [];
    for (const s of sourceList) add(sources, s.graph, s.key);
    return { identity, sources, derived, sourceList };
  }

  /**
   * What the selection stands for below it and is part of above it, over every graph: the sources of its lineage and theirs in turn,
   * and the elements that name it, and them in turn, as a source. Nodes and edges apart (`n:` and `e:` keys).
   */
  closure(sel: Selection): { lower: Map<string, Set<string>>; upper: Map<string, Set<string>> } {
    const walk = (from: Map<string, { graph: string; key: string }[]>) => {
      const found = new Map<string, Set<string>>();
      const start = `${sel.graph}|${(sel.kind === "entity" ? "n:" : "e:") + sel.id}`;
      const seen = new Set([start]), queue = [start];
      while (queue.length) {
        for (const x of from.get(queue.shift()!) ?? []) {
          const k = `${x.graph}|${x.key}`;
          if (seen.has(k)) continue;
          seen.add(k); queue.push(k);
          const set = found.get(x.graph) ?? new Set<string>(); set.add(x.key); found.set(x.graph, set);
        }
      }
      return found;
    };
    return { lower: walk(this.targetSources), upper: walk(this.derivedOf) };
  }

  updateCross(): void {
    const rel = this.selected ? this.related(this.selected) : null;
    const abs = this.selected ? this.closure(this.selected) : null;
    for (const [id, pane] of this.panes) {
      if (this.selected?.graph === id) { pane.setCross({ nodes: new Set(), edges: new Set() }, { nodes: new Set(), edges: new Set() }); continue; }
      const take = (m: Map<string, Set<string>> | undefined, p: "n:" | "e:") => new Set([...(m?.get(id) ?? [])].filter((k) => k.startsWith(p)).map((k) => k.slice(2)));
      const above = (p: "n:" | "e:") => new Set([...take(rel?.identity, p), ...take(abs?.upper, p)]);
      const below = (p: "n:" | "e:") => new Set([...take(rel?.sources, p), ...take(abs?.lower, p)]);
      pane.setCross({ nodes: above("n:"), edges: above("e:") }, { nodes: below("n:"), edges: below("e:") });
    }
  }

  /** An element named as a source of another: what the lineage of one says, kept in both directions. */
  private link(graph: string, target: string, source: { graph: string; key: string }): void {
    const t = `${graph}|${target}`, k = `${source.graph}|${source.key}`;
    const down = this.targetSources.get(t) ?? [];
    if (!down.some((x) => x.graph === source.graph && x.key === source.key)) { down.push(source); this.targetSources.set(t, down); }
    const up = this.derivedOf.get(k) ?? [];
    if (!up.some((x) => x.graph === graph && x.key === target)) { up.push({ graph, key: target }); this.derivedOf.set(k, up); }
  }

  /**
   * The abstraction an element belongs to, from the source's lineage: what it stands for below (its sources, theirs in turn, down to the master)
   * and what it is part of above (the elements that name it, and those in turn). Followed level by level so the highlight grows while it loads;
   * at most `BUDGET` lookups per selection, so a large graph never starts a flood of requests.
   */
  private lineageDone = new Set<string>();
  private reverseDone = new Set<string>();
  async loadAbstraction(sel: Selection): Promise<void> {
    const BUDGET = 400;
    this.cutOff.delete(`${sel.graph}|${sel.kind}|${sel.id}`);
    const here = () => !!this.selected && this.selected.graph === sel.graph && this.selected.id === sel.id;
    const role = (g: string) => this.graphInfo(g)?.role;
    const keyOf = (kind: "entity" | "relationship", id: string) => (kind === "entity" ? "n:" : "e:") + id;
    let budget = BUDGET;
    const down = async () => {
      const fn = this.source.lineage;
      if (!fn) return;
      const seen = new Set<string>();
      let frontier: Selection[] = [sel];
      while (frontier.length && budget > 0 && here()) {
        const next: Selection[] = [];
        for (const e of frontier) {
          const id = `${e.graph}|${e.kind}|${e.id}`;
          if (seen.has(id) || role(e.graph) === "catalog" || role(e.graph) === "master" || role(e.graph) === "input") continue;   // an input graph has no graph below it
          seen.add(id);
          if (!this.lineageDone.has(id)) {
            if (budget-- <= 0) break;
            this.lineageDone.add(id);
            let l;
            try { l = await fn.call(this.source, e.graph, e.id); } catch { continue; }
            if (!l) continue;
            this.lineageInfo.set(id, l);
            for (const s of l.sources) this.link(e.graph, keyOf(e.kind, e.id), { graph: s.graph, key: keyOf(s.kind, s.element) });
          }
          for (const s of this.targetSources.get(`${e.graph}|${keyOf(e.kind, e.id)}`) ?? []) next.push({ graph: s.graph, kind: s.key.startsWith("n:") ? "entity" : "relationship", id: s.key.slice(2) });
        }
        frontier = next;
        if (here()) { this.updateCross(); this.dispatch({ type: "data/touched", graph: sel.graph }); }
      }
    };
    const up = async () => {
      const fn = this.source.reverseLineage;
      if (!fn) return;
      const seen = new Set<string>();
      let frontier: Selection[] = [sel];
      while (frontier.length && budget > 0 && here()) {
        const next: Selection[] = [];
        for (const e of frontier) {
          const id = `${e.graph}|${e.kind}|${e.id}`;
          if (seen.has(id) || role(e.graph) === "catalog") continue;
          seen.add(id);
          if (!this.reverseDone.has(id)) {
            if (budget-- <= 0) break;
            this.reverseDone.add(id);
            let r;
            try { r = await fn.call(this.source, e.graph, e.id); } catch { continue; }
            if (!r) continue;
            this.reverseInfo.set(id, r);
            for (const p of r.partOf) this.link(p.graph, keyOf(p.kind, p.element), { graph: e.graph, key: keyOf(e.kind, e.id) });
          }
          for (const d of this.derivedOf.get(`${e.graph}|${keyOf(e.kind, e.id)}`) ?? []) next.push({ graph: d.graph, kind: d.key.startsWith("n:") ? "entity" : "relationship", id: d.key.slice(2) });
        }
        frontier = next;
        if (here()) { this.updateCross(); this.dispatch({ type: "data/touched", graph: sel.graph }); }
      }
    };
    await Promise.all([down(), up()]);
    if (budget <= 0) this.cutOff.add(`${sel.graph}|${sel.kind}|${sel.id}`);
  }
  private reverseInfo = new Map<string, import("../source/types").ReverseLineage>();
  private cutOff = new Set<string>();

  /**
   * Where the highlight of a selection is not complete, so it is never shown as if it were: elements below whose lineage the engine only knows by
   * the key of their group (`complete: false`), graphs above that read an element through an aggregate (`incompleteIn`), and a selection whose
   * lookups hit the cap. Only what has been looked up is judged.
   */
  partialOf(sel: Selection): { below: { graph: string; kind: "entity" | "relationship"; id: string }[]; aboveIn: string[]; cutOff: boolean } {
    const mine = { graph: sel.graph, kind: sel.kind, id: sel.id };
    const { lower, upper } = this.closure(sel);
    const kindOf = (k: string) => (k.startsWith("n:") ? "entity" : "relationship") as "entity" | "relationship";
    const below = [mine, ...[...lower].flatMap(([graph, keys]) => [...keys].map((k) => ({ graph, kind: kindOf(k), id: k.slice(2) })))]
      .filter((e) => this.graphInfo(e.graph)?.role !== "master" && this.graphInfo(e.graph)?.role !== "input" && this.lineageInfo.get(`${e.graph}|${e.kind}|${e.id}`)?.complete === false);
    const above = new Set<string>();
    for (const e of [mine, ...[...upper].flatMap(([graph, keys]) => [...keys].map((k) => ({ graph, kind: kindOf(k), id: k.slice(2) })))]) for (const g of this.reverseInfo.get(`${e.graph}|${e.kind}|${e.id}`)?.incompleteIn ?? []) above.add(g);
    return { below, aboveIn: [...above].sort(), cutOff: this.cutOff.has(`${sel.graph}|${sel.kind}|${sel.id}`) };
  }
  private readonly lineageInfo = new Map<string, import("../source/types").Lineage>();

  describe(sel: Selection): string {
    const h = this.histories.get(sel.graph);
    if (!h) return sel.id;
    const e = sel.kind === "entity" ? h.graph.entity(sel.id) : h.graph.rel(sel.id);
    return e ? ("type" in e ? `${e.type} ${h.graph.label(e)}` : sel.id) : sel.id;
  }

  /** What a definition edit can change in a graph (its ontology, the roles of its values), read again by the source. */
  applyMeta(graph: string, meta: import("../source/types").GraphMeta): void {
    const info = this.info.graphs.find((g) => g.id === graph);
    if (!info) return;
    Object.assign(info, meta);
    const g = this.histories.get(graph)?.graph;
    if (g) { g.namer = null; nameGraph(g, info.ontology, info.labels); }
    this.panes.get(graph)?.refreshAll();
  }

  dispose(): void {
    this.disposed = true;
    for (const u of this.unsub) u();
    this.source.dispose?.();
    for (const p of this.panes.values()) p.dispose();
    this.panes.clear();
    this.hubs.clear();
  }
}
