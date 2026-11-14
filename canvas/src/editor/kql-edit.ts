// The editing module of note 11 (section 5): every visual edit of a projection is a list of text changes, computed by pure functions from a
// reading (the text and the engine's outline of exactly that text). One rule serves every list: an item owns the comment lines directly above
// it (its body), and a joint, the separator stretch that ties it to the item before it (the first item: to the item after it). Remove takes
// both along, add puts a body in with a joint copied from a neighbour, move carries them. No edit is written per kind of operator.
import type { Analysis, Place } from "./kql";

export interface Change { from: number; to: number; insert: string }
/** What an edit gives: the changes, and where the item it made or moved stands in the new text (to keep it selected). */
export interface Edit { changes: Change[]; select: Place | null }
/** A list the text separates: the statements, the operators of a pipeline, the items of a project, by, summarize, join keys, union legs, ... */
export interface List { id: string; sep: ";" | "|" | ","; items: Place[] }
export interface Carried { body: string; joint: string }

/** The joint a list uses when it has no second item to copy one from (the contract's table of separators). */
const DEFAULT_JOINT: Record<List["sep"], string> = { ";": ";\n", "|": "\n    | ", ",": ", " };

const placeOf = (x: unknown): Place | null => (Array.isArray(x) && x.length === 2 ? (x as Place) : x && typeof x === "object" && Array.isArray((x as { place?: unknown }).place) ? (x as { place: Place }).place : null);
const places = (xs: unknown): Place[] => (Array.isArray(xs) ? xs.map(placeOf).filter((p): p is Place => !!p) : []);

interface Operator { place: Place; parts?: Record<string, unknown>; inner?: Operator[][] }
interface Statement { place: Place; settings?: unknown[]; operators: Operator[] }

/** Every list of the outline, with a stable id (the path to it), in text order. */
export function listsOf(a: Analysis): List[] {
  const out: List[] = [];
  const push = (id: string, sep: List["sep"], items: Place[]) => { if (items.length) out.push({ id, sep, items: [...items].sort((x, y) => x[0] - y[0]) }); };
  const g = a.graph as { place?: Place; settings?: unknown[] } | undefined;
  const params = a.parameters as { place?: Place; items?: unknown[] } | undefined;
  const fns = (a.functions as { place: Place; parameters?: unknown[] }[] | undefined) ?? [];
  const stmts = (a.statements as Statement[] | undefined) ?? [];
  push("statements", ";", [g?.place, ...places(a.inputs), params?.place, ...fns.map((f) => f.place), ...stmts.map((s) => s.place)].filter((p): p is Place => !!p));
  push("graph.settings", ",", places(g?.settings));
  push("parameters", ",", places(params?.items));
  fns.forEach((f, i) => push(`functions.${i}.parameters`, ",", places(f.parameters)));
  const pipeline = (id: string, ops: Operator[]) => {
    push(id, "|", ops.map((o) => o.place));
    ops.forEach((o, i) => {
      for (const k of ["pattern", "project", "by", "aggregates", "on", "legs", "arguments"]) push(`${id}.${i}.${k}`, ",", places(o.parts?.[k]));
      (o.inner ?? []).forEach((p, j) => pipeline(`${id}.${i}.inner.${j}`, p));
    });
  };
  stmts.forEach((s, i) => { push(`statements.${i}.settings`, ",", places(s.settings)); pipeline(`statements.${i}.operators`, s.operators ?? []); });
  return out;
}

/** Where the comment lines directly above position `at` begin (blank lines between them included); `at` itself when there are none. */
export function bodyStart(text: string, at: number): number {
  let start = at;
  let p = at;
  while (p > 0 && (text[p - 1] === " " || text[p - 1] === "\t")) p--;
  if (p > 0 && text[p - 1] !== "\n") return start; // something stands before the item on its own line: no comment above it
  for (;;) {
    const lineEnd = p - 1; // the newline that ends the line above
    if (lineEnd < 0) return start;
    const lineStart = text.lastIndexOf("\n", lineEnd - 1) + 1;
    const line = text.slice(lineStart, lineEnd).trim();
    if (line.startsWith("//")) { start = lineStart; p = lineStart; continue; }
    if (line === "" && lineStart > 0 && text.slice(text.lastIndexOf("\n", lineStart - 2) + 1, lineStart - 1).trim().startsWith("//")) { p = lineStart; continue; }
    return start;
  }
}

/** The text an item owns (its comments and itself) and its joint. */
export function carried(text: string, list: List, i: number): Carried {
  const it = list.items;
  const body = text.slice(bodyStart(text, it[i][0]), it[i][1]);
  const joint = i > 0 ? text.slice(it[i - 1][1], bodyStart(text, it[i][0])) : it.length > 1 ? text.slice(it[0][1], bodyStart(text, it[1][0])) : "";
  return { body, joint };
}

