import "./styles.css";
import { loadConfig, loadDiscussion, loadIssues, sendFeedback } from "./api";
import { WEIGHTS } from "../shared/scoring.mjs";
import { parseReview, type Finding, type LiveIssue, type ModuleNode, type ReviewDocument, type RuntimeConfig } from "./model";
import { pageWindow } from "./paging";

const mount = document.querySelector<HTMLDivElement>("#app");
if (!mount) throw new Error("Missing #app");
const app: HTMLDivElement = mount;

const state = {
  view: "board" as "board" | "map",
  filter: "all",
  query: "",
  selectedId: "",
  reviewKey: "",
  page: 1,
};

let review: ReviewDocument;
let config: RuntimeConfig;
let issues = new Map<number, LiveIssue>();
const drafts = new Map<string, { decision: string; rationale: string }>();
let discussionRequest = 0;
let discussionBusy = false;

const escape = (value: string) => value
  .replaceAll("&", "&amp;")
  .replaceAll("<", "&lt;")
  .replaceAll(">", "&gt;")
  .replaceAll('"', "&quot;")
  .replaceAll("'", "&#039;");

const issueUrl = (number: number) => `${config.repositoryUrl}/issues/${number}`;
const repositoryFileUrl = (path: string, line: number) => `${config.repositoryUrl}/blob/${review.revision}/${path}#L${line}`;
const readableDate = (value: string | null) => value ? new Date(value).toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" }) : "unknown";

function issueMarkup(number: number) {
  const live = issues.get(number);
  return `<a class="issue-link issue-state" data-issue="${number}" data-state="${live?.state ?? "unknown"}" href="${escape(issueUrl(number))}" target="_blank" rel="noreferrer">#${number}${live ? ` ${escape(live.title)}` : " · status unavailable"}</a>`;
}

function selectedFinding(): Finding | undefined {
  return review.findings.find((finding) => finding.id === state.selectedId) ?? review.findings[0];
}

function visibleFindings() {
  const query = state.query.trim().toLowerCase();
  return review.findings.filter((finding) => {
    if (state.filter !== "all" && !finding.kinds.includes(state.filter as Finding["kinds"][number]) && finding.risk !== state.filter) return false;
    return !query || `${finding.title} ${finding.summary} ${finding.principle} ${finding.question}`.toLowerCase().includes(query);
  });
}

