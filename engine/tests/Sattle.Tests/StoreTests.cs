using System.Collections.Immutable;
using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>One conformance suite for the six-operation storage port, run against every implementation (research 02 section 3.2).</summary>
public class StoreConformanceTests
{
    /// <summary>The Cosmos store joins when SATTLE_COSMOS_EMULATOR names a Cosmos DB emulator endpoint (docker run -p 8081:8081 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview --protocol http).</summary>
    public static string? CosmosEndpoint => Environment.GetEnvironmentVariable("SATTLE_COSMOS_EMULATOR");
    public static IEnumerable<object[]> Stores() => CosmosEndpoint is null ? [["memory"], ["sqlite"]] : [["memory"], ["sqlite"], ["cosmos"]];
    static readonly GraphKey G = new("t", "g");

    static IGraphStore Make(string kind)
    {
        if (kind == "memory") return new InMemoryGraphStore();
        if (kind == "cosmos") return Sattle.Cli.CosmosGraphStore.Open(CosmosEndpoint!, "t" + Guid.NewGuid().ToString("N")[..12], dropOnDispose: true);
        var path = Path.Combine(Path.GetTempPath(), "sattle-test-" + Guid.NewGuid().ToString("N") + ".db");
        return new SqliteGraphStore(path);
    }

    static Entity E(string id, string type = "T1", string? p = null) => new(id, type, p is null ? Props.Empty : Props.Of(("p", Value.Text(p))));
    static Rel R(string id, string s, string t, string type = "R1") => new(id, s, t, type, Props.Empty);
    static Patch P(long from, params Edit[] edits) => new("g", from, from + 1, edits);
    static CommitBatch B(long head, params Patch[] ps) => new(G, head, ps, [.. ps.Select(_ => Frontier.Empty)], []);

    [Theory, MemberData(nameof(Stores))]
    public void Empty_graph_has_head_zero_and_unavailable_future(string kind)
    {
        using var s = Make(kind);
        Assert.Equal(new HeadInfo(0, 0), s.Head(G));
        Assert.IsType<ReadResult.Unavailable>(s.Read(G, 1, [ElementKey.Entity("a")])[0]);
        Assert.IsType<ReadResult.Absent>(s.Read(G, 0, [ElementKey.Entity("a")])[0]);
    }

    [Theory, MemberData(nameof(Stores))]
    public void Commit_makes_all_revisions_visible_together_and_reads_follow_intervals(string kind)
    {
        using var s = Make(kind);
        var a = E("a", "T1", "x"); var a2 = E("a", "T2", "y");
        var res = s.Commit(B(0, P(0, Edit.Create(a)), P(1, Edit.Change(a, a2), Edit.Create(E("b"))), P(2, Edit.Remove(E("b")))));
        Assert.Equal(new CommitResult.Committed(3), res);
        Assert.Equal(3, s.Head(G).Head);
        var r1 = (ReadResult.Present)s.Read(G, 1, [ElementKey.Entity("a")])[0];
        Assert.Equal(a, r1.State); Assert.Equal(1, r1.From); Assert.Equal(2, r1.To);
        var r2 = (ReadResult.Present)s.Read(G, 3, [ElementKey.Entity("a")])[0];
        Assert.Equal(a2, r2.State); Assert.Null(r2.To);
        Assert.IsType<ReadResult.Absent>(s.Read(G, 3, [ElementKey.Entity("b")])[0]);
        Assert.IsType<ReadResult.Present>(s.Read(G, 2, [ElementKey.Entity("b")])[0]);
        Assert.IsType<ReadResult.Absent>(s.Read(G, 1, [ElementKey.Entity("b")])[0]);
    }

    [Theory, MemberData(nameof(Stores))]
    public void Stale_head_conflicts_and_writes_nothing(string kind)
    {
        using var s = Make(kind);
        s.Commit(B(0, P(0, Edit.Create(E("a")))));
        Assert.Equal(new CommitResult.Conflict(1), s.Commit(B(0, P(0, Edit.Create(E("b"))))));
        Assert.Equal(1, s.Head(G).Head);
        Assert.IsType<ReadResult.Absent>(s.Read(G, 1, [ElementKey.Entity("b")])[0]);
    }

