// @vitest-environment jsdom
// The five reference projections built through the interface of each notation, on a draft in memory over the catalog of the
// synthetic estate: an empty twin of the projection is filled in by an author's actions (drive.tsx), and what the twin says is
// compared with what the original says. Counted are the keys, the clicks and the changes of the draft it took.
// The same builds are sent to a live engine in replay-engine.test.tsx, which compares canonical text and preview digests.
// Run all five: REPLAY=all npx vitest run tests/editors/replay.test.tsx
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { docOf } from "../../src/editing/draft";
import { estateCatalog, estatePalette } from "../editing/estate";
import { memoryDraft } from "../editing/memory-draft";
import { differing, reading } from "../editing/reading";
import { build, Hands, unbuildable, WAYS } from "./drive";
import { mount } from "./harness";
import { positional, prepare, REFERENCES, walk } from "./references";

// The two small ones run with the suite (a few seconds per notation); all five with REPLAY=all (some minutes).
const all = (globalThis as unknown as { process: { env: Record<string, string | undefined> } }).process.env.REPLAY === "all";
const chosen = REFERENCES.filter(([title]) => all || title === "series with threshold" || title === "fixpoint closure");

const counts: Record<string, Record<string, string>> = {};
describe.each(chosen)("%s", (title, file) => {
  const w = walk(file);
  it.each(Object.keys(WAYS))("built through the %s notation, says what the original says", async (notation) => {
    const draft = memoryDraft(estateCatalog(), estatePalette);
    const { like, projection } = await prepare(draft, w, "twin");
    const missing = unbuildable(like, draft.palette);
    if (missing.length) { (counts[title] ??= {})[notation] = `not buildable: ${missing.join(", ")}`; return; }
    const sent = draft.log.length;
    const h = new Hands(userEvent.setup());
    mount(WAYS[notation].Editor, projection, draft);
    await build(WAYS[notation], h, draft, projection, like);
    expect(differing(reading(positional(like)), reading(positional(docOf(draft, projection)!)))).toEqual([]);
    (counts[title] ??= {})[notation] = `${h.keys} keys, ${h.clicks} clicks, ${draft.log.length - sent} changes`;
  }, 600_000);
});

it("what it took, per projection and notation", () => {
  console.log(JSON.stringify(counts, null, 1));
  expect(Object.keys(counts).length).toBe(chosen.length);
});
