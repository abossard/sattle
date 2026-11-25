#!/usr/bin/env python3
"""Native capture envelopes (lab/CAPTURE-FORMAT.md) as record envelopes of the tutorial's row types.

  capture.py CAPTURE_DIR OUT_DIR

Every *.ndjson under CAPTURE_DIR becomes the file of the same relative path under OUT_DIR, holding the records
its native envelopes give. A native object (a Kubernetes object, a Resource Graph resource) owns the rows it
gives; when it changes or is deleted, the rows it no longer gives are withdrawn. Samples (flows, metric points)
own nothing and are never withdrawn. A record keeps the native capturedAt, so a replay keeps its logical time.
"""
from argparse import ArgumentParser
from collections import namedtuple
import ipaddress
import json
from pathlib import Path
import sys

from sources import compact, instant

Line = namedtuple("Line", "path envelope")
Labelled = namedtuple("Labelled", "kind name labels")  # a Service selector or a Deployment's pod template labels


# Calculations: reading native JSON

def at(value, *path):
    """The value at path, None where a step is missing, null or not an object."""
    for name in path:
        if not isinstance(value, dict):
            return None
        value = value.get(name)
    return value


def text(value, *path):
    v = at(value, *path)
    return v if isinstance(v, str) else None


def filled(s):
    return s or None


def items(value):
    return value if isinstance(value, list) else []


def number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def time_text(value):
    """The instant text as the source wrote it, None when it is not an instant."""
    if not isinstance(value, str):
        return None
    try:
        instant(value)
    except ValueError:
        return None
    return value


def host_of(value):
    """The host a value names, lowercased, without scheme, user, port or path; None when it names none."""
    if not isinstance(value, str):
        return None
    v = value.strip()
    if "://" in v:
        v = v[v.index("://") + 3:]
    if "@" in v:
        v = v[v.index("@") + 1:]
    for i, c in enumerate(v):
        if c in "/?#:;,":
            v = v[:i]
            break
    h = v.lower()
    return h if h and all(c.isascii() and (c.isalnum() or c in ".-") for c in h) else None


def ip(value):
    try:
        return str(ipaddress.ip_address(value)) if isinstance(value, str) else None
    except ValueError:
        return None


# Calculations: Kubernetes objects

def labels_of(value):
    return tuple(sorted((k, v) for k, v in value.items() if isinstance(v, str))) if isinstance(value, dict) else ()


def kubernetes_object(env):
    """(cluster, kind, deleted, object) of a list item or watch event; None for bookmarks, errors and objects
    without uid or name."""
    payload, meta = env.get("payload"), env.get("meta") or {}
    cluster = filled(text(meta, "cluster"))
    if env["source"] == "kubernetes-list":
        kind, obj, deleted = (text(meta, "list", "kind") or "").replace("List", ""), payload, False
    else:
        if text(payload, "type") in ("BOOKMARK", "ERROR") or not isinstance(at(payload, "object"), dict):
            return None
        obj = payload["object"]
        kind, deleted = text(obj, "kind") or "", text(payload, "type") == "DELETED"
    if cluster is None or text(obj, "metadata", "uid") is None or text(obj, "metadata", "name") is None:
        return None
    return cluster, kind, deleted, obj


def owner(obj, kind, controller):
    for ref in items(at(obj, "metadata", "ownerReferences")):
        if text(ref, "kind") == kind and (not controller or at(ref, "controller") is True):
            return text(ref, "name")
    return None


def ready_of(pod):
    for c in items(at(pod, "status", "conditions")):
        if text(c, "type") == "Ready":
            return {"True": True, "False": False}.get(text(c, "status"))
    return None


def restarts_of(pod):
    statuses = at(pod, "status", "containerStatuses")
    if not isinstance(statuses, list):
        return None
    return sum(c["restartCount"] for c in statuses if isinstance(c, dict) and number(c.get("restartCount")))


def deployment_of(pod):
    """The Deployment behind a pod: its ReplicaSet is named after the Deployment and a pod-template hash."""
    for ref in items(at(pod, "metadata", "ownerReferences")):
        name = text(ref, "name")
        if text(ref, "kind") == "ReplicaSet" and name and name.rfind("-") > 0:
            return name[:name.rfind("-")]
    return None


def address_of(svc):
    """The load balancer ingress of a Service, else its cluster IP; a headless Service has none."""
    for i in items(at(svc, "status", "loadBalancer", "ingress")):
        a = filled(text(i, "ip") or text(i, "hostname"))
        if a:
            return a
    cluster_ip = text(svc, "spec", "clusterIP")
    return filled(cluster_ip) if cluster_ip != "None" else None


