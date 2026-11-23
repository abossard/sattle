import { describe, expect, it } from "vitest";
import { riskBand, scoreSignals } from "../shared/scoring.mjs";

describe("architecture priority score", () => {
  it.each([
    [{ impact: 1, ambiguity: 1, conflict: 1, churn: 1, evidenceGap: 1 }, 100, "critical"],
    [{ impact: 0.9, ambiguity: 0.8, conflict: 0, churn: 0.2, evidenceGap: 0.1 }, 51, "medium"],
    [{ impact: 0, ambiguity: 0, conflict: 0, churn: 0, evidenceGap: 0 }, 0, "low"],
  ])("scores normalized %o", (signals, expected, risk) => {
    const score = scoreSignals(signals);
    expect(score).toBe(expected);
    expect(riskBand(score)).toBe(risk);
  });

  it("prioritizes unresolved impact over settled low-impact churn", () => {
    const ambiguous = scoreSignals({ impact: 0.9, ambiguity: 1, conflict: 0.8, churn: 0.2, evidenceGap: 0.5 });
    const settled = scoreSignals({ impact: 0.3, ambiguity: 0.1, conflict: 0, churn: 1, evidenceGap: 0.1 });
    expect(ambiguous).toBeGreaterThan(settled);
  });
});
