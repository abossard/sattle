import { describe, expect, it } from "vitest";
import { Graph } from "../src/core/graph";
import type { Edit, Patch, Snapshot } from "../src/core/model";
import { MAX_UNDO, EditorSession } from "../src/editor/session";
import { ent, text } from "../src/editor/patches";
import { EngineError, type Applied, type EditorService } from "../src/editor/service";
import { parsePalette } from "../src/editor/palette";

/** An engine in memory: a draft that applies patches with guards and answers like the real one. */
function fakeEngine() {
  const base: Snapshot = { version: { tenant: "t", graph: "catalog", revision: "0" }, entities: [], relationships: [] };
  const g = Graph.fromSnapshot(base);
  let rev = 0;
  const log: Patch[] = [];
  const svc = {
    palette: async () => parsePalette({ operators: [] }),
    createDraft: async () => ({ draft: "d1", baseRevision: "11", revision: "0" }),
    snapshot: async () => ({ ...g.toSnapshot(), version: { ...base.version, revision: String(rev) } }),
    check: async (): Promise<Applied> => ({ revision: String(rev), ok: true, diagnostics: [], modules: { m: { text: `rev ${rev}` } } }),
    applyPatch: async (_: string, p: Patch, expected: string): Promise<Applied> => {
      if (expected !== String(rev)) throw new EngineError("StaleRevision", "stale", 409);
      const r = g.apply(p);
      if (!r.ok) throw new EngineError("GuardFailed", "guard", 409, r.conflicts[0].key);
      rev++; log.push(p);
      return { revision: String(rev), ok: true, diagnostics: [], modules: { m: { text: `rev ${rev}` } } };
    },
  } as unknown as EditorService;
  return { svc, g, get rev() { return rev; } };
}
const add = (n: number): Edit[] => [ent(`d:m/entity:E${n}`, "Entity", { name: text(`E${n}`) })];
const names = (g: Graph) => [...g.entities()].map((e) => e.id.slice(e.id.lastIndexOf("E") + 1)).sort((a, b) => Number(a) - Number(b));

describe("editor session", () => {
  it("applies edits to the draft and the browser copy, undo and redo are inverse patches", async () => {
    const eng = fakeEngine();
    const s = await EditorSession.open(eng.svc);
    for (let i = 1; i <= 3; i++) expect((await s.apply(add(i), `add ${i}`)).ok).toBe(true);
    expect(names(s.graph)).toEqual(["1", "2", "3"]);
    expect(s.revision).toBe("3");
    await s.undo(); await s.undo();
    expect(names(s.graph)).toEqual(["1"]);
    expect(names(eng.g)).toEqual(["1"]);                // the engine's draft followed
    expect(eng.rev).toBe(5);                            // undo is a new revision, never a rewind
    await s.redo();
    expect(names(eng.g)).toEqual(["1", "2"]);
    expect(s.texts.m).toBe("rev 6");
  });

  it("a new edit clears the redo stack", async () => {
    const s = await EditorSession.open(fakeEngine().svc);
    await s.apply(add(1), "a"); await s.undo();
    expect(s.redoStack.length).toBe(1);
    await s.apply(add(2), "b");
    expect(s.redoStack.length).toBe(0);
  });

  it("a restore that changes nothing is no undo step", async () => {
    const s = await EditorSession.open(fakeEngine().svc);
    await s.absorbRestore({ revision: "0", ok: true, diagnostics: [], modules: {}, patch: { from: { tenant: "t", graph: "catalog", revision: "0" }, to: { tenant: "t", graph: "catalog", revision: "0" }, projection: "catalog", profile: "extensionalFinite", edits: [], cause: { inputs: [], configurationVersion: "0" } } }, "restore release");
    expect(s.undoStack.length).toBe(0);
  });

  it("keeps 99 steps", async () => {
    const eng = fakeEngine();
    const s = await EditorSession.open(eng.svc);
    for (let i = 1; i <= MAX_UNDO + 5; i++) await s.apply(add(i), `add ${i}`);
    expect(s.undoStack.length).toBe(99);
    for (let i = 0; i < 99; i++) await s.undo();
    expect(await s.undo()).toBeNull();
    expect(names(eng.g)).toEqual(["1", "2", "3", "4", "5"]); // the five oldest steps fell off the stack
  }, 30000);

  it("a rejected patch changes nothing and reports the engine's code", async () => {
    const eng = fakeEngine();
    const s = await EditorSession.open(eng.svc);
    await s.apply(add(1), "a");
    const bad: Edit[] = [{ kind: "entity", id: "d:m/entity:E1", before: { id: "x", type: "Entity", props: {} }, after: null }];
    const r = await s.apply(bad, "bad");
    expect(r).toMatchObject({ ok: false, code: "GuardFailed" });
    expect(s.undoStack.length).toBe(1);
    expect(names(s.graph)).toEqual(["1"]);
  });

  it("a refactoring the engine planned is one undoable step; a restore the engine applied itself is read back from the draft's log", async () => {
    const eng = fakeEngine();
    const s = await EditorSession.open(eng.svc);
    await s.apply(add(1), "a");
    const plan = { patch: { from: { tenant: "t", graph: "catalog", revision: "1" }, to: { tenant: "t", graph: "catalog", revision: "2" }, projection: "catalog", profile: "extensionalFinite" as const, edits: add(2), cause: { inputs: [], configurationVersion: "0" } }, modules: {}, preserves: {} };
    expect((await s.applyRefactoring(plan, "extract")).ok).toBe(true);
    expect(names(eng.g)).toEqual(["1", "2"]);
    await s.undo();
    expect(names(eng.g)).toEqual(["1"]);
    // the engine adds E3 on its own (restore) and answers the patch it applied; the session joins it to the draft and can undo it
    const before = eng.rev;
    const restored = { ...plan.patch, from: { ...plan.patch.from, revision: String(before) }, to: { ...plan.patch.to, revision: String(before + 1) }, edits: add(3) };
    await (eng.svc as unknown as { applyPatch: (...a: unknown[]) => Promise<unknown> }).applyPatch("d1", restored, String(before));
    await s.absorbRestore({ revision: String(eng.rev), ok: true, diagnostics: [], modules: {}, patch: restored }, "restore release");
    expect(names(s.graph)).toEqual(["1", "3"]);
    await s.undo();
    expect(names(eng.g)).toEqual(["1"]);
    expect(s.undoStack.map((x) => x.label)).toEqual(["a"]);
  });
});

