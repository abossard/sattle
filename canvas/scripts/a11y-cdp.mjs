// Accessibility checks in real Chrome, driven over CDP: axe-core with color contrast on every view, theme and dialog,
// and keyboard-only walkthroughs with real key events (tab order, tab list arrows, dialog focus, focus visibility).
// Usage: node scripts/a11y-cdp.mjs [base-url] [chrome-path]   (serve the app first: npm run dev, or vite preview; default http://localhost:5173/)
// Exit code: 0 all checks ran and passed, 1 a check failed, 2 all that ran passed but some were skipped (no engine, no document graph, ...; A11Y_ALLOW_SKIP=1 accepts that).
import { spawn } from "node:child_process";
import { mkdtempSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createRequire } from "node:module";

// Vite binds localhost (often IPv6 only), so 127.0.0.1 does not connect
const base = process.argv[2] ?? "http://localhost:5173/";
const chrome = process.argv[3] ?? "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const axeSource = readFileSync(createRequire(import.meta.url).resolve("axe-core/axe.min.js"), "utf8");
const port = 9300 + Math.floor(Math.random() * 600);
const proc = spawn(chrome, ["--headless=new", `--remote-debugging-port=${port}`, `--user-data-dir=${mkdtempSync(join(tmpdir(), "sattle-a11y-"))}`, "--window-size=1440,900", "--use-angle=metal", "--ignore-gpu-blocklist", "--no-first-run", "about:blank"], { stdio: "ignore", detached: true });
const stop = () => { try { process.kill(-proc.pid); } catch { proc.kill(); } };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let targets;
for (let i = 0; i < 50; i++) { try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); if (targets.length) break; } catch {} await sleep(200); }
const ws = new WebSocket(targets.find((t) => t.type === "page").webSocketDebuggerUrl);
await new Promise((r) => (ws.onopen = r));
let id = 0; const pending = new Map();
ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } };
const send = (method, params = {}) => new Promise((res) => { const i = ++id; pending.set(i, res); ws.send(JSON.stringify({ id: i, method, params })); });
const evaluate = async (expression) => {
  const r = await send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true });
  if (r.result.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails).slice(0, 400));
  return r.result.result.value;
};
const key = async (k, { shift = false, ctrl = false } = {}) => {
  const map = { Tab: [9, "Tab"], Enter: [13, "Enter"], Escape: [27, "Escape"], ArrowRight: [39, "ArrowRight"], ArrowDown: [40, "ArrowDown"], " ": [32, "Space"] };
  const text = { Enter: "\r", " ": " " }[k] ?? (k.length === 1 && !ctrl ? k : undefined);
  const [code, kk] = map[k] ?? [k.charCodeAt(0), k];
  const mods = (shift ? 8 : 0) | (ctrl ? 2 : 0);
  await send("Input.dispatchKeyEvent", { type: "keyDown", key: k, code: kk, windowsVirtualKeyCode: code, modifiers: mods, text });
  await send("Input.dispatchKeyEvent", { type: "keyUp", key: k, code: kk, windowsVirtualKeyCode: code, modifiers: mods });
  await sleep(120);
};
const state = (expr) => evaluate(`window.__sattle.store.getState().${expr}`);
const dispatch = (a) => evaluate(`window.__sattle.store.getState().dispatch(${JSON.stringify(a)})`);
const clickText = (sel, text) => evaluate(`(()=>{const e=[...document.querySelectorAll(${JSON.stringify(sel)})].find(x=>x.textContent.trim().startsWith(${JSON.stringify(text)}));if(!e)return false;e.click();return true})()`);
/** A graph that is a tree (or a document) opens on its own tab: the checks of the canvas need the Graph tab. */
const showGraphTab = async () => { await evaluate(`(() => { const t = [...document.querySelectorAll('[aria-label="How to show this graph"] [role=tab]')].find((x) => x.textContent.startsWith("Graph")); if (t) t.click(); })()`); await sleep(1200); };
const who = () => evaluate(`(()=>{const e=document.activeElement;const n=e.getAttribute('aria-label')||e.textContent.trim().slice(0,40)||e.tagName;return n})()`);
/**
 * Whether the focused element shows a focus ring that a person can see. The element must match :focus-visible, and either have a visible outline,
 * or a visible border on its ::after (the Fluent focus indicator), or a style (box shadow, border) that changes between focused and not focused.
 * A shadow or border the element always has is a design, not a ring; being inside a tabster root is not a ring either.
 */
