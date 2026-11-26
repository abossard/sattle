export type AreaId = string;

export interface IssueRef { n: number; title: string; area: AreaId; url: string }
export interface CommitRef { sha: string; title: string; url: string; area: AreaId; lines: number }
export interface Evidence { label: string; url: string; kind: "issue" | "commit" | "file" | "comment"; ref: string }

export interface Area {
  id: AreaId;
  name: string;
  about: string;
  commits: number;
  added: number;
  deleted: number;
  folders: { path: string; lines: number; commits: number }[];
}

export interface Day {
  date: string;
  note: string;
  commits: (CommitRef & { date: string; added: number; deleted: number })[];
  closed: IssueRef[];
}

export interface Theme {
  id: string;
  area: AreaId;
  title: string;
  what: string;
  why: string;
  issues: IssueRef[];
  commits: CommitRef[];
  lines: number;
  shots: { src: string; caption: string; width: number; height: number }[];
}

export interface Finding {
  id: string;
  title: string;
  plain: string;
  cost: string;
  severity: "high" | "medium" | "low";
  verdict: "stop" | "keep" | "change";
  suggestion: string;
  evidence: Evidence[];
}

export interface Flow {
  title: string;
  steps: { id: string; title: string; text: string }[];
  loops: { from: string; to: string; title: string; evidence: Evidence }[];
}

export interface WeekReview {
  schemaVersion: 2;
  repository: string;
  revision: string;
  generatedAt: string;
  period: { from: string; to: string };
  summary: string;
  totals: { commits: number; added: number; deleted: number; issuesClosed: number };
  areas: Area[];
  days: Day[];
  themes: Theme[];
  waste: Finding[];
  overdone: Finding[];
  good: { title: string; plain: string }[];
  flow: Flow;
  tokens: Tokens;
}

export interface Tokens {
  method: string;
  weights: string;
  total: number;
  cacheRead: number;
  output: number;
  sessions: number;
  days: { date: string; input: number; cacheWrite: number; cacheRead: number; output: number; sessions: number; inputEquivalent: number }[];
  kinds: { id: string; name: string; sessions: number; own: number; subagents: number; output: number }[];
  issues: (IssueRef & { inputEquivalent: number; sessions: number })[];
  models: { name: string; inputEquivalent: number }[];
  longContext: { threshold: number; turns: number; share: number };
}

const isObject = (value: unknown): value is Record<string, unknown> => typeof value === "object" && value !== null;

export function parseReview(value: unknown): WeekReview {
  if (!isObject(value) || value.schemaVersion !== 2) throw new Error("Unsupported review schema");
  for (const key of ["areas", "days", "themes", "waste", "overdone", "good"]) {
    if (!Array.isArray(value[key])) throw new Error(`Review is missing ${key}`);
  }
  if (!isObject(value.flow) || !isObject(value.totals)) throw new Error("Review is missing flow or totals");
  return value as unknown as WeekReview;
}
