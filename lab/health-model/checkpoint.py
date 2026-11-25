#!/usr/bin/env python3
"""Compare the rebuilt health model with Azure at one reference checkpoint.

  prepare RAW REFERENCE OUT APP_ROLE LOCAL_SALT
                              collector output, a reference file (lab/health-mirror/export-model.py --scrub LOCAL_SALT),
                              the app role name and the same salt -> OUT/capture/rows.ndjson, OUT/checkpoint.json,
                              OUT/cuts.txt
  cases                       checkpoints/<case>/ for every hand-made case below: records whose signal values follow
                              from the Azure query alone, where the exported checkpoints hold none
  compare OUT                 OUT/replays/<cut>/master.final.snapshot.json -> one line per mismatch, and one if the
                              last replay divided by zero; exit 1 on any
  mutate OUT                 OUT/mutants/<name>: the definitions with one binding dropped, one edge reversed, one
                              threshold crossed, each with the cuts that show it
  verify OUT                  compare OUT and each mutant: OUT must match, every mutant must not

Run replay-cuts.sh on OUT and on each OUT/mutants/<name> between mutate and verify.

A record enters the engine when Azure could first see it: a log row at its ingestion time (the collector's position),
a metric minute when it ends. The engine's clock is the capture time, so `replay --until <cut> --advance-every 1s
--lateness 0s --no-flush` stops with the clock at the cut. Azure evaluates each signal at its own time (reportedAt),
so each signal is read from the replay cut at that time, and the entities from the one cut at the capture time.
That holds only for a settled checkpoint: no signal changes state between its evaluation and the capture.
Names are scrubbed by the alias of export-model.py under the same salt, so rows and reference keep their joins.
"""
from datetime import datetime, timedelta, timezone
import importlib.util
import json
from pathlib import Path
import re
import sys

TOLERANCE = 1e-6
ENTRY_DELAY = {"MetricRow": timedelta(minutes=1)}
SOURCE_IDS = {"resourceId", "_ItemId", "EventDataId"}  # the source's own record ids: the program reads none of them
_spec = importlib.util.spec_from_file_location("export_model", Path(__file__).parents[1] / "health-mirror" / "export-model.py")
_export_model = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_export_model)
alias = _export_model.alias
sys.path.insert(0, str(Path(__file__).parents[1] / "collector"))
from sources import instant, iso  # noqa: E402  the lab's one instant and time helpers
SELECTS = re.compile(r'\bName\s+(==|in|startswith)\s+(\([^)]*\)|"[^"]*")')


def vocabulary(model):
    """The names the model's queries select by == or in, and the prefixes they select by startswith (lower case)."""
    exact, prefixes = set(), set()
    for entity in model["entities"]:
        for signal in ((entity["properties"].get("signalGroups") or {}).get("azureLogAnalytics") or {}).get("signals", []):
            for op, arg in SELECTS.findall(signal["queryText"]):
                words = re.findall(r'"([^"]*)"', arg)
                (prefixes if op == "startswith" else exact).update(w.lower() if op == "startswith" else w for w in words)
    return exact, tuple(prefixes)


def scrubber(app_role, salt, model):
    """The row as the scrubbed reference names it, with no ids of the source. Kept: the names the model selects. By
    alias: test names, resource groups, other roles and every other name (a dependency's name can hold resource ids)."""
    exact, prefixes = vocabulary(model)
    def scrub(rows, values):
        out = {k: v for k, v in values.items() if k not in SOURCE_IDS}
        if out.get("AppRoleName"):
            out["AppRoleName"] = "demo-workload" if out["AppRoleName"] == app_role else alias(salt, "role", out["AppRoleName"])
        if rows == "AvailabilityRow":
            out["Name"] = alias(salt, "test", out["Name"])
        elif out.get("Name") and out["Name"] not in exact and not out["Name"].lower().startswith(prefixes):
            out["Name"] = alias(salt, "name", out["Name"])
        if out.get("ResourceGroup"):
            out["ResourceGroup"] = alias(salt, "group", out["ResourceGroup"])
        return out
    return scrub


