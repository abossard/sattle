using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>A Rollup aggregates telemetry, and what it may do depends on the temporality of the value: a delta (counts per window) sums and takes extremes; a gauge (a level) does too; a cumulative (a counter that only grows) does neither until `Delta` makes it a delta. The palette declares this per aggregate (`requires`); the checker must agree with the declaration for every temporality. KQL combines telemetry windows with sum, min and max only.</summary>
public class RollupTemporalityTests
{
    const string Delta = "telemetry delta sum every 60s", Gauge = "telemetry gauge max every 60s", Cumulative = "telemetry cumulative last every 60s";

    /// <summary>`Item.hits` declared with the temporality under test, rolled up per item by `fn` in the view `t.view`; the checker's refusals of that aggregate.</summary>
    static IReadOnlyList<Diagnostic> Diagnostics(string temporality, string fn)
    {
        SourceFile[] files =
        [
            new("t.sattle", $$"""
                module t @ 1.0.0;
                entity Item { k: Text; hits?: Integer {{temporality}}; }
                entity Load { k: Text; x?: Integer telemetry delta sum every 60s; }
                identity item for Item version "1" { k: Text by exact; }
                identity load for Load version "1" { k: Text by exact; }
                rows R { k: Text; }
                """),
            new("t.ingest.kql", "graph ingest : t;\ninput a : R;\nnodes Item on i with (rule = \"observed\", description = \"test\") = a | project i = identity(\"item\", k), k;\n"),
            new("t.view.kql", "graph view : t;\ninput master : t;\n"
                + "let items = graph(\"master\") | graph-match (i) where labels(i) has \"Item\" project i = i.id, k = i.k;\n"
                + "nodes Load on l = items | project l = identity(\"load\", k), k;\n"
                + "extend nodes Load on l = items | join kind=inner (telemetry(\"master\", 3m) | where property == \"hits\" | project i = id, windowStart, property, value) on i\n"
                + $"    | summarize x = {fn}(value) by k, windowStart | project l = identity(\"load\", k), windowStart, x;\n"),
            new("s.sattle", """
                module s @ 1.0.0;
                import t @ 1.0.0 sha256:… as t;
                space s {
                  source a adapter "record" rows t.R;
                  master graph master = t.ingest(a: a);
                  graph view = t.view(master: master);
                }
                """),
        ];
        return [.. new Catalog(Repo.Projections).Load(files, updatePins: true).Diagnostics.Where(d => d.IsError && d.Code == "Aggregate")];
    }

    public static IEnumerable<object[]> Matrix() =>
        from t in new[] { ("delta", Delta), ("gauge", Gauge), ("cumulative", Cumulative) }
        from fn in new[] { "sum", "min", "max" }
        select new object[] { t.Item1, t.Item2, fn };

    /// <summary>What the palette says is needed for the aggregate to be accepted over a value of this temporality.</summary>
    static bool Allowed(string temporality, string fn)
    {
        var ctx = Support.AggregateContexts[fn].Single(c => c.Operator == "Rollup");
        return temporality switch
        {
            "cumulative" => false,                                       // every Rollup aggregate says "not cumulative" or "gauge"
            "gauge" => true,                                             // `requires` is "not cumulative" or "gauge": both hold
            _ => ctx.Requires != "gauge",
        };
    }

    [Theory, MemberData(nameof(Matrix))]
    public void The_checker_accepts_exactly_what_the_palette_declares_for_each_temporality(string temporality, string declaration, string fn)
    {
        var diags = Diagnostics(declaration, fn);
        Assert.Equal(!Allowed(temporality, fn), diags.Count > 0);
    }

    [Theory]
    [InlineData("sum")] [InlineData("min")] [InlineData("max")]
    public void Over_a_cumulative_value_the_refusal_says_to_apply_Delta_first(string fn)
    {
        var d = Assert.Single(Diagnostics(Cumulative, fn));
        Assert.Contains("Delta", d.Message);
        Assert.Equal("t.view.kql", d.File);
        Assert.True(d.Line > 0);
    }
}
