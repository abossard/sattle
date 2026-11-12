// Generates the default source from real engine output: the synthetic estate fixture replayed by the engine, written in the layout of the
// canvas StaticSource (`replay --out`) into public/demo/estate. The output is committed; nothing in it is hand-authored.
// Run: npm run demo   (runs the engine's built Release binaries in place; it never builds `engine/`: the engine session owns those binaries.
// It stops with a clear message when they are missing or older than the engine's sources. `ENGINE_DLL=path/to/sattle.dll` names other binaries,
// `npm run demo -- --build` builds and runs with `dotnet run`, only on request.)
// It fails when a graph that should have edges has none, so a projection that lost its edges can never be committed as the demo.
import { spawnSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, "../..");
const out = resolve(here, "../public/demo/estate");
const fixture = join(root, "engine/tests/Sattle.Tests/fixtures/estate");

/** Graphs of the estate space that must have edges: the master and every projection whose ontology has relationship types that the data uses. */
export const EXPECT_EDGES = ["master", "serviceTree", "capabilities", "deploymentHealth", "workloadStructure", "workload", "treeStructure", "treeRollup", "healthTree"];

/** The graphs of a manifest with the number of entities and relationships of their final snapshot (`finalOf(id)` reads it). */
export function sizesOf(manifest, finalOf) {
  return Object.keys(manifest.graphs).map((id) => {
    const extent = finalOf(id).extent;
    return { id, entities: extent.entities.length, relationships: extent.relationships.length };
  });
}
const sizes = (dir) => sizesOf(JSON.parse(readFileSync(join(dir, "manifest.json"), "utf8")), (id) => JSON.parse(readFileSync(join(dir, `${id}.final.snapshot.json`), "utf8")));

/** Problems of an output: a graph that should have edges and has none, or one that is missing. */
export function problems(found, expect = EXPECT_EDGES) {
  const by = new Map(found.map((g) => [g.id, g]));
  return expect.flatMap((id) => !by.has(id) ? [`${id}: the engine wrote no such graph`] : by.get(id).relationships === 0 ? [`${id}: ${by.get(id).entities} nodes and no edges`] : []);
}

const BUILT = join(root, "engine/src/Sattle.Cli/bin/Release/net10.0/sattle.dll");

/** The newest modification time among the engine's sources and definitions (what a build of `engine/` is made from). */
export function newestSource(dir = join(root, "engine")) {
  let newest = 0;
  for (const f of readdirSync(dir, { recursive: true })) {
    const rel = String(f);
    if (/(^|\/)(bin|obj|tests|node_modules)(\/|$)/.test(rel) || !/\.(cs|csproj|props|sattle)$/.test(rel)) continue;
    newest = Math.max(newest, statSync(join(dir, rel)).mtimeMs);
  }
  return newest;
}
/** Why binaries cannot be used as they are: missing, or older than the sources they should have been built from (null when fine). */
export function binariesProblem(dll, builtAt, sourcesAt) {
  if (builtAt === null) return `the engine binaries are not built (${dll}). The engine session builds them; ask it for a consistent build.`;
  if (builtAt < sourcesAt) return `the engine binaries (${dll}) are older than the engine's sources by ${Math.round((sourcesAt - builtAt) / 60000)} minutes. Ask the engine session whether a rebuild is under way and wait for a consistent build.`;
  return null;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const tmp = mkdtempSync(join(tmpdir(), "sattle-demo-"));
  let cli;
  if (process.argv.includes("--build")) cli = ["dotnet", ["run", "--project", join(root, "engine/src/Sattle.Cli"), "-c", "Release", "--"]];
  else {
    const dll = process.env.ENGINE_DLL ?? BUILT;
    const problem = binariesProblem(dll, existsSync(dll) ? statSync(dll).mtimeMs : null, process.env.ENGINE_DLL ? 0 : newestSource());
    if (problem) { console.error(`demo: ${problem}`); process.exit(1); }
    cli = ["dotnet", [dll]];
  }
  const r = spawnSync(cli[0], [...cli[1], "replay", fixture, "--cluster", "synth", "--out", tmp], { cwd: root, stdio: ["ignore", "inherit", "inherit"] });
  if (r.status !== 0) { console.error(`demo: the engine exited with ${r.status}`); process.exit(1); }
  const found = sizes(tmp);
  const bad = problems(found);
  for (const g of found) console.log(`  ${g.id}: ${g.entities} nodes, ${g.relationships} edges`);
  if (bad.length) { console.error(`demo: not written, ${bad.length} graph(s) without edges:\n  ${bad.join("\n  ")}`); rmSync(tmp, { recursive: true }); process.exit(1); }
  if (existsSync(out)) rmSync(out, { recursive: true });
  mkdirSync(out, { recursive: true });
  for (const f of readdirSync(tmp)) cpSync(join(tmp, f), join(out, f), { recursive: true });
  rmSync(tmp, { recursive: true });
  console.log(`demo: ${found.length} graphs written to ${out}`);
}
