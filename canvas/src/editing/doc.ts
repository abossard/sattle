// The read model every editor shares: a projection of the catalog as statements (named row sets), their steps (operators),
// the slots of a step and the entries in them. Built from the definition graph and the palette alone: nothing here names an
// operator kind, so an operator the engine adds shows in every editor without a change.
import { Catalog, moduleOfId } from "../core/catalog";
import type { Rel } from "../core/model";
import { valueText } from "../core/model";
import type { EngineDiagnostic } from "../source/types";
import { durationText, isDuration } from "../editor/commands";
import { incompleteOperators } from "../editor/incomplete";
import type { Palette, PaletteParam, PaletteSlot, PaletteSlotValue } from "../editor/palette";
import { refsOf } from "../editor/refs";
import { EXPR_TYPES, exprText } from "./exprtext";

/** A parameter of a step or of an entry: what the palette says it is and what the draft holds. */
export interface FieldDoc { name: string; value: string; kind: string; required: boolean; values?: string[]; quantity?: string }
/** A relationship from a step or an entry to a definition elsewhere (the type an emit creates, the property a `set` writes). */
export interface RefDoc { rel: string; label: string; to: string; required: boolean; target: string | null; targetName: string }
/** One child of a slot. An expression is its text; anything else is a key, fields, references and nested parts. */
export interface EntryDoc {
  id: string; type: string; key: string;
  /** set when the entry is an expression */
  expr: string | null;
  fields: FieldDoc[]; refs: RefDoc[];
  parts: { slot: string; entries: EntryDoc[] }[];
  /** one line for a card, a block or a cell: `key = expr`, `(a: Type)` */
  text: string;
}
export interface SlotDoc { name: string; children: string[]; multiplicity: string; ordered: boolean; value?: PaletteSlotValue; entries: EntryDoc[] }
/** Where an input port of a step gets its rows or its graph from. */
export interface InputDoc {
  port: string; portId: string; kind: string;
  /** the port wired into it, or null while it is open */
  from: string | null;
  /** the statement or projection input that port belongs to */
  fromName: string;
  /** true when the source is the step before this one in the same statement */
  inChain: boolean;
}
export interface StepDoc {
  id: string; kind: string; name: string; role: string;
  fields: FieldDoc[]; refs: RefDoc[]; slots: SlotDoc[]; inputs: InputDoc[];
  /** the output port, or null for a sink */
  out: string | null;
  missing: string[]; diagnostics: EngineDiagnostic[];
}
/** A named row set: the steps of one `let`, or a sink with the name the engine gave it. */
export interface StatementDoc { name: string; steps: StepDoc[]; sink: boolean; readers: string[] }
export interface ProjectionInputDoc { id: string; name: string; kind: string; detail: string }
export interface ProjectionDoc {
  id: string; name: string; module: string;
  inputs: ProjectionInputDoc[];
  statements: StatementDoc[];
  /** diagnostics that name no step of this projection */
  diagnostics: EngineDiagnostic[];
}

const prop = (cat: Catalog, id: string, name: string): string => { const v = cat.entity(id)?.props[name]; return v ? valueText(v) : ""; };
/** `status#1` is piece 1 of the statement `status`; the piece without a number is its last step. */
export const statementOf = (opName: string): string => { const i = opName.indexOf("#"); return i < 0 ? opName : opName.slice(0, i); };
const pieceIndex = (opName: string): number => { const i = opName.indexOf("#"); return i < 0 ? Number.MAX_SAFE_INTEGER : Number(opName.slice(i + 1)); };
const ownerOfPort = (portId: string): string => portId.slice(0, portId.lastIndexOf("/port:"));
const natural = new Intl.Collator("en", { numeric: true });

function fields(cat: Catalog, id: string, params: PaletteParam[]): FieldDoc[] {
  return params.map((p) => {
    const raw = prop(cat, id, p.name);
    return { name: p.name, value: raw && isDuration(p) ? durationText(raw) : raw, kind: p.kind, required: p.required, values: p.values, quantity: p.quantity };
  });
}