def azure_host(node):
    """The Azure host of a node: its VM, or its scale set for a scale set instance."""
    provider = text(node, "spec", "providerID")
    if provider is None or not provider.lower().startswith("azure://"):
        return None
    rid = provider[len("azure://"):].lower()
    if not rid.startswith("/subscriptions/"):
        return None
    instance = rid.rfind("/virtualmachines/")
    return rid[:instance] if instance > 0 and "/virtualmachinescalesets/" in rid else rid


def link_of(cluster, ns, kind, name, obj):
    """The KubernetesLinkRow an object declares, if any."""
    if kind == "Pod":
        target = owner(obj, "ReplicaSet", True)
        return None if target is None else ("podOwner", target)
    if kind == "ReplicaSet":
        return "replicaSet", owner(obj, "Deployment", True)
    if kind == "Node":
        host = azure_host(obj)
        return None if host is None else ("azureHost", host)
    if kind == "Namespace":
        return "namespace", None
    return None


def kubernetes_rows(cluster, kind, obj):
    """The rows a live Kubernetes object gives, {(row type, key): values}."""
    uid, name = obj["metadata"]["uid"], obj["metadata"]["name"]
    ns = text(obj, "metadata", "namespace") or ""
    key = cluster + "/" + uid
    rows = {}
    if kind == "Pod":
        rows["PodRow", key] = {
            "cluster": cluster, "uid": uid, "namespace": ns, "name": name, "phase": text(obj, "status", "phase"),
            "teamLabel": text(obj, "metadata", "labels", "team"), "nodeName": text(obj, "spec", "nodeName"),
            "restarts": restarts_of(obj), "ready": ready_of(obj),
            "serviceAccount": text(obj, "spec", "serviceAccountName"), "deployment": deployment_of(obj)}
    elif kind == "Deployment":
        replicas = at(obj, "spec", "replicas")
        rows["DeploymentRow", key] = {"cluster": cluster, "namespace": ns, "name": name,
                                      "replicas": int(replicas) if number(replicas) else None}
    elif kind == "Service":
        rows["ServiceRow", key] = {"cluster": cluster, "namespace": ns, "name": name,
                                   "externalName": text(obj, "spec", "externalName"), "address": address_of(obj)}
    elif kind == "Node":
        provider = text(obj, "spec", "providerID")
        rows["NodeRow", key] = {
            "cluster": cluster, "uid": uid, "name": name,
            "resourceId": provider[len("azure://"):] if provider and provider.lower().startswith("azure://") else None,
            "pool": text(obj, "metadata", "labels", "kubernetes.azure.com/agentpool")
            or text(obj, "metadata", "labels", "agentpool")}
    elif kind == "EndpointSlice":
        service = text(obj, "metadata", "labels", "kubernetes.io/service-name")
        for ep in items(at(obj, "endpoints")) if service is not None else []:
            pod_uid = text(ep, "targetRef", "uid") if text(ep, "targetRef", "kind") == "Pod" else None
            for a in items(at(ep, "addresses")):
                if isinstance(a, str):
                    rows["EndpointRow", key + "/" + a] = {"cluster": cluster, "namespace": ns, "service": service,
                                                          "address": a, "podUid": pod_uid}
    link = link_of(cluster, ns, kind, name, obj)
    if link:
        rows["KubernetesLinkRow", key] = {"cluster": cluster, "namespace": ns, "kind": link[0], "name": name,
                                          "target": link[1]}
    return rows


def labelled(cluster, kind, deleted, obj):
    """((cluster, namespace), uid, Labelled or None) for a Service or Deployment; None for other kinds."""
    if kind not in ("Service", "Deployment"):
        return None
    scope = (cluster, text(obj, "metadata", "namespace") or "")
    uid, name = obj["metadata"]["uid"], obj["metadata"]["name"]
    if deleted:
        return scope, uid, None
    if kind == "Service":
        labels = () if text(obj, "spec", "type") == "ExternalName" else labels_of(at(obj, "spec", "selector"))
    else:
        labels = labels_of(at(obj, "spec", "template", "metadata", "labels"))
    return scope, uid, Labelled(kind, name, labels)


def selects(scope, objects):
    """The SelectsRow rows of one namespace: a Service selects a Deployment whose pod template labels hold every
    pair of its non-empty selector."""
    cluster, ns = scope
    services = [o for o in objects if o.kind == "Service" and o.labels]
    deployments = [o for o in objects if o.kind == "Deployment"]
    return {("SelectsRow", "/".join((cluster, ns, s.name, d.name))):
            {"cluster": cluster, "namespace": ns, "service": s.name, "deployment": d.name}
            for s in services for d in deployments if set(s.labels) <= set(d.labels)}


