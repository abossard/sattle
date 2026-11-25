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
    static RowInput Policy(string k) => new("b", Row(("k", Value.Text(k)), ("g", Value.Text("g0")), ("n", Value.Integer(1)), ("d", Value.Null), ("t", Value.Instant(Origin))), 1);
    static AdvanceInput Advance(long seconds) => new(Origin + seconds * Second);

    static object Fold(Engine engine, string graph) => engine.Graphs.Single(g => g.Name == graph).Plan.Ops.Single(o => o.GetType().Name == "FoldOp");
    static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.GetValue(o)!;
    static IEnumerable<object> Held(object fold) => Field<System.Collections.IDictionary>(fold, "states").Values.Cast<object>();

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
                    most = Math.Max(most, Field<System.Collections.ICollection>(key, part).Count);
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
}
