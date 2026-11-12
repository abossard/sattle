// One Event Hubs destination: a queue bounded in bytes, batched sends in arrival order, retry without reordering.
// Events are queued as serialized JSON text, so the bound is a real memory bound.
// ev = { body: <JSON text>, at: <ISO time of the event>, seq }. When the queue is full the oldest events are dropped and
// one marker takes their place at the head of the queue: it is built by `marker({ count, from, to, seq })` and spans
// exactly the dropped events, so the stream says which window is incomplete.
export function sink(client, partitionId, { maxBytes = Number(process.env.QUEUE_MAX_MB ?? 64) * 1024 * 1024, marker } = {}) {
  let queue = [];
  let queuedBytes = 0;
  let flushing = false;
  let sent = 0;
  let dropped = 0; // total

  const size = (e) => e.body.length;
  const makeMarker = (m) => { const body = marker(m); return { body, at: m.to, seq: m.seq, loss: m }; };

  // Sends in order and returns how many events the hub accepted, also when a later batch fails.
  const send = async (items) => {
    let accepted = 0;
    let batch = await client.createBatch({ partitionId });
    let inBatch = 0;
    const commit = async () => { if (batch.count) { await client.sendBatch(batch); accepted += inBatch; sent += batch.count; } };
    try {
      for (const e of items) {
        if (!batch.tryAdd(e)) { await commit(); batch = await client.createBatch({ partitionId }); inBatch = 0; batch.tryAdd(e); }
        inBatch++;
      }
      await commit();
    } catch (err) { err.accepted = accepted; throw err; }
    return accepted;
  };

  return {
    add(ev) {
      queue.push(ev); queuedBytes += size(ev);
      let n = 0; // hub unreachable or too slow for too long: drop the oldest and count it
      let from; let to; let seq;
      let head = queue[0]?.loss ? 1 : 0; // an earlier marker stays at the head and grows
      while (queuedBytes > maxBytes && queue.length - head - n > 1) {
        const e = queue[head + n];
        queuedBytes -= size(e); n++; if (n === 1) { from = e.at; seq = e.seq; } to = e.at;
      }
      if (!n) return;
      queue.splice(head, n); dropped += n;
      if (!marker) return;
      if (head) { // merge into the marker already there
        const m = queue[0].loss;
        queuedBytes -= size(queue[0]);
        queue[0] = makeMarker({ count: m.count + n, from: m.from, to, seq: m.seq });
      } else {
        queue.unshift(makeMarker({ count: n, from, to, seq }));
      }
      queuedBytes += size(queue[0]);
    },
    async flush() {
      if (flushing || !queue.length) return;
      flushing = true; // one flush at a time keeps batches in arrival order
      const items = queue; queue = []; queuedBytes = 0;
      try { await send(items); } catch (e) {
        // what the hub accepted is not sent again; the rest goes back in front of what arrived meanwhile
        queue = items.slice(e.accepted ?? 0).concat(queue); queuedBytes = queue.reduce((n, x) => n + size(x), 0); throw e;
      } finally { flushing = false; }
    },
    stats: () => ({ sent, queued: queue.length, queuedMB: Math.round(queuedBytes / 1e5) / 10, dropped }),
    idle: () => !flushing,
  };
}
