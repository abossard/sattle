// Tests for the azd hooks and the capture shell helpers, run against stand-ins for az, azd, kubectl and curl
// (a directory of small scripts first on PATH). Nothing here calls Azure. Run: node --test lab/tests/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const LAB = join(dirname(fileURLToPath(import.meta.url)), '..');

// A bin directory with stand-ins. Each logs its arguments to $LOG, one line per call. `az` and `kubectl` read
// $FAIL_ON (a substring of the arguments that makes them fail), $FAIL_MODE (auth | gone | empty) and print a value otherwise.
function sandbox() {
  const dir = mkdtempSync(join(tmpdir(), 'hooks-'));
  const bin = join(dir, 'bin'); mkdirSync(bin);
  const log = join(dir, 'calls.log'); writeFileSync(log, '');
  const tool = (name, body) => { writeFileSync(join(bin, name), `#!/bin/sh\n${body}\n`); chmodSync(join(bin, name), 0o755); };
  const cloud = (name) => `
printf '${name} %s\\n' "$*" >> "$LOG"
if [ -n "$FAIL_ON" ] && case "$*" in *"$FAIL_ON"*) true;; *) false;; esac; then
  case "$FAIL_MODE" in
    auth) echo "ERROR: Please run 'az login' to setup account." >&2; exit 1;;
    gone) echo "(ResourceNotFound) The Resource could not be found." >&2; exit 3;;
    empty) exit 0;;
  esac
fi
echo "value-of-call"`;
  tool('az', cloud('az'));
  tool('kubectl', cloud('kubectl'));
  tool('azd', `printf 'azd %s\\n' "$*" >> "$LOG"`);
  tool('curl', `printf 'curl %s\\n' "$*" >> "$LOG"
case "$*" in *restype=container*) echo '<Name>blob-1</Name>';; *) echo '{"records":[]}';; esac`);
  return { dir, bin, log, calls: () => readFileSync(log, 'utf8').split('\n').filter(Boolean) };
}
const runSh = (sb, script, { env = {}, input, cwd } = {}) => spawnSync('sh', [script], {
  encoding: 'utf8', input, cwd: cwd ?? sb.dir,
  env: { PATH: `${sb.bin}:/usr/bin:/bin`, HOME: sb.dir, LOG: sb.log, ...env },
});

// ---------- preprovision: a failing az must stop the hook, never store an empty value ----------
const PRE = join(LAB, 'hooks', 'preprovision.sh');
const preEnv = { SATTLE_AKS_RG: 'rg-x', SATTLE_AKS_NAME: 'aks-x' };
for (const [what, failOn, mode] of [
  ['the node resource group lookup fails', '--query nodeResourceGroup', 'auth'],
  ['the VNet list fails', 'network vnet list', 'auth'],
  ['the cluster id lookup fails', '--query id', 'auth'],
  ['the VNet id lookup fails', 'network vnet show', 'auth'],
  ['the subnet id lookup fails', 'subnet show', 'auth'],
  ['the signed-in user lookup fails', 'signed-in-user', 'auth'],
  ['the subnet lookup finds nothing', 'subnet show', 'empty'],
  ['the cluster id lookup finds nothing', '--query id', 'empty'],
])
  test(`preprovision stops and stores nothing empty when ${what}`, () => {
    const sb = sandbox();
    const r = runSh(sb, PRE, { env: { ...preEnv, FAIL_ON: failOn, FAIL_MODE: mode } });
    assert.notEqual(r.status, 0, `exit ${r.status}: ${r.stderr}`);
    const emptySets = sb.calls().filter((c) => /^azd env set \S+\s*$/.test(c));
    assert.deepEqual(emptySets, [], 'an empty value was stored in the azd environment');
  });
test('preprovision stores every value when az answers', () => {
  const sb = sandbox();
  const r = runSh(sb, PRE, { env: preEnv });
  assert.equal(r.status, 0, r.stderr);
  const stored = sb.calls().filter((c) => c.startsWith('azd env set')).map((c) => c.split(' ')[3]);
  for (const k of ['SATTLE_AKS_ID', 'SATTLE_NODE_RG', 'SATTLE_AKS_VNET_ID', 'SATTLE_AKS_SUBNET_ID', 'AZURE_PRINCIPAL_ID']) assert.ok(stored.includes(k), `${k} not stored`);
});
for (const missing of ['SATTLE_AKS_RG', 'SATTLE_AKS_NAME'])
  test(`preprovision names the value that is missing: ${missing}`, () => {
    const sb = sandbox();
    const env = { ...preEnv }; delete env[missing];
    const r = runSh(sb, PRE, { env });
    assert.notEqual(r.status, 0);
    assert.ok(r.stderr.includes(missing), r.stderr);
  });

