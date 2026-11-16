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
  // one datapoint (the forwarder sends each on its own): resource, metric, series (its dimension values) and time.
  // A response with more datapoints (a capture) has no natural id and keeps the content hash.
  'azure-metrics': (p, meta) => {
    const [m, more] = p?.value ?? [];
    const [s, more2] = m?.timeseries ?? [];
    const [d, more3] = s?.data ?? [];
    if (!d || more || more2 || more3) return undefined;
    return `${meta?.resourceId}/${m.name?.value}/${JSON.stringify((s.metadatavalues ?? []).map((x) => [x.name?.value, x.value]))}/${d.timeStamp}`;
  },
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

// The cluster a Kubernetes or Prometheus record comes from, in the envelope meta, where the engine reads it (Hubble
// flows carry their own `cluster_name`). Without a name the envelope is unchanged and the engine uses its default.
const clustered = new Set(['kubernetes-watch', 'kubernetes-list', 'prometheus']);
export const withCluster = (e, cluster) => (cluster && clustered.has(e.source) ? { ...e, meta: { ...(e.meta ?? {}), cluster } } : e);

// The cluster's name from `kubectl get configmap cilium-config`, or the one given with --cluster. A cluster without Cilium
// (the configmap is not found) has none, and the engine uses its default; any other failure (a timeout, no access) is an
// error, because records without the name would not meet the flows that name it.
export function clusterName({ given, stdout, error }) {
  if (given) return given;
  if (!error) return stdout.trim();
  if (/NotFound/.test(error.stderr ?? '')) return '';
  throw new Error(`cannot read the cluster name from configmap kube-system/cilium-config (${String(error.stderr || error.message).trim().slice(0, 200)}); pass --cluster NAME`);
}
