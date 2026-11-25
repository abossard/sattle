import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import { isIP } from 'node:net';
import { join } from 'node:path';
import { verifyKubernetes } from './verify-tutorial-k8s.mjs';

const [capture, output, engine, definitions] = process.argv.slice(2);
const json = (path) => JSON.parse(readFileSync(path, 'utf8'));
const ndjson = (path) => readFileSync(path, 'utf8').split('\n').filter(Boolean).map((line) => JSON.parse(line));
const extent = (graph, dir = output) => json(join(dir, `${graph}.final.snapshot.json`)).extent;
const patches = (graph) => json(join(output, `${graph}.patches.json`));
const name = (id) => id.value.split('.').at(-1);
const property = (node, key) => node?.properties.find((entry) => name(entry.property) === key)?.value.value;
const armId = (value) => value?.toLowerCase();
const groupId = (arm) => arm.match(/^\/subscriptions\/[^/]+\/resourcegroups\/[^/]+/)?.[0];
const stateChange = (values) => values.filter((value, index) => index === 0 || value !== values[index - 1]);
const isState = (value) => value === 'Healthy' || value === 'Degraded';
const exactNode = (graph, kind, arm) => graph.entities.find((node) => name(node.type) === kind && armId(property(node, 'armId')) === arm);

const samples = ndjson(join(capture, 'azure-metrics/aks.ndjson'));
const imported = new Map(['resources', 'resourcecontainers'].flatMap((source) =>
  ndjson(join(capture, `arg/${source}.ndjson`)).map(({ payload }) => [armId(payload.id), payload])));
assert(samples.length > 0, 'Fixture contains Azure Monitor samples');
const arm = armId(samples[0].meta.resourceId);
const monitoredGroup = groupId(arm);
assert.equal(armId(imported.get(arm)?.type), 'microsoft.containerservice/managedclusters', 'Azure Monitor joins an AKS inventory row');
assert(imported.has(monitoredGroup), 'Fixture includes the monitored cluster resource group');
const cpu = 'apiserver_cpu_usage_percentage';
const readings = [];
let last = Date.parse(samples[0].payload.value[0].timeseries[0].data[0].timeStamp) - 60_000;
let state = 'Healthy';
let streak = 0;
const expected = ['Healthy'];
const changes = [];

// Independently apply the tutorial rule to the measured readings in sample-time order.
for (const row of samples) {
  const [metric] = row.payload.value;
  const [point] = metric.timeseries[0].data;
  assert(armId(row.meta.resourceId) === arm, 'ARG inventory joins every Azure Monitor envelope');
  assert.equal(metric.name.value, cpu);
  assert.equal(Date.parse(point.timeStamp) - last, 60_000, 'CPU samples are consecutive one-minute readings');
  assert(Number.isFinite(point.average), 'CPU sample carries a numeric average');
  readings.push(point.average);
  last = Date.parse(point.timeStamp);
  assert.equal(Date.parse(row.capturedAt) - last, 60_000, 'Replay delivers the sample one minute after measurement');
  const extendsStreak = state === 'Healthy' ? point.average >= 8 : point.average <= 7;
  streak = extendsStreak ? streak + 1 : 0;
  if (streak === (state === 'Healthy' ? 2 : 3)) {
    const next = state === 'Healthy' ? 'Degraded' : 'Healthy';
    changes.push({ measuredAt: point.timeStamp, before: state, after: next });
    state = next;
    expected.push(state);
    streak = 0;
  }
}
assert(expected.length >= 3, 'Use a real capture with both an alarm and recovery under the tutorial rule');

