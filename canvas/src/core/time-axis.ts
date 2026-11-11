// One time axis for a graph space. Revisions sit at their commit time, telemetry frames at their window start,
// so a position on the axis selects the structure and the telemetry valid at that instant. Times are seconds.

/** Number of entries in an ascending array that are at or before t. */
export function countAtOrBefore(sorted: readonly number[], t: number): number {
  let lo = 0, hi = sorted.length;
  while (lo < hi) { const m = (lo + hi) >> 1; if (sorted[m] <= t) lo = m + 1; else hi = m; }
  return lo;
}

/** Sorted unique union of several time lists. */
export function mergeTimes(lists: readonly (readonly number[])[]): number[] {
  const all = new Set<number>();
  for (const l of lists) for (const t of l) all.add(t);
  return [...all].sort((a, b) => a - b);
}

/**
 * Times for patches whose source gave none: revision k is placed at the k-th telemetry window (or at k when there is no telemetry).
 * This aligns by order only; callers should mark such a graph as ordinal.
 */
export function ordinalTimes(patchCount: number, windowTimes: readonly number[]): number[] {
  // fewer windows than revisions (one window, say): placing revisions on them would stack them on one instant, so they go by order alone
  return Array.from({ length: patchCount }, (_, i) => (windowTimes.length >= patchCount ? windowTimes[Math.min(i + 1, windowTimes.length - 1)] : i + 1));
}

/**
 * Commit time of every patch. Times the source gave are kept; the others (history read over HTTP carries none) are placed by
 * order on the telemetry windows and then pulled back so the sequence never decreases and never passes a time the source gave.
 */
export function resolvePatchTimes(sourceTimes: readonly number[], windowTimes: readonly number[]): { times: number[]; source: "source" | "ordinal" | "mixed" } {
  const known = sourceTimes.filter((t) => Number.isFinite(t)).length;
  if (known === sourceTimes.length && known > 0) return { times: [...sourceTimes], source: "source" };
  const ord = ordinalTimes(sourceTimes.length, windowTimes);
  if (known === 0) return { times: ord, source: "ordinal" };
  const t = sourceTimes.map((s, i) => (Number.isFinite(s) ? s : ord[i]));
  for (let i = t.length - 2; i >= 0; i--) if (!Number.isFinite(sourceTimes[i]) && t[i] > t[i + 1]) t[i] = t[i + 1];
  for (let i = 1; i < t.length; i++) if (t[i] < t[i - 1]) t[i] = t[i - 1];
  return { times: t, source: "mixed" };
}

/** Revision cursor (patches applied) at time t. */
export const revisionCursorAt = (patchTimes: readonly number[], t: number): number => countAtOrBefore(patchTimes, t);

/** Index of the telemetry frame valid at time t: the last frame starting at or before t, or 0 before the first frame. */
export function frameIndexAt(frameTimes: readonly number[], t: number): number {
  return Math.max(0, countAtOrBefore(frameTimes, t) - 1);
}

/** Times after the year 2000 are instants (engine windows); smaller ones are seconds on a synthetic axis. */
const EPOCH_FLOOR = 946684800;

export function formatTime(t: number): string {
  if (t > EPOCH_FLOOR) { const d = new Date(t * 1000).toISOString(); return `${d.slice(5, 10)} ${d.slice(11, 19)}Z`; }
  if (Math.abs(t) < 120) return `${Number.isInteger(t) ? t : t.toFixed(1)} s`;
  const m = Math.floor(t / 60), s = Math.round(t - m * 60);
  return m < 120 ? `${m} min ${s} s` : `${(t / 3600).toFixed(2)} h`;
}
