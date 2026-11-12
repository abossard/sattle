// Tests for the azd hooks and the capture shell helpers, run against stand-ins for az, azd, kubectl and curl
// (a directory of small scripts first on PATH). Nothing here calls Azure. Run: node --test lab/tests/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { chmodSync, cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const LAB = join(dirname(fileURLToPath(import.meta.url)), '..');

// A bin directory with stand-ins. Each logs its arguments to $LOG, one line per call. `az` and `kubectl` read
// $FAIL_ON (a substring of the arguments that makes them fail), $FAIL_MODE (auth | gone | empty). Otherwise the first line
// `substring|answer` of the file $ANSWERS that matches the arguments gives the answer, and `value-of-call` is the default.
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
if [ -n "$ANSWERS" ]; then
  while IFS='|' read -r pat out; do case "$*" in *"$pat"*) printf '%s\\n' "$out"; exit 0;; esac; done < "$ANSWERS"
fi
echo "value-of-call"`;
  tool('az', cloud('az'));
  tool('kubectl', cloud('kubectl'));
  tool('azd', `printf 'azd %s\\n' "$*" >> "$LOG"`);
  tool('curl', `printf 'curl %s\\n' "$*" >> "$LOG"
case "$*" in *restype=container*) echo '<Name>blob-1</Name>';; *) echo '{"records":[]}';; esac`);
  // answers: [[substring of the arguments, what the tool prints]]
  const answers = (pairs) => { const f = join(dir, 'answers'); writeFileSync(f, pairs.map(([a, b]) => `${a}|${b}`).join('\n') + '\n'); return f; };
  return { dir, bin, log, answers, calls: () => readFileSync(log, 'utf8').split('\n').filter(Boolean) };
}
const runSh = (sb, script, { env = {}, input, cwd } = {}) => spawnSync('sh', [script], {
  encoding: 'utf8', input, cwd: cwd ?? sb.dir,
  env: { PATH: `${sb.bin}:/usr/bin:/bin`, HOME: sb.dir, LOG: sb.log, ...env },
});
// the real node and jq behind the stand-ins, for scripts that need them
const TOOLS = [dirname(process.execPath), ...['/opt/homebrew/bin', '/usr/local/bin'].filter((d) => existsSync(join(d, 'jq')))].join(':');

// ---------- preprovision: a failing az must stop the hook, never store an empty value ----------
const PRE = join(LAB, 'hooks', 'preprovision.sh');
const preEnv = { SATTLE_AKS_RG: 'rg-x', SATTLE_AKS_NAME: 'aks-x', AZURE_ENV_NAME: 'sattle' };
const NOTHING_THERE = [['group exists', 'false'], ['diagnostic-settings subscription show', '']];
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
    const r = runSh(sb, PRE, { env: { ...preEnv, FAIL_ON: failOn, FAIL_MODE: mode, ANSWERS: sb.answers(NOTHING_THERE) } });
    assert.notEqual(r.status, 0, `exit ${r.status}: ${r.stderr}`);
    const emptySets = sb.calls().filter((c) => /^azd env set \S+\s*$/.test(c));
    assert.deepEqual(emptySets, [], 'an empty value was stored in the azd environment');
  });
test('preprovision stores every value when az answers', () => {
  const sb = sandbox();
  const r = runSh(sb, PRE, { env: { ...preEnv, ANSWERS: sb.answers(NOTHING_THERE) } });
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

// azd down deletes the lab resource group and the hooks own fixed names in the subscription: a group or a diagnostic
// setting that this azd environment did not create must stop the deployment before anything is written
const RULE = (rg) => `/subscriptions/s/resourceGroups/${rg}/providers/Microsoft.EventHub/namespaces/n/authorizationRules/r`;
for (const [what, answers, env, ok] of [
  ['the lab group does not exist yet', NOTHING_THERE, {}, true],
  ['the lab group belongs to this azd environment', [['group exists', 'true'], ['group show', 'sattle'], ['diagnostic-settings subscription show', RULE('rg-sattle-lab')]], {}, true],
  ['the lab group belongs to another azd environment', [['group exists', 'true'], ['group show', 'other-env'], ['diagnostic-settings subscription show', '']], {}, false],
  ['the lab group exists and azd did not create it', [['group exists', 'true'], ['group show', ''], ['diagnostic-settings subscription show', '']], {}, false],
  ['an overridden lab group name points at a foreign group', [['group exists', 'true'], ['group show', ''], ['diagnostic-settings subscription show', '']], { SATTLE_LAB_RG: 'rg-production' }, false],
  ['the Activity Log setting already feeds another lab', [['group exists', 'false'], ['diagnostic-settings subscription show', RULE('rg-other-lab')]], {}, false],
  ['the Activity Log setting feeds an overridden group of this environment', [['group exists', 'true'], ['group show', 'sattle'], ['diagnostic-settings subscription show', RULE('rg-mine')]], { SATTLE_LAB_RG: 'rg-mine' }, true],
])
  test(`preprovision ${ok ? 'goes on' : 'stops'} when ${what}`, () => {
    const sb = sandbox();
    const r = runSh(sb, PRE, { env: { ...preEnv, ...env, ANSWERS: sb.answers(answers) } });
    const stored = sb.calls().filter((c) => c.startsWith('azd env set'));
    if (ok) { assert.equal(r.status, 0, r.stderr); assert.ok(stored.length >= 5); } else {
      assert.notEqual(r.status, 0, 'the deployment would go on');
      assert.deepEqual(stored, [], 'values were written before the stop');
      assert.match(r.stderr, new RegExp(env.SATTLE_LAB_RG ?? (what.includes('Activity Log') ? 'rg-other-lab' : 'rg-sattle-lab')));
    }
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

// ---------- Kubernetes watch: a failing watch is reported and fails the script, an early end is resumed ----------
const K8S = join(LAB, 'capture', 'sources', 'kubernetes.sh');
const watchEvent = (rv) => JSON.stringify({ type: 'MODIFIED', object: { kind: 'Pod', metadata: { name: 'p', uid: 'u', resourceVersion: String(rv) } } });
// The tests decide when a watch window ends: the script gets a window of ten minutes and is stopped with SIGTERM once
// the test has seen what it waits for. The deadline below only catches a hang, it is not a time the script must meet.
const HANG_MS = 60_000;
function watchSandbox(paths) {
  const sb = sandbox();
  const out = join(sb.dir, 'out'); mkdirSync(join(out, 'kubernetes'), { recursive: true });
  writeFileSync(join(out, 'kubernetes', '.versions.ndjson'), paths.map((p) => JSON.stringify({ path: p, resourceVersion: '100' })).join('\n') + '\n');
  const lines = (name) => { const f = join(out, 'kubernetes', `watch.${name}.ndjson`); return existsSync(f) ? readFileSync(f, 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l)) : []; };
  // runs the script until `ready(stderr)` is true, then stops it
  const runUntil = async (ready, env = {}) => {
    const child = spawn('bash', [K8S, 'watch', out, '600'], { env: { PATH: `${sb.bin}:${TOOLS}:/usr/bin:/bin`, HOME: sb.dir, LOG: sb.log, ...env } });
    let stderr = ''; child.stderr.on('data', (d) => { stderr += d; });
    const closed = new Promise((resolve) => child.on('close', (status, signal) => resolve({ status, signal })));
    const started = Date.now();
    while (!ready(stderr)) {
      if (Date.now() - started > HANG_MS) { child.kill('SIGKILL'); assert.fail(`the condition was not reached, stderr: ${stderr}`); }
      await new Promise((r) => setTimeout(r, 50));
    }
    child.kill('SIGTERM');
    let hangTimer;
    const end = await Promise.race([closed, new Promise((r) => { hangTimer = setTimeout(() => r({ hung: true }), HANG_MS); })]);
    clearTimeout(hangTimer); // a pending timer would keep the test process alive for a minute after the last test
    if (end.hung) { child.kill('SIGKILL'); assert.fail('the script did not stop on SIGTERM'); }
    return { ...end, stderr };
  };
  return { ...sb, out, lines, runUntil };
}
test('a watch that fails is named and fails the script, the other kinds are still captured', async () => {
  const sb = watchSandbox(['/api/v1/pods', '/api/v1/services']);
  const r = await sb.runUntil((err) => err.includes('az login') && sb.lines('services').length >= 1, { FAIL_ON: '/api/v1/pods', FAIL_MODE: 'auth', ANSWERS: sb.answers([['watch=1', watchEvent(101)]]) });
  assert.notEqual(r.status, 0, 'a dead watch went unnoticed');
  assert.match(r.stderr, /\/api\/v1\/pods/, 'the failing kind is not named');
  assert.match(r.stderr, /az login/, 'the reason kubectl gave is not shown');
  assert.ok(sb.lines('services').length >= 1, 'the healthy watch wrote nothing');
});
test('a watch that ends before the window is over resumes from the last version it saw', async () => {
  const sb = watchSandbox(['/api/v1/pods']);
  const r = await sb.runUntil(() => sb.calls().filter((c) => c.includes('watch=1')).length >= 2, { ANSWERS: sb.answers([['watch=1', watchEvent(555)]]) });
  assert.equal(r.status, 0, `stopping a healthy watch is not a failure: ${r.stderr}`);
  const calls = sb.calls().filter((c) => c.includes('watch=1'));
  assert.match(calls[0], /resourceVersion=100(&|$)/);
  assert.match(calls[1], /resourceVersion=555(&|$)/, 'the second watch did not resume from the last event');
});
test('all watches healthy: exit 0', async () => {
  const sb = watchSandbox(['/api/v1/pods', '/api/v1/services']);
  const r = await sb.runUntil(() => sb.lines('pods').length >= 1 && sb.lines('services').length >= 1, { ANSWERS: sb.answers([['watch=1', watchEvent(101)]]) });
  assert.equal(r.status, 0, r.stderr);
});

// ---------- one command from a capture to fixtures ----------
// Run in a copy of the script next to stand-ins for capture.sh and build.sh.
function pipelineSandbox({ captureExit = 0, buildExit = 0 } = {}) {
  const dir = mkdtempSync(join(tmpdir(), 'pipeline-'));
  const lab = join(dir, 'lab'); mkdirSync(join(lab, 'capture'), { recursive: true }); mkdirSync(join(lab, 'fixtures'));
  const script = join(LAB, 'capture-to-fixtures.sh');
  if (existsSync(script)) cpSync(script, join(lab, 'capture-to-fixtures.sh'));
  const log = join(dir, 'calls.log'); writeFileSync(log, '');
  const tool = (f, body) => { writeFileSync(f, `#!/usr/bin/env bash\n${body}\n`); chmodSync(f, 0o755); };
  tool(join(lab, 'capture', 'capture.sh'), `echo "capture $* ts=$CAPTURE_TS" >> "${log}"; mkdir -p "$(dirname "$0")/../captures/$CAPTURE_TS"; exit ${captureExit}`);
  tool(join(lab, 'fixtures', 'build.sh'), `echo "build $*" >> "${log}"; exit ${buildExit}`);
  const r = spawnSync('bash', [join(lab, 'capture-to-fixtures.sh'), '30'], { encoding: 'utf8', env: { PATH: '/usr/bin:/bin', HOME: dir } });
  return { r, lab, calls: readFileSync(log, 'utf8').split('\n').filter(Boolean), captures: existsSync(join(lab, 'captures')) ? readdirSync(join(lab, 'captures')) : [] };
}
test('capture-to-fixtures runs the capture, then builds fixtures from exactly that capture', () => {
  const p = pipelineSandbox();
  assert.equal(p.r.status, 0, p.r.stderr);
  assert.equal(p.captures.length, 1);
  assert.match(p.captures[0], /^\d{8}T\d{6}Z$/);
  assert.match(p.calls[0], /^capture 30 /);
  assert.ok(p.calls[1].startsWith('build ') && p.calls[1].endsWith(`/captures/${p.captures[0]}`), p.calls[1]);
});
for (const [what, opts, built] of [
  ['the capture has a hole (a stream died)', { captureExit: 1 }, false],
  ['the build or its verify fails', { buildExit: 1 }, true],
])
  test(`capture-to-fixtures fails when ${what}`, () => {
    const p = pipelineSandbox(opts);
    assert.notEqual(p.r.status, 0);
    assert.equal(p.calls.some((c) => c.startsWith('build ')), built);
    if (!built) assert.match(p.r.stderr, new RegExp(`fixtures/build.sh .*${p.captures[0]}`), 'it does not say how to build from the incomplete capture anyway');
  });

// ---------- Bicep: the principal type is not fixed to a user ----------
test('role assignments take the principal type from the azd environment', () => {
  const lab = readFileSync(join(LAB, 'infra', 'modules', 'lab.bicep'), 'utf8');
  assert.doesNotMatch(lab, /principalType:\s*'User'/, 'the type is hard-coded');
  const params = JSON.parse(readFileSync(join(LAB, 'infra', 'main.parameters.json'), 'utf8')).parameters;
  assert.equal(params.principalType?.value, '${AZURE_PRINCIPAL_TYPE=User}');
});
