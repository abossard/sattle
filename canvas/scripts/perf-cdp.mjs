// Runs the in-page perf harness (window.__perf) in headless Chrome with the GPU enabled, driven over CDP.
// Usage: node scripts/perf-cdp.mjs <url> [chrome-path]   (serve the build first: npx vite preview --port 4173)
import { spawn } from "node:child_process";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const url = process.argv[2] ?? "http://127.0.0.1:4173/#source=synth-100k&view=data";
const chrome = process.argv[3] ?? "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const port = 9300 + Math.floor(Math.random() * 600);
const proc = spawn(chrome, [
  "--headless=new", `--remote-debugging-port=${port}`, `--user-data-dir=${mkdtempSync(join(tmpdir(), "sattle-perf-"))}`,
  "--window-size=1440,900", "--use-angle=metal", "--enable-gpu-rasterization", "--ignore-gpu-blocklist", "--no-first-run", "--disk-cache-size=1", "--disable-application-cache", "--disable-background-timer-throttling", "--disable-renderer-backgrounding", "about:blank",
], { stdio: "ignore", detached: true });
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
  if (r.result.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails));
  return r.result.result.value;
};
await send("Page.enable");
await send("Emulation.setDeviceMetricsOverride", { width: 1440, height: 900, deviceScaleFactor: 2, mobile: false });
await send("Page.navigate", { url });
await sleep(1500);
const log = (k, v) => console.log(JSON.stringify({ [k]: v }));
log("gpu", await evaluate(`(()=>{const c=document.createElement('canvas').getContext('webgl2');const e=c.getExtension('WEBGL_debug_renderer_info');return e?c.getParameter(e.UNMASKED_RENDERER_WEBGL):'n/a'})()`));
// wait for the layout worker to finish
for (let i = 0; i < 300; i++) {
  const s = await evaluate(`(()=>{const w=window.__sattle?.ws; if(!w) return null; const ps=[...w.panes.values()]; return ps.length?{running:ps.some(p=>p.layoutRunning),ms:ps.map(p=>Math.round(p.lastLayoutMs)),n:ps.map(p=>p.graph.nodeCount),e:ps.map(p=>p.graph.edgeCount)}:null})()`);
  if (s && !s.running) { log("loaded", s); break; }
  await sleep(500);
}
await sleep(500);
for (const step of (process.argv[4] ?? "pan;zoom;panZoomDeep;pickCost;hoverLatency;selectLatency;patchReplay;memory").split(";")) {
  log(step, await evaluate(step.includes("(") ? `__perf.${step}` : `__perf.${step}()`));
}
if (process.env.EVAL) log("eval", await evaluate(process.env.EVAL));
if (process.env.SHOT) {
  const { writeFileSync } = await import("node:fs");
  const shot = await send("Page.captureScreenshot", { format: "png" });
  writeFileSync(process.env.SHOT, Buffer.from(shot.result.data, "base64"));
  log("screenshot", process.env.SHOT);
}
ws.close(); stop();
