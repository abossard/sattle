// Defects of the code review in the sources and the effects (section 3): each test says what should be true and fails today for the reason the review gives.
import { describe, expect, it } from "vitest";
import { EngineSource } from "../../src/source/engine";
import { SimulatedStream } from "../../src/source/simulated";
import { StaticSource } from "../../src/source/static";
import { SOURCES } from "../../src/state/sources";
import { runtime } from "../../src/state/runtime";
import { dispatch, useStore } from "../../src/state/store";
import type { SpaceInfo, StreamEvent } from "../../src/source/types";
import { BASE, FakeSocket, baseRoutes, collect, makeFetch, ndjson, settle, tel, wirePatch } from "../fixtures/engine-fake";
import { TreeSource } from "../fixtures/tree-source";

const open = async (extra: Record<string, () => { status?: number; body: unknown }> = {}) => {
  FakeSocket.instances = [];
  const log: string[] = [];
  const routes = baseRoutes({
    "GET /graphs/master/patches?after=0&to=3": () => ({ body: ndjson([wirePatch(0, 1)]) }),
    "GET /graphs/master/telemetry?format=batches": () => ({ body: "" }),
    ...extra,
  });
  const src = new EngineSource(BASE, { fetch: makeFetch(routes, log), WebSocket: FakeSocket as never, backoff: [1] });
  await src.open();
  const sink = collect(src, "master");
  await settle();
  return { src, ws: FakeSocket.instances[0], events: sink.events, log };
};

describe("source/engine.ts:244 a telemetry batch older than the newest window", () => {
  it("a revised window (the protocol pushes it again with the new values) reaches the sinks", async () => {
    const { src, ws, events } = await open();
    ws.push({ type: "telemetry", graph: "master", batch: tel("2026-11-10T10:42:00Z", 2) });
    ws.push({ type: "telemetry", graph: "master", batch: tel("2026-11-10T10:41:00Z", 9) }); // window 10:41 revised after 10:42 was seen
    await settle();
    const values = events.flatMap((e) => (e.type === "telemetry" ? [e.batch.values[0]] : []));
    expect(values, "the user keeps the old value of 10:41 until a reload").toEqual([2, 9]);
    src.dispose();
  });
});

describe("source/engine.ts:233-237 a failed gap fill", () => {
  it("does not leave the graph frozen while the status says live", async () => {
    const { src, ws } = await open({ "GET /graphs/master/patches?after=1": () => ({ status: 500, body: { error: { code: "Internal" } } }) });
    ws.push({ type: "patch", graph: "master", patch: wirePatch(2, 3) });  // revision 2 was skipped: the fill asks for it and fails
    await settle(40);
    expect(src.connection.status.state, "nothing is delivered any more, the status still says live").not.toBe("live");
    src.dispose();
  });
});

describe("source/engine.ts:251-257 an activation", () => {
  it("makes the source read the ontology and the value roles again", async () => {
    const { src, ws, log } = await open({ "GET /graphs/master/patches?after=1": () => ({ body: "" }) });
    const before = log.filter((l) => l.includes("/graphs/master/ontology")).length;
    ws.push({ type: "activation", catalogRevision: "11", graphs: [{ id: "master" }] });
    await settle(40);
    expect(log.filter((l) => l.includes("/graphs/master/ontology")).length, "a definition edit can change the ontology and its roles").toBeGreaterThan(before);
    src.dispose();
  });
});

describe("source/simulated.ts, synthetic.ts no dispose", () => {
  it("a simulated stream stops its timer when the workspace is disposed", async () => {
    const sim = new SimulatedStream(new StaticSource("t", "http://localhost/x/manifest.json", async () => "{}"));
    expect(typeof (sim as unknown as { dispose?: unknown }).dispose, "the interval of a playing stream leaks on every reset").toBe("function");
  });
});

/** A tree source whose open() waits for a gate: a load that is still going when the user opens another source. */
class SlowTree extends TreeSource {
  constructor(private readonly gate: Promise<void>) { super(); }
  override async open(): Promise<SpaceInfo> { await this.gate; return super.open(); }
}

describe("state/effects.ts:75 reloads and abandoned loads", () => {
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
    const own = fast.histories.get("tree")!.patches.length;
    release();                                             // the abandoned load wakes up, subscribes and its patch arrives
    await settle(300);
    expect(runtime.ws, "the workspace is still the fast one").toBe(fast);
    expect(fast.histories.get("tree")!.patches.length, "the old source delivered a second copy of its patch into the new workspace").toBe(own);
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

void ({} as StreamEvent);
