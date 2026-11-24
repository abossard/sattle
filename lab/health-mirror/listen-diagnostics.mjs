#!/usr/bin/env node
// Subscribe to native Azure Monitor metric records using the signed-in Azure CLI identity.
import { link, mkdir, open, readFile, rename, unlink } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { dirname, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { parseArgs } from 'node:util';
import { consumer, earliestEventPosition } from '../capture/lib/eventhub.mjs';

const groupId = /^\/subscriptions\/[\da-f]{8}(?:-[\da-f]{4}){3}-[\da-f]{12}\/resourcegroups\/[\w.()-]+$/i;
const timestamp = /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)$/i;
const aggregations = ['count', 'total', 'minimum', 'maximum', 'average'];

function metricRecords(body, scope) {
  if (Buffer.isBuffer(body) || body instanceof Uint8Array) body = Buffer.from(body).toString('utf8');
  if (typeof body === 'string') {
    try { body = JSON.parse(body); } catch { return []; }
  }
  const prefix = scope + '/providers/microsoft.app/containerapps/';
  const records = [];
  for (const envelope of Array.isArray(body) ? body : [body]) {
    if (!Array.isArray(envelope?.records)) continue;
    for (const raw of envelope.records) {
      const id = typeof raw?.resourceId === 'string' ? raw.resourceId.toLowerCase() : '';
      if (!id.startsWith(prefix) || !/^[a-z0-9-]+$/.test(id.slice(prefix.length))) continue;
      if (raw.category && raw.category.toLowerCase?.() !== 'allmetrics') continue;
      if (!/^[A-Za-z][A-Za-z0-9._-]{0,127}$/.test(raw.metricName ?? '')) continue;
      if (!timestamp.test(raw.time) || !Number.isFinite(Date.parse(raw.time))) continue;
      if (!/^P(?=\d|T\d)(?:\d+D)?(?:T(?:\d+H)?(?:\d+M)?(?:\d+(?:\.\d+)?S)?)?$/.test(raw.timeGrain ?? '')) continue;
      if (!aggregations.some(key => Object.hasOwn(raw, key) && (Number.isFinite(raw[key]) || raw[key] === null))) continue;
      const record = { resourceId: raw.resourceId, time: raw.time, timeGrain: raw.timeGrain, metricName: raw.metricName };
      for (const key of aggregations) {
        if (Number.isFinite(raw[key]) || raw[key] === null) record[key] = raw[key];
      }
      if (typeof raw.unit === 'string' && /^[\w%./ -]{1,64}$/.test(raw.unit)) record.unit = raw.unit;
      records.push(record);
    }
  }
  return records;
}

async function syncDir(dir) {
  const handle = await open(dir, 'r');
  try { await handle.sync(); } finally { await handle.close(); }
}

async function prepareDirectory(path) {
  const first = await mkdir(path, { recursive: true, mode: 0o700 });
  if (first) {
    for (let dir = path; dir !== dirname(first); dir = dirname(dir)) await syncDir(dirname(dir));
  }
}

// Immutable receipts survive replay after a crash. Checkpoints advance only after receipt commit and notification.
async function persist(path, value, immutable) {
  const folder = dirname(path);
  await prepareDirectory(folder);
  const temp = join(folder, '.' + randomUUID() + '.tmp');
  const handle = await open(temp, 'wx', 0o600);
  const text = JSON.stringify(value) + '\n';
  try {
    try { await handle.writeFile(text); await handle.sync(); } finally { await handle.close(); }
    if (immutable) {
      try { await link(temp, path); } catch (error) {
        if (error.code !== 'EEXIST') throw error;
        if (await readFile(path, 'utf8') !== text) throw new Error('Conflicting receipt at ' + path);
      }
    } else {
      await rename(temp, path);
    }
    await syncDir(folder);
  } finally {
    await unlink(temp).catch(error => { if (error.code !== 'ENOENT') throw error; });
  }
}

