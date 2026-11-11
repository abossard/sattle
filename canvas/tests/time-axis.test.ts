import { describe, expect, it } from "vitest";
import { countAtOrBefore, resolvePatchTimes, formatTime, frameIndexAt, mergeTimes, ordinalTimes, revisionCursorAt } from "../src/core/time-axis";

describe("resolvePatchTimes", () => {
  const W = [100, 160, 220, 280];
  it.each([
    [[NaN, NaN, NaN], [160, 220, 280], "ordinal"],
    [[10, 20, 30], [10, 20, 30], "source"],
    [[NaN, NaN, 500], [160, 220, 500], "mixed"],      // live patch with a time after history placed by order
    [[NaN, NaN, 200], [160, 200, 200], "mixed"],      // ordinal must not pass a time the source gave
    [[], [], "ordinal"],
  ])("%j -> %j (%s)", (src, times, source) => expect(resolvePatchTimes(src as number[], W)).toEqual({ times, source }));
});

describe("time axis", () => {
  it.each([[[], 5, 0], [[1, 2, 3], 0, 0], [[1, 2, 3], 2, 2], [[1, 2, 3], 2.5, 2], [[1, 2, 3], 99, 3], [[1, 1, 2], 1, 2]])("countAtOrBefore(%j, %f) = %i", (a, t, n) => expect(countAtOrBefore(a, t)).toBe(n));
  it("merges and sorts unique times from several graphs", () => expect(mergeTimes([[3, 1], [2, 3], []])).toEqual([1, 2, 3]));
  it.each([
    [0, [0, 60, 120], []],
    [3, [0, 60, 120, 180, 240], [60, 120, 180]],
    [4, [0, 60], [1, 2, 3, 4]],  // fewer windows than revisions: by order, not stacked on the last window
    [3, [], [1, 2, 3]],
  ])("ordinalTimes(%i, %j) places revisions at windows or by order", (n, windows, expected) => expect(ordinalTimes(n, windows)).toEqual(expected));
  it("a position on the axis selects revision and telemetry frame together", () => {
    const revisions = [10, 70, 130];       // commit times
    const windows = [0, 60, 120, 180];     // window starts
    for (const [t, rev, frame] of [[0, 0, 0], [9, 0, 0], [10, 1, 0], [65, 1, 1], [70, 2, 1], [125, 2, 2], [130, 3, 2], [500, 3, 3]]) {
      expect(revisionCursorAt(revisions, t)).toBe(rev);
      expect(frameIndexAt(windows, t)).toBe(frame);
    }
  });
  it.each([[5, "5 s"], [0.5, "0.5 s"], [90, "90 s"], [185, "3 min 5 s"], [7300, "2.03 h"]])("formatTime(%f) = %s", (t, s) => expect(formatTime(t)).toBe(s));
});
