using System.Text.Json.Nodes;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>PROTOCOL 3.29, a second input graph. A space with two graphs that ingest: the master holds what was observed, `authored` what people wrote. Source rows reach the graph that binds them, a view edit lands in the graph that ingests the edited type, and an authored element is complete in its lineage.</summary>
public class AuthoredGraphTests
{
    const string Base = """
        {"module": "t.base", "version": "1.0.0", "imports": [],
         "entities": [{"name": "Item", "properties": [{"name": "k", "type": "Text"}, {"name": "a", "type": "Text"}, {"name": "d", "type": "Text"}, {"name": "status", "type": "Text"}]},
                      {"name": "Seen", "properties": [{"name": "name", "type": "Text"}]}],
         "relationships": [{"name": "Under", "from": "Item", "to": "Item"}],
         "identities": [{"name": "item", "type": "Item", "components": [{"name": "k", "type": "Text"}]},
                        {"name": "seen", "type": "Seen", "components": [{"name": "name", "type": "Text"}]}],
         "rows": [{"name": "CapabilityRow", "fields": [{"name": "capId", "type": "Text"}, {"name": "name", "type": "Text"}, {"name": "owner", "type": "Text"}, {"name": "status", "type": "Text"}, {"name": "parentId", "type": "Text", "optional": true}]},
                  {"name": "RealizesRow", "fields": [{"name": "capId", "type": "Text"}, {"name": "site", "type": "Text"}, {"name": "namespace", "type": "Text"}, {"name": "service", "type": "Text"}]}]}
        """;

    const string Ingest = """
        {"graph": "ingest", "ontology": "t.base", "inputs": [{"name": "obs", "rows": "t.base.RealizesRow"}], "parameters": [],
         "verbs": [
           {"id": "obs", "verb": "rows", "source": "obs"},
           {"id": "seen", "verb": "nodes", "rule": "observed", "target": {"nodes": [
             {"var": "s", "label": "Seen", "id": {"identity": "seen", "args": [{"ref": "service"}]}, "props": {"name": {"ref": "service"}}}]}}],
         "wires": [{"from": "obs", "to": "seen"}],
         "rules": [{"name": "observed", "description": "what was observed"}]}
        """;

    const string Authored = """
        {"graph": "authored", "ontology": "t.base", "inputs": [{"name": "items", "rows": "t.base.CapabilityRow"}], "parameters": [],
         "verbs": [
           {"id": "items", "verb": "rows", "source": "items"},
           {"id": "item", "verb": "nodes", "rule": "written", "target": {"nodes": [
             {"var": "i", "label": "Item", "id": {"identity": "item", "args": [{"ref": "capId"}]}, "props": {"k": {"ref": "capId"}, "a": {"ref": "name"}, "d": {"ref": "owner"}, "status": {"ref": "status"}}}]}},
           {"id": "under", "verb": "edges", "rule": "written", "target": {
             "nodes": [{"var": "i", "label": "Item", "id": {"identity": "item", "args": [{"ref": "capId"}]}},
                       {"var": "p", "label": "Item", "id": {"identity": "item", "args": [{"ref": "parentId"}]}}],
             "edges": [{"var": "u", "label": "Under", "from": "i", "to": "p"}]}}],
         "wires": [{"from": "items", "to": "item"}, {"from": "items", "to": "under"}],
         "rules": [{"name": "written", "accept": ["create", "delete"], "update": ["k", "a", "d", "status"]}]}
        """;

    const string View = """
        {"module": "t.view", "version": "1.0.0", "imports": [{"module": "t.base", "alias": "base", "version": "1.0.0", "digest": null}],
         "entities": [{"name": "V", "properties": [{"name": "a", "type": "Text"}]}]}
        """;

    const string Lens = """
        {"graph": "v", "ontology": "t.view", "inputs": [{"name": "authored", "ontology": "t.base"}], "parameters": [],
         "verbs": [
           {"id": "shown", "verb": "graph-match", "graph": "authored", "pattern": {"nodes": [{"var": "i", "label": "Item"}]}},
           {"id": "items", "verb": "nodes", "rule": "items", "target": {"nodes": [{"var": "v", "label": "V", "id": {"ref": "i"}, "props": {"a": {"ref": "i.a"}}}]}}],
         "wires": [{"from": "shown", "to": "items"}],
         "rules": [{"name": "items", "update": ["a"]}]}
        """;

