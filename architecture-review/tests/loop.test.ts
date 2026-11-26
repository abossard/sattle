import { describe, expect, it } from "vitest";
import { initial, LOOP_SCRIPT, runLoop, step, type Event } from "../src/sim/loop";

const frames = runLoop(LOOP_SCRIPT.script);

describe("loop simulator", () => {
  it.each([
    [0, "plan", null, ["open", "open", "open"], 0, 0, false],
    [1, "doing", 1, ["open", "open", "open"], 0, 1, false],
    [2, "review", 1, ["proof", "proof", "proof"], 0, 1, false],
    [3, "doing", 1, ["proof", "open", "proof"], 1, 1, false],
    [4, "doing", 2, ["proof", "open", "proof"], 1, 2, false],
    [5, "review", 2, ["proof", "proof", "proof"], 1, 2, false],
    [6, "done", 2, ["ticked", "ticked", "ticked"], 2, 2, true],
  ])("frame %i: stage %s, holder %s, criteria %j, rounds %i, sessions %i, closed %s", (index, stage, holder, criteria, rounds, sessions, closed) => {
    expect(frames[index].state).toEqual({ stage, holder, criteria, rounds, sessions, closed });
  });

  it.each<[string, Event]>([
    ["proof before a claim", { kind: "proof" }],
    ["a verdict before proof", { kind: "pass" }],
    ["a failed verdict before proof", { kind: "fail", criterion: 0 }],
    ["a handover with no holder", { kind: "handover" }],
  ])("ignores %s", (_name, event) => {
    expect(step(initial, event)).toBe(initial);
  });

  it("does not change its input state", () => {
    const before = structuredClone(frames[2].state);
    step(frames[2].state, { kind: "fail", criterion: 0 });
    expect(frames[2].state).toEqual(before);
  });

  it("has one note per frame", () => {
    expect(LOOP_SCRIPT.notes.length).toBe(frames.length);
  });
});
