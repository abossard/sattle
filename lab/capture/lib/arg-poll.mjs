// Azure Resource Graph for the live forwarder: the inventory once, then its changes. Envelopes have the shape of
// sources/arg.sh (source `arg`, meta.query names the table), so the engine's Resource Graph adapter reads both.
//   snapshot  every row of Resources and ResourceContainers, paged (one page in memory at a time)
//   poll      the change records (resourcechanges, resourcecontainerchanges) of the last 30 minutes before the latest
//             change seen: a deleted resource is sent as its change record, a created or changed one as its current
//             row. Change records become visible minutes after their time, so a poll asks again what the last one
//             saw; the engine drops a repeated key and a row equal to the one it has.
// The time of the latest change seen is the poller's only state; a failed poll keeps it.
//   subscription  id     getToken  async (refresh) -> bearer token for https://management.azure.com/
//   fetch      the fetch function now      () -> ms     sleep  async (ms) -> void
import { envelope } from './envelope.mjs';
import { retryWaitMs } from './azure-metrics-poll.mjs';

const ARG = 'https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01';
const PAGE = 1000;
const IDS_PER_QUERY = 100;
const RETRIES = 3; // after the first try, for 429 and 5xx
const OVERLAP_MS = 30 * 60 * 1000;
export const TABLES = [
  { query: 'resources', kql: 'Resources', changes: 'resourcechanges' },
  { query: 'resourcecontainers', kql: 'ResourceContainers', changes: 'resourcecontainerchanges' },
];

const iso = (ms) => new Date(ms).toISOString();
const chunks = (xs, n) => Array.from({ length: Math.ceil(xs.length / n) }, (_, i) => xs.slice(i * n, i * n + n));
export const changesKql = (table, sinceMs) =>
  `${table} | extend t = todatetime(properties.changeAttributes.timestamp) | where t > datetime(${iso(sinceMs - OVERLAP_MS)}) | project id, t, properties | order by t asc`;
export const rowsKql = (kql, ids) => `${kql} | where id in~ (${ids.map((id) => `'${id.replace(/'/g, '')}'`).join(', ')})`;

// What the change records of one poll ask for: the deletes to send as they are, the resources to read again (each
// once, whatever the case of its id), and the time of the latest change seen.
export function planChanges(records, sinceMs) {
  const deletes = []; const ids = new Map(); let latest = sinceMs;
  for (const r of records) {
    const p = r.properties ?? {};
    const t = Date.parse(r.t ?? p.changeAttributes?.timestamp);
    if (t > latest) latest = t;
    if (p.changeType === 'Delete') deletes.push(r);
    else if (p.targetResourceId && !ids.has(p.targetResourceId.toLowerCase())) ids.set(p.targetResourceId.toLowerCase(), p.targetResourceId);
  }
  return { deletes, ids: [...ids.values()], latest };
}

export function argPoller({ subscription, getToken, fetch = globalThis.fetch, now = Date.now, sleep = (ms) => new Promise((r) => setTimeout(r, ms)) }) {
  let latest = null; // ms; null until the snapshot started

  // one query, every page; `onPage(rows, page)` for each
  const query = async (kql, onPage) => {
    let token = await getToken(false); let skip; let page = 0;
    do {
      const body = JSON.stringify({ subscriptions: [subscription], query: kql, options: { $top: PAGE, resultFormat: 'objectArray', ...(skip ? { $skipToken: skip } : {}) } });
      let res;
      for (let k = 0; ; k++) {
        res = await fetch(ARG, { method: 'POST', headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' }, body });
        if (res.status === 401 && k === 0) { token = await getToken(true); continue; }
        if (!(res.status === 429 || res.status >= 500) || k >= RETRIES) break;
        await sleep(retryWaitMs(res.headers, k));
      }
      if (!res.ok) throw new Error(`resource graph: HTTP ${res.status}`);
      const r = await res.json();
      await onPage(r.data ?? [], page++);
      skip = r.$skipToken;
    } while (skip);
  };
  const rowEnvelope = (t, row, page) => envelope({ source: 'arg', capturedAt: new Date(now()).toISOString(), seq: 0, payload: row, meta: { query: t.query, kql: t.kql, page, pageSize: PAGE } });

  return {
    // every resource and resource container; `send(envelope)` for each row
    async snapshot(send) {
      latest ??= now();
      for (const t of TABLES) await query(t.kql, (rows, page) => rows.forEach((row) => send(rowEnvelope(t, row, page))));
    },
    // the changes since the latest one seen; rejects when the poll failed
    async poll(send) {
      const since = latest; let seen = latest;
      for (const t of TABLES) {
        const records = [];
        await query(changesKql(t.changes, since), (rows) => { records.push(...rows); });
        const plan = planChanges(records, since);
        for (const r of plan.deletes) send(envelope({ source: 'arg', capturedAt: new Date(now()).toISOString(), seq: 0, payload: r, meta: { query: t.changes } }));
        for (const ids of chunks(plan.ids, IDS_PER_QUERY)) await query(rowsKql(t.kql, ids), (rows, page) => rows.forEach((row) => send(rowEnvelope(t, row, page))));
        seen = Math.max(seen, plan.latest);
      }
      latest = seen;
    },
  };
}
