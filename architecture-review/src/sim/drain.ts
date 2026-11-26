// Simulator for "a cutoff and a drain": one key keeps its minute windows within a cutoff in memory,
// puts older windows into a drain and gets one back when a late row reaches it.
// Pure: a script of input batches in, one frame per batch out.

export interface Row { minute: number; calls: number }
export interface Window { minute: number; total: number }
export interface Change<T> { row: T; weight: 1 | -1 }
export interface Move { kind: "put" | "get"; minute: number }

export interface Frame {
  input: Row[];
  memoryBefore: Window[];
  drainBefore: Window[];
  memory: Window[];
  drain: Window[];
  /** Puts and gets in the order they happened. */
  moves: Move[];
  /** Windows a row reached. */
  touched: number[];
  output: Change<Window>[];
  /** The most windows memory held at any moment of the batch. */
  peak: number;
}

/** How many minute windows the key keeps in memory, counted back from its newest minute. */
export const CUTOFF = 3;

export const minuteLabel = (minute: number) => `10:${String(minute).padStart(2, "0")}`;

interface State { clock: number; memory: ReadonlyMap<number, number>; drain: ReadonlyMap<number, number> }

const table = (map: ReadonlyMap<number, number>): Window[] => [...map].sort(([a], [b]) => a - b).map(([minute, total]) => ({ minute, total }));

/** Every window older than the cutoff leaves memory and goes into the drain. */
function putPast(state: State): { state: State; moves: Move[] } {
  const past = [...state.memory.keys()].filter((minute) => minute <= state.clock - CUTOFF).sort((a, b) => a - b);
  const memory = new Map(state.memory);
  const drain = new Map(state.drain);
  for (const minute of past) {
    drain.set(minute, memory.get(minute)!);
    memory.delete(minute);
  }
  return { state: { ...state, memory, drain }, moves: past.map((minute) => ({ kind: "put", minute })) };
}

/** One row: move the clock, put what passed the cutoff, add the row to its window (getting it back if drained), put again. */
function apply(state: State, row: Row): { state: State; moves: Move[]; held: number } {
  const moved = putPast({ ...state, clock: Math.max(state.clock, row.minute) });
  const memory = new Map(moved.state.memory);
  const drain = new Map(moved.state.drain);
  const moves = [...moved.moves];
  if (!memory.has(row.minute) && drain.has(row.minute)) {
    memory.set(row.minute, drain.get(row.minute)!);
    drain.delete(row.minute);
    moves.push({ kind: "get", minute: row.minute });
  }
  memory.set(row.minute, (memory.get(row.minute) ?? 0) + row.calls);
  const back = putPast({ ...moved.state, memory, drain });
  return { state: back.state, moves: [...moves, ...back.moves], held: memory.size };
}

const totalOf = (state: State, minute: number) => state.memory.get(minute) ?? state.drain.get(minute);

export function runDrain(script: Row[][]): Frame[] {
  let state: State = { clock: -Infinity, memory: new Map(), drain: new Map() };
  return script.map((input) => {
    const before = state;
    let moves: Move[] = [];
    let peak = before.memory.size;
    for (const row of input) {
      const next = apply(state, row);
      state = next.state;
      moves = [...moves, ...next.moves];
      peak = Math.max(peak, next.held);
    }
    const touched = [...new Set(input.map((row) => row.minute))].sort((a, b) => a - b);
    const output: Change<Window>[] = touched.flatMap((minute) => {
      const old = totalOf(before, minute);
      const now = totalOf(state, minute)!;
      return [
        ...(old === undefined ? [] : [{ row: { minute, total: old }, weight: -1 as const }]),
        { row: { minute, total: now }, weight: 1 as const },
      ];
    });
    return {
      input, moves, touched, output, peak,
      memoryBefore: table(before.memory), drainBefore: table(before.drain),
      memory: table(state.memory), drain: table(state.drain),
    };
  });
}

/** The totals per minute if every row had arrived in order and nothing had ever left memory. */
export function inOrderTotals(rows: Row[]): Window[] {
  const totals = new Map<number, number>();
  for (const row of rows) totals.set(row.minute, (totals.get(row.minute) ?? 0) + row.calls);
  return table(totals);
}

const row = (minute: number, calls: number): Row => ({ minute, calls });

export const DRAIN_SCRIPT = {
  title: "A cutoff and a drain keep memory bounded without dropping a row",
  lede: "Per key, the evaluation keeps in memory only the windows within a cutoff. Older windows go into a drain, storage outside the engine (put). A late row that reaches a drained window gets it back (get), revises its total and puts it back, so nothing is dropped.",
  batches: [
    [row(0, 4), row(1, 2), row(2, 5)],
    [row(3, 3)],
    [row(4, 6)],
    [row(0, 2)],
    [row(5, 1)],
  ],
  notes: [
    "Rows for 10:00, 10:01 and 10:02 open three windows for api in memory and each total goes out with +1, while the drain is still empty.",
    "A row for 10:03 moves the clock, so 10:00 passes the cutoff and is put into the drain, and only the new window 10:03 sends output while 10:01 and 10:02 are not touched.",
    "A row for 10:04 puts 10:01 into the drain and opens 10:04, while 10:00 in the drain and 10:02 and 10:03 in memory are not touched.",
    "A late row for 10:00 gets that window back from the drain, revises its total from 4 to 6 (−1 old, +1 new) and puts it back, so memory held four windows for a moment and 10:01 in the drain was not touched.",
    "A row for 10:05 puts 10:02 into the drain and opens 10:05, and every total in memory and in the drain equals the total of all rows taken in order, so nothing was dropped.",
  ],
};
