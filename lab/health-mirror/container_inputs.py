"""Translate explicit workload declarations into evidence-backed flow and rule records."""

import json
from datetime import datetime
import ipaddress


def inventory_events(path):
    if not path.exists():
        return []
    events = [json.loads(line) for line in path.read_text().splitlines() if line.strip()]
    for row in events:
        if row.get("source") != "arg" or not row.get("capturedAt"):
            raise ValueError("Container Apps inventory requires timestamped native Resource Graph envelopes")
        p = row["payload"]
        if row.get("meta", {}).get("query", "").endswith("changes"):
            if p["properties"].get("changeType") != "Delete" or not p["properties"].get("targetResourceId"):
                raise ValueError("Invalid ARM inventory deletion")
        elif not all(p.get(k) for k in ("id", "name", "type")):
            raise ValueError("ARM inventory requires id, name and type")
    return events


def ontology_facts(sidecar):
    """These rows carry declared relationships, never series values or computed health."""
    facts = {}

    def put(kind, key, name, config, **fields):
        row = {"kind": kind, "key": key, "name": name, "configuration": config, **fields}
        old = facts.setdefault((kind, key), row)
        if old != row:
            raise ValueError(f"Conflicting {kind} declarations for {key}")

    if sidecar is None:
        return list(facts.values())
    scope = sidecar["scopeId"].rstrip("/").lower()
    if (
        sidecar["schemaVersion"] != 1
        or not scope.startswith("/subscriptions/")
        or "/resourcegroups/" not in scope
    ):
        raise ValueError("Local ontology needs schemaVersion 1 and an ARM resource-group scopeId")
    for rule in sidecar.get("nameRules", []):
        if rule["match"] not in ("prefix", "suffix", "contains") or not all(
            isinstance(rule.get(k), str) and rule[k].strip() for k in ("id", "pattern", "class", "evidence")
        ):
            raise ValueError("A name rule needs id, prefix/suffix/contains, pattern, class and evidence")
        put(
            "nameRule",
            f"{scope}/nameRules/{rule['id']}",
            rule["id"],
            rule,
            scopeId=scope,
            targetKey=rule["pattern"],
            matchKind=rule["match"],
            className=rule["class"],
            evidence=rule["evidence"],
        )
    for flow in sidecar.get("flows", []):
        flow_id = f"{scope}/flows/{flow['id']}"
        if not flow.get("evidence", "").strip():
            raise ValueError(f"{flow_id}: describe the source of this journey")
        put(
            "userFlow",
            flow_id,
            flow["name"],
            {"source": "LocalOntology", "id": flow["id"], "evidence": flow["evidence"]},
            provenance="LocalOntology",
            scopeId=scope,
            localId=flow["id"],
        )
        for step in flow["systems"]:
            step_id = f"{scope}/systems/{step['id']}"
            if "apps" in step or not step.get("evidence", "").strip():
                raise ValueError(f"{step_id}: use evidenced class requirements for deployment membership")
            put(
                "systemComponent",
                step_id,
                step["name"],
                {"source": "LocalOntology", "id": step["id"], "evidence": step["evidence"]},
                provenance="LocalOntology",
                localId=step["id"],
                scopeId=scope,
            )
            put(
                "flowStep",
                f"{flow_id}/{step['id']}",
                step["name"],
                {
                    "source": "LocalOntology",
                    "flow": flow["id"],
                    "system": step["id"],
                    "evidence": flow["evidence"],
                },
                sourceKey=flow_id,
                targetKey=step_id,
            )
            for requirement in step.get("requires", []):
                required = requirement["class"]
                if not required.strip() or not requirement.get("evidence", "").strip():
                    raise ValueError(f"{step_id}: a class requirement needs a class and evidence")
                put(
                    "stepRequirement",
                    f"{step_id}/classes/{required}",
                    required,
                    {"source": "LocalOntology", "scopeId": scope, **requirement},
                    sourceKey=step_id,
                    targetKey=required,
                    scopeId=scope,
                )
            for collection, source in (("requests", "AppRequests"), ("spans", "AppDependencies")):
                for check in step.get(collection, []):
                    if not isinstance(check.get("role"), str) or not all(
                        isinstance(check.get(k), str) and check[k].strip()
                        for k in ("operation", "evidence")
                    ):
                        raise ValueError(
                            f"{step_id}: an operation selector needs a role, operation and evidence"
                        )
                    if source == "AppDependencies" and (
                        check.get("relationship") != "direct-child-span"
                        or not all(check.get(k) for k in ("target", "evidenceType"))
                    ):
                        raise ValueError(
                            f"{step_id}: a span check needs a direct parent request and exact target"
                        )
                    if source == "AppRequests" and any(k in check for k in ("relationship", "evidenceType", "target")):
                        raise ValueError(f"{step_id}: specify an HTTP route or a direct child span")
                    selector = operation_key(scope, check, source)
                    put(
                        "operationSelector",
                        step_id + "/operations/" + selector,
                        check["operation"],
                        {"source": "LocalOntology", "scopeId": scope, "telemetrySource": source, **check},
                        sourceKey=step_id,
                        targetKey=selector,
                        scopeId=scope,
                        role=check["role"],
                    )
    return list(facts.values())


