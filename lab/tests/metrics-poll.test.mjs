// Tests for the Azure metrics poll of the forwarder: overlapping polls must deliver each datapoint once, and only
// datapoints that have a value. Run: node --test lab/tests/*.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { newSamples } from '../capture/lib/metrics-poll.mjs';

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
const seriesPoints = (e) => e?.payload.value.flatMap((m) => m.timeseries.flatMap((s) => s.data.map((d) => `${s.metadatavalues?.[0]?.value ?? ''}@${d.timeStamp}`))) ?? [];
const points = (e) => e?.payload.value.flatMap((m) => m.timeseries.flatMap((s) => s.data.map((d) => `${m.name.value}@${d.timeStamp}`))) ?? [];
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
    const got = polls.map((p) => { const r = newSamples(state, p); state = r.state; return points(r.envelope); });
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
    const got = polls.map((p) => { const r = newSamples(state, p); state = r.state; return seriesPoints(r.envelope); });
    assert.deepEqual(got, expected);
  });
