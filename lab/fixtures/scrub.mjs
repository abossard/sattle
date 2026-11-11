#!/usr/bin/env node
// Deterministic scrubber and verifier for capture fixtures.
//   scrub.mjs build  <captureDir> <fixturesDir>   sample every source, scrub, write
//   scrub.mjs verify <captureDir> <fixturesDir>   fail if any real identifier survives in the fixtures
// Extra literals to scrub (tenant, subscription, user, display name) come in SCRUB_LITERALS, one per line.
// Aliases are HMACs under a local secret salt (captures/.scrub-salt, gitignored): the same input always maps to
// the same alias across every source, and aliases cannot be brute-forced back from the public fixtures.
import { createHmac, randomBytes } from 'node:crypto';
import { createReadStream, existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { createInterface } from 'node:readline';
import { dirname, join, relative } from 'node:path';

// ---------- data ----------
const GUID = /\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi;
// ids written without dashes, by field name (role assignment principalId in Activity Log evidence); other 32-digit hex such as content hashes stays
const ID_BARE = /((?:principal|object|tenant|subscription|client|user)id"\s*:\s*")([0-9a-f]{32})(")/gi;
const EMAIL = /[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g;
const MAC = /\b(?:[0-9a-f]{2}:){5}[0-9a-f]{2}\b/gi;
// NIC MACs in Network Watcher records and blob names are 12 bare hex digits
const MAC_BARE = /(macaddress(?:"\s*:\s*"|=))([0-9a-f]{12})/gi;
const IPV4 = /(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![\d.]|\w)/g;
const IPV6 = /(?<![:\w])(?:(?:[0-9a-f]{1,4}:){7}[0-9a-f]{1,4}|(?:[0-9a-f]{1,4}:){1,7}:(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){0,6})?)(?![:\w])/gi;
const AKS_HOST = /[a-z0-9-]+\.((?:portal\.)?hcp\.[a-z0-9]+\.azmk8s\.io)/gi;
const KEEP_IP = (ip) => /^(0\.|127\.|169\.254\.|255\.)/.test(ip) || ip === '168.63.129.16';
const GENERIC_NAMES = new Set(['kubernetes', 'default', 'systempool', 'agentpool', 'networkwatcherrg', 'microsoft.compute',
  'containers']); // `containers` would match inside Microsoft.ContainerService
// Azure region names (westeurope, northcentralus, ...) are public and stay readable
const isRegion = (n) => /^[a-z]+(?:central|east|west|north|south)[a-z0-9]*$/.test(n);
// claim keys whose values identify a person or token; values are replaced wholesale
const PERSON_KEY = /(^|\/)(name|givenname|surname|given_name|family_name|nameidentifier|upn|puid|preferred_username|unique_name|oid|sub|ipaddr|altsecid|uti|aio|emailaddress)$/i;

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

// ---------- calculations ----------
const hmac = (salt, s) => createHmac('sha256', salt).update(s).digest('hex');
const lc = (s) => s.toLowerCase();

const alias = {
  guid: (salt, g) => { const h = hmac(salt, `guid:${lc(g)}`); return `${h.slice(0, 8)}-${h.slice(8, 12)}-4${h.slice(13, 16)}-a${h.slice(17, 20)}-${h.slice(20, 32)}`; },
  idBare: (salt, g) => hmac(salt, `idbare:${lc(g)}`).slice(0, 32),
  macBare: (salt, m) => hmac(salt, `macbare:${lc(m)}`).slice(0, 12).toUpperCase(),
  mac: (salt, m) => { const h = hmac(salt, `mac:${lc(m)}`); return `02:${h.slice(0, 2)}:${h.slice(2, 4)}:${h.slice(4, 6)}:${h.slice(6, 8)}:${h.slice(8, 10)}`; },
  ipv4: (salt, ip) => {
    const h = hmac(salt, `ip:${ip}`); const b = (i) => parseInt(h.slice(i * 2, i * 2 + 2), 16);
    const priv = /^(10\.|172\.(1[6-9]|2\d|3[01])\.|192\.168\.)/.test(ip);
    return priv ? `10.${b(0)}.${b(1)}.${b(2)}` : `198.${18 + (b(0) & 1)}.${b(1)}.${b(2)}`;
  },
  ipv6: (salt, ip) => { const h = hmac(salt, `ip6:${lc(ip)}`); return `fd00:${h.slice(0, 4)}:${h.slice(4, 8)}::${h.slice(8, 12)}`; },
  email: (salt, e) => `user-${hmac(salt, `email:${lc(e)}`).slice(0, 8)}@example.com`,
  name: (salt, n) => {
    const prefix = n.match(/^(mc_|[a-z]{2,5}-)/i)?.[0].toLowerCase() ?? '';
    return `${prefix}${hmac(salt, `name:${lc(n)}`).slice(0, 8)}`;
  },
  value: (salt, v) => `redacted-${hmac(salt, `value:${v}`).slice(0, 8)}`,
};

const escapeRe = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

// ARM resource-group and resource names found in a record's text, plus ARG `name` values. Adds to `names`.
export function collectNamesFrom(line, record, names) {
  const addId = (id) => {
    const rg = id.match(/\/resourcegroups\/([^/"\\]+)/i)?.[1];
    if (rg) names.add(rg);
    for (const m of id.matchAll(/\/providers\/[^/"\\]+\/(?:[^/"\\]+\/[^/"\\]+\/)*?[^/"\\]+\/([^/"\\]+)/gi)) names.add(m[1]);
  };
  if (line.includes('/subscriptions/') || line.includes('/Subscriptions/')) for (const m of line.matchAll(/\/subscriptions\/[^"\\\s]+/gi)) addId(m[0]);
  if (record?.source === 'arg') { if (record.payload.name) names.add(record.payload.name); if (record.payload.resourceGroup) names.add(record.payload.resourceGroup); }
}

// dash-less variants too (Cilium reports the cluster as a dash-less name), and the `:2D` form Activity Log
// operation ids use for a dash; generic, region and short names are not scrubbed
export const usableNames = (names) => {
  const all = [...names].flatMap((n) => [n, n.replace(/[-_]/g, ''), n.replace(/-/g, ':2D')]);
  return [...new Set(all)].filter((n) => n.length >= 5 && !GENERIC_NAMES.has(lc(n)) && !isRegion(lc(n)));
};

export function makeScrubber({ salt, names, literals = [] }) {
  const tokens = [...new Set([...names.map((n) => ['name', n]), ...literals.map((l) => ['value', l])])]
    .filter(([, t]) => t.length >= 4)
    .sort((a, b) => b[1].length - a[1].length); // longest first so containing names win
  const tokenRe = tokens.length ? new RegExp(tokens.map(([, t]) => escapeRe(t)).join('|'), 'gi') : null;
  const kindOf = new Map(tokens.map(([k, t]) => [lc(t), k]));

  const scrubText = (text) => {
    let s = text;
    if (tokenRe) s = s.replace(tokenRe, (t) => alias[kindOf.get(lc(t))](salt, t));
    s = s.replace(EMAIL, (e) => alias.email(salt, e));
    s = s.replace(GUID, (g) => alias.guid(salt, g));
    s = s.replace(ID_BARE, (_, pre, g, post) => `${pre}${alias.idBare(salt, g)}${post}`);
    s = s.replace(MAC_BARE, (_, pre, m) => `${pre}${alias.macBare(salt, m)}`);
    s = s.replace(MAC, (m) => alias.mac(salt, m));
    s = s.replace(IPV6, (ip) => alias.ipv6(salt, ip));
    s = s.replace(IPV4, (ip) => (KEEP_IP(ip) ? ip : alias.ipv4(salt, ip)));
    s = s.replace(AKS_HOST, (_, rest) => `${alias.name(salt, rest)}.${rest}`);
    return s;
  };

  // Structural pass for person-identifying claim values, then the text pass for everything else.
  const walk = (v, key = '', inClaims = false) => {
    if (Array.isArray(v)) return v.map((x) => walk(x, key, inClaims));
    if (v && typeof v === 'object') return Object.fromEntries(Object.entries(v).filter(([k]) => k !== 'managedFields').map(([k, x]) => [k, walk(x, k, inClaims || k === 'claims')]));
    if (inClaims && typeof v === 'string' && PERSON_KEY.test(key)) return alias.value(salt, v);
    return v;
  };
  return (record) => JSON.parse(scrubText(JSON.stringify(walk(record))));
}

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
const literalsFromEnv = () => (process.env.SCRUB_LITERALS ?? '').split('\n').map((s) => s.trim()).filter(Boolean);

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

async function scanNames(captureDir) {
  const names = new Set();
  for (const { file, rel } of captureFiles(captureDir)) {
    // ARM ids only appear in a few sources; one parse per line is too slow for the Hubble file
    const parse = !rel.startsWith('hubble/');
    for await (const l of lines(file)) collectNamesFrom(l, parse && l.includes('"source":"arg"') ? JSON.parse(l) : null, names);
  }
  return usableNames(names);
}

async function build(captureDir, fixturesDir) {
  const names = await scanNames(captureDir);
  const scrub = makeScrubber({ salt: saltFor(captureDir), names, literals: literalsFromEnv() });
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
  const deny = new Set(literalsFromEnv().filter((t) => t.length >= 4).map(lc));
  for (const n of await scanNames(captureDir)) deny.add(lc(n));
  for (const { file } of captureFiles(captureDir)) {
    for await (const l of lines(file)) {
      for (const re of [GUID, MAC, IPV6, EMAIL]) for (const m of l.matchAll(re)) deny.add(lc(m[0]));
      for (const m of l.matchAll(ID_BARE)) deny.add(lc(m[2]));
      for (const m of l.matchAll(MAC_BARE)) deny.add(lc(m[2]));
      for (const m of l.matchAll(IPV4)) if (!KEEP_IP(m[0])) deny.add(m[0]);
    }
  }
  const fixtureFiles = walkFiles(fixturesDir);
  const text = fixtureFiles.map((f) => readFileSync(f, 'utf8')).join('\n').toLowerCase();
  const leaks = [...deny].filter((t) => text.includes(t));
  const strayEmails = [...text.matchAll(EMAIL)].map((m) => m[0]).filter((e) => !e.endsWith('@example.com'));
  console.log(`checked ${fixtureFiles.length} fixture files against ${deny.size} real identifiers`);
  if (leaks.length || strayEmails.length) {
    // report counts only: never print the real values
    console.error(`FAIL: ${leaks.length} real identifiers and ${strayEmails.length} unscrubbed emails found in fixtures`);
    process.exit(1);
  }
  console.log('OK: no real identifier found in fixtures');
}

if (process.argv[1] === new URL(import.meta.url).pathname) {
  const [cmd, captureDir, fixturesDir] = process.argv.slice(2);
  const actions = { build, verify };
  if (!actions[cmd]) { console.error('usage: scrub.mjs build|verify <captureDir> <fixturesDir>'); process.exit(2); }
  await actions[cmd](captureDir, fixturesDir);
}
