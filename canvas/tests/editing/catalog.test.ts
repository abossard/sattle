// The editing core on every projection of a current catalog (the synthetic estate: 14 projections, every operator kind the
// reference projections use), with the palette of the same engine.
import { describe, expect, it } from "vitest";
import { Catalog } from "../../src/core/catalog";
import { allSteps, readProjection, statementOf } from "../../src/editing/doc";
import { docOf, perform } from "../../src/editing/draft";
import { lower, withStatements } from "../../src/editing/statements";
import { parseExpr } from "../../src/editor/expr";
import { layoutOf } from "../../src/editors/graph-layout";
import { rowsOf } from "../../src/editors/rows";
import { estateCatalog, estatePalette as palette } from "./estate";
import { dangling, memoryDraft, shape } from "./memory-draft";

const base = estateCatalog();
const cat = new Catalog(base);
const projections = cat.projections().map((p) => p.id).sort();
const docs = new Map(projections.map((id) => [id, readProjection(cat, palette, id)!]));
const fresh = () => memoryDraft(estateCatalog(), palette);
const before = shape(base);
const rowSets = projections.flatMap((p) => docs.get(p)!.statements.filter((s) => !s.sink));

it("the catalog holds the projections the editors are meant for", () => {
  // loose on purpose: the definitions of the estate change, and the fixture is written again from the engine (scripts/sync-catalog.mjs)
  expect(projections.length).toBeGreaterThanOrEqual(5);
  expect(rowSets.length).toBeGreaterThan(50);
  const kinds = new Set([...docs.values()].flatMap((d) => allSteps(d).map((s) => s.kind)));
  for (const k of ["Match", "Filter", "Group", "Map", "Series", "Rollup", "Threshold", "Lift", "EmitEntity", "EmitRelationship"]) expect([...kinds]).toContain(k);
});

describe.each(projections)("%s", (id) => {
  const doc = docs.get(id)!;
  it("reads as statements that hold every operator once, each after what it reads", () => {
    expect(allSteps(doc).map((s) => s.id).sort()).toEqual([...cat.children(id, "op")].sort());
    for (const st of doc.statements) for (const s of st.steps) expect(statementOf(s.name)).toBe(st.name);
    const at = new Map(doc.statements.map((s, i) => [s.name, i]));
    for (const st of doc.statements) for (const r of st.readers) expect(at.get(r)!).toBeGreaterThan(at.get(st.name)!);
    for (const st of doc.statements) st.steps.slice(1).forEach((s) => expect(s.inputs.some((i) => i.inChain), `${s.name} reads the step before it`).toBe(true));
  });
  it("every expression in it prints as text the grammar reads", () => {
    const texts = allSteps(doc).flatMap((s) => s.slots.flatMap((sl) => sl.entries.flatMap((e) => [e.expr, ...e.parts.flatMap((p) => p.entries.map((x) => x.expr))]))).filter((t): t is string => t !== null);
    for (const t of texts) expect(parseExpr(t), t).toMatchObject({ ok: true });
  });
  it("has a row for everything and a card for every step, no card on another", () => {
    const rows = rowsOf(doc, palette, cat);
    expect(new Set(rows.map((r) => r.id)).size).toBe(rows.length);
    for (const s of allSteps(doc)) expect(rows.some((r) => r.id === s.id)).toBe(true);
    const l = layoutOf(doc, (k) => k, () => []);
    expect(l.cards.length).toBe(doc.inputs.length + allSteps(doc).length);
    for (const w of l.wires) expect(l.cards.find((c) => c.id === w.toCard)!.x).toBeGreaterThan(l.cards.find((c) => c.id === w.fromCard)!.x);
  });
});

describe("gestures on every row set of the catalog", () => {
  // one draft per projection: every change is undone by the next, and the graph is compared with the catalog once at the end
  it.each(projections)("%s: a step added at every position of every row set and removed again leaves the draft as it was", async (projection) => {
    const draft = fresh();
    for (const st of docs.get(projection)!.statements.filter((s) => !s.sink)) for (let at = 0; at <= st.steps.length; at++) {
      // before a step that reads no rows nothing can stand
      if (at === 0 && !st.steps[0].inputs.some((i) => i.kind === "rows")) continue;
      expect(await perform(draft, projection, { g: "addStep", kind: "Distinct", statement: st.name, at }), `${st.name} at ${at}`).toMatchObject({ ok: true });
      const now = docOf(draft, projection)!.statements.find((s) => s.name === st.name)!;
      expect(now.steps.map((s) => s.kind), `${st.name} at ${at}`).toEqual([...st.steps.slice(0, at).map((s) => s.kind), "Distinct", ...st.steps.slice(at).map((s) => s.kind)]);
      expect(await perform(draft, projection, { g: "removeStep", id: now.steps[at].id })).toMatchObject({ ok: true });
    }
    expect(dangling(draft.graph)).toEqual([]);
    expect(shape(draft.graph)).toEqual(before);
  }, 120_000);

  it.each(projections)("%s: every row set made a statement kind lowers to the operators it was made from, and expands back to them", async (projection) => {
    const inner = fresh();
    const sets = docs.get(projection)!.statements.filter((s) => !s.sink);
    for (const st of sets) {
      const draft = withStatements(inner);
      expect(await perform(draft, projection, { g: "declare", statement: st.name, name: "made" }), st.name).toMatchObject({ ok: true });
      const use = docOf(draft, projection)!.statements.find((s) => s.name === st.name)!.steps;
      expect(use.map((s) => s.kind)).toEqual(["made"]);
      // nothing was sent on: what the use lowers to is what the engine already holds
      expect(inner.log.length, st.name).toBe(0);
      if (st !== sets[0]) continue;
      expect(await perform(draft, projection, { g: "expand", id: use[0].id })).toMatchObject({ ok: true });
      expect(shape(lower(draft.graph, palette).graph)).toEqual(before);
    }
  }, 120_000);
});
