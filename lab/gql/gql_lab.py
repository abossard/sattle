#!/usr/bin/env python3
"""In-memory GQL health projection for native and collector captures."""
from collections import Counter
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
import hashlib
import importlib
from importlib.metadata import version
import json
import math
from pathlib import Path
import re
import sys


HERE = Path(__file__).resolve().parent
COLLECTOR = HERE.parent / "collector"
MASTER_GRAPH = "azure_master"
HEALTH_GRAPH = "health"
STALE_AFTER_NS = 300 * 1_000_000_000
WARNING_PERCENT = 80.0
CRITICAL_PERCENT = 95.0
INSTANT = re.compile(
    r"(?P<date>\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)"
    r"(?:\.(?P<fraction>\d{1,9}))?(?P<zone>Z|[+-]\d\d:\d\d)"
)


class LabError(RuntimeError):
    pass


@dataclass(frozen=True)
class InputEntry:
    path: str
    line: int
    envelope: dict
    captured_ns: int
    raw_text: str


def compact(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False)


def instant_ns(value):
    if not isinstance(value, str):
        raise LabError("time must be an ISO 8601 string")
    match = INSTANT.fullmatch(value)
    if not match:
        raise LabError(f"unsupported instant: {value!r}")
    zone = match.group("zone")
    offset = timezone.utc
    if zone != "Z":
        sign = 1 if zone[0] == "+" else -1
        offset = timezone(sign * timedelta(hours=int(zone[1:3]), minutes=int(zone[4:6])))
    try:
        at = datetime.strptime(match.group("date"), "%Y-%m-%dT%H:%M:%S").replace(tzinfo=offset)
    except ValueError as exc:
        raise LabError(f"invalid instant: {value!r}") from exc
    whole = int((at.astimezone(timezone.utc) - datetime(1970, 1, 1, tzinfo=timezone.utc)).total_seconds())
    fraction = int((match.group("fraction") or "").ljust(9, "0"))
    return whole * 1_000_000_000 + fraction


def instant_text(value_ns):
    whole, fraction = divmod(value_ns, 1_000_000_000)
    base = datetime.fromtimestamp(whole, timezone.utc).strftime("%Y-%m-%dT%H:%M:%S")
    return f"{base}.{fraction:09d}Z" if fraction else base + "Z"


def digest(value):
    return hashlib.sha256(value.encode("utf-8")).hexdigest()[:24]


def _collector_modules():
    path = str(COLLECTOR)
    sys.path.insert(0, path)
    try:
        return importlib.import_module("capture"), importlib.import_module("collect")
    finally:
        sys.path.pop(0)


def _statements(path):
    source = path.read_text(encoding="utf-8")
    return [statement.strip() + ";" for statement in source.split(";") if statement.strip()]


def _load_json(raw, where):
    def invalid(constant):
        raise ValueError(f"invalid JSON number {constant}")

    try:
        value = json.loads(raw, parse_constant=invalid)
    except (json.JSONDecodeError, ValueError) as exc:
        raise LabError(f"{where}: invalid JSON: {exc}") from exc
    if not isinstance(value, dict):
        raise LabError(f"{where}: envelope is not an object")
    return value


def _number(value):
    return value if isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) else None


def _dimensions_json(pairs):
    return compact([[name, value] for name, value in sorted(pairs)])


def _key_dimensions(key):
    if not isinstance(key, str) or "[" not in key:
        return None
    start = key.find("[")
    try:
        parts = json.loads(key[start:])
    except json.JSONDecodeError:
        return None
    if not isinstance(parts, list):
        return None
    for item in parts:
        if (isinstance(item, list)
                and all(isinstance(pair, list) and len(pair) == 2 for pair in item)):
            return _dimensions_json(item)
    return None


def native_metric_facts(envelope):
    facts = {}
    resource = (envelope.get("meta") or {}).get("resourceId")
    if envelope.get("source") != "azure-metrics" or not isinstance(resource, str):
        return facts
    payload = envelope.get("payload")
    metrics = payload.get("value", []) if isinstance(payload, dict) else []
    if not isinstance(metrics, list):
        return facts
    for metric in metrics:
        if not isinstance(metric, dict):
            continue
        name = metric.get("name", {}).get("value")
        unit = metric.get("unit") or ""
        if not isinstance(name, str):
            continue
        series_list = metric.get("timeseries", [])
        if not isinstance(series_list, list):
            continue
        for series in series_list:
            if not isinstance(series, dict):
                continue
            metadata = series.get("metadatavalues", [])
            if not isinstance(metadata, list):
                metadata = []
            dimension_pairs = []
            values = []
            for item in metadata:
                if isinstance(item, dict):
                    dim_name = (item.get("name") or {}).get("value")
                    dim_value = item.get("value")
                    values.append(str(dim_value))
                    if isinstance(dim_name, str):
                        dimension_pairs.append((dim_name, dim_value))
            dimensions = ",".join(values)
            for point in series.get("data", []):
                if not isinstance(point, dict) or not isinstance(point.get("timeStamp"), str):
                    continue
                if point.get("average") is not None:
                    aggregation, value = "average", point["average"]
                elif point.get("total") is not None:
                    aggregation, value = "total", point["total"]
                else:
                    continue
                key = (f"ResourceMetricRow/{resource.lower()}/{name}/{dimensions}/"
                       f"{point['timeStamp']}")
                facts[key] = {
                    "aggregation": aggregation,
                    "unit": unit if isinstance(unit, str) else "",
                    "dimensions_json": _dimensions_json(dimension_pairs),
                    "numeric": _number(value),
                }
    return facts


