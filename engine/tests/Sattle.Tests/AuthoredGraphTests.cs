using System.Text.Json.Nodes;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>PROTOCOL 3.29, a second input graph. A space with two graphs that ingest: the master holds what was observed, `authored` what people wrote. Source rows reach the graph that binds them, a view edit lands in the graph that ingests the edited type, and an authored element is complete in its lineage.</summary>
public class AuthoredGraphTests
{
    const string Base = """
        module t.base @ 1.0.0;
        entity Item { k: Text; a: Text; d: Text; status: Text; }
        relationship Under (Item) -> (Item);
        entity Seen { name: Text; }
        identity item for Item version "1" { k: Text by exact; }
        identity seen for Seen version "1" { name: Text by exact; }
        /// the row shapes of the authoring adapter
        rows CapabilityRow { capId: Text; name: Text; owner: Text; status: Text; parentId?: Text; }
        rows RealizesRow { capId: Text; cluster: Text; namespace: Text; service: Text; }
        """;

    const string Ingest = """
        graph ingest : t.base;
        input obs : RealizesRow;
        nodes Seen on s with (rule = "observed", description = "what was observed") = obs | project s = identity("seen", service), name = service;
        """;

    const string Authored = """
        graph authored : t.base;
        input items : CapabilityRow;
        nodes Item on i with (rule = "written", accept = "create, delete", update = "k, a, d, status") = items | project i = identity("item", capId), k = capId, a = name, d = owner, status;
        edges Under from i to p with (rule = "written") = items | project i = identity("item", capId), p = identity("item", parentId);
        """;

    const string View = """
        module t.view @ 1.0.0;
        import t.base @ 1.0.0 sha256:… as base;
        entity V { a: Text; }
        """;

    const string Lens = """
        graph v : t.view;
        input authored : t.base;
        let shown = graph("authored") | graph-match (i) where labels(i) has "Item" project i;
        nodes V on i with (rule = "items", update = "a") = shown | project i = i.id, a = i.a;
        """;

    static string Space(string graphs) => $$"""
        module t.space @ 1.0.0;
        import t.base @ 1.0.0 sha256:… as base;
        import t.view @ 1.0.0 sha256:… as view;
        space s {
          source items adapter "record" rows base.CapabilityRow;
          source obs adapter "record" rows base.RealizesRow;
          {{graphs}}
        }
        """;

    const string Graphs = """
        master graph master = base.ingest(obs: obs);
          graph authored = base.authored(items: items);
          graph view = view.v(authored: authored);
        """;

    /// <summary>The definitions: the base module with `types` added, its two ingestion projections with `observed` and `written` added, the lens view and the space with `graphs`.</summary>
    static SourceFile[] Files(string types = "", string observed = "", string written = "", string graphs = Graphs) =>
    [
        new("t.base.sattle", Base + "\n" + types), new("t.base.ingest.kql", Ingest + "\n" + observed), new("t.base.authored.kql", Authored + "\n" + written),
        new("t.view.sattle", View), new("t.view.v.kql", Lens), new("t.space.sattle", Space(graphs)),
    ];

