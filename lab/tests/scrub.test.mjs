// Tests for the fixture scrubber through its command line (`scrub.mjs build|verify <capture> <fixtures>`).
// The capture is synthetic: every "real" value below is made up. Run: node --test lab/tests/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const FIXTURES_DIR = join(dirname(fileURLToPath(import.meta.url)), '..', 'fixtures');

// ---------- the synthetic capture: all identifying values are made up ----------
const REAL = {
  sub: 'aaaaaaaa-1111-4222-8333-444444444444',
  tenant: 'bbbbbbbb-1111-4222-8333-555555555555',
  user: 'jane.doe@fabrikam.test',
  userOid: 'cccccccc-1111-4222-8333-666666666666',
  rg: 'zq-fakerg',
  nodeRg: 'MC_zq-fakerg_aks-fakecluster_swedencentral',
  cluster: 'aks-fakecluster',
  node: 'aks-pool1-12345678-vmss000000',
  vmss: 'aks-pool1-12345678-vmss',
  internalId: '0123456789abcdef0123456789abcdef',
  roleAssignmentId: 'fedcba9876543210fedcba9876543210',
  principalHex: '1111222233334444aaaabbbbccccdddd',
  machineId: 'deadbeefdeadbeefdeadbeefdeadbeef',
  orgTag: 'ZQCORP-4471',
  claimRh: 'rhvalue-QxZ91kLmNo',
  claimFtd: 'ftdvalue-AbCdEf123456',
  claimTcdt: '1788888888',
  ip: '10.99.88.77',
  callerIp: '203.0.113.45',
  vmName: 'memory', // a resource whose name is also an ordinary word used in metric names
  boxName: 'status', // also a JSON key
  opName: 'write',
};
const armId = (rg, tail, sub = REAL.sub) => `/subscriptions/${sub}/resourceGroups/${rg}/providers/${tail}`;
const CLUSTER_ID = armId(REAL.rg, `Microsoft.ContainerService/managedClusters/${REAL.cluster}`);
const VMSS_VM_ID = armId(REAL.nodeRg, `Microsoft.Compute/virtualMachineScaleSets/${REAL.vmss}/virtualMachines/0`);
const MEMORY_ID = armId(REAL.rg, `Microsoft.Compute/virtualMachines/${REAL.vmName}`);

const env = (source, key, payload, meta) => ({ v: 1, source, capturedAt: '2026-01-01T00:00:00.000Z', seq: 0, key, ...(meta ? { meta } : {}), payload });
const arg = (table, payload) => env('arg', `arg/${table}/${payload.id}`, payload, { query: table, kql: table, page: 0, pageSize: 100 });

