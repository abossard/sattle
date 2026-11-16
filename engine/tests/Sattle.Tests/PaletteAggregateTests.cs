using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>`/palette` says per aggregate where it may be used (`Group` over data, `Rollup` over telemetry).</summary>
public class PaletteAggregateTests
{
    static readonly string[] All = ["count", "countDistinct", "sum", "min", "max", "mean", "last"];

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
