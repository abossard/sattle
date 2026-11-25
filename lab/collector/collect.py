#!/usr/bin/env python3
"""One collector outside the engine: source plugins turn readings into the engine's record envelope.

  collect.py --streams FILE --out FILE [--every SECONDS] [--polls N] [--record DIR]
             [--workspace ID] [--prometheus URL] [--context NAME] [--eventhubs NAMESPACE]
             [--resources FILE]
  collect.py --streams FILE --out FILE --replay DIR

A stream is one query of one plugin (sources.py) and the row type its records feed. Each poll writes the new
records of a stream to the end of --out, or a heartbeat when there are none. The output is also the collector's
state: on start it is read back to find, per stream, the last source position written and the keys written
within the stream's overlap behind it, with a digest of their values: a reading the source revises inside the
overlap is written again under its key, and the later record replaces the first. --record keeps every source response; --replay polls from such a
recording instead of the sources.
"""
from argparse import ArgumentParser
from collections import namedtuple
from datetime import timedelta
import json
import os
from pathlib import Path
import sys
import time

import sources
from sources import ReadError, compact, digest, instant, iso

Cursor = namedtuple("Cursor", "last seen")  # last position written, {key: (position, digest of values)} not behind the overlap
DEFAULTS = {"overlap": 300, "backfill": 900}


def setting(stream, name):
    return timedelta(seconds=stream.get(name, DEFAULTS[name]))


# Calculations

def label(stream):
    return stream["plugin"] + ":" + stream["name"]


def envelope(stream, captured_at, reading):
    return {"v": 1, "source": "records:" + stream["rows"], "capturedAt": captured_at,
            "key": reading["key"],
            "meta": {"source": label(stream), "position": iso(reading["position"])},
            "payload": {"key": reading["key"], "values": reading["values"]}}


def heartbeat(stream, captured_at):
    return {"v": 1, "source": "records:" + stream["rows"], "capturedAt": captured_at,
            "key": stream["name"] + "/heartbeat/" + captured_at, "meta": {"source": label(stream)}, "payload": {}}


def prune(cursor, overlap):
    """Forget keys at or behind the overlap. Keys are in write order, so pruning stops at the first one still
    inside; a key kept longer only costs memory, since its position alone already rejects it."""
    if cursor.last is None:
        return cursor
    floor = cursor.last - overlap
    seen = cursor.seen
    drop = 0
    for position, _ in seen.values():
        if position > floor:
            break
        drop += 1
    if drop == 0:
        return cursor
    return Cursor(cursor.last, dict(list(seen.items())[drop:]))


def admit(cursor, overlap, readings):
    """The readings not written yet or revised since, in position order, and the cursor after writing them."""
    floor = cursor.last - overlap if cursor.last else None
    seen = dict(cursor.seen)
    last = cursor.last
    fresh = []
    latest = {}  # one record per key and poll: the engine refuses two values of one key at one capturedAt
    for reading in sorted(readings, key=lambda r: r["position"]):
        latest.pop(reading["key"], None)
        latest[reading["key"]] = reading
    for reading in latest.values():
        mark = digest(reading["values"])
        if (floor is not None and reading["position"] <= floor) or seen.get(reading["key"], (None, None))[1] == mark:
            continue
        seen.pop(reading["key"], None)  # a revision moves to the end, so the keys stay in write order
        seen[reading["key"]] = (reading["position"], mark)
        last = reading["position"] if last is None else max(last, reading["position"])
        fresh.append(reading)
    if not fresh:
        return [], cursor
    return fresh, prune(Cursor(last, seen), overlap)


def window(stream, cursor, now):
    start = cursor.last - setting(stream, "overlap") if cursor.last else now - setting(stream, "backfill")
    return start, now


def poll(stream, cursor, captured_at, response):
    """Envelopes for one source response and the stream's cursor after them."""
    # the stream name keeps the keys of two streams of one row type apart
    readings = [{**r, "key": stream["name"] + "/" + r["key"]} for r in sources.readings(stream, response)]
    fresh, cursor = admit(cursor, setting(stream, "overlap"), readings)
    envelopes = [envelope(stream, captured_at, r) for r in fresh] or [heartbeat(stream, captured_at)]
    return envelopes, cursor