def operation_key(scope, row, source):
    return json.dumps(
        [
            scope.lower(),
            source,
            row["role"],
            row["operation"],
            row.get("relationship", "inbound-request"),
            row.get("evidenceType", "http-request"),
            row.get("target", ""),
        ],
        separators=(",", ":"),
    )


def request_records(path, source):
    """One keyed result per scoped route. A later query window replaces its prior result."""
    if not path.exists():
        return []
    counts = (
        "calls",
        "spanCount",
        "failed",
        "unknown",
        "attributedCalls",
        "attributedFailed",
        "attributedUnknown",
    )
    rows = {}
    for raw in path.read_text().splitlines():
        if not raw.strip():
            continue
        row = json.loads(raw)
        if row["source"] != source or any(type(row[k]) is not int or row[k] < 0 for k in counts):
            raise ValueError("Expected nonnegative integer operation counts")
        if (
            row["failed"] + row["unknown"] > row["calls"]
            or row["attributedCalls"] > row["calls"]
            or row["attributedFailed"] + row["attributedUnknown"] > row["attributedCalls"]
            or row["attributedFailed"] > row["failed"]
            or row["attributedUnknown"] > row["unknown"]
        ):
            raise ValueError("Inconsistent request counts")
        scope = row["scopeId"].lower()
        if not row["componentId"].lower().startswith(scope + "/providers/microsoft.insights/components/"):
            raise ValueError("Request telemetry belongs to a different scope")
        selector = operation_key(scope, row, source)
        start, seen, end = (
            datetime.fromisoformat(row[k].replace("Z", "+00:00"))
            for k in ("windowStart", "lastSeen", "windowEnd")
        )
        if start.tzinfo is None or seen.tzinfo is None or end.tzinfo is None or not start <= seen < end:
            raise ValueError("Invalid request observation window")
        values = {
            k: row[k]
            for k in (
                "scopeId",
                "componentId",
                "source",
                "role",
                "operation",
                "windowStart",
                "windowEnd",
                "lastSeen",
                *counts,
            )
        }
        hosts = row["ingressHosts"]
        if not isinstance(hosts, list) or not all(isinstance(host, str) and host for host in hosts):
            raise ValueError("Expected a list of observed ingress hosts")
        values.update(
            selector=selector,
            ingressHosts=json.dumps(sorted(set(hosts)), separators=(",", ":")),
            relationship=row.get("relationship", "inbound-request"),
            evidenceType=row.get("evidenceType", "http-request"),
            target=row.get("target", ""),
        )
        event = {
            "source": "records:OperationObservationRow",
            "capturedAt": row["windowEnd"],
            "payload": {"key": selector, "values": values},
        }
        if (end, selector) in rows and rows[end, selector] != event:
            raise ValueError(f"Conflicting operation query results for {row['operation']}")
        rows[end, selector] = event
    return [v for _, v in sorted(rows.items())]


def arm_records(envelopes, pending_keys=None):
    """Retain history to retract old references; optionally publish only the selected native events."""
    for source_key, row in _arm_changes(envelopes):
        if pending_keys is None or source_key in pending_keys:
            yield row


