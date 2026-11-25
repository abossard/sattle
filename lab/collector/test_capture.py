"""Capture conversion tests: python3 -B lab/collector/test_capture.py

Native envelopes of lab/CAPTURE-FORMAT.md become record envelopes of the tutorial's row types. The local
tutorial fixture (lab/tutorial-fixtures, git-ignored) is converted whole when it is present.
"""
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

import capture
import sources

HERE = Path(__file__).parent
ROOT = HERE.parent.parent
FIXTURES = ROOT / "lab" / "tutorial-fixtures"
MODULE = json.loads((ROOT / "engine" / "tutorial" / "tutorial.module.gkql.json").read_text())
AT = "2026-01-01T00:01:00.000Z"
SUB = "/subscriptions/00000000-0000-0000-0000-000000000001"
GROUP = SUB + "/resourceGroups/rg-example"


def native(source, payload, meta=None, key="example/1"):
    env = {"v": 1, "source": source, "capturedAt": AT, "seq": 0, "key": key, "payload": payload}
    return {**env, "meta": meta} if meta is not None else env


def listed(kind, obj):
    return native("kubernetes-list", obj, {"list": {"kind": kind + "List"}, "cluster": "aks-example"})


def converted(*envelopes):
    return [r for _, records in capture.convert(envelopes) for r in records]


def rows(records):
    return {r["key"]: r["payload"].get("values", "deleted") for r in records}


def meta(name, uid, namespace="shop", **more):
    return {"name": name, "uid": uid, **({"namespace": namespace} if namespace else {}), **more}


POD = {"metadata": meta("web-7d9f-abcde", "pod-1", labels={"team": "checkout", "app": "web"},
                        ownerReferences=[{"kind": "ReplicaSet", "name": "web-7d9f", "controller": True}]),
       "spec": {"nodeName": "node-a", "serviceAccountName": "web"},
       "status": {"phase": "Running", "conditions": [{"type": "Ready", "status": "True"}],
                  "containerStatuses": [{"restartCount": 2}, {"restartCount": 1}]}}
DEPLOYMENT = {"metadata": meta("web", "dep-1"),
              "spec": {"replicas": 3, "template": {"metadata": {"labels": {"app": "web", "tier": "front"}}}}}
SERVICE = {"metadata": meta("web", "svc-1"), "spec": {"type": "LoadBalancer", "clusterIP": "10.0.0.10",
                                                      "selector": {"app": "web"}},
           "status": {"loadBalancer": {"ingress": [{"ip": "198.18.0.5"}]}}}
NODE = {"metadata": meta("node-a", "node-1", namespace=None, labels={"kubernetes.azure.com/agentpool": "system"}),
        "spec": {"providerID": "azure://" + SUB + "/resourceGroups/MC_EXAMPLE/providers/Microsoft.Compute/"
                 "virtualMachineScaleSets/aks-system/virtualMachines/0"}}
SLICE = {"metadata": meta("web-x1", "slice-1", labels={"kubernetes.io/service-name": "web"}),
         "endpoints": [{"addresses": ["10.1.0.4"], "targetRef": {"kind": "Pod", "uid": "pod-1"}},
                       {"addresses": ["10.1.0.5"]}]}
REPLICA_SET = {"metadata": meta("web-7d9f", "rs-1", ownerReferences=[
    {"kind": "Deployment", "name": "web", "controller": True}])}
NAMESPACE = {"metadata": meta("shop", "ns-1", namespace=None)}
NIC = {"id": GROUP + "/providers/Microsoft.Network/networkInterfaces/nic-a", "name": "nic-a",
       "type": "microsoft.network/networkinterfaces", "location": "westeurope", "resourceGroup": "rg-example",
       "subscriptionId": "00000000-0000-0000-0000-000000000001",
       "properties": {"virtualMachine": {"id": GROUP + "/providers/Microsoft.Compute/virtualMachines/vm-a"},
                      "ipConfigurations": [{"properties": {"privateIPAddress": "10.2.0.4", "subnet": {
                          "id": GROUP + "/providers/Microsoft.Network/virtualNetworks/vnet-a/subnets/default"}}}]}}
NIC_KEY = NIC["id"].lower()
CPU = {"value": [{"name": {"value": "node_cpu_usage_percentage"}, "timeseries": [
    {"data": [{"timeStamp": "2026-01-01T00:00:00Z", "average": 12.5}, {"timeStamp": "2026-01-01T00:01:00Z"}]}]}]}