def resume(streams, lines):
    """Fold written envelopes back into one cursor per stream."""
    by_label = {label(s): s for s in streams}
    cursors = {s["name"]: Cursor(None, {}) for s in streams}  # each dict is local to this fold

    def pruned():
        return {s["name"]: prune(cursors[s["name"]], setting(s, "overlap")) for s in streams}

    for n, line in enumerate(lines, 1):
        env = json.loads(line)
        meta = env.get("meta") or {}
        stream = by_label.get(meta.get("source"))
        if stream is not None and "position" in meta:
            position = instant(meta["position"])
            cursor = cursors[stream["name"]]
            cursor.seen.pop(env["payload"]["key"], None)
            cursor.seen[env["payload"]["key"]] = (position, digest(env["payload"]["values"]))
            cursors[stream["name"]] = Cursor(position if cursor.last is None else max(cursor.last, position),
                                             cursor.seen)
        if n % 1000 == 0:
            cursors = pruned()
    return pruned()


# Actions

def repair(out):
    """Cut a line torn by a crash, so the next write starts on a line of its own."""
    if not out.exists() or out.stat().st_size == 0:
        return
    with out.open("rb+") as f:
        data = f.read()
        if data.endswith(b"\n"):
            return
        f.truncate(data.rfind(b"\n") + 1)


def written(out):
    if not out.exists():
        return
    with out.open(encoding="utf-8") as f:
        for line in f:
            if line.strip():
                yield line


def append(out, envelopes):
    with out.open("a", encoding="utf-8") as f:
        f.write("".join(compact(e) + "\n" for e in envelopes))
        f.flush()
        os.fsync(f.fileno())


def run(streams, out, ticks):
    """ticks yields (stream, capturedAt, get) and get(window) returns the source response for that window."""
    repair(out)
    cursors = resume(streams, written(out))
    for stream, captured_at, get in ticks:
        name = stream["name"]
        try:
            response = get(window(stream, cursors[name], instant(captured_at)))
        except ReadError as error:
            print(f"{label(stream)}: {error}", file=sys.stderr)
            continue
        envelopes, cursors[name] = poll(stream, cursors[name], captured_at, response)
        append(out, envelopes)


def live_ticks(streams, options, every, polls, record):
    count = 0
    while polls is None or count < polls:
        for stream in streams:
            captured_at = iso(sources.now())

            def get(w, stream=stream, captured_at=captured_at):
                response = sources.fetch(stream, w, options)
                if record:
                    record.mkdir(parents=True, exist_ok=True)
                    line = {"stream": stream["name"], "capturedAt": captured_at,
                            "window": [iso(w[0]), iso(w[1])], "response": response}
                    with (record / (stream["name"] + ".ndjson")).open("a", encoding="utf-8") as f:
                        f.write(compact(line) + "\n")
                return response
            yield stream, captured_at, get
        count += 1
        if polls is None or count < polls:
            time.sleep(every)


def replay_ticks(streams, recording):
    by_name = {s["name"]: s for s in streams}
    lines = [json.loads(line) for p in sorted(Path(recording).glob("*.ndjson"))
             for line in p.read_text(encoding="utf-8").splitlines() if line.strip()]
    for line in sorted(lines, key=lambda x: instant(x["capturedAt"])):
        if line["stream"] in by_name:
            yield by_name[line["stream"]], line["capturedAt"], (lambda w, r=line["response"]: r)


def main(argv):
    p = ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--streams", type=Path, required=True)
    p.add_argument("--out", type=Path, required=True)
    p.add_argument("--replay", type=Path)
    p.add_argument("--record", type=Path)
    p.add_argument("--every", type=float, default=60)
    p.add_argument("--polls", type=int)
    p.add_argument("--workspace")
    p.add_argument("--prometheus")
    p.add_argument("--context")
    p.add_argument("--eventhubs")
    p.add_argument("--resources", type=Path, help="JSON map from a stream's resource or hub name to its ARM id or hub name")
    a = p.parse_args(argv)
    streams = json.loads(a.streams.read_text())
    if a.replay:
        ticks = replay_ticks(streams, a.replay)
    else:
        options = {"workspace": a.workspace, "prometheus": a.prometheus, "context": a.context, "eventhubs": a.eventhubs,
                   "resources": json.loads(a.resources.read_text()) if a.resources else {}}
        ticks = live_ticks(streams, options, a.every, a.polls, a.record)
    run(streams, a.out, ticks)


if __name__ == "__main__":
    main(sys.argv[1:])
