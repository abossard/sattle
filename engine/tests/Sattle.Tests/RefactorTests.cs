using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>PROTOCOL.md v3 refactoring: the union of the output graphs is preserved, or the refactoring is rejected with a typed reason.</summary>
[Trait("Category", "Slow")]
public class RefactorTests
{
    /// <summary>Every element of every graph with its state, and every telemetry window: the multiset that a refactoring must keep.</summary>
    static string[] Union(SpaceHost h)
    {
        var lines = new List<string>();
        foreach (var g in h.Engine.Graphs)
        {
            foreach (var e in g.State.Entities) lines.Add($"e|{e.Id}|{e.Type}|{Props(e.Props)}");
            foreach (var r in g.State.Rels) lines.Add($"r|{r.Id}|{r.Type}|{r.Source}|{r.Target}|{Props(r.Props)}");
            if (h.Engine.Telemetry is InMemoryTelemetryStore mem)
                foreach (var (e, p, ws, v) in mem.Dump(g.Key)) lines.Add($"w|{h.Interner.Get(e)}|{h.Interner.Get(p)}|{ws}|{Norm.Decimal(v)}");
        }
        lines.Sort(StringComparer.Ordinal);
        return [.. lines];
    }
    static string Props(Core.Props p) => string.Join(",", p.Items.Select(kv => kv.Key + "=" + kv.Value));

    static JsonObject Body(string kind, params (string, object)[] kv)
    {
        var o = new JsonObject { ["kind"] = kind };
        foreach (var (k, v) in kv) o[k] = v is string[] a ? new JsonArray([.. a.Select(x => (JsonNode)x)]) : JsonValue.Create((string)v);
        return o;
    }

    /// <summary>Applies the refactoring to a draft, commits it and returns the committed text of the module.</summary>
    static void Apply(SpaceHost h, JsonObject body)
    {
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var r = h.Refactor(d, body);
        var res = h.ApplyDraftPatch(d, r["patch"]!, 0);
        Assert.True(res["ok"]!.GetValue<bool>(), res["diagnostics"]!.ToJsonString());
        foreach (var (m, t) in r["modules"]!.AsObject()) Assert.Equal(t!["text"]!.GetValue<string>(), h.DraftText(d, m));
        h.CommitDraft(d, h.CatalogRevision);
    }

    public static TheoryData<string, string[], string> Extractions => new()
    {
        { "acme.biz", ["tree"], "capabilityTree" },
        { "acme.biz", ["capabilities", "counts"], "capabilityCore" },
    };

    [Theory]
    [MemberData(nameof(Extractions))]
    public void Extract_keeps_the_union_of_output_graphs_and_inline_restores_the_text(string module, string[] select, string name)
    {
        using var h = HostTests.NewHost();
        HostTests.Drive(h);
        var projection = module == "acme.biz" ? "capabilityView" : "ingest";
        var before = Union(h); var text = h.Definition(module);
        var graphsBefore = h.Engine.Graphs.Count;
        Apply(h, Body("extract", ("module", module), ("projection", projection), ("select", select), ("name", name)));
        Assert.Equal(graphsBefore + 1, h.Engine.Graphs.Count);
        Assert.Equal(before, Union(h));
        Assert.NotEqual(text, h.Definition(module));
        Apply(h, Body("inline", ("module", module), ("projection", name), ("into", projection)));
        Assert.Equal(graphsBefore, h.Engine.Graphs.Count);
        Assert.Equal(before, Union(h));
        Assert.Equal(text, h.Definition(module));                       // the round trip is textually exact
    }

    [Fact]
    public void A_rule_that_holds_a_lift_statement_moves_between_stacked_projections_with_its_statement_and_without_changing_any_output()
    {
        using var h = HostTests.NewHost();
        HostTests.Drive(h);
        var before = Union(h);
        Apply(h, Body("extract", ("module", "acme.biz"), ("projection", "capabilityView"), ("select", new[] { "capabilities", "counts" }), ("name", "capabilityCore")));
        Apply(h, Body("moveRule", ("module", "acme.biz"), ("from", "capabilityView"), ("to", "capabilityCore"), ("rule", "lifted")));
        Assert.Equal(before, Union(h));
        var text = h.Definition("acme.biz");
        Assert.Contains("lift dependsOn as DependsOn from tree svc.DependsOn via master ops.Realizes", text);
        Assert.True(text.IndexOf("projection capabilityCore", StringComparison.Ordinal) is var core && core > 0 && text.IndexOf("rule lifted", StringComparison.Ordinal) > core, "the rule with its lift statement is now in capabilityCore");
    }

