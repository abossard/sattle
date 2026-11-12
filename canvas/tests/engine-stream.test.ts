// The stream of the engine as a calculation: events in, effects out, no socket, no fetch, no clock.
import { describe, expect, it } from "vitest";
import { initialStream, transition, type Effect, type Event, type StreamState } from "../src/source/engine-stream";
import type { TelemetryBatch } from "../src/source/types";
import { patch } from "./helpers";

const win = (t: number, v = 1): TelemetryBatch => ({ graph: "g", tick: t, t, target: "node", property: "cpu", values: Float32Array.of(v) });
const fresh = (seq = "10"): StreamState => initialStream([{ id: "g", revision: "1", telemetry: true }, { id: "h", revision: "5", telemetry: false }], seq);
/** Fold events; the effects of every step, in order. */
function run(events: Event[], from: StreamState = fresh()) {
  let state = from;
  const effects: Effect[] = [];
  for (const e of events) { const r = transition(state, e); state = r.state; effects.push(...r.effects); }
  return { state, effects };
}
const kinds = (fx: Effect[]) => fx.map((f) => f.k === "emit" ? `emit ${f.event.type}` : f.k === "status" ? `status ${f.state ?? "same"}` : f.k);
const live = (extra: Event[] = []) => run([{ t: "subscribed", graph: "g" }, { t: "patches", graph: "g", gen: 0, why: "history", patches: [] }, { t: "windows", graph: "g", gen: 0, why: "history", batches: [] }, { t: "socketOpened", resumed: false }, ...extra]);

describe("the history of a graph", () => {
  it("reads the patches after the revision it holds, then the telemetry, then says it is caught up and asks for the socket", () => {
    const r = run([{ t: "subscribed", graph: "g" }, { t: "patches", graph: "g", gen: 0, why: "history", patches: [patch("1", "2", [])] }, { t: "windows", graph: "g", gen: 0, why: "history", batches: [win(100)] }]);
    expect(kinds(r.effects)).toEqual(["readPatches", "emit patch", "readTelemetry", "emit telemetry", "emit caught-up", "openSocket"]);
    expect(r.effects[0]).toMatchObject({ after: "1" });
    expect(r.state.graphs.g).toMatchObject({ lastRevision: "2", lastWindow: 100, reading: false });
  });
  it("a graph without telemetry is caught up after its patches", () => {
    const r = run([{ t: "subscribed", graph: "h" }, { t: "patches", graph: "h", gen: 0, why: "history", patches: [] }]);
    expect(kinds(r.effects)).toEqual(["readPatches", "emit caught-up", "openSocket"]);
  });
  it("patches the graph already holds are not delivered again", () => {
    const r = run([{ t: "subscribed", graph: "g" }, { t: "patches", graph: "g", gen: 0, why: "history", patches: [patch("0", "1", []), patch("1", "2", [])] }]);
    expect(r.effects.filter((f) => f.k === "emit").length).toBe(1);
  });
  it("a history that cannot be read is reported, the sinks are told it is caught up, no socket is asked for", () => {
    const r = run([{ t: "subscribed", graph: "g" }, { t: "readFailed", graph: "g", gen: 0, why: "history", error: "HTTP 500" }]);
    expect(kinds(r.effects)).toEqual(["readPatches", "reject", "emit caught-up"]);
  });
});