    [Theory, MemberData(nameof(Stores))]
    public void A_failing_patch_leaves_no_partial_batch(string kind)
    {
        using var s = Make(kind);
        s.Commit(B(0, P(0, Edit.Create(E("a")))));
        var bad = s.Commit(B(1, P(1, Edit.Create(E("b"))), P(2, Edit.Remove(E("zzz")))));          // guard fails on the second patch
        Assert.IsType<CommitResult.Rejected>(bad);
        Assert.Equal(1, s.Head(G).Head);
        Assert.IsType<ReadResult.Absent>(s.Read(G, 1, [ElementKey.Entity("b")])[0]);
        Assert.IsType<CommitResult.Committed>(s.Commit(B(1, P(1, Edit.Create(E("b")))))); // the store is still usable
    }

    [Theory, MemberData(nameof(Stores))]
    public void Uniqueness_of_source_target_type_is_enforced_atomically(string kind)
    {
        using var s = Make(kind);
        s.Commit(B(0, P(0, Edit.Create(R("r1", "a", "b")))));
        Assert.IsType<CommitResult.Rejected>(s.Commit(B(1, P(1, Edit.Create(R("r2", "a", "b"))))));
        Assert.IsType<CommitResult.Committed>(s.Commit(B(1, P(1, Edit.Create(R("r3", "a", "b", "R2"))))));
        // a swap in one patch is allowed
        Assert.IsType<CommitResult.Committed>(s.Commit(B(2, P(2, Edit.Remove(R("r1", "a", "b")), Edit.Create(R("r4", "a", "b"))))));
        Assert.Equal(3, s.Head(G).Head);
    }

    [Theory, MemberData(nameof(Stores))]
    public void Empty_or_misnumbered_patches_are_rejected(string kind)
    {
        using var s = Make(kind);
        Assert.IsType<CommitResult.Rejected>(s.Commit(B(0, P(5, Edit.Create(E("a"))))));
        Assert.IsType<CommitResult.Rejected>(s.Commit(B(0, new Patch("g", 0, 1, []))));
    }

    [Theory, MemberData(nameof(Stores))]
    public void Adjacency_scan_filters_by_direction_type_revision_and_pages_in_order(string kind)
    {
        using var s = Make(kind);
        s.Commit(B(0, P(0, Edit.Create(E("a")), Edit.Create(R("r1", "a", "b")), Edit.Create(R("r2", "a", "c")), Edit.Create(R("r3", "a", "dangling", "R2")), Edit.Create(R("r4", "z", "a")))));
        s.Commit(B(1, P(1, Edit.Remove(R("r2", "a", "c")))));
        var all = s.ScanAdjacency(G, 2, "a", Direction.Out, null, 10, null);
        Assert.Equal(["r1", "r3"], all.Entries.Select(e => e.RelId));
        Assert.Contains(all.Entries, e => e.Other == "dangling");
        Assert.Equal(["r1", "r2", "r3"], s.ScanAdjacency(G, 1, "a", Direction.Out, null, 10, null).Entries.Select(e => e.RelId)); // history
        Assert.Equal(["r3"], s.ScanAdjacency(G, 2, "a", Direction.Out, "R2", 10, null).Entries.Select(e => e.RelId));
        Assert.Equal(["r4"], s.ScanAdjacency(G, 2, "a", Direction.In, null, 10, null).Entries.Select(e => e.RelId));
        var p1 = s.ScanAdjacency(G, 1, "a", Direction.Out, null, 2, null);
        Assert.Equal(["r1", "r2"], p1.Entries.Select(e => e.RelId)); Assert.NotNull(p1.Continuation);
        var p2 = s.ScanAdjacency(G, 1, "a", Direction.Out, null, 2, p1.Continuation);
        Assert.Equal(["r3"], p2.Entries.Select(e => e.RelId)); Assert.Null(p2.Continuation);
    }

    [Theory, MemberData(nameof(Stores))]
    public void Retype_keeps_the_relationship_id_and_history(string kind)
    {
        using var s = Make(kind);
        var r = R("r1", "a", "b");
        s.Commit(B(0, P(0, Edit.Create(r))));
        s.Commit(B(1, P(1, Edit.Change(r, r with { Type = "R2" }))));
        Assert.Equal("R1", ((Rel)((ReadResult.Present)s.Read(G, 1, [ElementKey.Rel("r1")])[0]).State).Type);
        Assert.Equal("R2", ((Rel)((ReadResult.Present)s.Read(G, 2, [ElementKey.Rel("r1")])[0]).State).Type);
        Assert.Empty(s.ScanAdjacency(G, 2, "a", Direction.Out, "R1", 10, null).Entries);
        Assert.Single(s.ScanAdjacency(G, 1, "a", Direction.Out, "R1", 10, null).Entries);
    }

