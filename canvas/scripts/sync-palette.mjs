// Fetches GET /palette from a running engine into tests/fixtures/palette.json, so the tests read the engine's own operator, slot and
// aggregate declarations: a slot kind the panel cannot edit fails tests/slot-kinds.test.ts after the next sync instead of going unnoticed.
// Usage: node scripts/sync-palette.mjs [engine-url]   (default http://localhost:5080; any engine, the palette is kernel metadata)
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const base = (process.argv[2] ?? "http://localhost:5080").replace(/\/$/, "");
const to = resolve(dirname(fileURLToPath(import.meta.url)), "../tests/fixtures/palette.json");
const res = await fetch(`${base}/palette`).catch((e) => { console.error(`palette: no engine at ${base} (${e.message})`); process.exit(2); });
if (!res.ok) { console.error(`palette: ${base}/palette answered HTTP ${res.status}`); process.exit(2); }
const palette = await res.json();
mkdirSync(dirname(to), { recursive: true });
writeFileSync(to, `${JSON.stringify(palette, null, 1)}\n`);
console.log(`palette of kernel ${String(palette.kernelDigest).slice(0, 19)}: ${palette.operators.length} operators, ${palette.aggregates.length} aggregates, written to ${to}`);
