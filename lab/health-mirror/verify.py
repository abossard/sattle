#!/usr/bin/env python3
"""Compare Sattle's emitted snapshots and patches to held-out Azure model evaluations."""
import argparse
import copy
import ipaddress
import json
import math
import re
from collections import Counter, defaultdict
from datetime import datetime
from pathlib import Path


def read(path):
    return json.loads(path.read_text())


def instant(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def index(rows, key):
    by_key = {}
    for row in rows:
        value = row[key]
        if value in by_key:
            raise ValueError(f"Duplicate {key}: {value}")
        by_key[value] = row
    return by_key


def unwrap(item):
    return item["value"]


def properties(item):
    return {unwrap(entry["property"]).split(".")[-1]: entry["value"].get("value") for entry in item["properties"]}


def objects(items):
    return index([{**properties(item), "_id": unwrap(item["id"])} for item in items], "key")


def by_type(items, included):
    typed = defaultdict(list)
    for item in items:
        kind = unwrap(item["type"]).split(".")[-1]
        if kind in included:
            typed[kind].append(item)
    return {kind: objects(values) for kind, values in typed.items()}


class Checks:
    def __init__(self):
        self.failures = []
        self.total = Counter()

    def equal(self, section, key, expected, actual):
        self.total[section] += 1
        if expected != actual:
            self.failures.append({"section": section, "key": key, "expected": expected, "actual": actual})

    def keys(self, section, label, expected, actual):
        self.equal(section, label, sorted(expected), sorted(actual))


def config_checks(model, work, audit):
    directory = work / "replay"
    master = read(directory / "master.final.snapshot.json")["extent"]
    entities = {r["name"]: r["properties"] for r in model["entities"]}
    edges = {r["name"]: r["properties"] for r in model["relationships"]}
    bindings = index(model["bindings"], "id")
    definitions = {r["name"]: r["properties"] for r in model["signalDefinitions"]}
    declared = {}
    for name, entity in entities.items():
        for group, settings in entity.get("signalGroups", {}).items():
            for s in settings.get("signals", []):
                declared[name, group, s["name"]] = s
            auto = settings.get("resourceHealth", {})
            if auto.get("enabled") == "Enabled":
                declared[name, group, auto["signalName"]] = {"signalKind": "AzureResourceHealth", "name": auto["signalName"], **auto}
    bound = {}
    for key, binding in bindings.items():
        sig = declared.pop((binding["entityId"], binding["group"], binding["signalName"]))
        bound[key] = {**binding, "signal": sig}
        if not sig.get("signalDefinitionName"):
            definitions[binding["definitionId"]] = sig
    audit.equal("config", "missing bindings", [], sorted(declared))
    expected = {"ModelEntity": entities, "Model": {model["model"]["name"]: model["model"]}, "SignalBinding": bound,
                "SignalDefinition": definitions, "AuthenticationSetting": {r["name"]: r["properties"] for r in model["authenticationSettings"]},
                "DiscoveryRule": {r["name"]: r["properties"] for r in model["discoveryRules"]}}
    masters = by_type(master["entities"], expected.keys() | {"Observation"})
    for kind, wanted in expected.items():
        actual = masters.get(kind, {})
        audit.keys("config", kind, wanted, actual)
        for key in wanted.keys() & actual.keys():
            audit.equal("config", f"{kind}/{key}", wanted[key], json.loads(actual[key]["configuration"]))

    for graph in ("master", "structure", "health"):
        extent = read(directory / f"{graph}.final.snapshot.json")["extent"]
        local = by_type(extent["entities"], {"ModelEntity"}).get("ModelEntity", {})
        by_id = {value["_id"]: key for key, value in local.items()}
        audit.keys("topology", graph + " entity IDs", entities, local)
        for key in entities.keys() & local.keys():
            audit.equal("topology", f"{graph}/{key}/identity", masters["ModelEntity"][key]["_id"], local[key]["_id"])
            audit.equal("topology", f"{graph}/{key}/configuration", entities[key], json.loads(local[key]["configuration"]))
        seen = {}
        for edge in extent["relationships"]:
            if unwrap(edge["type"]).split(".")[-1] != "DependsOn":
                continue
            p = properties(edge)
            key = p["key"]
            if key in seen:
                raise ValueError(f"Duplicate dependency {key}")
            seen[key] = True
            cfg = edges.get(key, {})
            route = [by_id.get(unwrap(edge[end])) for end in ("source", "target")]
            audit.equal("topology", f"{graph}/{key}/direction", [cfg.get("parentEntityName"), cfg.get("childEntityName")], route)
            audit.equal("topology", f"{graph}/{key}/declaration", cfg, json.loads(p["configuration"]))
        audit.keys("topology", graph + " relationships", edges, seen)

    expected_links = defaultdict(list)
    for key, b in bindings.items():
        for kind, left, right in [("HasSignal", ("ModelEntity", b["entityId"]), ("SignalBinding", key)),
                                  ("UsesDefinition", ("SignalBinding", key), ("SignalDefinition", b["definitionId"])),
                                  ("MeasuredBy", ("SignalBinding", key), ("Observation", key))]:
            expected_links[kind].append((masters[left[0]][left[1]]["_id"], masters[right[0]][right[1]]["_id"]))
    for kind, pairs in expected_links.items():
        actual = [(unwrap(edge["source"]), unwrap(edge["target"])) for edge in master["relationships"]
                  if unwrap(edge["type"]).split(".")[-1] == kind]
        audit.equal("bindings", kind, sorted(pairs), sorted(actual))


def network_checks(fixture, work, model, audit, graph=None):
    def records(name):
        path = fixture / name
        return [json.loads(line) for line in path.read_text().splitlines() if line.strip()] if path.exists() else []

    def host_key(host, role):
        name = host.rstrip(".")
        try:
            loopback = ipaddress.ip_address(name).is_loopback
        except ValueError:
            loopback = name == "localhost" or name.endswith(".localhost")
        return ("DependencyHost", host, role[1:] if loopback else ())

    model_resources = defaultdict(set)
    for entity in model["entities"]:
        resource = entity["properties"].get("signalGroups", {}).get("azureResource", {}).get("azureResourceId")
        if resource:
            model_resources[resource.lower()].add(("ModelEntity", entity["name"]))
    latest = {}
    for row in sorted(records("dependencies.ndjson"), key=lambda r: instant(r.get("capturedAt", r["windowEnd"]))):
        key = (row["scopeId"].lower(), row["sourceComponent"], row["destination"].lower(), row["dependencyType"],
               row["windowStart"], row["windowEnd"], str(row["success"]))
        latest[key] = row
    routes = defaultdict(Counter)
    expected = set()
    for (scope, role_name, host, *_), row in latest.items():
        role = ("ApplicationRole", scope, role_name)
        target = host_key(host, role)
        route = routes[role, target]
        route["calls"] += row["calls"]
        route["spanCount"] += row["spanCount"]
        if row["success"] is False:
            route["failedCalls"] += row["calls"]
        elif row["success"] is None:
            route["unknownResultCalls"] += row["calls"]
        expected.update(("InstrumentedBy", role, e, ()) for e in model_resources[scope])
        expected.update(("EndpointOf", target, e, ()) for e in model_resources[row.get("targetResourceId", "").lower()])
    count_keys = ("calls", "spanCount", "failedCalls", "unknownResultCalls")
    expected.update(("CalledEndpoint", a, b, tuple(counts[k] for k in count_keys)) for (a, b), counts in routes.items())
    tuples = set()
    for row in records("network.ndjson"):
        p = row["payload"]
        for flow in p["flowRecords"]["flows"]:
            for group in flow["flowGroups"]:
                tuples.update((p["targetResourceID"], p.get("macAddress", ""), flow.get("aclID", ""), group.get("rule", ""), raw)
                              for raw in group["flowTuples"])
    pair_counts = Counter()
    for vnet, observer, acl, rule, raw in tuples:
        src, dst = raw.split(",")[1:3]
        pair_counts[("IpAddress", vnet, src), ("IpAddress", vnet, dst)] += 1
    expected.update(("ObservedTraffic", a, b, (count,)) for (a, b), count in pair_counts.items())

    if graph is None:
        graph = read(work / "replay/network.final.snapshot.json")["extent"]
    nodes = {}
    for n in graph["entities"]:
        kind, p = unwrap(n["type"]).split(".")[-1], properties(n)
        if kind == "ApplicationRole":
            key = (kind, p["scopeId"].lower(), p["sourceComponent"])
        elif kind == "DependencyHost":
            key = (kind, p["destination"], tuple(json.loads(p["endpointScope"] or "[]")))
        elif kind == "IpAddress":
            key = (kind, p["vnetId"], p["address"])
        else:
            key = (kind, p.get("key"))
        nodes[unwrap(n["id"])] = key
    actual = []
    for edge in graph["relationships"]:
        kind, props = unwrap(edge["type"]).split(".")[-1], properties(edge)
        columns = count_keys if kind == "CalledEndpoint" else ("tuples",) if kind == "ObservedTraffic" else ()
        actual.append((kind, nodes[unwrap(edge["source"])], nodes[unwrap(edge["target"])], tuple(int(props[k]) for k in columns)))
    audit.keys("network", "observed routes and counts", expected, actual)
    audit.keys("network", "observed endpoints and model resources", {n for edge in expected for n in edge[1:3]}, nodes.values())
    master = read(work / "replay/master.final.snapshot.json")["extent"]["entities"]
    raw_tuples = [properties(n) for n in master if unwrap(n["type"]).split(".")[-1] == "NetworkFlow"]
    audit.keys("network", "native VNet tuples", tuples, [tuple(p[k] for k in ("vnetId", "observer", "acl", "rule", "tuple")) for p in raw_tuples])
    windows = [properties(n) for n in master if unwrap(n["type"]).split(".")[-1] == "DependencyObservation"]
    results = {"success": "True", "failure": "False", "unknown": "None"}
    audit.keys("network", "AppDependencies windows",
               [(key, r["calls"], r["spanCount"], r.get("targetResourceId", "")) for key, r in latest.items()],
               [((p["scopeId"].lower(), p["sourceComponent"], p["destination"], p["dependencyType"],
                  p["windowStart"], p["windowEnd"], results[p["result"]]), int(p["calls"]), int(p["spanCount"]), p["targetResourceId"])
                for p in windows])


def checkpoint_snapshots(directory):
    # Graphs omit a patch when their contents stay the same. The import batch in
    # the patch cause orders both graphs even when their revision numbers diverge.
    changes = defaultdict(list)
    current = {}
    for graph in ("signals", "health"):
        initial = read(directory / f"{graph}.snapshot.json")["extent"]
        current[graph] = {unwrap(r["id"]): r for r in initial["entities"]}
        for patch in read(directory / f"{graph}.patches.json"):
            batches = [int(m[1]) for i in patch["cause"]["inputs"] if (m := re.fullmatch(r"export#(\d+)", unwrap(i)))]
            if not batches:
                raise ValueError(f"{graph}: missing export batch on patch")
            changes[max(batches)].append((graph, patch))
    checkpoints = {}
    for _, patches in sorted(changes.items()):
        for graph, patch in patches:
            for edit in patch["edits"]:
                if edit["kind"] == "entity":
                    key = unwrap(edit["id"])
                    if edit["after"] is None:
                        current[graph].pop(key)
                    else:
                        current[graph][key] = edit["after"]
        signals = objects(current["signals"].values())
        clocks = {instant(s["checkpoint"]) for s in signals.values()}
        if len(clocks) == 1:
            checkpoints[clocks.pop()] = (signals, objects(current["health"].values()))
    for graph, content in current.items():
        final = read(directory / f"{graph}.final.snapshot.json")["extent"]
        if content != {unwrap(i["id"]): i for i in final["entities"]}:
            raise ValueError(f"{graph}: replayed patches and final snapshot differ")
    return checkpoints


def same_number(actual, expected):
    if expected is None or actual is None:
        return expected is None and actual is None
    return math.isclose(float(actual), float(expected), rel_tol=1e-9, abs_tol=1e-9)


def compare_checkpoint(ref, snapshot, obs, model):
    audit = Checks()
    signals, health = snapshot or ({}, {})
    wanted_signals = index(ref["signals"], "bindingId")
    wanted_entities = index(ref["entities"], "entityId")
    audit.keys("coverage", "Azure binding coverage", [r["id"] for r in model["bindings"]], wanted_signals)
    audit.keys("coverage", "Azure entity coverage", [r["name"] for r in model["entities"]], wanted_entities)
    audit.keys("coverage", "Sattle evaluated signals", wanted_signals, signals)
    audit.keys("coverage", "Sattle evaluated entities", wanted_entities, health)
    audit.keys("coverage", "source observations", wanted_signals, obs)
    unavailable = Counter()
    bindings = index(model["bindings"], "id")
    entities = index(model["entities"], "name")
    library = {r["name"]: r["properties"] for r in model["signalDefinitions"]}
    for key in sorted(wanted_signals.keys() & signals.keys() & obs.keys()):
        ref_row, signal, measured = wanted_signals[key], signals[key], obs[key]
        audit.equal("signalStates", key, ref_row["state"], signal["state"])
        if ref_row["state"] is None:
            audit.equal("coverage", key + "/reference state", "reported", None)
        expected, actual = ref_row.get("value"), signal.get("value")
        audit.equal("signalValues", key, expected, expected if same_number(actual, expected) else actual)
        audit.equal("source", key + "/availability", ref_row.get("availabilityState"), signal.get("availabilityState") or None)
        audit.equal("source", key + "/sampling status", measured["status"], signal.get("status"))
        if "resolvedScopeType" in measured or "declaredScopeObservation" in measured:
            binding = bindings[key]
            group = entities[binding["entityId"]]["properties"]["signalGroups"][binding["group"]]
            attachment = next(s for s in group["signals"] if s["name"] == binding["signalName"])
            definition = {**library.get(binding["definitionId"], {}), **attachment}
            declaration = measured.get("declaredScopeObservation", {})
            audit.equal("source", key + "/resolved metric namespace", definition.get("metricNamespace"), measured.get("resolvedScopeType"))
            audit.equal("source", key + "/scope resolution", {
                "status": "queryError", "errorCode": "InvalidMetricNamespace",
                "value": None, "resourceType": "Microsoft.Storage/storageAccounts",
            }, declaration)
            if declaration.get("errorCode"):
                unavailable[declaration["errorCode"]] += 1
        if ref_row.get("reportedAt"):
            window_end = instant(ref_row["reportedAt"])
            if measured.get("source") == "AzureMonitorMetrics":
                window_end = window_end.replace(second=0, microsecond=0)
            audit.equal("source", key + "/evaluation window", window_end, instant(measured["windowEnd"]))
        if measured["status"] not in ("value", "noData"):
            audit.equal("source", key + "/read", "independent observation", measured.get("errorCode", measured["status"]))
        if not same_number(signal.get("value"), measured.get("value")):
            audit.equal("source", key + "/recorded value", measured.get("value"), signal.get("value"))
        if ref_row.get("errorCode") not in (None, "NoData"):
            audit.equal("source", key + "/provider error", ref_row["errorCode"], measured.get("errorCode"))
    for key in sorted(wanted_entities.keys() & health.keys()):
        audit.equal("entityStates", key, wanted_entities[key]["state"], health[key]["state"])
        if wanted_entities[key]["state"] is None:
            audit.equal("coverage", key + "/reference state", "reported", None)
    return {"capturedAt": ref["capturedAt"], "phase": ref.get("phase", "comparison"), "checks": audit.total,
            "passed": not audit.failures, "failures": audit.failures, "sourceRejections": unavailable}


def verify(fixture, work, scope, reference=None):
    model = read(fixture / "model.json")
    reference = reference or read(fixture / "reference.json")
    snapshots = checkpoint_snapshots(work / "replay")
    observations = defaultdict(dict)
    for line in (fixture / "observations.ndjson").read_text().splitlines():
        if not line.strip():
            continue
        row = json.loads(line)
        rows = observations[instant(row["checkpoint"])]
        if row["bindingId"] in rows:
            raise ValueError("Duplicate observation for the same checkpoint and binding")
        rows[row["bindingId"]] = row
    audit = Checks()
    config_checks(model, work, audit)
    network_checks(fixture, work, model, audit)
    references = index(reference["checkpoints"], "capturedAt")
    clocks = {instant(at) for at in references}
    audit.keys("coverage", "reference vs observation checkpoints", clocks, observations)
    audit.keys("coverage", "reference vs engine checkpoints", clocks, snapshots)
    results = [compare_checkpoint(cp, snapshots.get(instant(at)), observations[instant(at)], model)
               for at, cp in sorted(references.items(), key=lambda item: instant(item[0]))]
    compare = [r for r in results if r["phase"] == "comparison"]
    evaluation_clocks = {
        tuple(sorted((row["bindingId"], row.get("reportedAt")) for row in cp["signals"]))
        for cp in references.values() if cp.get("phase", "comparison") == "comparison"
    }
    if len(evaluation_clocks) < 2:
        audit.equal("coverage", "distinct Azure evaluation cycles", "at least 2", len(evaluation_clocks))
    selected = results if scope == "all" else compare
    return {"scope": scope, "structureChecks": audit.total, "structureFailures": audit.failures, "checkpoints": results,
            "passed": not audit.failures and bool(selected) and all(r["passed"] for r in selected)}


def negative_controls(fixture, work):
    """Corrupt a real matched sample and check that the independent comparison detects each change."""
    model = read(fixture / "model.json")
    refs = read(fixture / "reference.json")["checkpoints"]
    snapshots = checkpoint_snapshots(work / "replay")
    observations = [json.loads(line) for line in (fixture / "observations.ndjson").read_text().splitlines() if line]
    ref = next(r for r in reversed(refs) if instant(r["capturedAt"]) in snapshots)
    stamp = instant(ref["capturedAt"])
    source = index((r for r in observations if instant(r["checkpoint"]) == stamp), "bindingId")
    signals, entities = snapshots[stamp]
    available = [r for r in ref["signals"] if r["bindingId"] in signals]
    signal = next((r for r in available if r.get("value") is not None), available[0])
    entity = next(r for r in ref["entities"] if r["entityId"] in entities)
    sid, eid = signal["bindingId"], entity["entityId"]
    result = {}

    def detects(name, snapshot, inputs, section, key):
        rows = compare_checkpoint(ref, snapshot, inputs, model)["failures"]
        result[name] = any(row["section"] == section and row["key"] == key for row in rows)

    missing = dict(signals)
    del missing[sid]
    detects("droppedBinding", (missing, entities), source, "coverage", "Sattle evaluated signals")
    detects("changedNumericValue", ({**signals, sid: {**signals[sid], "value": float(signal.get("value") or 0) + 1234.5}}, entities),
            source, "signalValues", sid)
    wrong_state = "Unhealthy" if entity["state"] != "Unhealthy" else "Healthy"
    detects("wrongEntityState", (signals, {**entities, eid: {**entities[eid], "state": wrong_state}}),
            source, "entityStates", eid)
    bad_source = {**source, sid: {**source[sid], "status": "queryError", "errorCode": "ConnectionFailed"}}
    detects("unverifiedSamplingError", (signals, entities), bad_source, "source", sid + "/read")
    if model["relationships"]:
        altered = copy.deepcopy(model)
        rel = altered["relationships"][0]
        data = rel["properties"]
        data["parentEntityName"], data["childEntityName"] = data["childEntityName"], data["parentEntityName"]
        audit = Checks()
        config_checks(altered, work, audit)
        result["reversedDependency"] = any(row["key"] == "health/" + rel["name"] + "/direction" for row in audit.failures)
    network = read(work / "replay/network.final.snapshot.json")["extent"]
    if network["relationships"]:
        network["relationships"].pop()
        audit = Checks()
        network_checks(fixture, work, model, audit, network)
        result["missingNetworkConnection"] = any(row["key"] == "observed routes and counts" for row in audit.failures)
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True, help="Directory containing the Sattle replay subdirectory")
    parser.add_argument("--scope", choices=["all", "steady"], default="all", help="Which checkpoints must pass for exit 0")
    parser.add_argument("--report", type=Path, help="Detailed JSON evidence, including all mismatches regardless of scope")
    parser.add_argument("--negative-controls", action="store_true", help="Check deliberately corrupted copies of the capture")
    args = parser.parse_args()
    try:
        result = verify(args.fixture, args.work, args.scope)
        if args.negative_controls:
            controls = negative_controls(args.fixture, args.work)
            result["negativeControls"] = controls
            result["passed"] &= all(controls.values())
            for name, detected in controls.items():
                print(f"Negative control {name}: {'detected' if detected else 'MISSED'}")
        if args.report:
            args.report.write_text(json.dumps(result, indent=2, default=str) + "\n")
        print(f'Configuration, topology, bindings, network and checkpoint coverage: {sum(result["structureChecks"].values())} checks, '
              f'{len(result["structureFailures"])} failures')
        for failure in result["structureFailures"]:
            print(f'  {failure["section"]}: {failure["key"]}')
        for r in result["checkpoints"]:
            failed = Counter(f["section"] for f in r["failures"])
            print(f'{r["capturedAt"]} {r["phase"]} {"PASS" if r["passed"] else "FAIL"}  ' +
                  ", ".join(f'{k} {n - failed[k]}/{n}' for k, n in r["checks"].items()))
            for reason, count in r["sourceRejections"].items():
                print(f"  {count} bindings resolved the metric namespace on an ARM child after the declared scope returned {reason}")
            for failure in r["failures"]:
                print(f'  {failure["section"]} {failure["key"]}: Azure={failure["expected"]!r}, Sattle={failure["actual"]!r}')
        print(f'Scope {args.scope}: {"PASS" if result["passed"] else "FAIL"}')
        raise SystemExit(0 if result["passed"] else 1)
    except (KeyError, ValueError, OSError) as error:
        parser.exit(1, f"Invalid mirror evidence: {error}\n")