const CAPTURE = {
  'arg/resources.ndjson': [
    arg('resources', { id: CLUSTER_ID, name: REAL.cluster, resourceGroup: REAL.rg, subscriptionId: REAL.sub, tenantId: REAL.tenant, type: 'microsoft.containerservice/managedclusters', location: 'swedencentral', tags: { Org: REAL.orgTag }, properties: { internalId: REAL.internalId, nodeResourceGroup: REAL.nodeRg } }),
    arg('resources', { id: MEMORY_ID, name: REAL.vmName, resourceGroup: REAL.rg, subscriptionId: REAL.sub, tenantId: REAL.tenant, type: 'microsoft.compute/virtualmachines', location: 'swedencentral', tags: {}, properties: {} }),
    arg('resources', { id: armId(REAL.rg, `Microsoft.Storage/storageAccounts/${REAL.boxName}`), name: REAL.boxName, resourceGroup: REAL.rg, subscriptionId: REAL.sub, tenantId: REAL.tenant, type: 'microsoft.storage/storageaccounts', location: 'swedencentral', tags: {}, properties: {} }),
    arg('resources', { id: armId(REAL.rg, `Microsoft.Network/networkSecurityGroups/${REAL.opName}`), name: REAL.opName, resourceGroup: REAL.rg, subscriptionId: REAL.sub, tenantId: REAL.tenant, type: 'microsoft.network/networksecuritygroups', location: 'swedencentral', tags: {}, properties: {} }),
  ],
  'arg/resourcecontainers.ndjson': [
    arg('resourcecontainers', { id: `/subscriptions/${REAL.sub}/resourceGroups/${REAL.rg}`, name: REAL.rg, resourceGroup: REAL.rg, subscriptionId: REAL.sub, tenantId: REAL.tenant, type: 'microsoft.resources/subscriptions/resourcegroups', tags: { Org: REAL.orgTag }, properties: {} }),
  ],
  'azure-metrics/aks.ndjson': [
    env('azure-metrics', 'azure-metrics/sha256:0a1b2c3d4e5f60718293a4b5c6d7e8f9', {
      namespace: 'Microsoft.ContainerService/managedClusters',
      value: ['node_memory_working_set_percentage', 'node_cpu_usage_percentage', 'node_disk_usage_percentage'].map((m) => ({ id: `${CLUSTER_ID}/providers/Microsoft.Insights/metrics/${m}`, name: { value: m, localizedValue: m }, resourceGroup: REAL.rg, type: 'Microsoft.Insights/metrics', timeseries: [{ data: [{ average: 1, timeStamp: '2026-01-01T00:00:00Z' }] }] })),
    }, { resource: 'aks', resourceId: CLUSTER_ID, metrics: ['node_memory_working_set_percentage'] }),
  ],
  'prometheus/range.ndjson': ['cilium_forward_rate', 'container_memory_working_set_bytes'].map((name) => env('prometheus', `prometheus/sha256:${name.length}0a1b2c3d4e5f60718293a4b5c6d7e8`, {
    status: 'success', data: { resultType: 'matrix', result: [{ metric: { __name__: name, cluster: REAL.cluster, instance: REAL.node }, values: [[1, '2']] }] },
  }, { name, query: name })),
  'kubernetes/list.ndjson': [
    env('kubernetes-list', 'kubernetes-list/NodeList/uid/1', {
      metadata: { name: REAL.node, uid: 'dddddddd-1111-4222-8333-777777777777', labels: { 'kubernetes.azure.com/cluster': REAL.nodeRg, 'kubernetes.io/hostname': REAL.node } },
      spec: { providerID: `azure://${VMSS_VM_ID}` },
      status: { addresses: [{ type: 'InternalIP', address: REAL.ip }], nodeInfo: { machineID: REAL.machineId, systemUUID: 'eeeeeeee-1111-4222-8333-888888888888' } },
    }, { list: { kind: 'NodeList' } }),
    env('kubernetes-list', 'kubernetes-list/DeploymentList/uid/2', {
      metadata: { name: 'web', uid: 'ffffffff-1111-4222-8333-999999999999' },
      spec: { template: { spec: { containers: [{ name: 'web', image: 'nginx:1', livenessProbe: { tcpSocket: { host: REAL.ip } } }] } } },
    }, { list: { kind: 'DeploymentList' } }),
  ],
  'activity-log/backfill.ndjson': [
    env('activity-log', 'activity-log/1', {
      caller: REAL.user, operationName: { value: 'Microsoft.ContainerService/managedClusters/write', localizedValue: 'Update Managed Cluster' },
      resourceId: CLUSTER_ID.toLowerCase(), authorization: { action: 'Microsoft.ContainerService/managedClusters/write', scope: CLUSTER_ID },
      claims: { appid: 'aaaaaaaa-0000-4000-8000-000000000001', name: 'Jane Doe', 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress': REAL.user, 'http://schemas.microsoft.com/identity/claims/objectidentifier': REAL.userOid, 'http://schemas.microsoft.com/identity/claims/tenantid': REAL.tenant, rh: REAL.claimRh, xms_ftd: REAL.claimFtd, xms_tcdt: REAL.claimTcdt, aud: 'https://management.azure.com/', ver: '1.0' },
      httpRequest: { clientIpAddress: REAL.callerIp }, eventTimestamp: '2026-01-01T00:00:00Z',
    }),
  ],
  'eventhub/sattle-activity.ndjson': [
    env('eventhub:sattle-activity', 'eventhub:sattle-activity/1/0', {
      records: [{
        time: '2026-01-01T00:00:00Z', resourceId: CLUSTER_ID.toUpperCase().replace('/SUBSCRIPTIONS/', '/SUBSCRIPTIONS/'), operationName: 'MICROSOFT.CONTAINERSERVICE/MANAGEDCLUSTERS/WRITE', category: 'Administrative', callerIpAddress: REAL.callerIp,
        identity: { authorization: { scope: CLUSTER_ID, evidence: { role: `Contributor (${REAL.rg})`, roleAssignmentScope: `/subscriptions/${REAL.sub}/resourcegroups/${REAL.rg}`, roleAssignmentId: REAL.roleAssignmentId, roleDefinitionId: '68828df1bfbe559d872979f2a1a92cbf', principalId: REAL.principalHex } }, claims: { rh: REAL.claimRh, xms_ftd: REAL.claimFtd, xms_tcdt: REAL.claimTcdt, appid: 'aaaaaaaa-0000-4000-8000-000000000002' } },
        properties: { entity: CLUSTER_ID, message: 'Microsoft.ContainerService/managedClusters/write' },
      }],
    }, { partitionId: '1', sequenceNumber: 0 }),
  ],
};

// ---------- running the tool ----------
const tmp = (name) => mkdtempSync(join(tmpdir(), `scrub-${name}-`));

function writeCapture(dir, capture = CAPTURE) {
  for (const [file, records] of Object.entries(capture)) {
    mkdirSync(dirname(join(dir, file)), { recursive: true });
    writeFileSync(join(dir, file), records.map((r) => JSON.stringify(r)).join('\n') + '\n');
  }
}
const readNdjson = (file) => readFileSync(file, 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l));
const walkFiles = (d) => (existsSync(d) ? readdirSync(d, { withFileTypes: true }).flatMap((x) => (x.isDirectory() ? walkFiles(join(d, x.name)) : x.name.endsWith('.ndjson') ? [join(d, x.name)] : [])) : []);
const run = (tool, args, opts = {}) => spawnSync(process.execPath, [join(tool, 'scrub.mjs'), ...args], { encoding: 'utf8', env: { ...process.env, SCRUB_LITERALS: '', ...opts.env } });