def raw_category(envelope):
    source = envelope.get("source") or ""
    payload = envelope.get("payload")
    if source in ("arg", "kubernetes-list", "kubernetes-watch"):
        return "inventory"
    if source == "azure-metrics" or source == "prometheus":
        return "metric"
    if source == "hubble" and isinstance(payload, dict) and "lost_events" in payload:
        return "network_loss"
    if source == "hubble" and isinstance(payload, dict) and isinstance(payload.get("flow"), dict):
        return "network_trace"
    if source == "vnet-flow-log":
        return "network_trace"
    if (source == "sattle-forwarder" and isinstance(payload, dict)
            and payload.get("event") in ("dropped", "hubble-reconnect")):
        return "capture_loss"
    if source.startswith("records:"):
        if isinstance(payload, dict) and payload == {}:
            return "source_marker"
        row_type = source.partition(":")[2]
        if row_type in ("MetricRow", "ResourceMetricRow", "SampleRow", "UsageRow"):
            return "metric"
        if row_type == "FlowRow" and isinstance(payload, dict) and "lost" not in payload:
            return "network_trace"
        if row_type == "FlowRow":
            return "network_loss"
        if row_type == "AzureNetworkRow":
            return "network_trace"
        if row_type == "RequestRow":
            return "application_request"
        return "record"
    return "raw_source"


def raw_network_trace(envelope):
    payload = envelope.get("payload")
    source = envelope.get("source")
    key = str(envelope.get("key") or "")
    if source == "hubble" and isinstance(payload, dict) and isinstance(payload.get("flow"), dict):
        flow = payload["flow"]
        at = flow.get("time") or payload.get("time")
        verdict = flow.get("verdict") or ""
        return {"trace_key": key, "trace_at": at if isinstance(at, str) else "", "verdict": str(verdict)}
    if source == "vnet-flow-log" and isinstance(payload, dict):
        at = payload.get("time")
        return {
            "trace_key": str(payload.get("flowLogGUID") or key),
            "trace_at": at if isinstance(at, str) else "",
            "verdict": "raw-flow-tuples",
        }
    return None