    [Theory, MemberData(nameof(Stores))]
    public void Follow_returns_patches_in_order_with_their_frontiers(string kind)
    {
        using var s = Make(kind);
        var f = Frontier.Empty with { Pos = ImmutableSortedDictionary<string, long>.Empty.Add("src", 7), Cfg = 3 };
        s.Commit(new CommitBatch(G, 0, [P(0, Edit.Create(E("a"))), P(1, Edit.Create(E("b")))], [Frontier.Empty, f], []));
        var got = s.Follow(G, 0).ToList();
        Assert.Equal([1L, 2L], got.Select(c => c.Patch.To));
        Assert.Equal(f, got[1].Frontier);
        Assert.Single(s.Follow(G, 1));
        Assert.Empty(s.Follow(G, 2));
    }

    [Theory, MemberData(nameof(Stores))]
    public void Seal_digest_equals_the_digest_of_the_model_at_every_revision_for_random_histories(string kind)
    {
        for (var seed = 0; seed < 25; seed++)
        {
            using var s = Make(kind);
            var gen = new Gen(seed);
            var model = new GraphLog("g");
            var pending = new List<Patch>();
            for (var i = 0; i < 8; i++)
            {
                var p = gen.PatchFor(model.State, model.Head, guardOnly: false);
                if (p is null) break;
                var (c, _) = model.Commit(p.Edits);
                if (c is not null) pending.Add(c);
                if (pending.Count >= 3 || (i == 7 && pending.Count > 0))
                {
                    var head = s.Head(G).Head;
                    Assert.IsType<CommitResult.Committed>(s.Commit(new CommitBatch(G, head, pending, [.. pending.Select(_ => Frontier.Empty)], [])));
                    pending.Clear();
                }
            }
            for (var r = 0L; r <= model.Head; r++)
            {
                var seal = s.Seal(G, r);
                Assert.Equal(model.StateAt(r).Digest(), seal.Digest);
                Assert.Equal(model.StateAt(r).Count, s.OpenSnapshot(G, r).Count());
            }
        }
    }

    [Theory, MemberData(nameof(Stores))]
    public void The_ontology_slot_is_stored_like_any_element(string kind)
    {
        using var s = Make(kind);
        var o = new OntologyState("o1", "1", "sha256:00");
        s.Commit(B(0, P(0, Edit.Create(o))));
        Assert.Equal(o, ((ReadResult.Present)s.Read(G, 1, [ElementKey.Ontology])[0]).State);
    }

    [Fact]
    public void Sqlite_persists_across_reopen_and_stores_identity_bindings()
    {
        var path = Path.Combine(Path.GetTempPath(), "sattle-test-" + Guid.NewGuid().ToString("N") + ".db");
        using (var s = new SqliteGraphStore(path))
            s.Commit(new CommitBatch(G, 0, [P(0, Edit.Create(E("a", "T1", "x")))], [Frontier.Empty], [new IdentityBinding("d", "k1", "a", "[]")]));
        using var s2 = new SqliteGraphStore(path);
        Assert.Equal(1, s2.Head(G).Head);
        Assert.Equal(E("a", "T1", "x"), ((ReadResult.Present)s2.Read(G, 1, [ElementKey.Entity("a")])[0]).State);
        Assert.Equal(1, s2.BindingCount("t"));
    }