# Calculations: Azure Resource Graph

def managed_cluster(row):
    """The AKS cluster of a resource in its node resource group, mc_{group}_{cluster}_{region}."""
    rg, sub = text(row, "resourceGroup"), text(row, "subscriptionId")
    if rg is None or sub is None or not rg.lower().startswith("mc_"):
        return None
    parts = rg.split("_")
    return (f"/subscriptions/{sub}/resourcegroups/{parts[1]}/providers/microsoft.containerservice/managedclusters/"
            f"{parts[2]}") if len(parts) == 4 else None


def resource_values(row):
    rid, name, kind = text(row, "id"), text(row, "name"), text(row, "type")
    p = at(row, "properties") if isinstance(at(row, "properties"), dict) else None
    v = {"id": rid, "name": name, "type": kind, "location": filled(text(row, "location")),
         "resourceGroup": filled(text(row, "resourceGroup")), "subscriptionId": filled(text(row, "subscriptionId")),
         "parentId": managed_cluster(row), "address": None, "linkedId": None, "clientId": None, "principalId": None,
         "scope": None}
    kind = kind.lower()
    if kind == "microsoft.network/privateendpoints":
        dns = items(at(p, "customDnsConfigs"))
        ips = items(at(dns[0], "ipAddresses")) if dns else []
        v["address"] = ips[0] if ips and isinstance(ips[0], str) else None
        links = items(at(p, "privateLinkServiceConnections"))
        v["linkedId"] = filled(text(links[0], "properties", "privateLinkServiceId")) if links else None
        subnet = filled(text(p, "subnet", "id"))
        cut = subnet.lower().find("/subnets/") if subnet else -1
        if cut > 0:
            v["parentId"] = subnet[:cut]
    elif kind == "microsoft.apimanagement/service/backends":
        v["address"] = host_of(filled(text(p, "url")))
        cut = rid.lower().find("/backends/")
        if cut > 0:
            v["parentId"] = rid[:cut]
    elif kind == "microsoft.managedidentity/userassignedidentities":
        v["clientId"], v["principalId"] = filled(text(p, "clientId")), filled(text(p, "principalId"))
    elif kind == "microsoft.authorization/roleassignments":
        v["principalId"], v["scope"] = filled(text(p, "principalId")), filled(text(p, "scope"))
    return v