/** Outgoing relationships by source, built once per read. */
type Out = ReadonlyMap<string, Rel[]>;
function outgoing(cat: Catalog): Out {
  const m = new Map<string, Rel[]>();
  for (const r of cat.g.relationships()) { const l = m.get(r.source); if (l) l.push(r); else m.set(r.source, [r]); }
  return m;
}

/** Relationships of an entry to definitions elsewhere: everything it points at that is not a child. */
function entryRefs(cat: Catalog, out: Out, id: string): RefDoc[] {
  return (out.get(id) ?? []).filter((r) => r.type !== "contains")
    .map((r): RefDoc => ({ rel: r.type, label: r.type, to: cat.typeOf(r.target), required: false, target: r.target, targetName: cat.name(r.target) }))
    .sort((a, b) => a.rel.localeCompare(b.rel));
}

function entry(cat: Catalog, out: Out, slot: PaletteSlot | undefined, id: string): EntryDoc {
  const type = cat.typeOf(id);
  if (EXPR_TYPES.has(type)) { const expr = exprText(cat, id); return { id, type, key: "", expr, fields: [], refs: [], parts: [], text: expr }; }
  const params = slot?.childTypes?.find((c) => c.type === type)?.params ?? [];
  const fs = fields(cat, id, params);
  const refs = entryRefs(cat, out, id);
  const bySlot = new Map<string, EntryDoc[]>();
  for (const child of cat.children(id)) {
    const link = (out.get(id) ?? []).find((r) => r.type === "contains" && r.target === child);
    const s = link?.props.slot ? valueText(link.props.slot) : "";
    bySlot.set(s, [...(bySlot.get(s) ?? []), entry(cat, out, undefined, child)]);
  }
  const parts = [...bySlot].map(([s, entries]) => ({ slot: s, entries })).sort((a, b) => a.slot.localeCompare(b.slot));
  // the key is what the author named the entry: its first parameter, or the definition it writes to
  const key = fs[0]?.value || refs[0]?.targetName || "";
  const rest = [...fs.slice(1).filter((f) => f.value).map((f) => f.value), ...refs.slice(fs[0]?.value ? 0 : 1).map((r) => r.targetName)];
  const values = parts.map((p) => p.entries.map((e) => e.text).join(", ")).filter(Boolean);
  const head = [key, ...rest].filter(Boolean).join(" ");
  const to = (rel: string): string => refs.find((r) => r.rel === rel)?.targetName || "…";
  // the entries the text language has a spelling for read as it spells them; any other as its key and its values
  const text = type === "PatternNode" ? `(${key}: ${to("ofType")})`
    : type === "PatternEdge" ? `(${to("from")})-[${fs[0]?.value ? `${fs[0].value}: ` : ":"}${to("ofType")}]->(${to("to")})`
    : type === "Aggregate" ? `${key} = ${fs.find((f) => f.name === "fn")?.value ?? "…"}(${parts.flatMap((p) => p.entries.map((e) => e.text)).join(", ")})`
    : values.length ? `${head}${head ? " = " : ""}${values.join("; ")}` : head;
  return { id, type, key, expr: null, fields: fs, refs, parts, text };
}

