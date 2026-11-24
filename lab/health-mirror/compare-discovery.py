#!/usr/bin/env python3
"""Replay deployment evidence and compare the resulting business flows to a held-out Azure model."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

from verify import read
from verify_workloads import Graph, business_evidence, expect, flow_evidence, inventory, ndjson, verify_project
from verify_telemetry import verify as verify_telemetry

HERE = Path(__file__).resolve().parent
DEPLOYMENT_INPUTS = ("container-apps.ndjson", "dependencies.ndjson", "network.ndjson",
                     "request-observations.ndjson", "request-dependencies.ndjson", "diagnostic-events")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def isolated_run(args, base, policy, snapshot):
    fixture, run = base / "deployment", base / "space"
    fixture.mkdir(parents=True)
    for name in DEPLOYMENT_INPUTS:
        source = snapshot / name
        if source.is_dir():
            shutil.copytree(source, fixture / name)
        elif source.is_file():
            shutil.copyfile(source, fixture / name)
    (fixture / "flow-ontology.json").write_text(json.dumps(policy))
    subprocess.run([sys.executable, "-B", str(HERE / "build-space.py"), "--deployment-only", "--fixture", str(fixture),
                    "--out", str(run)], check=True)
    capture = [r for p in (run / "capture").glob("*.ndjson") for r in ndjson(p)]
    channels = Counter(r["source"] for r in capture)
    expect("no official CloudHealth computation input", channels["records:CloudHealthRow"], 0)
    for event in capture:
        if event["source"] == "records:ArmInventoryRow":
            values = event["payload"].get("values", {})
            if "/providers/microsoft.cloudhealth/" in str(values.get("armId", "")).casefold():
                raise ValueError("Official model leaked through inventory")
    result = subprocess.run(["dotnet", str(args.cli), "replay", str(run / "capture"), "--defs", str(args.work/"definitions"),
                             "--space", "healthMirror", "--lateness", "0s", "--out", str(run/"replay")], capture_output=True, text=True)
    if result.returncode or "rejected records: 0" not in result.stdout:
        raise ValueError(result.stderr or result.stdout)
    counts = verify_project(run/"replay", policy, ndjson(fixture/"container-apps.ndjson"))
    expect("standalone official health", Graph(run/"replay","health").typed("ModelEntity"), [])
    expect("standalone official signals", Graph(run/"replay","signals").typed("EvaluatedSignal"), [])
    try:
        measured = {"passed": True, **verify_telemetry(fixture, run/"replay")}
    except ValueError as exc:
        measured = {"passed": False, "failure": str(exc).split(": expected ", 1)[0]}
    return run/"replay", {**counts, "channels":dict(channels), "telemetry": measured}


def score(output, model, reference, resources):
    flows, components, _, expected_pairs = flow_evidence(model, resources)
    names = {e["name"]:e["properties"].get("displayName", e["name"]) for e in model["entities"]}
    held_names = {names[f] for f in flows}
    local, steps, _, _, observed_pairs = business_evidence(Graph(output, "workloadFlows"))
    targets = {(names[flow], app) for flow, app in expected_pairs}
    found_names = {f["name"] for f in local.values()}
    states = {r["entityId"]:r["state"] for r in max(reference["checkpoints"], key=lambda c:c["capturedAt"])["entities"]}
    per_flow = []
    for name in sorted(held_names | found_names):
        wanted = {a for f,a in targets if f==name}
        observed = {a for f,a in observed_pairs if f==name}
        row = next((f for f in local.values() if f["name"]==name),{})
        per_flow.append({"name":name, "matched":sorted(wanted & observed), "missing":sorted(wanted-observed),
                         "extra":sorted(observed-wanted), "curatedState":row.get("state"), "inferredState":row.get("inferredState"),
                         "officialState":next((states[f] for f in flows if names[f]==name),None)})
    declared_components = {names[c] for c in components}
    observed_components = {s["name"] for s in steps.values()}
    return {"flowNames":{"matched":sorted(held_names & found_names),"missing":sorted(held_names-found_names),"extra":sorted(found_names-held_names)},
            "matchedFlowAppPairs":len(targets & observed_pairs), "expectedFlowAppPairs":len(targets), "extraFlowAppPairs":len(observed_pairs-targets),
            "perFlow":per_flow, "componentNames":{"matched":sorted(declared_components & observed_components),
                 "officialOnly":sorted(declared_components-observed_components),"curatedOnly":sorted(observed_components-declared_components)},
            "unmeasuredSteps":sorted(s["name"] for s in steps.values() if not int(s.get("measuredChecks",0))),
            "officialBindingsHeldOut":len(model["bindings"]), "exactBusinessParity":held_names==found_names and targets==observed_pairs and
                  declared_components==observed_components and all(f["curatedState"]==f["officialState"] for f in per_flow)}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture",type=Path,required=True)
    parser.add_argument("--work",type=Path,required=True)
    parser.add_argument("--cli",type=Path,required=True)
    parser.add_argument("--require-parity",action="store_true",help="Exit nonzero if the held-out business comparison differs")
    args=parser.parse_args()
    policy=read(args.fixture/"flow-ontology.json")
    with tempfile.TemporaryDirectory(dir=args.work) as temporary:
        base=Path(temporary)
        snapshot = base / "sources"
        snapshot.mkdir()
        for name in DEPLOYMENT_INPUTS:
            source = args.fixture / name
            if source.is_dir():
                shutil.copytree(source, snapshot/name)
            elif source.is_file():
                shutil.copyfile(source, snapshot/name)
        graph, standalone = isolated_run(args, base/"curated", policy, snapshot)
        empty, control = isolated_run(args, base/"control", {**policy,"flows":[]}, snapshot)
        expect("negative control flows", control["curatedFlows"], 0)
        expect("negative control steps", control["curatedSystems"], 0)
        expect("negative control flow links", control["flowAppPairs"], 0)
        expect("same app inventory despite removing flows", control["apps"], standalone["apps"])
        expect("no duplicate or reference-derived business objects", read(graph/"workloadFlows.final.snapshot.json")["extent"],
               read(args.work/"replay/workloadFlows.final.snapshot.json")["extent"])
        # The replay processes have exited before any official health model or evaluation is opened.
        model=read(args.fixture/"model.json")
        reference=read(args.fixture/"reference.json")
        result=score(graph,model,reference,inventory(ndjson(args.fixture/"container-apps.ndjson")))
        full={"modelEntities":len(Graph(args.work/"replay","health").typed("ModelEntity")),
              "evaluatedBindings":len(Graph(args.work/"replay","signals").typed("EvaluatedSignal")),
              "curatedFlows":len(Graph(args.work/"replay","workloadFlows").typed("UserFlow"))}
        report={"model":model["model"]["name"],"definitions":{p.name:digest(p) for p in sorted((args.work/"definitions").glob("*.gkql.json"))},
                "combined":full,"deploymentWithOntology":standalone,"deploymentWithoutFlowDeclarations":control,"heldOutComparison":result}
        target=args.work/"discovery-comparison.json"
        with tempfile.NamedTemporaryFile(mode="w",dir=args.work,delete=False) as f:
            json.dump(report,f,indent=2);f.write("\n")
        Path(f.name).replace(target)
        print("Isolation and negative controls PASS", json.dumps({"combined":full,"withOntology":standalone,"withoutFlows":control}))
        print("Held-out comparison", "PASS" if result["exactBusinessParity"] else "DIFFERENCES",
              f'({result["matchedFlowAppPairs"]}/{result["expectedFlowAppPairs"]} flow/app pairs; {result["extraFlowAppPairs"]} extra)')
        for r in result["perFlow"]:
            print(r["name"],"matched",len(r["matched"]),"missing",[k.rsplit("/",1)[-1] for k in r["missing"]],
                  "extra",[k.rsplit("/",1)[-1] for k in r["extra"]],"state",r["curatedState"],"/",r["officialState"])
        if not standalone["telemetry"]["passed"] or not control["telemetry"]["passed"]:
            return 1
        if args.require_parity and not result["exactBusinessParity"]:
            return 1
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
