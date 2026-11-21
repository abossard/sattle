"""Translate explicit workload declarations into evidence-backed flow and rule records."""
import json
from collections import defaultdict
from datetime import datetime
import ipaddress
from urllib.parse import urlsplit
from pathlib import Path


def inventory_events(path):
    if not path.exists():
        return []
    events = [json.loads(line) for line in path.read_text().splitlines() if line.strip()]
    for row in events:
        if row.get("source") != "arg" or not row.get("capturedAt"):
            raise ValueError("Container Apps inventory requires timestamped native Resource Graph envelopes")
        p = row["payload"]
        if row.get("meta", {}).get("query", "").endswith("changes"):
            if p["properties"].get("changeType") != "Delete" or not p["properties"].get("targetResourceId"):
                raise ValueError("Invalid ARM inventory deletion")
        elif not all(p.get(k) for k in ("id", "name", "type")):
            raise ValueError("ARM inventory requires id, name and type")
    return events


def ontology_facts(model, sidecar):
    """These rows carry declared relationships, never series values or computed health."""
    facts = {}
    def put(kind, key, name, config, **fields):
        row = {"kind": kind, "key": key, "name": name, "configuration": config, **fields}
        old = facts.setdefault((kind, key), row)
        if old != row:
            raise ValueError(f"Conflicting {kind} declarations for {key}")
    if sidecar is None:
        return list(facts.values())
    scope = sidecar["scopeId"].rstrip("/").lower()
    if sidecar["schemaVersion"] != 1 or not scope.startswith("/subscriptions/") or "/resourcegroups/" not in scope:
        raise ValueError("Local ontology needs schemaVersion 1 and an ARM resource-group scopeId")
    for rule in sidecar.get("nameRules", []):
        if rule["match"] not in ("prefix", "suffix", "contains") or not all(isinstance(rule.get(k), str) and rule[k].strip()
                           for k in ("id", "pattern", "class", "evidence")):
            raise ValueError("A name rule needs id, prefix/suffix/contains, pattern, class and evidence")
        put("nameRule", f"{scope}/nameRules/{rule['id']}", rule["id"], rule, sourceKey=scope, targetKey=rule["pattern"],
            signalKind=rule["match"], status=rule["class"], impact=rule["evidence"])
    for flow in sidecar.get("flows", []):
        flow_id = f"{scope}/flows/{flow['id']}"
        put("userFlow", flow_id, flow["name"], {"source": "LocalOntology", "id": flow["id"]}, status="LocalOntology")
        for step in flow["systems"]:
            step_id = f"{scope}/systems/{step['id']}"
            put("systemComponent", step_id, step["name"], {"source": "LocalOntology", "id": step["id"]}, status="LocalOntology")
            put("flowStep", f"{flow_id}/{step['id']}", step["name"], {"source":"LocalOntology", "flow":flow["id"], "system":step["id"]},
                sourceKey=flow_id, targetKey=step_id)
            for binding in step.get("apps", []):
                arm_id = binding["resourceId"].lower()
                if not arm_id.startswith("/subscriptions/") or "/providers/microsoft.app/containerapps/" not in arm_id or not binding["evidence"].strip():
                    raise ValueError("App bindings need a full Container App ARM ID and curator evidence")
                put("stepApp", step_id + arm_id, step["name"], {"source": "LocalOntology", **binding}, sourceKey=step_id, targetKey=arm_id)
    return list(facts.values())


def arm_records(envelopes):
    """Map observed ARM fields to row values; replace each resource's reference set on a refresh."""
    last_relations = {}
    for event in sorted(envelopes, key=lambda e: datetime.fromisoformat(e["capturedAt"].replace("Z","+00:00"))):
        at, raw = event["capturedAt"], event["payload"]
        deleting = event.get("meta", {}).get("query", "").endswith("changes")
        arm_id = raw["properties"]["targetResourceId"] if deleting else raw["id"]
        resource_key = arm_id.lower()
        def record(row_type, key, values=None):
            return {"source": "records:"+row_type, "capturedAt": at, "payload": {"key": key,
                    **({"values":values} if values is not None else {"deleted":True})}}
        if deleting:
            yield record("ArmInventoryRow", resource_key)
            for key in last_relations.pop(resource_key, {}):
                yield record("AzureNetworkRow", key)
            continue
        p = raw.get("properties") or {}
        subscription, group = raw.get("subscriptionId") or "", raw.get("resourceGroup") or ""
        grp = "/subscriptions/"+subscription+"/resourceGroups/"+group if subscription and group else ""
        env_id = p.get("environmentId") or p.get("managedEnvironmentId") or ""
        yield record("ArmInventoryRow", resource_key, {
            "armId": arm_id, "armKey": resource_key, "name":raw["name"], "resourceType":raw["type"].lower(),
            "location":(raw.get("location") or "").lower(), "subscriptionId":subscription, "resourceGroup":group, "groupId":grp.lower(),
            "environmentId":env_id.lower(), "provisioningState":p.get("provisioningState") or "", "runningStatus":p.get("runningStatus") or "",
            "ingressHost":((p.get("configuration") or {}).get("ingress") or {}).get("fqdn") or "", "tags":json.dumps(raw.get("tags") or {}, sort_keys=True),
            "observedAt":at})
        facts = {}
        def add(kind, path, target, address=None):
            key = json.dumps([resource_key,path,target,address],separators=(",",":"))
            facts[key] = {"resourceId":resource_key,"kind":kind,"target":target,"resolvedAddress":address,"fieldPath":path}
        def visit(value,path,field_name):
            if isinstance(value, dict):
                for k,v in value.items(): visit(v,path+"."+k,k.lower())
            elif isinstance(value,list):
                for i,v in enumerate(value): visit(v,f"{path}[{i}]",field_name)
            elif isinstance(value,str) and value:
                if value.lower().startswith("/subscriptions/"):
                    add("armId",path,value.lower())
                elif field_name in ("fqdn","fqdns","privatednszonename","defaultdomain","defaultdomainname","internaldomainnamesuffix"):
                    add("dnsName",path,value.lower().rstrip("."))
                else:
                    try: ip = ipaddress.ip_address(value)
                    except ValueError: ip = None
                    if ip is not None: add("ip",path,str(ip))
                    elif "/" in value:
                        try: add("prefix",path,str(ipaddress.ip_network(value,strict=False)))
                        except ValueError: pass
        visit(p,"properties","")
        if raw["type"].lower() in ("microsoft.network/privatednszones/a","microsoft.network/privatednszones/aaaa"):
            zone = raw.get("managedBy") or ""
            if zone and "/" not in zone:
                host = zone if raw["name"]=="@" else raw["name"]+"."+zone
                for i,item in enumerate(p.get("records",[])):
                    address = item.get("ipv4Address") or item.get("ipv6Address")
                    if address: add("dnsName",f"properties.records[{i}]",host.lower(),address)
        previous = last_relations.get(resource_key,{})
        for key in sorted(previous.keys()-facts.keys()):
            yield record("AzureNetworkRow",key)
        for key,v in sorted(facts.items()):
            if previous.get(key)!=v:
                yield record("AzureNetworkRow",key,v)
        last_relations[resource_key]=facts
