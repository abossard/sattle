using System.Collections.Immutable;
using Sattle.Core;
using Sattle.Language;
using Sattle.Reference;
using static Sattle.Differential.Logs;

namespace Sattle.Differential;

/// <summary>
/// Proposition 5.7 at the engine boundary: a refactoring (Def. 5.12) is an edit of the projection text of one module, so each case is two definition sets, before and after the edit, run on the same generated log, compared by `Out` (Def. 5.13): the multiset of the lines of all graphs, the graph name forgotten.
/// Where R1 to R5 hold (R0 holds because the whole log is replayed), `Out` is the same for `extract`, `inline` and `moveRule`. Where R3 or R4 fails, `Out` differs the way the paper's counterexample says. On the extract case the reference evaluator is checked to agree, so the engine is not its own oracle.
/// </summary>
public class RefactoringTests
{
    // ---- the master: items of `a`, their hits per window from `b`, edges between items ----

    static SourceSpec Bag(string name) => new(name, (r, now) => Row(
        ("k", Text(r, "k", 4)), ("g", Text(r, "g", 3)), ("n", Value.Integer(r.Next(6))), ("d", Value.Null), ("t", Near(r, now))));
    static readonly IReadOnlyList<SourceSpec> Sources =
    [
        Bag("a") with { Key = f => ((TextValue)f["k"]).S },
        Bag("b"),
        new("edges", (r, _) => Row(("src", Text(r, "k", 4)), ("dst", Text(r, "k", 4))), f => ((TextValue)f["src"]).S + ">" + ((TextValue)f["dst"]).S),
    ];
    /// <summary>A space over the master with the projections of `w.view` that the case `name` refactors, and the graphs that bind them.</summary>
    static World Space(string name) => new("refactoring: " + name, "Prop. 5.7", Worlds.Files("refactoring: " + name), Sources, 0.25);

    /// <summary>`Then` gets `Out` before and after, each sorted; it asserts what Prop. 5.7 or its counterexample says.</summary>
    sealed record Case(World Before, World After, Action<ImmutableArray<string>, ImmutableArray<string>> Then);

    static readonly ImmutableDictionary<string, Case> Cases = new Dictionary<string, Case>
    {
        ["extract: the pairs, the update of Pair, the relationships between copies and the parameter they read move to a new graph; a reader of the copies stays bound to the parent"] = new(
            Space("extract, before"),
            Space("extract, after"),
            Same("entity:Pair ", "rel:Near ", "prop:n=", "entity:Seen ")),
        ["inline: the signal and its windows come back from their own graph into the parent"] = new(
            Space("inline, before"),
            Space("inline, after"),
            Same("entity:Copy ", "window ")),
        ["moveRule: the signal moves from the parent to another projection of the module, which has a parameter of its own"] = new(
            Space("moveRule, before"),
            Space("moveRule, after"),
            Same("entity:Copy ", "entity:Pair ", "window ")),

        ["without R3, one key: two sinks emit the same copy with the same properties; after extract Out holds it twice"] = new(
            Space("one key, before"),
            Space("one key, after"),
            (before, after) =>
            {
                var more = Minus(after, before);
                Assert.True(Minus(before, after).IsEmpty, "nothing of Out is lost");
                Assert.NotEmpty(more);
                Assert.All(more, l => Assert.True(l.StartsWith("entity ", StringComparison.Ordinal) && l.Contains(" d:w.view/entity:Copy ", StringComparison.Ordinal) && before.Contains(l), $"only copies already in Out are added, a second time: {l}"));
            }),
        ["without R3, a conflict: two sinks give owner the values a and b for one key; before the property is absent, after each graph holds its value"] = new(
            Space("conflict, before"),
            Space("conflict, after"),
            (before, after) =>
            {
                var owned = before.Count(l => l.Contains(" d:w.view/entity:Owned ", StringComparison.Ordinal));
                Assert.True(owned > 0, "the log makes owned elements");
                Assert.DoesNotContain(before, l => l.Contains("prop:owner=", StringComparison.Ordinal));
                Assert.Equal(owned, after.Count(l => l.Contains("prop:owner=t:a", StringComparison.Ordinal)));
                Assert.Equal(owned, after.Count(l => l.Contains("prop:owner=t:b", StringComparison.Ordinal)));
            }),
        ["without R3, a uniqueness tuple: two relationship sinks with different key rules emit one (source, target, Near); before only the smaller id is present, after both"] = new(
            Space("uniqueness tuple, before"),
            Space("uniqueness tuple, after"),
            (before, after) =>
            {
                static string[] Rels(ImmutableArray<string> o) => [.. o.Where(l => l.Contains(" d:w.view/rel:Near ", StringComparison.Ordinal))];
                Assert.NotEmpty(Rels(before));
                Assert.True(Minus(before, after).IsEmpty, "the smaller id stays");
                Assert.Equal(2 * Rels(before).Length, Rels(after).Length);
                Assert.Equal(Rels(before).Length, Rels(after).Select(Ends).Distinct().Count());
            }),
        ["without R4, a reader of what moves: a third graph reads the pairs from the parent graph, which loses them to the extracted one, so its output changes"] = new(
            Space("reader of what moves, before"),
            Space("reader of what moves, after"),
            (before, after) =>
            {
                var lost = Minus(before, after);
                Assert.True(Minus(after, before).IsEmpty, "nothing is added to Out");
                Assert.NotEmpty(lost);
                Assert.All(lost, l => Assert.Contains(" d:w.view/entity:Seen ", l, StringComparison.Ordinal));
                Assert.Equal(before.Count(l => l.Contains(" d:w.view/entity:Seen ", StringComparison.Ordinal)), lost.Length);
            }),
    }.ToImmutableDictionary();