    /// <summary>Several store instances over one backing store, as several writers on one database are.</summary>
    sealed class Backing(string kind) : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), "sattle-test-" + Guid.NewGuid().ToString("N") + ".db");
        readonly string database = "t" + Guid.NewGuid().ToString("N")[..12];
        readonly InMemoryGraphStore memory = new();
        readonly List<IGraphStore> opened = [];
        public IGraphStore Open() { IGraphStore s = kind switch { "memory" => memory, "cosmos" => Sattle.Cli.CosmosGraphStore.Open(CosmosEndpoint!, database, false), _ => new SqliteGraphStore(path) }; opened.Add(s); return s; }
        public void Dispose()
        {
            foreach (var s in opened) if (kind != "memory") s.Dispose();
            if (kind == "cosmos") { using var _ = Sattle.Cli.CosmosGraphStore.Open(CosmosEndpoint!, database, true); }
            else foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f);
        }
    }

    [Theory, MemberData(nameof(StoreConformanceTests.Stores), MemberType = typeof(StoreConformanceTests))]
    public void A_commit_above_the_two_megabyte_item_limit_is_one_commit_and_reads_back_whole(string kind)
    {
        using var backing = new Backing(kind);
        var s = backing.Open();
        var big = new string('x', 300_000);
        var edits = Enumerable.Range(0, 12).Select(i => Edit.Create(E("b" + i.ToString("D2"), "T1", big + i))).ToArray();      // 3.6 MB of edits, each element 0.3 MB
        var patch = P(0, edits);
        Assert.Equal(new CommitResult.Committed(1), s.Commit(B(0, patch)));
        Assert.Equal(1, s.Head(G).Head);
        var follow = s.Follow(G, 0).ToList();
        Assert.Single(follow);
        Assert.True(follow[0].Patch.Edits.AsSpan().SequenceEqual(patch.Edits.AsSpan()));
        Assert.Equal(12, s.OpenSnapshot(G, 1).Count());
        var read = (ReadResult.Present)s.Read(G, 1, [ElementKey.Entity("b07")])[0];
        Assert.Equal(big + 7, ((Entity)read.State).Props.Text("p"));
        // and the next commit builds on it
        Assert.Equal(new CommitResult.Committed(2), s.Commit(B(1, P(1, Edit.Change(E("b00", "T1", big + 0), E("b00", "T1", "small"))))));
        Assert.Equal("small", ((Entity)((ReadResult.Present)s.Read(G, 2, [ElementKey.Entity("b00")])[0]).State).Props.Text("p"));
    }

    [Theory, MemberData(nameof(StoreConformanceTests.Stores), MemberType = typeof(StoreConformanceTests))]
    public void A_second_writer_on_the_same_graph_does_not_get_a_commit_through_and_does_not_corrupt_the_log(string kind)
    {
        using var backing = new Backing(kind);
        var a = backing.Open(); var b = backing.Open();
        Assert.Equal(new CommitResult.Committed(1), a.Commit(B(0, P(0, Edit.Create(E("a"))))));
        Assert.Equal(1, b.Head(G).Head);                                       // the other writer sees the log
        var refused = b.Commit(B(0, P(0, Edit.Create(E("z")))));               // stale head: a conflict, or refused by the lease
        Assert.IsNotType<CommitResult.Committed>(refused);
        Assert.Equal(new CommitResult.Committed(2), a.Commit(B(1, P(1, Edit.Create(E("c"))))));
        Assert.Equal(["a", "c"], a.OpenSnapshot(G, 2).Select(e => e.Key.Id).Order().ToArray());
        Assert.IsType<ReadResult.Absent>(a.Read(G, 2, [ElementKey.Entity("z")])[0]);
    }

    [CosmosFact]
    public void Cosmos_lease_refuses_the_second_writer_even_with_the_right_head_until_the_first_lets_go_or_expires()
    {
        using var backing = new Backing("cosmos");
        var a = (Sattle.Cli.CosmosGraphStore)backing.Open(); var b = (Sattle.Cli.CosmosGraphStore)backing.Open();
        a.LeaseMillis = 3000; b.LeaseMillis = 3000;
        Assert.Equal(new CommitResult.Committed(1), a.Commit(B(0, P(0, Edit.Create(E("a"))))));
        Assert.Equal(1, b.Head(G).Head);
        var refused = Assert.IsType<CommitResult.Rejected>(b.Commit(B(1, P(1, Edit.Create(E("z"))))));      // right head, no lease
        Assert.Contains("lease", refused.Reason);
        Thread.Sleep(3500);                                                    // a stops renewing: its lease expires
        Assert.Equal(new CommitResult.Committed(2), b.Commit(B(1, P(1, Edit.Create(E("z"))))));
        var stale = a.Commit(B(1, P(1, Edit.Create(E("y")))));                 // a still believes it is at head 1 and has lost the lease
        Assert.IsNotType<CommitResult.Committed>(stale);
        Assert.Equal(["a", "z"], b.OpenSnapshot(G, 2).Select(e => e.Key.Id).Order().ToArray());
    }

    [CosmosFact]
    public void Cosmos_chunks_without_a_commit_record_are_not_a_commit()
    {
        using var backing = new Backing("cosmos");
        var a = (Sattle.Cli.CosmosGraphStore)backing.Open();
        a.InlineLimit = 100; a.ChunkChars = 50;                                  // force the chunk path with small data
        a.BeforeCommitRecord = _ => throw new InvalidOperationException("crash after the chunks, before the commit record");
        Assert.Throws<InvalidOperationException>(() => a.Commit(B(0, P(0, Edit.Create(E("a", "T1", new string('q', 400)))))));
        var b = (Sattle.Cli.CosmosGraphStore)backing.Open();
        a.Dispose();                                                            // releases the lease
        Assert.Equal(0, b.Head(G).Head);
        Assert.Empty(b.Follow(G, 0));
        b.InlineLimit = 100; b.ChunkChars = 70;                                  // a different cut of the same revision overwrites the orphans
        Assert.Equal(new CommitResult.Committed(1), b.Commit(B(0, P(0, Edit.Create(E("a", "T1", new string('r', 400)))))));
        Assert.Equal(new string('r', 400), ((Entity)b.Follow(G, 0).Single().Patch.Edits[0].After!).Props.Text("p"));
    }
}

