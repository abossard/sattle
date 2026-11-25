"""Collector tests: python3 -B lab/collector/test_collect.py

The recordings under recordings/ are scrubbed source responses of a live run (collect.py --record, then
lab/fixtures/scrub.mjs recording). Replaying them must give every source reading exactly once, also across a
restart and across a crash in the middle of a write.
"""
from datetime import datetime, timedelta, timezone
import importlib.util
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

import collect
import sources

HERE = Path(__file__).parent
STREAMS = json.loads((HERE / "streams.json").read_text())
INGEST = json.loads((HERE / "definitions" / "collector.ingest.gkql.json").read_text())
EVENT_TIME = {"log-analytics": "TimeGenerated", "prometheus": "at", "kubernetes-metrics": "at", "azure-monitor-hub": "at", "azure-metrics": "at"}


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
    if stream["plugin"] == "azure-metrics":
        return {(m["name"]["value"], p["timeStamp"]) for m in (r or {}).get("value", []) for t in m["timeseries"]
                for p in t["data"] if set(p) - {"timeStamp"}}
    if stream["plugin"] == "azure-monitor-hub":
        return {(m["resourceId"].lower(), m["metricName"], m["time"], m["timeGrain"]) for e in r
                for m in e["payload"]["records"]}
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


    def test_streams_of_one_row_type_never_share_a_key(self):
        with TemporaryDirectory() as d:
            out = replay(Path(d) / "out.ndjson", list(collect.replay_ticks(STREAMS, HERE / "recordings")))
            records = [(e["source"], e["payload"]["key"]) for e in out if e["payload"]]
            shared = {s["rows"] for s in STREAMS if sum(t["rows"] == s["rows"] for t in STREAMS) > 1}
            self.assertTrue(shared, "the recordings have two streams of one row type")
            self.assertEqual(len(set(records)), len(records))

    def test_one_resource_has_one_alias_in_every_stream(self):
        """#176 A3: the recordings are scrubbed into one directory under one salt, so a resource the hub and the
        platform metric stream both report carries one alias and their readings join on resourceId."""
        def resources(plugin):
            return {r["values"]["resourceId"] for s in STREAMS if s["plugin"] == plugin
                    for _, _, get in ticks(s) for r in sources.readings(s, get(None))}
        hub, platform = resources("azure-monitor-hub"), resources("azure-metrics")
        self.assertTrue(platform)
        self.assertLessEqual(platform, hub)

    def test_platform_readings_carry_the_hub_values_and_the_resource_name(self):
        """#176 A5, as the README's stream table says: the values of azure-monitor-hub and `resource`; the
        aggregates not asked for are null."""
        def readings(plugin):
            return [r["values"] for s in STREAMS if s["plugin"] == plugin for _, _, get in ticks(s)
                    for r in sources.readings(s, get(None))]
        hub, platform = readings("azure-monitor-hub"), readings("azure-metrics")
        for values in platform:
            with self.subTest(metric=values["metric"], at=values["at"]):
                self.assertEqual(set(values), set(hub[0]) | {"resource"})
                self.assertEqual([values[a] for a in ("average", "minimum", "maximum")], [None] * 3)


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


def platform(*points):
    return {"value": [{"id": "/x/providers/Microsoft.Insights/metrics/Requests", "name": {"value": "Requests"},
                       "timeseries": [{"metadatavalues": [], "data": list(points)}]}]}


def prometheus(*points):
    return {"status": "success", "data": {"resultType": "matrix", "result": [
        {"metric": {"pod": "a"}, "values": [[at(m).timestamp(), v] for m, v in points]}]}}


