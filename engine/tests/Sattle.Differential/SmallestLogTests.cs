using Sattle.Core;
using Sattle.Reference;
using static Sattle.Differential.Logs;

namespace Sattle.Differential;

/// <summary>Theorem 6.1 and Theorem 6.2 on the smallest log of every defect that the code reviews and the harness found: after every batch of every cut, and with the rows of a batch reversed, the engine equals the reference. Each log is written out, so a defect that comes back fails here by name, whatever the generated logs of its world reach.</summary>
public class SmallestLogTests
{
    static RowInput Row(string source, long weight, string k, string g, long n, long seconds, decimal? d = null) =>
        new(source, Logs.Row(("k", Value.Text(k)), ("g", Value.Text(g)), ("n", Value.Integer(n)), ("d", d is { } q ? Value.Decimal(q) : Value.Null), ("t", Value.Instant(Origin + seconds * Second))), weight);
    static RowInput Edge(long weight, string src, string dst, string source = "edges") => new(source, Logs.Row(("src", Value.Text(src)), ("dst", Value.Text(dst))), weight);
    static RowInput Load(string? uid, string? name, long n, long seconds) => new("loads", Logs.Row(("uid", uid is null ? Value.Null : Value.Text(uid)), ("name", name is null ? Value.Null : Value.Text(name)), ("n", Value.Integer(n)), ("t", Value.Instant(Origin + seconds * Second))), 1);
    static RowInput Pod(string uid, string name) => new("pods", Logs.Row(("uid", Value.Text(uid)), ("name", Value.Text(name)), ("g", Value.Text("g0"))), 1);
    static AdvanceInput Advance(long seconds) => new(Origin + seconds * Second);

    /// <summary>The defect, the world it shows in, its smallest log, and a text the reference's result must contain (so a log that no longer reaches the case fails).</summary>
    public static TheoryData<string, string, Input[], string?> Cases => new()
    {
        // ---- the first code review (paper/research/07-code-review-findings.md, section 1) ----
        { "a state inside the hysteresis band survives the look-back (Def. 7.10): only `exitAfter` windows at or below `below` leave the state", "threshold over a short look-back",
            [
                Row("a", 1, "k0", "g0", 1, 0),
                Row("b", 1, "k0", "g0", 6, 0), Row("b", 1, "k0", "g0", 6, 60), Advance(120),            // two windows at 6: on (above 3 for 2)
                Row("b", 1, "k0", "g0", 3, 120), Row("b", 1, "k0", "g0", 3, 180), Row("b", 1, "k0", "g0", 3, 240), Row("b", 1, "k0", "g0", 3, 300),
                Advance(360),                                                                         // the windows at 6 are older than the 3 minutes of the look-back; 3 never reached below 1
            ], "state=t:on" },
        { "a telemetry window stays while one of its two supporting rows remains", "shared window",
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

        // ---- the second code review, defect 22: telemetry of merged identities (Law 7.5 item 3) ----
        { "a view reads the windows of a merged pod in the step of the merge", "a view's series over a pod that a link merges",
            [Edge(1, "web", "web", "flows"), Load(null, "Web", 2, 20), Advance(90), Pod("u0", "Web")], "d:w.view/prop:load " },
        { "a window without a value under one id has none after the merge, whichever came first", "two groups by two keys of one class, one telemetry property",
            [Load(null, "Web", 5, 10), Load(null, "web", 7, 20), Load("u0", null, 3, 30), Advance(90), Pod("u0", "Web")], null },
        { "a view named by the element follows a merge of the master", "a view named by the element of a linked domain",
            [Edge(1, "web", "web", "flows"), Pod("u0", "Web")], "d:w.view/prop:name=" },
    };

    [Theory, MemberData(nameof(Cases))]
    public void The_engine_equals_the_reference_on_the_smallest_log_of_a_past_defect(string defect, string world, Input[] log, string? witness)
    {
        _ = defect;
        var w = Worlds.All.Single(x => x.Name == world);
        if (witness is not null)
            Assert.Contains(ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log, new Settings(TelemetryRetentionNs: w.RetentionNs)).Graphs.Values.SelectMany(g => g.Lines()), l => l.Contains(witness, StringComparison.Ordinal));
        foreach (var reordered in new[] { false, true })
        {
            var mismatch = Differential.FirstMismatch(w, log, 0, reordered);
            Assert.True(mismatch is null, $"{Print(log)}\n{mismatch}");
        }
    }
}
