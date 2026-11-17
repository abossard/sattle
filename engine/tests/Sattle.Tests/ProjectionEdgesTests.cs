using System.Text.Json;
using System.Text.RegularExpressions;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>A projection is an abstraction of the graph below it, with its edges: an edge between two lower nodes becomes an edge between their images (README, "Edge lifting"). On the synthetic estate every projection whose output ontology declares relationship types has relationships, the capabilities carry the service dependencies one level up with the telemetry summed, and the policies for the three special cases hold: self-loops are dropped, parallel edges collapse onto one with the sums, and an end without an image is not lifted.</summary>
public class ProjectionEdgesTests
{
    static string Estate => EstateTests.EstateDir;

    /// <summary>Graphs of the space that declare relationship types in their output ontology and are exempt from "has relationships on the estate", with the reason. Everything else must have them.</summary>
    internal static readonly Dictionary<string, string> Exempt = new()
    {
        ["deploymentReport"] = "a document: ordered blocks, its ontology (sattle.doc) declares no relationship type",
        ["healthReport"] = "a document: ordered blocks, its ontology (sattle.doc) declares no relationship type",
        ["treeSignals"] = "a list of signals: a signal names its element in a property, the hierarchy and the evidence edges are in the other graphs of the tree",
    };

    public static IEnumerable<object[]> Graphs() => Repo.LoadExamples(out _).Spaces.Values.Single().Graphs.Select(g => new object[] { g.Name });

    /// <summary>The shared run of the estate, read as it is served.</summary>
    static ReplayTests.Run OnEstate => Scenarios.Run(Estate);

    [Theory]
    [MemberData(nameof(Graphs))]
    public void A_projection_whose_output_ontology_declares_relationship_types_has_relationships_on_the_estate(string graph)
    {
        var e = OnEstate; var (entities, relationships) = (e.Entities(graph).Count, e.Relationships(graph).Count);
        var cat = Repo.LoadExamples(out _);
        var module = e.Engine.Graph(graph).Typed.OutputModule;
        var declared = cat.OntologyOf(module).Types.Values.Where(t => t.IsRel && Ids.ModuleOf(t.Id) == module).Select(t => t.Id).ToList();
        if (declared.Count == 0) { Assert.True(Exempt.ContainsKey(graph) || entities > 0); return; }
        Assert.True(entities > 0, $"{graph} has no entities on the estate");
        if (Exempt.ContainsKey(graph)) return;
        Assert.True(relationships > 0, $"{graph} declares {string.Join(", ", declared.Select(Ids.Name))} and emits {entities} entities and no relationship on the estate");
    }

