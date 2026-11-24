"""Serve the kiln in a disposable space or compare two GKQL policies on the same capture."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import tempfile


ENGINE = Path(__file__).resolve().parents[1] / "engine"
EXAMPLE = ENGINE / "standard/health"


def run(command, definitions, *arguments, quiet=False):
    invocation = [
        "dotnet", "run", "--project", "src/Sattle.Cli", "--", command, str(EXAMPLE / "capture"),
        "--defs", str(definitions), "--space", "kiln", "--lateness", "0s", *arguments,
    ]
    result = subprocess.run(invocation, cwd=ENGINE, stdout=subprocess.PIPE if quiet else None, text=True)
    if result.returncode and quiet:
        print(result.stdout, end="")
    result.check_returncode()


def compare(definitions, work):
    policy_file = definitions / "workshop.rating.gkql.json"
    policy = json.loads(policy_file.read_text())
    crossing = next(verb["hysteresis"] for verb in policy["verbs"] if verb["verb"] == "hysteresis")
    print("Enter after   Window start (UTC)       Instrument state")
    for windows in (2, 4):
        crossing["enterAfter"] = windows
        policy_file.write_text(json.dumps(policy))
        output = work / str(windows)
        run("replay", definitions, "--out", str(output), quiet=True)
        for line in (output / "rating.patches.ndjson").read_text().splitlines():
            for edit in json.loads(line)["edits"]:
                entity = edit.get("after")
                if entity and entity["type"]["value"] == "Instrument":
                    fields = {p["property"]["value"]: p["value"]["value"] for p in entity["properties"]}
                    print(f"{windows:>10}   {fields['changedIn']}     {fields['state']}")


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    commands = cli.add_subparsers(dest="command", required=True)
    serve = commands.add_parser("serve", help="start the local Canvas example")
    serve.add_argument("--port", type=int, default=5086)
    commands.add_parser("compare", help="replay the two- and four-window policies")
    args = cli.parse_args()
    with tempfile.TemporaryDirectory(prefix="sattle-kiln-") as directory:
        work = Path(directory)
        definitions = work / "definitions"
        definitions.mkdir()
        for file in EXAMPLE.glob("*.gkql.json"):
            shutil.copy(file, definitions)
        if args.command == "compare":
            compare(definitions, work)
        else:
            run("serve", definitions, "--write-definitions", "--port", str(args.port))


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        raise SystemExit(130)
