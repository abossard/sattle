import { describe, expect, it } from "vitest";
import { countAtOrBefore, resolvePatchTimes, formatTime, frameIndexAt, mergeTimes, ordinalTimes, revisionCursorAt } from "../src/core/time-axis";
import { History } from "../src/app/history";
import { WindowHub } from "../src/app/window-hub";
import { Workspace } from "../src/app/workspace";
import { darkTheme } from "../src/render/theme";
import { addN, ent, patch, ver } from "./helpers";

describe("a revision gets its commit time, or a place by order among the windows", () => {
  const W = [100, 160, 220, 280];
  it.each([
    [[NaN, NaN, NaN], [160, 220, 280], "ordinal"],
    [[10, 20, 30], [10, 20, 30], "source"],
    [[NaN, NaN, 500], [160, 220, 500], "mixed"],      // live patch with a time after history placed by order
    [[NaN, NaN, 200], [160, 200, 200], "mixed"],      // ordinal must not pass a time the source gave
    [[], [], "ordinal"],
  ])("%j -> %j (%s)", (src, times, source) => expect(resolvePatchTimes(src as number[], W)).toEqual({ times, source }));
  it("revisions of a graph without commit times are not placed at seconds 1..n against windows in epoch seconds", () => {
    const windows = [1_800_000_000, 1_800_000_060];
    const { times } = resolvePatchTimes([NaN, NaN, NaN, NaN, NaN], windows);
    expect(Math.min(...times), "the first revision lies before every window").toBeGreaterThanOrEqual(windows[0]);
  });
});

describe("one position on the time axis picks the revision and the window frame of every graph", () => {
  it.each([[[], 5, 0], [[1, 2, 3], 0, 0], [[1, 2, 3], 2, 2], [[1, 2, 3], 2.5, 2], [[1, 2, 3], 99, 3], [[1, 1, 2], 1, 2]])("countAtOrBefore(%j, %f) = %i", (a, t, n) => expect(countAtOrBefore(a, t)).toBe(n));
  it("merges and sorts unique times from several graphs", () => expect(mergeTimes([[3, 1], [2, 3], []])).toEqual([1, 2, 3]));
  it.each([
    [0, [0, 60, 120], []],
    [3, [0, 60, 120, 180, 240], [60, 120, 180]],
    [4, [0, 60], [1, 2, 3, 4]],  // fewer windows than revisions: by order, not stacked on the last window
    [3, [], [1, 2, 3]],
  ])("ordinalTimes(%i, %j) places revisions at windows or by order", (n, windows, expected) => expect(ordinalTimes(n, windows)).toEqual(expected));
  it("a position on the axis selects revision and window frame together", () => {
    const revisions = [10, 70, 130];       // commit times
    const windows = [0, 60, 120, 180];     // window starts
    for (const [t, rev, frame] of [[0, 0, 0], [9, 0, 0], [10, 1, 0], [65, 1, 1], [70, 2, 1], [125, 2, 2], [130, 3, 2], [500, 3, 3]]) {
      expect(revisionCursorAt(revisions, t)).toBe(rev);
      expect(frameIndexAt(windows, t)).toBe(frame);
    }
  });
  it.each([[5, "5 s"], [0.5, "0.5 s"], [90, "90 s"], [185, "3 min 5 s"], [7300, "2.03 h"]])("formatTime(%f) = %s", (t, s) => expect(formatTime(t)).toBe(s));
});

