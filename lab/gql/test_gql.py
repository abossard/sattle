#!/usr/bin/env python3
"""Focused tests: python3 -B lab/gql/test_gql.py."""
from collections import Counter
from contextlib import redirect_stdout
import io
import json
from pathlib import Path
import shutil
import sys
import unittest


HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
sys.path.insert(0, str(HERE))

from app import Terminal
from gql_lab import GqlHealthLab, LabError


SUBSCRIPTION = "00000000-0000-0000-0000-000000000000"
RESOURCE = (
    f"/subscriptions/{SUBSCRIPTION}/resourceGroups/example/providers/"
    "Microsoft.ContainerService/managedClusters/example"
)


def native(source, captured_at, payload, meta=None, key="input/1"):
    envelope = {
        "v": 1,
        "source": source,
        "capturedAt": captured_at,
        "seq": 0,
        "key": key,
        "payload": payload,
    }
    if meta is not None:
        envelope["meta"] = meta
    return envelope


def resource(resource_id=RESOURCE, name="example"):
    return {
        "id": resource_id,
        "name": name,
        "type": "Microsoft.ContainerService/managedClusters",
        "resourceGroup": "example",
        "subscriptionId": SUBSCRIPTION,
    }


def metric_payload(metric, at, aggregate, value, dimensions=(), unit="Percent"):
    return {
        "value": [{
            "name": {"value": metric},
            "unit": unit,
            "timeseries": [{
                "metadatavalues": [
                    {"name": {"value": name}, "value": dimension}
                    for name, dimension in dimensions
                ],
                "data": [{"timeStamp": at, aggregate: value}],
            }],
        }]
    }


def metric_capture(captured_at, payload, key="metric/1", resource_id=RESOURCE):
    return native("azure-metrics", captured_at, payload,
                  {"resourceId": resource_id}, key)


class HistoryTests(unittest.TestCase):
    def test_resource_updates_and_delete_are_bitemporal(self):
        updated = {**resource(), "name": "renamed"}
        deleted = native(
            "arg", "2026-01-01T00:02:00Z",
            {"properties": {"changeType": "Delete", "targetResourceId": RESOURCE.upper()}},
            {"query": "resourcechanges"}, "change/delete")
        with GqlHealthLab() as lab:
            lab.load_envelopes([
                native("arg", "2026-01-01T00:00:00Z", resource(),
                       {"query": "resources"}, "resource/1"),
                native("arg", "2026-01-01T00:01:00Z", updated,
                       {"query": "resources"}, "resource/update"),
                deleted,
            ])
            before = lab.project("2026-01-01T00:00:30Z")[0]
            after = lab.project("2026-01-01T00:01:30Z")[0]
            self.assertEqual(before["evidence"][0]["record_key"],
                             after["evidence"][0]["record_key"])
            self.assertEqual(json.loads(before["evidence"][0]["values_json"])["name"], "example")
            self.assertEqual(json.loads(after["evidence"][0]["values_json"])["name"], "renamed")
            self.assertEqual(lab.project("2026-01-01T00:02:00Z"), [])
            timeline = lab.timeline()
            self.assertEqual([row["captured_ns"] for row in timeline],
                             sorted(row["captured_ns"] for row in timeline))
            history = lab.query(
                "master",
                "MATCH (o:Observation {row_type: 'ResourceRow'}) "
                "RETURN o.operation AS operation, o.values_json AS values ORDER BY o.captured_ns")
            self.assertEqual([row["operation"] for row in history], ["upsert", "upsert", "delete"])

    def test_latest_metric_revision_does_not_leak_backwards(self):
        point = "2026-01-01T00:00:00Z"
        first = metric_payload("node_cpu_usage_percentage", point, "average", 12.5)
        revised = metric_payload("node_cpu_usage_percentage", point, "average", 97)
        with GqlHealthLab() as lab:
            lab.load_envelopes([
                native("arg", "2026-01-01T00:00:00Z", resource(),
                       {"query": "resources"}, "inventory"),
                metric_capture("2026-01-01T00:02:00Z", first, "revision/1"),
                metric_capture("2026-01-01T00:03:00Z", revised, "revision/2"),
            ])
            self.assertEqual(lab.project("2026-01-01T00:02:30Z")[0]["state"], "HEALTHY")
            self.assertEqual(lab.project("2026-01-01T00:03:00Z")[0]["state"], "CRITICAL")

    def test_gql_selects_latest_series_and_classifies_it(self):
        at = "2026-01-01T00:00:00Z"
        later = "2026-01-01T00:01:00Z"
        with GqlHealthLab() as lab:
            lab.load_envelopes([
                native("arg", at, resource(), {"query": "resources"}, "inventory"),
                metric_capture(
                    "2026-01-01T00:01:30Z",
                    metric_payload("node_cpu_usage_percentage", at, "average", 2),
                    "metric/early"),
                metric_capture(
                    "2026-01-01T00:02:00Z",
                    metric_payload("node_cpu_usage_percentage", later, "average", 90),
                    "metric/later"),
            ])
            query = (HERE / "projection.gql").read_text(encoding="utf-8").strip().rstrip(";")
            rows = lab.master.execute(query, {
                "as_of_ns": lab.latest_captured_ns,
                "stale_after_ns": 300_000_000_000,
                "warning_percent": 80.0,
                "critical_percent": 95.0,
            }).to_list()
            self.assertEqual(len(rows), 1)
            self.assertEqual(rows[0]["state"], "DEGRADED")
            self.assertEqual(rows[0]["point_evidence"]["event_at"], later)
            self.assertEqual(
                rows[0]["resource_evidence"]["subject_id"], RESOURCE.lower())
            self.assertNotIn("UNKNOWN_MISSING", [row["state"] for row in rows])

    def test_metric_join_cites_inventory_deletion_in_gql(self):
        point = "2026-01-01T00:00:00Z"
        deleted = native(
            "arg", "2026-01-01T00:02:00Z",
            {"properties": {"changeType": "Delete", "targetResourceId": RESOURCE}},
            {"query": "resourcechanges"}, "delete")
        with GqlHealthLab() as lab:
            lab.load_envelopes([
                native("arg", point, resource(), {"query": "resources"}, "inventory"),
                metric_capture(
                    "2026-01-01T00:01:00Z",
                    metric_payload("node_cpu_usage_percentage", point, "average", 0),
                    "metric"),
                deleted,
            ])
            assessment = lab.project("2026-01-01T00:02:00Z")[0]
            self.assertEqual(assessment["state"], "UNKNOWN_DELETED")
            self.assertEqual(
                [row["operation"] for row in assessment["evidence"]],
                ["upsert", "delete"])