CLUSTER_ID = GROUP + "/providers/Microsoft.ContainerService/managedClusters/aks-example"
FLOW = {"flow": {"uuid": "flow-1", "time": "2026-01-01T00:00:30.123456789Z", "verdict": "FORWARDED", "is_reply": True,
                 "IP": {"source": "10.1.0.4", "destination": "10.1.0.9"},
                 "source": {"cluster_name": "aks-example", "namespace": "shop", "pod_name": "web-7d9f-abcde"},
                 "destination": {"cluster_name": "aks-example", "namespace": "shop", "pod_name": "client"},
                 "source_names": ["https://web.shop.example:443/x"]}}


class MappingTests(unittest.TestCase):
    def test_each_row_type_gets_its_values_and_key(self):
        cases = [
            ("PodRow", [listed("Pod", POD)], "PodRow/aks-example/pod-1",
             {"cluster": "aks-example", "uid": "pod-1", "namespace": "shop", "name": "web-7d9f-abcde",
              "phase": "Running", "teamLabel": "checkout", "nodeName": "node-a", "restarts": 3, "ready": True,
              "serviceAccount": "web", "deployment": "web"}),
            ("DeploymentRow", [listed("Deployment", DEPLOYMENT)], "DeploymentRow/aks-example/dep-1",
             {"cluster": "aks-example", "namespace": "shop", "name": "web", "replicas": 3}),
            ("ServiceRow", [listed("Service", SERVICE)], "ServiceRow/aks-example/svc-1",
             {"cluster": "aks-example", "namespace": "shop", "name": "web", "externalName": None,
              "address": "198.18.0.5"}),
            ("NodeRow", [listed("Node", NODE)], "NodeRow/aks-example/node-1",
             {"cluster": "aks-example", "uid": "node-1", "name": "node-a",
              "resourceId": NODE["spec"]["providerID"][len("azure://"):], "pool": "system"}),
            ("EndpointRow", [listed("EndpointSlice", SLICE)], "EndpointRow/aks-example/slice-1/10.1.0.4",
             {"cluster": "aks-example", "namespace": "shop", "service": "web", "address": "10.1.0.4",
              "podUid": "pod-1"}),
            ("KubernetesLinkRow podOwner", [listed("Pod", POD)], "KubernetesLinkRow/aks-example/pod-1",
             {"cluster": "aks-example", "namespace": "shop", "kind": "podOwner", "name": "web-7d9f-abcde",
              "target": "web-7d9f"}),
            ("KubernetesLinkRow replicaSet", [listed("ReplicaSet", REPLICA_SET)], "KubernetesLinkRow/aks-example/rs-1",
             {"cluster": "aks-example", "namespace": "shop", "kind": "replicaSet", "name": "web-7d9f",
              "target": "web"}),
            ("KubernetesLinkRow azureHost", [listed("Node", NODE)], "KubernetesLinkRow/aks-example/node-1",
             {"cluster": "aks-example", "namespace": "", "kind": "azureHost", "name": "node-a",
              "target": (SUB + "/resourceGroups/MC_EXAMPLE/providers/Microsoft.Compute/"
                         "virtualMachineScaleSets/aks-system").lower()}),
            ("KubernetesLinkRow namespace", [listed("Namespace", NAMESPACE)], "KubernetesLinkRow/aks-example/ns-1",
             {"cluster": "aks-example", "namespace": "", "kind": "namespace", "name": "shop", "target": None}),
            ("SelectsRow", [listed("Service", SERVICE), listed("Deployment", DEPLOYMENT)],
             "SelectsRow/aks-example/shop/web/web",
             {"cluster": "aks-example", "namespace": "shop", "service": "web", "deployment": "web"}),
            ("ResourceRow", [native("arg", NIC, {"query": "resources"})], "ResourceRow/" + NIC_KEY,
             {"id": NIC["id"], "name": "nic-a", "type": "microsoft.network/networkinterfaces",
              "location": "westeurope", "resourceGroup": "rg-example",
              "subscriptionId": "00000000-0000-0000-0000-000000000001", "parentId": None, "address": None,
              "linkedId": None, "clientId": None, "principalId": None, "scope": None}),
            ("ResourceRow in a node resource group",
             [native("arg", {"id": SUB + "/resourceGroups/MC_rg-example_aks-example_westeurope/providers/"
                             "Microsoft.Compute/disks/d", "name": "d", "type": "microsoft.compute/disks",
                             "resourceGroup": "MC_rg-example_aks-example_westeurope",
                             "subscriptionId": "00000000-0000-0000-0000-000000000001"}, {"query": "resources"})],
             "ResourceRow/" + (SUB + "/resourceGroups/MC_rg-example_aks-example_westeurope/providers/"
                               "Microsoft.Compute/disks/d").lower(),
             {"id": SUB + "/resourceGroups/MC_rg-example_aks-example_westeurope/providers/Microsoft.Compute/disks/d",
              "name": "d", "type": "microsoft.compute/disks", "location": None,
              "resourceGroup": "MC_rg-example_aks-example_westeurope",
              "subscriptionId": "00000000-0000-0000-0000-000000000001",
              "parentId": "/subscriptions/00000000-0000-0000-0000-000000000001/resourcegroups/rg-example/providers/"
                          "microsoft.containerservice/managedclusters/aks-example",
              "address": None, "linkedId": None, "clientId": None, "principalId": None, "scope": None}),
            ("AzureNetworkRow", [native("arg", NIC, {"query": "resources"})],
             "AzureNetworkRow/" + NIC_KEY + "/ip/10.2.0.4",
             {"resourceId": NIC_KEY, "kind": "ip", "target": "10.2.0.4", "resolvedAddress": None}),
            ("ResourceMetricRow", [native("azure-metrics", CPU, {"resourceId": CLUSTER_ID})],
             "ResourceMetricRow/" + CLUSTER_ID.lower() + "/node_cpu_usage_percentage//2026-01-01T00:00:00Z",
             {"time": "2026-01-01T00:00:00Z", "resourceId": CLUSTER_ID, "metric": "node_cpu_usage_percentage",
              "value": 12.5}),
            ("FlowRow", [native("hubble", FLOW)], "FlowRow/flow-1",
             {"time": "2026-01-01T00:00:30.123456789Z", "cluster": "aks-example", "verdict": "FORWARDED",
              "srcNamespace": "shop", "srcPod": "client", "dstNamespace": "shop", "dstPod": "web-7d9f-abcde",
              "dstAddress": "10.1.0.4", "dstName": "web.shop.example"}),
        ]
        for name, envelopes, key, values in cases:
            with self.subTest(name):
                records = converted(*envelopes)
                self.assertEqual(rows(records)[key], values)
                record = next(r for r in records if r["key"] == key)
                self.assertEqual(record["capturedAt"], AT)
                self.assertEqual(record["meta"], {"source": "capture:" + envelopes[-1]["source"], "position": AT})
                self.assertEqual(record["source"], "records:" + key.split("/")[0])

    def test_network_evidence_of_a_resource(self):
        self.assertEqual(sorted(k for k in rows(converted(native("arg", NIC, {"query": "resources"})))
                                if k.startswith("AzureNetworkRow/")),
                         ["AzureNetworkRow/" + NIC_KEY + "/attachment/" + GROUP.lower()
                          + "/providers/microsoft.compute/virtualmachines/vm-a",
                          "AzureNetworkRow/" + NIC_KEY + "/ip/10.2.0.4",
                          "AzureNetworkRow/" + NIC_KEY + "/subnet/" + GROUP.lower()
                          + "/providers/microsoft.network/virtualnetworks/vnet-a/subnets/default"])

    def test_an_object_withdraws_the_rows_it_no_longer_gives(self):
        shrunk = {**SLICE, "endpoints": SLICE["endpoints"][:1]}
        relabelled = {**DEPLOYMENT, "spec": {**DEPLOYMENT["spec"], "template": {"metadata": {"labels": {"app": "api"}}}}}
        deleted = native("kubernetes-watch", {"type": "DELETED", "object": {"kind": "Pod", **POD}},
                         {"cluster": "aks-example"})
        gone = native("arg", {"properties": {"changeType": "Delete", "targetResourceId": NIC["id"].upper()}},
                      {"query": "resourcechanges"})
        cases = [
            ("an address leaves a slice", [listed("EndpointSlice", SLICE), listed("EndpointSlice", shrunk)],
             {"EndpointRow/aks-example/slice-1/10.1.0.5": "deleted"}),
            ("a template stops matching", [listed("Service", SERVICE), listed("Deployment", DEPLOYMENT),
                                           listed("Deployment", relabelled)],
             {"SelectsRow/aks-example/shop/web/web": "deleted"}),
            ("a pod is deleted", [listed("Pod", POD), deleted],
             {"PodRow/aks-example/pod-1": "deleted", "KubernetesLinkRow/aks-example/pod-1": "deleted"}),
            ("a resource is deleted", [native("arg", NIC, {"query": "resources"}), gone],
             {k: "deleted" for k in rows(converted(native("arg", NIC, {"query": "resources"})))}),
            ("an unchanged object again", [listed("Pod", POD), listed("Pod", POD)], {}),
        ]
        for name, envelopes, last in cases:
            with self.subTest(name):
                *_, (_, records) = capture.convert(envelopes)
                self.assertEqual(rows(records), last)

    def test_records_no_row_type_needs_give_nothing(self):
        cases = [
            ("a bookmark", native("kubernetes-watch", {"type": "BOOKMARK", "object": {"metadata": {}}})),
            ("an object without a cluster", native("kubernetes-list", POD, {"list": {"kind": "PodList"}})),
            ("a kind without a row", listed("ConfigMap", {"metadata": meta("settings", "cm-1")})),
            ("a service with an empty selector alone", listed("Service", {**SERVICE, "spec": {}})),
            ("a resource change that is no delete", native("arg", {"properties": {"changeType": "Update"}},
                                                           {"query": "resourcechanges"})),
            ("a row without a type", native("arg", {"id": GROUP, "name": "rg-example"}, {"query": "resources"})),
            ("a datapoint without a value", native("azure-metrics", {"value": [{"name": {"value": "m"}, "timeseries": [
                {"data": [{"timeStamp": "2026-01-01T00:00:00Z", "average": None}]}]}]}, {"resourceId": CLUSTER_ID})),
            ("a flow without a verdict", native("hubble", {"flow": {"time": AT}})),
            ("an activity log event", native("activity-log", {"eventDataId": "e"})),
        ]
        for name, envelope in cases:
            with self.subTest(name):
                self.assertEqual([r for r in converted(envelope) if not r["key"].startswith("ServiceRow/")], [])

    def test_hubble_lost_events_become_a_loss_mark_of_flows(self):
        cases = [("count as text", "7", 7), ("count as number", 7, 7), ("count unknown", None, 0)]
        for name, n, count in cases:
            with self.subTest(name):
                lost = {"lost_events": {"num_events_lost": n} if n is not None else {}, "node_name": "node-a",
                        "time": "2026-01-01T00:00:10Z"}
                [record] = converted(native("hubble", lost, key="hubble/lost:node-a:t"))
                self.assertEqual(record["source"], "records:FlowRow")
                self.assertEqual(record["payload"], {"lost": count, "time": "2026-01-01T00:00:10Z"})


