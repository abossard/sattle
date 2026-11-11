using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>A Rollup aggregates telemetry, and what it may do depends on the temporality of the value: a delta (counts per window) sums, takes extremes, but has no mean; a gauge (a level) takes all four; a cumulative (a counter that only grows) takes none until `Delta` makes it a delta. The palette declares this per aggregate (`requires`); the checker must agree with the declaration for every temporality.</summary>
public class RollupTemporalityTests
{
    const string Delta = "telemetry delta sum every 60s", Gauge = "telemetry gauge max every 60s", Cumulative = "telemetry cumulative last every 60s";

    static readonly string[] Fns = ["sum", "min", "max", "mean", "count"];

    /// <summary>`Calls.flows` declared with the temporality under test, rolled up in the service tree by every aggregate at once (one catalog load per temporality), each under the name `x_{fn}`; the diagnostics at that aggregate.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<IReadOnlyList<Diagnostic>>> Loaded = new();

    static IReadOnlyList<Diagnostic> Diagnostics(string temporality, string fn) =>
        Loaded.GetOrAdd(temporality, t => new Lazy<IReadOnlyList<Diagnostic>>(() =>
        {
            var calls = string.Join(", ", Fns.Select(f => $"x_{f}: " + (f == "count" ? "count()" : $"{f}(flows)")));
            var files = Catalog.ReadDirectory(Repo.Definitions).Select(f =>
                f.Path.EndsWith("sattle.infra.sattle", StringComparison.Ordinal) ? f with { Text = f.Text.Replace("flows?: Integer telemetry delta sum every 60s;", $"flows?: Integer {t};") } :
                f.Path.EndsWith("acme.svc.sattle", StringComparison.Ordinal) ? f with { Text = f.Text.Replace("  // telemetry: roll-up along the tree", $"  let depWindows = match master (a: infra.Pod)-[c:infra.Calls]->(b: infra.Pod) |> series master c.flows within 24h |> rollup by {{ a: a }} combine {{ {calls} }};\n  // telemetry: roll-up along the tree") } : f).ToList();
            Assert.Contains(t, files.First(f => f.Path.EndsWith("sattle.infra.sattle", StringComparison.Ordinal)).Text);
            return new Catalog().Load(files, updatePins: true).Diagnostics;
        })).Value.Where(d => d.IsError && d.Focus.EndsWith($"/op:depWindows/agg:x_{fn}", StringComparison.Ordinal)).ToList();

    public static IEnumerable<object[]> Matrix() =>
        from t in new[] { ("delta", Delta), ("gauge", Gauge), ("cumulative", Cumulative) }
        from fn in new[] { "sum", "min", "max", "mean" }
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
        Assert.Equal("Aggregate", d.Code);
        Assert.Contains("Delta", d.Message);
        Assert.True(d.Line > 0);
    }

    [Fact]
    public void A_mean_over_a_delta_is_refused_and_says_to_use_sum_but_over_a_gauge_it_is_accepted()
    {
        Assert.Contains("use sum", Assert.Single(Diagnostics(Delta, "mean")).Message);
        Assert.Empty(Diagnostics(Gauge, "mean"));
    }

    [Fact]
    public void Count_has_no_temporality_requirement()
    {
        foreach (var t in new[] { Delta, Gauge, Cumulative }) Assert.Empty(Diagnostics(t, "count"));
    }
}
