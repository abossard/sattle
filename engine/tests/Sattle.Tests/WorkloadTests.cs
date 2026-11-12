using Sattle.Core;
using Sattle.Runtime;

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
        using var h = new SpaceHost(Sattle.Language.Catalog.ReadDirectory(Repo.Definitions), new HostOptions { Cluster = "synth" });
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
        var rows = severities.Length == 0 ? "values { dk: \"none\", pk2: \"none\", k: \"none\", own: 0 }"
            : "values " + string.Join(", ", severities.Split(',').Select((sev, i) => $"{{ dk: \"{Key}\", pk2: \"pod{i}\", k: \"pod{i}\", own: {sev} }}"));
        var files = Sattle.Language.Catalog.ReadDirectory(Repo.Definitions).Select(f => !f.Path.EndsWith("acme.workload.sattle", StringComparison.Ordinal) ? f : f with
        {
            Text = f.Text.Contains("  let podsOfDeployment = join deployed, ownByKey on pk2 == k;", StringComparison.Ordinal)
                ? f.Text.Replace("  let podsOfDeployment = join deployed, ownByKey on pk2 == k;", $"  let podsOfDeployment = {rows};\n  let unusedPods = join deployed, ownByKey on pk2 == k;")
                : throw new InvalidOperationException("the statement of the pods of a deployment changed"),
        }).ToList();
        var cat = new Sattle.Language.Catalog();
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