for (const graph of ['master', 'inventory', 'tree', 'policy', 'signals', 'health', 'workloadHealth', 'report']) {
  assert(extent(graph).entities.length > 0, `${graph} has entities after replay`);
}
const master = extent('master');
const resourceNodes = master.entities.filter((node) => name(node.type) === 'Resource');
const resources = new Map(resourceNodes.map((node) => [armId(property(node, 'armId')), node]));
assert.equal(resourceNodes.length, imported.size, 'Master holds one resource per imported ARM ID');
const edges = new Map();
for (const edge of master.relationships) {
  const key = `${name(edge.type)}/${edge.source.value}`;
  const targets = edges.get(key) ?? new Set();
  targets.add(edge.target.value);
  edges.set(key, targets);
}
const out = (relation, source) => edges.get(`${relation}/${source.id.value}`) ?? new Set();
const connected = (relation, source, target) => !!(source && target && out(relation, source).has(target.id.value));
const nodes = new Map(master.entities.map((node) => [node.id.value, node]));
const mapped = (relation, owner, type, key, value) => [...out(relation, owner)].map((id) => nodes.get(id))
  .find((node) => node && name(node.type) === type && property(node, key) === value);

for (const [id, row] of imported) {
  const node = resources.get(id);
  assert(node, 'Master represents an imported resource or container');
  assert(property(node, 'name') === row.name, 'Resource name is copied from its source row');
  assert(property(node, 'resourceType') === armId(row.type), 'Resource type matches its source row');
  for (const key of ['location', 'resourceGroup', 'subscriptionId']) {
    if (row[key]) assert(property(node, key) === row[key], 'Master retains the imported resource location and scope');
  }
  const group = groupId(id);
  const parent = id === group ? id.split('/resourcegroups/')[0] : group;
  if (resources.has(parent)) assert(connected('AzureContains', resources.get(parent), node), 'Imported resource and group are connected in Azure containment');
}
console.log(`✓ Master covers all ${resources.size} imported resources and containers with ${master.relationships.length} relationships.`);

const normalizeAddress = (value) => isIP(value) === 6 ? new URL(`http://[${value}]/`).hostname.slice(1, -1) : value;
const networkChecks = { privateLinks: 0, interfaces: 0, ipAddresses: 0, configuredNames: 0, zoneLinks: 0, subnets: 0 };
function verifyAddress(owner, value) {
  const node = mapped('HasIpAddress', owner, 'IpAddress', 'address', normalizeAddress(value));
  assert(node, 'Observed IP configuration has an address node linked from its resource');
  networkChecks.ipAddresses++;
  return node;
}
function verifyName(owner, value, address) {
  const host = mapped('HasDnsName', owner, 'DnsName', 'dnsName', value.trim().toLowerCase().replace(/\.$/, ''));
  assert(host, 'Observed configuration has a linked DNS name');
  if (address) {
    assert(connected('ConfiguredAddress', host, address), 'Configured DNS name points to the address on the same NIC configuration');
    networkChecks.configuredNames++;
  }
}
function verifySubnet(owner, id, relation) {
  assert(mapped(relation, owner, 'Subnet', 'armId', armId(id)), 'Observed subnet reference connects to a typed subnet');
  networkChecks.subnets++;
}
function verifyIpConfigurations(owner, configurations) {
  for (const { properties: ip } of configurations ?? []) {
    const address = ip?.privateIPAddress ? verifyAddress(owner, ip.privateIPAddress) : undefined;
    for (const host of ip?.privateLinkConnectionProperties?.fqdns ?? []) verifyName(owner, host, address);
    if (ip?.subnet?.id) verifySubnet(owner, ip.subnet.id, 'UsesSubnet');
  }
}
for (const [id, { type, properties: p }] of imported) {
  if (!p) continue;
  const source = resources.get(id);
  switch (armId(type)) {
    case 'microsoft.network/privateendpoints':
      for (const link of [...(p.privateLinkServiceConnections ?? []), ...(p.manualPrivateLinkServiceConnections ?? [])]) {
        const target = resources.get(armId(link.properties?.privateLinkServiceId));
        if (!target) continue;
        assert(connected('PrivatelyConnectsTo', source, target), 'Private endpoint connects to its observed Azure target');
        networkChecks.privateLinks++;
      }
      for (const ref of p.networkInterfaces ?? []) {
        const target = resources.get(armId(ref.id));
        if (!target) continue;
        assert(connected('AttachedNetworkInterface', source, target), 'Private endpoint connects to its observed network interface');
        networkChecks.interfaces++;
      }
      if (p.subnet?.id) verifySubnet(source, p.subnet.id, 'UsesSubnet');
      for (const config of p.customDnsConfigs ?? []) {
        for (const ip of config.ipAddresses ?? []) {
          const address = verifyAddress(source, ip);
          if (config.fqdn) verifyName(source, config.fqdn, address);
        }
      }
      verifyIpConfigurations(source, p.ipConfigurations);
      break;
    case 'microsoft.network/networkinterfaces':
      verifyIpConfigurations(source, p.ipConfigurations);
      break;
    case 'microsoft.network/publicipaddresses':
      if (p.ipAddress) verifyAddress(source, p.ipAddress);
      break;
    case 'microsoft.network/virtualnetworks':
      for (const subnet of p.subnets ?? []) if (subnet.id) verifySubnet(source, subnet.id, 'ContainsSubnet');
      break;
    case 'microsoft.network/privatednszones/virtualnetworklinks': {
      const network = resources.get(armId(p.virtualNetwork?.id));
      const zone = resources.get(id.split('/virtualnetworklinks/')[0]);
      if (network) assert(connected('LinksVirtualNetwork', source, network), 'A Private DNS link points to its observed virtual network');
      if (zone) assert(connected('HasDnsZone', source, zone), 'A Private DNS link points to its observed zone');
      if (network && zone) networkChecks.zoneLinks++;
      break;
    }
  }
}
console.log(`✓ Network evidence includes ${networkChecks.privateLinks} private link targets, ${networkChecks.interfaces} endpoint NIC attachments, ${networkChecks.ipAddresses} IP observations, ${networkChecks.configuredNames} configured name/address pairs, ${networkChecks.zoneLinks} linked DNS zones and ${networkChecks.subnets} subnet links.`);

