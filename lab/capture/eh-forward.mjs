#!/usr/bin/env node
// Unattended live feed: Hubble flows, Kubernetes watch events and metrics polls into one single-partition hub
// as envelopes. With --mirror-hubble the flows are also written to the hubble hub. Stream semantics: see CAPTURE-FORMAT.md.
// usage: eh-forward.mjs --namespace <ns> --kubeconfig <file> [--hub sattle-ingest] [--seconds N] [--no-hubble] [--no-metrics] [--mirror-hubble] [--authoring FILE] [--cluster NAME]
//   --authoring FILE  one authoring record per line (signal rules, policies, curations), sent as `authoring` envelopes at every start
//   --cluster NAME    the cluster's name on Kubernetes and Prometheus records; default Cilium's cluster-name
//   env HUBBLE_FILTER  observe filter flags, default "--namespace sattle-demo --namespace demo"
//   env PROM_POLL_SECONDS (60), AZURE_POLL_SECONDS (60), QUEUE_MAX_MB per hub (64)
import { execFile } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { parseArgs, promisify } from 'node:util';
import { AzureCliCredential } from '@azure/identity';
import { azureMetricsPoller, cachedToken, nextPollMs } from './lib/azure-metrics-poll.mjs';
import { producer } from './lib/eventhub.mjs';
import { clusterName, envelope, withCluster } from './lib/envelope.mjs';
import { sink } from './lib/sink.mjs';
import { newSamples } from './lib/metrics-poll.mjs';
import { startChild, supervise, sleep } from './lib/supervise.mjs';
import { watchKind } from './lib/watch.mjs';

const { values: a } = parseArgs({
  options: {
    namespace: { type: 'string' }, hub: { type: 'string', default: 'sattle-ingest' }, kubeconfig: { type: 'string' }, seconds: { type: 'string' },
    'no-hubble': { type: 'boolean' }, 'no-metrics': { type: 'boolean' }, 'mirror-hubble': { type: 'boolean' }, 'debug-start-rv': { type: 'string' },
    authoring: { type: 'string' }, cluster: { type: 'string' },
  },
});
const here = new URL('.', import.meta.url).pathname;
const kubeEnv = { ...process.env, KUBECONFIG: a.kubeconfig };
const startedAt = Date.now();
// the cluster's name as Hubble gives it (Cilium's cluster-name), so Kubernetes and Prometheus records name the cluster the flows do
const cluster = await (async () => {
  try {
    return clusterName(a.cluster ? { given: a.cluster } : await promisify(execFile)('kubectl',
      ['-n', 'kube-system', 'get', 'configmap', 'cilium-config', '-o', 'jsonpath={.data.cluster-name}', '--request-timeout=10s'],
      { env: kubeEnv, timeout: 15_000 }).then((r) => ({ stdout: r.stdout }), (error) => ({ error })));
  } catch (err) { console.error(err.message); process.exit(1); }
})();

// ---- destinations
const ingestClient = producer(a.namespace, a.hub);
const hubbleClient = producer(a.namespace, 'sattle-hubble');
// when a queue is full the sink replaces the events it drops by one marker at their place in the stream
const lossMarker = ({ count, from, to, seq }) => JSON.stringify(envelope({ source: 'sattle-forwarder', capturedAt: to, seq, payload: { at: to, event: 'dropped', reason: 'queue-full', count, from, to } }));
const ingest = sink(ingestClient, '0', { marker: lossMarker });
const hubbleSink = sink(hubbleClient, '0', { marker: lossMarker });
const sinks = [ingest, hubbleSink];

// ---- one place that turns records into stream events; seq is global and increasing for this process
let seq = 0;
const counts = {};
const route = (record) => {
  const e = withCluster(record, cluster);
  e.seq = seq++;
  counts[e.source] = (counts[e.source] ?? 0) + 1;
  const body = JSON.stringify(e); // text body: the sinks bound memory by its length
  const ev = { body, at: e.capturedAt, seq: e.seq };
  ingest.add(ev);
  if (e.source === 'hubble' && a['mirror-hubble']) hubbleSink.add(ev); // doubles the volume: off by default
};
const emit = (source, payload, meta) => route(envelope({ source, capturedAt: new Date().toISOString(), seq: 0, payload, meta }));
const marker = (payload) => { emit('sattle-forwarder', { at: new Date().toISOString(), ...payload }); console.error('marker', JSON.stringify(payload).slice(0, 200)); };
let badLines = 0; // lines that were not JSON: counted, and reported by a marker with the next flush
const forwardLine = (source) => (line) => { // a line from a child: JSON native record
  let rec; try { rec = JSON.parse(line); } catch { badLines++; return; }
  emit(source, rec);
};

