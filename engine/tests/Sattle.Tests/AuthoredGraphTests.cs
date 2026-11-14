using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>A space with two graphs that ingest: the master holds what was observed, `authored` what people wrote. Source rows reach the graph that binds them, a view edit lands in the graph that ingests the edited type, and an authored element is complete in its lineage.</summary>
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
        projection ingest ingestion output t.base {
          in obs: rows RealizesRow;
          rule observed readonly "what was observed" {
            emit entity Seen from obs identity seen(name: service) set { name: service };
          }
        }
        projection authored ingestion output t.base {
          in items: rows CapabilityRow;
          rule written bijective {
            emit entity Item from items identity item(k: capId) set { k: capId, a: name, d: owner, status: status };
            emit relationship Under from items key under: item(k: capId) -> item(k: parentId);
          }
        }
        """;

    static string View(string space) => $$"""
        module t.view @ 1.0.0;
        import t.base @ 1.0.0 sha256:… as base;
        entity V { a: Text; }
        projection v view output t.view {
          in authored: graph base;
          rule items lens {
            let shown = match authored (i: base.Item);
            emit entity V from shown identity i set { a: i.a };
            accept update(a);
          }
        }
        space s {
          source items adapter "sattle.authoring" rows base.CapabilityRow;
          source obs adapter "sattle.authoring" rows base.RealizesRow;
          {{space}}
        }
        """;

    const string Graphs = """
        master graph master = base.ingest(obs: obs);
          graph authored = base.authored(items: items);
          graph view = v(authored: authored);
        """;

    static SpaceHost Host()
    {
        SourceFile[] files = [new("t.base.sattle", Base), new("t.view.sattle", View(Graphs))];
        var h = new SpaceHost(files, new HostOptions { Cluster = "synth" });
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
        string[] Types(string g) => [.. h.Engine.Graph(g).State.Entities.Select(e => Ids.Name(e.Type)).Distinct().Order()];
        Assert.Equal(["Seen"], Types("master"));
        Assert.Equal(["Item"], Types("authored"));
        Assert.Equal(("master", "input", "projection"), (Role(h, "master"), Role(h, "authored"), Role(h, "view")));
    }

    [Fact]
    public void A_view_edit_lands_in_the_graph_that_ingests_the_edited_type()
    {
        using var h = Host();
        var masterHead = Head(h, "master");
        var e = h.Engine.Graph("view").State.Entities.OrderBy(x => x.Id, StringComparer.Ordinal).First();
        static JsonObject V(string id) => new() { ["value"] = id };
        JsonNode Ent(string a) => new JsonObject { ["id"] = V(e.Id), ["type"] = V("V"), ["properties"] = new JsonArray(new JsonObject { ["property"] = V("a"), ["value"] = new JsonObject { ["kind"] = "text", ["value"] = a } }) };
        var res = h.ApplyEdits("view", new JsonObject
        {
            ["baseRevisions"] = new JsonObject { ["view"] = Head(h, "view"), ["authored"] = Head(h, "authored") },
            ["edits"] = new JsonArray(new JsonObject { ["kind"] = "entity", ["id"] = e.Id, ["before"] = Ent(e.Props.Text("d:t.view/prop:a")!), ["after"] = Ent("changed") }),
        });
        Assert.True(res["accepted"]!.GetValue<bool>());
        Assert.NotNull(res["revisions"]!["authored"]);
        Assert.Equal("changed", h.Engine.Graph("authored").State.Entity(e.Id)!.Props.Text("d:t.base/prop:a"));
        Assert.Equal(masterHead, Head(h, "master"));
    }

    [Fact]
    public void An_authored_element_is_complete_in_its_lineage()
    {
        using var h = Host();
        var item = h.Engine.Graph("authored").State.Entities.First(x => Ids.Name(x.Type) == "Item");
        Assert.True(h.Lineage("authored", item.Id)["complete"]!.GetValue<bool>());
    }

    /// <summary>What a graph other than the master that ingests may not do: share a source, link identities, write telemetry.</summary>
    [Theory]
    [InlineData("a source bound twice", "", "", "bound by more than one graph")]
    [InlineData("a link in authored", "identity seen for Seen", "identity itemName for Item version \"1\" { name: Text by exact; }\nequivalence itemKeyIsName between item, itemName assert threshold 1.0;\nidentity seen for Seen", "links no identities")]
    [InlineData("telemetry in authored", "entity Seen { name: Text; }", "entity Seen { name: Text; }\nentity Level { k: Text; level?: Decimal telemetry gauge max every 60s; }\nidentity level for Level version \"1\" { k: Text by exact; }\nrows LevelRow { k: Text; time: Instant; v: Decimal; }", "writes no telemetry")]
    public void A_graph_other_than_the_master_that_ingests_is_refused_what_belongs_to_the_master(string what, string from, string to, string message)
    {
        var text = from.Length == 0 ? Base : Base.Replace(from, to);
        if (what == "a link in authored")
            text = text.Replace("    emit relationship Under from items key under: item(k: capId) -> item(k: parentId);\n  }",
                "    emit relationship Under from items key under: item(k: capId) -> item(k: parentId);\n  }\n  rule linked readonly \"names\" {\n    link itemKeyIsName from items left item(k: capId) right itemName(name: name) confidence 1.0;\n  }");
        if (what == "telemetry in authored")
            text = text.Replace("    emit relationship Under from items key under: item(k: capId) -> item(k: parentId);\n  }",
                "    emit relationship Under from items key under: item(k: capId) -> item(k: parentId);\n  }\n  rule levels readonly \"levels\" {\n    let w = levels |> window on time width 60s late revise;\n    emit entity Level from w identity level(k: k) set { k: k, level: v };\n  }").Replace("  in items: rows CapabilityRow;", "  in items: rows CapabilityRow;\n  in levels: rows LevelRow;");
        var graphs = what switch
        {
            "a source bound twice" => "master graph master = base.ingest(obs: obs);\n  graph authored = base.authored(items: items);\n  graph again = base.authored(items: items);\n  graph view = v(authored: authored);",
            // these rows come from no adapter: the space is only loaded, never run
            "telemetry in authored" => "source levels adapter \"test\" rows base.LevelRow;\n  master graph master = base.ingest(obs: obs);\n  graph authored = base.authored(items: items, levels: levels);\n  graph view = v(authored: authored);",
            _ => Graphs,
        };
        SourceFile[] files = [new("t.base.sattle", text), new("t.view.sattle", View(graphs))];
        var e = Assert.Throws<InvalidOperationException>(() => SpaceHost.LoadDefinitions(files));
        Assert.True(e.Message.Contains(message, StringComparison.Ordinal), $"{what}: {e.Message}");
    }

    /// <summary>An edge that `authored` emits from an entity it does not create: its end is not deferred when another ingestion projection of the module and output creates
    /// that type, as `ingest` creates `Seen`; it is when nothing creates it, or only a view does.</summary>
    [Theory]
    [InlineData("a sibling ingestion projection creates the type", "Seen", "seen", false, false)]
    [InlineData("no projection creates the type", "Loose", "loose", false, true)]
    [InlineData("only a view creates the type", "Loose", "loose", true, true)]
    public void An_edge_end_that_a_sibling_ingestion_projection_creates_is_not_deferred(string what, string endType, string identity, bool view, bool deferred)
    {
        var text = Base
            .Replace("identity seen for Seen version \"1\" { name: Text by exact; }", $"identity seen for Seen version \"1\" {{ name: Text by exact; }}\nentity Loose {{ name: Text; }}\nidentity loose for Loose version \"1\" {{ name: Text by exact; }}\nrelationship Marks ({endType}) -> (Item);")
            .Replace("    emit relationship Under from items key under: item(k: capId) -> item(k: parentId);\n  }",
                $"    emit relationship Under from items key under: item(k: capId) -> item(k: parentId);\n  }}\n  rule marks readonly \"marks\" {{\n    emit relationship Marks from items key marks: {identity}(name: owner) -> item(k: capId);\n  }}");
        if (view) text += "\nprojection looseView view output t.base {\n  in g: graph t.base;\n  rule made readonly \"loose\" {\n    let x = match g (i: Item);\n    emit entity Loose from x identity loose(name: i.k) set { name: i.k };\n  }\n}\n";
        Assert.Contains("rule marks readonly", text);
        var res = new Catalog().Load([new SourceFile("t.base.sattle", text), new SourceFile("t.view.sattle", View(Graphs))], updatePins: true);
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
        Assert.Equal(deferred, res.Diagnostics.Any(d => d.Code == "EndpointDeferred"));
        _ = what;
    }

    /// <summary>An authored note is minted under two keys, and a link the master observes later binds them: the two ids merge, and `authored`, whose rows are not evaluated again, follows the merge.</summary>
    [Fact]
    public void A_graph_that_ingests_follows_a_merge()
    {
        var linked = Base
            .Replace("identity seen for Seen", "entity Note { n?: Text; }\nidentity noteKey for Note version \"1\" { k: Text by exact; }\nidentity noteName for Note version \"1\" { name: Text by exact; }\nequivalence noteKeyIsName between noteKey, noteName assert threshold 1.0;\nidentity seen for Seen")
            .Replace("emit entity Seen from obs identity seen(name: service) set { name: service };",
                "emit entity Seen from obs identity seen(name: service) set { name: service };\n    link noteKeyIsName from obs left noteKey(k: capId) right noteName(name: service) confidence 1.0;")
            .Replace("    emit relationship Under from items key under: item(k: capId) -> item(k: parentId);\n  }",
                "    emit relationship Under from items key under: item(k: capId) -> item(k: parentId);\n  }\n  rule notes readonly \"a note per item, by key and by name\" {\n    emit entity Note from items identity noteKey(k: capId);\n    emit entity Note from items identity noteName(name: name);\n  }");
        Assert.Contains("link noteKeyIsName", linked);
        Assert.Contains("rule notes readonly", linked);
        SourceFile[] files = [new("t.base.sattle", linked), new("t.view.sattle", View(Graphs))];
        using var h = new SpaceHost(files, new HostOptions { Cluster = "synth" });
        int Notes() => h.Engine.Graph("authored").State.Entities.Count(e => Ids.Name(e.Type) == "Note");
        var rw = new RowWriter(h.RowLayout("items"), h.Interner);
        rw.Begin(); rw.Text("capId", "k1"); rw.Text("name", "a-k1"); rw.Text("owner", "d"); rw.Text("status", "live");
        h.Ingest("items", rw.Build()!, 1);
        Assert.Equal(2, Notes());                                       // one note under two keys, not yet known to be one
        var ow = new RowWriter(h.RowLayout("obs"), h.Interner);
        ow.Begin(); ow.Text("capId", "k1"); ow.Text("cluster", "c"); ow.Text("namespace", "n"); ow.Text("service", "a-k1");
        h.Ingest("obs", ow.Build()!, 2);
        Assert.Equal(1, h.Engine.Identities.Merges);
        Assert.Equal(1, Notes());
    }
}
