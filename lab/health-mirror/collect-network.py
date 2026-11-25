#!/usr/bin/env python3
"""Collect outbound dependency calls for the Application Insights resources in a deployed health model."""
import argparse
from collections import Counter
from datetime import timedelta
from importlib.util import module_from_spec, spec_from_file_location
import ipaddress
import json
import os
from pathlib import Path
import re
import tempfile
from urllib.parse import quote, urlsplit

from azure_monitor import ARM, LOGS, ReadError, azure, iso, read, timestamp, workspace_id

BUCKET = timedelta(minutes=5)
_spec = spec_from_file_location("export_model", Path(__file__).with_name("export-model.py"))
_export_model = module_from_spec(_spec)
_spec.loader.exec_module(_export_model)
alias = _export_model.alias  # the lab's one scrub module makes it, under a local secret salt


def endpoint_name(value, salt):
    name = value.strip().rstrip(".").lower()
    if not name:
        return "unreported-target"
    try:
        ipaddress.ip_address(name.strip("[]"))
        return alias(salt, "ip", name)
    except ValueError:
        pass
    if len(name) <= 253 and all(re.fullmatch(r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?", part)
                                for part in name.split(".")):
        return name
    return alias(salt, "host", name)


def role_name(value, salt):
    if not value:
        return "unreported-role"
    if re.fullmatch(r"[a-zA-Z][a-zA-Z0-9 _.-]{0,127}", value):
        return value
    return alias(salt, "role", value)


def resource_endpoints(raw, subscription):
    """Map a hostname back to an ARM ID only when Azure reports it on that exact resource."""
    accounts = {}
    for entity in read(raw / "entities.json"):
        for group in entity["properties"].get("signalGroups", {}).values():
            rid = group.get("azureResourceId", "")
            if re.fullmatch(r"/subscriptions/[^/]+/resourceGroups/[^/]+/providers/"
                            r"(?:Microsoft.Storage/storageAccounts|Microsoft.CognitiveServices/accounts)/[^/]+", rid, re.I):
                accounts[rid.lower()] = rid
    result = {}
    for rid in accounts.values():
        is_storage = "/microsoft.storage/storageaccounts/" in rid.lower()
        version = "2023-05-01" if is_storage else "2024-10-01"
        props = azure(ARM + rid + "?api-version=" + version, subscription)["properties"]
        labels = {rid.rsplit("/", 1)[-1].lower(), (props.get("customSubDomainName") or "").lower()}
        if is_storage:
            urls = [*(props.get("primaryEndpoints") or {}).values(), *(props.get("secondaryEndpoints") or {}).values()]
        else:
            urls = [props.get("endpoint"), *(props.get("endpoints") or {}).values()]
        for url in urls:
            if isinstance(url, str) and urlsplit(url).hostname:
                host = urlsplit(url).hostname.lower()
                # Some AI services also list shared, region-wide gateways.
                if not is_storage and host.split(".")[0] not in labels:
                    continue
                if host in result and result[host].casefold() != rid.casefold():
                    raise ValueError("Multiple modeled resources declare the same endpoint")
                result[host] = rid
    return result


def rows_at(scope, workspace, subscription, start, end, endpoints, salt):
    # Extract only the hostname at the data source. Paths, query strings, IPs from client
    # metadata, request payloads, properties and account identities are outside this query.
    query = f"""
AppDependencies
| where _ResourceId =~ {json.dumps(scope)}
| where TimeGenerated >= datetime({iso(start)}) and TimeGenerated < datetime({iso(end)})
| extend sdkType = tolower(DependencyType)
| extend protocol = case(sdkType in ("http", "https"), "HTTP", sdkType == "sql", "SQL",
    sdkType in ("grpc", "grpc client"), "gRPC", sdkType in ("azure blob", "azure blobs"), "Azure Blob",
    sdkType in ("azure queue", "azure queues"), "Azure Queue", sdkType in ("azure table", "azure tables"), "Azure Table",
    sdkType in ("azure service bus", "azure servicebus"), "Azure Service Bus", sdkType == "redis", "Redis", "")
| where isnotempty(protocol)
| extend address = trim(" ", tostring(split(Target, " | ")[0]))
| extend endpoint = tolower(tostring(parse_url(iif(address contains "://", address, strcat("https://", address))).Host))
| summarize calls=sum(coalesce(ItemCount, 1)), spanCount=count()
    by bucket=bin(TimeGenerated, 5m), AppRoleName, endpoint, protocol, Success
"""
    response = azure(f"{LOGS}/v1/workspaces/{quote(workspace, safe='')}/query", subscription,
                     query={"query": query, "timespan": iso(start) + "/" + iso(end)}, audience=LOGS)
    if response.get("error"):
        raise ReadError(response["error"].get("code", "PartialQueryError"))
    table = next(t for t in response["tables"] if t["name"] == "PrimaryResult")
    keys = [column["name"] for column in table["columns"]]
    aggregated = {}
    for values in table["rows"]:
        row = dict(zip(keys, values, strict=True))
        bucket = timestamp(row["bucket"])
        host = endpoint_name(row["endpoint"], salt)
        role = role_name(row["AppRoleName"], salt)
        success = row["Success"]
        if success is not None and type(success) is not bool:
            raise ValueError("Unexpected dependency success value")
        if not start <= bucket < end or bucket.minute % 5 or bucket.second:
            raise ValueError("Dependency timestamp outside the requested complete bins")
        key = (bucket, role, host, row["protocol"], success)
        if key not in aggregated:
            identity = {"source": "AppDependencies", "scopeId": scope, "windowStart": iso(bucket),
                        "windowEnd": iso(bucket + BUCKET), "sourceComponent": role, "destination": host,
                        "dependencyType": row["protocol"], "success": success, "calls": 0, "spanCount": 0}
            if host in endpoints:
                identity["targetResourceId"] = endpoints[host]
            aggregated[key] = identity
        for field in ("calls", "spanCount"):
            if type(row[field]) is not int or row[field] <= 0:
                raise ValueError("Invalid dependency count")
            aggregated[key][field] += row[field]
    return list(aggregated.values())


def collect(raw, hours, salt, at=None):
    checkpoint = timestamp(at or (raw / "readAt.txt").read_text().strip())
    end = checkpoint.replace(minute=checkpoint.minute // 5 * 5, second=0, microsecond=0)
    start = end - timedelta(hours=hours)
    rules = [r["properties"]["specification"] for r in read(raw / "discoveryRules.json")]
    scopes = {r["applicationInsightsResourceId"].lower(): r["applicationInsightsResourceId"]
              for r in rules if r["kind"] == "ApplicationInsightsTopology"}
    if not scopes:
        raise ValueError("The model has no Application Insights topology discovery rule")
    subscription = read(raw / "model.json")["id"].split("/")[2]
    endpoints = resource_endpoints(raw, subscription)
    result = []
    for scope in scopes.values():
        component = azure(ARM + scope + "?api-version=2020-02-02", subscription)
        workspace = workspace_id(component["properties"]["WorkspaceResourceId"], subscription)
        current = start
        while current < end:
            until = min(current + timedelta(days=1), end)
            result.extend(rows_at(scope, workspace, subscription, current, until, endpoints, salt))
            current = until
    return sorted(result, key=lambda r: (r["windowStart"], r["scopeId"], r["sourceComponent"], r["destination"],
                                          r["dependencyType"], str(r["success"])))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("raw", type=Path, help="private ARM export directory from export-model.py")
    parser.add_argument("--output", required=True, type=Path, help="local NDJSON for the entire requested interval")
    parser.add_argument("--hours", type=int, default=24, help="hours of completed 5-minute bins")
    parser.add_argument("--at", help="end of the collection in UTC; defaults to the model export time")
    parser.add_argument("--scrub", required=True, type=Path, metavar="LOCAL_SALT",
                        help="local key file the aliases of addresses, odd host names and roles are salted by")
    args = parser.parse_args()
    if args.hours <= 0:
        parser.error("--hours must be positive")
    salt = args.scrub.read_text().strip()
    if not salt:
        parser.error("The scrub key file is empty")
    rows = collect(args.raw, args.hours, salt, args.at)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(mode="w", dir=args.output.parent, delete=False) as out:
        for row in rows:
            out.write(json.dumps(row, separators=(",", ":"), allow_nan=False) + "\n")
    os.replace(out.name, args.output)
    counts = Counter()
    for row in rows:
        counts[row["dependencyType"]] += row["calls"]
    print(f"{len(rows)} dependency groups, {sum(counts.values())} calls", dict(counts))


if __name__ == "__main__":
    main()
