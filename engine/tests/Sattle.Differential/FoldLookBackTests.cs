using System.Globalization;
using System.Reflection;
using Sattle.Core;
using Sattle.Reference;
using Sattle.Runtime;
using static Sattle.Differential.Logs;

namespace Sattle.Differential;

/// <summary>#173 (Def. 6.7, #122 D32): the fold with a look-back holds per key its seed, its policy rows and its windows in the look-back, and nothing of a key
/// with no policy row and no window there. Each test runs the engine one input at a time, reads the fold's state, and checks that the reference agrees with the
/// engine under every cut of its log.</summary>
public class FoldLookBackTests
{
    static RowInput Item(string k, long weight = 1) => new("a", Row(("k", Value.Text(k)), ("g", Value.Text("g0")), ("n", Value.Integer(1)), ("d", Value.Null), ("t", Value.Instant(Origin))), weight);
    static RowInput Hits(string k, long n, long seconds) => new("b", Row(("k", Value.Text(k)), ("g", Value.Text("g0")), ("n", Value.Integer(n)), ("d", Value.Null), ("t", Value.Instant(Origin + seconds * Second))), 1);
    /// <summary>A row of `a` in `fold over a windowed summarize`: a hit of `k` at `seconds`, with the decimal `d`; a row of `b` is a policy row of `k`.</summary>
    static RowInput Hit(string k, long seconds, decimal? d = null) => new("a", Row(("k", Value.Text(k)), ("g", Value.Text("g0")), ("n", Value.Integer(1)), ("d", d is { } x ? Value.Decimal(x) : Value.Null), ("t", Value.Instant(Origin + seconds * Second))), 1);
    static RowInput Policy(string k, long weight = 1) => new("b", Row(("k", Value.Text(k)), ("g", Value.Text("g0")), ("n", Value.Integer(1)), ("d", Value.Null), ("t", Value.Instant(Origin))), weight);
    static AdvanceInput Advance(long seconds) => new(Origin + seconds * Second);

    static object Fold(Engine engine, string graph) => engine.Graphs.Single(g => g.Name == graph).Plan.Ops.Single(o => o.GetType().Name == "FoldOp");
    static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.GetValue(o)!;
    static IEnumerable<object> Held(object fold) => Field<System.Collections.IDictionary>(fold, "states").Values.Cast<object>();
    /// <summary>The windows a key holds in one of its parts.</summary>
    static int Count(object part) => (int)part.GetType().GetProperty("Count")!.GetValue(part)!;