export async function openReceiver({ namespace, hub, scope, fixture }, notify) {
  if (!namespace || !hub || !scope || !fixture) throw new Error('Expected --namespace NAME --hub HUB --scope RESOURCE_GROUP_ID --fixture DIR');
  namespace = namespace.toLowerCase().replace(/\.servicebus\.windows\.net$/, '');
  hub = hub.toLowerCase();
  scope = scope.toLowerCase().replace(/\/$/, '');
  if (!/^[a-z0-9][a-z0-9-]{0,49}$/.test(namespace) || !/^[a-z0-9][a-z0-9._-]{0,255}$/.test(hub)) throw new Error('Invalid namespace or hub');
  if (!groupId.test(scope)) throw new Error('Expected a resource group ARM ID');
  const receiptRoot = resolve(fixture, 'diagnostic-events', namespace, hub);
  const checkpointRoot = resolve(fixture, '.diagnostic-checkpoints', namespace, hub);
  await persist(join(checkpointRoot, 'scope.json'), { scope }, true);
  const positions = new Map();
  async function lastSequence(partition) {
    if (!/^\d+$/.test(partition)) throw new Error('Invalid Event Hubs partition');
    if (!positions.has(partition)) {
      const path = join(checkpointRoot, partition + '.json');
      let last = -1;
      try {
        last = JSON.parse(await readFile(path, 'utf8')).sequenceNumber;
        if (!Number.isSafeInteger(last) || last < 0) throw new Error('Invalid checkpoint at ' + path);
      } catch (error) { if (error.code !== 'ENOENT') throw error; }
      positions.set(partition, last);
    }
    return positions.get(partition);
  }

  return {
    async startPosition(partition) {
      const sequenceNumber = await lastSequence(partition);
      return sequenceNumber < 0 ? earliestEventPosition : { sequenceNumber, isInclusive: false };
    },
    async receive(partition, event) {
      const sequenceNumber = event.sequenceNumber;
      if (!Number.isSafeInteger(sequenceNumber) || sequenceNumber < 0) throw new Error('Invalid Event Hubs sequence number');
      if (sequenceNumber <= await lastSequence(partition)) return;
      const records = metricRecords(event.body, scope);
      if (records.length) {
        const path = join(receiptRoot, partition, sequenceNumber + '.json');
        const enqueuedAt = event.enqueuedTimeUtc.toISOString();
        await persist(path, { namespace, hub, partition, sequenceNumber, enqueuedAt, records }, true);
        await notify(path);
      }
      await persist(join(checkpointRoot, partition + '.json'), { sequenceNumber }, false);
      positions.set(partition, sequenceNumber);
    },
  };
}

async function main() {
  const options = { namespace: { type: 'string' }, hub: { type: 'string' }, scope: { type: 'string' }, fixture: { type: 'string' } };
  const { values } = parseArgs({ options });
  const receiver = await openReceiver(values, path => new Promise((resolveWrite, rejectWrite) => {
    process.stdout.write(path + '\n', error => error ? rejectWrite(error) : resolveWrite());
  }));
  const client = consumer(values.namespace, values.hub, '$Default');
  let subscription, stop;
  const done = new Promise(resolveDone => { stop = resolveDone; });
  const onSignal = () => stop();
  process.once('SIGTERM', onSignal);
  process.once('SIGINT', onSignal);
  try {
    const ids = await client.getPartitionIds();
    const startPosition = Object.fromEntries(await Promise.all(ids.map(async id => [id, await receiver.startPosition(id)])));
    subscription = client.subscribe({
      async processEvents(events, context) {
        try {
          for (const event of events) await receiver.receive(context.partitionId, event);
          if (events.length) await context.updateCheckpoint(events.at(-1));
        } catch (error) { stop(error); throw error; }
      },
      async processError(error) {
        if (error.retryable) console.error('Event Hubs retry:', error.code);
        else stop(error);
      },
    }, { startPosition, maxBatchSize: 100, maxWaitTimeInSeconds: 2 });
    console.error('Listening to ' + values.namespace + '/' + values.hub + ' partitions ' + ids.join(', '));
    const error = await done;
    if (error) throw error;
  } finally {
    process.off('SIGTERM', onSignal);
    process.off('SIGINT', onSignal);
    try { await subscription?.close(); } finally { await client.close(); }
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main().catch(error => { console.error(error.message); process.exitCode = 1; });
}