    static string Space(string graphs, string sources = "") => $$$"""
        {"module": "t.space", "version": "1.0.0",
         "imports": [{"module": "t.base", "alias": "base", "version": "1.0.0", "digest": null}, {"module": "t.view", "alias": "view", "version": "1.0.0", "digest": null}],
         "space": {"name": "s",
                   "sources": [{"name": "items", "adapter": "record", "rows": "base.CapabilityRow"}, {"name": "obs", "adapter": "record", "rows": "base.RealizesRow"}{{{sources}}}],
                   "graphs": [{{{graphs}}}]}}
        """;

    const string Graphs = """
        {"name": "master", "projection": "base.ingest", "master": true, "inputs": {"obs": "obs"}},
        {"name": "authored", "projection": "base.authored", "inputs": {"items": "items"}},
        {"name": "view", "projection": "view.v", "inputs": {"authored": "authored"}}
        """;

    /// <summary>`document` with the arrays of the object `more` appended to its arrays of the same name, and its other members added.</summary>
    static string With(string document, string more)
    {
        var d = JsonNode.Parse(document)!.AsObject();
        foreach (var (name, value) in JsonNode.Parse(more)!.AsObject())
            if (d[name] is JsonArray items) foreach (var x in value!.AsArray()) items.Add(x!.DeepClone());
            else d[name] = value!.DeepClone();
        return d.ToJsonString();
    }

    /// <summary>The definitions: the base module with the members of `types` added, its two ingestion projections with the verbs, wires, inputs and rules of `observed` and `written` added, the lens view and the space with `graphs`.</summary>
    static SourceFile[] Files(string types = "{}", string observed = "{}", string written = "{}", string graphs = Graphs, string sources = "") =>
    [
        new("t.base.module.gkql.json", With(Base, types)), new("t.base.ingest.gkql.json", With(Ingest, observed)), new("t.base.authored.gkql.json", With(Authored, written)),
        new("t.view.module.gkql.json", View), new("t.view.v.gkql.json", Lens), new("t.space.module.gkql.json", Space(graphs, sources)),
    ];

    static SpaceHost Host()
    {
        var h = new SpaceHost(Files(), new HostOptions { Projections = Repo.Projections});
        var rw = new RowWriter(h.RowLayout("items"), h.Interner);
        foreach (var k in new[] { "k1", "k2" }) { rw.Begin(); rw.Text("capId", k); rw.Text("name", "a-" + k); rw.Text("owner", "d"); rw.Text("status", "live"); }
        h.Ingest("items", rw.Build()!, 1);
        var ow = new RowWriter(h.RowLayout("obs"), h.Interner);
        ow.Begin(); ow.Text("capId", "k1"); ow.Text("site", "c"); ow.Text("namespace", "n"); ow.Text("service", "web");
        h.Ingest("obs", ow.Build()!, 2);
        return h;
    }

    static string Head(SpaceHost h, string g) => h.SpaceInfo()["graphs"]!.AsArray().First(x => x!["id"]!.GetValue<string>() == g)!["head"]!.GetValue<string>();
    static string Role(SpaceHost h, string g) => h.SpaceInfo()["graphs"]!.AsArray().First(x => x!["id"]!.GetValue<string>() == g)!["role"]!.GetValue<string>();

    [Fact]
    public void Rows_reach_the_graph_that_binds_their_source()
    {
        using var h = Host();
        string[] Types(string g) => [.. h.Entities(g).Select(e => e["type"].Id().Split('.')[^1]).Distinct().Order()];
        Assert.Equal(["Seen"], Types("master"));
        Assert.Equal(["Item"], Types("authored"));
        Assert.Equal(("master", "input", "projection"), (Role(h, "master"), Role(h, "authored"), Role(h, "view")));
    }

    [Fact]
    public void A_view_edit_lands_in_the_graph_that_ingests_the_edited_type()
    {
        using var h = Host();
        var masterHead = Head(h, "master");
        var shown = h.Entities("view").OrderBy(x => x["id"].Id(), StringComparer.Ordinal).First();
        var id = shown["id"].Id();
        static JsonObject V(string id) => new() { ["value"] = id };
        JsonNode Ent(string a) => new JsonObject { ["id"] = V(id), ["type"] = V("V"), ["properties"] = new JsonArray(new JsonObject { ["property"] = V("a"), ["value"] = new JsonObject { ["kind"] = "text", ["value"] = a } }) };
        var res = h.ApplyEdits("view", new JsonObject
        {
            ["baseRevisions"] = new JsonObject { ["view"] = Head(h, "view"), ["authored"] = Head(h, "authored") },
            ["edits"] = new JsonArray(new JsonObject { ["kind"] = "entity", ["id"] = id, ["before"] = Ent(shown.Prop("a")!), ["after"] = Ent("changed") }),
        });
        Assert.True(res["accepted"]!.GetValue<bool>());
        Assert.NotNull(res["revisions"]!["authored"]);
        Assert.Equal("changed", h.Entities("authored").Single(x => x["id"].Id() == id).Prop("a"));
        Assert.Equal(masterHead, Head(h, "master"));
    }