// ---------- postdown: undo steps that fail for a real reason must fail azd down; gone is fine ----------
const DOWN = join(LAB, 'hooks', 'postdown.sh');
const downEnv = { SATTLE_AKS_RG: 'rg-x', SATTLE_AKS_NAME: 'aks-x', SATTLE_AKS_ID: '/id', AZURE_LOCATION: 'somewhere', SATTLE_ACNS_BY_LAB: 'true' };
test('postdown fails when the login has expired, and still tries every undo step', () => {
  const sb = sandbox();
  const r = runSh(sb, DOWN, { env: { ...downEnv, FAIL_ON: ' ', FAIL_MODE: 'auth' }, cwd: sb.dir });
  assert.notEqual(r.status, 0, 'azd down would report success while the lab features stay on');
  const calls = sb.calls().join('\n');
  for (const step of ['diagnostic-settings subscription delete', 'diagnostic-settings delete', 'flow-log delete', 'namespace sattle-demo', 'aks update']) assert.ok(calls.includes(step), `step not tried: ${step}`);
});
test('postdown succeeds when everything is already gone', () => {
  const sb = sandbox();
  const r = runSh(sb, DOWN, { env: { ...downEnv, FAIL_ON: ' ', FAIL_MODE: 'gone' } });
  assert.equal(r.status, 0, r.stderr);
});
test('postdown succeeds when every step works', () => {
  const sb = sandbox();
  const r = runSh(sb, DOWN, { env: downEnv });
  assert.equal(r.status, 0, r.stderr);
});
test('postdown leaves ACNS alone when the lab did not switch it on', () => {
  const sb = sandbox();
  const r = runSh(sb, DOWN, { env: { ...downEnv, SATTLE_ACNS_BY_LAB: 'false' } });
  assert.equal(r.status, 0, r.stderr);
  assert.ok(!sb.calls().some((c) => c.includes('aks update')));
});
test('postdown names a missing value instead of running', () => {
  const sb = sandbox();
  const env = { ...downEnv }; delete env.SATTLE_AKS_RG;
  const r = runSh(sb, DOWN, { env });
  assert.notEqual(r.status, 0);
  assert.ok(r.stderr.includes('SATTLE_AKS_RG'), r.stderr);
});

// ---------- flow log reader: the token never appears in a process argument ----------
test('the blob reader keeps the storage token out of every command line', () => {
  const remote = join(LAB, 'capture', 'sources', 'flowlog-remote.sh');
  assert.ok(existsSync(remote), 'the script that runs inside the pod is its own file, so it can be tested');
  const sb = sandbox();
  const token = 'TOKEN-eyJ0eXAiOiJKV1Qi-SECRET';
  const r = spawnSync('sh', [remote, 'account'], { encoding: 'utf8', input: `${token}\n`, env: { PATH: `${sb.bin}:/usr/bin:/bin`, LOG: sb.log, HOME: sb.dir, TMPDIR: sb.dir } });
  assert.equal(r.status, 0, r.stderr);
  const calls = sb.calls().filter((c) => c.startsWith('curl'));
  assert.ok(calls.length >= 2, 'curl was not called for the list and the blob');
  assert.ok(!calls.join('\n').includes(token), 'the token is in a curl argument');
});

// ---------- capture.sh: wait for every stream on its own ----------
const waitStreams = (script) => spawnSync('bash', ['-c', `set -u; source "${join(LAB, 'capture', 'lib.sh')}"; ${script}`], { encoding: 'utf8' });
for (const [name, jobs, expectStatus, expectFailed] of [
  ['all streams end well', [['watch', 0], ['churn', 0]], 0, []],
  ['only the churn job fails', [['watch', 0], ['churn', 3]], 0, ['churn']],
  ['a consumer dies', [['watch', 0], ['consume', 2], ['churn', 0]], 1, ['consume']],
  ['the churn job fails and a watch dies', [['watch', 4], ['churn', 3]], 1, ['watch', 'churn']],
  ['the last pid is a failing churn job and a consumer failed earlier', [['consume', 5], ['churn', 1]], 1, ['consume', 'churn']],
])
  test(`wait_streams: ${name}`, () => {
    const spawn = jobs.map(([n, code], i) => `(sleep 0.${i + 1}; exit ${code}) & p${i}=$!`).join('\n');
    const args = jobs.map(([n], i) => `${n}:$p${i}`).join(' ');
    const r = waitStreams(`${spawn}\nwait_streams ${args}; echo "status=$?"`);
    assert.equal(r.status, 0, r.stderr);
    assert.match(r.stdout, new RegExp(`status=${expectStatus}`));
    for (const n of expectFailed) assert.match(r.stderr, new RegExp(`FAILED stream: ${n}`), `no report for ${n}: ${r.stderr}`);
    for (const [n, code] of jobs) if (code === 0) assert.doesNotMatch(r.stderr, new RegExp(`FAILED stream: ${n}`));
  });