class MetricPolicyTests(unittest.TestCase):
    def test_zero_missing_total_only_and_stale_are_distinct(self):
        total_id = RESOURCE + "-total"
        missing_id = RESOURCE + "-missing"
        at = "2026-01-01T00:00:00Z"
        events = [
            native("arg", at, resource(), {"query": "resources"}, "inventory/zero"),
            native("arg", at, resource(total_id, "total"), {"query": "resources"}, "inventory/total"),
            native("arg", at, resource(missing_id, "missing"),
                   {"query": "resources"}, "inventory/missing"),
            metric_capture("2026-01-01T00:02:00Z",
                           metric_payload("node_cpu_usage_percentage", at, "average", 0),
                           "metric/zero"),
            metric_capture("2026-01-01T00:02:00Z",
                           metric_payload("node_cpu_usage_percentage", at, "total", 0),
                           "metric/total", total_id),
            metric_capture("2026-01-01T00:02:00Z",
                           metric_payload("node_cpu_usage_percentage", at, "average", None),
                           "metric/missing", missing_id),
        ]
        with GqlHealthLab() as lab:
            lab.load_envelopes(events)
            now = lab.project("2026-01-01T00:02:00Z")
            states = {row["resource_id"]: row["state"] for row in now}
            self.assertEqual(states[RESOURCE.lower()], "HEALTHY")
            self.assertEqual(states[total_id.lower()], "UNKNOWN_UNSUPPORTED_AGGREGATION")
            self.assertEqual(states[missing_id.lower()], "UNKNOWN_MISSING")
            later = lab.project("2026-01-01T00:06:00Z")
            self.assertEqual(next(row for row in later if row["resource_id"] == missing_id.lower())["state"],
                             "UNKNOWN_MISSING")
            self.assertTrue(all(row["state"] == "UNKNOWN_STALE" for row in later
                                if row["resource_id"] != missing_id.lower()))
            no_metric = GqlHealthLab()
            try:
                no_metric.load_envelopes([
                    native("arg", at, resource(), {"query": "resources"}, "inventory/only")])
                missing = no_metric.project("2026-01-01T00:00:30Z")[0]
                self.assertEqual(missing["state"], "UNKNOWN_MISSING")
                self.assertEqual(missing["evidence_count"], 1)
            finally:
                no_metric.close()

    def test_dimensions_define_separate_time_series(self):
        at = "2026-01-01T00:00:00Z"
        payload = metric_payload(
            "node_cpu_usage_percentage", at, "average", 10,
            (("Node", "node-a"), ("Pool", "blue")))
        payload["value"][0]["timeseries"].append({
            "metadatavalues": [
                {"name": {"value": "Node"}, "value": "node-b"},
                {"name": {"value": "Pool"}, "value": "blue"},
            ],
            "data": [{"timeStamp": at, "average": 90}],
        })
        with GqlHealthLab() as lab:
            lab.load_envelopes([
                native("arg", at, resource(), {"query": "resources"}, "inventory"),
                metric_capture("2026-01-01T00:02:00Z", payload, "metric/dimensions"),
            ])
            assessments = lab.project("2026-01-01T00:02:00Z")
            self.assertEqual({row["state"] for row in assessments}, {"HEALTHY", "DEGRADED"})
            self.assertEqual(len({row["dimensions_json"] for row in assessments}), 2)
            self.assertTrue(all('"Node","node-' in row["dimensions_json"] for row in assessments))

    def test_future_arrival_and_future_event_time_are_hidden(self):
        event = "2026-01-01T00:05:00Z"
        with GqlHealthLab() as lab:
            lab.load_envelopes([
                native("arg", "2026-01-01T00:00:00Z", resource(),
                       {"query": "resources"}, "inventory"),
                metric_capture("2026-01-01T00:03:00Z",
                               metric_payload("node_cpu_usage_percentage", event, "average", 0),
                               "metric/future-event"),
            ])
            self.assertEqual(lab.project("2026-01-01T00:04:00Z")[0]["state"], "UNKNOWN_MISSING")
            self.assertEqual(lab.project("2026-01-01T00:05:00Z")[0]["state"], "HEALTHY")
            self.assertEqual(lab.project("2026-01-01T00:02:00Z")[0]["state"], "UNKNOWN_MISSING")


