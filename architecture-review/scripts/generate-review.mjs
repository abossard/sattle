import { execFileSync } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { buildReview, parseLog } from "./week.mjs";

const appRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const repoRoot = resolve(appRoot, "..");
const argument = (name, fallback) => {
  const index = process.argv.indexOf(name);
  if (index < 0) return fallback;
  if (!process.argv[index + 1] || process.argv[index + 1].startsWith("--")) throw new Error(`${name} requires a path`);
  return resolve(process.argv[index + 1]);
};
const sourcePath = argument("--source", join(appRoot, "data", "week-source.json"));
const outputPath = argument("--output", join(appRoot, "public", "review.json"));
const check = process.argv.includes("--check");

function git(args) {
  return execFileSync("git", args, { cwd: repoRoot, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"], maxBuffer: 64 * 1024 * 1024 }).trim();
}

const source = JSON.parse(readFileSync(sourcePath, "utf8"));
const revision = git(["rev-parse", "HEAD"]);
const since = new Date(Date.parse(source.period.from) - 2 * 86_400_000).toISOString().slice(0, 10);
const review = buildReview(source, {
  revision,
  generatedAt: git(["show", "-s", "--format=%cI", "HEAD"]),
  log: parseLog(git(["log", `--since=${since}`, "--no-renames", "--format=%x1e%H%x1f%aI%x1f%s", "--numstat", revision])),
  resolve: (sha) => { try { return git(["rev-parse", "--verify", "--quiet", `${sha}^{commit}`]); } catch { return null; } },
  fileExists: (path) => { try { git(["cat-file", "-e", `${revision}:${path}`]); return true; } catch { return false; } },
  image: (path) => { const absolute = resolve(repoRoot, path); return relative(repoRoot, absolute).startsWith("..") || !existsSync(absolute) ? null : readFileSync(absolute); },
});

const text = `${JSON.stringify(review, null, 2)}\n`;
if (check && (!existsSync(outputPath) || readFileSync(outputPath, "utf8") !== text)) {
  throw new Error("public/review.json is stale. Run npm run generate.");
}
if (!check) {
  writeFileSync(outputPath, text);
  const shots = join(dirname(outputPath), "shots");
  mkdirSync(shots, { recursive: true });
  for (const item of review.themes.flatMap((theme) => theme.shots)) copyFileSync(resolve(repoRoot, item.file), join(dirname(outputPath), item.src));
}
console.log(`generated ${relative(repoRoot, outputPath)}: ${review.totals.commits} commits, ${review.themes.length} themes, ${review.waste.length + review.overdone.length} findings, revision ${revision}`);
