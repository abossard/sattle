using System.Globalization;
using System.Text.Json;

namespace Sattle.Cli;

static partial class Bench
{
    /// <summary>One capture file being written: envelopes in the order they are added, which must be capture-time order (the reader merges files by capture time).</summary>
    sealed class CaptureFile(string path) : IDisposable
    {
        readonly FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        Utf8JsonWriter? writer;
        long seq, lastMs = long.MinValue;
        public long Records => seq;
        static readonly byte[] Newline = "\n"u8.ToArray();

        public void Add(DateTimeOffset t, string source, string key, object? meta, object payload)
        {
            var ms = t.ToUnixTimeMilliseconds();
            if (ms < lastMs) throw new InvalidOperationException($"{path}: {key} at {t:O} is before the record written last");
            lastMs = ms;
            if (writer is null) writer = new Utf8JsonWriter(stream); else writer.Reset(stream);
            writer.WriteStartObject();
            writer.WriteNumber("v", 1); writer.WriteString("source", source); writer.WriteString("capturedAt", Iso(t)); writer.WriteNumber("seq", ++seq); writer.WriteString("key", key);
            writer.WritePropertyName("meta"); JsonSerializer.Serialize(writer, meta);
            writer.WritePropertyName("payload"); JsonSerializer.Serialize(writer, payload);
            writer.WriteEndObject(); writer.Flush();
            stream.Write(Newline);
        }
        public void Dispose() { writer?.Dispose(); stream.Dispose(); }
    }

