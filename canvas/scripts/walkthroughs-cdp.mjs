// Replays the walkthroughs of tests/walkthroughs/*.json in real Chrome, over CDP, against the engine behind the app: each is the list of
// editor commands an author sends (the same `editor/do` actions as the UI, never the Text tab), and the result is checked on the engine.
//   - every command is accepted and the engine reports no diagnostics beyond those the walkthrough allows;
//   - `original`: the projection it rebuilds prints the same canonical text, and its preview has the same digest;
//   - `text`: the same projection written as text (applied through the Text path into a twin projection) prints the same canonical
//     text as the one the commands built, and the preview digests are equal;
//   - `nonEmpty` / `atLeast`: the preview has entities or relationships (at least that many), so equal digests are not just two empty graphs;
//   - `identity`: the preview's identity effect (engine 3.7: what a Link does is not an element of a graph) differs from the live space by at least that many
//     of `minted`, `aliased`, `merged`, `conflicts`;
// Usage: node scripts/walkthroughs-cdp.mjs [base-url] [--allow-empty] [walkthrough names...]   (serve the app and an engine first, see the README;
// a walkthrough whose module or original projection is not on that engine is skipped and said so; exit 1 on any mismatch, 2 when nothing ran)
import { spawn } from "node:child_process";
import { mkdtempSync, readdirSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const dir = join(here, "..", "tests", "walkthroughs");
const args = process.argv.slice(2);
const allowEmpty = args.includes("--allow-empty") || process.env.WALK_ALLOW_EMPTY === "1";
const [base = "http://localhost:5173/", ...only] = args.filter((a) => a !== "--allow-empty");
const chrome = process.env.CHROME ?? "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const walks = readdirSync(dir).filter((f) => f.endsWith(".json")).map((f) => ({ file: f.replace(/\.json$/, ""), ...JSON.parse(readFileSync(join(dir, f), "utf8")) }))
  .filter((w) => !only.length || only.includes(w.file) || only.includes(w.name));

const port = 9300 + Math.floor(Math.random() * 600);
const proc = spawn(chrome, ["--headless=new", `--remote-debugging-port=${port}`, `--user-data-dir=${mkdtempSync(join(tmpdir(), "sattle-walk-"))}`, "--window-size=1440,900", "--use-angle=metal", "--ignore-gpu-blocklist", "--no-first-run", "about:blank"], { stdio: "ignore", detached: true });
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
  if (r.result.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails.exception?.description ?? r.result.exceptionDetails.text).slice(0, 400));
  return r.result.result.value;
};
await send("Page.enable"); await send("Runtime.enable");
await send("Emulation.setDeviceMetricsOverride", { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });

