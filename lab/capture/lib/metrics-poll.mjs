// Azure metrics polls overlap (each poll asks for a range longer than the poll interval, so a late minute is not
// missed). This keeps each datapoint to the first poll that has a value for it, per series: a metric split by a
// dimension (one series per Event Hub, for example) is tracked series by series, and a minute whose value arrives
// after a newer minute is still sent.
//   state     Map series key -> Set of the timeStamps already sent (minutes older than KEEP_MS before the newest one
//             sent are forgotten; polls ask for far less than that)
//   envelope  one azure-metrics envelope as azure-metrics.sh writes it (native response in payload)
// Returns { state, envelope }: the new state (the old one is not changed) and the envelope with only the new
// datapoints, or envelope null when nothing is new.
const KEEP_MS = 2 * 3600 * 1000;
const hasValue = (d) => typeof d.average === 'number' || typeof d.total === 'number';
const seriesKey = (rid, metric, s) => `${rid}|${metric}|${JSON.stringify((s.metadatavalues ?? []).map((m) => [m.name?.value, m.value]))}`;

export function newSamples(state, envelope) {
  const next = new Map(state);
  const rid = envelope.meta?.resourceId ?? '';
  let kept = 0;
  const value = (envelope.payload?.value ?? []).map((m) => ({
    ...m,
    timeseries: (m.timeseries ?? []).map((s) => {
      const key = seriesKey(rid, m.name?.value, s);
      const sent = state.get(key) ?? new Set();
      const fresh = (s.data ?? []).filter((d) => hasValue(d) && !sent.has(d.timeStamp));
      if (fresh.length) {
        const all = [...sent, ...fresh.map((d) => d.timeStamp)];
        const newest = Math.max(...all.map((x) => Date.parse(x)));
        next.set(key, new Set(all.filter((x) => newest - Date.parse(x) <= KEEP_MS)));
        kept += fresh.length;
      }
      return { ...s, data: fresh };
    }),
  }));
  return { state: next, envelope: kept ? { ...envelope, payload: { ...envelope.payload, value } } : null };
}
