#!/usr/bin/env node
// Deterministic scrubber and verifier for capture fixtures.
//   scrub.mjs build  <captureDir> <fixturesDir>   sample every source, scrub, write; also space-arguments.json
//   scrub.mjs tutorial <captureDir> <fixturesDir> scrub the full ARG inventory and its matched Azure Monitor observations
//   scrub.mjs verify <captureDir> <fixturesDir>   fail if any identifying value of the capture survives in the fixtures
//   scrub.mjs recording <recordingDir> <outDir>    scrub a collector recording (lab/collector), names from SCRUB_NAMES
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
import { aliases, makeScrubber } from './scrub/core.mjs';
import { harvestCapture } from './scrub/harvest.mjs';
import { spaceArguments } from './scrub/space-arguments.mjs';
import { tutorialInventoryPayload } from './scrub/tutorial-inventory.mjs';
import { tutorialKubernetes } from './scrub/tutorial-kubernetes.mjs';
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
  const { names, persons, clusters } = await harvestCapture(captureDir);
  const scrub = makeScrubber({ salt: saltFor(captureDir), names, persons, clusters });
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
  const scrubbed = new Map([...merged].map(([rel, records]) => [rel, records.map(scrub)]));
  for (const [rel, records] of scrubbed) {
    const out = join(fixturesDir, rel);
    mkdirSync(dirname(out), { recursive: true });
    writeFileSync(out, records.map((r) => JSON.stringify(r)).join('\n') + '\n');
    console.log(`${records.length}\t${rel}`);
  }
  // the binding of the engine's space to this data, from the scrubbed records only
  mkdirSync(fixturesDir, { recursive: true });
  writeFileSync(join(fixturesDir, 'space-arguments.json'), JSON.stringify(spaceArguments(scrubbed), null, 2) + '\n');
}

const AKS_TYPE = 'microsoft.containerservice/managedclusters';
const AKS_CPU = 'apiserver_cpu_usage_percentage';
const MINUTE_MS = 60_000;

async function inventory(file) {
  const rows = new Map();
  if (!existsSync(file)) return rows;
  for await (const line of lines(file)) {
    const record = JSON.parse(line);
    const row = record.payload;
    if (record.source === 'arg' && [row?.id, row?.name, row?.type].every((field) => typeof field === 'string' && field.trim())) {
      rows.set(row.id.toLowerCase(), record);
    }
  }
  return rows;
}

// Keep a single measured series in event-time order, with a gap starting a new run.
function longestRun(data) {
  const points = new Map();
  for (const { timeStamp, average } of data) {
    const time = Date.parse(timeStamp);
    if (Number.isFinite(time) && Number.isFinite(average)) points.set(time, { time, timeStamp, average });
  }
  let run = [];
  let longest = [];
  for (const point of [...points.values()].sort((a, b) => a.time - b.time)) {
    if (run.length && point.time - run.at(-1).time !== MINUTE_MS) run = [];
    run.push(point);
    if (run.length > longest.length) longest = run;
  }
  return longest;
}

async function clusterReadings(file, resources) {
  let selected = { points: [] };
  if (!existsSync(file)) return selected;
  for await (const line of lines(file)) {
    const record = JSON.parse(line);
    const resourceId = record.meta?.resourceId;
    if (record.source !== 'azure-metrics' || typeof resourceId !== 'string') continue;
    const resource = resources.get(resourceId.toLowerCase());
    if (resource?.payload.type?.toLowerCase() !== AKS_TYPE) continue;
    for (const metric of record.payload?.value ?? []) {
      if (metric.name?.value !== AKS_CPU) continue;
      for (const series of metric.timeseries ?? []) {
        const points = longestRun(series.data ?? []);
        if (points.length > selected.points.length) selected = { record, points };
      }
    }
  }
  return selected;
}

const aggregations = ['average', 'total', 'minimum', 'maximum', 'count'];

function metricEnvelope(record, metric, sample, time, index, key, resource) {
  return {
    v: 1, source: 'azure-metrics', seq: index + 1, key,
    capturedAt: new Date(time + MINUTE_MS).toISOString(),
    meta: {
      resource, resourceId: record.meta.resourceId, interval: 'PT1M',
      originalCapturedAt: record.capturedAt, tutorialClock: 'sample-time-plus-one-minute',
    },
    payload: { value: [{ name: { value: metric }, timeseries: [{ data: [sample] }] }] },
  };
}

async function otherMetrics(directory, inventoryIds, primaryId, range) {
  const { keyFor } = await import('../capture/lib/envelope.mjs');
  const readings = new Map();
  for (const file of readdirSync(directory).filter((name) => name.endsWith('.ndjson')).sort()) {
    for await (const line of lines(join(directory, file))) {
      const record = JSON.parse(line);
      const resourceId = record.meta?.resourceId;
      if (record.source !== 'azure-metrics' || typeof resourceId !== 'string') continue;
      const scope = resourceId.toLowerCase();
      if (!inventoryIds.has(scope)) continue;
      for (const metric of record.payload?.value ?? []) {
        const name = metric.name?.value;
        if (typeof name !== 'string' || (scope === primaryId && name === AKS_CPU)) continue;
        for (const series of metric.timeseries ?? []) {
          for (const datum of series.data ?? []) {
            const time = Date.parse(datum.timeStamp);
            if (!Number.isFinite(time) || time < range[0] || time > range[1]) continue;
            if (!Number.isFinite(datum.average) && !Number.isFinite(datum.total)) continue;
            const sample = { timeStamp: datum.timeStamp };
            for (const field of aggregations) if (Number.isFinite(datum[field])) sample[field] = datum[field];
            // Use the same resource/metric/dimension/time identity as the live forwarder to discard duplicate pulls.
            const payload = { value: [{ name: { value: name }, timeseries: [{ metadatavalues: series.metadatavalues, data: [sample] }] }] };
            const identity = keyFor('azure-metrics', payload, { resourceId: scope });
            readings.set(identity, { record, name, sample, time });
          }
        }
      }
    }
  }
  return [...readings.values()].sort((left, right) => left.time - right.time)
    .map(({ record, name, sample, time }, index) => metricEnvelope(record, name, sample, time, index, `tutorial/azure-metrics/other/${index + 1}`));
}