/** Runs in the page: everything that needs the app's store and session. Returns plain data for the checks below. */
const IN_PAGE = `(async (W) => {
  const st = () => window.__sattle.store.getState(), d = (a) => st().dispatch(a), w = (ms) => new Promise((r) => setTimeout(r, ms));
  const idle = async () => { for (let i = 0; i < 5 && !st().editor.busy; i++) await w(20); for (let i = 0; i < 3000; i++) { if (!st().editor.busy) return; await w(10); } };
  const out = { skipped: "", failed: "", diagnostics: [], text: "", originalText: "", twinText: "", preview: [], twinDiagnostics: [], types: [] };
  for (let i = 0; i < 100 && st().editor.status !== "ready"; i++) await w(200);
  if (st().editor.status !== "ready") { out.failed = "the editor did not open a draft: " + st().editor.message; return out; }
  const cat = () => window.__sattle.session.catalog;
  const first = cat().projections().find((p) => p.module === W.module);
  if (!first) { out.skipped = "module " + W.module + " is not on this engine"; return out; }
  if (W.original && !cat().projections().some((p) => p.id === "d:" + W.module + "/projection:" + W.original)) { out.skipped = "projection " + W.original + " is not on this engine"; return out; }
  // a parameter bound to "$original" takes the value the space binds to the same parameter of the projection it rebuilds, so the walkthrough
  // follows the engine it runs on (the health roll-up's workload namespace differs between captures)
  if (W.original) {
    const g = window.__sattle.session.graph, port = (n) => "d:" + W.module + "/projection:" + W.original + "/port:" + n;
    const bound = (n) => { for (const r of g.relationships()) if (r.type === "bindsPort" && r.target === port(n)) return g.entity(r.source)?.props.value?.value; return undefined; };
    for (const { cmd } of W.commands) if (cmd.t === "newProjection") for (const p of cmd.spec.params) if (p.value === "$original") { const v = bound(p.port); if (v === undefined) { out.failed = "the original has no binding for " + p.port; return out; } p.value = String(v); }
  }
  d({ type: "editor/projection", module: W.module, projection: first.id }); await w(400);
  const run = async (cmd) => { d({ type: "editor/do", cmd }); await idle(); return st().editor.notice; };
  for (const { label, cmd } of W.commands) { const n = await run(cmd); if (n) { out.failed = label + ": " + n; return out; } }
  const block = (t, n) => { const m = t.match(new RegExp("projection " + n + " [\\\\s\\\\S]*?\\n}\\n")); return m ? m[0] : ""; };
  const text = () => window.__sattle.session.texts[W.module] || "";
  out.text = block(text(), W.name);
  out.diagnostics = st().editor.diagnostics.map((x) => ({ code: x.code, severity: x.severity, message: x.message }));
  if (W.original) out.originalText = block(text(), W.original);
  const graphs = [...W.graphs];
  if (W.text) {
    const twin = W.name + "Text", spec = { ...W.commands[0].cmd.spec, name: twin, graphName: twin };
    let n = await run({ t: "newProjection", spec });
    if (n) { out.failed = "twin projection: " + n; return out; }
    const body = W.text.join("\\n").replaceAll("NAME", twin) + "\\n";
    const whole = text(), b = block(whole, twin);
    if (!b) { out.failed = "twin projection is not printed"; return out; }
    n = await run({ t: "text", module: W.module, text: whole.replace(b, body) });
    if (n) { out.failed = "text of the twin: " + n; return out; }
    out.twinText = block(text(), twin);
    out.twinDiagnostics = st().editor.diagnostics.filter((x) => (x.focus || "").includes("projection:" + twin + "/") || (x.focus || "").endsWith("projection:" + twin)).map((x) => ({ code: x.code, severity: x.severity, message: x.message }));
    graphs.push(twin);
  }
  if (!out.diagnostics.some((x) => x.severity === "error")) {
    d({ type: "editor/preview", graphs }); await idle();
    for (let i = 0; i < 480 && !st().editor.preview; i++) await w(250);
    const full = window.__sattle.preview;
    out.types = (full?.graphs ?? []).map((g) => g.id + ": " + JSON.stringify([...(g.snapshot?.entities ?? [])].reduce((m, e) => (m[e.type] = (m[e.type] || 0) + 1, m), {})));
    const sizes = new Map((st().editor.preview?.graphs ?? []).map((g) => [g.id, g]));
    out.preview = (full ? full.graphs : []).map((g) => ({ id: g.id, digest: g.digest || "", entities: sizes.get(g.id)?.entities ?? 0, relationships: sizes.get(g.id)?.relationships ?? 0 }));
    out.identity = st().editor.preview?.identity ?? null;
    out.previewNotice = st().editor.preview ? "" : [st().editor.notice, st().editor.message, st().editor.busy && "still " + st().editor.busy].filter(Boolean).join("; ") || "the engine answered no preview";
  }
  // durations typed into the properties panel the way a person does (for example 36h): the field must show the text it was given and the engine
  // must hold the nanoseconds
  out.durations = [];
  for (const x of W.durations ?? []) {
    const r = { id: x.id, param: x.param, problem: "" };
    d({ type: "editor/select", ids: [x.id] });
    let input = null;
    for (let i = 0; i < 50 && !input; i++) {
      await w(100);
      const label = [...document.querySelectorAll("#editor-props label")].find((l) => l.textContent.startsWith(x.param + " (duration"));
      input = label && document.getElementById(label.htmlFor);
    }
    if (!input) { r.problem = "the properties panel has no duration field " + x.param; out.durations.push(r); continue; }
    input.focus();
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set.call(input, x.enter);
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
    await idle();
    const notice = st().editor.notice, held = window.__sattle.session.graph.entity(x.id)?.props[x.param]?.value;
    await w(300);
    const shown = (() => { const l = [...document.querySelectorAll("#editor-props label")].find((q) => q.textContent.startsWith(x.param + " (duration")); return l ? document.getElementById(l.htmlFor)?.value : undefined; })();
    if (notice) r.problem = notice; else if (String(held) !== x.wire) r.problem = "the engine holds " + held + ", expected " + x.wire; else if (shown !== x.enter) r.problem = "the field shows " + shown + ", expected " + x.enter;
    out.durations.push(r);
  }
  return out;
})`;

const normal = (t, from, to) => t.split("\n").map((l) => l.trim()).filter(Boolean).map((l) => l.replace(`projection ${from} `, `projection ${to} `));
const sameLines = (a, b) => a.length === b.length && a.every((l, i) => l === b[i]);

