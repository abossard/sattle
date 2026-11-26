import "@fontsource/ibm-plex-sans/latin-400.css";
import "@fontsource/ibm-plex-sans/latin-600.css";
import "@fontsource/ibm-plex-mono/latin-400.css";
import "@fontsource/ibm-plex-mono/latin-600.css";
import "./styles.css";
import { CHANGES_SCRIPT, runChanges, type Change, type Frame } from "./sim/changes";
import { step, stepper, type StepEvent, type Stepper } from "./stepper";
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
        <a href="#timeline">Timeline</a><a href="#map">Map</a><a href="#explain">Ideas</a><a href="#built">Built</a><a href="#waste">Waste</a><a href="#tokens">Tokens</a><a href="#flow">Flow</a>
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
    <article class="day" data-step="${index}" style="--d:${index}">
      <h3>${escape(dayName(day.date))}</h3>
      <p class="count">${day.commits.length} ${day.commits.length === 1 ? "commit" : "commits"}</p>
      <p>${escape(day.note)}</p>
      <ul class="squares" aria-label="Commits on ${escape(dayName(day.date))}">${squares}</ul>
      ${closed}
    </article>`;
}

const stepperControls = (name: string, label: string) => `
  <div class="stepper" data-stepper="${name}" role="group" aria-label="${escape(label)}">
    <button type="button" class="step-button" data-event="back">Back</button>
    <button type="button" class="step-button primary" data-event="play">Play</button>
    <button type="button" class="step-button" data-event="next">Next</button>
    <button type="button" class="step-button quiet-button" data-event="all">Show all</button>
    <span class="step-status" aria-live="polite"></span>
  </div>`;

function timeline(review: WeekReview) {
  return `
    <section id="timeline" aria-labelledby="timeline-title">
      <h2 id="timeline-title">The week, day by day</h2>
      <p class="section-note">Each square is one commit, coloured by the area it changed most. Open a square to see the commit on GitHub. Play replays the week one day at a time.</p>
      ${stepperControls("week", "Replay the week")}
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

const sign = (weight: number) => (weight > 0 ? "+1" : "−1");
const rowChip = <T,>(change: Change<T>, text: (row: T) => string, column: number, index: number) =>
  `<li class="row-chip" data-weight="${change.weight > 0 ? "add" : "remove"}" style="--c:${column};--i:${index}"><span class="weight">${sign(change.weight)}</span>${escape(text(change.row))}</li>`;
const rowList = (items: string[], empty: string) => items.length ? `<ul class="rows">${items.join("")}</ul>` : `<p class="quiet">${empty}</p>`;

function changesStage(frame: Frame) {
  const row = (value: { service: string; status: string }) => `${value.service}, ${value.status}`;
  const count = (value: { service: string; errors: number }) => `${value.service}: ${value.errors} ${value.errors === 1 ? "error" : "errors"}`;
  const state = (list: { service: string; errors: number }[]) => list.length ? list.map((item) => `${item.service} ${item.errors}`).join(", ") : "empty";
  const arrow = `<svg class="stage-arrow" aria-hidden="true" viewBox="0 0 24 24" width="20" height="20"><path d="M4 12h15m-6-6 6 6-6 6" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>`;
  return `
    <div class="stage-column"><h4>Input changes</h4>${rowList(frame.input.map((change, index) => rowChip(change, row, 0, index)), "none")}</div>${arrow}
    <div class="stage-column"><h4>Map: keep errors</h4>${rowList(frame.mapped.map((change, index) => rowChip(change, row, 1, index)), "nothing kept")}</div>${arrow}
    <div class="stage-column"><h4>Reduce: errors per service</h4><p class="state"><span class="label">State before</span> ${escape(state(frame.before))}</p><p class="state"><span class="label">Keys touched</span> ${escape(frame.touched.join(", ") || "none")}</p><p class="state"><span class="label">State after</span> ${escape(state(frame.after))}</p></div>${arrow}
    <div class="stage-column"><h4>Output changes</h4>${rowList(frame.output.map((change, index) => rowChip(change, count, 3, index)), "nothing changed")}</div>`;
}

