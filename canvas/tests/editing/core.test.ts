import { describe, expect, it } from "vitest";
import { Catalog } from "../../src/core/catalog";
import { Graph } from "../../src/core/graph";
import { setExprEdits } from "../../src/editor/commands";
import { parseExpr } from "../../src/editor/expr";
import { coalesce } from "../../src/editor/patches";
import { allSteps, readProjection, statementOf } from "../../src/editing/doc";
import { docOf, perform } from "../../src/editing/draft";
import { EXPR_TYPES, exprText } from "../../src/editing/exprtext";
import { compose } from "../../src/editing/gestures";
import { entryShape } from "../../src/editing/shapes";
import { dangling, demoCatalog, memoryDraft, palette, shape, skeletonOf } from "./memory-draft";

const SVC = "d:acme.svc/projection:serviceDeps", OPS = "d:acme.ops/projection:ingest";
const base = demoCatalog();
const cat = new Catalog(base);
const copy = () => Graph.fromSnapshot(base.toSnapshot());

/** Every place of the catalog that holds one expression: the element it hangs under and the slot. */
const exprPlaces = (): [string, string, string][] => {
  const out: [string, string, string][] = [];
  for (const r of base.relationships()) {
    if (r.type !== "contains" || !EXPR_TYPES.has(cat.typeOf(r.target)) || EXPR_TYPES.has(cat.typeOf(r.source)) || cat.typeOf(r.source) === "SetComponent") continue;
    const slot = r.props.slot?.kind === "text" ? r.props.slot.value : "";
    if (cat.children(r.source, slot).length === 1) out.push([r.source, slot, exprText(cat, r.target)]);
  }
  return out;
};