function metricMarkup() {
  return review.metrics.map((metric) => `
    <article class="metric" data-status="${metric.status}">
      <span class="metric-value">${escape(metric.value)}</span>
      <span class="metric-label">${escape(metric.label)}</span>
      <p class="metric-detail">${escape(metric.detail)}</p>
      ${metric.issueNumbers.length ? `<a href="${issueUrl(metric.issueNumbers[0])}" target="_blank" rel="noreferrer">Evidence in #${metric.issueNumbers[0]}</a>` : ""}
    </article>
  `).join("");
}

function edgePath(from: ModuleNode, to: ModuleNode) {
  const x1 = from.x, y1 = from.y, x2 = to.x, y2 = to.y;
  const mx = (x1 + x2) / 2;
  return `M ${x1} ${y1} C ${mx} ${y1}, ${mx} ${y2}, ${x2} ${y2}`;
}

function moduleRisk(module: ModuleNode): string {
  const linked = review.findings.filter((finding) => finding.ownerFiles.some((path) => path === module.path || path.startsWith(`${module.path}/`)));
  const top = linked.reduce((score, finding) => Math.max(score, finding.score), 0);
  if (top >= 80) return "critical";
  if (top >= 62) return "high";
  if (top >= 42) return "medium";
  return "low";
}

function mapMarkup() {
  const limit = 160;
  const modules = review.modules.slice(0, limit);
  const nodes = new Map(modules.map((module) => [module.id, module]));
  const edges = review.edges.map((edge) => {
    const from = nodes.get(edge.from), to = nodes.get(edge.to);
    if (!from || !to) return "";
    const path = edgePath(from, to);
    const x = (from.x + to.x) / 2, y = (from.y + to.y) / 2 - 7;
    return `
      <path class="map-edge map-edge-${edge.kind}" d="${path}" marker-end="url(#arrow-${edge.kind})"></path>
      <text class="map-edge-label" x="${x}" y="${y}" text-anchor="middle">${escape(edge.label)}</text>
    `;
  }).join("");
  const moduleMarkup = modules.map((module) => `
    <g class="map-node" data-risk="${moduleRisk(module)}" data-module="${escape(module.id)}" tabindex="0" role="button" aria-label="${escape(module.title)}: ${escape(module.role)}" transform="translate(${module.x - 100} ${module.y - 42})">
      <rect width="200" height="84"></rect>
      <text class="map-node-title" x="16" y="29">${escape(module.title)}</text>
      <text class="map-node-role" x="16" y="50">${escape(module.role)}</text>
      <text class="map-node-role" x="16" y="69">${escape(module.issueNumbers.map((number) => `#${number}`).join(" · ") || module.path)}</text>
    </g>
  `).join("");
  return `
    <section class="panel" aria-labelledby="map-title">
      <div class="panel-head"><h2 id="map-title">Architecture and feedback map</h2><span>${modules.length === review.modules.length ? `${modules.length} modules` : `showing ${modules.length} of ${review.modules.length} modules`}</span></div>
      <div class="map-wrap">
        <svg class="architecture-map" viewBox="0 0 1200 620" role="img" aria-label="Sattle module boundary and data-flow map">
          <defs>
            <marker id="arrow-data" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto"><path d="M0 0 L0 6 L7 3 z" fill="var(--muted)"></path></marker>
            <marker id="arrow-definition" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto"><path d="M0 0 L0 6 L7 3 z" fill="var(--violet)"></path></marker>
            <marker id="arrow-commit" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto"><path d="M0 0 L0 6 L7 3 z" fill="var(--amber)"></path></marker>
            <marker id="arrow-feedback" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto"><path d="M0 0 L0 6 L7 3 z" fill="var(--cyan)"></path></marker>
          </defs>
          ${edges}
          ${moduleMarkup}
        </svg>
      </div>
    </section>
  `;
}

function findingMarkup(finding: Finding) {
  return `
    <article class="finding" tabindex="0" role="button" data-finding="${escape(finding.id)}" aria-current="${finding.id === selectedFinding()?.id}">
      <div class="finding-top">
        <div>
          <h3>${escape(finding.title)}</h3>
          <p>${escape(finding.summary)}</p>
        </div>
        <span class="score ${finding.risk}" aria-label="Priority ${finding.score}">${finding.score}</span>
      </div>
      <div class="tags">
        <span class="tag">${escape(finding.risk)}</span>
        ${finding.kinds.map((kind) => `<span class="tag">${escape(kind)}</span>`).join("")}
        <span class="tag">${finding.history.commits} commits</span>
        ${finding.issueNumbers.map((number) => `<span class="tag">#${number}</span>`).join("")}
      </div>
    </article>
  `;
}

function findingsMarkup() {
  const filtered = visibleFindings();
  const page = pageWindow(filtered, state.page, 25);
  const findings = page.items;
  state.page = page.page;
  return `
    <section class="panel" aria-labelledby="findings-title">
      <div class="panel-head"><h2 id="findings-title">Prioritized review queue</h2><span>${page.start}-${page.end} of ${filtered.length} concerns</span></div>
      <div class="findings">${findings.length ? findings.map(findingMarkup).join("") : `<p class="empty">No concern matches this filter.</p>`}</div>
      ${page.pages > 1 ? `<div class="page-controls"><button class="button" type="button" data-page="${page.page - 1}" ${page.page === 1 ? "disabled" : ""}>Previous</button><span>Page ${page.page} of ${page.pages}</span><button class="button" type="button" data-page="${page.page + 1}" ${page.page === page.pages ? "disabled" : ""}>Next</button></div>` : ""}
    </section>
  `;
}

function signalMarkup(label: string, value: number) {
  const percent = Math.round(value * 100);
  return `<div class="signal"><span class="signal-label">${escape(label)} ${percent}%</span><div class="signal-bar" role="meter" aria-label="${escape(label)}" aria-valuenow="${percent}" aria-valuemin="0" aria-valuemax="100"><span style="width:${percent}%"></span></div></div>`;
}

function detailMarkup() {
  const finding = selectedFinding();
  if (!finding) return `<aside class="detail-panel"><div class="detail-content"><p>No findings.</p></div></aside>`;
  const linkedIssues = finding.issueNumbers.map((number) => issueMarkup(number)).join("<br>");
  const draft = drafts.get(finding.id);
  const options = [...finding.options, { id: "needs-discussion", title: "Question or different direction", detail: "Challenge the framing, request evidence, or propose another choice." }].map((option, index) => `
    <label class="option">
      <input type="radio" name="decision" value="${escape(option.id)}" ${index === 0 ? "required" : ""} ${draft?.decision === option.id ? "checked" : ""}>
      <span><strong>${escape(option.title)}</strong>${option.detail ? `<small>${escape(option.detail)}</small>` : ""}</span>
    </label>
  `).join("");
  const target = finding.issueNumbers[0];
  return `
    <aside class="detail-panel" aria-labelledby="detail-title">
      <div class="detail-content">
        <p class="eyebrow">${escape(finding.risk)} · ${finding.score}/100 · last changed ${readableDate(finding.history.lastChangedAt)}</p>
        <h2 id="detail-title" tabindex="-1">${escape(finding.title)}</h2>
        <p>${escape(finding.summary)}</p>
        <div class="tags">${finding.kinds.map((kind) => `<span class="tag">${escape(kind)}</span>`).join("")}</div>
        <div class="signal-grid">
          ${signalMarkup("impact", finding.signals.impact)}
          ${signalMarkup("ambiguity", finding.signals.ambiguity)}
          ${signalMarkup("conflict", finding.signals.conflict)}
          ${signalMarkup("churn", finding.signals.churn)}
          ${signalMarkup("evidence", finding.signals.evidenceGap)}
        </div>
        <p class="score-explanation">Priority = ${Object.entries(WEIGHTS).map(([name, weight]) => `${weight}% ${name}`).join(" + ")}. Signals are reviewer estimates; churn is increased by file history. Commit count is not proof of a reversed decision.</p>
        <div class="notice"><strong>Constraint:</strong> ${escape(finding.principle)}</div>
        <section aria-labelledby="evidence-title">
          <h3 id="evidence-title">Evidence</h3>
          <ul class="evidence">${finding.evidence.map((evidence) => `<li><a href="${repositoryFileUrl(evidence.path, evidence.line)}" target="_blank" rel="noreferrer"><code>${escape(evidence.path)}:${evidence.line}</code></a><p>${escape(evidence.detail)}</p><p><code>${escape(evidence.anchor)}</code></p></li>`).join("")}</ul>
        </section>
        <section aria-labelledby="question-title">
          <h3 id="question-title">Decision to make</h3>
          <p>${escape(finding.question)}</p>
          <div class="notice"><strong>Recommended:</strong> ${escape(finding.recommendation)}</div>
        </section>
        <details class="verification"><summary>How to settle this concern</summary><ul>${finding.verification.map((check) => `<li>${escape(check)}</li>`).join("")}</ul></details>
        <section class="discussion" aria-labelledby="discussion-title">
          <div class="discussion-heading"><h3 id="discussion-title">Live GitHub record</h3><button class="button" type="button" id="refresh-discussion">Refresh</button></div>
          <p class="discussion-status" id="discussion-status" role="status">Loading the owning issue. Refreshes every 30 seconds.</p>
          <div id="discussion-content"></div>
        </section>
        <form class="feedback" id="feedback-form">
          <h3>Feed the answer into GitHub</h3>
          <p>${linkedIssues || "No issue exists yet. The server will create an architecture decision issue."}</p>
          <div class="option-list">${options}</div>
          <label class="field">Rationale<textarea name="rationale" required minlength="8" maxlength="8000" placeholder="Why this choice? What evidence settles it?">${escape(draft?.rationale ?? "")}</textarea></label>
          <label class="field">Review key<input name="reviewKey" type="password" autocomplete="off" spellcheck="false" value="${escape(state.reviewKey)}" required placeholder="Configured on Cloudflare Pages"></label>
          <input type="hidden" name="findingId" value="${escape(finding.id)}">
          ${target ? `<input type="hidden" name="issueNumber" value="${target}">` : ""}
          <div class="feedback-actions">
            <button class="button primary" type="submit" ${config.feedbackEnabled ? "" : "disabled"}>${target ? `Post to #${target}` : "Create GitHub issue"}</button>
            <button class="button" type="button" id="copy-draft">Copy draft</button>
          </div>
          <div id="feedback-result" class="notice" role="status" aria-live="polite">${config.feedbackEnabled ? "Posting is enabled. The key is kept only in this tab. Answers become GitHub comments, not automatically accepted decisions." : "Posting is disabled on this deployment. Copy the draft or configure GITHUB_TOKEN and REVIEW_KEY on Cloudflare Pages."}</div>
        </form>
      </div>
    </aside>
  `;
}

function mainView() {
  if (state.view === "map") {
    return `<div class="review-grid">${mapMarkup()}${detailMarkup()}</div>`;
  }
  return `<div class="review-grid">${findingsMarkup()}${detailMarkup()}</div>`;
}

function render() {
  const previous = document.querySelector<HTMLFormElement>("#feedback-form");
  if (previous) {
    const data = new FormData(previous);
    drafts.set(String(data.get("findingId")), { decision: String(data.get("decision") ?? ""), rationale: String(data.get("rationale") ?? "") });
    state.reviewKey = String(data.get("reviewKey") ?? "");
  }
  const generated = new Date(review.generatedAt);
  const issueCount = new Set(review.findings.flatMap((finding) => finding.issueNumbers)).size;
  app.innerHTML = `
    <div class="shell">
      <header class="topbar">
        <div class="mark" aria-hidden="true">S</div>
        <div class="brand"><h1>Sattle architecture review</h1><p>Generated evidence. Durable decisions live in GitHub issues.</p></div>
        <div class="spacer"></div>
        <span class="status-chip">${issueCount} linked issues · ${escape(review.revision.slice(0, 7))}</span>
        <a class="button" href="${config.repositoryUrl}/issues" target="_blank" rel="noreferrer">Open issues</a>
        <button class="icon-button" type="button" id="theme-toggle" aria-label="Toggle light or dark theme">◐</button>
      </header>
      <main class="main" id="review-main">
        <section class="hero">
          <div class="hero-copy">
            <p class="eyebrow">Evidence baseline ${generated.toLocaleDateString()} · ${escape(review.revision.slice(0, 7))}</p>
            <h2>Changes flow. <span>Decisions stay traceable.</span></h2>
            <p>This board ranks ambiguity, conflicting direction, churn, leaks, and unmeasured scale risk. GitHub is the only place a choice is accepted.</p>
            <p class="baseline-note">Code evidence is pinned to the committed revision. Uncommitted working-tree changes are not represented. Recommendations are proposals, not decisions.</p>
            <div class="hero-actions">
              <button class="button primary" type="button" data-view="board">Review queue</button>
              <button class="button" type="button" data-view="map">Architecture map</button>
            </div>
          </div>
          <aside class="principles">
            <h3>Architecture north stars</h3>
            <ol>${review.principles.map((principle) => `<li>${escape(principle)}</li>`).join("")}</ol>
          </aside>
        </section>
        <section class="metrics" aria-label="Architecture measurements">${metricMarkup()}</section>
        <details class="direction-history panel">
          <summary>Decisions that changed direction</summary>
          <p>These changes are recorded in GitHub, not inferred from code churn. Open the issue for the authoritative rationale and current status.</p>
          <ul>
            <li><a href="${issueUrl(7)}" target="_blank" rel="noreferrer">#7 · Migrate Sattle to graph-native GKQL as its only definition language</a>. On November 19, the sole-language migration replaced the earlier parallel-KQL destination in #106.</li>
            <li><a href="${issueUrl(122)}" target="_blank" rel="noreferrer">#122 · Orchestrator state</a>. On November 18, the engine gate was revised to 8 GB live, 12 GB peak and 37 microseconds per record. These are the current recorded limits, not the earlier budgets.</li>
            <li><a href="${issueUrl(119)}" target="_blank" rel="noreferrer">#119 · Window-close latency at one million nodes</a>. The earlier whole-graph waves prototype exposed unpublished results. Its replacement prepares matches privately and publishes in writer order; the longer steady p99 measurement remains open.</li>
          </ul>
        </details>
        <div class="toolbar">
          <div class="tabs" role="group" aria-label="Review view">
            <button class="tab" type="button" data-view="board" aria-pressed="${state.view === "board"}">Queue</button>
            <button class="tab" type="button" data-view="map" aria-pressed="${state.view === "map"}">Architecture</button>
          </div>
          <label class="field" aria-label="Search findings"><span>Search</span><input id="review-search" value="${escape(state.query)}" type="search" placeholder="Find leaks, churn, health, GKQL…"></label>
          <label class="field">Priority or kind<select id="review-filter">
            ${["all", "critical", "high", "medium", "ambiguity", "conflict", "gap", "misnomer", "leak", "churn"].map((filter) => `<option value="${filter}" ${state.filter === filter ? "selected" : ""}>${filter}</option>`).join("")}
          </select></label>
        </div>
        ${mainView()}
      </main>
      <footer class="footer"><span>${escape(review.repository)} · evidence generated from ${escape(review.revision)}</span><span>Client contains no durable decisions. GitHub comments and issues are the record.</span></footer>
    </div>
  `;
  bind();
  void refreshDiscussion();
}

async function refreshDiscussion() {
  const finding = selectedFinding();
  const target = finding?.issueNumbers[0];
  if (!target) return;
  const request = ++discussionRequest;
  discussionBusy = true;
  try {
    const discussion = await loadDiscussion(target);
    if (request !== discussionRequest || selectedFinding()?.id !== finding.id) return;
    const content = document.querySelector<HTMLElement>("#discussion-content");
    const status = document.querySelector<HTMLElement>("#discussion-status");
    if (status) status.textContent = `${discussion.issue.state} · ${discussion.totalComments} comments · checked ${new Date().toLocaleTimeString()} · refreshes every 30s`;
    if (content) content.innerHTML = `
      <details><summary>Issue brief: ${escape(discussion.issue.title)}</summary><pre>${escape(discussion.issue.body)}</pre></details>
      ${discussion.comments.length ? `<ol class="discussion-comments">${discussion.comments.map((comment) => `<li><a href="${escape(comment.url)}" target="_blank" rel="noreferrer">${escape(comment.author)} · ${readableDate(comment.createdAt)}</a><pre>${escape(comment.body)}</pre></li>`).join("")}</ol>` : `<p>No comments yet. Your answer will appear here after it reaches GitHub.</p>`}
      ${discussion.totalComments > 20 ? `<a href="${issueUrl(target)}" target="_blank" rel="noreferrer">Read all ${discussion.totalComments} comments on GitHub</a>` : ""}`;
    document.querySelectorAll<HTMLAnchorElement>(`[data-issue="${target}"]`).forEach((link) => {
      link.dataset.state = discussion.issue.state;
      link.textContent = `#${target} ${discussion.issue.title}`;
    });
  } catch (error) {
    if (request !== discussionRequest) return;
    const status = document.querySelector<HTMLElement>("#discussion-status");
    if (status) status.textContent = `GitHub record unavailable: ${error instanceof Error ? error.message : "request failed"}. Previous content may be stale.`;
  } finally {
    if (request === discussionRequest) discussionBusy = false;
  }
}