KINDS = {"Text": lambda v: isinstance(v, str), "Integer": lambda v: isinstance(v, int) and not isinstance(v, bool),
         "Decimal": lambda v: isinstance(v, (int, float)) and not isinstance(v, bool),
         "Boolean": lambda v: isinstance(v, bool), "Instant": lambda v: isinstance(v, str) and sources.instant(v)}


@unittest.skipUnless(FIXTURES.is_dir(), "lab/tutorial-fixtures is absent (local, git-ignored)")
class TutorialFixtureTests(unittest.TestCase):
    def test_every_bound_row_type_gets_well_typed_records(self):
        fields = {r["name"]: r["fields"] for r in MODULE["rows"]}
        bound = sorted({s["rows"] for s in MODULE["space"]["sources"]})
        with TemporaryDirectory() as d:
            capture.main([str(FIXTURES), d])
            out = [json.loads(line) for p in sorted(Path(d).rglob("*.ndjson"))
                   for line in p.read_text().splitlines()]
        for row_type in bound:
            with self.subTest(row_type):
                records = [r for r in out if r["source"] == "records:" + row_type and "values" in r["payload"]]
                self.assertTrue(records)
                held = {}
                for r in records:
                    values = r["payload"]["values"]
                    self.assertEqual(sorted(values), sorted(f["name"] for f in fields[row_type]))
                    for f in fields[row_type]:
                        v = values[f["name"]]
                        self.assertTrue(f.get("optional") or v is not None, f["name"])
                        self.assertTrue(v is None or KINDS[f["type"]](v), f["name"])
                    at = (r["capturedAt"], r["payload"]["key"])
                    self.assertEqual(held.setdefault(at, values), values, r["key"])


if __name__ == "__main__":
    unittest.main()
