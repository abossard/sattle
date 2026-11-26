import { describe, expect, it } from "vitest";
import { step, stepper, type StepEvent, type Stepper } from "../src/stepper";

const at = (value: number | null, playing = false): Stepper => ({ ...stepper(4), at: value, playing });

describe("stepper", () => {
  it.each<[string, Stepper, StepEvent, Stepper]>([
    ["next from all starts at the first step", at(null), "next", at(0)],
    ["next stops at the last step", at(3), "next", at(3)],
    ["back from all starts at the last step", at(null), "back", at(3)],
    ["back stops at the first step", at(0), "back", at(0)],
    ["play from all starts at the first step", at(null), "play", at(0, true)],
    ["play at the end starts again", at(3), "play", at(0, true)],
    ["play in the middle continues there", at(2), "play", at(2, true)],
    ["tick moves on while playing", at(1, true), "tick", at(2, true)],
    ["tick at the end stops playing", at(3, true), "tick", at(3, false)],
    ["tick while paused does nothing", at(1), "tick", at(1)],
    ["next while playing pauses", at(1, true), "next", at(2)],
    ["pause keeps the step", at(2, true), "pause", at(2)],
    ["all shows every step and stops", at(2, true), "all", at(null)],
  ])("%s", (_name, state, event, expected) => {
    expect(step(state, event)).toEqual(expected);
  });
});
