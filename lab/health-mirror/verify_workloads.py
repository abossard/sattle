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


def matched_classes(app, policy):
    name = app["name"].casefold()
    group = arm_id(app["id"]).split("/providers/", 1)[0]
    matches = set()
    if group == arm_id(policy["scopeId"]):
        for rule in policy.get("nameRules", []):
            text = rule["pattern"].casefold()
            if {"prefix": name.startswith(text), "suffix": name.endswith(text), "contains": text in name}[rule["match"]]:
                matches.add(rule["class"])
    return matches


def business_evidence(graph):
    flows = {row["key"]:row for row in graph.typed("UserFlow")}
    systems = {row["key"]:row for row in graph.typed("SystemComponent")}
    steps = {(a["key"],b["key"]) for k,(_,a),(_,b) in graph.edges if k == "DeclaresStep"}
    apps = {(a["key"],arm_id(b["armId"])) for k,(_,a),(_,b) in graph.edges if k == "RunsOnApp"}
    pairs = {(flows[f]["name"],a) for f,s in steps for c,a in apps if s==c}
    return flows, systems, steps, apps, pairs


def verify_project(output, policy, rows):
    source = inventory(rows)
    apps = {key: row for key, row in source.items() if row["type"].casefold() == APP}
    scope = arm_id(policy["scopeId"])
    local_flows, local_systems, expected_steps, expected_bindings = {}, {}, set(), set()
    for flow in policy.get("flows", []):
        fid = scope + "/flows/" + flow["id"]
        local_flows[fid] = flow["name"]
        for system in flow.get("systems", []):
            sid = scope + "/systems/" + system["id"]
            local_systems[sid] = system["name"]
            expected_steps.add((fid, sid))
            wanted = {r["class"] for r in system.get("requires", [])}
            for aid, resource in apps.items():
                classes = matched_classes(resource, policy)
                if len(classes) == 1 and classes & wanted:
                    expected_bindings.add((sid, aid))
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
        assigned = any(app_id == key for _, app_id in expected_bindings)
        expect(key + " flow assignment", app["assignment"], "Assigned" if assigned else "Unassigned")

    platform_edges = {"HostsApp", "ContainsApp", "AppInRegion", "ContainsEnvironment", "EnvironmentInRegion"}
    actual_platform = [(kind, arm_id(left["key"]), arm_id(right.get("armId") or right["key"]))
                       for kind, (_, left), (_, right) in deployed.edges if kind in platform_edges]
    expected_platform = []
    for key, item in source.items():
        app = item["type"].casefold() == APP
        group = arm_id(f"/subscriptions/{item['subscriptionId']}/resourceGroups/{item['resourceGroup']}")
        region = arm_id(f"{item['subscriptionId']}/{item['location']}")
        expected_platform.extend([("ContainsApp" if app else "ContainsEnvironment", group, key),
                                  ("AppInRegion" if app else "EnvironmentInRegion", region, key)])
        environment = item["properties"].get("environmentId") or item["properties"].get("managedEnvironmentId")
        if app and environment and arm_id(environment) in source:
            expected_platform.append(("HostsApp", arm_id(environment), key))
    expect("ARM platform edges", sorted(actual_platform), sorted(expected_platform))
    local, systems, steps, resolved, pairs = business_evidence(flows)
    expect("declared flow IDs and names", {k:v["name"] for k,v in local.items()}, local_flows)
    expect("declared system IDs and names", {k:v["name"] for k,v in systems.items()}, local_systems)
    expect("unique flow nodes", len(flows.typed("UserFlow")), len(local_flows))
    expect("unique system nodes", len(flows.typed("SystemComponent")), len(local_systems))
    for row in [*local.values(), *systems.values()]:
        expect("curated provenance", row["provenance"], "LocalOntology")
    expect("explicit flow to component", steps, expected_steps)
    expect("unambiguous class to deployed app", resolved, expected_bindings)
    return {"apps": len(apps), "curatedFlows":len(local), "curatedSystems":len(systems), "platformLinks":len(actual_platform),
            "stepAppLinks":len(resolved), "flowAppPairs":len(pairs), "measuredSteps":sum(int(r["measuredChecks"])>0 for r in systems.values())}



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


def draft_check(work, rows, policy):
    """Change two existing local rule declarations. Keep the Azure observations and memberships byte-identical."""
    from container_inputs import ontology_facts
    from record_adapter import normalize
    all_events = [row for path in sorted((work / "capture").glob("*.ndjson")) for row in ndjson(path)]
    inputs = [r for r in all_events if r["source"] == "records:WorkloadOntologyRow" and r["payload"]["values"].get("kind") == "nameRule"]
    if len(policy.get("nameRules", [])) != 2 or len(inputs) != 2:
        raise ValueError("The CORE rule draft expects both existing app-name declarations")
    # Hold back one genuine inventory record. The unchanged official model still refers to this app.
    # The full replay verified above includes it as soon as its ARM record joins the input.
    observed_apps = sorted(k for k,v in inventory(rows).items() if v["type"].casefold() == APP)
    held_out_id = observed_apps[-1]
    subset = [e for e in rows if arm_id(e["payload"].get("id", "")) != held_out_id]
    before = [e for e in all_events if not (
        e["source"] == "records:ArmInventoryRow" and arm_id(e["payload"].get("values", {}).get("armId", "")) == held_out_id)]
    with tempfile.TemporaryDirectory() as tmp:
        output = replay(work / "definitions", before, Path(tmp))
        verify_project(output, policy, subset)
    print("Inventory replay PASS", "restoring one captured ARM record adds its app and projected relationships")
    for shared_class in (False, True):
        edited = copy.deepcopy(policy)
        left, right = edited["nameRules"]
        left["pattern"], left["match"] = right["pattern"], right["match"]
        if shared_class:
            left["class"] = right["class"]
        local_rules = {fact["key"]: fact for fact in ontology_facts(edited) if fact["kind"] == "nameRule"}
        stream = []
        for row in all_events:
            if row in inputs:
                fact = local_rules[row["payload"]["values"]["key"]]
                stream.extend(normalize({"source": "workload-ontology", "capturedAt": row["capturedAt"], "payload": fact}))
            else:
                stream.append(row)
        with tempfile.TemporaryDirectory() as tmp:
            output = replay(work / "definitions", stream, Path(tmp))
            verify_project(output, edited, rows)
            expect("official health independent of name rules", read(output/"health.final.snapshot.json")["extent"], read(work/"replay/health.final.snapshot.json")["extent"])
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
        path = args.fixture / "flow-ontology.json"
        sidecar = read(path) if path.exists() else {"nameRules": []}
        project = verify_project(args.work / "replay", sidecar, rows)
        rules = verify_name_rules(args.work / "replay", inventory(rows), sidecar)
        print("Source comparison PASS", json.dumps({**project, **rules}, sort_keys=True))
        if args.draft:
            draft_check(args.work, rows, sidecar)
    except (KeyError, ValueError, OSError) as exc:
        parser.exit(1, f"Workload verification failed: {exc}\n")


if __name__ == "__main__":
    main()
