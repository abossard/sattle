using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>A relationship whose endpoint is not an entity of the graph says so: the snapshot lists the endpoint as a reference (absent from every graph, or held by another graph, which it names) with the key it was minted from, and `/space` counts the relationships by cause. A viewer can draw a stub or resolve it.</summary>
public class ReferencesTests
{
    static string Estate => EstateTests.EstateDir;

    /// <summary>A module whose projection `calls` emits only the `Calls` relationships of the master: its endpoints are instances of the master, none of them an entity of this graph.</summary>
    const string CallsView = """
        {"module": "acme.callsview", "version": "1.0.0",
         "imports": [{"module": "sattle.infra", "alias": "infra", "version": "1.0.0", "digest": null},
                     {"module": "acme.ops", "alias": "ops", "version": "1.0.0", "digest": null}]}
        """;

    const string Calls = """
        {"graph": "calls", "ontology": "sattle.infra", "history": 4, "inputs": [{"name": "master", "ontology": "acme.ops"}], "parameters": [],
         "verbs": [
           {"id": "calls", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "x", "label": "Instance"}, {"var": "y", "label": "Instance"}], "edges": [{"var": "k", "label": "Calls", "from": "x", "to": "y"}]}},
           {"id": "emit", "verb": "edges", "target": {
             "nodes": [{"var": "a", "label": "Instance", "id": {"ref": "x"}}, {"var": "b", "label": "Instance", "id": {"ref": "y"}}],
             "edges": [{"var": "e", "label": "Calls", "from": "a", "to": "b", "key": "c"}]}}],
         "wires": [{"from": "calls", "to": "emit"}]}
        """;

    static List<SourceFile> Files()
    {
        var files = Catalog.ReadDirectory(Repo.Definitions).ToList();
        var i = files.FindIndex(f => f.Path.EndsWith("acme.space.module.gkql.json", StringComparison.Ordinal));
        var space = JsonNode.Parse(files[i].Text)!;
        space["imports"]!.AsArray().Add(new JsonObject { ["module"] = "acme.callsview", ["alias"] = "cv", ["version"] = "1.0.0", ["digest"] = null });
        space["space"]!["graphs"]!.AsArray().Add(new JsonObject { ["name"] = "callsOnly", ["projection"] = "cv.calls", ["inputs"] = new JsonObject { ["master"] = "master" } });
        files[i] = files[i] with { Text = space.ToJsonString() };
        files.AddRange([new SourceFile("acme.callsview.module.gkql.json", CallsView), new SourceFile("acme.callsview.calls.gkql.json", Calls)]);
        return files;
    }

