#!/usr/bin/env node
// stdin NDJSON (native records) -> envelope NDJSON appended to --out (or stdout).
// usage: wrap.mjs --source <name> [--meta '<json>'] [--out <file>]
import { appendFileSync } from 'node:fs';
import { createInterface } from 'node:readline';
import { parseArgs } from 'node:util';
import { envelope, payloadOf } from './lib/envelope.mjs';

const { values } = parseArgs({ options: { source: { type: 'string' }, meta: { type: 'string' }, out: { type: 'string' } } });
if (!values.source) throw new Error('--source is required');
const meta = values.meta ? JSON.parse(values.meta) : undefined;

let seq = 0;
for await (const line of createInterface({ input: process.stdin })) {
  if (!line.trim()) continue;
  const e = envelope({ source: values.source, capturedAt: new Date().toISOString(), seq: seq++, payload: payloadOf(line), meta });
  const text = JSON.stringify(e) + '\n';
  if (values.out) appendFileSync(values.out, text); // append per record so a killed capture keeps what it saw
  else process.stdout.write(text);
}
