// The recorded walkthroughs (tests/walkthroughs/*.json) replayed through the gesture layer the editors share, against a live engine:
// the commands of a walkthrough are composed by `perform` on the engine's draft session into one patch. What the engine then holds is checked
// the way scripts/walkthroughs-cdp.mjs checks the application's own path: no diagnostics beyond the allowed ones, canonical text equal
// to the projection it rebuilds, preview digests of the rebuilt and the original graph equal, and data in the preview.
// Runs only with an engine: ENGINE=http://127.0.0.1:5080 npx vitest run tests/editing/engine-replay.test.ts
import { describe, expect, it } from "vitest";
import { EditorService } from "../../src/editor/service";
import { EditorSession } from "../../src/editor/session";
import { docOf, perform } from "../../src/editing/draft";
import { sessionDraft } from "../../src/editing/session-draft";
import type { Command } from "../../src/state/types";

interface Walk { name: string; module: string; original?: string; graphs: string[]; commands: { label: string; cmd: Command }[]; allowDiagnostics?: string[]; nonEmpty?: boolean; atLeast?: Record<string, number> }
const files = import.meta.glob("../walkthroughs/*.json", { eager: true, import: "default" }) as Record<string, Walk>;
const walks = Object.entries(files).map(([f, w]) => [f.replace(/^.*\/|\.json$/g, ""), w] as [string, Walk]);
const ENGINE = (globalThis as unknown as { process: { env: Record<string, string | undefined> } }).process.env.ENGINE;

const block = (text: string, name: string): string => text.match(new RegExp(`projection ${name} [\\s\\S]*?\\n}\\n`))?.[0] ?? "";
const lines = (text: string, from: string, to: string): string[] => text.split("\n").map((l) => l.trim()).filter(Boolean).map((l) => l.replace(`projection ${from} `, `projection ${to} `));

describe.skipIf(!ENGINE)("the walkthroughs through the gesture layer, on the engine", () => {
  it.each(walks)("%s", async (_file, w) => {
    const svc = new EditorService(ENGINE!);
    const session = await EditorSession.open(svc);
    try {
      const projection = `d:${w.module}/projection:${w.name}`;
      if (!session.catalog.projections().some((p) => p.module === w.module)) return console.log(`skip: module ${w.module} is not on this engine`);
      if (w.original && !session.graph.entity(`d:${w.module}/projection:${w.original}`)) return console.log(`skip: projection ${w.original} is not on this engine`);
      // a parameter bound to "$original" takes the value the space binds to the same parameter of the projection it rebuilds
      const bound = (port: string) => { for (const r of session.graph.relationships()) if (r.type === "bindsPort" && r.target === `d:${w.module}/projection:${w.original}/port:${port}`) return session.graph.entity(r.source)?.props.value; return undefined; };
      const commands: { label: string; cmd: Command }[] = JSON.parse(JSON.stringify(w.commands));
      for (const { cmd } of commands) if (cmd.t === "newProjection") for (const p of cmd.spec.params) if (p.value === "$original") { const v = bound(p.port); expect(v, `binding of ${p.port}`).toBeDefined(); p.value = String((v as { value: unknown }).value); }
      const draft = sessionDraft(session);
      // all commands of the walkthrough as one change: the engine checks (and, once the projection is whole, evaluates) after every patch,
      // which takes seconds each, and what is compared is what the engine holds at the end
      expect(await perform(draft, projection, { g: "batch", label: w.name, items: commands.map(({ cmd }) => ({ g: "do" as const, cmd })) })).toMatchObject({ ok: true });

      const allowed = new Set(w.allowDiagnostics ?? []);
      expect(session.diagnostics.filter((d) => !allowed.has(d.code)).map((d) => `${d.code}: ${d.message}`)).toEqual([]);
      const text = session.texts[w.module] ?? "";
      expect(block(text, w.name)).not.toBe("");
      if (w.original) expect(lines(block(text, w.name), w.name, w.original)).toEqual(lines(block(text, w.original), w.original, w.original));
      if (session.diagnostics.some((d) => d.severity === "error")) return;
      const pv = await svc.preview(session.draft.draft, { graphs: w.graphs, include: ["counts"], limit: "3000" });
      expect(pv.graphs.map((g) => g.id).sort()).toEqual([...w.graphs].sort());
      expect(new Set(pv.graphs.map((g) => g.digest)).size, pv.graphs.map((g) => `${g.id} ${g.digest}`).join(", ")).toBe(1);
      console.log(`${w.name}: ${commands.length} commands, preview ${pv.graphs.map((g) => `${g.id} ${g.counts.entities}/${g.counts.relationships} ${String(g.digest).slice(0, 19)}`).join(", ")}`);
      for (const g of pv.graphs) {
        if (w.nonEmpty) expect(Number(g.counts.entities) + Number(g.counts.relationships)).toBeGreaterThan(0);
        for (const [k, n] of Object.entries(w.atLeast ?? {})) expect(Number(g.counts[k as "entities" | "relationships"]), `${g.id} ${k}`).toBeGreaterThanOrEqual(n);
      }
    } finally { await svc.discard(session.draft.draft).catch(() => {}); }
  }, 600_000);
});

describe.skipIf(!ENGINE)("placeholders of the engine's skeleton (engine 3.19)", () => {
  const P = "d:acme.svc/projection:serviceDeps";
  it("a new rollup holds a placeholder aggregate; the first real one replaces it, and an author's `n = count()` stays", async () => {
    const svc = new EditorService(ENGINE!);
    const session = await EditorSession.open(svc);
    try {
      const draft = sessionDraft(session);
      const agg = (step: string) => docOf(draft, P)!.statements.flatMap((s) => s.steps).find((s) => s.id === step)!.slots.find((s) => s.name === "agg")!;
      /** done, or the reason it was not, as the failure */
      const done = async (r: ReturnType<typeof perform>) => { const x = await r; expect(x.ok ? "" : x.error).toBe(""); };
      const add = (statement: string) => perform(draft, P, { g: "addStep", kind: "Rollup", statement, from: { in: `${P}/op:dependsOn/port:out` } });
      const entry = (step: string, name: string, fn: string, src: string) => perform(draft, P, { g: "addEntry", step, slot: "agg", childType: "Aggregate", values: { name, fn, src } });
      await done(add("probeA"));
      const a = `${P}/op:probeA`;
      // the engine named the placeholder, the draft knows it, and the author sees an empty slot
      expect(agg(a).placeholders.length).toBe(1);
      expect(agg(a).entries).toEqual([]);
      await done(entry(a, "total", "sum", "flows"));
      expect(agg(a)).toMatchObject({ placeholders: [] });
      expect(agg(a).entries.map((e) => e.text)).toEqual(["total = sum(flows)"]);
      // in a second rollup the author writes what the placeholder is, then another aggregate: both stay
      await done(add("probeB"));
      const b = `${P}/op:probeB`;
      await done(entry(b, "n", "count", ""));
      await done(entry(b, "total", "sum", "flows"));
      expect(agg(b).entries.map((e) => e.text).sort()).toEqual(["n = count()", "total = sum(flows)"]);
      console.log(`placeholders: ${[...draft.placeholders].length} still known after the two rollups (their keys)`);
    } finally { await svc.discard(session.draft.draft).catch(() => {}); }
  }, 300_000);
});
