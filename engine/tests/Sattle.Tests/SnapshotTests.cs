using System.Text.Json;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>A seal is a snapshot of the whole engine state (operator state included): loaded into a fresh engine built from the same definitions it gives the same state, byte for byte when saved again, and the engine then continues exactly as the original would.</summary>
[Trait("Category", "Slow")]
public class SnapshotTests
{
    const long Min = 60_000_000_000;

    /// <summary>Records every input of a run as journal entries, so any number of engines can be fed the same inputs.</summary>
    sealed class Tape(Engine source) : IEngineInput
    {
        public readonly List<string> Entries = [];
        public Interner Interner => source.Interner;
        public Layout RowLayout(string s) => source.RowLayout(s);
        public void Ingest(string s, Batch rows, long seq) { Entries.Add(BatchCodec.Encode(s, seq, rows, source.Interner)); source.Ingest(s, rows, seq); }
        public void Advance(long l, long seq) { Entries.Add($"{{\"k\":\"a\",\"l\":{l},\"q\":{seq}}}"); source.Advance(l, seq); }
        public void RecordLoss(long t, long n, string reason = "sourceLoss") { Entries.Add(new System.Text.Json.Nodes.JsonObject { ["k"] = "l", ["t"] = t, ["n"] = n, ["r"] = reason }.ToJsonString()); source.RecordLoss(t, n, reason); }
    }

    static void Play(Engine e, IEnumerable<string> entries)
    {
        foreach (var body in entries)
        {
            using var d = JsonDocument.Parse(body); var r = d.RootElement;
            switch (r.GetProperty("k").GetString())
            {
                case "r": { var (src, seq, rows) = BatchCodec.Decode(body, e.RowLayout, e.Interner); e.Ingest(src, rows, seq); break; }
                case "a": e.Advance(r.GetProperty("l").GetInt64(), r.GetProperty("q").GetInt64()); break;
                case "l": e.RecordLoss(r.GetProperty("t").GetInt64(), r.GetProperty("n").GetInt64(), r.GetProperty("r").GetString()!); break;
            }
        }
    }

    /// <summary>A store that takes every commit: the engine state is under test here, not the log (the host test covers the log).</summary>
    sealed class AcceptingStore : IGraphStore
    {
        public HeadInfo Head(GraphKey g) => new(0, 0);
        public CommitResult Commit(CommitBatch b) => new CommitResult.Committed(b.ExpectedHead + b.Patches.Count);
        public IReadOnlyList<ReadResult> Read(GraphKey g, long r, IReadOnlyList<ElementKey> k) => [];
        public AdjacencyPage ScanAdjacency(GraphKey g, long r, string e, Direction d, string? t, int n, string? c) => new([], null);
        public IEnumerable<CommittedPatch> Follow(GraphKey g, long a) => [];
        public SealInfo Seal(GraphKey g, long r) => throw new NotSupportedException();
        public IEnumerable<Element> OpenSnapshot(GraphKey g, long r) => [];
        public void Dispose() { }
    }

    static EngineOptions Options => new() { Tenant = "lab", LateHorizonWindows = 3, StoreFactory = _ => new AcceptingStore() };

    static byte[] Plain(byte[] z) { using var gz = new System.IO.Compression.GZipStream(new MemoryStream(z), System.IO.Compression.CompressionMode.Decompress); using var m = new MemoryStream(); gz.CopyTo(m); return m.ToArray(); }

    static void AssertSameBytes(byte[] a, byte[] b)
    {
        var diff = StateSnapshot.FirstDifference(a, b);
        if (diff is null) return;
        Assert.Fail("snapshots differ: " + diff);
        byte[] x = Plain(a), y = Plain(b);
        var ha = StateSnapshot.Histogram(a); var hb = StateSnapshot.Histogram(b);
        var types = ha.Keys.Union(hb.Keys).Where(k => ha.GetValueOrDefault(k) != hb.GetValueOrDefault(k)).Select(k => $"{k}: {ha.GetValueOrDefault(k)} against {hb.GetValueOrDefault(k)}").ToList();
        if (types.Count > 0) Assert.Fail("object counts differ: " + string.Join("; ", types));
        var i = 0; while (i < x.Length && i < y.Length && x[i] == y[i]) i++;
        if (i == x.Length && i == y.Length) return;
        string Ctx(byte[] z) => new string(z.Skip(Math.Max(0, i - 120)).Take(200).Select(c => c >= 32 && c < 127 ? (char)c : '.').ToArray());
        string Hex(byte[] z) => Convert.ToHexString(z.Skip(i - 12).Take(60).ToArray());
        Assert.Fail($"snapshots differ at byte {i} of {x.Length}/{y.Length}:\n  A: {Ctx(x)}\n  B: {Ctx(y)}\n  A hex: {Hex(x)}\n  B hex: {Hex(y)}");
    }

    static byte[] Save(Engine e) => StateSnapshot.Save(e.StateRootOf());
    static void Restore(Engine fresh, byte[] snap) { var root = fresh.StateRootOf(); StateSnapshot.Load(snap, root); fresh.AdoptState(root); }

    static (Catalog Cat, string Space, List<string> Tape, string Digest) Record(string dir, string cluster, bool estate)
    {
        var cat = estate ? EstateTests.LoadCatalog(demo: true) : Repo.LoadExamples(out _);
        var space = cat.Spaces.Keys.Single();
        var e = new Engine(cat, space, Options);
        var tape = new Tape(e);
        new Replay(tape, cat.Spaces[space], AdapterRegistry.Create(cluster), new ReplayOptions { ChunkRows = 40, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = cluster }).Run(dir);
        return (cat, space, tape.Entries, e.StateDigest());
    }

    static readonly Lazy<(Catalog Cat, string Space, List<string> Tape, string Digest)> Estate = new(() => Record(EstateTests.EstateDir, "synth", estate: true));
    static readonly Lazy<(Catalog Cat, string Space, List<string> Tape, string Digest)> Lab = new(() => Record(Repo.Fixtures, Repo.ClusterOf(Repo.Fixtures), estate: false));

    public static IEnumerable<object[]> Runs() => [["estate", 0.25], ["estate", 0.5], ["estate", 0.9], ["lab", 0.3], ["lab", 0.7]];

    [Theory, MemberData(nameof(Runs))]
    public void A_snapshot_loaded_into_a_fresh_engine_saves_to_the_same_bytes_and_the_engine_continues_like_the_original(string which, double at)
    {
        var (cat, space, tape, fullDigest) = which == "estate" ? Estate.Value : Lab.Value;
        var k = (int)(tape.Count * at);
        var opts = Options;
        using var original = new Engine(cat, space, opts);
        Play(original, tape.Take(k));
        var snap = Save(original);
        using var restored = new Engine(cat, space, opts);
        Restore(restored, snap);
        Assert.Equal(original.StateDigest(), restored.StateDigest());
        AssertSameBytes(snap, Save(restored));                                                  // the same state, field for field
        foreach (var g in original.Graphs) Assert.Equal(g.Head, restored.Graph(g.Name).Head);
        Play(restored, tape.Skip(k));
        Play(original, tape.Skip(k));
        Assert.Equal(fullDigest, restored.StateDigest());                                      // continuing from the snapshot equals the uninterrupted run
        Assert.Equal(original.StateDigest(), restored.StateDigest());
        foreach (var g in original.Graphs) { Assert.Equal(g.Head, restored.Graph(g.Name).Head); Assert.Equal(g.State.Digest(), restored.Graph(g.Name).State.Digest()); }   // (the bytes of two snapshots may differ here: hash containers enumerate in another order after other removals)
    }
}
