#!/usr/bin/env python3
"""Compare the rebuilt health model with Azure at one reference checkpoint.

  prepare RAW REFERENCE OUT APP_ROLE
                              collector output, a reference file (lab/health-mirror/export-model.py --scrub) and
                              the app role name -> OUT/capture/rows.ndjson, OUT/checkpoint.json, OUT/cuts.txt
  compare OUT                 OUT/replays/<cut>/master.final.snapshot.json -> one line per mismatch, exit 1 on any
  mutate OUT                  OUT/mutants/<name>: the definitions with one binding dropped, one edge reversed, one
                              threshold crossed, each with the cuts that show it
  verify OUT                  compare OUT and each mutant: OUT must match, every mutant must not

Run replay-cuts.sh on OUT and on each OUT/mutants/<name> between mutate and verify.

A record enters the engine when Azure could first see it: a log row at its ingestion time (the collector's position),
a metric minute when it ends. The engine's clock is the capture time, so `replay --until <cut> --advance-every 1s
--lateness 0s --no-flush` stops with the clock at the cut. Azure evaluates each signal at its own time (reportedAt),
so each signal is read from the replay cut at that time, and the entities from the one cut at the capture time.
That holds only for a settled checkpoint: no signal changes state between its evaluation and the capture.
Names are scrubbed as export-model.py --scrub does, so rows and reference keep their joins.
"""
from datetime import datetime, timedelta, timezone
import hashlib
import json
from pathlib import Path
import sys

TOLERANCE = 1e-6
ENTRY_DELAY = {"MetricRow": timedelta(minutes=1)}
SOURCE_IDS = {"resourceId", "_ItemId", "EventDataId"}  # the source's own record ids: the program reads none of them


def digest(name):
    return hashlib.sha256(name.casefold().encode()).hexdigest()[:12]


def instant(text):
    return datetime.fromisoformat(text.replace("Z", "+00:00")).astimezone(timezone.utc)


def iso(moment):
    return moment.strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def scrub(values, app_role):
    """The row as the scrubbed reference names it: app role and resource groups by alias, no ids of the source."""
    out = {k: v for k, v in values.items() if k not in SOURCE_IDS}
    if out.get("AppRoleName") == app_role:
        out["AppRoleName"] = "demo-workload"
    if out.get("ResourceGroup"):
        out["ResourceGroup"] = "group-" + digest(out["ResourceGroup"])
    return out


def entry(envelope):
    """The capture time at which Azure could first see the record."""
    rows = envelope["source"].split(":", 1)[1]
    values = envelope["payload"]["values"]
    if rows in ENTRY_DELAY:
        return instant(values["at"]) + ENTRY_DELAY[rows]
    return instant(envelope["meta"]["position"])


def capture(raw_lines, cuts, app_role):
    """Records at their entry time and a heartbeat at every cut, in capture order."""
    records = []
    for line in raw_lines:
        e = json.loads(line)
        if "values" not in e["payload"]:
            continue
        key = e["source"] + "/" + digest(e["key"])
        records.append((entry(e), {"v": 1, "source": e["source"], "key": key,
                                   "payload": {"key": key, "values": scrub(e["payload"]["values"], app_role)}}))
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


def prepare(raw, reference, out, app_role):
    expected = {**checkpoint(json.loads(Path(reference).read_text())),
                "edges": edges(json.loads(Path(reference).with_name("model.json").read_text()))}
    cuts = sorted({instant(s["cut"]) for s in expected["signals"].values()} | {instant(expected["capturedAt"])})
    out = Path(out)
    (out / "capture").mkdir(parents=True, exist_ok=True)
    lines = Path(raw).read_text().splitlines()
    (out / "capture" / "rows.ndjson").write_text("".join(json.dumps(e) + "\n" for e in capture(lines, cuts, app_role)))
    (out / "checkpoint.json").write_text(json.dumps(expected, indent=1) + "\n")
    (out / "cuts.txt").write_text("".join(iso(c) + "\n" for c in cuts))


def found(out):
    out = Path(out)
    expected = json.loads((out / "checkpoint.json").read_text())
    snapshots = {cut: json.loads((out / "replays" / cut / "master.final.snapshot.json").read_text())
                 for cut in (out / "cuts.txt").read_text().split()}
    return expected, mismatches(expected, snapshots)


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
    if sys.argv[1:2] == ["prepare"] and len(sys.argv) == 6:
        prepare(*sys.argv[2:6])
    elif sys.argv[1:2] == ["compare"] and len(sys.argv) == 3:
        sys.exit(compare(sys.argv[2]))
    elif sys.argv[1:2] == ["mutate"] and len(sys.argv) == 3:
        mutate(sys.argv[2], Path(__file__).parent / "definitions")
    elif sys.argv[1:2] == ["verify"] and len(sys.argv) == 3:
        sys.exit(verify(sys.argv[2]))
    else:
        sys.exit(__doc__)
