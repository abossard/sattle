// Tests for the forwarder's Azure metrics poller (REST, no az CLI per poll), with a stand-in for fetch.
// Run: node --test lab/tests/*.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { azureMetricsPoller, cachedToken, nextPollMs } from '../capture/lib/azure-metrics-poll.mjs';

const R1 = '/subscriptions/s/resourceGroups/g/providers/Microsoft.ContainerService/managedClusters/c';
const R2 = '/subscriptions/s/resourceGroups/g/providers/Microsoft.EventHub/namespaces/n';
// a fake ARM: definitions per resource, metric names that cannot be asked for together with others, counted calls
function fakeArm({ defs, loners = [], broken = [] }) {
  const calls = [];
  const fetch = async (url) => {
    calls.push(url);
    const u = new URL(url);
    const rid = decodeURIComponent(u.pathname).replace(/\/providers\/Microsoft\.Insights\/metric(Definitions|s)$/, '');
    if (u.pathname.endsWith('/metricDefinitions')) return { ok: true, status: 200, json: async () => ({ value: defs[rid].map((n) => ({ name: { value: n } })) }) };
    const names = u.searchParams.get('metricnames').split(',');
    if (names.some((n) => broken.includes(n)) || (names.length > 1 && names.some((n) => loners.includes(n)))) return { ok: false, status: 400, json: async () => ({ error: { code: 'BadRequest' } }) };
    return { ok: true, status: 200, json: async () => ({ timespan: u.searchParams.get('timespan'), interval: 'PT1M', value: names.map((n) => ({ name: { value: n }, timeseries: [{ data: [{ timeStamp: '2026-11-16T10:00:00Z', average: 1 }] }] })) }) };
  };
  return { fetch, calls };
}
const base = { getToken: async () => 'token', now: () => Date.parse('2026-11-16T10:05:00Z'), sleep: async () => {} };
const names = (envs) => envs.flatMap((e) => e.payload.value.map((m) => m.name.value)).sort();

test('one poll returns every metric of every resource as azure-metrics envelopes', async () => {
  const arm = fakeArm({ defs: { [R1]: ['a', 'b'], [R2]: ['c'] } });
  const p = azureMetricsPoller({ ...base, fetch: arm.fetch, resources: [{ kind: 'aks', id: R1 }, { kind: 'eventhubs', id: R2 }] });
  const envs = await p.poll(300);
  assert.deepEqual(names(envs), ['a', 'b', 'c']);
  const e = envs.find((x) => x.meta.resource === 'aks');
  assert.equal(e.source, 'azure-metrics');
  assert.equal(e.meta.resourceId, R1);
  assert.equal(e.meta.interval, 'PT1M');
  assert.equal(e.meta.start, '2026-11-16T10:00:00Z');
  assert.equal(e.meta.end, '2026-11-16T10:05:00Z');
  assert.deepEqual(e.meta.metrics, ['a', 'b']);
});
test('at most 20 metrics per request', async () => {
  const many = Array.from({ length: 45 }, (_, i) => `m${i}`);
  const arm = fakeArm({ defs: { [R1]: many } });
  const p = azureMetricsPoller({ ...base, fetch: arm.fetch, resources: [{ kind: 'aks', id: R1 }] });
  const envs = await p.poll(300);
  assert.equal(names(envs).length, 45);
  const sizes = arm.calls.filter((u) => u.includes('/metrics?')).map((u) => new URL(u).searchParams.get('metricnames').split(',').length);
  assert.deepEqual(sizes.sort((a, b) => b - a), [20, 20, 5]);
});
test('a metric that cannot be asked with others is asked alone, and the split is remembered', async () => {
  const arm = fakeArm({ defs: { [R1]: ['a', 'b', 'lone'] }, loners: ['lone'] });
  const p = azureMetricsPoller({ ...base, fetch: arm.fetch, resources: [{ kind: 'aks', id: R1 }] });
  assert.deepEqual(names(await p.poll(300)), ['a', 'b', 'lone']);
  const before = arm.calls.length;
  assert.deepEqual(names(await p.poll(300)), ['a', 'b', 'lone']);
  const second = arm.calls.slice(before);
  assert.ok(second.every((u) => u.includes('/metrics?')), 'definitions were read again');
  assert.equal(second.length, 3, `${second.length} requests in the second poll: a rejected group was asked again`);
});
test('a metric that fails alone is skipped from then on, the rest is delivered', async () => {
  const arm = fakeArm({ defs: { [R1]: ['a', 'bad'] }, broken: ['bad'] });
  const p = azureMetricsPoller({ ...base, fetch: arm.fetch, resources: [{ kind: 'aks', id: R1 }] });
  assert.deepEqual(names(await p.poll(300)), ['a']);
  const before = arm.calls.length;
  assert.deepEqual(names(await p.poll(300)), ['a']);
  assert.equal(arm.calls.length - before, 1);
});
test('requests carry the token and the time span of the window', async () => {
  const seen = [];
  const arm = fakeArm({ defs: { [R1]: ['a'] } });
  const p = azureMetricsPoller({ ...base, fetch: async (url, init) => { seen.push(init?.headers?.Authorization); return arm.fetch(url); }, resources: [{ kind: 'aks', id: R1 }] });
  await p.poll(120);
  assert.ok(seen.every((h) => h === 'Bearer token'));
  assert.equal(new URL(arm.calls.at(-1)).searchParams.get('timespan'), '2026-11-16T10:03:00Z/2026-11-16T10:05:00Z');
});

