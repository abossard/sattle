using Sattle.Reference;
using Sattle.Runtime;

namespace Sattle.Differential;

/// <summary>T-SEAL (premise S1 of Proposition 11.10): on every world's generated log, the engine state is sealed at a generated position and restored into a new engine; the restored engine equals the one that went on in every field (the whole state snapshot: interner, identity catalog, support counts, fold states, windows, sink support), and both continue input by input to equal states.</summary>
public class SealRestoreTests
{
    public static TheoryData<string> WorldNames => [.. Worlds.All.Select(w => w.Name)];

    static void Feed(Engine e, Input input, long seq)
    {
        switch (input)
        {
            case AdvanceInput a: e.Advance(a.Frontier, seq); break;
            case ProgressInput p: e.Progress(p.Source, p.Until, seq); break;
            case LossInput l: e.RecordLoss(l.Time, 1); break;
            case RowInput r: EngineRun.Ingest(e, [r], seq); break;
        }
    }

    static byte[] Bytes(Engine e) => StateSnapshot.Save(e.StateRootOf());

    /// <summary>A commit as text: graph, revision range, the edits, the window changes with their values (interned keys read back), and the conflicts.</summary>
    static string Text(CommitEvent c) => string.Join("|", c.GraphId, c.Patch is { } p ? $"{p.From}->{p.To}:" + string.Join(";", p.Edits.Select(e => $"{e.Key}:{e.Before}=>{e.After}")) : "-",
        string.Join(";", c.Windows.Select(w => $"{c.Interner.Get(w.Key.Element)}/{c.Interner.Get(w.Key.Prop)}/{w.Key.WindowStart}:{w.Old}=>{w.New}")), string.Join(";", c.Conflicts.Select(x => x.Text)));

    /// <summary>A store that accepts every commit and keeps nothing: the engine's state is under test here, not the commit log.</summary>
    sealed class AcceptingStore : IGraphStore
    {
        public HeadInfo Head(GraphKey g) => new(0, 0);
        public CommitResult Commit(CommitBatch b) => new CommitResult.Committed(b.ExpectedHead + b.Patches.Count);
        public IReadOnlyList<ReadResult> Read(GraphKey g, long r, IReadOnlyList<Sattle.Core.ElementKey> k) => [];
        public AdjacencyPage ScanAdjacency(GraphKey g, long r, string e, Direction d, string? t, int n, string? c) => new([], null);
        public IEnumerable<CommittedPatch> Follow(GraphKey g, long a) => [];
        public SealInfo Seal(GraphKey g, long r) => throw new NotSupportedException();
        public IEnumerable<Sattle.Core.Element> OpenSnapshot(GraphKey g, long r) => [];
        public void Dispose() { }
    }

    /// <summary>#115: the joins of a graph that read one input store its rows once. A seal restore keeps that table as one object that every one of them reads, and gives the same digest.</summary>
    [Theory]
    [InlineData("two joins on one input")]
    public void A_restore_keeps_an_input_that_several_joins_read_as_one_table(string world)
    {
        var w = Worlds.All.Single(x => x.Name == world);
        var space = w.Catalog.Spaces.Keys.Single();
        var options = new EngineOptions { WindowRetentionNs = w.RetentionNs, StoreFactory = _ => new AcceptingStore() };
        using var original = new Engine(w.Catalog, space, options);
        var log = w.Log(0, 30);
        for (var i = 0; i < log.Length; i++) Feed(original, log[i], i + 1);
        using var restored = new Engine(w.Catalog, space, options);
        var root = restored.StateRootOf(); StateSnapshot.Load(Bytes(original), root); restored.AdoptState(root);
        static object Field(object o, string name) => o.GetType().GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(o)!;
        static object[] Tables(Engine e) => [.. e.Graphs.SelectMany(g => g.Plan.Ops).Where(o => o.GetType().Name == "JoinOp").SelectMany(o => new[] { "left", "right" }.Select(side => Field(Field(o, side), "Rows")))];
        static int Distinct(object[] tables) => tables.Distinct(ReferenceEqualityComparer.Instance).Count();
        var (before, after) = (Tables(original), Tables(restored));
        Assert.True(Distinct(before) < before.Length, "no input is read by two joins");
        Assert.Equal(Distinct(before), Distinct(after));
        Assert.Equal(original.StateDigest(), restored.StateDigest());
    }

    [Theory, MemberData(nameof(WorldNames))]
    public void A_seal_restore_equals_a_full_replay_in_every_field(string world)
    {
        var w = Worlds.All.Single(x => x.Name == world);
        var space = w.Catalog.Spaces.Keys.Single();
        for (var seed = 0; seed < 3; seed++)
        {
            var log = w.Log(seed, 30);
            var cut = new Random(seed * 7 + world.Length).Next(1, log.Length);
            var options = new EngineOptions { WindowRetentionNs = w.RetentionNs, StoreFactory = _ => new AcceptingStore() };
            using var original = new Engine(w.Catalog, space, options);
            for (var i = 0; i < cut; i++) Feed(original, log[i], i + 1);
            Engine Restore(byte[] bytes) { var e = new Engine(w.Catalog, space, options); var r = e.StateRootOf(); StateSnapshot.Load(bytes, r); e.AdoptState(r); return e; }
            using var restored = Restore(Bytes(original));
            Assert.Equal(original.StateDigest(), restored.StateDigest());
            var d1 = StateSnapshot.FirstDifference(Bytes(original), Bytes(restored));
            Assert.True(d1 is null, $"{world}, seed {seed}, sealed after {cut}: {d1}");
            // both continue with equal commits: the same patches, window changes and conflicts, input by input
            var (fromOriginal, fromRestored) = (new List<string>(), new List<string>());
            original.Committed += c => fromOriginal.Add(Text(c)); restored.Committed += c => fromRestored.Add(Text(c));
            for (var i = cut; i < log.Length; i++)
            {
                Feed(original, log[i], i + 1); Feed(restored, log[i], i + 1);
                Assert.Equal(fromOriginal, fromRestored);
                Assert.Equal(original.StateDigest(), restored.StateDigest());
            }
            // and a second seal of each now restores to the same state (the snapshots differ only in hash-table layout, which a restore does not carry)
            Assert.Equal(original.StateDigest(), Restore(Bytes(original)).StateDigest());
            Assert.Equal(Restore(Bytes(original)).StateDigest(), Restore(Bytes(restored)).StateDigest());
        }
    }
}