class GqlHealthLab:
    def __init__(self, database_path=None):
        try:
            import grafeo
        except ImportError as exc:
            raise LabError("Grafeo is not installed; use lab/gql/requirements.txt in an external venv") from exc
        for package in ("grafeo", "solvor"):
            installed = version(package)
            if installed != "0.6.3":
                raise LabError(f"{package}==0.6.3 is required, found {installed}")
        self.database_path = Path(database_path).expanduser().resolve() if database_path else None
        if self.database_path:
            self.database_path.parent.mkdir(parents=True, exist_ok=True)
        self.db = grafeo.GrafeoDB(str(self.database_path)) if self.database_path else grafeo.GrafeoDB()
        existing = {MASTER_GRAPH, HEALTH_GRAPH}.intersection(self.db.list_graphs())
        if existing:
            self.db.close()
            raise LabError(
                f"refusing to overwrite an existing experimental graph: {', '.join(sorted(existing))}")
        self.master = self._new_graph(MASTER_GRAPH)
        self.health = self._new_graph(HEALTH_GRAPH)
        self._deploy()
        self._raw_count = 0
        self._observation_count = 0
        self._latest_captured_ns = None
        self._earliest_captured_ns = None
        self._master_entities = {}
        self.current_as_of_ns = None

    def _new_graph(self, name):
        self.db.create_graph(name)
        return self.db.graph(name)

    def close(self):
        self.db.close()

    def __enter__(self):
        return self

    def __exit__(self, _type, _value, _traceback):
        self.close()

    def _execute_ddl(self, graph, statements):
        for statement in statements:
            try:
                graph.execute(statement)
            except Exception as exc:
                if "Type already exists" not in str(exc):
                    raise LabError(f"cannot deploy GQL ontology: {statement}\n{exc}") from exc

    def _deploy(self):
        self._execute_ddl(self.master, _statements(HERE / "azure_ontology.gql"))
        self._execute_ddl(self.health, _statements(HERE / "health_ontology.gql"))

    @staticmethod
    def _entity(kind, key, display_name=""):
        key = str(key or "")
        return {
            "id": digest(f"entity|{kind}|{key}"),
            "entity_key": key,
            "kind": kind,
            "display_name": str(display_name or key),
        }

    def _ensure_master_entity(self, entity):
        if entity["id"] in self._master_entities:
            return entity
        self.master.execute(
            """INSERT (:MasterEntity {
                id: $id, entity_key: $entity_key, kind: $kind, display_name: $display_name
            })""",
            entity)
        self._master_entities[entity["id"]] = entity
        return entity

    @staticmethod
    def _kubernetes_entity(cluster, namespace, kind, name):
        key = ":".join(str(value or "") for value in (cluster, namespace, kind, name))
        return GqlHealthLab._entity("kubernetes-" + str(kind).lower(), "kubernetes|" + key, name)

    @staticmethod
    def _resource_entity(resource_id, display_name=""):
        return GqlHealthLab._entity(
            "azure-resource", str(resource_id or "").lower(), display_name or resource_id)

    def _topology(self, row_type, values, record_key, metric, operation):
        subject = None
        role = None
        relations = []
        cluster = values.get("cluster")
        namespace = values.get("namespace")

        if row_type == "ResourceRow":
            rid = values.get("id") or record_key.removeprefix("ResourceRow/")
            subject = self._resource_entity(rid, values.get("name"))
            role = "describe"
            for field, relation in (("parentId", "resourceParent"), ("linkedId", "resourceLink")):
                target_id = values.get(field)
                if target_id:
                    relations.append((self._resource_entity(target_id), relation, ""))
        elif row_type in ("ResourceMetricRow", "MetricRow", "SampleRow", "UsageRow"):
            if metric["resource_id"] and row_type != "SampleRow":
                subject = self._resource_entity(
                    metric["resource_id"],
                    values.get("resourceId") or metric["resource_id"])
            elif row_type == "SampleRow":
                subject = self._entity(
                    "prometheus-series",
                    "prometheus|" + metric["dimensions_json"],
                    str(values.get("pod") or values.get("namespace") or "series"))
            elif row_type == "UsageRow" and values.get("kind"):
                subject = self._kubernetes_entity(
                    "", values.get("namespace") or "", values.get("kind"),
                    values.get("name"))
            role = "measure" if operation == "upsert" else None
        elif row_type in ("PodRow", "DeploymentRow", "ServiceRow", "NodeRow"):
            kind = row_type.removesuffix("Row")
            subject = self._kubernetes_entity(cluster, namespace, kind, values.get("name"))
            role = "describe"
        elif row_type == "AzureNetworkRow" and operation == "upsert":
            resource = values.get("resourceId")
            target = values.get("target")
            fact = values.get("kind") or "networkFact"
            subject = self._resource_entity(resource)
            target_kind = (
                "azure-resource" if isinstance(target, str) and target.startswith("/") else
                "dns-name" if fact == "dnsName" else
                "ip-address" if fact == "ip" else
                "network-target")
            relations.append((self._entity(target_kind, target, target), fact, ""))
            role = "describe"
        elif row_type == "KubernetesLinkRow" and operation == "upsert":
            relation = values.get("kind") or "kubernetesLink"
            source_kind = {
                "podOwner": "Pod",
                "replicaSet": "ReplicaSet",
                "azureHost": "Node",
                "namespace": "Namespace",
            }.get(relation, "Object")
            subject = self._kubernetes_entity(
                cluster, namespace, source_kind, values.get("name"))
            target = values.get("target")
            if target:
                if relation == "azureHost":
                    target_entity = self._resource_entity(target)
                else:
                    target_kind = "ReplicaSet" if relation == "podOwner" else "Deployment"
                    target_entity = self._kubernetes_entity(
                        cluster, namespace, target_kind, target)
                relations.append((target_entity, relation, ""))
            role = "describe"
        elif row_type == "SelectsRow" and operation == "upsert":
            subject = self._kubernetes_entity(
                cluster, namespace, "Service", values.get("service"))
            target = self._kubernetes_entity(
                cluster, namespace, "Deployment", values.get("deployment"))
            relations.append((target, "selects", ""))
            role = "describe"
        elif row_type == "EndpointRow" and operation == "upsert":
            subject = self._kubernetes_entity(
                cluster, namespace, "Service", values.get("service"))
            if values.get("podUid"):
                target = self._entity(
                    "kubernetes-uid", "kubernetes-uid|" + str(values["podUid"]), values["podUid"])
            else:
                target = self._entity(
                    "ip-address", "ip|" + str(values.get("address") or ""), values.get("address"))
            relations.append((target, "endpoint", ""))
            role = "describe"
        elif row_type == "FlowRow" and operation == "upsert":
            source = (self._kubernetes_entity(
                cluster, values.get("srcNamespace"), "Pod", values.get("srcPod"))
                if values.get("srcPod") else None)
            if values.get("dstPod"):
                target = self._kubernetes_entity(
                    cluster, values.get("dstNamespace"), "Pod", values.get("dstPod"))
            elif values.get("dstName"):
                target = self._entity(
                    "dns-name", str(values["dstName"]).lower(), values["dstName"])
            elif values.get("dstAddress"):
                target = self._entity(
                    "ip-address", str(values["dstAddress"]), values["dstAddress"])
            else:
                target = None
            if source and target:
                subject = source
                relations.append((target, "networkFlow", values.get("verdict") or ""))
            role = "describe" if subject else None

        if not subject or not subject["entity_key"]:
            subject = None
            role = None
        return subject, role, relations

    def _attach_master_topology(
            self, observation_id, record_key, captured_ns, event_ns, subject, role, relations):
        if subject is not None and role:
            self._ensure_master_entity(subject)
            edge = "MEASURES_ENTITY" if role == "measure" else "DESCRIBES_ENTITY"
            self.master.execute(
                f"""MATCH (o:Observation {{id: $observation_id}}),
                         (e:MasterEntity {{id: $entity_id}})
                   INSERT (o)-[:{edge} {{observation_id: $observation_id}}]->(e)""",
                {"observation_id": observation_id, "entity_id": subject["id"]})
        for target, relation, verdict in relations:
            if subject is None or not target["entity_key"]:
                continue
            self._ensure_master_entity(subject)
            self._ensure_master_entity(target)
            self.master.execute(
                """MATCH (source:MasterEntity {id: $source_id}),
                         (target:MasterEntity {id: $target_id})
                   INSERT (source)-[:OBSERVED_RELATION {
                     observation_id: $observation_id, record_key: $record_key,
                     relation: $relation, verdict: $verdict,
                     captured_ns: $captured_ns, event_ns: $event_ns
                   }]->(target)""",
                {
                    "source_id": subject["id"],
                    "target_id": target["id"],
                    "observation_id": observation_id,
                    "record_key": record_key,
                    "relation": relation,
                    "verdict": verdict,
                    "captured_ns": captured_ns,
                    "event_ns": event_ns,
                })

    def load_path(self, path):
        path = Path(path)
        if not path.exists():
            raise LabError(f"input does not exist: {path}")
        files = [path] if path.is_file() else sorted(path.rglob("*.ndjson"))
        if not files:
            raise LabError(f"no .ndjson inputs below {path}")
        entries = []
        for file in files:
            relative = file.name if path.is_file() else str(file.relative_to(path))
            for line_number, raw in enumerate(file.read_text(encoding="utf-8").splitlines(), 1):
                if raw.strip():
                    entries.append((relative, line_number, _load_json(raw, f"{file}:{line_number}"), raw))
        self.load_envelopes(entries, paths=True)

    def load_collector_replay(self, recordings, streams_path=None):
        _capture, collect = _collector_modules()
        recordings = Path(recordings)
        if not recordings.is_dir():
            raise LabError(f"collector recordings directory does not exist: {recordings}")
        streams_path = Path(streams_path) if streams_path else COLLECTOR / "streams.json"
        streams = json.loads(streams_path.read_text(encoding="utf-8"))
        if not isinstance(streams, list):
            raise LabError("collector streams must be an array")
        cursors = {stream["name"]: collect.Cursor(None, {}) for stream in streams}
        output = []
        for stream, captured_at, get in collect.replay_ticks(streams, recordings):
            fresh, cursors[stream["name"]] = collect.poll(
                stream, cursors[stream["name"]], captured_at, get((None, None)))
            output.extend(fresh)
        self.load_envelopes([(f"collector-replay:{index}", index, env)
                             for index, env in enumerate(output, 1)], paths=True)
        return output

    def load_envelopes(self, envelopes, paths=False):
        prepared = []
        for index, item in enumerate(envelopes, 1):
            if paths:
                if len(item) == 4:
                    name, line, envelope, raw_text = item
                else:
                    name, line, envelope = item
                    raw_text = compact(envelope)
            else:
                name, line, envelope = "memory", index, item
                raw_text = compact(envelope)
            if not isinstance(envelope, dict):
                raise LabError(f"{name}:{line}: envelope is not an object")
            try:
                captured_ns = instant_ns(envelope.get("capturedAt"))
            except LabError as exc:
                raise LabError(f"{name}:{line}: {exc}") from exc
            if envelope.get("v") != 1 or not isinstance(envelope.get("source"), str):
                raise LabError(f"{name}:{line}: expected a v1 envelope with a source")
            prepared.append(InputEntry(str(name), int(line), envelope, captured_ns, raw_text))
        prepared.sort(key=lambda entry: (
            entry.captured_ns, entry.path,
            entry.envelope.get("seq", 0) if isinstance(entry.envelope.get("seq"), int) else 0,
            entry.line))
        native = [entry for entry in prepared if not str(entry.envelope["source"]).startswith("records:")]
        capture, _collect = _collector_modules()
        converted = {}
        if native:
            try:
                for entry, (native_envelope, records) in zip(
                        native, capture.convert(entry.envelope for entry in native)):
                    if native_envelope is not entry.envelope:
                        raise LabError("collector normalizer changed envelope identity")
                    converted[id(entry)] = records
            except LabError:
                raise
            except Exception as exc:
                raise LabError(f"native capture normalization failed: {exc}") from exc
        for entry in prepared:
            records = ([entry.envelope] if str(entry.envelope["source"]).startswith("records:")
                       else converted.get(id(entry), []))
            self._ingest_entry(entry, records)

    def _ingest_entry(self, entry, records):
        envelope = entry.envelope
        raw_id = digest(
            f"{entry.path}:{entry.line}:{entry.captured_ns}:{entry.envelope.get('key', '')}:{entry.raw_text}")
        category = raw_category(envelope)
        raw_trace = raw_network_trace(envelope)
        raw_query = "INSERT (:RawCapture"
        if raw_trace:
            raw_query += ":NetworkTrace"
        raw_query += """ {
            id: $id, input_key: $input_key, source: $source, category: $category,
            captured_at: $captured_at, captured_ns: $captured_ns, ordinal: $ordinal,
            raw_json: $raw_json"""
        params = {
            "id": raw_id,
            "input_key": str(envelope.get("key") or ""),
            "source": str(envelope["source"]),
            "category": category,
            "captured_at": str(envelope["capturedAt"]),
            "captured_ns": entry.captured_ns,
            "ordinal": entry.line,
            "raw_json": entry.raw_text,
        }
        if raw_trace:
            raw_query += ", trace_key: $trace_key, trace_at: $trace_at, verdict: $verdict"
            params.update(raw_trace)
        raw_query += "})"
        self.master.execute(raw_query, params)
        self._raw_count += 1
        self._earliest_captured_ns = (
            entry.captured_ns if self._earliest_captured_ns is None
            else min(self._earliest_captured_ns, entry.captured_ns))
        self._latest_captured_ns = (
            entry.captured_ns if self._latest_captured_ns is None
            else max(self._latest_captured_ns, entry.captured_ns))
        facts = native_metric_facts(envelope)
        for record_index, record in enumerate(records):
            self._insert_observation(entry, raw_id, record_index, record, facts)

    def _insert_observation(self, entry, raw_id, record_index, record, metric_facts):
        source = record.get("source")
        if not isinstance(source, str) or not source.startswith("records:"):
            raise LabError(f"normalizer emitted an invalid record from {entry.path}:{entry.line}")
        row_type = source.partition(":")[2]
        payload = record.get("payload")
        if not isinstance(payload, dict):
            raise LabError(f"{source} from {entry.path}:{entry.line} has no object payload")
        operation = (
            "delete" if payload.get("deleted") is True else
            "loss" if row_type == "FlowRow" and "lost" in payload else
            "marker" if payload == {} else
            "upsert"
        )
        values = payload.get("values")
        if operation == "marker":
            values = payload
        elif operation == "loss":
            values = payload
        if operation == "upsert" and not isinstance(values, dict):
            raise LabError(f"{source} from {entry.path}:{entry.line} has no values object")
        values = {} if operation == "delete" else values
        record_key = str(payload.get("key") or record.get("key") or "")
        event_at = self._event_at(row_type, values, record.get("capturedAt"))
        event_ns = instant_ns(event_at)
        fact = metric_facts.get(record.get("key"), {})
        metric = self._metric_context(row_type, values, record_key, fact)
        subject, entity_role, relations = self._topology(
            row_type, values, record_key, metric, operation)
        observation_id = digest(f"{raw_id}:{record_index}:{row_type}:{record_key}:{operation}")
        labels = ["Observation"]
        extra = {}
        if row_type == "ResourceRow" and operation == "upsert":
            labels.append("InventoryVersion")
            extra = {
                "resource_id": str(values.get("id") or values.get("resourceId") or record_key).lower(),
                "name": str(values.get("name") or ""),
                "resource_type": str(values.get("type") or ""),
            }
        elif (row_type in ("ResourceMetricRow", "MetricRow", "SampleRow")
              and operation == "upsert"):
            labels.append("MetricPoint")
            extra = {
                "resource_id": metric["resource_id"],
                "metric": metric["metric"],
                "numeric_value": metric["numeric"] if metric["has_numeric"] else 0.0,
                "has_numeric": metric["has_numeric"],
            }
        elif row_type == "FlowRow" and operation == "upsert":
            labels.append("NetworkTrace")
            extra = {
                "trace_key": record_key,
                "trace_at": event_at,
                "verdict": str(values.get("verdict") or ""),
            }
        elif row_type == "AzureNetworkRow" and operation == "upsert":
            labels.append("NetworkFact")
            extra = {
                "resource_id": str(values.get("resourceId") or "").lower(),
                "fact_kind": str(values.get("kind") or ""),
                "target": str(values.get("target") or ""),
            }
        elif row_type == "RequestRow" and operation == "upsert":
            labels.append("ApplicationObservation")
            extra = {
                "observation_name": str(values.get("Name") or values.get("name") or ""),
                "result": str(values.get("ResultCode") or values.get("resultCode") or ""),
            }
        common = {
            "id": observation_id,
            "raw_capture_id": raw_id,
            "row_type": row_type,
            "record_key": record_key,
            "operation": operation,
            "subject_id": subject["entity_key"] if subject else "",
            "source": source,
            "captured_at": str(record.get("capturedAt") or entry.envelope["capturedAt"]),
            "captured_ns": instant_ns(str(record.get("capturedAt") or entry.envelope["capturedAt"])),
            "event_at": event_at,
            "event_ns": event_ns,
            "ordinal": self._observation_count,
            "values_json": compact(values if operation == "upsert" else payload),
            "series_key": metric["series_key"],
            "dimensions_json": metric["dimensions_json"],
            "metric_fold": metric["metric"].casefold(),
            "aggregation": metric["aggregation"],
            "unit": metric["unit"],
            "availability": metric["availability"],
        }
        params = {**common, **extra}
        props = list(common) + list(extra)
        query = "INSERT (" + "".join(":" + label for label in labels) + " {" + ", ".join(
            f"{name}: ${name}" for name in props) + "})"
        self.master.execute(query, params)
        self.master.execute(
            """MATCH (o:Observation {id: $observation_id}), (r:RawCapture {id: $raw_capture_id})
               INSERT (o)-[:DERIVED_FROM {normalizer: $normalizer}]->(r)""",
            {"observation_id": observation_id, "raw_capture_id": raw_id,
             "normalizer": "collector.capture" if not str(entry.envelope["source"]).startswith("records:")
                            else "collector-record"})
        self._attach_master_topology(
            observation_id,
            record_key,
            common["captured_ns"],
            event_ns,
            subject,
            entity_role,
            relations,
        )
        self._observation_count += 1

    @staticmethod
    def _event_at(row_type, values, captured_at):
        field = {
            "ResourceMetricRow": "time",
            "MetricRow": "at",
            "SampleRow": "at",
            "UsageRow": "at",
            "FlowRow": "time",
            "RequestRow": "TimeGenerated",
        }.get(row_type)
        candidate = values.get(field) if field and isinstance(values, dict) else None
        result = candidate if isinstance(candidate, str) else captured_at
        if not isinstance(result, str):
            raise LabError(f"{row_type} has no usable event or capture time")
        return result

    @staticmethod
    def _metric_context(row_type, values, record_key, fact):
        if row_type not in ("ResourceMetricRow", "MetricRow", "SampleRow"):
            return {
                "resource_id": "", "metric": "", "series_key": "", "dimensions_json": "[]",
                "aggregation": "", "unit": "", "numeric": None, "has_numeric": False,
                "availability": "",
            }
        metric = str(values.get("metric") or "")
        resource = str(values.get("resourceId") or "")
        aggregation = fact.get("aggregation") or ""
        unit = fact.get("unit") or ""
        dimensions_json = fact.get("dimensions_json") or "[]"
        numeric = fact.get("numeric")
        if row_type == "MetricRow":
            dimensions_json = _key_dimensions(record_key) or "[]"
            if values.get("average") is not None:
                aggregation = "average"
                numeric = _number(values.get("average"))
            elif values.get("total") is not None:
                aggregation = "total"
                numeric = _number(values.get("total"))
        elif row_type == "SampleRow":
            namespace = values.get("namespace")
            pod = values.get("pod")
            dimensions_json = _dimensions_json(
                [(key, values[key]) for key in ("namespace", "pod") if values.get(key) is not None])
            resource = "/".join(str(x) for x in (namespace, pod) if x is not None)
            aggregation = "sample"
            numeric = _number(values.get("value"))
        elif row_type == "ResourceMetricRow":
            numeric = numeric if numeric is not None else _number(values.get("value"))
            if not aggregation:
                aggregation = "unavailable"
        series_key = "|".join((resource.lower(), metric, dimensions_json))
        if row_type == "ResourceMetricRow":
            availability = (
                "threshold_candidate" if aggregation == "average" and unit.casefold() in {
                    "%", "percent", "percentage"} else
                "aggregation_unsupported" if aggregation != "average" else
                "unit_unverified")
        elif row_type == "MetricRow":
            availability = "unit_not_retained"
        else:
            availability = "rule_not_defined"
        return {
            "resource_id": resource.lower(),
            "metric": metric,
            "series_key": series_key,
            "dimensions_json": dimensions_json,
            "aggregation": aggregation,
            "unit": unit,
            "numeric": numeric,
            "has_numeric": numeric is not None,
            "availability": availability,
        }

    @property
    def latest_captured_ns(self):
        return self._latest_captured_ns

    @property
    def earliest_captured_ns(self):
        return self._earliest_captured_ns

    @property
    def raw_count(self):
        return self._raw_count

    def timeline(self):
        return self.master.execute(
            """MATCH (r:RawCapture)
               RETURN r.captured_at AS captured_at, r.captured_ns AS captured_ns,
                      r.source AS source, r.category AS category, count(r) AS arrivals
               ORDER BY r.captured_ns, r.source""").to_list()

    def master_summary(self):
        categories = self.master.execute(
            """MATCH (r:RawCapture)
               RETURN r.category AS category, count(r) AS arrivals
               ORDER BY category""").to_list()
        traces = self.master.execute(
            """MATCH (t:NetworkTrace)
               RETURN labels(t) AS labels, t.trace_at AS trace_at,
                      t.trace_key AS trace_key, t.verdict AS verdict
               ORDER BY trace_at""").to_list()
        network_facts = self.master.execute(
            """MATCH (f:NetworkFact)
               RETURN f.resource_id AS resource_id, f.fact_kind AS fact_kind,
                      f.target AS target
               ORDER BY resource_id, fact_kind, target""").to_list()
        relationships = self.master.execute(
            """MATCH (source:MasterEntity)-[r:OBSERVED_RELATION]->(target:MasterEntity)
               RETURN source.entity_key AS source_key, source.kind AS source_kind,
                      r.relation AS relation, r.verdict AS verdict,
                      target.entity_key AS target_key, target.kind AS target_kind,
                      r.observation_id AS observation_id
               ORDER BY source_key, relation, target_key""").to_list()
        observations = self.master.execute(
            "MATCH (o:Observation) RETURN count(o) AS observations").scalar()
        return {
            "categories": categories,
            "network_traces": traces,
            "network_facts": network_facts,
            "relationships": relationships,
            "observations": observations,
        }

    def query(self, graph, query, params=None):
        if graph not in ("master", "health"):
            raise LabError("graph must be master or health")
        name = MASTER_GRAPH if graph == "master" else HEALTH_GRAPH
        self.db.set_graph(name)
        try:
            with self.db.session(read_only=True, external_access=False) as session:
                return session.execute(query, params).to_list()
        except Exception as exc:
            raise LabError(f"GQL query failed: {exc}") from exc
        finally:
            self.db.reset_graph()

    def _selected_as_of(self, point):
        if point is None:
            if self._latest_captured_ns is None:
                raise LabError("cannot project: no captures loaded")
            return self._latest_captured_ns, instant_text(self._latest_captured_ns)
        if isinstance(point, int):
            return point, instant_text(point)
        ns = instant_ns(point)
        return ns, point

    def project(self, point=None):
        as_of_ns, as_of = self._selected_as_of(point)
        params = {
            "as_of_ns": as_of_ns,
            "stale_after_ns": STALE_AFTER_NS,
            "warning_percent": WARNING_PERCENT,
            "critical_percent": CRITICAL_PERCENT,
        }
        projection = (HERE / "projection.gql").read_text(encoding="utf-8").strip().rstrip(";")
        topology = (HERE / "topology.gql").read_text(encoding="utf-8").strip().rstrip(";")
        projected = self.master.execute(projection, params).to_list()
        active_topology = self.master.execute(
            topology, {"as_of_ns": as_of_ns}).to_list()
        assessments = [
            self._projected_assessment(row, as_of_ns, as_of)
            for row in projected
        ]
        assessments.sort(key=lambda item: item["subject_key"])
        statuses = self._source_statuses(as_of_ns)
        self._materialize_health(
            as_of_ns, as_of, assessments, statuses, active_topology)
        self.current_as_of_ns = as_of_ns
        return assessments

    @staticmethod
    def _projected_assessment(row, as_of_ns, as_of):
        evidence = []
        for value in (row.get("point_evidence"), row.get("resource_evidence")):
            if value is not None and value.get("id") not in {
                    item.get("id") for item in evidence}:
                evidence.append(value)
        return {
            "id": digest(f"{as_of_ns}|{row['subject_key']}|{row['state']}"),
            "as_of": as_of,
            "as_of_ns": as_of_ns,
            "subject_key": row["subject_key"],
            "resource_id": row["resource_id"],
            "metric": row["metric"],
            "dimensions_json": row["dimensions_json"],
            "state": row["state"],
            "reason": row["reason"],
            "policy": "illustrative/cpu-average-percent/v1",
            "evidence": evidence,
            "evidence_count": len(evidence),
            "subject": {
                "id": row["subject_id"],
                "entity_key": row["resource_id"],
                "kind": row["subject_kind"],
                "display_name": row["subject_name"],
            },
        }

    def _source_statuses(self, as_of_ns):
        raw = self.master.execute(
            """MATCH (r:RawCapture)
               WHERE r.captured_ns <= $as_of_ns
               RETURN r.category AS category, r.source AS source""",
            {"as_of_ns": as_of_ns}).to_list()
        categories = Counter(row["category"] for row in raw)
        visible_sources = Counter(row["source"] for row in raw)
        statuses = [
            ("inventory", "AVAILABLE" if categories["inventory"] else "NO_INPUT",
             "Azure/Kubernetes inventory visibility only; inventory is not application tracing."),
            ("network_trace", "AVAILABLE" if categories["network_trace"] else "NO_INPUT",
             "Hubble and VNet flow evidence remains raw; network traces do not prove application spans."),
            ("application_trace", "NOT_PROVIDED",
             "Inventory, Hubble, and VNet flows alone do not provide end-to-end application traces."),
            ("metrics", "AVAILABLE" if categories["metric"] else "NO_INPUT",
             "Only metric points visible by capturedAt and event time participate at this as-of point."),
            ("metric_metadata",
             "PARTIAL" if visible_sources["records:MetricRow"] else (
                 "AVAILABLE" if visible_sources["azure-metrics"] else "NO_INPUT"),
             "Collector MetricRow omits unit; native ResourceMetricRow must retain unit and average to qualify."),
            ("network_completeness",
             "LOSS_REPORTED" if categories["network_loss"] or categories["capture_loss"] else "NO_EXPLICIT_LOSS_MARKER",
             "Loss markers are explicit evidence of incompleteness; their absence does not prove complete capture."),
        ]
        if categories["application_request"]:
            statuses[2] = (
                "application_trace", "REQUEST_TELEMETRY_ONLY",
                f"{categories['application_request']} request rows are visible, but distributed trace coverage is unknown.")
        return [
            {"id": digest(f"{as_of_ns}|{kind}"), "kind": kind, "state": state, "detail": detail}
            for kind, state, detail in statuses
        ]

    def _materialize_health(self, as_of_ns, as_of, assessments, statuses, topology):
        for label in (
                "HealthEvidence", "HealthAssessment", "SourceStatus",
                "HealthEntity", "ProjectionSnapshot"):
            self.health.execute(f"MATCH (n:{label}) DETACH DELETE n")
        snapshot_id = digest(f"projection:{as_of_ns}")
        self.health.execute(
            "INSERT (:ProjectionSnapshot {id: $id, as_of: $as_of, as_of_ns: $as_of_ns})",
            {"id": snapshot_id, "as_of": as_of, "as_of_ns": as_of_ns})
        for status in statuses:
            self.health.execute(
                "INSERT (:SourceStatus {id: $id, kind: $kind, state: $state, detail: $detail})",
                status)
            self.health.execute(
                """MATCH (p:ProjectionSnapshot {id: $snapshot_id}), (s:SourceStatus {id: $status_id})
                   INSERT (p)-[:SNAPSHOT_HAS {kind: $kind}]->(s)""",
                {"snapshot_id": snapshot_id, "status_id": status["id"], "kind": "source_status"})
        health_entities = {}

        def ensure_health_entity(entity):
            if entity["id"] in health_entities:
                return
            self.health.execute(
                """INSERT (:HealthEntity {
                    id: $id, entity_key: $entity_key, kind: $kind, display_name: $display_name
                })""",
                entity)
            health_entities[entity["id"]] = entity

        for assessment in assessments:
            values = {
                key: value for key, value in assessment.items()
                if key not in ("evidence", "subject")
            }
            self.health.execute(
                """INSERT (:HealthAssessment {
                    id: $id, as_of: $as_of, as_of_ns: $as_of_ns, subject_key: $subject_key,
                    resource_id: $resource_id, metric: $metric, dimensions_json: $dimensions_json,
                    state: $state, reason: $reason, policy: $policy, evidence_count: $evidence_count
                })""", values)
            self.health.execute(
                """MATCH (p:ProjectionSnapshot {id: $snapshot_id}), (a:HealthAssessment {id: $assessment_id})
                   INSERT (p)-[:SNAPSHOT_HAS {kind: $kind}]->(a)""",
                {"snapshot_id": snapshot_id, "assessment_id": assessment["id"], "kind": "assessment"})
            ensure_health_entity(assessment["subject"])
            self.health.execute(
                """MATCH (a:HealthAssessment {id: $assessment_id}),
                         (e:HealthEntity {id: $entity_id})
                   INSERT (a)-[:ASSESSES {kind: $kind}]->(e)""",
                {
                    "assessment_id": assessment["id"],
                    "entity_id": assessment["subject"]["id"],
                    "kind": assessment["subject"]["kind"],
                })
            for row in assessment["evidence"]:
                evidence_id = digest(f"{assessment['id']}|{row['id']}")
                values = {
                    "id": evidence_id,
                    "observation_id": row["id"],
                    "raw_capture_id": row["raw_capture_id"],
                    "captured_at": row["captured_at"],
                    "captured_ns": row["captured_ns"],
                    "event_at": row["event_at"],
                    "event_ns": row["event_ns"],
                    "row_type": row["row_type"],
                    "record_key": row["record_key"],
                    "aggregation": row["aggregation"],
                    "unit": row["unit"],
                    "values_json": row["values_json"],
                    "dimensions_json": row["dimensions_json"],
                }
                self.health.execute(
                    """INSERT (:HealthEvidence {
                        id: $id, observation_id: $observation_id, raw_capture_id: $raw_capture_id,
                        captured_at: $captured_at, captured_ns: $captured_ns, event_at: $event_at,
                        event_ns: $event_ns, row_type: $row_type, record_key: $record_key,
                        aggregation: $aggregation, unit: $unit, values_json: $values_json,
                        dimensions_json: $dimensions_json
                    })""", values)
                self.health.execute(
                    """MATCH (a:HealthAssessment {id: $assessment_id}),
                             (e:HealthEvidence {id: $evidence_id})
                       INSERT (a)-[:SUPPORTED_BY {rule: $rule}]->(e)""",
                    {"assessment_id": assessment["id"], "evidence_id": evidence_id,
                     "rule": assessment["policy"]})
                if row.get("subject_id") == assessment["subject"]["entity_key"]:
                    self.health.execute(
                        """MATCH (e:HealthEvidence {id: $evidence_id}),
                                 (subject:HealthEntity {id: $entity_id})
                           INSERT (e)-[:EVIDENCES_ENTITY {role: $role}]->(subject)""",
                        {
                            "evidence_id": evidence_id,
                            "entity_id": assessment["subject"]["id"],
                            "role": row["row_type"],
                        })

        relevant_entities = set(health_entities)
        relevant_topology = [
            relation for relation in topology
            if relation["source_id"] in relevant_entities
            or relation["target_id"] in relevant_entities
        ]
        for relation in relevant_topology:
            ensure_health_entity({
                "id": relation["source_id"],
                "entity_key": relation["source_key"],
                "kind": relation["source_kind"],
                "display_name": relation["source_name"],
            })
            ensure_health_entity({
                "id": relation["target_id"],
                "entity_key": relation["target_key"],
                "kind": relation["target_kind"],
                "display_name": relation["target_name"],
            })
            self.health.execute(
                """MATCH (source:HealthEntity {id: $source_id}),
                         (target:HealthEntity {id: $target_id})
                   INSERT (source)-[:PRESERVED_RELATION {
                     observation_id: $observation_id, record_key: $record_key,
                     relation: $relation, verdict: $verdict,
                     captured_ns: $captured_ns, event_ns: $event_ns
                   }]->(target)""",
                relation)

    def health_view(self):
        assessments = self.health.execute(
            """MATCH (a:HealthAssessment)
               RETURN a.state AS state, a.resource_id AS resource_id, a.metric AS metric,
                      a.dimensions_json AS dimensions_json, a.reason AS reason,
                      a.as_of AS as_of, a.evidence_count AS evidence_count
               ORDER BY resource_id, metric, dimensions_json""").to_list()
        evidence = self.health.execute(
            """MATCH (a:HealthAssessment)-[:SUPPORTED_BY]->(e:HealthEvidence)
               RETURN a.resource_id AS resource_id, e.row_type AS row_type,
                      e.record_key AS record_key, e.event_at AS event_at,
                      e.raw_capture_id AS raw_capture_id
               ORDER BY resource_id, event_at, row_type""").to_list()
        statuses = self.health.execute(
            """MATCH (s:SourceStatus)
               RETURN s.kind AS kind, s.state AS state, s.detail AS detail
               ORDER BY kind""").to_list()
        topology = self.health.execute(
            """MATCH (source:HealthEntity)-[r:PRESERVED_RELATION]->(target:HealthEntity)
               RETURN source.entity_key AS source_key, r.relation AS relation,
                      target.entity_key AS target_key, r.verdict AS verdict,
                      r.observation_id AS observation_id
               ORDER BY source_key, relation, target_key""").to_list()
        return {
            "assessments": assessments,
            "evidence": evidence,
            "topology": topology,
            "source_status": statuses,
        }
