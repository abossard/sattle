#!/usr/bin/env python3
"""Resolve an explicitly selected workload ontology against inventory and aggregated HTTP telemetry."""

import argparse
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import re
import tempfile
from urllib.parse import quote

from azure_monitor import LOGS, azure, iso, read, timestamp, workspace_id


def scoped_inventory(path):
    scope = None
    rows = {}
    for line in path.read_text().splitlines():
        e = json.loads(line)
        s = e.get("meta", {}).get("scopeId", "").rstrip("/")
        if (
            not re.fullmatch(
                r"/subscriptions/[a-f0-9-]{36}/resourceGroups/[^/]+", s, re.I
            )
            or scope
            and s.lower() != scope.lower()
        ):
            raise ValueError("Expected one complete resource-group inventory")
        scope = s
        p = e["payload"]
        if e["meta"]["query"] == "resourcechanges":
            rows.pop(p["properties"]["targetResourceId"].lower(), None)
        elif p["id"].lower().startswith(scope.lower() + "/providers/"):
            rows[p["id"].lower()] = p
        else:
            raise ValueError("Out-of-scope inventory row")
    if not scope:
        raise ValueError("Empty inventory")
    return scope, list(rows.values())


def kusto_source(recipe, component, apps, start, end, destinations):
    rid = component["id"]
    roles = {
        v
        for a in apps
        for v in [
            a["name"],
            *[
                a["tags"].get(k)
                for k in ("azd-env-name", "azd-service-name", "workload")
            ],
        ]
        if v
    }
    hosts = {
        a["properties"].get("configuration", {}).get("ingress", {}).get("fqdn")
        for a in apps
    } - {None}
    variables = {
        "COMPONENT": json.dumps(rid),
        "START": iso(start),
        "END": iso(end),
        "ROLES": json.dumps(sorted(roles)),
        "INGRESS": json.dumps(sorted(hosts)),
        "TARGETS": json.dumps(
            sorted(destinations | {"management.azure.com", "login.microsoftonline.com"})
        ),
    }
    common = (recipe / "request-scope.kql").read_text()

    return {
        kind: re.sub(
            r"__([A-Z]+)__",
            lambda match: variables[match[1]],
            common + "\n" + (recipe / ("request-" + kind + ".kql")).read_text(),
        )
        for kind in ("routes", "dependencies")
    }


def query_logs(kusto, component, scope, start, end):
    ws = component["properties"]["WorkspaceResourceId"]
    if not ws.lower().startswith(
        scope.lower() + "/providers/microsoft.operationalinsights/workspaces/"
    ):
        raise ValueError("The component's workspace is outside this resource group")
    subscription = scope.split("/")[2]
    guid = workspace_id(ws, subscription)
    url = f"{LOGS}/v1/workspaces/{quote(guid, safe='')}/query"

    def fetch(kind, source):
        query = kusto[kind]
        doc = azure(
            url,
            subscription,
            audience=LOGS,
            query={"query": query, "timespan": iso(start) + "/" + iso(end)},
        )
        if doc.get("error"):
            raise ValueError(
                "Incomplete log query: " + doc["error"].get("code", "UnknownError")
            )
        table = next(t for t in doc["tables"] if t["name"] == "PrimaryResult")
        columns = [c["name"] for c in table["columns"]]
        rows = []
        for values in table["rows"]:
            row = dict(zip(columns, values, strict=True))
            if isinstance(row["ingressHosts"], str):
                row["ingressHosts"] = json.loads(row["ingressHosts"])
            row["ingressHosts"] = sorted(row["ingressHosts"])
            rows.append(
                {
                    "scopeId": scope,
                    "componentId": component["id"],
                    "windowStart": iso(start),
                    "windowEnd": iso(end),
                    "source": source,
                    **row,
                }
            )
        return rows

    return fetch("routes", "AppRequests"), fetch("dependencies", "AppDependencies")


