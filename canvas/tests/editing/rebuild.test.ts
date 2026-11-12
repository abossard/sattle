// Every projection of the catalog built again from nothing, with the gestures the editors emit, and compared with the original by
// what it says (statements, steps, what each reads, every field, reference and entry), not by the ids it says it with.
import { describe, expect, it } from "vitest";
import { Catalog } from "../../src/core/catalog";
import { allSteps, readProjection, type EntryDoc, type ProjectionDoc, type SlotDoc, type StepDoc } from "../../src/editing/doc";
import { docOf, perform } from "../../src/editing/draft";
import type { Gesture } from "../../src/editing/gestures";
import type { Command } from "../../src/state/types";
import { estateCatalog, estatePalette as palette } from "./estate";
import { dangling, memoryDraft } from "./memory-draft";

const base = estateCatalog();
const cat = new Catalog(base);
const projections = cat.projections().map((p) => p.id).sort();

/** What a projection says, without the ids it says it with: one line per step, field, reference, read and slot. */
function reading(doc: ProjectionDoc): string[] {
  return doc.statements.flatMap((st) => st.steps.flatMap((s, i) => {
    const at = `${st.name}[${i}] ${s.kind}`;
    return [
      at, ...s.fields.filter((f) => f.value !== "").map((f) => `${at} ${f.name}=${f.value}`), ...s.refs.map((r) => `${at} ${r.rel}->${r.targetName}`), ...s.inputs.map((x) => `${at} ${x.port}<-${x.fromName}`),
      ...s.slots.flatMap((sl) => (sl.ordered ? sl.entries.map((e) => e.text) : sl.entries.map((e) => e.text).sort()).map((t) => `${at} ${sl.name}: ${t}`)),
    ];
  }));
}
const differing = (a: string[], b: string[]): string[] => [...a.filter((l) => !b.includes(l)).map((l) => `- ${l}`), ...b.filter((l) => !a.includes(l)).map((l) => `+ ${l}`)];

const value = (e: EntryDoc, slot?: string): string => (slot ? e.parts.find((p) => p.slot === slot) : e.parts[0])?.entries[0]?.expr ?? "";
const field = (e: EntryDoc, name: string): string => e.fields.find((f) => f.name === name)?.value ?? "";
const ref = (e: EntryDoc, rel: string) => e.refs.find((r) => r.rel === rel);

/** The command that adds the entry `e` to `slot` of `step`, or null when the editors have no gesture for an entry of its type. */
function entryCommand(step: StepDoc, slot: SlotDoc, e: EntryDoc): Command | null {
  const id = step.id;
  if (e.expr !== null) {
    if (slot.value?.kind === "fieldRef") return { t: "read", id, slot: slot.name, path: e.expr };
    return slot.multiplicity === "*" || slot.multiplicity === "+" ? { t: "exprAdd", id, slot: slot.name, src: e.expr } : { t: "expr", id, slot: slot.name, src: e.expr };
  }
  switch (e.type) {
    case "Assign": return { t: "named", id, slot: slot.name, childType: "Assign", name: e.key, src: value(e) };
    case "Aggregate": { const second = e.parts.filter((p) => p.slot !== "value" && p.entries[0]?.expr != null)[0]?.entries[0].expr; return { t: "named", id, slot: slot.name, childType: "Aggregate", name: e.key, src: value(e, "value"), extra: { fn: field(e, "fn"), ...(second ? { second } : {}) } }; }
    case "SetProperty": return ref(e, "property")?.target ? { t: "set", id, slot: slot.name, property: ref(e, "property")!.target!, src: value(e) } : null;
    case "PatternNode": return ref(e, "ofType")?.target ? { t: "patternNode", id, variable: e.key, type: ref(e, "ofType")!.target! } : null;
    case "PatternEdge": return { t: "patternEdge", id, variable: field(e, "var"), from: ref(e, "from")!.targetName, to: ref(e, "to")!.targetName, type: ref(e, "ofType")!.target! };
    default: return e.fields.some((f) => f.name === "target") && !e.parts.length && !e.refs.length ? { t: "child", id, slot: slot.name, childType: e.type, key: field(e, "target"), params: Object.fromEntries(e.fields.filter((f) => f.value !== "").map((f) => [f.name, f.value])) } : null;
  }
}

/** The gestures that build `doc` in an empty projection, in the order an author would: row set by row set, first its steps, then what is in them. `skipped` gets what has no gesture. */
function gesturesOf(doc: ProjectionDoc, skipped: string[]): { label: string; g: Gesture }[] {
  const out: { label: string; g: Gesture }[] = [];
  for (const st of doc.statements) {
    st.steps.forEach((s, i) => out.push({ label: `${st.name}: ${s.kind}`, g: { g: "addStep", kind: s.kind, statement: st.name, from: Object.fromEntries(s.inputs.filter((x) => x.from && !(i > 0 && x.inChain)).map((x) => [x.port, x.from!])) } }));
    for (const s of st.steps) {
      for (const f of s.fields) if (f.value !== "") out.push({ label: `${s.name} ${f.name}`, g: { g: "do", cmd: { t: "param", id: s.id, name: f.name, value: f.value } } });
      for (const r of s.refs) if (r.target) out.push({ label: `${s.name} ${r.rel}`, g: { g: "do", cmd: { t: "ref", id: s.id, rel: r.rel, target: r.target } } });
      for (const sl of s.slots) for (const e of sl.entries) {
        const cmd = entryCommand(s, sl, e);
        if (cmd) out.push({ label: `${s.name} ${sl.name} ${e.text}`, g: { g: "do", cmd } }); else skipped.push(`${s.kind}.${sl.name}: ${e.type}`);
      }
    }
  }
  return out;
}

describe.each(projections)("%s rebuilt from nothing", (id) => {
  it("reads as the original does", async () => {
    const original = readProjection(cat, palette, id)!;
    const draft = memoryDraft(estateCatalog(), palette);
    expect(await perform(draft, id, { g: "do", cmd: { t: "delete", ids: allSteps(original).map((s) => s.id) } })).toMatchObject({ ok: true });
    expect(docOf(draft, id)!.statements).toEqual([]);
    const skipped: string[] = [], refused: string[] = [];
    for (const { label, g } of gesturesOf(original, skipped)) { const r = await perform(draft, id, g); if (!r.ok) refused.push(`${label}: ${r.error}`); }
    expect(refused).toEqual([]);
    expect(dangling(draft.graph)).toEqual([]);
    const built = docOf(draft, id)!;
    if (skipped.length) console.log(`${id}: no gesture for ${[...new Set(skipped)].join(", ")}`);
    else expect(differing(reading(original), reading(built))).toEqual([]);
    expect(allSteps(built).map((s) => `${s.name} ${s.kind}`).sort()).toEqual(allSteps(original).map((s) => `${s.name} ${s.kind}`).sort());
  }, 120_000);
});
