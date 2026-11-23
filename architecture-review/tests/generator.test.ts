import { execFileSync, spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

const root = resolve(import.meta.dirname, "..");
const source = JSON.parse(readFileSync(resolve(root, "data/review-source.json"), "utf8"));

describe("review generation boundary", () => {
  it.each([
    ["unowned finding", (data: typeof source) => { data.findings[0].issueNumbers = []; }, "issue"],
    ["missing evidence", (data: typeof source) => { data.findings[0].evidence = []; }, "evidence"],
    ["stale evidence", (data: typeof source) => { data.findings[0].evidence[0].anchor = "not-a-real-code-anchor"; }, "anchor"],
    ["broken edge", (data: typeof source) => { data.edges[0].to = "missing"; }, "module"],
    ["duplicate finding", (data: typeof source) => { data.findings.push(data.findings[0]); }, "duplicate"],
    ["invalid signal", (data: typeof source) => { data.findings[0].signals.impact = 2; }, "signal"],
  ])("rejects %s before publishing", (_name, mutate, message) => {
    const directory = mkdtempSync(resolve(tmpdir(), "sattle-review-"));
    try {
      const data = structuredClone(source);
      mutate(data);
      const input = resolve(directory, "source.json");
      writeFileSync(input, JSON.stringify(data));
      const run = spawnSync(process.execPath, ["scripts/generate-review.mjs", "--source", input, "--output", resolve(directory, "review.json")], { cwd: root, encoding: "utf8" });
      expect(run.status).not.toBe(0);
      expect(run.stderr).toMatch(new RegExp(message, "i"));
    } finally {
      rmSync(directory, { recursive: true, force: true });
    }
  });

  it("pins every generated source line to its committed evidence anchor", () => {
    const review = JSON.parse(readFileSync(resolve(root, "public/review.json"), "utf8"));
    expect(review.revision).toMatch(/^[a-f0-9]{40}$/);
    for (const finding of review.findings) {
      for (const evidence of finding.evidence) {
        const content = execFileSync("git", ["show", `${review.revision}:${evidence.path}`], { cwd: root, encoding: "utf8" });
        expect(content.split("\n")[evidence.line - 1]).toContain(evidence.anchor);
      }
    }
  });
});