    [Fact]
    public void The_exemptions_are_graphs_of_the_space_and_each_still_has_entities_but_no_relationship_to_wait_for()
    {
        var names = Repo.LoadExamples(out _).Spaces.Values.Single().Graphs.Select(g => g.Name).ToHashSet();
        Assert.All(Exempt.Keys, k => Assert.Contains(k, names));
        Assert.All(Exempt.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
    }

    // ---- the capability level of the estate -------------------------------------------------------------------------------------------

    /// <summary>The names of the entities of a served graph, by id.</summary>
    static Dictionary<string, string> Names(ReplayTests.Run e, string graph) => e.Entities(graph).ToDictionary(x => x["id"].Id(), x => x.Prop("name") ?? "");

    /// <summary>The summed `prop` windows of every `DependsOn` relationship of `graph`, by the names of its two ends, which are entities of the graph `ends`.</summary>
    static Dictionary<string, decimal> Summed(ReplayTests.Run e, string graph, string prop, string ends)
    {
        var name = Names(e, ends);
        var rels = e.Relationships(graph, "DependsOn").ToDictionary(r => r["id"].Id(), r => name[r["source"].Id()] + " -> " + name[r["target"].Id()]);
        var sums = new Dictionary<string, decimal>();
        foreach (var w in e.Windows(graph, prop))
            if (rels.TryGetValue(w.Element, out var pair)) sums[pair] = sums.GetValueOrDefault(pair) + w.Value;
        return sums;
    }

    [Fact]
    public void The_estate_capabilities_have_a_hierarchy_and_a_dependency_lifted_from_the_service_level_with_the_same_flows()
    {
        var e = OnEstate; var caps = Names(e, "capabilities");
        Assert.Equal(4, e.Entities("capabilities", "BizCapability").Count);
        var under = e.Relationships("capabilities", "Under").Select(r => caps[r["source"].Id()] + " -> " + caps[r["target"].Id()]).Order().ToList();
        Assert.Equal(["Ordering -> Commerce", "Storefront -> Commerce"], under);
        var lifted = Summed(e, "capabilities", "callFlows", "capabilities");
        var service = Summed(e, "serviceTree", "flows", "serviceTree");
        Assert.Single(lifted);                                                                           // database has no pods, so nothing calls into data-platform
        Assert.Equal(service["web -> api"], lifted["Storefront -> Ordering"]);
        Assert.True(lifted.Values.All(v => v > 0));
    }

    [Fact]
    public void On_the_estate_the_flows_out_of_the_service_groups_are_in_every_window_the_sum_of_the_flows_out_of_the_services()
    {
        var e = OnEstate;
        var groups = e.Entities("serviceTree", "ServiceGroup").Select(x => x["id"].Id()).ToHashSet();
        var perWindow = e.Windows("serviceTree", "flowsOut").GroupBy(x => x.Window).ToList();
        Assert.NotEmpty(perWindow);
        foreach (var w in perWindow)
            Assert.Equal(w.Where(x => !groups.Contains(x.Element)).Sum(x => x.Value), w.Where(x => groups.Contains(x.Element)).Sum(x => x.Value));
    }

    // ---- the policies of the lift, on variants of the estate's authored capabilities and flows ----------------------------------------

    /// <summary>A copy of the estate whose authored `realizes` rows (service to capability, null: none) and flows differ.</summary>
    public static string CopyEstate(string?[] realizesWebApiDb, bool extraWebToDb, string? retired) => Copy(realizesWebApiDb, extraWebToDb, retired);

    static string Copy(string?[] realizesWebApiDb, bool extraWebToDb, string? retired)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-lift-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        foreach (var f in Directory.EnumerateFiles(Estate, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Estate, f); if (rel.StartsWith("space", StringComparison.Ordinal)) continue;
            var target = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(f, target);
        }
        var lines = File.ReadAllLines(Path.Combine(Estate, "authoring", "capabilities.ndjson")).Where(l => !l.Contains("\"kind\":\"realizes\"")).ToList();
        if (retired is not null) lines = [.. lines.Select(l => l.Contains($"\"capId\":\"{retired}\"") && l.Contains("\"kind\":\"capability\"") ? l.Replace("\"status\":\"active\"", "\"status\":\"retired\"") : l)];
        var services = new[] { "web", "api", "db" };
        for (var i = 0; i < 3; i++)
            foreach (var cap in (realizesWebApiDb[i] ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
                lines.Add($"{{\"v\":1,\"source\":\"authoring\",\"capturedAt\":\"2026-01-01T10:00:00.600Z\",\"seq\":{100 + i * 10 + Array.IndexOf((realizesWebApiDb[i] ?? "").Split('|'), cap)},\"key\":\"authoring/realizes/{cap}/{services[i]}\",\"meta\":null,\"payload\":{{\"kind\":\"realizes\",\"capId\":\"{cap}\",\"cluster\":\"synth\",\"namespace\":\"shop\",\"service\":\"{services[i]}\"}}}}");
        File.WriteAllLines(Path.Combine(dir, "authoring", "capabilities.ndjson"), lines);
        AddDb(dir, extraWebToDb);
        return dir;
    }

    /// <summary>A third service behind api for the lift tests: Deployment, Service and pod `db` and its endpoint slice, flows from both api pods to the db pod every 5 s for ten minutes (every other one dropped in minutes 4 to 7), and with `extraWebToDb` the same flows from web-7d4-1 too. The shipped estate's database is an ExternalName Service without pods.</summary>
    static void AddDb(string dir, bool extraWebToDb)
    {
        static string At(double sec) => new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero).AddSeconds(sec).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        static string Line(string source, double sec, string key, object payload) =>
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["v"] = 1, ["source"] = source, ["capturedAt"] = At(sec), ["seq"] = 0, ["key"] = key, ["meta"] = null, ["payload"] = payload });
        object Obj(string kind, string uid, string name, Dictionary<string, object> extra) =>
            new Dictionary<string, object>(extra) { ["kind"] = kind, ["metadata"] = new Dictionary<string, object>((Dictionary<string, object>?)extra.GetValueOrDefault("metadata") ?? []) { ["name"] = name, ["uid"] = uid, ["namespace"] = "shop", ["resourceVersion"] = "900" } };
        var k8s = new List<string>
        {
            Line("kubernetes-watch", 0.2, "kubernetes-watch/ADDED/Deployment/dep-db/900", new { type = "ADDED", @object = Obj("Deployment", "dep-db", "db", new() { ["spec"] = new { replicas = 1 } }) }),
            Line("kubernetes-watch", 0.4, "kubernetes-watch/ADDED/Service/svc-db/900", new { type = "ADDED", @object = Obj("Service", "svc-db", "db", new()) }),
            Line("kubernetes-watch", 1, "kubernetes-watch/ADDED/Pod/uid-db-6b8-1/900", new { type = "ADDED", @object = Obj("Pod", "uid-db-6b8-1", "db-6b8-1", new()
            {
                ["metadata"] = new Dictionary<string, object> { ["ownerReferences"] = new[] { new { kind = "ReplicaSet", name = "db-6b8" } } },
                ["spec"] = new { nodeName = "node-b" }, ["status"] = new { phase = "Running" },
            }) }),
            Line("kubernetes-watch", 2, "kubernetes-watch/ADDED/EndpointSlice/slice-db/900", new { type = "ADDED", @object = Obj("EndpointSlice", "slice-db", "db-slice", new()
            {
                ["metadata"] = new Dictionary<string, object> { ["labels"] = new Dictionary<string, string> { ["kubernetes.io/service-name"] = "db" } },
                ["endpoints"] = new[] { new { addresses = new[] { "10.0.2.1" }, targetRef = new { kind = "Pod", uid = "uid-db-6b8-1" } } },
            }) }),
        };
        var flows = new List<string>(); var n = 0;
        for (var sec = 10; sec < 600; sec += 5)
            foreach (var src in extraWebToDb ? new[] { "api-5c9-1", "api-5c9-2", "web-7d4-1" } : ["api-5c9-1", "api-5c9-2"])
            {
                var verdict = sec is >= 240 and < 420 && sec / 5 % 2 == 0 ? "DROPPED" : "FORWARDED";
                var t = At(sec + 0.0005);
                flows.Add(Line("hubble", sec + 0.0005, $"hubble/flow-db-{n}", new { flow = new { time = t, uuid = $"flow-db-{n}", verdict, source = new { @namespace = "shop", cluster_name = "synth", pod_name = src }, destination = new { @namespace = "shop", cluster_name = "synth", pod_name = "db-6b8-1" } }, node_name = "node-a", time = t }));
                n++;
            }
        File.WriteAllLines(Path.Combine(dir, "kubernetes", "db.ndjson"), k8s);
        File.WriteAllLines(Path.Combine(dir, "hubble", "db.ndjson"), flows);
    }

    /// <summary>Realizes of web, api, db; the extra flow web to db; the retired capability; and the lifted edges expected, with the sum of flows read from the service level.</summary>
    [Theory]
    [InlineData("storefront,ordering,data-platform", false, null, "Storefront -> Ordering|Ordering -> Data platform")]            // each service its own capability: both edges lift
    [InlineData("ordering,ordering,data-platform", false, null, "Ordering -> Data platform")]                                     // web and api in one capability: the call between them is a self-loop and is dropped
    [InlineData("storefront,ordering,", false, null, "Storefront -> Ordering")]                                                   // db realizes nothing: an end without an image is not lifted
    [InlineData("ordering,ordering,ordering", false, null, "")]                                                                   // everything in one capability: no edge
    [InlineData("storefront,ordering,data-platform", false, "ordering", "")]                                                      // a retired capability has no image: both of its edges are gone
    [InlineData("storefront,ordering,ordering", true, null, "Storefront -> Ordering")]                                            // web to api and web to db are parallel edges between the same capabilities: one edge, flows summed
    public void The_lift_drops_self_loops_and_ends_without_an_image_and_collapses_parallel_edges_summing_the_flows(string realizes, bool extraWebToDb, string? retired, string expected)
    {
        var dir = Copy([.. realizes.Split(',').Select(x => x == "" ? null : x)], extraWebToDb, retired);
        try
        {
            var e = ReplayTests.Replay(dir, cluster: "synth");
            var lifted = Summed(e, "capabilities", "callFlows", "capabilities");
            Assert.Equal(expected == "" ? [] : expected.Split('|').Order().ToList(), lifted.Keys.Order().ToList());
            var rels = e.Relationships("capabilities", "DependsOn").Count;
            Assert.Equal(lifted.Count, rels);                                                       // one relationship per pair of images: parallel edges did not stay parallel
            var service = Summed(e, "serviceTree", "flows", "serviceTree");
            if (lifted.TryGetValue("Storefront -> Ordering", out var storefront)) Assert.Equal(service["web -> api"] + (extraWebToDb ? service["web -> db"] : 0), storefront);
            if (extraWebToDb) Assert.True(service["web -> db"] > 0);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- a capture names its workload in an arguments file; the tracked space takes those values ---------------------------------------

    static string WithoutBinding(string text) => Regex.Replace(Regex.Replace(text, @"(workloadNamespace|clusterLabel|aksName|nodeResourceGroup|hubResourceGroup): ""[^""]*""", "$1: \"_\""), @"sha256:[0-9a-f]{64}", "sha256:_");

    [Fact]
    public void The_arguments_file_of_a_capture_gives_the_tracked_space_its_values_by_name()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-args-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(Sattle.Cli.Program.ArgumentsOf(dir));                                          // no file: the tracked arguments, as on a fresh clone
            File.WriteAllText(Path.Combine(dir, Sattle.Cli.Program.SpaceArguments), """{ "workloadNamespace": "other", "aksName": "a \"quoted\" name", "notAnArgument": "ignored" }""");
            var tracked = File.ReadAllText(Path.Combine(Repo.Definitions, "acme.space.sattle"));
            var files = Repo.DefinitionFilesFor(dir);
            var space = files.Single(f => f.Path.EndsWith("acme.space.sattle", StringComparison.Ordinal)).Text;
            Assert.Equal(WithoutBinding(tracked), WithoutBinding(space));                               // nothing but argument values differs
            Assert.Equal(Regex.Matches(tracked, @"workloadNamespace: ").Count, Regex.Matches(space, @"workloadNamespace: ""other""").Count);       // every use of the name
            Assert.Contains(@"aksName: ""a \u0022quoted\u0022 name""", space);                                                                  // a value is written as a text literal of the language
            Assert.DoesNotContain("notAnArgument", space);
            var cat = Repo.CatalogFor(dir, out var res);
            Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
            Assert.Contains(cat.Spaces.Values.Single().Graphs, g => g.Name == "workload");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_estate_space_copy_is_the_tracked_space()
    {
        Assert.Equal(WithoutBinding(File.ReadAllText(Path.Combine(Repo.Definitions, "acme.space.sattle"))), WithoutBinding(File.ReadAllText(Path.Combine(Estate, "space", "acme.space.sattle"))));
    }

    // ---- the image is a function: policy for a node with two images, composition of two lifts, ratio and state of a lifted edge -------------

    static ReplayTests.Run RunWith(string dir, string? module, SourceFile[] moduleFiles, string? graphLine)
    {
        var files = Catalog.ReadDirectory(Repo.Definitions).ToList();
        var i = files.FindIndex(f => f.Path.EndsWith("acme.space.sattle", StringComparison.Ordinal));
        var space = File.ReadAllText(Path.Combine(Estate, "space", "acme.space.sattle"));
        if (module is not null) space = space.Replace("import acme.biz ", $"import {module} @ 1.0.0 sha256:… as extra;\nimport acme.biz ").Replace("  graph workload = wl.workloadHealth(master: master, authored: authored, structure: workloadStructure, workloadNamespace: \"shop\");", "  graph workload = wl.workloadHealth(master: master, authored: authored, structure: workloadStructure, workloadNamespace: \"shop\");\n  " + graphLine);
        files[i] = files[i] with { Text = space };
        files.AddRange(moduleFiles);
        var cat = new Catalog(Repo.Projections); var res = cat.Load(files, updatePins: true);
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
        return ReplayTests.Replay(dir, 200, "synth", o: new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 }, cat: cat);
    }

    /// <summary>One step from the pods straight to the capabilities: the composition of the two lifts, written as one.</summary>
    const string OneStep = """
        module acme.onestep @ 1.0.0;
        import sattle.infra @ 1.0.0 sha256:… as infra;
        import acme.ops     @ 1.0.0 sha256:… as ops;
        import acme.biz     @ 1.0.0 sha256:… as biz;
        """;

    const string OneStepGraph = """
        graph oneStep : acme.biz with (history = 4);
        input master : acme.ops;
        input authored : acme.ops;

        let realized = graph("authored")
            | graph-match (s)-[r]->(c) where labels(r) has "Realizes" and c.status != "retired"
              project service = s.id, capability = c.id;
        let edges = graph("master")
            | graph-match (a)-[k]->(b), (a)-[x]->(s), (b)-[y]->(t)
              where labels(a) has "Pod" and labels(k) has "Calls" and labels(b) has "Pod" and labels(x) has "Backs" and labels(s) has "Service" and labels(y) has "Backs" and labels(t) has "Service"
              project k = k.id, s = s.id, t = t.id
            | join kind=inner (realized | project sa = service, ca = capability) on $left.s == $right.sa
            | join kind=inner (realized | project sb = service, cb = capability) on $left.t == $right.sb
            | where ca != cb;
        edges DependsOn from ca to cb with (key = "one") = edges | project ca, cb;
        extend edges DependsOn from ca to cb with (key = "one") = edges
            | join kind=inner (telemetry("master", 24h) | where property in ("flows", "drops") | project k = id, windowStart, property, value) on k
            | summarize callFlows = sumif(value, property == "flows"), callDrops = sumif(value, property == "drops") by ca, cb, windowStart;
        """;

    [Theory]
    [InlineData("storefront,ordering,data-platform", false)]               // each service one capability
    [InlineData("storefront,ordering,ordering", true)]                     // two service edges between the same capabilities, one self-loop
    public void The_composition_of_two_lifts_gives_the_same_edges_and_sums_as_the_one_step_lift_from_the_pods(string realizes, bool extraWebToDb)
    {
        var dir = Copy([.. realizes.Split(',')], extraWebToDb, null);
        try
        {
            var e = RunWith(dir, "acme.onestep", [new SourceFile("acme.onestep.sattle", OneStep), new SourceFile("acme.onestep.oneStep.kql", OneStepGraph)], "graph oneStep = extra.oneStep(master: master, authored: authored);");
            foreach (var prop in new[] { "callFlows", "callDrops" })
            {
                var twoSteps = Summed(e, "capabilities", prop, "capabilities");
                var oneStep = Summed(e, "oneStep", prop, "capabilities");
                Assert.NotEmpty(twoSteps);
                Assert.Equal(twoSteps.OrderBy(x => x.Key).ToList(), oneStep.OrderBy(x => x.Key).ToList());
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_service_with_two_images_is_not_lifted_the_declared_cardinality_reports_it_and_no_call_is_counted_twice()
    {
        var dir = Copy(["storefront|ordering", "ordering", "data-platform"], false, null);        // web realizes two capabilities
        try
        {
            var e = ReplayTests.Replay(dir, cluster: "synth", o: new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 });
            var lifted = Summed(e, "capabilities", "callFlows", "capabilities");
            var service = Summed(e, "serviceTree", "flows", "serviceTree");
            Assert.Equal(["Ordering -> Data platform"], lifted.Keys.ToList());                      // web has two images: its edge is not lifted to either; api to db is
            Assert.Equal(service["api -> db"], lifted["Ordering -> Data platform"]);
            var cat = Repo.LoadExamples(out _);
            var authored = e.Engine.Graph("authored");                                                        // what people wrote, `Realizes` among it
            var violations = Conformance.Check(authored.State, cat.OntologyOf(authored.Typed.OutputModule));
            Assert.Contains(violations, d => d.Code == "Cardinality");                              // `constraint cardinality Realizes per source 0 .. 1`
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_ratio_of_a_lifted_edge_is_recomputed_from_the_summed_counts_and_its_state_follows_the_aggregate()
    {
        var dir = Copy(["storefront", "ordering", "ordering"], extraWebToDb: true, null);         // web to api (no drops) and web to db (drops in minutes 4 to 7) lift to one edge
        try
        {
            // a host, for the windows of two graphs by their start: the replay's files number the windows of each graph on their own
            using var h = HostTests.NewHost(lateHorizon: 1_000_000);
            h.RunCapture(dir, new ReplayOptions { ChunkRows = 2000, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000, Cluster = "synth" });
            var edge = Assert.Single(h.Relationships("capabilities", "DependsOn"))["id"].Id();
            Dictionary<string, decimal> Of(string prop) => h.Windows("capabilities", prop).Where(x => x.Element == edge).ToDictionary(x => x.Window, x => x.Value);
            var (flows, drops, ratio) = (Of("callFlows"), Of("callDrops"), Of("callDropRatio"));
            Assert.True(flows.Count >= 8);
            // the ratio of every window is drops over flows of the lifted sums
            Assert.All(flows.Where(w => w.Value > 0), w => Assert.Equal(Math.Round(drops[w.Key] / w.Value, 6), Math.Round(ratio[w.Key], 6)));
            // and it is not the mean of the ratios of the two service edges, which carry different numbers of flows
            var services = h.Entities("serviceTree").ToDictionary(x => x["id"].Id(), x => x.Prop("name"));
            var fromWeb = h.Relationships("serviceTree", "DependsOn").Where(r => services[r["source"].Id()] == "web").Select(r => r["id"].Id()).ToHashSet();
            var ratios = h.Windows("serviceTree", "dropRatio").Where(x => fromWeb.Contains(x.Element)).GroupBy(x => x.Window).ToDictionary(w => w.Key, w => w.Select(x => x.Value).ToList());
            var diverging = flows.Keys.Where(w => ratios.TryGetValue(w, out var l) && l.Count == 2 && drops[w] > 0 && Math.Abs(ratio[w] - l.Average()) > 0.01m).ToList();
            Assert.NotEmpty(diverging);
            // the state is derived from the aggregate: degraded while the summed ratio stays above 0.05 for three windows, ok again after three windows below 0.02
            var states = h.Patches("capabilities", 0).SelectMany(p => p["edits"]!.AsArray()).Where(x => x!["after"] is { } after && after["id"].Id() == edge).Select(x => x!["after"]!.Prop("callStatus")).Where(x => x is not null).ToList();
            Assert.Contains("degraded", states);
            Assert.Equal("ok", states.Last());                                                       // recovered after three windows below 0.02
        }
        finally { Directory.Delete(dir, true); }
    }
}
