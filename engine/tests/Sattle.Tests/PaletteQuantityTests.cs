using System.Text.Json.Nodes;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Every numeric parameter the palette offers says what it measures, so an editor can show `24h` for a duration and not a number of nanoseconds.</summary>
public class PaletteQuantityTests
{
    static IEnumerable<JsonObject> Params(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o:
                if (o.ContainsKey("name") && o.ContainsKey("kind") && o.ContainsKey("required")) yield return o;
                foreach (var kv in o) foreach (var p in Params(kv.Value)) yield return p;
                break;
            case JsonArray a:
                foreach (var x in a) foreach (var p in Params(x)) yield return p;
                break;
        }
    }

    [Fact]
    public void Every_Integer_and_Decimal_parameter_of_the_palette_has_a_quantity_and_a_unit()
    {
        var numeric = Params(Palette.Build()).Where(p => p["kind"]!.GetValue<string>() is "Integer" or "Decimal").ToList();
        Assert.True(numeric.Count >= 12);
        Assert.True(numeric.All(p => p["quantity"] is not null && p["unit"] is not null), string.Join(", ", numeric.Where(p => p["quantity"] is null).Select(p => p["name"]!.GetValue<string>())));
    }

    [Theory]
    [InlineData("width")] [InlineData("within")] [InlineData("age")]
    public void A_duration_parameter_is_nanoseconds_on_the_wire_and_says_how_it_is_written(string name)
    {
        var p = Params(Palette.Build()).First(x => x["name"]!.GetValue<string>() == name);
        Assert.Equal("duration", p["quantity"]!.GetValue<string>());
        Assert.Equal("ns", p["unit"]!.GetValue<string>());
        Assert.Equal("<integer><ms|s|m|h>", p["literal"]!.GetValue<string>());
        Assert.Equal("24h", p["example"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("enterAfter", "windows")] [InlineData("exitAfter", "windows")] [InlineData("maxIterations", "iterations")] [InlineData("minDepth", "hops")] [InlineData("maxDepth", "hops")]
    public void A_count_parameter_names_what_it_counts(string name, string unit)
    {
        var p = Params(Palette.Build()).First(x => x["name"]!.GetValue<string>() == name);
        Assert.Equal("count", p["quantity"]!.GetValue<string>());
        Assert.Equal(unit, p["unit"]!.GetValue<string>());
    }
}
