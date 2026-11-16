// Azure platform metrics for the live forwarder, read from the Azure Monitor REST API with the user's Entra token.
// One poll is a handful of parallel HTTP requests (the az CLI took about 4 s per request, 2 minutes per poll, which
// pushed every minute past the engine's late horizon). Envelopes have the shape of sources/azure-metrics.sh.
// The metric definitions are read once per resource. What happens to an answer that is not 200:
//   400        the metrics of the group cannot be asked together: the group is split into single metrics once and
//              remembered; a metric that gets 400 alone is left out from then on
//   429, 5xx   the same request again after Retry-After, or after 1, 2, 4 s; still failing, the poll fails
//   401        a new token, then the same request once more; still 401, the poll fails
//   other      the poll fails
// A failed poll keeps every metric for the next poll.
//   resources  [{ kind, id }]     getToken  async (refresh) -> bearer token for https://management.azure.com/
//   fetch      the fetch function now      () -> ms     sleep  async (ms) -> void
import { envelope } from './envelope.mjs';

const ARM = 'https://management.azure.com';
const API = '2023-10-01';
const MAX_PER_REQUEST = 20;
const RETRIES = 3; // after the first try, for 429 and 5xx
const iso = (ms) => new Date(Math.floor(ms / 1000) * 1000).toISOString().replace('.000Z', 'Z');
const chunks = (xs, n) => Array.from({ length: Math.ceil(xs.length / n) }, (_, i) => xs.slice(i * n, i * n + n));
const transient = (status) => status === 429 || status >= 500;
// wait before retry `k` (0-based): Retry-After in seconds when the service sent it, else 1, 2, 4 s
export const retryWaitMs = (headers, k) => {
  const after = Number(headers?.get?.('retry-after'));
  return Number.isFinite(after) && after > 0 ? after * 1000 : 1000 * 2 ** k;
};

// time until the next poll: the cadence while polls succeed; after n failed polls in a row twice as long per failure,
// at most 15 minutes, so a throttled or broken API is not asked every minute
export const nextPollMs = (everySec, failures) => Math.min(everySec * 1000 * 2 ** failures, 15 * 60 * 1000);

// a token getter that asks `credential` only when the cached token is within 5 minutes of expiry or when told to
export function cachedToken(credential, scope = 'https://management.azure.com/.default', now = Date.now) {
  let cached = null;
  return async (refresh) => {
    if (refresh || !cached || cached.expiresOnTimestamp - now() < 5 * 60 * 1000) cached = await credential.getToken(scope);
    return cached.token;
  };
}

export function azureMetricsPoller({ resources, getToken, fetch = globalThis.fetch, now = Date.now, sleep = (ms) => new Promise((r) => setTimeout(r, ms)) }) {
  const groups = new Map(); // resource id -> [[metric names asked together]]

  // one poll's access: one token, renewed at most once when the service answers 401
  const session = async () => {
    let token = await getToken(false);
    let renewed = null;
    const renew = (used) => (renewed ??= (async () => { if (token === used) token = await getToken(true); return token; })());
    const get = async (path, params) => {
      const url = `${ARM}${path}?${new URLSearchParams({ 'api-version': API, ...params })}`;
      let used = token; let res;
      for (let k = 0; ; k++) {
        res = await fetch(url, { headers: { Authorization: `Bearer ${used}` } });
        if (res.status === 401) { used = await renew(used); res = await fetch(url, { headers: { Authorization: `Bearer ${used}` } }); }
        if (!transient(res.status) || k === RETRIES) break;
        await sleep(retryWaitMs(res.headers, k));
      }
      return { ok: res.ok, status: res.status, body: await res.json().catch(() => null) };
    };
    return get;
  };
  const failed = (what, r) => new Error(`${what}: HTTP ${r.status}`);

  const groupsOf = async (get, id) => {
    if (!groups.has(id)) {
      const r = await get(`${id}/providers/Microsoft.Insights/metricDefinitions`, {});
      if (!r.ok) throw failed(`metric definitions of ${id}`, r);
      groups.set(id, chunks((r.body?.value ?? []).map((d) => d.name.value), MAX_PER_REQUEST));
    }
    return groups.get(id);
  };
  const ask = (get, id, names, start, end) => get(`${id}/providers/Microsoft.Insights/metrics`, {
    metricnames: names.join(','), timespan: `${start}/${end}`, interval: 'PT1M', aggregation: 'Average,Total,Maximum',
  });

  // one group: its response; after a 400 the responses of its metrics asked one by one (remembered)
  const pollGroup = async (get, r, names, start, end) => {
    const res = await ask(get, r.id, names, start, end);
    if (res.ok) return [{ names, body: res.body }];
    if (res.status !== 400) throw failed(`metrics of ${r.id}`, res);
    const alone = names.length > 1 ? await Promise.all(names.map(async (n) => ({ names: [n], res: await ask(get, r.id, [n], start, end) }))) : [{ names, res }];
    const other = alone.find((x) => !x.res.ok && x.res.status !== 400);
    if (other) throw failed(`metrics of ${r.id}`, other.res);
    const kept = alone.filter((x) => x.res.ok);
    groups.set(r.id, [...groups.get(r.id).filter((g) => g !== names), ...kept.map((x) => x.names)]);
    return kept.map((x) => ({ names: x.names, body: x.res.body }));
  };

  return {
    // envelopes for the last `windowSec` seconds; rejects when the poll failed
    async poll(windowSec) {
      const get = await session();
      const t = now(); const start = iso(t - windowSec * 1000); const end = iso(t);
      const per = await Promise.all(resources.map(async (r) => {
        const gs = await groupsOf(get, r.id);
        const answers = (await Promise.all(gs.map((g) => pollGroup(get, r, g, start, end)))).flat();
        return answers.map(({ names, body }) => envelope({
          source: 'azure-metrics', capturedAt: new Date(now()).toISOString(), seq: 0, payload: body,
          meta: { resource: r.kind, resourceId: r.id, interval: 'PT1M', start, end, metrics: names },
        }));
      }));
      return per.flat();
    },
  };
}