// which engine this ran against: what its /space answers (the engine reports no build number; the kernel digest and catalog revision identify what it serves)
try {
  const sp = await (await fetch(new URL("engine/space", base))).json();
  console.log(`engine: space ${sp.space}, protocol ${sp.protocol}, kernel ${String(sp.kernelDigest).slice(0, 19)}, catalog revision ${sp.catalogRevision}, seq ${sp.seq}${sp.input ? `, input ${sp.input.mode} ${sp.input.rows ?? "?"} rows` : ""}`);
} catch (e) { console.log(`engine: /space did not answer (${e.message})`); }
let ran = 0, failures = 0;
/** what each failed walkthrough said first, for the summary line */
const reasons = [];
for (const walk of walks) {
  const problems = [], notes = [];
  await send("Page.navigate", { url: `${base}${base.includes("?") ? "&" : "?"}walk=${Date.now()}#source=engine&view=editor` });
  for (let i = 0; i < 100 && !(await evaluate(`window.__sattle?.store.getState().load.status`).catch(() => false) === "ready"); i++) await sleep(200);
  await sleep(500);
  let r;
  try { r = await evaluate(`${IN_PAGE}(${JSON.stringify(walk)})`); } catch (e) { r = { failed: String(e.message) }; }
  if (process.env.WALK_DEBUG) console.log(JSON.stringify({ ...r, text: undefined, originalText: undefined, twinText: undefined }, null, 1) + "\n--- built\n" + r.text + "--- text\n" + r.twinText);
  if (r.skipped) { console.log(`skip  ${walk.file}: ${r.skipped}`); continue; }
  ran++;
  if (r.failed) problems.push(r.failed);
  else {
    const allowed = new Set(walk.allowDiagnostics ?? []);
    for (const x of r.diagnostics) if (!allowed.has(x.code)) problems.push(`diagnostic ${x.code}: ${x.message}`);
    if (!r.text) problems.push("the new projection is not printed");
    if (walk.original && !sameLines(normal(r.text, walk.name, walk.original), normal(r.originalText, walk.original, walk.original))) problems.push(`canonical text differs from ${walk.original}`);
    if (walk.text) {
      for (const x of r.twinDiagnostics) if (!allowed.has(x.code)) problems.push(`twin diagnostic ${x.code}: ${x.message}`);
      if (!sameLines(normal(r.text, walk.name, "P"), normal(r.twinText, `${walk.name}Text`, "P"))) problems.push("canonical text of the projection written as text differs from the one built");
    }
    if (!r.preview.length) problems.push(`no preview: ${r.previewNotice ?? ""}`);
    else {
      const digests = new Set(r.preview.map((g) => g.digest));
      if (digests.size !== 1) problems.push(`preview digests differ: ${r.preview.map((g) => `${g.id} ${g.digest.slice(0, 19)}`).join(", ")}`);
      // data checks: strict by default (run on the `estate` fixture, which has data for every operator); --allow-empty (or WALK_ALLOW_EMPTY=1)
      // keeps the text and digest checks and only notes missing data, for an engine whose data is thinner or different
      const data = [];
      if (walk.nonEmpty && r.preview.some((g) => !(Number(g.entities) + Number(g.relationships) > 0))) data.push("the preview is empty");
      for (const g of r.preview) for (const k of ["entities", "relationships"]) if (walk.atLeast?.[k] !== undefined && Number(g[k]) < walk.atLeast[k]) data.push(`${g.id} has ${g[k]} ${k}, expected at least ${walk.atLeast[k]}`);
      for (const m of data) (allowEmpty ? notes : problems).push(m);
    }
    if (walk.identity) {
      const id = r.identity, data = [];
      if (!id) data.push("the engine answered no identity effect for the preview");
      else if (!id.comparable) data.push("the identity effect is not comparable with the live space (partial input)");
      else for (const [k, n] of Object.entries(walk.identity)) if (Number(id.difference[k]) < n) data.push(`identity ${k}: ${id.difference[k]} more than the live space, expected at least ${n}`);
      for (const m of data) (allowEmpty ? notes : problems).push(m);
    }
  }
  for (const x of r.durations ?? []) if (x.problem) problems.push(`duration ${x.param}: ${x.problem}`);
  if (problems.length) { failures++; reasons.push(`${walk.file}: ${problems[0]}${problems.length > 1 ? ` (+${problems.length - 1} more)` : ""}`.replace(/\s+/g, " ").slice(0, 240)); }
  const typed = (r.durations ?? []).filter((x) => !x.problem).map((x) => x.param).join(", ");
  const sized = (r.preview ?? []).map((g) => `${g.id} ${g.entities}/${g.relationships}`).join(", ") + (walk.identity && r.identity?.comparable ? `${r.preview?.length ? ", " : ""}identity vs live ${["minted", "aliased", "merged", "conflicts"].map((k) => `${k} ${r.identity.difference[k]}`).join(" ")}` : "");
  console.log(`${problems.length ? "FAIL" : "ok  "}  ${walk.file}: ${walk.commands.length} commands${sized ? `, preview ${sized}` : ""}${typed ? `, durations typed in the panel: ${typed}` : ""}${problems.map((p) => `\n        ${p}`).join("")}${notes.map((n) => `\n        note: ${n}`).join("")}`);
}
stop();
if (!ran) { console.error("walkthroughs: nothing ran (is the app up at " + base + " on an engine that has the modules?)"); process.exit(2); }
console.log(failures ? `walkthroughs: ${failures} of ${ran} failed\n  ${reasons.join("\n  ")}` : `walkthroughs: ${ran} passed`);
process.exit(failures ? 1 : 0);
