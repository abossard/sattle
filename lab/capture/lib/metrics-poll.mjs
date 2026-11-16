// Azure metrics polls overlap (each poll asks for a range longer than the poll interval, so a late minute is not
// missed). Each datapoint with a value becomes an envelope of its own, keyed by resource, metric, series and time
// (envelope.mjs), and is sent by the first poll that has a value for it: a metric split by a dimension (one series per
// Event Hub, for example) is tracked series by series, and a minute whose value arrives after a newer minute is still
// sent. A forwarder that restarts sends the datapoints of its first poll again under the keys they had, so the engine
// drops them as delivered (within its window of the last 20,000 keys).
//   state     Map key -> time (ms) of the datapoints already sent (those older than KEEP_MS before the newest one are
//             forgotten; polls ask for far less than that)
//   envelope  one azure-metrics envelope as azure-metrics.sh writes it (native response in payload)
// Returns { state, envelopes }: the new state (the old one is not changed) and one envelope per new datapoint, in the
// order of the response.
import { envelope as wrap } from './envelope.mjs';

const KEEP_MS = 2 * 3600 * 1000;
const hasValue = (d) => typeof d.average === 'number' || typeof d.total === 'number';

export function newSamples(state, e) {
  const samples = (e.payload?.value ?? []).flatMap((m) => (m.timeseries ?? []).flatMap((s) => (s.data ?? []).filter(hasValue).map((d) => ({
    at: Date.parse(d.timeStamp),
    envelope: wrap({
      source: e.source, capturedAt: e.capturedAt, seq: e.seq, meta: { ...e.meta, metrics: [m.name?.value] },
      payload: { ...e.payload, value: [{ ...m, timeseries: [{ ...s, data: [d] }] }] },
    }),
  }))));
  const fresh = samples.filter((x) => !state.has(x.envelope.key));
  if (!fresh.length) return { state, envelopes: [] };
  const next = new Map(state);
  for (const x of fresh) next.set(x.envelope.key, x.at);
  const newest = Math.max(...next.values());
  for (const [k, at] of next) if (newest - at > KEEP_MS) next.delete(k);
  return { state: next, envelopes: fresh.map((x) => x.envelope) };
}