// build once, with no extra literals: the capture itself must identify what is real
function buildOnce(name, { tool = FIXTURES_DIR, capture = CAPTURE } = {}) {
  const root = tmp(name);
  const cap = join(root, 'captures', 'cap');
  const out = join(root, 'fixtures');
  writeCapture(cap, capture);
  const r = run(tool, ['build', cap, out]);
  return { root, cap, out, r, text: walkFiles(out).map((f) => readFileSync(f, 'utf8')).join('\n'), records: Object.fromEntries(walkFiles(out).map((f) => [f.slice(out.length + 1), readNdjson(f)])) };
}
const built = buildOnce('main');

const keyPaths = (v, p = '') => (Array.isArray(v) ? v.flatMap((x) => keyPaths(x, `${p}[]`)) : v && typeof v === 'object' ? Object.entries(v).flatMap(([k, x]) => [`${p}.${k}`, ...keyPaths(x, `${p}.${k}`)]) : []);
const get = (o, path) => path.split('.').reduce((x, k) => (x == null ? x : /^\d+$/.test(k) ? x[Number(k)] : x[k]), o);
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const HEX32 = /^[0-9a-f]{32}$/i;

test('the build writes fixtures (exit 0, files exist)', () => {
  assert.equal(built.r.status, 0, built.r.stderr);
  assert.ok(Object.keys(built.records).length >= 7, `only ${Object.keys(built.records).join(', ')}`);
});

// ---------- finding: names replaced as substrings damage keys, resource types, metric names, operations ----------
test('structure survives: every JSON key is kept (a real name that is also a word must not rename a key)', () => {
  for (const [file, records] of Object.entries(CAPTURE)) {
    const out = built.records[file];
    assert.ok(out, `${file} missing`);
    const kin = new Set(records.flatMap((r) => keyPaths(r)));
    const kout = new Set(out.flatMap((r) => keyPaths(r)));
    const lost = [...kin].filter((k) => !kout.has(k));
    assert.deepEqual(lost.slice(0, 5), [], `${file}: keys lost or renamed`);
  }
});

