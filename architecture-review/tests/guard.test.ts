import { describe, expect, it } from "vitest";
import { GUARD_SCRIPT, findDomainWord, runGuard } from "../src/sim/guard";

describe("guard matcher", () => {
  it.each([
    ["var r = PodsReady;", "Pods"],
    ["var m = podsByNode;", "pods"],
    ["var p = nodePod;", "Pod"],
    ["var k = kubePod;", "Pod"],
    ["var n = nodePods;", "Pods"],
    ["app.MapGet(\"/Health\", Live);", "\"/Health"],
    ["app.MapGet(\"/HealthTree\", Live);", "\"/Health"],
    ["app.MapGet(\"/healthz\", Live);", "\"/health"],
  ])("catches %s", (line, word) => {
    expect(findDomainWord(line)).toBe(word);
  });

  it.each([
    ["var x = Podium + nodePodcast;"],
    ["var a = Podcast;"],
    ["app.MapGet(\"/health\", Liveness);"],
    ["var a = RecordAdapter;"],
  ])("lets %s pass", (line) => {
    expect(findDomainWord(line)).toBeUndefined();
  });
});

describe("guard simulator", () => {
  const frames = runGuard(GUARD_SCRIPT.sources);

  it.each([
    [0, "passes"], [1, "passes"], [2, "passes"], [3, "fails"], [4, "fails"], [5, "fails"],
  ])("frame %i has verdict %s", (index, verdict) => {
    expect(frames[index].verdict).toBe(verdict);
  });

  it("has one note per frame", () => {
    expect(GUARD_SCRIPT.notes.length).toBe(frames.length);
  });
});
