#!/usr/bin/env python3
"""Read a complete resource-group inventory from Azure Resource Graph and record its changes."""
import argparse
from collections import Counter
from datetime import datetime, timezone
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile

from azure_monitor import ARM, azure, iso

ARG = ARM + "/providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01"
DEFAULT_TAGS = ("workload", "azd-env-name", "azd-service-name")
SCOPE = re.compile(r"^(/subscriptions/([a-f0-9-]{36})/resourcegroups/([^/]+))"
                   r"(?:/providers/microsoft\.cloudhealth/healthmodels/[^/]+)?/?$", re.I)

# Project only fields used by the topology. App templates, environment variables, identity
# credentials, registry configuration, Monitoring connection strings and arbitrary tags stay in Azure.
SCALARS = {
    "provisioningState": "properties.provisioningState", "runningStatus": "properties.runningStatus",
    "environmentId": "properties.environmentId", "managedEnvironmentId": "properties.managedEnvironmentId",
    "ingressFqdn": "properties.configuration.ingress.fqdn", "ingressExternal": "properties.configuration.ingress.external",
    "ingressPort": "properties.configuration.ingress.targetPort", "ingressTransport": "properties.configuration.ingress.transport",
    "defaultDomain": "properties.defaultDomain", "staticIp": "properties.staticIp",
    "infrastructureSubnetId": "properties.vnetConfiguration.infrastructureSubnetId", "internalIngress": "properties.vnetConfiguration.internal",
    "publicNetworkAccess": "properties.publicNetworkAccess", "subnetId": "properties.subnet.id",
    "networkSecurityGroupId": "properties.networkSecurityGroup.id", "virtualNetworkId": "properties.virtualNetwork.id",
    "privateEndpointId": "properties.privateEndpoint.id", "virtualMachineId": "properties.virtualMachine.id",
    "workspaceId": "properties.WorkspaceResourceId", "ipAddress": "properties.ipAddress",
}
# Networking objects use their native Azure shape in the envelope. Keep only the structural
# fields below when walking arrays. Private-link approval messages and object metadata are omitted.
NETWORK = {
    "subnets": {"id": None, "name": None, "properties": {"addressPrefix": None, "addressPrefixes": None,
               "networkSecurityGroup": {"id": None}, "routeTable": {"id": None}}},
    "customDnsConfigs": {"fqdn": None, "ipAddresses": None}, "networkInterfaces": {"id": None},
    "privateLinkServiceConnections": {"id": None, "properties": {"privateLinkServiceId": None, "groupIds": None}},
    "manualPrivateLinkServiceConnections": {"id": None, "properties": {"privateLinkServiceId": None, "groupIds": None}},
    "ipConfigurations": {"id": None, "properties": {"privateIPAddress": None, "subnet": {"id": None},
                         "publicIPAddress": {"id": None}, "privateLinkConnectionProperties": {"fqdns": None}}},
    "privateDnsZoneConfigs": {"properties": {"privateDnsZoneId": None}}, "addressSpace": {"addressPrefixes": None},
    "dnsSettings": {"fqdn": None, "internalFqdn": None, "dnsServers": None, "appliedDnsServers": None},
}


def scope_for(identifier):
    match = SCOPE.fullmatch(identifier)
    if not match:
        raise ValueError("Expected a resource group or Microsoft.CloudHealth/healthModels ARM ID")
    return match.group(1), match.group(2), match.group(3)


def pages(subscription, query):
    options = {"resultFormat": "objectArray", "$top": 1000, "allowPartialScopes": False}
    rows = []
    tokens = set()
    expected = None
    while True:
        page = azure(ARG, subscription, query={"subscriptions": [subscription], "query": query, "options": options})
        if expected is None:
            expected = page["totalRecords"]
        if page["totalRecords"] != expected or page["count"] != len(page["data"]):
            raise ValueError("Azure inventory changed during pagination; retry the poll")
        rows.extend(page["data"])
        token = page.get("$skipToken")
        if not token:
            if str(page.get("resultTruncated")).lower() == "true" or len(rows) != expected:
                raise ValueError("Azure returned a partial inventory; previous snapshot retained")
            return rows
        if token in tokens:
            raise ValueError("Azure repeated the inventory continuation token")
        tokens.add(token)
        options["$skipToken"] = token


