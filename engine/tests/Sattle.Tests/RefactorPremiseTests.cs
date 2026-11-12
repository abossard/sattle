using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>T-REFACTOR (Proposition 5.7, O5.5): on generated projections every extraction and its inline keep the union of the outputs (elements, ids, telemetry) and say so; one case per broken premise (two sinks of one key, two sinks that disagree, one relationship tuple under two key rules, a reader of the parent graph, a rule moved across output ontologies) is answered "not preserved" with the premise named, and for R3 and R4 the union does change.</summary>
[Trait("Category", "Slow")]
public class RefactorPremiseTests
{
    const string Base = """
        module t.base @ 1.0.0;
        entity Item { k: Text; name: Text; owner: Text; }
        identity item for Item version "1" { k: Text by exact; }
        /// the row shape of the authoring adapter
        rows CapabilityRow { capId: Text; name: Text; owner: Text; status: Text; parentId?: Text; }
        projection ingest ingestion output t.base {
          in items: rows CapabilityRow;
          rule observed readonly "test" {
            emit entity Item from items identity item(k: capId) set { k: capId, name: name, owner: owner };
          }
        }
        """;

    static SourceFile[] Files(string rules, string types, string extraModules = "", string extraGraphs = "") =>
    [
        new("t.base.sattle", Base),
        new("t.view.sattle", $$"""
            module t.view @ 1.0.0;
            import t.base @ 1.0.0 sha256:… as base;
            {{types}}
            projection v view output t.view {
              in master: graph base;
            {{rules}}
            }
            {{extraModules}}
            """),
        new("t.space.sattle", $$"""
            module t.space @ 1.0.0;
            import t.base @ 1.0.0 sha256:… as base;
            import t.view @ 1.0.0 sha256:… as view;
            space s {
              source items adapter "sattle.authoring" rows base.CapabilityRow;
              master graph master = base.ingest(items: items);
              graph view = view.v(master: master);
            {{extraGraphs}}
            }
            """),
    ];

    static string Rule(int i, string type, string set, string? owner = null, string identity = "x") => $$"""
          rule r{{i}} readonly "test" {
            let m{{i}} = match master (x: base.Item){{(owner is null ? "" : $" |> where x.owner == \"{owner}\"")}};
            emit entity {{type}} from m{{i}} identity {{identity}} set { {{set}} };
          }
        """;

    static SpaceHost Host(SourceFile[] files)
    {
        var h = new SpaceHost(files, new HostOptions { Cluster = "synth" }, SpaceHost.LoadDefinitions(files));
        var rw = new RowWriter(h.RowLayout("items"), h.Interner);
        for (var i = 0; i < 6; i++) { rw.Begin(); rw.Text("capId", "k" + i); rw.Text("name", "n" + i); rw.Text("owner", "o" + i % 3); rw.Text("status", "active"); }
        h.Ingest("items", rw.Build()!, 1);
        return h;
    }

    static string[] Union(SpaceHost h)
    {
        var lines = new List<string>();
        foreach (var g in h.Engine.Graphs)
        {
            foreach (var e in g.State.Entities) lines.Add($"e|{e.Id}|{e.Type}|{string.Join(",", e.Props.Items.Select(kv => kv.Key + "=" + kv.Value))}");
            foreach (var r in g.State.Rels) lines.Add($"r|{r.Id}|{r.Type}|{r.Source}|{r.Target}|{string.Join(",", r.Props.Items.Select(kv => kv.Key + "=" + kv.Value))}");
        }
        lines.Sort(StringComparer.Ordinal);
        return [.. lines];
    }

    static JsonObject Body(string kind, params (string, object)[] kv)
    {
        var o = new JsonObject { ["kind"] = kind };
        foreach (var (k, v) in kv) o[k] = v is string[] a ? new JsonArray([.. a.Select(x => (JsonNode)x)]) : JsonValue.Create((string)v);
        return o;
    }