const focusVisible = () => evaluate(`(()=>{
  const e=document.activeElement; if(!e||e===document.body) return false;
  const shown=(c)=>!!c&&c!=='transparent'&&c!=='rgba(0, 0, 0, 0)';
  const outline=(s)=>s.outlineStyle!=='none'&&parseFloat(s.outlineWidth)>0&&shown(s.outlineColor);
  const border=(s)=>s.content!=='none'&&s.borderTopStyle!=='none'&&parseFloat(s.borderTopWidth)>0&&shown(s.borderTopColor);
  const sig=()=>{const c=getComputedStyle(e),a=getComputedStyle(e,'::after');return JSON.stringify([c.outlineStyle,c.outlineWidth,c.outlineColor,c.boxShadow,c.borderTopColor,c.borderTopWidth,a.content,a.borderTopStyle,a.borderTopWidth,a.borderTopColor])};
  if(!e.matches(':focus-visible')) return false;
  const c=getComputedStyle(e),a=getComputedStyle(e,'::after');
  if(outline(c)||border(a)) return true;
  const focused=sig(); e.blur(); const plain=sig(); e.focus();
  return focused!==plain;
})()`);

const results = { axe: [], keyboard: [] };
const fail = [];
await send("Page.enable");
if (process.env.A11Y_SCHEME) await send("Emulation.setEmulatedMedia", { features: [{ name: "prefers-color-scheme", value: process.env.A11Y_SCHEME }] });
await send("Emulation.setDeviceMetricsOverride", { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });
const die = (msg) => { console.error(`a11y: ${msg}`); stop(); process.exit(2); };
const open = async (source, view, theme) => {
  await send("Page.navigate", { url: `${base}#source=${source}&view=${view}` });
  await sleep(800);
  await evaluate("location.reload()");
  await sleep(1500);
  const booted = await evaluate(`typeof window.__sattle !== "undefined"`).catch(() => false);
  if (!booted) {
    for (let i = 0; i < 25 && !(await evaluate(`typeof window.__sattle !== "undefined"`).catch(() => false)); i++) await sleep(200);
    if (!(await evaluate(`typeof window.__sattle !== "undefined"`).catch(() => false))) die(`the app did not start at ${base} (is the dev server or vite preview running there? the title is "${await evaluate("document.title").catch(() => "?")}")`);
  }
  let ready = false;
  for (let i = 0; i < 150 && !ready; i++) { ready = (await evaluate(`window.__sattle.store.getState().load.status`)) === "ready"; if (!ready) await sleep(200); }
  if (!ready) die(`source "${source}" did not load: ${await evaluate(`window.__sattle.store.getState().load.message`)}`);
  if (view === "editor") for (let i = 0; i < 50; i++) { if (await evaluate(`window.__sattle.store.getState().editor.status`) === "ready") break; await sleep(200); }
  await dispatch({ type: "ui/theme", theme });
  await settle();
};
/** Styles transition for a moment after a theme change (and slowly on a busy page): axe must not read colors mid-transition. */
const settle = async () => { await sleep(300); await evaluate(`Promise.all(document.getAnimations().map((a) => a.finished.catch(() => {})))`); await sleep(300); };
const runAxe = async (name) => {
  await evaluate(axeSource);
  const v = await evaluate(`axe.run({include:[['body']],exclude:[['[data-tabster-dummy]']]},{rules:{region:{enabled:false}}}).then(r=>r.violations.map(v=>({id:v.id,help:v.help,impact:v.impact,nodes:v.nodes.slice(0,3).map(n=>n.target.join(' ')+' :: '+(n.any[0]?.message||n.all[0]?.message||''))})))`);
  results.axe.push({ name, violations: v.length, detail: v });
  if (v.length) fail.push(`axe ${name}: ${v.map((x) => x.id).join(", ")}`);
};