    static SpaceHost Host()
    {
        var h = new SpaceHost(Files(), new HostOptions { Projections = Repo.Projections});
        var rw = new RowWriter(h.RowLayout("items"), h.Interner);
        foreach (var k in new[] { "k1", "k2" }) { rw.Begin(); rw.Text("capId", k); rw.Text("name", "a-" + k); rw.Text("owner", "d"); rw.Text("status", "live"); }
        h.Ingest("items", rw.Build()!, 1);
        var ow = new RowWriter(h.RowLayout("obs"), h.Interner);
        ow.Begin(); ow.Text("capId", "k1"); ow.Text("cluster", "c"); ow.Text("namespace", "n"); ow.Text("service", "web");
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

    /// <summary>What a graph other than the master that ingests may not do: share a source, link identities, write telemetry.</summary>
    [Theory]
    [InlineData("a source bound twice", "", "", "bound by more than one graph")]
    [InlineData("a link in authored", "identity itemName for Item version \"1\" { name: Text by exact; }\nequivalence itemKeyIsName between item, itemName assert threshold 1.0;\n",
        "link itemKeyIsName from i to n with (rule = \"linked\", confidence = 1.0, description = \"names\") = items | project i = identity(\"item\", capId), n = identity(\"itemName\", name);\n", "links no identities")]
    [InlineData("telemetry in authored", "entity Level { k: Text; level?: Decimal telemetry gauge max every 60s; }\nidentity level for Level version \"1\" { k: Text by exact; }\nrows LevelRow { k: Text; time: Instant; v: Decimal; }\n",
        "input levels : LevelRow;\nnodes Level on l with (rule = \"levels\", description = \"levels\") = levels | summarize level = max(v) by k, windowStart = bin(['time'], 60s) | project l = identity(\"level\", k), windowStart, k, level;\n", "writes no telemetry")]
    public void A_graph_other_than_the_master_that_ingests_is_refused_what_belongs_to_the_master(string what, string types, string written, string message)
    {
        var graphs = what switch
        {
            "a source bound twice" => "master graph master = base.ingest(obs: obs);\n  graph authored = base.authored(items: items);\n  graph again = base.authored(items: items);\n  graph view = view.v(authored: authored);",
            // these rows come from no adapter: the space is only loaded, never run
            "telemetry in authored" => "source levels adapter \"test\" rows base.LevelRow;\n  master graph master = base.ingest(obs: obs);\n  graph authored = base.authored(items: items, levels: levels);\n  graph view = view.v(authored: authored);",
            _ => Graphs,
        };
        var e = Assert.Throws<InvalidOperationException>(() => SpaceHost.LoadDefinitions(Files(types, written: written, graphs: graphs), Repo.Projections));
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
        var types = $"entity Loose {{ name: Text; }}\nidentity loose for Loose version \"1\" {{ name: Text by exact; }}\nrelationship Marks ({endType}) -> (Item);\n";
        var marks = $"edges Marks from m to i with (rule = \"marks\", description = \"marks\") = items | project m = identity(\"{identity}\", owner), i = identity(\"item\", capId);\n";
        SourceFile[] files = [.. Files(types, written: marks), .. view ? new[] { new SourceFile("t.base.looseView.kql", """
            graph looseView : t.base;
            input g : t.base;
            nodes Loose on l with (rule = "made", description = "loose") = graph("g") | graph-match (i) where labels(i) has "Item" project l = identity("loose", i.k), name = i.k;
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
            "entity Note { n?: Text; }\nidentity noteKey for Note version \"1\" { k: Text by exact; }\nidentity noteName for Note version \"1\" { name: Text by exact; }\nequivalence noteKeyIsName between noteKey, noteName assert threshold 1.0;\n",
            "link noteKeyIsName from k to n with (rule = \"observed\", confidence = 1.0) = obs | project k = identity(\"noteKey\", capId), n = identity(\"noteName\", service);\n",
            "nodes Note on n with (rule = \"notes\", description = \"a note per item, by key and by name\") = items | project n = identity(\"noteKey\", capId);\n"
            + "nodes Note on n with (rule = \"notes\") = items | project n = identity(\"noteName\", name);\n");
        using var h = new SpaceHost(files, new HostOptions { Projections = Repo.Projections});
        int Notes() => h.Entities("authored", "Note").Count;
        var rw = new RowWriter(h.RowLayout("items"), h.Interner);
        rw.Begin(); rw.Text("capId", "k1"); rw.Text("name", "a-k1"); rw.Text("owner", "d"); rw.Text("status", "live");
        h.Ingest("items", rw.Build()!, 1);
        Assert.Equal(2, Notes());                                       // one note under two keys, not yet known to be one
        var ow = new RowWriter(h.RowLayout("obs"), h.Interner);
        ow.Begin(); ow.Text("capId", "k1"); ow.Text("cluster", "c"); ow.Text("namespace", "n"); ow.Text("service", "a-k1");
        h.Ingest("obs", ow.Build()!, 2);
        Assert.Equal(1, Notes());                                       // the link merged the two ids, and the graph that ingested them followed
    }
}
