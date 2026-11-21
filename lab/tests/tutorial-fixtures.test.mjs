import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const builder = fileURLToPath(new URL('../fixtures/build-tutorial.sh', import.meta.url));
const capturedAt = '2026-01-01T12:30:00Z';
const subscription = '16160500-1234-433f-ab90-2954d9dbbf81';
const group = 'inventory-group-sensitive';
const cluster = 'example-cluster-sensitive';
const groupId = `/subscriptions/${subscription}/resourceGroups/${group}`;
const clusterId = `${groupId}/providers/Microsoft.ContainerService/managedClusters/${cluster}`;
const clusterType = 'microsoft.containerservice/managedclusters';
const metric = 'apiserver_cpu_usage_percentage';
const storageId = `${groupId}/providers/Microsoft.Storage/storageAccounts/storage-private`;
const endpointId = `${groupId}/providers/Microsoft.Network/privateEndpoints/endpoint-private`;
const nicId = `${groupId}/providers/Microsoft.Network/networkInterfaces/interface-private`;
const vmssId = `${groupId}/providers/Microsoft.Compute/virtualMachineScaleSets/vmss-private`;
const dnsName = 'private-host.corp.example';
const privateIp = '172.22.7.9';

function capture(t, { match = true } = {}) {
  const root = mkdtempSync(join(tmpdir(), 'sattle-tutorial-fixture-test-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const raw = join(root, 'raw');
  const out = join(root, 'tutorial');
  const write = (path, source, payloads) => {
    const file = join(raw, path);
    mkdirSync(dirname(file), { recursive: true });
    writeFileSync(file, payloads.map(({ meta, payload }, index) => JSON.stringify({
      v: 1, source, capturedAt, seq: index + 1, meta, payload,
    })).join('\n') + '\n');
  };
  write('arg/resources.ndjson', 'arg', [
    { payload: { id: storageId, name: 'storage-private', type: 'microsoft.storage/storageaccounts' } },
    { payload: { id: clusterId, name: cluster, type: clusterType, resourceGroup: group, subscriptionId: subscription, properties: { nodeResourceGroup: group }, tags: { secret: 'dropped-property' } } },
    { payload: { id: endpointId, name: 'endpoint-private', type: 'microsoft.network/privateendpoints', properties: {
      networkInterfaces: [{ id: nicId, unrelated: 'dropped-field' }],
      privateLinkServiceConnections: [{ properties: { privateLinkServiceId: storageId, secret: 'dropped-field' } }],
      customDnsConfigs: [{ fqdn: `${dnsName.toUpperCase()}.`, ipAddresses: [privateIp] }],
    } } },
    { payload: { id: nicId, name: 'interface-private', type: 'microsoft.network/networkinterfaces', properties: {
      privateEndpoint: { id: endpointId },
      ipConfigurations: [{ properties: { privateIPAddress: privateIp, privateLinkConnectionProperties: { fqdns: [dnsName] } } }],
    } } },
    { payload: { id: vmssId, name: 'vmss-private', type: 'microsoft.compute/virtualmachinescalesets' } },
    { payload: { id: `${groupId}/providers/Microsoft.Network/networkInterfaces/no-name`, type: 'microsoft.network/networkinterfaces' } },
  ]);
  write('arg/resourcecontainers.ndjson', 'arg', [
    { payload: { id: `/subscriptions/${subscription}`, name: 'private-subscription', type: 'microsoft.resources/subscriptions' } },
    { payload: { id: groupId, name: group, type: 'microsoft.resources/subscriptions/resourcegroups', resourceGroup: group, subscriptionId: subscription } },
    { payload: { id: `/subscriptions/${subscription}/resourceGroups/unrelated-private`, name: 'unrelated-private', type: 'microsoft.resources/subscriptions/resourcegroups' } },
  ]);
  const point = (minute, value) => ({ timeStamp: `2026-01-01T12:${String(minute).padStart(2, '0')}:00Z`, average: value });
  const expected = [point(10, 8), point(11, 9), point(12, 7), point(13, 6), point(14, 7)];
  write('azure-metrics/aks.ndjson', 'azure-metrics', [{
    meta: { resourceId: (match ? clusterId : `${clusterId}-missing`).toUpperCase() },
    payload: { value: [{ name: { value: metric }, timeseries: [{ data: [point(1, 20), point(2, 30), ...expected.toReversed(), point(15, null)] }] }] },
  }]);
  const otherMetric = 'Transactions';
  const otherExpected = [point(10, 1.5), { timeStamp: point(13).timeStamp, total: 14, maximum: 8 }];
  const otherPayload = { value: [{ name: { value: otherMetric }, timeseries: [{
    data: [otherExpected[1], point(8, 20), otherExpected[0], otherExpected[0], point(11, null), point(15, 30)],
  }] }] };
  write('azure-metrics/storage.ndjson', 'azure-metrics', [
    { meta: { resourceId: storageId }, payload: otherPayload },
    { meta: { resourceId: `${storageId}-missing` }, payload: otherPayload },
  ]);
  const ns = 'internal-app-private';
  const clusterLabel = 'cilium-cluster-private';
  const nodeName = 'node-private';
  const deployment = 'deployment-private';
  const replicaSet = `${deployment}-97ba43`;
  const service = 'service-private';
  const caller = 'caller-private';
  const callee = 'callee-private';
  const kubeUid = (n) => `11111111-1111-4111-a111-11111111111${n}`;
  const object = (kind, name, uid, rest = {}, info = {}) => ({
    meta: { list: { kind: `${kind}List` } },
    payload: { ...rest, metadata: { name, uid: kubeUid(uid), namespace: ns, ...info } },
  });
  const owner = (kind, name, uid) => ({ kind, name, uid: kubeUid(uid), controller: true });
  write('kubernetes/list.ndjson', 'kubernetes-list', [
    object('Node', nodeName, 1, { spec: { providerID: `azure://${vmssId}/virtualMachines/0` } }),
    object('Deployment', deployment, 2, { spec: { replicas: 1, template: { metadata: { labels: { 'internal-label-private': 'internal-value-private' } } } } }),
    object('ReplicaSet', replicaSet, 3, {}, { ownerReferences: [owner('Deployment', deployment, 2)] }),
    object('Pod', caller, 4, { spec: { nodeName } }, { ownerReferences: [owner('ReplicaSet', replicaSet, 3)] }),
    object('Pod', callee, 5, { spec: { nodeName } }, { ownerReferences: [owner('ReplicaSet', replicaSet, 3)] }),
    object('Service', service, 6, { spec: { selector: { 'internal-label-private': 'internal-value-private' } } }),
    object('EndpointSlice', 'slice-private', 7, { endpoints: [{ addresses: [privateIp], targetRef: { ...owner('Pod', callee, 5), namespace: ns } }] },
      { labels: { 'kubernetes.io/service-name': service } }),
  ]);
  const endpoint = (pod_name) => ({ namespace: ns, pod_name, cluster_name: clusterLabel });
  write('hubble/flows.ndjson', 'hubble', [
    { payload: { flow: { source: endpoint(caller), destination: endpoint(callee), time: '2026-01-01T12:11:10.123Z', verdict: 'FORWARDED' } } },
    { payload: { flow: { source: endpoint(callee), destination: endpoint(caller), time: '2026-01-01T12:12:10.456Z', verdict: 'FORWARDED', is_reply: true } } },
    { payload: { flow: { source: endpoint(callee), destination: endpoint('missing-pod-private'), time: '2026-01-01T12:13:10Z', verdict: 'FORWARDED' } } },
    { payload: { flow: { source: endpoint(callee), destination: endpoint(callee), time: '2026-01-01T12:13:20Z', verdict: 'FORWARDED' } } },
    { payload: { flow: { source: endpoint(caller), destination: endpoint(callee), time: '2026-01-01T12:14:10.789Z', verdict: 'DROPPED' } } },
  ]);
  return { raw, out, expected, otherMetric, otherExpected };
}

test('tutorial builder keeps the full valid inventory, scrubbed network references and measured streak', (t) => {
  const { raw, out, expected, otherMetric, otherExpected } = capture(t);
  const run = spawnSync('bash', [builder, raw, out], { encoding: 'utf8' });
  assert.equal(run.status, 0, run.stderr);
  assert.match(run.stdout, /OK: no identifying value/);
  const read = (file) => {
    const contents = readFileSync(join(out, file), 'utf8');
    assert.doesNotMatch(contents, /inventory-group-sensitive|example-cluster-sensitive|private-subscription|dropped-property|dropped-field|16160500|storage-private|endpoint-private|interface-private|vmss-private|unrelated-private|private-host\.corp\.example|172\.22\.7\.9|internal-app-private|cilium-cluster-private|node-private|deployment-private|service-private|caller-private|callee-private|slice-private|internal-label-private|internal-value-private/i);
    return contents.trim().split('\n').map((line) => JSON.parse(line));
  };
  const inventory = read('arg/resources.ndjson');
  const resourceOf = (type) => inventory.find((row) => row.payload.type === type);
  const resource = resourceOf(clusterType);
  const storage = resourceOf('microsoft.storage/storageaccounts');
  const endpoint = resourceOf('microsoft.network/privateendpoints');
  const nic = resourceOf('microsoft.network/networkinterfaces');
  const containers = read('arg/resourcecontainers.ndjson');
  const samples = read('azure-metrics/aks.ndjson');
  const other = read('azure-metrics/other.ndjson');
  assert.equal(inventory.length, 5);
  assert.equal(resource.capturedAt, '2026-01-01T12:10:00.000Z');
  assert.equal(resource.meta.originalCapturedAt, capturedAt);
  assert.equal(containers.length, 3);
  assert([...inventory, ...containers].every((row) => row.capturedAt === resource.capturedAt));
  assert.equal(resource.payload.properties.nodeResourceGroup, resource.payload.resourceGroup);
  const endpointProps = endpoint.payload.properties;
  const nicProps = nic.payload.properties;
  assert.equal(endpointProps.networkInterfaces[0].id, nic.payload.id);
  assert.equal(endpointProps.privateLinkServiceConnections[0].properties.privateLinkServiceId, storage.payload.id);
  assert.equal(nicProps.privateEndpoint.id, endpoint.payload.id);
  const ipConfig = nicProps.ipConfigurations[0].properties;
  assert.equal(endpointProps.customDnsConfigs[0].ipAddresses[0], ipConfig.privateIPAddress);
  assert.match(ipConfig.privateIPAddress, /^10\./);
  assert.equal(endpointProps.customDnsConfigs[0].fqdn, ipConfig.privateLinkConnectionProperties.fqdns[0]);
  assert.match(endpointProps.customDnsConfigs[0].fqdn, /\.example\.invalid$/);
  assert.deepEqual(samples.map((row) => row.payload.value[0].timeseries[0].data[0]), expected);
  for (const [index, row] of samples.entries()) {
    assert.equal(row.meta.resourceId.toLowerCase(), resource.payload.id.toLowerCase());
    assert.equal(row.meta.originalCapturedAt, capturedAt);
    assert.equal(row.meta.tutorialClock, 'sample-time-plus-one-minute');
    assert.equal(Date.parse(row.capturedAt) - Date.parse(expected[index].timeStamp), 60_000);
  }
  assert.deepEqual(other.map((row) => row.payload.value[0].timeseries[0].data[0]), otherExpected);
  for (const [index, row] of other.entries()) {
    assert.equal(row.meta.resourceId.toLowerCase(), storage.payload.id.toLowerCase());
    assert.equal(row.payload.value[0].name.value, otherMetric);
    assert.equal(Date.parse(row.capturedAt) - Date.parse(otherExpected[index].timeStamp), 60_000);
  }
  const kube = read('kubernetes/list.ndjson');
  assert.equal(kube.length, 7);
  const kubernetes = (kind) => kube.find((row) => row.meta.list.kind === `${kind}List`).payload;
  const [caller, callee] = kube.filter((row) => row.meta.list.kind === 'PodList').map((row) => row.payload);
  const deployment = kubernetes('Deployment');
  const replicaSet = kubernetes('ReplicaSet');
  const node = kubernetes('Node');
  assert.equal(caller.metadata.ownerReferences[0].uid, replicaSet.metadata.uid);
  assert.equal(caller.metadata.ownerReferences[0].name, replicaSet.metadata.name);
  assert.equal(replicaSet.metadata.ownerReferences[0].uid, deployment.metadata.uid);
  assert.equal(replicaSet.metadata.ownerReferences[0].name, deployment.metadata.name);
  assert.equal(replicaSet.metadata.name.slice(0, replicaSet.metadata.name.lastIndexOf('-')), deployment.metadata.name);
  assert.equal(caller.spec.nodeName, node.metadata.name);
  const vmss = resourceOf('microsoft.compute/virtualmachinescalesets');
  assert.equal(node.spec.providerID.split('/virtualMachines/')[0], `azure://${vmss.payload.id}`);
  const service = kubernetes('Service');
  const slice = kubernetes('EndpointSlice');
  assert.deepEqual(service.spec.selector, deployment.spec.template.metadata.labels);
  assert.equal(slice.metadata.labels['kubernetes.io/service-name'], service.metadata.name);
  assert.equal(slice.endpoints[0].targetRef.uid, callee.metadata.uid);
  assert.equal(slice.endpoints[0].targetRef.name, callee.metadata.name);
  const calls = read('hubble/flows.ndjson');
  assert.equal(calls.length, 2);
  const [reply, drop] = calls.map((row) => row.payload.flow);
  assert.equal(reply.time, '2026-01-01T12:12:10.456Z');
  assert.equal(reply.is_reply, true);
  assert.equal(reply.destination.pod_name, caller.metadata.name);
  assert.equal(reply.source.pod_name, callee.metadata.name);
  assert.equal(reply.source.cluster_name, kube[0].meta.cluster);
  assert.equal(reply.source.namespace, caller.metadata.namespace);
  assert.equal(drop.verdict, 'DROPPED');
  assert.equal(drop.source.pod_name, caller.metadata.name);
  assert.equal(drop.destination.pod_name, callee.metadata.name);
});

test('tutorial builder leaves the output absent when Azure metrics have no inventory join', (t) => {
  const { raw, out } = capture(t, { match: false });
  const run = spawnSync('bash', [builder, raw, out], { encoding: 'utf8' });
  assert.equal(run.status, 1);
  assert.match(run.stderr, /matching Resource Graph resource/);
  assert.equal(existsSync(out), false);
});