def pick(value, schema):
    if value is None:
        return None
    if isinstance(value, list):
        return [filtered for v in value if (filtered := pick(v, schema)) is not None]
    if schema is None:
        return value if isinstance(value, (str, bool, int, float)) else None
    if not isinstance(value, dict):
        raise ValueError("Unexpected Azure network property shape")
    return {k: filtered for k, sub in schema.items() if (filtered := pick(value.get(k), sub)) is not None}


def resource(row, tags):
    result = {k: row[k] for k in ("id", "name", "type", "location", "resourceGroup", "subscriptionId")}
    result["tags"] = {tag: row[f"tag{i}"] for i, tag in enumerate(tags) if row.get(f"tag{i}") is not None}
    props = {k: row[k] for k in ("provisioningState", "runningStatus", "environmentId", "managedEnvironmentId",
             "defaultDomain", "staticIp", "publicNetworkAccess", "ipAddress") if row.get(k) is not None}
    for field, path in {
        "ingressFqdn": ("configuration", "ingress", "fqdn"), "ingressExternal": ("configuration", "ingress", "external"),
        "ingressPort": ("configuration", "ingress", "targetPort"), "ingressTransport": ("configuration", "ingress", "transport"),
        "infrastructureSubnetId": ("vnetConfiguration", "infrastructureSubnetId"), "internalIngress": ("vnetConfiguration", "internal"),
        "subnetId": ("subnet", "id"), "virtualNetworkId": ("virtualNetwork", "id"), "virtualMachineId": ("virtualMachine", "id"),
        "privateEndpointId": ("privateEndpoint", "id"), "networkSecurityGroupId": ("networkSecurityGroup", "id"),
        "workspaceId": ("WorkspaceResourceId",),
    }.items():
        if row.get(field) is not None:
            node = props
            for part in path[:-1]:
                node = node.setdefault(part, {})
            node[path[-1]] = row[field]
    for k, schema in NETWORK.items():
        if row.get(k) is not None:
            props[k] = pick(row[k], schema)
    result["properties"] = props
    return result


def inventory(scope, subscription, group, tags):
    groups = pages(subscription, "ResourceContainers | where type =~ 'microsoft.resources/subscriptions/resourcegroups'"
                   + " and id =~ " + json.dumps(scope) + " | project id")
    if len(groups) != 1:
        raise ValueError("Resource group is missing or inaccessible; previous inventory retained")
    projection = ["id", "name", "type", "location", "resourceGroup", "subscriptionId"]
    projection += [f"tag{i}=tags[{json.dumps(t)}]" for i, t in enumerate(tags)]
    projection += [f"{key}={field}" for key, field in SCALARS.items()]
    projection += [f"{key}=iif(type startswith 'microsoft.network/', properties.{key}, dynamic(null))" for key in NETWORK]
    q = "Resources | where resourceGroup =~ " + json.dumps(group) + " | project " + ", ".join(projection) + " | order by id asc"
    snapshot = {}
    rows = pages(subscription, q)
    dns_types = ["a", "aaaa", "cname"]
    if any(row["type"].lower() == "microsoft.network/privatednszones" for row in rows):
        accepted = ", ".join(json.dumps("microsoft.network/privatednszones/" + key) for key in dns_types)
        dns_query = ("dnsresources | where resourceGroup =~ " + json.dumps(group) + " and type in~ (" + accepted
                     + ") | project id, name, type, location, resourceGroup, subscriptionId, managedBy, "
                       "records=properties.records, ttl=properties.ttl | order by id asc")
        rows.extend(pages(subscription, dns_query))
    for row in rows:
        key = row["id"].lower()
        if key in snapshot or not key.startswith(scope.lower() + "/providers/"):
            raise ValueError("Duplicate or out-of-scope Azure resource")
        payload = resource(row, tags)
        if row.get("managedBy") is not None:
            payload["managedBy"] = row["managedBy"]
            payload["properties"] = {"ttl": row["ttl"], "records": pick(row["records"], {
                "ipv4Address": None, "ipv6Address": None, "cname": None})}
        snapshot[key] = payload
    return snapshot


def confirmed_missing(resource_id, subscription):
    # ARG can temporarily omit rows during indexing or an RBAC change. Only a native ARM
    # not-found response confirms a previously seen resource should be removed from Sattle.
    result = subprocess.run(["az", "resource", "show", "--ids", resource_id, "--subscription", subscription,
                             "--query", "id", "-o", "tsv", "--only-show-errors"], capture_output=True, text=True, timeout=90)
    if result.returncode == 0:
        return False
    if re.search(r"\((?:ResourceNotFound|ParentResourceNotFound|ResourceGroupNotFound)\)", result.stderr):
        return True
    raise ValueError("Could not confirm a missing resource with ARM; previous inventory retained")


