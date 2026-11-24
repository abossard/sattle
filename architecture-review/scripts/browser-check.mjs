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
    await send("Input.dispatchKeyEvent", { type: "keyDown", key: name, code: name, windowsVirtualKeyCode: name === "Enter" ? 13 : 32, text: name === "Enter" ? "\r" : " " });
    await send("Input.dispatchKeyEvent", { type: "keyUp", key: name, code: name });
  };
  const click = async (selector) => {
    const point = await evaluate(`(() => {
      const element = document.querySelector(${JSON.stringify(selector)});
      element.scrollIntoView({block:'center'});
      const rect = element.getBoundingClientRect();
      return {x:rect.x + rect.width / 2, y:rect.y + rect.height / 2};
    })()`);
    await send("Input.dispatchMouseEvent", { type: "mousePressed", ...point, button: "left", clickCount: 1 });
    await send("Input.dispatchMouseEvent", { type: "mouseReleased", ...point, button: "left", clickCount: 1 });
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

  assert.equal(await evaluate("document.querySelectorAll('[data-view]').length"), 2, "One global view switch");
  assert.ok(await evaluate("document.querySelector('[data-finding]').getBoundingClientRect().bottom < innerHeight"), "Queue available without scrolling past a hero");
  assert.ok(await evaluate("document.querySelector('#feedback-form').getBoundingClientRect().top < document.querySelector('.discussion').getBoundingClientRect().top"), "Response precedes discussion history");
  console.log("PASS C1 workflow-first hierarchy and single view switch");

  await click(".review-context > summary");
  assert.ok(await evaluate("document.querySelector('.review-context').open && document.querySelector('.metric').checkVisibility()"));
  await evaluate("document.querySelector('.review-context > summary').focus()");
  await key("Enter");
  assert.equal(await evaluate("document.querySelector('.review-context').open"), false);
  await click(".evidence-details > summary");
  assert.ok(await evaluate("document.querySelector('.evidence code').checkVisibility()"));
  await evaluate("document.querySelector('.evidence-details > summary').focus()");
  await key("Enter");
  assert.equal(await evaluate("document.querySelector('.evidence-details').open"), false);
  await click("[data-finding]:nth-child(2)");
  const selectedTitle = await evaluate("document.querySelector('[data-finding]').textContent");
  await evaluate("document.querySelector('[data-finding]').focus()");
  await key("Enter");
  assert.ok(selectedTitle.includes(await evaluate("document.querySelector('#detail-title').textContent")));
  console.log("PASS C2 context and evidence disclosures, native pointer and keyboard queue selection");

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

  await evaluate(`document.querySelector('#review-filter').value = 'all';
    document.querySelector('#review-filter').dispatchEvent(new Event('change'));
    document.querySelector('#review-search').value = '';
    document.querySelector('#review-search').dispatchEvent(new Event('change'));`);
  for (const width of [1440, 390, 320]) {
    await send("Emulation.setDeviceMetricsOverride", { width, height: 900, deviceScaleFactor: 1, mobile: width < 600 });
    for (const view of ["map", "board"]) {
      await click(`.tabs [data-view="${view}"]`);
      const dimensions = await evaluate(`({width: document.documentElement.clientWidth, content: document.documentElement.scrollWidth,
        overflow: [...document.querySelectorAll('body *')].filter(e => e.getBoundingClientRect().right > document.documentElement.clientWidth + 1 && !e.closest('.map-wrap')).slice(0,8).map(e => ({tag:e.tagName, class:e.className, width:e.getBoundingClientRect().width, right:e.getBoundingClientRect().right}))})`);
      assert.equal(dimensions.width, width, "Browser viewport matches requested width");
      assert.ok(dimensions.content <= dimensions.width + 1, `No ${view} page overflow at ${width}: ${JSON.stringify(dimensions)}`);
      if (width < 600) {
        const targets = await evaluate(`[...document.querySelectorAll('button, input:not([type="hidden"]):not([type="radio"]), select, summary, .option')].filter(e => e.checkVisibility()).filter(e => e.getBoundingClientRect().height < 44).map(e => ({tag:e.tagName, text:e.textContent.slice(0,40), height:e.getBoundingClientRect().height}))`);
        assert.deepEqual(targets, [], `C3 mobile control height at ${width}`);
      }
      if (process.env.SCREENSHOT) {
        await evaluate("scrollTo(0, 0)");
        const screenshot = await send("Page.captureScreenshot", { format: "png" });
        writeFileSync(`${process.env.SCREENSHOT}.${view}-${width}.png`, Buffer.from(screenshot.result.data, "base64"));
      }
    }
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
      return new Promise(resolve => {
        window.finishReviewRequest = () => resolve(Response.json({ url: 'https://github.com/abossard/sattle/issues/144', id: 144, action: 'comment' }));
      });
    };
    document.querySelectorAll('[name="decision"]').forEach(input => input.checked = false);
    document.querySelector('[name="rationale"]').value = '';
    document.querySelector('#feedback-form').requestSubmit();`);
  assert.equal(await evaluate("window.reviewRequests.length"), 0, "Invalid form must not send feedback");
  await evaluate(`
    document.querySelector('[name="decision"]').checked = true;
    document.querySelector('[name="rationale"]').value = 'Browser-only transport check, never posted to GitHub';
    document.querySelector('[name="reviewKey"]').value = 'browser-only-test-key';
    document.querySelector('#feedback-form').requestSubmit();`);
  await waitFor("document.querySelector('#feedback-result').textContent.includes('Simulated lost response')");
  assert.equal(await evaluate("document.querySelector('#feedback-form').dataset.state"), "error");
  await evaluate("document.querySelector('#feedback-form').requestSubmit()");
  await waitFor("document.querySelector('#feedback-form').dataset.state === 'loading'");
  assert.ok(await evaluate("document.querySelector('button[type=\"submit\"]').disabled && document.querySelector('button[type=\"submit\"]').getAttribute('aria-busy') === 'true'"));
  await evaluate("window.finishReviewRequest()");
  await waitFor("document.querySelector('#feedback-result').textContent.includes('Saved to GitHub')");
  assert.equal(await evaluate("document.querySelector('#feedback-form').dataset.state"), "success");
  assert.equal(await evaluate("window.reviewRequests[0].submissionId === window.reviewRequests[1].submissionId"), true);
  assert.equal(await evaluate("window.reviewRequests.length"), 2);
  await evaluate("window.fetch = window.originalReviewFetch");
  console.log("PASS feedback retry identity with browser transport mocked; no GitHub write");

  if (process.env.AXE_PATH) {
    await evaluate(readFileSync(process.env.AXE_PATH, "utf8"));
    for (const theme of ["dark", "light"]) {
      if (await evaluate("document.documentElement.dataset.theme") !== theme) await click("#theme-toggle");
      assert.equal(await evaluate("document.documentElement.dataset.theme"), theme, "Theme button updates the page");
      if (process.env.SCREENSHOT) {
        await evaluate("scrollTo(0, 0)");
        const screenshot = await send("Page.captureScreenshot", { format: "png" });
        writeFileSync(`${process.env.SCREENSHOT}.${theme}.png`, Buffer.from(screenshot.result.data, "base64"));
      }
      await click(".review-context > summary");
      await click(".evidence-details > summary");
      const violations = await evaluate("axe.run(document, {runOnly: {type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa']}}).then(r => r.violations.map(v => ({id:v.id, nodes:v.nodes.map(n=>n.target)})))");
      assert.deepEqual(violations, [], `Accessibility violations in ${theme}`);
      await click(".evidence-details > summary");
      await click(".review-context > summary");
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
    console.log("PASS accent text contrast in both themes");
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
