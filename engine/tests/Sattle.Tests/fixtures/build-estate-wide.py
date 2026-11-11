#!/usr/bin/env python3
"""Writes the synthetic capture `estate-wide/` next to this script: three subscriptions, eight resource groups, two AKS clusters (labels red and blue, so the Kubernetes records carry a
`cluster` in their meta), six namespaces, twelve workloads, 24 pods, node and Azure metrics, warning events and Activity Log operations, with failures at different depths of the health tree
(a pod, a node, a cluster, an Event Hubs namespace), and the authored signal rules (authoring/rules.ndjson) and one curation record of each kind that a test can use (authoring/curation.ndjson,
which the tests copy away to show that the default tree needs none). No real identifiers: all names are invented. Deterministic."""
import json, os, math
from datetime import datetime, timedelta, timezone

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "estate-wide")
T0 = datetime(2026, 1, 1, 10, 0, 0, tzinfo=timezone.utc)
files, seqs = {}, {}
def iso(t): return t.strftime("%Y-%m-%dT%H:%M:%S.") + f"{t.microsecond // 1000:03d}Z"
def at(sec): return T0 + timedelta(seconds=sec)
def add(path, source, t, key, payload, meta=None):
    seqs[path] = seqs.get(path, 0) + 1
    files.setdefault(path, []).append({"v": 1, "source": source, "capturedAt": iso(t), "seq": seqs[path], "key": key, "meta": meta, "payload": payload})

CLUSTERS = [
    {"label": "red", "sub": "sub-a", "rg": "rg-red", "aks": "aks-red", "mc": "mc_rg-red_aks-red_north", "nodes": ["red-n1", "red-n2"],
     "ns": {"shop": ["cart", "checkout"], "pay": ["gateway", "ledger"], "data": ["store", "cache"]}},
    {"label": "blue", "sub": "sub-b", "rg": "rg-blue", "aks": "aks-blue", "mc": "mc_rg-blue_aks-blue_north", "nodes": ["blue-n1", "blue-n2"],
     "ns": {"web": ["site", "api"], "batch": ["etl", "report"], "mon": ["metrics", "alerts"]}},
]
def rid(sub, rg, provider, name): return f"/subscriptions/{sub}/resourcegroups/{rg}/providers/{provider}/{name}"
def cluster_id(c): return rid(c["sub"], c["rg"], "microsoft.containerservice/managedclusters", c["aks"])

# ---- Azure Resource Graph: clusters, their machines and load balancers, and resources of other resource groups and subscriptions -------------------
def arg(r, name, typ, rg, sub):
    add("arg/resources.ndjson", "arg", at(3), f"arg/resources/{r}", {"id": r, "name": name, "type": typ, "location": "north", "resourceGroup": rg, "subscriptionId": sub, "properties": None})
HUB = rid("sub-a", "rg-shared", "microsoft.eventhub/namespaces", "hub-shared")
for c in CLUSTERS:
    arg(cluster_id(c), c["aks"], "microsoft.containerservice/managedclusters", c["rg"], c["sub"])
    for n in c["nodes"]: arg(rid(c["sub"], c["mc"], "microsoft.compute/virtualmachines", n), n, "microsoft.compute/virtualmachines", c["mc"], c["sub"])
    arg(rid(c["sub"], c["mc"], "microsoft.network/loadbalancers", "kubernetes"), "kubernetes", "microsoft.network/loadbalancers", c["mc"], c["sub"])
arg(HUB, "hub-shared", "microsoft.eventhub/namespaces", "rg-shared", "sub-a")
arg(rid("sub-b", "rg-data", "microsoft.storage/storageaccounts", "stdatablue"), "stdatablue", "microsoft.storage/storageaccounts", "rg-data", "sub-b")
arg(rid("sub-c", "rg-net", "microsoft.network/virtualnetworks", "vnet-hub"), "vnet-hub", "microsoft.network/virtualnetworks", "rg-net", "sub-c")
arg(rid("sub-c", "rg-spare", "microsoft.compute/virtualmachines", "vm-spare"), "vm-spare", "microsoft.compute/virtualmachines", "rg-spare", "sub-c")

