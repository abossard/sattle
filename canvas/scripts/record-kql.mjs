// Records the engine's answers to the KQL editor calls (PROTOCOL 3.41, 3.42) into tests/fixtures/kql.json.gz, so the text editor's tests run
// without an engine on the answers the engine gives: every shipped projection's text and fast analysis, and the cases of note 11, stage a
// (a seeded dcount, completion after a pipe and after `s.` in a graph-match, hover on a column).
// Usage: node scripts/record-kql.mjs [engine-url] [definitions-dir]   (defaults http://localhost:5080, ../engine/definitions)
import { readdirSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { gzipSync } from "node:zlib";

const here = dirname(fileURLToPath(import.meta.url));
const base = (process.argv[2] ?? "http://localhost:5080").replace(/\/$/, "");
const defs = resolve(process.argv[3] ?? resolve(here, "../../engine/definitions"));
const to = resolve(here, "../tests/fixtures/kql.json.gz");

const fail = (m) => { console.error(`record-kql: ${m}`); process.exit(2); };
const get = async (path) => { const r = await fetch(`${base}${path}`).catch((e) => fail(`no engine at ${base} (${e.message})`)); if (!r.ok) fail(`GET ${path}: HTTP ${r.status}`); return r.json(); };
const post = async (call, body) => { const r = await fetch(`${base}/kql/${call}`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) }); if (!r.ok) fail(`POST /kql/${call}: HTTP ${r.status}`); return { request: body, answer: await r.json() }; };
const at = (text, needle, plus = 0) => { const i = text.indexOf(needle); if (i < 0) fail(`"${needle}" is not in the text`); return i + plus; };

const projections = readdirSync(defs).filter((f) => f.endsWith(".kql")).map((f) => f.slice(0, -4)).sort();
const files = [];
for (const projection of projections) {
  const { digest, text } = await get(`/kql/${projection}`);
  files.push({ projection, digest, text, analyse: (await post("analyse", { projection, text, depth: "fast" })).answer });
}
const text = (p) => files.find((f) => f.projection === p).text;
const cap = "acme.biz.capabilityView", svc = "acme.svc.serviceDeps";
const seeded = text(cap).replace("summarize serviceCount = count() by capability", "summarize serviceCount = dcount(service) by capability");
const cases = {
  dcount: await post("analyse", { projection: cap, text: seeded, depth: "fast" }),
  afterPipe: await post("complete", { projection: svc, text: text(svc), offset: at(text(svc), "| ", 2) }),
  afterDot: await post("complete", { projection: svc, text: text(svc), offset: at(text(svc), "project service = s.", 20) }),
  hoverColumn: await post("hover", { projection: svc, text: text(svc), offset: at(text(svc), "project service = ", 9) }),
};
writeFileSync(to, gzipSync(`${JSON.stringify({ engine: base, files, cases }, null, 1)}\n`));
console.log(`${files.length} projections and ${Object.keys(cases).length} cases written to ${to}`);