const cluster = resources.get(arm);
const meter = master.entities.find((node) => name(node.type) === 'Meter' && armId(property(node, 'armId')) === arm && property(node, 'metric') === cpu);
assert(cluster && meter, 'Master carries the inventory node and the Azure Monitor meter');
assert(connected('MeterOf', meter, cluster), 'Master connects the meter to the inventoried resource');
const batches = ndjson(join(output, 'master.windows.ndjson')).filter((batch) => batch.property.split('.').at(-1) === 'reading');
const measurements = batches
  .filter((batch) => batch.ids.includes(meter.id.value))
  .map((batch) => batch.values[batch.ids.indexOf(meter.id.value)]);
assert.deepEqual(measurements, readings, 'Master preserves the Azure measurements in order');
console.log(`✓ Master attached ${samples.length} Azure Monitor readings to the inventory resource.`);

const otherMetrics = join(capture, 'azure-metrics/other.ndjson');
if (existsSync(otherMetrics)) {
  const metricKey = (id, metric) => `${id}\0${metric}`;
  const expectedMeters = new Map();
  for (const row of [...samples, ...ndjson(otherMetrics)]) {
    const id = armId(row.meta.resourceId);
    assert(resources.has(id), 'Every Azure Monitor response joins an imported resource');
    for (const metric of row.payload.value) {
      const key = metricKey(id, metric.name.value);
      for (const series of metric.timeseries) {
        for (const point of series.data) {
          const reading = point.average ?? point.total;
          if (!Number.isFinite(reading)) continue;
          const minute = Math.floor(Date.parse(point.timeStamp) / 60_000) * 60_000;
          assert(Number.isFinite(minute), 'Each imported metric reading has a valid sample time');
          if (!expectedMeters.has(key)) expectedMeters.set(key, new Map());
          const windows = expectedMeters.get(key);
          const previous = windows.get(minute) ?? { sum: 0, count: 0 };
          windows.set(minute, { sum: previous.sum + reading, count: previous.count + 1 });
        }
      }
    }
  }
  const meters = master.entities.filter((node) => name(node.type) === 'Meter');
  assert.equal(meters.length, expectedMeters.size, 'Master carries one meter per imported resource and metric');
  const windowsByMeter = new Map();
  for (const { ids, values, t } of batches) {
    for (const [i, id] of ids.entries()) {
      if (!windowsByMeter.has(id)) windowsByMeter.set(id, new Map());
      windowsByMeter.get(id).set(t, values[i]);
    }
  }
  const firstExpected = Math.min(...[...expectedMeters.values()].flatMap((windows) => [...windows.keys()]));
  const firstObserved = Math.min(...batches.map((batch) => batch.t));
  let windows = 0;
  for (const node of meters) {
    const id = armId(property(node, 'armId'));
    const expected = expectedMeters.get(metricKey(id, property(node, 'metric')));
    const observed = windowsByMeter.get(node.id.value);
    assert(expected && observed, 'Master meter matches an imported Azure Monitor series');
    assert(connected('MeterOf', node, resources.get(id)), 'Master attaches the meter to its actual imported resource');
    assert.equal(observed.size, expected.size, 'Meter has every imported one-minute window');
    for (const [minute, { sum, count }] of expected) {
      const actual = observed.get((minute - firstExpected) / 1000 + firstObserved);
      const mean = sum / count;
      assert(Math.abs(actual - mean) <= Number.EPSILON * Math.max(1, Math.abs(mean)) * 8, 'Meter preserves the observed average in its actual time window');
    }
    windows += observed.size;
  }
  console.log(`✓ Master verified ${meters.length} matched Azure Monitor series and all ${windows} one-minute windows.`);
}

