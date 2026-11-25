using Sattle.Core;
using Sattle.Reference;
using static Sattle.Differential.Logs;

namespace Sattle.Differential;

/// <summary>Theorem 6.1 and Theorem 6.2 on the smallest log of every defect that the code reviews and the harness found, and on a written log of every case a review named that generated logs may miss: after every batch of every cut, and with the rows of a batch reversed, the engine equals the reference. Each log is written out, so a defect that comes back fails here by name, whatever the generated logs of its world reach.</summary>
public class SmallestLogTests
{
    static RowInput Row(string source, long weight, string k, string g, long n, long seconds, decimal? d = null) =>
        new(source, Logs.Row(("k", Value.Text(k)), ("g", Value.Text(g)), ("n", Value.Integer(n)), ("d", d is { } q ? Value.Decimal(q) : Value.Null), ("t", Value.Instant(Origin + seconds * Second))), weight);
    static RowInput Edge(long weight, string src, string dst, string source = "edges") => new(source, Logs.Row(("src", Value.Text(src)), ("dst", Value.Text(dst))), weight);
    static RowInput Load(string? uid, string? name, long n, long seconds) => new("loads", Logs.Row(("uid", uid is null ? Value.Null : Value.Text(uid)), ("name", name is null ? Value.Null : Value.Text(name)), ("n", Value.Integer(n)), ("t", Value.Instant(Origin + seconds * Second))), 1);
    static RowInput Instance(string uid, string name) => new("instances", Logs.Row(("uid", Value.Text(uid)), ("name", Value.Text(name)), ("g", Value.Text("g0"))), 1);
    static AdvanceInput Advance(long seconds) => new(Origin + seconds * Second);
    static Input[] Items(int count) => [.. Enumerable.Range(0, count).Select(i => Row("a", 1, "k" + i, "g0", 1, 0))];
    /// <summary>Edges of `edges` between items, each written `from>to`.</summary>
    static Input[] Edges(params string[] pairs) => [.. pairs.Select(p => p.Split('>')).Select(e => Edge(1, e[0], e[1]))];
    /// <summary>The fact text of a `Reach` from item `a` to item `b`.</summary>
    static string Reach(string a, string b) => $"prop:a=t:{a},d:w.view/prop:b=t:{b},";