def envelope(payload, scope, at, seq, deletion=False):
    kind = "resourcechanges" if deletion else "resources"
    serialized = json.dumps([kind, at, payload], sort_keys=True, separators=(",", ":"))
    key = "arg/" + hashlib.sha256(serialized.encode()).hexdigest()
    return {"v": 1, "source": "arg", "capturedAt": at, "seq": seq, "key": key,
            "meta": {"query": kind, "scopeId": scope}, "payload": payload}


def read_rows(path):
    if not path.exists():
        return []
    return [json.loads(line) for line in path.read_text().splitlines() if line.strip()]


def materialize(rows, scope):
    resources = {}
    for row in rows:
        if row["source"] != "arg" or row["meta"]["scopeId"].lower() != scope.lower():
            raise ValueError("Existing inventory belongs to a different scope")
        payload = row["payload"]
        if row["meta"]["query"] == "resourcechanges":
            resources.pop(payload["properties"]["targetResourceId"].lower(), None)
        else:
            resources[payload["id"].lower()] = payload
    return resources


def write_rows(path, rows):
    with tempfile.NamedTemporaryFile(mode="w", dir=path.parent, delete=False) as out:
        for row in rows:
            out.write(json.dumps(row, separators=(",", ":"), sort_keys=True) + "\n")
        out.flush()
        os.fsync(out.fileno())
    os.replace(out.name, path)
    directory = os.open(path.parent, os.O_RDONLY)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


def poll(identifier, output, delta, tags):
    scope, subscription, group = scope_for(identifier)
    output.parent.mkdir(parents=True, exist_ok=True)
    if delta:
        delta.parent.mkdir(parents=True, exist_ok=True)
        if output.resolve() == delta.resolve():
            raise ValueError("History and delta paths must differ")
    directory = os.open(output.parent, os.O_RDONLY)
    try:
        fcntl.flock(directory, fcntl.LOCK_EX)
        history = read_rows(output)
        previous = materialize(history, scope)
        pending = read_rows(delta) if delta else []
        if pending:
            materialize(pending, scope)
            seen = {row["key"] for row in history}
            new = [row for row in pending if row["key"] not in seen]
            if new:
                if [r["seq"] for r in new] != list(range(len(history) + 1, len(history) + len(new) + 1)):
                    raise ValueError("Pending delta does not follow this inventory history")
                write_rows(output, history + new)
            print(f"{len(pending)} inventory updates pending publication")
            return
        snapshot = inventory(scope, subscription, group, tags)
        at = iso(datetime.now(timezone.utc))
        changes = []
        for rid, payload in sorted(snapshot.items()):
            if previous.get(rid) != payload:
                changes.append(envelope(payload, scope, at, len(history) + len(changes) + 1))
        for rid in sorted(previous.keys() - snapshot.keys()):
            if confirmed_missing(previous[rid]["id"], subscription):
                payload = {"properties": {"changeType": "Delete", "targetResourceId": previous[rid]["id"]}}
                changes.append(envelope(payload, scope, at, len(history) + len(changes) + 1, deletion=True))
        if delta and changes:
            write_rows(delta, changes)
        if changes or not output.exists():
            write_rows(output, history + changes)
        counts = Counter(p["type"].lower() for p in snapshot.values())
        print(f"{len(snapshot)} Azure resources, {counts['microsoft.app/containerapps']} Container Apps, {len(changes)} inventory updates")
    finally:
        os.close(directory)


if __name__ == "__main__":
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("scope", help="resource group ARM ID or health model ARM ID")
    cli.add_argument("--output", type=Path, required=True, help="local native ARG event history")
    cli.add_argument("--delta", type=Path, help="pending native event batch; publisher removes it after acknowledgment")
    cli.add_argument("--tag", action="append", default=[], help="additional exact-case workload or business-flow label key to retain")
    args = cli.parse_args()
    for tag in args.tag:
        if not re.fullmatch(r"[\w .:/-]{1,128}", tag) or re.search(r"secret|passw|token|credential|connectionstring|api[-_]?key", tag, re.I):
            cli.error("--tag expects a workload label key")
    poll(args.scope, args.output, args.delta, list(dict.fromkeys((*DEFAULT_TAGS, *args.tag))))
