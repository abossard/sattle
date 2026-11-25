"""Collector tests: python3 -B lab/collector/test_collect.py

The recordings under recordings/ are scrubbed source responses of a live run (collect.py --record, then
lab/fixtures/scrub.mjs recording). Replaying them must give every source reading exactly once, also across a
restart and across a crash in the middle of a write.
"""
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

import collect
import sources

HERE = Path(__file__).parent
STREAMS = json.loads((HERE / "streams.json").read_text())
EVENT_TIME = {"log-analytics": "TimeGenerated", "prometheus": "at", "kubernetes-metrics": "at"}


def ticks(stream):
    return [t for t in collect.replay_ticks(STREAMS, HERE / "recordings") if t[0] is stream]


def replay(out, ticks):
    collect.run(STREAMS, out, iter(ticks))
    return [json.loads(line) for line in out.read_text().splitlines()]


def source_readings(stream, line):
    """What the source answered in one recorded response, counted without the plugin code."""
    r = line["response"]
    if stream["plugin"] == "log-analytics":
        table = r["tables"][0]
        item = [c["name"] for c in table["columns"]].index("_ItemId")
        return {row[item] for row in table["rows"]}
    if stream["plugin"] == "prometheus":
        return {(json.dumps(s["metric"], sort_keys=True), v[0]) for s in (r or {}).get("data", {}).get("result", [])
                for v in s["values"]}
    return {(i["metadata"].get("namespace"), i["metadata"]["name"], c.get("name"), i["timestamp"])
            for i in r["items"] for c in (i.get("containers") or [{}])}


class ReplayTests(unittest.TestCase):
    def test_each_stream_gives_every_reading_once_with_source_position_and_event_time(self):
        for stream in STREAMS:
            with self.subTest(stream=stream["name"]), TemporaryDirectory() as d:
                recorded = ticks(stream)
                self.assertGreaterEqual(len(recorded), 2, "a recording with overlapping polls")
                lines = [json.loads(x) for x in (HERE / "recordings" / (stream["name"] + ".ndjson")).read_text().splitlines()]
                expected = set().union(*(source_readings(stream, line) for line in lines))
                out = replay(Path(d) / "out.ndjson", recorded)
                records = [e for e in out if e["payload"]]
                self.assertTrue(records)
                self.assertEqual(len(records), len(expected))
                self.assertEqual(len({e["key"] for e in records}), len(records))
                for e in records:
                    self.assertEqual(e["source"], "records:" + stream["rows"])
                    self.assertEqual(e["meta"]["source"], stream["plugin"] + ":" + stream["name"])
                    sources.instant(e["meta"]["position"])
                    sources.instant(e["payload"]["values"][EVENT_TIME[stream["plugin"]]])
                    self.assertTrue(e["payload"]["key"])


