import "./styles.css";
import { parseReview, type Area, type CommitRef, type Day, type Evidence, type Finding, type IssueRef, type Theme, type WeekReview } from "./model";

const mount = document.querySelector<HTMLDivElement>("#app");
if (!mount) throw new Error("Missing #app");
const app: HTMLDivElement = mount;

const escape = (value: string) => value
  .replaceAll("&", "&amp;")
  .replaceAll("<", "&lt;")
  .replaceAll(">", "&gt;")
  .replaceAll('"', "&quot;")
  .replaceAll("'", "&#039;");
const number = (value: number) => value.toLocaleString("en-US");
const dayName = (date: string) => new Date(`${date}T12:00:00Z`).toLocaleDateString("en-GB", { weekday: "long", day: "numeric", month: "long", timeZone: "UTC" });
const external = (url: string, body: string, className = "") => `<a${className ? ` class="${className}"` : ""} href="${escape(url)}" target="_blank" rel="noreferrer">${body}</a>`;

const issueLink = (issue: IssueRef) => external(issue.url, `${escape(issue.title)} <span class="ref">#${issue.n}</span>`, "link");
const commitLink = (commit: CommitRef) => external(commit.url, `${escape(commit.title)} <span class="ref">${escape(commit.sha)}</span>`, "link");
const evidenceLink = (evidence: Evidence) => external(evidence.url, `${escape(evidence.label)}${evidence.ref ? ` <span class="ref">${escape(evidence.ref)}</span>` : ""}`, "link");

function header(review: WeekReview) {
  const { totals } = review;
  return `
    <header class="masthead">
      <p class="kicker">Sattle week review, ${escape(dayName(review.period.from))} to ${escape(dayName(review.period.to))}</p>
      <h1>What was built this week, and where effort was wasted</h1>
      <p class="lede">${escape(review.summary)}</p>
      <dl class="totals">
        <div><dt>Commits</dt><dd>${number(totals.commits)}</dd></div>
        <div><dt>Issues closed</dt><dd>${number(totals.issuesClosed)}</dd></div>
        <div><dt>Lines added</dt><dd>+${number(totals.added)}</dd></div>
        <div><dt>Lines removed</dt><dd>−${number(totals.deleted)}</dd></div>
      </dl>
    </header>`;
}

function controls(review: WeekReview) {
  const buttons = review.areas.map((area) => `<button type="button" class="chip" data-filter="${area.id}" data-area="${area.id}" aria-pressed="false"><span class="swatch" aria-hidden="true"></span>${escape(area.name)}</button>`).join("");
  return `
    <nav class="controls" aria-label="Sections and areas">
      <div class="sections">
        <a href="#timeline">Timeline</a><a href="#map">Map</a><a href="#built">Built</a><a href="#waste">Waste</a><a href="#tokens">Tokens</a><a href="#flow">Flow</a>
      </div>
      <div class="filters" role="group" aria-label="Show one area">
        <button type="button" class="chip" data-filter="all" aria-pressed="true">All areas</button>${buttons}
      </div>
    </nav>`;
}

function dayColumn(day: Day, index: number) {
  const squares = day.commits.map((commit, at) => `<li data-area="${commit.area}" style="--i:${at}">${external(commit.url, `<span class="visually-hidden">${escape(commit.sha)}: ${escape(commit.title)}</span>`, "square")}</li>`).join("");
  const closed = day.closed.length
    ? `<details class="closed"><summary>${day.closed.length} ${day.closed.length === 1 ? "issue" : "issues"} closed</summary><ul>${day.closed.map((issue) => `<li data-area="${issue.area}">${issueLink(issue)}</li>`).join("")}</ul></details>`
    : `<p class="quiet">No issue closed.</p>`;
  return `
    <article class="day" style="--d:${index}">
      <h3>${escape(dayName(day.date))}</h3>
      <p class="count">${day.commits.length} ${day.commits.length === 1 ? "commit" : "commits"}</p>
      <p>${escape(day.note)}</p>
      <ul class="squares" aria-label="Commits on ${escape(dayName(day.date))}">${squares}</ul>
      ${closed}
    </article>`;
}