    [Fact]
    public void A_rule_moves_between_stacked_projections_without_changing_any_output()
    {
        using var h = HostTests.NewHost();
        HostTests.Drive(h);
        var before = Union(h);
        Apply(h, Body("extract", ("module", "acme.biz"), ("projection", "capabilityView"), ("select", new[] { "capabilities", "counts" }), ("name", "capabilityCore")));
        Assert.Equal(before, Union(h));
        Apply(h, Body("moveRule", ("module", "acme.biz"), ("from", "capabilityView"), ("to", "capabilityCore"), ("rule", "tree")));
        Assert.Equal(before, Union(h));
        Assert.Contains("projection capabilityView view output acme.biz", h.Definition("acme.biz"));          // only the readonly rule `lifted` is left in it
    }

    public static TheoryData<string, string[], string> Rejections => new()
    {
        { "UpdateWithoutCreate", ["backed", "emit.2"], "serviceDeps" },
        { "SharedOperator", ["services", "emit.1"], "serviceDeps" },
        { "DependsOnUnmoved", ["serviceOut", "emit.6"], "serviceDeps" },
        { "SharedOperator", ["dependsOn"], "serviceDeps" },                 // the lift statement alone: the roll-ups that read its relation stay
        { "UnknownSelection", ["nope"], "serviceDeps" },
        { "IngestionProjection", ["observedNodes", "observedMetrics"], "ingest" },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public void Unsafe_extractions_are_rejected_with_a_typed_reason_and_change_nothing(string code, string[] select, string projection)
    {
        using var h = HostTests.NewHost();
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var e = Assert.Throws<HostError>(() => h.Refactor(d, Body("extract", ("module", projection == "ingest" ? "acme.ops" : "acme.svc"), ("projection", projection), ("select", select), ("name", "part"))));
        Assert.Equal(code, e.Code);
        Assert.Empty(h.DraftPatches(d, 0));
    }

    [Fact]
    public void Selecting_a_statement_inside_a_rule_is_a_split_rule_and_an_unknown_projection_is_named()
    {
        using var h = HostTests.NewHost();
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        Assert.Equal("SplitRule", Assert.Throws<HostError>(() => h.Refactor(d, Body("extract", ("module", "acme.biz"), ("projection", "capabilityView"), ("select", new[] { "tree.1" }), ("name", "x")))).Code);
        Assert.Equal("NotAProjection", Assert.Throws<HostError>(() => h.Refactor(d, Body("extract", ("module", "acme.biz"), ("projection", "nope"), ("select", new[] { "tree" }), ("name", "x")))).Code);
    }

    [Fact]
    public void Extract_copies_the_parameters_the_moved_statements_use_and_binds_them_in_the_space()
    {
        using var h = HostTests.NewHost();
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var r = h.Refactor(d, Body("extract", ("module", "acme.healthmodel"), ("projection", "signals"), ("select", new[] { "errors", "errorsDegraded", "errorsUnhealthy", "emit.15", "emit.16", "emit.17" }), ("name", "errorSignals")));
        var text = r["modules"]!["acme.healthmodel"]!["text"]!.GetValue<string>();
        var part = text[text.IndexOf("projection errorSignals", StringComparison.Ordinal)..];
        part = part[..part.IndexOf("\n}\n", StringComparison.Ordinal)];
        Assert.Contains("in hubResourceGroup: param Text;", part);
        Assert.DoesNotContain("in aksName", part);                         // not used by the moved statements
        var space = r["modules"]!["acme.space"]!["text"]!.GetValue<string>();
        Assert.Matches("graph healthSignals_errorSignals = hm.errorSignals\\(.*hubResourceGroup: \"rg-hub\".*\\);", space);
        Assert.Contains("master: master", space[space.IndexOf("healthSignals_errorSignals", StringComparison.Ordinal)..]);
    }
}
