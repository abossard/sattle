// The arguments the engine's space needs to point at this data, read from the scrubbed records: each value is the
// alias as the fixture files carry it, never a real value. An argument the records do not show is left out, and the
// engine then keeps its own default for it.
//   workloadNamespace  namespace of the Service in the Kubernetes list
//   clusterLabel       the cluster name on Hubble flows (what the engine detects as the cluster of the directory)
//   aksName            name of the managed cluster resource
//   nodeResourceGroup  the cluster's node resource group (`mc_...`)
//   hubResourceGroup   resource group of the Event Hubs namespace
const strings = function* (v) {
  if (Array.isArray(v)) for (const x of v) yield* strings(x);
  else if (v && typeof v === 'object') for (const x of Object.values(v)) yield* strings(x);
  else if (typeof v === 'string') yield v;
};
const firstMatch = (records, re) => { for (const r of records) for (const s of strings(r)) { const m = s.match(re); if (m) return m[1]; } return undefined; };

// records: Map or object of fixture path -> scrubbed records
export function spaceArguments(recordsByFile) {
  const files = recordsByFile instanceof Map ? Object.fromEntries(recordsByFile) : recordsByFile;
  const all = Object.values(files).flat();
  const service = (files['kubernetes/list.ndjson'] ?? []).find((r) => r.meta?.list?.kind === 'ServiceList');
  const flow = (files['hubble/flows.ndjson'] ?? []).map((r) => r.payload?.flow).find((f) => f?.source?.cluster_name || f?.destination?.cluster_name);
  const args = {
    workloadNamespace: service?.payload?.metadata?.namespace,
    clusterLabel: flow?.source?.cluster_name || flow?.destination?.cluster_name,
    aksName: firstMatch(all, /\/providers\/microsoft\.containerservice\/managedclusters\/([^/\s"]+)/i),
    nodeResourceGroup: firstMatch(all, /\/resourcegroups\/(mc_[^/\s"]+)/i) ?? firstMatch(all, /^(mc_[^/\s"]+)$/i),
    hubResourceGroup: firstMatch(all, /\/resourcegroups\/([^/]+)\/providers\/microsoft\.eventhub\/namespaces\//i),
  };
  return Object.fromEntries(Object.entries(args).filter(([, v]) => typeof v === 'string' && v));
}