function draftFor(finding: Finding) {
  const decision = document.querySelector<HTMLInputElement>('input[name="decision"]:checked')?.value ?? finding.options[0]?.id ?? "needs-input";
  const rationale = document.querySelector<HTMLTextAreaElement>('textarea[name="rationale"]')?.value.trim() ?? "";
  return [
    `## Architecture dashboard response`,
    "",
    `- Finding: \`${finding.id}\``,
    `- Choice: \`${decision}\``,
    `- Dashboard revision: \`${review.revision}\``,
    "",
    "### Rationale",
    rationale || "Pending owner rationale.",
    "",
    "### Evidence",
    ...finding.evidence.map((evidence) => `- \`${evidence.path}:${evidence.line}\` ${evidence.detail}`),
  ].join("\n");
}

function bind() {
  document.querySelector<HTMLButtonElement>("#theme-toggle")?.addEventListener("click", () => {
    document.documentElement.dataset.theme = document.documentElement.dataset.theme === "light" ? "dark" : "light";
  });
  document.querySelectorAll<HTMLButtonElement>("[data-view]").forEach((button) => button.addEventListener("click", () => {
    state.view = button.dataset.view === "map" ? "map" : "board";
    render();
    document.querySelector<HTMLButtonElement>(`.tabs [data-view="${state.view}"]`)?.focus({ preventScroll: true });
  }));
  document.querySelector<HTMLInputElement>("#review-search")?.addEventListener("change", (event) => {
    state.query = (event.currentTarget as HTMLInputElement).value;
    state.page = 1;
    render();
  });
  document.querySelector<HTMLSelectElement>("#review-filter")?.addEventListener("change", (event) => {
    state.filter = (event.currentTarget as HTMLSelectElement).value;
    state.page = 1;
    render();
  });
  document.querySelectorAll<HTMLButtonElement>("[data-page]").forEach((button) => button.addEventListener("click", () => {
    state.page = Number(button.dataset.page);
    render();
  }));
  document.querySelectorAll<HTMLElement>("[data-finding]").forEach((finding) => {
    const choose = () => {
      state.selectedId = finding.dataset.finding ?? "";
      render();
      document.querySelector("#detail-title")?.scrollIntoView({ behavior: "smooth", block: "start" });
      document.querySelector<HTMLElement>("#detail-title")?.focus({ preventScroll: true });
    };
    finding.addEventListener("click", choose);
    finding.addEventListener("keydown", (event) => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); choose(); } });
  });
  document.querySelectorAll<SVGGElement>(".map-node").forEach((module) => {
    const choose = () => {
      const id = module.dataset.module;
      const node = review.modules.find((item) => item.id === id);
      const next = review.findings.find((finding) => node && finding.ownerFiles.some((path) => path === node.path || path.startsWith(`${node.path}/`)));
      if (next) { state.selectedId = next.id; render(); document.querySelector<HTMLElement>("#detail-title")?.focus({ preventScroll: true }); }
    };
    module.addEventListener("click", choose);
    module.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") { event.preventDefault(); choose(); }
    });
  });
  document.querySelector<HTMLButtonElement>("#refresh-discussion")?.addEventListener("click", () => { void refreshDiscussion(); });
  const finding = selectedFinding();
  document.querySelector<HTMLButtonElement>("#copy-draft")?.addEventListener("click", async () => {
    if (!finding) return;
    const result = document.querySelector<HTMLElement>("#feedback-result");
    try {
      await navigator.clipboard.writeText(draftFor(finding));
      if (result) result.textContent = "Draft copied. Paste it into the linked issue if the Pages function is not configured.";
    } catch {
      if (result) { result.textContent = "Clipboard unavailable on plain HTTP. Select this draft and paste it into GitHub:"; const draft = document.createElement("textarea"); draft.readOnly = true; draft.value = draftFor(finding); result.append(draft); draft.select(); }
    }
  });
  document.querySelector<HTMLFormElement>("#feedback-form")?.addEventListener("submit", async (event) => {
    event.preventDefault();
    if (!finding) return;
    const form = event.currentTarget as HTMLFormElement;
    const data = new FormData(form);
    const reviewKey = String(data.get("reviewKey") ?? "");
    state.reviewKey = reviewKey;
    const result = document.querySelector<HTMLElement>("#feedback-result");
    const submit = form.querySelector<HTMLButtonElement>('button[type="submit"]');
    if (submit) submit.disabled = true;
    if (result) { result.className = "notice"; result.textContent = "Posting the decision to GitHub…"; }
    const issueNumber = Number(data.get("issueNumber"));
    try {
      const response = await sendFeedback(reviewKey, {
        action: Number.isInteger(issueNumber) && issueNumber > 0 ? "comment" : "create",
        findingId: finding.id,
        issueNumber: Number.isInteger(issueNumber) && issueNumber > 0 ? issueNumber : undefined,
        decision: String(data.get("decision") ?? ""),
        rationale: String(data.get("rationale") ?? ""),
        summary: finding.summary,
        question: finding.question,
        revision: review.revision,
        evidence: finding.evidence,
        title: `[Architecture] ${finding.title}`,
      });
      if (result) result.innerHTML = `Saved to GitHub: <a href="${escape(response.url)}" target="_blank" rel="noreferrer">Open the ${escape(response.action)}</a>. The live record below refreshes automatically.`;
      await refreshDiscussion();
    } catch (error) {
      if (result) { result.className = "notice error"; result.textContent = error instanceof Error ? error.message : "Could not post feedback."; }
    } finally {
      if (submit) submit.disabled = false;
    }
  });
}

async function start() {
  app.innerHTML = `<div class="main"><p class="notice">Generating the architecture review…</p></div>`;
  try {
    const fragment = new URLSearchParams(location.hash.slice(1));
    state.reviewKey = fragment.get("review-key") ?? "";
    if (fragment.has("review-key")) history.replaceState(null, "", location.pathname + location.search);
    const response = await fetch("/review.json");
    if (!response.ok) throw new Error(`Could not load review.json (${response.status})`);
    review = parseReview(await response.json());
    config = await loadConfig();
    issues = await loadIssues(review.findings.flatMap((finding) => finding.issueNumbers));
    state.selectedId = review.findings[0]?.id ?? "";
    document.documentElement.dataset.theme = window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark";
    render();
    window.setInterval(() => { if (!document.hidden && !discussionBusy) void refreshDiscussion(); }, 30_000);
    document.addEventListener("visibilitychange", () => { if (!document.hidden) void refreshDiscussion(); });
  } catch (error) {
    app.innerHTML = `<main class="main"><div class="notice error">${escape(error instanceof Error ? error.message : "Could not load the review")}</div></main>`;
  }
}

void start();
