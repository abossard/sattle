// Simulator for "changes as +1 and -1": a map step keeps error rows, a reduce step counts them per service.
// Pure: a script of input batches in, one frame per batch out.

export interface Row { service: string; status: string }
export interface Change<T> { row: T; weight: 1 | -1 }
export interface Count { service: string; errors: number }

export interface Frame {
  input: Change<Row>[];
  mapped: Change<Row>[];
  touched: string[];
  before: Count[];
  after: Count[];
  output: Change<Count>[];
}

const keep = (row: Row) => row.status === "error";

export function runChanges(script: Change<Row>[][]): Frame[] {
  let state = new Map<string, number>();
  return script.map((input) => {
    const mapped = input.filter((change) => keep(change.row));
    const touched = [...new Set(mapped.map((change) => change.row.service))].sort();
    const next = new Map(state);
    for (const change of mapped) next.set(change.row.service, (next.get(change.row.service) ?? 0) + change.weight);
    const output: Change<Count>[] = touched.flatMap((service) => {
      const old = state.get(service) ?? 0;
      const now = next.get(service) ?? 0;
      if (old === now) return [];
      return [
        ...(old > 0 ? [{ row: { service, errors: old }, weight: -1 as const }] : []),
        ...(now > 0 ? [{ row: { service, errors: now }, weight: 1 as const }] : []),
      ];
    });
    for (const [service, errors] of next) if (errors === 0) next.delete(service);
    const table = (map: Map<string, number>) => [...map].sort().map(([service, errors]) => ({ service, errors }));
    const frame = { input, mapped, touched, before: table(state), after: table(next), output };
    state = next;
    return frame;
  });
}

const add = (service: string, status: string): Change<Row> => ({ row: { service, status }, weight: 1 });
const remove = (service: string, status: string): Change<Row> => ({ row: { service, status }, weight: -1 });

export const CHANGES_SCRIPT = {
  title: "Changes flow as rows with +1 and −1",
  lede: "The engine never recomputes a whole graph. Each input is a row with a weight: +1 adds it, −1 takes it back. A map step looks at one row at a time; a reduce step keeps a little state per key and sends on only what changed.",
  batches: [
    [add("api", "error"), add("api", "ok"), add("web", "error")],
    [add("api", "error")],
    [remove("api", "error")],
    [remove("web", "error")],
  ],
  notes: [
    "Three rows arrive. The map step drops the ok row. The reduce step starts a count for api and for web and sends both counts on.",
    "One more api error. Only the api count changes: the old count is taken back (−1) and the new one added (+1). web is not touched.",
    "An api error is withdrawn with −1. The count goes back from 2 to 1, again as one −1 and one +1.",
    "The last web error is withdrawn. Its count reaches 0, so only a −1 goes out and the key leaves the state.",
  ],
};