for (const [file, path, expected] of [
  ['arg/resources.ndjson', '0.payload.type', 'microsoft.containerservice/managedclusters'],
  ['arg/resources.ndjson', '1.payload.type', 'microsoft.compute/virtualmachines'],
  ['arg/resources.ndjson', '2.payload.type', 'microsoft.storage/storageaccounts'],
  ['arg/resources.ndjson', '3.payload.type', 'microsoft.network/networksecuritygroups'],
  ['azure-metrics/aks.ndjson', '0.payload.namespace', 'Microsoft.ContainerService/managedClusters'],
  ['azure-metrics/aks.ndjson', '0.payload.value.0.name.value', 'node_memory_working_set_percentage'],
  ['azure-metrics/aks.ndjson', '0.payload.value.1.name.value', 'node_cpu_usage_percentage'],
  ['azure-metrics/aks.ndjson', '0.payload.value.2.name.value', 'node_disk_usage_percentage'],
  ['prometheus/range.ndjson', '0.meta.name', 'cilium_forward_rate'],
  ['prometheus/range.ndjson', '0.payload.data.result.0.metric.__name__', 'cilium_forward_rate'],
  ['prometheus/range.ndjson', '1.payload.data.result.0.metric.__name__', 'container_memory_working_set_bytes'],
  ['activity-log/backfill.ndjson', '0.payload.operationName.value', 'Microsoft.ContainerService/managedClusters/write'],
  ['activity-log/backfill.ndjson', '0.payload.authorization.action', 'Microsoft.ContainerService/managedClusters/write'],
  ['eventhub/sattle-activity.ndjson', '0.payload.records.0.operationName', 'MICROSOFT.CONTAINERSERVICE/MANAGEDCLUSTERS/WRITE'],
  ['eventhub/sattle-activity.ndjson', '0.payload.records.0.properties.message', 'Microsoft.ContainerService/managedClusters/write'],
  ['kubernetes/list.ndjson', '1.payload.spec.template.spec.containers.0.image', 'nginx:1'],
])
  test(`vocabulary is never touched: ${file} ${path}`, () => {
    assert.equal(get(built.records[file], path), expected);
  });

