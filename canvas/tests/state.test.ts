import { beforeEach, describe, expect, it } from "vitest";
import { runtime } from "../src/state/runtime";
import { initialState, reduce } from "../src/state/reducer";
import { dispatch, useStore } from "../src/state/store";
import type { Action, State } from "../src/state/types";
import { emptyFilter } from "../src/state/types";
import { addN, ent, patch, fakeWorkspace } from "./helpers";
import { editorTransition, type EditorEvent, type EditorPhase } from "../src/state/editor-phase";
import { EditorSession } from "../src/editor/session";
import type { Preview } from "../src/editor/service";
import { fakeEngine } from "./fixtures/editor-fake";
import { Graph } from "../src/core/graph";
import type { SpaceInfo } from "../src/source/types";
import { SOURCES } from "../src/state/sources";
import { settle } from "./fixtures/engine-fake";
import { TreeSource } from "./fixtures/tree-source";

const base = (over: Partial<State> = {}): State => ({ ...initialState(), ...over });
const sel = { graph: "master", kind: "entity" as const, id: "e1" };

describe("the reducer turns an action into a new state and never changes the old one", () => {
  it.each<[string, Action, (s: State) => unknown, unknown]>([
    ["theme", { type: "ui/theme", theme: "contrast" }, (s) => s.theme, "contrast"],
    ["view", { type: "ui/view", view: "editor" }, (s) => s.view, "editor"],
    ["dialog", { type: "ui/dialog", dialog: "shortcuts" }, (s) => s.dialog, "shortcuts"],
    ["selection", { type: "select/element", selection: sel }, (s) => s.selection, sel],
    ["selection is announced", { type: "select/element", selection: sel }, (s) => s.announcement, "Selected entity e1 in master"],
    ["clearing the selection", { type: "select/element", selection: null }, (s) => s.announcement, "Selection cleared"],
    ["a seek moves the shared time", { type: "time/seek", t: 5 }, (s) => s.time.now, 5],
    ["a rejection is kept with its time", { type: "feed/rejected", at: 7, rejection: { source: "x", where: "y", errors: ["z"] } }, (s) => s.rejections.map((r) => r.at), [7]],
    ["a patch and a windows batch do not change the state (the runtime holds the data)", { type: "feed/patch", graph: "master", patch: patch("0", "1", [addN(ent("a"))]) }, (s) => s === base(), false],
    ["a connection that is not live means stale data", { type: "feed/connection", status: { state: "reconnecting", detail: "x", gapsFilled: 0, reconnects: 1 } }, (s) => s.freshness, "stale"],
    ["a live connection means live data", { type: "feed/connection", status: { state: "live", detail: "x", gapsFilled: 0, reconnects: 0 } }, (s) => s.freshness, "live"],
    ["a resync is announced", { type: "feed/resync", reason: "event gap" }, (s) => s.freshness, "resyncing"],
    ["a filter change merges into the graph's filter", { type: "filter/set", graph: "master", filter: { search: "pod", hiddenTypes: ["Pod"] } }, (s) => [s.filters.master.search, s.filters.master.hiddenTypes, s.filters.master.layout], ["pod", ["Pod"], "auto"]],
    ["editor summary merges", { type: "editor/summary", patch: { revision: "4", notice: "n" } }, (s) => [s.editor.revision, s.editor.notice], ["4", "n"]],
  ])("%s", (_, action, pick, expected) => {
    const s0 = base({ filters: { master: emptyFilter() } });
    if (action.type === "feed/patch") expect(reduce(s0, action)).toBe(s0);
    else expect(pick(reduce(s0, action))).toEqual(expected);
  });

  it("counts every rejection, not only the last 200", () => {
    let s = initialState();
    for (let i = 0; i < 250; i++) s = reduce(s, { type: "feed/rejected", rejection: { source: "x", where: `w${i}`, errors: ["e"] }, at: i });
    expect(s.rejectedTotal, "the header says Rejected: 200 after 250").toBe(250);
    expect(s.rejections.length, "the list itself stays bounded").toBeLessThanOrEqual(200);
  });

  it("is pure: the input state is never changed", () => {
    const s0 = base();
    const frozen = JSON.stringify(s0);
    for (const a of [{ type: "ui/theme", theme: "dark" }, { type: "select/element", selection: sel }, { type: "filter/set", graph: "g", filter: { search: "x" } }] as Action[]) reduce(s0, a);
    expect(JSON.stringify(s0)).toBe(frozen);
  });
});