def save(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(mode="w", dir=path.parent, delete=False) as out:
        out.write(text)
    os.replace(out.name, path)


def ingress(app):
    return (
        app.get("properties", {})
        .get("configuration", {})
        .get("ingress", {})
        .get("fqdn", "")
        .casefold()
    )


def workload(template, inventory):
    wanted = template["deployment"]
    selected = [
        row
        for row in inventory
        if row.get("tags", {}).get("workload") == wanted["workload"]
    ]
    apps = [
        r for r in selected if r["type"].casefold() == "microsoft.app/containerapps"
    ]
    if not set(wanted["requiredServices"]) <= {
        r["tags"].get("azd-service-name") for r in apps
    }:
        raise ValueError(
            "This resource group lacks the template's workload and service tags"
        )
    components = [
        r for r in selected if r["type"].casefold() == "microsoft.insights/components"
    ]
    if len(components) != 1:
        raise ValueError(
            "Expected one Application Insights component tagged for this workload"
        )
    for rule in template["nameRules"]:
        kind = rule["match"]
        match = {
            "prefix": str.startswith,
            "suffix": str.endswith,
            "contains": str.__contains__,
        }[kind]
        pattern = rule["pattern"].casefold()
        names = {
            a["id"]
            for a in inventory
            if a["type"].casefold() == "microsoft.app/containerapps"
            and match(a["name"].casefold(), pattern)
        }
        tags = {
            a["id"]
            for a in apps
            if a["tags"].get("azd-service-name") == rule["serviceTag"]
        }
        if not tags or names != tags:
            raise ValueError(
                f"Service tag and resource-name rule disagree for {rule['class']}"
            )
    return apps, components[0]


def captured(path):
    return [json.loads(line) for line in path.read_text().splitlines() if line.strip()]


def check_receipts(rows, source, scope, component, windows):
    keys = set()
    for row in rows:
        if (
            row["scopeId"].casefold() != scope.casefold()
            or row["componentId"].casefold() != component["id"].casefold()
            or row["source"] != source
        ):
            raise ValueError(f"Foreign {source} measurement")
        start, end, seen = (
            timestamp(row[k]) for k in ("windowStart", "windowEnd", "lastSeen")
        )
        if not start <= seen < end:
            raise ValueError(f"Measurement outside its {source} window")
        windows.add((row["windowStart"], row["windowEnd"]))
        key = tuple(
            row[k]
            for k in (
                ("role", "operation")
                if source == "AppRequests"
                else ("role", "operation", "relationship", "evidenceType", "target")
            )
        )
        if key in keys:
            raise ValueError(f"Duplicate {source} measurement {key}")
        keys.add(key)
        counts = [
            row[k]
            for k in (
                "calls",
                "failed",
                "unknown",
                "attributedCalls",
                "attributedFailed",
                "attributedUnknown",
            )
        ]
        total, failed, unknown, attributed, attributed_failed, attributed_unknown = (
            counts
        )
        if (
            not all(isinstance(x, int) and x >= 0 for x in counts)
            or attributed > total
            or failed + unknown > total
            or attributed_failed + attributed_unknown > attributed
            or attributed_failed > failed
            or attributed_unknown > unknown
        ):
            raise ValueError(f"Inconsistent counts in {source} measurement")


def resolve(template, scope, apps, requests, spans):
    hosts = {}
    for app in apps:
        if ingress(app):
            hosts.setdefault(app["tags"].get("azd-service-name"), set()).add(
                ingress(app)
            )

    def qualified(row, service_hosts):
        observed = {h.casefold() for h in row["ingressHosts"]}
        return (
            row["attributedCalls"] > 0
            and observed
            and observed <= service_hosts
            and row["role"] not in ("", "unattributed")
        )

    def bind(selector, is_span):
        service_hosts = hosts.get(selector["service"], set())
        operation = selector["operation"]
        routable = [
            r
            for r in requests
            if r["operation"] == operation
            and r["distinctTemplates"] == 1
            and qualified(r, service_hosts)
        ]
        candidates = {r["role"] for r in routable}
        if is_span:
            if selector["relationship"] != "direct-child-span":
                raise ValueError(
                    "Only direct request-child spans can measure a workflow step"
                )
            candidates &= {
                r["role"]
                for r in spans
                if qualified(r, service_hosts)
                and all(
                    r[k] == selector[k]
                    for k in ("operation", "relationship", "evidenceType", "target")
                )
            }
        # An unresolved selector remains outstanding. It cannot join any HTTP or span outcome.
        return [{**selector, "role": role} for role in sorted(candidates or {""})]

    flows = deepcopy(template["flows"])
    classes = {r["class"] for r in template["nameRules"]}
    for flow in flows:
        for step in flow["systems"]:
            if any(x["class"] not in classes for x in step["requires"]):
                raise ValueError(f"Unrecognized service class in {step['id']}")
            for key in ("requests", "spans"):
                if key in step:
                    step[key] = [b for s in step[key] for b in bind(s, key == "spans")]
    return {
        "schemaVersion": template["schemaVersion"],
        "scopeId": scope,
        "nameRules": template["nameRules"],
        "flows": flows,
    }


def deployment_queries(recipe, fixture, scope, component, apps, start, end):
    destinations = set()
    dependency_file = fixture / "dependencies.ndjson"
    if dependency_file.exists():
        for row in captured(dependency_file):
            if (
                row.get("scopeId", "").casefold() == component["id"].casefold()
                and row.get("targetResourceId", "").casefold().startswith(scope.casefold() + "/providers/")
            ):
                destinations.add(row["destination"])
    return kusto_source(recipe, component, apps, start, end, destinations)


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--fixture", required=True, type=Path)
    cli.add_argument("--template", required=True, type=Path)
    cli.add_argument("--hours", type=int, default=72)
    cli.add_argument("--at", help="UTC end of the measurement window")
    mode = cli.add_mutually_exclusive_group()
    mode.add_argument(
        "--offline",
        action="store_true",
        help="Apply the edited ontology to the saved HTTP requests and spans",
    )
    mode.add_argument(
        "--print-kql",
        choices=("routes", "dependencies"),
        help="Print the runnable workspace query for this inventory and window",
    )
    mode.add_argument(
        "--print-captured-kql",
        choices=("routes", "dependencies", "both"),
        help="Print Kusto for the saved measurement window. With both, print the scope and queries as JSON.",
    )
    args = cli.parse_args()
    if args.hours <= 0:
        cli.error("--hours must be positive")
    if args.at and (args.offline or args.print_captured_kql):
        cli.error("The requested mode uses the window in the saved measurements")
    template = read(args.template)
    scope, inventory = scoped_inventory(args.fixture / "container-apps.ndjson")
    apps, component = workload(template, inventory)
    requests_file = args.fixture / "request-observations.ndjson"
    spans_file = args.fixture / "request-dependencies.ndjson"
    if args.offline or args.print_captured_kql:
        observations, dependencies = captured(requests_file), captured(spans_file)
    else:
        end = (
            timestamp(args.at)
            if args.at
            else datetime.now(timezone.utc).replace(second=0, microsecond=0)
        )
        start = end - timedelta(hours=args.hours)
        kusto = deployment_queries(args.template.parent, args.fixture, scope, component, apps, start, end)
        if args.print_kql:
            print(kusto[args.print_kql], end="")
            return
        observations, dependencies = query_logs(kusto, component, scope, start, end)
    windows = set()
    check_receipts(observations, "AppRequests", scope, component, windows)
    check_receipts(dependencies, "AppDependencies", scope, component, windows)
    if len(windows) > 1:
        raise ValueError("Saved requests and spans cover different windows")
    if args.print_captured_kql:
        if not windows:
            cli.error("No saved Kusto measurement window")
        window_start, window_end = next(iter(windows))
        start, end = timestamp(window_start), timestamp(window_end)
        kusto = deployment_queries(args.template.parent, args.fixture, scope, component, apps, start, end)
        if args.print_captured_kql == "both":
            print(json.dumps({
                "componentId": component["id"], "windowStart": window_start,
                "windowEnd": window_end, "queries": kusto,
            }))
        else:
            print(kusto[args.print_captured_kql], end="")
        return
    ontology = resolve(template, scope, apps, observations, dependencies)
    if windows:
        start, end = windows.pop()
        ontology["evidenceWindow"] = {"start": start, "end": end}
    if not args.offline:
        for path, rows in ((requests_file, observations), (spans_file, dependencies)):
            lines = sorted(json.dumps(r, separators=(",", ":")) for r in rows)
            save(path, "".join(line + "\n" for line in lines))
    save(args.fixture / "flow-ontology.json", json.dumps(ontology, indent=2) + "\n")
    print(
        f"{len(ontology['flows'])} curated flows, {len(observations)} routes, {sum(r['calls'] for r in observations)} sample-weighted requests"
    )


if __name__ == "__main__":
    main()
