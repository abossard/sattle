using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>`/palette` says per aggregate where it may be used (`Group` over data, `Rollup` over telemetry). The declaration and the checker must agree: every listed context is accepted, every other one is a typed error.</summary>
public class PaletteAggregateTests
{
    static readonly string[] All = ["count", "countDistinct", "sum", "min", "max", "mean", "weightedMean", "any", "all", "last"];

    /// <summary>One call of each aggregate over the columns of the EventRow (count Integer, time Instant, name Text) and over the telemetry `flows`.</summary>
    static string Call(string fn, bool rollup) => (fn, rollup) switch
    {
        ("count", _) => "count()",
        ("countDistinct", false) => "countDistinct(name)",
        ("weightedMean", false) => "weightedMean(count, count)",
        ("any" or "all", false) => fn + "(count > 0)",
        ("last", false) => "last(name, time)",
        (_, false) => fn + "(count)",
        ("weightedMean", true) => "weightedMean(flows, flows)",
        ("last", true) => "last(flows, flows)",
        ("any" or "all", true) => fn + "(flows > 0)",
        (_, true) => fn + "(flows)",
    };

    /// <summary>Every aggregate in one mutated definition (one catalog load for all of them): the Group over EventRow columns and the Rollup over the `flows` telemetry, each aggregate under its own name `x_{fn}`.</summary>
    static IReadOnlyList<Diagnostic> LoadAll(string file, string from, string to)
    {
        var files = Catalog.ReadDirectory(Repo.Definitions).Select(f => f.Path.EndsWith(file, StringComparison.Ordinal) ? f with { Text = f.Text.Replace(from, to) } : f).ToList();
        Assert.Contains(to, files.First(f => f.Path.EndsWith(file, StringComparison.Ordinal)).Text);
        return new Catalog().Load(files, updatePins: true).Diagnostics;
    }

    static readonly Lazy<IReadOnlyList<Diagnostic>> GroupDiagnostics = new(() => LoadAll("acme.ops.sattle", "aggregate { warnings: sum(count) }", "aggregate { warnings: sum(count), " + string.Join(", ", All.Select(fn => $"x_{fn}: {Call(fn, false)}")) + " }"));
    static readonly Lazy<IReadOnlyList<Diagnostic>> RollupDiagnostics = new(() => LoadAll("acme.svc.sattle", "let serviceOut = dependsOn |> rollup by { s: src } combine { flowsOut: sum(flows) };", "let serviceOut = dependsOn |> rollup by { s: src } combine { flowsOut: sum(flows), " + string.Join(", ", All.Select(fn => $"x_{fn}: {Call(fn, true)}")) + " };"));

    static bool Refused(IEnumerable<Diagnostic> diags, string agg) => diags.Any(d => d.IsError && d.Focus.EndsWith("/agg:" + agg, StringComparison.Ordinal) && d.Code is "Aggregate" or "Unsupported");

    [Theory]
    [InlineData("count")] [InlineData("countDistinct")] [InlineData("sum")] [InlineData("min")] [InlineData("max")] [InlineData("mean")] [InlineData("weightedMean")] [InlineData("any")] [InlineData("all")] [InlineData("last")]
    public void A_group_over_data_accepts_exactly_the_aggregates_the_palette_lists_for_Group(string fn)
    {
        var listed = Support.AggregateContexts[fn].Any(c => c.Operator == "Group");
        Assert.Equal(!listed, Refused(GroupDiagnostics.Value, "x_" + fn));
    }

    [Theory]
    [InlineData("count")] [InlineData("countDistinct")] [InlineData("sum")] [InlineData("min")] [InlineData("max")] [InlineData("mean")] [InlineData("weightedMean")] [InlineData("any")] [InlineData("all")] [InlineData("last")]
    public void A_rollup_over_telemetry_accepts_exactly_the_aggregates_the_palette_lists_for_Rollup_given_a_delta_value(string fn)
    {
        var ctx = Support.AggregateContexts[fn].FirstOrDefault(c => c.Operator == "Rollup");
        // `flows` is a delta: a mean over a delta is the one listed context whose requirement (gauge) the value does not meet
        var expectRefused = ctx is null || ctx.Requires == "gauge";
        Assert.Equal(expectRefused, Refused(RollupDiagnostics.Value, "x_" + fn));
    }

    [Fact]
    public void The_palette_carries_the_contexts_of_every_aggregate_and_marks_unlisted_ones_unsupported()
    {
        var aggs = Palette.Build()["aggregates"]!.AsArray();
        Assert.All(All, fn =>
        {
            var a = aggs.First(x => x!["name"]!.GetValue<string>() == fn)!;
            Assert.True(a["supported"]!.GetValue<bool>());
            Assert.Equal(Support.AggregateContexts[fn].Select(c => c.Operator), a["contexts"]!.AsArray().Select(c => c!["operator"]!.GetValue<string>()));
        });
        Assert.Equal(["Group"], aggs.First(x => x!["name"]!.GetValue<string>() == "last")!["contexts"]!.AsArray().Select(c => c!["operator"]!.GetValue<string>()));
        Assert.Equal(["Group", "Rollup"], aggs.First(x => x!["name"]!.GetValue<string>() == "sum")!["contexts"]!.AsArray().Select(c => c!["operator"]!.GetValue<string>()));
    }
}
