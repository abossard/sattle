// Theorem 12.1 end to end: a replica that follows a real engine holds the engine's state at every version. The source reads the first state
// of every graph of the space and follows its feed; the replica applies each patch with the guard of Def. 3.7 (Graph.apply) and is compared,
// after every patch, with the state the engine serves for that revision. Nothing is faked: the engine of tests/engine/global-setup.ts serves
// the estate, the reads are HTTP and the feed is its socket.
import { describe, expect, it } from "vitest";
import { Graph } from "../../src/core/graph";
import type { Patch, Snapshot } from "../../src/core/model";
import type { Rejection } from "../../src/core/validate";
import { EngineSource } from "../../src/source/engine";
import { ENGINE_ENDPOINTS } from "../../src/source/engine-protocol";
import { parseSnapshot } from "../../src/source/snapshot-file";

const ENGINE = (globalThis as unknown as { process: { env: Record<string, string | undefined> } }).process.env.ENGINE!;

/** A state without the order its elements arrived in: elements by id, the properties of each by name. */
const sorted = (v: unknown): unknown => Array.isArray(v) ? v.map(sorted) : v && typeof v === "object" ? Object.fromEntries(Object.entries(v).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)).map(([k, x]) => [k, sorted(x)])) : v;
const byId = <T extends { id: string }>(xs: T[]) => [...xs].sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0)).map(sorted);
const stateOf = (s: Snapshot) => ({ revision: s.version.revision, entities: byId(s.entities), relationships: byId(s.relationships) });

/** The patches of a graph in the order the source delivers them, up to the head it had when the history was read. */
const historyOf = (src: EngineSource, graph: string): Promise<Patch[]> => new Promise((resolve, reject) => {
  const out: Patch[] = [];
  src.subscribe(graph, (e) => {
    if (e.type === "patch") out.push(e.patch);
    else if (e.type === "caught-up") resolve(out);
    else if (e.type === "resync" || e.type === "rejected") reject(new Error(`${graph}: ${JSON.stringify(e)}`));
  });
});

/** The state the engine serves for one revision of a graph. */
async function served(graph: string, revision: string): Promise<Snapshot> {
  const path = ENGINE_ENDPOINTS.snapshot(graph, revision), res = await fetch(ENGINE + path);
  if (!res.ok) throw new Error(`${path}: HTTP ${res.status}`);
  const parsed = parseSnapshot(await res.json(), path, (r) => { throw new Error(`${path}: ${r.errors.join("; ")}`); });
  return parsed!.snapshot;
}

describe("a replica that follows the engine holds the engine's state at every version (Theorem 12.1)", () => {
  it("every graph of the estate: each patch passes the guard, and the replica equals the served state of that revision, up to the head", async () => {
    const src = new EngineSource(ENGINE);
    const rejected: Rejection[] = [];
    src.onRejected((r) => rejected.push(r));
    try {
      const info = await src.open();
      const heads = new Map(src.space!.graphs.map((g) => [g.id, g.head]));
      let compared = 0;
      for (const g of info.graphs) {
        const replica = Graph.fromSnapshot(await src.snapshot(g.id));
        expect(stateOf(replica.toSnapshot()), `${g.id} at its first state`).toEqual(stateOf(await served(g.id, replica.revision!)));
        for (const p of await historyOf(src, g.id)) {
          expect(replica.apply(p), `${g.id} ${p.from.revision} to ${p.to.revision}`).toMatchObject({ ok: true });
          expect(stateOf(replica.toSnapshot()), `${g.id} at ${p.to.revision}`).toEqual(stateOf(await served(g.id, p.to.revision)));
          compared++;
        }
        // the replica is not behind: it stands at the head the engine named, or past it when the engine has moved on since
        expect(Number(replica.revision), `${g.id} reaches the head`).toBeGreaterThanOrEqual(Number(heads.get(g.id)));
      }
      // the estate moves: a run that compared nothing would prove nothing
      expect(info.graphs.length).toBeGreaterThan(5);
      expect(compared).toBeGreaterThan(info.graphs.length);
      // the one record the viewer refuses is the kernel's ontology (its value kind "any"), reported and not hidden
      expect(rejected.map((r) => r.where).filter((w) => !w.startsWith("ontology of "))).toEqual([]);
    } finally { src.dispose(); }
  }, 300000);
});