// ---- authoring records: the rules of the model travel in the stream, so an engine that reads from this start has them
if (a.authoring) for (const line of readFileSync(a.authoring, 'utf8').split('\n')) if (line.trim()) emit('authoring', JSON.parse(line));

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
// Azure metrics polls overlap, so each datapoint is sent once, by the first poll that has a value for it
let metricsSent = new Map();
const onlyNew = (e) => { if (e.source !== 'azure-metrics') return e; const r = newSamples(metricsSent, e); metricsSent = r.state; return r.envelope; };
const poller = (name, script, args, everySec) => (async () => {
  while (!stopping) {
    const t0 = Date.now();
    const child = startChild('bash', [`${here}sources/${script}`, ...args], {
      env: kubeEnv,
      onLine: (l) => { let e; try { e = JSON.parse(l); } catch { badLines++; return; } e.seq = 0; const fresh = onlyNew(e); if (fresh) route(fresh); },
    });
    register(child);
    const { code } = await child.done;
    if (code !== 0 && !stopping) marker({ event: 'poll-failed', poll: name, code });
    const wait = Math.max(5_000, everySec * 1000 - (Date.now() - t0));
    for (let waited = 0; waited < wait && !stopping; waited += 500) await sleep(500);
  }
})();
// Azure metrics through the REST API: the resources are looked up once (cluster, its load balancer, the hub namespace)
const azureResources = async () => {
  const script = `source "${here}lib.sh"; load_context
    lb=$(az network lb list -g "$NODE_RG" --query '[0].id' -o tsv); ns=$(az eventhubs namespace show -g "$LAB_RG" -n "$EH_NS" --query id -o tsv)
    jq -nc --arg a "$AKS_ID" --arg l "$lb" --arg n "$ns" '[{kind:"aks",id:$a},{kind:"loadbalancer",id:$l},{kind:"eventhubs",id:$n}] | map(select(.id != ""))'`;
  const { stdout } = await promisify(execFile)('bash', ['-c', script], { env: kubeEnv });
  return JSON.parse(stdout);
};
const azureMetricsLoop = (everySec, windowSec) => (async () => {
  const getToken = cachedToken(new AzureCliCredential());
  let p; let failures = 0;
  while (!stopping) {
    const t0 = Date.now();
    try {
      p ??= azureMetricsPoller({ resources: await azureResources(), getToken });
      for (const e of await p.poll(windowSec)) { const fresh = onlyNew(e); if (fresh) route(fresh); }
      failures = 0;
    } catch (err) { failures++; if (!stopping) marker({ event: 'poll-failed', poll: 'azure-metrics', failures, error: String(err.message).slice(0, 160) }); }
    const wait = Math.max(5_000, nextPollMs(everySec, failures) - (Date.now() - t0));
    for (let waited = 0; waited < wait && !stopping; waited += 500) await sleep(500);
  }
})();
if (!a['no-metrics']) {
  const prom = Number(process.env.PROM_POLL_SECONDS ?? 60);
  // Azure metrics: a minute is accepted while its window is within the late horizon (3 windows of 60 s past the
  // watermark, which trails capture time by 60 s), so latency plus poll interval must stay under about 240 s.
  // Measured latency is under 30 s (CAPTURE-FORMAT.md): poll every 60 s over the last 300 s; newSamples drops repeats.
  const azure = Number(process.env.AZURE_POLL_SECONDS ?? 60);
  loops.push(poller('prometheus', 'prometheus.sh', ['-', String(prom * 2)], prom));
  loops.push(azureMetricsLoop(azure, Math.max(300, azure * 2)));
}

// ---- flush loop and stats
const flushAll = () => sinks.map((k) => k.flush().catch((e) => console.error('send error', String(e.message).slice(0, 120))));
const timer = setInterval(async () => {
  await Promise.all(flushAll());
  if (badLines) { marker({ event: 'dropped', count: badLines, reason: 'non-json' }); badLines = 0; }
}, 1000);
const statsTimer = setInterval(() => {
  const s = { event: 'stats', uptimeSeconds: Math.round((Date.now() - startedAt) / 1000), ingest: ingest.stats(), hubble: hubbleSink.stats(), counts, rssMB: Math.round(process.memoryUsage().rss / 1e6) };
  marker(s);
}, 60_000);

marker({ event: 'start', pid: process.pid, hub: a.hub, cluster: cluster || null, hubbleFilter: process.env.HUBBLE_FILTER ?? '--namespace sattle-demo --namespace demo' });

// ---- clean shutdown: stop producers, drain the queues (bounded wait), say goodbye, close
const stop = async (why) => {
  if (stopping) return;
  stopping = true;
  console.error(`stopping (${why})`);
  clearInterval(timer); clearInterval(statsTimer);
  for (const c of children) c.kill();
  await Promise.race([Promise.allSettled(loops), sleep(8000)]);
  marker({ event: 'stop', reason: why, counts });
  // retry until every queue is empty or the deadline passes; events left behind make the exit status non-zero
  const deadline = Date.now() + 15_000;
  for (const k of sinks) {
    while ((!k.idle() || k.stats().queued) && Date.now() < deadline) {
      if (k.idle()) await k.flush().catch((e) => console.error('final send error', String(e.message).slice(0, 120)));
      await sleep(k.stats().queued ? 500 : 50);
    }
  }
  const lost = sinks.reduce((n, k) => n + k.stats().queued, 0);
  await Promise.allSettled([ingestClient.close(), hubbleClient.close()]);
  console.error(`eh-forward: sent ingest=${ingest.stats().sent} hubble=${hubbleSink.stats().sent}`, JSON.stringify(counts));
  if (lost) console.error(`eh-forward: ${lost} events were still queued at exit and are lost`);
  process.exit(lost ? 1 : 0);
};
process.on('SIGINT', () => stop('SIGINT'));
process.on('SIGTERM', () => stop('SIGTERM'));
if (a.seconds) setTimeout(() => stop('duration'), Number(a.seconds) * 1000);