class MissingStaysMissingTests(unittest.TestCase):
    PROMETHEUS = {"name": "p", "plugin": "prometheus", "rows": "SampleRow", "labels": ["pod"]}
    LOGS = {"name": "l", "plugin": "log-analytics", "rows": "RequestRow", "key": ["id"]}
    METRICS = {"name": "k", "plugin": "kubernetes-metrics", "rows": "UsageRow", "kind": "pods"}
    PLATFORM = {"name": "a", "plugin": "azure-metrics", "rows": "MetricRow", "resource": "r"}

    def platform(self, *points):
        return platform(*points)

    def prometheus(self, *points):
        return prometheus(*points)

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
            ("platform zero", self.PLATFORM, self.platform({"timeStamp": "2026-11-25T10:00:00Z", "total": 0}), "total", 0),
            ("platform absent", self.PLATFORM, self.platform({"timeStamp": "2026-11-25T10:00:00Z", "total": 0}),
             "average", None),
        ]
        for name, stream, response, field, expected in cases:
            with self.subTest(name):
                [reading] = sources.readings(stream, response)
                self.assertIn(field, reading["values"])
                self.assertEqual(reading["values"][field], expected)

    def test_an_empty_platform_bucket_is_no_reading(self):
        self.assertEqual(sources.readings(self.PLATFORM, self.platform({"timeStamp": "2026-11-25T10:00:00Z"})), [])

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


class RevisionTests(unittest.TestCase):
    """#176 A1: a point or bucket the source revises after the first read is asked again while it is inside the
    stream's overlap and written again under its key; the later capturedAt makes it replace the first record."""
    STREAMS = [{"name": "p", "plugin": "prometheus", "rows": "SampleRow", "labels": ["pod"]},
               {"name": "a", "plugin": "azure-metrics", "rows": "MetricRow", "resource": "r"}]
    ASKED = {"prometheus": lambda w: (lambda g: g and (g[0], g[1] + 1))(sources.prometheus_range({}, w)),
             "azure-metrics": lambda w: (lambda s: s and (s[0].timestamp(), s[1].timestamp()))(
                 sources.azure_metrics_window({}, w))}
    ANSWER = {"prometheus": lambda points: prometheus(*[(m, str(v)) for m, v in points]),
              "azure-metrics": lambda points: platform(*[{"timeStamp": sources.iso(at(m)), "total": v, "count": 1}
                                                         for m, v in points])}

    def ticks(self, stream, polls):
        """One tick per poll: (minute of the poll, {minute: value the source holds then}); the source answers only
        the minutes the plugin asks for."""
        for now, held in polls:
            def get(w, held=held):
                span = self.ASKED[stream["plugin"]](w)
                return self.ANSWER[stream["plugin"]](
                    sorted((m, v) for m, v in held.items() if span and span[0] <= at(m).timestamp() < span[1]))
            yield stream, sources.iso(at(now)), get

    def test_one_poll_writes_the_latest_reading_of_a_key(self):
        def reading(minute, value):
            return {"key": "k", "position": at(minute), "values": {"value": value}}
        cases = [("revised in the poll", [reading(1, 1), reading(2, 5)], [5]),
                 ("revision first in the response", [reading(2, 5), reading(1, 1)], [5]),
                 ("delivered twice", [reading(1, 1), reading(1, 1)], [1])]
        for name, readings, expected in cases:
            with self.subTest(name):
                fresh, _ = collect.admit(collect.Cursor(None, {}), timedelta(seconds=300), readings)
                self.assertEqual([r["values"]["value"] for r in fresh], expected)

    def test_a_revision_inside_the_overlap_replaces_the_first_value(self):
        cases = [
            ("revised once", [(5, {2: 1}), (6, {2: 2}), (7, {2: 2})], [(2, 1), (2, 2)]),
            ("revised twice", [(5, {2: 1}), (6, {2: 2}), (7, {2: 3})], [(2, 1), (2, 2), (2, 3)]),
            ("revised back", [(5, {2: 1}), (6, {2: 2}), (7, {2: 1})], [(2, 1), (2, 2), (2, 1)]),
            ("unchanged", [(5, {2: 1}), (6, {2: 1}), (7, {2: 1})], [(2, 1)]),
            ("behind the overlap", [(5, {2: 1}), (15, {2: 1, 12: 1}), (16, {2: 9, 12: 1})], [(2, 1), (12, 1)]),
        ]
        for stream in self.STREAMS:
            field = "value" if stream["plugin"] == "prometheus" else "total"
            for name, polls, expected in cases:
                for restart in range(len(polls)):
                    with self.subTest(stream=stream["plugin"], case=name, restart_after=restart), TemporaryDirectory() as d:
                        out = Path(d) / "out.ndjson"
                        ticks = list(self.ticks(stream, polls))
                        collect.run([stream], out, iter(ticks[:restart]))
                        collect.run([stream], out, iter(ticks[restart:]))
                        records = [json.loads(x) for x in out.read_text().splitlines()]
                        records = [e for e in records if e["payload"]]
                        self.assertEqual([(sources.instant(e["payload"]["values"]["at"]).minute, e["payload"]["values"][field])
                                          for e in records], expected)
                        latest = {}
                        for e in records:
                            self.assertGreater(sources.instant(e["capturedAt"]),
                                               latest.get(e["payload"]["key"], (at(0),))[0])
                            latest[e["payload"]["key"]] = (sources.instant(e["capturedAt"]), e["payload"]["values"][field])
                        self.assertEqual(sorted(v for _, v in latest.values()),
                                         sorted({m: v for m, v in expected}.values()))