describe("an expression as text", () => {
  const places = exprPlaces();
  it("the catalog holds expressions of every kind to try", () => {
    expect(places.length).toBeGreaterThan(100);
    expect(places.some(([, , t]) => /\(.*: /.test(t)) && places.some(([, , t]) => / (==|and|\/|>) /.test(t))).toBe(true);
  });
  it.each(places)("%s %s: `%s` parses", (_parent, _slot, text) => {
    expect(parseExpr(text)).toMatchObject({ ok: true });
  });
  it.each(places)("%s %s: typing `%s` back changes nothing", async (parent, slot, text) => {
    const draft = memoryDraft(copy());
    const edits = setExprEdits(draft.graph, parent, slot, text, palette);
    expect(edits.ok && (await draft.apply(coalesce(edits.value), "retype"))).toMatchObject({ ok: true });
    expect(shape(draft.graph)).toEqual(shape(base));
  });
});

describe.each([[SVC], [OPS]])("the read model of %s", (id) => {
  const doc = readProjection(cat, palette, id)!;
  it("holds every operator once, in a statement named by its pieces", () => {
    const ops = cat.children(id, "op");
    expect(allSteps(doc).map((s) => s.id).sort()).toEqual([...ops].sort());
    for (const st of doc.statements) for (const s of st.steps) expect(statementOf(s.name)).toBe(st.name);
  });
  it("puts a statement after the statements it reads, and sinks last", () => {
    const at = new Map(doc.statements.map((s, i) => [s.name, i]));
    for (const st of doc.statements) for (const r of st.readers) expect(at.get(r)!).toBeGreaterThan(at.get(st.name)!);
    const firstSink = doc.statements.findIndex((s) => s.sink);
    expect(doc.statements.slice(firstSink).every((s) => s.sink || s.steps.some((x) => x.inputs.some((i) => !i.inChain)))).toBe(true);
  });
  it("chains the steps of a statement: each reads the one before it", () => {
    for (const st of doc.statements) st.steps.slice(1).forEach((s, i) => expect(s.inputs.some((x) => x.inChain && x.from?.startsWith(`${st.steps[i].id}/port:`))).toBe(true));
  });
  it("gives every entry a line of text and every input a name", () => {
    for (const s of allSteps(doc)) {
      for (const sl of s.slots) for (const e of sl.entries) expect(e.text, `${s.name}.${sl.name}`).not.toBe("");
      for (const i of s.inputs) if (i.from) expect(i.fromName, `${s.name}.${i.port}`).not.toBe("");
    }
  });
});

const consistent = dangling;
const statements = (id: string) => readProjection(cat, palette, id)!.statements.filter((s) => !s.sink);

describe("adding and removing a step", () => {
  const cases = [SVC, OPS].flatMap((p) => statements(p).flatMap((st) => Array.from({ length: st.steps.length + 1 }, (_, at) => [p, st.name, at] as [string, string, number])));
  it.each(cases)("%s: a filter in `%s` at position %i, removed again, leaves the draft as it was", async (projection, statement, at) => {
    const draft = memoryDraft(copy());
    const before = shape(draft.graph);
    const added = await perform(draft, projection, { g: "addStep", kind: "Filter", statement, at });
    expect(added).toMatchObject({ ok: true });
    expect(consistent(draft.graph)).toEqual([]);
    const doc = docOf(draft, projection)!;
    const st = doc.statements.find((s) => s.name === statement)!;
    expect(st.steps[at].kind).toBe("Filter");
    expect(st.steps.map((s) => s.name)).toEqual(st.steps.map((_, i) => (i === st.steps.length - 1 ? statement : `${statement}#${i}`)));
    const removed = await perform(draft, projection, { g: "removeStep", id: st.steps[at].id });
    expect(removed).toMatchObject({ ok: true });
    expect(shape(draft.graph)).toEqual(before);
    expect(draft.log.length).toBe(2);
  });

  it.each(cases.filter(([p, s, at]) => at === statements(p).find((x) => x.name === s)!.steps.length))("%s: what read `%s` reads its new end (position %i)", async (projection, statement) => {
    const draft = memoryDraft(copy());
    const readers = docOf(draft, projection)!.statements.find((s) => s.name === statement)!.readers;
    await perform(draft, projection, { g: "addStep", kind: "Filter", statement });
    const doc = docOf(draft, projection)!;
    const end = doc.statements.find((s) => s.name === statement)!.steps.at(-1)!;
    expect(end.kind).toBe("Filter");
    expect(doc.statements.find((s) => s.name === statement)!.readers.sort()).toEqual([...readers].sort());
    for (const r of readers) expect(doc.statements.find((s) => s.name === r)!.steps.some((x) => x.inputs.some((i) => i.from === end.out))).toBe(true);
  });

  it("one gesture is one patch that names every element once", async () => {
    const c = await compose(base, palette, SVC, { g: "addStep", kind: "Filter", statement: statements(SVC)[0].name, at: 0 }, skeletonOf(palette));
    const ids = c.ok ? c.value.edits.map((e) => ("id" in e ? `${e.kind}:${e.id}` : "")) : [];
    expect(c.ok && new Set(ids).size).toBe(ids.length);
    expect(shape(base)).toEqual(shape(copy()));
  });
});

describe("moving a step", () => {
  const multi = [SVC, OPS].flatMap((p) => statements(p).filter((st) => st.steps.length > 1).map((st) => [p, st.name, st.steps.length] as [string, string, number]));
  it.each(multi)("%s: the first step of `%s` to the end and back leaves the draft as it was", async (projection, statement, n) => {
    const draft = memoryDraft(copy());
    const before = shape(draft.graph);
    const names = () => docOf(draft, projection)!.statements.find((s) => s.name === statement)!.steps;
    const kinds = names().map((s) => s.kind);
    expect(await perform(draft, projection, { g: "moveStep", id: names()[0].id, statement })).toMatchObject({ ok: true });
    expect(consistent(draft.graph)).toEqual([]);
    expect(names().map((s) => s.kind)).toEqual([...kinds.slice(1), kinds[0]]);
    expect(await perform(draft, projection, { g: "moveStep", id: names()[n - 1].id, statement, at: 0 })).toMatchObject({ ok: true });
    expect(shape(draft.graph)).toEqual(before);
  });
  it.each(multi)("%s: the last step of `%s` to a new statement and back keeps what is in it", async (projection, statement, n) => {
    const draft = memoryDraft(copy());
    const before = shape(draft.graph);
    const steps = (name: string) => docOf(draft, projection)!.statements.find((s) => s.name === name)?.steps ?? [];
    const last = steps(statement)[n - 1];
    const entries = last.slots.flatMap((s) => s.entries.map((e) => e.text));
    expect(await perform(draft, projection, { g: "moveStep", id: last.id, statement: "elsewhere" })).toMatchObject({ ok: true });
    expect(steps("elsewhere")[0].slots.flatMap((s) => s.entries.map((e) => e.text))).toEqual(entries);
    expect(steps(statement).length).toBe(n - 1);
    expect(await perform(draft, projection, { g: "moveStep", id: steps("elsewhere")[0].id, statement })).toMatchObject({ ok: true });
    expect(shape(draft.graph)).toEqual(before);
  });
});

describe("placeholders of a skeleton", () => {
  const P = SVC, rollup = `${P}/op:sums`;
  const slot = (draft: ReturnType<typeof memoryDraft>, id: string, name: string) => docOf(draft, P)!.statements.flatMap((s) => s.steps).find((s) => s.id === id)!.slots.find((s) => s.name === name)!;
  const start = async () => {
    const draft = memoryDraft(copy());
    expect(await perform(draft, P, { g: "addStep", kind: "Rollup", statement: "sums", from: { in: `${P}/op:depWindows/port:out` } })).toMatchObject({ ok: true });
    return draft;
  };
  const agg = (id: string, name: string, fn: string, src = "") => ({ g: "addEntry" as const, step: id, slot: "agg", childType: "Aggregate", values: { name, fn, src } });

  it("a required slot of a new step holds a placeholder, which is not one of its entries and is known to the draft", async () => {
    const draft = await start();
    expect(slot(draft, rollup, "agg")).toMatchObject({ entries: [], placeholders: [`${rollup}/agg:n`] });
    expect(slot(draft, rollup, "key")).toMatchObject({ entries: [], placeholders: [`${rollup}/key:x`] });
    expect([...draft.placeholders].sort()).toEqual([`${rollup}/agg:n`, `${rollup}/key:x`]);
  });

  it("the first real entry of the slot takes its place, and only its place", async () => {
    const draft = await start();
    expect(await perform(draft, P, agg(rollup, "total", "sum", "flows"))).toMatchObject({ ok: true });
    expect(slot(draft, rollup, "agg").entries.map((e) => e.text)).toEqual(["total = sum(flows)"]);
    expect(draft.graph.entity(`${rollup}/agg:n`)).toBeUndefined();
    expect([...draft.placeholders]).toEqual([`${rollup}/key:x`]);
  });

  it("an author's `n = count()` in place of the placeholder `n = count()` is the author's: the next aggregate leaves it", async () => {
    const draft = await start();
    expect(await perform(draft, P, agg(rollup, "n", "count"))).toMatchObject({ ok: true });
    expect(slot(draft, rollup, "agg")).toMatchObject({ placeholders: [] });
    expect(await perform(draft, P, agg(rollup, "total", "sum", "flows"))).toMatchObject({ ok: true });
    expect(slot(draft, rollup, "agg").entries.map((e) => e.text).sort()).toEqual(["n = count()", "total = sum(flows)"]);
  });

  it("an aggregate that only looks like a placeholder is never taken for one", async () => {
    const draft = memoryDraft(copy());
    // `backed` ends in a group of the catalog: nothing in it came from a skeleton of this draft
    const group = `${P}/op:backed`;
    expect(await perform(draft, P, agg(group, "n", "count"))).toMatchObject({ ok: true });
    expect(await perform(draft, P, agg(group, "m", "countDistinct", "p"))).toMatchObject({ ok: true });
    expect(slot(draft, group, "agg").entries.map((e) => e.key).sort()).toEqual(["m", "n", "pods"]);
  });

  it("a placeholder is known under its new id when its step is renamed with its row set", async () => {
    const draft = await start();
    expect(await perform(draft, P, { g: "addStep", kind: "Distinct", statement: "sums" })).toMatchObject({ ok: true });
    const moved = `${P}/op:sums#0`;
    expect([...draft.placeholders].sort()).toEqual([`${moved}/agg:n`, `${moved}/key:x`]);
    expect(await perform(draft, P, agg(moved, "total", "sum", "flows"))).toMatchObject({ ok: true });
    expect(slot(draft, moved, "agg").entries.map((e) => e.text)).toEqual(["total = sum(flows)"]);
    expect(await perform(draft, P, { g: "rename", statement: "sums", to: "totals" })).toMatchObject({ ok: true });
    expect([...draft.placeholders]).toEqual([`${P}/op:totals#0/key:x`]);
  });
});

describe("a new statement", () => {
  it("a match from picked elements has a pattern node per element and an edge per relationship", async () => {
    const draft = memoryDraft(copy());
    const doc0 = docOf(draft, SVC)!;
    const pod = "d:sattle.infra/entity:Pod", calls = "d:sattle.infra/rel:Calls";
    const r = await perform(draft, SVC, { g: "match", statement: "picked", graph: doc0.inputs.find((i) => i.kind === "graph")!.id, nodes: [{ variable: "a", type: pod }, { variable: "b", type: pod }], edges: [{ from: "a", to: "b", type: calls }] });
    expect(r).toMatchObject({ ok: true });
    const st = docOf(draft, SVC)!.statements.find((s) => s.name === "picked")!;
    const slots = Object.fromEntries(st.steps[0].slots.map((s) => [s.children[0], s.entries.length]));
    expect(slots).toMatchObject({ PatternNode: 2, PatternEdge: 1 });
    expect(st.steps[0].inputs.find((i) => i.kind === "graph")!.fromName).toBe(doc0.inputs.find((i) => i.kind === "graph")!.name);
    expect(draft.log.length).toBe(1);
  });
});

describe("the shape of a new entry", () => {
  const slots = palette.operators.flatMap((o) => o.slots.filter((s) => s.name !== "port").flatMap((s) => s.children.map((c) => [o.kind, s.name, c] as [string, string, string])));
  it.each(slots)("%s.%s: a %s is added by a shape, or is not added by hand", (kind, slot, child) => {
    const sh = entryShape(palette, kind, slot, child);
    if (child === "Operator" || child === "RowType" || child === "RowValue") expect(sh).toBeNull();
    else expect(sh!.fields.length).toBeGreaterThan(0);
  });
  it.each([
    ["Map", "assign", "Assign", { name: "n", src: "a.b + 1" }, "n = a.b + 1"],
    ["Group", "agg", "Aggregate", { name: "total", fn: "sum", src: "n" }, "total = sum(n)"],
    ["Filter", "predicate", "Expr", { src: "x == 1 and not y" }, "x == 1 and !y"],
  ] as [string, string, string, Record<string, string>, string][])("%s.%s: a %s entry reads back as `%s`", async (kind, slot, child, values, text) => {
    const draft = memoryDraft(copy());
    const added = await perform(draft, SVC, { g: "addStep", kind, statement: "fresh" });
    const id = added.ok ? added.select[0] : "";
    expect(await perform(draft, SVC, { g: "addEntry", step: id, slot, childType: child, values })).toMatchObject({ ok: true });
    const entries = docOf(draft, SVC)!.statements.find((s) => s.name === "fresh")!.steps[0].slots.find((s) => s.name === slot)!.entries;
    expect(entries.map((e) => e.text)).toEqual([text]);
  });
});
