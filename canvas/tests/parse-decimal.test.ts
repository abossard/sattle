import { describe, expect, it } from "vitest";
import { parseDecimal } from "../src/ui/Filters";
describe("a number typed into a threshold", () => {
  it.each<[string | undefined, number | undefined]>([["0.7", 0.7], ["0,7", 0.7], [" 12 ", 12], ["-1,5", -1.5], ["", undefined], [undefined, undefined], ["abc", undefined], ["1,2,3", undefined], ["1e3", 1000]])("%j is %j", (typed, n) => expect(parseDecimal(typed)).toBe(n));
});