    [Fact]
    public void An_authored_element_is_complete_in_its_lineage()
    {
        using var h = Host();
        var item = h.Entities("authored", "Item").First()["id"].Id();
        Assert.True(h.Lineage("authored", item)["complete"]!.GetValue<bool>());
    }

    /// <summary>What a graph other than the master that ingests may not do: share a source, link identities, write windows.</summary>
    [Theory]
    [InlineData("a source bound twice", "{}", "{}", "bound by more than one graph")]
    [InlineData("a link in authored", """
        {"identities": [{"name": "itemName", "type": "Item", "components": [{"name": "name", "type": "Text"}]}],
         "equivalences": [{"name": "itemKeyIsName", "left": "item", "right": "itemName", "mode": "assert", "threshold": 1.0}]}
        """, """
        {"verbs": [{"id": "linked", "verb": "link", "rule": "linked", "link": {"policy": "itemKeyIsName", "left": {"identity": "item", "args": [{"ref": "capId"}]}, "right": {"identity": "itemName", "args": [{"ref": "name"}]}, "confidence": 1.0}}],
         "wires": [{"from": "items", "to": "linked"}],
         "rules": [{"name": "linked", "description": "names"}]}
        """, "links no identities")]
    [InlineData("windows in authored", """
        {"entities": [{"name": "Level", "properties": [{"name": "k", "type": "Text"}, {"name": "level", "type": "Decimal", "optional": true, "events": {"temporality": "gauge", "combine": "max", "period": "60s"}}]}],
         "identities": [{"name": "level", "type": "Level", "components": [{"name": "k", "type": "Text"}]}],
         "rows": [{"name": "LevelRow", "fields": [{"name": "k", "type": "Text"}, {"name": "time", "type": "Instant"}, {"name": "v", "type": "Decimal"}]}]}
        """, """
        {"inputs": [{"name": "levels", "rows": "t.base.LevelRow"}],
         "verbs": [{"id": "levelRows", "verb": "rows", "source": "levels"},
                   {"id": "minutes", "verb": "window", "window": {"time": "time", "period": "1m"}},
                   {"id": "highest", "verb": "summarize", "by": {"k": {"ref": "k"}, "windowStart": {"ref": "windowStart"}}, "aggregates": {"level": {"fn": "max", "args": [{"ref": "v"}]}}},
                   {"id": "levels", "verb": "nodes", "rule": "levels", "target": {"nodes": [{"var": "l", "label": "Level", "id": {"identity": "level", "args": [{"ref": "k"}]}, "props": {"k": {"ref": "k"}, "level": {"ref": "level"}}}]}}],
         "wires": [{"from": "levelRows", "to": "minutes"}, {"from": "minutes", "to": "highest"}, {"from": "highest", "to": "levels"}],
         "rules": [{"name": "levels", "description": "levels"}]}
        """, "writes no windows")]
    public void A_graph_other_than_the_master_that_ingests_is_refused_what_belongs_to_the_master(string what, string types, string written, string message)
    {
        var (graphs, sources) = what switch
        {
            "a source bound twice" => (Graphs + """, {"name": "again", "projection": "base.authored", "inputs": {"items": "items"}}""", ""),
            // these rows come from no adapter: the space is only loaded, never run
            "windows in authored" => (Graphs.Replace("""{"items": "items"}""", """{"items": "items", "levels": "levels"}"""), """, {"name": "levels", "adapter": "test", "rows": "base.LevelRow"}"""),
            _ => (Graphs, ""),
        };
        var e = Assert.Throws<InvalidOperationException>(() => SpaceHost.LoadDefinitions(Files(types, written: written, graphs: graphs, sources: sources), Repo.Projections));
        Assert.True(e.Message.Contains(message, StringComparison.Ordinal), $"{what}: {e.Message}");
    }

