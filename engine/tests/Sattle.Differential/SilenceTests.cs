using System.Collections.Immutable;
using Sattle.Core;
using Sattle.Reference;
using static Sattle.Differential.Logs;

namespace Sattle.Differential;

/// <summary>Def. 7.14 to 7.16 on the smallest logs that show them, in the world `fill and no data`: hits filled with 0, a look-back of five minutes, a time to live of three, "on" at one window of 3 or more, "off" after two windows of 1 or less, "silent" for no data. The engine and the reference agree on each, and each gives the state the definitions say.</summary>
public class SilenceTests
{
    static RowInput Item(string k, long weight = 1) => new("a", Logs.Row(("k", Value.Text(k)), ("g", Value.Text("g0")), ("n", Value.Integer(1)), ("d", Value.Null), ("t", Value.Instant(Origin))), weight);
    static RowInput Hits(string k, long n, long seconds) => new("b", Logs.Row(("k", Value.Text(k)), ("g", Value.Text("g0")), ("n", Value.Integer(n)), ("d", Value.Null), ("t", Value.Instant(Origin + seconds * Second))), 1);
    static AdvanceInput Advance(long seconds) => new(Origin + seconds * Second);
    static ProgressInput Reported(long seconds) => new("b", Origin + seconds * Second);
    static LossInput Lost(long seconds) => new(Origin + seconds * Second);

    static readonly Input[] On = [Item("k0"), Hits("k0", 5, 10), Advance(60)];       // one window of 5: on. The item has existed since the advance to 60

    public static TheoryData<string, Input[], string> Cases => new()
    {
        { "one window at or above the threshold", On, "on" },
        { "the source reported for three more minutes and the item had no hits: the windows are filled with 0 and two of them end the state", [.. On, Reported(240), Advance(240)], "off" },
        { "the source lost events in one of the two windows: that window is not covered and holds no fill, so one window of 0 does not end the state", [.. On, Lost(70), Reported(180), Advance(180)], "on" },
        { "the loss mark arrives after the window was filled: the fill is taken back", [.. On, Reported(180), Advance(180), Lost(70)], "on" },
        { "without the loss the two windows of 0 end the state", [.. On, Reported(180), Advance(180)], "off" },
        { "the source did not report: nothing is filled, and at exactly the time to live the state still stands", [.. On, Advance(240)], "on" },
        { "the source did not report for longer than the time to live: no data, and not the state a fill would give", [.. On, Advance(300)], "silent" },
        { "the report arrives after the advance: the windows are covered from then on", [.. On, Advance(300), Reported(300)], "off" },
        { "data return after more than the look-back: a new episode, which starts in the initial state", [.. On, Advance(300), Hits("k0", 2, 310), Advance(360)], "off" },
        { "data return and reach the threshold", [.. On, Advance(300), Hits("k0", 5, 310), Advance(360)], "on" },
        { "an item that appears is not in no data before its time to live has passed", [Advance(60), Item("k1"), Advance(120), Advance(300)], "" },
        { "an item that went and returns has existed again only since it returned: its old record does not put it into no data at once", [.. On, Item("k0", -1), Advance(120), Item("k0"), Advance(280)], "on" },
        { "and it is in no data once it has been back for longer than the time to live", [.. On, Item("k0", -1), Advance(120), Item("k0"), Advance(240), Advance(430)], "silent" },
        { "an item that has existed for longer than the time to live without a record is in no data", [Advance(60), Item("k1"), Advance(120), Advance(360)], "silent" },
    };

    /// <summary>
    /// Def. 7.16 for a roll-up, as built (world `roll-up in no data when all members are`, time to live two minutes, the key is the group `g0`): a roll-up knows a member only through its records.
    /// A member that appeared less than the time to live ago and has no record yet (no fill, because its source has not reported) is invisible to it, so a key whose only other member has long been silent is in no data.
    /// Whether such a member should keep its key out of no data is open with the paper (content review, third pass, point 1); this test pins what is built, in the engine and the reference alike.
    /// </summary>
    [Fact]
    public void A_roll_up_knows_a_member_only_through_its_records()
    {
        var w = Worlds.RollupAll;
        Input[] log = [Item("k0"), Hits("k0", 5, 10), Advance(60), Advance(240), Item("k1"), Advance(300)];       // k0 silent since 120, k1 there since 300 without a record
        Assert.Contains(ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log).Graphs["view"].Lines(), l => l.Contains("state=t:silent", StringComparison.Ordinal));
        Input[] reported = [.. log, Hits("k1", 0, 300), Advance(360)];                                                // a record of k1: the key is out of no data
        Assert.DoesNotContain(ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), reported).Graphs["view"].Lines(), l => l.Contains("state=t:silent", StringComparison.Ordinal));
        Assert.Null(Differential.FirstMismatch(w, [.. reported], 0, reordered: false)?.ToString());
    }

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
