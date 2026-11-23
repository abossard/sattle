import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import net from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";

const base = process.argv[2] || "http://127.0.0.1:59314";
const chrome = process.env.CHROME || "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const directory = mkdtempSync(join(tmpdir(), "sattle-review-browser-"));
const socket = net.createServer();
await new Promise((resolve) => socket.listen(0, "127.0.0.1", resolve));
const port = socket.address().port;
await new Promise((resolve) => socket.close(resolve));
const browser = spawn(chrome, ["--headless=new", `--remote-debugging-port=${port}`, "--remote-debugging-address=127.0.0.1", `--user-data-dir=${directory}`, "--no-first-run", "about:blank"], { stdio: "ignore" });
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
let ws;

try {
  let target;
  for (let attempt = 0; attempt < 50 && !target; attempt++) {
    try { target = (await (await fetch(`http://127.0.0.1:${port}/json`)).json()).find((page) => page.type === "page"); } catch {}
    if (!target) await sleep(100);
  }
  assert.ok(target, "Chrome must start with a local debugging port");
  ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
  let id = 0;
  const pending = new Map();
  const errors = [];
  ws.onmessage = ({ data }) => {
    const message = JSON.parse(data);
    if (message.id) { pending.get(message.id)?.(message); pending.delete(message.id); }
    if (message.method === "Runtime.exceptionThrown") errors.push(message.params.exceptionDetails.text);
  };
  const send = (method, params = {}) => new Promise((resolve) => {
    const next = ++id;
    pending.set(next, resolve);
    ws.send(JSON.stringify({ id: next, method, params }));
  });
  const evaluate = async (expression) => {
    const response = await send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true });
    assert.ok(!response.result.exceptionDetails, JSON.stringify(response.result.exceptionDetails));
    return response.result.result.value;
  };
  const waitFor = async (expression) => {
    for (let attempt = 0; attempt < 100; attempt++) {
      if (await evaluate(expression)) return;
      await sleep(100);
    }
    throw new Error(`Timed out: ${expression}`);
  };
  const key = async (name) => {
    await send("Input.dispatchKeyEvent", { type: "keyDown", key: name, code: name, windowsVirtualKeyCode: name === "Enter" ? 13 : 32 });
    await send("Input.dispatchKeyEvent", { type: "keyUp", key: name, code: name });
  };
  await send("Page.enable");
  await send("Runtime.enable");
  await send("Emulation.setDeviceMetricsOverride", { width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false });
  await send("Page.navigate", { url: base });
  await waitFor("document.querySelectorAll('[data-finding]').length === 11");
  await waitFor("document.querySelector('#discussion-content')?.textContent.includes('Issue brief:')");
  assert.equal(await evaluate("document.querySelector('#discussion-status').textContent.includes('unavailable')"), false);
  assert.ok(await evaluate("document.querySelectorAll('.evidence code').length >= 2"));
  console.log("PASS committed evidence, 11 ranked findings and real GitHub discussion");

  await evaluate(`document.querySelector('[name="rationale"]').value = "Preserve this unsaved review while navigating";
    document.querySelector('[name="decision"]').checked = true;
    document.querySelector('.tabs [data-view="map"]').click();`);
  assert.equal(await evaluate("document.querySelectorAll('.map-node').length"), 10);
  assert.equal(await evaluate("document.querySelector('[name=\"rationale\"]').value"), "Preserve this unsaved review while navigating");
  await evaluate(`document.querySelector('[data-module="engine"]').focus()`);
  await key("Enter");
  assert.equal(await evaluate("document.activeElement.id"), "detail-title");
  await evaluate(`document.querySelector('.tabs [data-view="board"]').click();
    const filter = document.querySelector('#review-filter'); filter.value = 'misnomer'; filter.dispatchEvent(new Event('change'));`);
  assert.ok(await evaluate("document.querySelectorAll('[data-finding]').length > 0 && [...document.querySelectorAll('[data-finding]')].every(e => e.textContent.includes('misnomer'))"));
  await evaluate(`const search = document.querySelector('#review-search'); search.value = 'no-such-concern-123'; search.dispatchEvent(new Event('change'));`);
  assert.equal(await evaluate("document.querySelectorAll('[data-finding]').length"), 0);
  console.log("PASS architecture keyboard selection, filters, search and retained drafts");

  for (const width of [1440, 390, 320]) {
    await send("Emulation.setDeviceMetricsOverride", { width, height: 900, deviceScaleFactor: 1, mobile: width < 600 });
    await evaluate(`document.querySelector('.tabs [data-view="map"]').click()`);
    const dimensions = await evaluate(`({width: document.documentElement.clientWidth, content: document.documentElement.scrollWidth,
      overflow: [...document.querySelectorAll('body *')].filter(e => e.getBoundingClientRect().right > document.documentElement.clientWidth + 1 && !e.closest('.map-wrap')).slice(0,8).map(e => ({tag:e.tagName, class:e.className, width:e.getBoundingClientRect().width, right:e.getBoundingClientRect().right}))})`);
    assert.ok(dimensions.content <= dimensions.width + 1, `No page overflow at ${width}: ${JSON.stringify(dimensions)}`);
  }
  console.log("PASS desktop, 390px and 320px layouts without page overflow");

  await send("Emulation.setDeviceMetricsOverride", { width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false });
  await evaluate(`document.querySelector('.tabs [data-view="board"]').click();
    window.reviewRequests = [];
    window.originalReviewFetch = window.fetch;
    window.fetch = async (url, init) => {
      if (url !== '/api/feedback') return window.originalReviewFetch(url, init);
      window.reviewRequests.push(JSON.parse(init.body));
      if (window.reviewRequests.length === 1) throw new Error('Simulated lost response');
      return Response.json({ url: 'https://github.com/abossard/sattle/issues/144', id: 144, action: 'comment' });
    };
    document.querySelector('[name="decision"]').checked = true;
    document.querySelector('[name="rationale"]').value = 'Browser-only transport check, never posted to GitHub';
    document.querySelector('[name="reviewKey"]').value = 'browser-only-test-key';
    document.querySelector('#feedback-form').requestSubmit();`);
  await waitFor("document.querySelector('#feedback-result').textContent.includes('Simulated lost response')");
  await evaluate("document.querySelector('#feedback-form').requestSubmit()");
  await waitFor("document.querySelector('#feedback-result').textContent.includes('Saved to GitHub')");
  assert.equal(await evaluate("window.reviewRequests[0].submissionId === window.reviewRequests[1].submissionId"), true);
  assert.equal(await evaluate("window.reviewRequests.length"), 2);
  await evaluate("window.fetch = window.originalReviewFetch");
  console.log("PASS feedback retry identity with browser transport mocked; no GitHub write");

  if (process.env.AXE_PATH) {
    await evaluate(readFileSync(process.env.AXE_PATH, "utf8"));
    for (const theme of ["dark", "light"]) {
      await evaluate(`document.documentElement.dataset.theme = ${JSON.stringify(theme)}`);
      const violations = await evaluate("axe.run(document, {runOnly: {type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa']}}).then(r => r.violations.map(v => ({id:v.id, nodes:v.nodes.map(n=>n.target)})))");
      assert.deepEqual(violations, [], `Accessibility violations in ${theme}`);
    }
    for (const theme of ["dark", "light"]) {
      const contrast = await evaluate(`(() => {
        document.documentElement.dataset.theme = ${JSON.stringify(theme)};
        const style = getComputedStyle(document.documentElement);
        const luminance = (name) => {
          let hex = style.getPropertyValue(name).trim().slice(1);
          if (hex.length === 3) hex = [...hex].map(v => v + v).join('');
          const rgb = [0,2,4].map(i => parseInt(hex.slice(i,i+2),16)/255).map(v => v <= 0.04045 ? v/12.92 : ((v+0.055)/1.055)**2.4);
          return rgb[0]*0.2126 + rgb[1]*0.7152 + rgb[2]*0.0722;
        };
        const bg = luminance('--panel-solid');
        return ['--cyan','--lime','--violet','--amber','--coral'].map(name => {
          const fg = luminance(name); return {name, ratio:(Math.max(fg,bg)+0.05)/(Math.min(fg,bg)+0.05)};
        });
      })()`);
      assert.ok(contrast.every(({ ratio }) => ratio >= 4.5), `${theme} accent text contrast: ${JSON.stringify(contrast)}`);
    }
    console.log("PASS accent text contrast in both themes, including gradient surfaces axe cannot evaluate");
    console.log("PASS dark and light WCAG accessibility checks");
  }
  assert.deepEqual(errors, [], "No uncaught browser errors");
  assert.equal(await evaluate("localStorage.length"), 0);
  assert.equal(await evaluate("sessionStorage.length"), 0);
  const fixture = await (await fetch(`${base}/review.json`)).json();
  fixture.findings = Array.from({ length: 1000 }, (_, index) => ({ ...fixture.findings[index % fixture.findings.length], id: `scale-${index}` }));
  fixture.modules = Array.from({ length: 200 }, (_, index) => ({ ...fixture.modules[index % fixture.modules.length], id: index < 10 ? fixture.modules[index].id : `module-${index}` }));
  const injected = await send("Page.addScriptToEvaluateOnNewDocument", { source: `const originalFetch = window.fetch; window.fetch = (url, init) => url === '/review.json' ? Promise.resolve(Response.json(${JSON.stringify(fixture)})) : originalFetch(url, init);` });
  await send("Page.navigate", { url: base });
  await waitFor("document.querySelectorAll('[data-finding]').length === 25");
  assert.ok(await evaluate("document.querySelector('#findings-title').parentElement.textContent.includes('of 1000 concerns')"));
  await evaluate("document.querySelector('[data-page=\"2\"]').click()");
  assert.equal(await evaluate("document.querySelectorAll('[data-finding]').length"), 25);
  await evaluate("document.querySelector('.tabs [data-view=\"map\"]').click()");
  assert.equal(await evaluate("document.querySelectorAll('.map-node').length"), 160);
  console.log("PASS actual DOM bounded to 25/1,000 findings and 160/200 modules");
  await send("Page.removeScriptToEvaluateOnNewDocument", { identifier: injected.result.identifier });
  await send("Page.navigate", { url: base });
  await waitFor("document.querySelectorAll('[data-finding]').length === 11");
  await evaluate("document.documentElement.dataset.theme = 'dark'");
  if (process.env.SCREENSHOT) {
    const screenshot = await send("Page.captureScreenshot", { format: "png" });
    writeFileSync(process.env.SCREENSHOT, Buffer.from(screenshot.result.data, "base64"));
  }
  console.log("PASS no uncaught errors or persistent browser state");
} finally {
  ws?.close();
  browser.kill("SIGTERM");
  await Promise.race([new Promise((resolve) => browser.once("exit", resolve)), sleep(3000)]);
  if (browser.exitCode === null && browser.signalCode === null) {
    browser.kill("SIGKILL");
    await new Promise((resolve) => browser.once("exit", resolve));
  }
  rmSync(directory, { recursive: true, force: true });
}
