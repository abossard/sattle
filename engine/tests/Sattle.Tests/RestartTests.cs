using Microsoft.Data.Sqlite;
using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>A served space survives a process restart from its durable store alone: the journal is replayed, every recomputed commit is compared with the stored one, and a subscription that resumes by sequence number sees neither a gap nor a duplicate.</summary>
[Trait("Category", "Slow")]
public class RestartTests
{
    const long Min = 60_000_000_000;
    static string Fixture => Path.Combine(Repo.Root, "engine", "tests", "Sattle.Tests", "fixtures", "deploy-health");
    static IReadOnlyList<Sattle.Language.SourceFile> Files => Sattle.Language.Catalog.ReadDirectory(Repo.Definitions);
    static HostOptions Options => new() { Cluster = "synth" };

    public static IEnumerable<object[]> Stores() => StoreConformanceTests.CosmosEndpoint is null ? [["memory"], ["sqlite"]] : [["memory"], ["sqlite"], ["cosmos"]];

    /// <summary>A store that can be closed and opened again, like a process restart: the memory store is the same object, the others reopen their file or database.</summary>
    public sealed class Durable(string kind) : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), "sattle-restart-" + Guid.NewGuid().ToString("N") + ".db");
        readonly string database = "r" + Guid.NewGuid().ToString("N")[..12];
        readonly InMemoryGraphStore memory = new();
        IGraphStore? open;
        public IGraphStore Open() => open = kind switch
        {
            "memory" => memory,
            "cosmos" => Sattle.Cli.CosmosGraphStore.Open(StoreConformanceTests.CosmosEndpoint!, database, false),
            _ => new SqliteGraphStore(path),
        };
        public void Close() { if (kind != "memory") open?.Dispose(); open = null; }
        public string Path_ => path;
        public void Dispose()
        {
            Close();
            if (kind == "cosmos") { using var s = Sattle.Cli.CosmosGraphStore.Open(StoreConformanceTests.CosmosEndpoint!, database, true); }
            else foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f);
        }
    }

    static void Feed(SpaceHost h) => new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 1, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" }).Run(Fixture);

    static string Space(SpaceHost h) => h.SpaceInfo().ToJsonString();

    [Theory, MemberData(nameof(Stores))]
    public void A_restarted_space_has_the_same_state_digest_space_and_sequence(string kind)
    {
        using var d = new Durable(kind);
        string digest, space; long seq;
        using (var h = SpaceHost.OpenDurable(d.Open(), Files, Options))
        {
            Feed(h); h.Finish();
            digest = h.StateDigest(); space = Space(h); seq = h.Seq;
            Assert.False(h.Restored);
        }
        d.Close();
        using var r = SpaceHost.OpenDurable(d.Open(), null, Options);
        Assert.True(r.Restored);
        Assert.Equal(digest, r.StateDigest());
        Assert.Equal(space, Space(r));
        Assert.Equal(seq, r.Seq);
        Assert.True(r.Done);
        Assert.True(r.Verified > 0);
        Assert.All(r.Engine.Graphs, g => Assert.Equal(r.Head(g.Name), g.Head));
    }

    [Theory, MemberData(nameof(Stores))]
    public void A_subscription_resumed_after_a_restart_gets_exactly_the_events_it_missed(string kind)
    {
        using var d = new Durable(kind);
        long seq; List<string> tail;
        using (var h = SpaceHost.OpenDurable(d.Open(), Files, Options))
        {
            Feed(h);
            seq = h.Seq;
            var s = h.Subscribe(null, false, seq - 4);
            tail = Drain(s);
            Assert.Equal(4, tail.Count);
            h.Unsubscribe(s);
        }
        d.Close();
        using var r = SpaceHost.OpenDurable(d.Open(), null, Options);
        Assert.Equal(seq, r.Seq);
        var up = r.Subscribe(null, false, seq);
        Assert.Empty(Drain(up));                                       // nothing missed: no gap, no snapshot, no repeat
        r.Unsubscribe(up);
        var missed = r.Subscribe(null, false, seq - 4);
        Assert.Equal(tail, Drain(missed));                             // the same four events, byte for byte
        r.Unsubscribe(missed);
    }

    static List<string> Drain(Subscription s)
    {
        var l = new List<string>();
        while (s.Messages.Reader.TryRead(out var m)) l.Add(m);
        return l;
    }

    [Theory, MemberData(nameof(Stores))]
    public void A_definition_commit_survives_a_restart_and_the_space_keeps_going_after_it(string kind)
    {
        using var d = new Durable(kind);
        string digest; long rev, seq;
        var edited = File.ReadAllText(Path.Combine(Repo.Definitions, "acme.deployhealth.sattle")).Replace("param Integer = 0", "param Integer = 1");
        Assert.NotEqual(File.ReadAllText(Path.Combine(Repo.Definitions, "acme.deployhealth.sattle")), edited);
        using (var h = SpaceHost.OpenDurable(d.Open(), Files, Options))
        {
            Feed(h);
            h.Commit("acme.deployhealth", edited, h.CatalogRevision);
            digest = h.StateDigest(); rev = h.CatalogRevision; seq = h.Seq;
            h.CreateDraft(null);                                      // a draft consumes a sequence number and is not stored
            seq = h.Seq;
        }
        d.Close();
        using (var r = SpaceHost.OpenDurable(d.Open(), null, Options))
        {
            Assert.Equal(digest, r.StateDigest());
            Assert.Equal(rev, r.CatalogRevision);
            Assert.Equal(seq, r.Seq);
            Assert.Contains("param Integer = 1", r.Definition("acme.deployhealth"));
            r.Advance(100 * 365 * 24 * 60 * Min, 1_000_000);          // the restored space accepts more input and journals it
            digest = r.StateDigest(); seq = r.Seq;
        }
        d.Close();
        using var again = SpaceHost.OpenDurable(d.Open(), null, Options);
        Assert.Equal(digest, again.StateDigest());
        Assert.Equal(seq, again.Seq);
    }

    [Fact]
    public void A_store_that_disagrees_with_its_journal_is_refused()
    {
        using var d = new Durable("sqlite");
        using (var h = SpaceHost.OpenDurable(d.Open(), Files, Options)) Feed(h);
        d.Close();
        using (var c = new SqliteConnection($"Data Source={d.Path_};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM journal WHERE n IN (SELECT MAX(n) FROM journal WHERE body LIKE '{\"k\":\"r\"%')"; cmd.ExecuteNonQuery();
        }
        var e = Assert.ThrowsAny<Exception>(() => SpaceHost.OpenDurable(d.Open(), null, Options));
        Assert.Contains("journal reproduces", e.Message);
    }

    [Fact]
    public void The_journal_codec_round_trips_text_numbers_booleans_presence_and_weights()
    {
        using var h = HostTests.NewHost();
        var it = h.Interner; var layout = h.RowLayout("pods");
        var w = new RowWriter(layout, it);
        w.Begin(); w.Text("cluster", "c"); w.Text("uid", "u1"); w.Text("namespace", "ns"); w.Text("name", "p1"); w.Int(w.Col("restarts"), 3_000_000_000_000_000_000); w.Absent(w.Col("phase"));
        w.Begin(-1); w.Text("cluster", "c"); w.Text("uid", "u2"); w.Text("namespace", "ns"); w.Text("name", "\u00fc\"q\n"); w.Int(w.Col("ready"), 1); w.Text("phase", "Running");
        var b = w.Build()!;
        var body = BatchCodec.Encode("pods", 7, b, it);
        var other = new Interner();
        var (src, seq, back) = BatchCodec.Decode(body, h.RowLayout, other);
        Assert.Equal(("pods", 7L), (src, seq));
        Assert.Equal(body, BatchCodec.Encode("pods", 7, back, other));
        Assert.Equal(-1, back.Weight(1));
        Assert.False(back.Cols[layout.Index("phase")].Present(0));
    }
}
