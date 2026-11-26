import { spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

const root = resolve(import.meta.dirname, "..");
const source = JSON.parse(readFileSync(resolve(root, "data/week-source.json"), "utf8"));

describe("week review generation boundary", () => {
  it.each([
    ["a theme without an issue", (data: typeof source) => { data.themes[0].issues = []; }, "issue"],
    ["an issue without a title", (data: typeof source) => { data.themes[0].issues.push(99999); }, "no title"],
    ["a commit that does not exist", (data: typeof source) => { data.themes[0].commits.push("0000000"); }, "resolve"],
    ["a commit outside the week", (data: typeof source) => { data.themes[0].commits.push("4a9a57d~1"); }, "outside the period"],
    ["a finding without evidence", (data: typeof source) => { data.waste[0].evidence = []; }, "evidence"],
    ["a finding without a verdict", (data: typeof source) => { data.waste[0].verdict = "maybe"; }, "verdict"],
    ["a finding without a recommendation", (data: typeof source) => { data.overdone[0].suggestion = ""; }, "recommendation"],
    ["evidence outside the repository", (data: typeof source) => { data.waste[0].evidence.push({ label: "x", url: "https://example.com/" }); }, "point into"],
    ["a missing evidence file", (data: typeof source) => { data.overdone[1].evidence.push({ file: "docs/missing.md" }); }, "missing"],
    ["a flow loop to a missing step", (data: typeof source) => { data.flow.loops[0].to = "nowhere"; }, "missing step"],
    ["a duplicate item id", (data: typeof source) => { data.overdone.push(data.waste[0]); }, "duplicate"],
    ["an Azure id in text", (data: typeof source) => { data.summary += " 0f8fad5b-d9cb-469f-a165-70867728950e"; }, "azure id"],
    ["an IP address in text", (data: typeof source) => { data.waste[0].plain += " 10.1.2.3"; }, "ip address"],
    ["an e-mail address in text", (data: typeof source) => { data.good[0].plain += " someone@example.org"; }, "e-mail"],
    ["a local path in text", (data: typeof source) => { data.themes[0].why += " /Users/someone/x"; }, "local path"],
    ["paper notation in text", (data: typeof source) => { data.themes[0].what += " see Thm. 6.3"; }, "paper notation"],
  ])("rejects %s before publishing", (_name, mutate, message) => {
    const directory = mkdtempSync(resolve(tmpdir(), "sattle-week-"));
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
});
