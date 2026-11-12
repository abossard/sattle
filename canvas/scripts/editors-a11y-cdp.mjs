// The five notations (and the statement kinds panel) in real Chrome, driven over CDP: axe-core with color contrast in each of the
// application's three themes, on the page that shows them on a draft in memory (tests/editors/page). The unit tests run the same
// rules in a test DOM, which has no colors; this is where contrast is checked.
// Usage: node scripts/editors-a11y-cdp.mjs [base-url] [chrome-path]   (serve the app first: npm run dev; default http://localhost:5173/)
// Exit code: 0 no violation, 1 a violation, 2 the page did not start.
import { spawn } from "node:child_process";
import { mkdtempSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createRequire } from "node:module";

const base = process.argv[2] ?? "http://localhost:5173/";
const chrome = process.argv[3] ?? "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const axeSource = readFileSync(createRequire(import.meta.url).resolve("axe-core/axe.min.js"), "utf8");
const port = 9300 + Math.floor(Math.random() * 600);
const proc = spawn(chrome, ["--headless=new", `--remote-debugging-port=${port}`, `--user-data-dir=${mkdtempSync(join(tmpdir(), "sattle-editors-a11y-"))}`, "--window-size=1440,900", "--use-angle=metal", "--ignore-gpu-blocklist", "--no-first-run", "about:blank"], { stdio: "ignore", detached: true });
const stop = () => { try { process.kill(-proc.pid); } catch { proc.kill(); } };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let targets;
for (let i = 0; i < 50; i++) { try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); if (targets.length) break; } catch { /* chrome is starting */ } await sleep(200); }
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
await send("Page.enable");
await send("Emulation.setDeviceMetricsOverride", { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });

/** What is checked: each notation as it opens, and three states an author gets into (an editor open in a row, a block in hand, a wire in hand). */
const CASES = [
  { name: "structure", query: "on=structure" },
  { name: "structure, a row being edited", query: "on=structure", act: `(() => { const r = [...document.querySelectorAll('[role=treeitem]')].find((x) => x.dataset.kind === "entry"); r.dispatchEvent(new MouseEvent("dblclick", { bubbles: true })); })()` },
  { name: "steps", query: "on=pipeline" },
  { name: "blocks", query: "on=blocks" },
  { name: "blocks, a block in hand", query: "on=blocks", act: `[...document.querySelectorAll('[aria-label="Blocks"] button')].find((b) => b.textContent === "filter").click()` },
  { name: "wires", query: "on=graph" },
  { name: "wires, a wire in hand and a card selected", query: "on=graph", act: `(() => { document.querySelector('[data-card*="/op:"]').click(); document.querySelector('button[aria-label^="connect from"]').click(); })()` },
  { name: "by example, one element picked", query: "on=example", act: `document.querySelector('[aria-label="Entities"] input[type=checkbox]').click()` },
  { name: "statement kinds", query: "on=structure&kinds" },
];
const failures = [];
for (const theme of ["light", "dark", "contrast"]) for (const c of CASES) {
  await send("Page.navigate", { url: `${base}tests/editors/page/index.html?${c.query}&theme=${theme}` });
  let up = false;
  for (let i = 0; i < 100 && !up; i++) { await sleep(200); up = await evaluate(`!!document.querySelector('[role=tablist]')`).catch(() => false); }
  if (!up) { console.error(`editors a11y: the page did not start at ${base} (is the dev server running?)`); stop(); process.exit(2); }
  if (c.act) await evaluate(c.act);
  // styles move for a moment after a change: axe must not read colors in between
  await sleep(400); await evaluate(`Promise.all(document.getAnimations().map((a) => a.finished.catch(() => {})))`); await sleep(200);
  await evaluate(axeSource);
  const v = await evaluate(`axe.run({include:[['body']],exclude:[['[data-tabster-dummy]']]},{rules:{region:{enabled:false}}}).then(r=>r.violations.map(v=>({id:v.id,impact:v.impact,nodes:v.nodes.slice(0,4).map(n=>n.target.join(' ')+' :: '+(n.any[0]?.message||n.all[0]?.message||''))})))`);
  console.log(`${v.length ? "FAIL" : "ok  "}  ${theme}: ${c.name}${v.map((x) => `\n        ${x.id} (${x.impact}): ${x.nodes.join("\n          ")}`).join("")}`);
  if (v.length) failures.push(`${theme}: ${c.name}: ${v.map((x) => x.id).join(", ")}`);
}
stop();
console.log(failures.length ? `editors a11y: ${failures.length} of ${CASES.length * 3} cases failed` : `editors a11y: ${CASES.length * 3} cases passed (axe-core with color contrast, three themes)`);
process.exit(failures.length ? 1 : 0);