const tree = extent('tree');
const group = exactNode(tree, 'Group', monitoredGroup);
const child = exactNode(tree, 'Cluster', arm);
assert(group && child, 'Tree includes the monitored cluster and its group');
assert(tree.relationships.some((edge) => name(edge.type) === 'Contains' && edge.source.value === group.id.value && edge.target.value === child.id.value), 'Tree puts the monitored cluster under its imported group');
const inherited = extent('policy').entities.find((node) => node.id.value === child.id.value);
assert(property(group, 'policy'), 'The resource group declares a policy');
assert.equal(property(inherited, 'policy'), property(group, 'policy'), 'The cluster inherits its group policy');
console.log('✓ The tree contains the AKS cluster and rolls its group policy down.');

const health = extent('health');
const healthPatches = patches('health');
// The Canvas hides support and duplicate roll-up links on first open. Every modeled
// parent and child must still connect through the ontology served with this replay.
function visibleHealthLinks(graph, snapshot) {
  const ontology = json(join(output, `${graph}.ontology.json`));
  const visibleTypes = new Set(ontology.relationshipTypes
    .filter((type) => !type.rollup && ['composition', 'dependency'].includes(type.kind))
    .map((type) => type.id.value));
  return snapshot.relationships.filter((edge) => visibleTypes.has(edge.type.value));
}
assert(health.relationships.length > 0 && visibleHealthLinks('health', health).length === health.relationships.length,
  'The default graph view shows the resource group to cluster hierarchy');
const healthTargets = [['HealthCluster', arm, 'AKS cluster', '3'], ['HealthGroup', monitoredGroup, 'Resource group', '2']];
for (const [type, id] of healthTargets) {
  const node = exactNode(health, type, id);
  assert(node, `Health creates ${type}`);
  const states = healthPatches.flatMap((patch) => patch.edits
    .filter((edit) => edit.id.value === node.id.value)
    .map((edit) => property(edit.after, 'state'))).filter(isState);
  assert.deepEqual(stateChange(states), expected, `${type} mirrors the measured alarm and recovery sequence`);
  assert.equal(property(node, 'state'), state, `${type} has the final measured state`);
  console.log(`✓ ${type} followed ${expected.length - 1} alarm and recovery transitions.`);
}