describe("effects: server pushes are applied to the runtime in the order they are dispatched", () => {
  beforeEach(async () => {
    dispatch({ type: "source/opening", id: "synth-small" });
    for (let i = 0; i < 200 && useStore.getState().load.status !== "ready"; i++) await new Promise((r) => setTimeout(r, 25));
    expect(useStore.getState().load.status).toBe("ready");
  });

  it("consecutive patches append in order, a gap is rejected and shows up in the state", async () => {
    const ws = runtime.ws!;
    const graph = useStore.getState().activeGraph;
    const h = ws.history(graph)!;
    const head = h.revisionAt(h.length);
    const n = Number(head);
    const mk = (from: number, to: number, id: string) => ({ ...patch(String(from), String(to), [addN(ent(id, "Service"))]), from: { ...h.snapshot.version, revision: String(from) }, to: { ...h.snapshot.version, revision: String(to) } });
    const before = h.length;
    dispatch({ type: "feed/patch", graph, patch: mk(n, n + 1, "zz-a") });
    dispatch({ type: "feed/patch", graph, patch: mk(n + 1, n + 2, "zz-b") });
    dispatch({ type: "feed/patch", graph, patch: mk(n + 5, n + 6, "zz-gap") });
    expect(h.length).toBe(before + 2);
    expect(h.graph.entity("zz-a")).toBeDefined();
    expect(h.graph.entity("zz-b")).toBeDefined();
    expect(h.graph.entity("zz-gap")).toBeUndefined();
    expect(useStore.getState().rejections.length).toBeGreaterThan(0);
    await new Promise((r) => setTimeout(r, 400));
    expect(useStore.getState().graphs.find((g) => g.id === graph)!.patches).toBe(before + 2);
  });

  it("window frames never reach the state", () => {
    const ws = runtime.ws!;
    const graph = useStore.getState().activeGraph;
    const hub = ws.hub(graph);
    const s0 = useStore.getState();
    dispatch({ type: "feed/windows", graph, batch: { graph, tick: 1, t: 1, target: "node", property: hub?.store.metersOf("node")[0]?.id ?? "cpu", values: new Float32Array(0), ids: [] } });
    expect(useStore.getState().graphs).toBe(s0.graphs);
    expect(useStore.getState().time).toBe(s0.time);
  });

  it("selecting dispatches through the runtime and back", () => {
    const graph = useStore.getState().activeGraph;
    const id = runtime.ws!.history(graph)!.graph.entities().next().value!.id;
    dispatch({ type: "select/element", selection: { graph, kind: "entity", id } });
    expect(runtime.ws!.selection).toEqual({ graph, kind: "entity", id });
    expect(useStore.getState().selection!.id).toBe(id);
  });
});

describe("the editor flow is one state machine: closed, opening, ready with at most one piece of work, failed", () => {
  const ready = (work: "undo" | "preview" | null = null): EditorPhase => ({ t: "ready", work });
  it.each<[string, EditorPhase, EditorEvent, EditorPhase]>([
    ["a closed editor opens", { t: "closed" }, { t: "open" }, { t: "opening" }],
    ["a draft opened", { t: "opening" }, { t: "opened" }, ready()],
    ["opening failed", { t: "opening" }, { t: "failed", message: "no engine" }, { t: "failed", message: "no engine" }],
    ["a failure after the draft opened changes nothing", ready(), { t: "failed", message: "late" }, ready()],
    ["a late opened after a failure changes nothing", { t: "failed", message: "x" }, { t: "opened" }, { t: "failed", message: "x" }],
    ["try again", { t: "failed", message: "x" }, { t: "open" }, { t: "opening" }],
    ["work starts", ready(), { t: "start", work: "preview" }, ready("preview")],
    ["work ends", ready("preview"), { t: "done", work: "preview" }, ready()],
    ["the end of work another start replaced is no end", ready("preview"), { t: "done", work: "undo" }, ready("preview")],
    ["no work without a draft", { t: "opening" }, { t: "start", work: "undo" }, { t: "opening" }],
    ["a new draft while work runs", ready("undo"), { t: "open" }, { t: "opening" }],
  ])("%s", (_, from, event, to) => {
    expect(editorTransition(from, event)).toEqual(to);
  });
});

/** A promise and the function that settles it, for an engine call that answers when the test says. */
const later = <T,>() => { let settle!: (v: T) => void; const p = new Promise<T>((r) => { settle = r; }); return { p, settle }; };
const tick = (ms = 0) => new Promise((r) => setTimeout(r, ms));

