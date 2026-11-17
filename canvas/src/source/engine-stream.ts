// The event stream of the engine as a calculation: `transition(state, event) -> { state, effects }`, per graph, with no socket, no fetch and no clock.
// Events are what happened (a socket message, the result of a read, a retry that came due, a resync, a dispose). Effects are data: what to read,
// what to tell the sinks, what the connection status is. `EngineSource` is the thin shell that turns messages and reads into events and runs the effects.
//
// The generation rule: every read an effect asks for carries the generation of its graph; a graph's generation is bumped when its state is replaced
// (a gap, an activation) and by dispose. The result of a read of an older generation yields no effects, so a step that was overtaken delivers nothing.
import type { Patch } from "../core/model";
import type { ConnectionStatus, GraphMeta, StreamEvent, TelemetryBatch } from "./types";

const num = (r: string): number => Number(r);
/** Sequence numbers are decimal strings that can outgrow a double. */
const seqLeq = (a: string, b: string): boolean => BigInt(a) <= BigInt(b);

export interface GraphStream {
  /** bumped when the state of the graph is replaced; results of reads of an older one are ignored */
  gen: number;
  lastRevision: string | undefined;
  /** start of the newest telemetry window delivered (a read starts there, inclusive: a window may have been revised) */
  lastWindow: number | undefined;
  /** the catalog has no telemetry and the engine answers 404 for it */
  telemetry: boolean;
  /** a sink follows the graph: only those are read again after a reconnect, an activation or a gap */
  subscribed: boolean;
  /** a read of patches or telemetry is in flight: one at a time per graph */
  reading: boolean;
  /** a patch of the stream that skipped a revision, with its commit time, held until the read that fills the hole is back */
  parked: { patch: Patch; time: number | undefined } | null;
  /** failed catch-ups in a row; the graph is behind while this is not 0 */
  failures: number;
  /**
   * The sequence number the newest telemetry read was asked at: the highest the client knew when it issued the read. The read reflects at least
   * every event up to it, so a telemetry event with `seq <= historySeq` is already in what the read delivered. An event without `seq` is delivered
   * (nothing proves it was included); later events follow in order, so the last value of a window is still the engine's.
   */
  historySeq: string | undefined;
}

export interface StreamState {
  graphs: Record<string, GraphStream>;
  /** the ids of the graphs the space had when it was opened */
  space: string[];
  /** server-wide sequence number of the newest event seen: where a reconnect resumes */
  seq: string | undefined;
  /** the engine's logical time (epoch seconds) from the newest `time` message: the commit time of a live patch from an engine before 3.54, whose patch events carry none */
  logicalTime: number;
  /** where the socket is: one value, so a late answer cannot bring back a socket the engine ended or the view closed */
  link: Link;
}

/**
 * The socket's life. `none`: the history is still read. `connecting`: asked for, `attempt` 0 the first time, n after n drops in a row.
 * `waiting`: dropped, opened again when its backoff is over. `ended`: the engine ended the stream, `unavailable`: no WebSocket here (both:
 * reads that are in flight still deliver, nothing reconnects, retries or speaks for the connection). `closed`: the view closed it; nothing counts.
 */
export type Link = { k: "none" } | { k: "connecting"; attempt: number } | { k: "open" } | { k: "waiting"; attempt: number } | { k: "ended" } | { k: "unavailable" } | { k: "closed" };

export type Why = "history" | "reconnect" | "retry" | "activation" | "gap-in-stream";

export type Event =
  | { t: "subscribed"; graph: string }
  /** `times`: the commit time of each revision the read knows one for (engine 3.54) */
  | { t: "patches"; graph: string; gen: number; why: Why; patches: Patch[]; times?: Record<string, number> }
  | { t: "windows"; graph: string; gen: number; why: Why; batches: TelemetryBatch[] }
  | { t: "readFailed"; graph: string; gen: number; why: Why; error: string }
  | { t: "meta"; graph: string; gen: number; meta: GraphMeta }
  | { t: "retry"; graph: string; gen: number }
  | { t: "socketOpened" }
  /** the socket closed or could not be made (`why`: the error that kept it from being made) */
  | { t: "socketClosed"; why?: string }
  /** the backoff after a drop is over */
  | { t: "reconnectDue" }
  /** this environment has no WebSocket */
  | { t: "socketUnavailable" }
  | { t: "wsPatch"; graph: string; patch: Patch; seq?: string; time?: number }
  | { t: "wsTelemetry"; graph: string; batch: TelemetryBatch; seq?: string }
  | { t: "wsTime"; logicalTime: number }
  | { t: "wsActivation"; catalogRevision: string; graphs: string[]; seq?: string }
  | { t: "wsGap"; firstSeq: string }
  | { t: "wsError"; code: string; message: string }
  | { t: "wsEnd"; seq?: string }
  | { t: "seq"; seq: string }
  | { t: "dispose" };