/// <summary>The telemetry port: Append, ReadWindow, ClosedWindows (in-memory ring).</summary>
public class TelemetryStoreTests
{
    static readonly GraphKey G = new("t", "g");
    const long W = 60_000_000_000;

    [Fact]
    public void Append_dedupes_replaces_and_reports_closed_windows_once_in_order()
    {
        var s = new InMemoryTelemetryStore();
        s.Append(G, [new Sample(1, 1, 0, W, 10, 1), new Sample(1, 1, W, W, 20, 2)]);
        s.Append(G, [new Sample(1, 1, W, W, 20, 2)]);                // duplicate delivery
        var c = s.ClosedWindows(G, 0, out var pos);
        Assert.Equal(2, c.Count); Assert.Equal(2, pos);
        s.Append(G, [new Sample(1, 1, W, W, 25, 3)]);                // revision of a window
        var rev = s.ClosedWindows(G, pos, out var pos2);
        Assert.Single(rev); Assert.Equal(20m, rev[0].Old); Assert.Equal(25m, rev[0].New);
        Assert.Empty(s.ClosedWindows(G, pos2, out _));
        var read = s.ReadWindow(G, [1], [1], 0, 2 * W);
        Assert.Equal([10m, 25m], read[0].Points.Select(p => p.Value));
        Assert.Equal("Complete", read[0].Completeness);
    }

    /// <summary>Law 7.5: when two ids merge, a window both have combines as if every row had been written under the survivor: by the property's rule for sum, min and max; otherwise equal values are one value and different values leave the window without one.</summary>
    [Theory]
    [InlineData("sum", 3, 4, 7)]
    [InlineData("min", 3, 4, 3)]
    [InlineData("max", 3, 4, 4)]
    [InlineData("mean", 4, 4, 4)]
    [InlineData("mean", 3, 4, null)]
    [InlineData(null, 3, 4, null)]
    public void Windows_of_two_merged_ids_combine_by_the_rule_of_their_property(string? rule, int loser, int survivor, int? expected)
    {
        var s = new InMemoryTelemetryStore();
        s.Append(G, [new Sample(1, 1, 0, W, loser, 1), new Sample(2, 1, 0, W, survivor, 2), new Sample(1, 1, W, W, 9, 3)]);
        s.ClosedWindows(G, 0, out var position);
        s.MoveElement(G, 1, 2, _ => rule);
        Assert.Empty(s.ReadWindow(G, [1], [1], 0, 2 * W)[0].Points);
        var points = s.ReadWindow(G, [2], [1], 0, 2 * W)[0].Points;
        Assert.Equal(expected is { } v ? [v, 9m] : [9m], points.Select(p => p.Value));        // the window only the loser had moves as it is
        Assert.Contains(s.ClosedWindows(G, position, out _), c => c.Key.Element == 2 && c.Key.WindowStart == 0 && c.New == expected);
    }

    /// <summary>Law 7.5: "no value" is absorbing. Three ids with different values under a rule that cannot combine them give no value whatever the order of the merges, and the window is counted once.</summary>
    [Theory]
    [InlineData(1, 2, 3)]
    [InlineData(3, 1, 2)]
    [InlineData(2, 3, 1)]
    public void A_window_without_a_value_stays_so_when_a_third_id_merges_in(int first, int second, int survivor)
    {
        var s = new InMemoryTelemetryStore();
        s.Append(G, [new Sample(1, 1, 0, W, 5, 1), new Sample(2, 1, 0, W, 6, 2), new Sample(3, 1, 0, W, 5, 3)]);
        s.MoveElement(G, first, second, _ => "mean");
        s.MoveElement(G, second, survivor, _ => "mean");
        Assert.Empty(s.ReadWindow(G, [1, 2, 3], [1], 0, W).SelectMany(r => r.Points));
        Assert.Equal(1, s.MergeConflicts(G));
    }

