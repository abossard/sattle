// Calculations for the week review: source data and git facts in, the published review out. No I/O here.

const SEVERITIES = new Set(["high", "medium", "low"]);
const VERDICTS = new Set(["stop", "keep", "change"]);
const FORBIDDEN = [
  [/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/i, "an id that looks like an Azure id"],
  [/\b\d{1,3}(\.\d{1,3}){3}\b/, "an IP address"],
  [/[\w.+-]+@[\w-]+\.[\w.]+/, "an e-mail address"],
  [/\/Users\/|tmp\/hm-/, "a local path"],
  [/\b(subscriptions|resourceGroups)\//i, "an Azure resource path"],
  [/\b(Thm\.|Def\.|Prop\.|Cor\.|Lemma|Theorem|Proposition|Corollary)\s*\d/, "paper notation"],
];

export function textProblem(text) {
  const hit = FORBIDDEN.find(([pattern]) => pattern.test(text));
  return hit ? hit[1] : null;
}

/** Every string in a value, with its path, for the public-text guard. */
function strings(value, path = "") {
  if (typeof value === "string") return [[path, value]];
  if (Array.isArray(value)) return value.flatMap((item, index) => strings(item, `${path}[${index}]`));
  if (value && typeof value === "object") return Object.entries(value).flatMap(([key, item]) => strings(item, path ? `${path}.${key}` : key));
  return [];
}

export function guardText(value, where) {
  for (const [path, text] of strings(value)) {
    const problem = textProblem(text);
    if (problem) throw new Error(`${where} ${path} contains ${problem}: ${text.slice(0, 80)}`);
  }
}

/** A commit subject as a short plain label: no issue refs, no decision ids, no paper notation. */
export function plainSubject(subject) {
  const text = subject
    .replace(/\s*\([^()]*#\d+[^()]*\)/g, "")
    .replace(/\s+under\s+#\d+\s+D\d+(\s+and\s+#\d+\s+D\d+)*/g, "")
    .replace(/\s*\((?:[A-Z]\d+[a-z]?[,\s]*)+\)/g, "")
    .replace(/\b(Thm\.|Def\.|Prop\.|Cor\.|Sec\.|Ch\.|Lemma|Theorem|Proposition|Corollary)\s*[\d.]+[a-z]?(\s*\([^)]*\))?/g, "")
    .replace(/§\s*[\d.]+/g, "")
    .replace(/\(\s*(paper)?\s*\)/g, "")
    .replace(/\s+([,:;.)])/g, "$1")
    .split("; ")[0]
    .replace(/\s{2,}/g, " ")
    .trim();
  return text.length > 90 ? `${text.slice(0, 89).trimEnd()}…` : text;
}

/** Width and height of a PNG from its header bytes. */
export function pngSize(bytes) {
  const signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
  if (bytes.length < 24 || signature.some((value, index) => bytes[index] !== value)) return null;
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  return { width: view.getUint32(16), height: view.getUint32(20) };
}

/** Parse `git log --format=%x1e%H%x1f%cI%x1f%s --numstat`. */
export function parseLog(text) {
  return text.split("\x1e").filter((block) => block.trim()).map((block) => {
    const [header, ...rest] = block.split("\n");
    const [sha, date, subject] = header.split("\x1f");
    const files = rest.filter(Boolean).map((line) => {
      const [added, deleted, path] = line.split("\t");
      return { path, added: Number(added) || 0, deleted: Number(deleted) || 0 };
    });
    return { sha, date, subject, files };
  });
}

export function areaOfPath(areas, path) {
  const match = areas.find((area) => area.paths.some((prefix) => path === prefix || path.startsWith(prefix)));
  return (match ?? areas.find((area) => area.paths.length === 0)).id;
}

function areaOfCommit(areas, files) {
  const lines = new Map();
  for (const file of files) {
    const id = areaOfPath(areas, file.path);
    lines.set(id, (lines.get(id) ?? 0) + file.added + file.deleted);
  }
  const ranked = [...lines].sort((left, right) => right[1] - left[1]);
  return ranked[0]?.[0] ?? areas.find((area) => area.paths.length === 0).id;
}

const folder = (path) => path.split("/").slice(0, path.includes("/") ? 2 : 1).join("/");
const inPeriod = (period, date) => date.slice(0, 10) >= period.from && date.slice(0, 10) <= period.to;

function validateSource(source) {
  if (source.schemaVersion !== 2) throw new Error("Unsupported review schema");
  const areaIds = new Set(source.areas.map((area) => area.id));
  if (areaIds.size !== source.areas.length) throw new Error("Duplicate area id");
  if (source.areas.filter((area) => area.paths.length === 0).length !== 1) throw new Error("Exactly one area must catch the paths no other area names");
  for (const [number, issue] of Object.entries(source.issues)) {
    if (!/^\d+$/.test(number) || !areaIds.has(issue.area)) throw new Error(`Issue ${number} names a missing area`);
  }
  const ids = new Set();
  for (const item of [...source.themes, ...source.waste, ...source.overdone]) {
    if (ids.has(item.id)) throw new Error(`Duplicate item id: ${item.id}`);
    ids.add(item.id);
  }
  for (const theme of source.themes) {
    if (!areaIds.has(theme.area)) throw new Error(`Theme ${theme.id} names a missing area`);
    if (!theme.issues?.length) throw new Error(`Theme ${theme.id} needs a GitHub issue`);
    if (!theme.what || !theme.why) throw new Error(`Theme ${theme.id} needs what and why`);
  }
  for (const item of [...source.waste, ...source.overdone]) {
    if (!SEVERITIES.has(item.severity)) throw new Error(`Item ${item.id} has an invalid severity`);
    if (!VERDICTS.has(item.verdict)) throw new Error(`Item ${item.id} needs a verdict: stop, keep or change`);
    if (!item.evidence?.length) throw new Error(`Item ${item.id} needs evidence`);
    if (!item.suggestion) throw new Error(`Item ${item.id} needs a recommendation`);
  }
  const steps = new Set(source.flow.steps.map((step) => step.id));
  for (const loop of source.flow.loops) {
    if (!steps.has(loop.from) || !steps.has(loop.to)) throw new Error(`Flow loop ${loop.from}->${loop.to} names a missing step`);
  }
  guardText(source, "Source");
}

/**
 * Build the published review.
 * facts: { revision, generatedAt, log: parsed commits, resolve(sha) -> full sha or null, fileExists(path) -> boolean, image(path) -> bytes or null }
 */
export function buildReview(source, facts) {
  validateSource(source);
  const base = `https://github.com/${source.repository}`;
  const commits = facts.log.filter((commit) => inPeriod(source.period, commit.date)).map((commit) => {
    const added = commit.files.reduce((sum, file) => sum + file.added, 0);
    const deleted = commit.files.reduce((sum, file) => sum + file.deleted, 0);
    return {
      sha: commit.sha.slice(0, 7),
      date: commit.date.slice(0, 10),
      area: areaOfCommit(source.areas, commit.files),
      title: plainSubject(commit.subject),
      url: `${base}/commit/${commit.sha}`,
      added,
      deleted,
      files: commit.files,
    };
  });
  const bySha = new Map(commits.map((commit) => [commit.sha, commit]));

  const issueRef = (number) => {
    const issue = source.issues[String(number)];
    if (!issue) throw new Error(`Issue ${number} has no title in the source`);
    return { n: number, title: issue.title, area: issue.area, url: `${base}/issues/${number}` };
  };
  const commitRef = (sha) => {
    const full = facts.log.find((commit) => commit.sha.startsWith(sha))?.sha ?? facts.resolve(sha);
    if (!full) throw new Error(`Commit ${sha} does not resolve`);
    const commit = bySha.get(full.slice(0, 7));
    if (!commit) throw new Error(`Commit ${sha} is outside the period`);
    return { sha: commit.sha, title: commit.title, url: commit.url, area: commit.area, lines: commit.added + commit.deleted };
  };
  const evidenceRef = (evidence) => {
    if (evidence.issue) { const issue = issueRef(evidence.issue); return { label: issue.title, url: issue.url, kind: "issue", ref: `#${issue.n}` }; }
    if (evidence.commit) { const commit = commitRef(evidence.commit); return { label: commit.title, url: commit.url, kind: "commit", ref: commit.sha }; }
    if (evidence.file) {
      if (!facts.fileExists(evidence.file)) throw new Error(`Evidence file is missing: ${evidence.file}`);
      return { label: evidence.file, url: `${base}/blob/${facts.revision}/${evidence.file}`, kind: "file", ref: "" };
    }
    if (!evidence.label || !evidence.url?.startsWith(`${base}/`)) throw new Error(`Evidence link must point into ${base}: ${evidence.url}`);
    return { label: evidence.label, url: evidence.url, kind: "comment", ref: "" };
  };
  const finding = (item) => ({ ...item, evidence: item.evidence.map(evidenceRef) });

  const areas = source.areas.map((area) => {
    const own = commits.filter((commit) => commit.area === area.id);
    const folders = new Map();
    for (const commit of own) {
      for (const file of commit.files) {
        const key = folder(file.path);
        const entry = folders.get(key) ?? { path: key, lines: 0, commits: new Set() };
        entry.lines += file.added + file.deleted;
        entry.commits.add(commit.sha);
        folders.set(key, entry);
      }
    }
    return {
      id: area.id,
      name: area.name,
      about: area.about,
      commits: own.length,
      added: own.reduce((sum, commit) => sum + commit.added, 0),
      deleted: own.reduce((sum, commit) => sum + commit.deleted, 0),
      folders: [...folders.values()].sort((left, right) => right.lines - left.lines).slice(0, 4)
        .map((entry) => ({ path: entry.path, lines: entry.lines, commits: entry.commits.size })),
    };
  });

  const closed = Object.entries(source.issues)
    .filter(([, issue]) => issue.closed && inPeriod(source.period, issue.closed))
    .map(([number, issue]) => ({ ...issueRef(Number(number)), closed: issue.closed }));

  const days = source.days.map((day) => ({
    date: day.date,
    note: day.note,
    commits: commits.filter((commit) => commit.date === day.date).map(({ files, ...commit }) => commit),
    closed: closed.filter((issue) => issue.closed === day.date).map(({ closed: _closed, ...issue }) => issue),
  }));
  const unlisted = commits.filter((commit) => !source.days.some((day) => day.date === commit.date));
  if (unlisted.length) throw new Error(`Commits on a day the source does not list: ${unlisted[0].date}`);

  const shot = (item) => {
    const bytes = facts.image(item.file);
    const size = bytes && pngSize(bytes);
    if (!size) throw new Error(`Screenshot is missing or not a PNG: ${item.file}`);
    if (!item.caption) throw new Error(`Screenshot ${item.file} needs a caption`);
    return { file: item.file, src: `shots/${item.file.split("/").pop()}`, caption: item.caption, ...size };
  };
  const themes = source.themes.map((theme) => {
    const refs = theme.commits.map(commitRef);
    return { ...theme, issues: theme.issues.map(issueRef), commits: refs, lines: refs.reduce((sum, commit) => sum + commit.lines, 0), shots: (theme.shots ?? []).map(shot) };
  });
  const names = themes.flatMap((theme) => theme.shots.map((item) => item.src));
  if (new Set(names).size !== names.length) throw new Error("Two screenshots share a file name");

  const tokens = source.tokens;
  if (tokens.days.map((day) => day.date).join() !== source.days.map((day) => day.date).join()) throw new Error("Token days must match the days of the period");
  const equivalent = (row) => row.input + row.cacheWrite + 0.1 * row.cacheRead;
  const tokenDays = tokens.days.map((day) => ({ ...day, inputEquivalent: Math.round(equivalent(day)) }));
  const tokenTotal = tokenDays.reduce((sum, day) => sum + day.inputEquivalent, 0);
  const kindTotal = tokens.kinds.reduce((sum, kind) => sum + kind.own + kind.subagents, 0);
  if (Math.abs(kindTotal - tokenTotal) > tokenTotal * 0.001) throw new Error("Token kinds do not add up to the token days");

  const review = {
    schemaVersion: 2,
    repository: source.repository,
    revision: facts.revision,
    generatedAt: facts.generatedAt,
    period: source.period,
    summary: source.summary,
    totals: {
      commits: commits.length,
      added: commits.reduce((sum, commit) => sum + commit.added, 0),
      deleted: commits.reduce((sum, commit) => sum + commit.deleted, 0),
      issuesClosed: closed.length,
    },
    areas,
    days,
    themes,
    waste: source.waste.map(finding),
    overdone: source.overdone.map(finding),
    good: source.good,
    tokens: {
      method: tokens.method,
      weights: tokens.weights,
      total: tokenTotal,
      cacheRead: tokens.days.reduce((sum, day) => sum + day.cacheRead, 0),
      output: tokens.days.reduce((sum, day) => sum + day.output, 0),
      sessions: tokens.sessions,
      days: tokenDays,
      kinds: tokens.kinds,
      issues: tokens.issues.map((row) => ({ ...issueRef(row.issue), inputEquivalent: row.inputEquivalent, sessions: row.sessions })),
      models: tokens.models,
      longContext: tokens.longContext,
    },
    flow: {
      ...source.flow,
      loops: source.flow.loops.map((loop) => ({ ...loop, evidence: evidenceRef(loop.evidence) })),
    },
  };
  guardText(review, "Review");
  return review;
}
