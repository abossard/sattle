using Sattle.Core;
using Sattle.Runtime;
using Sattle.Kql;

namespace Sattle.Tests;

/// <summary>
/// The workload model (acme.workload) on the estate: the workload `shop` with web, api and the database Service, the nodes and the cluster they run on, and the Azure resources they use.
/// Composition, communication and dependency are three kinds of edges; each dependency is derived from what a source shows and keeps its evidence; health rolls up along dependency and composition only.
/// The estate's timeline has five scenarios (build-estate.py): A the cache overloaded in minutes 4 to 6, C one of three web pods crash-looping in minutes 8 to 10, B the database down in minutes 11 to 13, D API Management failing in minutes 16 to 18, E the storage account's metrics stopping after minute 18.
/// </summary>
public class WorkloadTests
{
    const long Min = 60_000_000_000;
    const string M = "d:acme.workload/prop:";
    static readonly DateTimeOffset T0 = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Lazy<Engine>> Runs = new();
    /// <summary>The estate replayed up to the capture time `seconds` after 10:00 (all of it for -1).</summary>
    static Engine At(int seconds) => Runs.GetOrAdd(seconds, s => new Lazy<Engine>(() =>
    {
        var cat = Repo.LoadExamples(out _);
        var space = cat.Spaces.Keys.Single();
        var engine = new Engine(cat, space, new EngineOptions { Tenant = "lab", LateHorizonWindows = 3 });
        new Replay(engine, cat.Spaces[space], AdapterRegistry.Create("synth"), new ReplayOptions
        {
            ChunkRows = 50, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth", UntilNs = s < 0 ? null : T0.AddSeconds(s).ToUnixTimeMilliseconds() * 1_000_000,
        }).Run(EstateTests.EstateDir);
        return engine;
    })).Value;

    static string? P(Entity e, string prop) => e.Props.TryGet(M + prop, out var v) ? v switch { TextValue t => t.S, IntegerValue i => i.N.ToString(), _ => v.ToString() } : null;
    static string NameOf(Graph g, string id) => g.Entity(id) is { } e ? P(e, "name") ?? P(e, "rule") ?? P(e, "detail") ?? id : id;
    static Dictionary<string, string?> States(Engine e) =>
        e.Graph("workload").State.Entities.Where(x => P(x, "state") is not null).ToDictionary(x => P(x, "kind") + ":" + P(x, "name"), x => P(x, "state"));
    static HashSet<string> Edges(Graph g, string type) =>
        [.. g.Rels.Where(r => Ids.Name(r.Type) == type).Select(r => NameOf(g, r.Source) + " -> " + NameOf(g, r.Target))];

