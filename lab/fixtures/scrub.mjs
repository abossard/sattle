#!/usr/bin/env node
// Deterministic scrubber and verifier for capture fixtures.
//   scrub.mjs build  <captureDir> <fixturesDir>   sample every source, scrub, write
//   scrub.mjs verify <captureDir> <fixturesDir>   fail if any identifying value of the capture survives in the fixtures
// Optional extra values to scrub (for example the signed-in user) come in SCRUB_LITERALS, one per line; the capture
// itself already names its tenant, subscription, resources and people.
// Aliases are HMACs under a local secret salt (captures/.scrub-salt, gitignored): the same input always maps to
// the same alias across every source, and aliases cannot be brute-forced back from the public fixtures.
// The scrub is a field-aware walk over parsed JSON (scrub/core.mjs, scrub/rules.mjs); verify (scrub/verify.mjs) is
// independent of it.
import { randomBytes } from 'node:crypto';
import { createReadStream, existsSync, mkdirSync, readFileSync, readdirSync, realpathSync, writeFileSync } from 'node:fs';
import { createInterface } from 'node:readline';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeScrubber } from './scrub/core.mjs';
import { harvestCapture } from './scrub/harvest.mjs';
import { checkFixtures, denyList } from './scrub/verify.mjs';

// What to sample per file: group function, records per group, cap.
const SAMPLE = {
  'arg/resources.ndjson': { group: (r) => r.payload.type, per: 1, max: 10 },
  'arg/resourcecontainers.ndjson': { group: (r) => r.payload.type, per: 2, max: 4 },
  'arg/pages.ndjson': { group: () => 'p', per: 5, max: 5 },
  'kubernetes/list.ndjson': { group: (r) => r.meta?.list?.kind, per: 1, max: 25 },
  'kubernetes/watch.*.ndjson': { group: (r) => `${r.payload.type}/${r.payload.object?.kind}`, per: 1, max: 1, merge: 'kubernetes/watch.ndjson' },
  'hubble/flows.ndjson': { group: (r) => hubbleGroup(r.payload), per: 2, max: 14 },
  'prometheus/range.ndjson': { group: (r) => r.meta?.name, per: 1, max: 16 },
  'azure-metrics/*.ndjson': { group: () => 'm', per: 1, max: 1 },
  'activity-log/backfill.ndjson': { group: (r) => r.payload.operationName?.value, per: 1, max: 10 },
  'vnet-flow-logs/records.ndjson': { group: (r) => r.payload.flowRecords.flows.some((f) => f.flowGroups.some((g) => g.flowTuples.some((t) => t.split(',')[7] === 'D'))) ? 'deny' : 'allow', per: 2, max: 4 },
  'eventhub/*.ndjson': { group: (r) => r.payload?.source ?? 'raw', per: 3, max: 8 },
};
const hubbleGroup = (p) => (p.lost_events ? 'lost' : `${p.flow?.verdict}/${p.flow?.l7 ? 'l7' : (p.flow?.l4 && Object.keys(p.flow.l4)[0])}/${p.flow?.traffic_direction}`);

const escapeRe = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

// Fixture size cap for flow log records: a few flows, groups and tuples, denied (state D) first so a deny survives.
const isDeny = (t) => t.split(',')[7] === 'D';
const denyFirst = (xs, has) => [...xs.filter(has), ...xs.filter((x) => !has(x))];
const capFlows = (r) => (r.source === 'vnet-flow-log' && r.payload?.flowRecords?.flows
  ? { ...r, payload: { ...r.payload, flowRecords: { flows: denyFirst(r.payload.flowRecords.flows, (f) => f.flowGroups.some((g) => g.flowTuples.some(isDeny))).slice(0, 3)
    .map((f) => ({ ...f, flowGroups: denyFirst(f.flowGroups, (g) => g.flowTuples.some(isDeny)).slice(0, 3).map((g) => ({ ...g, flowTuples: denyFirst(g.flowTuples, isDeny).slice(0, 5) })) })) } } }
  : r);