const workload = extent('workloadHealth');
const workloadPatches = patches('workloadHealth');
const role = (node) => property(node, 'role');
const ofRole = (kind) => workload.entities.filter((node) => role(node) === kind);
const [workloadRoot] = ofRole('Workload');
const flows = new Map(ofRole('Flow').map((node) => [property(node, 'scope'), node]));
assert.equal(ofRole('Workload').length, 1, 'One modeled workload');
assert.equal(flows.size, 2, 'Two independent user flows');
assert.equal(property(flows.get('deploy'), 'name'), 'Deploy to AKS');
assert.equal(property(flows.get('serve'), 'name'), 'Serve traffic to Services');
const workloadLinked = (type, from, to) => workload.relationships.some((edge) =>
  name(edge.type) === type && edge.source.value === from.id.value && edge.target.value === to.id.value);
const checkNodes = workload.entities.filter((node) => !['Workload', 'Flow'].includes(role(node)));
const severity = ['Healthy', 'Unknown', 'Degraded', 'Unhealthy'];
const worst = (nodes) => severity[Math.max(...nodes.map((node) => severity.indexOf(property(node, 'state'))))];
for (const [scope, flow] of flows) {
  assert(workloadLinked('IncludesFlow', workloadRoot, flow), 'The workload includes the flow');
  const checks = checkNodes.filter((node) => property(node, 'scope') === scope);
  assert(checks.length > 0 && checks.every((check) => workloadLinked('RequiresCheck', flow, check)), 'Only the owning flow requires its checks');
  assert.equal(property(flow, 'state'), worst(checks), 'The flow takes the worst required check');
  assert.equal(property(flow, 'ownState'), undefined, 'The flow has no direct measurement');
}
const rollupEdges = workload.relationships.filter((edge) => ['IncludesFlow', 'RequiresCheck'].includes(name(edge.type)));
assert.equal(rollupEdges.length, flows.size + checkNodes.length, 'Each flow and check has one parent');
assert.deepEqual(visibleHealthLinks('workloadHealth', workload), rollupEdges,
  'The default graph view connects the workload to both flows and every required check');
assert.equal(property(workloadRoot, 'state'), worst([...flows.values()]), 'The root takes the worst flow');
assert(checkNodes.every((node) => property(node, 'ownState') === property(node, 'state') && property(node, 'evidence')),
  'Every check has a state and describes its source evidence');

const kube = ndjson(join(capture, 'kubernetes/list.ndjson'));
const kubeKey = (cluster, namespace, n) => [cluster, namespace, n].join('/');
const readyByPod = new Map(kube.filter((row) => row.meta.list.kind === 'PodList').map(({ meta, payload }) => {
  const status = payload.status?.conditions?.find((condition) => condition.type === 'Ready')?.status;
  return [kubeKey(meta.cluster, payload.metadata.namespace, payload.metadata.name),
    status === 'True' ? true : status === 'False' ? false : undefined];
}));
const typed = (kind) => master.entities.filter((node) => name(node.type) === kind);
const sourceKey = (node) => kubeKey(property(node, 'cluster'), property(node, 'namespace'), property(node, 'name'));
for (const pod of typed('Pod')) assert.equal(property(pod, 'ready'), readyByPod.get(sourceKey(pod)), 'Pod readiness matches the capture');
const serviceChecks = new Map(ofRole('Service backends').map((node) => [property(node, 'sourceKey'), node]));
assert.equal(serviceChecks.size, typed('Service').length, 'Each discovered Service gets a backend check, including missing observations');
for (const service of typed('Service')) {
  const backends = master.relationships.filter((edge) => name(edge.type) === 'BacksService' && edge.target.value === service.id.value)
    .map((edge) => nodes.get(edge.source.value));
  const observed = backends.map((pod) => property(pod, 'ready'));
  const endpoints = out('HasServiceEndpoint', service).size;
  const expectedState = observed.includes(true) ? 'Healthy'
    : endpoints && observed.filter((value) => value === false).length >= endpoints ? 'Unhealthy' : 'Unknown';
  const check = serviceChecks.get(sourceKey(service));
  assert.equal(property(check, 'state'), expectedState, 'The service check preserves observed readiness and missing evidence');
  assert(backends.every((pod) => workloadLinked('ReadinessFromPod', check, pod)), 'The Service check points to its observed backing Pods');
}
assert([...serviceChecks.values()].some((node) => property(node, 'state') === 'Unknown'), 'The real capture exercises backend evidence gaps');

