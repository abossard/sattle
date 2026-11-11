// Envelope: the only shape the capture tooling adds around a native record.
//   { v, source, capturedAt, seq, key, meta?, payload }
// Everything here is a pure calculation. File and network actions live elsewhere.
import { createHash } from 'node:crypto';

const sha = (s) => createHash('sha256').update(s).digest('hex').slice(0, 32);

// Natural identity per source when the native record has one, else a content hash.
// Keys identify a delivery: re-reading the same record yields the same key.
const keyers = {
  hubble: (p) => p?.flow?.uuid ?? (p?.lost_events ? `lost:${p.node_name}:${p.time}` : undefined),
  'kubernetes-watch': (p) => {
    const o = p?.object ?? {};
    const m = o.metadata ?? {};
    return m.uid ? `${p.type}/${o.kind}/${m.uid}/${m.resourceVersion}` : `${p?.type}/${o.kind}/${m.resourceVersion}`;
  },
  'kubernetes-list': (p, meta) => (p?.metadata?.uid ? `${meta?.list?.kind}/${p.metadata.uid}/${p.metadata.resourceVersion}` : undefined),
  arg: (p, meta) => (p?.id ? `${meta?.query ?? ''}/${p.id}` : undefined),
  'activity-log': (p) => p?.eventDataId ?? (p?.operationId ? `${p.operationId}/${p.operationName?.value ?? p.operationName}` : undefined),
};

export function keyFor(source, payload, meta) {
  const base = source.split(':')[0];
  const natural = keyers[source]?.(payload, meta) ?? keyers[base]?.(payload, meta);
  return natural ? `${source}/${natural}` : `${source}/sha256:${sha(JSON.stringify([payload, meta?.hashSalt ?? null]))}`;
}

export function envelope({ source, capturedAt, seq, payload, meta }) {
  const e = { v: 1, source, capturedAt, seq, key: keyFor(source, payload, meta) };
  if (meta !== undefined) e.meta = meta;
  e.payload = payload;
  return e;
}

// A text line becomes a payload: JSON when it parses, otherwise { text }.
export function payloadOf(line) {
  try {
    return JSON.parse(line);
  } catch {
    return { text: line };
  }
}