const SOURCE = process.env.A11Y_SOURCE ?? "synth-small";
const ENGINE = process.env.A11Y_ENGINE ?? "engine"; // the editor needs the engine: its views are checked on that source
for (const theme of ["light", "dark", "contrast"]) {
  for (const view of ["data", "space", "ontology", "editor"]) {
    await open(view === "editor" ? ENGINE : SOURCE, view, theme);
    await runAxe(`${view}/${theme}`);
  }
}
// no visible label is an internal id (the engine's element ids are hashes): the text of every view of the default source and the labels its canvases draw.
// The Inspector shows an id on purpose, and nothing is selected here.
const ID_PATTERN = "\\b[a-z]{1,3}:[a-z0-9]{20,}\\b";
const idsShown = () => evaluate(`(() => { const re = new RegExp(${JSON.stringify(ID_PATTERN)}); const bad = []; const text = document.body.innerText; const m = re.exec(text); if (m) bad.push("text: " + m[0]); for (const [g, p] of window.__sattle.ws.panes) for (const l of p.labelsShown()) if (re.test(l)) bad.push(g + ": " + l); return bad.slice(0, 5); })()`);
for (const view of ["space", "data", "ontology"]) {
  await open("demo", view, "light");
  const graphs = view === "data" ? await evaluate(`window.__sattle.ws.info.graphs.filter((g) => g.role !== "catalog").map((g) => g.id)`) : [""];
  for (const g of graphs) {
    if (g) { await dispatch({ type: "ui/activeGraph", graph: g }); await sleep(700); }
    const bad = await idsShown();
    results.axe.push({ name: `no internal id as a label: ${view}${g ? `/${g}` : ""}`, found: bad, ok: bad.length === 0 });
    if (bad.length) fail.push(`internal id shown as a label on ${view}${g ? `/${g}` : ""}: ${bad.join("; ")}`);
  }
}
// a document graph in the data view (engine 3.4): needs an engine that serves one, for example the deploy-health fixture
for (const theme of ["light", "dark", "contrast"]) {
  await open(ENGINE, "data", theme);
  const doc = await evaluate(`window.__sattle.ws.info.graphs.find((g) => g.document)?.id ?? ""`);
  if (!doc) { results.axe.push({ name: `document/${theme}`, skipped: "the engine serves no document graph" }); continue; }
  await dispatch({ type: "ui/activeGraph", graph: doc });
  for (let i = 0; i < 50 && !(await evaluate(`!!document.querySelector('article')`)); i++) await sleep(200);
  if (!(await evaluate(`!!document.querySelector('article')`))) { fail.push(`document/${theme}: the document did not render`); continue; }
  await settle();
  await runAxe(`document/${theme}`);
}
// value roles (engine 3.2): a graph whose ontology declares roles is colored by them with no user action, in each theme. The engine's graph
// (A11Y_ROLES_GRAPH, default workload) is looked at in a real screenshot: every legend color of a role shows as pixels in the canvas, every element
// of a type that carries a hinted property has a role, and the legend in the panel names each role.
const ROLES_GRAPH = process.env.A11Y_ROLES_GRAPH ?? "workload";
const rgb = (hex) => { const m = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})/i.exec(hex); return m ? [1, 2, 3].map((i) => parseInt(m[i], 16)) : null; };
for (const theme of ["light", "dark", "contrast"]) {
  const name = `roles ${ROLES_GRAPH}/${theme}`;
  await open(ENGINE, "data", theme);
  if (!(await evaluate(`window.__sattle.ws.info.graphs.some((g) => g.id === ${JSON.stringify(ROLES_GRAPH)})`))) { results.axe.push({ name, skipped: `the engine has no graph ${ROLES_GRAPH}` }); continue; }
  await dispatch({ type: "ui/activeGraph", graph: ROLES_GRAPH });
  await showGraphTab();
  await sleep(3000); await settle();
  const info = JSON.parse(await evaluate(`(() => {
    const ws = window.__sattle.ws, g = ${JSON.stringify(ROLES_GRAPH)}, pane = ws.panes.get(g), gi = ws.graphInfo(g);
    const hinted = new Set((gi.presentation ?? []).map((h) => h.property));
    const typed = new Set(gi.ontology.entityTypes.filter((t) => t.properties.some((p) => hinted.has(p.value))).map((t) => t.id.value));
    let expected = 0; for (const e of ws.histories.get(g).graph.entities()) if (typed.has(e.type)) expected++;
    const cv = [...document.querySelectorAll("canvas")].map((c) => c.getBoundingClientRect()).sort((a, b) => b.width * b.height - a.width * a.height)[0];
    return JSON.stringify({ legend: pane.legendOfTarget("node"), hinted: [...hinted], expected, rect: { x: Math.round(cv.x), y: Math.round(cv.y), w: Math.round(cv.width), h: Math.round(cv.height) }, setting: window.__sattle.store.getState().filters[g]?.colorNodes ?? "@default" });
  })()`));
  if (!info.hinted.length) { fail.push(`${name}: the ontology has no value roles`); continue; }
  if (info.setting !== "@default") fail.push(`${name}: the coloring was changed before the check (${info.setting})`);
  const roles = new Set(info.legend.filter((l) => l.role).map((l) => l.role));
  if (roles.size < 2) fail.push(`${name}: the legend has ${roles.size} role(s), expected at least two`);
  const colored = info.legend.filter((l) => l.role && l.value !== "(no value)").reduce((n, l) => n + l.count, 0);
  if (colored < info.expected) fail.push(`${name}: ${colored} elements have a role, ${info.expected} are of types that carry a hinted property`);
  const shot = (await send("Page.captureScreenshot", { format: "png" })).result.data;
  const want = info.legend.map((l) => rgb(l.color));
  const px = await evaluate(`(async () => {
    const bmp = await createImageBitmap(await (await fetch("data:image/png;base64," + ${JSON.stringify(shot)})).blob());
    const r = ${JSON.stringify(info.rect)}, c = new OffscreenCanvas(r.w, r.h), x = c.getContext("2d");
    x.drawImage(bmp, r.x, r.y, r.w, r.h, 0, 0, r.w, r.h);
    const d = x.getImageData(0, 0, r.w, r.h).data, want = ${JSON.stringify(want)}, out = want.map(() => 0);
    for (let i = 0; i < d.length; i += 4) for (let k = 0; k < want.length; k++) { const w = want[k]; if (w && Math.abs(d[i] - w[0]) <= 12 && Math.abs(d[i + 1] - w[1]) <= 12 && Math.abs(d[i + 2] - w[2]) <= 12) out[k]++; }
    return out;
  })()`);
  info.legend.forEach((l, k) => { if (l.role && l.count > 0 && px[k] < 12) fail.push(`${name}: the color of "${l.value}" (${l.role}, ${l.color}) has ${px[k]} pixels in the canvas`); });
  await clickText("button", "Color by property"); await sleep(400);
  const items = JSON.parse(await evaluate(`JSON.stringify([...document.querySelectorAll('ul[aria-label^="Legend of nodes"] li')].map((li) => li.textContent))`));
  for (const l of info.legend) if (l.role && !items.some((t) => t.includes(l.value) && t.includes(`, ${l.role}`))) fail.push(`${name}: the legend in the panel does not name the role of "${l.value}" (${l.role})`);
  results.roles = [...(results.roles ?? []), { name, legend: info.legend.map((l) => `${l.value} ${l.role} ${l.count} px ${px[info.legend.indexOf(l)]}`) }];
  await runAxe(name);
}
// the default source: the engine's output on the estate (static files, `npm run demo`). The master and every projection are drawn with their
// edges; selecting a lifted edge highlights what it aggregates below it in other graphs, selecting a master node what it is part of above it;
// a projection without edges says so; the supporting elements of the workload health are collapsed onto what they support.
const HAS_EDGES = ["master", "serviceTree", "capabilities", "deploymentHealth", "treeStructure", "treeRollup", "healthTree", "workloadStructure", "workload"];
for (const theme of ["light", "dark", "contrast"]) {
  const name = `default source/${theme}`;
  await open("demo", "space", theme);
  await sleep(1500); await settle();
  const sizes = JSON.parse(await evaluate(`JSON.stringify(window.__sattle.store.getState().graphs.map((g) => [g.id, g.nodes, g.edges]))`));
  for (const id of HAS_EDGES) { const g = sizes.find((x) => x[0] === id); if (!g || !(g[2] > 0)) fail.push(`${name}: ${id} has ${g ? g[2] : "no"} edges`); }
  const notices = JSON.parse(await evaluate(`JSON.stringify([...document.querySelectorAll('[role=group]')].filter((g) => /^Graph [A-Za-z]+$/.test(g.getAttribute("aria-label") ?? "") && /no edges\\. Its ontology/.test(g.textContent)).map((g) => g.getAttribute("aria-label")))`));
  if (JSON.stringify(notices) !== JSON.stringify(["Graph treeSignals"])) fail.push(`${name}: the notice of a projection without edges is on ${JSON.stringify(notices)}, expected only Graph treeSignals`);
  await runAxe(name);
  if (theme !== "light") continue;
  const reach = async (sel) => {
    await dispatch({ type: "select/element", selection: sel });
    await sleep(2500);
    // what the selection reaches in each graph, below it (lower) and above it (upper): tiles that are a list or a count have no pane to count in
    return JSON.parse(await evaluate(`(() => { const c = window.__sattle.ws.closure(${JSON.stringify(sel)}); const n = {}; for (const [g, s] of [...c.lower, ...c.upper]) n[g] = (n[g] ?? 0) + s.size; return JSON.stringify(n); })()`));
  };
  const lifted = await evaluate(`[...window.__sattle.ws.histories.get("capabilities").graph.relationships()].find((r) => r.type.endsWith("DependsOn"))?.id ?? ""`);
  const down = await reach({ graph: "capabilities", kind: "relationship", id: lifted });
  if (!(down.serviceTree > 0 && down.master > 0)) fail.push(`${name}: selecting a lifted edge highlights ${JSON.stringify(down)}, expected elements in serviceTree and master`);
  const service = await evaluate(`[...window.__sattle.ws.histories.get("master").graph.entities()].find((e) => e.type === "Service")?.id ?? ""`);
  const up = await reach({ graph: "master", kind: "entity", id: service });
  for (const g of ["serviceTree", "workloadStructure", "workload"]) if (!(up[g] > 0)) fail.push(`${name}: selecting a master service highlights ${up[g] ?? 0} elements in ${g}`);
  results.keyboard.push({ name: "abstraction highlight", lifted: down, service: up, ok: true });
  await dispatch({ type: "select/element", selection: null });
  await dispatch({ type: "ui/view", view: "data" }); await dispatch({ type: "ui/activeGraph", graph: "workload" });
  await showGraphTab();
  await sleep(2500);
  // the ontology declares which edges are support (engine 3.17): the elements that only support others are left out and counted on what they support
  const grouped = JSON.parse(await evaluate(`(() => { const p = window.__sattle.ws.panes.get("workload"); return JSON.stringify({ hiddenKinds: p ? [...p.hiddenKinds()] : [], counted: p ? p.labelsShown().filter((l) => /\\(\\+\\d+\\)/.test(l)).length : 0 }); })()`));
  if (!grouped.hiddenKinds.includes("support") || !(grouped.counted > 0)) fail.push(`${name}: the supporting elements of workload are not left out and counted (${JSON.stringify(grouped)})`);
  await runAxe(`${name} workload collapsed`);
  // telemetry of the default source (static meters and windows): the lifted edges carry callDropRatio, the panel offers it
  await dispatch({ type: "ui/activeGraph", graph: "capabilities" }); await sleep(1500);
  await clickText("button", "Telemetry"); await sleep(600);
  const meters = JSON.parse(await evaluate(`JSON.stringify([...document.querySelectorAll("select")].flatMap((x) => [...x.options].map((o) => o.textContent)).filter((t) => /^call/.test(t)))`));
  if (!meters.includes("callDropRatio")) fail.push(`${name}: the telemetry panel does not offer callDropRatio on capabilities (${JSON.stringify(meters)})`);
  await runAxe(`${name} telemetry`);
  // a selection whose lineage is partial says so in the Inspector, and a complete one does not
  await dispatch({ type: "ui/view", view: "space" });
  const partialSel = { graph: "treeStructure", kind: "entity", id: await evaluate(`(async () => { const ws = window.__sattle.ws; for (const e of ws.histories.get("treeStructure").graph.entities()) { const l = await ws.source.lineage("treeStructure", e.id); if (l && l.complete === false) return e.id; } return ""; })()`) };
  await dispatch({ type: "select/element", selection: partialSel }); await sleep(2500);
  const inspector = await evaluate(`document.querySelector('aside[aria-label="Inspector"]')?.textContent ?? ""`);
  if (!partialSel.id || !/The highlight is partial/.test(inspector)) fail.push(`${name}: the Inspector does not say the highlight is partial for ${partialSel.id || "(none found)"}`);
  await runAxe(`${name} partial highlight`);
  await dispatch({ type: "select/element", selection: null });
  await dispatch({ type: "ui/view", view: "space" });
}
// a hierarchical graph (engine 3.14: the ontology marks its hierarchy): the Tree tab in each theme, and the keyboard model of a tree (arrows, Home, End, collapse)
for (const theme of ["light", "dark", "contrast"]) {
  const name = `tree/${theme}`;
  await open(ENGINE, "data", theme);
  const treeGraphId = await evaluate(`window.__sattle.ws.info.graphs.find((g) => g.tree && g.id === "healthTree")?.id ?? window.__sattle.ws.info.graphs.find((g) => g.tree)?.id ?? ""`);
  if (!treeGraphId) { results.axe.push({ name, skipped: "no graph of the engine marks a hierarchy" }); continue; }
  await dispatch({ type: "ui/activeGraph", graph: treeGraphId });
  for (let i = 0; i < 50 && !(await evaluate(`!!document.querySelector('[role=tree]')`)); i++) await sleep(200);
  if (!(await evaluate(`!!document.querySelector('[role=tree]')`))) { fail.push(`${name}: the tree did not render`); continue; }
  await settle();
  const rows = JSON.parse(await evaluate(`JSON.stringify([...document.querySelectorAll('[role=treeitem]')].map((r) => [r.getAttribute("aria-level"), r.getAttribute("aria-posinset"), r.getAttribute("aria-setsize"), (r.textContent || "").trim().length > 0]))`));
  if (!rows.length || !rows.every((r) => r[0] && r[1] && r[2] && r[3])) fail.push(`${name}: rows without level, position or an accessible name (${JSON.stringify(rows.slice(0, 3))})`);
  if (theme === "light") {
    await evaluate(`document.querySelector('[role=treeitem]').focus()`);
    const first = await evaluate(`document.activeElement.textContent`);
    await key("ArrowDown"); await sleep(250);
    const second = await evaluate(`document.activeElement.textContent`);
    if (rows.length > 1 && second === first) fail.push(`${name}: ArrowDown did not move to the next row`);
    await key("End"); await sleep(250);
    const last = await evaluate(`document.activeElement.textContent`);
    await key("Home"); await sleep(250);
    const home = await evaluate(`document.activeElement.textContent`);
    if (home !== first) fail.push(`${name}: Home did not return to the first row (${home} instead of ${first})`);
    results.keyboard.push({ name: "tree: arrows, End and Home", first, last, ok: home === first });
  }
  await runAxe(name);
}
await open(SOURCE, "data", "light");
for (const dialog of ["shortcuts", "rejections"]) { await dispatch({ type: "ui/dialog", dialog }); await settle(); await runAxe(`dialog ${dialog}`); await dispatch({ type: "ui/dialog", dialog: null }); await sleep(300); }

