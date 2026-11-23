const WEIGHTS = Object.freeze({
  impact: 30,
  ambiguity: 24,
  conflict: 18,
  churn: 16,
  evidenceGap: 12,
});

/**
 * @typedef {{ impact: number, ambiguity: number, conflict: number, churn: number, evidenceGap: number }} Signals
 */

/** @param {Signals} signals */
export function scoreSignals(signals) {
  const clamped = Object.fromEntries(Object.entries(signals).map(([key, value]) => [key, Math.max(0, Math.min(1, value))]));
  return Math.round(Object.entries(WEIGHTS).reduce((score, [key, weight]) => score + clamped[key] * weight, 0));
}

/** @param {number} score */
export function riskBand(score) {
  if (score >= 80) return "critical";
  if (score >= 62) return "high";
  if (score >= 42) return "medium";
  return "low";
}

export { WEIGHTS };
