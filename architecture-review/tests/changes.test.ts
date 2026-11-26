import { describe, expect, it } from "vitest";
import { CHANGES_SCRIPT, runChanges } from "../src/sim/changes";

const frames = runChanges(CHANGES_SCRIPT.batches);
const show = (changes: { row: { service: string; errors: number }; weight: number }[]) => changes.map((change) => `${change.weight > 0 ? "+" : "-"}${change.row.service}=${change.row.errors}`);

describe("changes simulator", () => {
  it.each([
    [0, ["+api=1", "+web=1"], ["api", "web"]],
    [1, ["-api=1", "+api=2"], ["api"]],
    [2, ["-api=2", "+api=1"], ["api"]],
    [3, ["-web=1"], ["web"]],
  ])("batch %i sends %j and touches only %j", (index, output, touched) => {
    expect(show(frames[index].output)).toEqual(output);
    expect(frames[index].touched).toEqual(touched);
  });

  it("drops rows the map step does not keep", () => {
    expect(frames[0].mapped.every((change) => change.row.status === "error")).toBe(true);
    expect(frames[0].input.length - frames[0].mapped.length).toBe(1);
  });

  it("removes a key whose count reaches zero", () => {
    expect(frames[3].after).toEqual([{ service: "api", errors: 1 }]);
  });

  it("has one note per batch", () => {
    expect(CHANGES_SCRIPT.notes.length).toBe(CHANGES_SCRIPT.batches.length);
  });
});
