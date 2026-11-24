import assert from 'node:assert/strict';
import { mkdtemp, readFile, readdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { openReceiver } from '../health-mirror/listen-diagnostics.mjs';

const scope = '/subscriptions/00000000-0000-4000-8000-000000000001/resourceGroups/demo';
const time = '2026-11-21T21:15:00.1234567Z';
const enqueued = new Date('2026-11-21T21:15:36Z');
const cpu = {
  resourceId: scope.toUpperCase() + '/PROVIDERS/MICROSOFT.APP/CONTAINERAPPS/WEB',
  metricName: 'CpuPercentage', time, timeGrain: 'PT1M', average: 18.1, minimum: 0,
  maximum: 18.3, total: null, count: 2, unit: 'Percent',
};
const event = (sequenceNumber, body) => ({ sequenceNumber, body, enqueuedTimeUtc: enqueued });

async function fixture(t) {
  const fixture = await mkdtemp(join(tmpdir(), 'sattle-diagnostics-'));
  t.after(() => rm(fixture, { recursive: true, force: true }));
  return { namespace: 'test-metrics.servicebus.windows.net', hub: 'native', scope, fixture };
}

test('preserves explicit no-data windows from the diagnostic stream', async t => {
  const config = await fixture(t);
  const paths = [];
  const receiver = await openReceiver(config, path => paths.push(path));
  const missing = { resourceId: cpu.resourceId, metricName: cpu.metricName, time, timeGrain: 'PT1M', average: null };
  await receiver.receive('0', event(0, { records: [missing] }));
  assert.deepEqual(JSON.parse(await readFile(paths[0], 'utf8')).records, [missing]);
});

test('keeps only scoped native metric aggregates before persistence', async t => {
  const config = await fixture(t);
  const paths = [];
  const receiver = await openReceiver(config, p => paths.push(p));
  const foreign = { ...cpu, resourceId: cpu.resourceId.replace('/DEMO/', '/DEMO-OTHER/') };
  const log = { ...cpu, category: 'ContainerAppHTTPLogs', properties: { url: 'private' } };
  const child = { ...cpu, resourceId: cpu.resourceId + '/revisions/internal' };
  const body = [{ records: [foreign, log, child, { ...cpu, average: undefined, maximum: undefined, minimum: undefined, total: undefined, count: undefined }] },
    { records: [{ ...cpu, dimensions: { private: 1 }, properties: { token: 'private' } }] }];
  await receiver.receive('1', event(12, Buffer.from(JSON.stringify(body))));
  const [path] = paths;
  assert.equal(path, join(config.fixture, 'diagnostic-events/test-metrics/native/1/12.json'));
  assert.deepEqual(JSON.parse(await readFile(path)), {
    namespace: 'test-metrics', hub: 'native', partition: '1', sequenceNumber: 12,
    enqueuedAt: enqueued.toISOString(), records: [cpu],
  });
  await receiver.receive('1', event(13, { records: [foreign] }));
  await receiver.receive('1', event(14, { source: 'records:PlatformMetricRow', payload: cpu }));
  await receiver.receive('1', event(15, 'malformed JSON'));
  assert.deepEqual(paths, [path]);
  assert.deepEqual(await readdir(join(path, '..')), ['12.json']);
  assert.deepEqual(await receiver.startPosition('1'), { sequenceNumber: 15, isInclusive: false });
});

test('restarts each partition at its own checkpoint and replays an interrupted notification', async t => {
  const config = await fixture(t);
  const input = event(4, { records: [cpu] });
  const interrupted = await openReceiver(config, () => { throw new Error('downstream gone'); });
  await assert.rejects(interrupted.receive('0', input), /downstream gone/);
  const paths = [];
  const reader = await openReceiver(config, p => paths.push(p));
  const earliest = await reader.startPosition('0');
  assert.ok(!('sequenceNumber' in earliest));
  await reader.receive('0', input);
  await reader.receive('0', input);
  await reader.receive('1', event(3, JSON.stringify({ records: [cpu] })));
  const restarted = await openReceiver(config, p => paths.push(p));
  assert.deepEqual(await restarted.startPosition('0'), { sequenceNumber: 4, isInclusive: false });
  assert.deepEqual(await restarted.startPosition('1'), { sequenceNumber: 3, isInclusive: false });
  assert.deepEqual(await restarted.startPosition('2'), earliest);
  await restarted.receive('0', input);
  assert.equal(paths.length, 2);
  await assert.rejects(openReceiver({ ...config, scope: scope + 'other' }, () => {}), /Conflicting receipt/);
});

test('rejects checkpoint path traversal and conflicting replay content', async t => {
  const config = await fixture(t);
  const interrupted = await openReceiver(config, () => { throw new Error('stopped'); });
  await assert.rejects(interrupted.receive('0', event(42, { records: [cpu] })), /stopped/);
  const receiver = await openReceiver(config, () => {});
  await assert.rejects(receiver.receive('0', event(42, { records: [{ ...cpu, average: 5 }] })), /Conflicting receipt/);
  await assert.rejects(receiver.receive('../escape', event(12, {})), /Invalid Event Hubs partition/);
  await assert.rejects(openReceiver({ ...config, hub: '../hub' }, () => {}), /Invalid namespace or hub/);
});
