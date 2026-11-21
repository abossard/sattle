import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { dnsAlias } from './tutorial-inventory.mjs';

const minute = 60_000;
const retained = new Set(['PodList', 'ReplicaSetList', 'DeploymentList', 'ServiceList', 'EndpointSliceList', 'NodeList', 'NamespaceList']);
const platformNamespaces = new Set(['default', 'kube-system', 'sattle-demo']);
const namespacedName = (namespace, name) => JSON.stringify([namespace, name]);

function retainObjects(rows, cluster, alias, start) {
  const name = (value) => typeof value === 'string' && value ? alias.name(value) : undefined;
  const namespace = (value) => platformNamespaces.has(value) ? value : name(value);
  const dns = (value) => typeof value === 'string' && value ? dnsAlias(value, alias) : undefined;
  const replicaOwners = new Map(rows.filter((row) => row.meta.list.kind === 'ReplicaSetList').map(({ payload }) => [
    namespacedName(payload.metadata.namespace, payload.metadata.name),
    payload.metadata.ownerReferences?.find((ref) => ref.kind === 'Deployment')?.name,
  ]));
  const replicaSet = (value, ns) => {
    const deployment = replicaOwners.get(namespacedName(ns, value));
    return deployment ? `${name(deployment)}-${name(value).slice(4)}` : name(value);
  };
  const labels = (values) => values && Object.fromEntries(Object.entries(values)
    .filter(([, value]) => typeof value === 'string')
    .map(([key, value]) => [name(key), alias.value(value)]));
  const ownerReferences = (owners, ns) => owners?.map(({ kind, name: value, uid, controller }) => ({
    kind, name: kind === 'ReplicaSet' ? replicaSet(value, ns) : name(value), uid, controller,
  }));

  const capturedAt = new Date(start).toISOString();
  return rows.map((row, index) => {
    const kind = row.meta.list.kind;
    const input = row.payload;
    const { metadata, spec, status } = input;
    const ns = metadata.namespace;
    const payload = { metadata: {
      name: kind === 'ReplicaSetList' ? replicaSet(metadata.name, ns) : kind === 'NamespaceList' ? namespace(metadata.name) : name(metadata.name),
      namespace: namespace(ns), uid: metadata.uid,
      ownerReferences: ownerReferences(metadata.ownerReferences, ns),
    } };
    if (kind === 'NodeList') {
      payload.spec = { providerID: spec?.providerID };
      payload.metadata.labels = {
        'kubernetes.azure.com/agentpool': name(metadata.labels?.['kubernetes.azure.com/agentpool']),
        agentpool: name(metadata.labels?.agentpool),
      };
    }
    if (kind === 'PodList') {
      payload.metadata.labels = { team: name(metadata.labels?.team) };
      payload.spec = { nodeName: name(spec?.nodeName), serviceAccountName: name(spec?.serviceAccountName) };
      payload.status = {
        phase: status?.phase, podIP: status?.podIP,
        podIPs: status?.podIPs?.map(({ ip }) => ({ ip })),
        conditions: status?.conditions?.filter((condition) => condition.type === 'Ready').map(({ type, status }) => ({ type, status })),
        containerStatuses: status?.containerStatuses?.map(({ restartCount }) => ({ restartCount })),
      };
    }
    if (kind === 'DeploymentList') {
      payload.spec = { replicas: spec?.replicas, template: { metadata: { labels: labels(spec?.template?.metadata?.labels) } } };
      payload.status = { replicas: status?.replicas, readyReplicas: status?.readyReplicas };
    }
    if (kind === 'ServiceList') {
      payload.spec = { type: spec?.type, clusterIP: spec?.clusterIP, externalName: dns(spec?.externalName), selector: labels(spec?.selector) };
      payload.status = { loadBalancer: { ingress: status?.loadBalancer?.ingress?.map(({ ip, hostname }) => ({ ip, hostname: dns(hostname) })) } };
    }
    if (kind === 'EndpointSliceList') {
      payload.metadata.labels = { 'kubernetes.io/service-name': name(metadata.labels?.['kubernetes.io/service-name']) };
      payload.endpoints = input.endpoints?.map(({ addresses, targetRef }) => ({
        addresses,
        targetRef: targetRef && { kind: targetRef.kind, name: name(targetRef.name), namespace: namespace(targetRef.namespace), uid: targetRef.uid },
      }));
    }
    return {
      v: 1, source: 'kubernetes-list', seq: index + 1, key: `tutorial/kubernetes/${index + 1}`, capturedAt,
      meta: { list: { kind }, cluster, originalCapturedAt: row.capturedAt, tutorialClock: 'inventory-at-first-sample' },
      payload,
    };
  });
}

