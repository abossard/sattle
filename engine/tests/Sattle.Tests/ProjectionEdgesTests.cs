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
    static readonly Dictionary<string, string> Exempt = new()
    {
        ["healthSignals"] = "a list of signals: a signal names its component in a property, the Measures edge to the component is added by healthState",
        ["deploymentReport"] = "a document: ordered blocks, its ontology (sattle.doc) declares no relationship type",
        ["healthReport"] = "a document: ordered blocks, its ontology (sattle.doc) declares no relationship type",
        ["treeSignals"] = "a list of signals: a signal names its element in a property, the hierarchy and the evidence edges are in the other graphs of the tree",
    };

    public static IEnumerable<object[]> Graphs() => Repo.LoadExamples(out _).Spaces.Values.Single().Graphs.Select(g => new object[] { g.Name });

    static readonly Lazy<Engine> OnEstate = new(() => ReplayTests.Replay(Estate, cluster: "synth").Engine);

    [Theory]
    [MemberData(nameof(Graphs))]
    public void A_projection_whose_output_ontology_declares_relationship_types_has_relationships_on_the_estate(string graph)
    {
        var e = OnEstate.Value; var g = e.Graph(graph);
        var cat = Repo.LoadExamples(out _);
        var module = g.Typed.OutputModule;
        var declared = cat.OntologyOf(module).Types.Values.Where(t => t.IsRel && Ids.ModuleOf(t.Id) == module).Select(t => t.Id).ToList();
        if (declared.Count == 0) { Assert.True(Exempt.ContainsKey(graph) || g.State.EntityCount > 0); return; }
        Assert.True(g.State.EntityCount > 0, $"{graph} has no entities on the estate");
        if (Exempt.ContainsKey(graph)) return;
        Assert.True(g.State.RelCount > 0, $"{graph} declares {string.Join(", ", declared.Select(Ids.Name))} and emits {g.State.EntityCount} entities and no relationship on the estate");
    }

    [Fact]
    public void The_exemptions_are_graphs_of_the_space_and_each_still_has_entities_but_no_relationship_to_wait_for()
    {
        var names = Repo.LoadExamples(out _).Spaces.Values.Single().Graphs.Select(g => g.Name).ToHashSet();
        Assert.All(Exempt.Keys, k => Assert.Contains(k, names));
        Assert.All(Exempt.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
    }

    // ---- the capability level of the estate -------------------------------------------------------------------------------------------

    static string ServiceName(Engine e, string id) => e.Graph("serviceTree").State.Entities.First(x => x.Id == id).Props.Text("d:acme.svc/prop:name")!;
    static string CapName(Engine e, string id) => e.Graph("capabilities").State.Entities.First(x => x.Id == id).Props.Text("d:acme.biz/prop:name")!;

    /// <summary>The summed `prop` windows of every relationship of `graph`, by the names of its two ends.</summary>
    static Dictionary<string, decimal> Summed(Engine e, string graph, string prop, Func<string, string> name)
    {
        var rels = e.Graph(graph).State.Rels.Where(r => Ids.Name(r.Type) == "DependsOn").ToDictionary(r => r.Id, r => name(r.Source) + " -> " + name(r.Target));
        var sums = new Dictionary<string, decimal>();
        foreach (var s in ((InMemoryTelemetryStore)e.Telemetry).Dump(e.Graph(graph).Key))
            if (e.Interner.Get(s.Prop).EndsWith("/prop:" + prop, StringComparison.Ordinal) && rels.TryGetValue(e.Interner.Get(s.Element), out var pair))
                sums[pair] = sums.GetValueOrDefault(pair) + s.Value;
        return sums;
    }

    [Fact]
    public void The_estate_capabilities_have_a_hierarchy_and_a_dependency_lifted_from_the_service_level_with_the_same_flows()
    {
        var e = OnEstate.Value; var g = e.Graph("capabilities").State;
        Assert.Equal(4, g.Entities.Count(x => Ids.Name(x.Type) == "BizCapability"));
        var under = g.Rels.Where(r => Ids.Name(r.Type) == "Under").Select(r => CapName(e, r.Source) + " -> " + CapName(e, r.Target)).Order().ToList();
        Assert.Equal(["Ordering -> Commerce", "Storefront -> Commerce"], under);
        var lifted = Summed(e, "capabilities", "callFlows", id => CapName(e, id));
        var service = Summed(e, "serviceTree", "flows", id => ServiceName(e, id));
        Assert.Equal(2, lifted.Count);
        Assert.Equal(service["web -> api"], lifted["Storefront -> Ordering"]);
        Assert.Equal(service["api -> db"], lifted["Ordering -> Data platform"]);
        Assert.True(lifted.Values.All(v => v > 0));
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
        if (extraWebToDb)
        {
            var flows = Path.Combine(dir, "hubble", "flows.ndjson");
            var all = File.ReadAllLines(flows).ToList();
            var extra = all.Where(l => l.Contains("\"pod_name\":\"api-5c9-1\"},\"destination\"") && l.Contains("db-6b8-1")).Select(l => l.Replace("\"pod_name\":\"api-5c9-1\"", "\"pod_name\":\"web-7d4-1\"")
                .Replace("\"seq\":", "\"seq\":9").Replace("hubble/flow-", "hubble/flow-x").Replace("\"uuid\":\"flow-", "\"uuid\":\"flow-x")).ToList();
            all.AddRange(extra);
            File.WriteAllLines(flows, all.OrderBy(l => JsonDocument.Parse(l).RootElement.GetProperty("capturedAt").GetString(), StringComparer.Ordinal));
        }
        return dir;
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
            var e = ReplayTests.Replay(dir, cluster: "synth").Engine;
            var lifted = Summed(e, "capabilities", "callFlows", id => CapName(e, id));
            Assert.Equal(expected == "" ? [] : expected.Split('|').Order().ToList(), lifted.Keys.Order().ToList());
            var rels = e.Graph("capabilities").State.Rels.Count(r => Ids.Name(r.Type) == "DependsOn");
            Assert.Equal(lifted.Count, rels);                                                       // one relationship per pair of images: parallel edges did not stay parallel
            var service = Summed(e, "serviceTree", "flows", id => ServiceName(e, id));
            if (lifted.TryGetValue("Storefront -> Ordering", out var storefront)) Assert.Equal(service["web -> api"] + (extraWebToDb ? service["web -> db"] : 0), storefront);
            if (extraWebToDb) Assert.True(service["web -> db"] > 0);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- the static space `replay --out` writes for the canvas ----------------------------------------------------------------------

    [Fact]
    public void The_static_space_written_for_the_canvas_has_every_graph_with_its_edges_in_the_final_snapshot_and_in_the_patches()
    {
        var run = ReplayTests.Replay(Estate, cluster: "synth");
        var dir = Path.Combine(Path.GetTempPath(), "sattle-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            run.Export.WriteFiles(dir, Repo.LoadExamples(out _).Spaces.Values.Single());
            var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json"))).RootElement.GetProperty("graphs");
            var names = manifest.EnumerateObject().Select(x => x.Name).Order().ToList();
            Assert.Equal(Repo.LoadExamples(out _).Spaces.Values.Single().Graphs.Select(g => g.Name).Append("master").Distinct().Order().ToList(), names);
            Assert.All(manifest.EnumerateObject(), g => Assert.False(string.IsNullOrWhiteSpace(g.Value.GetProperty("description").GetString()), $"graph {g.Name} has no description"));       // what the graph is, in the words of its projection
            foreach (var g in names.Where(n => !Exempt.ContainsKey(n)))
            {
                var final = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, $"{g}.final.snapshot.json"))).RootElement.GetProperty("extent");
                var inPatches = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, $"{g}.patches.json"))).RootElement.EnumerateArray()
                    .SelectMany(p => p.GetProperty("edits").EnumerateArray()).Count(x => x.GetProperty("kind").GetString() == "relationship");
                Assert.True(final.GetProperty("entities").GetArrayLength() > 0, $"{g}: no entities in the final snapshot");
                Assert.True(final.GetProperty("relationships").GetArrayLength() > 0, $"{g}: no relationships in the final snapshot");
                Assert.True(inPatches > 0, $"{g}: no relationship edit in the patches");
            }
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
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
            File.WriteAllText(Path.Combine(dir, Sattle.Cli.Program.SpaceArguments), """{ "workloadNamespace": "other", "clusterLabel": "a \"quoted\" label", "notAnArgument": "ignored" }""");
            var tracked = File.ReadAllText(Path.Combine(Repo.Definitions, "acme.space.sattle"));
            var files = Repo.DefinitionFilesFor(dir);
            var space = files.Single(f => f.Path.EndsWith("acme.space.sattle", StringComparison.Ordinal)).Text;
            Assert.Equal(WithoutBinding(tracked), WithoutBinding(space));                               // nothing but argument values differs
            Assert.Equal(Regex.Matches(tracked, @"workloadNamespace: ").Count, Regex.Matches(space, @"workloadNamespace: ""other""").Count);       // every use of the name
            Assert.Contains(@"clusterLabel: ""a \u0022quoted\u0022 label""", space);                                                             // a value is written as a text literal of the language
            Assert.Equal(Regex.Match(tracked, @"aksName: ""[^""]*""").Value, Regex.Match(space, @"aksName: ""[^""]*""").Value);                    // not given: kept
            Assert.DoesNotContain("notAnArgument", space);
            var cat = Repo.CatalogFor(dir, out var res);
            Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
            Assert.Contains(cat.Spaces.Values.Single().Graphs, g => g.Name == "health");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_estate_space_copy_is_the_tracked_space()
    {
        Assert.Equal(WithoutBinding(File.ReadAllText(Path.Combine(Repo.Definitions, "acme.space.sattle"))), WithoutBinding(File.ReadAllText(Path.Combine(Estate, "space", "acme.space.sattle"))));
    }

    // ---- authored rows in a capture -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("{\"kind\":\"capability\",\"capId\":\"x\",\"name\":\"X\",\"owner\":\"o\"}")]                          // no status
    [InlineData("{\"kind\":\"realizes\",\"capId\":\"x\",\"cluster\":\"c\",\"namespace\":\"n\"}")]                    // no service
    [InlineData("{\"kind\":\"other\",\"capId\":\"x\"}")]                                                          // unknown kind
    [InlineData("{\"kind\":\"capability\",\"name\":\"X\",\"owner\":\"o\",\"status\":\"active\"}")]                    // no capId
    public void An_authoring_record_that_is_incomplete_is_rejected_and_counted(string payload)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-authoring-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(dir, "authoring"));
        File.WriteAllText(Path.Combine(dir, "authoring", "a.ndjson"), $"{{\"v\":1,\"source\":\"authoring\",\"capturedAt\":\"2026-01-01T10:00:00.000Z\",\"seq\":1,\"key\":\"authoring/x\",\"meta\":null,\"payload\":{payload}}}\n");
        try
        {
            var r = ReplayTests.Replay(dir, cluster: "synth");
            Assert.Equal(1, r.Stats.Rejected);
            Assert.Equal(0, r.Engine.Master.State.EntityCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- the image is a function: policy for a node with two images, composition of two lifts, ratio and state of a lifted edge -------------

    static Engine RunWith(string dir, string? module, string? moduleText, string? graphLine)
    {
        var files = Catalog.ReadDirectory(Repo.Definitions).ToList();
        var i = files.FindIndex(f => f.Path.EndsWith("acme.space.sattle", StringComparison.Ordinal));
        var space = File.ReadAllText(Path.Combine(Estate, "space", "acme.space.sattle"));
        if (module is not null) space = space.Replace("import acme.biz ", $"import {module} @ 1.0.0 sha256:… as extra;\nimport acme.biz ").Replace("  graph health = hm.health(master: master, st: healthState, workloadNamespace: \"shop\");", "  graph health = hm.health(master: master, st: healthState, workloadNamespace: \"shop\");\n  " + graphLine);
        files[i] = files[i] with { Text = space };
        if (module is not null) files.Add(new SourceFile(module + ".sattle", moduleText!));
        var cat = new Catalog(); var res = cat.Load(files, updatePins: true);
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
        var id = cat.Spaces.Keys.Single();
        var engine = new Engine(cat, id, new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 });
        new Replay(engine, cat.Spaces[id], AdapterRegistry.Create("synth"), new ReplayOptions { ChunkRows = 200, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000, Cluster = "synth" }).Run(dir);
        return engine;
    }

    /// <summary>One step from the pods straight to the capabilities: the composition of the two lifts, written as one.</summary>
    const string OneStep = """
        module acme.onestep @ 1.0.0;
        import sattle.infra @ 1.0.0 sha256:… as infra;
        import acme.ops     @ 1.0.0 sha256:… as ops;
        import acme.biz     @ 1.0.0 sha256:… as biz;

        projection oneStep view forward output biz {
          in master: graph ops;
          retain 4;
          let realized = match master (s: infra.Service)-[:ops.Realizes]->(c: ops.Capability) |> where c.status != "retired";
          let fromImage = realized |> select { sa: s, ca: c };
          let toImage = realized |> select { sb: s, cb: c };
          let calls = match master (a: infra.Pod)-[k:infra.Calls]->(b: infra.Pod), (a)-[:infra.Backs]->(s: infra.Service), (b)-[:infra.Backs]->(t: infra.Service);
          let withFrom = join calls, fromImage on s == sa;
          let ends = join withFrom, toImage on t == sb;
          let edges = ends |> where ca != cb;
          emit relationship biz.DependsOn from edges key one: ca -> cb;
          let windows = edges
            |> series master k.flows, k.drops within 24h
            |> rollup by { ca: ca, cb: cb } combine { flows: sum(flows), drops: sum(drops) };
          emit update relationship biz.DependsOn from windows key one: ca -> cb set { callFlows: flows, callDrops: drops };
        }
        """;

    [Theory]
    [InlineData("storefront,ordering,data-platform", false)]               // each service one capability
    [InlineData("storefront,ordering,ordering", true)]                     // two service edges between the same capabilities, one self-loop
    public void The_composition_of_two_lifts_gives_the_same_edges_and_sums_as_the_one_step_lift_from_the_pods(string realizes, bool extraWebToDb)
    {
        var dir = Copy([.. realizes.Split(',')], extraWebToDb, null);
        try
        {
            var e = RunWith(dir, "acme.onestep", OneStep, "graph oneStep = extra.oneStep(master: master);");
            foreach (var prop in new[] { "callFlows", "callDrops" })
            {
                var twoSteps = Summed(e, "capabilities", prop, id => CapName(e, id));
                var oneStep = Summed(e, "oneStep", prop, id => CapName(e, id));
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
            var run = ReplayTests.Replay(dir, cluster: "synth", o: new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 });
            var e = run.Engine;
            var lifted = Summed(e, "capabilities", "callFlows", id => CapName(e, id));
            var service = Summed(e, "serviceTree", "flows", id => ServiceName(e, id));
            Assert.Equal(["Ordering -> Data platform"], lifted.Keys.ToList());                      // web has two images: its edge is not lifted to either; api to db is
            Assert.Equal(service["api -> db"], lifted["Ordering -> Data platform"]);
            var cat = Repo.LoadExamples(out _);
            var violations = Conformance.Check(e.Master.State, cat.OntologyOf(e.Master.Typed.OutputModule));
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
            var run = ReplayTests.Replay(dir, cluster: "synth", o: new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 });
            var e = run.Engine; var g = e.Graph("capabilities");
            var edge = g.State.Rels.Single(r => Ids.Name(r.Type) == "DependsOn");
            var windows = ((InMemoryTelemetryStore)e.Telemetry).Dump(g.Key).Where(x => e.Interner.Get(x.Element) == edge.Id).GroupBy(x => x.Ws)
                .ToDictionary(w => w.Key, w => w.ToDictionary(x => e.Interner.Get(x.Prop).Split(':')[^1], x => x.Value));
            Assert.True(windows.Count >= 8);
            // the ratio of every window is drops over flows of the lifted sums
            Assert.All(windows.Values.Where(w => w["callFlows"] > 0), w => Assert.Equal(Math.Round(w["callDrops"] / w["callFlows"], 6), Math.Round(w["callDropRatio"], 6)));
            // and it is not the mean of the ratios of the two service edges, which carry different numbers of flows
            var tree = e.Graph("serviceTree"); var ratios = new Dictionary<long, List<decimal>>();
            foreach (var r in tree.State.Rels.Where(r => Ids.Name(r.Type) == "DependsOn" && ServiceName(e, r.Source) == "web"))
                foreach (var x in ((InMemoryTelemetryStore)e.Telemetry).Dump(tree.Key).Where(x => e.Interner.Get(x.Element) == r.Id && e.Interner.Get(x.Prop).EndsWith("/prop:dropRatio", StringComparison.Ordinal)))
                    (ratios.TryGetValue(x.Ws, out var l) ? l : ratios[x.Ws] = []).Add(x.Value);
            var diverging = windows.Where(w => ratios.TryGetValue(w.Key, out var l) && l.Count == 2 && w.Value["callDrops"] > 0 && Math.Abs(w.Value["callDropRatio"] - l.Average()) > 0.01m).ToList();
            Assert.NotEmpty(diverging);
            // the state is derived from the aggregate: degraded while the summed ratio stays above 0.05 for three windows, ok again after three windows below 0.02
            var states = run.Export.PatchesOf("capabilities").SelectMany(p => p.Edits).Where(x => x.After is Rel r && r.Id == edge.Id).Select(x => ((Rel)x.After!).Props.Text("d:acme.biz/prop:callStatus")).Where(x => x is not null).ToList();
            Assert.Contains("degraded", states);
            Assert.Equal("ok", states.Last());                                                       // recovered after three windows below 0.02
        }
        finally { Directory.Delete(dir, true); }
    }
}
