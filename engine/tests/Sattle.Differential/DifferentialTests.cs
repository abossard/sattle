using Sattle.Runtime;
using Sattle.Core;
namespace Sattle.Differential;

/// <summary>T-INC and batch invariance per operator kind (Theorem 6.1, Theorem 6.2, obligation O6.1): on generated logs the engine's incremental result equals the reference evaluator's batch result after every batch, for every way of cutting the log. A failure prints the smallest log found.</summary>
public class DifferentialTests
{
    public static TheoryData<string> WorldNames => [.. Worlds.All.Select(w => w.Name)];

    [Theory, MemberData(nameof(WorldNames)), Trait("Category", "Differential")]
    public void The_engine_equals_the_reference_however_the_log_is_cut(string world) => Agrees(world, seeds: 12, length: 14, reordered: false);

    [Theory, MemberData(nameof(WorldNames)), Trait("Category", "Differential")]
    public void The_engine_equals_the_reference_when_the_rows_of_a_batch_are_reordered(string world) => Agrees(world, seeds: 12, length: 14, reordered: true);

    [Theory, MemberData(nameof(WorldNames)), Trait("Category", "Slow")]
    public void The_engine_equals_the_reference_on_many_logs_however_they_are_cut(string world) => Agrees(world, seeds: 150, length: 20, reordered: false);

