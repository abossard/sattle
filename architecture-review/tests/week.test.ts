import { describe, expect, it } from "vitest";
import { areaOfPath, parseLog, plainSubject, textProblem } from "../scripts/week.mjs";

const areas = [
  { id: "paper", paths: ["paper/"] },
  { id: "engine", paths: ["engine/", "docs/gkql-conformance.md"] },
  { id: "run", paths: [] },
];

describe("week review calculations", () => {
  it.each([
    ["Paper Ch. 6 under #122 D36 and #182 D3: a late row is read like any other", "Paper: a late row is read like any other"],
    ["Engine: the fold (#151 A2) carries a seed; and more", "Engine: the fold carries a seed"],
    ["A graph's name is GraphId", "A graph's name is GraphId"],
    ["Paper Def. 6.7: a key is dropped only at a clock step", "Paper: a key is dropped only at a clock step"],
    ["Revert the series since-from-arrival change (G3)", "Revert the series since-from-arrival change"],
    ["Engine: the fold carries a seed at its look-back (paper §6.4): at the advance", "Engine: the fold carries a seed at its look-back: at the advance"],
  ])("turns %j into a plain label", (subject, expected) => {
    expect(plainSubject(subject)).toBe(expected);
  });

  it.each([
    ["paper/core/ch06-time.md", "paper"],
    ["engine/src/Sattle.Runtime/Engine.cs", "engine"],
    ["docs/gkql-conformance.md", "engine"],
    ["README.md", "run"],
  ])("puts %s in area %s", (path, area) => {
    expect(areaOfPath(areas, path)).toBe(area);
  });

  it.each([
    ["plain words", null],
    ["host 192.168.10.4", "an IP address"],
    ["mail a@b.io", "an e-mail address"],
    ["/subscriptions/x", "an Azure resource path"],
    ["Lemma 6.9 holds", "paper notation"],
  ])("finds %j as %s", (text, problem) => {
    expect(textProblem(text)).toBe(problem);
  });

  it("parses commits with their changed lines", () => {
    const log = "\x1eabc\x1f2026-11-24T10:00:00+01:00\x1fFirst\n\n3\t1\tpaper/a.md\n-\t-\tbin.png\n";
    expect(parseLog(log)).toEqual([{ sha: "abc", date: "2026-11-24T10:00:00+01:00", subject: "First", files: [{ path: "paper/a.md", added: 3, deleted: 1 }, { path: "bin.png", added: 0, deleted: 0 }] }]);
  });
});