    /// <summary>An edge that `authored` makes from a node it does not make: its end is not deferred when another ingestion projection of the module and output makes
    /// that type, as `ingest` makes `Seen`; it is when nothing makes it, or only a view does.</summary>
    [Theory]
    [InlineData("a sibling ingestion projection creates the type", "Seen", "seen", false, false)]
    [InlineData("no projection creates the type", "Loose", "loose", false, true)]
    [InlineData("only a view creates the type", "Loose", "loose", true, true)]
    public void An_edge_end_that_a_sibling_ingestion_projection_creates_is_not_deferred(string what, string endType, string identity, bool view, bool deferred)
    {
        var types = $$"""
            {"entities": [{"name": "Loose", "properties": [{"name": "name", "type": "Text"}]}],
             "identities": [{"name": "loose", "type": "Loose", "components": [{"name": "name", "type": "Text"}]}],
             "relationships": [{"name": "Marks", "from": "{{endType}}", "to": "Item"}]}
            """;
        var marks = $$$"""
            {"verbs": [{"id": "marks", "verb": "edges", "rule": "marks", "target": {
               "nodes": [{"var": "m", "label": "{{{endType}}}", "id": {"identity": "{{{identity}}}", "args": [{"ref": "owner"}]}},
                         {"var": "i", "label": "Item", "id": {"identity": "item", "args": [{"ref": "capId"}]}}],
               "edges": [{"var": "e", "label": "Marks", "from": "m", "to": "i"}]}}],
             "wires": [{"from": "items", "to": "marks"}],
             "rules": [{"name": "marks", "description": "marks"}]}
            """;
        SourceFile[] files = [.. Files(types, written: marks), .. view ? new[] { new SourceFile("t.base.looseView.gkql.json", """
            {"graph": "looseView", "ontology": "t.base", "inputs": [{"name": "g", "ontology": "t.base"}], "parameters": [],
             "verbs": [
               {"id": "items", "verb": "graph-match", "graph": "g", "pattern": {"nodes": [{"var": "i", "label": "Item"}]}},
               {"id": "made", "verb": "nodes", "rule": "made", "target": {"nodes": [{"var": "l", "label": "Loose", "id": {"identity": "loose", "args": [{"ref": "i.k"}]}, "props": {"name": {"ref": "i.k"}}}]}}],
             "wires": [{"from": "items", "to": "made"}],
             "rules": [{"name": "made", "description": "loose"}]}
            """) } : []];
        var res = new Catalog(Repo.Projections).Load(files, updatePins: true);
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
        Assert.Equal(deferred, res.Diagnostics.Any(d => d.Code == "EndpointDeferred"));
        _ = what;
    }

    /// <summary>An authored note is minted under two keys, and a link the master observes later binds them: the two ids merge, and `authored`, whose rows are not evaluated again, follows the merge.</summary>
    [Fact]
    public void A_graph_that_ingests_follows_a_merge()
    {
        var files = Files(
            """
            {"entities": [{"name": "Note", "properties": [{"name": "n", "type": "Text", "optional": true}]}],
             "identities": [{"name": "noteKey", "type": "Note", "components": [{"name": "k", "type": "Text"}]},
                            {"name": "noteName", "type": "Note", "components": [{"name": "name", "type": "Text"}]}],
             "equivalences": [{"name": "noteKeyIsName", "left": "noteKey", "right": "noteName", "mode": "assert", "threshold": 1.0}]}
            """,
            """
            {"verbs": [{"id": "linked", "verb": "link", "rule": "observed", "link": {"policy": "noteKeyIsName", "left": {"identity": "noteKey", "args": [{"ref": "capId"}]}, "right": {"identity": "noteName", "args": [{"ref": "service"}]}, "confidence": 1.0}}],
             "wires": [{"from": "obs", "to": "linked"}]}
            """,
            """
            {"verbs": [{"id": "byKey", "verb": "nodes", "rule": "notes", "target": {"nodes": [{"var": "n", "label": "Note", "id": {"identity": "noteKey", "args": [{"ref": "capId"}]}}]}},
                       {"id": "byName", "verb": "nodes", "rule": "notes", "target": {"nodes": [{"var": "n", "label": "Note", "id": {"identity": "noteName", "args": [{"ref": "name"}]}}]}}],
             "wires": [{"from": "items", "to": "byKey"}, {"from": "items", "to": "byName"}],
             "rules": [{"name": "notes", "description": "a note per item, by key and by name"}]}
            """);
        using var h = new SpaceHost(files, new HostOptions { Projections = Repo.Projections});
        int Notes() => h.Entities("authored", "Note").Count;
        var rw = new RowWriter(h.RowLayout("items"), h.Interner);
        rw.Begin(); rw.Text("capId", "k1"); rw.Text("name", "a-k1"); rw.Text("owner", "d"); rw.Text("status", "live");
        h.Ingest("items", rw.Build()!, 1);
        Assert.Equal(2, Notes());                                       // one note under two keys, not yet known to be one
        var ow = new RowWriter(h.RowLayout("obs"), h.Interner);
        ow.Begin(); ow.Text("capId", "k1"); ow.Text("site", "c"); ow.Text("namespace", "n"); ow.Text("service", "a-k1");
        h.Ingest("obs", ow.Build()!, 2);
        Assert.Equal(1, Notes());                                       // the link merged the two ids, and the graph that ingested them followed
    }
}