    /// <summary>The defect, the world it shows in, its smallest log, and a text the reference's result must contain (so a log that no longer reaches the case fails).</summary>
    public static TheoryData<string, string, Input[], string?> Cases => new()
    {
        // ---- the first code review (paper/research/07-code-review-findings.md, section 1) ----
        { "a state inside the band of the fold is carried over its windows (#151 D2): only `exitAfter` windows at or below `below` leave the state", "threshold over a short look-back",
            [
                Row("a", 1, "k0", "g0", 1, 0),
                Row("b", 1, "k0", "g0", 6, 0), Row("b", 1, "k0", "g0", 6, 60), Advance(120),            // two windows at 6: on (above 3 for 2)
                Row("b", 1, "k0", "g0", 3, 120), Row("b", 1, "k0", "g0", 3, 180), Row("b", 1, "k0", "g0", 3, 240), Row("b", 1, "k0", "g0", 3, 300),
                Advance(360),                                                                         // the windows at 6 are inside the 10 minutes of the look-back; 3 never reached below 1
            ], "state=t:on" },
        { "a window stays while one of its two supporting rows remains", "shared window",
            [
                Row("a", 1, "K0", "g0", 1, 0), Row("a", 1, "k0", "g0", 1, 0),
                Row("b", 1, "K0", "g0", 4, 0), Row("b", 1, "k0", "g0", 4, 0), Advance(60),
                Row("a", -1, "K0", "g0", 1, 0),
            ], "window " },
        { "the last row of a group breaks a tie of its order by the greater text", "group last of one row",
            [Row("a", 1, "z", "g0", 1, 0), Row("a", 1, "a", "g0", 1, 0)], "v=t:z" },

        // ---- latent in the review, reached by the harness ----
        { "a Decimal join key is compared by its value: 2.5 and 2.50 join", "join on a decimal key",
            [Row("a", 1, "k1", "g1", 3, 0, 2.5m), Row("b", 1, "k0", "g0", 5, 0, 2.50m)], null },
        { "Distinct compares a Decimal by its value: 2.5 and 2.50 are one row", "distinct over a decimal",
            [Row("b", 2, "k2", "g0", 1, 0, 2.5m), Row("b", 1, "k0", "g0", 5, 0, 2.50m)], null },
        { "Retain keeps the rest of a held row after a partial retraction and withdraws it at its age", "retain",
            [Row("a", 2, "k2", "g0", 4, -280, 2.5m), Row("a", -1, "k2", "g0", 4, -280, 2.5m), Advance(120)], null },
        { "countDistinct next to count: a retraction cancels its insertion", "group count with countDistinct",
            [Row("a", 2, "k0", "g1", 5, 0), Row("a", -2, "k0", "g1", 5, 0)], null },
        { "Traverse reaches an entity that appears at exactly the maximum depth", "traverse",
            [Row("a", 1, "k1", "g1", 2, 0), Edge(1, "k1", "k0"), Edge(1, "k0", "k3"), Row("a", 1, "k3", "g2", 3, 0)], null },
        { "Series joins no window older than the look-back to a row that arrives later", "series and rollup",
            [Advance(150), Row("b", 2, "k3", "g1", 5, -10), Row("a", 1, "k3", "g0", 1, 150)], null },

        // ---- ruled after the review, and found by the harness ----
        { "of two relationships on one pair and type the smaller id is present (Def. 6.4), the edge first", "two key rules",
            [Edge(1, "k0", "k1"), Edge(1, "k0", "k1", "more")], "rel " },
        { "of two relationships on one pair and type the smaller id is present (Def. 6.4), the edge last", "two key rules",
            [Edge(1, "k0", "k1", "more"), Edge(1, "k0", "k1")], "rel " },
        { "a sink adds weights whatever the order inside a batch: a retraction before its insertion leaves nothing", "match nodes",
            [Row("a", 1, "k0", "g1", 5, 0), Row("a", -1, "k0", "g1", 5, 0)], null },

        // ---- the second code review, defect 20: one number at two scales (1.5 and 1.50) is one value; a row that comes at one scale and goes at the other leaves nothing ----
        { "two scales of one number cancel at a sink", "optional property",
            [Row("a", 1, "k0", "g0", 1, 0, 1.5m), Row("a", 1, "k1", "g0", 1, 0, 1.50m), Row("a", -1, "k0", "g0", 1, 0, 1.50m), Row("a", -1, "k1", "g0", 1, 0, 1.5m)], null },
        { "two scales of one number cancel through Distinct", "distinct over a decimal",
            [Row("a", 1, "k0", "g0", 1, 0, 1.5m), Row("a", 1, "k1", "g0", 1, 0, 1.50m), Row("a", -1, "k0", "g0", 1, 0, 1.50m), Row("a", -1, "k1", "g0", 1, 0, 1.5m)], null },
        { "two scales of one number cancel through a Join on the number", "join on a decimal key",
            [Row("b", 1, "k9", "g0", 1, 0, 1.5m), Row("a", 1, "k0", "g0", 1, 0, 1.5m), Row("a", 1, "k1", "g0", 1, 0, 1.50m), Row("a", -1, "k0", "g0", 1, 0, 1.50m), Row("a", -1, "k1", "g0", 1, 0, 1.5m)], null },

        // ---- the second code review, defect 22: windows of merged identities (Law 7.5 item 3) ----
        { "a view reads the windows of a merged instance in the step of the merge", "a view's series over an instance that a link merges",
            [Edge(1, "web", "web", "flows"), Load(null, "Web", 2, 20), Advance(90), Instance("u0", "Web")], "d:w.view/prop:load " },
        { "a window without a value under one id has none after the merge, whichever came first", "two groups by two keys of one class, one event property",
            [Load(null, "Web", 5, 10), Load(null, "web", 7, 20), Load("u0", null, 3, 30), Advance(90), Instance("u0", "Web")], null },
        { "a view named by the element follows a merge of the master", "a view named by the element of a linked domain",
            [Edge(1, "web", "web", "flows"), Instance("u0", "Web")], "d:w.view/prop:name=" },

        // ---- #113: traverse in increments (an added edge lowers depths from its far end, a lost edge walks its starts again); an advance ends a batch, so the last edges form a patch of their own under every cut ----
        { "an added edge lowers depths two hops past its far end, and an item comes into reach at the maximum depth", "traverse from depth 1 to 3",
            [.. Items(5), .. Edges("k0>k1", "k1>k2", "k2>k3", "k3>k4"), Advance(60), .. Edges("k0>k2")], Reach("k0", "k4") },
        { "a cycle back to the start does not reach the start below the minimum depth", "traverse from depth 1 to 3",
            [.. Items(2), .. Edges("k0>k1"), Advance(60), .. Edges("k1>k0")], Reach("k0", "k1") },
        { "two added edges of one patch, the second lowering the near end of the first", "traverse from depth 1 to 3",
            [.. Items(5), .. Edges("k0>k1", "k1>k2", "k3>k4"), Advance(60), .. Edges("k2>k3", "k0>k2")], Reach("k0", "k4") },
        { "an added and a lost edge of one patch, of different starts", "traverse from depth 1 to 3",
            [.. Items(5), .. Edges("k0>k1", "k3>k4"), Advance(60), Edge(-1, "k3", "k4"), Edge(1, "k1", "k2")], Reach("k0", "k2") },
        { "against the direction of the edges, an added edge lowers depths two hops past its far end", "traverse in",
            [.. Items(5), .. Edges("k1>k0", "k2>k1", "k3>k2", "k4>k3"), Advance(60), .. Edges("k2>k0")], Reach("k0", "k4") },
        { "either way, an added edge against the direction lowers depths past its far end", "traverse either way",
            [.. Items(4), .. Edges("k1>k0", "k1>k2", "k3>k2"), Advance(60), .. Edges("k2>k0")], Reach("k0", "k3") },
        { "a traverse follows a sub-type of its relationship type and not another type", "traverse over a sub-type, not over another type",
            [.. Items(3), Edge(1, "k0", "k1", "fast"), Edge(1, "k1", "k2", "other"), Advance(60), Edge(1, "k0", "k2", "other"), Advance(120), Edge(1, "k1", "k2", "fast")], Reach("k0", "k2") },
    };

    [Theory, MemberData(nameof(Cases))]
    public void The_engine_equals_the_reference_on_the_smallest_log_of_a_past_defect(string defect, string world, Input[] log, string? witness)
    {
        _ = defect;
        var w = Worlds.All.Single(x => x.Name == world);
        if (witness is not null)
            Assert.Contains(ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log, new Settings(WindowRetentionNs: w.RetentionNs)).Graphs.Values.SelectMany(g => g.Lines()), l => l.Contains(witness, StringComparison.Ordinal));
        foreach (var reordered in new[] { false, true })
        {
            var mismatch = Differential.FirstMismatch(w, log, 0, reordered);
            Assert.True(mismatch is null, $"{Print(log)}\n{mismatch}");
        }
    }
}