// keyboard: tab order of the header
await open(SOURCE, "data", "light");
const order = [];
for (let i = 0; i < 5; i++) { await key("Tab"); order.push(await who()); if (!(await focusVisible())) fail.push(`focus not visible on "${order[i]}"`); }
const expected = ["Skip to the main view", "Data graph", "Data source", "Rejected: 0", "Filters"];
results.keyboard.push({ name: "header tab order", order, ok: JSON.stringify(order) === JSON.stringify(expected) });
if (JSON.stringify(order) !== JSON.stringify(expected)) fail.push(`header tab order: ${order.join(" > ")}`);
// the application toolbar is one tab stop; arrows move inside it
await evaluate(`[...document.querySelectorAll('[role=toolbar] button')].find(b=>b.textContent.includes('Rejected')).focus()`);
await key("ArrowRight");
const tb = await who();
results.keyboard.push({ name: "toolbar arrow", focus: tb, ok: tb === "Keyboard shortcuts" });
if (tb !== "Keyboard shortcuts") fail.push(`toolbar arrow: got "${tb}"`);
// tab list: arrows move focus, Enter selects
await evaluate(`[...document.querySelectorAll('[role=tab]')].find(t=>t.textContent.includes('Data graph')).focus()`);
await key("ArrowRight");
results.keyboard.push({ name: "tab list arrow moves focus", focus: await who() });
await key("Enter");
const viewAfter = await state("view");
results.keyboard.push({ name: "tab list arrow + Enter", view: viewAfter, ok: viewAfter === "space" });
if (viewAfter !== "space") fail.push(`tab list: expected space, got ${viewAfter}`);
// dialog: ? opens, focus inside, Escape closes, focus returns
await evaluate(`document.querySelector('[aria-label="Keyboard shortcuts"]').focus()`);
await key("?");
await sleep(400);
const inDialog = await evaluate(`!!document.activeElement.closest('[role=dialog]')`);
await key("Escape"); await sleep(500);
const back = await who();
results.keyboard.push({ name: "dialog focus", inDialog, returnedTo: back, ok: inDialog && back === "Keyboard shortcuts" });
if (!inDialog || back !== "Keyboard shortcuts") fail.push(`dialog focus: inside=${inDialog}, returned to "${back}"`);
// the canvas takes focus and arrow keys work on it
await dispatch({ type: "ui/view", view: "data" }); await sleep(500);
await evaluate(`document.querySelector('.graph-view')?.focus()`);
const onCanvas = await evaluate(`document.activeElement.classList.contains('graph-view')`);
results.keyboard.push({ name: "canvas focusable", ok: onCanvas });
if (!onCanvas) fail.push("canvas host is not focusable");