class EvidenceAndGqlTests(unittest.TestCase):
    def test_collector_heartbeat_does_not_claim_metric_availability(self):
        heartbeat = {
            "v": 1,
            "source": "records:MetricRow",
            "capturedAt": "2026-01-01T00:00:00Z",
            "key": "app-metrics/heartbeat/2026-01-01T00:00:00Z",
            "meta": {"source": "azure-monitor-hub:app-metrics"},
            "payload": {},
        }
        with GqlHealthLab() as lab:
            lab.load_envelopes([heartbeat])
            self.assertEqual(lab.project(), [])
            statuses = {row["kind"]: row for row in lab.health_view()["source_status"]}
            self.assertEqual(statuses["metrics"]["state"], "NO_INPUT")
            history = lab.query(
                "master", "MATCH (o:Observation) RETURN o.operation AS operation")
            self.assertEqual(history, [{"operation": "marker"}])

    def test_hubble_loss_is_not_evidence_of_a_flow(self):
        loss = native("hubble", "2026-01-01T00:00:00Z", {
            "lost_events": {"num_events_lost": 3},
            "node_name": "node-a",
            "time": "2026-01-01T00:00:00Z",
        }, key="hubble/loss")
        with GqlHealthLab() as lab:
            lab.load_envelopes([loss])
            lab.project()
            statuses = {row["kind"]: row for row in lab.health_view()["source_status"]}
            self.assertEqual(statuses["network_trace"]["state"], "NO_INPUT")
            self.assertEqual(statuses["network_completeness"]["state"], "LOSS_REPORTED")
            self.assertEqual(
                lab.query("master", "MATCH (r:RawCapture) RETURN r.category AS category"),
                [{"category": "network_loss"}])

    def test_raw_network_evidence_reaches_master_and_is_not_an_app_trace(self):
        nic = {
            "id": (f"/subscriptions/{SUBSCRIPTION}/resourceGroups/example/providers/"
                   "Microsoft.Network/networkInterfaces/nic"),
            "name": "nic",
            "type": "microsoft.network/networkinterfaces",
            "resourceGroup": "example",
            "subscriptionId": SUBSCRIPTION,
            "properties": {
                "ipConfigurations": [{
                    "properties": {
                        "privateIPAddress": "10.2.0.4",
                        "subnet": {"id": "/subscriptions/example/subnets/default"},
                    }
                }]
            },
        }
        flow = native("hubble", "2026-01-01T00:00:02Z", {
            "flow": {
                "uuid": "flow/1",
                "time": "2026-01-01T00:00:01.123456789Z",
                "verdict": "FORWARDED",
                "source": {"cluster_name": "example", "namespace": "demo", "pod_name": "client"},
                "destination": {"cluster_name": "example", "namespace": "demo", "pod_name": "server"},
            }
        }, key="hubble/1")
        vnet = native("vnet-flow-log", "2026-01-01T00:00:03Z", {
            "time": "2026-01-01T00:00:00Z",
            "flowLogGUID": "raw-vnet-guid",
            "flowRecords": {"flows": [{"flowGroups": []}]},
        }, key="vnet/1")
        with GqlHealthLab() as lab:
            lab.load_envelopes([
                native("arg", "2026-01-01T00:00:00Z", resource(),
                       {"query": "resources"}, "inventory"),
                native("arg", "2026-01-01T00:00:00Z", nic,
                       {"query": "resources"}, "inventory/nic"),
                flow, vnet,
            ])
            lab.project("2026-01-01T00:00:03Z")
            raw = lab.query(
                "master",
                "MATCH (r:RawCapture) WHERE r.category = 'network_trace' "
                "RETURN r.source AS source, r.raw_json AS raw ORDER BY source")
            self.assertEqual({row["source"] for row in raw}, {"hubble", "vnet-flow-log"})
            self.assertTrue(any('"flowLogGUID"' in row["raw"] for row in raw))
            statuses = {row["kind"]: row for row in lab.health_view()["source_status"]}
            self.assertEqual(statuses["application_trace"]["state"], "NOT_PROVIDED")
            self.assertEqual(statuses["network_trace"]["state"], "AVAILABLE")
            self.assertEqual(statuses["network_completeness"]["state"], "NO_EXPLICIT_LOSS_MARKER")
            self.assertTrue(any(
                fact["target"] == "10.2.0.4"
                for fact in lab.master_summary()["network_facts"]))
            master_relations = lab.master_summary()["relationships"]
            self.assertTrue(any(
                relation["relation"] == "networkFlow"
                for relation in master_relations))
            self.assertTrue(any(
                relation["relation"] == "ip"
                and relation["target_key"] == "10.2.0.4"
                for relation in master_relations))
            health_relations = lab.health_view()["topology"]
            self.assertTrue(any(
                relation["relation"] == "ip"
                and relation["target_key"] == "10.2.0.4"
                for relation in health_relations))
            edge = lab.query(
                "master",
                "MATCH (o:Observation)-[:DERIVED_FROM]->(r:RawCapture) "
                "WHERE o.row_type = 'FlowRow' RETURN count(o) AS linked")
            self.assertEqual(edge[0]["linked"], 1)

    def test_actual_gql_execution_and_read_only_shell(self):
        artifact = HERE / ".test-artifacts" / "input.ndjson"
        artifact.parent.mkdir(parents=True, exist_ok=True)
        event = native("arg", "2026-01-01T00:00:00Z", resource(),
                       {"query": "resources"}, "inventory")
        artifact.write_text(json.dumps(event) + "\n", encoding="utf-8")
        before = artifact.read_bytes()
        try:
            with GqlHealthLab() as lab:
                lab.load_path(artifact)
                lab.project()
                rows = lab.query("master", "MATCH (o:Observation) RETURN count(o) AS count")
                self.assertEqual(rows, [{"count": 1}])
                raw = lab.query(
                    "master", "MATCH (r:RawCapture) RETURN r.raw_json AS raw")
                self.assertEqual(raw, [{"raw": artifact.read_text(encoding="utf-8").rstrip("\n")}])
                terminal = Terminal(lab)
                with redirect_stdout(io.StringIO()):
                    terminal.command("use health")
                    terminal.command("gql MATCH (a:HealthAssessment) RETURN count(a) AS count")
                    with self.assertRaises(LabError):
                        terminal.command("gql INSERT (:HealthAssessment {id: 'bad'})")
                self.assertEqual(artifact.read_bytes(), before)
        finally:
            shutil.rmtree(artifact.parent, ignore_errors=True)


class CollectorEnvelopeTests(unittest.TestCase):
    def test_actual_scrubbed_collector_recordings(self):
        with GqlHealthLab() as lab:
            records = lab.load_collector_replay(ROOT / "lab/collector/recordings")
            kinds = Counter(record["source"] for record in records)
            self.assertEqual(kinds, Counter({
                "records:RequestRow": 11,
                "records:SampleRow": 91,
                "records:UsageRow": 24,
                "records:MetricRow": 80,
            }))
            assessments = lab.project()
            self.assertTrue(assessments)
            self.assertFalse(any(row["state"] == "HEALTHY" for row in assessments))
            summary = lab.master_summary()
            self.assertEqual(summary["observations"], 206)
            sample = lab.query(
                "master",
                "MATCH (o:Observation) WHERE o.row_type = 'MetricRow' "
                "RETURN o.record_key AS key, o.values_json AS values LIMIT 1")
            self.assertIn('"at"', sample[0]["values"])


if __name__ == "__main__":
    unittest.main()
