#!/usr/bin/env python3
"""Run the submission review against a disposable copy of the standard GKQL modules."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import tempfile


ROOT = Path(__file__).resolve().parents[1]
ENGINE = ROOT / "engine"
FLOW = ENGINE / "standard/flows"
COVERAGE = ENGINE / "standard/coverage"
EXAMPLE = ENGINE / "examples/coverage"


def prepare(destination):
    destination.mkdir(parents=True, exist_ok=True)
    if any(destination.iterdir()):
        raise SystemExit(f"Choose an empty directory for the review: {destination}")
    definitions = destination / "definitions"
    capture = destination / "capture"
    definitions.mkdir()
    capture.mkdir()
    for path in [*(FLOW / "definitions").glob("sattle.flow.*.gkql.json"), *COVERAGE.glob("*.gkql.json")]:
        shutil.copy(path, definitions)
    shutil.copy(FLOW / "capture/submit.ndjson", capture)
    shutil.copy(EXAMPLE / "capture/evidence.ndjson", capture)
    rules_file = definitions / "sattle.coverage.rules.gkql.json"
    rules = json.loads(rules_file.read_text())
    table = next(verb for verb in rules["verbs"] if verb["id"] == "rules")
    table["values"]["rows"] = json.loads((EXAMPLE / "rules.json").read_text())
    rules_file.write_text(json.dumps(rules, indent=2) + "\n")
    # Both ingestions accept keyed records. Keep the standard graph names for their readers.
    module_file = definitions / "sattle.coverage.module.gkql.json"
    module = json.loads(module_file.read_text())
    flow_example = json.loads((FLOW / "definitions/flowExample.module.gkql.json").read_text())
    module["imports"].extend(flow_example["imports"])
    space = module["space"]
    space["name"] = "flowReview"
    flow = flow_example["space"]
    space["sources"].extend(flow["sources"])
    space["graphs"].extend([
        {"name": "traces", "projection": "flow.ingest", "inputs": next(g["inputs"] for g in flow["graphs"] if g.get("master"))},
        {"name": "flows", "projection": "flow.discover", "inputs": {"master": "traces"}},
    ])
    module_file.write_text(json.dumps(module, indent=2) + "\n")


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    commands = cli.add_subparsers(dest="command", required=True)
    stage = commands.add_parser("prepare", help="copy the definitions and evidence to an empty directory")
    stage.add_argument("directory", type=Path)
    serve = commands.add_parser("serve", help="serve the review locally until Ctrl-C")
    serve.add_argument("--port", type=int, default=5085)
    commands.add_parser("empty", help="replay the same ingress receipt with no project rules")
    args = cli.parse_args()
    if args.command == "prepare":
        prepare(args.directory)
        return
    with tempfile.TemporaryDirectory(prefix="sattle-flow-review-") as work:
        path = Path(work)
        if args.command == "empty":
            replay = subprocess.run([
                "dotnet", "run", "--project", str(ENGINE / "src/Sattle.Cli"), "--",
                "replay", str(EXAMPLE / "capture"), "--defs", str(COVERAGE),
                "--space", "coverageReview", "--out", work,
            ], cwd=ENGINE, stdout=subprocess.PIPE, text=True)
            if replay.returncode:
                print(replay.stdout, end="")
                return replay.returncode
            snapshot = json.loads((path / "coverage.final.snapshot.json").read_text())
            for node in snapshot["extent"]["entities"]:
                if node["type"]["value"].split(".")[-1] == "Scope":
                    fields = {p["property"]["value"].split(".")[-1]: p["value"]["value"] for p in node["properties"]}
                    print(f"{fields['source']} / {fields['role']} · {fields['eligible']} eligible · "
                          f"{fields['unmapped']} unmapped · {fields['supportedBindings']} supported · {fields['availability']}")
            return
        prepare(path)
        command = [
            "dotnet", "run", "--project", str(ENGINE / "src/Sattle.Cli"), "--",
            "serve", str(path / "capture"), "--defs", str(path / "definitions"),
            "--space", "flowReview", "--write-definitions", "--port", str(args.port),
        ]
        try:
            return subprocess.run(command, cwd=ENGINE).returncode
        except KeyboardInterrupt:
            return 130


if __name__ == "__main__":
    raise SystemExit(main())
