#!/usr/bin/env node
// Append every event received from one hub to NDJSON as envelopes. Runs until SIGINT/SIGTERM, or --seconds plus a drain.
// usage: eh-consume.mjs --namespace <name> --hub <hub> --out <file> [--from earliest|latest] [--seconds N]
import { appendFileSync } from 'node:fs';
import { parseArgs } from 'node:util';
import { consumer, earliestEventPosition, latestEventPosition } from './lib/eventhub.mjs';
import { envelope } from './lib/envelope.mjs';

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
// --seconds is the earliest stop. After it, keep reading until the hub has been quiet for 10 s, so backlog is drained.
if (a.seconds) setTimeout(() => { const t = setInterval(() => { if (Date.now() - lastEventAt > 10000) { clearInterval(t); stop(); } }, 1000); }, Number(a.seconds) * 1000);