// the editor, keyboard only: the canvas takes focus, arrows select, A opens the add dialog, W starts wiring, Escape cancels, Ctrl+Z undoes
await open(ENGINE, "editor", "light");
const sel = () => state("editor.selected.length");
await evaluate(`document.querySelector('[aria-label^="Projection graph"]').focus()`);
await key("ArrowRight");
results.keyboard.push({ name: "editor: arrow selects an operator", selected: await sel(), ok: (await sel()) === 1 });
if ((await sel()) !== 1) fail.push("editor: arrow key did not select an operator");
await key("ArrowRight"); await key("ArrowRight");
await key("w");
const wiring = await state("editor.wiring");
results.keyboard.push({ name: "editor: W starts connecting", wiring: !!wiring, ok: !!wiring });
await key("Escape");
if (await state("editor.wiring")) fail.push("editor: Escape did not cancel connecting");
await key("a"); await sleep(500);
const dlg = await evaluate(`!!document.querySelector('[role=dialog]') && document.activeElement.closest('[role=dialog]') !== null`);
results.keyboard.push({ name: "editor: A opens the add dialog with focus inside", ok: dlg });
if (!dlg) fail.push("editor: A did not open the add dialog");
await runAxe("editor add dialog");
await key("Escape"); await sleep(500);
await dispatch({ type: "editor/do", cmd: { t: "addOperator", kind: "Filter", name: "kbd1" } }); await sleep(1200);
const before = await state("editor.undo.length");
await evaluate(`document.querySelector('[aria-label^="Projection graph"]').focus()`);
await key("z", { ctrl: true }); await sleep(1200);
const after = await state("editor.undo.length");
results.keyboard.push({ name: "editor: Ctrl+Z undoes", before, after, ok: after === before - 1 });
if (after !== before - 1) fail.push(`editor: Ctrl+Z did not undo (${before} -> ${after})`);
await runAxe("editor after edits");
// the rule program, the definitions tab and the keyboard marquee
await dispatch({ type: "editor/select", ids: [await evaluate(`window.__sattle.session.catalog.projectionGraph(window.__sattle.store.getState().editor.projection).nodes.find(n=>n.kind==="rule")?.id ?? ""`)].filter(Boolean) });
await sleep(500);
await runAxe("editor rule program");
if (!(await clickText("[role=tab]", "Definitions"))) fail.push("editor: no Definitions tab");
await sleep(500);
await runAxe("editor definitions");
// a lift (engine 3.13): its card and its panel (parameters with defaults, the three relationship types, carry, keep, derive, state and when)
const liftOp = await evaluate(`(() => { const c = window.__sattle.session.catalog; for (const p of c.projections()) for (const o of c.children(p.id, "op")) if (window.__sattle.session.graph.entity(o)?.type === "Lift") return JSON.stringify([p.module, p.id, o]); return ""; })()`);
if (liftOp) {
  const [lm, lp, lo] = JSON.parse(liftOp);
  await dispatch({ type: "editor/projection", module: lm, projection: lp }); await sleep(600);
  await dispatch({ type: "editor/select", ids: [lo] }); await sleep(800);
  const panel = await evaluate(`document.querySelector("#editor-props")?.textContent ?? ""`);
  for (const want of ["within", "images", "selfLoops", "Relationship type lifted to", "Lower relationship type", "Mapping relationship type", "carry", "keep", "derive", "state"]) if (!panel.includes(want)) fail.push(`editor lift: the panel does not show ${want}`);
  await runAxe("editor lift");
} else results.axe.push({ name: "editor lift", skipped: "the engine's catalog has no Lift" });
await evaluate(`document.querySelector('[aria-label^="Projection graph"]').focus()`);
await key("Escape"); await key("m");
for (let i = 0; i < 4; i++) { await key("ArrowRight"); await key("ArrowDown"); }
await key("Enter");
const marq = await sel();
results.keyboard.push({ name: "editor: M, arrows and Enter select a region", selected: marq, ok: marq >= 1 });
if (marq < 1) fail.push("editor: keyboard marquee selected nothing");