# ---- Kubernetes: nodes, deployments, pods (two per workload) and warning events; the meta names the cluster ---------------------------------------------
rv = 100
def k8s(c, kind, uid, name, ns, t, extra):
    global rv
    rv += 1
    meta = {"name": name, "uid": uid, "resourceVersion": str(rv)}
    if ns: meta["namespace"] = ns
    meta.update(extra.pop("metadata", {}))
    add("kubernetes/watch.ndjson", "kubernetes-watch", t, f"kubernetes-watch/ADDED/{kind}/{uid}/{rv}", {"type": "ADDED", "object": {"kind": kind, **extra, "metadata": meta}}, {"path": "/synthetic", "cluster": c["label"]})
PODS = {}
for c in CLUSTERS:
    for n in c["nodes"]:
        k8s(c, "Node", f"uid-{n}", n, None, at(0), {"spec": {"providerID": f"azure:///subscriptions/{c['sub']}/resourceGroups/{c['mc']}/providers/Microsoft.Compute/virtualMachines/{n}"}})
    for ns, deps in c["ns"].items():
        for d in deps:
            k8s(c, "Deployment", f"dep-{c['label']}-{d}", d, ns, at(0.2), {"spec": {"replicas": 2}})
            PODS[(c["label"], ns, d)] = []
            for i in (1, 2):
                name = f"{d}-7a1-{i}"; PODS[(c["label"], ns, d)].append(name)
                k8s(c, "Pod", f"uid-{c['label']}-{name}", name, ns, at(1), {"metadata": {"ownerReferences": [{"kind": "ReplicaSet", "name": f"{d}-7a1"}]}, "spec": {"nodeName": c["nodes"][i - 1]},
                    "status": {"phase": "Running", "conditions": [{"type": "Ready", "status": "True"}], "containerStatuses": [{"restartCount": 0}]}})
# warnings: the ledger pod of red/pay is the deep failure (six events in one minute), one event elsewhere is noise below the rule
ev = 0
def warn(c, ns, pod, sec):
    global ev
    ev += 1
    k8s(c, "Event", f"ev-{ev}", f"{pod}.{ev}", ns, at(sec), {"involvedObject": {"kind": "Pod", "name": pod, "namespace": ns}, "reason": "BackOff", "type": "Warning", "message": "back-off restarting container",
        "count": 1, "lastTimestamp": iso(at(sec))})
for s in (200, 205, 210, 215, 220, 225): warn(CLUSTERS[0], "pay", "ledger-7a1-1", s)
warn(CLUSTERS[1], "web", "site-7a1-2", 120)

# ---- Prometheus node drop and forward rates per cluster; blue-n2 drops hard from minute 4 ---------------------------------------------------------------
for minute in range(10):
    for metric in ["cilium_drop_rate", "cilium_forward_rate"]:
        for c in CLUSTERS:
            result = []
            for nn in c["nodes"]:
                vals = []
                for k in range(2):
                    ts = (T0 + timedelta(seconds=minute * 60 + k * 30)).timestamp()
                    if metric == "cilium_drop_rate": v = 60.0 if nn == "blue-n2" and minute >= 4 else 0.002
                    else: v = 150 + 40 * math.sin(minute + k)
                    vals.append([ts, f"{v:.4f}"])
                result.append({"metric": {"instance": nn, "direction": "egress"}, "values": vals})
            add("prometheus/range.ndjson", "prometheus", at(minute * 60 + 62), f"prometheus/{c['label']}/{metric}/{minute}", {"status": "success", "data": {"resultType": "matrix", "result": result}}, {"name": metric, "query": metric, "step": "30s", "cluster": c["label"]})

