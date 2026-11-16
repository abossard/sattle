// Tests for the Azure metrics poll of the forwarder: overlapping polls must deliver each datapoint once, and only
// datapoints that have a value, each in an envelope of its own keyed by resource, metric, series and time.
// Run: node --test lab/tests/*.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { newSamples } from '../capture/lib/metrics-poll.mjs';
import { envelope } from '../capture/lib/envelope.mjs';

const data = (points) => points.map(([t, v]) => (v === null ? { timeStamp: t } : { timeStamp: t, average: v }));
const response = (id, series) => ({
  source: 'azure-metrics', meta: { resourceId: id },
  payload: { value: Object.entries(series).map(([metric, points]) => ({ name: { value: metric }, timeseries: [{ data: data(points) }] })) },
});
// one metric split into series by a dimension (for example one series per Event Hub)
const bySeries = (id, metric, series) => ({
  source: 'azure-metrics', meta: { resourceId: id },
  payload: { value: [{ name: { value: metric }, timeseries: Object.entries(series).map(([dim, points]) => ({ metadatavalues: [{ name: { value: 'EntityName' }, value: dim }], data: data(points) })) }] },
});
const seriesPoints = (envs) => envs.flatMap((e) => e.payload.value.flatMap((m) => m.timeseries.flatMap((s) => s.data.map((d) => `${s.metadatavalues?.[0]?.value ?? ''}@${d.timeStamp}`))));
const points = (envs) => envs.flatMap((e) => e.payload.value.flatMap((m) => m.timeseries.flatMap((s) => s.data.map((d) => `${m.name.value}@${d.timeStamp}`))));
const t = (m) => `2026-11-16T10:${String(m).padStart(2, '0')}:00Z`;

for (const [name, polls, expected] of [
  ['two overlapping polls give each minute once', [
    response('/r1', { cpu: [[t(0), 1], [t(1), 2], [t(2), 3]] }),
    response('/r1', { cpu: [[t(1), 2], [t(2), 3], [t(3), 4]] }),
  ], [['cpu@' + t(0), 'cpu@' + t(1), 'cpu@' + t(2)], ['cpu@' + t(3)]]],
  ['a minute without a value yet comes with the poll that has it', [
    response('/r1', { cpu: [[t(0), 1], [t(1), null]] }),
    response('/r1', { cpu: [[t(0), 1], [t(1), 5]] }),
  ], [['cpu@' + t(0)], ['cpu@' + t(1)]]],
  ['metrics and resources are tracked apart', [
    response('/r1', { cpu: [[t(0), 1]], mem: [[t(0), 7]] }),
    response('/r2', { cpu: [[t(0), 1]] }),
    response('/r1', { cpu: [[t(0), 1], [t(1), 2]], mem: [[t(0), 7]] }),
  ], [['cpu@' + t(0), 'mem@' + t(0)], ['cpu@' + t(0)], ['cpu@' + t(1)]]],
  ['a poll with nothing new sends nothing', [
    response('/r1', { cpu: [[t(0), 1]] }),
    response('/r1', { cpu: [[t(0), 1]] }),
  ], [['cpu@' + t(0)], []]],
])
  test(`metrics poll: ${name}`, () => {
    let state = new Map();
    const got = polls.map((p) => { const r = newSamples(state, p); state = r.state; return points(r.envelopes); });
    assert.deepEqual(got, expected);
  });
test('metrics poll: the state is not changed in place', () => {
  const state = new Map();
  newSamples(state, response('/r1', { cpu: [[t(0), 1]] }));
  assert.equal(state.size, 0);
});

for (const [name, polls, expected] of [
  ['a slower second series of the same metric is not dropped', [
    bySeries('/r1', 'msgs', { hubA: [[t(0), 1], [t(1), 1]], hubB: [[t(0), 2]] }),
    bySeries('/r1', 'msgs', { hubA: [[t(0), 1], [t(1), 1]], hubB: [[t(0), 2], [t(1), 3]] }),
  ], [['hubA@' + t(0), 'hubA@' + t(1), 'hubB@' + t(0)], ['hubB@' + t(1)]]],
  ['a value that arrives late for an earlier minute is still sent', [
    bySeries('/r1', 'msgs', { hubA: [[t(0), null], [t(1), 4]] }),
    bySeries('/r1', 'msgs', { hubA: [[t(0), 3], [t(1), 4]] }),
  ], [['hubA@' + t(1)], ['hubA@' + t(0)]]],
])
  test(`metrics poll: ${name}`, () => {
    let state = new Map();
    const got = polls.map((p) => { const r = newSamples(state, p); state = r.state; return seriesPoints(r.envelopes); });
    assert.deepEqual(got, expected);
  });

// #102: a forwarder that restarts polls the last 300 s again with an empty state; its envelopes must have the keys the
// first process sent, so the engine drops them as delivered instead of counting them twice
test('metrics poll: one envelope per datapoint, keyed by resource, metric, series and time, the same after a restart', () => {
  const poll = bySeries('/r1', 'msgs', { hubA: [[t(0), 1], [t(1), 2]], hubB: [[t(1), null]] });
  const { envelopes } = newSamples(new Map(), poll);
  assert.deepEqual(envelopes.map((e) => e.key), [
    `azure-metrics//r1/msgs/[["EntityName","hubA"]]/${t(0)}`,
    `azure-metrics//r1/msgs/[["EntityName","hubA"]]/${t(1)}`,
  ]);
  assert.deepEqual(envelopes.map((e) => e.meta.metrics), [['msgs'], ['msgs']]);
  const later = bySeries('/r1', 'msgs', { hubA: [[t(1), 2], [t(2), 3]] });
  const restarted = newSamples(new Map(), later).envelopes.map((e) => e.key);
  assert.equal(restarted[0], envelopes[1].key);
  assert.equal(restarted.length, 2);
});
for (const [name, a, b, same] of [
  ['another resource', response('/r1', { cpu: [[t(0), 1]] }), response('/r2', { cpu: [[t(0), 1]] }), false],
  ['another metric', response('/r1', { cpu: [[t(0), 1]] }), response('/r1', { mem: [[t(0), 1]] }), false],
  ['another series', bySeries('/r1', 'msgs', { hubA: [[t(0), 1]] }), bySeries('/r1', 'msgs', { hubB: [[t(0), 1]] }), false],
  ['another minute', response('/r1', { cpu: [[t(0), 1]] }), response('/r1', { cpu: [[t(1), 1]] }), false],
  ['another poll window and capture time', { ...response('/r1', { cpu: [[t(0), 1]] }), capturedAt: t(3), meta: { resourceId: '/r1', start: t(0), end: t(5) } },
    { ...response('/r1', { cpu: [[t(0), 1]] }), capturedAt: t(4), meta: { resourceId: '/r1', start: t(1), end: t(6) } }, true],
])
  test(`metrics poll: the key of one datapoint against ${name}`, () => {
    const key = (p) => newSamples(new Map(), p).envelopes[0].key;
    assert.equal(key(a) === key(b), same);
  });
test('metrics poll: a response with several datapoints (a capture) keeps the content hash', () => {
  const e = envelope({ source: 'azure-metrics', capturedAt: t(5), seq: 0, meta: { resourceId: '/r1' }, payload: response('/r1', { cpu: [[t(0), 1], [t(1), 2]] }).payload });
  assert.match(e.key, /^azure-metrics\/sha256:[0-9a-f]{32}$/);
});
