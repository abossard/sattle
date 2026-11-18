using System.Text.Json.Nodes;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Theorem 9.7, lineage of lifted elements: an edge or node of a higher level names every lower element it is made of (complete, paged), and a lower element names the higher elements it is part of. `replay --out` writes the same answers as files.</summary>
public class LineageTests
{
    static string Estate => EstateTests.EstateDir;
    static List<SourceFile> EstateFiles() => Catalog.ReadDirectory(Repo.Definitions).ToList();

    static SpaceHost Serve(string dir)
    {
        var h = new SpaceHost(EstateFiles(), new HostOptions { Projections = Repo.Projections, Cluster = "synth", LateHorizonWindows = 1_000_000 });
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

    /// <summary>The services a capability is realized by are ends of `Realizes` in `authored`, references there; the view reads no graph that holds them, so the lineage names them as the service nodes of the tree.</summary>
    [Fact]
    public void A_lifted_capability_edge_names_the_service_edge_the_services_and_the_capabilities_it_is_made_of()
    {
        var h = OnEstate.Value;
        var edge = LiftedEdge(h, "capabilities", "Storefront", "Ordering");
        var l = h.Lineage("capabilities", edge["id"]!["value"]!.GetValue<string>());
        Assert.True(l["complete"]!.GetValue<bool>());
        Assert.Equal("lifted", l["rule"]!.GetValue<string>());
        Assert.Equal(["dependency@serviceTree", "fromCapability@authored", "fromNode@serviceTree", "toCapability@authored", "toNode@serviceTree"], Sources(l));
        Assert.Equal("1", l["contributions"]!.GetValue<string>());
        // the service edge it stands for is web to api of the service tree
        var d = l["sources"]!.AsArray().Single(s => s!["role"]!.GetValue<string>() == "dependency")!["element"]!.GetValue<string>();
        Assert.Equal("relationship", l["sources"]!.AsArray().Single(s => s!["role"]!.GetValue<string>() == "dependency")!["kind"]!.GetValue<string>());
        Assert.Contains(h.Snapshot("serviceTree", null)["extent"]!["relationships"]!.AsArray(), r => r!["id"]!["value"]!.GetValue<string>() == d);
    }

    [Fact]
    public void A_group_node_names_every_service_it_is_made_of()
    {
        var h = OnEstate.Value;
        var group = h.Snapshot("serviceTree", null)["extent"]!["entities"]!.AsArray().Single(e => e!["type"]!["value"]!.GetValue<string>() == "ServiceGroup")!["id"]!["value"]!.GetValue<string>();
        var l = h.Lineage("serviceTree", group);
        Assert.True(l["complete"]!.GetValue<bool>());
        Assert.Equal(["service@master", "service@master", "service@master"], Sources(l));
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
            Assert.Equal(2, full["sources"]!.AsArray().Count(s => s!["role"]!.GetValue<string>() == "dependency"));
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

    /// <summary>Code review 2, defect 22: on the estate every pod of the master has a lineage, also the pods whose first id a link forwarded to the id of their name. Before, 1 pod in 5 had none.</summary>
    [Fact]
    public void Every_pod_of_the_master_has_a_lineage_also_one_that_a_link_forwarded_to_another_id()
    {
        var h = OnEstate.Value;
        Assert.True(h.Engine.Identities.Merges > 0, "no identity merged on the estate");          // the protocol does not serve the number of merges: the one read inside the engine
        var pods = h.Entities("master", "Pod");
        var without = pods.Count(p => h.Lineage("master", p["id"].Id())["rule"] is null);
        Assert.True(pods.Count > 0 && without == 0, $"{without} of {pods.Count} pods have no lineage");
    }

    /// <summary>Code review 2: every block of a report that counts or lists elements names them. Before, the count blocks of the two reports named no source. The two blocks without a source are the heading and the table header, which the text of the projection gives as values.</summary>
    [Theory]
    [InlineData("deploymentReport")] [InlineData("healthReport")]
    public void Every_block_of_a_report_that_counts_or_lists_elements_names_them(string graph)
    {
        var h = OnEstate.Value;
        var blocks = h.Entities(graph);
        Assert.Contains(blocks, b => b.Prop("kind") == "stat");
        Assert.Contains(blocks, b => b.Prop("kind") == "row");
        var without = blocks.Where(b => h.Lineage(graph, b["id"].Id())["sources"]!.AsArray().Count == 0).Select(b => b.Prop("kind")).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["header", "heading"], without);
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
    public void The_static_files_hold_every_graph_with_its_edges_and_the_lineage_and_the_documents_the_host_serves()
    {
        var run = Scenarios.Run(Estate);
        var dir = Path.Combine(Path.GetTempPath(), "sattle-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            run.Read(x => { x.WriteFiles(dir, Repo.LoadExamples(out _).Spaces.Values.Single()); return 0; });
            var h = OnEstate.Value;
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["graphs"]!.AsObject();
            // every graph of the space is there with what it is, in the words of its projection, and with its edges in the final snapshot and in the patches
            Assert.Equal(Repo.LoadExamples(out _).Spaces.Values.Single().Graphs.Select(g => g.Name).Append("master").Distinct().Order().ToList(), manifest.Select(g => g.Key).Order().ToList());
            Assert.All(manifest, g => Assert.False(string.IsNullOrWhiteSpace(g.Value!["description"]!.GetValue<string>()), $"graph {g.Key} has no description"));
            foreach (var g in manifest.Select(g => g.Key).Where(n => !ProjectionEdgesTests.Exempt.ContainsKey(n)))
            {
                var final = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, $"{g}.final.snapshot.json")))!["extent"]!;
                var inPatches = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, $"{g}.patches.json")))!.AsArray().SelectMany(p => p!["edits"]!.AsArray()).Count(x => x!["kind"]!.GetValue<string>() == "relationship");
                Assert.True(final["entities"]!.AsArray().Count > 0, $"{g}: no entities in the final snapshot");
                Assert.True(final["relationships"]!.AsArray().Count > 0, $"{g}: no relationships in the final snapshot");
                Assert.True(inPatches > 0, $"{g}: no relationship edit in the patches");
            }
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

    /// <summary>The service dependencies of the master written as a quotient through `summarize`: one edge per pair of services, made of every pod call between them.</summary>
    const string ByGroup = """
        module acme.bygroup @ 1.0.0;
        import sattle.infra @ 1.0.0 sha256:… as infra;
        import acme.ops     @ 1.0.0 sha256:… as ops;
        import acme.svc     @ 1.0.0 sha256:… as svc;
        """;

    const string Pairs = """
        graph pairs : acme.svc with (history = 4);
        input master : acme.ops;

        let backing = graph("master") | graph-match (p)-[k]->(v) where labels(k) has "Backs" project pod = p.id, service = v.id;
        edges DependsOn from s to t with (key = "pair") = graph("master")
            | graph-match (a)-[c]->(b) where labels(c) has "Calls" project c = c.id, fromPod = a.id, toPod = b.id
            | join kind=inner (backing | project fromPod = pod, s = service) on fromPod
            | join kind=inner (backing | project toPod = pod, t = service) on toPod
            | where s != t
            | summarize n = count() by s, t
            | project s, t;
        """;

    [Fact]
    public void An_edge_emitted_from_a_group_names_every_row_of_the_group_forward_and_reverse_like_a_row_level_emit()
    {
        var files = EstateFiles();
        var i = files.FindIndex(f => f.Path.EndsWith("acme.space.sattle", StringComparison.Ordinal));
        files[i] = files[i] with { Text = files[i].Text.Replace("import acme.biz ", "import acme.bygroup @ 1.0.0 sha256:… as bg;\nimport acme.biz ").Replace("  graph workload = wl.workloadHealth(master: master, authored: authored, structure: workloadStructure, workloadNamespace: \"shop\");", "  graph workload = wl.workloadHealth(master: master, authored: authored, structure: workloadStructure, workloadNamespace: \"shop\");\n  graph byGroup = bg.pairs(master: master);") };
        files.AddRange([new SourceFile("acme.bygroup.sattle", ByGroup), new SourceFile("acme.bygroup.pairs.kql", Pairs)]);
        var h = new SpaceHost(files, new HostOptions { Projections = Repo.Projections, Cluster = "synth", LateHorizonWindows = 1_000_000 });
        using (h)
        {
            new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 200, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000, Cluster = "synth" }).Run(Estate);
            h.Finish();
            var grouped = h.Snapshot("byGroup", null)["extent"]!["relationships"]!.AsArray();
            var rowLevel = h.Snapshot("serviceTree", null)["extent"]!["relationships"]!.AsArray().Where(r => r!["type"]!["value"]!.GetValue<string>().EndsWith("DependsOn", StringComparison.Ordinal)).ToList();
            Assert.Single(grouped); Assert.Single(rowLevel);                                                       // web to api
            foreach (var edge in grouped)
            {
                var id = edge!["id"]!["value"]!.GetValue<string>();
                var l = h.Lineage("byGroup", id);
                Assert.True(l["complete"]!.GetValue<bool>());
                Assert.Equal("3", l["contributions"]!.GetValue<string>());                                         // three pod calls make up the service dependency
                Assert.Equal(3, l["sources"]!.AsArray().Count(s => s!["role"]!.GetValue<string>() == "c"));      // the three Calls relationships of the master
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