def entry(envelope):
    """The capture time at which Azure could first see the record."""
    rows = envelope["source"].split(":", 1)[1]
    values = envelope["payload"]["values"]
    if rows in ENTRY_DELAY:
        return instant(values["at"]) + ENTRY_DELAY[rows]
    return instant(envelope["meta"]["position"])


def capture(raw_lines, cuts, scrub, salt):
    """Records at their entry time and a heartbeat at every cut, in capture order."""
    records = []
    for line in raw_lines:
        e = json.loads(line)
        if "values" not in e["payload"]:
            continue
        key = e["source"] + "/" + alias(salt, "key", e["key"])
        values = scrub(e["source"].split(":", 1)[1], e["payload"]["values"])
        records.append((entry(e), {"v": 1, "source": e["source"], "key": key, "payload": {"key": key, "values": values}}))
    sources = sorted({r["source"] for _, r in records}) or ["records:MetricRow"]
    for cut in cuts:
        records.append((cut, {"v": 1, "source": sources[0], "key": "heartbeat/" + iso(cut), "payload": {}}))
    records.sort(key=lambda r: r[0])
    return [{**r, "capturedAt": iso(at), "seq": i} for i, (at, r) in enumerate(records)]


def checkpoint(reference, index=-1):
    """Expected states: signals at their evaluation time (no Resource Health), entities at the capture time."""
    c = reference["checkpoints"][index]
    signals = {s["bindingId"]: {"cut": iso(instant(s["reportedAt"])), "state": s["state"], "value": s.get("value")}
               for s in c["signals"] if "/azureResource/resource-health" not in s["bindingId"]}
    return {"capturedAt": iso(instant(c["capturedAt"])), "signals": signals,
            "entities": {e["entityId"]: e["state"] for e in c["entities"]}}


def edges(model):
    return sorted([r["properties"]["parentEntityName"], r["properties"]["childEntityName"]] for r in model["relationships"])


def nodes(snapshot, label):
    """name -> {property: value} of the snapshot's nodes of one type."""
    out = {}
    for e in snapshot["extent"]["entities"]:
        if e["type"]["value"].split(".")[-1] != label:
            continue
        props = {p["property"]["value"].split(".")[-1]: p["value"].get("value") for p in e["properties"]}
        out[props.get("name")] = {**props, "id": e["id"]["value"]}
    return out


def contains(snapshot):
    names = {p["id"]: name for name, p in nodes(snapshot, "Entity").items()}
    return sorted([names.get(r["source"]["value"]), names.get(r["target"]["value"])] for r in snapshot["extent"]["relationships"]
                  if r["type"]["value"].split(".")[-1] == "Contains")


def mismatches(expected, snapshots):
    """One line per signal or entity whose state or value differs from the reference."""
    out = []
    for key, want in sorted(expected["signals"].items()):
        got = nodes(snapshots[want["cut"]], "Signal").get(key, {})
        value = float(got["value"]) if got.get("value") is not None else None
        if got.get("state") != want["state"]:
            out.append(f"signal {key} at {want['cut']}: state {got.get('state')} != {want['state']}")
        elif (value is None) != (want["value"] is None) or (value is not None and abs(value - want["value"]) > TOLERANCE * max(1, abs(value))):
            out.append(f"signal {key} at {want['cut']}: value {value} != {want['value']}")
    at = snapshots[expected["capturedAt"]]
    # Azure rolls up each signal as last evaluated; a checkpoint is settled when no signal changed state since then
    for key, want in sorted(expected["signals"].items()):
        then, now = (nodes(snapshots[c], "Signal").get(key, {}).get("state") for c in (want["cut"], expected["capturedAt"]))
        if then != now:
            out.append(f"not settled: signal {key} is {then} at {want['cut']} and {now} at {expected['capturedAt']}")
    if contains(at) != expected["edges"]:
        out.append(f"edges at {expected['capturedAt']}: {len(contains(at))} Contains, not the model's {len(expected['edges'])}")
    entities = nodes(at, "Entity")
    for key, want in sorted(expected["entities"].items()):
        if entities.get(key, {}).get("state") != want:
            out.append(f"entity {key} at {expected['capturedAt']}: state {entities.get(key, {}).get('state')} != {want}")
    return out


