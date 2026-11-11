// Defects of the code review in the editor session (editor/session.ts:55-59 and :96-108).
import { describe, expect, it } from "vitest";
import type { Patch } from "../../src/core/model";
import { EditorSession } from "../../src/editor/session";
import { add, fakeEngine, names } from "../fixtures/editor-fake";

const V = (rev: string) => ({ tenant: "t", graph: "catalog", revision: rev });
const emptyPatch = (from: string, to: string): Patch => ({ from: V(from), to: V(to), projection: "catalog", profile: "extensionalFinite", edits: [], cause: { inputs: [], configurationVersion: "0" } });

describe("editor/session.ts:55-59 commands are not serialised", () => {
  it("two undos started together undo two steps", async () => {
    const eng = fakeEngine();
    const s = await EditorSession.open(eng.svc);
    await s.apply(add(1), "one"); await s.apply(add(2), "two");
    await Promise.all([s.undo(), s.undo()]);
    expect(names(eng.g), "both read the same top of the stack: the second is a guard failure").toEqual([]);
    expect(names(s.graph)).toEqual([]);
  });
});

describe("editor/session.ts:96-108 the restore of a release equal to the draft", () => {
  it("is followed by an edit that works", async () => {
    const eng = fakeEngine();
    const s = await EditorSession.open(eng.svc);
    // the engine answers a restore that changes nothing with a new revision and an empty patch
    await (eng.svc as unknown as { applyPatch: (...a: unknown[]) => Promise<unknown> }).applyPatch("d1", emptyPatch("0", "1"), "0");
    await s.absorbRestore({ revision: "1", ok: true, diagnostics: [], modules: {}, patch: emptyPatch("0", "1") }, "restore release");
    const next = await s.apply(add(1), "edit after the restore");
    expect(next, "the draft is at revision 1 on the engine; the browser copy still says its graph is at 0").toMatchObject({ ok: true });
  });
});