def network_facts(row):
    """The network evidence of a resource, {(kind, target, address)}, observed at the field that supplied it."""
    p = at(row, "properties")
    if not isinstance(p, dict):
        return set()
    facts = set()

    def add(kind, target, address=None):
        if isinstance(target, str) and target.strip():
            facts.add((kind, target, address))

    def arm(kind, rid):
        if isinstance(rid, str) and rid.lower().startswith("/subscriptions/"):
            add(kind, rid.lower())

    def dns(name, address=None):
        if isinstance(name, str):
            add("dnsName", name.rstrip(".").lower(), address)

    def refs(kind, values):
        for value in items(values):
            arm(kind, text(value, "id"))

    def dns_servers(values):
        for value in items(values):
            add("dnsServer", ip(value))

    def ip_configurations(values):
        for cfg in items(values):
            c = at(cfg, "properties")
            if c is None:
                continue
            address = ip(text(c, "privateIPAddress"))
            add("ip", address)
            arm("subnet", text(c, "subnet", "id"))
            arm("attachment", text(c, "publicIPAddress", "id"))
            refs("attachment", at(c, "loadBalancerBackendAddressPools"))
            for fqdn in items(at(c, "privateLinkConnectionProperties", "fqdns")):
                dns(fqdn, address)

    def prefixes(values):
        for value in items(values):
            add("prefix", value)

    kind = text(row, "type").lower()
    if kind == "microsoft.network/privateendpoints":
        arm("subnet", text(p, "subnet", "id"))
        refs("networkInterface", at(p, "networkInterfaces"))
        for name in ("privateLinkServiceConnections", "manualPrivateLinkServiceConnections"):
            for connection in items(at(p, name)):
                arm("privateLink", text(connection, "properties", "privateLinkServiceId"))
        for config in items(at(p, "customDnsConfigs")):
            dns(text(config, "fqdn"))
            for value in items(at(config, "ipAddresses")):
                address = ip(value)
                if address:
                    add("ip", address)
                    dns(text(config, "fqdn"), address)
        ip_configurations(at(p, "ipConfigurations"))
    elif kind == "microsoft.network/networkinterfaces":
        arm("attachment", text(p, "virtualMachine", "id"))
        arm("attachment", text(p, "networkSecurityGroup", "id"))
        dns(text(p, "dnsSettings", "internalFqdn"))
        dns(text(p, "dnsSettings", "internalDomainNameSuffix"))
        dns_servers(at(p, "dnsSettings", "dnsServers"))
        dns_servers(at(p, "dnsSettings", "appliedDnsServers"))
        ip_configurations(at(p, "ipConfigurations"))
    elif kind == "microsoft.network/publicipaddresses":
        address = ip(text(p, "ipAddress"))
        add("ip", address)
        dns(text(p, "dnsSettings", "fqdn"), address)
        arm("attachment", text(p, "ipConfiguration", "id"))
    elif kind in ("microsoft.network/privatednszones", "microsoft.network/dnszones"):
        dns(text(row, "name"))
    elif kind == "microsoft.network/privatednszones/virtualnetworklinks":
        arm("virtualNetwork", text(p, "virtualNetwork", "id"))
        link = text(row, "id") or ""
        cut = link.lower().find("/virtualnetworklinks/")
        if cut > 0:
            arm("dnsZone", link[:cut])
    elif kind == "microsoft.network/privateendpoints/privatednszonegroups":
        for zone in items(at(p, "privateDnsZoneConfigs")):
            arm("dnsZone", text(zone, "properties", "privateDnsZoneId"))
    elif kind == "microsoft.network/virtualnetworks":
        prefixes(at(p, "addressSpace", "addressPrefixes"))
        for subnet in items(at(p, "subnets")):
            arm("containsSubnet", text(subnet, "id"))
            add("prefix", text(subnet, "properties", "addressPrefix"))
            prefixes(at(subnet, "properties", "addressPrefixes"))
    elif kind == "microsoft.network/loadbalancers":
        ip_configurations(at(p, "frontendIPConfigurations"))
    elif kind == "microsoft.compute/virtualmachinescalesets":
        for nic in items(at(p, "virtualMachineProfile", "networkProfile", "networkInterfaceConfigurations")):
            ip_configurations(at(nic, "properties", "ipConfigurations"))
    return facts


def resource_rows(row):
    rid = row["id"].lower()
    rows = {("ResourceRow", rid): resource_values(row)}
    for kind, target, address in network_facts(row):
        rows["AzureNetworkRow", f"{rid}/{kind}/{target}" + ("@" + address if address else "")] = {
            "resourceId": rid, "kind": kind, "target": target, "resolvedAddress": address}
    return rows


# Calculations: samples

def flow_rows(env):
    payload, meta = env["payload"], env.get("meta") or {}
    flow = at(payload, "flow")
    if not isinstance(flow, dict):
        return {}
    time = time_text(flow["time"] if isinstance(flow.get("time"), str) else at(payload, "time"))
    verdict = text(flow, "verdict")
    if time is None or verdict is None:
        return {}
    # an answer runs from the callee back to the caller; a row runs from the caller to the callee
    src_end, dst_end, names = (("destination", "source", "source_names") if at(flow, "is_reply") is True
                               else ("source", "destination", "destination_names"))
    src, dst = at(flow, src_end), at(flow, dst_end)
    cluster = text(src, "cluster_name") if isinstance(at(src, "cluster_name"), str) else text(dst, "cluster_name")
    cluster = filled(cluster) or filled(text(meta, "cluster"))
    if cluster is None:
        return {}
    first_name = items(at(flow, names))
    key = filled(text(flow, "uuid")) or env["key"]
    return {("FlowRow", key): {
        "time": time, "cluster": cluster, "verdict": verdict,
        "srcNamespace": filled(text(src, "namespace")), "srcPod": filled(text(src, "pod_name")),
        "dstNamespace": filled(text(dst, "namespace")), "dstPod": filled(text(dst, "pod_name")),
        "dstAddress": filled(text(flow, "IP", dst_end)), "dstName": host_of(first_name[0]) if first_name else None}}


def metric_rows(env):
    """One row per datapoint with an average (else a total), keyed by resource, metric, series and time."""
    rid = text(env.get("meta"), "resourceId")
    rows = {}
    for m in items(at(env["payload"], "value")) if rid is not None else []:
        name = text(m, "name", "value")
        for s in items(at(m, "timeseries")) if name is not None else []:
            series = ",".join(str(at(d, "value")) for d in items(at(s, "metadatavalues")))
            for d in items(at(s, "data")):
                time = time_text(at(d, "timeStamp"))
                value = at(d, "average") if at(d, "average") is not None else at(d, "total")
                if time is not None and number(value):
                    rows["ResourceMetricRow", f"{rid.lower()}/{name}/{series}/{time}"] = {
                        "time": time, "resourceId": rid, "metric": name, "value": value}
    return rows


