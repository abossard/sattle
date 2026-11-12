// Statement kinds made in the editor, on the local stand-in (src/editing/statements.ts): the inner draft plays the engine and must
// only ever see kernel operators.
import { describe, expect, it } from "vitest";
import { Catalog } from "../../src/core/catalog";
import { Graph } from "../../src/core/graph";
import { docOf, perform } from "../../src/editing/draft";
import { DECL, PARAM, ROLE, declarations, diffEdits, lower, withStatements } from "../../src/editing/statements";
import { places } from "../../src/editors/BlockEditor";
import { dangling, demoCatalog, memoryDraft, palette, shape } from "./memory-draft";

const SVC = "d:acme.svc/projection:serviceDeps", OPS = "d:acme.ops/projection:ingest";
const base = demoCatalog();
const fresh = () => { const inner = memoryDraft(Graph.fromSnapshot(base.toSnapshot())); return { inner, draft: withStatements(inner) }; };
const rowSets = [SVC, OPS].flatMap((p) => docOf(memoryDraft(base), p)!.statements.filter((s) => !s.sink).map((s) => [p, s.name] as [string, string]));
const steps = (d: Parameters<typeof docOf>[0], projection: string, name: string) => docOf(d, projection)!.statements.find((s) => s.name === name)?.steps ?? [];
const kernel = new Set(palette.operators.map((o) => o.kind));
const foreign = (g: Graph) => [...g.entities()].filter((e) => e.type === DECL || e.type === PARAM || (e.id.includes("/op:") && !e.id.includes("/op:", e.id.indexOf("/op:") + 1) && /\/op:[^/]+$/.test(e.id) && !kernel.has(e.type))).map((e) => e.id);

describe("a statement kind from a row set", () => {
  it.each(rowSets)("%s: `%s` as a kind and one use of it lowers to the operators it was made from", async (projection, statement) => {
    const { inner, draft } = fresh();
    expect(await perform(draft, projection, { g: "declare", statement, name: "made" })).toMatchObject({ ok: true });
    // the author's graph holds a declaration, a template and one use; the engine's holds what it held
    const use = steps(draft, projection, statement);
    expect(use.map((s) => s.kind)).toEqual(["made"]);
    expect(declarations(new Catalog(draft.graph)).map((d) => d.name)).toEqual(["made"]);
    expect(shape(inner.graph)).toEqual(shape(base));
    expect(inner.log.length).toBe(0);
    expect(dangling(draft.graph)).toEqual([]);
    // expanding the use gives the steps back to the author
    expect(await perform(draft, projection, { g: "expand", id: use[0].id })).toMatchObject({ ok: true });
    expect(steps(draft, projection, statement).map((s) => s.kind)).toEqual(steps(inner, projection, statement).map((s) => s.kind));
    expect(shape(lower(draft.graph, palette).graph)).toEqual(shape(base));
  });

  it("the template reads through ports named after what the row set read, and gives its rows as `result`", async () => {
    const { draft } = fresh();
    await perform(draft, SVC, { g: "declare", statement: "depWindows", name: "windows" });
    const d = declarations(new Catalog(draft.graph))[0];
    const t = docOf(draft, d.template)!;
    expect(t.inputs.map((i) => `${i.name}: ${i.kind}`).sort()).toEqual(["in: rows", "master: graph"]);
    expect(t.statements.map((s) => `${s.name}: ${s.steps.map((x) => x.kind).join(" > ")}`)).toEqual(["result: Series > Rollup"]);
    expect(t.statements[0].steps.flatMap((s) => s.missing)).toEqual([]);
  });
});

