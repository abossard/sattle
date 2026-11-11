import { beforeEach, describe, expect, it } from "vitest";
import { runtime } from "../src/state/runtime";
import { initialState, reduce } from "../src/state/reducer";
import { dispatch, useStore } from "../src/state/store";
import type { Action, State } from "../src/state/types";
import { emptyFilter } from "../src/state/types";
import { addN, ent, patch } from "./helpers";

const base = (over: Partial<State> = {}): State => ({ ...initialState(), ...over });
const sel = { graph: "master", kind: "entity" as const, id: "e1" };

describe("reducer", () => {
  it.each<[string, Action, (s: State) => unknown, unknown]>([
    ["theme", { type: "ui/theme", theme: "contrast" }, (s) => s.theme, "contrast"],
    ["view", { type: "ui/view", view: "editor" }, (s) => s.view, "editor"],
    ["dialog", { type: "ui/dialog", dialog: "shortcuts" }, (s) => s.dialog, "shortcuts"],
    ["selection", { type: "select/element", selection: sel }, (s) => s.selection, sel],
    ["selection is announced", { type: "select/element", selection: sel }, (s) => s.announcement, "Selected entity e1 in master"],
    ["clearing the selection", { type: "select/element", selection: null }, (s) => s.announcement, "Selection cleared"],
    ["a seek moves the shared time", { type: "time/seek", t: 5 }, (s) => s.time.now, 5],
    ["a rejection is kept with its time", { type: "feed/rejected", at: 7, rejection: { source: "x", where: "y", errors: ["z"] } }, (s) => s.rejections.map((r) => r.at), [7]],
    ["a patch and a telemetry batch do not change the state (the runtime holds the data)", { type: "feed/patch", graph: "master", patch: patch("0", "1", [addN(ent("a"))]) }, (s) => s === base(), false],
    ["a connection that is not live means stale data", { type: "feed/connection", status: { state: "reconnecting", detail: "x", gapsFilled: 0, reconnects: 1 } }, (s) => s.freshness, "stale"],
    ["a live connection means live data", { type: "feed/connection", status: { state: "live", detail: "x", gapsFilled: 0, reconnects: 0 } }, (s) => s.freshness, "live"],
    ["a resync is announced", { type: "feed/resync", reason: "event gap" }, (s) => s.freshness, "resyncing"],
    ["a filter change merges into the graph's filter", { type: "filter/set", graph: "master", filter: { search: "pod", hiddenTypes: ["Pod"] } }, (s) => [s.filters.master.search, s.filters.master.hiddenTypes, s.filters.master.layout], ["pod", ["Pod"], "force"]],
    ["editor selection clears an unfinished wire", { type: "editor/select", ids: ["a"] }, (s) => [s.editor.selected, s.editor.wiring], [["a"], null]],
    ["editor summary merges", { type: "editor/summary", patch: { revision: "4", notice: "n" } }, (s) => [s.editor.revision, s.editor.notice], ["4", "n"]],
  ])("%s", (_, action, pick, expected) => {
    const s0 = base({ filters: { master: emptyFilter() }, editor: { ...initialState().editor, wiring: "w" } });
    if (action.type === "feed/patch") expect(reduce(s0, action)).toBe(s0);
    else expect(pick(reduce(s0, action))).toEqual(expected);
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
    const h = ws.histories.get(graph)!;
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

  it("telemetry frames never reach the state", () => {
    const ws = runtime.ws!;
    const graph = useStore.getState().activeGraph;
    const hub = ws.hubs.get(graph);
    const s0 = useStore.getState();
    dispatch({ type: "feed/telemetry", graph, batch: { graph, tick: 1, t: 1, target: "node", property: hub?.store.metersOf("node")[0]?.id ?? "cpu", values: new Float32Array(0), ids: [] } });
    expect(useStore.getState().graphs).toBe(s0.graphs);
    expect(useStore.getState().time).toBe(s0.time);
  });

  it("selecting dispatches through the runtime and back", () => {
    const graph = useStore.getState().activeGraph;
    const id = runtime.ws!.histories.get(graph)!.graph.entities().next().value!.id;
    dispatch({ type: "select/element", selection: { graph, kind: "entity", id } });
    expect(runtime.ws!.selection).toEqual({ graph, kind: "entity", id });
    expect(useStore.getState().selection!.id).toBe(id);
  });
});
