import { describe, expect, it } from "vitest";
import { FOLD_SCRIPT, POLICY, runFold, step, type Mode } from "../src/sim/fold";

const frames = runFold(FOLD_SCRIPT.batches, FOLD_SCRIPT.keys);
const show = (changes: { row: { key: string; mode: string }; weight: number }[]) => changes.map((change) => `${change.weight > 0 ? "+" : "-"}${change.row.key}=${change.row.mode}`);
const states = (index: number) => frames[index].after.map((state) => `${state.key}=${state.mode}/${state.run}`);

describe("fold simulator", () => {
  it.each([
    [0, [], ["api"], ["api=Healthy/1", "web=Healthy/0"]],
    [1, ["-api=Healthy", "+api=Unhealthy"], ["api"], ["api=Unhealthy/0", "web=Healthy/0"]],
    [2, [], ["web"], ["api=Unhealthy/0", "web=Healthy/0"]],
    [3, [], ["api"], ["api=Unhealthy/1", "web=Healthy/0"]],
    [4, [], ["api"], ["api=Unhealthy/2", "web=Healthy/0"]],
    [5, ["-api=Unhealthy", "+api=Healthy"], ["api"], ["api=Healthy/0", "web=Healthy/0"]],
  ])("window %i sends %j, touches only %j and leaves %j", (index, output, touched, after) => {
    expect(show(frames[index].output)).toEqual(output);
    expect(frames[index].touched).toEqual(touched);
    expect(states(index)).toEqual(after);
  });

  it.each<[Mode, number, number, Mode, number]>([
    ["Healthy", 0, 9.0, "Healthy", 1],
    ["Healthy", 1, 9.0, "Unhealthy", 0],
    ["Healthy", 1, 8.0, "Healthy", 0],
    ["Healthy", 1, 7.5, "Healthy", 0],
    ["Unhealthy", 0, 6.0, "Unhealthy", 1],
    ["Unhealthy", 2, 6.0, "Healthy", 0],
    ["Unhealthy", 2, 7.0, "Unhealthy", 0],
    ["Unhealthy", 1, 9.0, "Unhealthy", 0],
  ])("from %s with run %i, a window of %f gives %s with run %i", (mode, run, cpu, nextMode, nextRun) => {
    expect(step({ key: "api", mode, run }, cpu, POLICY)).toEqual({ key: "api", mode: nextMode, run: nextRun });
  });

  it("leaves the state of an untouched key as it was", () => {
    for (const frame of frames) {
      for (const state of frame.after.filter((s) => !frame.touched.includes(s.key))) {
        expect(frame.before.find((s) => s.key === state.key)).toEqual(state);
      }
    }
  });

  it("has one note per batch", () => {
    expect(FOLD_SCRIPT.notes.length).toBe(FOLD_SCRIPT.batches.length);
  });
});
