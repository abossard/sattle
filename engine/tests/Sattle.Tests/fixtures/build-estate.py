#!/usr/bin/env python3
"""Writes the synthetic capture `estate/` next to this script: a small invented estate (one cluster, one workload namespace, two nodes, three
services) with every source the engine reads, so that every operator of the projection language has data in the master graph and in the
health chain. No real identifiers: all names are invented. Deterministic: running it again rewrites identical files.
The tracked definitions have the workload arguments of this estate (namespace shop, cluster label synth, cluster aks-synth), so it is served with no overlay (estate/space/acme.space.sattle is a copy of the tracked space file, kept for launch entries that pass it)."""
import json, os, math
from datetime import datetime, timedelta, timezone

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "estate")
T0 = datetime(2026, 1, 1, 10, 0, 0, tzinfo=timezone.utc)
SUB = "sub-0001"
CLUSTER_ID = f"/subscriptions/{SUB}/resourcegroups/rg-synth/providers/microsoft.containerservice/managedclusters/aks-synth"
NODE_RG = "mc_rg-synth_aks-synth_north"
NODES = ["node-a", "node-b"]
files = {}
seqs = {}

def iso(t): return t.strftime("%Y-%m-%dT%H:%M:%S.") + f"{t.microsecond // 1000:03d}Z"
def at(sec): return T0 + timedelta(seconds=sec)

def add(path, source, t, key, payload, meta=None):
    seqs[path] = seqs.get(path, 0) + 1
    files.setdefault(path, []).append({"v": 1, "source": source, "capturedAt": iso(t), "seq": seqs[path], "key": key, "meta": meta, "payload": payload})

# ---- Kubernetes: nodes, deployments, services, pods, endpoint slices, warning events ------------------------------------------
def k8s(kind, uid, name, ns, t, extra, rv):
    meta = {"name": name, "uid": uid, "resourceVersion": str(rv)}
    if ns: meta["namespace"] = ns
    meta.update(extra.pop("metadata", {}))
    add("kubernetes/watch.ndjson", "kubernetes-watch", t, f"kubernetes-watch/ADDED/{kind}/{uid}/{rv}", {"type": "ADDED", "object": {"kind": kind, **extra, "metadata": meta}}, {"path": "/synthetic"})

rv = 100
for n in NODES:
    rv += 1
    k8s("Node", f"uid-{n}", n, None, at(0), {"spec": {"providerID": f"azure:///subscriptions/{SUB}/resourceGroups/{NODE_RG}/providers/Microsoft.Compute/virtualMachines/{n}"}}, rv)
DEPLOYMENTS = {"web": 2, "api": 2, "db": 1}
for d, r in DEPLOYMENTS.items():
    rv += 1
    k8s("Deployment", f"dep-{d}", d, "shop", at(0.2), {"spec": {"replicas": r}}, rv)
    rv += 1
    k8s("Service", f"svc-{d}", d, "shop", at(0.4), {}, rv)
PODS = [("web-7d4-1", "web", "node-a", 1), ("web-7d4-2", "web", "node-b", 1), ("api-5c9-1", "api", "node-a", 1), ("api-5c9-2", "api", "node-b", 1), ("db-6b8-1", "db", "node-b", 330)]
for name, dep, node, sec in PODS:       # the db pod appears 5.5 minutes in: flows name it long before, so its identity is merged late
    rv += 1
    k8s("Pod", f"uid-{name}", name, "shop", at(sec), {"metadata": {"ownerReferences": [{"kind": "ReplicaSet", "name": name.rsplit("-", 1)[0]}]}, "spec": {"nodeName": node},
        "status": {"phase": "Running", "conditions": [{"type": "Ready", "status": "True"}], "containerStatuses": [{"restartCount": 0}]}}, rv)
for dep in DEPLOYMENTS:
    pods = [p for p in PODS if p[1] == dep]
    rv += 1
    k8s("EndpointSlice", f"slice-{dep}", f"{dep}-slice", "shop", at(2), {"metadata": {"labels": {"kubernetes.io/service-name": dep}},
        "endpoints": [{"addresses": [f"10.0.{i}.{j}"], "targetRef": {"kind": "Pod", "uid": f"uid-{p[0]}"}} for j, p in enumerate(pods, 1) for i in [list(DEPLOYMENTS).index(dep)]]}, rv)