async function readCalls(file, readLines, pods, first, end) {
  const calls = new Map();
  let cluster;
  if (!existsSync(file)) return { calls, cluster };
  for await (const line of readLines(file)) {
    if (!line.includes('"pod_name"')) continue;
    const row = JSON.parse(line);
    const flow = row.payload?.flow;
    const from = namespacedName(flow?.source?.namespace, flow?.source?.pod_name);
    const to = namespacedName(flow?.destination?.namespace, flow?.destination?.pod_name);
    if (row.source !== 'hubble' || from === to || !pods.has(from) || !pods.has(to)) continue;
    const time = Date.parse(flow.time ?? row.payload.time);
    if (!Number.isFinite(time) || time < first || time >= end || typeof flow.verdict !== 'string') continue;
    const clusterName = flow.source.cluster_name || flow.destination.cluster_name;
    if (cluster && clusterName && cluster !== clusterName) continue;
    if (clusterName) cluster ??= clusterName;
    const direction = flow.is_reply ? [to, from] : [from, to];
    const key = JSON.stringify([...direction, flow.verdict]);
    if (!calls.has(key) || calls.get(key).time < time) calls.set(key, { row, time });
  }
  return { calls, cluster };
}

// The replay keeps one real representative per caller, callee and observed verdict.
function retainCalls(calls, cluster, alias) {
  const endpoint = ({ namespace, pod_name: name }) => ({
    namespace: platformNamespaces.has(namespace) ? namespace : alias.name(namespace), pod_name: alias.name(name), cluster_name: cluster,
  });
  return [...calls.values()].sort((left, right) => left.time - right.time).map(({ row, time }, index) => {
    const flow = row.payload.flow;
    return {
      v: 1, source: 'hubble', seq: index + 1, key: `tutorial/hubble/${index + 1}`,
      capturedAt: new Date(Math.floor(time / minute) * minute + minute).toISOString(),
      meta: { originalCapturedAt: row.capturedAt, tutorialClock: 'end-of-sample-minute' },
      payload: { flow: {
        time: flow.time ?? row.payload.time, verdict: flow.verdict, is_reply: flow.is_reply,
        source: endpoint(flow.source), destination: endpoint(flow.destination),
        IP: flow.IP && { source: flow.IP.source, destination: flow.IP.destination },
      } },
    };
  });
}

export async function tutorialKubernetes(captureDir, readLines, alias, first, last) {
  const file = join(captureDir, 'kubernetes/list.ndjson');
  if (!existsSync(file)) return { objects: [], calls: [] };
  const objects = [];
  for await (const line of readLines(file)) {
    const row = JSON.parse(line);
    if (row.source === 'kubernetes-list' && retained.has(row.meta?.list?.kind) && row.payload?.metadata?.name && row.payload.metadata.uid) objects.push(row);
  }
  const pods = new Set(objects.filter((row) => row.meta.list.kind === 'PodList' && row.payload.metadata.namespace)
    .map(({ payload }) => namespacedName(payload.metadata.namespace, payload.metadata.name)));
  const selected = await readCalls(join(captureDir, 'hubble/flows.ndjson'), readLines, pods, first, last + minute);
  const rawCluster = selected.cluster ?? objects.find((row) => row.meta.cluster)?.meta.cluster;
  const cluster = rawCluster ? alias.name(rawCluster) : 'tutorial-aks';
  return { objects: retainObjects(objects, cluster, alias, first), calls: retainCalls(selected.calls, cluster, alias) };
}
