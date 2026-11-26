import { describe, expect, it } from "vitest";
import { CUTOFF, DRAIN_SCRIPT, inOrderTotals, runDrain, type Row } from "../src/sim/drain";

const frames = runDrain(DRAIN_SCRIPT.batches);
const minutes = (windows: { minute: number }[]) => windows.map((window) => window.minute);
const show = (changes: { row: { minute: number; total: number }; weight: number }[]) => changes.map((change) => `${change.weight > 0 ? "+" : "-"}${change.row.minute}=${change.row.total}`);
const moves = (index: number) => frames[index].moves.map((move) => `${move.kind} ${move.minute}`);

describe("drain simulator", () => {
  it.each([
    [0, ["+0=4", "+1=2", "+2=5"], [], [0, 1, 2], [], 3],
    [1, ["+3=3"], ["put 0"], [1, 2, 3], [0], 3],
    [2, ["+4=6"], ["put 1"], [2, 3, 4], [0, 1], 3],
    [3, ["-0=4", "+0=6"], ["get 0", "put 0"], [2, 3, 4], [0, 1], 4],
    [4, ["+5=1"], ["put 2"], [3, 4, 5], [0, 1, 2], 3],
  ])("batch %i sends %j, moves %j, keeps %j in memory and %j in the drain, holds at most %i", (index, output, moved, memory, drain, peak) => {
    expect(show(frames[index].output)).toEqual(output);
    expect(moves(index)).toEqual(moved);
    expect(minutes(frames[index].memory)).toEqual(memory);
    expect(minutes(frames[index].drain)).toEqual(drain);
    expect(frames[index].peak).toBe(peak);
  });

  it("never holds more than the cutoff plus the one window brought back", () => {
    expect(Math.max(...frames.map((frame) => frame.peak))).toBe(CUTOFF + 1);
    expect(frames.every((frame) => frame.memory.length <= CUTOFF)).toBe(true);
  });

  const rows = DRAIN_SCRIPT.batches.flat();
  const ordered = [...rows].sort((a, b) => a.minute - b.minute);
  it.each([
    ["the script", DRAIN_SCRIPT.batches],
    ["in order", ordered.map((row) => [row])],
    ["reversed", [...ordered].reverse().map((row) => [row])],
    ["all late after the newest", [[ordered.at(-1)!], ordered.slice(0, -1)]],
  ] as [string, Row[][]][])("drops nothing when rows arrive %s", (_, script) => {
    const last = runDrain(script).at(-1)!;
    const kept = [...last.memory, ...last.drain].sort((a, b) => a.minute - b.minute);
    expect(kept).toEqual(inOrderTotals(rows));
  });

  it("has one note per batch", () => {
    expect(DRAIN_SCRIPT.notes.length).toBe(DRAIN_SCRIPT.batches.length);
  });
});