function explain() {
  const explainers = [{ id: "changes", title: "Changes as +1 and −1", script: CHANGES_SCRIPT }];
  return `
    <section id="explain" aria-labelledby="explain-title">
      <h2 id="explain-title">How this week's ideas work</h2>
      <p class="section-note">Small simulators of the main ideas. Step through them with Back and Next, or press Play.</p>
      <div class="tabs" role="tablist" aria-label="Explainers">${explainers.map((item, index) => `<button type="button" role="tab" id="tab-${item.id}" aria-controls="panel-${item.id}" aria-selected="${index === 0}" tabindex="${index === 0 ? 0 : -1}">${escape(item.title)}</button>`).join("")}</div>
      ${explainers.map((item, index) => `
        <div class="tab-panel explainer" role="tabpanel" id="panel-${item.id}" aria-labelledby="tab-${item.id}" data-explainer="${item.id}"${index === 0 ? "" : " hidden"}>
          <h3>${escape(item.script.title)}</h3>
          <p>${escape(item.script.lede)}</p>
          ${stepperControls(item.id, item.script.title)}
          <ol class="batches">${item.script.batches.map((_batch, at) => `<li data-step="${at}">Batch ${at + 1}</li>`).join("")}</ol>
          <div class="stage" aria-live="polite"></div>
          <p class="stage-note" aria-live="polite"></p>
        </div>`).join("")}
    </section>`;
}

