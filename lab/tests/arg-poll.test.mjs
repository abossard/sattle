// Tests for the forwarder's Resource Graph poller (inventory once, then its changes) and for the envelope of an event
// read from a hub Azure writes to, with a stand-in for fetch.
// Run: node --test lab/tests/*.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { argPoller, planChanges } from '../capture/lib/arg-poll.mjs';
import { hubEnvelope } from '../capture/lib/envelope.mjs';

const A = '/subscriptions/s/resourceGroups/g/providers/Microsoft.Storage/storageAccounts/a';
const B = '/subscriptions/s/resourceGroups/g/providers/Microsoft.Storage/storageAccounts/b';
const change = (id, changeType, t) => ({ id: `${id}/providers/Microsoft.Resources/changes/${t}`, t, properties: { changeType, targetResourceId: id, changeAttributes: { timestamp: t } } });
const T0 = Date.parse('2026-11-17T10:00:00Z');

for (const [name, records, deletes, ids, latest] of [
  ['no change', [], [], [], T0],
  ['a create and an update of one resource read it once', [change(A, 'Create', '2026-11-17T10:01:00Z'), change(A.toUpperCase(), 'Update', '2026-11-17T10:02:00.5Z')], [], [A], Date.parse('2026-11-17T10:02:00.5Z')],
  ['a delete is sent as it is', [change(B, 'Delete', '2026-11-17T10:03:00Z')], [B], [], Date.parse('2026-11-17T10:03:00Z')],
  ['a change older than the latest seen keeps the latest', [change(A, 'Update', '2026-11-17T09:50:00Z')], [], [A], T0],
]) {
  test(`plan of change records: ${name}`, () => {
    const plan = planChanges(records, T0);
    assert.deepEqual(plan.deletes.map((r) => r.properties.targetResourceId), deletes);
    assert.deepEqual(plan.ids, ids);
    assert.equal(plan.latest, latest);
  });
}

// a fake Resource Graph: answers by the table the query starts with, pages of `page` rows, counted calls
function fakeArg({ tables, page = 2, status = [] }) {
  const calls = [];
  const fetch = async (url, init) => {
    const body = JSON.parse(init.body);
    calls.push({ auth: init.headers.Authorization, query: body.query, skip: body.options.$skipToken });
    const s = status.shift();
    if (s) return { ok: false, status: s, headers: { get: () => '0' }, json: async () => ({}) };
    const [table] = body.query.split(' ');
    const ids = /in~ \((.*)\)/.exec(body.query)?.[1].split(', ').map((x) => x.slice(1, -1).toLowerCase());
    const all = (tables[table] ?? []).filter((r) => !ids || ids.includes((r.id ?? r.properties.targetResourceId).toLowerCase()));
    const from = Number(body.options.$skipToken ?? 0);
    const rest = from + page < all.length ? { $skipToken: String(from + page) } : {};
    return { ok: true, status: 200, json: async () => ({ data: all.slice(from, from + page), ...rest }) };
  };
  return { fetch, calls };
}
const row = (id, location) => ({ id, name: id.split('/').pop(), type: 'microsoft.storage/storageaccounts', location });
const base = (arg, tokens = ['t1', 't2']) => ({ subscription: 's', fetch: arg.fetch, getToken: async (refresh) => (refresh ? tokens[1] : tokens[0]), now: () => T0, sleep: async () => {} });
const collect = () => { const sent = []; return { sent, send: (e) => sent.push(e) }; };

test('the snapshot sends every row of both tables, page by page, as arg envelopes keyed by query and id', async () => {
  const arg = fakeArg({ tables: { Resources: [row(A, 'x'), row(B, 'x'), row(`${A}2`, 'x')], ResourceContainers: [{ id: '/subscriptions/s/resourceGroups/g', name: 'g', type: 'microsoft.resources/subscriptions/resourcegroups' }] } });
  const { sent, send } = collect();
  await argPoller(base(arg)).snapshot(send);
  assert.deepEqual(sent.map((e) => e.key), [`arg/resources/${A}`, `arg/resources/${B}`, `arg/resources/${A}2`, 'arg/resourcecontainers//subscriptions/s/resourceGroups/g']);
  assert.ok(sent.every((e) => e.source === 'arg'));
  assert.equal(arg.calls.filter((c) => c.query === 'Resources').length, 2);       // two pages
});

test('a poll sends the delete records, then the current rows of the created and changed resources', async () => {
  const arg = fakeArg({ tables: {
    Resources: [row(A, 'new')],
    resourcechanges: [change(A, 'Update', '2026-11-17T10:01:00Z'), change(B, 'Delete', '2026-11-17T10:02:00Z')],
  } });
  const p = argPoller(base(arg));
  await p.snapshot(() => {});
  const { sent, send } = collect();
  await p.poll(send);
  assert.deepEqual(sent.map((e) => [e.meta.query, e.payload.properties?.targetResourceId ?? e.payload.id]), [['resourcechanges', B], ['resources', A]]);
  assert.equal(sent[1].payload.location, 'new');
  const asked = arg.calls.filter((c) => c.query.startsWith('resourcechanges')).map((c) => c.query);
  assert.match(asked[0], /datetime\(2026-11-17T09:30:00.000Z\)/);       // 30 minutes before the start of the snapshot
  await p.poll(() => {});
  assert.match(arg.calls.filter((c) => c.query.startsWith('resourcechanges')).at(-1).query, /datetime\(2026-11-17T09:32:00.000Z\)/);     // and then before the latest change seen
});

for (const [name, status, ok, auths] of [
  ['401 renews the token once', [401], true, ['Bearer t1', 'Bearer t2']],
  ['429 and 503 are asked again', [429, 503], true, ['Bearer t1', 'Bearer t1', 'Bearer t1']],
  ['403 fails the poll', [403], false, ['Bearer t1']],
]) {
  test(`resource graph answer: ${name}`, async () => {
    const arg = fakeArg({ tables: { Resources: [row(A, 'x')] }, status: [...status] });
    const run = argPoller(base(arg)).snapshot(() => {});
    if (ok) await run; else await assert.rejects(run, /HTTP 403/);
    assert.deepEqual(arg.calls.filter((c) => c.query === 'Resources').map((c) => c.auth), auths);
  });
}

for (const [name, body, payload] of [
  ['JSON text', '{"records":[{"time":"t"}]}', { records: [{ time: 't' }] }],
  ['bytes', new TextEncoder().encode('{"records":[]}'), { records: [] }],
  ['an object the SDK decoded', { records: [1] }, { records: [1] }],
  ['text that is not JSON', 'plain', { text: 'plain' }],
]) {
  test(`hub envelope: ${name}`, () => {
    const event = { body, sequenceNumber: 42, offset: '7', enqueuedTimeUtc: new Date('2026-11-17T10:00:00Z'), properties: {} };
    const e = hubEnvelope({ hub: 'sattle-aks', partitionId: '0', event, capturedAt: '2026-11-17T10:00:01Z', seq: 3 });
    assert.equal(e.source, 'eventhub:sattle-aks');
    assert.equal(e.key, 'eventhub:sattle-aks/0/42');       // the same key whenever the hub is read again
    assert.deepEqual(e.payload, payload);
    assert.equal(e.meta.enqueuedTimeUtc, '2026-11-17T10:00:00.000Z');
  });
}