    static (SpaceHost Host, JsonNode Space) Serve(string dir, List<SourceFile> files)
    {
        var h = new SpaceHost(files, new HostOptions { Projections = Repo.Projections, LateHorizonWindows = 1_000_000 });
        new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 200, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000}).Run(dir);
        h.Finish();
        return (h, h.SpaceInfo());
    }

    static JsonNode Entry(JsonNode space, string graph) => space["graphs"]!.AsArray().Single(g => g!["id"]!.GetValue<string>() == graph)!;

    /// <summary>The estate's flows only: the instances they name were never listed.</summary>
    static string FlowsOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-refs-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(dir, "records"));
        File.WriteAllLines(Path.Combine(dir, "records", "flows.ndjson"), File.ReadLines(Path.Combine(Estate, "records", "records.ndjson")).Where(l => l.Contains("\"source\":\"records:FlowRow\"", StringComparison.Ordinal)));
        return dir;
    }

    /// <summary>The one exception is `authored`: a `Realizes` edge runs from a service, which the master holds, so its service end is a reference that names the master.</summary>
    [Fact]
    public void The_estate_has_no_relationship_without_an_endpoint_and_so_no_references_except_the_service_ends_of_what_people_wrote()
    {
        var (h, space) = Serve(Estate, Catalog.ReadDirectory(Repo.Definitions).ToList());
        using (h)
            foreach (var g in space["graphs"]!.AsArray().Where(x => x!["id"]!.GetValue<string>() != "catalog"))
            {
                var id = g!["id"]!.GetValue<string>();
                var refs = h.Snapshot(id, null)["extent"]!["references"]!.AsArray();
                if (id == "authored")
                {
                    var realizes = h.Relationships("authored", "Realizes").Count;
                    Assert.Equal(realizes.ToString(), g["dangling"]!.GetValue<string>());
                    Assert.All(refs, r => { Assert.Equal("inOtherGraph", r!["state"]!.GetValue<string>()); Assert.Equal("master", r["graph"]!.GetValue<string>()); Assert.Equal("Service", r["entityType"]!.GetValue<string>()); });
                    continue;
                }
                Assert.Equal("0", g["dangling"]!.GetValue<string>());
                Assert.Empty(g["danglingBy"]!.AsArray());
                Assert.Empty(refs);
            }
    }

    [Fact]
    public void An_instance_a_flow_names_and_no_source_listed_is_a_reference_absent_from_every_graph_with_the_key_it_was_minted_from()
    {
        var dir = FlowsOnly();
        try
        {
            var (h, space) = Serve(dir, Catalog.ReadDirectory(Repo.Definitions).ToList());
            using (h)
            {
                var refs = h.Snapshot("master", null)["extent"]!["references"]!.AsArray();
                Assert.Equal(5, refs.Count);                                                                   // web-7d4-1, -2 and -3, api-5c9-1 and -2
                Assert.All(refs, r =>
                {
                    Assert.Equal("absent", r!["state"]!.GetValue<string>());
                    Assert.Null(r["graph"]);
                    Assert.Equal("Instance", r["entityType"]!.GetValue<string>());
                    var key = r["identity"]!["key"]!.AsObject();
                    Assert.Equal("shop", key["namespace"]!.GetValue<string>());
                    Assert.Contains(key["name"]!.GetValue<string>(), new[] { "web-7d4-1", "web-7d4-2", "web-7d4-3", "api-5c9-1", "api-5c9-2" });
                });
                var by = Entry(space, "master")["danglingBy"]!.AsArray();
                Assert.All(by, x => { Assert.Contains(x!["relationshipType"]!.GetValue<string>(), new[] { "Calls", "Sends" }); Assert.Equal("absent", x["state"]!.GetValue<string>()); });       // the calls between instances and the flows of an instance to an address outside
                Assert.Equal(["source", "target"], by.Select(x => x!["end"]!.GetValue<string>()).Order().Distinct().ToArray());
                var dangling = long.Parse(Entry(space, "master")["dangling"]!.GetValue<string>());
                Assert.True(dangling > 0 && by.Sum(x => long.Parse(x!["relationships"]!.GetValue<string>())) >= dangling);   // a relationship with both ends missing counts once in `dangling` and once per end in `danglingBy`
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_endpoint_that_another_graph_holds_is_a_reference_that_names_the_graph()
    {
        var (h, space) = Serve(Estate, Files());
        using (h)
        {
            var refs = h.Snapshot("callsOnly", null)["extent"]!["references"]!.AsArray();
            Assert.NotEmpty(refs);
            Assert.All(refs, r => { Assert.Equal("inOtherGraph", r!["state"]!.GetValue<string>()); Assert.Equal("master", r["graph"]!.GetValue<string>()); Assert.Equal("Instance", r["entityType"]!.GetValue<string>()); });
            Assert.All(Entry(space, "callsOnly")["danglingBy"]!.AsArray(), x => { Assert.Equal("inOtherGraph", x!["state"]!.GetValue<string>()); Assert.Equal("master", x["graph"]!.GetValue<string>()); });
        }
    }

    [Fact]
    public void The_references_alone_are_paged_by_id_and_equal_the_references_of_the_snapshot()
    {
        var dir = FlowsOnly();
        try
        {
            var (h, _) = Serve(dir, Catalog.ReadDirectory(Repo.Definitions).ToList());
            using (h)
            {
                var inSnapshot = h.Snapshot("master", null)["extent"]!["references"]!.AsArray().Select(x => x!.ToJsonString()).ToList();
                var paged = new List<string>(); string? after = null; var pages = 0;
                do
                {
                    var page = h.References("master", null, 2, after);
                    Assert.Equal("5", page["count"]!.GetValue<string>());
                    Assert.True(page["references"]!.AsArray().Count <= 2);
                    paged.AddRange(page["references"]!.AsArray().Select(x => x!.ToJsonString()));
                    after = page["next"]?.GetValue<string>(); pages++;
                } while (after is not null);
                Assert.Equal(3, pages);
                Assert.Equal(inSnapshot, paged);
                Assert.Null(h.Snapshot("master", null, references: false)["extent"]!["references"]);
                Assert.Equal(404, Assert.Throws<HostError>(() => h.References("master", 9_999_999, 2)).Status);
                var early = h.References("master", 1, 100);                                    // revision 1: an earlier state is read from the history
                Assert.Equal("1", early["revision"]!.GetValue<string>());
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_static_files_hold_the_references_and_the_meter_declarations_the_host_serves()
    {
        var dir = FlowsOnly(); var outDir = Path.Combine(Path.GetTempPath(), "sattle-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = Catalog.ReadDirectory(Repo.Definitions).ToList();
            var (h, _) = Serve(dir, files);
            using (h)
            {
                var run = ReplayTests.Replay(dir, o: new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 });
                run.Read(x => { x.WriteFiles(outDir, Repo.LoadExamples(out _).Spaces.Values.Single()); return 0; });
                var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "manifest.json")))!["graphs"]!.AsObject();
                Assert.All(manifest, g =>
                {
                    Assert.NotNull(g.Value!["references"]); Assert.NotNull(g.Value["meters"]);
                    Assert.True(File.Exists(Path.Combine(outDir, g.Value["references"]!.GetValue<string>())));
                    Assert.Equal(h.Meters(g.Key)["meters"]!.ToJsonString(), JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, g.Value["meters"]!.GetValue<string>())))!["meters"]!.ToJsonString());
                });
                var refs = File.ReadAllLines(Path.Combine(outDir, manifest["master"]!["references"]!.GetValue<string>()));
                Assert.Equal(5, refs.Length);
                Assert.Equal(h.Snapshot("master", null)["extent"]!["references"]!.AsArray().Select(x => x!.ToJsonString()).ToList(), refs.ToList());
            }
        }
        finally { Directory.Delete(dir, true); if (Directory.Exists(outDir)) Directory.Delete(outDir, true); }
    }
}
