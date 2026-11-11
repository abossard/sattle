#!/usr/bin/env node
// Unattended live feed: Hubble flows, Kubernetes watch events and metrics polls into one single-partition hub
// as envelopes. With --mirror-hubble the flows are also written to the hubble hub. Stream semantics: see CAPTURE-FORMAT.md.
// usage: eh-forward.mjs --namespace <ns> --kubeconfig <file> [--hub sattle-ingest] [--seconds N] [--no-hubble] [--no-metrics] [--mirror-hubble]
//   env HUBBLE_FILTER  observe filter flags, default "--namespace sattle-demo --namespace demo"
//   env PROM_POLL_SECONDS (60), AZURE_POLL_SECONDS (300), QUEUE_MAX_MB per hub (64)
import { parseArgs } from 'node:util';
import { producer } from './lib/eventhub.mjs';
import { envelope } from './lib/envelope.mjs';
import { sink } from './lib/sink.mjs';
import { startChild, supervise, sleep } from './lib/supervise.mjs';
import { watchKind } from './lib/watch.mjs';

const { values: a } = parseArgs({
  options: {
    namespace: { type: 'string' }, hub: { type: 'string', default: 'sattle-ingest' }, kubeconfig: { type: 'string' }, seconds: { type: 'string' },
    'no-hubble': { type: 'boolean' }, 'no-metrics': { type: 'boolean' }, 'mirror-hubble': { type: 'boolean' }, 'debug-start-rv': { type: 'string' },
  },
});
const here = new URL('.', import.meta.url).pathname;
const kubeEnv = { ...process.env, KUBECONFIG: a.kubeconfig };
const startedAt = Date.now();

// ---- destinations
const ingestClient = producer(a.namespace, a.hub);
const hubbleClient = producer(a.namespace, 'sattle-hubble');
const ingest = sink(ingestClient, '0');
const hubbleSink = sink(hubbleClient, '0');
const sinks = [ingest, hubbleSink];

// ---- one place that turns records into stream events; seq is global and increasing for this process
let seq = 0;
const counts = {};
const route = (e) => {
  e.seq = seq++;
  counts[e.source] = (counts[e.source] ?? 0) + 1;
  const body = JSON.stringify(e); // text body: the sinks bound memory by its length
  ingest.add({ body });
  if (e.source === 'hubble' && a['mirror-hubble']) hubbleSink.add({ body }); // doubles the volume: off by default
};
const emit = (source, payload, meta) => route(envelope({ source, capturedAt: new Date().toISOString(), seq: 0, payload, meta }));
const marker = (payload) => { emit('sattle-forwarder', { at: new Date().toISOString(), ...payload }); console.error('marker', JSON.stringify(payload).slice(0, 200)); };
const forwardLine = (source) => (line) => { // a line from a child: JSON native record
  try { emit(source, JSON.parse(line)); } catch { /* partial or non-JSON noise */ }
};

// ---- lifecycle
let stopping = false;
const children = new Set();
const register = (c) => { children.add(c); c.done.then(() => children.delete(c)); };
const isStopping = () => stopping;
const loops = [];

// ---- Kubernetes: a list-then-watch loop per kind
const kinds = ['/api/v1/namespaces', '/api/v1/nodes', '/api/v1/pods', '/api/v1/services', '/api/v1/events',
  '/apis/apps/v1/deployments', '/apis/apps/v1/replicasets', '/apis/discovery.k8s.io/v1/endpointslices'];
for (const path of kinds) {
  loops.push(watchKind({ path, kubeconfig: a.kubeconfig, emit, marker, isStopping, register, startRv: path.endsWith('/pods') ? a['debug-start-rv'] : undefined }));
}

