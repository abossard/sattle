import { describe, expect, it } from "vitest";
import { pageWindow } from "../src/paging";

describe("bounded review rendering", () => {
  const findings = Array.from({ length: 1000 }, (_, index) => index);

  it.each([
    [1, 0, 24],
    [20, 475, 499],
    [40, 975, 999],
  ])("shows page %i without rendering the full queue", (page, first, last) => {
    const window = pageWindow(findings, page, 25);
    expect(window.items).toHaveLength(25);
    expect(window.items[0]).toBe(first);
    expect(window.items.at(-1)).toBe(last);
  });

  it("clamps out-of-range pages", () => {
    expect(pageWindow(findings, 99, 25).page).toBe(40);
    expect(pageWindow(findings, -4, 25).page).toBe(1);
  });
});
