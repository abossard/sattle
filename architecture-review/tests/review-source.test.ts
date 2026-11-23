import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { parseReview } from "../src/model";

const root = resolve(import.meta.dirname, "..");
const generated = JSON.parse(readFileSync(resolve(root, "public/review.json"), "utf8"));

describe("generated review evidence", () => {
  it("parses as the public review schema", () => {
    const review = parseReview(generated);
    expect(review.findings.length).toBeGreaterThanOrEqual(8);
    expect(review.modules.length).toBeGreaterThanOrEqual(6);
  });

  it.each(generated.findings)("keeps $id connected to evidence and a GitHub issue", (finding) => {
    expect(finding.evidence.length).toBeGreaterThan(0);
    expect(finding.issueNumbers.length).toBeGreaterThan(0);
    expect(finding.verification.length).toBeGreaterThan(0);
  });

  it("sorts findings by generated priority", () => {
    const scores = generated.findings.map((finding: { score: number }) => finding.score);
    expect(scores).toEqual([...scores].sort((left, right) => right - left));
  });
});
