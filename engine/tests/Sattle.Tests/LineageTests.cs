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
        var h = new SpaceHost(EstateFiles(), new HostOptions { Projections = Repo.Projections });
        new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 200, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000}).Run(dir);
        h.Finish();
        return h;
    }

    internal static readonly Lazy<SpaceHost> OnEstate = new(() => Serve(Estate));

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
        // an instance is part of the service dependency it calls through, and that one of the capability edge
        var tree = h.Snapshot("serviceTree", null)["extent"]!["relationships"]!.AsArray().First(r => r!["type"]!["value"]!.GetValue<string>().EndsWith("DependsOn", StringComparison.Ordinal))!["id"]!["value"]!.GetValue<string>();
        Assert.NotEmpty(h.LineageReverse("serviceTree", tree)["partOf"]!.AsArray());
        Assert.Equal("0", h.LineageReverse("capabilities", lifted)["count"]!.GetValue<string>());     // nothing is built on the capability edge
    }

    /// <summary>Code review 2, defect 22: on the estate every instance of the master has a lineage, also the instances whose first id a link forwarded to the id of their name. Before, 1 instance in 5 had none.</summary>
    [Fact]
    public void Every_instance_of_the_master_has_a_lineage_also_one_that_a_link_forwarded_to_another_id()
    {
        var h = OnEstate.Value;
        Assert.True(long.Parse(h.Liveness()["counters"]!["identity"]!["merged"]!.GetValue<string>()) > 0, "no identity merged on the estate");
        var instances = h.Entities("master", "Instance");
        var without = instances.Count(p => h.Lineage("master", p["id"].Id())["rule"] is null);
        Assert.True(instances.Count > 0 && without == 0, $"{without} of {instances.Count} instances have no lineage");
    }

    /// <summary>Code review 2: every block of a report that counts or lists elements names them. Before, the count blocks of the reports named no source. The two blocks without a source are the heading and the table header, which the projection gives as values.</summary>
    [Theory]
    [InlineData("readinessReport")]
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
            var doc = h.Document("readinessReport", null);
            Assert.Equal(doc.Markdown, File.ReadAllText(Path.Combine(dir, manifest["readinessReport"]!["document"]!.GetValue<string>())));
            Assert.Equal(doc.Json["digest"]!.GetValue<string>(), JsonNode.Parse(File.ReadAllText(Path.Combine(dir, manifest["readinessReport"]!["documentJson"]!.GetValue<string>())))!["digest"]!.GetValue<string>());
            Assert.Null(manifest["serviceTree"]!["document"]);
            var meters = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, manifest["serviceTree"]!["meters"]!.GetValue<string>())))!;
            Assert.Contains(meters["meters"]!.AsArray(), m => m!["id"]!.GetValue<string>() == "dropRatio" && m["temporality"]!.GetValue<string>() == "gauge" && m["periodSeconds"]!.GetValue<long>() == 60);
            Assert.Null(meters["thresholds"]);                                                    // a status is folded in gkql, no threshold operator to list (PROTOCOL 3.67, #151)
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ---- elements emitted from a group ----------------------------------------------------------------------------------------------

    /// <summary>The service dependencies of the master written as a quotient through `summarize`: one edge per pair of services, made of every instance call between them.</summary>
    const string ByGroup = """
        {"module": "acme.bygroup", "version": "1.0.0",
         "imports": [{"module": "sattle.infra", "alias": "infra", "version": "1.0.0", "digest": null},
                     {"module": "acme.ops", "alias": "ops", "version": "1.0.0", "digest": null},
                     {"module": "acme.svc", "alias": "svc", "version": "1.0.0", "digest": null}]}
        """;

    const string Pairs = """
        {"graph": "pairs", "ontology": "acme.svc", "history": 4, "inputs": [{"name": "master", "ontology": "acme.ops"}], "parameters": [],
         "verbs": [
           {"id": "backing", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "p", "label": "Instance"}, {"var": "v", "label": "Service"}], "edges": [{"var": "k", "label": "Backs", "from": "p", "to": "v"}]}},
           {"id": "backed", "verb": "project", "columns": {"instance": {"ref": "p"}, "service": {"ref": "v"}}},
           {"id": "calls", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "a", "label": "Instance"}, {"var": "b", "label": "Instance"}], "edges": [{"var": "c", "label": "Calls", "from": "a", "to": "b"}]}},
           {"id": "called", "verb": "project", "columns": {"c": {"ref": "c"}, "fromInstance": {"ref": "a"}, "toInstance": {"ref": "b"}}},
           {"id": "sources", "verb": "project", "columns": {"fromInstance1": {"ref": "instance"}, "s": {"ref": "service"}}},
           {"id": "bySource", "verb": "join", "kind": "inner", "on": [{"left": "fromInstance", "right": "fromInstance1"}]},
           {"id": "targets", "verb": "project", "columns": {"toInstance1": {"ref": "instance"}, "t": {"ref": "service"}}},
           {"id": "byTarget", "verb": "join", "kind": "inner", "on": [{"left": "toInstance", "right": "toInstance1"}]},
           {"id": "apart", "verb": "where", "condition": {"fn": "!=", "args": [{"ref": "s"}, {"ref": "t"}]}},
           {"id": "grouped", "verb": "summarize", "by": {"s": {"ref": "s"}, "t": {"ref": "t"}}, "aggregates": {"n": {"fn": "count"}}},
           {"id": "ends", "verb": "project", "columns": {"s": {"ref": "s"}, "t": {"ref": "t"}}},
           {"id": "emit", "verb": "edges", "target": {
             "nodes": [{"var": "x", "label": "ServiceNode", "id": {"ref": "s"}}, {"var": "y", "label": "ServiceNode", "id": {"ref": "t"}}],
             "edges": [{"var": "e", "label": "DependsOn", "from": "x", "to": "y", "key": "pair"}]}}],
         "wires": [{"from": "backing", "to": "backed"}, {"from": "calls", "to": "called"}, {"from": "backed", "to": "sources"}, {"from": "backed", "to": "targets"},
                   {"from": "called", "to": "bySource", "port": "left"}, {"from": "sources", "to": "bySource", "port": "right"},
                   {"from": "bySource", "to": "byTarget", "port": "left"}, {"from": "targets", "to": "byTarget", "port": "right"},
                   {"from": "byTarget", "to": "apart"}, {"from": "apart", "to": "grouped"}, {"from": "grouped", "to": "ends"}, {"from": "ends", "to": "emit"}]}
        """;

    [Fact]
    public void An_edge_emitted_from_a_group_names_every_row_of_the_group_forward_and_reverse_like_a_row_level_emit()
    {
        var files = EstateFiles();
        var i = files.FindIndex(f => f.Path.EndsWith("acme.space.module.gkql.json", StringComparison.Ordinal));
        var space = JsonNode.Parse(files[i].Text)!;
        space["imports"]!.AsArray().Add(new JsonObject { ["module"] = "acme.bygroup", ["alias"] = "bg", ["version"] = "1.0.0", ["digest"] = null });
        space["space"]!["graphs"]!.AsArray().Add(new JsonObject { ["name"] = "byGroup", ["projection"] = "bg.pairs", ["inputs"] = new JsonObject { ["master"] = "master" } });
        files[i] = files[i] with { Text = space.ToJsonString() };
        files.AddRange([new SourceFile("acme.bygroup.module.gkql.json", ByGroup), new SourceFile("acme.bygroup.pairs.gkql.json", Pairs)]);
        var h = new SpaceHost(files, new HostOptions { Projections = Repo.Projections });
        using (h)
        {
            new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 200, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000}).Run(Estate);
            h.Finish();
            var grouped = h.Snapshot("byGroup", null)["extent"]!["relationships"]!.AsArray();
            var rowLevel = h.Snapshot("serviceTree", null)["extent"]!["relationships"]!.AsArray().Where(r => r!["type"]!["value"]!.GetValue<string>().EndsWith("DependsOn", StringComparison.Ordinal)).ToList();
            Assert.Single(grouped); Assert.Single(rowLevel);                                                       // web to api
            foreach (var edge in grouped)
            {
                var id = edge!["id"]!["value"]!.GetValue<string>();
                var l = h.Lineage("byGroup", id);
                Assert.True(l["complete"]!.GetValue<bool>());
                Assert.Equal("3", l["contributions"]!.GetValue<string>());                                         // three instance calls make up the service dependency
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

    // ---- lineage through the steps of a projection, on a small world of items and tags ----------------------------------------------

    const string MiniBase = """
        {"module": "t.base", "version": "1.0.0", "imports": [],
         "entities": [{"name": "Item", "properties": [{"name": "key", "type": "Text"}, {"name": "group", "type": "Text"}, {"name": "score", "type": "Text"}]},
                      {"name": "Tag", "properties": [{"name": "key", "type": "Text"}, {"name": "label", "type": "Text"}]}],
         "identities": [{"name": "item", "type": "Item", "components": [{"name": "key", "type": "Text"}]},
                        {"name": "tag", "type": "Tag", "components": [{"name": "key", "type": "Text"}]}],
         "rows": [{"name": "CapabilityRow", "fields": [{"name": "capId", "type": "Text"}, {"name": "name", "type": "Text"}, {"name": "owner", "type": "Text"}, {"name": "status", "type": "Text"}, {"name": "parentId", "type": "Text", "optional": true}]},
                  {"name": "RealizesRow", "fields": [{"name": "capId", "type": "Text"}, {"name": "site", "type": "Text"}, {"name": "namespace", "type": "Text"}, {"name": "service", "type": "Text"}]}]}
        """;

    /// <summary>The row types CapabilityRow and RealizesRow, the rows a host takes from a test: an item is a capability row (its name the group, its owner the score), a tag a realizes row (its site the label).</summary>
    const string MiniIngest = """
        {"graph": "ingest", "ontology": "t.base", "inputs": [{"name": "items", "rows": "t.base.CapabilityRow"}, {"name": "tags", "rows": "t.base.RealizesRow"}], "parameters": [],
         "verbs": [
           {"id": "items", "verb": "rows", "source": "items"},
           {"id": "tags", "verb": "rows", "source": "tags"},
           {"id": "item", "verb": "nodes", "rule": "observed", "target": {"nodes": [
             {"var": "i", "label": "Item", "id": {"identity": "item", "args": [{"ref": "capId"}]}, "props": {"key": {"ref": "capId"}, "group": {"ref": "name"}, "score": {"ref": "owner"}}}]}},
           {"id": "tag", "verb": "nodes", "rule": "observed", "target": {"nodes": [
             {"var": "t", "label": "Tag", "id": {"identity": "tag", "args": [{"ref": "capId"}]}, "props": {"key": {"ref": "capId"}, "label": {"ref": "site"}}}]}}],
         "wires": [{"from": "items", "to": "item"}, {"from": "tags", "to": "tag"}]}
        """;

    const string Rolled = """{"name": "rolled", "projection": "v.roll", "inputs": {"master": "master"}}""";

    /// <summary>A host over the small world: items and tags as authored rows in the master, the module `t.view` with the members `view` (its entities, relationships and identities) and its projection `roll` with `verbs` (its verbs and wires), the space with `graphs`; `imports` and `more` add a module.</summary>
    static SpaceHost Mini(string view, string verbs, string graphs = Rolled, string imports = "", params SourceFile[] more) =>
        new(
        [
            new("t.base.module.gkql.json", MiniBase), new("t.base.ingest.gkql.json", MiniIngest),
            new("t.view.module.gkql.json", """{"module": "t.view", "version": "1.0.0", "imports": [{"module": "t.base", "alias": "base", "version": "1.0.0", "digest": null}], """ + view + "}"),
            new("t.view.roll.gkql.json", """{"graph": "roll", "ontology": "t.view", "inputs": [{"name": "master", "ontology": "t.base"}], "parameters": [], """ + verbs + "}"),
            .. more,
            new("t.space.module.gkql.json", """
                {"module": "t.space", "version": "1.0.0",
                 "imports": [{"module": "t.base", "alias": "base", "version": "1.0.0", "digest": null}, {"module": "t.view", "alias": "v", "version": "1.0.0", "digest": null}
                """ + imports + """
                 ],
                 "space": {"name": "s",
                   "sources": [{"name": "items", "adapter": "record", "rows": "base.CapabilityRow"}, {"name": "tags", "adapter": "record", "rows": "base.RealizesRow"}],
                   "graphs": [{"name": "master", "projection": "base.ingest", "master": true, "inputs": {"items": "items", "tags": "tags"}},
                """ + graphs + "]}}"),
        ], new HostOptions { Projections = Repo.Projections});

    static void Items(SpaceHost h, long seq, params (string Key, string Group, string Score, long W)[] rows)
    {
        var rw = new RowWriter(h.RowLayout("items"), h.Interner);
        foreach (var (key, group, score, w) in rows) { rw.Begin(w); rw.Text("capId", key); rw.Text("name", group); rw.Text("owner", score); rw.Text("status", "live"); }
        h.Ingest("items", rw.Build()!, seq);
    }

    const string Summary = """
        "entities": [{"name": "Summary", "properties": [{"name": "group", "type": "Text"}, {"name": "best", "type": "Integer", "optional": true}]}],
        "identities": [{"name": "summary", "type": "Summary", "components": [{"name": "group", "type": "Text"}]}]
        """;

    /// <summary>The items of the master with their properties, then `steps` one after the other: the verbs of a projection whose wires run in their order.</summary>
    static string Matched(string steps)
    {
        var verbs = new JsonArray(
            JsonNode.Parse("""{"verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "i", "label": "Item"}]}}"""),
            JsonNode.Parse("""{"verb": "project", "columns": {"key": {"ref": "i.key"}, "group": {"ref": "i.group"}, "score": {"ref": "i.score"}}}"""));
        foreach (var step in JsonNode.Parse(steps)!.AsArray().ToList()) verbs.Add(step!.DeepClone());
        var wires = new JsonArray();
        for (var n = 0; n < verbs.Count; n++)
        {
            verbs[n]!["id"] = "s" + n;
            if (n > 0) wires.Add(new JsonObject { ["from"] = "s" + (n - 1), ["to"] = "s" + n });
        }
        return $"\"verbs\": {verbs.ToJsonString()}, \"wires\": {wires.ToJsonString()}";
    }

    /// <summary>Code review 2, defect 16: an element names the elements it is made of also when a `project` or a `where` stands between the match and the summary, between the summary and the step, or between the match and the step. Before, only the first shape named anything.</summary>
    [Theory]
    [InlineData("the summary read directly",
            """
            [{"verb": "summarize", "by": {"group": {"ref": "group"}}, "aggregates": {"best": {"fn": "count"}}},
             {"verb": "nodes", "target": {"nodes": [{"var": "x", "label": "Summary", "id": {"identity": "summary", "args": [{"ref": "group"}]}, "props": {"group": {"ref": "group"}, "best": {"ref": "best"}}}]}}]
            """,
        "a=a1+a2 b=b1", "a=a1 b=b1")]
    [InlineData("a project and a where before the summary",
            """
            [{"verb": "project", "columns": {"grp": {"ref": "group"}, "s": {"ref": "score"}}},
             {"verb": "where", "condition": {"fn": "!=", "args": [{"ref": "s"}, {"lit": "0"}]}},
             {"verb": "summarize", "by": {"group": {"ref": "grp"}}, "aggregates": {"best": {"fn": "count"}}},
             {"verb": "nodes", "target": {"nodes": [{"var": "x", "label": "Summary", "id": {"identity": "summary", "args": [{"ref": "group"}]}, "props": {"group": {"ref": "group"}, "best": {"ref": "best"}}}]}}]
            """,
        "a=a1+a2 b=b1", "a=a1 b=b1")]
    [InlineData("a project and a where after the summary",
            """
            [{"verb": "summarize", "by": {"group": {"ref": "group"}}, "aggregates": {"best": {"fn": "count"}}},
             {"verb": "project", "columns": {"name": {"ref": "group"}, "n": {"ref": "best"}}},
             {"verb": "where", "condition": {"fn": ">", "args": [{"ref": "n"}, {"lit": 0}]}},
             {"verb": "nodes", "target": {"nodes": [{"var": "x", "label": "Summary", "id": {"identity": "summary", "args": [{"ref": "name"}]}, "props": {"group": {"ref": "name"}, "best": {"ref": "n"}}}]}}]
            """,
        "a=a1+a2 b=b1", "a=a1 b=b1")]
    [InlineData("projects on both sides, the second without the key as it was",
            """
            [{"verb": "project", "columns": {"grp": {"ref": "group"}}},
             {"verb": "summarize", "by": {"group": {"ref": "grp"}}, "aggregates": {"best": {"fn": "count"}}},
             {"verb": "project", "columns": {"name": {"fn": "concat", "args": [{"ref": "group"}, {"lit": "!"}]}, "n": {"ref": "best"}}},
             {"verb": "project", "columns": {"name": {"ref": "name"}, "n": {"ref": "n"}}},
             {"verb": "nodes", "target": {"nodes": [{"var": "x", "label": "Summary", "id": {"identity": "summary", "args": [{"ref": "name"}]}, "props": {"group": {"ref": "name"}, "best": {"ref": "n"}}}]}}]
            """,
        "a!=a1+a2 b!=b1", "a!=a1 b!=b1")]
    [InlineData("row by row through a project",
            """
            [{"verb": "project", "columns": {"k": {"ref": "key"}, "s": {"ref": "score"}}},
             {"verb": "where", "condition": {"fn": "!=", "args": [{"ref": "s"}, {"lit": "0"}]}},
             {"verb": "nodes", "target": {"nodes": [{"var": "x", "label": "Summary", "id": {"identity": "summary", "args": [{"ref": "k"}]}, "props": {"group": {"ref": "k"}}}]}}]
            """,
        "a1=a1 a2=a2 b1=b1", "a1=a1 b1=b1")]
    public void An_element_names_what_it_is_made_of_through_project_and_where(string shape, string steps, string named, string afterOneWent)
    {
        using var h = Mini(Summary, Matched(steps));
        string Named()
        {
            var keyOf = h.Entities("master").ToDictionary(x => x["id"].Id(), x => x.Prop("key")!);
            return string.Join(' ', h.Entities("rolled").Select(x => x.Prop("group") + "="
                + string.Join('+', h.Lineage("rolled", x["id"].Id())["sources"]!.AsArray().Select(s => keyOf[s!["element"]!.GetValue<string>()]).Order(StringComparer.Ordinal))).Order(StringComparer.Ordinal));
        }
        Items(h, 1, ("a1", "a", "1", 1), ("a2", "a", "2", 1), ("b1", "b", "3", 1));
        Assert.True(named == Named(), $"{shape}: {Named()}");
        Items(h, 2, ("a2", "a", "2", -1));
        Assert.True(afterOneWent == Named(), $"{shape}, after a2 went: {Named()}");
    }

    /// <summary>The lineage of a row that binds a reference (Def. 3.14): the graph `rolled` holds only relationships between items and tags of the master, so both ends are references there. A reference is an entity of another graph: it is named in the input of the projection whose graph holds its type, and not at all when the projection reads no such graph. The relationship is named in the graph it was matched in.</summary>
    [Theory]
    [InlineData(true, "i@master p@rolled t@master")]
    [InlineData(false, "p@rolled")]
    public void A_reference_is_named_in_the_graph_that_holds_it(bool masterToo, string named)
    {
        const string top = """
            {"module": "t.top", "version": "1.0.0",
             "imports": [{"module": "t.base", "alias": "base", "version": "1.0.0", "digest": null}, {"module": "t.view", "alias": "v", "version": "1.0.0", "digest": null}],
             "relationships": [{"name": "Seen", "from": "base.Item", "to": "base.Tag"}]}
            """;
        const string paired = """
            "verbs": [
              {"id": "items", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "i", "label": "Item"}]}},
              {"id": "byGroup", "verb": "project", "columns": {"item": {"ref": "i"}, "g": {"ref": "i.group"}}},
              {"id": "tags", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "t", "label": "Tag"}]}},
              {"id": "byKey", "verb": "project", "columns": {"t": {"ref": "t"}, "tk": {"ref": "t.key"}}},
              {"id": "matched", "verb": "join", "kind": "inner", "on": [{"left": "g", "right": "tk"}]},
              {"id": "ends", "verb": "project", "columns": {"item": {"ref": "item"}, "t": {"ref": "t"}}},
              {"id": "paired", "verb": "edges", "target": {
                "nodes": [{"var": "a", "label": "Item", "id": {"ref": "item"}}, {"var": "b", "label": "Tag", "id": {"ref": "t"}}],
                "edges": [{"var": "e", "label": "Paired", "from": "a", "to": "b"}]}}],
            "wires": [{"from": "items", "to": "byGroup"}, {"from": "tags", "to": "byKey"}, {"from": "byGroup", "to": "matched", "port": "left"}, {"from": "byKey", "to": "matched", "port": "right"},
                      {"from": "matched", "to": "ends"}, {"from": "ends", "to": "paired"}]
            """;
        var seenOf = """
            {"graph": "top", "ontology": "t.top", "parameters": [],
             "verbs": [
               {"id": "pairs", "verb": "graph-match", "graph": "rolled", "pattern": {"nodes": [{"var": "i", "label": "Item"}, {"var": "t", "label": "Tag"}], "edges": [{"var": "p", "label": "Paired", "from": "i", "to": "t"}]}},
               {"id": "ends", "verb": "project", "columns": {"i": {"ref": "i"}, "t": {"ref": "t"}}},
               {"id": "seen", "verb": "edges", "target": {
                 "nodes": [{"var": "a", "label": "Item", "id": {"ref": "i"}}, {"var": "b", "label": "Tag", "id": {"ref": "t"}}],
                 "edges": [{"var": "e", "label": "Seen", "from": "a", "to": "b"}]}}],
             "wires": [{"from": "pairs", "to": "ends"}, {"from": "ends", "to": "seen"}],
             "inputs": [{"name": "rolled", "ontology": "t.view"}
            """ + (masterToo ? """, {"name": "master", "ontology": "t.base"}""" : "") + "]}";
        using var h = Mini(Summary + """, "relationships": [{"name": "Paired", "from": "base.Item", "to": "base.Tag"}]""", paired,
            Rolled + """, {"name": "top", "projection": "t2.top", "inputs": {"rolled": "rolled" """ + (masterToo ? """, "master": "master" """ : "") + "}}",
            """, {"module": "t.top", "alias": "t2", "version": "1.0.0", "digest": null}""",
            new SourceFile("t.top.module.gkql.json", top),
            new SourceFile("t.top.top.gkql.json", seenOf));
        var tags = new RowWriter(h.RowLayout("tags"), h.Interner);
        tags.Begin(1); tags.Text("capId", "g1"); tags.Text("site", "L"); tags.Text("namespace", "n"); tags.Text("service", "s");
        h.Ingest("tags", tags.Build()!, 1);
        Items(h, 2, ("a1", "g1", "1", 1));
        var seen = h.Relationships("top", "Seen").Single()["id"].Id();
        var lineage = h.Lineage("top", seen);
        Assert.Equal(named, string.Join(' ', Sources(lineage).Order(StringComparer.Ordinal)));
        foreach (var s in lineage["sources"]!.AsArray())                                // each named element is in the graph it is named in, and knows what it is part of
            Assert.Contains(h.LineageReverse(s!["graph"]!.GetValue<string>(), s["element"]!.GetValue<string>())["partOf"]!.AsArray(), p => p!["element"]!.GetValue<string>() == seen);
    }
}
