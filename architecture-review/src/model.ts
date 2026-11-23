import { riskBand, scoreSignals, type Signals } from "../shared/scoring.mjs";

export type RiskBand = ReturnType<typeof riskBand>;
export type FindingKind = "ambiguity" | "conflict" | "gap" | "misnomer" | "leak" | "churn";
export type DecisionOption = { id: string; title: string; detail?: string };

export interface Evidence {
  path: string;
  line: number;
  detail: string;
  anchor: string;
}

export interface Discussion {
  issue: { number: number; title: string; state: "open" | "closed"; body: string; url: string };
  totalComments: number;
  comments: { id: number; body: string; author: string; url: string; createdAt: string }[];
}

export interface Finding {
  id: string;
  title: string;
  summary: string;
  principle: string;
  kinds: FindingKind[];
  signals: Signals;
  ownerFiles: string[];
  evidence: Evidence[];
  issueNumbers: number[];
  question: string;
  options: DecisionOption[];
  recommendation: string;
  verification: string[];
  score: number;
  risk: RiskBand;
  history: {
    commits: number;
    lastChangedAt: string | null;
  };
}

export interface ModuleNode {
  id: string;
  title: string;
  role: string;
  path: string;
  x: number;
  y: number;
  issueNumbers: number[];
}

export interface ArchitectureEdge {
  from: string;
  to: string;
  label: string;
  kind: "data" | "definition" | "commit" | "feedback";
}

export interface Metric {
  label: string;
  value: string;
  detail: string;
  status: "strong" | "watch" | "open" | "context";
  issueNumbers: number[];
}

export interface ReviewDocument {
  schemaVersion: 1;
  repository: string;
  generatedAt: string;
  revision: string;
  principles: string[];
  modules: ModuleNode[];
  edges: ArchitectureEdge[];
  metrics: Metric[];
  findings: Finding[];
}

export interface LiveIssue {
  number: number;
  title: string;
  state: "open" | "closed";
  url: string;
  labels: string[];
  updatedAt: string;
}

export interface RuntimeConfig {
  repository: string;
  repositoryUrl: string;
  feedbackEnabled: boolean;
}

function object(value: unknown, name: string): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error(`${name} must be an object`);
  return value as Record<string, unknown>;
}

function text(value: unknown, name: string): string {
  if (typeof value !== "string" || value.trim() === "") throw new Error(`${name} must be non-empty text`);
  return value;
}

function list<T>(value: unknown, name: string, read: (item: unknown, index: number) => T): T[] {
  if (!Array.isArray(value)) throw new Error(`${name} must be an array`);
  return value.map(read);
}

function numbers(value: unknown, name: string): number[] {
  return list(value, name, (item) => {
    if (typeof item !== "number" || !Number.isInteger(item) || item < 1) throw new Error(`${name} must contain positive integers`);
    return item;
  });
}

function parseSignals(value: unknown, name: string): Signals {
  const raw = object(value, name);
  const read = (key: keyof Signals): number => {
    const item = raw[key];
    if (typeof item !== "number" || item < 0 || item > 1) throw new Error(`${name}.${key} must be between 0 and 1`);
    return item;
  };
  return {
    impact: read("impact"),
    ambiguity: read("ambiguity"),
    conflict: read("conflict"),
    churn: read("churn"),
    evidenceGap: read("evidenceGap"),
  };
}

