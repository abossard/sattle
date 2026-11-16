import { describe, expect, it } from "vitest";
import { Graph } from "../src/core/graph";
import type { Edit, Patch, Snapshot } from "../src/core/model";
import { MAX_UNDO, EditorSession } from "../src/editor/session";
import { ent, text } from "../src/editor/patches";
import { EngineError, type Applied, type EditorService } from "../src/editor/service";
import { parsePalette } from "../src/editor/palette";

import { add, fakeEngine, names, restoresTo } from "./fixtures/editor-fake";

describe("editor session", () => {
  it.each<[string, (s: EditorSession) => Promise<unknown>, boolean]>([
    ["an edit whose answer carries a new text", (s) => s.apply(add(1), "add 1"), true],
    ["an edit refused before the engine", (s) => s.apply([], "nothing"), false],
  ])("the texts are a new object exactly when a text changed, and the old one stays as it was: %s", async (_, act, changed) => {
    const s = await EditorSession.open(fakeEngine().svc);
    const before = s.texts, was = { ...before };
    await act(s);
    expect([s.texts !== before, before]).toEqual([changed, was]);
  });
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
    const eng = fakeEngine();
    restoresTo(eng, { from: { tenant: "t", graph: "catalog", revision: "0" }, to: { tenant: "t", graph: "catalog", revision: "1" }, projection: "catalog", profile: "extensionalFinite", edits: [], cause: { inputs: [], configurationVersion: "0" } });
    const s = await EditorSession.open(eng.svc);
    await s.restore("sha256:r");
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

  it("a restore the engine applied itself is read back from the draft's log and is one undoable step", async () => {
    const eng = fakeEngine();
    const s = await EditorSession.open(eng.svc);
    await s.apply(add(1), "a");
    // the engine adds E3 on its own (restore) and answers the patch it applied; the session joins it to the draft and can undo it
    const before = eng.rev;
    const restored = { from: { tenant: "t", graph: "catalog", revision: String(before) }, to: { tenant: "t", graph: "catalog", revision: String(before + 1) }, projection: "catalog", profile: "extensionalFinite" as const, edits: add(3), cause: { inputs: [], configurationVersion: "0" } };
    restoresTo(eng, restored);
    await s.restore("sha256:r");
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

  it.each([
    ["at the draft's revision, the text is kept and the session follows the new revision", 0, true],
    ["at a revision the draft has left, it is refused and the session reads the draft again", 5, false],
  ])("storing a projection's KQL text %s", async (_what, moved, ok) => {
    const eng = fakeEngine();
    const s = await EditorSession.open(eng.svc);
    await s.apply(add(1), "add 1");
    eng.rev += moved; // another writer moved the draft on
    const o = await s.storeKql("m.p.kql", "graph p : m;");
    expect(o.ok).toBe(ok);
    expect(eng.files.get("m.p.kql")).toBe(ok ? "graph p : m;" : undefined);
    expect(s.revision).toBe(String(eng.rev));
    expect(s.undoStack.map((x) => x.label)).toEqual(["add 1"]);
  });
});

// Code review 2, change 2: every engine call on a draft goes through the session's queue.
describe("the session's queue", () => {
  it.each(["preview", "commit", "discard", "restore"] as const)("%s waits for an edit in flight", async (call) => {
    const eng = fakeEngine();
    const s = await EditorSession.open(eng.svc);
    const order: string[] = [];
    let release!: () => void;
    const gate = new Promise<void>((r) => { release = r; });
    const svc = eng.svc as unknown as Record<string, (...a: unknown[]) => Promise<unknown>>;
    const apply = svc.applyPatch;
    svc.applyPatch = async (...a) => { await gate; order.push("apply"); return apply(...a); };
    svc.preview = async () => { order.push("preview"); return { scope: { rows: "0", totalRows: "0", complete: true, logicalTime: "" }, diagnostics: [], graphs: [], milliseconds: "0" }; };
    svc.commit = async () => { order.push("commit"); return { catalogRevision: "12", modules: [] }; };
    svc.discard = async () => { order.push("discard"); };
    svc.restore = async () => { order.push("restore"); const r = String(eng.rev); return { revision: r, ok: true, diagnostics: [], modules: {}, patch: { from: { tenant: "t", graph: "catalog", revision: r }, to: { tenant: "t", graph: "catalog", revision: r }, projection: "catalog", profile: "extensionalFinite", edits: [], cause: { inputs: [], configurationVersion: "0" } } }; };
    const edit = s.apply(add(1), "a");
    const other = call === "preview" ? s.preview({}) : call === "commit" ? s.commit("m", undefined, new Graph(), "11") : call === "discard" ? s.discard() : s.restore("sha256:x");
    release();
    await Promise.all([edit, other]);
    expect(order, "the call reached the engine before the edit it was started after").toEqual(["apply", call]);
  });
});