// ---------- answers other than "this group cannot be asked together" ----------
// a fake ARM that answers the metrics requests from a script: each entry is a status, or a status with headers
function scriptedArm(script, defs = { [R1]: ['a', 'b'] }) {
  const calls = []; let i = 0;
  const fetch = async (url, init) => {
    calls.push({ url, auth: init?.headers?.Authorization });
    const u = new URL(url);
    if (u.pathname.endsWith('/metricDefinitions')) return { ok: true, status: 200, headers: new Headers(), json: async () => ({ value: defs[R1].map((n) => ({ name: { value: n } })) }) };
    const step = script[Math.min(i++, script.length - 1)];
    const [status, headers = {}] = Array.isArray(step) ? step : [step];
    const names = u.searchParams.get('metricnames').split(',');
    return { ok: status < 300, status, headers: new Headers(headers), json: async () => (status < 300 ? { value: names.map((n) => ({ name: { value: n }, timeseries: [] })) } : { error: { code: String(status) } }) };
  };
  return { fetch, calls, metricCalls: () => calls.filter((c) => c.url.includes('/metrics?')) };
}
for (const [what, script, waitsAtLeast] of [
  ['429 waits as long as Retry-After says, then asks the same group again', [[429, { 'retry-after': '7' }], 200], [7000]],
  ['500 backs off and asks the same group again', [500, 503, 200], [1000, 2000]],
])
  test(`metrics poller: ${what}; nothing is split or dropped`, async () => {
    const arm = scriptedArm(script); const waits = [];
    const p = azureMetricsPoller({ ...base, sleep: async (ms) => { waits.push(ms); }, fetch: arm.fetch, resources: [{ kind: 'aks', id: R1 }] });
    assert.deepEqual(names(await p.poll(300)), ['a', 'b']);
    assert.ok(arm.metricCalls().every((c) => new URL(c.url).searchParams.get('metricnames') === 'a,b'), 'the group was split');
    assert.deepEqual(waits.slice(0, waitsAtLeast.length), waitsAtLeast);
    assert.equal(arm.metricCalls().length, script.length);
  });
test('metrics poller: when 5xx persists the poll fails and keeps every metric for the next poll', async () => {
  const arm = scriptedArm([500]);
  const p = azureMetricsPoller({ ...base, fetch: arm.fetch, resources: [{ kind: 'aks', id: R1 }] });
  await assert.rejects(p.poll(300), /HTTP 500/);
  assert.ok(arm.metricCalls().length <= 4, `${arm.metricCalls().length} requests for one group`);
  const ok = scriptedArm([200]);
  const p2 = azureMetricsPoller({ ...base, fetch: async (u, i) => (u.includes('/metrics?') ? ok.fetch(u, i) : arm.fetch(u, i)), resources: [{ kind: 'aks', id: R1 }] });
  assert.deepEqual(names(await p2.poll(300)), ['a', 'b']);
});
test('metrics poller: 401 takes a new token and asks once more', async () => {
  const arm = scriptedArm([401, 200]); const forced = [];
  const p = azureMetricsPoller({ ...base, getToken: async (refresh) => { forced.push(Boolean(refresh)); return refresh ? 'new' : 'old'; }, fetch: arm.fetch, resources: [{ kind: 'aks', id: R1 }] });
  assert.deepEqual(names(await p.poll(300)), ['a', 'b']);
  assert.equal(arm.metricCalls().at(-1).auth, 'Bearer new');
  assert.equal(forced.filter(Boolean).length, 1);
});
test('metrics poller: 401 twice fails the poll without dropping metrics', async () => {
  const arm = scriptedArm([401]);
  const p = azureMetricsPoller({ ...base, fetch: arm.fetch, resources: [{ kind: 'aks', id: R1 }] });
  await assert.rejects(p.poll(300), /HTTP 401/);
  assert.equal(arm.metricCalls().length, 2);
});
test('metrics poller: one token per poll, not one per request', async () => {
  const many = Array.from({ length: 45 }, (_, i) => `m${i}`);
  const arm = fakeArm({ defs: { [R1]: many, [R2]: ['c'] } }); let tokens = 0;
  const p = azureMetricsPoller({ ...base, getToken: async () => { tokens++; return 'token'; }, fetch: arm.fetch, resources: [{ kind: 'aks', id: R1 }, { kind: 'eventhubs', id: R2 }] });
  await p.poll(300); await p.poll(300);
  assert.equal(tokens, 2);
});

for (const [failures, expected] of [[0, 60_000], [1, 120_000], [3, 480_000], [10, 900_000]])
  test(`next poll after ${failures} failed polls in a row: ${expected / 1000} s`, () => {
    assert.equal(nextPollMs(60, failures), expected);
  });
test('the token is asked for once until it nears expiry or a refresh is wanted', async () => {
  let t = 0; let asked = 0;
  const credential = { getToken: async () => ({ token: `t${++asked}`, expiresOnTimestamp: t + 3600_000 }) };
  const get = cachedToken(credential, 'scope', () => t);
  assert.equal(await get(), 't1'); assert.equal(await get(), 't1');
  t = 3600_000 - 60_000; assert.equal(await get(), 't2');     // within 5 minutes of expiry
  assert.equal(await get(true), 't3');                        // after a 401
  assert.equal(asked, 3);
});
