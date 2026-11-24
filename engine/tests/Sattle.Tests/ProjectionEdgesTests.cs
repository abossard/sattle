using System.Text.Json;
using System.Text.Json.Nodes;
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
        ["readinessReport"] = "a document: ordered blocks, its ontology (sattle.doc) declares no relationship type",
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
        Assert.Single(lifted);                                                                           // database has no instances, so nothing calls into data-platform
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
        var records = Path.Combine(dir, "records", "records.ndjson");
        var lines = File.ReadAllLines(records).Where(l => !(l.Contains("\"source\":\"records:RealizesRow\"") && l.Contains("\"values\""))).ToList();
        if (retired is not null) lines = [.. lines.Select(l => l.Contains($"\"capId\":\"{retired}\"") && l.Contains("\"source\":\"records:CapabilityRow\"") ? l.Replace("\"status\":\"active\"", "\"status\":\"retired\"") : l)];
        File.WriteAllLines(records, lines);
        var services = new[] { "web", "api", "db" }; var realizes = new List<string>();
        for (var i = 0; i < 3; i++)
            foreach (var cap in (realizesWebApiDb[i] ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
                realizes.Add(Record("RealizesRow", 0.6, 100 + i * 10 + Array.IndexOf((realizesWebApiDb[i] ?? "").Split('|'), cap), $"realizes/{cap}/{services[i]}", new { capId = cap, site = "synth", @namespace = "shop", service = services[i] }));
        File.WriteAllLines(Path.Combine(dir, "records", "realizes.ndjson"), realizes);
        AddDb(dir, extraWebToDb);
        return dir;
    }

    static string At(double sec) => new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero).AddSeconds(sec).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A record envelope of the row type `type`, captured `sec` seconds after 10:00, its row `values` under the record key `key`.</summary>
    static string Record(string type, double sec, long seq, string key, object values) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["v"] = 1, ["source"] = "records:" + type, ["capturedAt"] = At(sec), ["seq"] = seq, ["key"] = $"records:{type}/{key}", ["meta"] = null, ["payload"] = new { key, values } });

    /// <summary>A third service behind api for the lift tests: Unit, Service and instance `db` and its endpoint, flows from both api instances to the db instance every 5 s for ten minutes (every other one dropped in minutes 4 to 7), and with `extraWebToDb` the same flows from web-7d4-1 too. The shipped estate's database is an ExternalName Service without instances.</summary>
    static void AddDb(string dir, bool extraWebToDb)
    {
        var platform = new List<string>
        {
            Record("UnitRow", 0.2, 0, "unit/shop/db", new { site = "synth", @namespace = "shop", name = "db", replicas = 1 }),
            Record("ServiceRow", 0.4, 0, "service/shop/db", new { site = "synth", @namespace = "shop", name = "db" }),
            Record("InstanceRow", 1, 0, "instance/uid-db-6b8-1", new { site = "synth", uid = "uid-db-6b8-1", @namespace = "shop", name = "db-6b8-1", phase = "Running", hostName = "node-b", unit = "db" }),
            Record("EndpointRow", 2, 0, "endpoint/shop/db/10.0.2.1", new { site = "synth", @namespace = "shop", service = "db", address = "10.0.2.1", instanceUid = "uid-db-6b8-1" }),
        };
        var flows = new List<string>(); var n = 0;
        for (var sec = 10; sec < 600; sec += 5)
            foreach (var src in extraWebToDb ? new[] { "api-5c9-1", "api-5c9-2", "web-7d4-1" } : ["api-5c9-1", "api-5c9-2"])
            {
                var verdict = sec is >= 240 and < 420 && sec / 5 % 2 == 0 ? "DROPPED" : "FORWARDED";
                flows.Add(Record("FlowRow", sec + 0.0005, 0, $"flow-db-{n}", new { time = At(sec + 0.0005), site = "synth", verdict, srcNamespace = "shop", srcInstance = src, dstNamespace = "shop", dstInstance = "db-6b8-1" }));
                n++;
            }
        File.WriteAllLines(Path.Combine(dir, "records", "db-platform.ndjson"), platform);
        File.WriteAllLines(Path.Combine(dir, "records", "db-flows.ndjson"), flows);
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
            var e = ReplayTests.Replay(dir);
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

    // ---- the image is a function: policy for a node with two images, composition of two lifts, ratio and state of a lifted edge -------------

    static ReplayTests.Run RunWith(string dir, string? module, SourceFile[] moduleFiles, string? graph)
    {
        var files = Catalog.ReadDirectory(Repo.Definitions).ToList();
        var i = files.FindIndex(f => f.Path.EndsWith("acme.space.module.gkql.json", StringComparison.Ordinal));
        var space = JsonNode.Parse(files[i].Text)!;
        if (module is not null)
        {
            space["imports"]!.AsArray().Add(new JsonObject { ["module"] = module, ["alias"] = "extra", ["version"] = "1.0.0", ["digest"] = null });
            space["space"]!["graphs"]!.AsArray().Add(JsonNode.Parse(graph!));
        }
        files[i] = files[i] with { Text = space.ToJsonString() };
        files.AddRange(moduleFiles);
        var cat = new Catalog(Repo.Projections); var res = cat.Load(files, updatePins: true);
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
        return ReplayTests.Replay(dir, 200, o: new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 }, cat: cat);
    }

    /// <summary>One step from the instances straight to the capabilities: the composition of the two lifts, written as one.</summary>
    const string OneStep = """
        {"module": "acme.onestep", "version": "1.0.0",
         "imports": [{"module": "sattle.infra", "alias": "infra", "version": "1.0.0", "digest": null},
                     {"module": "acme.ops", "alias": "ops", "version": "1.0.0", "digest": null},
                     {"module": "acme.biz", "alias": "biz", "version": "1.0.0", "digest": null}]}
        """;

    const string OneStepGraph = """
        {"graph": "oneStep", "ontology": "acme.biz", "history": 4,
         "inputs": [{"name": "master", "ontology": "acme.ops"}, {"name": "authored", "ontology": "acme.ops"}], "parameters": [],
         "verbs": [
           {"id": "realized_1", "verb": "graph-match", "graph": "authored", "pattern": {"nodes": [{"var": "s", "label": "Service"}, {"var": "c", "label": "Capability"}], "edges": [{"var": "r", "label": "Realizes", "from": "s", "to": "c"}]}},
           {"id": "realized_2", "verb": "where", "condition": {"fn": "!=", "args": [{"fn": "coalesce", "args": [{"ref": "c.status"}, {"lit": ""}]}, {"lit": "retired"}]}},
           {"id": "realized_3", "verb": "project", "columns": {"service": {"ref": "s"}, "capability": {"ref": "c"}}},
           {"id": "fromImage", "verb": "project", "columns": {"sa": {"fn": "id", "args": [{"ref": "service"}]}, "ca": {"ref": "capability"}}},
           {"id": "toImage", "verb": "project", "columns": {"sb": {"fn": "id", "args": [{"ref": "service"}]}, "cb": {"ref": "capability"}}},
           {"id": "edges_1", "verb": "graph-match", "graph": "master", "pattern": {
             "nodes": [{"var": "a", "label": "Instance"}, {"var": "b", "label": "Instance"}, {"var": "s", "label": "Service"}, {"var": "t", "label": "Service"}],
             "edges": [{"var": "k", "label": "Calls", "from": "a", "to": "b"}, {"var": "x", "label": "Backs", "from": "a", "to": "s"}, {"var": "y", "label": "Backs", "from": "b", "to": "t"}]}},
           {"id": "edges_2", "verb": "project", "columns": {"k": {"ref": "k"}, "s": {"fn": "id", "args": [{"ref": "s"}]}, "t": {"fn": "id", "args": [{"ref": "t"}]}}},
           {"id": "edges_3", "verb": "join", "kind": "inner", "on": [{"left": "s", "right": "sa"}]},
           {"id": "edges_4", "verb": "join", "kind": "inner", "on": [{"left": "t", "right": "sb"}]},
           {"id": "edges_5", "verb": "where", "condition": {"fn": "!=", "args": [{"ref": "ca"}, {"ref": "cb"}]}},
           {"id": "calls_1", "verb": "series", "graph": "master", "series": {"element": "k", "lookback": "24h", "properties": ["flows", "drops"]}},
           {"id": "calls_2", "verb": "summarize", "by": {"ca": {"ref": "ca"}, "cb": {"ref": "cb"}, "windowStart": {"ref": "windowStart"}}, "aggregates": {"callFlows": {"fn": "sum", "args": [{"ref": "flows"}]}, "callDrops": {"fn": "sum", "args": [{"ref": "drops"}]}}},
           {"id": "one_1", "verb": "edges", "rule": "one", "target": {
             "nodes": [{"var": "s", "label": "BizCapability", "id": {"ref": "ca"}}, {"var": "t", "label": "BizCapability", "id": {"ref": "cb"}}],
             "edges": [{"var": "e", "label": "DependsOn", "from": "s", "to": "t", "key": "one"}]}},
           {"id": "one_2", "verb": "edges", "rule": "one", "target": {
             "nodes": [{"var": "s", "label": "BizCapability", "id": {"ref": "ca"}}, {"var": "t", "label": "BizCapability", "id": {"ref": "cb"}}],
             "edges": [{"var": "e", "label": "DependsOn", "from": "s", "to": "t", "key": "one", "extend": true, "props": {"callFlows": {"ref": "callFlows"}, "callDrops": {"ref": "callDrops"}}}]}}],
         "wires": [
           {"from": "realized_1", "to": "realized_2"}, {"from": "realized_2", "to": "realized_3"},
           {"from": "realized_3", "to": "fromImage"}, {"from": "realized_3", "to": "toImage"},
           {"from": "edges_1", "to": "edges_2"},
           {"from": "edges_2", "to": "edges_3", "port": "left"}, {"from": "fromImage", "to": "edges_3", "port": "right"},
           {"from": "edges_3", "to": "edges_4", "port": "left"}, {"from": "toImage", "to": "edges_4", "port": "right"},
           {"from": "edges_4", "to": "edges_5"},
           {"from": "edges_5", "to": "one_1"}, {"from": "edges_5", "to": "calls_1"},
           {"from": "calls_1", "to": "calls_2"}, {"from": "calls_2", "to": "one_2"}]}
        """;

    [Theory]
    [InlineData("storefront,ordering,data-platform", false)]               // each service one capability
    [InlineData("storefront,ordering,ordering", true)]                     // two service edges between the same capabilities, one self-loop
    public void The_composition_of_two_lifts_gives_the_same_edges_and_sums_as_the_one_step_lift_from_the_instances(string realizes, bool extraWebToDb)
    {
        var dir = Copy([.. realizes.Split(',')], extraWebToDb, null);
        try
        {
            var e = RunWith(dir, "acme.onestep", [new SourceFile("acme.onestep.module.gkql.json", OneStep), new SourceFile("acme.onestep.oneStep.gkql.json", OneStepGraph)],
                """{"name": "oneStep", "projection": "extra.oneStep", "master": false, "inputs": {"master": "master", "authored": "authored"}}""");
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
            var e = ReplayTests.Replay(dir, o: new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 });
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
            h.RunCapture(dir, new ReplayOptions { ChunkRows = 2000, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000});
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