export type Effect =
  | { k: "emit"; graph: string; event: StreamEvent }
  | { k: "readPatches"; graph: string; gen: number; why: Why; after: string }
  | { k: "readTelemetry"; graph: string; gen: number; why: Why; from: number | undefined }
  | { k: "readMeta"; graph: string; gen: number }
  /** `state` absent: keep the state the connection is in; `gapFilled`: one more gap was filled */
  | { k: "status"; state?: ConnectionStatus["state"]; detail: string; gapFilled?: boolean }
  | { k: "reject"; where: string; errors: string[] }
  /** try the read again after the `attempt`-th backoff delay */
  | { k: "retry"; graph: string; gen: number; attempt: number }
  | { k: "openSocket" }
  | { k: "closeSocket" }
  /** open the socket again after the `attempt`-th backoff delay (the same delays as a read's retry) */
  | { k: "reconnect"; attempt: number };

export const initialStream = (graphs: { id: string; revision: string | undefined; telemetry: boolean }[], seq: string | undefined): StreamState => ({
  graphs: Object.fromEntries(graphs.map((g) => [g.id, { gen: 0, lastRevision: g.revision, lastWindow: undefined, telemetry: g.telemetry, subscribed: false, reading: false, parked: null, failures: 0, historySeq: undefined }])),
  space: graphs.map((g) => g.id), seq, logicalTime: NaN, link: { k: "none" },
});

type Out = { state: StreamState; effects: Effect[] };
const out = (state: StreamState, ...effects: Effect[]): Out => ({ state, effects });
const withGraph = (s: StreamState, id: string, g: GraphStream): StreamState => ({ ...s, graphs: { ...s.graphs, [id]: g } });

/** Deliver a patch with its commit time: the revision moves to its end and the sinks get it. */
function deliver(graph: string, g: GraphStream, patch: Patch, time: number | undefined): { g: GraphStream; effect: Effect } {
  return { g: { ...g, lastRevision: patch.to.revision }, effect: { k: "emit", graph, event: { type: "patch", patch, time } } };
}
/** The commit time of a live patch: its own, else (an engine before 3.54) the newest logical time. */
const liveTime = (s: StreamState, time: number | undefined): number | undefined => time ?? (Number.isFinite(s.logicalTime) ? s.logicalTime : undefined);

/** The reads of a graph start: the patches after the last revision, then the telemetry (the continuation is in the patches' result). */
function startRead(s: StreamState, graph: string, why: Why): Out {
  const g = s.graphs[graph];
  if (!g || g.reading || s.link.k === "closed") return out(s);
  return out(withGraph(s, graph, { ...g, reading: true }), { k: "readPatches", graph, gen: g.gen, why, after: g.lastRevision ?? "0" });
}

/** The graph's state is replaced: the generation moves on and what was in flight or held is dropped. */
const replaced = (g: GraphStream): GraphStream => ({ ...g, gen: g.gen + 1, reading: false, parked: null, failures: 0 });

/** After the end (or without a socket) what a read brings is still delivered, but nothing speaks for the connection, retries or reconnects. */
const QUIET: ReadonlySet<Effect["k"]> = new Set(["status", "retry", "openSocket", "reconnect"]);

export function transition(s: StreamState, e: Event): Out {
  const r = step(s, e);
  const quiet = s.link.k === "ended" || s.link.k === "unavailable";
  return quiet ? { state: r.state, effects: r.effects.filter((f) => !QUIET.has(f.k)) } : r;
}

/** The socket is wanted (a graph's history is in): asked for once. */
function connect(s: StreamState): Out {
  return s.link.k === "none" ? out({ ...s, link: { k: "connecting", attempt: 0 } }, { k: "status", state: "connecting", detail: "connecting" }, { k: "openSocket" }) : out(s);
}

