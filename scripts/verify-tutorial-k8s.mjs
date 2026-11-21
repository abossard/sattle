import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

const modelTypes = new Map([['Namespace', 'Namespace'], ['Pod', 'Pod'], ['ReplicaSet', 'ReplicaSet'], ['Deployment', 'Deployment'], ['Service', 'Service'], ['Node', 'KubernetesNode']]);
const emittedTypes = new Set(modelTypes.values());
const property = (node, key) => node?.properties.find((entry) => entry.property.value.split('.').at(-1) === key)?.value.value;
const kindOf = (node) => node.type.value.split('.').at(-1);
const key = (...parts) => JSON.stringify(parts);
const rows = (file) => readFileSync(file, 'utf8').split('\n').filter(Boolean).map(JSON.parse);
const uid = (record) => key(record.cluster, record.object.metadata.uid);
const namespace = (record) => record.object.metadata.namespace ?? '';
const cluster = (label) => (label || 'lab').toLowerCase();
const arm = (id = '') => id.toLowerCase();

function kubeInventory(dir) {
  const events = readdirSync(dir).filter((path) => path.endsWith('.ndjson')).flatMap((path) => rows(join(dir, path)));
  events.sort((a, b) => a.capturedAt.localeCompare(b.capturedAt) || a.seq - b.seq);
  const current = new Map();
  for (const row of events) {
    const list = row.source === 'kubernetes-list';
    if (!list && row.source !== 'kubernetes-watch') continue;
    const object = list ? row.payload : row.payload.object;
    const kind = list ? row.meta.list.kind.replace(/List$/, '') : object?.kind;
    if (!object?.metadata?.uid || !object?.metadata?.name) continue;
    const record = { object, kind, cluster: cluster(row.meta?.cluster) };
    if (!list && row.payload.type === 'DELETED') current.delete(uid(record));
    else current.set(uid(record), record);
  }
  return current;
}

const objectKey = (record) => key(modelTypes.get(record.kind), record.cluster, namespace(record), record.object.metadata.name);
const entityKey = (entity) => key(kindOf(entity), cluster(property(entity, 'cluster')), property(entity, 'namespace') ?? '', property(entity, 'name'));
const owner = (record, kind) => record.object.metadata.ownerReferences?.find((ref) => ref.kind === kind && ref.controller === true);

class GraphEvidence {
  constructor(graph) {
    this.entities = new Set(graph.entities.map((entity) => entity.id.value));
    this.edges = new Set(graph.relationships.map((edge) => key(kindOf(edge), edge.source.value, edge.target.value)));
    this.relationships = graph.relationships;
  }

  shows(relation, from, to) {
    return this.entities.has(from) && this.entities.has(to) && this.edges.has(key(relation, from, to));
  }

  check(relation, from, to) {
    assert(this.shows(relation, from, to), `Graph represents captured ${relation} evidence`);
  }
}

const add = (map, from, to) => {
  if (!map.has(from)) map.set(from, new Set());
  map.get(from).add(to);
};
const count = (map) => [...map.values()].reduce((total, targets) => total + targets.size, 0);

