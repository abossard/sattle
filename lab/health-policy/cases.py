#!/usr/bin/env python3
"""Cases of the health policy library (definitions/policy.*): captures, the transitions each must give, and the check.

  write     cases/<case>/capture/rows.ndjson for every case below
  check     cases/<case>/replays/<run>/ -> one line per difference from the expected rows, exit 1 on any

Run replay.sh between write and check. Time is minutes from 2026-01-01 00:00 UTC; windows are one minute wide. A reading
of minute m is taken at m:10 and captured at m:15; a policy row is captured at its given minute and second; the last
record is a heartbeat at the case's end, where the replay stops with the clock (--no-flush).
"""
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import sys

HERE = Path(__file__).parent
T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)
FIELDS = ("revision", "above", "below", "enterAfter", "exitAfter", "gap", "missing")


def at(minute, second=0):
    return T0 + timedelta(minutes=minute, seconds=second)


def iso(moment):
    return moment.strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def policy(key, revision=1, enter=2, exit=3, gap=1000, minute=-1, second=30):
    """A policy row: unhealthy at 80 and above, healthy again below 50, missing data shown as Unknown."""
    return (at(minute, second), "PolicyRow", dict(zip(("key",) + FIELDS, (key, revision, 80, 50, enter, exit, gap, "Unknown"))))


def readings(key, values):
    """{minute: value} -> one reading per minute."""
    return [(at(m, 15), "Reading", {"key": key, "t": iso(at(m, 10)), "value": v}) for m, v in values.items()]


def task(key, events):
    return [(at(m, 15), "TaskEvent", {"task": key, "t": iso(at(m, 10)), "phase": p}) for m, p in events.items()]


def depends(consumer, dependency, required):
    return (at(-1, 30), "DependsOn", {"consumer": consumer, "dependency": dependency, "required": required})


def t(key, minute, before, after, revision=None, since=None):
    """An expected transition: key, its window (minute), shown state before and after, policy revision and since."""
    return {"key": key, "windowStart": iso(at(minute)), "windowEnd": iso(at(minute + 1)), "from": before, "to": after,
            **({"revision": revision} if revision else {}), **({"since": iso(since)} if since else {})}


ABOVE4 = {0: 90, 1: 90, 2: 90, 3: 90}
CASES = {
    # A3: four windows above the threshold; the entry streak is data of the policy row, the program is the same
    "a3-streak2": {"records": [policy("api", enter=2), *readings("api", ABOVE4)], "end": 8,
                   "expect": {"health": {"Transition": [t("api", 0, "Healthy", "Healthy", 1), t("api", 1, "Healthy", "Unhealthy", 1)]}}},
    "a3-streak4": {"records": [policy("api", enter=4), *readings("api", ABOVE4)], "end": 8,
                   "expect": {"health": {"Transition": [t("api", 0, "Healthy", "Healthy", 1), t("api", 3, "Healthy", "Unhealthy", 1)]}}},
    # A4: recovery after three windows below 50; two (minutes 1, 2) and a window between the limits (3) change nothing;
    # three silent windows (7, 8, 9) reach the freshness limit of 3 and show the missing-data state
    "a4": {"records": [policy("db", enter=1, exit=3, gap=3), *readings("db", {0: 90, 1: 10, 2: 10, 3: 60, 4: 10, 5: 10, 6: 10})], "end": 14,
           "expect": {"health": {"Transition": [t("db", 0, "Healthy", "Unhealthy", 1), t("db", 6, "Unhealthy", "Healthy", 1),
                                                t("db", 9, "Healthy", "Unknown", 1)]}}},
    # A5: checkout requires store, search only uses it; both read the same transition of store
    "a5": {"records": [policy("store", enter=1), policy("checkout"), policy("search"), depends("checkout", "store", True),
                       depends("search", "store", False), *readings("store", {0: 20, 1: 95})], "end": 6,
           "expect": {"health": {
               "Transition": [t("store", 0, "Healthy", "Healthy", 1), t("store", 1, "Healthy", "Unhealthy", 1)],
               "Impact": [{"consumer": "checkout", "dependency": "store", "required": True, "dependencyState": "Unhealthy", "effect": "Degraded",
                           "windowStart": iso(at(1)), "value": 95},
                          {"consumer": "search", "dependency": "store", "required": False, "dependencyState": "Unhealthy", "effect": "None",
                           "windowStart": iso(at(1)), "value": 95}]}}},
    # A6: t2 succeeds at minute 3 while t1 stays Unknown until its own failure at minute 5
    "a6": {"records": [*task("t1", {0: "started", 2: "running", 5: "failed"}), *task("t2", {1: "started", 3: "succeeded"})], "end": 9,
           "expect": {"completion": {"Transition": [t("t1", 0, "Unknown", "Unknown", 1), t("t1", 5, "Unknown", "Failed", 1),
                                                    t("t2", 1, "Unknown", "Unknown", 1), t("t2", 3, "Unknown", "Succeeded", 1)]}}},
    # A7: revision 2 (entry streak 4) is added at 8:30, after window 6 closed (8:00) and before window 7 closes (9:00);
    # the windows before keep revision 1, the later ones are folded with revision 2
    "a7": {"records": [policy("api", enter=2), *readings("api", {**ABOVE4, 4: 10, 5: 10, 6: 10, 9: 90, 10: 90, 11: 90, 12: 90}),
                       policy("api", revision=2, enter=4, minute=8, second=30)], "end": 17,
           "expect": {"health": {"Transition": [t("api", 0, "Healthy", "Healthy", 1), t("api", 1, "Healthy", "Unhealthy", 1),
                                                t("api", 6, "Unhealthy", "Healthy", 1), t("api", 12, "Healthy", "Unhealthy", 2, since=at(8, 30))]}}},
}