# Calculations: native envelope to owned rows

def observations(env):
    """[(owner, rows)] of a native envelope: the rows each object it describes gives now. An owner of None
    marks samples, which are never withdrawn."""
    source = env.get("source")
    if source in ("kubernetes-list", "kubernetes-watch"):
        seen = kubernetes_object(env)
        if seen is None:
            return []
        cluster, kind, deleted, obj = seen
        return [("kubernetes/" + cluster + "/" + obj["metadata"]["uid"],
                 {} if deleted else kubernetes_rows(cluster, kind, obj))]
    if source == "arg":
        row, query = env.get("payload"), text(env.get("meta"), "query") or ""
        if query.endswith("changes"):
            gone = text(row, "properties", "targetResourceId")
            return [("arg/" + gone.lower(), {})] if text(row, "properties", "changeType") == "Delete" and gone else []
        if any(text(row, f) is None for f in ("id", "name", "type")):
            return []
        return [("arg/" + row["id"].lower(), resource_rows(row))]
    if source == "hubble":
        return [(None, flow_rows(env))]
    if source == "azure-metrics":
        return [(None, metric_rows(env))]
    return []


def losses(env):
    """Hubble lost_events as a loss mark of FlowRow: the count (0 when unknown) at the record's time."""
    if env.get("source") != "hubble" or at(env["payload"], "lost_events") is None:
        return []
    n = at(env["payload"], "lost_events", "num_events_lost")
    count = n if number(n) else int(n) if isinstance(n, str) and n.isascii() and n.isdigit() else 0
    time = time_text(at(env["payload"], "time"))
    return [envelope(env, "FlowRow", "lost/" + env["key"], {"lost": count, **({"time": time} if time else {})})]


def envelope(native, row_type, key, payload):
    return {"v": 1, "source": "records:" + row_type, "capturedAt": native["capturedAt"], "key": row_type + "/" + key,
            "meta": {"source": "capture:" + native["source"], "position": native["capturedAt"]}, "payload": payload}


def changes(native, before, now):
    """Records that turn the rows `before` into the rows `now`."""
    out = [envelope(native, t, k, {"key": k, "values": v}) for (t, k), v in now.items() if before.get((t, k)) != v]
    return out + [envelope(native, t, k, {"key": k, "deleted": True}) for t, k in before if (t, k) not in now]


def convert(envelopes):
    """(native envelope, its records) for native envelopes in capture order. The rows each owner gives and the
    labels of each namespace are local to this fold."""
    held = {}
    scopes = {}
    for env in envelopes:
        out = []
        for name, now in observations(env):
            out += changes(env, held.get(name, {}), now)
            if name is not None and now:
                held[name] = now
            elif name is not None:
                held.pop(name, None)
        seen = kubernetes_object(env) if env.get("source") in ("kubernetes-list", "kubernetes-watch") else None
        label = labelled(*seen) if seen else None
        if label:
            scope, uid, entry = label
            before = selects(scope, scopes.get(scope, {}).values())
            scopes[scope] = {**scopes.get(scope, {}), uid: entry} if entry else {
                u: e for u, e in scopes.get(scope, {}).items() if u != uid}
            out += changes(env, before, selects(scope, scopes[scope].values()))
        yield env, out + losses(env)


def capture_order(line):
    return instant(line.envelope["capturedAt"]), str(line.path), line.envelope.get("seq", 0)


# Actions

def read(capture):
    return [Line(p.relative_to(capture), json.loads(raw)) for p in sorted(capture.rglob("*.ndjson"))
            for raw in p.read_text(encoding="utf-8").splitlines() if raw.strip()]


def main(argv):
    parser = ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("capture", type=Path)
    parser.add_argument("out", type=Path)
    a = parser.parse_args(argv)
    capture, out = a.capture, a.out
    lines = sorted(read(capture), key=capture_order)
    files = {f.relative_to(capture): [] for f in capture.rglob("*.ndjson")}
    for line, (_, records) in zip(lines, convert(line.envelope for line in lines)):
        files[line.path] += records
    for path, records in files.items():
        (out / path).parent.mkdir(parents=True, exist_ok=True)
        (out / path).write_text("".join(compact(r) + "\n" for r in records), encoding="utf-8")


if __name__ == "__main__":
    main(sys.argv[1:])
