#!/usr/bin/env python3
"""Writes the synthetic capture `estate/` next to this script: a small invented estate (one cluster with two nodes, the workload namespace shop with the services web, api and database,
and the Azure platform the workload uses) with every source the engine reads, so that every operator of the projection language has data in the master graph and in the
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
TOTAL_MINUTES = 24
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

# ---- the workload shop: web (three pods) and api (two) run in the cluster, database is an ExternalName Service for the managed PostgreSQL server ----
SHOP_RG = f"/subscriptions/{SUB}/resourcegroups/rg-shop/providers"
CACHE_ID, STORAGE_ID, PG_ID, APIM_ID = f"{SHOP_RG}/microsoft.cache/redis/shopcache", f"{SHOP_RG}/microsoft.storage/storageaccounts/shopassets", f"{SHOP_RG}/microsoft.dbforpostgresql/flexibleservers/pg-shop", f"{SHOP_RG}/microsoft.apimanagement/service/apim-shop"
VNET_ID = f"/subscriptions/{SUB}/resourcegroups/{NODE_RG}/providers/microsoft.network/virtualnetworks/vnet-synth"
WEB_CLIENT, WEB_PRINCIPAL = "c1e7a0de-0000-4000-8000-000000000001", "9a1b2c3d-0000-4000-8000-000000000002"
# private endpoints: address in the cluster's virtual network, the resource behind it, the name it is reached by
ENDPOINTS = [("pe-shopcache", "10.2.0.4", CACHE_ID, "shopcache.redis.cache.windows.net"), ("pe-shopassets", "10.2.0.5", STORAGE_ID, "shopassets.blob.core.windows.net"), ("pe-pg-shop", "10.2.0.6", PG_ID, "pg-shop.postgres.database.azure.com")]

rv = 100
for n in NODES:
    rv += 1
    k8s("Node", f"uid-{n}", n, None, at(0), {"metadata": {"labels": {"kubernetes.azure.com/agentpool": "apps"}}, "spec": {"providerID": f"azure:///subscriptions/{SUB}/resourceGroups/{NODE_RG}/providers/Microsoft.Compute/virtualMachines/{n}"}}, rv)
rv += 1
k8s("ServiceAccount", "sa-web", "web", "shop", at(0.1), {"metadata": {"annotations": {"azure.workload.identity/client-id": WEB_CLIENT}}}, rv)
rv += 1
k8s("ServiceAccount", "sa-api", "api", "shop", at(0.1), {}, rv)
# what each Deployment names in its environment: web the cache and api, api the database Service
ENV = {"web": [{"name": "CACHE_HOST", "value": "shopcache.redis.cache.windows.net"}, {"name": "API_URL", "value": "http://api.shop.svc.cluster.local:8080"}, {"name": "LOG_LEVEL", "value": "info"}],
       "api": [{"name": "DATABASE_HOST", "value": "database.shop.svc.cluster.local"}, {"name": "DATABASE_PASSWORD", "value": "not-read"}]}
DEPLOYMENTS = {"web": 3, "api": 2}
for d, r in DEPLOYMENTS.items():
    rv += 1
    k8s("Deployment", f"dep-{d}", d, "shop", at(0.2), {"spec": {"replicas": r, "template": {"spec": {"serviceAccountName": d, "containers": [{"name": d, "env": ENV[d]}]}}}}, rv)
rv += 1
k8s("Service", "svc-web", "web", "shop", at(0.4), {"spec": {"clusterIP": "10.0.0.11"}}, rv)
rv += 1
k8s("Service", "svc-api", "api", "shop", at(0.4), {"spec": {"type": "LoadBalancer", "clusterIP": "10.0.0.12"}, "status": {"loadBalancer": {"ingress": [{"ip": "10.1.0.20"}]}}}, rv)
rv += 1
k8s("Service", "svc-database", "database", "shop", at(0.4), {"spec": {"type": "ExternalName", "externalName": "pg-shop.postgres.database.azure.com"}}, rv)
PODS = [("web-7d4-1", "web", "node-a", 1), ("web-7d4-2", "web", "node-b", 1), ("web-7d4-3", "web", "node-a", 1), ("api-5c9-1", "api", "node-a", 1), ("api-5c9-2", "api", "node-b", 330)]
for name, dep, node, sec in PODS:       # the second api pod appears 5.5 minutes in: flows name it long before, so its identity is merged late
    rv += 1
    k8s("Pod", f"uid-{name}", name, "shop", at(sec), {"metadata": {"ownerReferences": [{"kind": "ReplicaSet", "name": name.rsplit("-", 1)[0]}]}, "spec": {"nodeName": node, "serviceAccountName": dep},
        "status": {"phase": "Running", "conditions": [{"type": "Ready", "status": "True"}], "containerStatuses": [{"restartCount": 0}]}}, rv)
for dep in DEPLOYMENTS:
    pods = [p for p in PODS if p[1] == dep]
    rv += 1
    k8s("EndpointSlice", f"slice-{dep}", f"{dep}-slice", "shop", at(2), {"metadata": {"labels": {"kubernetes.io/service-name": dep}},
        "endpoints": [{"addresses": [f"10.0.{i}.{j}"], "targetRef": {"kind": "Pod", "uid": f"uid-{p[0]}"}} for j, p in enumerate(pods, 1) for i in [list(DEPLOYMENTS).index(dep)]]}, rv)
# scenario C, minutes 8 to 10: one of the three web pods crash-loops (three warnings a minute); its source keeps reporting, so the minutes after are windows of 0
ev = 0
for minute in (8, 9, 10):
    for k in range(3):
        rv += 1; sec = minute * 60 + 5 + k * 15
        k8s("Event", f"ev-{ev}", f"web-7d4-3.{ev}", "shop", at(sec), {"involvedObject": {"kind": "Pod", "name": "web-7d4-3", "namespace": "shop"}, "reason": "BackOff", "type": "Warning", "message": "back-off restarting container",
            "count": 1, "lastTimestamp": iso(at(sec))}, rv)
        ev += 1
# the forwarder's stats marker once a minute: the Kubernetes adapter has reported up to it, also when nothing changed (Def. 10.9)
for minute in range(TOTAL_MINUTES + 1):
    add("kubernetes/watch.ndjson", "sattle-forwarder", at(minute * 60 + 59), f"sattle-forwarder/stats/{minute}", {"at": iso(at(minute * 60 + 59)), "event": "stats", "uptimeSeconds": minute * 60 + 59})

# ---- Hubble flows every 5 s: web to api, web to the cache and the storage account, api to the database; the database's endpoint drops in scenario B -------------------
n = 0
for sec in range(10, TOTAL_MINUTES * 60, 5):
    pairs = [("web-7d4-1", "api-5c9-1", None), ("web-7d4-2", "api-5c9-2", None), ("web-7d4-3", "api-5c9-1", None),
             ("web-7d4-1", None, ENDPOINTS[0]), ("web-7d4-2", None, ENDPOINTS[1]), ("api-5c9-1", None, ENDPOINTS[2]), ("api-5c9-2", None, ENDPOINTS[2])]
    for (src, dst, pe) in pairs:
        down = pe is ENDPOINTS[2] and 11 * 60 <= sec < 14 * 60
        verdict = "DROPPED" if down else "FORWARDED"
        t = at(sec + (0.001 * (n % 7)))
        flow = {"time": iso(t), "uuid": f"flow-{n}", "verdict": verdict, "source": {"namespace": "shop", "cluster_name": "synth", "pod_name": src}}
        if dst: flow["destination"] = {"namespace": "shop", "cluster_name": "synth", "pod_name": dst}
        else: flow["IP"] = {"source": "10.0.0.1", "destination": pe[1]}; flow["destination_names"] = [pe[3]]; flow["destination"] = {"identity": 2}
        add("hubble/flows.ndjson", "hubble", t, f"hubble/flow-{n}", {"flow": flow, "node_name": "node-a", "time": iso(t)})
        n += 1

# ---- Azure Resource Graph: the cluster, its node machines, load balancer, virtual network, and an Event Hubs namespace -------------
def arg(rid, name, typ, rg, loc="north", properties=None):
    add("arg/resources.ndjson", "arg", at(3), f"arg/resources/{rid}", {"id": rid, "name": name, "type": typ, "location": loc, "resourceGroup": rg, "subscriptionId": SUB, "properties": properties})
arg(CLUSTER_ID, "aks-synth", "microsoft.containerservice/managedclusters", "rg-synth")
for nn in NODES: arg(f"/subscriptions/{SUB}/resourcegroups/{NODE_RG}/providers/microsoft.compute/virtualmachines/{nn}", nn, "microsoft.compute/virtualmachines", NODE_RG)
arg(f"/subscriptions/{SUB}/resourcegroups/{NODE_RG}/providers/microsoft.network/loadbalancers/kubernetes", "kubernetes", "microsoft.network/loadbalancers", NODE_RG)
arg(f"/subscriptions/{SUB}/resourcegroups/{NODE_RG}/providers/microsoft.network/virtualnetworks/vnet-synth", "vnet-synth", "microsoft.network/virtualnetworks", NODE_RG)
arg(f"/subscriptions/{SUB}/resourcegroups/rg-hub/providers/microsoft.eventhub/namespaces/hub-synth", "hub-synth", "microsoft.eventhub/namespaces", "rg-hub")
# the platform the workload uses: a cache, a storage account, a PostgreSQL server, API Management in front of api, each data store behind a private endpoint in the cluster's network
arg(CACHE_ID, "shopcache", "microsoft.cache/redis", "rg-shop")
arg(STORAGE_ID, "shopassets", "microsoft.storage/storageaccounts", "rg-shop")
arg(PG_ID, "pg-shop", "microsoft.dbforpostgresql/flexibleservers", "rg-shop")
arg(APIM_ID, "apim-shop", "microsoft.apimanagement/service", "rg-shop")
arg(f"{APIM_ID}/backends/api", "api", "microsoft.apimanagement/service/backends", "rg-shop", properties={"url": "http://10.1.0.20/api", "protocol": "http"})
for pe, address, target, host in ENDPOINTS:
    arg(f"{SHOP_RG}/microsoft.network/privateendpoints/{pe}", pe, "microsoft.network/privateendpoints", "rg-shop", properties={"subnet": {"id": f"{VNET_ID}/subnets/endpoints"},
        "customDnsConfigs": [{"fqdn": host, "ipAddresses": [address]}], "privateLinkServiceConnections": [{"name": pe, "properties": {"privateLinkServiceId": target}}]})
# web's workload identity and its role on the storage account
arg(f"{SHOP_RG}/microsoft.managedidentity/userassignedidentities/id-web", "id-web", "microsoft.managedidentity/userassignedidentities", "rg-shop", properties={"clientId": WEB_CLIENT, "principalId": WEB_PRINCIPAL})
arg(f"{STORAGE_ID}/providers/microsoft.authorization/roleassignments/ra-web-blob", "ra-web-blob", "microsoft.authorization/roleassignments", "rg-shop",
    properties={"principalId": WEB_PRINCIPAL, "scope": STORAGE_ID, "roleDefinitionId": "/providers/Microsoft.Authorization/roleDefinitions/2a2b9908-6ea1-4ae2-8e65-a410df84e7d1"})

# ---- Prometheus: node drop rate and forward rate, two samples per node and metric every minute, all steady
for minute in range(TOTAL_MINUTES):
    for metric in ["cilium_drop_rate", "cilium_forward_rate"]:
        result = []
        for nn in NODES:
            vals = []
            for k in range(2):
                ts = (T0 + timedelta(seconds=minute * 60 + k * 30)).timestamp()
                if metric == "cilium_drop_rate": v = 0.002
                else: v = 150 + 40 * math.sin(minute + k) + (10 if nn == "node-b" else 0)
                vals.append([ts, f"{v:.4f}"])
            result.append({"metric": {"instance": nn, "direction": "egress"}, "values": vals})
        add("prometheus/range.ndjson", "prometheus", at(minute * 60 + 62), f"prometheus/{metric}/{minute}", {"status": "success", "data": {"resultType": "matrix", "result": result}}, {"name": metric, "query": metric, "step": "30s"})

# ---- Azure Monitor, one point a minute: cluster CPU steady at 35 %, and the metrics of the platform resources with the scenarios of the workload model ------------------------------------------------
def metric(path, rid, name, minute, value):
    add(path, "azure-metrics", at(minute * 60 + 61), f"azure-metrics/{name}/{rid.rsplit('/', 1)[-1]}/{minute}", {"value": [{"name": {"value": name}, "timeseries": [{"data": [
        {"timeStamp": iso(at(minute * 60)), "average": value}]}]}]}, {"resource": rid.rsplit("/", 1)[-1], "resourceId": rid})
for minute in range(TOTAL_MINUTES):
    metric("azure-metrics/aks.ndjson", CLUSTER_ID, "node_cpu_usage_percentage", minute, 35 + (minute % 3))
    metric("azure-metrics/shop.ndjson", CACHE_ID, "serverLoad", minute, 85 if 4 <= minute <= 6 else 20 + (minute % 4))          # scenario A, minutes 4 to 6: the cache is overloaded
    metric("azure-metrics/shop.ndjson", PG_ID, "is_db_alive", minute, 0 if 11 <= minute <= 13 else 1)                              # scenario B, minutes 11 to 13: the database is down
    metric("azure-metrics/shop.ndjson", APIM_ID, "FailedRequests", minute, 40 if 16 <= minute <= 18 else 0)                         # scenario D, minutes 16 to 18: API Management fails requests
    if minute < 19: metric("azure-metrics/shop.ndjson", STORAGE_ID, "Availability", minute, 100)                                    # scenario E: the storage account's metrics stop after minute 18

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
for cap, svc in [("storefront", "web"), ("ordering", "api"), ("data-platform", "database")]:
    add("authoring/capabilities.ndjson", "authoring", at(0.6), f"authoring/realizes/{cap}/{svc}", {"kind": "realizes", "capId": cap, "cluster": "synth", "namespace": "shop", "service": svc})

# ---- authored signal rules of the health tree: a meter of an element type is a signal (thresholds with hysteresis, severity 1 degraded or 2 unhealthy) ----
RULES = [("cpu-high", "microsoft.containerservice/managedclusters", "node_cpu_usage_percentage", 70, 3, 60, 3, 1, False), ("cpu-critical", "microsoft.containerservice/managedclusters", "node_cpu_usage_percentage", 90, 3, 80, 3, 2, False),
         ("node-drops", "node", "cilium_drop_rate", 5, 2, 2, 2, 1, False), ("node-drops-critical", "node", "cilium_drop_rate", 50, 2, 20, 2, 2, False),
         ("pod-warnings", "pod", "warnings", 1, 1, 0, 2, 1, False), ("pod-warnings-critical", "pod", "warnings", 2, 1, 0, 2, 2, False),
         ("cache-load", "microsoft.cache/redis", "serverLoad", 70, 2, 50, 2, 1, False),
         ("database-down", "microsoft.dbforpostgresql/flexibleservers", "is_db_alive", -0.5, 1, -0.9, 2, 2, True),
         ("gateway-failures", "microsoft.apimanagement/service", "FailedRequests", 10, 1, 1, 2, 2, False),
         ("storage-availability", "microsoft.storage/storageaccounts", "Availability", -99, 2, -99.5, 2, 1, True)]
for rid, typ, meter, above, ea, below, xa, sev, inv in RULES:
    add("authoring/rules.ndjson", "authoring", at(0.7), f"authoring/signalRule/{rid}", {"kind": "signalRule", "id": rid, "type": typ, "meter": meter, "above": above, "enterAfter": ea, "below": below, "exitAfter": xa, "severity": sev, "invert": inv})

for path, lines in files.items():
    lines.sort(key=lambda l: l["capturedAt"])          # a capture file is in capture-time order (the markers of a minute are written after its objects)
    for i, l in enumerate(lines, 1): l["seq"] = i
    full = os.path.join(OUT, path)
    os.makedirs(os.path.dirname(full), exist_ok=True)
    with open(full, "w") as f:
        for line in lines: f.write(json.dumps(line, separators=(",", ":")) + "\n")
print(f"wrote {sum(len(v) for v in files.values())} records in {len(files)} files to {OUT}")

# ---- the space binding for this estate: the tracked space file already carries these invented workload arguments, so the copy under estate/space is the tracked file itself ----
import shutil
os.makedirs(os.path.join(OUT, "space"), exist_ok=True)
shutil.copyfile(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "definitions", "acme.space.sattle"), os.path.join(OUT, "space", "acme.space.sattle"))
