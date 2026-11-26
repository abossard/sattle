import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
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
  const review = JSON.parse(readFileSync(new URL("../public/review.json", import.meta.url), "utf8"));
  const repository = `https://github.com/${review.repository}/`;
  const shots = process.env.SCREENSHOT;
  if (shots) mkdirSync(shots, { recursive: true });

  for (const [width, height, mobile] of [[1280, 900, false], [390, 844, true]]) {
    for (const scheme of ["light", "dark"]) {
      await send("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: mobile ? 2 : 1, mobile });
      await send("Emulation.setEmulatedMedia", { features: [{ name: "prefers-color-scheme", value: scheme }, { name: "prefers-reduced-motion", value: scheme === "dark" ? "reduce" : "no-preference" }] });
      await send("Page.navigate", { url: base });
      await waitFor("document.documentElement.dataset.ready === 'true'");
      await evaluate("for (const image of document.images) image.loading = 'eager'");
      await waitFor("[...document.images].every((image) => image.complete)");
      await evaluate("Promise.all(document.getAnimations().map((animation) => animation.finished))");
      const page = await evaluate(`(() => ({
        overflow: document.documentElement.scrollWidth - innerWidth,
        squares: document.querySelectorAll('.square').length,
        foreign: [...document.querySelectorAll('a[href^="http"]')].map((a) => a.href).filter((href) => !href.startsWith(${JSON.stringify(repository)})),
        brokenImages: [...document.images].filter((image) => !image.naturalWidth).length,
        smallTargets: [...document.querySelectorAll('.chip, .step-button, [role=tab]')].filter((el) => el.getBoundingClientRect().height < (innerWidth < 600 ? 44 : 36)).length,
        canvas: getComputedStyle(document.body).backgroundColor,
      }))()`);
      assert.ok(page.overflow <= 0, `No horizontal scroll at ${width}px ${scheme}: ${page.overflow}px`);
      assert.equal(page.squares, review.totals.commits, "One square per commit");
      assert.deepEqual(page.foreign, [], "Links only into the repository");
      assert.equal(page.brokenImages, 0, "Every screenshot loads");
      assert.equal(page.smallTargets, 0, "Controls keep their touch size");
      assert.equal(page.canvas === "rgb(14, 15, 16)", scheme === "dark", `Background follows the ${scheme} scheme`);
      console.log(`PASS ${width}px ${scheme}: ${page.squares} commits, no overflow, links and images`);
      if (shots) {
        const { result: metrics } = await send("Page.getLayoutMetrics");
        const screenshot = await send("Page.captureScreenshot", { format: "png", captureBeyondViewport: true, clip: { x: 0, y: 0, width, height: Math.ceil(metrics.cssContentSize.height), scale: 1 } });
        writeFileSync(`${shots}/${mobile ? "phone" : "desktop"}-${scheme}.png`, Buffer.from(screenshot.result.data, "base64"));
      }
    }
  }

  await send("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
  await send("Emulation.setEmulatedMedia", { features: [{ name: "prefers-color-scheme", value: "light" }, { name: "prefers-reduced-motion", value: "no-preference" }] });
  await send("Page.navigate", { url: base });
  await waitFor("document.documentElement.dataset.ready === 'true'");
  await click('[data-filter="engine"]');
  assert.equal(await evaluate("document.querySelectorAll('.squares li:not(.is-dimmed)').length"), review.areas.find((area) => area.id === "engine").commits, "The area filter keeps only engine commits bright");
  assert.equal(await evaluate("[...document.querySelectorAll('.area-group')].filter((group) => !group.hidden).length"), 1, "The area filter shows one area of built work");
  await click('[data-stepper="week"] [data-event="next"]');
  await click('[data-stepper="week"] [data-event="next"]');
  assert.equal(await evaluate("document.querySelectorAll('.day.is-future').length"), review.days.length - 2, "The week replay fades the days still to come");
  await click('[data-stepper="changes"] [data-event="next"]');
  await click('[data-stepper="changes"] [data-event="next"]');
  await evaluate("Promise.all(document.getAnimations().map((animation) => animation.finished))");
  assert.equal(await evaluate("document.querySelectorAll('#panel-changes .stage-column:last-of-type .row-chip').length"), 2, "Batch 2 sends one -1 and one +1");
  assert.ok(await evaluate("document.querySelector('#panel-changes .stage-note').textContent.startsWith('One more api error')"), "Each batch explains itself");
  if (shots) {
    const box = await evaluate("(() => { const r = document.querySelector('#explain').getBoundingClientRect(); return { x: 0, y: r.top + scrollY, width: innerWidth, height: r.height }; })()");
    const screenshot = await send("Page.captureScreenshot", { format: "png", captureBeyondViewport: true, clip: { ...box, scale: 1 } });
    writeFileSync(`${shots}/explainer-changes-${await evaluate("innerWidth")}.png`, Buffer.from(screenshot.result.data, "base64"));
  }
  await click('[data-stepper="flow"] [data-event="next"]');
  await click('.node[data-id="review"]');
  assert.ok(await evaluate("document.querySelector('.flow-detail').textContent.includes('back to')"), "A step opens its detail with the loops that touch it");
  await click('[role="tab"]#tab-kind');
  assert.equal(await evaluate("document.querySelector('#panel-kind').hidden"), false, "A tab shows its own chart");
  console.log("PASS area filter, week replay, flow detail and tabs");
  assert.deepEqual(errors, [], "No uncaught errors");
  console.log("PASS no uncaught errors");
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