describe("text applied over a draft that already holds graph edits", () => {
  const V = { tenant: "t", graph: "catalog", revision: "0" };
  const pin = (digest: string) => ent("d:sp/import:a", "Import", { digest: text(digest) });
  type EntityEdit = Extract<Edit, { kind: "entity" }>;
  const edit = (before: Edit | null, after: Edit | null): Edit => ({ kind: "entity", id: ((before ?? after) as EntityEdit).id, before: before ? (before as EntityEdit).after : null, after: after ? (after as EntityEdit).after : null });
  it("the text replaces its own module; another module it affects only gets the check patch's edits, so a graph added to the space stays", async () => {
    const eng = fakeEngine();
    const s = await EditorSession.open(eng.svc);
    // the draft: a committed entity of module a, the import pin of the space, and a graph the draft added to the space
    await s.apply([ent("d:a/entity:Old", "EntityType"), pin("old"), ent("d:sp/graph:new", "Graph")], "setup");
    const head = Graph.fromSnapshot({ version: V, entities: [ent("d:a/entity:Old", "EntityType"), pin("old")].map((e) => (e as EntityEdit).after!), relationships: [] });
    const patch: Patch = { from: V, to: V, projection: "catalog", profile: "extensionalFinite", cause: { inputs: [], configurationVersion: "" },
      edits: [edit(ent("d:a/entity:Old", "EntityType"), null), edit(null, ent("d:a/entity:Renamed", "EntityType")), edit(pin("old"), pin("new"))] };
    (eng.svc as unknown as { checkText: unknown }).checkText = async () => ({ ok: true, patch, diagnostics: [], affectedModules: ["sp"] });
    const out = await s.applyText("a", "entity Renamed;", head);
    expect(out.ok).toBe(true);
    const ids = [...s.graph.entities()].map((e) => e.id).sort();
    expect(ids).toEqual(["d:a/entity:Renamed", "d:sp/graph:new", "d:sp/import:a"]);
    expect(s.graph.entity("d:sp/import:a")!.props.digest).toEqual(text("new"));
  });
});