# the db pod, the leaf of web -> api -> db, is the only one with warnings (three in each of two minutes), so that api, web and the workload are Degraded only by the roll-up
for i, sec in enumerate([345, 350, 355, 405, 410, 415]):
    rv += 1
    pod = "db-6b8-1"
    k8s("Event", f"ev-{i}", f"{pod}.{i}", "shop", at(sec), {"involvedObject": {"kind": "Pod", "name": pod, "namespace": "shop"}, "reason": "BackOff", "type": "Warning", "message": "back-off restarting container",
        "count": 1, "lastTimestamp": iso(at(sec))}, rv)

# ---- Hubble flows: web -> api -> db every 5 s for 10 minutes; the db pod drops traffic from minute 4 to minute 7 -------------------
n = 0
for sec in range(10, 600, 5):
    for (src, dst) in [("web-7d4-1", "api-5c9-1"), ("web-7d4-2", "api-5c9-2"), ("api-5c9-1", "db-6b8-1"), ("api-5c9-2", "db-6b8-1")]:
        verdict = "DROPPED" if dst == "db-6b8-1" and 240 <= sec < 420 and (sec // 5) % 2 == 0 else "FORWARDED"
        t = at(sec + (0.001 * (n % 7)))
        add("hubble/flows.ndjson", "hubble", t, f"hubble/flow-{n}", {"flow": {"time": iso(t), "uuid": f"flow-{n}", "verdict": verdict,
            "source": {"namespace": "shop", "cluster_name": "synth", "pod_name": src}, "destination": {"namespace": "shop", "cluster_name": "synth", "pod_name": dst}}, "node_name": "node-a", "time": iso(t)})
        n += 1

# ---- Azure Resource Graph: the cluster, its node machines, load balancer, virtual network, and an Event Hubs namespace -------------
def arg(rid, name, typ, rg, loc="north"):
    add("arg/resources.ndjson", "arg", at(3), f"arg/resources/{rid}", {"id": rid, "name": name, "type": typ, "location": loc, "resourceGroup": rg, "subscriptionId": SUB, "properties": None})
arg(CLUSTER_ID, "aks-synth", "microsoft.containerservice/managedclusters", "rg-synth")
for nn in NODES: arg(f"/subscriptions/{SUB}/resourcegroups/{NODE_RG}/providers/microsoft.compute/virtualmachines/{nn}", nn, "microsoft.compute/virtualmachines", NODE_RG)
arg(f"/subscriptions/{SUB}/resourcegroups/{NODE_RG}/providers/microsoft.network/loadbalancers/kubernetes", "kubernetes", "microsoft.network/loadbalancers", NODE_RG)
arg(f"/subscriptions/{SUB}/resourcegroups/{NODE_RG}/providers/microsoft.network/virtualnetworks/vnet-synth", "vnet-synth", "microsoft.network/virtualnetworks", NODE_RG)
arg(f"/subscriptions/{SUB}/resourcegroups/rg-hub/providers/microsoft.eventhub/namespaces/hub-synth", "hub-synth", "microsoft.eventhub/namespaces", "rg-hub")

# ---- Prometheus: node drop rate and forward rate, two samples per node and metric every minute; node-b drops above the 1 % line in minutes 4 to 7
for minute in range(10):
    for metric in ["cilium_drop_rate", "cilium_forward_rate"]:
        result = []
        for nn in NODES:
            vals = []
            for k in range(2):
                ts = (T0 + timedelta(seconds=minute * 60 + k * 30)).timestamp()
                if metric == "cilium_drop_rate": v = 0.02 if nn == "node-b" and 4 <= minute < 8 else 0.002
                else: v = 150 + 40 * math.sin(minute + k) + (10 if nn == "node-b" else 0)
                vals.append([ts, f"{v:.4f}"])
            result.append({"metric": {"instance": nn, "direction": "egress"}, "values": vals})
        add("prometheus/range.ndjson", "prometheus", at(minute * 60 + 62), f"prometheus/{metric}/{minute}", {"status": "success", "data": {"resultType": "matrix", "result": result}}, {"name": metric, "query": metric, "step": "30s"})

# ---- Azure Monitor: cluster CPU, one point a minute, steady at 35 % (a busy cluster would degrade every component through the roll-up) ------------------------------------------------
for minute in range(10):
    add("azure-metrics/aks.ndjson", "azure-metrics", at(minute * 60 + 61), f"azure-metrics/cpu/{minute}", {"value": [{"name": {"value": "node_cpu_usage_percentage"}, "timeseries": [{"data": [
        {"timeStamp": iso(at(minute * 60)), "average": 35 + (minute % 3)}]}]}]}, {"resource": "aks", "resourceId": CLUSTER_ID})

# ---- Activity Log from two sources: the management API and the diagnostic setting (a Union of the two) -----------------------------
for i in range(10):
    t = at(30 + i * 55)
    add("activity-log/backfill.ndjson", "activity-log", t + timedelta(seconds=2), f"activity-log/mgmt-{i}", {"eventTimestamp": iso(t), "resourceId": CLUSTER_ID, "status": {"value": "Succeeded"},
        "operationName": {"value": "microsoft.containerservice/managedclusters/read"}, "eventDataId": f"mgmt-{i}", "caller": "operator@example.invalid"}, {"via": "synthetic"})
for i in range(8):
    t = at(45 + i * 70)
    add("eventhub/sattle-activity.ndjson", "eventhub:sattle-activity", t + timedelta(seconds=3), f"eventhub:sattle-activity/1/{i}", {"records": [{"time": iso(t), "resourceId": CLUSTER_ID.upper(),
        "operationName": "MICROSOFT.CONTAINERSERVICE/MANAGEDCLUSTERS/WRITE", "category": "Administrative", "resultType": "Success", "correlationId": f"corr-{i}"}]}, {"partitionId": "1", "sequenceNumber": i})

# ---- authored business capabilities: a hierarchy (commerce > storefront, ordering; data-platform alone) and the service that realizes each leaf ----------------
CAPS = [("commerce", "Commerce", "team-shop", None), ("storefront", "Storefront", "team-shop", "commerce"), ("ordering", "Ordering", "team-shop", "commerce"), ("data-platform", "Data platform", "team-data", None)]
for cap, name, owner, parent in CAPS:
    add("authoring/capabilities.ndjson", "authoring", at(0.5), f"authoring/capability/{cap}", {"kind": "capability", "capId": cap, "name": name, "owner": owner, "status": "active", "parentId": parent})
for cap, svc in [("storefront", "web"), ("ordering", "api"), ("data-platform", "db")]:
    add("authoring/capabilities.ndjson", "authoring", at(0.6), f"authoring/realizes/{cap}/{svc}", {"kind": "realizes", "capId": cap, "cluster": "synth", "namespace": "shop", "service": svc})

# ---- authored signal rules of the health tree: a meter of an element type is a signal (thresholds with hysteresis, severity 1 degraded or 2 unhealthy) ----
RULES = [("cpu-high", "microsoft.containerservice/managedclusters", "node_cpu_usage_percentage", 70, 3, 60, 3, 1, False), ("cpu-critical", "microsoft.containerservice/managedclusters", "node_cpu_usage_percentage", 90, 3, 80, 3, 2, False),
         ("node-drops", "node", "cilium_drop_rate", 5, 2, 2, 2, 1, False), ("node-drops-critical", "node", "cilium_drop_rate", 50, 2, 20, 2, 2, False),
         ("pod-warnings", "pod", "warnings", 1, 1, 0, 3, 1, False), ("pod-warnings-critical", "pod", "warnings", 5, 1, 1, 3, 2, False)]
for rid, typ, meter, above, ea, below, xa, sev, inv in RULES:
    add("authoring/rules.ndjson", "authoring", at(0.7), f"authoring/signalRule/{rid}", {"kind": "signalRule", "id": rid, "type": typ, "meter": meter, "above": above, "enterAfter": ea, "below": below, "exitAfter": xa, "severity": sev, "invert": inv})

for path, lines in files.items():
    full = os.path.join(OUT, path)
    os.makedirs(os.path.dirname(full), exist_ok=True)
    with open(full, "w") as f:
        for line in lines: f.write(json.dumps(line, separators=(",", ":")) + "\n")
print(f"wrote {sum(len(v) for v in files.values())} records in {len(files)} files to {OUT}")

# ---- the space binding for this estate: the tracked space file already carries these invented workload arguments, so the copy under estate/space is the tracked file itself ----
import shutil
os.makedirs(os.path.join(OUT, "space"), exist_ok=True)
shutil.copyfile(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "definitions", "acme.space.sattle"), os.path.join(OUT, "space", "acme.space.sattle"))