export function verifyKubernetes(capture, extent) {
  const dir = join(capture, 'kubernetes');
  if (!existsSync(dir)) return;
  const current = kubeInventory(dir);
  const records = [...current.values()].filter((record) => modelTypes.has(record.kind));
  const recordsOf = (kind) => records.filter((record) => record.kind === kind);
  const master = extent('master');
  const graph = new GraphEvidence(master);
  const kube = new Map(master.entities.filter((entity) => emittedTypes.has(kindOf(entity))).map((entity) => [entityKey(entity), entity.id.value]));
  const resources = new Map(master.entities.filter((entity) => kindOf(entity) === 'Resource').map((entity) => [arm(property(entity, 'armId')), entity]));
  const endpoints = new Map(master.entities.filter((entity) => kindOf(entity) === 'ServiceEndpoint')
    .map((entity) => [key(entityKey(entity), property(entity, 'address')), entity.id.value]));
  const addresses = new Map(master.entities.filter((entity) => kindOf(entity) === 'IpAddress')
    .map((entity) => [property(entity, 'address'), entity.id.value]));
  assert.equal(kube.size, records.length, 'Master contains every imported Kubernetes object represented by these types');
  for (const record of records) assert(kube.has(objectKey(record)), 'Kubernetes object appears by its captured kind, namespace and name');
  for (const kind of modelTypes.keys()) assert(recordsOf(kind).length > 0, `Tutorial capture includes ${kind} records`);

  const id = (record) => record && kube.get(objectKey(record));
  for (const kind of ['Pod', 'ReplicaSet', 'Deployment', 'Service'])
    for (const record of recordsOf(kind)) {
      const scope = kube.get(key('Namespace', record.cluster, '', namespace(record)));
      assert(scope, 'The object has an imported Kubernetes namespace');
      graph.check(`NamespaceContains${kind}`, scope, id(record));
    }
  const ownedBy = (record, kind) => {
    if (!record) return undefined;
    const ref = owner(record, kind);
    const owning = ref && current.get(key(record.cluster, ref.uid));
    return owning?.kind === kind ? owning : undefined;
  };
  const deployments = new Map();
  const scheduled = new Map();
  const backs = new Map();
  const serviceEndpoints = new Map();
  const hosts = new Map();
  const podsByName = new Map();
  let podOwners = 0, replicaOwners = 0, selectors = 0;

  for (const record of recordsOf('ReplicaSet')) {
    const deployment = id(ownedBy(record, 'Deployment'));
    if (!deployment) continue;
    graph.check('ReplicaSetOwnedBy', id(record), deployment);
    replicaOwners++;
  }
  for (const record of recordsOf('Pod')) {
    const pod = id(record);
    const { metadata, spec } = record.object;
    podsByName.set(key(record.cluster, namespace(record), metadata.name), pod);
    const replicaSet = ownedBy(record, 'ReplicaSet');
    if (replicaSet) {
      graph.check('PodOwnedBy', pod, id(replicaSet));
      podOwners++;
    }
    const deployment = id(ownedBy(replicaSet, 'Deployment'));
    if (deployment) deployments.set(pod, deployment);
    const host = kube.get(key('KubernetesNode', record.cluster, '', spec?.nodeName));
    if (!host) continue;
    graph.check('RunsOnNode', pod, host);
    if (deployment) add(scheduled, deployment, host);
  }
  for (const record of recordsOf('Service')) {
    const selector = Object.entries(record.object.spec?.selector ?? {});
    if (!selector.length || record.object.spec?.type === 'ExternalName') continue;
    for (const deployment of recordsOf('Deployment')) {
      if (record.cluster !== deployment.cluster || namespace(record) !== namespace(deployment)) continue;
      const labels = deployment.object.spec?.template?.metadata?.labels ?? {};
      if (!selector.every(([label, value]) => labels[label] === value)) continue;
      graph.check('SelectsDeployment', id(record), id(deployment));
      selectors++;
    }
  }
  for (const { object, kind, cluster: label } of current.values()) {
    if (kind !== 'EndpointSlice') continue;
    const service = kube.get(key('Service', label, object.metadata.namespace ?? '', object.metadata.labels?.['kubernetes.io/service-name']));
    if (!service) continue;
    for (const endpoint of object.endpoints ?? []) {
      for (const address of endpoint.addresses ?? []) {
        const endpointKey = key('ServiceEndpoint', label, object.metadata.namespace ?? '', object.metadata.labels['kubernetes.io/service-name']);
        const target = endpoints.get(key(endpointKey, address));
        graph.check('HasServiceEndpoint', service, target);
        add(serviceEndpoints, service, target);
      }
      const ref = endpoint.targetRef;
      const record = ref?.kind === 'Pod' && current.get(key(label, ref.uid));
      const pod = record && id(record);
      if (!pod || !endpoint.addresses?.length) continue;
      graph.check('BacksService', pod, service);
      add(backs, pod, service);
    }
  }
  for (const record of recordsOf('Node')) {
    const provider = arm(record.object.spec?.providerID).replace(/^azure:\/\//, '');
    if (!provider) continue;
    const vmss = provider.match(/^(.*\/virtualmachinescalesets\/[^/]+)\/virtualmachines\/[^/]+$/)?.[1];
    const host = (vmss && resources.get(vmss)) || resources.get(provider);
    if (!host) continue;
    graph.check('HostedByAzure', id(record), host.id.value);
    hosts.set(id(record), host);
  }
  assert(podOwners && replicaOwners && selectors && serviceEndpoints.size && backs.size && hosts.size, 'Capture supplies actual Kubernetes ownership, Services, endpoints and Azure node hosts');
  console.log(`✓ Master connects ${podOwners} Pods to ReplicaSets, ${replicaOwners} ReplicaSets to Deployments, ${selectors} Service selectors, ${count(serviceEndpoints)} endpoints, ${count(backs)} Pod backends and ${hosts.size} AKS node hosts.`);

  const flows = rows(join(capture, 'hubble/flows.ndjson'));
  const calls = new Map();
  const observedIps = new Map();
  for (const row of flows) {
    const flow = row.payload.flow;
    if (!flow) continue;
    const [from, to] = flow.is_reply === true ? [flow.destination, flow.source] : [flow.source, flow.destination];
    const label = cluster(from?.cluster_name ?? to?.cluster_name ?? row.meta?.cluster);
    const source = podsByName.get(key(label, from?.namespace, from?.pod_name));
    const target = podsByName.get(key(label, to?.namespace, to?.pod_name));
    if (!source || !target) continue;
    if (source !== target) add(calls, source, target);
    const observation = flow.IP?.[flow.is_reply === true ? 'source' : 'destination'];
    if (observation) {
      const address = addresses.get(observation);
      graph.check('ObservedAtIp', target, address);
      add(observedIps, target, address);
    }
  }

  const workloads = new GraphEvidence(extent('workloads'));
  const deploymentCalls = new Map();
  const serviceCalls = new Map();
  for (const [source, targets] of calls) {
    for (const target of targets) {
      graph.check('PodCalls', source, target);
      const from = deployments.get(source), to = deployments.get(target);
      if (from && to && from !== to) add(deploymentCalls, from, to);
      for (const a of backs.get(source) ?? []) for (const b of backs.get(target) ?? []) {
        if (a !== b) add(serviceCalls, a, b);
      }
    }
  }
  for (const [from, targets] of deploymentCalls) for (const to of targets) workloads.check('DeploymentCalls', from, to);
  for (const [from, targets] of serviceCalls) for (const to of targets) workloads.check('ServiceCalls', from, to);
  for (const [from, targets] of scheduled) for (const to of targets) workloads.check('ScheduledOn', from, to);
  assert(deploymentCalls.size && serviceCalls.size && scheduled.size, 'Capture witnesses deployment calls, service calls and node scheduling');
  console.log(`✓ Workloads can trace ${count(deploymentCalls)} directed Deployment calls and ${count(serviceCalls)} Service calls to ${count(calls)} Hubble Pod call pairs, with ${count(observedIps)} Pod/IP observations and ${count(scheduled)} Deployment-to-node placements.`);

  const placement = new GraphEvidence(extent('placement'));
  const vmss = new Map();
  const machines = new Map();
  for (const [deployment, nodes] of scheduled) {
    for (const node of nodes) {
      const host = hosts.get(node);
      const type = property(host, 'resourceType');
      if (type === 'microsoft.compute/virtualmachinescalesets') add(vmss, host.id.value, deployment);
      else if (type === 'microsoft.compute/virtualmachines') add(machines, host.id.value, deployment);
    }
  }
  for (const [host, members] of vmss) for (const deployment of members) placement.check('RunsOnVmss', deployment, host);
  for (const [host, members] of machines) for (const deployment of members) placement.check('RunsOnVm', deployment, host);
  assert(vmss.size, 'Capture witnesses Deployment placement on a VM scale set');

  const colocation = new GraphEvidence(extent('colocation'));
  const colocated = new Set();
  const pair = (a, b) => key(...[a, b].sort());
  for (const members of vmss.values()) {
    const ordered = [...members];
    for (const [i, a] of ordered.entries()) {
      for (const b of ordered.slice(i + 1)) {
        assert(colocation.shows('SharesVmssWith', a, b) || colocation.shows('SharesVmssWith', b, a), 'Graph shows distinct Deployments observed on the same VM scale set');
        colocated.add(pair(a, b));
      }
    }
  }
  assert(colocated.size, 'Capture has two or more Deployments on the same VM scale set');
  const shownPairs = new Set(colocation.relationships.filter((edge) => kindOf(edge) === 'SharesVmssWith').map((edge) => pair(edge.source.value, edge.target.value)));
  assert.equal(shownPairs.size, colocated.size, 'Every colocated pair shares a captured VM scale set');
  console.log(`✓ Placement traces ${count(vmss)} Deployment/VMSS pairs and ${count(machines)} Deployment/VM pairs, including ${colocated.size} pairs of colocated Deployments.`);
}