    [Fact]
    public void Telemetry_never_touches_graph_revisions()
    {
        // a second window of flows between the same pods changes telemetry only: the windows grow, the revisions of the master and the stored head do not move
        using var h = HostTests.NewHost();
        HostTests.Drive(h, windows: 2);
        long Windows() => ((InMemoryTelemetryStore)h.Engine.Telemetry).Dump(h.Engine.Master.Key).Count;
        var (head, stored, windows, digest) = (h.Engine.Master.Head, h.Engine.Store.Head(h.Engine.Master.Key).Head, Windows(), h.Engine.Master.State.Digest());
        HostTests.Flows(h, window: 2);
        Assert.True(Windows() > windows, "the third window wrote no telemetry");
        Assert.Equal((head, stored, digest), (h.Engine.Master.Head, h.Engine.Store.Head(h.Engine.Master.Key).Head, h.Engine.Master.State.Digest()));
    }

    [Fact]
    public void Delete_retracts_a_window_and_unknown_keys_read_as_empty()
    {
        var s = new InMemoryTelemetryStore();
        s.Append(G, [new Sample(1, 1, 0, W, 10, 1)]);
        s.Append(G, [new Sample(1, 1, 0, W, 0, 1, Deleted: true)]);
        Assert.Empty(s.ReadWindow(G, [1, 9], [1], 0, W)[0].Points);
        Assert.Equal(2, s.ReadWindow(G, [1, 9], [1], 0, W).Count);
    }

    [Theory]
    [InlineData(0, 100)] [InlineData(3, 7)] [InlineData(10, 11)] [InlineData(50, 50)] [InlineData(0, 0)]
    public void ScanWindow_visits_exactly_the_windows_ReadWindow_returns_in_the_same_order(long fromWindow, long toWindow)
    {
        var s = new InMemoryTelemetryStore();
        var samples = new List<Sample>();
        for (var e = 0; e < 4; e++) for (var p = 0; p < 2; p++) for (var w = 0; w < 20; w++) samples.Add(new Sample(e, p, w * W, W, e * 1000 + p * 100 + w, w));
        s.Append(G, [.. samples]);
        int[] els = [0, 1, 2, 3, 9], props = [0, 1];
        var seen = new List<(int, int, long, decimal)>();
        var n = s.ScanWindow(G, els, props, fromWindow * W, toWindow * W, (e, p, ws, v) => seen.Add((e, p, ws, v)));
        var expected = s.ReadWindow(G, els, props, fromWindow * W, toWindow * W).SelectMany(r => r.Points.Select(x => (r.Element, r.Prop, x.WindowStart, x.Value))).ToList();
        Assert.Equal(expected, seen);
        Assert.Equal(expected.Count, n);
    }

    /// <summary>Def. 7.13: a window [a, b) is kept while b is after the logical frontier minus the declared age. The frontier decides, not the newest window the store has seen; a dropped window is not a change for readers.</summary>
    [Theory]
    [InlineData(2, new[] { 1, 2, 3 })]            // frontier 2 W, age 2 W: the first window ended at W, which is after 0
    [InlineData(3, new[] { 2, 3 })]               // it ended at W, which is not after 3 W - 2 W
    [InlineData(5, new[] { 2, 3 })]               // the second ended at 4 W, after 3 W
    [InlineData(6, new[] { 3 })]                  // and is not after 4 W
    [InlineData(20, new int[0])]
    public void Retention_keeps_the_windows_that_end_after_the_frontier_minus_the_age(int frontierWindows, int[] kept)
    {
        var s = new InMemoryTelemetryStore();
        s.Append(G, [new Sample(1, 1, 0, W, 1, 1), new Sample(1, 1, 3 * W, W, 2, 2), new Sample(1, 1, 10 * W, W, 3, 3)]);
        Assert.Equal([1m, 2m, 3m], s.ReadWindow(G, [1], [1], 0, 20 * W)[0].Points.Select(p => p.Value));       // a newer window alone drops nothing
        s.ClosedWindows(G, 0, out var position);
        s.Retain(G, frontierWindows * W, 2 * W);
        Assert.Equal(kept.Select(v => (decimal)v), s.ReadWindow(G, [1], [1], 0, 20 * W).SelectMany(r => r.Points).Select(p => p.Value));
        Assert.Empty(s.ClosedWindows(G, position, out _));
        Assert.Equal(frontierWindows * W - 2 * W, s.RetainedFrom(G));
    }
}