const apiChecks = new Map(ofRole('API CPU capacity').map((node) => [property(node, 'sourceKey'), node]));
const measuredCpu = apiChecks.get(arm);
const observedStates = (node) => stateChange(workloadPatches.flatMap((patch) => patch.edits
  .filter((edit) => edit.id.value === node.id.value)
  .map((edit) => property(edit.after, 'state'))).filter(isState));
assert.equal(apiChecks.size, tree.entities.filter((node) => name(node.type) === 'Cluster').length);
assert.deepEqual(observedStates(measuredCpu), expected, 'The API CPU leaf follows the measured hysteresis');
const cpuSignal = exactNode(extent('signals'), 'Signal', arm);
assert(cpuSignal && workloadLinked('CpuFromSignal', measuredCpu, cpuSignal), 'The CPU check points to the measured signal');
assert.equal(property(flows.get('deploy'), 'state'), worst([...apiChecks.values()]), 'The deploy flow depends only on API CPU checks');
const unmeasured = [...apiChecks.entries()].filter(([key]) => key !== arm);
assert(unmeasured.length > 0 && unmeasured.every(([, node]) => property(node, 'state') === 'Unknown'),
  'An imported cluster with no processor reading is Unknown');

const probes = ofRole('Azure ingress probe');
assert.equal(probes.length, 1, 'One LoadBalancer Service has a matched Azure probe in the capture');
const [probe] = probes;
const [ingressIp] = kube
  .filter(({ meta, payload }) => meta.list.kind === 'ServiceList' && payload.spec?.type === 'LoadBalancer')
  .flatMap(({ payload }) => (payload.status?.loadBalancer?.ingress ?? []).map((item) => item.ip))
  .filter(Boolean);
assert(ingressIp, 'The capture contains the Service ingress address');
const publicIp = [...imported.values()].find((row) => armId(row.type) === 'microsoft.network/publicipaddresses' && row.properties?.ipAddress === ingressIp);
assert(publicIp, 'The same address appears in an exported public IP');
const lb = [...imported.values()].find((row) => armId(row.type) === 'microsoft.network/loadbalancers' &&
  (row.properties?.frontendIPConfigurations ?? []).some((frontend) => armId(frontend.properties?.publicIPAddress?.id) === armId(publicIp.id)));
assert(lb, 'The load balancer references the public-IP resource');
assert.equal(armId(property(probe, 'sourceKey')), armId(lb.id), 'The probe is attached to the identified load balancer');
const vipMeter = typed('Meter').find((node) => armId(property(node, 'armId')) === armId(lb.id) && property(node, 'metric') === 'VipAvailability');
assert(vipMeter && workloadLinked('IngressFromMeter', probe, vipMeter), 'The serving check points to the matched Azure meter');
assert.equal(workload.relationships.length, rollupEdges.length + master.relationships.filter((edge) => name(edge.type) === 'BacksService').length + 2,
  'Every direct source link is tied to a captured observation');
const vip = ndjson(join(capture, 'azure-metrics/other.ndjson')).filter((row) => armId(row.meta.resourceId) === armId(lb.id))
  .flatMap((row) => row.payload.value.filter((metric) => metric.name.value === 'VipAvailability'))
  .flatMap((metric) => metric.timeseries.flatMap((series) => series.data))
  .filter((sample) => Number.isFinite(sample.average))
  .sort((a, b) => Date.parse(a.timeStamp) - Date.parse(b.timeStamp));
let probeState = 'Healthy', consecutive = 0;
const probeStates = [probeState];
for (const { average } of vip) {
  const high = probeState === 'Healthy';
  consecutive = (high ? average <= 98 : average >= 99) ? consecutive + 1 : 0;
  if (consecutive === (high ? 2 : 3)) {
    probeState = high ? 'Degraded' : 'Healthy';
    probeStates.push(probeState);
    consecutive = 0;
  }
}
assert(vip.length && probeStates.length >= 3, 'Real ingress probe readings include a decline and recovery');
assert.deepEqual(observedStates(probe), probeStates, 'The matched ingress probe follows measured availability windows');
assert(observedStates(flows.get('serve')).includes('Degraded'), 'The probe degrades the serving flow');
console.log(`✓ Workload model rolls ${checkNodes.length} evidence checks into two flows, with independent CPU and ingress probe history and explicit Unknowns.`);