const capSeries = (r0) => { const r = capFlows(r0); return (r.source === 'prometheus' && Array.isArray(r.payload?.data?.result) ? { ...r, payload: { ...r.payload, data: { ...r.payload.data, result: r.payload.data.result.slice(0, 3) } } } : r); };

// ---------- actions ----------
const walkFiles = (dir) => readdirSync(dir, { withFileTypes: true }).flatMap((d) => (d.isDirectory() ? walkFiles(join(dir, d.name)) : d.name.endsWith('.ndjson') ? [join(dir, d.name)] : []));
const globRe = (g) => new RegExp(`^${g.split('*').map(escapeRe).join('[^/]*')}$`);

// Captures can be gigabytes: always stream, one line at a time.
async function* lines(file) {
  for await (const l of createInterface({ input: createReadStream(file), crlfDelay: Infinity })) if (l) yield l;
}

function saltFor(captureDir) {
  const f = join(dirname(captureDir), '.scrub-salt');
  if (!existsSync(f)) writeFileSync(f, randomBytes(32).toString('hex'), { mode: 0o600 });
  return readFileSync(f, 'utf8').trim();
}

const captureFiles = (captureDir) => walkFiles(captureDir).map((f) => ({ file: f, rel: relative(captureDir, f) }));

async function build(captureDir, fixturesDir) {
  const { names, persons } = await harvestCapture(captureDir);
  const scrub = makeScrubber({ salt: saltFor(captureDir), names, persons });
  const merged = new Map();
  for (const { file, rel } of captureFiles(captureDir)) {
    const spec = Object.entries(SAMPLE).find(([g]) => globRe(g).test(rel))?.[1];
    if (!spec) continue;
    // stream and stop as soon as the sample is full
    const seen = new Map(); const picked = [];
    for await (const l of lines(file)) {
      const r = JSON.parse(l); const g = String(spec.group(r));
      if ((seen.get(g) ?? 0) >= spec.per) continue;
      seen.set(g, (seen.get(g) ?? 0) + 1); picked.push(r);
      if (picked.length >= spec.max) break;
    }
    const target = spec.merge ?? rel;
    merged.set(target, [...(merged.get(target) ?? []), ...picked.map(capSeries)]);
  }
  for (const [rel, records] of merged) {
    const out = join(fixturesDir, rel);
    mkdirSync(dirname(out), { recursive: true });
    writeFileSync(out, records.map((r) => JSON.stringify(scrub(r))).join('\n') + '\n');
    console.log(`${records.length}\t${rel}`);
  }
}

async function verify(captureDir, fixturesDir) {
  const deny = await denyList(captureDir);
  const found = checkFixtures(fixturesDir, deny);
  console.log(`checked ${found.checked.values} fixture values against ${found.checked.ids} ids and addresses, ${found.checked.names} names and ${found.checked.where} names, tags and claims that are also ordinary words (checked where they came from) from the capture`);
  // report counts only: never print the real values
  if (found.ids || found.names || found.positional || found.strayMails) {
    console.error(`FAIL: ${found.ids} ids or addresses, ${found.names} names, ${found.positional} values at the place of a name, tag or claim, and ${found.strayMails} unscrubbed e-mails found in fixtures`);
    process.exit(1);
  }
  console.log('OK: no identifying value of the capture found in fixtures');
}

// Run as a program, also from a path with spaces or through a link
const isMain = () => { try { return realpathSync(process.argv[1]) === realpathSync(fileURLToPath(import.meta.url)); } catch { return resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url); } };
if (isMain()) {
  const [cmd, captureDir, fixturesDir] = process.argv.slice(2);
  const actions = { build, verify };
  if (!actions[cmd] || !captureDir || !fixturesDir) { console.error('usage: scrub.mjs build|verify <captureDir> <fixturesDir>'); process.exit(2); }
  await actions[cmd](resolve(captureDir), resolve(fixturesDir));
}
