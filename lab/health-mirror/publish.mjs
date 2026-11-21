// Send the same ordered envelopes that Sattle replays from disk.
import { readdir, readFile, stat } from 'node:fs/promises';
import { join } from 'node:path';
import { producer } from '../capture/lib/eventhub.mjs';

const [namespace, hub, capture] = process.argv.slice(2);
if (!capture) throw new Error('Expected namespace, hub and local capture directory');
const records = [];
const paths = (await stat(capture)).isDirectory()
  ? (await readdir(capture)).filter(name => name.endsWith('.ndjson')).sort().map(file => join(capture, file))
  : [capture];
for (const file of paths) {
  for (const line of (await readFile(file, 'utf8')).split('\n').filter(Boolean)) {
    const { capturedAt, source } = JSON.parse(line);
    if (!source || !Number.isFinite(Date.parse(capturedAt))) throw new Error('Invalid capture envelope in ' + file);
    records.push({ line, capturedAt, source });
  }
}
if (!records.length) throw new Error('Empty capture');
records.sort((a, b) => Date.parse(a.capturedAt) - Date.parse(b.capturedAt) || a.capturedAt.localeCompare(b.capturedAt));
const client = producer(namespace, hub);
try {
  const partitionIds = await client.getPartitionIds();
  if (partitionIds.length !== 1) throw new Error('The mirror requires a dedicated single-partition hub');
  const makeBatch = () => client.createBatch({ partitionId: partitionIds[0] });
  let batch = await makeBatch();
  for (const { line } of records) {
    const event = { body: line };
    if (!batch.tryAdd(event)) {
      if (batch.count) await client.sendBatch(batch);
      batch = await makeBatch();
      if (!batch.tryAdd(event)) throw new Error('One capture envelope exceeds the Event Hubs batch limit');
    }
  }
  if (batch.count) await client.sendBatch(batch);
  console.log('Published ' + records.length + ' envelopes to ' + hub);
} finally {
  await client.close();
}
