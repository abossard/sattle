// The editing core on every projection of a current catalog (the synthetic estate: 14 projections, every operator kind the
// reference projections use), with the palette of the same engine.
import { describe, expect, it } from "vitest";
import { Catalog } from "../../src/core/catalog";
import type { Edit } from "../../src/core/model";
import { allSteps, readProjection, statementOf } from "../../src/editing/doc";
import { docOf, perform } from "../../src/editing/draft";
import { candidates, referenceCandidates } from "../../src/editing/scope";
import { lower, withStatements } from "../../src/editing/statements";
import { setExprEdits } from "../../src/editor/commands";
import { parseExpr } from "../../src/editor/expr";
import { coalesce } from "../../src/editor/patches";
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

/** The edits that change what is said: a decimal written `0.0` where the engine holds `0` is the same decimal. */
const real = (edits: Edit[]): Edit[] => coalesce(edits).filter((e) => {
  if (e.kind !== "entity" || !e.before || !e.after || e.after.type !== "Literal") return true;
  const a = e.before.props.value, b = e.after.props.value;
  return !(a?.kind === "decimal" && b?.kind === "decimal" && Number(a.value) === Number(b.value));
});

it("the catalog holds the projections the editors are meant for", () => {
  // loose on purpose: the definitions of the estate change, and the fixture is written again from the engine (scripts/sync-catalog.mjs)
  expect(projections.length).toBeGreaterThanOrEqual(5);
  expect(rowSets.length).toBeGreaterThan(50);
  const kinds = new Set([...docs.values()].flatMap((d) => allSteps(d).map((s) => s.kind)));
  for (const k of ["Match", "Filter", "Group", "Map", "Series", "Rollup", "Threshold", "Lift", "EmitEntity", "EmitRelationship"]) expect([...kinds]).toContain(k);
});

it("a lift is one step with its clauses as entries, as the definition has one statement for it", () => {
  const doc = docs.get("d:acme.svc/projection:serviceDeps")!;
  const lift = doc.statements.find((s) => s.name === "dependsOn")!;
  expect(lift.steps.map((s) => s.kind)).toEqual(["Lift"]);
  expect(lift.steps[0].inputs.map((i) => `${i.port} from ${i.fromName}`)).toEqual(["edges from master", "map from master"]);
  expect(lift.steps[0].slots.find((s) => s.name === "state")!.entries.map((e) => e.text)).toEqual(["status of dropRatio above 0.05 enterAfter 3 high degraded below 0.02 exitAfter 3 low ok initial ok"]);
  expect(lift.readers.sort()).toEqual(["groupOut", "serviceOut"]);
});

it("a parameter the kernel gained shows in the rows with no change here: an optional one as a place to add it, also on an entry", async () => {
  const draft = fresh();
  const find = () => { for (const p of projections) { const doc = docOf(draft, p)!; const step = allSteps(doc).find((s) => s.kind === "Series"); if (step) return { p, doc, step }; } throw new Error("no series in the catalog"); };
  const { p, step } = find();
  const row = (id: string) => rowsOf(find().doc, palette, new Catalog(draft.graph)).find((r) => r.id === id)!;
  /** What typing `value` into the row gives: its edit is a text field. */
  const typed = (r: ReturnType<typeof row>, value: string) => { if (r.edit?.kind !== "text") throw new Error("not a text edit"); return r.edit.commit(value)!; };
  // `ttl` of a series and `fill` of a field it reads came with kernel 3.18
  const ttl = row(`${step.id}#field:ttl`), read = step.slots.find((s) => s.name === "read")!.entries[0];
  expect([ttl.kind, ttl.label]).toEqual(["hole", "add ttl"]);
  expect(await perform(draft, p, typed(ttl, "36h"))).toMatchObject({ ok: true });
  expect(find().step.fields.find((f) => f.name === "ttl")!.value).toBe("36h");
  const fill = row(`${read.id}#field:fill`);
  expect([fill.kind, fill.label]).toEqual(["hole", `add fill of ${read.expr}`]);
  expect(await perform(draft, p, typed(fill, "0"))).toMatchObject({ ok: true });
  expect(find().step.slots.find((s) => s.name === "read")!.entries[0].text).toBe(`${read.expr} fill 0`);
  expect(row(`${read.id}#field:fill`).kind).toBe("field");
});

it("a property to set offers what the emitted type inherits: a WorkloadService has the key of every Component", () => {
  const doc = docs.get("d:acme.workload/projection:workloadStructure")!;
  const emit = allSteps(doc).find((s) => s.refs.some((r) => r.targetName.endsWith("WorkloadService") && r.rel === "ofType"))!;
  const offered = candidates(cat, doc, "PropertyType", emit).map((o) => o.label).sort();
  expect(offered).toEqual(expect.arrayContaining(["cluster", "externalName", "key", "kind", "name", "namespace", "own", "severity", "state"]));
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
  it("every expression that stands alone in its slot, typed back as the text it prints, is the expression it was", () => {
    const changed: string[] = [];
    for (const s of allSteps(doc)) for (const sl of s.slots) {
      if (sl.entries.length !== 1 || sl.entries[0].expr === null || sl.multiplicity === "*" || sl.multiplicity === "+") continue;
      const edits = setExprEdits(base, s.id, sl.name, sl.entries[0].expr, palette);
      if (!edits.ok || real(edits.value).length) changed.push(`${s.name} ${sl.name}: ${sl.entries[0].expr}${edits.ok ? "" : ` (${edits.reason})`}`);
    }
    // and inside an entry (an assignment, a property that is set): the value of the entry
    for (const s of allSteps(doc)) for (const sl of s.slots) for (const e of sl.entries) for (const p of e.parts) {
      if (p.entries.length !== 1 || p.entries[0].expr === null) continue;
      const edits = setExprEdits(base, e.id, p.slot, p.entries[0].expr, palette);
      if (!edits.ok || real(edits.value).length) changed.push(`${s.name} ${sl.name} ${e.key} ${p.slot}: ${p.entries[0].expr}${edits.ok ? "" : ` (${edits.reason})`}`);
    }
    expect(changed).toEqual([]);
    // the ingest projection has some hundred expressions, each typed back on the whole catalog: seconds when the machine is busy
  }, 60_000);
  it("offers every reference's present target among its choices, from the scope the palette names", () => {
    const missing: string[] = [];
    for (const s of allSteps(doc)) for (const r of s.refs) {
      if (!r.target) continue;
      const offered = referenceCandidates(cat, doc, r, s).map((o) => o.value);
      if (!offered.includes(r.target)) missing.push(`${s.name} ${r.rel} -> ${r.targetName} (scope ${r.scope ?? "none"}, ${offered.length} offered)`);
    }
    expect(missing).toEqual([]);
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