    static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>`sattle bench gen-estate --nodes N [--minutes M] [--pairs-per-node P] --out DIR`: a synthetic capture shaped like the test estate, scaled to about N master nodes (pods, Services and Azure resources).
    /// The estate's workload namespace is the unit: per namespace the Services web, api and database, the Deployments web (3 pods) and api (2 pods), their service accounts and EndpointSlices, and the platform behind it (a cache, a storage account, a PostgreSQL server, API Management with a backend, three private endpoints, a managed identity and a role assignment): 18 master nodes.
    /// One Kubernetes node (with its virtual machine) per 30 pods, in one cluster `synth`. The first namespace is `shop`, which the tracked space binds the workload model to. Per minute: one flow for each active pod pair (P per master node, 0.24 by default: 240,000 at 10^6), the four flows of each namespace to its private endpoints, one Azure Monitor point per platform resource, two Prometheus samples per node and metric, the forwarder's minute marker; warning events, Activity Log operations and the estate's scenarios A to C in one namespace of every hundred. The estate's signal rules and capabilities are authored. Deterministic.</summary>
    public static int GenEstate(Program.Options o)
    {
        var target = long.TryParse(o.Get("nodes"), out var nn) ? nn : 10_000;
        var minutes = int.TryParse(o.Get("minutes"), out var mm) ? mm : 10;
        var pairsPerNode = double.TryParse(o.Get("pairs-per-node"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ppn) ? ppn : 0.24;
        var outDir = o.Get("out") ?? throw new ArgumentException("--out DIR is required");
        const int perSlice = 18, podsPerNode = 30;
        var slices = (int)Math.Max(1, Math.Round(target / (perSlice + 5.0 / podsPerNode)));
        var nodeCount = (slices * 5 + podsPerNode - 1) / podsPerNode;
        var masterNodes = (long)slices * perSlice + nodeCount + 3;              // + the cluster, its load balancer and its virtual network
        var pairs = (int)Math.Min(6L * slices, (long)Math.Round(pairsPerNode * masterNodes));
        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset At(double sec) => t0.AddMilliseconds(Math.Round(sec * 1000));
        const string Sub = "sub-0001", NodeRg = "mc_rg-synth_aks-synth_north";
        var clusterId = $"/subscriptions/{Sub}/resourcegroups/rg-synth/providers/microsoft.containerservice/managedclusters/aks-synth";
        var vnetId = $"/subscriptions/{Sub}/resourcegroups/{NodeRg}/providers/microsoft.network/virtualnetworks/vnet-synth";
        string Ns(int i) => i == 0 ? "shop" : $"ns-{i:D6}";
        string Rg(int i) => $"/subscriptions/{Sub}/resourcegroups/rg-{Ns(i)}/providers";
        string Ip(int first, long x) => $"{first}.{(x >> 16) & 255}.{(x >> 8) & 255}.{x & 255}";
        string Node(int n) => $"node-{n:D5}";
        string NodeOf(int slice, int pod) => Node((slice * 5 + pod) / podsPerNode);
        string[] podNames = ["web-7d4-1", "web-7d4-2", "web-7d4-3", "api-5c9-1", "api-5c9-2"];
        // per namespace: the private endpoints of the cache, the storage account and the database (name, address, resource, host)
        (string Name, string Address, string Target, string Host)[] Endpoints(int i) =>
        [
            ($"pe-cache-{i}", Ip(10, i * 8L + 1), $"{Rg(i)}/microsoft.cache/redis/cache-{i}", $"cache-{i}.redis.cache.windows.net"),
            ($"pe-assets-{i}", Ip(10, i * 8L + 2), $"{Rg(i)}/microsoft.storage/storageaccounts/assets{i}", $"assets{i}.blob.core.windows.net"),
            ($"pe-pg-{i}", Ip(10, i * 8L + 3), $"{Rg(i)}/microsoft.dbforpostgresql/flexibleservers/pg-{i}", $"pg-{i}.postgres.database.azure.com"),
        ];
        bool Scenario(int i, int kind) => slices >= 100 ? i % 100 == kind : i == kind;      // scenario A (1), B (2), C (3) in one namespace of every hundred

        Directory.CreateDirectory(outDir);
        foreach (var d in new[] { "kubernetes", "hubble", "arg", "prometheus", "azure-metrics", "activity-log", "authoring" }) Directory.CreateDirectory(Path.Combine(outDir, d));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long records = 0;

        // ---- Kubernetes: nodes, service accounts, Deployments, Services, pods, EndpointSlices at the start; warning events of scenario C; the forwarder's minute marker ----
        using (var k = new CaptureFile(Path.Combine(outDir, "kubernetes", "watch.ndjson")))
        {
            var rv = 100L;
            void K8s(double sec, string kind, string uid, string name, string? ns, Dictionary<string, object?> obj, Dictionary<string, object?>? metaExtra = null)
            {
                rv++;
                var meta = new Dictionary<string, object?> { ["name"] = name, ["uid"] = uid, ["resourceVersion"] = rv.ToString(CultureInfo.InvariantCulture) };
                if (ns is not null) meta["namespace"] = ns;
                if (metaExtra is not null) foreach (var (key, v) in metaExtra) meta[key] = v;
                obj["kind"] = kind; obj["metadata"] = meta;
                k.Add(At(sec), "kubernetes-watch", $"kubernetes-watch/ADDED/{kind}/{uid}/{rv}", new { path = "/synthetic" }, new { type = "ADDED", @object = obj });
            }
            for (var n = 0; n < nodeCount; n++)
                K8s(0, "Node", $"uid-{Node(n)}", Node(n), null, new() { ["spec"] = new { providerID = $"azure:///subscriptions/{Sub}/resourceGroups/{NodeRg}/providers/Microsoft.Compute/virtualMachines/{Node(n)}" } },
                    new() { ["labels"] = new Dictionary<string, string> { ["kubernetes.azure.com/agentpool"] = "apps" } });
            for (var i = 0; i < slices; i++)
                foreach (var sa in new[] { "web", "api" }) K8s(0.1, "ServiceAccount", $"sa-{sa}-{i}", sa, Ns(i), [], sa == "web" ? new() { ["annotations"] = new Dictionary<string, string> { ["azure.workload.identity/client-id"] = $"c1e7a0de-0000-4000-8000-{i:D12}" } } : null);
            for (var i = 0; i < slices; i++)
            {
                var ns = Ns(i);
                var env = new Dictionary<string, object[]>
                {
                    ["web"] = [new { name = "CACHE_HOST", value = $"cache-{i}.redis.cache.windows.net" }, new { name = "API_URL", value = $"http://api.{ns}.svc.cluster.local:8080" }, new { name = "LOG_LEVEL", value = "info" }],
                    ["api"] = [new { name = "DATABASE_HOST", value = $"database.{ns}.svc.cluster.local" }],
                };
                foreach (var (d, r) in new[] { ("web", 3), ("api", 2) })
                    K8s(0.2, "Deployment", $"dep-{d}-{i}", d, ns, new() { ["spec"] = new { replicas = r, template = new { metadata = new { labels = new { app = d, tier = ns } }, spec = new { serviceAccountName = d, containers = new[] { new { name = d, env = env[d] } } } } } });
            }
            for (var i = 0; i < slices; i++)
            {
                var ns = Ns(i);
                K8s(0.4, "Service", $"svc-web-{i}", "web", ns, new() { ["spec"] = new { clusterIP = Ip(11, i * 4L + 1), selector = new { app = "web" } } });
                K8s(0.4, "Service", $"svc-api-{i}", "api", ns, new() { ["spec"] = new { type = "LoadBalancer", clusterIP = Ip(11, i * 4L + 2), selector = new { app = "api" } }, ["status"] = new { loadBalancer = new { ingress = new[] { new { ip = Ip(13, i) } } } } });
                K8s(0.4, "Service", $"svc-database-{i}", "database", ns, new() { ["spec"] = new { type = "ExternalName", externalName = $"pg-{i}.postgres.database.azure.com" } });
            }
            for (var i = 0; i < slices; i++)
                for (var p = 0; p < 5; p++)
                {
                    var name = podNames[p]; var dep = p < 3 ? "web" : "api";
                    K8s(1, "Pod", $"uid-{i}-{name}", name, Ns(i), new()
                    {
                        ["spec"] = new { nodeName = NodeOf(i, p), serviceAccountName = dep },
                        ["status"] = new { phase = "Running", conditions = new[] { new { type = "Ready", status = "True" } }, containerStatuses = new[] { new { restartCount = 0 } } },
                    }, new() { ["ownerReferences"] = new[] { new { kind = "ReplicaSet", name = name[..^2] } } });
                }
            for (var i = 0; i < slices; i++)
                foreach (var (dep, first, count, col) in new[] { ("web", 0, 3, 0), ("api", 3, 2, 1) })
                    K8s(2, "EndpointSlice", $"slice-{dep}-{i}", $"{dep}-slice", Ns(i), new()
                    {
                        ["endpoints"] = Enumerable.Range(first, count).Select(p => new { addresses = new[] { Ip(12, i * 8L + p) }, targetRef = new { kind = "Pod", uid = $"uid-{i}-{podNames[p]}" } }).ToArray(),
                    }, new() { ["labels"] = new Dictionary<string, string> { ["kubernetes.io/service-name"] = dep } });
            var ev = 0L;
            for (var m = 0; m < minutes; m++)
            {
                if (m is >= 4 and <= 6)
                    for (var q = 0; q < 3; q++)
                        for (var i = 0; i < slices; i++)
                        {
                            if (!Scenario(i, 3)) continue;
                            var sec = m * 60 + 5 + q * 15;
                            K8s(sec, "Event", $"ev-{ev}", $"web-7d4-3.{ev}", Ns(i), new() { ["involvedObject"] = new { kind = "Pod", name = "web-7d4-3", @namespace = Ns(i) }, ["reason"] = "BackOff", ["type"] = "Warning", ["message"] = "back-off restarting container", ["count"] = 1, ["lastTimestamp"] = Iso(At(sec)) });
                            ev++;
                        }
                k.Add(At(m * 60 + 59), "sattle-forwarder", $"sattle-forwarder/stats/{m}", null, new { at = Iso(At(m * 60 + 59)), @event = "stats", uptimeSeconds = m * 60 + 59 });
            }
            records += k.Records;
        }

        // ---- Hubble: per minute one flow for each active pod pair (pair p of each namespace in turn, web to api) and the namespace's four flows to its private endpoints; the database endpoint drops in scenario B ----
        (int Src, int Dst)[] candidates = [(0, 3), (1, 4), (2, 3), (0, 4), (1, 3), (2, 4)];
        using (var h = new CaptureFile(Path.Combine(outDir, "hubble", "flows.ndjson")))
        {
            var perMinute = pairs + 4L * slices; var n = 0L;
            for (var m = 0; m < minutes; m++)
            {
                var j = 0L;
                DateTimeOffset Next() => At(m * 60 + 1 + 57.0 * j++ / perMinute);
                for (var p = 0; p < pairs; p++)
                {
                    var (i, c) = (p % slices, p / slices); var (s, d) = candidates[c];
                    var t = Next();
                    h.Add(t, "hubble", $"hubble/flow-{n}", null, new { flow = new { time = Iso(t), uuid = $"flow-{n++}", verdict = "FORWARDED", source = new { @namespace = Ns(i), cluster_name = "synth", pod_name = podNames[s] }, destination = new { @namespace = Ns(i), cluster_name = "synth", pod_name = podNames[d] } }, node_name = NodeOf(i, s), time = Iso(t) });
                }
                for (var i = 0; i < slices; i++)
                {
                    var pe = Endpoints(i);
                    foreach (var (s, e) in new[] { (0, 0), (1, 1), (3, 2), (4, 2) })
                    {
                        var t = Next();
                        var verdict = e == 2 && Scenario(i, 2) && m is >= 6 and <= 8 ? "DROPPED" : "FORWARDED";
                        h.Add(t, "hubble", $"hubble/flow-{n}", null, new
                        {
                            flow = new { time = Iso(t), uuid = $"flow-{n++}", verdict, source = new { @namespace = Ns(i), cluster_name = "synth", pod_name = podNames[s] }, IP = new { source = Ip(12, i * 8L + s), destination = pe[e].Address }, destination_names = new[] { pe[e].Host }, destination = new { identity = 2 } },
                            node_name = NodeOf(i, s), time = Iso(t),
                        });
                    }
                }
            }
            records += h.Records;
        }

        // ---- Azure Resource Graph at 3 s: the cluster, its node machines, load balancer and network, and the platform of every namespace ----
        using (var a = new CaptureFile(Path.Combine(outDir, "arg", "resources.ndjson")))
        {
            void Arg(string rid, string name, string type, string rg, object? properties = null) =>
                a.Add(At(3), "arg", $"arg/resources/{rid}", null, new { id = rid, name, type, location = "north", resourceGroup = rg, subscriptionId = Sub, properties });
            Arg(clusterId, "aks-synth", "microsoft.containerservice/managedclusters", "rg-synth");
            for (var n = 0; n < nodeCount; n++) Arg($"/subscriptions/{Sub}/resourcegroups/{NodeRg}/providers/microsoft.compute/virtualmachines/{Node(n)}", Node(n), "microsoft.compute/virtualmachines", NodeRg);
            Arg($"/subscriptions/{Sub}/resourcegroups/{NodeRg}/providers/microsoft.network/loadbalancers/kubernetes", "kubernetes", "microsoft.network/loadbalancers", NodeRg);
            Arg(vnetId, "vnet-synth", "microsoft.network/virtualnetworks", NodeRg);
            for (var i = 0; i < slices; i++)
            {
                var rg = $"rg-{Ns(i)}"; var pe = Endpoints(i); var apim = $"{Rg(i)}/microsoft.apimanagement/service/apim-{i}";
                Arg(pe[0].Target, $"cache-{i}", "microsoft.cache/redis", rg);
                Arg(pe[1].Target, $"assets{i}", "microsoft.storage/storageaccounts", rg);
                Arg(pe[2].Target, $"pg-{i}", "microsoft.dbforpostgresql/flexibleservers", rg);
                Arg(apim, $"apim-{i}", "microsoft.apimanagement/service", rg);
                Arg($"{apim}/backends/api", "api", "microsoft.apimanagement/service/backends", rg, new { url = $"http://{Ip(13, i)}/api", protocol = "http" });
                foreach (var (name, address, targetId, host) in pe)
                    Arg($"{Rg(i)}/microsoft.network/privateendpoints/{name}", name, "microsoft.network/privateendpoints", rg, new
                    {
                        subnet = new { id = $"{vnetId}/subnets/endpoints" }, customDnsConfigs = new[] { new { fqdn = host, ipAddresses = new[] { address } } },
                        privateLinkServiceConnections = new[] { new { name, properties = new { privateLinkServiceId = targetId } } },
                    });
                var principal = $"9a1b2c3d-0000-4000-8000-{i:D12}";
                Arg($"{Rg(i)}/microsoft.managedidentity/userassignedidentities/id-web-{i}", $"id-web-{i}", "microsoft.managedidentity/userassignedidentities", rg, new { clientId = $"c1e7a0de-0000-4000-8000-{i:D12}", principalId = principal });
                Arg($"{pe[1].Target}/providers/microsoft.authorization/roleassignments/ra-web-blob-{i}", $"ra-web-blob-{i}", "microsoft.authorization/roleassignments", rg,
                    new { principalId = principal, scope = pe[1].Target, roleDefinitionId = "/providers/Microsoft.Authorization/roleDefinitions/2a2b9908-6ea1-4ae2-8e65-a410df84e7d1" });
            }
            records += a.Records;
        }

        // ---- Prometheus at 62 s of each minute: drop rate and forward rate, two samples per node and metric, 500 nodes per record ----
        using (var pr = new CaptureFile(Path.Combine(outDir, "prometheus", "range.ndjson")))
        {
            for (var m = 0; m < minutes; m++)
                foreach (var metric in new[] { "cilium_drop_rate", "cilium_forward_rate" })
                    for (var from = 0; from < nodeCount; from += 500)
                    {
                        var result = Enumerable.Range(from, Math.Min(500, nodeCount - from)).Select(n => new
                        {
                            metric = new { instance = Node(n), direction = "egress" },
                            values = new[] { 0, 1 }.Select(q => new object[] { t0.AddSeconds(m * 60 + q * 30).ToUnixTimeSeconds(), (metric == "cilium_drop_rate" ? 0.002 : 150 + 40 * Math.Sin(m + q + n)).ToString("F4", CultureInfo.InvariantCulture) }).ToArray(),
                        }).ToArray();
                        pr.Add(At(m * 60 + 62), "prometheus", $"prometheus/{metric}/{m}/{from}", new { name = metric, query = metric, step = "30s" }, new { status = "success", data = new { resultType = "matrix", result } });
                    }
            records += pr.Records;
        }

        // ---- Azure Monitor at 61 s of each minute: the cluster's CPU and one metric of each platform resource; scenario A (the cache) and B (the database) ----
        using (var am = new CaptureFile(Path.Combine(outDir, "azure-metrics", "platform.ndjson")))
        {
            void Metric(string rid, string name, int m, double value) =>
                am.Add(At(m * 60 + 61), "azure-metrics", $"azure-metrics/{name}/{rid}/{m}", new { resource = rid[(rid.LastIndexOf('/') + 1)..], resourceId = rid },
                    new { value = new[] { new { name = new { value = name }, timeseries = new[] { new { data = new[] { new { timeStamp = Iso(At(m * 60)), average = value } } } } } } });
            for (var m = 0; m < minutes; m++)
            {
                Metric(clusterId, "node_cpu_usage_percentage", m, 35 + m % 3);
                for (var i = 0; i < slices; i++)
                {
                    var pe = Endpoints(i);
                    Metric(pe[0].Target, "serverLoad", m, Scenario(i, 1) && m is >= 4 and <= 6 ? 85 : 20 + (m + i) % 4);
                    Metric(pe[2].Target, "is_db_alive", m, Scenario(i, 2) && m is >= 6 and <= 8 ? 0 : 1);
                    Metric($"{Rg(i)}/microsoft.apimanagement/service/apim-{i}", "FailedRequests", m, 0);
                    Metric(pe[1].Target, "Availability", m, 100);
                }
            }
            records += am.Records;
        }

        // ---- Activity Log: one operation a minute on the cache of one namespace in fifty ----
        using (var al = new CaptureFile(Path.Combine(outDir, "activity-log", "backfill.ndjson")))
        {
            for (var m = 0; m < minutes; m++)
                for (var i = m % 50; i < slices; i += 50)
                {
                    var t = At(m * 60 + 30);
                    al.Add(t.AddSeconds(2), "activity-log", $"activity-log/mgmt-{m}-{i}", new { via = "synthetic" }, new
                    {
                        eventTimestamp = Iso(t), resourceId = Endpoints(i)[0].Target, status = new { value = "Succeeded" }, operationName = new { value = "microsoft.cache/redis/write" }, eventDataId = $"mgmt-{m}-{i}", caller = "operator@example.invalid",
                    });
                }
            records += al.Records;
        }

        // ---- authored: the estate's capabilities of shop and its signal rules ----
        using (var au = new CaptureFile(Path.Combine(outDir, "authoring", "authoring.ndjson")))
        {
            foreach (var (cap, name, owner, parent) in new[] { ("commerce", "Commerce", "team-shop", (string?)null), ("storefront", "Storefront", "team-shop", "commerce"), ("ordering", "Ordering", "team-shop", "commerce"), ("data-platform", "Data platform", "team-data", null) })
                au.Add(At(0.5), "authoring", $"authoring/capability/{cap}", null, new { kind = "capability", capId = cap, name, owner, status = "active", parentId = parent });
            foreach (var (cap, svc) in new[] { ("storefront", "web"), ("ordering", "api"), ("data-platform", "database") })
                au.Add(At(0.6), "authoring", $"authoring/realizes/{cap}/{svc}", null, new { kind = "realizes", capId = cap, cluster = "synth", @namespace = "shop", service = svc });
            foreach (var (id, type, meter, above, ea, below, xa, sev, inv) in new[]
            {
                ("cpu-high", "microsoft.containerservice/managedclusters", "node_cpu_usage_percentage", 70.0, 3, 60.0, 3, 1, false), ("cpu-critical", "microsoft.containerservice/managedclusters", "node_cpu_usage_percentage", 90, 3, 80, 3, 2, false),
                ("node-drops", "node", "cilium_drop_rate", 5, 2, 2, 2, 1, false), ("node-drops-critical", "node", "cilium_drop_rate", 50, 2, 20, 2, 2, false),
                ("pod-warnings", "pod", "warnings", 1, 1, 0, 2, 1, false), ("pod-warnings-critical", "pod", "warnings", 2, 1, 0, 2, 2, false),
                ("cache-load", "microsoft.cache/redis", "serverLoad", 70, 2, 50, 2, 1, false),
                ("database-down", "microsoft.dbforpostgresql/flexibleservers", "is_db_alive", -0.5, 1, -0.9, 2, 2, true),
                ("gateway-failures", "microsoft.apimanagement/service", "FailedRequests", 10, 1, 1, 2, 2, false),
                ("storage-availability", "microsoft.storage/storageaccounts", "Availability", -99, 2, -99.5, 2, 1, true),
            })
                au.Add(At(0.7), "authoring", $"authoring/signalRule/{id}", null, new { kind = "signalRule", id, type, meter, above, enterAfter = ea, below, exitAfter = xa, severity = sev, invert = inv });
            records += au.Records;
        }
        Console.WriteLine($"# gen-estate: {slices:N0} namespaces, {masterNodes:N0} master nodes (pods {slices * 5L:N0}, Services {slices * 3L:N0}, Azure resources {slices * 10L + nodeCount + 3:N0}), {nodeCount:N0} Kubernetes nodes, {pairs:N0} active pod pairs and {4L * slices:N0} endpoint flows per minute, {minutes} minutes: {records:N0} records in {sw.Elapsed.TotalSeconds:F1} s to {outDir}");
        return 0;
    }
}
