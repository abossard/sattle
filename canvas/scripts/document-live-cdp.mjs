// A document graph follows the event stream without a reload, in real Chrome over CDP. Start an engine that plays a capture slowly, for example
//   dotnet engine/src/Sattle.Cli/bin/Release/net10.0/sattle.dll serve lab/fixtures --speed 2 --port 5080
// then the app (npm run dev) and run: node scripts/document-live-cdp.mjs [base-url]
// The page is opened once and never reloaded. The script shows the first document graph of the space, waits for the engine to stop moving
// (its head stays the same for a while) and checks that
//   - the page showed more than one revision of the document while it stayed open (updates arrived over the stream), and
//   - the revision and digest on the page equal what GET /graphs/{g}/document answers at the head.
// Exit 1 on a mismatch; 2 when it could not observe a live update (no document graph, or the engine had already finished when the page opened).
import { spawn } from "node:child_process";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const base = process.argv[2] ?? "http://localhost:5173/";
const chrome = process.env.CHROME ?? "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const port = 9300 + Math.floor(Math.random() * 600);
const proc = spawn(chrome, ["--headless=new", `--remote-debugging-port=${port}`, `--user-data-dir=${mkdtempSync(join(tmpdir(), "sattle-doc-"))}`, "--window-size=1440,900", "--use-angle=metal", "--ignore-gpu-blocklist", "--no-first-run", "about:blank"], { stdio: "ignore", detached: true });
const stop = () => { try { process.kill(-proc.pid); } catch { proc.kill(); } };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const done = (code, msg) => { console[code ? "error" : "log"](msg); stop(); process.exit(code); };
let targets;
for (let i = 0; i < 50; i++) { try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); if (targets.length) break; } catch { /* chrome is starting */ } await sleep(200); }
const ws = new WebSocket(targets.find((t) => t.type === "page").webSocketDebuggerUrl);
await new Promise((r) => (ws.onopen = r));
let id = 0; const pending = new Map();
ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } };
const send = (method, params = {}) => new Promise((res) => { const i = ++id; pending.set(i, res); ws.send(JSON.stringify({ id: i, method, params })); });
const evaluate = async (expression) => {
  const r = await send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true });
  if (r.result.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails.exception?.description ?? r.result.exceptionDetails.text).slice(0, 300));
  return r.result.result.value;
};
await send("Page.enable"); await send("Runtime.enable");
await send("Emulation.setDeviceMetricsOverride", { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });
await send("Page.navigate", { url: `${base}#source=engine&view=data` });
for (let i = 0; i < 150 && (await evaluate(`window.__sattle?.store.getState().load.status`).catch(() => "")) !== "ready"; i++) await sleep(200);
if ((await evaluate(`window.__sattle?.store.getState().load.status`).catch(() => "")) !== "ready") done(2, "document-live: the app did not load a source (is the engine up behind the proxy?)");

const graph = await evaluate(`window.__sattle.ws.info.graphs.find((g) => g.document)?.id ?? ""`);
if (!graph) done(2, "document-live: the engine serves no document graph");
await evaluate(`window.__noReload = true; window.__sattle.store.getState().dispatch({ type: "ui/activeGraph", graph: ${JSON.stringify(graph)} })`);

/** What the page shows: the revision and the digest of the document view. */
const shown = () => evaluate(`(() => { const s = document.querySelector('section[aria-label^="Document"]'); if (!s) return null; const b = [...s.querySelectorAll('[class*=Badge]')]; const rev = /revision (\\d+)/.exec(b[0]?.textContent ?? ""); return rev ? { revision: rev[1], digest: b[1]?.getAttribute("title") ?? "", busy: s.getAttribute("aria-busy") } : null; })()`);
const head = async () => { const r = await fetch(new URL(`engine/graphs/${encodeURIComponent(graph)}/document`, base)); return { revision: r.headers.get("X-Revision"), digest: r.headers.get("X-Document-Digest") }; };
/** The head of the document graph and the engine's logical time: the engine has finished when neither moves for a while. */
const spaceState = async () => { const sp = await (await fetch(new URL("engine/space", base))).json(); return { head: String(sp.graphs.find((g) => g.id === graph).head), key: `${sp.graphs.find((g) => g.id === graph).head}|${sp.input?.logicalTime}` }; };

const seen = new Set();
let first = null, quietSince = Date.now(), lastKey = (await spaceState()).key;
for (let i = 0; i < 4800; i++) {
  const s = await shown();
  if (s) { first ??= s; seen.add(s.revision); }
  const h = await spaceState();
  if (h.key !== lastKey) { lastKey = h.key; quietSince = Date.now(); }
  if (Date.now() - quietSince > 90000 && s && s.revision === h.head) break;
  await sleep(250);
}
const final = await shown(), want = await head();
if (!(await evaluate(`window.__noReload === true`))) done(1, "document-live: the page was reloaded");
if (!final) done(1, "document-live: the page shows no document at the end");
if (final.revision !== want.revision || final.digest !== want.digest) done(1, `document-live: the page shows revision ${final.revision} ${final.digest.slice(0, 19)}, the engine's head is revision ${want.revision} ${want.digest.slice(0, 19)}`);
if (seen.size < 2) done(2, `document-live: the page showed only revision ${[...seen].join(", ")}; the engine had finished before the page opened (start it with a lower --speed)`);
done(0, `document-live: ${graph} followed the stream without a reload through revisions ${[...seen].join(", ")} and ends at revision ${final.revision}, digest ${final.digest.slice(0, 19)} as the engine answers`);
