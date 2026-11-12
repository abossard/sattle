#!/usr/bin/env node
// Append every event received from one hub to NDJSON as envelopes. Runs until SIGINT/SIGTERM, or --seconds plus a drain.
// usage: eh-consume.mjs --namespace <name> --hub <hub> --out <file> [--from earliest|latest] [--seconds N]
import { appendFileSync } from 'node:fs';
import { parseArgs } from 'node:util';
import { consumer, earliestEventPosition, latestEventPosition } from './lib/eventhub.mjs';
import { envelope } from './lib/envelope.mjs';
import { drainState } from './lib/drain.mjs';

const { values: a } = parseArgs({
  options: { namespace: { type: 'string' }, hub: { type: 'string' }, out: { type: 'string' }, from: { type: 'string', default: 'earliest' }, seconds: { type: 'string' } },
});
const client = consumer(a.namespace, a.hub);
const startPosition = a.from === 'latest' ? latestEventPosition : earliestEventPosition;

// Event body is JSON for everything we send and for Azure diagnostic settings; keep text otherwise.
const bodyOf = (b) => (typeof b === 'string' || b instanceof Uint8Array ? tryJson(Buffer.from(b).toString('utf8')) : b);
const tryJson = (s) => { try { return JSON.parse(s); } catch { return { text: s }; } };

let seq = 0;
let lastEventAt = Date.now();
const seen = new Map(); // partition id -> last sequence number received
const sub = client.subscribe(
  {
    processEvents: async (events, ctx) => {
      for (const ev of events) {
        const e = envelope({
          source: `eventhub:${a.hub}`,
          capturedAt: new Date().toISOString(),
          seq: seq++,
          payload: bodyOf(ev.body),
          meta: { partitionId: ctx.partitionId, sequenceNumber: ev.sequenceNumber, offset: ev.offset, enqueuedTimeUtc: ev.enqueuedTimeUtc.toISOString(), properties: ev.properties ?? {} },
        });
        // Delivery identity of an Event Hubs event is (partition, sequence number).
        e.key = `eventhub:${a.hub}/${ctx.partitionId}/${ev.sequenceNumber}`;
        appendFileSync(a.out, JSON.stringify(e) + '\n');
        seen.set(ctx.partitionId, ev.sequenceNumber);
      }
      if (events.length) lastEventAt = Date.now();
    },
    processError: async (err) => console.error('consume error', err.message),
  },
  { startPosition, maxBatchSize: 1000, maxWaitTimeInSeconds: 5 },
);

const stop = async () => { await sub.close(); await client.close(); console.error(`eh-consume ${a.hub}: ${seq} events`); process.exit(0); };
process.on('SIGINT', stop);
process.on('SIGTERM', stop);
// --seconds ends the window. Then read what the hub held at that moment and stop. Quiet is not waited for (a forwarder
// next to the capture keeps the hub busy); a 10 s quiet and a cap of 2 minutes only guard against a lost event.
const endOfWindow = async () => {
  const windowEnd = Date.now();
  const targets = new Map();
  for (const id of await client.getPartitionIds()) {
    const p = await client.getPartitionProperties(id);
    targets.set(id, p.isEmpty ? -1 : p.lastEnqueuedSequenceNumber);
  }
  const t = setInterval(() => {
    const { done, reason } = drainState({ targets, seen, now: Date.now(), windowEnd, lastEventAt });
    if (done) { clearInterval(t); console.error(`eh-consume ${a.hub}: drained (${reason})`); stop(); }
  }, 500);
};
if (a.seconds) setTimeout(() => endOfWindow().catch((e) => { console.error('end of window failed', e.message); stop(); }), Number(a.seconds) * 1000);
