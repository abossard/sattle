using Sattle.Runtime;
using Sattle.Core;
namespace Sattle.Differential;

/// <summary>T-INC and batch invariance per operator kind (Theorem 6.1, Theorem 6.2, obligation O6.1): on generated logs the engine's incremental result equals the reference evaluator's batch result after every batch, for every way of cutting the log. A failure prints the smallest log found.</summary>
public class DifferentialTests
{
    public static TheoryData<string> WorldNames => [.. Worlds.All.Select(w => w.Name)];

    [Theory, MemberData(nameof(WorldNames))]
    public void The_engine_equals_the_reference_however_the_log_is_cut(string world) => Agrees(world, seeds: 12, length: 14, reordered: false);

    [Theory, MemberData(nameof(WorldNames))]
    public void The_engine_equals_the_reference_when_the_rows_of_a_batch_are_reordered(string world) => Agrees(world, seeds: 12, length: 14, reordered: true);

    [Theory, MemberData(nameof(WorldNames)), Trait("Category", "Slow")]
    public void The_engine_equals_the_reference_on_many_logs_however_they_are_cut(string world) => Agrees(world, seeds: 150, length: 20, reordered: false);

    /// <summary>The comparison is not empty: on the default seeds the reference result of every world holds facts of every graph, and the facts that show its operator at work (a window, a state that switched on, a reach of depth two, a merged identity).</summary>
    [Theory, MemberData(nameof(WorldNames))]
    public void The_generated_logs_exercise_the_world(string world)
    {
        var w = Worlds.All.Single(x => x.Name == world);
        var results = Enumerable.Range(0, 12).Select(seed => Sattle.Reference.ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), w.Log(seed, 14), new Sattle.Reference.Settings(WindowRetentionNs: w.RetentionNs))).ToArray();
        foreach (var graph in results[0].Graphs.Keys) Assert.Contains(results, r => r.Graphs[graph].Lines().Length > 0);
        var lines = results.SelectMany(r => r.Graphs.Values.SelectMany(g => g.Lines())).ToArray();
        Assert.All(w.Witnesses, witness => Assert.Contains(lines, l => l.Contains(witness, StringComparison.Ordinal)));
    }

    /// <summary>The worlds of a join on the start of a traverse check the plan as run: the engine joins the start rows before the walk.</summary>
    [Theory]
    [InlineData("join on the start of a traverse")]
    [InlineData("join on the start of a traverse on its right")]
    public void The_engine_joins_the_start_rows_before_the_walk(string world)
    {
        var catalog = EngineRun.Load(Worlds.All.Single(w => w.Name == world).Files);
        using var engine = new Engine(catalog, catalog.Spaces.Keys.Single());
        var ops = engine.Graphs.Single(g => !g.IsMaster).Plan.Ops.Select(o => o.Typed.Kind).ToList();
        Assert.True(ops.IndexOf("Join") >= 0 && ops.IndexOf("Join") < ops.IndexOf("Traverse"), string.Join(", ", ops));
    }

    /// <summary>A traced graph runs its plan as written, the walk before the join, and gives the outputs of the same graph untraced, which runs the join below the walk.</summary>
    [Theory]
    [InlineData("join on the start of a traverse")]
    [InlineData("join on the start of a traverse on its right")]
    public void A_traced_run_gives_the_outputs_of_an_untraced_one(string world)
    {
        var w = Worlds.All.Single(x => x.Name == world);
        var traced = new EngineOptions { TraceGraphs = w.Catalog.Spaces.Values.Single().Graphs.Select(g => g.Name).ToHashSet() };
        using (var engine = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single(), traced))
        {
            var ops = engine.Graphs.Single(g => !g.IsMaster).Plan.Ops.Select(o => o.Typed.Kind).ToList();
            Assert.True(ops.IndexOf("Traverse") >= 0 && ops.IndexOf("Traverse") < ops.IndexOf("Join"), string.Join(", ", ops));
        }
        foreach (var seed in Enumerable.Range(0, 8))
        {
            var log = w.Log(seed, 24);
            foreach (var cut in new[] { Chunking.OneByOne, Chunking.Largest })
            {
                var (want, got) = (EngineRun.Final(w.Catalog, log, cut).Graphs, EngineRun.Final(w.Catalog, log, cut, traced).Graphs);
                foreach (var (graph, facts) in want) Assert.True(facts.Lines().SequenceEqual(got[graph].Lines()), $"seed {seed}, {cut}, graph {graph}");
            }
        }
    }

    /// <summary>T-RETAIN: retention keeps exactly the rows younger than the age at the frontier (Proposition 5.9), on logs with rows that are expired when they arrive, retractions of held rows and frontier advances, however the log is cut.</summary>
    [Fact]
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

    /// <summary>T-MERGE-TELE (Law 7.5 item 3): one window of an instance written by a group over its uid and a group over its name, with the link that merges the two identities before both rows, between them or after both, in every order and batching. The window is the same in every case and equals the reference: the combination of the two values by the property's rule, where a rule that does not combine two different values leaves no value.</summary>
    [Theory]
    [InlineData("delta sum", 1, 2, "3")] [InlineData("delta sum", 2, 2, "4")]
    [InlineData("gauge min", 1, 2, "1")] [InlineData("gauge max", 1, 2, "2")] [InlineData("gauge max", 2, 2, "2")]
    [InlineData("gauge mean", 1, 2, null)] [InlineData("gauge mean", 2, 2, "2")]                                   // a mean stored as one number: only equal values combine
    [InlineData("gauge last", 1, 2, null)] [InlineData("gauge last", 2, 2, "2")]                                   // a last without its order: the same
    [InlineData("gauge mean", 1, 2, "1.5", "avg", "Decimal")] [InlineData("gauge mean", 2, 2, "2", "avg", "Decimal")]       // a mean with its sum and count
    [InlineData("gauge last", 1, 2, "2", "arg_max")] [InlineData("gauge last", 3, 2, "2", "arg_max")]                         // a last with its order: the name's row is later
    [InlineData("gauge mean", 1, 4, "2.3333333333333333333333333333", "avg", "Decimal", 2L)]                                  // (1 + 2 + 4) / 3, not the mean 2.75 of the two means
    [InlineData("delta sum", 1, 4, "7", "sum", "Integer", 2L)]
    public void A_merge_combines_windows_as_if_written_under_the_survivor(string rule, long byUid, long byName, string? expected, string aggregate = "sum", string kind = "Integer", long? secondByUid = null)
    {
        var w = Worlds.LinkedLoadsUnder(rule, aggregate, kind);
        static Sattle.Reference.RowInput Load(string? uid, string? name, long n, long seconds) => new("loads", Logs.Row(("uid", uid is null ? Value.Null : Value.Text(uid)), ("name", name is null ? Value.Null : Value.Text(name)), ("n", Value.Integer(n)), ("t", Value.Instant(Logs.Origin + seconds * Logs.Second))), 1);
        var link = new Sattle.Reference.RowInput("instances", Logs.Row(("uid", Value.Text("u0")), ("name", Value.Text("Web")), ("g", Value.Text("g0"))), 1);
        // the advance that closes the window takes every place too: a merge of windows already stored, of rows not yet closed, and rows that revise a closed window
        Sattle.Reference.Input[] parts = [Load("u0", null, byUid, 10), Load(null, "Web", byName, 20), link, new Sattle.Reference.AdvanceInput(Logs.Origin + 90 * Logs.Second), .. secondByUid is { } more ? new[] { Load("u0", null, more, 15) } : []];
        static IEnumerable<int[]> Permutations(int[] xs) => xs.Length <= 1 ? [xs] : xs.SelectMany(x => Permutations([.. xs.Where(y => y != x)]).Select(rest => (int[])[x, .. rest]));
        var orders = Permutations([.. Enumerable.Range(0, parts.Length)]).ToList();
        var seen = new HashSet<string>();
        foreach (var order in orders)
            foreach (var chunking in new[] { Chunking.OneByOne, Chunking.Largest, Chunking.Reversed })
            {
                Sattle.Reference.Input[] log = [.. order.Select(i => parts[i]), new Sattle.Reference.AdvanceInput(Logs.Origin + 120 * Logs.Second)];
                string Load_(Sattle.Reference.GraphFacts g) => string.Join(",", g.Windows.Where(x => x.Key.Property.EndsWith("prop:load", StringComparison.Ordinal)).Select(x => Sattle.Core.Norm.Decimal(x.Value)));
                var engine = Load_(EngineRun.Final(w.Catalog, log, chunking).Graphs["master"]);
                var reference = Load_(Sattle.Reference.ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log).Graphs["master"]);
                Assert.True(engine == reference, $"order {string.Join(" ", order)}, {chunking}: engine [{engine}], reference [{reference}]");
                seen.Add(engine);
            }
        Assert.Equal([expected ?? ""], seen);
    }

    /// <summary>#159 G1 (Ch. 2, Def. 2.5): the class of a key is a function of the links present now. A link whose rows are all retracted is withdrawn; the class may split, and the part without the old id gets the smallest id among its keys. What was written under a key of that part (rows, relationships at it, windows) moves to the part's id.
    /// A change is `+A=Web` (a row linking uid A to the name Web, `/g1` for a second row of the same link), `-A=Web` its retraction, `~web>api` a call, `A:1` or `Web:2` a load by uid or by name at 10 s (`-Web:2` retracts it), `@90` an advance. A, B are the uids of the smallest and the next own id, smaller than the names' (checked); Y, Z the uids of the largest, larger than the names' (checked). `splits`: the withdrawal changes the ids (else another link holds the class).</summary>
    [Theory]
    [InlineData("+A=Web +B=Web ~web>api A:1 Web:2 B:4 @90 -A=Web @150", true)]                // a chain A-web-B split at A: web and B, its relationship and their windows move to the smaller of their ids
    [InlineData("+A=Web +B=Web ~web>api A:1 Web:2 B:4 @90 -A=Web -Web:2 @150", true)]         // and a row written before the split is retracted after it
    [InlineData("+A=Web +B=Web A:1 Web:2 -A=Web @90", true)]                                    // split before the window closes
    [InlineData("+A=Web +B=Web @30 -A=Web Web:2 A:1 @90", true)]                                // rows after the split
    [InlineData("+Y=Web +Z=Web/g1 ~web>api -Y=Web -Z=Web/g1 @90", false)]                       // the name's id names the class; both rows that disagreed on its properties leave in one batch, so the conflict ends (no key is left to move)
    [InlineData("+A=Web Web:2 A:1 @90 -A=Web @150", true)]                                      // the last link of a class of two
    [InlineData("+A=Web +A=api +B=Web +B=api ~web>api A:1 Web:2 @90 -A=Web @150", false)]      // a link on a cycle: no split
    [InlineData("+A=Web +A=Web/g1 ~web>api Web:2 @90 -A=Web @150", false)]                      // another row still holds the link
    [InlineData("+A=Web ~web>api Web:2 @90 -A=Web +A=Web @150", false)]                          // withdrawn and asserted again
    public void A_withdrawn_link_splits_a_class(string changes, bool splits)
    {
        var w = Worlds.All.Single(x => x.Name.EndsWith("(a link withdrawn)", StringComparison.Ordinal));
        static string Own(string domain, string key) => Sattle.Reference.Identities.Allocate("lab", new Sattle.Reference.IdentityKey("d:w/identity:" + domain, [key]));
        var uids = Enumerable.Range(0, 10).Select(i => "u" + i).OrderBy(u => Own("instanceUid", u), StringComparer.Ordinal).ToArray();
        var (a, b, y, z) = (uids[0], uids[1], uids[^2], uids[^1]);
        Assert.True(string.CompareOrdinal(Own("instanceUid", a), Own("instanceName", "web")) < 0 && string.CompareOrdinal(Own("instanceUid", a), Own("instanceName", "api")) < 0, "A is the id of the class");
        Assert.True(string.CompareOrdinal(Own("instanceName", "web"), Own("instanceUid", y)) < 0 && string.CompareOrdinal(Own("instanceName", "api"), Own("instanceUid", y)) < 0, "Y and Z are not the id of a class with a name");
        string U(string x) => x switch { "A" => a, "B" => b, "Y" => y, "Z" => z, _ => x };
        Sattle.Reference.Input Change(string c)
        {
            if (c[0] == '@') return new Sattle.Reference.AdvanceInput(Logs.Origin + long.Parse(c[1..]) * Logs.Second);
            var weight = c[0] == '-' ? -1 : 1; var body = c[0] is '+' or '-' ? c[1..] : c;
            if (body.Split('=') is [var uid, var rest])
            {
                var (name, g) = rest.Split('/') is [var n, var gg] ? (n, gg) : (rest, "g0");
                return new Sattle.Reference.RowInput("instances", Logs.Row(("uid", Value.Text(U(uid))), ("name", Value.Text(name)), ("g", Value.Text(g))), weight);
            }
            if (body[0] == '~') return new Sattle.Reference.RowInput("flows", Logs.Row(("src", Value.Text(body[1..].Split('>')[0])), ("dst", Value.Text(body.Split('>')[1]))), weight);
            var (key, load) = (body.Split(':')[0], long.Parse(body.Split(':')[1]));
            var byUid = key is "A" or "B" or "Y" or "Z";
            return new Sattle.Reference.RowInput("loads", Logs.Row(("uid", byUid ? Value.Text(U(key)) : Value.Null), ("name", byUid ? Value.Null : Value.Text(key)), ("n", Value.Integer(load)), ("t", Value.Instant(Logs.Origin + 10 * Logs.Second))), weight);
        }
        var log = changes.Split(' ').Select(Change).ToArray();
        var kept = changes.Split(' ').Where(c => !(c[0] == '-' && c.Contains('='))).Select(Change).ToArray();
        string Ids(Sattle.Reference.Input[] l) => string.Join(" ", Sattle.Reference.ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), l).Graphs.Values.SelectMany(g => g.Entities.Keys.Concat(g.Rels.Keys)));
        Assert.Equal(splits, Ids(log) != Ids(kept));                                          // the case reaches what it says: the withdrawal changes the ids
        foreach (var reordered in new[] { false, true })
            Assert.Null(Differential.FirstMismatch(w, log, 0, reordered)?.ToString());
    }

    /// <summary>T-B3 (Theorem 6.2 premise B3, Proposition 1.8): instances named by uid and by name, links that join random sets of them into classes, the first uses and the links in 20 random orders, each cut three ways. A uid named twice links two names, so a link can join two classes that already hold several keys. Every key resolves to the smallest own id of its class, every forwarding record points to a smaller id, and the state is the same for every order and cut. On the first order, the views that group by the merged pod (a count, and a sum through a union and joins) end equal to the reference under the three cuts: one group over all keys of a class.</summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Identity_is_independent_of_key_arrival_order_on_generated_inputs(int seed)
    {
        var w = Worlds.All.Single(x => x.Name == "link");
        var rng = new Random(seed);
        var (uids, names) = (rng.Next(3, 21), rng.Next(2, 21));
        var rows = new List<Sattle.Reference.RowInput>();
        for (var u = 0; u < uids; u++)
            foreach (var name in rng.Next(3) == 0 ? new[] { rng.Next(names), rng.Next(names) }.Distinct() : [rng.Next(names)])
                rows.Add(new("instances", Logs.Row(("uid", Value.Text("u" + u)), ("name", Value.Text("P" + name)), ("g", Value.Text("g0"))), 1));
        for (var n = 0; n < names; n++) rows.Add(new("flows", Logs.Row(("src", Value.Text("p" + n)), ("dst", Value.Text("p" + rng.Next(names)))), 1));
        var links = rows.Where(r => r.Source == "instances").Select(r => new Sattle.Reference.LinkFact(
            new Sattle.Reference.IdentityKey("d:w/identity:instanceUid", [((TextValue)r.Fields["uid"]).S]), new Sattle.Reference.IdentityKey("d:w/identity:instanceName", [((TextValue)r.Fields["name"]).S.ToLowerInvariant()]))).ToList();
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
                foreach (var (rep, members) in engine.Identities.Classes)
                    Assert.All(members, m => Assert.True(string.CompareOrdinal(engine.Interner.Get(rep), engine.Interner.Get(m)) <= 0, "a class is named by an id greater than one of its members"));
                digests.Add(engine.StateDigest());
            }
            if (order == 0)
                foreach (var view in new[] { Worlds.LinkedView, Worlds.LinkedUnionJoin })
                {
                    var want = Sattle.Reference.ReferenceEvaluator.Evaluate(view.Catalog, view.Catalog.Spaces.Keys.Single(), log).Graphs;
                    foreach (var cut in new[] { Chunking.OneByOne, Chunking.Largest, Chunking.Random(seed) })
                    {
                        var got = EngineRun.Final(view.Catalog, log, cut).Graphs;
                        foreach (var (graph, facts) in want) Assert.True(facts.Lines().SequenceEqual(got[graph].Lines()), $"{view.Name}, {cut}, graph {graph}\nreference only:\n{string.Join("\n", facts.Lines().Except(got[graph].Lines()))}\nengine only:\n{string.Join("\n", got[graph].Lines().Except(facts.Lines()))}");
                    }
                }
        }
        Assert.Single(digests);
    }

    /// <summary>Code review 2, defect 22: an entity that a link names by another id keeps its lineage, since what was known about the id that lost is then known about the survivor. Before, the survivor of a merge had no lineage.</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void An_entity_that_a_link_forwards_to_another_id_keeps_its_lineage(bool rowByRow)
    {
        var w = Worlds.All.Single(x => x.Name == "link");
        using var engine = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single());
        var rows = new[] { ("u0", "Web"), ("u1", "api"), ("u2", "DB"), ("u3", "cache") }
            .Select(p => new Sattle.Reference.RowInput("instances", Logs.Row(("uid", Value.Text(p.Item1)), ("name", Value.Text(p.Item2)), ("g", Value.Text("g0"))), 1)).ToList();
        if (rowByRow) { var seq = 0L; foreach (var r in rows) EngineRun.Ingest(engine, [r], ++seq); } else EngineRun.Ingest(engine, [.. rows], 1);
        Assert.NotEmpty(engine.Identities.Classes);
        Assert.NotEmpty(engine.Master.State.Entities);
        var lineage = new LineageReader(engine);
        Assert.All(engine.Master.State.Entities, e => Assert.Equal("observed", lineage.Forward("master", e.Id)["rule"]?.GetValue<string>()));
    }

    /// <summary>PROTOCOL 3.22: the state digest does not depend on whether the link comes before or after the rows of the two ids (code review 2, defect 22).</summary>
    [Fact]
    public void The_state_digest_does_not_depend_on_the_order_of_the_link_and_the_rows()
    {
        static Sattle.Reference.RowInput Load(string? uid, string? name, long n, long seconds) => new("loads", Logs.Row(("uid", uid is null ? Value.Null : Value.Text(uid)), ("name", name is null ? Value.Null : Value.Text(name)), ("n", Value.Integer(n)), ("t", Value.Instant(Logs.Origin + seconds * Logs.Second))), 1);
        var link = new Sattle.Reference.RowInput("instances", Logs.Row(("uid", Value.Text("u0")), ("name", Value.Text("Web")), ("g", Value.Text("g0"))), 1);
        string Digest(params Sattle.Reference.Input[] log)
        {
            using var engine = new Engine(Worlds.LinkedLoads.Catalog, Worlds.LinkedLoads.Catalog.Spaces.Keys.Single());
            long seq = 0;
            foreach (var i in log)
                if (i is Sattle.Reference.AdvanceInput a) engine.Advance(a.Frontier, ++seq); else EngineRun.Ingest(engine, [(Sattle.Reference.RowInput)i], ++seq);
            return engine.StateDigest();
        }
        var rows = new Sattle.Reference.Input[] { Load(null, "Web", 5, 10), Load(null, "web", 7, 20), Load("u0", null, 3, 30), new Sattle.Reference.AdvanceInput(Logs.Origin + 90 * Logs.Second) };
        Assert.Equal(Digest([.. rows, link]), Digest([link, .. rows]));
    }

    /// <summary>The support of windows written under two ids of one class is engine state: a snapshot taken after the merge restores it, and both engines continue to the same state (premise S1 for the new state).</summary>
    [Fact]
    public void A_snapshot_keeps_the_windows_written_under_two_ids()
    {
        var w = Worlds.LinkedLoads;
        static Sattle.Reference.RowInput Load(string? uid, string? name, long n, long seconds, long weight = 1) => new("loads", Logs.Row(("uid", uid is null ? Value.Null : Value.Text(uid)), ("name", name is null ? Value.Null : Value.Text(name)), ("n", Value.Integer(n)), ("t", Value.Instant(Logs.Origin + seconds * Logs.Second))), weight);
        var link = new Sattle.Reference.RowInput("instances", Logs.Row(("uid", Value.Text("u0")), ("name", Value.Text("Web")), ("g", Value.Text("g0"))), 1);
        var space = w.Catalog.Spaces.Keys.Single();
        using var original = new Engine(w.Catalog, space);
        EngineRun.Ingest(original, [Load("u0", null, 1, 10)], 1); EngineRun.Ingest(original, [Load(null, "Web", 2, 20)], 2); EngineRun.Ingest(original, [link], 3);
        original.Advance(Logs.Origin + 90 * Logs.Second, 4);
        Assert.Equal(1, original.Graphs.Sum(g => g.Out.Counts.WindowsOfTwoIds));
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
        string LoadOf(Engine e) => string.Join(",", EngineRun.FactsOf(e).Graphs["master"].Windows.Where(x => x.Key.Property.EndsWith("prop:load", StringComparison.Ordinal)).Select(x => Sattle.Core.Norm.Decimal(x.Value)));
        Assert.Equal("7", LoadOf(restored));
    }

    /// <summary>#151 D4, world `threshold with parameters of the row`: the policy of a key comes from its policy rows, here the item, `above` its `n` and `enterAfter` n - 1.
    /// A window is folded with the policy row of greatest `since` that entered before the window closed, so a revised item holds from the windows that close after it
    /// entered; the withdrawn row no longer holds anywhere (D6), so a window that closed before the revision has the policy of no row. The engine gives the
    /// reference's state under every cut.</summary>
    [Theory]
    [InlineData("n = 2: above 2 for one window, so a window of 2 turns the key on", new[] { 2L }, false, "on")]
    [InlineData("n = 3: above 3 for two windows, so one window of 2 leaves it off", new[] { 3L }, false, "off")]
    [InlineData("revised to n = 2 before the window from 0 s is folded: the window is folded with the newer policy", new[] { 3L, 2L }, false, "on")]
    [InlineData("revised to n = 2 after the window from 0 s was folded with n = 3: that window closed before the new row entered and the old row is gone, so the key has no state at 300 s, and a window of 2 from 240 s turns it on", new[] { 3L, 2L }, true, "on")]
    public void A_policy_holds_from_the_windows_that_close_after_it_entered(string what, long[] ns, bool afterTheFold, string expected)
    {
        var w = Worlds.RowParameters;
        static Sattle.Reference.Input At(long seconds) => new Sattle.Reference.AdvanceInput(Logs.Origin + seconds * Logs.Second);
        Sattle.Reference.Input[] revise = ns.Length == 1 ? [] : [PlainRow("a", -1, "k0", "g0", ns[0], 0), PlainRow("a", 1, "k0", "g0", ns[1], 0)];
        Sattle.Reference.Input[] log = afterTheFold
            ? [PlainRow("a", 1, "k0", "g0", ns[0], 0), PlainRow("b", 1, "k0", "g0", 2, 10), At(60), At(240), .. revise, At(300), PlainRow("b", 1, "k0", "g0", 2, 250), At(480)]
            : [PlainRow("a", 1, "k0", "g0", ns[0], 0), PlainRow("b", 1, "k0", "g0", 2, 10), At(60), .. revise, At(240)];
        static string States(Sattle.Reference.SpaceFacts f) => string.Join(" ", f.Graphs["view"].Entities.Values.SelectMany(e => e.Props).Where(kv => kv.Key.EndsWith("prop:state", StringComparison.Ordinal)).Select(kv => kv.Value is TextValue t ? t.S : "-").Order(StringComparer.Ordinal));
        Assert.True(States(Sattle.Reference.ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log)) == expected, what);
        if (afterTheFold)
            Assert.Equal("", States(Sattle.Reference.ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log[..^2])));       // at 300 s: no row holds a policy for the window from 0 s
        foreach (var chunking in new[] { Chunking.OneByOne, Chunking.Largest, Chunking.Reversed })
        {
            var engine = States(EngineRun.Final(w.Catalog, log, chunking));
            Assert.True(engine == expected, $"{what}, {chunking.Name}: engine {engine}, expected {expected}");
        }
    }

    /// <summary>Def. 6.6, world `a policy that an advance makes`: the hits of item k0 at 10 s make the node `Busy` of the window from 0 s when the clock reaches its end
    /// (60 s), at the advance to `advance`; it gives the key's policy row. The advance moves the clock and adds the row, so the row is stamped with the new clock, not
    /// with 30 s before it, and the transitions from that window on (it closes at 240 s) carry the stamp, in the reference and in the engine under every cut.</summary>
    [Theory]
    [InlineData(60)] [InlineData(90)]
    public void A_policy_row_that_an_advance_adds_is_stamped_with_the_new_clock(long advance)
    {
        var w = Worlds.AdvancePolicy;
        static Sattle.Reference.Input At(long seconds) => new Sattle.Reference.AdvanceInput(Logs.Origin + seconds * Logs.Second);
        Sattle.Reference.Input[] log = [PlainRow("a", 1, "k0", "g0", 0, 0), PlainRow("b", 1, "k0", "g0", 3, 10), At(30), At(advance), At(300)];
        static string Since(Sattle.Reference.SpaceFacts f) => string.Join(" ", f.Graphs["view"].Entities.Values.SelectMany(e => e.Props).Where(kv => kv.Key.EndsWith("prop:since", StringComparison.Ordinal)).Select(kv => kv.Value is InstantValue i ? Norm.Instant(i.Ns) : "-").Distinct().Order(StringComparer.Ordinal));
        var expected = Norm.Instant(Logs.Origin + advance * Logs.Second);
        Assert.Equal(expected, Since(Sattle.Reference.ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log)));
        foreach (var chunking in new[] { Chunking.OneByOne, Chunking.Largest, Chunking.Reversed })
            Assert.True(Since(EngineRun.Final(w.Catalog, log, chunking)) == expected, $"{chunking.Name}: engine {Since(EngineRun.Final(w.Catalog, log, chunking))}, expected {expected}");
    }

    static void Agrees(string world, int seeds, int length, bool reordered)
    {
        var report = Differential.Check(Worlds.All.Single(w => w.Name == world), seeds, length, reordered);
        Assert.True(report is null, report);
    }

    // ---- facts of a world that are no comparison with the reference ----

    static Sattle.Reference.RowInput PlainRow(string source, long weight, string k, string g, long n, long seconds, decimal? d = null) =>
        new(source, Logs.Row(("k", Value.Text(k)), ("g", Value.Text(g)), ("n", Value.Integer(n)), ("d", d is { } q ? Value.Decimal(q) : Value.Null), ("t", Value.Instant(Logs.Origin + seconds * Logs.Second))), weight);

    /// <summary>#159: the percentile of nearest rank of the rows of one group, each `+v` or `-v` (an integer `n` for p0 to p100, a decimal `d` for median and high, p 50 and 99.9), under every cut, equal to the reference. A value at rank that goes, ties at rank, one row, a group whose rows all went and one with no value; a decimal is its number whatever digits came first.</summary>
    [Theory]
    [InlineData("+5", "p50", "5")]                                                       // one row
    [InlineData("+3 +1 +4 +2", "p0", "1")] [InlineData("+3 +1 +4 +2", "p50", "2")] [InlineData("+3 +1 +4 +2", "p95", "4")] [InlineData("+3 +1 +4 +2", "p100", "4")]
    [InlineData("+1 +2 +3 +4 -2", "p50", "3")]                                         // the value at rank goes: 1 3 4, rank 2
    [InlineData("+1 +2 +2 +2 +5", "p50", "2")] [InlineData("+2 +5 +5 +1 +5", "p95", "5")] [InlineData("+2 +2 +3 -2", "p50", "2")]       // ties at rank
    [InlineData("+3 -3", "p50", null)]                                                  // the group's rows all went
    [InlineData("+null", "median", null)]                                               // a group with no value
    [InlineData("+2.50 +2.5 -2.50", "median", "2.5")] [InlineData("+2.5 +2.50 -2.5", "median", "2.5")] [InlineData("+2.50", "median", "2.5")]
    [InlineData("+1 +2.50 +0", "median", "1")] [InlineData("+1 +2.50 +0", "high", "2.5")]
    public void A_percentile_is_the_value_of_nearest_rank(string changes, string property, string? expected)
    {
        var w = Worlds.Percentile;
        var dec = property is "median" or "high";
        Sattle.Reference.Input[] log = [.. changes.Split(' ').Select(c => (Sattle.Reference.Input)(dec
            ? PlainRow("a", c[0] == '+' ? 1 : -1, "k0", "g0", 0, 0, c[1..] == "null" ? null : decimal.Parse(c[1..], System.Globalization.CultureInfo.InvariantCulture))
            : PlainRow("a", c[0] == '+' ? 1 : -1, "k0", "g0", long.Parse(c[1..], System.Globalization.CultureInfo.InvariantCulture), 0)))];
        static Value? Of(Sattle.Reference.SpaceFacts f, string name) => f.Graphs["master"].Entities.Values.SelectMany(e => e.Props).Where(kv => kv.Key.EndsWith("prop:" + name, StringComparison.Ordinal)).Select(kv => kv.Value).SingleOrDefault();
        static string? Text(Value? v) => v switch { DecimalValue d => d.Q.ToString(System.Globalization.CultureInfo.InvariantCulture), IntegerValue i => i.N.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => null };
        var reference = Of(Sattle.Reference.ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log), property);
        Assert.Equal(expected, Text(reference));
        foreach (var chunking in new[] { Chunking.OneByOne, Chunking.Largest, Chunking.Reversed })
        {
            var engine = Text(Of(EngineRun.Final(w.Catalog, log, chunking), property));
            Assert.True(engine == expected, $"{chunking.Name}: engine {engine}, expected {expected}");
        }
    }

    /// <summary>#161 (#159 G5): the greatest decimal `top` and the least `bottom` of one group are their number whatever digits came first: the six orders
    /// of `+2.50 +2.5 -2.50`, the same with the digits swapped, two rows, one row, and beside another value; under every cut, equal to the reference.</summary>
    [Theory]
    [InlineData("+2.50 +2.5 -2.50", "top")] [InlineData("+2.5 +2.50 -2.50", "top")] [InlineData("+2.50 -2.50 +2.5", "top")]
    [InlineData("+2.5 -2.50 +2.50", "top")] [InlineData("-2.50 +2.50 +2.5", "top")] [InlineData("-2.50 +2.5 +2.50", "top")]
    [InlineData("+2.5 +2.50 -2.5", "top")] [InlineData("+2.5 -2.5 +2.50", "top")]
    [InlineData("+2.50 +2.5", "top")] [InlineData("+2.5 +2.50", "top")] [InlineData("+2.50", "top")] [InlineData("+1 +2.50 +2.5 -2.50", "top")]
    [InlineData("+2.50 +2.5 -2.50", "bottom")] [InlineData("+2.5 +2.50 -2.50", "bottom")] [InlineData("-2.50 +2.50 +2.5", "bottom")]
    [InlineData("+2.50", "bottom")] [InlineData("+3 +2.50 +2.5 -2.50", "bottom")]
    public void A_decimal_extreme_is_its_number_whatever_digits_came_first(string changes, string property)
    {
        var w = Worlds.All.Single(x => x.Name == "group min and max");
        Sattle.Reference.Input[] log = [.. changes.Split(' ').Select(c => (Sattle.Reference.Input)PlainRow("a", c[0] == '+' ? 1 : -1, "k0", "g0", 0, 0, decimal.Parse(c[1..], System.Globalization.CultureInfo.InvariantCulture)))];
        Value? Of(Sattle.Reference.SpaceFacts f) => f.Graphs["master"].Entities.Values.SelectMany(e => e.Props).Where(kv => kv.Key.EndsWith("prop:" + property, StringComparison.Ordinal)).Select(kv => kv.Value).SingleOrDefault();
        static string? Text(Value? v) => v is DecimalValue d ? d.Q.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        Assert.Equal("2.5", Text(Of(Sattle.Reference.ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log))));
        foreach (var chunking in new[] { Chunking.OneByOne, Chunking.Largest, Chunking.Reversed })
        {
            var engine = Text(Of(EngineRun.Final(w.Catalog, log, chunking)));
            Assert.True(engine == "2.5", $"{chunking.Name}: engine {engine}");
        }
    }

    /// <summary>#159 G4: a sum and a mean are exact over the rows of one group, each `+v` or `-v` (an integer `n` for `v`, a decimal `d` for `w`); a sum the output type
    /// cannot hold is no value. Under every cut, equal to the reference: a row that goes after a sum left the range, digits beyond 28 that a decimal sum used to round away.</summary>
    [Theory]
    [InlineData("group sum", "+9223372036854775807 +1", "v", null)]
    [InlineData("group sum", "+9223372036854775807 +1 -1", "v", "9223372036854775807")]
    [InlineData("group sum", "+9223372036854775807 +9223372036854775807 -9223372036854775807", "v", "9223372036854775807")]
    [InlineData("group sum", "+-9223372036854775808 +-1 -+-1", "v", "-9223372036854775808")]
    [InlineData("group sum", "+100000000000000000000 +0.0000000001", "w", null)]
    [InlineData("group sum", "+100000000000000000000 +0.0000000001 -100000000000000000000", "w", "0.0000000001")]
    [InlineData("group sum", "+79228162514264337593543950335 +1", "w", null)]
    [InlineData("group sum", "+79228162514264337593543950335 +1 -1", "w", "79228162514264337593543950335")]
    [InlineData("group sum", "+1.50 +2.50", "w", "4")]
    [InlineData("group mean", "+100000000000000000000 +0.0000000001 -100000000000000000000", "w", "0.0000000001")]
    [InlineData("group mean", "+79228162514264337593543950335 +79228162514264337593543950335", "w", null)]
    [InlineData("group mean", "+9223372036854775807 +9223372036854775807", "v", "9223372036854775807")]
    public void A_sum_is_exact_or_no_value(string world, string changes, string property, string? expected)
    {
        var w = Worlds.All.Single(x => x.Name == world);
        var dec = property == "w";
        static (long Weight, string Value) Change(string c) => c.StartsWith("-+", StringComparison.Ordinal) ? (-1, c[2..]) : (c[0] == '+' ? 1 : -1, c[1..]);
        Sattle.Reference.Input[] log = [.. changes.Split(' ').Select(Change).Select(c => (Sattle.Reference.Input)(dec
            ? PlainRow("a", c.Weight, "k0", "g0", 0, 0, decimal.Parse(c.Value, System.Globalization.CultureInfo.InvariantCulture))
            : PlainRow("a", c.Weight, "k0", "g0", long.Parse(c.Value, System.Globalization.CultureInfo.InvariantCulture), 0)))];
        static Value? Of(Sattle.Reference.SpaceFacts f, string name) => f.Graphs["master"].Entities.Values.SelectMany(e => e.Props).Where(kv => kv.Key.EndsWith("prop:" + name, StringComparison.Ordinal)).Select(kv => kv.Value).SingleOrDefault();
        static string? Text(Value? v) => v switch { DecimalValue d => d.Q.ToString(System.Globalization.CultureInfo.InvariantCulture), IntegerValue i => i.N.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => null };
        var reference = Of(Sattle.Reference.ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log), property);
        Assert.Equal(expected, Text(reference));
        foreach (var chunking in new[] { Chunking.OneByOne, Chunking.Largest, Chunking.Reversed })
        {
            var engine = Text(Of(EngineRun.Final(w.Catalog, log, chunking), property));
            Assert.True(engine == expected, $"{chunking.Name}: engine {engine}, expected {expected}");
        }
    }

    /// <summary>#159 G2 (R1) and #151 D1: two items of one group give a fold by the group two rows for one window. The program is accepted. The window at 0 s
    /// (k0 above) turns the group on. The window at 60 s gets a row below from one item before the advance to 150 s, and a row above from the other after, both
    /// before the window is folded at 300 s: its observation is the set of both values (D1), whose max is not below, so the group stays on whichever came first,
    /// under every cut, as in the reference.</summary>
    [Theory]
    [InlineData("k0", 0, "k1", 3)]
    [InlineData("k1", 3, "k0", 0)]
    public void Two_rows_for_one_key_and_window_give_one_state(string first, long firstHits, string second, long secondHits)
    {
        var w = Worlds.SharedKeyThreshold;
        Sattle.Reference.Input[] log = [PlainRow("a", 1, "k0", "g0", 1, 0), PlainRow("a", 1, "k1", "g0", 1, 0), PlainRow("b", 1, "k0", "g0", 3, 10),
            PlainRow("b", 1, first, "g0", firstHits, 70), new Sattle.Reference.AdvanceInput(Logs.Origin + 150 * Logs.Second),
            PlainRow("b", 1, second, "g0", secondHits, 80), new Sattle.Reference.AdvanceInput(Logs.Origin + 300 * Logs.Second)];
        static string States(Sattle.Reference.SpaceFacts f) => string.Join(" ", f.Graphs["view"].Entities.Values.SelectMany(e => e.Props).Where(kv => kv.Key.EndsWith("prop:state", StringComparison.Ordinal)).Select(kv => kv.Value is TextValue t ? t.S : "-").Order(StringComparer.Ordinal));
        const string expected = "on";
        Assert.Equal(expected, States(Sattle.Reference.ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log)));
        foreach (var chunking in new[] { Chunking.OneByOne, Chunking.Largest, Chunking.Reversed })
        {
            var engine = States(EngineRun.Final(w.Catalog, log, chunking));
            Assert.True(engine == expected, $"{first} first, {chunking.Name}: engine {engine}, expected {expected}");
        }
    }

    /// <summary>#159 G3 (R1): an element is in a Series since its row arrived, at the clock it entered, not since an advance after it. Two runs with the same rows
    /// and the same clock, one with an advance between, give the same states, under every cut, and the reference agrees. The record of the element in the window
    /// from 60 s starts its key (#151 D3: a key starts at its first window that is not silent). Without a report of its source the windows after it are silent and
    /// make the key silent. With the source reported to 300 s, `fill and no data` fills the windows from 120 to 240 s with 0 because k0 has been in the series
    /// since 60 s, so they are observed and the key stays off; were k0 in the series only since the next advance (300 s in the direct run), they would be
    /// silent, three of them, and the key silent.</summary>
    [Theory]
    [InlineData("no data without a fill", 0L, "silent")]
    [InlineData("fill and no data", 0L, "silent")]
    [InlineData("fill and no data", 300L, "off")]
    public void An_element_is_in_a_series_since_its_row_arrived(string world, long reportedTo, string expected)
    {
        var w = Worlds.All.Single(x => x.Name == world);
        static Sattle.Reference.Input At(long seconds) => new Sattle.Reference.AdvanceInput(Logs.Origin + seconds * Logs.Second);
        Sattle.Reference.Input[] report = reportedTo > 0 ? [new Sattle.Reference.ProgressInput("b", Logs.Origin + reportedTo * Logs.Second)] : [];
        Sattle.Reference.Input[] between = [At(60), PlainRow("a", 1, "k0", "g0", 1, 0), PlainRow("b", 1, "k0", "g0", 0, 70), At(120), At(300), .. report, At(600)],
            direct = [At(60), PlainRow("a", 1, "k0", "g0", 1, 0), PlainRow("b", 1, "k0", "g0", 0, 70), At(300), .. report, At(600)];
        static string States(Sattle.Reference.SpaceFacts f) => string.Join(" ", f.Graphs["view"].Entities.Values.SelectMany(e => e.Props).Where(kv => kv.Key.EndsWith("prop:state", StringComparison.Ordinal)).Select(kv => kv.Value is TextValue t ? t.S : "-").Order(StringComparer.Ordinal));
        Assert.Equal(expected, States(Sattle.Reference.ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), direct)));
        Assert.Equal(expected, States(Sattle.Reference.ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), between)));
        foreach (var log in new[] { between, direct })
            foreach (var chunking in new[] { Chunking.OneByOne, Chunking.Largest, Chunking.Reversed })
            {
                var engine = States(EngineRun.Final(w.Catalog, log, chunking));
                Assert.True(engine == expected, $"{log.Length} inputs, {chunking.Name}: engine {engine}, reference {expected}");
            }
    }

    /// <summary>#151 review 1, reason 2: policy rows are told apart as whole rows (Def. 6.5), world `threshold with parameters of the row`, whose fold is by the
    /// group of the items, so each item of `g0` is a policy row of the one key. Row A (item k0, n = 2) enters before the first advance, row B (k1, n = 3) at 50 s,
    /// row C (k2, n = 2, the policy of A) at 100 s. The window from 0 s closes at 240 s; the row in force is C, of the greatest `since` below that close, so a
    /// reading of 2 turns the key on (above 2 for one window), and the transition's `since` is 100 s. Were C merged into A for its equal policy, B would hold
    /// (above 3 for two windows) and the key would stay off. Without C, B holds. The engine gives the reference's state and `since` under every cut.</summary>
    [Theory]
    [InlineData(true, "on 100")]
    [InlineData(false, "off 50")]
    public void A_policy_row_is_told_apart_as_a_whole_row(bool withC, string expected)
    {
        var w = Worlds.RowParameters;
        static Sattle.Reference.Input At(long seconds) => new Sattle.Reference.AdvanceInput(Logs.Origin + seconds * Logs.Second);
        Sattle.Reference.Input[] c = withC ? [PlainRow("a", 1, "k2", "g0", 2, 0)] : [];
        Sattle.Reference.Input[] log = [PlainRow("a", 1, "k0", "g0", 2, 0), At(50), PlainRow("a", 1, "k1", "g0", 3, 0), At(100), .. c, PlainRow("b", 1, "k0", "g0", 2, 10), At(240)];
        static string Shown(Sattle.Reference.SpaceFacts f) => string.Join(" ", f.Graphs["view"].Entities.Values.SelectMany(e => e.Props)
            .Where(kv => kv.Key.EndsWith("prop:state", StringComparison.Ordinal) || kv.Key.EndsWith("prop:since", StringComparison.Ordinal))
            .OrderBy(kv => kv.Key.EndsWith("prop:since", StringComparison.Ordinal))
            .Select(kv => kv.Value switch { TextValue t => t.S, InstantValue i => ((i.Ns - Logs.Origin) / Logs.Second).ToString(System.Globalization.CultureInfo.InvariantCulture), _ => "-" }));
        Assert.Equal(expected, Shown(Sattle.Reference.ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log)));
        foreach (var chunking in new[] { Chunking.OneByOne, Chunking.Largest, Chunking.Reversed })
            Assert.Equal(expected, Shown(EngineRun.Final(w.Catalog, log, chunking)));
    }

    /// <summary>#151 D6: a fold is a function of its present rows (Def. 6.5), so a window its Series withdraws for leaving the look-back leaves the fold, and the
    /// key's transitions change from that window on (Prop. 6.7 (2)): the key starts again at its first window still inside the look-back, and a key with none has
    /// no state. `fill and no data` (look-back 30 minutes): k1's record at 70 s; the window from 60 s closes at 300 s, three silent windows make k1 silent by
    /// 540 s, and the window leaves the look-back after 1860 s. `threshold over a short look-back` (10 minutes, a window and its late horizon of three take four):
    /// two windows at 6, from 0 and 60 s, close at 240 and 300 s and turn k0 on; the first leaves after 600 s, and one window at 6 leaves k0 off; at 720 s both
    /// have left. The engine gives the reference's state under every cut.</summary>
    [Theory]
    [InlineData("fill and no data", new[] { 120L, 540L }, "silent")]
    [InlineData("fill and no data", new[] { 120L, 300L, 360L, 540L }, "silent")]
    [InlineData("fill and no data", new[] { 120L, 540L, 1920L }, "")]
    [InlineData("threshold over a short look-back", new[] { 120L, 300L }, "on")]
    [InlineData("threshold over a short look-back", new[] { 120L, 240L, 300L, 660L }, "off")]
    [InlineData("threshold over a short look-back", new[] { 660L }, "off")]
    [InlineData("threshold over a short look-back", new[] { 120L, 300L, 720L }, "")]
    public void A_window_that_leaves_the_look_back_leaves_the_fold(string world, long[] advances, string expected)
    {
        var w = Worlds.All.Single(x => x.Name == world);
        static Sattle.Reference.Input At(long seconds) => new Sattle.Reference.AdvanceInput(Logs.Origin + seconds * Logs.Second);
        Sattle.Reference.Input[] rows = world == "fill and no data"
            ? [At(60), PlainRow("a", 1, "k1", "g0", 1, 0), PlainRow("b", 1, "k1", "g0", 2, 70)]
            : [PlainRow("a", 1, "k0", "g0", 1, 0), PlainRow("b", 1, "k0", "g0", 6, 0), PlainRow("b", 1, "k0", "g0", 6, 60)];
        Sattle.Reference.Input[] log = [.. rows, .. advances.Select(At)];
        static string States(Sattle.Reference.SpaceFacts f) => string.Join(" ", f.Graphs["view"].Entities.Values.SelectMany(e => e.Props).Where(kv => kv.Key.EndsWith("prop:state", StringComparison.Ordinal)).Select(kv => kv.Value is TextValue t ? t.S : "-").Order(StringComparer.Ordinal));
        Assert.Equal(expected, States(Sattle.Reference.ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log)));
        foreach (var chunking in new[] { Chunking.OneByOne, Chunking.Largest, Chunking.Reversed })
        {
            var engine = States(EngineRun.Final(w.Catalog, log, chunking));
            Assert.True(engine == expected, $"{world}, advances {string.Join(",", advances)}, {chunking.Name}: engine {engine}, expected {expected}");
        }
    }

    /// <summary>A node row of `ranked` (item `k`, rank `n`, none for null) or an edge row `from>to` with its impact after a colon, signed.</summary>
    static Sattle.Reference.RowInput FixpointRow(string change)
    {
        var weight = change[0] == '+' ? 1 : -1; var body = change[1..];
        if (body.Split('>') is [var from, var rest])
        {
            var (to, impact) = rest.Split(':') is [var t, var i] ? (t, Value.Text(i)) : (rest, Value.Null);
            return new("edges", Logs.Row(("src", Value.Text(from)), ("dst", Value.Text(to)), ("impact", impact), ("ds", Value.Decimal(1m)), ("dd", Value.Decimal(1m))), weight);
        }
        var (k, n) = body.Split('=') is [var a, var b] ? (a, b) : throw new ArgumentException(change);
        return new("nodes", Logs.Row(("k", Value.Text(k)), ("rank", n == "null" ? Value.Null : Value.Integer(long.Parse(n, System.Globalization.CultureInfo.InvariantCulture))), ("d", Value.Decimal(1m)), ("s", Value.Null)), weight);
    }

    /// <summary>The ranks of the items in the final facts, as `k=rank`, in the order of the items.</summary>
    static string Ranks(Sattle.Reference.SpaceFacts f) => string.Join(" ", f.Graphs["master"].Entities.Values
        .Select(e => (K: e.Props.SingleOrDefault(kv => kv.Key.EndsWith("prop:k", StringComparison.Ordinal)).Value, N: e.Props.SingleOrDefault(kv => kv.Key.EndsWith("prop:rank", StringComparison.Ordinal)).Value))
        .Where(x => x.N is IntegerValue).Select(x => $"{((TextValue)x.K).S}={((IntegerValue)x.N).N}").Order(StringComparer.Ordinal));

    /// <summary>#159 A3: the least solution of `ranked` (order 0 to 3, Limited maps 0 1 1 2, None maps to 0) after the changes, equal in the reference and in the engine under every cut.</summary>
    [Theory]
    [InlineData("chain", "+k0=0 +k1=0 +k2=3 +k0>k1 +k1>k2", "k0=3 k1=3 k2=3")]
    [InlineData("chain through Limited", "+k0=0 +k1=0 +k2=3 +k0>k1:Limited +k1>k2", "k0=2 k1=3 k2=3")]
    [InlineData("diamond", "+k0=0 +k1=0 +k2=1 +k3=3 +k0>k1 +k0>k2 +k1>k3:Limited +k2>k3:None", "k0=2 k1=2 k2=1 k3=3")]
    [InlineData("cycle", "+k0=0 +k1=2 +k2=0 +k0>k1 +k1>k2 +k2>k0", "k0=2 k1=2 k2=2")]
    [InlineData("removal of an edge on a cycle", "+k0=0 +k1=2 +k2=0 +k0>k1 +k1>k2 +k2>k0 -k0>k1", "k0=0 k1=2 k2=0")]
    [InlineData("removal of the own value that holds a cycle up", "+k0=0 +k1=3 +k2=0 +k0>k1 +k1>k2 +k2>k0 -k1=3 +k1=1", "k0=1 k1=1 k2=1")]
    [InlineData("change of a leaf value", "+k0=0 +k1=0 +k2=3 +k0>k1 +k1>k2 -k2=3 +k2=1", "k0=1 k1=1 k2=1")]
    [InlineData("self-loop", "+k0=2 +k0>k0:Limited", "k0=2")]
    [InlineData("a key only in edge rows is no output row", "+k0=0 +k0>k1 +k1>k2 +k2=3", "k0=3 k2=3")]
    [InlineData("a node row that went", "+k0=0 +k1=3 +k0>k1 -k1=3", "k0=0")]
    [InlineData("no value and a value outside the order", "+k0=null +k1=4", "")]
    [InlineData("no value of its own, a value through an edge", "+k0=null +k1=4 +k2=1 +k0>k2 +k1>k2", "k0=1 k1=1 k2=1")]
    [InlineData("an edge that went twice counts while its weight is positive", "+k0=0 +k1=3 +k0>k1 -k0>k1 -k0>k1 +k0>k1", "k0=0 k1=3")]
    public void A_fixpoint_is_the_least_solution_of_its_equations(string name, string changes, string expected)
    {
        Assert.NotEmpty(name);
        var w = Worlds.Fixpoint;
        Sattle.Reference.Input[] log = [.. changes.Split(' ').Select(FixpointRow)];
        Assert.Equal(expected, Ranks(Sattle.Reference.ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log)));
        foreach (var chunking in new[] { Chunking.OneByOne, Chunking.Largest, Chunking.Reversed })
            Assert.True(Ranks(EngineRun.Final(w.Catalog, log, chunking)) is var engine && engine == expected, $"{chunking.Name}: engine {engine}, expected {expected}");
    }

    /// <summary>#159 A5 (Theorem 7.6): a change evaluates only the nodes that reach a changed node. Next to a chain k0 to k2 lies a chain of 100 items f0 to f99;
    /// a change of the leaf k2 evaluates at most |Aff| + ht·P = 3 + 4·2 equations, so no node of the far chain, and a change of the leaf f99 evaluates all 100 of it.</summary>
    [Theory]
    [InlineData("k2", 3, 11)]
    [InlineData("f99", 100, 100 + 4 * 99)]
    public void A_change_evaluates_only_the_nodes_that_reach_it(string leaf, long least, long most)
    {
        var w = Worlds.Fixpoint;
        using var engine = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single());
        var far = Enumerable.Range(0, 100).SelectMany(i => i < 99 ? new[] { $"+f{i}=0", $"+f{i}>f{i + 1}" } : [$"+f{i}=1"]);
        var seq = 0L;
        foreach (var source in "+k0=0 +k1=0 +k2=1 +k0>k1 +k1>k2".Split(' ').Concat(far).Select(FixpointRow).GroupBy(r => r.Source)) EngineRun.Ingest(engine, [.. source], ++seq);
        var op = engine.Master.Plan.Ops.OfType<FixpointOp>().Single(o => o.Name == "ranked_1");
        var before = op.Evaluations;
        EngineRun.Ingest(engine, [FixpointRow($"-{leaf}=1"), FixpointRow($"+{leaf}=3")], ++seq);
        var work = op.Evaluations - before;
        Assert.InRange(work, least, most);
    }

    /// <summary>#159 A6: on trees of three levels the fixpoint gives the roll-up the unrolled joins give, for every node, in the reference and in the engine.</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void The_fixpoint_rolls_up_as_the_three_unrolled_levels(int seed)
    {
        var w = Worlds.RollupOfThreeLevels;
        var log = w.Log(seed, 30);
        static string Of(Sattle.Reference.SpaceFacts f, string type) => string.Join(" ", f.Graphs["master"].Entities.Values.Where(e => e.Type.EndsWith("entity:" + type, StringComparison.Ordinal))
            .Select(e => $"{e.Props.Single(kv => kv.Key.EndsWith("prop:key", StringComparison.Ordinal)).Value}={e.Props.Single(kv => kv.Key.EndsWith("prop:rank", StringComparison.Ordinal)).Value}").Order(StringComparer.Ordinal));
        foreach (var facts in new[] { Sattle.Reference.ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log), EngineRun.Final(w.Catalog, log, Chunking.OneByOne) })
        {
            Assert.Equal(11, facts.Graphs["master"].Entities.Values.Count(e => e.Type.EndsWith("entity:Fixed", StringComparison.Ordinal)));
            Assert.Equal(Of(facts, "Unrolled"), Of(facts, "Fixed"));
        }
    }

    /// <summary>`!m` moves the clock to minute m; `+a:k@m` or `-b:k@m` adds or withdraws a row of source a or b with key k and time minute m.</summary>
    static Sattle.Reference.Input LatenessInput(string change)
    {
        if (change[0] == '!') return new Sattle.Reference.AdvanceInput(long.Parse(change[1..], System.Globalization.CultureInfo.InvariantCulture) * Logs.Minute);
        var (source, rest) = change[1..].Split(':') is [var s, var r] ? (s, r) : throw new ArgumentException(change);
        var (k, minute) = rest.Split('@') is [var key, var m] ? (key, long.Parse(m, System.Globalization.CultureInfo.InvariantCulture)) : throw new ArgumentException(change);
        return new Sattle.Reference.RowInput(source, Logs.Row(("k", Value.Text(k)), ("g", Value.Text("g0")), ("n", Value.Integer(1)), ("d", Value.Null), ("t", Value.Instant(minute * Logs.Minute))), change[0] == '+' ? 1 : -1);
    }

    /// <summary>#159 A7 (Ch. 6, Def. 6.1, Thm. 6.3, Cor. 6.5): a join of `a` (lateness 1 minute) and `b` (lateness 4 minutes). Each row is late or on time by
    /// its own input's lateness, and a window of the joined rows stays revisable for the larger lateness after its end, not for the host's 3 widths. The pairs
    /// per window as `minute=count`, equal in the reference and in the engine under every cut, and the late rows the engine counted.</summary>
    [Theory]
    [InlineData("both on time", "!10 +a:k0@9 +b:k0@9", "9=1", 0)]
    [InlineData("a row of a 2 minutes late is left out", "!10 +a:k0@8 +b:k0@8", "", 1)]
    [InlineData("a row of b 4 minutes late is on time", "!10 +a:k0@9 +b:k0@6", "9=1", 0)]
    [InlineData("a row of b 5 minutes late is left out", "!10 +a:k0@9 +b:k0@5", "", 1)]
    [InlineData("a row is judged by the clock when it arrives", "+a:k0@8 !10 +b:k0@8", "8=1", 0)]
    [InlineData("a window revises within the larger lateness", "+a:k0@8 !12 +b:k0@9", "8=1", 0)]
    [InlineData("a window is final at its end plus the larger lateness", "+a:k0@8 !13 +b:k0@9", "", 1)]
    [InlineData("a late withdrawal of a is left out", "+a:k0@9 +b:k0@9 !11 -a:k0@9", "9=1", 1)]
    [InlineData("an on-time withdrawal of b removes the pair", "+a:k0@9 +b:k0@9 !11 -b:k0@9", "", 0)]
    public void Each_input_is_late_by_its_own_lateness(string name, string changes, string expected, long late)
    {
        Assert.NotEmpty(name);
        var w = Worlds.LatenessPerInput;
        Sattle.Reference.Input[] log = [.. changes.Split(' ').Select(LatenessInput)];
        static string Pairs(Sattle.Reference.SpaceFacts f) => string.Join(" ", f.Graphs["master"].Windows
            .Where(t => t.Key.Property.EndsWith("pairs", StringComparison.Ordinal) && t.Value != 0).Select(t => $"{t.Key.Start / Logs.Minute}={Norm.Decimal(t.Value)}"));
        Assert.Equal(expected, Pairs(Sattle.Reference.ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log)));
        foreach (var chunking in new[] { Chunking.OneByOne, Chunking.Largest, Chunking.Reversed })
            Assert.True(Pairs(EngineRun.Final(w.Catalog, log, chunking)) is var engine && engine == expected, $"{chunking.Name}: engine {engine}, expected {expected}");
        using var e = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single());
        var seq = 0L;
        foreach (var input in log)
            if (input is Sattle.Reference.AdvanceInput a) e.Advance(a.Frontier, ++seq); else EngineRun.Ingest(e, [(Sattle.Reference.RowInput)input], ++seq);
        Assert.Equal(late, e.Counters().LateRows);
    }

    /// <summary>A world whose every declared lateness of 3 minutes, on its rows inputs and its series, is `minutes` instead.</summary>
    static World WithLateness(World w, int minutes) => w with
    {
        Name = $"{w.Name}, lateness {minutes}m",
        Files = [.. w.Files.Select(f => f with { Text = f.Text.Replace("\"lateness\": \"3m\"", $"\"lateness\": \"{minutes}m\"", StringComparison.Ordinal) })],
    };

    /// <summary>#169 A3 (Ch. 6, Def. 6.1, Cor. 6.5): window 0 ([0, 1m)) of `a` with a declared lateness λ. Its state is held while the clock is below its end plus λ
    /// and no longer, whatever order the rows arrive in: a row of minute 0 that arrives at the last clock before that revises it, one that arrives at that clock is
    /// late and left out. `{e}` is the clock 1m + λ − 1m. The reference agrees with the engine under every cut and with the rows of each batch reversed.</summary>
    [Theory]
    [MemberData(nameof(LatenessAndArrival))]
    public void A_window_is_held_until_its_end_plus_the_declared_lateness_and_no_longer(int lambda, string order, string changes, long late)
    {
        Assert.NotEmpty(order);
        var w = WithLateness(Worlds.Windowed, lambda);
        var e = lambda;                                                         // the last clock (in minutes) below end + λ = 1 + λ
        Sattle.Reference.Input[] log = [.. changes.Replace("{e+1}", (e + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{e}", e.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal).Split(' ').Select(LatenessInput)];
        Assert.Null(Differential.FirstMismatch(w, log, 1, reordered: false));
        Assert.Null(Differential.FirstMismatch(w, log, 1, reordered: true));
        using var engine = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single(), new EngineOptions { WindowRetentionNs = w.RetentionNs });
        // the windows whose state the windowed Group holds: its open ones and its closed ones within the lateness
        var group = engine.Master.Plan.Ops.Single(o => o.GetType().Name == "WindowedGroupOp");
        var field = group.GetType().GetField("windows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        int Held() => ((System.Collections.ICollection)field.GetValue(group)!).Count;
        var (seq, rowsIn) = (0L, false);
        foreach (var input in log)
        {
            if (input is Sattle.Reference.AdvanceInput a)
            {
                engine.Advance(a.Frontier, ++seq);
                Assert.True(Held() == (rowsIn && a.Frontier < (1 + lambda) * Logs.Minute ? 1 : 0), $"clock {a.Frontier / Logs.Minute}m: {Held()} windows held");
            }
            else { EngineRun.Ingest(engine, [(Sattle.Reference.RowInput)input], ++seq); rowsIn = true; }
        }
        Assert.Equal(late, engine.Counters().LateRows);
    }

    public static TheoryData<int, string, string, long> LatenessAndArrival()
    {
        var data = new TheoryData<int, string, string, long>();
        foreach (var lambda in new[] { 0, 1, 3, 10 })
        {
            data.Add(lambda, "on time, then the clock passes the close", "+a:k0@0 +a:k1@0 !{e} !{e+1}", 0);
            data.Add(lambda, "a row at the last clock before the close revises", "+a:k1@0 !{e} +a:k0@0 !{e+1}", 0);
            data.Add(lambda, "the clock first, then both rows at the last clock", "!{e} +a:k0@0 +a:k1@0 !{e+1}", 0);
            data.Add(lambda, "a row at the close is late and left out", "+a:k1@0 !{e+1} +a:k0@0 !{e+1}", 1);
        }
        return data;
    }

    /// <summary>#169 A3: the reference agrees with the engine under every cut for a windowed Group and a Fold over a series, whatever lateness they declare.</summary>
    [Theory]
    [InlineData("window late revise", 0)] [InlineData("window late revise", 1)] [InlineData("window late revise", 10)]
    [InlineData("threshold", 0)] [InlineData("threshold", 1)] [InlineData("threshold", 10)]
    public void The_reference_agrees_under_every_cut_whatever_lateness_is_declared(string world, int lambda)
    {
        var w = WithLateness(Worlds.All.First(x => x.Name == world), lambda);
        foreach (var seed in Enumerable.Range(0, 4))
            Assert.Null(Differential.FirstMismatch(w, w.Log(seed, 14), seed, reordered: seed % 2 == 1)?.ToString());
    }

    /// <summary>Review: Identity.cs:165 with Exprs.cs:107. An identity is allocated for the rows a sink emits (Def. 5.9), not for rows a Filter rejected.</summary>
    [Fact]
    public void A_row_that_a_filter_rejected_mints_no_identity()
    {
        var w = Worlds.FilteredEmit;
        using var engine = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single());
        EngineRun.Ingest(engine, [PlainRow("a", 1, "k0", "g0", 0, 0), PlainRow("a", 1, "k1", "g0", 1, 0), PlainRow("a", 1, "k2", "g0", 5, 0)], 1);
        Assert.Single(engine.Master.State.Entities);
        Assert.Equal(1, engine.Counters().Minted);
    }

    /// <summary>Review: Operators.cs:58, Columnar.cs:59 (Synthetic.cs:22, Bench.cs:75). A column that a row writer never sets is absent. The row builder marks every column present, so it reads as the value with code 0.</summary>
    [Fact]
    public void A_column_that_the_writer_never_sets_is_absent()
    {
        var w = Worlds.OptionalProperty;
        using var engine = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single());
        var writer = new RowWriter(engine.RowLayout("a"), engine.Interner);
        writer.Begin(1); writer.Text("k", "k0"); writer.Text("g", "g0"); writer.Int(writer.Col("n"), 1); writer.Instant("t", Logs.Origin);       // d is not set
        engine.Ingest("a", writer.Build()!, 1);
        Assert.DoesNotContain(EngineRun.FactsOf(engine).Graphs["master"].Lines(), l => l.Contains("prop:d="));
    }

    /// <summary>#112: in this world the numbers the texts get depend on how the log is cut (one row per batch against the largest batches), so the differential comparison of the world would see an output that depends on them.</summary>
    [Fact]
    public void Code_numbers_depend_on_the_cut_where_a_where_drops_rows()
    {
        var w = Worlds.TraverseAfterWhere;
        Dictionary<string, int> Codes(IReadOnlyList<Sattle.Reference.Input> log, bool oneByOne)
        {
            using var engine = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single());
            long seq = 0; var batch = new List<Sattle.Reference.RowInput>();
            void Flush() { if (batch.Count > 0) EngineRun.Ingest(engine, [.. batch], ++seq); batch.Clear(); }
            foreach (var row in log.Cast<Sattle.Reference.RowInput>()) { if (oneByOne || (batch.Count > 0 && batch[0].Source != row.Source)) Flush(); batch.Add(row); }
            Flush();
            return engine.Interner.Slots.Select((t, i) => (t, i)).Where(x => x.t is not null).ToDictionary(x => x.t!, x => x.i);
        }
        var differ = Enumerable.Range(0, 12).Select(seed => w.Log(seed, 14)).Count(log => !Codes(log, true).OrderBy(x => x.Key, StringComparer.Ordinal).SequenceEqual(Codes(log, false).OrderBy(x => x.Key, StringComparer.Ordinal)));
        Assert.True(differ > 0, "no default seed gives the texts other numbers under another cut");
    }
}