class ResumeTests(unittest.TestCase):
    def test_a_restart_after_any_poll_writes_what_one_run_writes(self):
        for stream in STREAMS:
            recorded = ticks(stream)
            for k in range(1, len(recorded)):
                with self.subTest(stream=stream["name"], restart_after=k), TemporaryDirectory() as d:
                    whole = Path(d) / "whole.ndjson"
                    replay(whole, recorded)
                    split = Path(d) / "split.ndjson"
                    replay(split, recorded[:k])
                    replay(split, recorded[k:])
                    self.assertEqual(split.read_text(), whole.read_text())

    def test_a_crash_inside_a_write_is_repaired_by_polling_again(self):
        for stream in STREAMS:
            recorded = ticks(stream)
            with TemporaryDirectory() as d:
                whole = Path(d) / "whole.ndjson"
                replay(whole, recorded)
                text = whole.read_text()
                first = len(replay(Path(d) / "first.ndjson", recorded[:1]))
                for cut in (first // 2, first - 1):
                    with self.subTest(stream=stream["name"], lines_kept=cut):
                        crashed = Path(d) / f"crashed-{cut}.ndjson"
                        lines = text.splitlines(keepends=True)
                        crashed.write_text("".join(lines[:cut]) + lines[cut][: len(lines[cut]) // 2])
                        replay(crashed, recorded)
                        self.assertEqual(crashed.read_text(), text)


def at(minute):
    return datetime(2026, 11, 25, 10, minute, tzinfo=timezone.utc)


class MissingStaysMissingTests(unittest.TestCase):
    PROMETHEUS = {"name": "p", "plugin": "prometheus", "rows": "SampleRow", "labels": ["pod"]}
    LOGS = {"name": "l", "plugin": "log-analytics", "rows": "RequestRow", "key": ["id"]}
    METRICS = {"name": "k", "plugin": "kubernetes-metrics", "rows": "UsageRow", "kind": "pods"}

    def prometheus(self, *points):
        return {"status": "success", "data": {"resultType": "matrix", "result": [
            {"metric": {"pod": "a"}, "values": [[at(m).timestamp(), v] for m, v in points]}]}}

    def logs(self, value):
        return {"tables": [{"name": "PrimaryResult", "columns": [
            {"name": "_position", "type": "datetime"}, {"name": "id", "type": "string"},
            {"name": "DurationMs", "type": "real"}], "rows": [["2026-11-25T10:00:00Z", "r1", value]]}]}

    def pod(self, usage):
        return {"items": [{"metadata": {"name": "a", "namespace": "n"}, "timestamp": "2026-11-25T10:00:00Z",
                           "window": "20s", "containers": [{"name": "c", "usage": usage}]}]}

    def test_a_reading_keeps_zero_and_missing_apart(self):
        cases = [
            ("prometheus zero", self.PROMETHEUS, self.prometheus((0, "0")), "value", 0),
            ("prometheus NaN", self.PROMETHEUS, self.prometheus((0, "NaN")), "value", None),
            ("log analytics zero", self.LOGS, self.logs(0), "DurationMs", 0),
            ("log analytics null", self.LOGS, self.logs(None), "DurationMs", None),
            ("metrics api zero", self.METRICS, self.pod({"cpu": "0", "memory": "0"}), "cpu", 0),
            ("metrics api absent", self.METRICS, self.pod({"cpu": "5m"}), "memory", None),
        ]
        for name, stream, response, field, expected in cases:
            with self.subTest(name):
                [reading] = sources.readings(stream, response)
                self.assertIn(field, reading["values"])
                self.assertEqual(reading["values"][field], expected)

    def test_a_gap_in_the_source_is_no_record_and_no_zero(self):
        stream = self.PROMETHEUS
        envelopes, _ = collect.poll(stream, collect.Cursor(None, {}), "2026-11-25T10:05:00Z",
                                    self.prometheus((0, "1"), (2, "3")))
        self.assertEqual([e["payload"]["values"]["at"] for e in envelopes],
                         [sources.iso(at(0)), sources.iso(at(2))])

    def test_a_poll_with_nothing_new_is_a_heartbeat(self):
        stream = self.PROMETHEUS
        first, cursor = collect.poll(stream, collect.Cursor(None, {}), "2026-11-25T10:05:00Z", self.prometheus((0, "1")))
        again, _ = collect.poll(stream, cursor, "2026-11-25T10:06:00Z", self.prometheus((0, "1")))
        self.assertEqual(len(first), 1)
        self.assertEqual([e["payload"] for e in again], [{}])


class WindowTests(unittest.TestCase):
    def test_the_next_window_starts_one_overlap_behind_the_last_position(self):
        stream = {"name": "p", "plugin": "prometheus", "rows": "R", "overlap": 120, "backfill": 600}
        now = at(30)
        cases = [("first poll", collect.Cursor(None, {}), now - timedelta(seconds=600)),
                 ("later poll", collect.Cursor(at(20), {}), at(18))]
        for name, cursor, start in cases:
            with self.subTest(name):
                self.assertEqual(collect.window(stream, cursor, now), (start, now))

    def test_the_prometheus_grid_is_fixed_and_ends_one_step_early(self):
        cases = [("aligned", (at(0), at(5)), (at(0).timestamp(), at(4).timestamp(), 60)),
                 ("unaligned", (at(0) + timedelta(seconds=1), at(5) + timedelta(seconds=30)),
                  (at(1).timestamp(), at(4).timestamp(), 60)),
                 ("shorter than a step", (at(0), at(0) + timedelta(seconds=59)), None)]
        for name, window, grid in cases:
            with self.subTest(name):
                self.assertEqual(sources.prometheus_range({"step": 60}, window), grid)


if __name__ == "__main__":
    unittest.main()
