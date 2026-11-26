// Simulator for "a late row revises the windows it reaches": one-minute windows of request errors for api,
// each total first shown once the clock passes the end of its window plus the lateness.
// Pure: rows with event and arrival times plus the clock steps in, one frame per clock step out.

/** Seconds after 10:00. */
export type Seconds = number;

export interface Row { event: Seconds; arrival: Seconds; errors: number }
export interface Total { window: Seconds; total: number }
export interface Change<T> { row: T; weight: 1 | -1 }
export interface WindowState { window: Seconds; total: number; shown: boolean }

export interface Frame {
  clock: Seconds;
  arrivals: { row: Row; late: boolean }[];
  windows: WindowState[];
  /** Windows a row or the clock reached in this step. */
  touched: Seconds[];
  /** Windows already shown that a row of this step changed. */
  revised: Seconds[];
  output: Change<Total>[];
  /** Every total shown after this step. */
  shown: Total[];
}

export const WINDOW = 60;
export const LATENESS = 60;

export const windowOf = (event: Seconds) => Math.floor(event / WINDOW) * WINDOW;
export const isLate = (row: Row) => row.arrival - row.event > LATENESS;
const shownBy = (window: Seconds, clock: Seconds) => clock >= window + WINDOW + LATENESS;
const byTime = (a: number, b: number) => a - b;
const table = (map: Map<Seconds, number>): Total[] => [...map].sort(([a], [b]) => a - b).map(([window, total]) => ({ window, total }));

export function runLate(rows: readonly Row[], clocks: readonly Seconds[]): Frame[] {
  let totals = new Map<Seconds, number>();
  let shown = new Map<Seconds, number>();
  let previous = -Infinity;
  return clocks.map((clock) => {
    const arrivals = rows
      .filter((row) => row.arrival > previous && row.arrival <= clock)
      .sort((a, b) => a.arrival - b.arrival)
      .map((row) => ({ row, late: isLate(row) }));
    const nextTotals = new Map(totals);
    for (const { row } of arrivals) nextTotals.set(windowOf(row.event), (nextTotals.get(windowOf(row.event)) ?? 0) + row.errors);
    const nextShown = new Map([...nextTotals].filter(([window]) => shownBy(window, clock)));
    const reached = new Set(arrivals.map(({ row }) => windowOf(row.event)));
    const touched = [...nextTotals.keys()].filter((window) => reached.has(window) || (nextShown.has(window) && !shown.has(window))).sort(byTime);
    const output: Change<Total>[] = touched.flatMap((window) => {
      const old = shown.get(window);
      const now = nextShown.get(window);
      if (old === now) return [];
      return [
        ...(old !== undefined ? [{ row: { window, total: old }, weight: -1 as const }] : []),
        ...(now !== undefined ? [{ row: { window, total: now }, weight: 1 as const }] : []),
      ];
    });
    const frame: Frame = {
      clock,
      arrivals,
      windows: table(nextTotals).map(({ window, total }) => ({ window, total, shown: nextShown.has(window) })),
      touched,
      revised: touched.filter((window) => shown.has(window) && reached.has(window)),
      output,
      shown: table(nextShown),
    };
    totals = nextTotals;
    shown = nextShown;
    previous = clock;
    return frame;
  });
}

/** The totals the same rows give when read in event order, all at once. */
export function totalsInEventOrder(rows: readonly Row[]): Total[] {
  const totals = new Map<Seconds, number>();
  for (const row of [...rows].sort((a, b) => a.event - b.event)) totals.set(windowOf(row.event), (totals.get(windowOf(row.event)) ?? 0) + row.errors);
  return table(totals);
}

const two = (value: number) => String(value).padStart(2, "0");
/** 10:00 plus the given minutes, as "10:01". */
export const minute = (at: Seconds) => `10:${two(Math.floor(at / 60))}`;
/** 10:00 plus the given seconds, as "10:01:20". */
export const clockTime = (at: Seconds) => `${minute(at)}:${two(at % 60)}`;

const row = (event: Seconds, arrival: Seconds, errors: number): Row => ({ event, arrival, errors });

export const LATE_SCRIPT = {
  title: "A late row revises the windows it reaches",
  lede: "Every row carries the time it happened and the time it arrived, and the two orders can differ. A one-minute window first shows its total once the clock passes its end plus the declared lateness, here one minute. A row that arrives after that is not dropped: it takes the shown total back with −1 and adds the new one with +1, and windows it does not reach stay as they are.",
  rows: [
    row(50, 55, 1),
    row(20, 60, 2),
    row(65, 68, 3),
    row(100, 105, 1),
    row(40, 150, 2),
    row(130, 170, 1),
  ],
  clocks: [70, 120, 150, 180, 240],
  notes: [
    "Three rows arrive, the 10:00:20 row after the 10:00:50 row, and the 10:00 and 10:01 totals grow, but nothing is shown yet because neither window has passed its end plus one minute.",
    "At 10:02:00 the clock passes the end of the 10:00 window plus one minute, so its total of 3 is shown with +1, while the 10:01 window takes a new row and stays held.",
    "A row from 10:00:40 arrives late at 10:02:30 and revises the shown 10:00 total with −1 for 3 and +1 for 5, while the 10:01 window is not touched.",
    "At 10:03:00 the 10:01 total of 4 is shown with +1 and a new row starts the 10:02 window, while the 10:00 total is not touched.",
    "At 10:04:00 the 10:02 total of 1 is shown, and the shown totals 5, 4 and 1 equal what the same rows give in event order, so nothing was lost by arriving late.",
  ],
};
