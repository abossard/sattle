using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Lineage of lifted elements: an edge or node of a higher level names every lower element it is made of (complete, paged), and a lower element names the higher elements it is part of. `replay --out` writes the same answers as files.</summary>
public class LineageTests
{
    static string Estate => EstateTests.EstateDir;
    static List<SourceFile> EstateFiles() => Catalog.ReadDirectory(Repo.Definitions).ToList().Select(f => f.Path.EndsWith("acme.space.sattle", StringComparison.Ordinal) ? f with { Text = File.ReadAllText(Path.Combine(Estate, "space", "acme.space.sattle")) } : f).ToList();

    static SpaceHost Serve(string dir)
    {
        var h = new SpaceHost(EstateFiles(), new HostOptions { Cluster = "synth", LateHorizonWindows = 1_000_000 });
        new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 200, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000, Cluster = "synth" }).Run(dir);
        h.Finish();
        return h;
    }

    static readonly Lazy<SpaceHost> OnEstate = new(() => Serve(Estate));

    static JsonNode LiftedEdge(SpaceHost h, string graph, string from, string to)
    {
        var snap = h.Snapshot(graph, null)["extent"]!;
        var names = snap["entities"]!.AsArray().ToDictionary(e => e!["id"]!["value"]!.GetValue<string>(), e => e!["properties"]!.AsArray().First(p => p!["property"]!["value"]!.GetValue<string>().EndsWith("name", StringComparison.Ordinal))!["value"]!["value"]!.GetValue<string>());
        return snap["relationships"]!.AsArray().Single(r => names[r!["source"]!["value"]!.GetValue<string>()] == from && names[r["target"]!["value"]!.GetValue<string>()] == to && r["type"]!["value"]!.GetValue<string>().EndsWith("DependsOn", StringComparison.Ordinal))!;
    }

    static List<string> Sources(JsonNode lineage) => lineage["sources"]!.AsArray().Select(s => s!["role"]!.GetValue<string>() + "@" + s["graph"]!.GetValue<string>()).ToList();

    [Fact]
    public void A_lifted_capability_edge_names_the_service_edge_the_services_and_the_capabilities_it_is_made_of()
    {
        var h = OnEstate.Value;
        var edge = LiftedEdge(h, "capabilities", "Storefront", "Ordering");
        var l = h.Lineage("capabilities", edge["id"]!["value"]!.GetValue<string>());
        Assert.True(l["complete"]!.GetValue<bool>());
        Assert.Equal("lifted", l["rule"]!.GetValue<string>());
        Assert.Equal(["edge@serviceTree", "source@serviceTree", "sourceImage@master", "sourceMapped@master", "target@serviceTree", "targetImage@master", "targetMapped@master"], Sources(l));
        Assert.Equal("1", l["contributions"]!.GetValue<string>());
        // the service edge it stands for is web to api of the service tree
        var d = l["sources"]!.AsArray().Single(s => s!["role"]!.GetValue<string>() == "edge")!["element"]!.GetValue<string>();
        Assert.Equal("relationship", l["sources"]!.AsArray().Single(s => s!["role"]!.GetValue<string>() == "edge")!["kind"]!.GetValue<string>());
        Assert.Contains(h.Snapshot("serviceTree", null)["extent"]!["relationships"]!.AsArray(), r => r!["id"]!["value"]!.GetValue<string>() == d);
    }

    [Fact]
    public void A_group_node_names_every_service_it_is_made_of()
    {
        var h = OnEstate.Value;
        var group = h.Snapshot("serviceTree", null)["extent"]!["entities"]!.AsArray().Single(e => e!["type"]!["value"]!.GetValue<string>() == "ServiceGroup")!["id"]!["value"]!.GetValue<string>();
        var l = h.Lineage("serviceTree", group);
        Assert.True(l["complete"]!.GetValue<bool>());
        Assert.Equal(["s@master", "s@master", "s@master"], Sources(l));
        Assert.Equal("3", l["sourceCount"]!.GetValue<string>());
    }

    /// <summary>web to api and web to db both lift to Storefront to Ordering (api and db in one capability): the edge stands for both, and the api to db call is a self-loop.</summary>
    [Fact]
    public void A_lifted_edge_of_parallel_edges_names_all_of_them_and_the_pages_give_the_same_list()
    {
        var dir = ProjectionEdgesTests.CopyEstate(["storefront", "ordering", "ordering"], extraWebToDb: true, retired: null);
        try
        {
            using var h = Serve(dir);
            var edge = LiftedEdge(h, "capabilities", "Storefront", "Ordering")["id"]!["value"]!.GetValue<string>();
            var full = h.Lineage("capabilities", edge, int.MaxValue - 1);
            Assert.Equal("2", full["contributions"]!.GetValue<string>());
            Assert.Equal(2, full["sources"]!.AsArray().Count(s => s!["role"]!.GetValue<string>() == "edge"));
            Assert.Null(full["next"]);
            var paged = new List<string>(); string? after = null; var pages = 0;
            do
            {
                var page = h.Lineage("capabilities", edge, 3, after);
                Assert.True(page["sources"]!.AsArray().Count <= 3);
                paged.AddRange(page["sources"]!.AsArray().Select(s => s!["role"]!.GetValue<string>() + "/" + s["element"]!.GetValue<string>()));
                after = page["next"]?.GetValue<string>(); pages++;
            } while (after is not null);
            Assert.True(pages > 1);
            Assert.Equal(full["sources"]!.AsArray().Select(s => s!["role"]!.GetValue<string>() + "/" + s["element"]!.GetValue<string>()).ToList(), paged);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_lower_element_names_the_higher_elements_it_is_part_of()
    {
        var h = OnEstate.Value;
        var lifted = LiftedEdge(h, "capabilities", "Storefront", "Ordering")["id"]!["value"]!.GetValue<string>();
        var forward = h.Lineage("capabilities", lifted);
        foreach (var s in forward["sources"]!.AsArray())
        {
            var rev = h.LineageReverse(s!["graph"]!.GetValue<string>(), s["element"]!.GetValue<string>());
            Assert.Contains(rev["partOf"]!.AsArray(), p => p!["graph"]!.GetValue<string>() == "capabilities" && p["element"]!.GetValue<string>() == lifted && p["role"]!.GetValue<string>() == s["role"]!.GetValue<string>());
        }
        // a pod is part of the service dependency it calls through, and that one of the capability edge
        var tree = h.Snapshot("serviceTree", null)["extent"]!["relationships"]!.AsArray().First(r => r!["type"]!["value"]!.GetValue<string>().EndsWith("DependsOn", StringComparison.Ordinal))!["id"]!["value"]!.GetValue<string>();
        Assert.NotEmpty(h.LineageReverse("serviceTree", tree)["partOf"]!.AsArray());
        Assert.Equal("0", h.LineageReverse("capabilities", lifted)["count"]!.GetValue<string>());     // nothing is built on the capability edge
    }

    [Fact]
    public void An_unknown_element_is_a_404_for_both_directions()
    {
        var h = OnEstate.Value;
        Assert.Equal(404, Assert.Throws<HostError>(() => h.Lineage("capabilities", "e:nothing")).Status);
        Assert.Equal(404, Assert.Throws<HostError>(() => h.LineageReverse("capabilities", "e:nothing")).Status);
    }

    // ---- replay --out writes what the host serves -------------------------------------------------------------------------------------

    [Fact]
    public void The_static_files_hold_the_lineage_and_the_documents_the_host_serves()
    {
        var run = ReplayTests.Replay(Estate, cluster: "synth", o: new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 });
        var dir = Path.Combine(Path.GetTempPath(), "sattle-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            run.Export.WriteFiles(dir, Repo.LoadExamples(out _).Spaces.Values.Single());
            var h = OnEstate.Value;
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["graphs"]!.AsObject();
            Assert.All(manifest, g =>
            {
                var name = g.Key;
                var forward = File.ReadAllLines(Path.Combine(dir, g.Value!["lineage"]!.GetValue<string>())).Select(l => JsonNode.Parse(l)!).ToList();
                var ids = h.Snapshot(name, null)["extent"]!; var expected = ids["entities"]!.AsArray().Count + ids["relationships"]!.AsArray().Count;
                Assert.Equal(expected, forward.Count);                                                                                       // every element of the graph has its lineage
                Assert.All(forward.Take(50), f => Assert.Equal(h.Lineage(name, f["element"]!.GetValue<string>(), int.MaxValue - 1).ToJsonString(), f.ToJsonString()));
                var reverse = File.ReadAllLines(Path.Combine(dir, g.Value["lineageReverse"]!.GetValue<string>())).Select(l => JsonNode.Parse(l)!).ToList();
                Assert.All(reverse.Take(50), r => Assert.Equal(h.LineageReverse(name, r["element"]!.GetValue<string>(), int.MaxValue - 1).ToJsonString(), r.ToJsonString()));
            });
            var doc = h.Document("deploymentReport", null);
            Assert.Equal(doc.Markdown, File.ReadAllText(Path.Combine(dir, manifest["deploymentReport"]!["document"]!.GetValue<string>())));
            Assert.Equal(doc.Json["digest"]!.GetValue<string>(), JsonNode.Parse(File.ReadAllText(Path.Combine(dir, manifest["deploymentReport"]!["documentJson"]!.GetValue<string>())))!["digest"]!.GetValue<string>());
            Assert.Null(manifest["serviceTree"]!["document"]);
            var meters = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, manifest["serviceTree"]!["meters"]!.GetValue<string>())))!;
            Assert.Contains(meters["meters"]!.AsArray(), m => m!["id"]!.GetValue<string>() == "dropRatio" && m["temporality"]!.GetValue<string>() == "gauge" && m["periodSeconds"]!.GetValue<long>() == 60);
            Assert.Equal("degraded", meters["thresholds"]![0]!["high"]!.GetValue<string>());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ---- elements emitted from a group ----------------------------------------------------------------------------------------------

    /// <summary>The service dependencies of the master written as a quotient through `group by`: one edge per pair of services, made of every pod call between them.</summary>
    const string ByGroup = """
        module acme.bygroup @ 1.0.0;
        import sattle.infra @ 1.0.0 sha256:… as infra;
        import acme.ops     @ 1.0.0 sha256:… as ops;
        import acme.svc     @ 1.0.0 sha256:… as svc;

        projection pairs view forward output svc {
          in master: graph ops;
          retain 4;
          let calls = match master (a: infra.Pod)-[c:infra.Calls]->(b: infra.Pod), (a)-[:infra.Backs]->(s: infra.Service), (b)-[:infra.Backs]->(t: infra.Service) |> where s != t;
          let perPair = calls |> group by { s: s, t: t } aggregate { n: count() };
          emit relationship svc.DependsOn from perPair key pair: s -> t;
        }
        """;

    [Fact]
    public void An_edge_emitted_from_a_group_names_every_row_of_the_group_forward_and_reverse_like_a_row_level_emit()
    {
        var files = EstateFiles();
        var i = files.FindIndex(f => f.Path.EndsWith("acme.space.sattle", StringComparison.Ordinal));
        files[i] = files[i] with { Text = files[i].Text.Replace("import acme.biz ", "import acme.bygroup @ 1.0.0 sha256:… as bg;\nimport acme.biz ").Replace("  graph health = hm.health(master: master, st: healthState, workloadNamespace: \"shop\");", "  graph health = hm.health(master: master, st: healthState, workloadNamespace: \"shop\");\n  graph byGroup = bg.pairs(master: master);") };
        files.Add(new SourceFile("acme.bygroup.sattle", ByGroup));
        var h = new SpaceHost(files, new HostOptions { Cluster = "synth", LateHorizonWindows = 1_000_000 });
        using (h)
        {
            new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 200, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000, Cluster = "synth" }).Run(Estate);
            h.Finish();
            var grouped = h.Snapshot("byGroup", null)["extent"]!["relationships"]!.AsArray();
            var rowLevel = h.Snapshot("serviceTree", null)["extent"]!["relationships"]!.AsArray().Where(r => r!["type"]!["value"]!.GetValue<string>().EndsWith("DependsOn", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, grouped.Count); Assert.Equal(2, rowLevel.Count);
            foreach (var edge in grouped)
            {
                var id = edge!["id"]!["value"]!.GetValue<string>();
                var l = h.Lineage("byGroup", id);
                Assert.True(l["complete"]!.GetValue<bool>());
                Assert.Equal("2", l["contributions"]!.GetValue<string>());                                         // two pod calls make up each service dependency
                Assert.Equal(2, l["sources"]!.AsArray().Count(s => s!["role"]!.GetValue<string>() == "c"));      // both Calls relationships of the master
                var peer = rowLevel.Single(r => r!["source"]!["value"]!.GetValue<string>() == edge["source"]!["value"]!.GetValue<string>() && r["target"]!["value"]!.GetValue<string>() == edge["target"]!["value"]!.GetValue<string>());
                var rl = h.Lineage("serviceTree", peer!["id"]!["value"]!.GetValue<string>());
                Assert.Equal(rl["sources"]!.AsArray().Select(s => s!["element"]!.GetValue<string>()).Distinct().Order().ToList(),
                    l["sources"]!.AsArray().Select(s => s!["element"]!.GetValue<string>()).Distinct().Order().ToList());     // the same elements as the lift of the service tree (the roles are the variables of each statement)
                foreach (var s in l["sources"]!.AsArray())
                    Assert.Contains(h.LineageReverse("master", s!["element"]!.GetValue<string>())["partOf"]!.AsArray(), p => p!["graph"]!.GetValue<string>() == "byGroup" && p["element"]!.GetValue<string>() == id);
            }
        }
    }
}
