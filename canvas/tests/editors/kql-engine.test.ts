// The KQL text through a real engine's draft (note 11, stage d): a text stored in a draft and committed is what the space holds afterwards and
// what a new draft reads, and the other projection files keep their text. Commits the edit and then the original back, so the engine ends as
// it began; it writes no file unless the engine was started with --write-definitions.
// Runs only with an engine: ENGINE=http://127.0.0.1:5080 npx vitest run tests/editors/kql-engine.test.ts
import { describe, expect, it } from "vitest";
import type { Analysis } from "../../src/editor/kql";
import { add, addBy, apply, listsOf, newOperatorAt, starter, startersFor, type Column } from "../../src/editor/kql-edit";
import { EditorService } from "../../src/editor/service";
import { EditorSession } from "../../src/editor/session";

const ENGINE = (globalThis as unknown as { process: { env: Record<string, string | undefined> } }).process.env.ENGINE;
const projection = "acme.biz.capabilityView", file = `${projection}.kql`;
const before = "let images = realized | join kind=inner (realized | summarize n = count() by service | where n == 1) on service | project service, capability;";
const after = "let images = realized | project service, capability | join kind=inner (realized | summarize n = count() by service | where n == 1) on service;";

describe.skipIf(!ENGINE)("a projection's KQL text through the engine's draft", () => {
  it("store, commit and reload give the edited file exactly, and the other files keep their text", async () => {
    const svc = new EditorService(ENGINE!);
    const others = ["acme.svc.serviceDeps", "acme.workload.workloadHealth", "acme.ops.ingest"];
    const digests = async () => Promise.all(others.map(async (p) => (await svc.kqlText(p)).digest));
    const was = await digests();
    const commit = async (text: string) => {
      const s = await EditorSession.open(svc);
      expect((await s.storeKql(file, text)).ok).toBe(true);
      expect((await svc.kqlText(projection, s.draft.draft)).text).toBe(text);
      await svc.commit(s.draft.draft, s.draft.baseRevision);
    };
    const original = (await svc.kqlText(projection)).text;
    expect(original).toContain(before);
    const edited = original.replace(before, after);
    await commit(edited);
    try {
      expect((await svc.kqlText(projection)).text).toBe(edited);
      const fresh = await EditorSession.open(svc);
      expect((await svc.kqlText(projection, fresh.draft.draft)).text).toBe(edited);
      await svc.discard(fresh.draft.draft);
      expect(await digests()).toEqual(was);
    } finally {
      await commit(original);
    }
    expect((await svc.kqlText(projection)).text).toBe(original);
  }, 120000);

  it("a new operator of every kind, where each statement takes one, reads without errors at its place; a summarize without by takes one", async () => {
    const svc = new EditorService(ENGINE!);
    const projections = ["acme.biz.capabilityView", "acme.svc.serviceDeps", "acme.workload.workloadHealth", "acme.deployhealth.deploymentHealth", "acme.tree.treeSignals"];
    let tried = 0;
    const errorsAt = (a: Analysis, at: [number, number]) => a.diagnostics.filter((d) => d.severity === "error" && d.start < at[1] && d.end > at[0]).map((d) => d.message);
    for (const p of projections) {
      const { text } = await svc.kqlText(p);
      // the columns before a new operator as the engine has them (full depth): fast depth gives Kusto's, which can differ after a join
      const a = await svc.kqlAnalyse(p, text, "full");
      const stmts = a.statements as { kind: string; operators: { kind: string; columns?: Column[] }[] }[];
      for (const [i, st] of stmts.entries()) {
        const ops = listsOf(a).find((l) => l.id === `statements.${i}.operators`);
        const k = newOperatorAt(st);
        const cols = st.operators[k - 1]?.columns ?? [];
        // the outline's columns where an `invoke` flows in (the body's internal `match_id`) and after a join (Kusto's renamed key, `t1`) are not the
        // rows' columns at either depth (440c8ed): reported to the KQL session; new operators there are left out of this check
        if (!ops || !cols.length || cols.some((c) => c.name === "match_id") || ["join", "telemetry-join"].includes(st.operators[k - 1].kind)) continue;
        for (const kind of startersFor(st)) {
          const op = starter(kind, cols)!;
          const e = add(text, ops, k, op);
          const out = apply(text, e.changes);
          expect(errorsAt(await svc.kqlAnalyse(p, out, "fast"), e.select!), `${p} statement ${i}: ${op}`).toEqual([]);
          tried++;
        }
        // a summarize without by, then its by (where a summarize fits)
        if (!startersFor(st).includes("summarize")) continue;
        const bare = add(text, ops, k, "summarize n = count()");
        const t1 = apply(text, bare.changes);
        const a1 = await svc.kqlAnalyse(p, t1, "fast");
        const sm = (a1.statements as { operators: { kind: string; place: [number, number] }[] }[])[i].operators.find((o) => o.kind === "summarize" && o.place[0] === bare.select![0])!;
        const by = addBy(sm as never, cols.find((c) => !c.element)?.name ?? cols[0].name)!;
        const t2 = apply(t1, by.changes);
        expect(errorsAt(await svc.kqlAnalyse(p, t2, "fast"), [bare.select![0], by.select![1]]), `${p} statement ${i}: by`).toEqual([]);
      }
    }
    expect(tried).toBeGreaterThan(40);
  }, 300000);
});
