export interface Signals {
  impact: number;
  ambiguity: number;
  conflict: number;
  churn: number;
  evidenceGap: number;
}

export const WEIGHTS: Readonly<Record<keyof Signals, number>>;
export function scoreSignals(signals: Signals): number;
export function riskBand(score: number): "critical" | "high" | "medium" | "low";