    /// <summary>The refactoring's answer, applied and committed.</summary>
    static JsonNode Apply(SpaceHost h, JsonObject body)
    {
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var r = h.Refactor(d, body);
        Assert.True(h.ApplyDraftPatch(d, r["patch"]!, 0)["ok"]!.GetValue<bool>());
        h.CommitDraft(d, h.CatalogRevision);
        return r["preserves"]!;
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Extract_inline_and_move_preserve_the_output_union_on_generated_projections(int seed)
    {
        var rnd = new Random(seed);
        var n = rnd.Next(3, 6);
        var types = string.Join("\n", Enumerable.Range(0, n).Select(i => $"entity T{i} {{ name: Text; owner?: Text; }}\nidentity t{i} for T{i} version \"1\" {{ k: Text by exact; }}"));
        var rules = string.Join("\n", Enumerable.Range(0, n).Select(i => Rule(i, $"T{i}", rnd.Next(2) == 0 ? "name: x.name" : "name: x.name, owner: x.owner", rnd.Next(2) == 0 ? null : "o" + rnd.Next(3), $"t{i}(k: x.k)")));
        using var h = Host(Files(rules, types));
        var before = Union(h); var text = h.Definition("t.view");
        var select = Enumerable.Range(0, n).Where(_ => rnd.Next(2) == 0).Select(i => "r" + i).DefaultIfEmpty("r0").Take(n - 1).ToArray();
        var extracted = Apply(h, Body("extract", ("module", "t.view"), ("projection", "v"), ("select", select), ("name", "part")));
        Assert.Equal("union", extracted["outputs"]!.GetValue<string>());
        Assert.Equal(before, Union(h));
        var inlined = Apply(h, Body("inline", ("module", "t.view"), ("projection", "part"), ("into", "v")));
        Assert.Equal("union", inlined["outputs"]!.GetValue<string>());
        Assert.Equal(before, Union(h));
        Assert.Equal(text, h.Definition("t.view"));
    }

    [Theory]
    [InlineData("two sinks of one key", "R3", true)]
    [InlineData("two sinks that disagree", "R3", true)]
    [InlineData("one relationship tuple under two key rules", "R3", true)]
    [InlineData("a reader of the parent graph", "R4", true)]
    public void A_refactoring_that_breaks_a_premise_does_not_claim_the_union(string broken, string premise, bool unionChanges)
    {
        var (types, rules, extraModules, extraGraphs) = broken switch
        {
            "two sinks of one key" => ("entity T0 { name: Text; }", Rule(0, "T0", "name: x.name") + Rule(1, "T0", "name: x.name"), "", ""),
            "two sinks that disagree" => ("entity T0 { name: Text; }", Rule(0, "T0", "name: x.name") + Rule(1, "T0", "name: x.owner"), "", ""),
            "one relationship tuple under two key rules" => ("entity T0 { name: Text; }\nrelationship L (T0) -> (T0);", Rule(0, "T0", "name: x.name") + """
                  rule r1 readonly "test" {
                    let m1 = match master (x: base.Item);
                    emit relationship L from m1 key one: x -> x;
                  }
                  rule r2 readonly "test" {
                    let m2 = match master (x: base.Item);
                    emit relationship L from m2 key two: x -> x;
                  }
                """, "", ""),
            _ => ("entity T0 { name: Text; }\nentity T1 { name: Text; }\nentity Seen { name: Text; }", Rule(0, "T0", "name: x.name") + Rule(1, "T1", "name: x.name"), """
                projection reader view forward output t.view {
                  in level: graph t.view;
                  let s = match level (y: T1);
                  emit entity Seen from s identity y set { name: y.name };
                }
                """, "  graph readerGraph = view.reader(level: view);"),
        };
        using var h = Host(Files(rules, types, extraModules, extraGraphs));
        var before = Union(h);
        var select = broken == "one relationship tuple under two key rules" ? new[] { "r2" } : ["r1"];
        var preserves = Apply(h, Body("extract", ("module", "t.view"), ("projection", "v"), ("select", select), ("name", "part")));
        Assert.Equal("not preserved", preserves["outputs"]!.GetValue<string>());
        Assert.Contains(preserves["premises"]!.AsArray(), p => p!["premise"]!.GetValue<string>() == premise);
        Assert.Equal(unionChanges, !before.SequenceEqual(Union(h)));
    }

    /// <summary>R5: a rule moved into a projection with another output ontology is refused before anything is computed; nothing changes.</summary>
    [Fact]
    public void A_rule_moved_across_output_ontologies_is_refused_and_changes_nothing()
    {
        var into = """
            projection w view output base {
              in master: graph base;
              rule s0 readonly "test" {
                let q = match master (x: base.Item);
                emit entity base.Item from q identity x set { k: x.k, name: x.name, owner: x.owner };
              }
            }
            """;
        using var h = Host(Files(Rule(0, "T0", "name: x.name") + Rule(1, "T1", "name: x.name"), "entity T0 { name: Text; }\nentity T1 { name: Text; }", into, "  graph baseView = view.w(master: master);"));
        var before = Union(h); var revision = h.CatalogRevision;
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var e = Assert.Throws<HostError>(() => h.Refactor(d, Body("moveRule", ("module", "t.view"), ("from", "v"), ("to", "w"), ("rule", "r1"))));
        Assert.Equal("CrossModule", e.Code);                                  // the move would change what the rule's output ontology is
        Assert.Equal(before, Union(h)); Assert.Equal(revision, h.CatalogRevision);
    }
}
