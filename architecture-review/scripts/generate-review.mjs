import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { scoreSignals, riskBand } from "../shared/scoring.mjs";

const appRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const repoRoot = resolve(appRoot, "..");
const argument = (name, fallback) => {
  const index = process.argv.indexOf(name);
  if (index < 0) return fallback;
  if (!process.argv[index + 1] || process.argv[index + 1].startsWith("--")) throw new Error(`${name} requires a path`);
  return resolve(process.argv[index + 1]);
};
const sourcePath = argument("--source", join(appRoot, "data", "review-source.json"));
const outputPath = argument("--output", join(appRoot, "public", "review.json"));
const check = process.argv.includes("--check");

function git(args) {
  return execFileSync("git", args, { cwd: repoRoot, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] }).trim();
}

function validatePath(path) {
  const absolute = resolve(repoRoot, path);
  const rel = relative(repoRoot, absolute);
  if (rel.startsWith("..") || !existsSync(absolute)) throw new Error(`Review source points outside the repo or at a missing path: ${path}`);
  return absolute;
}

function historyFor(paths) {
  if (paths.length === 0) return { commits: 0, lastChangedAt: null, churn: 0 };
  const revisions = git(["log", "--format=%H", "--", ...paths]).split("\n").filter(Boolean);
  const lastChangedAt = git(["log", "-1", "--format=%aI", "--", ...paths]) || null;
  return {
    commits: revisions.length,
    lastChangedAt,
    churn: Math.min(1, Math.log2(revisions.length + 1) / 6),
  };
}

const revision = git(["rev-parse", "HEAD"]);
const committedFiles = new Map();
function evidence(source) {
  validatePath(source.path);
  if (typeof source.anchor !== "string" || !source.anchor.trim()) throw new Error(`Missing evidence anchor: ${source.path}`);
  if (!committedFiles.has(source.path)) committedFiles.set(source.path, git(["show", `${revision}:${source.path}`]).split("\n"));
  const matches = committedFiles.get(source.path).flatMap((line, index) => line.includes(source.anchor) ? [index + 1] : []);
  if (matches.length !== 1) throw new Error(`Evidence anchor must match exactly once at ${revision}: ${source.path}: ${source.anchor}`);
  return { ...source, line: matches[0] };
}

const source = JSON.parse(readFileSync(sourcePath, "utf8"));
if (source.schemaVersion !== 1) throw new Error("Unsupported review schema");
const moduleIds = new Set(source.modules.map((module) => module.id));
if (moduleIds.size !== source.modules.length) throw new Error("Duplicate module id");
for (const module of source.modules) validatePath(module.path);
for (const edge of source.edges) {
  if (!moduleIds.has(edge.from) || !moduleIds.has(edge.to)) throw new Error(`Edge ${edge.from}->${edge.to} names a missing module`);
}

const ids = new Set();
const findings = source.findings.map((finding) => {
  if (ids.has(finding.id)) throw new Error(`Duplicate finding id: ${finding.id}`);
  ids.add(finding.id);
  if (!finding.issueNumbers?.length || finding.issueNumbers.some((number) => !Number.isSafeInteger(number) || number < 1)) throw new Error(`Finding ${finding.id} needs a GitHub issue owner`);
  if (!finding.evidence?.length) throw new Error(`Finding ${finding.id} needs evidence`);
  if (!finding.options?.length || !finding.verification?.length || !finding.ownerFiles?.length) throw new Error(`Finding ${finding.id} needs options, verification and owner files`);
  for (const key of ["impact", "ambiguity", "conflict", "churn", "evidenceGap"]) {
    const value = finding.signals[key];
    if (!Number.isFinite(value) || value < 0 || value > 1) throw new Error(`Invalid signal ${finding.id}.${key}`);
  }
  finding.ownerFiles.forEach(validatePath);
  const resolvedEvidence = finding.evidence.map(evidence);
  const history = historyFor(finding.ownerFiles);
  const signals = { ...finding.signals, churn: Math.max(finding.signals.churn, history.churn) };
  const score = scoreSignals(signals);
  return {
    ...finding,
    evidence: resolvedEvidence,
    signals,
    score,
    risk: riskBand(score),
    history: { commits: history.commits, lastChangedAt: history.lastChangedAt },
  };
}).sort((left, right) => right.score - left.score);

const generatedAt = git(["show", "-s", "--format=%cI", "HEAD"]);
const generated = {
  schemaVersion: 1,
  repository: source.repository,
  generatedAt,
  revision,
  principles: source.principles,
  modules: source.modules,
  edges: source.edges,
  metrics: source.metrics,
  findings,
};
const text = `${JSON.stringify(generated, null, 2)}\n`;
if (check && (!existsSync(outputPath) || readFileSync(outputPath, "utf8") !== text)) {
  throw new Error("public/review.json is stale. Run npm run generate.");
}
if (!check) writeFileSync(outputPath, text);
console.log(`generated ${relative(repoRoot, outputPath)}: ${findings.length} findings, revision ${revision}`);