export function parseReview(value: unknown): ReviewDocument {
  const raw = object(value, "review");
  if (raw.schemaVersion !== 1) throw new Error("review.schemaVersion must be 1");
  const repository = text(raw.repository, "review.repository");
  const generatedAt = text(raw.generatedAt, "review.generatedAt");
  const revision = text(raw.revision, "review.revision");
  const principles = list(raw.principles, "review.principles", (item, index) => text(item, `review.principles[${index}]`));
  const modules = list(raw.modules, "review.modules", (item, index) => {
    const m = object(item, `review.modules[${index}]`);
    return {
      id: text(m.id, `review.modules[${index}].id`),
      title: text(m.title, `review.modules[${index}].title`),
      role: text(m.role, `review.modules[${index}].role`),
      path: text(m.path, `review.modules[${index}].path`),
      x: typeof m.x === "number" ? m.x : 0,
      y: typeof m.y === "number" ? m.y : 0,
      issueNumbers: numbers(m.issueNumbers, `review.modules[${index}].issueNumbers`),
    };
  });
  const edges: ArchitectureEdge[] = list(raw.edges, "review.edges", (item, index) => {
    const e = object(item, `review.edges[${index}]`);
    const kind = e.kind;
    if (kind !== "data" && kind !== "definition" && kind !== "commit" && kind !== "feedback") throw new Error(`review.edges[${index}].kind is invalid`);
    return {
      from: text(e.from, `review.edges[${index}].from`),
      to: text(e.to, `review.edges[${index}].to`),
      label: text(e.label, `review.edges[${index}].label`),
      kind,
    };
  });
  const metrics: Metric[] = list(raw.metrics, "review.metrics", (item, index) => {
    const m = object(item, `review.metrics[${index}]`);
    const status = m.status;
    if (status !== "strong" && status !== "watch" && status !== "open" && status !== "context") throw new Error(`review.metrics[${index}].status is invalid`);
    return {
      label: text(m.label, `review.metrics[${index}].label`),
      value: text(m.value, `review.metrics[${index}].value`),
      detail: text(m.detail, `review.metrics[${index}].detail`),
      status,
      issueNumbers: numbers(m.issueNumbers, `review.metrics[${index}].issueNumbers`),
    };
  });
  const findings = list(raw.findings, "review.findings", (item, index) => {
    const f = object(item, `review.findings[${index}]`);
    const signals = parseSignals(f.signals, `review.findings[${index}].signals`);
    const score = typeof f.score === "number" ? f.score : scoreSignals(signals);
    const history = object(f.history, `review.findings[${index}].history`);
    const options = list(f.options, `review.findings[${index}].options`, (option, optionIndex) => {
      const o = object(option, `review.findings[${index}].options[${optionIndex}]`);
      return {
        id: text(o.id, `review.findings[${index}].options[${optionIndex}].id`),
        title: text(o.title, `review.findings[${index}].options[${optionIndex}].title`),
        detail: typeof o.detail === "string" ? o.detail : undefined,
      };
    });
    const evidence = list(f.evidence, `review.findings[${index}].evidence`, (entry, evidenceIndex) => {
      const e = object(entry, `review.findings[${index}].evidence[${evidenceIndex}]`);
      return {
        path: text(e.path, `review.findings[${index}].evidence[${evidenceIndex}].path`),
        line: typeof e.line === "number" && Number.isInteger(e.line) && e.line > 0 ? e.line : 1,
        detail: text(e.detail, `review.findings[${index}].evidence[${evidenceIndex}].detail`),
        anchor: text(e.anchor, `review.findings[${index}].evidence[${evidenceIndex}].anchor`),
      };
    });
    const allowedKinds = new Set<FindingKind>(["ambiguity", "conflict", "gap", "misnomer", "leak", "churn"]);
    const kinds = list(f.kinds, `review.findings[${index}].kinds`, (kind) => {
      if (typeof kind !== "string" || !allowedKinds.has(kind as FindingKind)) throw new Error(`review.findings[${index}].kinds contains an invalid value`);
      return kind as FindingKind;
    });
    return {
      id: text(f.id, `review.findings[${index}].id`),
      title: text(f.title, `review.findings[${index}].title`),
      summary: text(f.summary, `review.findings[${index}].summary`),
      principle: text(f.principle, `review.findings[${index}].principle`),
      kinds,
      signals,
      ownerFiles: list(f.ownerFiles, `review.findings[${index}].ownerFiles`, (entry, ownerIndex) => text(entry, `review.findings[${index}].ownerFiles[${ownerIndex}]`)),
      evidence,
      issueNumbers: numbers(f.issueNumbers, `review.findings[${index}].issueNumbers`),
      question: text(f.question, `review.findings[${index}].question`),
      options,
      recommendation: text(f.recommendation, `review.findings[${index}].recommendation`),
      verification: list(f.verification, `review.findings[${index}].verification`, (entry, verificationIndex) => text(entry, `review.findings[${index}].verification[${verificationIndex}]`)),
      score,
      risk: riskBand(score),
      history: {
        commits: typeof history.commits === "number" ? history.commits : 0,
        lastChangedAt: typeof history.lastChangedAt === "string" ? history.lastChangedAt : null,
      },
    };
  });
  return {
    schemaVersion: 1,
    repository,
    generatedAt,
    revision,
    principles,
    modules,
    edges,
    metrics,
    findings: findings.sort((a, b) => b.score - a.score),
  };
}