describe("a statement kind with a parameter, used twice", () => {
  const setup = async () => {
    const { inner, draft } = fresh();
    const run = async (projection: string, g: Parameters<typeof perform>[2]) => { const r = await perform(draft, projection, g); expect(r).toMatchObject({ ok: true }); return r; };
    await run(SVC, { g: "declare", statement: "deps", name: "pairs" });
    const d = declarations(new Catalog(draft.graph))[0];
    const filter = docOf(draft, d.template)!.statements[0].steps[1];
    await run(d.template, { g: "param", decl: d.id, name: "keep", step: filter.id, slot: "predicate" });
    await run(SVC, { g: "addStep", kind: "pairs", statement: "others", from: { master: `${SVC}/port:master` } });
    await run(SVC, { g: "addEntry", step: `${SVC}/op:others`, slot: "keep", childType: "Expr", values: { src: "s == t" } });
    return { inner, draft, d, filter, run };
  };

  it("the palette offers the kind with the template's inputs as ports and the parameter as a slot", async () => {
    const { draft } = await setup();
    const k = draft.palette.operators.find((o) => o.kind === "pairs")!;
    expect(k.role).toBe(ROLE);
    expect(k.ports.map((p) => `${p.direction} ${p.name}: ${p.kind}`)).toEqual(["in master: graph", "out out: rows"]);
    expect(k.slots.map((s) => s.name)).toEqual(["keep", "port"]);
    // and the editors place it like any operator: a kind that reads no rows starts a row set
    expect(places(docOf(draft, SVC)!, draft.palette, { kind: "pairs" }).map((p) => p.statement)).toEqual([null]);
  });

  it("each use lowers to the template with its own value, or the template's when it gives none", async () => {
    const { inner, draft } = await setup();
    const predicate = (d: Parameters<typeof docOf>[0], name: string) => steps(d, SVC, name).map((s) => `${s.kind} ${s.slots.find((x) => x.name === "predicate")?.entries[0]?.expr ?? ""}`.trim());
    expect(steps(draft, SVC, "others").map((s) => s.kind)).toEqual(["pairs"]);
    expect(predicate(inner, "others")).toEqual(["Match", "Filter s == t"]);
    expect(predicate(inner, "deps")).toEqual(["Match", "Filter s != t"]);
    expect(steps(inner, SVC, "others")[0].inputs.map((i) => i.fromName)).toEqual(["master"]);
    expect(foreign(inner.graph)).toEqual([]);
    expect(dangling(inner.graph)).toEqual([]);
  });

  it("a change of the template reaches every use that does not say otherwise", async () => {
    const { inner, d, filter, run } = await setup();
    await run(d.template, { g: "do", cmd: { t: "expr", id: filter.id, slot: "predicate", src: "s == s" } });
    const predicate = (name: string) => steps(inner, SVC, name)[1].slots[0].entries[0].expr;
    expect(predicate("deps")).toBe("s == s");
    expect(predicate("others")).toBe("s == t");
    expect(inner.log.at(-1)!.edits.every((e) => e.kind !== "entity" || e.id.startsWith(`${SVC}/op:deps`))).toBe(true);
  });

  it("a diagnostic of a lowered operator is reported at the use", async () => {
    const { inner, draft } = await setup();
    inner.diagnostics.push({ severity: "error", code: "X", message: "bad predicate", focus: `${SVC}/op:others/predicate:#0`, module: "acme.svc" } as never);
    expect(draft.diagnostics[0].focus).toBe(`${SVC}/op:others`);
    expect(steps(draft, SVC, "others")[0].diagnostics.map((x) => x.message)).toEqual(["bad predicate"]);
  });

  it("a kind that would use itself is refused, and nothing changes", async () => {
    const { inner, draft, d } = await setup();
    const before = shape(draft.graph), sent = inner.log.length;
    const r = await perform(draft, d.template, { g: "addStep", kind: "pairs", statement: "again", from: { master: `${d.template}/port:master` } });
    expect(r).toMatchObject({ ok: false, error: "the statement kind pairs uses itself" });
    expect(shape(draft.graph)).toEqual(before);
    expect(inner.log.length).toBe(sent);
  });
});

describe("the difference of two graphs", () => {
  it.each(rowSets.slice(0, 6))("%s: removing `%s` and the edits back", async (projection, statement) => {
    const a = Graph.fromSnapshot(base.toSnapshot());
    const d = memoryDraft(Graph.fromSnapshot(base.toSnapshot()));
    for (const s of [...steps(d, projection, statement)].reverse()) await perform(d, projection, { g: "removeStep", id: s.id });
    const edits = diffEdits(d.graph, a);
    expect((await d.apply(edits, "back")).ok).toBe(true);
    expect(shape(d.graph)).toEqual(shape(a));
    expect(diffEdits(d.graph, a)).toEqual([]);
  });
});