describe("an engine answer that arrives after its draft ended changes nothing the author sees", () => {
  it("a list of releases that answers after a commit is not shown in the next draft", async () => {
    const eng = fakeEngine();
    const releases = later<unknown[]>(), commit = later<{ catalogRevision: string; modules: string[] }>();
    Object.assign(eng.svc, { releases: () => releases.p, commit: () => commit.p, discard: async () => {} });
    const ws = runtime.ws;
    // the catalog head a commit is checked against
    runtime.ws = { source: { catalogGraph: "catalog" }, ...fakeWorkspace({ catalog: { graph: new Graph(), length: 0, revisionAt: () => "11" } }) } as never;
    runtime.session = await EditorSession.open(eng.svc);
    dispatch({ type: "editor/event", event: { t: "open" } });
    dispatch({ type: "editor/event", event: { t: "opened" } });
    dispatch({ type: "editor/releases", module: "m" });
    dispatch({ type: "editor/commit" });
    await tick();
    // no engine for the draft that opens after the commit: it fails to open, which is not what this test is about
    runtime.ws = null;
    commit.settle({ catalogRevision: "12", modules: ["m"] });
    await tick(20);
    releases.settle([{ module: "m", version: "1", digest: "sha256:old", catalogRevision: "11", supersedes: null }]);
    await tick(20);
    expect([useStore.getState().editor.releases, useStore.getState().editor.committed]).toEqual([[], "Committed catalog revision 12 (m)"]);
    runtime.ws = ws;
  });

  it("a restore that answers after the draft was discarded changes nothing the author sees", async () => {
    const eng = fakeEngine();
    const restore = later<unknown>();
    Object.assign(eng.svc, { restore: () => restore.p, discard: async () => {} });
    const ws = runtime.ws;
    runtime.ws = null;
    runtime.session = await EditorSession.open(eng.svc);
    dispatch({ type: "editor/event", event: { t: "open" } });
    dispatch({ type: "editor/event", event: { t: "opened" } });
    dispatch({ type: "editor/do", cmd: { t: "restore", digest: "sha256:r" } });
    await tick();
    dispatch({ type: "editor/discard" });
    restore.settle({ revision: "1", ok: true, diagnostics: [], modules: {}, patch: patch("0", "1", [addN(ent("d:m/entity:E1"))]) });
    await tick(20);
    const ed = useStore.getState().editor;
    expect([ed.revision, ed.undo, ed.phase.t], "the old draft's restore is reported over the new one").toEqual(["", [], "failed"]);
    runtime.ws = ws;
  });

  it("a preview that answers after the draft was discarded shows nothing", async () => {
    const eng = fakeEngine();
    let answer!: (p: Preview) => void;
    const svc = eng.svc as unknown as Record<string, unknown>;
    svc.preview = () => new Promise<Preview>((r) => { answer = r; });
    svc.discard = async () => {};
    runtime.session = await EditorSession.open(eng.svc);
    dispatch({ type: "editor/event", event: { t: "open" } });
    dispatch({ type: "editor/event", event: { t: "opened" } });
    dispatch({ type: "editor/preview" });
    await new Promise((r) => setTimeout(r, 0));
    dispatch({ type: "editor/discard" });
    answer({ scope: { rows: "1", totalRows: "1", complete: true, logicalTime: "" }, diagnostics: [], graphs: [], milliseconds: "1" });
    await new Promise((r) => setTimeout(r, 20));
    expect([useStore.getState().editor.preview, runtime.preview], "the old draft's preview is shown over the new one").toEqual([null, null]);
    expect(useStore.getState().editor.phase.t).not.toBe("ready");
  });
});

/** A tree source whose open() waits for a gate: a load that is still going when the user opens another source. */
class SlowTree extends TreeSource {
  constructor(private readonly gate: Promise<void>) { super(); }
  override async open(): Promise<SpaceInfo> { await this.gate; return super.open(); }
}

describe("a source replaced by another, or read again after a resync, leaves the new workspace and the author's draft alone", () => {
  it("an abandoned load does not deliver its patches into the workspace that replaced it", async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => { release = r; });
    SOURCES.push({ id: "slow-tree", label: "slow", make: () => new SlowTree(gate) }, { id: "fast-tree", label: "fast", make: () => new TreeSource() });
    dispatch({ type: "source/opening", id: "slow-tree" });
    await settle(20);
    dispatch({ type: "source/opening", id: "fast-tree" });
    await settle(200);
    const fast = runtime.ws!;
    // keep the fast source's own history from moving: it also ends with one patch, so compare with what it delivered itself
    const own = fast.history("tree")!.patches.length;
    release();                                             // the abandoned load wakes up, subscribes and its patch arrives
    await settle(300);
    expect(runtime.ws, "the workspace is still the fast one").toBe(fast);
    expect(fast.history("tree")!.patches.length, "the old source delivered a second copy of its patch into the new workspace").toBe(own);
    expect(useStore.getState().rejections.length, "and the second copy is reported as a rejection").toBe(0);
  }, 30000);

  it("a resync does not drop the author's editor session without a word", async () => {
    SOURCES.push({ id: "resync-tree", label: "resync", make: () => new TreeSource() });
    dispatch({ type: "source/opening", id: "resync-tree" });
    await settle(200);
    const session = { draft: "draft-7" } as never;
    runtime.session = session;
    dispatch({ type: "feed/resync", reason: "event gap" });
    await settle(300);
    expect(runtime.session, "the draft is orphaned on the engine, and the author is not told").toBe(session);
  }, 30000);
});
