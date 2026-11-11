// An engine in memory for the editor tests: a draft that applies patches with guards and answers like the real one.
import { Graph } from "../../src/core/graph";
import type { Edit, Patch, Snapshot } from "../../src/core/model";
import { ent, text } from "../../src/editor/patches";
import { EngineError, type Applied, type EditorService } from "../../src/editor/service";
import { parsePalette } from "../../src/editor/palette";

/** An engine in memory: a draft that applies patches with guards and answers like the real one. */
/** `seed`: the entities and relationships the catalog starts with (a module with a projection and an operator, say). */
export function fakeEngine(seed: { entities?: Snapshot["entities"]; relationships?: Snapshot["relationships"] } = {}) {
  const base: Snapshot = { version: { tenant: "t", graph: "catalog", revision: "0" }, entities: seed.entities ?? [], relationships: seed.relationships ?? [] };
  const calls = { applyPatch: 0, checkText: 0 };
  const g = Graph.fromSnapshot(base);
  let rev = 0;
  const log: Patch[] = [];
  const svc = {
    palette: async () => parsePalette({ operators: [] }),
    createDraft: async () => ({ draft: "d1", baseRevision: "11", revision: "0" }),
    snapshot: async () => ({ ...g.toSnapshot(), version: { ...base.version, revision: String(rev) } }),
    check: async (): Promise<Applied> => ({ revision: String(rev), ok: true, diagnostics: [], modules: { m: { text: `rev ${rev}` } } }),
    checkText: async () => { calls.checkText++; return { ok: false, patch: null, diagnostics: [], affectedModules: [] }; },
    applyPatch: async (_: string, p: Patch, expected: string): Promise<Applied> => {
      calls.applyPatch++;
      if (expected !== String(rev)) throw new EngineError("StaleRevision", "stale", 409);
      const r = g.apply(p);
      if (!r.ok) throw new EngineError("GuardFailed", "guard", 409, r.conflicts[0].key);
      rev++; log.push(p);
      return { revision: String(rev), ok: true, diagnostics: [], modules: { m: { text: `rev ${rev}` } } };
    },
  } as unknown as EditorService;
  return { svc, g, calls, get rev() { return rev; } };
}
export const add = (n: number): Edit[] => [ent(`d:m/entity:E${n}`, "Entity", { name: text(`E${n}`) })];
export const names = (g: Graph) => [...g.entities()].map((e) => e.id.slice(e.id.lastIndexOf("E") + 1)).sort((a, b) => Number(a) - Number(b));

