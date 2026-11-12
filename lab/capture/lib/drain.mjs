// When the consumer may stop. After the window closes it must still read what the hub held at that moment, but it must
// not wait for quiet: a forwarder running next to the capture keeps the hub busy and quiet never comes.
//   targets  partition id -> last sequence number the hub held when the window closed (-1: the partition is empty)
//   seen     partition id -> last sequence number this consumer received
// Stops when every partition has reached its target; otherwise when the hub has been quiet for `quietMs`
// (a lost event, or no targets known); and in any case `capMs` after the window closed.
export function drainState({ targets, seen, now, windowEnd, lastEventAt, quietMs = 10_000, capMs = 120_000 }) {
  if (targets.size && [...targets].every(([p, t]) => t < 0 || (seen.get(p) ?? -1) >= t)) return { done: true, reason: 'reached' };
  if (now - lastEventAt > quietMs) return { done: true, reason: 'quiet' };
  if (now - windowEnd > capMs) return { done: true, reason: 'cap' };
  return { done: false, reason: null };
}