    static string Ends(string relLine) => string.Join(' ', relLine.Split(' ').Skip(2));

    /// <summary>R1 to R5 hold: `Out` is the same, and it holds each `witness`, so the moved part is not empty.</summary>
    static Action<ImmutableArray<string>, ImmutableArray<string>> Same(params string[] witnesses) => (before, after) =>
    {
        foreach (var w in witnesses) Assert.Contains(before, l => l.Contains(w, StringComparison.Ordinal));
        Assert.True(before.SequenceEqual(after), $"before only:\n{string.Join("\n", Minus(before, after))}\nafter only:\n{string.Join("\n", Minus(after, before))}");
    };

    /// <summary>`x` minus `y` as multisets of lines.</summary>
    static ImmutableArray<string> Minus(ImmutableArray<string> x, ImmutableArray<string> y)
    {
        var left = y.GroupBy(l => l, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return [.. x.Where(l => left.GetValueOrDefault(l) is > 0 and var n ? (left[l] = n - 1) < 0 : true)];
    }

    /// <summary>`Out` of the world after the whole log (R0), from the engine.</summary>
    static ImmutableArray<string> Out(World w, IReadOnlyList<Input> log) => Out(EngineRun.Final(w.Catalog, log, Chunking.Largest));
    static ImmutableArray<string> Out(SpaceFacts facts) => [.. facts.Graphs.Values.SelectMany(g => g.Lines()).Order(StringComparer.Ordinal)];

    /// <summary>A generated log of `seed` and, after it, two items of keys the generator never makes, an edge between them, a hit of the first and an advance that closes its window: every case then has something to move, whatever the seed left held.</summary>
    static ImmutableArray<Input> Log(int seed)
    {
        var log = Generate(Sources, 0.25, seed, 100);
        var now = log.OfType<AdvanceInput>().Select(a => a.Frontier).DefaultIfEmpty(Origin).Max();
        static RowInput Item(string source, string k, string g, long n, long at) => new(source, Row(("k", Value.Text(k)), ("g", Value.Text(g)), ("n", Value.Integer(n)), ("d", Value.Null), ("t", Value.Instant(at))), 1);
        return [.. log, Item("a", "k8", "g8", 3, now), Item("a", "k9", "g9", 4, now), new RowInput("edges", Row(("src", Value.Text("k8")), ("dst", Value.Text("k9"))), 1), Item("b", "k8", "g8", 5, now), new AdvanceInput(now + 3 * Minute)];
    }

    public static TheoryData<string, int> Runs()
    {
        var data = new TheoryData<string, int>();
        foreach (var name in Cases.Keys.Order(StringComparer.Ordinal)) foreach (var seed in new[] { 0, 1, 2 }) data.Add(name, seed);
        return data;
    }

    [Theory, MemberData(nameof(Runs))]
    public void A_refactoring_keeps_the_output_union_exactly_when_its_premises_hold(string refactoring, int seed)
    {
        var c = Cases[refactoring];
        var log = Log(seed);
        c.Then(Out(c.Before, log), Out(c.After, log));
    }

    /// <summary>The engine is not its own oracle: on the extract case the reference evaluator gives the engine's graphs before and after, so the same `Out` (the reference is the slow side, so one case and one seed).</summary>
    [Fact]
    public void The_reference_keeps_the_output_union_of_the_extract_case_as_the_engine_does()
    {
        var c = Cases[Cases.Keys.Single(k => k.StartsWith("extract:", StringComparison.Ordinal))];
        var log = Log(0);
        ImmutableArray<string> Checked(World w)
        {
            var (engine, reference) = (EngineRun.Final(w.Catalog, log, Chunking.Largest), ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log));
            foreach (var (graph, expected) in reference.Graphs)
                Assert.True(expected.Lines().SequenceEqual(engine.Graphs[graph].Lines()), $"{w.Name}, graph {graph}: the engine differs from the reference");
            return Out(reference);
        }
        c.Then(Checked(c.Before), Checked(c.After));
    }
}