/** Apply changes made on one text (non-overlapping; an insertion at the start of a deletion stays before it). */
export function apply(text: string, changes: Change[]): string {
  let out = text;
  for (const c of [...changes].sort((a, b) => b.from - a.from || b.to - a.to)) out = out.slice(0, c.from) + c.insert + out.slice(c.to);
  return out;
}

/**
 * Where a position of the old text stands after the changes. At an insertion point, `side` 1 (an item's start) goes after the inserted text,
 * -1 (an item's end) stays before it; a position inside a deletion goes to where the deletion was.
 */
export function mapPos(pos: number, changes: Change[], side: 1 | -1 = -1): number {
  let shift = 0;
  for (const c of [...changes].sort((a, b) => a.from - b.from || a.to - b.to)) {
    if (c.from > pos || (c.from === pos && c.to === pos && side < 0) || (c.from === pos && c.to > pos)) break;
    if (c.to <= pos) shift += c.insert.length - (c.to - c.from);
    else return c.from + shift;
  }
  return pos + shift;
}

/** Replace one or more places by new text (a rename sets every place that names the thing). */
export const set = (at: Place[], text: string): Edit => ({ changes: at.map(([from, to]) => ({ from, to, insert: text })), select: at.length === 1 ? [at[0][0], at[0][0] + text.length] : null });

/** Remove item i with what it owns: its comments, and its joint (the one before it; for the first item, the one after it). */
export function remove(text: string, list: List, i: number): Edit {
  const it = list.items;
  if (it.length === 1) return { changes: [{ from: bodyStart(text, it[0][0]), to: it[0][1], insert: "" }], select: null };
  const from = i > 0 ? it[i - 1][1] : bodyStart(text, it[0][0]);
  const to = i > 0 ? it[i][1] : bodyStart(text, it[1][0]);
  return { changes: [{ from, to, insert: "" }], select: null };
}

/** The joint to copy for a new item: the nearest existing one, else the list's default. */
const jointFor = (text: string, list: List, k: number) => (list.items.length > 1 ? carried(text, list, Math.max(1, Math.min(k, list.items.length - 1))).joint : DEFAULT_JOINT[list.sep]);
/** The place of the item text inside a body (after its comment lines). */
const itemIn = (body: string, at: number): Place => { const lead = body.length - body.replace(/^(?:[ \t]*\/\/[^\n]*\n|[ \t]*\n)*[ \t]*/, "").length; return [at + lead, at + body.length]; };

/** Add a body (item text, with comment lines above it if it has any) at position k of a list; `joint` defaults to a neighbour's. */
export function add(text: string, list: List, k: number, body: string, joint = jointFor(text, list, k)): Edit {
  const it = list.items;
  if (k > 0) { const p = it[k - 1][1]; return { changes: [{ from: p, to: p, insert: joint + body }], select: itemIn(body, p + joint.length) }; }
  const p = bodyStart(text, it[0][0]);
  return { changes: [{ from: p, to: p, insert: body + joint }], select: itemIn(body, p) };
}

/** Move item i to position k of the list without it (k = i puts it back). Its body and joint go with it. */
export function move(text: string, list: List, i: number, k: number): Edit {
  const { body, joint } = carried(text, list, i);
  const out = remove(text, list, i);
  // positions of the remaining items in the old text; the insertion is computed there, so both changes are on the old text
  const rest = list.items.filter((_, j) => j !== i);
  if (!rest.length) return { changes: [], select: list.items[i] };
  const ins = k > 0 ? { from: rest[k - 1][1], to: rest[k - 1][1], insert: joint + body } : { from: bodyStart(text, rest[0][0]), to: bodyStart(text, rest[0][0]), insert: body + joint };
  // the first item's comments stay where they are when an item is put before it (bodyStart of the old first item is after the removed span)
  if (k === 0 && i === 0) return { changes: [], select: list.items[0] };
  const changes = [...out.changes, ins];
  const start = mapPos(ins.from, out.changes) + (k > 0 ? joint.length : 0);
  return { changes, select: itemIn(body, start) };
}

/** The innermost item at a position of the text: its list and index. */
export function itemAt(lists: List[], pos: number): { list: List; index: number } | null {
  let best: { list: List; index: number; size: number } | null = null;
  for (const list of lists) list.items.forEach((p, index) => { if (p[0] <= pos && pos <= p[1] && (!best || p[1] - p[0] < best.size)) best = { list, index, size: p[1] - p[0] }; });
  return best ? { list: (best as { list: List }).list, index: (best as { index: number }).index } : null;
}

