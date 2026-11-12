using System.Collections.Immutable;
using Sattle.Core;
using Sattle.Reference;
using static Sattle.Differential.Logs;

namespace Sattle.Differential;

/// <summary>Def. 7.14 to 7.16 on the smallest logs that show them, in the world `fill and no data`: hits filled with 0, a look-back of five minutes, a time to live of three, "on" at one window of 3 or more, "off" after two windows of 1 or less, "silent" for no data. The engine and the reference agree on each, and each gives the state the definitions say.</summary>
public class SilenceTests
{
    static RowInput Item(string k) => new("a", Logs.Row(("k", Value.Text(k)), ("g", Value.Text("g0")), ("n", Value.Integer(1)), ("d", Value.Null), ("t", Value.Instant(Origin))), 1);
    static RowInput Hits(string k, long n, long seconds) => new("b", Logs.Row(("k", Value.Text(k)), ("g", Value.Text("g0")), ("n", Value.Integer(n)), ("d", Value.Null), ("t", Value.Instant(Origin + seconds * Second))), 1);
    static AdvanceInput Advance(long seconds) => new(Origin + seconds * Second);
    static ProgressInput Reported(long seconds) => new("b", Origin + seconds * Second);

    static readonly Input[] On = [Item("k0"), Hits("k0", 5, 10), Advance(60)];       // one window of 5: on. The item has existed since the advance to 60

    public static TheoryData<string, Input[], string> Cases => new()
    {
        { "one window at or above the threshold", On, "on" },
        { "the source reported for three more minutes and the item had no hits: the windows are filled with 0 and two of them end the state", [.. On, Reported(240), Advance(240)], "off" },
        { "the source did not report: nothing is filled, and at exactly the time to live the state still stands", [.. On, Advance(240)], "on" },
        { "the source did not report for longer than the time to live: no data, and not the state a fill would give", [.. On, Advance(300)], "silent" },
        { "the report arrives after the advance: the windows are covered from then on", [.. On, Advance(300), Reported(300)], "off" },
        { "data return after more than the look-back: a new episode, which starts in the initial state", [.. On, Advance(300), Hits("k0", 2, 310), Advance(360)], "off" },
        { "data return and reach the threshold", [.. On, Advance(300), Hits("k0", 5, 310), Advance(360)], "on" },
        { "an item that appears is not in no data before its time to live has passed", [Advance(60), Item("k1"), Advance(120), Advance(300)], "" },
        { "an item that has existed for longer than the time to live without a record is in no data", [Advance(60), Item("k1"), Advance(120), Advance(360)], "silent" },
    };

    [Theory, MemberData(nameof(Cases))]
    public void A_series_with_a_fill_and_a_time_to_live_gives_the_state_the_definitions_say(string what, Input[] log, string state)
    {
        var w = Worlds.Fill;
        var reference = ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log).Graphs["view"].Lines();
        var expected = state == "" ? [] : new[] { $"state=t:{state}" };
        Assert.True(reference.Length == expected.Length && expected.All(e => reference.Any(l => l.Contains(e, StringComparison.Ordinal))), $"{what}: the reference gives\n{string.Join("\n", reference)}\nfor\n{Print(log)}");
        Assert.Null(Differential.FirstMismatch(w, [.. log], 0, reordered: false)?.ToString());
    }
}
