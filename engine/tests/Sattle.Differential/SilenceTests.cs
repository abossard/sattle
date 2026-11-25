using System.Collections.Immutable;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;
using Sattle.Reference;
using static Sattle.Differential.Logs;

namespace Sattle.Differential;

/// <summary>Def. 7.14 and 7.15 and the fold of Def. 6.5 on the smallest logs that show them, in the world `fill and no data`: hits filled with 0, a look-back of thirty minutes, a time to live of three, "on" at one window of 3 or more, "off" after two windows of 1 or less, "silent" after three silent windows in a row (#151 D3: silence is silent windows, a `quiet` counter and a `gap` of 3 in the policy). A window of 60 s is folded when the clock passes its end plus the late horizon of three windows, so the window from 0 s at 240 s. The engine and the reference agree on each, and each gives the state the definitions say.</summary>
public class SilenceTests
{
    static RowInput Item(string k, long weight = 1) => new("a", Logs.Row(("k", Value.Text(k)), ("g", Value.Text("g0")), ("n", Value.Integer(1)), ("d", Value.Null), ("t", Value.Instant(Origin))), weight);
    static RowInput Hits(string k, long n, long seconds) => new("b", Logs.Row(("k", Value.Text(k)), ("g", Value.Text("g0")), ("n", Value.Integer(n)), ("d", Value.Null), ("t", Value.Instant(Origin + seconds * Second))), 1);
    static AdvanceInput Advance(long seconds) => new(Origin + seconds * Second);
    static ProgressInput Reported(long seconds) => new("b", Origin + seconds * Second);
    static LossInput Lost(long seconds) => new(Origin + seconds * Second);

    static readonly Input[] On = [Item("k0"), Hits("k0", 5, 10), Advance(60), Advance(240)];       // one window of 5, folded at 240 s: on. The item has existed since the advance to 60

    public static TheoryData<string, Input[], string> Cases => new()
    {
        { "one window at or above the threshold", On, "on" },
        { "the source reported for three more minutes and the item had no hits: the windows are filled with 0 and two of them end the state", [.. On, Reported(240), Advance(360)], "off" },
        { "the source lost events in one of the two windows: that window is not covered and holds no fill, so one window of 0 does not end the state", [.. On, Lost(70), Reported(180), Advance(360)], "on" },
        { "the loss mark arrives after the window was filled, before it is folded: the fill is taken back", [.. On, Reported(180), Advance(280), Lost(70), Advance(360)], "on" },
        { "without the loss the two windows of 0 end the state", [.. On, Reported(180), Advance(360)], "off" },
        { "the source did not report: nothing is filled, and two silent windows, fewer than the gap, leave the state standing (D3)", [.. On, Advance(360)], "on" },
        { "the source did not report for three windows: they are silent, and the third makes the key silent, not the state a fill would give (D3)", [.. On, Advance(420)], "silent" },
        { "the report arrives after the advance, before the windows are folded: they are covered and end the state", [.. On, Advance(280), Reported(300), Advance(360)], "off" },
        { "data return after a silence shorter than the gap: the state is carried over the silent windows (D2)", [.. On, Advance(360), Hits("k0", 2, 190), Advance(420)], "on" },
        { "data return after the key went silent: it leaves silent from off, and a window of 2 stays there (D2, D3)", [.. On, Advance(420), Hits("k0", 2, 430), Advance(660)], "off" },
        { "data return after the key went silent and reach the threshold", [.. On, Advance(420), Hits("k0", 5, 430), Advance(660)], "on" },
        { "an item without a record and without a fill has no window that is not silent: its key never starts, so it has no state, not even silent (D3)", [Advance(60), Item("k1"), Advance(120), Advance(600)], "" },
        { "an item that went and returned: its policy row left, and the row that came back entered at 300 s, after the window of 5 closed; so no window that is not silent has a policy, and the key has no state (D4, D6)", [.. On, Item("k0", -1), Advance(300), Item("k0"), Advance(360)], "" },
        { "and silent windows with a policy after it do not start the key (D3)", [.. On, Item("k0", -1), Advance(300), Item("k0"), Advance(480)], "" },
        { "an item with one record and none after it is silent three windows later (D3)", [Advance(60), Item("k1"), Hits("k1", 2, 70), Advance(300), Advance(540)], "silent" },
    };