function mountExplainers() {
  const panel = app.querySelector<HTMLElement>('[data-explainer="changes"]');
  if (!panel) return;
  const frames = runChanges(CHANGES_SCRIPT.batches);
  const stage = panel.querySelector<HTMLElement>(".stage")!;
  const note = panel.querySelector<HTMLElement>(".stage-note")!;
  mountStepper(panel, (at) => `batch ${at + 1}`, (at) => {
    const index = at ?? 0;
    stage.innerHTML = changesStage(frames[index]);
    note.textContent = CHANGES_SCRIPT.notes[index];
  });
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
  const plural = (count: number) => `${count} ${count === 1 ? "session" : "sessions"}`;
  const panels = [
    { id: "day", title: "Per day", body: `
      <p class="legend"><span class="key cache"></span>cache reads, one tenth <span class="key fresh"></span>new input and cache writes</p>
      <ul class="bars">${data.days.map((day) => barRow(escape(dayName(day.date)), [{ value: 0.1 * day.cacheRead, className: "cache" }, { value: day.input + day.cacheWrite, className: "fresh" }], dayMax, `${millions(day.inputEquivalent)}, ${plural(day.sessions)}`)).join("")}</ul>` },
    { id: "kind", title: "Per kind of session", body: `
      <p class="legend"><span class="key fresh"></span>the session <span class="key sub"></span>its subagents</p>
      <ul class="bars">${data.kinds.map((kind) => barRow(escape(kind.name), [{ value: kind.own, className: "fresh" }, { value: kind.subagents, className: "sub" }], kindMax, `${millions(kind.own + kind.subagents)}, ${plural(kind.sessions)}`)).join("")}</ul>` },
    { id: "issue", title: "Per issue", body: `
      <p class="legend">Sessions started for one issue only.</p>
      <ul class="bars columns">${data.issues.map((issue) => barRow(issueLink(issue), [{ value: issue.inputEquivalent, className: "fresh" }], issueMax, millions(issue.inputEquivalent))).join("")}</ul>` },
  ];
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
      <div class="tabs" role="tablist" aria-label="Token charts">${panels.map((panel, index) => `<button type="button" role="tab" id="tab-${panel.id}" aria-controls="panel-${panel.id}" aria-selected="${index === 0}" tabindex="${index === 0 ? 0 : -1}">${panel.title}</button>`).join("")}</div>
      ${panels.map((panel, index) => `<div class="tab-panel" role="tabpanel" id="panel-${panel.id}" aria-labelledby="tab-${panel.id}"${index === 0 ? "" : " hidden"}>${panel.body}</div>`).join("")}
      <details class="method"><summary>How these numbers were counted</summary><p>${escape(data.method)}</p><p>${escape(data.weights)}</p></details>
    </section>`;
}

const NODE = { width: 128, height: 60, gap: 28, top: 24 };
const nodeX = (index: number) => 16 + index * (NODE.width + NODE.gap);

function flowDrawing(review: WeekReview) {
  const { steps, loops } = review.flow;
  const position = new Map(steps.map((item, index) => [item.id, index]));
  const width = nodeX(steps.length - 1) + NODE.width + 16;
  const bottom = NODE.top + NODE.height;
  const spans = loops.map((loop) => Math.abs((position.get(loop.from) ?? 0) - (position.get(loop.to) ?? 0)));
  const depth = (index: number) => 30 + 26 * [...new Set(spans)].sort((left, right) => left - right).indexOf(spans[index]) * 1.6;
  const height = bottom + Math.max(...loops.map((_loop, index) => depth(index))) + 40;
  const arrows = steps.slice(0, -1).map((_item, index) => {
    const x1 = nodeX(index) + NODE.width;
    const x2 = nodeX(index + 1) - 4;
    const y = NODE.top + NODE.height / 2;
    return `<path class="edge" d="M${x1} ${y}H${x2}" marker-end="url(#arrow)"/>`;
  }).join("");
  const arcs = loops.map((loop, index) => {
    const from = nodeX(position.get(loop.from) ?? 0) + NODE.width / 2;
    const to = nodeX(position.get(loop.to) ?? 0) + NODE.width / 2;
    const dip = bottom + depth(index) * 1.33;
    const labelY = bottom + depth(index) + 4;
    return `<g class="loop" data-from="${loop.from}" data-to="${loop.to}">
      <path class="edge back" d="M${from} ${bottom} C ${from} ${dip}, ${to} ${dip}, ${to} ${bottom + 4}" marker-end="url(#arrow-back)"/>
      <text class="halo loop-label" x="${(from + to) / 2}" y="${labelY}" text-anchor="middle">${escape(loop.title)}</text>
    </g>`;
  }).join("");
  const nodes = steps.map((item, index) => `
    <g class="node" data-step="${index}" data-id="${item.id}" tabindex="0" role="button" aria-label="Step ${index + 1}: ${escape(item.title)}">
      <rect x="${nodeX(index)}" y="${NODE.top}" width="${NODE.width}" height="${NODE.height}" rx="8"/>
      <text class="step-number" x="${nodeX(index) + 10}" y="${NODE.top + 20}">${index + 1}</text>
      <text class="step-title" x="${nodeX(index) + 10}" y="${NODE.top + 42}">${escape(item.title)}</text>
    </g>`).join("");
  return `<svg viewBox="0 0 ${width} ${height}" width="${width}" height="${height}" role="group" aria-label="${escape(review.flow.title)}">
    <defs>
      <marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path class="arrow-head" d="M0 0L10 5L0 10z"/></marker>
      <marker id="arrow-back" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path class="arrow-head back" d="M0 0L10 5L0 10z"/></marker>
    </defs>
    ${arrows}${arcs}${nodes}
  </svg>`;
}

function flowDetail(review: WeekReview, id: string) {
  const item = review.flow.steps.find((candidate) => candidate.id === id) ?? review.flow.steps[0];
  const titles = new Map(review.flow.steps.map((candidate) => [candidate.id, candidate.title]));
  const touching = review.flow.loops.filter((loop) => loop.from === item.id || loop.to === item.id);
  return `
    <h3>${escape(item.title)}</h3>
    <p>${escape(item.text)}</p>
    ${touching.length ? `<p class="label">Where work went back</p><ul class="evidence">${touching.map((loop) => `<li>${escape(titles.get(loop.from) ?? "")} back to ${escape(titles.get(loop.to) ?? "")}: ${escape(loop.title)}. ${evidenceLink(loop.evidence)}</li>`).join("")}</ul>` : `<p class="quiet">No work went back at this step this week.</p>`}`;
}

function flow(review: WeekReview) {
  return `
    <section id="flow" aria-labelledby="flow-title">
      <h2 id="flow-title">${escape(review.flow.title)}</h2>
      <p class="section-note">The planned path runs left to right. The dashed arcs are where this week's work went back. Select a step to read it; Play walks the path.</p>
      ${stepperControls("flow", "Walk the path")}
      <div class="flow-grid">
        <div class="drawing" tabindex="0" role="region" aria-label="Drawing of the path, scrolls sideways">${flowDrawing(review)}</div>
        <aside class="flow-detail" aria-live="polite">${flowDetail(review, review.flow.steps[0].id)}</aside>
      </div>
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

/** One stepper per section: the state machine in ./stepper, its timer and its view here. */
function mountStepper(section: HTMLElement, describe: (at: number) => string, onStep: (at: number | null) => void) {
  const steps = [...section.querySelectorAll<HTMLElement | SVGElement>("[data-step]")];
  const controls = section.querySelector<HTMLElement>(".stepper");
  if (!controls) return;
  let state: Stepper = stepper(steps.length);
  let timer: number | undefined;
  const view = () => {
    for (const element of steps) {
      const index = Number(element.dataset.step);
      element.classList.toggle("is-future", state.at !== null && index > state.at);
      element.classList.toggle("is-current", state.at === index);
    }
    const play = controls.querySelector<HTMLButtonElement>('[data-event="play"], [data-event="pause"]');
    if (play) { play.dataset.event = state.playing ? "pause" : "play"; play.textContent = state.playing ? "Pause" : "Play"; }
    controls.querySelector<HTMLButtonElement>('[data-event="back"]')!.disabled = state.at === 0;
    controls.querySelector<HTMLButtonElement>('[data-event="next"]')!.disabled = state.at === state.count - 1;
    controls.querySelector<HTMLElement>(".step-status")!.textContent = state.at === null ? "" : `${state.at + 1} of ${state.count}: ${describe(state.at)}`;
    onStep(state.at);
  };
  const send = (event: StepEvent) => {
    state = step(state, event);
    window.clearInterval(timer);
    timer = state.playing ? window.setInterval(() => send("tick"), 1400) : undefined;
    view();
  };
  controls.addEventListener("click", (event) => {
    const button = (event.target as HTMLElement).closest<HTMLButtonElement>("[data-event]");
    if (button) send(button.dataset.event as StepEvent);
  });
  view();
}

function mountTabs(list: HTMLElement) {
  const tabs = [...list.querySelectorAll<HTMLButtonElement>('[role="tab"]')];
  const select = (tab: HTMLButtonElement) => {
    for (const other of tabs) {
      const selected = other === tab;
      other.setAttribute("aria-selected", String(selected));
      other.tabIndex = selected ? 0 : -1;
      document.getElementById(other.getAttribute("aria-controls") ?? "")!.hidden = !selected;
    }
  };
  list.addEventListener("click", (event) => {
    const tab = (event.target as HTMLElement).closest<HTMLButtonElement>('[role="tab"]');
    if (tab) select(tab);
  });
  list.addEventListener("keydown", (event) => {
    const index = tabs.indexOf(document.activeElement as HTMLButtonElement);
    const next = { ArrowRight: index + 1, ArrowLeft: index - 1, Home: 0, End: tabs.length - 1 }[event.key];
    if (index < 0 || next === undefined) return;
    event.preventDefault();
    const tab = tabs[(next + tabs.length) % tabs.length];
    select(tab);
    tab.focus();
  });
}

function mountFlow(review: WeekReview) {
  const section = app.querySelector<HTMLElement>("#flow");
  const detail = app.querySelector<HTMLElement>(".flow-detail");
  if (!section || !detail) return;
  const show = (id: string) => {
    detail.innerHTML = flowDetail(review, id);
    for (const node of section.querySelectorAll<SVGGElement>(".node")) node.classList.toggle("is-selected", node.dataset.id === id);
    for (const loop of section.querySelectorAll<SVGGElement>(".loop")) loop.classList.toggle("is-related", loop.dataset.from === id || loop.dataset.to === id);
  };
  section.addEventListener("click", (event) => {
    const node = (event.target as Element).closest<SVGGElement>(".node");
    if (node?.dataset.id) show(node.dataset.id);
  });
  section.addEventListener("keydown", (event) => {
    const node = (event.target as Element).closest<SVGGElement>(".node");
    if (node?.dataset.id && (event.key === "Enter" || event.key === " ")) { event.preventDefault(); show(node.dataset.id); }
  });
  mountStepper(section, (at) => review.flow.steps[at].title, (at) => { if (at !== null) show(review.flow.steps[at].id); });
  show(review.flow.steps[0].id);
}

async function start() {
  try {
    const response = await fetch("./review.json", { cache: "no-store" });
    if (!response.ok) throw new Error(`review.json answered ${response.status}`);
    const review = parseReview(await response.json());
    app.innerHTML = `${header(review)}${controls(review)}<main id="review-main">${timeline(review)}${map(review)}${explain()}${built(review)}${waste(review)}${tokens(review)}${flow(review)}</main>${footer(review)}`;
    app.addEventListener("click", (event) => {
      const filter = (event.target as HTMLElement).closest<HTMLButtonElement>("[data-filter]");
      if (filter) applyFilter(filter.dataset.filter ?? "all");
    });
    mountStepper(app.querySelector<HTMLElement>("#timeline")!, (at) => dayName(review.days[at].date), () => undefined);
    for (const list of app.querySelectorAll<HTMLElement>('[role="tablist"]')) mountTabs(list);
    mountExplainers();
    mountFlow(review);
    document.documentElement.dataset.ready = "true";
  } catch (error) {
    app.innerHTML = `<main id="review-main" class="failure"><h1>The review could not load</h1><p>${escape(error instanceof Error ? error.message : String(error))}</p></main>`;
  }
}

void start();
