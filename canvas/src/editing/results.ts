// What the engine answers about the rows after each operator of a previewed graph (engine 3.21, `include: ["rows"]`), as the rows
// after each step of a projection: the columns an author reads and some rows. A calculation over the answer.
// An element is never shown by its id: an element column is shown as the name the match read from it (`s.name`), when it read one.

import type { OperatorRows, OperatorSchema } from "../editor/service";

export type { OperatorRows };
/** The rows after a step: the columns, some rows, how many there are in all, and whether the sample is all of them. */
export interface StepRows { columns: string[]; rows: string[][]; total: number; complete: boolean }

const ELEMENT = new Set(["Entity", "Relationship"]);
/** How an element looks in a cell: an id the engine minted, never shown. */
const ELEMENT_ID = /^[a-z]{1,3}:[a-z0-9]{20,}$/;

/** What is known about the cells of an operator besides the answer: the kinds of its columns (from the draft's check) and the names of elements (from the data the workspace holds). */
export interface CellReading {
  kinds?: ReadonlyMap<string, string>;
  label?: (elementId: string) => string | undefined;
}

/** A point in time as an author reads it: the engine gives nanoseconds since 1970. */
const instant = (ns: string): string => { const ms = Number(BigInt(ns) / 1_000_000n); return Number.isFinite(ms) ? new Date(ms).toISOString().replace("T", " ").replace(/:00\.000Z$|\.000Z$/, "") : ns; };

/**
 * The rows after one operator as an author reads them: an element by its name (the `x.name` the match read, else the name the data
 * gives it, else "(element)"), never by its id; a point in time as a date; a name column of an element folded into the element's column.
 */
export function stepRowsOf(o: OperatorRows, reading: CellReading = {}): StepRows {
  const names = o.columns.map((c) => c.name);
  const kindOf = (c: string) => reading.kinds?.get(c) ?? o.columns.find((x) => x.name === c)?.kind ?? "";
  const isElement = (c: string) => ELEMENT.has(kindOf(c)) || (o.sample.length > 0 && o.sample.every((r) => !r.cells[c] || ELEMENT_ID.test(r.cells[c])) && o.sample.some((r) => r.cells[c]));
  const nameColumn = (c: string) => names.find((x) => x.startsWith(`${c}.`) && /name$/i.test(x));
  const folded = new Set(names.filter(isElement).map(nameColumn).filter((x): x is string => !!x));
  const shown = names.filter((c) => !folded.has(c));
  const cell = (cells: Record<string, string>, c: string): string => {
    const v = cells[c] ?? "";
    if (isElement(c)) { const n = nameColumn(c); return (n && cells[n]) || (v && reading.label?.(v)) || (v ? "(element)" : ""); }
    if (kindOf(c) === "Instant" && /^-?\d+$/.test(v)) return instant(v);
    return v;
  };
  return {
    columns: shown,
    rows: o.sample.map((r) => shown.map((c) => cell(r.cells, c))),
    total: Number(o.rows),
    complete: !o.truncated && o.sample.length === Number(o.rows),
  };
}

/** Whether the operator `op` is a part the engine planned for the step `step` (`.../op:dependsOn__edges` for `.../op:dependsOn`). */
const partOf = (op: string, step: string): boolean => op.startsWith(`${step}__`) || op.startsWith(`${step}/`);

/** The columns after each step, by step id, from the schemas of a draft check: `name: kind`, an element with its type's name. */
export function columnsByStep(schemas: readonly OperatorSchema[], steps: readonly string[], typeName: (id: string) => string): Map<string, string[]> {
  const out = new Map<string, string[]>();
  for (const step of steps) {
    const mine = schemas.filter((o) => o.op === step || partOf(o.op, step));
    const last = mine.find((o) => o.op === step) ?? mine[mine.length - 1];
    if (last) out.set(step, last.columns.map((c) => `${c.name}: ${c.type ? typeName(c.type) : c.kind}${c.required ? "" : "?"}${c.role === "telemetry" ? " (telemetry)" : ""}`));
  }
  return out;
}

/**
 * The rows after each step of a projection, by step id, from the operators of the previewed graphs. A step that the engine plans
 * as several operators (a lift, whose parts it names `dependsOn__edges`) is shown by the operator of its own id, else by the last of
 * its parts; an operator no step stands for is left out.
 */
export function rowsByStep(operators: readonly OperatorRows[], steps: readonly string[], reading: (op: string) => CellReading = () => ({})): Map<string, StepRows> {
  const out = new Map<string, StepRows>();
  for (const step of steps) {
    const mine = operators.filter((o) => o.op === step || partOf(o.op, step));
    const last = mine.find((o) => o.op === step) ?? mine[mine.length - 1];
    if (last) out.set(step, stepRowsOf(last, reading(step)));
  }
  return out;
}