class MeanTests(unittest.TestCase):
    """#176 A2: the mean of a metric per minute is its total over its count, so it is defined for streams that carry
    only total and count (azure-metrics) and weights each bucket by its count, not a mean of per-bucket averages."""
    VERBS = {v["id"]: v for v in INGEST["verbs"]}
    FEEDS = {w["to"]: w["from"] for w in INGEST["wires"]}

    def source(self, at, expr):
        """The expression at the input of verb `at` with every column it reads replaced by its definition upstream,
        down to the fields of the source rows."""
        if "fn" in expr:
            return {**expr, "args": [self.source(at, a) for a in expr["args"]]}
        if "ref" not in expr:
            return expr
        verb = self.VERBS[self.FEEDS[at]]
        while verb["verb"] != "rows":
            defined = verb.get("columns") or verb.get("aggregates") or {}
            if expr["ref"] in defined:
                return self.source(verb["id"], defined[expr["ref"]])
            verb = self.VERBS[self.FEEDS[verb["id"]]]
        return expr

    def test_the_metric_properties_read_the_source_fields(self):
        def total(field):
            return {"fn": "sum", "args": [{"ref": field}]}
        cases = [("name", {"ref": "metric"}),
                 ("average", {"fn": "/", "args": [total("total"), total("count")]})]
        [prop] = [n["props"] for n in self.VERBS["showMetric"]["target"]["nodes"]]
        for name, expected in cases:
            with self.subTest(name):
                self.assertEqual(self.source("showMetric", prop[name]), expected)

    def test_a_bucket_without_samples_is_not_counted(self):
        chain, at = [], "showMetric"
        while at in self.FEEDS:
            at = self.FEEDS[at]
            chain.append(self.VERBS[at])
        conditions = [v["condition"] for v in chain if v["verb"] == "where"]
        self.assertIn({"fn": ">", "args": [{"fn": "coalesce", "args": [{"ref": "count"}, {"lit": 0.0}]}, {"lit": 0.0}]},
                      conditions)


class OnePlaceTests(unittest.TestCase):
    """#176 A4: the collector, the checkpoint compare and the case check read and write instants with one helper."""
    def test_the_checkpoint_compare_and_the_case_check_use_the_collectors_time_helpers(self):
        for path, names in [(HERE.parent / "health-model" / "checkpoint.py", ("instant", "iso")),
                            (HERE.parent / "health-policy" / "cases.py", ("iso",))]:
            spec = importlib.util.spec_from_file_location(path.stem, path)
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            for name in names:
                with self.subTest(file=path.name, helper=name):
                    self.assertIs(getattr(module, name), getattr(sources, name))


if __name__ == "__main__":
    unittest.main()