// color by property: the legend and its control
await open(SOURCE, "data", "light");
await clickText("[role=tab]", "Filters");
await clickText("button", "Color by property"); await sleep(400);
await runAxe("data color by property");
await clickText("[role=tab]", "Elements"); await sleep(600);
await runAxe("data element list with the colored value column");
await dispatch({ type: "ui/dialog", dialog: null });
for (const theme of ["dark", "contrast"]) { await dispatch({ type: "ui/theme", theme }); await clickText("[role=tab]", "Filters"); await sleep(300); await settle(); await runAxe(`data color by property (${theme})`); }
await dispatch({ type: "editor/open" });

console.log(JSON.stringify(results.axe.map((r) => ({ name: r.name, violations: r.violations })), null, 0));
for (const r of results.axe) if (r.violations) console.log(JSON.stringify(r.detail));
console.log(JSON.stringify(results.keyboard));
if (results.roles) console.log(JSON.stringify(results.roles));
// a check that was skipped (the engine has no document graph, no Lift, ...) was not made: it is not a pass. A11Y_ALLOW_SKIP=1 accepts skips for a run without the engine.
const skipped = results.axe.filter((r) => r.skipped);
if (skipped.length) console.log(`a11y: ${skipped.length} check(s) SKIPPED, not passed:\n${skipped.map((r) => `  ${r.name}: ${r.skipped}`).join("\n")}`);
console.log(fail.length ? `FAILED (${fail.length}):\n${fail.join("\n")}` : skipped.length ? "a11y: the checks that ran passed, others were skipped" : "a11y: all checks passed");
stop();
process.exit(fail.length ? 1 : skipped.length && !process.env.A11Y_ALLOW_SKIP ? 2 : 0);