describe("the generation of a graph", () => {
  // a read of the old generation is overtaken by whatever replaced the graph's state: its result yields no effects and changes nothing
  it.each<[string, Event]>([
    ["a gap in the event stream", { t: "wsGap", firstSeq: "900" }],
    ["an activation", { t: "wsActivation", catalogRevision: "11", graphs: ["g", "h"] }],
    ["dispose", { t: "dispose" }],
  ])("after %s the result of an older read delivers nothing", (_, replacing) => {
    const started = run([{ t: "subscribed", graph: "g" }]);
    const replaced = run([replacing], started.state);
    const late = transition(replaced.state, { t: "patches", graph: "g", gen: 0, why: "history", patches: [patch("1", "2", [])] });
    expect(late.effects).toEqual([]);
    expect(late.state).toBe(replaced.state);
    for (const e of [{ t: "windows", graph: "g", gen: 0, why: "history", batches: [win(1)] }, { t: "readFailed", graph: "g", gen: 0, why: "retry", error: "x" }, { t: "retry", graph: "g", gen: 0 }] as Event[]) expect(transition(replaced.state, e).effects).toEqual([]);
  });
  it("an activation starts a new generation that reads again, so its own result is delivered", () => {
    const r = run([{ t: "subscribed", graph: "g" }, { t: "wsActivation", catalogRevision: "11", graphs: ["g"] }]);
    const again = r.effects.find((f) => f.k === "readPatches" && f.why === "activation");
    expect(again).toMatchObject({ gen: 1 });
    expect(kinds(r.effects)).toEqual(["readPatches", "emit telemetry-reset", "readMeta", "readPatches"]);
  });
  it("an activation that brings a graph the space did not have asks every graph to resync", () => {
    const r = run([{ t: "subscribed", graph: "g" }, { t: "wsActivation", catalogRevision: "11", graphs: ["g", "new"] }]);
    expect(r.effects.filter((f) => f.k === "emit" && f.event.type === "resync").length).toBe(1);
  });
  it("after dispose nothing happens", () => {
    const r = run([{ t: "dispose" }, { t: "wsPatch", graph: "g", patch: patch("1", "2", []) }, { t: "socketOpened", resumed: true }, { t: "subscribed", graph: "g" }]);
    expect(kinds(r.effects)).toEqual(["closeSocket", "status closed"]);
  });
});

describe("patches of the stream", () => {
  it("one that continues the log is delivered with the logical time", () => {
    const r = live([{ t: "wsTime", logicalTime: 1800 }, { t: "wsPatch", graph: "g", patch: patch("1", "2", []) }]);
    expect(r.effects.at(-1)).toMatchObject({ k: "emit", event: { type: "patch", time: 1800 } });
  });
  it("one the history already delivered is dropped", () => {
    expect(live([{ t: "wsPatch", graph: "g", patch: patch("0", "1", []) }]).effects.filter((f) => f.k === "emit" && f.event.type === "patch")).toEqual([]);
  });
  it("one that skipped a revision is held, the missing ones are read, then both are delivered in order", () => {
    const held = live([{ t: "wsPatch", graph: "g", patch: patch("2", "3", []) }]);
    expect(held.effects.at(-1)).toMatchObject({ k: "readPatches", why: "gap-in-stream", after: "1" });
    expect(held.state.graphs.g.parked).not.toBeNull();
    const filled = run([{ t: "patches", graph: "g", gen: 0, why: "gap-in-stream", patches: [patch("1", "2", [])] }, { t: "windows", graph: "g", gen: 0, why: "gap-in-stream", batches: [] }], held.state);
    expect(filled.effects.filter((f) => f.k === "emit").map((f) => (f as { event: { patch: { to: { revision: string } } } }).event.patch.to.revision)).toEqual(["2", "3"]);
    expect(filled.state.graphs.g).toMatchObject({ lastRevision: "3", parked: null });
  });
  it("one the read that fills the hole brought itself is not delivered twice", () => {
    const held = live([{ t: "wsPatch", graph: "g", patch: patch("2", "3", []) }]);
    const filled = run([{ t: "patches", graph: "g", gen: 0, why: "gap-in-stream", patches: [patch("1", "2", []), patch("2", "3", [])] }], held.state);
    expect(filled.effects.filter((f) => f.k === "emit").length).toBe(2);
  });
});

