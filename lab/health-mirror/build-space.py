#!/usr/bin/env python3
"""Build a local Sattle space from a CloudHealth configuration and independent observations."""
import argparse
from collections import defaultdict
from datetime import datetime, timedelta, timezone
import ipaddress
import json
import math
from pathlib import Path

import mirror_program as gkql
import container_inputs
import platform_metrics
import record_adapter


def indexed(rows, column, label):
    result = {}
    for row in rows:
        key = row[column]
        if not isinstance(key, str) or not key or key in result:
            raise ValueError(f"{label} has an empty or duplicate {column}: {key}")
        result[key] = row
    return result


def finite(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def stamp(value):
    dt = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if dt.tzinfo is None:
        raise ValueError(f"Checkpoint needs a UTC offset: {value}")
    return dt.astimezone(timezone.utc)


def timestamp(dt):
    return dt.isoformat(timespec="microseconds").replace("+00:00", "Z")


def assert_configuration(model):
    # Check Azure status-bearing objects. A user-defined tag can legitimately be named "status".
    declarations = [model["model"].get("properties", {})]
    declarations.extend(entry["properties"] for entry in model["signalDefinitions"])
    for entity in model["entities"]:
        cfg = entity["properties"]
        declarations.append(cfg)
        declarations.extend(cfg.get("signalAggregationGroups", []))
        for group in cfg.get("signalGroups", {}).values():
            declarations.extend([group, group.get("resourceHealth", {}), *group.get("signals", [])])
    for declared in declarations:
        if any(k in declared for k in ("healthState", "aggregatedHealthState", "status", "reportedValue", "unresolvedMembers")):
            raise ValueError("Azure's observed output belongs in the reference file, outside the configuration")


def dependency_depth(entities, edges):
    children = defaultdict(list)
    incoming = dict.fromkeys(entities, 0)
    pairs = set()
    for item in edges:
        data = item["properties"]
        parent, child = data["parentEntityName"], data["childEntityName"]
        if parent not in entities or child not in entities:
            raise ValueError(f"Missing dependency endpoint: {parent} -> {child}")
        if (parent, child) in pairs:
            raise ValueError(f"Parallel dependency declarations cannot share an edge identity: {parent} -> {child}")
        pairs.add((parent, child))
        children[parent].append(child)
        incoming[child] += 1
    queue = [key for key, count in incoming.items() if count == 0]
    levels = dict.fromkeys(entities, 0)
    for parent in queue:
        for child in children[parent]:
            levels[child] = max(levels[child], levels[parent] + 1)
            incoming[child] -= 1
            if incoming[child] == 0:
                queue.append(child)
    if len(queue) != len(entities):
        raise ValueError("A dependency cycle has no finite bottom-up evaluation")
    return max(levels.values(), default=0)


def rule_fields(key, definition):
    kind = definition.get("signalKind")
    if kind == "AzureResourceHealth":
        return {"signalKind": kind}
    if kind not in ("AzureResourceMetric", "LogAnalyticsQuery"):
        raise ValueError(f"{key}: unsupported signal kind {kind}")
    rules = definition.get("evaluationRules", {})
    if not rules or set(rules) - {"degradedRule", "unhealthyRule"}:
        raise ValueError(f"{key}: unsupported or missing Azure signal evaluation rules")
    result = {"signalKind": kind}
    for severity in ("unhealthy", "degraded"):
        rule = rules.get(severity + "Rule")
        if rule is None:
            continue
        if (set(rule) - {"operator", "threshold", "evaluationType"}
                or rule.get("evaluationType", "Static") != "Static"
                or rule.get("operator") not in {"GreaterThan", "GreaterThanOrEqual", "LessThan", "LessThanOrEqual", "Equal", "NotEqual"} or not finite(rule.get("threshold"))):
            raise ValueError(f"{key}: unsupported {severity} threshold {rule}")
        result[severity + "Operator"] = rule["operator"]
        result[severity + "At"] = rule["threshold"]
    return result


def build_model(model):
    """Return captured declarations and dependency depth. No observation or reference states enter this step."""
    if model["schemaVersion"] != 1:
        raise ValueError("Unsupported model export schema")
    assert_configuration(model)
    entities = indexed(model["entities"], "name", "entities")
    if not entities:
        raise ValueError("Empty Azure health model")
    relationships = indexed(model["relationships"], "name", "relationships")
    library = indexed(model["signalDefinitions"], "name", "signal definitions")
    facts = []

    def emit(kind, key, cfg, **values):
        facts.append({"kind": kind, "key": key, "name": cfg.get("displayName", key), "configuration": cfg, **values})

    header = model["model"]
    emit("model", header["name"], header)
    for group, kind in [("discoveryRules", "discovery"), ("authenticationSettings", "authentication")]:
        for key, item in indexed(model.get(group, []), "name", group).items():
            emit(kind, key, item["properties"])
    declarations = {key: item["properties"] for key, item in library.items()}
    bound_signals = {}
    for key, item in entities.items():
        cfg = item["properties"]
        impact = cfg.get("impact", "Standard")
        if impact not in ("Standard", "Limited", "Suppressed"):
            raise ValueError(f"{key}: unsupported impact {impact}")
        if cfg.get("signalAggregationGroups") or cfg.get("healthModelReference") or cfg.get("healthStateOverride"):
            raise ValueError(f"{key}: unsupported signal aggregation, nested model, or entity override")
        groups = cfg.get("signalGroups", {})
        for group, settings in groups.items():
            if group == "dependencies":
                if settings.get("aggregationType", "WorstOf") != "WorstOf" or set(settings) - {"aggregationType", "ignoreUnknown"}:
                    raise ValueError(f"{key}: unsupported dependency aggregation {settings}")
                continue
            if group not in ("azureResource", "azureLogAnalytics"):
                raise ValueError(f"{key}: unsupported signal group {group}")
            for signal in settings.get("signals", []):
                address = (key, group, signal["name"])
                if address in bound_signals:
                    raise ValueError(f"Duplicate signal {address}")
                bound_signals[address] = signal
            auto = settings.get("resourceHealth", {})
            if auto.get("enabled") == "Enabled":
                name = auto["signalName"]
                address = (key, group, name)
                if address in bound_signals:
                    raise ValueError(f"Duplicate resource health signal {address}")
                bound_signals[address] = {"signalKind": "AzureResourceHealth", "name": name, **auto}
        resource = groups.get("azureResource", {}).get("azureResourceId", "")
        emit("entity", key, cfg, impact=impact, sourceKey=resource.lower(), iconName=cfg.get("icon", {}).get("iconName", ""),
             sourceModelId=header.get("sourceId", header["name"]))
    depth = dependency_depth(entities, relationships.values())
    for key, item in relationships.items():
        cfg = item["properties"]
        a, b = cfg["parentEntityName"], cfg["childEntityName"]
        emit("relationship", key, cfg, sourceKey=a, targetKey=b, impact=entities[b]["properties"].get("impact", "Standard"))
    bindings = indexed(model["bindings"], "id", "bindings")
    for key, binding in bindings.items():
        signal = bound_signals.pop((binding["entityId"], binding["group"], binding["signalName"]), None)
        if signal is None:
            raise ValueError(f"{key}: binding does not resolve to a deployed signal")
        definition = binding["definitionId"]
        if signal.get("signalDefinitionName"):
            if signal["signalDefinitionName"] != definition or definition not in library:
                raise ValueError(f"{key}: unresolved or mismatched reusable definition")
            local = set(signal) - {"name", "displayName", "signalDefinitionName"}
            conflicts = sorted(k for k in local if k not in declarations[definition] or signal[k] != declarations[definition][k])
            if conflicts:
                raise ValueError(f"{key}: unsupported binding overrides {conflicts}")
        else:
            if definition in declarations:
                raise ValueError(f"Duplicate inline definition {definition}")
            declarations[definition] = signal
        emit("binding", key, {**binding, "signal": signal}, sourceKey=binding["entityId"], targetKey=definition)
    if bound_signals:
        raise ValueError(f"Missing bindings for exported signals: {list(bound_signals)}")
    for key, cfg in declarations.items():
        emit("definition", key, cfg, **rule_fields(key, cfg))
    return facts, bindings, depth


def validate_reading(row, binding, signal_kind):
    status, value = row["status"], row.get("value")
    if any(k in row for k in ("state", "healthState", "referenceState")):
        raise ValueError("A signal input cannot contain Azure's output health state")
    if row["entityId"] != binding["entityId"] or row["definitionId"] != binding["definitionId"]:
        raise ValueError(f"Observation is attached to a different entity or definition: {row['bindingId']}")
    if status == "value":
        if signal_kind == "AzureResourceHealth":
            if row.get("availabilityState") not in ("Available", "Unavailable", "Degraded", "Unknown") or value is not None:
                raise ValueError(f"Invalid Resource Health availability: {row['bindingId']}")
        elif not finite(value):
            raise ValueError(f"Measured binding needs a finite number: {row['bindingId']}")
    elif status not in ("noData", "queryError") or value is not None:
        raise ValueError(f"Unsupported or inconsistent observation status: {status}")
    end = stamp(row["windowEnd"])
    start = end if signal_kind == "AzureResourceHealth" and row["windowStart"] is None else stamp(row["windowStart"])
    if end < start or end > stamp(row["checkpoint"]):
        raise ValueError(f"Invalid or future observation window: {row['bindingId']}")


def network_envelopes(path):
    if not path.exists():
        return []
    result = []
    for index, line in enumerate(path.read_text().splitlines(), 1):
        if not line.strip():
            continue
        try:
            row = json.loads(line)
            if row["source"] != "vnet-flow-log":
                raise ValueError("expected vnet-flow-log source")
            at = stamp(row["capturedAt"])
            payload = row["payload"]
            if not isinstance(payload["targetResourceID"], str) or not payload["targetResourceID"]:
                raise ValueError("expected a VNet scope")
            for flow in payload["flowRecords"]["flows"]:
                for group in flow["flowGroups"]:
                    for raw in group["flowTuples"]:
                        parts = raw.split(",")
                        if len(parts) != 13 or not parts[0].isdigit() or int(parts[0]) > 9223372036854:
                            raise ValueError("expected a Network Watcher v2 tuple with a millisecond timestamp")
                        for address in parts[1:3]:
                            ipaddress.ip_address(address)
                        for value, bound in zip(parts[3:6], (65535, 65535, 255)):
                            if not value.isdigit() or int(value) > bound:
                                raise ValueError("invalid port or IP protocol")
                        if any(v and not v.isdigit() for v in parts[9:]):
                            raise ValueError("invalid packet or byte count")
        except (KeyError, TypeError, AttributeError, ValueError) as exc:
            raise ValueError(f"{path.name} line {index}: {exc}") from exc
        result.append((at, index, line))
    return [line for at, index, line in sorted(result)]


def dependency_envelopes(path):
    if not path.exists():
        return []
    windows = {}
    for index, line in enumerate(path.read_text().splitlines(), 1):
        if not line.strip():
            continue
        try:
            row = json.loads(line)
            keys = ("scopeId", "sourceComponent", "destination", "dependencyType")
            if row["source"] != "AppDependencies" or any(not isinstance(row[k], str) or not row[k] for k in keys):
                raise ValueError("expected an AppDependencies group with a scope, role, destination and type")
            if (row["success"] is not None and type(row["success"]) is not bool
                    or type(row["calls"]) is not int or not 0 <= row["calls"] <= 2**63 - 1
                    or type(row["spanCount"]) is not int or not 1 <= row["spanCount"] <= 2**63 - 1):
                raise ValueError("invalid result, sampling-weighted call count or span count")
            if "targetResourceId" in row and not isinstance(row["targetResourceId"], str):
                raise ValueError("invalid target resource identifier")
            start, end = stamp(row["windowStart"]), stamp(row["windowEnd"])
            at = stamp(row.get("capturedAt", row["windowEnd"]))
            if not start < end <= at:
                raise ValueError("invalid or incomplete dependency window")
            key = (at, start, end, row["scopeId"].lower(), row["sourceComponent"], row["destination"].lower(), row["dependencyType"], str(row["success"]))
            if key in windows and windows[key] != row:
                raise ValueError("conflicting counts for the same dependency group")
            windows[key] = row
        except (KeyError, TypeError, ValueError) as exc:
            raise ValueError(f"{path.name} line {index}: {exc}") from exc
    return [(key[0], row) for key, row in sorted(windows.items())]


def build(fixture, output, captured_at=None, deployment_only=False):
    inventory = container_inputs.inventory_events(fixture / "container-apps.ndjson")
    if deployment_only:
        inventory = [row for row in inventory if "/providers/microsoft.cloudhealth/" not in
                     (row["payload"].get("id") or row["payload"].get("properties", {}).get("targetResourceId", "")).casefold()]
    model = json.loads((fixture / "model.json").read_text()) if not deployment_only and (fixture / "model.json").exists() else None
    if model is None and not inventory:
        raise ValueError("Supply a model export or a Container Apps inventory")
    facts, bindings, depth = build_model(model) if model else ([], {}, 0)
    sidecar = fixture / "flow-ontology.json"
    ontology = container_inputs.ontology_facts(json.loads(sidecar.read_text()) if sidecar.exists() else None)
    definitions = {f["key"]: f["signalKind"] for f in facts if f["kind"] == "definition"}
    events = defaultdict(dict)
    observations = fixture / "observations.ndjson"
    for line in observations.read_text().splitlines() if not deployment_only and (observations.exists() or bindings) else []:
        if not line.strip():
            continue
        row = json.loads(line)
        key, at = row["bindingId"], stamp(row["checkpoint"])
        if key not in bindings:
            raise ValueError(f"Reading references unknown binding {key}")
        validate_reading(row, bindings[key], definitions[bindings[key]["definitionId"]])
        if key in events[at] and events[at][key] != row:
            raise ValueError(f"Conflicting readings for {key} at {at}")
        events[at][key] = row
    if bindings and not events:
        raise ValueError("A model with signals requires independent observations")
    for at, rows in events.items():
        if rows.keys() != bindings.keys():
            raise ValueError(f"Incomplete checkpoint {at}: missing {sorted(bindings.keys() - rows.keys())}")
    if events:
        base = min(events) - timedelta(seconds=1)
    else:
        captured_at = captured_at or (model or {}).get("capturedAt") or (inventory[0]["capturedAt"] if inventory else None)
        if not captured_at:
            raise ValueError("A model without signals needs its configuration export timestamp (--captured-at)")
        base = stamp(captured_at)
    network = network_envelopes(fixture / "network.ndjson")
    app_calls = dependency_envelopes(fixture / "dependencies.ndjson")
    defs = output / "definitions"
    capture = output / "capture"
    defs.mkdir(parents=True, exist_ok=True)
    capture.mkdir(parents=True, exist_ok=True)
    gkql.materialize(defs, depth)

    def lines(event):
        return "".join(record_adapter.compact(row) + "\n" for row in record_adapter.normalize(event))

    def envelope(at, payload, source="azure-cloud-health"):
        return lines({"source": source, "capturedAt": timestamp(at), "payload": payload})

    with (capture / "model.ndjson").open("w") as stream:
        for fact in facts:
            stream.write(envelope(base, fact))
    with (capture / "ontology.ndjson").open("w") as stream:
        for fact in ontology:
            stream.write(envelope(base, fact, "workload-ontology"))
    with (capture / "observations.ndjson").open("w") as stream:
        for at, rows in sorted(events.items()):
            for key, reading in sorted(rows.items()):
                fact = {"kind": "observation", "key": key, "configuration": reading, "name": reading.get("signalName", bindings[key]["signalName"]),
                        **{k: reading[k] for k in ("value", "status", "checkpoint", "availabilityState") if k in reading}}
                stream.write(envelope(at, fact))
    with (capture / "network.ndjson").open("w") as stream:
        for line in network:
            stream.write(lines(json.loads(line)))
    with (capture / "dependencies.ndjson").open("w") as stream:
        for at, record in app_calls:
            stream.write(envelope(at, record, "app-dependencies"))
    with (capture / "inventory.ndjson").open("w") as stream:
        for row in container_inputs.arm_records(inventory):
            stream.write(lines(row))
    with (capture / "platform-metrics.ndjson").open("w") as stream:
        for row in platform_metrics.records(fixture):
            stream.write(lines(row))
    with (capture / "requests.ndjson").open("w") as stream:
        operations = [*container_inputs.request_records(fixture / "request-observations.ndjson", "AppRequests"),
                      *container_inputs.request_records(fixture / "request-dependencies.ndjson", "AppDependencies")]
        for row in sorted(operations, key=lambda r: stamp(r["capturedAt"])):
            stream.write(lines(row))
    print(f"healthMirror · {len((model or {}).get('entities',[]))} model entities · {len(bindings)} bindings · {len(events)} health checkpoints · {len(inventory)} inventory records")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--deployment-only", action="store_true", help="Use only deployment inventory, project ontology and telemetry")
    parser.add_argument("--captured-at", help="Configuration export time for a model with no signals")
    args = parser.parse_args()
    try:
        build(args.fixture, args.out, args.captured_at, args.deployment_only)
    except (ValueError, KeyError, OSError) as error:
        parser.exit(1, f"Cannot build mirror: {error}\n")
