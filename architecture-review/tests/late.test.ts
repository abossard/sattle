import { describe, expect, it } from "vitest";
import { LATE_SCRIPT, runLate, totalsInEventOrder, type Row } from "../src/sim/late";

const frames = runLate(LATE_SCRIPT.rows, LATE_SCRIPT.clocks);
const show = (changes: { row: { window: number; total: number }; weight: number }[]) => changes.map((change) => `${change.weight > 0 ? "+" : "-"}${change.row.window}=${change.row.total}`);
const arrivingAt = (arrivals: number[]): Row[] => LATE_SCRIPT.rows.map((row, index) => ({ ...row, arrival: arrivals[index] }));

describe("late simulator", () => {
  it.each([
    [0, [], [0, 60], []],
    [1, ["+0=3"], [0, 60], []],
    [2, ["-0=3", "+0=5"], [0], [0]],
    [3, ["+60=4"], [60, 120], []],
    [4, ["+120=1"], [120], []],
  ])("step %i sends %j, touches only %j and revises %j", (index, output, touched, revised) => {
    expect(show(frames[index].output)).toEqual(output);
    expect(frames[index].touched).toEqual(touched);
    expect(frames[index].revised).toEqual(revised);
  });

  it.each([
    [0, [false, false, false]],
    [2, [true]],
  ])("step %i marks late rows as %j", (index, late) => {
    expect(frames[index].arrivals.map((arrival) => arrival.late)).toEqual(late);
  });

  it.each([
    ["as scripted", LATE_SCRIPT.rows.map((row) => row.arrival)],
    ["in event order", [50, 20, 65, 100, 40, 130]],
    ["all late at the end", [230, 220, 210, 200, 190, 180]],
    ["reversed", [170, 150, 105, 68, 60, 55]],
  ])("arriving %s gives the same final totals as event order", (_, arrivals) => {
    const last = runLate(arrivingAt(arrivals), LATE_SCRIPT.clocks).at(-1)!;
    expect(last.shown).toEqual(totalsInEventOrder(LATE_SCRIPT.rows));
  });

  it("has one note per clock step", () => {
    expect(LATE_SCRIPT.notes.length).toBe(LATE_SCRIPT.clocks.length);
  });
});