// ---- Hubble: the relay script is restarted with backoff; after a gap it replays what the relay still holds
if (!a['no-hubble']) {
  let lastFlowAt = Date.now();
  loops.push(supervise({
    name: 'hubble', isStopping, register,
    run: (attempt) => {
      const gapSec = Math.ceil((Date.now() - lastFlowAt) / 1000) + 5;
      const env = { ...kubeEnv, HUBBLE_FILTER: process.env.HUBBLE_FILTER ?? '--namespace sattle-demo --namespace demo', ...(attempt > 0 ? { HUBBLE_SINCE: `${gapSec}s` } : {}) };
      return startChild('bash', [`${here}sources/hubble-follow.sh`], { env, onLine: (l) => { lastFlowAt = Date.now(); forwardLine('hubble')(l); } });
    },
    onExit: (x) => marker({ event: 'hubble-reconnect', exitCode: x.code, ranSeconds: Math.round(x.ranMs / 1000), retryInMs: x.backoffMs, stderr: x.stderr.slice(-160) }),
  }));
}

// ---- Telemetry: a poller runs one script at a time, so slow polls never overlap
const poller = (name, script, args, everySec) => (async () => {
  while (!stopping) {
    const t0 = Date.now();
    const child = startChild('bash', [`${here}sources/${script}`, ...args], {
      env: kubeEnv,
      onLine: (l) => { try { const e = JSON.parse(l); e.seq = 0; route(e); } catch { /* noise */ } },
    });
    register(child);
    const { code } = await child.done;
    if (code !== 0 && !stopping) marker({ event: 'poll-failed', poll: name, code });
    const wait = Math.max(5_000, everySec * 1000 - (Date.now() - t0));
    for (let waited = 0; waited < wait && !stopping; waited += 500) await sleep(500);
  }
})();
if (!a['no-metrics']) {
  const prom = Number(process.env.PROM_POLL_SECONDS ?? 60);
  const azure = Number(process.env.AZURE_POLL_SECONDS ?? 300);
  loops.push(poller('prometheus', 'prometheus.sh', ['-', String(prom * 2)], prom));
  loops.push(poller('azure-metrics', 'azure-metrics.sh', ['-', String(azure * 2)], azure));
}

// ---- flush loop and stats
const flushAll = () => sinks.map((k) => k.flush().catch((e) => console.error('send error', String(e.message).slice(0, 120))));
const timer = setInterval(async () => {
  await Promise.all(flushAll());
  const n = ingest.takeDropped() + hubbleSink.takeDropped();
  if (n) marker({ event: 'dropped', count: n, reason: 'queue-full' }); // events were lost between the previous event and this marker
}, 1000);
const statsTimer = setInterval(() => {
  const s = { event: 'stats', uptimeSeconds: Math.round((Date.now() - startedAt) / 1000), ingest: ingest.stats(), hubble: hubbleSink.stats(), counts, rssMB: Math.round(process.memoryUsage().rss / 1e6) };
  marker(s);
}, 60_000);

marker({ event: 'start', pid: process.pid, hub: a.hub, hubbleFilter: process.env.HUBBLE_FILTER ?? '--namespace sattle-demo --namespace demo' });

// ---- clean shutdown: stop producers, drain the queues (bounded wait), say goodbye, close
const stop = async (why) => {
  if (stopping) return;
  stopping = true;
  console.error(`stopping (${why})`);
  clearInterval(timer); clearInterval(statsTimer);
  for (const c of children) c.kill();
  await Promise.race([Promise.allSettled(loops), sleep(8000)]);
  marker({ event: 'stop', reason: why, counts });
  const deadline = Date.now() + 15_000;
  for (const k of sinks) {
    while (!k.idle() && Date.now() < deadline) await sleep(50);
    await k.flush().catch((e) => console.error('final send error', String(e.message).slice(0, 120)));
  }
  await Promise.allSettled([ingestClient.close(), hubbleClient.close()]);
  console.error(`eh-forward: sent ingest=${ingest.stats().sent} hubble=${hubbleSink.stats().sent}`, JSON.stringify(counts));
  process.exit(0);
};
process.on('SIGINT', () => stop('SIGINT'));
process.on('SIGTERM', () => stop('SIGTERM'));
if (a.seconds) setTimeout(() => stop('duration'), Number(a.seconds) * 1000);