function step(cat: Catalog, out: Out, palette: Palette, id: string, missing: string[], diagnostics: EngineDiagnostic[]): StepDoc {
  const kind = cat.typeOf(id);
  const spec = palette.operators.find((o) => o.kind === kind);
  const name = cat.name(id);
  const ports = cat.children(id, "port").map((p) => ({ id: p, name: prop(cat, p, "name"), dir: prop(cat, p, "direction"), kind: prop(cat, p, "kind") }));
  const inputs = ports.filter((p) => p.dir === "in").map((p): InputDoc => {
    const from = cat.sources(p.id, "wire")[0] ?? null;
    const owner = from ? ownerOfPort(from) : "";
    const ownerName = owner ? cat.name(owner) : "";
    const sameStatement = !!owner && cat.typeOf(owner) !== "Projection" && statementOf(ownerName) === statementOf(name);
    return { port: p.name, portId: p.id, kind: p.kind, from, fromName: from ? (cat.typeOf(owner) === "Projection" ? prop(cat, from, "name") : statementOf(ownerName)) : "", inChain: sameStatement };
  });
  const slots = (spec?.slots ?? []).filter((s) => s.name !== "port").map((s): SlotDoc => ({
    name: s.name, children: s.children, multiplicity: s.multiplicity, ordered: s.ordered, value: s.value,
    entries: cat.children(id, s.name).map((c) => entry(cat, out, s, c)),
  }));
  const refs = refsOf(kind).map((r): RefDoc => { const target = cat.targets(id, r.rel)[0] ?? null; return { ...r, target, targetName: target ? cat.typeLabel(target.includes("/port:") ? ownerOfPort(target) : target, moduleOfId(id)) : "" }; });
  return {
    id, kind, name, role: spec?.role ?? "", fields: fields(cat, id, (spec?.params ?? []).filter((p) => p.name !== "name")), refs, slots, inputs,
    out: ports.find((p) => p.dir === "out")?.id ?? null, missing, diagnostics,
  };
}

/** The projection `id` as statements in the order an author reads them: every statement after the ones it reads, sinks last. */
export function readProjection(cat: Catalog, palette: Palette, id: string, diagnostics: EngineDiagnostic[] = []): ProjectionDoc | null {
  const p = cat.entity(id);
  if (!p) return null;
  const ops = cat.children(id, "op");
  const missing = new Map(incompleteOperators(cat.g, palette, id).map((m) => [m.op, m.missing]));
  const mine = (opId: string) => diagnostics.filter((d) => d.focus === opId || d.focus.startsWith(`${opId}/`));
  const out = outgoing(cat);
  const steps = ops.map((o) => step(cat, out, palette, o, missing.get(o) ?? [], mine(o)));
  const groups = new Map<string, StepDoc[]>();
  for (const s of steps) { const k = statementOf(s.name); groups.set(k, [...(groups.get(k) ?? []), s]); }
  const statements = [...groups].map(([name, ss]): StatementDoc => {
    const sorted = [...ss].sort((a, b) => pieceIndex(a.name) - pieceIndex(b.name));
    return { name, steps: sorted, sink: sorted[sorted.length - 1].out === null, readers: [] };
  });
  const reads = (s: StatementDoc) => new Set(s.steps.flatMap((x) => x.inputs).filter((i) => i.from && !i.inChain && groups.has(i.fromName)).map((i) => i.fromName));
  for (const s of statements) for (const r of reads(s)) statements.find((x) => x.name === r)?.readers.push(s.name);
  // order: a statement follows what it reads; among the ready ones row sets before sinks, then by name
  const done = new Set<string>(), ordered: StatementDoc[] = [];
  const rest = [...statements].sort((a, b) => Number(a.sink) - Number(b.sink) || natural.compare(a.name, b.name));
  while (rest.length) {
    const i = Math.max(0, rest.findIndex((s) => [...reads(s)].every((r) => done.has(r) || r === s.name)));
    const [s] = rest.splice(i, 1);
    done.add(s.name); ordered.push(s);
  }
  const inputs = cat.children(id, "port").map((port): ProjectionInputDoc => {
    const kind = prop(cat, port, "kind");
    const what = cat.targets(port, kind === "param" ? "valueKind" : kind === "rows" ? "rowType" : "expects")[0];
    return { id: port, name: prop(cat, port, "name"), kind, detail: what ? cat.name(what) : "" };
  });
  const inSteps = new Set(steps.flatMap((s) => s.diagnostics));
  return { id, name: cat.name(id), module: moduleOfId(id), inputs, statements: ordered, diagnostics: diagnostics.filter((d) => !inSteps.has(d) && (d.focus === id || d.focus.startsWith(`${id}/`))) };
}

/** The step that `id` is or is part of. */
export const stepHolding = (doc: ProjectionDoc, id: string | null): StepDoc | undefined => (id ? allSteps(doc).find((x) => id === x.id || id.startsWith(`${x.id}/`)) : undefined);
export const allSteps = (doc: ProjectionDoc): StepDoc[] => doc.statements.flatMap((s) => s.steps);