function step(s: StreamState, e: Event): Out {
  if (e.t === "dispose") {
    if (s.link.k === "closed") return out(s);
    const graphs = Object.fromEntries(Object.entries(s.graphs).map(([id, g]) => [id, replaced(g)]));
    return out({ ...s, graphs, link: { k: "closed" } }, { k: "closeSocket" }, { k: "status", state: "closed", detail: "closed" });
  }
  if (s.link.k === "closed") return out(s);
  const all = Object.keys(s.graphs).filter((id) => s.graphs[id].subscribed);
  switch (e.t) {
    case "seq": return out(s.seq === undefined || BigInt(e.seq) > BigInt(s.seq) ? { ...s, seq: e.seq } : s);
    case "subscribed": { const g = s.graphs[e.graph]; return g ? startRead(withGraph(s, e.graph, { ...g, subscribed: true }), e.graph, "history") : out(s); }
    case "wsTime": return out({ ...s, logicalTime: e.logicalTime });
    case "socketOpened": {
      // a socket that opens when none is wanted any more (the stream ended meanwhile) is closed again
      if (s.link.k !== "connecting") return out(s, { k: "closeSocket" });
      const resumed = s.link.attempt > 0;
      const behind = Object.values(s.graphs).some((g) => g.failures > 0);
      const effects: Effect[] = [{ k: "status", state: behind ? "reconnecting" : "live", detail: behind ? "connected, catching up" : resumed ? "reconnected" : "connected" }];
      let next: StreamState = { ...s, link: { k: "open" } };
      // after a reconnect the stream resumes after the last sequence number, and what it skipped is read over HTTP
      if (resumed) for (const id of all) { const r = startRead(next, id, "reconnect"); next = r.state; effects.push(...r.effects); }
      return { state: next, effects };
    }
    case "socketClosed": {
      if (s.link.k !== "open" && s.link.k !== "connecting") return out(s);
      const attempt = (s.link.k === "connecting" ? s.link.attempt : 0) + 1;
      return out({ ...s, link: { k: "waiting", attempt } }, { k: "status", state: "reconnecting", detail: `${e.why ?? "connection closed"}; reconnecting (attempt ${attempt})` }, { k: "reconnect", attempt });
    }
    case "reconnectDue": return s.link.k === "waiting" ? out({ ...s, link: { k: "connecting", attempt: s.link.attempt } }, { k: "status", state: "reconnecting", detail: `reconnecting, attempt ${s.link.attempt}` }, { k: "openSocket" }) : out(s);
    case "socketUnavailable": return out({ ...s, link: { k: "unavailable" } }, { k: "status", state: "closed", detail: "no WebSocket in this environment; history only" });
    case "wsEnd": if (s.link.k === "ended") return out(s);
      return out({ ...s, link: { k: "ended" } }, { k: "closeSocket" }, { k: "status", state: "ended", detail: "the engine ended the stream" }, ...all.map((graph): Effect => ({ k: "emit", graph, event: { type: "end" } })));
    case "wsError": {
      const effects: Effect[] = [{ k: "reject", where: e.code, errors: [e.message || e.code] }];
      if (e.code === "SlowConsumer") effects.push({ k: "closeSocket" }); // the reconnect resumes with `after`
      if (e.code === "AheadOfHead") for (const graph of all) effects.push({ k: "emit", graph, event: { type: "resync", reason: "this client is ahead of the engine" } });
      return { state: s, effects };
    }
    case "wsGap": {
      // the engine's retained events no longer reach back: every graph is read again from a snapshot, nothing in flight counts any more
      const graphs = Object.fromEntries(Object.entries(s.graphs).map(([id, g]) => [id, replaced(g)]));
      return { state: { ...s, graphs, seq: undefined }, effects: [{ k: "reject", where: "event stream", errors: [`the engine's retained events start at ${e.firstSeq}; resynchronizing from a snapshot`] }, ...all.map((graph): Effect => ({ k: "emit", graph, event: { type: "resync", reason: "event gap" } }))] };
    }
    case "wsActivation": {
      // a graph the space did not have before (a committed projection brought it): the view reloads to show it
      if (e.graphs.some((id) => !s.space.includes(id))) return out(s, ...all.map((graph): Effect => ({ k: "emit", graph, event: { type: "resync", reason: "the space has a new graph" } })));
      // a definition edit re-activated the space: stored telemetry may differ, the ontology and the roles of the values may too, and all are read again
      let next = s;
      const effects: Effect[] = [];
      for (const id of all) {
        next = withGraph(next, id, { ...replaced(next.graphs[id]), lastWindow: undefined });
        effects.push({ k: "emit", graph: id, event: { type: "telemetry-reset" } }, { k: "readMeta", graph: id, gen: next.graphs[id].gen });
        const r = startRead(next, id, "activation");
        next = r.state; effects.push(...r.effects);
      }
      return { state: next, effects };
    }
    case "wsPatch": {
      const g = s.graphs[e.graph];
      if (!g) return out(s);
      const last = g.lastRevision;
      if (last !== undefined && num(e.patch.to.revision) <= num(last)) return out(s); // already delivered by the history
      if (last !== undefined && e.patch.from.revision !== last) {
        // a revision was skipped: hold the patch, read what is missing; the read's result releases it
        const held = withGraph(s, e.graph, { ...g, parked: { patch: e.patch, time: liveTime(s, e.time) } });
        return g.reading ? out(held) : startRead(held, e.graph, "gap-in-stream");
      }
      const d = deliver(e.graph, g, e.patch, liveTime(s, e.time));
      return out(withGraph(s, e.graph, d.g), d.effect);
    }
    case "wsTelemetry": {
      const g = s.graphs[e.graph];
      if (!g) return out(s);
      // what the telemetry read already holds is not delivered again (an older value of a window must not replace a newer one); a revised window is not "older"
      if (e.seq !== undefined && g.historySeq !== undefined && seqLeq(e.seq, g.historySeq)) return out(s);
      return out(withGraph(s, e.graph, { ...g, lastWindow: Math.max(g.lastWindow ?? -Infinity, e.batch.t) }), { k: "emit", graph: e.graph, event: { type: "telemetry", batch: e.batch } });
    }
    case "meta": {
      const g = s.graphs[e.graph];
      return g && g.gen === e.gen ? out(s, { k: "emit", graph: e.graph, event: { type: "meta", meta: e.meta } }) : out(s);
    }
    case "patches": {
      const g = s.graphs[e.graph];
      if (!g || g.gen !== e.gen) return out(s);
      let cur = g;
      const effects: Effect[] = [];
      let delivered = 0;
      for (const p of e.patches) {
        if (cur.lastRevision !== undefined && num(p.to.revision) <= num(cur.lastRevision)) continue;
        const d = deliver(e.graph, cur, p, e.times?.[p.to.revision]);
        cur = d.g; effects.push(d.effect); delivered++;
      }
      // the stream's own patch that was waiting for this read: delivered unless the read brought it, and then it follows in order even if a hole is left
      if (cur.parked) {
        const { patch: p, time } = cur.parked;
        cur = { ...cur, parked: null };
        if (cur.lastRevision === undefined || num(p.to.revision) > num(cur.lastRevision)) { const d = deliver(e.graph, cur, p, time); cur = d.g; effects.push(d.effect); }
      }
      if (delivered && e.why !== "history") effects.push({ k: "status", detail: `${e.why}: filled ${delivered} revision(s)`, gapFilled: true });
      if (cur.telemetry) {
        const asked = { ...cur, historySeq: s.seq };
        effects.push({ k: "readTelemetry", graph: e.graph, gen: cur.gen, why: e.why, from: cur.lastWindow });
        return { state: withGraph(s, e.graph, asked), effects };
      }
      return finishRead(withGraph(s, e.graph, cur), e.graph, e.why, effects);
    }
    case "windows": {
      const g = s.graphs[e.graph];
      if (!g || g.gen !== e.gen) return out(s);
      const effects: Effect[] = e.batches.map((batch): Effect => ({ k: "emit", graph: e.graph, event: { type: "telemetry", batch } }));
      const lastWindow = e.batches.reduce((m, b) => Math.max(m, b.t), g.lastWindow ?? -Infinity);
      return finishRead(withGraph(s, e.graph, { ...g, lastWindow: Number.isFinite(lastWindow) ? lastWindow : undefined }), e.graph, e.why, effects);
    }
    case "readFailed": {
      const g = s.graphs[e.graph];
      if (!g || g.gen !== e.gen) return out(s);
      const effects: Effect[] = [{ k: "reject", where: e.why === "history" ? `history of ${e.graph}` : `catch-up of ${e.graph}`, errors: [e.error] }];
      if (e.why === "history") {
        // the history could not be read: the sinks are told it is as far as it goes (no socket is opened for it)
        return { state: withGraph(s, e.graph, { ...g, reading: false }), effects: [...effects, { k: "emit", graph: e.graph, event: { type: "caught-up" } }] };
      }
      // nothing arrives for this graph until a catch-up works: the status says so, and the read is tried again
      const failures = g.failures + 1;
      return { state: withGraph(s, e.graph, { ...g, reading: false, failures }), effects: [...effects, { k: "status", state: "reconnecting", detail: `catching up ${e.graph} failed (attempt ${failures}); retrying` }, { k: "retry", graph: e.graph, gen: g.gen, attempt: failures }] };
    }
    case "retry": {
      const g = s.graphs[e.graph];
      return g && g.gen === e.gen ? startRead(s, e.graph, "retry") : out(s);
    }
  }
}

/** The patches and the telemetry of a read are in: the history is done (sinks are caught up, the socket follows), or a catch-up worked (the graph is not behind any more). */
function finishRead(s: StreamState, graph: string, why: Why, effects: Effect[]): Out {
  const g = s.graphs[graph];
  const done = withGraph(s, graph, { ...g, reading: false, failures: 0 });
  if (why === "history") { const c = connect(done); return { state: c.state, effects: [...effects, { k: "emit", graph, event: { type: "caught-up" } }, ...c.effects] }; }
  const wasBehind = g.failures > 0;
  const stillBehind = Object.entries(done.graphs).some(([, x]) => x.failures > 0);
  if (wasBehind && !stillBehind && s.link.k === "open") effects.push({ k: "status", state: "live", detail: "caught up" });
  return { state: done, effects };
}
