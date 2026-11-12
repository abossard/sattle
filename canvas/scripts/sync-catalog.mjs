// Fetches the catalog of a running engine (GET /graphs/catalog/snapshot) and its palette into tests/fixtures, so the editing tests
// read real definitions: every projection the engine serves, with the palette of the same kernel. Serve the synthetic estate
// fixture for it (the committed fixture is from that one). The snapshot is stored gzipped: it is about 5 MB of JSON.
// Usage: node scripts/sync-catalog.mjs [engine-url]   (default http://localhost:5080)
import { writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { gzipSync } from "node:zlib";

const base = (process.argv[2] ?? "http://localhost:5080").replace(/\/$/, "");
const dir = resolve(dirname(fileURLToPath(import.meta.url)), "../tests/fixtures");
const get = async (path) => {
  const res = await fetch(`${base}${path}`).catch((e) => { console.error(`catalog: no engine at ${base} (${e.message})`); process.exit(2); });
  if (!res.ok) { console.error(`catalog: ${base}${path} answered HTTP ${res.status}`); process.exit(2); }
  return res.json();
};
const [catalog, palette] = [await get("/graphs/catalog/snapshot"), await get("/palette")];
writeFileSync(resolve(dir, "catalog.estate.json.gz"), gzipSync(JSON.stringify(catalog)));
writeFileSync(resolve(dir, "catalog.estate.palette.json"), `${JSON.stringify(palette, null, 1)}\n`);
const projections = catalog.extent.entities.filter((e) => e.type.value === "Projection").length;
console.log(`catalog of kernel ${String(palette.kernelDigest).slice(0, 19)}: ${catalog.extent.entities.length} entities, ${projections} projections, written to ${dir}`);
