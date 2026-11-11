// Run a long-lived child process forever: restart with exponential backoff, stream its stdout lines.
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Each child leads its own process group so a stop kills the whole tree (a script and its port-forward).
export function startChild(cmd, args, { env, onLine, onStderr }) {
  const p = spawn(cmd, args, { env, stdio: ['ignore', 'pipe', 'pipe'], detached: true });
  if (onLine) createInterface({ input: p.stdout }).on('line', onLine);
  let err = '';
  p.stderr.on('data', (d) => { err = (err + d).slice(-2000); onStderr?.(String(d)); });
  const done = new Promise((resolve) => p.on('close', (code, signal) => resolve({ code, signal, stderr: err })));
  const kill = () => { try { process.kill(-p.pid, 'SIGTERM'); } catch { /* already gone */ } };
  return { done, kill, pid: p.pid };
}

// `run(attempt)` starts one child and returns { done, kill }. `onExit` sees how it ended and may return env tweaks.
export async function supervise({ name, run, onExit, isStopping, register }) {
  let backoff = 1000;
  for (let attempt = 0; !isStopping(); attempt++) {
    const startedAt = Date.now();
    const child = run(attempt);
    register(child);
    const result = await child.done;
    if (isStopping()) return;
    const ranMs = Date.now() - startedAt;
    if (ranMs > 60_000) backoff = 1000; // it was healthy for a while: next failure retries quickly
    onExit?.({ name, attempt, ...result, ranMs, backoffMs: backoff });
    await sleep(backoff);
    backoff = Math.min(backoff * 2, 30_000);
  }
}
