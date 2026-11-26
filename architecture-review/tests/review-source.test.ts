import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { parseReview } from "../src/model";

const root = resolve(import.meta.dirname, "..");
const review = parseReview(JSON.parse(readFileSync(resolve(root, "public/review.json"), "utf8")));
const repository = `https://github.com/${review.repository}/`;
const links = (value: unknown): string[] => typeof value === "string"
  ? (value.startsWith("http") ? [value] : [])
  : Array.isArray(value) ? value.flatMap(links) : value && typeof value === "object" ? Object.values(value).flatMap(links) : [];

describe("generated week review", () => {
  it("covers every area, every day and every commit of the week", () => {
    expect(review.areas.map((area) => area.id)).toEqual(["paper", "engine", "usecases", "canvas", "run"]);
    expect(review.days.reduce((sum, day) => sum + day.commits.length, 0)).toBe(review.totals.commits);
    expect(review.days.reduce((sum, day) => sum + day.closed.length, 0)).toBe(review.totals.issuesClosed);
  });

  it("links only into the repository", () => {
    expect(links(review).filter((url) => !url.startsWith(repository))).toEqual([]);
  });

  it.each([...review.waste, ...review.overdone])("gives $id evidence and a recommendation", (finding) => {
    expect(finding.evidence.length).toBeGreaterThan(0);
    expect(["stop", "keep", "change"]).toContain(finding.verdict);
    expect(finding.suggestion.length).toBeGreaterThan(0);
  });

  it.each(review.themes)("links $id to an issue", (theme) => {
    expect(theme.issues.length).toBeGreaterThan(0);
  });
});