async function tutorial(captureDir, fixturesDir) {
  const resources = await inventory(join(captureDir, 'arg/resources.ndjson'));
  const containers = await inventory(join(captureDir, 'arg/resourcecontainers.ndjson'));
  const metricDirectory = join(captureDir, 'azure-metrics');
  const { record, points } = await clusterReadings(join(metricDirectory, 'aks.ndjson'), resources);
  if (points.length < 5) throw new Error('Need five or more consecutive one-minute AKS CPU readings with a matching Resource Graph resource');

  const capturedAt = new Date(points[0].time).toISOString();
  const salt = saltFor(captureDir);
  const alias = aliases(salt);
  const argRecords = (rows, query) => [...rows.values()].map((row, index) => ({
    v: 1, source: 'arg', seq: row.seq, key: `tutorial/arg/${query}/${index + 1}`, capturedAt,
    meta: { query, originalCapturedAt: row.capturedAt, tutorialClock: 'inventory-at-first-sample' },
    payload: tutorialInventoryPayload(row.payload, alias),
  }));
  const metricRows = points.map(({ time, timeStamp, average }, index) => metricEnvelope(
    record, AKS_CPU, { timeStamp, average }, time, index, `tutorial/azure-metrics/${index + 1}`, 'aks'));
  const other = await otherMetrics(metricDirectory, new Set([...resources.keys(), ...containers.keys()]),
    record.meta.resourceId.toLowerCase(), [points[0].time, points.at(-1).time]);
  const { objects, calls } = await tutorialKubernetes(captureDir, lines, alias, points[0].time, points.at(-1).time);
  const scrub = makeScrubber({ salt, ...await harvestCapture(captureDir) });
  const outputs = [
    ['arg/resources.ndjson', argRecords(resources, 'resources')],
    ['arg/resourcecontainers.ndjson', argRecords(containers, 'resourcecontainers')],
    ['azure-metrics/aks.ndjson', metricRows],
    ['azure-metrics/other.ndjson', other],
    ['kubernetes/list.ndjson', objects],
    ['hubble/flows.ndjson', calls],
  ];
  for (const [relativePath, rows] of outputs) {
    const out = join(fixturesDir, relativePath);
    mkdirSync(dirname(out), { recursive: true });
    writeFileSync(out, rows.map((row) => `${JSON.stringify(scrub(row))}\n`).join(''));
    console.log(`${rows.length}\t${relativePath}`);
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

// A collector recording (lab/collector, --record): every value as text, the names come from SCRUB_NAMES (one per
// line: cluster, resource group, nodes, apps) and SCRUB_LITERALS. Fails when one of them, an id of the recording
// or a public address survives; reports counts only.
async function recording(recordingDir, outDir) {
  const list = (v) => (process.env[v] ?? '').split('\n').map((x) => x.trim()).filter(Boolean);
  const names = list('SCRUB_NAMES');
  const literals = list('SCRUB_LITERALS');
  const scrub = makeScrubber({ salt: saltFor(recordingDir), names, clusters: names, literals });
  const deny = new Set([...names, ...literals].map((x) => x.toLowerCase()));
  const ids = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi;
  let records = 0;
  let found = 0;
  for (const file of walkFiles(recordingDir)) {
    const rows = [];
    for await (const line of lines(file)) {
      for (const id of line.match(ids) ?? []) deny.add(id.toLowerCase());
      rows.push(JSON.stringify(scrub(JSON.parse(line))));
    }
    const text = rows.join('\n').toLowerCase();
    found += [...deny].filter((d) => text.includes(d)).length;
    found += (text.match(/(?<![\d.])\d{1,3}(?:\.\d{1,3}){3}(?![\d.])/g) ?? []).filter((ip) => !/^(10\.|127\.|169\.254\.|172\.(1[6-9]|2\d|3[01])\.|192\.168\.|198\.1[89]\.|0\.)/.test(ip)).length;
    const out = join(outDir, relative(recordingDir, file));
    mkdirSync(dirname(out), { recursive: true });
    writeFileSync(out, rows.map((r) => `${r}\n`).join(''));
    records += rows.length;
  }
  console.log(`${records} records scrubbed, ${deny.size} names and ids checked`);
  if (found) { console.error(`FAIL: ${found} names, ids or public addresses survive`); process.exit(1); }
}

// Run as a program, also from a path with spaces or through a link
const isMain = () => { try { return realpathSync(process.argv[1]) === realpathSync(fileURLToPath(import.meta.url)); } catch { return resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url); } };
if (isMain()) {
  const [cmd, captureDir, fixturesDir] = process.argv.slice(2);
  const actions = { build, tutorial, verify, recording };
  if (!actions[cmd] || !captureDir || !fixturesDir) { console.error('usage: scrub.mjs build|tutorial|verify|recording <captureDir> <fixturesDir>'); process.exit(2); }
  await actions[cmd](resolve(captureDir), resolve(fixturesDir));
}
