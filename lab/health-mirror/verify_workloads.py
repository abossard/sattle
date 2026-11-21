#!/usr/bin/env python3
"""Check projected Container Apps and user flows against independent ARM and CloudHealth evidence."""
import argparse
import copy
from collections import defaultdict
import json
from pathlib import Path
import subprocess
import tempfile

from verify import properties, read, unwrap

HERE = Path(__file__).resolve().parent
CLI = HERE.parents[1] / "engine/src/Sattle.Cli/bin/Release/net11.0/sattle.dll"
APP = "microsoft.app/containerapps"
ENVIRONMENT = "microsoft.app/managedenvironments"


def ndjson(path):
    return [json.loads(line) for line in path.read_text().splitlines() if line.strip()]


def arm_id(value):
    return value.rstrip("/").casefold()


def inventory(rows):
    """Apply native ARG upserts and delete receipts in source-clock order."""
    result = {}
    for envelope in sorted(rows, key=lambda row: row["capturedAt"]):
        if envelope["source"] != "arg":
            raise ValueError("Found another source in the Container Apps inventory")
        payload = envelope["payload"]
        if payload.get("properties", {}).get("changeType") == "Delete":
            result.pop(arm_id(payload["properties"]["targetResourceId"]), None)
        elif payload.get("type", "").casefold() in (APP, ENVIRONMENT):
            result[arm_id(payload["id"])] = payload
    return result


def flow_evidence(model, resources):
    """Walk authoritative parent-to-child edges, then follow each entity's bound ARM resource."""
    entities = {r["name"]: r["properties"] for r in model.get("entities", [])}
    children = defaultdict(set)
    for row in model.get("relationships", []):
        declaration = row["properties"]
        children[declaration["parentEntityName"]].add(declaration["childEntityName"])
    applications = {k for k, row in resources.items() if row["type"].casefold() == APP}
    roles = {}
    flows = set()
    systems = set()
    for key, state in entities.items():
        icon = state.get("icon", {}).get("iconName")
        if icon == "UserFlow":
            flows.add(key)
        if icon == "SystemComponent":
            systems.add(key)
        target = state.get("signalGroups", {}).get("azureResource", {}).get("azureResourceId")
        if target and arm_id(target) in applications:
            roles[key] = arm_id(target)
    links = set()
    for flow in flows:
        pending, visited = [flow], set()
        while pending:
            item = pending.pop()
            if item not in visited:
                visited.add(item)
                if item in roles:
                    links.add((flow, roles[item]))
                pending.extend(children[item])
    return flows, systems, roles, links


class Graph:
    def __init__(self, output, graph):
        data = read(output / (graph + ".final.snapshot.json"))["extent"]
        self.nodes = {
            unwrap(row["id"]): (unwrap(row["type"]).split(".")[-1], properties(row))
            for row in data["entities"]
        }
        self.edges = [
            (unwrap(row["type"]).split(".")[-1], self.nodes[unwrap(row["source"])], self.nodes[unwrap(row["target"])])
            for row in data["relationships"]
        ]

    def typed(self, kind):
        return [p for typ, p in self.nodes.values() if typ == kind]


def expect(test, actual, expected):
    if actual != expected:
        raise ValueError(f"{test}: expected {expected!r}, got {actual!r}")


def replay(definitions, events, directory):
    recording = directory / "capture"
    recording.mkdir(exist_ok=True)
    if any(p.name != "source.ndjson" for p in recording.iterdir()):
        raise ValueError("Use a separate output directory for local draft records")
    (recording / "source.ndjson").write_text("".join(json.dumps(row) + "\n" for row in events))
    output = directory / "replay"
    execution = subprocess.run(["dotnet", str(CLI), "replay", str(recording), "--defs", str(definitions),
                                "--space", "healthMirror", "--lateness", "0s", "--out", str(output)],
                               capture_output=True, text=True)
    if execution.returncode or "rejected records: 0" not in execution.stdout:
        raise ValueError(execution.stderr or execution.stdout)
    return output


def baseline(properties):
    provisioning = properties.get("provisioningState", "").casefold()
    running = properties.get("runningStatus", "").casefold()
    if provisioning in ("failed", "canceled") or running in ("failed", "stopped"):
        return "Unhealthy"
    if provisioning in ("creating", "updating", "deleting") or running in ("progressing", "degraded"):
        return "Degraded"
    if provisioning == "succeeded" and running == "running":
        return "Healthy"
    return "Unknown"