function timeline(review: WeekReview) {
  return `
    <section id="timeline" aria-labelledby="timeline-title">
      <h2 id="timeline-title">The week, day by day</h2>
      <p class="section-note">Each square is one commit, coloured by the area it changed most. Open a square to see the commit on GitHub.</p>
      <div class="days">${review.days.map(dayColumn).join("")}</div>
    </section>`;
}

function areaTile(area: Area, review: WeekReview) {
  const lines = area.added + area.deleted;
  const widest = Math.max(1, ...area.folders.map((folder) => folder.lines));
  const folders = area.folders.map((folder) => `
    <li>
      ${external(`https://github.com/${review.repository}/tree/${review.revision}/${folder.path}`, escape(folder.path), "link")}
      <span class="bar" style="--share:${(folder.lines / widest).toFixed(3)}" aria-hidden="true"></span>
      <span class="num">${number(folder.lines)} lines, ${folder.commits} ${folder.commits === 1 ? "commit" : "commits"}</span>
    </li>`).join("");
  return `
    <article class="tile${lines >= 10_000 ? " wide" : ""}" data-area="${area.id}">
      <h3><span class="swatch" aria-hidden="true"></span>${escape(area.name)}</h3>
      <p>${escape(area.about)}</p>
      <p class="num">${area.commits} commits, +${number(area.added)} −${number(area.deleted)}</p>
      ${folders ? `<ul class="folders">${folders}</ul>` : `<p class="quiet">Its work lives in issues and session notes, not in the repository.</p>`}
      <a class="more" href="#built-${area.id}">What was built</a>
    </article>`;
}

function map(review: WeekReview) {
  return `
    <section id="map" aria-labelledby="map-title">
      <h2 id="map-title">Where the changes went</h2>
      <p class="section-note">Areas that changed more than 10,000 lines get a wide tile. The bars show the folders that changed most.</p>
      <div class="tiles">${review.areas.map((area) => areaTile(area, review)).join("")}</div>
    </section>`;
}

function themeCard(theme: Theme) {
  const commits = theme.commits.length
    ? `<details class="commits"><summary>${theme.commits.length} ${theme.commits.length === 1 ? "commit" : "commits"}, ${number(theme.lines)} lines</summary><ul>${theme.commits.map((commit) => `<li>${commitLink(commit)}</li>`).join("")}</ul></details>`
    : `<p class="quiet">No commits: this work lives in issues and the run's decision list.</p>`;
  return `
    <article class="theme" data-area="${theme.area}">
      <h4><span class="swatch" aria-hidden="true"></span>${escape(theme.title)}</h4>
      <p>${escape(theme.what)}</p>
      <p class="why"><strong>Why.</strong> ${escape(theme.why)}</p>
      <ul class="issues">${theme.issues.map((issue) => `<li>${issueLink(issue)}</li>`).join("")}</ul>
      ${commits}
      ${theme.shots.map((shot) => `<figure class="shot"><img src="${escape(shot.src)}" width="${shot.width}" height="${shot.height}" alt="${escape(shot.caption)}" loading="lazy" decoding="async"><figcaption>${escape(shot.caption)}</figcaption></figure>`).join("")}
    </article>`;
}

function built(review: WeekReview) {
  const groups = review.areas.map((area) => {
    const themes = review.themes.filter((theme) => theme.area === area.id);
    if (!themes.length) return "";
    return `
      <section class="area-group" id="built-${area.id}" data-area="${area.id}" aria-labelledby="built-${area.id}-title">
        <h3 id="built-${area.id}-title"><span class="swatch" aria-hidden="true"></span>${escape(area.name)}</h3>
        <div class="themes">${themes.map(themeCard).join("")}</div>
      </section>`;
  }).join("");
  return `
    <section id="built" aria-labelledby="built-title">
      <h2 id="built-title">What was built, and why</h2>
      ${groups}
    </section>`;
}

const VERDICT = { stop: "Stop", keep: "Keep", change: "Change" } as const;

function findingCard(finding: Finding) {
  return `
    <article class="finding" data-severity="${finding.severity}">
      <h4>${escape(finding.title)}</h4>
      <p>${escape(finding.plain)}</p>
      <p class="cost"><span class="label">Cost</span> ${escape(finding.cost)}</p>
      <p class="advice" data-verdict="${finding.verdict}"><span class="verdict">${VERDICT[finding.verdict]}</span> ${escape(finding.suggestion)}</p>
      <ul class="evidence" aria-label="Evidence">${finding.evidence.map((evidence) => `<li>${evidenceLink(evidence)}</li>`).join("")}</ul>
    </article>`;
}

function waste(review: WeekReview) {
  return `
    <section id="waste" aria-labelledby="waste-title">
      <h2 id="waste-title">Where effort was wasted</h2>
      <p class="section-note">Each finding links to its evidence and ends with a recommendation: stop it, keep it, or change it.</p>
      <h3>Work redone or lost</h3>
      <div class="findings">${review.waste.map(findingCard).join("")}</div>
      <h3>Overdone</h3>
      <div class="findings">${review.overdone.map(findingCard).join("")}</div>
      <h3>What worked</h3>
      <ul class="good">${review.good.map((item) => `<li><strong>${escape(item.title)}.</strong> ${escape(item.plain)}</li>`).join("")}</ul>
    </section>`;
}

const millions = (value: number) => `${(value / 1e6).toLocaleString("en-US", { maximumFractionDigits: 1 })} M`;
const percent = (value: number) => `${(value * 100).toLocaleString("en-US", { maximumFractionDigits: 1 })}%`;

function barRow(label: string, parts: { value: number; className: string }[], widest: number, readout: string) {
  const total = parts.reduce((sum, part) => sum + part.value, 0);
  const segments = parts.map((part) => `<span class="${part.className}" style="--share:${(part.value / Math.max(1, total)).toFixed(4)}"></span>`).join("");
  return `<li><span class="bar-label">${label}</span><span class="stack" style="--share:${(total / Math.max(1, widest)).toFixed(4)}" aria-hidden="true">${segments}</span><span class="num">${readout}</span></li>`;
}

function tokens(review: WeekReview) {
  const data = review.tokens;
  const cacheShare = (0.1 * data.cacheRead) / data.total;
  const dayMax = Math.max(...data.days.map((day) => day.inputEquivalent));
  const kindMax = Math.max(...data.kinds.map((kind) => kind.own + kind.subagents));
  const issueMax = Math.max(...data.issues.map((issue) => issue.inputEquivalent));
  const largest = [...data.models].sort((left, right) => right.inputEquivalent - left.inputEquivalent)[0];
  const modelTotal = data.models.reduce((sum, model) => sum + model.inputEquivalent, 0);
  return `
    <section id="tokens" aria-labelledby="tokens-title">
      <h2 id="tokens-title">What the run cost in tokens</h2>
      <p class="section-note">${percent(cacheShare)} of the input side was re-reading context already in the cache. Turns with more than ${number(data.longContext.threshold)} tokens of context carried ${percent(data.longContext.share)} of it. ${escape(largest.name)} did ${percent(largest.inputEquivalent / modelTotal)} of the work.</p>
      <dl class="totals">
        <div><dt>Input-equivalent</dt><dd>${millions(data.total)}</dd></div>
        <div><dt>Cache reads</dt><dd>${millions(data.cacheRead)}</dd></div>
        <div><dt>Output</dt><dd>${millions(data.output)}</dd></div>
        <div><dt>Sessions</dt><dd>${number(data.sessions)}</dd></div>
      </dl>
      <div class="token-grid">
        <div>
          <h3>Per day</h3>
          <p class="legend"><span class="key cache"></span>cache reads, one tenth <span class="key fresh"></span>new input and cache writes</p>
          <ul class="bars">${data.days.map((day) => barRow(escape(dayName(day.date)), [{ value: 0.1 * day.cacheRead, className: "cache" }, { value: day.input + day.cacheWrite, className: "fresh" }], dayMax, `${millions(day.inputEquivalent)}, ${day.sessions} ${day.sessions === 1 ? "session" : "sessions"}`)).join("")}</ul>
        </div>
        <div>
          <h3>Per kind of session</h3>
          <p class="legend"><span class="key fresh"></span>the session <span class="key sub"></span>its subagents</p>
          <ul class="bars">${data.kinds.map((kind) => barRow(escape(kind.name), [{ value: kind.own, className: "fresh" }, { value: kind.subagents, className: "sub" }], kindMax, `${millions(kind.own + kind.subagents)}, ${kind.sessions} ${kind.sessions === 1 ? "session" : "sessions"}`)).join("")}</ul>
        </div>
        <div class="wide-block">
          <h3>Per issue, sessions started for one issue</h3>
          <ul class="bars columns">${data.issues.map((issue) => barRow(issueLink(issue), [{ value: issue.inputEquivalent, className: "fresh" }], issueMax, millions(issue.inputEquivalent))).join("")}</ul>
        </div>
      </div>
      <details class="method"><summary>How these numbers were counted</summary><p>${escape(data.method)}</p><p>${escape(data.weights)}</p></details>
    </section>`;
}

function flow(review: WeekReview) {
  const { steps, loops } = review.flow;
  const position = new Map(steps.map((step, index) => [step.id, index]));
  const title = (id: string) => steps[position.get(id) ?? 0].title;
  return `
    <section id="flow" aria-labelledby="flow-title">
      <h2 id="flow-title">${escape(review.flow.title)}</h2>
      <p class="section-note">The planned path runs left to right. The loops below are where this week's work went back, each with an example.</p>
      <figure class="flow" data-state="idle">
        <ol class="steps">${steps.map((step, index) => `<li style="--i:${index}"><strong>${escape(step.title)}</strong><span>${escape(step.text)}</span></li>`).join("")}</ol>
        <ul class="loops">${loops.map((loop, index) => `
          <li style="--i:${steps.length + index}">
            <span class="hop"><span>${escape(title(loop.from))}</span><span class="visually-hidden">back to</span><svg aria-hidden="true" viewBox="0 0 24 24" width="18" height="18"><path d="M4 12h15m-6-6 6 6-6 6" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg><span>${escape(title(loop.to))}</span></span>
            <span>${escape(loop.title)}. ${evidenceLink(loop.evidence)}</span>
          </li>`).join("")}</ul>
        <figcaption><button type="button" class="play" aria-describedby="flow-title">Play the path</button></figcaption>
      </figure>
    </section>`;
}

function footer(review: WeekReview) {
  const url = `https://github.com/${review.repository}/commit/${review.revision}`;
  return `<footer class="foot">Built from the repository at ${external(url, `<span class="ref">${escape(review.revision.slice(0, 7))}</span>`, "link")} and the issues it names. Every figure comes from git; every judgement links to its evidence.</footer>`;
}

function applyFilter(area: string) {
  for (const button of app.querySelectorAll<HTMLButtonElement>("[data-filter]")) button.setAttribute("aria-pressed", String(button.dataset.filter === area));
  for (const element of app.querySelectorAll<HTMLElement>("main [data-area]")) {
    element.classList.toggle("is-dimmed", area !== "all" && element.dataset.area !== area);
  }
  for (const group of app.querySelectorAll<HTMLElement>(".area-group")) group.hidden = area !== "all" && group.dataset.area !== area;
}

function playFlow() {
  const figure = app.querySelector<HTMLElement>(".flow");
  if (!figure) return;
  figure.dataset.state = "idle";
  void figure.offsetWidth;
  figure.dataset.state = "playing";
}

async function start() {
  try {
    const response = await fetch("./review.json", { cache: "no-store" });
    if (!response.ok) throw new Error(`review.json answered ${response.status}`);
    const review = parseReview(await response.json());
    app.innerHTML = `${header(review)}${controls(review)}<main id="review-main">${timeline(review)}${map(review)}${built(review)}${waste(review)}${tokens(review)}${flow(review)}</main>${footer(review)}`;
    app.addEventListener("click", (event) => {
      const target = event.target as HTMLElement;
      const filter = target.closest<HTMLButtonElement>("[data-filter]");
      if (filter) applyFilter(filter.dataset.filter ?? "all");
      if (target.closest(".play")) playFlow();
    });
    document.documentElement.dataset.ready = "true";
  } catch (error) {
    app.innerHTML = `<main id="review-main" class="failure"><h1>The review could not load</h1><p>${escape(error instanceof Error ? error.message : String(error))}</p></main>`;
  }
}

void start();
