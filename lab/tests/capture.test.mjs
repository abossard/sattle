// Tests for the Node core of the capture tooling that can run without Azure: the forwarder's sink and the consumer's
// stop rule. Run: node --test lab/tests/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { sink } from '../capture/lib/sink.mjs';
import { drainState } from '../capture/lib/drain.mjs';

// ---------- a stand-in for the Event Hubs producer: batches of at most `capacity` events, sends can fail ----------
function fakeClient({ capacity = 100, failOnSend = [] } = {}) {
  const sent = []; let sends = 0;
  return {
    sent,
    createBatch: async () => { const items = []; return { items, get count() { return items.length; }, tryAdd: (e) => (items.length < capacity ? (items.push(e), true) : false) }; },
    sendBatch: async (b) => { if (failOnSend.includes(sends++)) throw new Error('hub unreachable'); sent.push(...b.items); },
  };
}
const ev = (n) => ({ body: JSON.stringify({ n, pad: 'x'.repeat(60) }), at: new Date(1_000_000 + n * 1000).toISOString(), seq: n });
const ns = (events) => events.filter((e) => !JSON.parse(e.body).event).map((e) => JSON.parse(e.body).n);
const markerBody = ({ count, from, to, seq }) => JSON.stringify({ event: 'dropped', reason: 'queue-full', count, from, to, seq });

// ---------- finding: dropped events are the oldest, the marker must stand where they were, with their time span ----------
test('a full queue drops the oldest events and puts one marker at the head, with the span of what was lost', async () => {
  const client = fakeClient();
  const q = sink(client, '0', { maxBytes: 450, marker: markerBody });
  for (let i = 0; i < 10; i++) q.add(ev(i));
  await q.flush();
  const first = JSON.parse(client.sent[0].body);
  assert.equal(first.event, 'dropped', 'the first thing sent is the marker');
  const kept = ns(client.sent);
  assert.equal(first.count + kept.length, 10, 'every event is either kept or counted');
  assert.deepEqual(kept, [...kept].sort((a, b) => a - b), 'order is kept');
  assert.equal(kept[0], first.count, 'the marker stands exactly before the first surviving event');
  assert.equal(first.from, ev(0).at, 'the loss starts at the oldest dropped event');
  assert.equal(first.to, ev(first.count - 1).at, 'the loss ends at the newest dropped event, not at the time of the drop');
});
test('drops between two flushes make one marker that covers all of them', async () => {
  const client = fakeClient();
  const q = sink(client, '0', { maxBytes: 450, marker: markerBody });
  for (let i = 0; i < 6; i++) q.add(ev(i));
  for (let i = 6; i < 12; i++) q.add(ev(i));
  await q.flush();
  const markers = client.sent.filter((e) => JSON.parse(e.body).event === 'dropped');
  assert.equal(markers.length, 1);
  const m = JSON.parse(markers[0].body);
  assert.equal(m.count + ns(client.sent).length, 12);
  assert.equal(m.from, ev(0).at);
});
test('the memory bound holds, the marker included', () => {
  const q = sink(fakeClient(), '0', { maxBytes: 450, marker: markerBody });
  for (let i = 0; i < 500; i++) q.add(ev(i));
  assert.ok(q.stats().queued <= 8, `queued ${q.stats().queued}`);
  assert.equal(q.stats().dropped + q.stats().queued - 1, 500, 'dropped plus kept (one entry is the marker) is everything');
});

// ---------- finding: a partly failed send re-queued batches the hub had already accepted ----------
test('after a failed send only the events the hub did not accept are sent again', async () => {
  const client = fakeClient({ capacity: 2, failOnSend: [1] });
  const q = sink(client, '0', { maxBytes: 1e6, marker: markerBody });
  for (let i = 0; i < 6; i++) q.add(ev(i));
  await assert.rejects(q.flush(), /unreachable/);
  await q.flush();
  assert.deepEqual(ns(client.sent), [0, 1, 2, 3, 4, 5], 'each event exactly once, in order');
  assert.equal(q.stats().sent, 6);
});

// ---------- finding: the consumer next to a forwarder never saw 10 s of quiet ----------
const base = { now: 1_000_000, windowEnd: 990_000, quietMs: 10_000, capMs: 120_000 };
for (const [name, state, expected] of [
  ['the end of the hub as it was when the window closed is reached, events still flowing', { ...base, targets: new Map([['0', 100]]), seen: new Map([['0', 100]]), lastEventAt: 999_999 }, { done: true, reason: 'reached' }],
  ['reached on every partition', { ...base, targets: new Map([['0', 5], ['1', 7]]), seen: new Map([['0', 9], ['1', 7]]), lastEventAt: 999_999 }, { done: true, reason: 'reached' }],
  ['one partition is still behind', { ...base, targets: new Map([['0', 5], ['1', 7]]), seen: new Map([['0', 9], ['1', 6]]), lastEventAt: 999_999 }, { done: false, reason: null }],
  ['an empty partition needs nothing', { ...base, targets: new Map([['0', -1]]), seen: new Map(), lastEventAt: 999_999 }, { done: true, reason: 'reached' }],
  ['behind but the hub went quiet', { ...base, targets: new Map([['0', 100]]), seen: new Map([['0', 50]]), lastEventAt: 985_000 }, { done: true, reason: 'quiet' }],
  ['no targets known, quiet', { ...base, targets: new Map(), seen: new Map(), lastEventAt: 980_000 }, { done: true, reason: 'quiet' }],
  ['no targets known, still flowing', { ...base, targets: new Map(), seen: new Map(), lastEventAt: 999_000 }, { done: false, reason: null }],
  ['behind, still flowing, past the cap', { ...base, now: 1_200_000, targets: new Map([['0', 100]]), seen: new Map([['0', 50]]), lastEventAt: 1_199_999 }, { done: true, reason: 'cap' }],
])
  test(`drain: ${name}`, () => {
    assert.deepEqual(drainState(state), expected);
  });
