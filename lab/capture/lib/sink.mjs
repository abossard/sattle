// One Event Hubs destination: a queue bounded in bytes, batched sends in arrival order, retry without reordering.
// Events are queued as serialized JSON text, so the bound is a real memory bound.
export function sink(client, partitionId, { maxBytes = Number(process.env.QUEUE_MAX_MB ?? 64) * 1024 * 1024 } = {}) {
  let queue = [];
  let queuedBytes = 0;
  let flushing = false;
  let sent = 0;
  let dropped = 0; // total
  let droppedUnreported = 0;

  const send = async (items) => {
    let batch = await client.createBatch({ partitionId });
    for (const ev of items) {
      if (!batch.tryAdd(ev)) { await client.sendBatch(batch); sent += batch.count; batch = await client.createBatch({ partitionId }); batch.tryAdd(ev); }
    }
    if (batch.count) { await client.sendBatch(batch); sent += batch.count; }
  };

  return {
    add(ev) { // ev = { body: <JSON text> }
      queue.push(ev); queuedBytes += ev.body.length;
      let n = 0; // hub unreachable or too slow for too long: drop the oldest and count it
      while (queuedBytes > maxBytes && queue.length > 1) { queuedBytes -= queue[n].body.length; n++; }
      if (n) { queue.splice(0, n); dropped += n; droppedUnreported += n; }
    },
    async flush() {
      if (flushing || !queue.length) return;
      flushing = true; // one flush at a time keeps batches in arrival order
      const items = queue; queue = []; queuedBytes = 0;
      try { await send(items); } catch (e) { queue = items.concat(queue); queuedBytes = queue.reduce((n, x) => n + x.body.length, 0); throw e; } finally { flushing = false; }
    },
    takeDropped() { const n = droppedUnreported; droppedUnreported = 0; return n; },
    stats: () => ({ sent, queued: queue.length, queuedMB: Math.round(queuedBytes / 1e5) / 10, dropped }),
    idle: () => !flushing,
  };
}