def write():
    for name, case in CASES.items():
        records = sorted(case["records"], key=lambda r: r[0]) + [(at(case["end"]), "Reading", None)]
        lines = []
        for i, (captured, rows, values) in enumerate(records):
            key = f"{rows}/{i}" if values is not None else "heartbeat/" + iso(captured)
            payload = {"key": key, "values": values} if values is not None else {}
            lines.append({"v": 1, "source": "records:" + rows, "key": key, "payload": payload, "capturedAt": iso(captured), "seq": i})
        out = HERE / "cases" / name / "capture"
        out.mkdir(parents=True, exist_ok=True)
        (out / "rows.ndjson").write_text("".join(json.dumps(l) + "\n" for l in lines))


def nodes(snapshot, label):
    """The snapshot's nodes of one type as {property: value}."""
    out = []
    for e in snapshot["extent"]["entities"]:
        if e["type"]["value"].split(".")[-1] == label:
            out.append({p["property"]["value"].split(".")[-1]: p["value"].get("value") for p in e["properties"]})
    return out


def same(want, got):
    if isinstance(want, bool) or isinstance(got, bool):
        return want == got
    if isinstance(want, (int, float)):
        try:
            return abs(float(got) - want) < 1e-9
        except (TypeError, ValueError):
            return False
    if isinstance(want, str) and isinstance(got, str) and want.endswith("Z") and "T" in want:
        return got.replace("+00:00", "Z").rstrip("Z").ljust(26, "0")[:19] == want[:19]
    return want == got


def differences(expect, replay):
    """Expected rows of each graph and type against the replay's final snapshot: missing, unexpected, differing rows."""
    out = []
    for graph, types in expect.items():
        snapshot = json.loads((replay / f"{graph}.final.snapshot.json").read_text())
        for label, rows in types.items():
            got = nodes(snapshot, label)
            ident = ("consumer", "dependency") if label == "Impact" else ("key", "windowStart")
            for want in rows:
                match = [g for g in got if all(same(want[k], g.get(k)) for k in ident)]
                if not match:
                    out.append(f"{graph} {label} {[want[k] for k in ident]}: missing")
                    continue
                bad = {k: (v, match[0].get(k)) for k, v in want.items() if not same(v, match[0].get(k))}
                if bad:
                    out.append(f"{graph} {label} {[want[k] for k in ident]}: {bad}")
            for g in got:
                if not any(all(same(w[k], g.get(k)) for k in ident) for w in rows):
                    out.append(f"{graph} {label} unexpected: {g}")
    return out


def check():
    wrong = 0
    for name, case in CASES.items():
        replays = sorted((HERE / "cases" / name / "replays").glob("*"))
        if not replays:
            print(f"{name}: no replay"); wrong += 1; continue
        for replay in replays:
            lines = differences(case["expect"], replay)
            wrong += bool(lines)
            print(f"{name} {replay.name}: {len(lines)} differences")
            for line in lines:
                print("  " + line)
        # the same events and policy replayed again give the same transitions, window, revision and since included
        runs = {r.name: r for r in replays}
        if {"1s", "1s-again"} <= runs.keys():
            first, again = ({graph: sorted(map(json.dumps, nodes(json.loads((runs[r] / f"{graph}.final.snapshot.json").read_text()), "Transition")))
                             for graph in case["expect"]} for r in ("1s", "1s-again"))
            wrong += first != again
            print(f"{name} 1s-again: transitions {'identical' if first == again else 'DIFFER'} to 1s")
    return 1 if wrong else 0


if __name__ == "__main__":
    if sys.argv[1:] == ["write"]:
        write()
    elif sys.argv[1:] == ["check"]:
        sys.exit(check())
    else:
        sys.exit(__doc__)