const markdown = readFileSync(join(output, 'report.document.md'), 'utf8');
const report = json(join(output, 'report.document.json'));
assert.equal(report.blocks.filter((block) => block.kind === 'heading').length, 1, 'Report has a single heading block');
assert.equal(report.blocks.filter((block) => block.kind === 'header').length, 1, 'Report has one shared table header');
assert(markdown.includes(state), 'Rendered Markdown contains the current health state');
assert.equal(report.digest, `sha256:${createHash('sha256').update(markdown).digest('hex')}`);
const reportPatches = patches('report');
for (const [, id, type, order] of healthTargets) {
  const row = report.blocks.find((block) => block.kind === 'row' && block.b === type && block.order === `${order}/${id}`);
  assert(row, `Report has a row for ${type}`);
  assert.equal(row.c, state, `Report has the final state for ${type}`);
  const states = reportPatches.flatMap((patch) => patch.edits
    .filter((edit) => edit.id.value === row.id)
    .map((edit) => property(edit.after, 'c'))).filter(isState);
  assert.deepEqual(stateChange(states), expected, `Report row for ${type} follows measured health`);
}
for (const node of [workloadRoot, ...flows.values()]) {
  const label = property(node, 'name');
  const row = report.blocks.find((block) => block.kind === 'row' && block.a === label);
  assert(row, `Report has the workload summary for ${label}`);
  assert.equal(row.c, property(node, 'state'), `Report reflects the final state of ${label}`);
  const summaries = reportPatches.flatMap((patch) => patch.edits.filter((edit) => edit.id.value === row.id)
    .map((edit) => property(edit.after, 'c'))).filter((s) => severity.includes(s));
  const graphStates = workloadPatches.flatMap((patch) => patch.edits.filter((edit) => edit.id.value === node.id.value)
    .map((edit) => property(edit.after, 'state'))).filter((s) => severity.includes(s));
  assert.deepEqual(stateChange(summaries), stateChange(graphStates), `The report follows each state change of ${label}`);
}
console.log('✓ Report blocks follow the same transitions and render to matching Markdown.');

verifyKubernetes(capture, extent);

if (engine && definitions) {
  // The fold steps a minute window when the clock reaches its close: end plus the late horizon of 3 windows (Def. 6.4).
  // --no-flush keeps the clock at the cutoff instead of advancing it past the last record.
  const close = (measuredAt) => Date.parse(measuredAt) + 60_000 * (1 + 3);
  const checkpoints = changes.slice(0, 2).flatMap(({ measuredAt, before, after }) => [
    { cutoff: new Date(close(measuredAt) - 1).toISOString(), state: before },
    { cutoff: new Date(close(measuredAt)).toISOString(), state: after },
  ]);
  for (const [index, checkpoint] of checkpoints.entries()) {
    const dir = join(output, `checkpoint-${index}`);
    const replay = spawnSync('dotnet', [engine, 'replay', capture, '--defs', definitions, '--space', 'azureTutorial',
      '--lateness', '0s', '--late-horizon', '3', '--no-flush', '--until', checkpoint.cutoff, '--out', dir], { encoding: 'utf8' });
    assert.equal(replay.status, 0, 'Checkpoint replay succeeded');
    const atCheckpoint = extent('health', dir);
    const observed = healthTargets.map(([type, id]) => property(exactNode(atCheckpoint, type, id), 'state'));
    assert.deepEqual(observed, [checkpoint.state, checkpoint.state], `Health at cutoff ${checkpoint.cutoff}`);
  }
  console.log('✓ The first alarm holds for two high readings and recovery waits for three low readings.');
}