def _arm_changes(envelopes):
    """Map observed ARM fields to row values; replace each resource's reference set on a refresh."""
    last_relations = {}
    for event in sorted(
        envelopes, key=lambda e: datetime.fromisoformat(e["capturedAt"].replace("Z", "+00:00"))
    ):
        at, raw = event["capturedAt"], event["payload"]
        deleting = event.get("meta", {}).get("query", "").endswith("changes")
        arm_id = raw["properties"]["targetResourceId"] if deleting else raw["id"]
        resource_key = arm_id.lower()

        def record(row_type, key, values=None):
            return {
                "source": "records:" + row_type,
                "capturedAt": at,
                "meta": {"source": "resource-graph:inventory", "position": at},
                "payload": {"key": key, **({"values": values} if values is not None else {"deleted": True})},
            }

        if deleting:
            yield event.get("key"), record("ArmInventoryRow", resource_key)
            for key in last_relations.pop(resource_key, {}):
                yield event.get("key"), record("AzureNetworkRow", key)
            continue
        p = raw.get("properties") or {}
        subscription, group = raw.get("subscriptionId") or "", raw.get("resourceGroup") or ""
        grp = "/subscriptions/" + subscription + "/resourceGroups/" + group if subscription and group else ""
        env_id = p.get("environmentId") or p.get("managedEnvironmentId") or ""
        yield (
            event.get("key"),
            record(
                "ArmInventoryRow",
                resource_key,
                {
                    "armId": arm_id,
                    "armKey": resource_key,
                    "name": raw["name"],
                    "resourceType": raw["type"].lower(),
                    "location": (raw.get("location") or "").lower(),
                    "subscriptionId": subscription,
                    "resourceGroup": group,
                    "groupId": grp.lower(),
                    "environmentId": env_id.lower(),
                    "provisioningState": p.get("provisioningState") or "",
                    "runningStatus": p.get("runningStatus") or "",
                    "ingressHost": ((p.get("configuration") or {}).get("ingress") or {}).get("fqdn") or "",
                    "tags": json.dumps(raw.get("tags") or {}, sort_keys=True),
                    "observedAt": at,
                },
            ),
        )
        facts = {}

        def add(kind, path, target, address=None):
            key = json.dumps([resource_key, path, target, address], separators=(",", ":"))
            facts[key] = {
                "resourceId": resource_key,
                "kind": kind,
                "target": target,
                "resolvedAddress": address,
                "fieldPath": path,
            }

        def visit(value, path, field_name):
            if isinstance(value, dict):
                for k, v in value.items():
                    visit(v, path + "." + k, k.lower())
            elif isinstance(value, list):
                for i, v in enumerate(value):
                    visit(v, f"{path}[{i}]", field_name)
            elif isinstance(value, str) and value:
                if value.lower().startswith("/subscriptions/"):
                    add("armId", path, value.lower())
                elif field_name in (
                    "fqdn",
                    "fqdns",
                    "privatednszonename",
                    "defaultdomain",
                    "defaultdomainname",
                    "internaldomainnamesuffix",
                ):
                    add("dnsName", path, value.lower().rstrip("."))
                else:
                    try:
                        ip = ipaddress.ip_address(value)
                    except ValueError:
                        ip = None
                    if ip is not None:
                        add("ip", path, str(ip))
                    elif "/" in value:
                        try:
                            add("prefix", path, str(ipaddress.ip_network(value, strict=False)))
                        except ValueError:
                            pass

        visit(p, "properties", "")
        if raw["type"].lower() in (
            "microsoft.network/privatednszones/a",
            "microsoft.network/privatednszones/aaaa",
        ):
            zone = raw.get("managedBy") or ""
            if zone and "/" not in zone:
                host = zone if raw["name"] == "@" else raw["name"] + "." + zone
                for i, item in enumerate(p.get("records", [])):
                    address = item.get("ipv4Address") or item.get("ipv6Address")
                    if address:
                        add("dnsName", f"properties.records[{i}]", host.lower(), address)
        previous = last_relations.get(resource_key, {})
        for key in sorted(previous.keys() - facts.keys()):
            yield event.get("key"), record("AzureNetworkRow", key)
        for key, v in sorted(facts.items()):
            if previous.get(key) != v:
                yield event.get("key"), record("AzureNetworkRow", key, v)
        last_relations[resource_key] = facts
