#!/usr/bin/env node
// Convert local Network Watcher PT1H.json blobs into the native Sattle flow-log envelopes.
import { randomUUID } from 'node:crypto';
import { closeSync, mkdirSync, openSync, readFileSync, renameSync, unlinkSync, writeSync } from 'node:fs';
import { isIP } from 'node:net';
import { dirname, join } from 'node:path';
import { parseArgs } from 'node:util';
import { envelope } from '../capture/lib/envelope.mjs';
import { aliases, makeScrubber } from '../fixtures/scrub/core.mjs';

const { values, positionals } = parseArgs({
  allowPositionals: true,
  options: { vnet: { type: 'string' }, output: { type: 'string' }, 'scrub-key-file': { type: 'string' }, help: { type: 'boolean' } },
});
const usage = 'node collect-flow-blobs.mjs --vnet VNET_ARM_ID --output network.ndjson [--scrub-key-file LOCAL_SALT] PT1H.json ...';
if (values.help) { console.log(usage); process.exit(0); }
if (!/^\/subscriptions\/[^/]+\/resourcegroups\/[^/]+\/providers\/microsoft\.network\/virtualnetworks\/[^/]+$/i.test(values.vnet ?? '') ||
    !values.output || !positionals.length) throw new Error(usage);

const secret = values['scrub-key-file'] && readFileSync(values['scrub-key-file'], 'utf8').trim();
if (values['scrub-key-file'] && !secret) throw new Error('The scrub key file is empty');
const alias = secret && aliases(secret);
const scrub = secret && makeScrubber({ salt: secret });
const scope = values.vnet.toLowerCase();

function bounded(value, limit) { return /^\d+$/.test(value) && BigInt(value) <= limit; }

function normalizeTuple(row) {
  const parts = typeof row === 'string' ? row.split(',') : [];
  if (parts.length !== 13 || !bounded(parts[0], 9223372036854n) || !isIP(parts[1]) || !isIP(parts[2]) ||
      !bounded(parts[3], 65535n) || !bounded(parts[4], 65535n) || !bounded(parts[5], 255n) ||
      !['I', 'O'].includes(parts[6]) || !['B', 'C', 'E', 'D'].includes(parts[7]) ||
      !/^[A-Z0-9_]*$/.test(parts[8]) || parts.slice(9).some(s => s && !bounded(s, 9223372036854775807n))) {
    throw new Error('Expected a native Network Watcher flow tuple with 13 fields');
  }
  return row;
}

function flowRecord(record) {
  if (record.targetResourceID?.toLowerCase() !== scope) throw new Error('The blob contains a record outside --vnet');
  if (!record.time || !/(Z|[+-]\d\d:\d\d)$/.test(record.time) || !Number.isFinite(Date.parse(record.time)) ||
      record.category !== 'FlowLogFlowEvent' || !Array.isArray(record.flowRecords?.flows)) {
    throw new Error('Expected a Network Watcher FlowLogFlowEvent with time and flows');
  }
  const flows = record.flowRecords.flows.map(flow => ({
    aclID: flow.aclID,
    flowGroups: flow.flowGroups.map(group => ({
      rule: alias && group.rule ? alias.name(group.rule) : group.rule,
      flowTuples: group.flowTuples.map(normalizeTuple),
    })),
  }));
  const payload = { time: record.time, category: record.category, flowLogVersion: record.flowLogVersion,
    macAddress: alias && record.macAddress ? alias.macBare(record.macAddress) : record.macAddress,
    targetResourceID: record.targetResourceID, flowRecords: { flows } };
  return scrub ? scrub({ source: 'vnet-flow-log', payload }).payload : payload;
}

mkdirSync(dirname(values.output), { recursive: true });
const temp = join(dirname(values.output), '.' + randomUUID() + '.ndjson');
const fd = openSync(temp, 'wx', 0o600);
let count = 0;
try {
  const seen = new Set();
  for (const file of positionals) {
    const { records } = JSON.parse(readFileSync(file, 'utf8'));
    if (!Array.isArray(records)) throw new Error('Expected a Network Watcher blob with a records array');
    for (const raw of records) {
      const payload = flowRecord(raw);
      const event = envelope({ source: 'vnet-flow-log', capturedAt: new Date(raw.time).toISOString(), seq: count + 1, payload });
      if (seen.has(event.key)) continue;
      seen.add(event.key);
      writeSync(fd, JSON.stringify(event) + '\n');
      count++;
    }
  }
  if (!count) throw new Error('No VNet flow records in the supplied blobs');
  closeSync(fd);
  renameSync(temp, values.output);
} catch (error) {
  try { closeSync(fd); } catch { /* The file can already be closed. */ }
  unlinkSync(temp);
  throw error;
}
console.log(`${count} VNet flow records`);