def prepare(raw, reference, out, app_role, salt_file):
    salt = Path(salt_file).read_text().strip()
    if not salt:
        raise ValueError("The scrub key file is empty")
    model = json.loads(Path(reference).with_name("model.json").read_text())
    expected = {**checkpoint(json.loads(Path(reference).read_text())), "edges": edges(model)}
    cuts = sorted({instant(s["cut"]) for s in expected["signals"].values()} | {instant(expected["capturedAt"])})
    out = Path(out)
    (out / "capture").mkdir(parents=True, exist_ok=True)
    lines = Path(raw).read_text().splitlines()
    (out / "capture" / "rows.ndjson").write_text("".join(json.dumps(e) + "\n" for e in capture(lines, cuts, scrubber(app_role, salt, model), salt)))
    (out / "checkpoint.json").write_text(json.dumps(expected, indent=1) + "\n")
    (out / "cuts.txt").write_text("".join(iso(c) + "\n" for c in cuts))


def divisions(log):
    """Divisions by zero the replay counted: a value the program left undefined, where Azure gives none."""
    hit = re.search(r"(\d+) division by zero", log)
    return [f"runtime faults: {hit[1]} division by zero"] if hit and hit[1] != "0" else []


def found(out):
    out = Path(out)
    expected = json.loads((out / "checkpoint.json").read_text())
    snapshots = {cut: json.loads((out / "replays" / cut / "master.final.snapshot.json").read_text())
                 for cut in (out / "cuts.txt").read_text().split()}
    return expected, mismatches(expected, snapshots) + divisions((out / "replays.log").read_text())


CASE_START = datetime(2026, 1, 1, tzinfo=timezone.utc)
APPLY = {"AppRoleName": "demo-workload", "Name": "POST /api/sessions/{sid}/tasks/{tid}/apply"}
CASES = {
    # A1 (#174): 2 of 3 runs pass; Azure gives round(100.0 * 2 / 3, 2), below the unhealthy limit 67
    "ratio": ([("AvailabilityRow", m, {"Name": "test-f0205069f4a4", "Success": ok}) for m, ok in ((1, True), (2, False), (3, True))],
              {"container-app-apply/azureLogAnalytics/availability-test": ("Unhealthy", 66.67)}),
    # A2 (#174): every minute of the window has a count of 0, so the average has no value
    "empty-average": ([("MetricRow", m, {"resource": "webApp", "metric": "CpuPercentage", "at": iso(CASE_START + timedelta(minutes=m)),
                                         "total": 0, "count": 0}) for m in (1, 2, 3)],
                      {"container-app-apply/azureResource/cpu-percentage": ("Unknown", None)}),
    # A3 (#174): Azure counts toint(ResultCode) >= 500: 503 and 600, not 5, 50 or 200; 2 is above degraded (0), not unhealthy (2)
    "server-errors": ([("RequestRow", 1, {**APPLY, "ResultCode": code}) for code in ("5", "50", "503", "600", "200")],
                      {"system-model-write/azureLogAnalytics/apply-request-failures": ("Degraded", 2.0)}),
}


def write_cases():
    """Each case as a checkpoint: its records seen 5 s after their time, a metric minute at its end, one cut at minute 5."""
    model = json.loads((Path(__file__).parent / "reference" / "model.json").read_text())
    cut = CASE_START + timedelta(minutes=5)
    for name, (records, signals) in CASES.items():
        lines = []
        for i, (rows, m, values) in enumerate(records):
            moment = CASE_START + timedelta(minutes=m, seconds=i)
            values = values if rows == "MetricRow" else {"TimeGenerated": iso(moment), **values}
            lines.append(json.dumps({"source": "records:" + rows, "key": f"{name}/{i}", "payload": {"values": values},
                                     "meta": {"position": iso(moment + timedelta(seconds=5))}}))
        expected = {"capturedAt": iso(cut), "entities": {}, "edges": edges(model),
                    "signals": {key: {"cut": iso(cut), "state": state, "value": value} for key, (state, value) in signals.items()}}
        out = Path(__file__).parent / "checkpoints" / name
        (out / "capture").mkdir(parents=True, exist_ok=True)
        (out / "capture" / "rows.ndjson").write_text("".join(json.dumps(e) + "\n" for e in capture(lines, [cut], lambda rows, v: v, "case")))
        (out / "checkpoint.json").write_text(json.dumps(expected, indent=1) + "\n")
        (out / "cuts.txt").write_text(iso(cut) + "\n")


