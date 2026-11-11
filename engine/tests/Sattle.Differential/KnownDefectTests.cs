using System.Collections.Immutable;
using Sattle.Core;
using Sattle.Language;
using Sattle.Reference;
using Sattle.Runtime;
using static Sattle.Differential.Logs;

namespace Sattle.Differential;

/// <summary>One test per defect of the code review (paper/research/07-code-review-findings.md, section 1) that the engine core has today. Each states the correct behaviour and fails until the defect is fixed; none is skipped or filtered out of the default run.</summary>
[Trait("Category", "KnownDefect")]
public class KnownDefectTests
{
    static RowInput Row(string source, long weight, string k, string g, long n, long seconds, decimal? d = null) =>
        new(source, Logs.Row(("k", Value.Text(k)), ("g", Value.Text(g)), ("n", Value.Integer(n)), ("d", d is { } q ? Value.Decimal(q) : Value.Null), ("t", Value.Instant(Origin + seconds * Second))), weight);
    static RowInput Edge(long weight, string src, string dst) => new("edges", Logs.Row(("src", Value.Text(src)), ("dst", Value.Text(dst))), weight);
    static AdvanceInput Advance(long seconds) => new(Origin + seconds * Second);

    static World Named(string name) => Worlds.All.Single(w => w.Name == name);
    static ImmutableArray<string> ByEngine(World w, IReadOnlyList<Input> log, string graph) => EngineRun.Final(w.Catalog, log, Chunking.OneByOne).Graphs[graph].Lines();
    static ImmutableArray<string> ByReference(World w, IReadOnlyList<Input> log, string graph) => ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log).Graphs[graph].Lines();

    static void Same(IEnumerable<string> expected, IEnumerable<string> actual, IReadOnlyList<Input> log) =>
        Assert.True(expected.SequenceEqual(actual), $"{Print(log)}\nexpected:\n{string.Join("\n", expected)}\nengine:\n{string.Join("\n", actual)}");

    // ---- reached today ----

    /// <summary>Review: Operators.cs:643. A key that entered "on" and stays inside the hysteresis band must stay "on" (Def. 7.10: only `exitAfter` windows at or below `below` leave the state). The engine folds from `initial` over the windows still in the look-back and reports "off" once the entering windows are older than `within`.</summary>
    [Fact]
    public void A_state_inside_the_hysteresis_band_survives_the_look_back()
    {
        var w = Worlds.Hysteresis;
        Input[] log =
        [
            Row("a", 1, "k0", "g0", 1, 0),
            Row("b", 1, "k0", "g0", 6, 0), Row("b", 1, "k0", "g0", 6, 60), Advance(120),            // two windows at 6: on (above 3 for 2)
            Row("b", 1, "k0", "g0", 3, 120), Row("b", 1, "k0", "g0", 3, 180), Row("b", 1, "k0", "g0", 3, 240), Row("b", 1, "k0", "g0", 3, 300),
            Advance(360),                                                                         // the windows at 6 are older than the 3 minutes of the look-back; 3 never reached below 1
        ];
        Assert.Contains(ByReference(w, log, "view"), l => l.Contains("state=t:on"));
        Same(ByReference(w, log, "view"), ByEngine(w, log, "view"), log);
    }

    /// <summary>Review: Identity.cs:165 with Exprs.cs:107. An identity is allocated for the rows a sink emits (Def. 5.9), not for rows a Filter rejected.</summary>
    [Fact]
    public void A_row_that_a_filter_rejected_mints_no_identity()
    {
        var w = Worlds.FilteredEmit;
        using var engine = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single());
        EngineRun.Ingest(engine, [Row("a", 1, "k0", "g0", 0, 0), Row("a", 1, "k1", "g0", 1, 0), Row("a", 1, "k2", "g0", 5, 0)], 1);
        Assert.Single(engine.Master.State.Entities);
        Assert.Equal(1, engine.Identities.Minted);
    }

    /// <summary>Review: Output.cs:295-306. A telemetry window has the value its supporting rows give it; it goes when the last of them goes. The engine keeps no support: the retraction of one of two equal contributions deletes the window.</summary>
    [Fact]
    public void A_telemetry_window_stays_while_one_of_its_two_supporting_rows_remains()
    {
        var w = Worlds.SharedWindow;
        Input[] log =
        [
            Row("a", 1, "K0", "g0", 1, 0), Row("a", 1, "k0", "g0", 1, 0),
            Row("b", 1, "K0", "g0", 4, 0), Row("b", 1, "k0", "g0", 4, 0), Advance(60),
            Row("a", -1, "K0", "g0", 1, 0),
        ];
        var expected = ByReference(w, log, "view");
        Assert.Contains(expected, l => l.StartsWith("window ", StringComparison.Ordinal));
        Same(expected, ByEngine(w, log, "view"), log);
    }

    /// <summary>Review: Exprs.cs:119. `<` on Text compares the text. The engine compares interner codes, the order in which the strings were first seen.</summary>
    [Fact]
    public void Text_is_ordered_by_its_characters_and_not_by_first_appearance()
    {
        var w = Worlds.TextOrder;
        Input[] log = [Row("a", 1, "b", "a", 1, 0)];       // "b" is seen before "a": k < g is false
        Assert.Empty(ByReference(w, log, "master"));
        Same([], ByEngine(w, log, "master"), log);
    }

    /// <summary>Review: Exprs.cs:119, Operators.cs:250, :361. `last(value, order)` breaks a tie on the order by the greater value. For Text the engine takes the string that was interned later.</summary>
    [Fact]
    public void Last_breaks_a_tie_by_the_greater_text()
    {
        var w = Named("group last");
        Input[] log = [Row("a", 1, "z", "g0", 1, 0), Row("a", 1, "a", "g0", 1, 0)];
        Assert.Contains(ByReference(w, log, "master"), l => l.Contains("v=t:z"));
        Same(ByReference(w, log, "master"), ByEngine(w, log, "master"), log);
    }

    /// <summary>Review: Operators.cs:58, Columnar.cs:59 (Synthetic.cs:22, Bench.cs:75). A column that a row writer never sets is absent. The row builder marks every column present, so it reads as the value with code 0.</summary>
    [Fact]
    public void A_column_that_the_writer_never_sets_is_absent()
    {
        var w = Worlds.OptionalProperty;
        using var engine = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single());
        var writer = new RowWriter(engine.RowLayout("a"), engine.Interner);
        writer.Begin(1); writer.Text("k", "k0"); writer.Text("g", "g0"); writer.Int(writer.Col("n"), 1); writer.Instant("t", Origin);       // d is not set
        engine.Ingest("a", writer.Build()!, 1);
        Assert.DoesNotContain(EngineRun.FactsOf(engine).Graphs["master"].Lines(), l => l.Contains("prop:d="));
    }

    /// <summary>Review: Catalog.cs:170 with Checker.cs:54, :638. A Match reads the properties its own projection uses. One checker serves every projection of a module and keeps its demand set, so a Match also reads what a same-named operator of an earlier projection demanded.</summary>
    [Fact]
    public void A_match_reads_only_the_properties_its_own_projection_uses()
    {
        var catalog = EngineRun.Load([new SourceFile("w.base.sattle", Worlds.BaseModule), new SourceFile("w.two.sattle", """
            module w.two @ 1.0.0;
            import w.base @ 1.0.0 sha256:… as base;
            entity Copy { k: Text; g?: Text; }
            identity copy for Copy version "1" { k: Text by exact; }
            projection first view forward output w.two {
              in master: graph base;
              let m = match master (i: base.Item);
              emit entity Copy from m identity copy(k: i.k) set { k: i.k, g: i.g };
            }
            projection second view forward output w.two {
              in master: graph base;
              let m = match master (i: base.Item);
              emit entity Copy from m identity copy(k: i.k) set { k: i.k };
            }
            """)]);
        var second = catalog.Projections.Values.Single(p => p.Name == "second").Ops.OfType<TMatch>().Single();
        Assert.Equal(["k"], second.Reads.Select(r => Ids.Name(r.PropId)));
    }

    // ---- latent in the review, reached by the differential harness; the smallest inputs it found ----

    public static TheoryData<string, string, Input[]> Latent => new()
    {
        { "Operators.cs:753, :818: a Decimal join key is compared by its bits, so 2.5 and 2.50 do not join", "join on a decimal key",
            [Row("a", 1, "k1", "g1", 3, 0, 2.5m), Row("b", 1, "k0", "g0", 5, 0, 2.50m)] },
        { "Operators.cs:753: Distinct compares a Decimal by its bits, so 2.5 and 2.50 are two rows", "distinct over a decimal",
            [Row("b", 2, "k2", "g0", 1, 0, 2.5m), Row("b", 1, "k0", "g0", 5, 0, 2.50m)] },
        { "Operators.cs:1109: Retain drops the whole held row on a partial retraction, so the rest is never withdrawn", "retain",
            [Row("a", 2, "k2", "g0", 4, -280, 2.5m), Row("a", -1, "k2", "g0", 4, -280, 2.5m), Advance(120)] },
        { "Operators.cs:435: countDistinct next to count stores its last value in the wrong slot, so the retraction cancels nothing", "group count with countDistinct",
            [Row("a", 2, "k0", "g1", 5, 0), Row("a", -2, "k0", "g1", 5, 0)] },
        { "Operators.cs:948: Traverse does not watch the elements at exactly the maximum depth, so an entity that appears there is not reached", "traverse",
            [Row("a", 1, "k1", "g1", 2, 0), Edge(1, "k1", "k0"), Edge(1, "k0", "k3"), Row("a", 1, "k3", "g2", 3, 0)] },
        { "Output.cs:72-74, :94: Series keeps a window older than the look-back and joins it to a row that arrives later", "series and rollup",
            [Advance(150), Row("b", 2, "k3", "g1", 5, -10), Row("a", 1, "k3", "g0", 1, 150)] },
    };

    [Theory, MemberData(nameof(Latent))]
    public void The_engine_equals_the_reference_on_the_smallest_input_the_harness_found(string defect, string world, Input[] log)
    {
        var w = Named(world);
        _ = defect;
        var (reference, engine) = (ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log), EngineRun.Final(w.Catalog, log, Chunking.OneByOne));
        Same(reference.Graphs.SelectMany(g => g.Value.Lines().Select(l => g.Key + ": " + l)), engine.Graphs.SelectMany(g => g.Value.Lines().Select(l => g.Key + ": " + l)), log);
    }

    /// <summary>Ruled after the review (Def. 6.4, D34), not fixed yet: of several relationships with positive support on one (source, target, type) the one with the smallest id is present, whatever arrived first. The engine keeps the one it saw first and drops the other (`Output.cs`, the uniqueness pass of `Flush`).</summary>
    [Fact]
    public void Of_two_relationships_on_one_pair_and_type_the_smaller_id_is_present_whatever_arrived_first()
    {
        var w = Worlds.TwoKeyRules;
        Input[] first = [Edge(1, "k0", "k1"), new RowInput("more", Logs.Row(("src", Value.Text("k0")), ("dst", Value.Text("k1"))), 1)];
        Input[] second = [first[1], first[0]];
        Same(ByReference(w, first, "master"), ByEngine(w, first, "master"), first);
        Same(ByReference(w, second, "master"), ByEngine(w, second, "master"), second);
    }

    /// <summary>Found by the harness, not in the review's list: a sink is not linear in the weights. A retraction that reaches a sink before the insertion it cancels (the same batch, rows in the other order) is dropped (Output.cs:234, `else if (weight > 0)`), so the element stays.</summary>
    [Fact]
    public void A_sink_adds_weights_whatever_the_order_inside_a_batch()
    {
        var w = Named("match nodes");
        Input[] log = [Row("a", -1, "k0", "g1", 5, 0), Row("a", 1, "k0", "g1", 5, 0)];
        Assert.Empty(ByReference(w, log, "master"));
        Same([], EngineRun.Final(w.Catalog, log, Chunking.Largest).Graphs["master"].Lines(), log);
    }
}