    /// <summary>Runs the engine one input at a time and calls `look` after each.</summary>
    static void Run(World w, IReadOnlyList<Input> log, Action<Engine, Input> look, IReadOnlySet<string>? traced = null)
    {
        using var engine = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single(), new EngineOptions { WindowRetentionNs = w.RetentionNs, TraceGraphs = traced });
        long seq = 0;
        foreach (var input in log)
        {
            if (input is AdvanceInput a) engine.Advance(a.Frontier, ++seq);
            else EngineRun.Ingest(engine, [(RowInput)input], ++seq);
            look(engine, input);
        }
    }

    /// <summary>A2: in `threshold over a short look-back` items come and go. Item k`r` has a window of 6 hits at minute 20`r`, is folded at the advance to minute
    /// 20`r` + 5, and leaves before the advance a minute later. A key whose item left has no policy row and no window, so from that advance on the fold holds nothing
    /// of it: the number of held keys falls back to none after every item, however many came before, and the slots of the keys are reused.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(12)]
    public void A_key_with_no_window_and_no_policy_row_holds_nothing(int items)
    {
        var w = Worlds.Hysteresis;
        Input[] log = [.. Enumerable.Range(0, items).SelectMany(r => new Input[] { Item($"k{r}"), Hits($"k{r}", 6, r * 1200 + 10), Advance(r * 1200 + 300), Item($"k{r}", -1), Advance(r * 1200 + 360) })];
        Assert.Null(Differential.FirstMismatch(w, log, 1, reordered: false)?.ToString());
        var held = new List<int>();
        var slots = 0;
        Run(w, log, (engine, input) =>
        {
            if (input is not AdvanceInput) return;
            var fold = Fold(engine, "view");
            held.Add(Held(fold).Count());
            slots = Math.Max(slots, Field<System.Collections.ICollection>(fold, "byId").Count);
        });
        Assert.Equal([.. Enumerable.Range(0, items).SelectMany(_ => new[] { 1, 0 })], held);
        Assert.Equal(1, slots);
    }

    /// <summary>A3: in `fold over a windowed summarize` (windows of a minute and a lateness of 3 minutes, so a window leaves the look-back at its close) key k0 has
    /// a policy row and two hits in each of `minutes` minutes, the clock a minute behind them. Per key the fold holds at most the windows of its look-back, five,
    /// and its states and transitions there, however many minutes pass.</summary>
    [Theory]
    [InlineData(10)]
    [InlineData(120)]
    public void A_fold_over_a_windowed_summarize_holds_only_the_windows_of_its_look_back(int minutes)
    {
        var w = Worlds.WindowedFold;
        Input[] log = [Policy("k0"), .. Enumerable.Range(0, minutes).SelectMany(m => new Input[] { Hit("k0", m * 60 + 10), Hit("k0", m * 60 + 20), Advance(m * 60 + 60) })];
        Assert.Null(Differential.FirstMismatch(w, log, 1, reordered: false)?.ToString());
        var most = 0;
        Run(w, log, (engine, _) =>
        {
            foreach (var key in Held(Fold(engine, "master")))
                foreach (var part in new[] { "Obs", "Trace", "Out" })
                    most = Math.Max(most, Count(Field<object>(key, part)));
        });
        Assert.InRange(most, 1, 5);
    }

    /// <summary>A4: in `fold transitions as nodes by key and window` (a look-back of 30 minutes, every transition a node) item k0 of group g0 has 5 hits in each of
    /// `minutes` minutes and the clock moves a minute at a time, so from minute 30 on a window leaves the look-back at every advance. The key turns on at its first
    /// window and stays on. From then on every advance steps the fold once, for the window that closes, and changes no row of the fold's output, read from a traced
    /// run: no earlier transition is written again, in any column. Over the whole run the fold's output changes once, by the first transition.</summary>
    [Theory]
    [InlineData(40)]
    [InlineData(90)]
    public void Under_a_sliding_look_back_a_stable_key_costs_one_step_per_advance(int minutes)
    {
        var w = Worlds.FoldTransitionNodes;
        Input[] log = [Item("k0"), .. Enumerable.Range(0, minutes).SelectMany(m => new Input[] { Hits("k0", 5, m * 60 + 10), Advance(m * 60 + 65) })];
        Assert.Null(Differential.FirstMismatch(w, log, 1, reordered: false)?.ToString());
        var perAdvance = new List<(long Steps, int Changes)>();
        var (steps, rows) = (0L, new Dictionary<string, long>());
        Run(w, log, (engine, input) =>
        {
            if (input is not AdvanceInput) return;
            var fold = Fold(engine, "view");
            var now = engine.Traced.GetValueOrDefault("view")?.GetValueOrDefault(((PhysOp)fold).Name)?.ToDictionary(r => r.Key, r => r.Value.Weight) ?? [];
            var changes = now.Count(r => rows.GetValueOrDefault(r.Key) != r.Value) + rows.Keys.Count(k => !now.ContainsKey(k));
            perAdvance.Add((Field<long>(fold, "steps") - steps, changes));
            (steps, rows) = (Field<long>(fold, "steps"), now);
        }, new HashSet<string> { "view" });
        Assert.Contains(rows.Keys, r => r.Contains("\u001ftoff\u001fton", StringComparison.Ordinal));
        Assert.Equal(1, perAdvance.Sum(x => x.Changes));        // the first transition, written once
        Assert.All(perAdvance.Skip(30), x => Assert.Equal((1L, 0), x));
    }

    /// <summary>A5: an observed min or max over decimals is the number without trailing zeros, as in the reference. In `fold over a windowed summarize`, here
    /// observing the max of `coalesce(top, 0.0) * 1.0`, which carries a trailing zero, k0's minute has the top `d`. The sinks write a decimal without trailing
    /// zeros anyway, so the check reads the fold's transition rows from a traced run.</summary>
    [Theory]
    [InlineData("2.5")]
    [InlineData("1")]
    [InlineData("0.1")]
    public void An_observed_max_of_decimals_has_no_trailing_zeros(string d)
    {
        var w = Worlds.WindowedFold with
        {
            Name = "fold over a windowed summarize, top times 1.0",
            Files = [.. Worlds.WindowedFold.Files.Select(f => f with { Text = f.Text.Replace("\"top\": {\"fn\": \"max\", \"args\": [{\"ref\": \"top\"}]}",
                "\"top\": {\"fn\": \"max\", \"args\": [{\"fn\": \"*\", \"args\": [{\"fn\": \"coalesce\", \"args\": [{\"ref\": \"top\"}, {\"lit\": 0.0}]}, {\"lit\": 1.0}]}]}", StringComparison.Ordinal) })],
        };
        Input[] log = [Policy("k0"), Hit("k0", 10, decimal.Parse(d, CultureInfo.InvariantCulture)), Hit("k0", 20), Advance(60), Advance(300)];
        Assert.Null(Differential.FirstMismatch(w, log, 1, reordered: false)?.ToString());
        using var engine = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single(), new EngineOptions { TraceGraphs = new HashSet<string> { "master" } });
        long seq = 0;
        foreach (var input in log)
            if (input is AdvanceInput a) engine.Advance(a.Frontier, ++seq); else EngineRun.Ingest(engine, [(RowInput)input], ++seq);
        var fold = engine.Master.Plan.Ops.Single(o => o.GetType().Name == "FoldOp").Name;
        Assert.Equal([d], engine.Traced["master"][fold].Values.SelectMany(r => r.Cells.OfType<decimal>()).Select(x => x.ToString(CultureInfo.InvariantCulture)));
    }
    /// <summary>#172 D2: a fold straight after a windowed summarize reads a window from its first row, before the window ends. In `fold over a windowed
    /// summarize` k0 turns on in minute 0, which is dropped at 240 s with the seed on; its policy row leaves while its only row is in minute 4, not yet ended, so k0
    /// keeps its seed, and when a policy row comes back k0 goes on from it. The engine gives the reference's state under every cut.</summary>
    [Fact]
    public void A_row_in_a_window_not_yet_ended_keeps_the_seed()
    {
        var w = Worlds.WindowedFold;
        var gone = new RowInput("b", Row(("k", Value.Text("k0")), ("g", Value.Text("g0")), ("n", Value.Integer(1)), ("d", Value.Null), ("t", Value.Instant(Origin))), -1);
        Input[] log = [Policy("k0"), Hit("k0", 10), Hit("k0", 20), Advance(60), Advance(240), Hit("k0", 250), gone, Advance(260), Policy("k0"), Advance(300), Advance(540)];
        Assert.Null(Differential.FirstMismatch(w, log, 1, reordered: false)?.ToString());
    }

    /// <summary>#173 D1: a fold may read a summarize keyed by windowStart that nothing else reads, since the summary gives it its windows before their end; the
    /// refusal says how to write it instead.</summary>
    [Fact]
    public void A_summary_that_a_fold_reads_has_no_other_reader()
    {
        var files = Worlds.WindowedFold.Files.Select(f => f with { Text = f.Text.Replace("{\"from\": \"signal_fold\", \"to\": \"observed_2\"}", "{\"from\": \"meter_2\", \"to\": \"observed_2\"}", StringComparison.Ordinal) });
        var refused = Assert.Throws<InvalidOperationException>(() => EngineRun.Load(files));
        Assert.Contains("a fold reads the summary meter_2 keyed by windowStart, and nothing else reads it: give the other reader a summarize of its own", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>#175 A3: a variant of `fold transitions as nodes by key and window` (a look-back of 30 minutes) whose policy rows come from the edges between
    /// items: the edge from k0 to k1, both of group g0, gives g0 its policy row. k0 has hits in 10 minutes and the clock follows them. Then one step changes k1's
    /// `n`, its rows in either order: the match of the edge retracts its row and adds it back, so the policy row is withdrawn and added back unchanged, and k1,
    /// with no hits, changes no window. The fold steps no window (it stepped 7 when each of the two rows touched the key) and its output does not change. The
    /// reference agrees under every cut, where the two rows may also come in two steps.</summary>
    [Theory]
    [InlineData(-1L)]
    [InlineData(1L)]
    public void A_policy_row_withdrawn_and_added_back_in_one_step_refolds_nothing(long firstWeight)
    {
        var w = Worlds.FoldTransitionNodes with
        {
            Name = "fold transitions as nodes by key and window, policy by edges",
            Files = [.. Worlds.FoldTransitionNodes.Files.Select(f => f with { Text = f.Text
                .Replace("""{"from": "signal_2", "to": "signal_5Policy"},""", """{"from": "pol_1", "to": "pol_2"}, {"from": "pol_2", "to": "signal_5Policy"},""", StringComparison.Ordinal)
                .Replace("""{"id": "signal_3",""", """{"id": "pol_1", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "s", "label": "Item"}, {"var": "t", "label": "Item"}], "edges": [{"var": "e", "label": "Edge", "from": "s", "to": "t"}]}}, {"id": "pol_2", "verb": "project", "columns": {"c": {"ref": "s.g"}}}, {"id": "signal_3",""", StringComparison.Ordinal) })],
        };
        Input[] before = [Item("k0"), Item("k1"), new RowInput("edges", Row(("src", Value.Text("k0")), ("dst", Value.Text("k1"))), 1), .. Enumerable.Range(0, 10).SelectMany(m => new Input[] { Hits("k0", 5, m * 60 + 10), Advance(m * 60 + 65) })];
        static RowInput K1(long n, long weight) => new("a", Row(("k", Value.Text("k1")), ("g", Value.Text("g0")), ("n", Value.Integer(n)), ("d", Value.Null), ("t", Value.Instant(Origin))), weight);
        RowInput[] step = firstWeight < 0 ? [K1(1, -1), K1(2, 1)] : [K1(2, 1), K1(1, -1)];
        Assert.Null(Differential.FirstMismatch(w, [.. before, .. step, Advance(10 * 60 + 65)], 1, reordered: false)?.ToString());
        using var engine = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single(), new EngineOptions { WindowRetentionNs = w.RetentionNs, TraceGraphs = new HashSet<string> { "view" } });
        long seq = 0;
        foreach (var input in before)
            if (input is AdvanceInput a) engine.Advance(a.Frontier, ++seq); else EngineRun.Ingest(engine, [(RowInput)input], ++seq);
        var fold = Fold(engine, "view");
        Dictionary<string, long> Rows() => engine.Traced["view"][((PhysOp)fold).Name].ToDictionary(r => r.Key, r => r.Value.Weight);
        var (steps, rows) = (Field<long>(fold, "steps"), Rows());
        Assert.NotEmpty(rows);
        EngineRun.Ingest(engine, [.. step], ++seq);
        Assert.Equal(steps, Field<long>(fold, "steps"));
        Assert.Equal(rows, Rows());
    }

    /// <summary>#181: the world of the test above, its policy rows from the edges between items through `project c = s.g`. The edge from k0 to k1 gives g0
    /// its policy row, k0 has hits in 10 minutes and the clock follows them. Then the edge from k1 to k0 comes and the one from k0 to k1 leaves, in either
    /// order, one row per step: both edges give g0 the same policy row, the bindings `s`, `t` and `e` the projection dropped do not tell them apart, so g0
    /// holds one policy row throughout and it keeps its stamp. The reference agrees under every cut.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_column_the_program_dropped_does_not_tell_policy_rows_apart(bool addFirst)
    {
        var w = Worlds.FoldTransitionNodes with
        {
            Name = "fold transitions as nodes by key and window, policy by edges",
            Files = [.. Worlds.FoldTransitionNodes.Files.Select(f => f with { Text = f.Text
                .Replace("""{"from": "signal_2", "to": "signal_5Policy"},""", """{"from": "pol_1", "to": "pol_2"}, {"from": "pol_2", "to": "signal_5Policy"},""", StringComparison.Ordinal)
                .Replace("""{"id": "signal_3",""", """{"id": "pol_1", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "s", "label": "Item"}, {"var": "t", "label": "Item"}], "edges": [{"var": "e", "label": "Edge", "from": "s", "to": "t"}]}}, {"id": "pol_2", "verb": "project", "columns": {"c": {"ref": "s.g"}}}, {"id": "signal_3",""", StringComparison.Ordinal) })],
        };
        static RowInput Edge(string src, string dst, long weight) => new("edges", Row(("src", Value.Text(src)), ("dst", Value.Text(dst))), weight);
        RowInput[] swap = addFirst ? [Edge("k1", "k0", 1), Edge("k0", "k1", -1)] : [Edge("k0", "k1", -1), Edge("k1", "k0", 1)];
        Input[] log = [Item("k0"), Item("k1"), Edge("k0", "k1", 1), .. Enumerable.Range(0, 10).SelectMany(m => new Input[] { Hits("k0", 5, m * 60 + 10), Advance(m * 60 + 65) }), .. swap, Advance(10 * 60 + 65)];
        Assert.Null(Differential.FirstMismatch(w, log, 1, reordered: false)?.ToString());
        Run(w, log, (engine, _) => Assert.All(Held(Fold(engine, "view")), st => Assert.True(Field<System.Collections.IList>(st, "Policies").Count <= 1)));
    }

    /// <summary>#175 A4: in `fold over a windowed summarize` k0 has a hit in each of minutes 0 to 5, each closed after its hit, so k0 moves at minute 5. Its policy
    /// row leaves, and the clock jumps `minutes` minutes. With no policy in force the state stays up to the next window a policy row enters, of which there is
    /// none, so the key reaches the newest closed window in one look and does not step window by window. The reference agrees under every cut.</summary>
    [Theory]
    [InlineData(30)]
    [InlineData(2000)]
    public void A_key_with_no_policy_in_force_jumps_to_its_next_event(int minutes)
    {
        var w = Worlds.WindowedFold;
        Input[] log = [Policy("k0"), .. Enumerable.Range(0, 6).SelectMany(m => new Input[] { Hit("k0", m * 60 + 10), Advance(m * 60 + 240) }), Policy("k0", -1), Advance(540 + minutes * 60)];
        Assert.Null(Differential.FirstMismatch(w, log, 1, reordered: false)?.ToString());
        var perAdvance = new List<long>();
        var steps = 0L;
        Run(w, log, (engine, input) =>
        {
            if (input is not AdvanceInput) return;
            var now = Field<long>(Fold(engine, "master"), "steps");
            perAdvance.Add(now - steps); steps = now;
        });
        Assert.Equal(1L, perAdvance[^1]);
    }

    /// <summary>#175 A5: the transition function of a key's mode in the fold. A change at a closed window makes a key dirty from the least window changed; its
    /// close makes it dirty from the window after the newest stepped; a job ends it moving, at rest or stopped at its last window, or inactive when no window starts
    /// it; a drop moves its newest window up; the seed none makes it inactive.</summary>
    [Theory]
    [InlineData("Resting", 4L, Max, "Changed", 2L, "Dirty", 4L, 2L)]
    [InlineData("Dirty", 4L, 3L, "Changed", 5L, "Dirty", 4L, 3L)]
    [InlineData("Dirty", 4L, 3L, "Changed", 1L, "Dirty", 4L, 1L)]
    [InlineData("Moving", 4L, Max, "Due", 0L, "Dirty", 4L, Max)]
    [InlineData("Dirty", 4L, 3L, "Due", 0L, "Dirty", 4L, 3L)]
    [InlineData("Dirty", 4L, 3L, "Moved", 9L, "Moving", 9L, Max)]
    [InlineData("Dirty", 4L, Max, "Rested", 9L, "Resting", 9L, Max)]
    [InlineData("Dirty", 4L, Max, "Stopped", 9L, "Stopped", 9L, Max)]
    [InlineData("Dirty", 4L, 2L, "Unstarted", 9L, "Inactive", 9L, Max)]
    [InlineData("Resting", 4L, Max, "Dropped", 9L, "Resting", 9L, Max)]
    [InlineData("Moving", 9L, Max, "Dropped", 4L, "Moving", 9L, Max)]
    [InlineData("Moving", 9L, Max, "Emptied", 0L, "Inactive", 9L, Max)]
    public void A_key_mode_follows_its_transition_function(string phase, long at, long from, string e, long ws, string toPhase, long toAt, long toFrom)
    {
        var op = typeof(Engine).Assembly.GetType("Sattle.Runtime.FoldOp")!;
        var mode = op.GetNestedType("Mode", BindingFlags.NonPublic)!;
        object Mode(string p, long a, long f) => Activator.CreateInstance(mode, Enum.Parse(op.GetNestedType("Phase", BindingFlags.NonPublic)!, p), a, f)!;
        var next = mode.GetMethod("Next", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [Mode(phase, at, from), Enum.Parse(op.GetNestedType("Event", BindingFlags.NonPublic)!, e), ws]);
        Assert.Equal(Mode(toPhase, toAt, toFrom), next);
    }
    const long Max = long.MaxValue;
}