// ---------- finding: one subscription got two aliases (upper and lower case); ids must stay GUIDs ----------
// `microsoft.resources/subscriptions/resourcegroups` is a resource type, not a subscription id
const subscriptionsIn = (text) => [...text.matchAll(/\/subscriptions\/([^/"\\]+)/gi)].map((m) => m[1]).filter((x) => x.toLowerCase() !== 'resourcegroups');
test('one subscription has one alias whatever the case of the id that carried it', () => {
  const found = subscriptionsIn(built.text);
  assert.ok(found.length >= 8, `${found.length} occurrences`);
  assert.equal(new Set(found).size, 1, `aliases: ${new Set(found).size}`);
});
test('the subscription id maps to a GUID (CAPTURE-FORMAT: GUIDs map to GUIDs)', () => {
  assert.match(subscriptionsIn(built.text)[0], GUID);
});
const TENANT_CLAIM = 'http://schemas.microsoft.com/identity/claims/tenantid';
for (const [name, read] of [
  ['tenantId of an ARG row', () => built.records['arg/resources.ndjson'][0].payload.tenantId],
  ['tenant id claim', () => built.records['activity-log/backfill.ndjson'][0].payload.claims[TENANT_CLAIM]],
])
  test(`tenant id stays a GUID: ${name}`, () => {
    assert.match(read(), GUID);
  });
test('the same real GUID gets the same alias in every source', () => {
  const claim = built.records['activity-log/backfill.ndjson'][0].payload.claims[TENANT_CLAIM];
  assert.equal(claim, built.records['arg/resources.ndjson'][0].payload.tenantId);
});

// ---------- finding: 32 hex ids without dashes, role assignment ids, machine ids ----------
for (const [name, read, real] of [
  ['internalId (ARG)', () => built.records['arg/resources.ndjson'][0].payload.properties.internalId, REAL.internalId],
  ['roleAssignmentId (hub)', () => built.records['eventhub/sattle-activity.ndjson'][0].payload.records[0].identity.authorization.evidence.roleAssignmentId, REAL.roleAssignmentId],
  ['principalId (hub)', () => built.records['eventhub/sattle-activity.ndjson'][0].payload.records[0].identity.authorization.evidence.principalId, REAL.principalHex],
  ['node machineID (Kubernetes)', () => built.records['kubernetes/list.ndjson'][0].payload.status.nodeInfo.machineID, REAL.machineId],
])
  test(`32 hex id without dashes is replaced and keeps its shape: ${name}`, () => {
    const v = read();
    assert.notEqual(v?.toLowerCase(), real);
    assert.match(v, HEX32);
  });

// ---------- finding: free-text tag values, a short prefix in an alias, token claims ----------
test('free-text tag values are replaced, tag keys stay', () => {
  const tags = built.records['arg/resources.ndjson'][0].payload.tags;
  assert.deepEqual(Object.keys(tags), ['Org']);
  assert.ok(!JSON.stringify(tags).toLowerCase().includes(REAL.orgTag.toLowerCase()));
});
test('a resource group alias keeps no part of the real name (no initials prefix)', () => {
  const rg = built.records['arg/resources.ndjson'][0].payload.resourceGroup;
  assert.ok(!/zq/i.test(rg), `alias ${rg}`);
  assert.notEqual(rg, REAL.rg);
});
for (const [file, get1] of [
  ['activity-log/backfill.ndjson', (r) => r.payload.claims],
  ['eventhub/sattle-activity.ndjson', (r) => r.payload.records[0].identity.claims],
])
  for (const claim of ['rh', 'xms_ftd', 'xms_tcdt'])
    test(`token claim ${claim} does not survive: ${file}`, () => {
      const c = get1(built.records[file][0]);
      const real = { rh: REAL.claimRh, xms_ftd: REAL.claimFtd, xms_tcdt: REAL.claimTcdt }[claim];
      assert.ok(c[claim] === undefined || c[claim] !== real);
    });

// ---------- no real value survives (what a reviewer would grep for) ----------
const valuesOf = (v) => (Array.isArray(v) ? v.flatMap(valuesOf) : v && typeof v === 'object' ? Object.values(v).flatMap(valuesOf) : typeof v === 'string' ? [v] : []);
const allValues = Object.values(built.records).flat().flatMap(valuesOf);
const WORDS = ['vmName', 'boxName', 'opName']; // real names that are ordinary words: only whole names are wrong, not the word inside a metric name
for (const [what, value] of Object.entries({ ...REAL, userLocal: 'jane.doe', displayName: 'Jane Doe' })) {
  if (WORDS.includes(what)) {
    test(`no real name left as a name: ${what}`, () => {
      // a whole value, or a segment of a resource id (an operation such as .../write legitimately ends in the word)
      const hit = allValues.find((x) => x.toLowerCase() === value || (/\/subscriptions\//i.test(x) && x.toLowerCase().split('/').includes(value)));
      assert.equal(hit, undefined, `${what} still present as a whole name`);
    });
  } else {
    test(`no real value in the output: ${what}`, () => {
      assert.ok(!allValues.some((x) => x.toLowerCase().includes(value.toLowerCase())), `${what} still present`);
    });
  }
}

// ---------- joins keep working ----------
test('a node has one name in Kubernetes and in Prometheus', () => {
  const k = built.records['kubernetes/list.ndjson'][0].payload.metadata.name;
  const p = built.records['prometheus/range.ndjson'][0].payload.data.result[0].metric.instance;
  assert.equal(k, p);
  assert.notEqual(k, REAL.node);
});
test('a cluster has one name in ARG and in Prometheus', () => {
  assert.equal(built.records['arg/resources.ndjson'][0].payload.name, built.records['prometheus/range.ndjson'][0].payload.data.result[0].metric.cluster);
});
test('a resource id spelled in upper and lower case joins after lower-casing', () => {
  const lower = built.records['activity-log/backfill.ndjson'][0].payload.resourceId.toLowerCase();
  const upper = built.records['eventhub/sattle-activity.ndjson'][0].payload.records[0].resourceId.toLowerCase();
  assert.equal(lower, upper);
  assert.equal(lower, built.records['arg/resources.ndjson'][0].payload.id.toLowerCase());
});

// ---------- shapes that already worked (guard against regressions) ----------
test('an email maps to an example.com address', () => {
  assert.match(built.records['activity-log/backfill.ndjson'][0].payload.caller, /@example\.com$/);
});
test('private addresses map to private addresses, public to 198.18.0.0/15', () => {
  assert.match(built.records['kubernetes/list.ndjson'][0].payload.status.addresses[0].address, /^10\./);
  assert.match(built.records['activity-log/backfill.ndjson'][0].payload.httpRequest.clientIpAddress, /^198\.1[89]\./);
});
test('building twice from the same capture gives identical files', () => {
  const again = buildOnce('again');
  // the salt differs between the two roots, so compare shapes: same keys, same record counts
  assert.deepEqual(Object.keys(again.records), Object.keys(built.records));
  assert.deepEqual(Object.values(again.records).map((r) => r.length), Object.values(built.records).map((r) => r.length));
  // same root and salt: byte-identical
  const out2 = join(built.root, 'fixtures2');
  assert.equal(run(FIXTURES_DIR, ['build', built.cap, out2]).status, 0);
  assert.equal(walkFiles(out2).map((f) => readFileSync(f, 'utf8')).join('\n'), built.text);
});

// ---------- finding: the entry point does nothing when the tool lives in a path with a space ----------
test('build and verify run from a checkout path with a space', () => {
  const holder = join(tmp('space'), 'path with space');
  mkdirSync(join(holder, 'fixtures'), { recursive: true });
  for (const f of readdirSync(FIXTURES_DIR, { withFileTypes: true })) {
    if ((f.isFile() && /\.(mjs|json)$/.test(f.name)) || (f.isDirectory() && f.name === 'scrub')) cpSync(join(FIXTURES_DIR, f.name), join(holder, 'fixtures', f.name), { recursive: true });
  }
  const copy = buildOnce('space', { tool: join(holder, 'fixtures') });
  assert.equal(copy.r.status, 0, copy.r.stderr);
  assert.ok(Object.keys(copy.records).length >= 7, 'build wrote nothing');
  const v = run(join(holder, 'fixtures'), ['verify', copy.cap, copy.out]);
  assert.equal(v.status, 0, v.stderr);
  assert.match(v.stdout, /OK|checked/i, 'verify printed nothing: it did not run');
});

// ---------- finding: verify reuses the scrub's detectors, so it cannot see what the scrub does not see ----------
const verifyWith = (mutate) => {
  const root = tmp('verify');
  const cap = join(root, 'captures', 'cap');
  const out = join(root, 'fixtures');
  writeCapture(cap);
  assert.equal(run(FIXTURES_DIR, ['build', cap, out]).status, 0);
  mutate(out);
  return run(FIXTURES_DIR, ['verify', cap, out]);
};
const edit = (out, file, fn) => { const f = join(out, file); writeFileSync(f, readFileSync(f, 'utf8').split('\n').filter(Boolean).map((l) => fn(l)).join('\n') + '\n'); };

test('verify passes on a clean build', () => {
  const r = verifyWith(() => {});
  assert.equal(r.status, 0, r.stderr + r.stdout);
});
for (const [name, plant] of [
  ['a 32 hex id without dashes', (l) => l.replace('"seq":0', `"seq":0,"leak":"${REAL.internalId}"`)],
  ['a free-text tag value', (l) => l.replace('"seq":0', `"seq":0,"leak":"${REAL.orgTag}"`)],
  ['a resource group name', (l) => l.replace('"seq":0', `"seq":0,"leak":"${REAL.rg}"`)],
  ['a resource group name inside a longer text', (l) => l.replace('"seq":0', `"seq":0,"leak":"Contributor on (${REAL.rg.toUpperCase()}) for all"`)],
  ['a node name', (l) => l.replace('"seq":0', `"seq":0,"leak":"${REAL.node}"`)],
  ['a token claim', (l) => l.replace('"seq":0', `"seq":0,"leak":"${REAL.claimFtd}"`)],
  ['a user name part', (l) => l.replace('"seq":0', `"seq":0,"leak":"jane.doe"`)],
  ['a subscription id without dashes', (l) => l.replace('"seq":0', `"seq":0,"leak":"${REAL.sub.replaceAll('-', '')}"`)],
])
  test(`verify fails on a planted leak: ${name}`, () => {
    const r = verifyWith((out) => edit(out, 'arg/resources.ndjson', plant));
    assert.notEqual(r.status, 0, 'verify accepted a leak');
    assert.ok(!r.stdout.concat(r.stderr).toLowerCase().includes('zqcorp'), 'verify printed a real value');
  });
test('verify never prints a real value', () => {
  const r = verifyWith((out) => edit(out, 'arg/resources.ndjson', (l) => l.replace('"seq":0', `"seq":0,"leak":"${REAL.rg} ${REAL.sub} ${REAL.user}"`)));
  const said = (r.stdout + r.stderr).toLowerCase();
  for (const v of [REAL.rg, REAL.sub, REAL.user, 'fakecluster']) assert.ok(!said.includes(v.toLowerCase()), 'a real value was printed');
});