# ---- Azure Monitor: cluster CPU (aks-red unhealthy from minute 3), Event Hubs server errors (hub-shared degraded from minute 5) -----------------------------------
for minute in range(10):
    for c in CLUSTERS:
        cpu = (95 if minute >= 3 else 40) if c["label"] == "red" else 35 + (minute % 3)
        add("azure-metrics/aks.ndjson", "azure-metrics", at(minute * 60 + 61), f"azure-metrics/cpu/{c['label']}/{minute}", {"value": [{"name": {"value": "node_cpu_usage_percentage"}, "timeseries": [{"data": [
            {"timeStamp": iso(at(minute * 60)), "average": cpu}]}]}]}, {"resource": "aks", "resourceId": cluster_id(c)})
    add("azure-metrics/eventhubs.ndjson", "azure-metrics", at(minute * 60 + 61), f"azure-metrics/eventhub/{minute}", {"value": [{"name": {"value": "ServerErrors"}, "timeseries": [{"data": [
        {"timeStamp": iso(at(minute * 60)), "average": 3 if minute >= 5 else 0}]}]}]}, {"resource": "eventhub", "resourceId": HUB})

# ---- Activity Log operations on the clusters (evidence) -----------------------------------------------------------------------------------------------------
for i, c in enumerate(CLUSTERS):
    for j in range(3):
        t = at(40 + 90 * j + 20 * i)
        add("activity-log/backfill.ndjson", "activity-log", t + timedelta(seconds=2), f"activity-log/{c['label']}-{j}", {"eventTimestamp": iso(t), "resourceId": cluster_id(c), "status": {"value": "Succeeded"},
            "operationName": {"value": "microsoft.containerservice/managedclusters/write"}, "eventDataId": f"mgmt-{c['label']}-{j}", "caller": "operator@example.invalid"}, {"via": "synthetic"})

# ---- authored: signal rules (a table: element type, meter, thresholds with hysteresis, severity) and, separately, curation ---------------------------------------
RULES = [("cpu-high", "microsoft.containerservice/managedclusters", "node_cpu_usage_percentage", 70, 3, 60, 3, 1), ("cpu-critical", "microsoft.containerservice/managedclusters", "node_cpu_usage_percentage", 90, 3, 80, 3, 2),
         ("node-drops", "node", "cilium_drop_rate", 5, 2, 2, 2, 1), ("node-drops-critical", "node", "cilium_drop_rate", 50, 2, 20, 2, 2),
         ("pod-warnings", "pod", "warnings", 1, 1, 0, 3, 1), ("pod-warnings-critical", "pod", "warnings", 5, 1, 1, 3, 2),
         ("hub-errors", "microsoft.eventhub/namespaces", "ServerErrors", 1, 1, 0, 3, 1)]
for r, typ, meter, above, ea, below, xa, sev in RULES:
    add("authoring/rules.ndjson", "authoring", at(0.7), f"authoring/signalRule/{r}", {"kind": "signalRule", "id": r, "type": typ, "meter": meter, "above": above, "enterAfter": ea, "below": below, "exitAfter": xa, "severity": sev, "invert": False})
add("authoring/curation.ndjson", "authoring", at(0.8), "authoring/curation/g1", {"kind": "curation", "id": "g1", "action": "group", "target": "ns/red/shop", "value": "Checkout platform"})
add("authoring/curation.ndjson", "authoring", at(0.8), "authoring/curation/g2", {"kind": "curation", "id": "g2", "action": "group", "target": "ns/red/pay", "value": "Checkout platform"})
add("authoring/curation.ndjson", "authoring", at(0.8), "authoring/curation/c1", {"kind": "curation", "id": "c1", "action": "critical", "target": "wl/red/pay/ledger"})

for path, lines in files.items():
    full = os.path.join(OUT, path)
    os.makedirs(os.path.dirname(full), exist_ok=True)
    with open(full, "w") as f:
        for line in lines: f.write(json.dumps(line, separators=(",", ":")) + "\n")
print(f"wrote {sum(len(v) for v in files.values())} records in {len(files)} files to {OUT}")
