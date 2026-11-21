import { afterEach, expect, it, vi } from "vitest";
import { EngineError } from "../src/editor/service";
import { EditorSession } from "../src/editor/session";
import { endSession, recoverExpiredDraft, run } from "../src/state/editor-effects";
import { runtime } from "../src/state/runtime";
import { fakeEngine } from "./fixtures/editor-fake";

const expired = new EngineError("UnknownDraft", "no draft d1", 404);

afterEach(endSession);

it("requests one replacement draft with local edits when a scoped preview expires", async () => {
  const { svc } = fakeEngine();
  const preview = vi.fn(async () => { throw expired; });
  svc.preview = preview;
  const session = await EditorSession.open(svc);
  runtime.session = session;
  session.noteLocalText("tutorial.firstWorkloads.gkql.json", "pending source");
  const dispatch = vi.fn();

  await run({ type: "editor/preview", graphs: ["firstWorkloads"] }, dispatch);

  expect(preview).toHaveBeenCalledExactlyOnceWith("d1", expect.objectContaining({ graphs: ["firstWorkloads"] }));
  expect(dispatch.mock.lastCall).toEqual([{ type: "editor/open", recovery: { "tutorial.firstWorkloads.gkql.json": "pending source" } }]);
});

it("ignores an expired answer from an editor session that has already been replaced", async () => {
  const old = await EditorSession.open(fakeEngine().svc);
  const current = await EditorSession.open(fakeEngine().svc);
  runtime.session = current;
  const dispatch = vi.fn();

  expect(recoverExpiredDraft(old, expired, dispatch)).toBe(false);
  expect(dispatch).not.toHaveBeenCalled();
  expect(runtime.session).toBe(current);
});