// Whether the space follows now or shows a time is one value of the workspace: graphs, catalog and windows read it.
describe("while the shared timeline shows an earlier time, live patches and values wait for it and the catalog does not", () => {
  /** the workspace's own maps, filled by hand where a test builds a space without a source */
  const histories = (ws: unknown) => (ws as { histories: Map<string, unknown> }).histories;
  const hubs = (ws: unknown) => (ws as { hubs: Map<string, unknown> }).hubs;
  const space = () => {
    const ws = new Workspace({} as never, darkTheme, () => {});
    (ws as unknown as { info: unknown }).info = { graphs: [{ id: "a", role: "master" }, { id: "b", role: "master" }] };
    const a = new History({ version: ver("1", "a"), entities: [], relationships: [] });
    const b = new History({ version: ver("1", "b"), entities: [], relationships: [] });
    histories(ws).set("a", a); histories(ws).set("b", b);
    a.append(patch("1", "2", [addN(ent("x"))]), 10);
    a.append(patch("2", "3", [addN(ent("y"))]), 20);
    return { ws, a, b };
  };
  it("a graph that has nothing newer than the scrubbed time stays where the timeline says when a live patch arrives", () => {
    const { ws, a, b } = space();
    ws.seek(10, {});                       // the user scrubs back to second 10: a shows its first patch, b its snapshot
    expect(a.cursor).toBe(1);
    ws.applyPatch("b", patch("1", "2", [addN(ent("z"))]), 30);   // live: b gets a patch from second 30
    expect(b.cursor, "b jumped to now while the timeline shows second 10").toBe(ws.cursorAt("b", 10));
  });
  it("a live patch is kept for later and never applied to the graph and taken back", () => {
    const { ws, b } = space();
    ws.seek(10, {});
    let applied = 0;
    const apply = b.graph.apply.bind(b.graph);
    b.graph.apply = (p) => { applied++; return apply(p); };
    ws.applyPatch("b", patch("1", "2", [addN(ent("z"))]), 30);
    expect([applied, b.cursor, b.length], "the patch was applied and then inverted").toEqual([0, 0, 1]);
    ws.seek(30, {});
    expect(b.graph.entity("z"), "at the newest time it shows").toBeTruthy();
  });
  it("windows that arrives while the space shows a time does not move the time of the values", () => {
    const { ws, a } = space();
    const hub = new WindowHub(a.graph, { meters: [{ id: "cpu", target: "node", label: "CPU", unit: "", min: 0, max: 1 }], dt: 1, ticks: 1, maxTicks: 1, fetch: async () => ({ batches: [] }), defaults: { edge: { width: "", flow: "", state: "" }, node: { ring: "cpu", size: "cpu", state: "cpu" } } } as never);
    hubs(ws).set("a", hub);
    hub.ingest({ graph: "a", tick: 5, t: 5, target: "node", property: "cpu", values: Float32Array.of(0.5) });
    ws.seek(10, {});                       // the graphs show second 10 of 20; the values' newest window is second 5
    hub.ingest({ graph: "a", tick: 15, t: 15, target: "node", property: "cpu", values: Float32Array.of(0.9) });
    expect(hub.time, "the values followed the newest window while the graphs show second 10").toBe(10);
  });
  it("a catalog patch is applied at once: definitions are not scrubbed by data time, and later catalog patches still apply", () => {
    const ws = new Workspace({} as never, darkTheme, () => {});
    (ws as unknown as { info: unknown }).info = { graphs: [{ id: "catalog", role: "catalog" }, { id: "a", role: "master" }] };
    const cat = new History({ version: ver("1", "catalog"), entities: [], relationships: [] });
    const a = new History({ version: ver("1", "a"), entities: [], relationships: [] });
    histories(ws).set("catalog", cat); histories(ws).set("a", a);
    a.append(patch("1", "2", [addN(ent("x"))]), 10);
    a.append(patch("2", "3", [addN(ent("y"))]), 20);
    ws.seek(10, {});
    ws.applyPatch("catalog", patch("1", "2", [addN(ent("m"))]), 30);
    ws.seek(20, {});
    ws.applyPatch("catalog", patch("2", "3", [addN(ent("n"))]), 40);
    expect([cat.cursor, cat.atHead, !!cat.graph.entity("m"), !!cat.graph.entity("n")], "the catalog patch kept while scrubbed back is never applied").toEqual([2, true, true, true]);
  });
  it("values whose hub is made while the space shows a time show that time, not the newest window", () => {
    const ws = new Workspace({} as never, darkTheme, () => {});
    (ws as unknown as { info: unknown }).info = { graphs: [{ id: "a", role: "master" }] };
    const a = new History({ version: ver("1", "a"), entities: [], relationships: [] });
    histories(ws).set("a", a);
    a.append(patch("1", "2", [addN(ent("x"))]), 10);
    a.append(patch("2", "3", [addN(ent("y"))]), 20);
    ws.seek(10, {});
    const hub = ws.hubFor("a");
    hub.ingest({ graph: "a", tick: 15, t: 15, target: "node", property: "cpu", values: Float32Array.of(0.9) });
    expect(hub.time, "a hub made while scrubbed back followed the newest window").toBe(10);
  });
});