def verify_project(output, model, rows, reference=None):
    source = inventory(rows)
    apps = {key: row for key, row in source.items() if row["type"].casefold() == APP}
    flow_ids, system_ids, bound, expected_pairs = flow_evidence(model, source)
    deployed = Graph(output, "containerApps")
    flows = Graph(output, "workloadFlows")
    observed = {arm_id(p["armId"]): p for p in deployed.typed("ContainerApp")}
    expect("app identities", sorted(observed), sorted(apps))
    expect("unique app nodes", len(observed), len(deployed.typed("ContainerApp")))
    for key, row in apps.items():
        app = observed[key]
        expect(key + " name", app["name"], row["name"])
        expect(key + " region", app["location"].casefold(), row["location"].casefold())
        expect(key + " resource group", app["resourceGroup"].casefold(), row["resourceGroup"].casefold())
        expect(key + " ARM assessment", app["state"], baseline(row["properties"]))
        assigned = any(app_id == key for _, app_id in expected_pairs)
        expect(key + " flow assignment", app["assignment"], "Assigned" if assigned else "Unassigned")

    modeled = {p["modelKey"]: p for p in flows.typed("UserFlow")}
    expect("user flows", sorted(modeled), sorted(flow_ids))
    if reference:
        latest = max(reference["checkpoints"], key=lambda cp: cp["capturedAt"])
        states = {r["entityId"]: r["state"] for r in latest["entities"]}
        for key in sorted(flow_ids):
            expect(key + " authoritative health", modeled[key]["state"], states[key])
    steps = defaultdict(set)
    runs_on = defaultdict(set)
    for kind, (left_type, left), (right_type, right) in flows.edges:
        if kind == "DeclaresStep":
            expect("DeclaresStep endpoint types", (left_type, right_type), ("UserFlow", "SystemComponent"))
            steps[left["modelKey"]].add(right["modelKey"])
        if kind == "RunsOnApp":
            expect("RunsOnApp endpoint types", (left_type, right_type), ("SystemComponent", "ContainerApp"))
            runs_on[left["modelKey"]].add(arm_id(right["armId"]))
    pairs = {(flow, app) for flow, components in steps.items() for component in components for app in runs_on[component]}
    expect("flow-to-app reachability", sorted(pairs), sorted(expected_pairs))
    return {"apps": len(apps), "officialFlowStates": len(flow_ids), "flowAppPairs": len(pairs),
            "modelBindingsToApps": len(bound)}


def verify_name_rules(output, resources, sidecar):
    apps = Graph(output, "containerApps")
    gaps = Graph(output, "ontologyGaps")
    found = {(arm_id(a["armId"]), r["name"]) for kind, (_, a), (_, r) in apps.edges if kind == "MatchedRule"}
    expected = {}
    for key, resource in resources.items():
        if resource["type"].casefold() != APP:
            continue
        name = resource["name"].casefold()
        matched = []
        for rule in sidecar.get("nameRules", []):
            pattern = rule["pattern"].casefold()
            scoped = key.startswith(arm_id(sidecar["scopeId"]) + "/providers/")
            match = {"prefix": name.startswith(pattern), "suffix": name.endswith(pattern), "contains": pattern in name}
            if scoped and match[rule["match"]]:
                matched.append(rule)
        expected[key] = matched
    wanted_edges = {(key, rule["id"]) for key, matches in expected.items() for rule in matches}
    expect("app rule provenance", sorted(found), sorted(wanted_edges))
    expected_gaps = set()
    for a in apps.typed("ContainerApp"):
        key = arm_id(a["armId"])
        classes = {rule["class"] for rule in expected[key]}
        count = len(classes)
        status = "Unmapped" if not count else "Mapped" if count == 1 else "Ambiguous"
        expect(key + " ontology", (a["ontologyStatus"], int(a["classCount"])), (status, count))
        if count != 1:
            expected_gaps.add(key)
    expect("ontology gap membership", {arm_id(a["armId"]) for a in gaps.typed("ContainerApp")}, expected_gaps)
    return {"matchedRules": len(found), "gaps": len(expected_gaps)}


def draft_check(work, model, rows, reference, policy):
    """Change two existing local rule declarations. Keep the Azure observations and memberships byte-identical."""
    from container_inputs import ontology_facts
    from record_adapter import normalize
    all_events = [row for path in sorted((work / "capture").glob("*.ndjson")) for row in ndjson(path)]
    inputs = [r for r in all_events if r["source"] == "records:CloudHealthRow" and r["payload"]["values"].get("kind") == "nameRule"]
    if len(policy.get("nameRules", [])) != 2 or len(inputs) != 2:
        raise ValueError("The CORE rule draft expects both existing app-name declarations")
    for shared_class in (False, True):
        edited = copy.deepcopy(policy)
        left, right = edited["nameRules"]
        left["pattern"], left["match"] = right["pattern"], right["match"]
        if shared_class:
            left["class"] = right["class"]
        local_rules = {fact["key"]: fact for fact in ontology_facts(None, edited) if fact["kind"] == "nameRule"}
        stream = []
        for row in all_events:
            if row in inputs:
                fact = local_rules[row["payload"]["values"]["key"]]
                stream.extend(normalize({"source": "azure-cloud-health", "capturedAt": row["capturedAt"], "payload": fact}))
            else:
                stream.append(row)
        with tempfile.TemporaryDirectory() as tmp:
            output = replay(work / "definitions", stream, Path(tmp))
            verify_project(output, model, rows, reference)
            result = verify_name_rules(output, inventory(rows), edited)
            expect("local draft gap count", result["gaps"], 1 if shared_class else 2)
        print("Local draft PASS", "two rules agree on one class" if shared_class else "one ambiguous app and one unmapped app")

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--draft", action="store_true", help="Try an isolated classification edit against the two real CORE applications")
    args = parser.parse_args()
    try:
        rows = ndjson(args.fixture / "container-apps.ndjson")
        path = args.fixture / "model.json"
        model = read(path) if path.exists() else {}
        path = args.fixture / "reference.json"
        reference = read(path) if path.exists() else None
        path = args.fixture / "flow-ontology.json"
        sidecar = read(path) if path.exists() else {"nameRules": []}
        project = verify_project(args.work / "replay", model, rows, reference)
        rules = verify_name_rules(args.work / "replay", inventory(rows), sidecar)
        print("Source comparison PASS", json.dumps({**project, **rules}, sort_keys=True))
        if args.draft:
            draft_check(args.work, model, rows, reference, sidecar)
    except (KeyError, ValueError, OSError) as exc:
        parser.exit(1, f"Workload verification failed: {exc}\n")


if __name__ == "__main__":
    main()
