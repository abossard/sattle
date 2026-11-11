// A Kubernetes watch that survives: lists, watches from the list's resourceVersion, resumes after drops,
// and relists (with markers in the stream) when the server says the resourceVersion expired.
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { startChild, sleep } from './supervise.mjs';

const exec = promisify(execFile);
const EXPIRED = /too old resource version|Expired|\b410\b|Gone/i;

export async function watchKind({ path, kubeconfig, emit, marker, isStopping, register, watchSeconds = 300, startRv }) {
  const env = { ...process.env, KUBECONFIG: kubeconfig };
  let rv = startRv ?? null; // startRv is a test knob: a bogus value provokes the expiry path
  let reason = 'start';
  let backoff = 1000;

  while (!isStopping()) {
    if (rv === null) { rv = await relist(reason); if (rv === null) { await sleep(backoff); backoff = Math.min(backoff * 2, 30_000); continue; } }
    backoff = 1000;
    const startRv0 = rv;
    let expired = false;
    const child = startChild('kubectl', ['get', '--raw', `${path}?watch=1&allowWatchBookmarks=true&resourceVersion=${rv}&timeoutSeconds=${watchSeconds}`], {
      env,
      onLine: (line) => {
        let ev;
        try { ev = JSON.parse(line); } catch { return; }
        if (ev.type === 'ERROR') { expired ||= ev.object?.code === 410 || EXPIRED.test(ev.object?.message ?? ''); return; }
        const next = ev.object?.metadata?.resourceVersion;
        if (next) rv = next;
        emit('kubernetes-watch', ev, { path, startResourceVersion: startRv0 });
      },
    });
    register(child);
    const { code, stderr } = await child.done;
    if (isStopping()) return;
    if (expired || EXPIRED.test(stderr)) { rv = null; reason = 'expired'; marker({ event: 'watch-expired', path, lastResourceVersion: startRv0 }); continue; }
    if (code !== 0) { marker({ event: 'watch-error', path, code, stderr: stderr.slice(-200) }); await sleep(backoff); backoff = Math.min(backoff * 2, 30_000); }
    // exit 0 is the server-side timeout: resume from the latest resourceVersion
  }

  // List current state into the stream between markers. Returns the list's resourceVersion, or null on failure.
  async function relist(why) {
    marker({ event: 'relist-start', path, reason: why });
    try {
      const { stdout } = await exec('kubectl', ['get', '--raw', path], { env, maxBuffer: 512 * 1024 * 1024 });
      const list = JSON.parse(stdout);
      const meta = { list: { kind: list.kind, apiVersion: list.apiVersion, resourceVersion: list.metadata.resourceVersion }, path, relist: why };
      for (const item of list.items ?? []) emit('kubernetes-list', item, meta);
      marker({ event: 'relist-end', path, reason: why, resourceVersion: list.metadata.resourceVersion, count: (list.items ?? []).length });
      return list.metadata.resourceVersion;
    } catch (e) {
      marker({ event: 'relist-failed', path, reason: why, error: String(e.message).slice(0, 200) });
      return null;
    }
  }
}