/**
 * The card each engine operator's rows go under, by the operator's id: an operator's `engine` ids and a call's `expands` to the operator, a
 * step's `sink` to its last `project` or `extend` when that has no operator of its own (`inSink`), else to the step. Card ids are the list ids
 * of `listsOf` with the item's index (`statements.3.operators.1`), or the step itself (`statements.3`).
 */
export function cardsOf(a: Analysis): Map<string, string> {
  const out = new Map<string, string>();
  type Op = { engine?: string[]; expands?: string[]; inSink?: boolean; inner?: Op[][] };
  const walk = (id: string, ops: Op[]) => ops.forEach((o, i) => {
    for (const e of [...(o.engine ?? []), ...(o.expands ?? [])]) out.set(e, `${id}.${i}`);
    (o.inner ?? []).forEach((p, j) => walk(`${id}.${i}.inner.${j}`, p));
  });
  ((a.statements as { sink?: string; operators?: Op[] }[] | undefined) ?? []).forEach((s, i) => {
    const ops = s.operators ?? [];
    walk(`statements.${i}.operators`, ops);
    const last = ops.findIndex((o) => o.inSink);
    if (s.sink) out.set(s.sink, last >= 0 ? `statements.${i}.operators.${last}` : `statements.${i}`);
  });
  return out;
}

// ---- new operators and absent parts (the contract's stage b, "absent parts") ----
export interface Column { name: string; type?: string | null; element?: string | null; telemetry?: boolean | null }
export const STARTER_KINDS = ["where", "project", "extend", "summarize", "distinct"] as const;
export type StarterKind = (typeof STARTER_KINDS)[number];

/** Text for a new operator after one with these columns, that reads without error: it names only columns that are there. */
export function starter(kind: StarterKind, columns: Column[]): string | null {
  const plain = columns.filter((c) => !c.element);
  const first = (plain.find((c) => c.type === "string") ?? plain[0] ?? columns[0])?.name;
  if (!first) return null;
  const all = columns.map((c) => c.name).join(", ");
  switch (kind) {
    case "where": return `where isnotempty(${first})`;
    case "project": return `project ${all}`;
    case "extend": return `extend ${first}_copy = ${first}`;
    case "summarize": return `summarize n = count() by ${first}`;
    case "distinct": return `distinct ${all}`;
  }
}

const insertAt = (at: number, insert: string, select: Place): Edit => ({ changes: [{ from: at, to: at, insert }], select });
interface OperatorParts { place: Place; parts?: { aggregates?: { place: Place }[]; by?: { place: Place }[]; pattern?: Place[]; where?: Place | null } }

/** ` by expr` after the last aggregate of a summarize without one (with one: the next item of its list, after the last). */
export function addBy(op: OperatorParts, expr: string): Edit | null {
  const by = op.parts?.by ?? [], aggs = op.parts?.aggregates ?? [];
  if (by.length) { const at = by[by.length - 1].place[1]; return insertAt(at, `, ${expr}`, [at + 2, at + 2 + expr.length]); }
  if (!aggs.length) return null;
  const at = aggs[aggs.length - 1].place[1];
  return insertAt(at, ` by ${expr}`, [at + 4, at + 4 + expr.length]);
}

/** ` where cond` after the last pattern of a graph-match without a condition. */
export function addWhere(op: OperatorParts, cond: string): Edit | null {
  const pats = op.parts?.pattern ?? [];
  if (op.parts?.where || !pats.length) return null;
  const at = pats[pats.length - 1][1];
  return insertAt(at, ` where ${cond}`, [at + 7, at + 7 + cond.length]);
}

/** A setting of a step: ` with (name = value)` at the end of its head when it has none, else the next item after the last. */
export function addSetting(step: { head?: Place | null; settings?: { place: Place }[] }, name: string, value: string): Edit | null {
  const item = `${name} = ${value}`;
  const s = step.settings ?? [];
  if (s.length) { const at = s[s.length - 1].place[1]; return insertAt(at, `, ${item}`, [at + 2, at + 2 + item.length]); }
  if (!step.head) return null;
  const at = step.head[1];
  return insertAt(at, ` with (${item})`, [at + 7, at + 7 + item.length]);
}

/** Where a new operator of a statement goes: at the end of a `let`; in a step, before its last operator, whose columns feed the step's sink. */
export function newOperatorAt(statement: { kind?: string; operators?: unknown[] }): number {
  const n = statement.operators?.length ?? 0;
  return statement.kind !== "let" && n > 1 ? n - 1 : n;
}
/** The new operators that fit at `newOperatorAt`: any kind, except as a step's last operator, where only a `where` keeps the sink's columns. */
export function startersFor(statement: { kind?: string; operators?: unknown[] }): StarterKind[] {
  const n = statement.operators?.length ?? 0;
  return statement.kind !== "let" && newOperatorAt(statement) === n ? ["where"] : [...STARTER_KINDS];
}