    /// <summary>Code review 2, defect 4, on the workload model: the answer api gave to web is no dependency of api on web. Scenario A (the cache is overloaded and only web uses it) leaves api healthy. Before, the answer made api depend on web and api became Degraded.</summary>
    [Fact]
    public void A_service_does_not_depend_on_its_caller_through_the_answer_it_gave()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-workload-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var f in Directory.EnumerateFiles(EstateTests.EstateDir, "*.ndjson", SearchOption.AllDirectories))
            {
                var target = Path.Combine(dir, Path.GetRelativePath(EstateTests.EstateDir, f)); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target);
            }
            // what Hubble writes for the answer of api to web: the ends are those of the packet, is_reply says it is the answer
            File.WriteAllLines(Path.Combine(dir, "hubble", "replies.ndjson"), ["""{"v":1,"source":"hubble","capturedAt":"2026-01-01T10:00:20.000Z","seq":1,"key":"extra","meta":null,"payload":{"flow":{"time":"2026-01-01T10:00:20.000Z","uuid":"reply-0","verdict":"FORWARDED","is_reply":true,"source":{"namespace":"shop","cluster_name":"synth","pod_name":"api-5c9-1"},"destination":{"namespace":"shop","cluster_name":"synth","pod_name":"web-7d4-1"}},"node_name":"node-a","time":"2026-01-01T10:00:20.000Z"}}"""]);
            var cat = Repo.LoadExamples(out _);
            var space = cat.Spaces.Keys.Single();
            using var engine = new Engine(cat, space, new EngineOptions { Tenant = "lab", LateHorizonWindows = 3 });
            new Replay(engine, cat.Spaces[space], AdapterRegistry.Create("synth"), new ReplayOptions
            {
                ChunkRows = 50, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth", UntilNs = T0.AddSeconds(390).ToUnixTimeMilliseconds() * 1_000_000,
            }).Run(dir);
            Assert.DoesNotContain("api -> web", Edges(engine.Graph("workloadStructure").State, "DependsOn"));
            Assert.Contains("web -> api", Edges(engine.Graph("workloadStructure").State, "DependsOn"));
            Assert.Equal("Healthy", States(engine)["service:api"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- what the workload is made of ----

    [Theory]
    [InlineData("Contains", "shop -> web,shop -> api,shop -> database,web -> web,api -> api,web -> web-7d4-1,web -> web-7d4-2,web -> web-7d4-3,api -> api-5c9-1,api -> api-5c9-2")]
    [InlineData("HostedOn", "web-7d4-1 -> node-a,web-7d4-2 -> node-b,web-7d4-3 -> node-a,api-5c9-1 -> node-a,api-5c9-2 -> node-b")]
    [InlineData("MemberOf", "node-a -> apps,node-b -> apps,apps -> aks-synth")]
    [InlineData("BackedBy", "database -> pg-shop")]
    [InlineData("ConnectedThrough", "shopcache -> pe-shopcache,shopassets -> pe-shopassets,pg-shop -> pe-pg-shop,pe-shopcache -> vnet-synth,pe-shopassets -> vnet-synth,pe-pg-shop -> vnet-synth")]
    [InlineData("TalksTo", "web -> api,web -> shopcache,web -> shopassets,api -> database")]
    [InlineData("DependsOn", "web -> api,web -> shopcache,web -> shopassets,api -> database")]
    [InlineData("Exposes", "apim-shop -> api")]
    public void The_workload_graph_has_each_edge_the_estate_shows_and_no_other(string type, string expected)
    {
        Assert.Equal(expected.Split(',').Order(StringComparer.Ordinal), Edges(At(-1).Graph("workload").State, type).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Containment_is_a_tree_under_the_workload_and_every_node_has_a_name_and_an_edge()
    {
        var g = At(-1).Graph("workload").State;
        var parents = g.Rels.Where(r => Ids.Name(r.Type) == "Contains").GroupBy(r => r.Target).ToDictionary(x => x.Key, x => x.Count());
        Assert.All(parents.Values, n => Assert.Equal(1, n));                                                          // one parent each
        var roots = g.Rels.Where(r => Ids.Name(r.Type) == "Contains").Select(r => r.Source).Distinct().Where(s => !parents.ContainsKey(s)).ToList();
        Assert.Equal(["workload:shop"], roots.Select(r => P(g.Entity(r)!, "kind") + ":" + P(g.Entity(r)!, "name")));   // and one root, the workload
        var touched = g.Rels.SelectMany(r => new[] { r.Source, r.Target }).ToHashSet();
        Assert.All(g.Entities, e => Assert.True(touched.Contains(e.Id), $"{Ids.Name(e.Type)} {NameOf(g, e.Id)} has no edge"));
        Assert.All(g.Entities, e => Assert.False(string.IsNullOrEmpty(P(e, "name") ?? P(e, "rule") ?? P(e, "detail")), $"{Ids.Name(e.Type)} {e.Id} has no label"));
    }

    /// <summary>Each way of finding a dependency finds its own on the estate, and the evidence says what it saw.</summary>
    [Theory]
    [InlineData("privateEndpoint", "svc/synth/shop/web", "shopcache", "sends flows to 10.2.0.4, the address of private endpoint pe-shopcache")]
    [InlineData("dnsName", "svc/synth/shop/web", "shopassets", "sends flows to shopassets.blob.core.windows.net")]
    [InlineData("identity", "svc/synth/shop/web", "shopassets", "service account web runs as identity id-web")]
    [InlineData("configuration", "svc/synth/shop/api", "database", "environment variable DATABASE_HOST names database.shop.svc.cluster.local")]
    [InlineData("calls", "svc/synth/shop/web", "api", "calls pod api-5c9-")]
    [InlineData("backend", "/subscriptions/sub-0001/resourcegroups/rg-shop/providers/microsoft.apimanagement/service/apim-shop", "api", "backend api of apim-shop sends requests to 10.1.0.20")]
    public void Each_way_of_finding_a_dependency_finds_it_and_keeps_what_it_saw(string how, string source, string targetName, string seen)
    {
        var g = At(-1).Graph("workload").State;
        var evidence = g.Entities.Where(e => P(e, "how") == how && P(e, "sourceKey") == source).ToList();
        Assert.Contains(evidence, e => P(e, "targetKey")!.EndsWith("/" + targetName, StringComparison.Ordinal) && P(e, "detail")!.Contains(seen, StringComparison.Ordinal));
    }

    /// <summary>Why does web depend on the cache? The dependency has its evidence, one element per way it was found, and the lineage of each evidence element leads to the master facts it was found from.</summary>
    [Theory]
    [InlineData("privateEndpoint", "Pod,Destination,AzureResource")]
    [InlineData("dnsName", "Pod,Destination,AzureResource")]
    [InlineData("configuration", "HostReference,AzureResource")]
    public void Why_web_depends_on_the_cache_is_answered_by_its_evidence_and_the_lineage_of_the_evidence(string how, string masterTypes)
    {
        using var h = new SpaceHost(Sattle.Language.Catalog.ReadDirectory(Repo.Definitions), new HostOptions { Projections = new KqlSource(), Cluster = "synth" });
        new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 50, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" }).Run(EstateTests.EstateDir);
        var structure = h.Engine.Graph("workloadStructure").State;
        Assert.Contains(structure.Rels, r => Ids.Name(r.Type) == "DependsOn" && NameOf(structure, r.Source) == "web" && NameOf(structure, r.Target) == "shopcache");
        var evidence = structure.Entities.Single(e => P(e, "how") == how && P(e, "sourceKey") == "svc/synth/shop/web" && P(e, "targetKey")!.EndsWith("/shopcache", StringComparison.Ordinal));
        Assert.Contains(structure.Rels, r => Ids.Name(r.Type) == "EvidenceFor" && r.Source == evidence.Id && NameOf(structure, r.Target) == "web");
        var master = h.Engine.Master.State;
        string TypeOf(string id) => master.Entity(id)?.Type ?? master.Rels.FirstOrDefault(r => r.Id == id)?.Type ?? "";
        var sources = h.Lineage("workloadStructure", evidence.Id)["sources"]!.AsArray();
        Assert.All(sources, x => Assert.Equal("master", x!["graph"]!.GetValue<string>()));
        var types = sources.Select(x => Ids.Name(TypeOf(x!["element"]!.GetValue<string>()))).ToHashSet();
        Assert.True(masterTypes.Split(',').All(types.Contains), $"{how}: the lineage names {string.Join(", ", types.Order())}");
    }

    // ---- health: the five scenarios ----

    /// <summary>The state of each component a user looks at, at a capture time in the middle or after each scenario (the frontier lags the capture time by a minute).</summary>
    [Theory]
    [InlineData("before anything happens", 210, "workload:shop=Healthy, service:web=Healthy, service:api=Healthy, service:database=Healthy, resource:shopcache=Healthy")]
    [InlineData("A: the cache is overloaded: web uses it and is Degraded with its workload, api does not and stays Healthy", 390, "resource:shopcache=Degraded, service:web=Degraded, workload:shop=Degraded, service:api=Healthy, service:database=Healthy")]
    [InlineData("C: one of three web pods crash-loops: the pod is Unhealthy, its deployment and web stay Healthy by the share of pods", 630, "pod:web-7d4-3=Unhealthy, deployment:web=Healthy, service:web=Healthy, workload:shop=Healthy")]
    [InlineData("B: the database is down: what needs it is Unhealthy up to API Management and the workload; the cache and the storage are not", 810, "resource:pg-shop=Unhealthy, service:database=Unhealthy, service:api=Unhealthy, service:web=Unhealthy, gateway:apim-shop=Unhealthy, workload:shop=Unhealthy, resource:shopcache=Healthy, resource:shopassets=Healthy")]
    [InlineData("D: API Management fails requests: it is Unhealthy, api behind it is not, and nothing that contains api is", 1050, "gateway:apim-shop=Unhealthy, service:api=Healthy, service:web=Healthy, workload:shop=Healthy")]
    [InlineData("E: the storage account's metrics stopped: it is in no data, and so are web and the workload; api is not", 1410, "resource:shopassets=No data, service:web=No data, workload:shop=No data, service:api=Healthy")]
    public void Each_scenario_gives_the_state_it_should(string scenario, int seconds, string expected)
    {
        var states = States(At(seconds));
        foreach (var pair in expected.Split(", "))
        {
            var (component, state) = (pair[..pair.IndexOf('=')], pair[(pair.IndexOf('=') + 1)..]);
            Assert.True(states.TryGetValue(component, out var actual) && actual == state, $"{scenario}: {component} is {(states.TryGetValue(component, out var a) ? a : "absent")}, expected {state}");
        }
    }

    /// <summary>The share rule keeps the rank of no data (content review): the web deployment's pods are given as values in place of their signals, and the estate runs to the minute before the first scenario, when everything else is healthy. A pod without a severity does not count; a deployment none of whose pods has one is Unknown.</summary>
    [Theory]
    [InlineData("all three pods in no data", "1,1,1", "No data", "No data")]
    [InlineData("one of three pods in no data", "0,0,1", "Healthy", "Healthy")]
    [InlineData("half in no data, half unhealthy", "1,3", "Unhealthy", "Unhealthy")]
    [InlineData("half in no data, half degraded", "1,2", "Degraded", "Degraded")]
    [InlineData("two of three degraded, one unhealthy", "2,2,3", "Degraded", "Degraded")]
    [InlineData("no pod measured", "", "Unknown", "Healthy")]
    public void A_deployment_takes_the_rank_that_at_least_half_of_its_measured_pods_reach(string pods, string severities, string deployment, string service)
    {
        const string Key = "deploy/synth/shop/web";
        var rows = severities.Length == 0 ? "\"none\", \"none\", \"none\", 0"
            : string.Join(", ", severities.Split(',').Select((sev, i) => $"\"{Key}\", \"pod{i}\", \"pod{i}\", {sev}"));
        const string PodsOfDeployment = "graph(\"structure\")\n    | graph-match (d)-[c]->(p) where labels(d) has \"WorkloadDeployment\" and labels(c) has \"Contains\" and labels(p) has \"WorkloadPod\" project dk = d.key, pk2 = p.key\n    | join kind=inner (ownByKey) on $left.pk2 == $right.k";
        var files = Sattle.Language.Catalog.ReadDirectory(Repo.Definitions).Select(f => !f.Path.EndsWith("acme.workload.workloadHealth.kql", StringComparison.Ordinal) ? f : f with
        {
            Text = f.Text.Contains(PodsOfDeployment, StringComparison.Ordinal)
                ? f.Text.Replace(PodsOfDeployment, $"datatable(dk:string, pk2:string, k:string, own:long) [{rows}]")
                : throw new InvalidOperationException("the statement of the pods of a deployment changed"),
        }).ToList();
        var cat = new Sattle.Language.Catalog(new KqlSource());
        var res = cat.Load(files, updatePins: true);
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
        var space = cat.Spaces.Keys.Single();
        using var engine = new Engine(cat, space, new EngineOptions { Tenant = "lab", LateHorizonWindows = 3 });
        new Replay(engine, cat.Spaces[space], AdapterRegistry.Create("synth"), new ReplayOptions
        {
            ChunkRows = 50, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth", UntilNs = T0.AddSeconds(210).ToUnixTimeMilliseconds() * 1_000_000,
        }).Run(EstateTests.EstateDir);
        var states = States(engine);
        Assert.Equal((deployment, service), (states["deployment:web"], states["service:web"]));
        _ = pods;
    }

    /// <summary>#95: a pod of the namespace that backs no Service (here `probe`, a client like the lab's intruder) is part of the workload under its deployment, and the calls of it that are dropped (by a network policy) are its own signal: the caller is to blame, not the pods it calls. Its warning events count like those of any pod (review). The estate gets a deployment `probe` with one pod on node-a, calls from it to api-5c9-1 every 5 s in minutes 1 to 3, and the rule `pod-drops`; the replay stops at 10:04:30, before scenario A.</summary>
    [Theory]
    [InlineData("FORWARDED", 0, "pod:probe-6f8-1=Healthy, deployment:probe=Healthy, pod:api-5c9-1=Healthy, service:api=Healthy")]
    [InlineData("DROPPED", 0, "pod:probe-6f8-1=Unhealthy, deployment:probe=Unhealthy, workload:shop=Unhealthy, pod:api-5c9-1=Healthy, service:api=Healthy")]
    [InlineData("FORWARDED", 3, "pod:probe-6f8-1=Unhealthy, deployment:probe=Unhealthy, workload:shop=Unhealthy, pod:api-5c9-1=Healthy, service:api=Healthy")]
    public void A_pod_that_backs_no_Service_is_in_the_workload_and_its_dropped_calls_blame_it(string verdict, int warningsPerMinute, string expected)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var f in Directory.EnumerateFiles(EstateTests.EstateDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(EstateTests.EstateDir, f); if (rel.StartsWith("space", StringComparison.Ordinal)) continue;
                var target = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(f, target);
            }
            static string At(double sec) => T0.AddSeconds(sec).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
            static string Line(string source, double sec, string key, object payload) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?> { ["v"] = 1, ["source"] = source, ["capturedAt"] = At(sec), ["seq"] = 0, ["key"] = key, ["meta"] = null, ["payload"] = payload });
            File.WriteAllLines(Path.Combine(dir, "kubernetes", "probe.ndjson"),
            [
                Line("kubernetes-watch", 0.3, "kubernetes-watch/ADDED/Deployment/dep-probe/950", new { type = "ADDED", @object = new { kind = "Deployment", spec = new { replicas = 1 }, metadata = new { name = "probe", uid = "dep-probe", @namespace = "shop", resourceVersion = "950" } } }),
                Line("kubernetes-watch", 1.5, "kubernetes-watch/ADDED/Pod/uid-probe-6f8-1/951", new { type = "ADDED", @object = new
                {
                    kind = "Pod", spec = new { nodeName = "node-a" },
                    status = new { phase = "Running", conditions = new[] { new { type = "Ready", status = "True" } }, containerStatuses = new[] { new { restartCount = 0 } } },
                    metadata = new { name = "probe-6f8-1", uid = "uid-probe-6f8-1", @namespace = "shop", resourceVersion = "951", ownerReferences = new[] { new { kind = "ReplicaSet", name = "probe-6f8" } } },
                } }),
            ]);
            File.AppendAllLines(Path.Combine(dir, "kubernetes", "probe.ndjson"), Enumerable.Range(1, warningsPerMinute > 0 ? 3 : 0).Select(m => Line("kubernetes-watch", m * 60 + 5, $"kubernetes-watch/ADDED/Event/ev-probe-{m}/96{m}", new
            {
                type = "ADDED", @object = new
                {
                    kind = "Event", involvedObject = new { kind = "Pod", name = "probe-6f8-1", @namespace = "shop" }, reason = "BackOff", type = "Warning", message = "back-off restarting container",
                    count = warningsPerMinute, lastTimestamp = At(m * 60 + 5), metadata = new { name = $"probe-6f8-1.{m}", uid = $"ev-probe-{m}", resourceVersion = $"96{m}", @namespace = "shop" },
                },
            })));
            File.WriteAllLines(Path.Combine(dir, "hubble", "probe.ndjson"), Enumerable.Range(0, 36).Select(i => 60 + i * 5.0).Select(sec => Line("hubble", sec + 0.007, $"hubble/probe-{sec}", new
            {
                flow = new { time = At(sec + 0.007), uuid = $"probe-{sec}", verdict, source = new { @namespace = "shop", cluster_name = "synth", pod_name = "probe-6f8-1" }, destination = new { @namespace = "shop", cluster_name = "synth", pod_name = "api-5c9-1" } },
                node_name = "node-a", time = At(sec + 0.007),
            })));
            File.WriteAllLines(Path.Combine(dir, "authoring", "probe.ndjson"),
                [Line("authoring", 0.8, "authoring/signalRule/pod-drops", new { kind = "signalRule", id = "pod-drops", type = "pod", meter = "drops", above = 5, enterAfter = 1, below = 0, exitAfter = 2, severity = 2, invert = false })]);
            var cat = Repo.LoadExamples(out _);
            var space = cat.Spaces.Keys.Single();
            using var engine = new Engine(cat, space, new EngineOptions { Tenant = "lab", LateHorizonWindows = 3 });
            new Replay(engine, cat.Spaces[space], AdapterRegistry.Create("synth"), new ReplayOptions
            {
                ChunkRows = 50, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth", UntilNs = T0.AddSeconds(270).ToUnixTimeMilliseconds() * 1_000_000,
            }).Run(dir);
            var structure = engine.Graph("workloadStructure").State;
            Assert.Contains("shop -> probe", Edges(structure, "Contains"));
            Assert.Contains("probe -> probe-6f8-1", Edges(structure, "Contains"));
            Assert.Contains("probe-6f8-1 -> node-a", Edges(structure, "HostedOn"));
            var states = States(engine);
            foreach (var pair in expected.Split(", "))
            {
                var (component, state) = (pair[..pair.IndexOf('=')], pair[(pair.IndexOf('=') + 1)..]);
                Assert.True(states.TryGetValue(component, out var actual) && actual == state, $"{verdict}: {component} is {(states.TryGetValue(component, out var a) ? a : "absent")}, expected {state}");
            }
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ---- when pods, endpoints and flows go away (#102) ----

    /// <summary>The estate with the lines `keep` lets through and the extra lines per file, replayed up to `seconds` after 10:00.</summary>
    static Engine Changed(Func<string, bool> keep, (string File, string[] Lines)[] extra, int seconds)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-changed-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var f in Directory.EnumerateFiles(EstateTests.EstateDir, "*.ndjson", SearchOption.AllDirectories))
            {
                var target = Path.Combine(dir, Path.GetRelativePath(EstateTests.EstateDir, f)); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllLines(target, File.ReadLines(f).Where(keep));
            }
            foreach (var (file, lines) in extra) File.AppendAllLines(Path.Combine(dir, file), lines);
            var cat = Repo.LoadExamples(out _);
            var space = cat.Spaces.Keys.Single();
            var engine = new Engine(cat, space, new EngineOptions { Tenant = "lab", LateHorizonWindows = 3 });
            new Replay(engine, cat.Spaces[space], AdapterRegistry.Create("synth"), new ReplayOptions
            {
                ChunkRows = 50, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth", UntilNs = T0.AddSeconds(seconds).ToUnixTimeMilliseconds() * 1_000_000,
            }).Run(dir);
            return engine;
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    static string Record(string source, double sec, string key, object payload) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        { ["v"] = 1, ["source"] = source, ["capturedAt"] = T0.AddSeconds(sec).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture), ["seq"] = 0, ["key"] = key, ["meta"] = null, ["payload"] = payload });
    static string Watch(double sec, string type, string kind, string uid, int version, object obj) => Record("kubernetes-watch", sec, $"kubernetes-watch/{type}/{kind}/{uid}/{version}", new { type, @object = obj });
    static HashSet<string> KeyEdges(Graph g, string type) =>
        [.. g.Rels.Where(r => Ids.Name(r.Type) == type).Select(r => (g.Entity(r.Source) is { } s ? P(s, "key") : r.Source) + " -> " + (g.Entity(r.Target) is { } t ? P(t, "key") : r.Target))];

    /// <summary>A Deployment stays in the workload whatever happens to its pods. At 10:02 the api EndpointSlice empties ("leave"), or api is scaled to 0: its pods deleted, the slice emptied ("scale0"); with "return" a pod is back in the slice at 10:03.
    /// A Deployment none of whose pods is in a Service's EndpointSlices is under the workload instead of its Service (not ready pods stay in the slice, so this takes pods without an address); one scaled to 0 keeps its node with no pods and is Unknown. Either way the Service has nothing left under it and is Unknown until a pod is back. The replay stops at 10:03:30, before scenario A; api-5c9-2 is first seen at 10:05:30.</summary>
    [Theory]
    [InlineData("leave", "ns/synth/shop -> deploy/synth/shop/api,deploy/synth/shop/api -> pod/synth/shop/api-5c9-1", "svc/synth/shop/api -> deploy/synth/shop/api", "deployment:api=Healthy, service:api=Unknown, workload:shop=Healthy")]
    [InlineData("scale0", "ns/synth/shop -> deploy/synth/shop/api", "svc/synth/shop/api -> deploy/synth/shop/api,deploy/synth/shop/api -> pod/synth/shop/api-5c9-1", "deployment:api=Unknown, service:api=Unknown, workload:shop=Healthy")]
    [InlineData("return", "svc/synth/shop/api -> deploy/synth/shop/api,deploy/synth/shop/api -> pod/synth/shop/api-5c9-1", "ns/synth/shop -> deploy/synth/shop/api", "deployment:api=Healthy, service:api=Healthy, workload:shop=Healthy")]
    public void A_deployment_stays_in_the_workload_when_its_pods_leave_the_EndpointSlice_or_it_is_scaled_to_0(string change, string contained, string notContained, string expected)
    {
        object Slice(params (string Address, string Pod)[] endpoints) => new
        {
            kind = "EndpointSlice", endpoints = endpoints.Select(e => new { addresses = new[] { e.Address }, targetRef = new { kind = "Pod", uid = e.Pod } }).ToArray(),
            metadata = new { name = "api-slice", uid = "slice-api", resourceVersion = "980", @namespace = "shop", labels = new Dictionary<string, string> { ["kubernetes.io/service-name"] = "api" } },
        };
        object Pod(string name, string node) => new
        {
            kind = "Pod", spec = new { nodeName = node, serviceAccountName = "api" }, status = new { phase = "Running" },
            metadata = new { name, uid = "uid-" + name, resourceVersion = "981", @namespace = "shop", ownerReferences = new[] { new { kind = "ReplicaSet", name = "api-5c9" } } },
        };
        List<string> lines = [Watch(120, "MODIFIED", "EndpointSlice", "slice-api", 980, Slice())];
        if (change == "scale0")
            lines.AddRange([
                Watch(120.1, "MODIFIED", "Deployment", "dep-api", 982, new { kind = "Deployment", spec = new { replicas = 0 }, metadata = new { name = "api", uid = "dep-api", resourceVersion = "982", @namespace = "shop" } }),
                Watch(120.2, "DELETED", "Pod", "uid-api-5c9-1", 983, Pod("api-5c9-1", "node-a")),
                Watch(120.2, "DELETED", "Pod", "uid-api-5c9-2", 984, Pod("api-5c9-2", "node-b")),
            ]);
        if (change == "return") lines.Add(Watch(180, "MODIFIED", "EndpointSlice", "slice-api", 985, Slice(("10.0.1.1", "uid-api-5c9-1"))));
        using var engine = Changed(_ => true, [("kubernetes/changes.ndjson", [.. lines])], 210);
        var contains = KeyEdges(engine.Graph("workloadStructure").State, "Contains");
        foreach (var edge in contained.Split(',')) Assert.Contains(edge, contains);
        foreach (var edge in notContained.Split(',')) Assert.DoesNotContain(edge, contains);
        var states = States(engine);
        foreach (var pair in expected.Split(", "))
        {
            var (component, state) = (pair[..pair.IndexOf('=')], pair[(pair.IndexOf('=') + 1)..]);
            Assert.True(states.TryGetValue(component, out var actual) && actual == state, $"{change}: {component} is {actual ?? "absent"}, expected {state}");
        }
    }

    /// <summary>The dropped calls of a pod (rule `pod-drops`) when its calls stop and when Hubble goes silent, both at 10:01. A pod that stops calling while Hubble reports has 0 dropped calls in every window, so its signal is off; when Hubble is silent the signal is in no data once a closed window is five minutes past its last flow (from 10:08 with a minute of lateness), and the pods it does not measure are not. The one call of web-7d4-2 is first matched at the 10:06 advance, so its time to live counts from there (Def. 7.16): it is in no data from 10:12, and until then it keeps the pod's signal out of no data (a roll-up key is in no data when every member with a record is). The replay stops at `seconds` after 10:00; scenario C (web-7d4-3 crash-looping from 10:08) is why the silent cases look at web-7d4-1 and web-7d4-2.</summary>
    [Theory]
    [InlineData("web-7d4-3 stops calling", 450, "pod:web-7d4-3=Healthy, pod:web-7d4-1=Healthy, pod:api-5c9-1=Healthy", "web-7d4-3", "off")]
    [InlineData("Hubble silent for 3 minutes", 240, "pod:web-7d4-1=Healthy, pod:web-7d4-2=Healthy, pod:api-5c9-1=Healthy", "web-7d4-1", "off")]
    [InlineData("Hubble silent for more than 5 minutes", 540, "pod:web-7d4-1=No data, pod:web-7d4-2=Healthy, pod:api-5c9-1=Healthy", "web-7d4-1", "nodata")]
    [InlineData("Hubble silent for more than 5 minutes", 540, "pod:web-7d4-1=No data, pod:web-7d4-2=Healthy, pod:api-5c9-1=Healthy", "web-7d4-2", "off")]
    [InlineData("Hubble silent for more than 5 minutes", 720, "pod:web-7d4-1=No data, pod:web-7d4-2=No data, pod:api-5c9-1=Healthy", "web-7d4-2", "nodata")]
    public void A_pod_whose_calls_stop_is_healthy_and_one_Hubble_stopped_reporting_on_is_in_no_data(string change, int seconds, string expected, string pod, string signalState)
    {
        bool Keep(string line)
        {
            if (!line.Contains("\"source\":\"hubble\"", StringComparison.Ordinal)) return true;
            var flow = System.Text.Json.JsonDocument.Parse(line).RootElement;
            if (DateTimeOffset.Parse(flow.GetProperty("capturedAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture) < T0.AddSeconds(60)) return true;
            return change.StartsWith("web-7d4-3", StringComparison.Ordinal) && !line.Contains("\"pod_name\":\"web-7d4-3\"", StringComparison.Ordinal);
        }
        var rule = Record("authoring", 0.8, "authoring/signalRule/pod-drops", new { kind = "signalRule", id = "pod-drops", type = "pod", meter = "drops", above = 5, enterAfter = 1, below = 0, exitAfter = 2, severity = 2, invert = false });
        using var engine = Changed(Keep, [("authoring/pod-drops.ndjson", [rule])], seconds);
        var states = States(engine);
        foreach (var pair in expected.Split(", "))
        {
            var (component, state) = (pair[..pair.IndexOf('=')], pair[(pair.IndexOf('=') + 1)..]);
            Assert.True(states.TryGetValue(component, out var actual) && actual == state, $"{change}: {component} is {actual ?? "absent"}, expected {state}");
        }
        var signal = engine.Graph("workload").State.Entities.Single(e => P(e, "componentKey") == "pod/synth/shop/" + pod && P(e, "rule") == "pod-drops");
        Assert.Equal(signalState, P(signal, "signalState"));
    }

    [Fact]
    public void An_observed_call_carries_no_failure_API_Management_needs_api_and_not_the_other_way()
    {
        var g = At(-1).Graph("workloadStructure").State;
        var affects = Edges(g, "Affects");
        Assert.Contains("apim-shop -> api", affects);           // the gateway is affected by its backend
        Assert.DoesNotContain("api -> apim-shop", affects);
        Assert.DoesNotContain(affects, a => Edges(g, "TalksTo").Contains(a) && !Edges(g, "DependsOn").Contains(a));       // every roll-up edge between services comes from a dependency
    }

    [Theory]
    [InlineData(1)] [InlineData(50)] [InlineData(100000)]
    public void The_workload_graphs_do_not_depend_on_the_chunk_size_and_conform_to_their_ontology(int chunk)
    {
        var cat = Repo.LoadExamples(out _);
        var space = cat.Spaces.Keys.Single();
        using var engine = new Engine(cat, space, new EngineOptions { Tenant = "lab", LateHorizonWindows = 3 });
        new Replay(engine, cat.Spaces[space], AdapterRegistry.Create("synth"), new ReplayOptions { ChunkRows = chunk, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" }).Run(EstateTests.EstateDir);
        foreach (var name in new[] { "workloadStructure", "workload" })
        {
            Assert.Equal(At(-1).Graph(name).State.Digest(), engine.Graph(name).State.Digest());
            Assert.Empty(Conformance.Check(engine.Graph(name).State, cat.OntologyOf("acme.workload")));
        }
    }

    [Trait("Category", "Capture")]
    [CaptureTheory]
    [MemberData(nameof(CaptureNames))]
    public void On_a_real_capture_the_workload_graphs_build_with_no_dangling_edge(string capture)
    {
        var dir = Path.Combine(Repo.Captures, capture);
        var r = CaptureRuns.Run(dir);
        foreach (var name in new[] { "workloadStructure", "workload" })
        {
            var g = r.Engine.Graph(name).State;
            Assert.Empty(g.Rels.Where(x => g.Entity(x.Source) is null || g.Entity(x.Target) is null).Select(x => Ids.Name(x.Type) + " " + x.Source + " -> " + x.Target));
        }
        Assert.Contains(r.Engine.Graph("workload").State.Entities, e => P(e, "kind") == "workload");
    }

    public static IEnumerable<object[]> CaptureNames() => Repo.CaptureDirs.Count > 0 ? Repo.CaptureDirs.Select(d => new object[] { Path.GetFileName(d) }) : [[""]];
}
