// What identifies the capture's owner, read from the capture itself: resource, group and node names found in ids and
// name fields, and the people behind token claims and callers. Streaming: a capture can be gigabytes.
import { createReadStream, readdirSync } from 'node:fs';
import { createInterface } from 'node:readline';
import { join, relative } from 'node:path';
import { ARM_REGION, namesInArmId } from './core.mjs';

const PERSON_CLAIM = /(^|[/.])(name|givenname|given_name|surname|family_name|unique_name|upn|preferred_username|emailaddress)$/i;

const strings = (v, path, f) => {
  if (Array.isArray(v)) v.forEach((x) => strings(x, `${path}[]`, f));
  else if (v && typeof v === 'object') for (const [k, x] of Object.entries(v)) strings(x, `${path}.${k}`, f);
  else if (typeof v === 'string') f(v, path);
};

// One record's contribution to `acc` ({ names: Set, persons: Set }).
export function harvestRecord(r, acc) {
  strings(r, '', (s, path) => {
    if (/\/subscriptions\//i.test(s)) for (const region of s.match(ARM_REGION) ?? []) for (const n of namesInArmId(region)) acc.names.add(n);
    const claim = path.match(/\.claims\.(.+)$/)?.[1];
    if (claim && PERSON_CLAIM.test(claim)) addPerson(s, acc);
    if (/\.caller$/.test(path) && s.includes('@')) addPerson(s, acc);
    if (r.source === 'arg' && /^\.payload\.(name|resourceGroup)$/.test(path) && s) acc.names.add(s);
  });
  const obj = r.payload?.object ?? r.payload; // a watch event wraps the object
  const isNode = obj?.kind === 'Node' || r.meta?.list?.kind === 'NodeList';
  if (isNode && obj?.metadata?.name) acc.names.add(obj.metadata.name);
}

export function addPerson(s, acc) {
  acc.persons.add(s);
  if (s.includes('@')) { acc.persons.add(s.split('@')[0]); acc.persons.add(s.split('@')[0].split('_')[0]); }
}

const walkFiles = (d) => readdirSync(d, { withFileTypes: true }).flatMap((x) => (x.isDirectory() ? walkFiles(join(d, x.name)) : x.name.endsWith('.ndjson') ? [join(d, x.name)] : []));

// Hubble flows are the bulk of a capture and carry no resource ids; their node and pod names also come from the lists.
export async function harvestCapture(captureDir, { skip = (rel) => rel.startsWith('hubble/') } = {}) {
  const acc = { names: new Set(), persons: new Set() };
  for (const file of walkFiles(captureDir)) {
    if (skip(relative(captureDir, file))) continue;
    for await (const l of createInterface({ input: createReadStream(file), crlfDelay: Infinity })) if (l) harvestRecord(JSON.parse(l), acc);
  }
  for (const lit of (process.env.SCRUB_LITERALS ?? '').split('\n').map((x) => x.trim()).filter(Boolean)) addPerson(lit, acc);
  return acc;
}