describe("telemetry of the stream and the sequence number of the history read", () => {
  // the read was asked at seq 10 (the highest the client knew): it reflects at least every event up to 10
  const asked = () => live();
  it.each<[string, string | undefined, number, boolean]>([
    ["an event at the read's own number is in what the read delivered", "10", 100, false],
    ["an older one is too", "7", 100, false],
    ["a newer one is delivered", "11", 100, true],
    ["a revised window (older than the newest, newer than the read) is delivered", "12", 40, true],
    ["an event without a sequence number cannot be proven included: it is delivered", undefined, 100, true],
  ])("%s", (_, seq, t, delivered) => {
    const r = run([{ t: "wsTelemetry", graph: "g", batch: win(t, 9), seq }], run([{ t: "windows", graph: "g", gen: 0, why: "history", batches: [win(100)] }], asked().state).state);
    expect(r.effects.filter((f) => f.k === "emit").length).toBe(delivered ? 1 : 0);
  });
  it("the read is asked at the highest sequence number known when it is issued", () => {
    const r = run([{ t: "seq", seq: "42" }, { t: "subscribed", graph: "g" }, { t: "patches", graph: "g", gen: 0, why: "history", patches: [] }]);
    expect(r.state.graphs.g.historySeq).toBe("42");
  });
  it("sequence numbers are compared as numbers of any size", () => {
    const big = "9007199254740993", bigger = "9007199254740994";
    const r = run([{ t: "seq", seq: big }, { t: "subscribed", graph: "g" }, { t: "patches", graph: "g", gen: 0, why: "history", patches: [] }, { t: "wsTelemetry", graph: "g", batch: win(5), seq: bigger }]);
    expect(r.effects.at(-1)).toMatchObject({ k: "emit" });
  });
});

describe("a catch-up that fails", () => {
  it("says the connection is not live, retries, and says live again when a catch-up works", () => {
    const failed = live([{ t: "wsPatch", graph: "g", patch: patch("2", "3", []) }, { t: "readFailed", graph: "g", gen: 0, why: "gap-in-stream", error: "HTTP 500" }]);
    expect(kinds(failed.effects).slice(-3)).toEqual(["reject", "status reconnecting", "retry"]);
    expect(failed.state.graphs.g.failures).toBe(1);
    const again = run([{ t: "retry", graph: "g", gen: 0 }, { t: "patches", graph: "g", gen: 0, why: "retry", patches: [patch("1", "2", [])] }, { t: "windows", graph: "g", gen: 0, why: "retry", batches: [] }], failed.state);
    expect(kinds(again.effects)).toEqual(["readPatches", "emit patch", "emit patch", "status same", "readTelemetry", "status live"]);
    expect(again.state.graphs.g.failures).toBe(0);
  });
  it("the backoff attempt grows with the failures", () => {
    const a = live([{ t: "readFailed", graph: "g", gen: 0, why: "retry", error: "x" }]);
    const b = run([{ t: "retry", graph: "g", gen: 0 }, { t: "readFailed", graph: "g", gen: 0, why: "retry", error: "x" }], a.state);
    expect(b.effects.filter((f) => f.k === "retry")).toEqual([{ k: "retry", graph: "g", gen: 0, attempt: 2 }]);
  });
});

describe("the socket", () => {
  it("opened again after a drop reads what was missed for every graph that is followed", () => {
    const r = run([{ t: "socketOpened", resumed: true }], live().state);
    expect(r.effects.filter((f) => f.k === "readPatches").map((f) => (f as { graph: string }).graph)).toEqual(["g"]);
    expect(r.effects[0]).toMatchObject({ k: "status", state: "live", detail: "reconnected" });
  });
  it("opened while a graph is behind says it is catching up, not live", () => {
    const r = run([{ t: "socketOpened", resumed: false }], live([{ t: "readFailed", graph: "g", gen: 0, why: "retry", error: "x" }]).state);
    expect(r.effects[0]).toMatchObject({ state: "reconnecting" });
  });
  it.each<[string, Event, string[]]>([
    ["end", { t: "wsEnd" }, ["status ended", "emit end"]],
    ["a gap", { t: "wsGap", firstSeq: "9" }, ["reject", "emit resync"]],
    ["a slow consumer", { t: "wsError", code: "SlowConsumer", message: "" }, ["reject", "closeSocket"]],
    ["ahead of the head", { t: "wsError", code: "AheadOfHead", message: "" }, ["reject", "emit resync"]],
  ])("%s", (_, e, want) => expect(kinds(run([e], live().state).effects.slice(-want.length))).toEqual(want));
  it("a gap forgets the sequence number: the next subscription asks for snapshots", () => expect(run([{ t: "wsGap", firstSeq: "9" }], live().state).state.seq).toBeUndefined());
});