    /// <summary>
    /// Def. 7.15 and the fold for a roll-up, as built (world `roll-up in no data when all members are`, a gap of two silent windows, the key is the group `g0`): a roll-up knows a member only through its records.
    /// A member that appeared and has no record yet (no fill, because its source has not reported) is invisible to it, so the windows of a key whose only other member has long been silent stay silent, and the key is silent (#151 D3).
    /// Whether such a member should keep its key out of silence is open with the paper (content review, third pass, point 1); this test pins what is built, in the engine and the reference alike.
    /// </summary>
    [Fact]
    public void A_roll_up_knows_a_member_only_through_its_records()
    {
        var w = Worlds.RollupAll;
        Input[] log = [Item("k0"), Hits("k0", 5, 10), Advance(60), Advance(240), Advance(420), Item("k1"), Advance(480)];      // windows from 60 s silent, the key silent at 360 s; k1 there since 420 s without a record
        Assert.Contains(ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log).Graphs["view"].Lines(), l => l.Contains("state=t:silent", StringComparison.Ordinal));
        Input[] reported = [.. log, Hits("k1", 0, 480), Advance(720)];                                                // a record of k1, its window folded at 720 s: the key leaves silent
        Assert.DoesNotContain(ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), reported).Graphs["view"].Lines(), l => l.Contains("state=t:silent", StringComparison.Ordinal));
        Assert.Null(Differential.FirstMismatch(w, [.. reported], 0, reordered: false)?.ToString());
    }

    /// <summary>#111, world `fill and no data over hop windows`: the hits are in windows of 3 minutes every 60 s, so the hit at 10 s is in the windows from -120, -60 and 0 s. A window is covered, and filled, only once the source has reported past its end, not the end of its grid window. The fold steps one window per 60 s and folds the window from a at a + 240 s.</summary>
    public static TheoryData<string, Input[], string> HopCases => new()
    {
        { "reported to 240 s: the window [120 s, 300 s) has not ended, so it is not filled, is silent, and the state stands", [.. On, Reported(240), Advance(360)], "on" },
        { "reported to 360 s: the windows from 60 and 120 s ended without hits, are filled with 0 and end the state", [.. On, Reported(360), Advance(360)], "off" },
        { "a hit late in a window: the window from 120 s holds it and is not filled with 0", [.. On, Reported(240), Advance(240), Hits("k0", 5, 250), Reported(360), Advance(360)], "on" },
        { "a loss at 250 s: only the window from 120 s reaches it; it is not covered, so one window of 0 does not end the state", [.. On, Lost(250), Reported(360), Advance(360)], "on" },
        { "the loss mark arrives after the windows were filled, before the window from 120 s is folded: its fill is taken back", [.. On, Reported(360), Advance(300), Lost(250), Advance(360)], "on" },
        { "the windows of 3, folded at 120 s, left the look-back at 360 s, and the state they set outlives them (D2): the window from 60 s, filled with 0, is one window toward off", [Advance(60), Hits("k1", 3, -90), Item("k1"), Advance(120), Reported(240), Advance(360)], "on" },
        { "and likewise when the item went and returned before", [Advance(60), Hits("k1", 3, -90), Item("k1"), Item("k1", -1), Item("k1"), Advance(120), Reported(240), Advance(360)], "on" },
    };

    [Theory, MemberData(nameof(HopCases))]
    public void A_fill_over_hop_windows_waits_for_the_end_of_each_window(string what, Input[] log, string state)
    {
        var w = Worlds.FillOverHop;
        var reference = ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log).Graphs["view"].Lines();
        Assert.True(reference.Length == 1 && reference[0].Contains($"state=t:{state}", StringComparison.Ordinal), $"{what}: the reference gives\n{string.Join("\n", reference)}\nfor\n{Print(log)}");
        Assert.Null(Differential.FirstMismatch(w, [.. log], 0, reordered: false)?.ToString());
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

    /// <summary>PROTOCOL 3.67, Def. 6.5 item 3: a fold must come to rest after finitely many silent windows. In a variant of `fill and no data` whose `quiet`
    /// counter grows without bound on silent windows, the state never repeats, so the fold is stopped after 65,536 silent windows in a row and counted once
    /// under `faults.invalidParameter`; the fold of the world, whose counter stops at the gap, comes to rest and is not counted. Both show silent. The record's
    /// window stays in the input (a look-back of 50 days, a retention of 60), since a window that leaves it leaves the fold (#151 D6).</summary>
    [Theory]
    [InlineData(true, 0L)]
    [InlineData(false, 1L)]
    public void A_fold_that_does_not_come_to_rest_is_stopped_and_counted(bool comesToRest, long counted)
    {
        static SourceFile Variant(SourceFile f, bool rest)
        {
            if (!f.Path.EndsWith(".v.gkql.json", StringComparison.Ordinal)) return f;
            var doc = System.Text.Json.Nodes.JsonNode.Parse(f.Text)!;
            foreach (var v in doc["verbs"]!.AsArray())
            {
                if (v!["series"] is { } series) series["lookback"] = "1200h";
                if (!rest && v["fold"] is { } fold)
                    fold["state"]!["quiet"]!["next"] = System.Text.Json.Nodes.JsonNode.Parse("""{"fn": "iff", "args": [{"fn": "==", "args": [{"ref": "n"}, {"lit": 0}]}, {"fn": "+", "args": [{"ref": "quiet"}, {"lit": 1}]}, {"lit": 0}]}""");
            }
            return f with { Text = doc.ToJsonString() };
        }
        var w = Worlds.Fill with { Name = comesToRest ? "fill and no data, look-back of 50 days" : "fill and no data, quiet without bound", Files = [.. Worlds.Fill.Files.Select(f => Variant(f, comesToRest))] };
        using var engine = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single(), new EngineOptions { WindowRetentionNs = 60L * 24 * 60 * Minute });
        Input[] log = [Advance(60), Item("k1"), Hits("k1", 2, 70), Advance(300), Advance(70_000 * 60)];
        var seq = 0L;
        foreach (var input in log)
            if (input is AdvanceInput a) engine.Advance(a.Frontier, ++seq); else EngineRun.Ingest(engine, [(RowInput)input], ++seq);
        Assert.Equal(counted, engine.Counters().InvalidParameter);
        Assert.Equal("silent", engine.Graph("view").State.Entities.Single().Props.Text("d:w.view/prop:state"));
    }
}