def compare(out):
    expected, lines = found(out)
    print("\n".join(lines))
    print(f"{len(expected['signals'])} signals, {len(expected['entities'])} entities, {len(expected['edges'])} edges, "
          f"{len(lines)} mismatches")
    return 1 if lines else 0


def mutants(program, expected):
    """name -> (program, the signal whose cut shows it): each changes one thing the comparison must catch."""
    def copy():
        return json.loads(json.dumps(program))
    def rows(p, verb):
        return next(v for v in p["verbs"] if v["id"] == verb)["values"]["rows"]
    out = {}
    p = copy(); dropped = rows(p, "signals").pop(0)
    out["drop-binding"] = (p, dropped["signal"])
    p = copy(); edge = rows(p, "children")[0]; edge["parent"], edge["child"] = edge["child"], edge["parent"]
    out["reverse-edge"] = (p, None)
    p = copy()
    row = next(r for r in rows(p, "signals") if expected["signals"][r["signal"]]["state"] == "Healthy"
               and expected["signals"][r["signal"]]["value"] is not None)
    value = expected["signals"][row["signal"]]["value"]
    row["unhealthy"] = value - 1 if row["above"] else value + 1
    out["threshold"] = (p, row["signal"])
    return out


def mutate(out, definitions):
    out, definitions = Path(out), Path(definitions)
    expected = json.loads((out / "checkpoint.json").read_text())
    program_file = definitions / "health.signals.gkql.json"
    for name, (program, signal) in mutants(json.loads(program_file.read_text()), expected).items():
        target = out / "mutants" / name
        (target / "definitions").mkdir(parents=True, exist_ok=True)
        for f in definitions.glob("*.gkql.json"):
            (target / "definitions" / f.name).write_text(f.read_text())
        (target / "definitions" / program_file.name).write_text(json.dumps(program, indent=1))
        signals = {signal: expected["signals"][signal]} if signal else {}
        (target / "checkpoint.json").write_text(json.dumps({**expected, "signals": signals}, indent=1) + "\n")
        cuts = sorted({expected["capturedAt"]} | {s["cut"] for s in signals.values()})
        (target / "cuts.txt").write_text("".join(c + "\n" for c in cuts))
        if not (target / "capture").exists():
            (target / "capture").symlink_to("../../capture")


def verify(out):
    """OUT matches the reference and every mutant does not."""
    rows = [("checkpoint", Path(out), False)] + [(m.name, m, True) for m in sorted((Path(out) / "mutants").iterdir())]
    wrong = 0
    for name, path, must_differ in rows:
        _, lines = found(path)
        ok = bool(lines) == must_differ
        wrong += not ok
        print(f"{name}: {len(lines)} mismatches, {'as expected' if ok else 'WRONG'}")
    return 1 if wrong else 0


if __name__ == "__main__":
    if sys.argv[1:2] == ["prepare"] and len(sys.argv) == 7:
        prepare(*sys.argv[2:7])
    elif sys.argv[1:] == ["cases"]:
        write_cases()
    elif sys.argv[1:2] == ["compare"] and len(sys.argv) == 3:
        sys.exit(compare(sys.argv[2]))
    elif sys.argv[1:2] == ["mutate"] and len(sys.argv) == 3:
        mutate(sys.argv[2], Path(__file__).parent / "definitions")
    elif sys.argv[1:2] == ["verify"] and len(sys.argv) == 3:
        sys.exit(verify(sys.argv[2]))
    else:
        sys.exit(__doc__)
