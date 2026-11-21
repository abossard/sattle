import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { cpSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const [capture, definitions, engine, work, windowCount = "4"] = process.argv.slice(2);
const programFile = "tutorial.signals.gkql.json";
const readJson = (path) => JSON.parse(readFileSync(path, "utf8"));
const program = readJson(join(definitions, programFile));
const { hysteresis: rule } = program.verbs.find(({ id }) => id === "cpuState");
const longer = Number(windowCount);
assert(Number.isSafeInteger(longer) && longer > rule.enterAfter, `Choose a whole number above ${rule.enterAfter}.`);

const samples = readFileSync(join(capture, "azure-metrics/aks.ndjson"), "utf8").split("\n").filter(Boolean).map((line) => JSON.parse(line));
let streak = 0;
const checkpoint = samples.find((row) => {
  const point = row.payload.value[0].timeseries[0].data[0];
  streak = point.average >= rule.above ? streak + 1 : 0;
  return streak === rule.enterAfter;
});
assert(checkpoint, "The captured CPU values must cross the baseline threshold.");

const modified = join(work, "definitions");
cpSync(definitions, modified, { recursive: true });
writeFileSync(join(modified, programFile), JSON.stringify({
  ...program,
  verbs: program.verbs.map((verb) => verb.id === "cpuState" ? { ...verb, hysteresis: { ...rule, enterAfter: longer } } : verb),
}, null, 2) + "\n");

function replay(label, defs) {
  const out = join(work, label);
  const result = spawnSync("dotnet", [engine, "replay", capture, "--defs", defs, "--space", "azureTutorial",
    "--lateness", "0s", "--until", checkpoint.capturedAt, "--out", out], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stderr || result.stdout);
  const { extent } = readJson(join(out, "signals.final.snapshot.json"));
  const prop = (node, key) => node.properties.find((entry) => entry.property.value.split(".").at(-1) === key)?.value.value;
  const signal = extent.entities.find((node) => prop(node, "armId") === checkpoint.meta.resourceId.toLowerCase());
  assert(signal, "The monitored AKS cluster has a CPU state.");
  return prop(signal, "state");
}

const baseline = replay("baseline", definitions);
const changed = replay("longer", modified);
assert.equal(baseline, rule.high);
assert.equal(changed, rule.initial);
console.log(`Same capture at ${checkpoint.capturedAt}`);
console.log(`${rule.enterAfter} high windows → ${baseline}`);
console.log(`${longer} high windows → ${changed}`);
