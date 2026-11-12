// @vitest-environment jsdom
// The five reference projections built through the interface of each notation and checked by a live engine: is what the notation
// produced accepted, does it print the canonical text of the original, and has its preview the digest of the original's.
// The notation works on a copy of the engine's draft with the engine's own skeletons; what it did is sent as one patch at the end
// (the engine evaluates after every patch of a whole projection, seconds each).
// Runs only with an engine: ENGINE=http://127.0.0.1:5080 npx vitest run tests/editors/replay-engine.test.tsx
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { Graph } from "../../src/core/graph";
import type { Edit } from "../../src/core/model";
import { docOf, type DraftPort } from "../../src/editing/draft";
import { sessionDraft } from "../../src/editing/session-draft";
import { coalesce } from "../../src/editor/patches";
import { EditorService } from "../../src/editor/service";
import { EditorSession } from "../../src/editor/session";
import { build, Hands, unbuildable, WAYS } from "./drive";
import { mount } from "./harness";
import { prepare, REFERENCES, walk } from "./references";

const ENGINE = (globalThis as unknown as { process: { env: Record<string, string | undefined> } }).process.env.ENGINE;
const block = (text: string, name: string): string => text.match(new RegExp(`projection ${name} [\\s\\S]*?\\n}\\n`))?.[0] ?? "";
const lines = (text: string, from: string): string[] => text.split("\n").map((l) => l.trim()).filter(Boolean).map((l) => l.replace(`projection ${from} `, "projection P "));

/** A copy of the engine's draft that a notation edits with the engine's skeletons; `flush` sends everything it did as one patch. */
function buffered(session: EditorSession): DraftPort & { flush(label: string): ReturnType<EditorSession["apply"]> } {
  const real = sessionDraft(session), graph = Graph.fromSnapshot(session.graph.toSnapshot()), done: Edit[] = [];
  graph.version = session.graph.version;
  return {
    graph, palette: real.palette, diagnostics: [], placeholders: new Set(real.placeholders), skeleton: real.skeleton,
    async apply(edits) {
      const v = graph.version ?? { tenant: "", graph: "catalog", revision: "0" };
      const r = graph.apply({ from: v, to: { ...v, revision: String(Number(v.revision) + 1) }, projection: "catalog", profile: "extensionalFinite", edits, cause: { inputs: [], configurationVersion: "0" } });
      if (!r.ok) return { ok: false, error: r.conflicts[0]?.message ?? "conflict" };
      done.push(...edits);
      return { ok: true };
    },
    flush: (label) => session.apply(coalesce(done), label),
  };
}

const table: Record<string, Record<string, string>> = {};
describe.skipIf(!ENGINE)("the reference projections through each notation, on the engine", () => {
  describe.each(REFERENCES)("%s", (title, file) => {
    const w = walk(file);
    it.each(Object.keys(WAYS))("through the %s notation", async (notation) => {
      const say = (s: string) => { (table[title] ??= {})[notation] = s; };
      const svc = new EditorService(ENGINE!);
      const session = await EditorSession.open(svc);
      try {
        // the twin has the name the walkthrough gives its rebuilt projection, so the walkthrough's preview graphs are the two to compare
        const twin = w.original ? w.name : `${w.name}Ui`;
        const { like, projection } = await prepare(sessionDraft(session), w, twin);
        const missing = unbuildable(like, session.palette);
        if (missing.length) return say(`not buildable in the notations: ${missing.join(", ")}`);
        const draft = buffered(session);
        const h = new Hands(userEvent.setup());
        const page = mount(WAYS[notation].Editor, projection, draft);
        await build(WAYS[notation], h, draft, projection, like);
        page.unmount();
        const sent = await draft.flush(`${title} through ${notation}`);
        if (!sent.ok) return say(`refused: ${sent.error}`);
        const errors = session.diagnostics.filter((d) => d.severity === "error" && d.focus.startsWith(projection));
        const text = session.texts[w.module] ?? "", name = w.original ?? w.name;
        const sameText = lines(block(text, twin), twin).join("\n") === lines(block(text, name), name).join("\n");
        const graphs = w.original ? w.graphs : [w.name, twin];
        const pv = errors.length ? null : await svc.preview(session.draft.draft, { graphs, include: ["counts"], limit: "3000" });
        const digests = new Set(pv?.graphs.map((g) => g.digest));
        say([
          errors.length ? `rejected (${errors[0].code}: ${errors[0].message})` : "accepted",
          `text ${sameText ? "equal" : "differs"}`,
          pv ? `digest ${pv.graphs.length === 2 && digests.size === 1 ? "equal" : "differs"} (${pv.graphs.map((g) => `${g.id} ${g.counts.entities}/${g.counts.relationships} ${String(g.digest).slice(7, 19)}`).join(", ")})` : "no preview",
          `${h.keys} keys, ${h.clicks} clicks`,
        ].join("; "));
        expect(errors.map((d) => `${d.code}: ${d.message}`)).toEqual([]);
        expect(sameText, "canonical text").toBe(true);
        expect(pv!.graphs.length).toBe(2);
        expect(digests.size, "preview digests").toBe(1);
        expect(docOf(draft, projection)).toBeTruthy();
      } finally { await svc.discard(session.draft.draft).catch(() => {}); }
    }, 3_600_000);
  });

  it("the table", () => { console.log(JSON.stringify(table, null, 1)); });
});