    /// <summary>The comparison is not empty: on the default seeds the reference result of every world holds facts of every graph, and the facts that show its operator at work (a window, a state that switched on, a reach of depth two, a merged identity).</summary>
    [Theory, MemberData(nameof(WorldNames)), Trait("Category", "Differential")]
    public void The_generated_logs_exercise_the_world(string world)
    {
        var w = Worlds.All.Single(x => x.Name == world);
        var results = Enumerable.Range(0, 12).Select(seed => Sattle.Reference.ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), w.Log(seed, 14), new Sattle.Reference.Settings(TelemetryRetentionNs: w.RetentionNs))).ToArray();
        foreach (var graph in results[0].Graphs.Keys) Assert.Contains(results, r => r.Graphs[graph].Lines().Length > 0);
        var lines = results.SelectMany(r => r.Graphs.Values.SelectMany(g => g.Lines())).ToArray();
        Assert.All(w.Witnesses, witness => Assert.Contains(lines, l => l.Contains(witness, StringComparison.Ordinal)));
    }

    /// <summary>T-RETAIN: retention keeps exactly the rows younger than the age at the frontier (Proposition 5.9), on logs with rows that are expired when they arrive, retractions of held rows and frontier advances, however the log is cut.</summary>
    [Fact, Trait("Category", "Differential")]
    public void Retention_keeps_exactly_the_rows_younger_than_the_age_under_generated_frontiers()
    {
        var w = Worlds.All.Single(x => x.Name == "retain");
        const int Seeds = 60, Length = 24;
        var report = Differential.Check(w, Seeds, Length, reordered: false);
        Assert.True(report is null, report);
        var (expired, retracted, advances) = (0, 0, 0);
        foreach (var log in Enumerable.Range(0, Seeds).Select(seed => w.Log(seed, Length)))
        {
            long? frontier = null;
            foreach (var x in log)
                switch (x)
                {
                    case Sattle.Reference.AdvanceInput a: frontier = a.Frontier; advances++; break;
                    case Sattle.Reference.RowInput { Weight: < 0 }: retracted++; break;
                    case Sattle.Reference.RowInput r when frontier is { } f && r.Fields["t"] is InstantValue t && t.Ns + 2 * Logs.Minute <= f: expired++; break;
                }
        }
        Assert.True(expired > 0 && retracted > 0 && advances > 0, $"expired on arrival {expired}, retracted {retracted}, advances {advances}");
    }

    /// <summary>T-MERGE-TELE (Law 7.5 item 3): one window of a pod written by a group over its uid and a group over its name, with the link that merges the two identities before both rows, between them or after both, in every order and batching. The window is the same in every case and equals the reference: the combination of the two values by the property's rule, where a rule that does not combine two different values leaves no value.</summary>
    [Theory, Trait("Category", "Differential")]
    [InlineData("delta sum", 1, 2, "3")] [InlineData("delta sum", 2, 2, "4")]
    [InlineData("gauge min", 1, 2, "1")] [InlineData("gauge max", 1, 2, "2")] [InlineData("gauge max", 2, 2, "2")]
    [InlineData("gauge mean", 1, 2, null)] [InlineData("gauge mean", 2, 2, "2")]                                   // a mean stored as one number: only equal values combine
    [InlineData("gauge last", 1, 2, null)] [InlineData("gauge last", 2, 2, "2")]                                   // a last without its order: the same
    [InlineData("gauge mean", 1, 2, "1.5", "mean(n)", "Decimal")] [InlineData("gauge mean", 2, 2, "2", "mean(n)", "Decimal")]       // a mean with its sum and count
    [InlineData("gauge last", 1, 2, "2", "last(n, t)")] [InlineData("gauge last", 3, 2, "2", "last(n, t)")]                         // a last with its order: the name's row is later
    [InlineData("gauge mean", 1, 4, "2.3333333333333333333333333333", "mean(n)", "Decimal", 2L)]                                  // (1 + 2 + 4) / 3, not the mean 2.75 of the two means
    [InlineData("delta sum", 1, 4, "7", "sum(n)", "Integer", 2L)]
    public void A_merge_combines_windows_as_if_written_under_the_survivor(string rule, long byUid, long byName, string? expected, string aggregate = "sum(n)", string kind = "Integer", long? secondByUid = null)
    {
        var w = Worlds.LinkedLoadsUnder(rule, aggregate, kind);
        static Sattle.Reference.RowInput Load(string? uid, string? name, long n, long seconds) => new("loads", Logs.Row(("uid", uid is null ? Value.Null : Value.Text(uid)), ("name", name is null ? Value.Null : Value.Text(name)), ("n", Value.Integer(n)), ("t", Value.Instant(Logs.Origin + seconds * Logs.Second))), 1);
        var link = new Sattle.Reference.RowInput("pods", Logs.Row(("uid", Value.Text("u0")), ("name", Value.Text("Web")), ("g", Value.Text("g0"))), 1);
        // the advance that closes the window takes every place too: a merge of windows already stored, of rows not yet closed, and rows that revise a closed window
        Sattle.Reference.Input[] parts = [Load("u0", null, byUid, 10), Load(null, "Web", byName, 20), link, new Sattle.Reference.AdvanceInput(Logs.Origin + 90 * Logs.Second), .. secondByUid is { } more ? new[] { Load("u0", null, more, 15) } : []];
        static IEnumerable<int[]> Permutations(int[] xs) => xs.Length <= 1 ? [xs] : xs.SelectMany(x => Permutations([.. xs.Where(y => y != x)]).Select(rest => (int[])[x, .. rest]));
        var orders = Permutations([.. Enumerable.Range(0, parts.Length)]).ToList();
        var seen = new HashSet<string>();
        foreach (var order in orders)
            foreach (var chunking in new[] { Chunking.OneByOne, Chunking.Largest, Chunking.Reversed })
            {
                Sattle.Reference.Input[] log = [.. order.Select(i => parts[i]), new Sattle.Reference.AdvanceInput(Logs.Origin + 120 * Logs.Second)];
                string Load_(Sattle.Reference.GraphFacts g) => string.Join(",", g.Telemetry.Where(x => x.Key.Property.EndsWith("prop:load", StringComparison.Ordinal)).Select(x => Sattle.Core.Norm.Decimal(x.Value)));
                var engine = Load_(EngineRun.Final(w.Catalog, log, chunking).Graphs["master"]);
                var reference = Load_(Sattle.Reference.ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log).Graphs["master"]);
                Assert.True(engine == reference, $"order {string.Join(" ", order)}, {chunking}: engine [{engine}], reference [{reference}]");
                seen.Add(engine);
            }
        Assert.Equal([expected ?? ""], seen);
    }

    /// <summary>T-B3 (Theorem 6.2 premise B3, Proposition 1.8): pods named by uid and by name, links that join random sets of them into classes, the first uses and the links in 20 random orders, each cut three ways. Every key resolves to the smallest own id of its class, every forwarding record points to a smaller id, and the state is the same for every order and cut. That a group by the merged identity counts one group over both keys is the world "a view grouped by a pod that a link merges".</summary>
    [Theory, Trait("Category", "Differential")]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Identity_is_independent_of_key_arrival_order_on_generated_inputs(int seed)
    {
        var w = Worlds.All.Single(x => x.Name == "link");
        var rng = new Random(seed);
        var (uids, names) = (rng.Next(3, 21), rng.Next(2, 21));
        var rows = new List<Sattle.Reference.RowInput>();
        for (var u = 0; u < uids; u++) rows.Add(new("pods", Logs.Row(("uid", Value.Text("u" + u)), ("name", Value.Text("P" + rng.Next(names))), ("g", Value.Text("g0"))), 1));
        for (var n = 0; n < names; n++) rows.Add(new("flows", Logs.Row(("src", Value.Text("p" + n)), ("dst", Value.Text("p" + rng.Next(names)))), 1));
        var links = rows.Where(r => r.Source == "pods").Select(r => new Sattle.Reference.LinkFact(
            new Sattle.Reference.IdentityKey("d:w/identity:podUid", [((TextValue)r.Fields["uid"]).S]), new Sattle.Reference.IdentityKey("d:w/identity:podName", [((TextValue)r.Fields["name"]).S.ToLowerInvariant()]))).ToList();
        var expected = new Sattle.Reference.Identities("lab", links);
        var digests = new HashSet<string>();
        for (var order = 0; order < 20; order++)
        {
            var log = rows.OrderBy(_ => rng.Next()).Cast<Sattle.Reference.Input>().ToList();
            foreach (var cut in new[] { Chunking.OneByOne, Chunking.Largest, Chunking.Random(seed * 100 + order) })
            {
                using var engine = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single());
                var seq = 0L; var batch = new List<Sattle.Reference.RowInput>();
                for (var i = 0; i < log.Count; i++)
                {
                    batch.Add((Sattle.Reference.RowInput)log[i]);
                    if (i == log.Count - 1 || ((Sattle.Reference.RowInput)log[i + 1]).Source != batch[0].Source || cut.SplitAfter(i)) { EngineRun.Ingest(engine, [.. batch], ++seq); batch.Clear(); }
                }
                foreach (var (domain, id, components) in engine.Identities.Bindings())
                    Assert.Equal(expected.IdOf(new Sattle.Reference.IdentityKey(domain.Id, [.. components])), id);          // the smallest own id of the class
                foreach (var (loser, survivor) in engine.Identities.Forwarding)
                    Assert.True(string.CompareOrdinal(engine.Interner.Get(survivor), engine.Interner.Get(loser)) < 0, "a forwarding record points to a greater id");
                digests.Add(engine.StateDigest());
            }
        }
        Assert.Single(digests);
    }

    /// <summary>The support of windows written under two ids of one class is engine state: a snapshot taken after the merge restores it, and both engines continue to the same state (premise S1 for the new state).</summary>
    [Fact, Trait("Category", "Differential")]
    public void A_snapshot_keeps_the_windows_written_under_two_ids()
    {
        var w = Worlds.LinkedLoads;
        static Sattle.Reference.RowInput Load(string? uid, string? name, long n, long seconds, long weight = 1) => new("loads", Logs.Row(("uid", uid is null ? Value.Null : Value.Text(uid)), ("name", name is null ? Value.Null : Value.Text(name)), ("n", Value.Integer(n)), ("t", Value.Instant(Logs.Origin + seconds * Logs.Second))), weight);
        var link = new Sattle.Reference.RowInput("pods", Logs.Row(("uid", Value.Text("u0")), ("name", Value.Text("Web")), ("g", Value.Text("g0"))), 1);
        var space = w.Catalog.Spaces.Keys.Single();
        using var original = new Engine(w.Catalog, space);
        EngineRun.Ingest(original, [Load("u0", null, 1, 10)], 1); EngineRun.Ingest(original, [Load(null, "Web", 2, 20)], 2); EngineRun.Ingest(original, [link], 3);
        original.Advance(Logs.Origin + 90 * Logs.Second, 4);
        Assert.Equal(1, original.Graphs.Sum(g => g.Out.WindowsOfTwoIds));
        var snapshot = StateSnapshot.Save(original.StateRootOf());
        using var restored = new Engine(w.Catalog, space);
        var root = restored.StateRootOf(); StateSnapshot.Load(snapshot, root); restored.AdoptState(root);
        Assert.Equal(original.StateDigest(), restored.StateDigest());
        foreach (var e in new[] { original, restored })
        {
            EngineRun.Ingest(e, [Load("u0", null, 1, 10, -1), Load("u0", null, 5, 15)], 5);          // a revision of the uid's row after the merge
            e.Advance(Logs.Origin + 150 * Logs.Second, 6);
        }
        Assert.Equal(original.StateDigest(), restored.StateDigest());
        string LoadOf(Engine e) => string.Join(",", EngineRun.FactsOf(e).Graphs["master"].Telemetry.Where(x => x.Key.Property.EndsWith("prop:load", StringComparison.Ordinal)).Select(x => Sattle.Core.Norm.Decimal(x.Value)));
        Assert.Equal("7", LoadOf(restored));
    }

    static void Agrees(string world, int seeds, int length, bool reordered)
    {
        var report = Differential.Check(Worlds.All.Single(w => w.Name == world), seeds, length, reordered);
        Assert.True(report is null, report);
    }
}
