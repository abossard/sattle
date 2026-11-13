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

    public static IEnumerable<object[]> Stores() => StoreConformanceTests.CosmosEndpoint is null ? [["memory"], ["headOnly"], ["sqlite"]] : [["memory"], ["headOnly"], ["sqlite"], ["cosmos"]];

    /// <summary>A store that can be closed and opened again, like a process restart: the memory store is the same object, the others reopen their file or database.</summary>
    public sealed class Durable(string kind) : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), "sattle-restart-" + Guid.NewGuid().ToString("N") + ".db");
        readonly string database = "r" + Guid.NewGuid().ToString("N")[..12];
        readonly InMemoryGraphStore memory = new();
        readonly HeadOnlyGraphStore headOnly = new();
        IGraphStore? open;
        public IGraphStore Open() => open = kind switch
        {
            "memory" => memory,
            "headOnly" => headOnly,
            "cosmos" => Sattle.Cli.CosmosGraphStore.Open(StoreConformanceTests.CosmosEndpoint!, database, false),
            _ => new SqliteGraphStore(path),
        };
        public void Close() { if (kind is not ("memory" or "headOnly")) open?.Dispose(); open = null; }
        public string Path_ => path;
        public void Dispose()
        {
            Close();
            if (kind == "cosmos") { using var s = Sattle.Cli.CosmosGraphStore.Open(StoreConformanceTests.CosmosEndpoint!, database, true); }
            else foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f);
        }
    }

    /// <summary>Opens a space again on its store, as a restart does, and checks that the latest seal was used if the store has one. A restart that falls back to replaying the whole journal gives the same state, so only this tells the two apart (code review 2: the restart tests passed with the seal restore forced to refuse).</summary>
    public static SpaceHost Reopen(IGraphStore store, HostOptions options)
    {
        var h = SpaceHost.OpenDurable(store, null, options);
        Assert.NotNull(h.Restart);
        Assert.True(((IJournalStore)store).SealLatest() is not null == h.Restart!.FromSeal, $"the store {(h.Restart.FromSeal ? "has no seal" : "has a seal that was not used: " + h.Restart.SealRefusal)}");
        return h;
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
            Assert.Null(h.Restart);
        }
        d.Close();
        using var r = SpaceHost.OpenDurable(d.Open(), null, Options);
        Assert.NotNull(r.Restart);
        Assert.Equal(digest, r.StateDigest());
        Assert.Equal(space, Space(r));
        Assert.Equal(seq, r.Seq);
        Assert.True(r.Done);
        Assert.True(r.Restart!.Verified > 0);
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

    /// <summary>Every kind of journal entry has one stored form, the one journals have always had, and reads back to an entry with that form.</summary>
    [Theory]
    [InlineData("advance", """{"k":"a","l":120000000000,"q":7}""")]
    [InlineData("progress", """{"k":"g","s":"flows","u":120000000000,"q":8}""")]
    [InlineData("loss", """{"k":"l","t":120000000000,"n":5,"r":"sourceLoss"}""")]
    [InlineData("finish", """{"k":"f"}""")]
    [InlineData("liveStart", """{"k":"L","o":"{\u0022c\u0022:40,\u0022a\u0022:60000000000,\u0022l\u0022:60000000000,\u0022f\u0022:false,\u0022k\u0022:\u0022synth\u0022}"}""")]
    [InlineData("envelope", """{"k":"e","s":"hub","t":"{}","p":"{\u0022n\u0022:3}"}""")]
    [InlineData("skipped", """{"k":"p","s":"hub","p":"{\u0022n\u0022:4}"}""")]
    [InlineData("flush", """{"k":"F"}""")]
    [InlineData("sequenceStep", """{"k":"s"}""")]
    [InlineData("commit", """{"k":"c","e":12,"t":{"acme.svc":"module acme.svc @ 1.0.0;"}}""")]
    [InlineData("commitRefused", """{"k":"x"}""")]
    [InlineData("header", """{"k":"h","tenant":"lab","lateHorizon":null,"cluster":"synth","space":"operations","mode":"replay","generation":0,"files":[{"p":"a.sattle","t":"module a @ 1.0.0;"}]}""")]
    public void A_journal_entry_has_the_stored_form_it_always_had_and_reads_back(string kind, string stored)
    {
        Entry entry = kind switch
        {
            "advance" => new Entry.Advance(2 * Min, 7),
            "progress" => new Entry.Progress("flows", 2 * Min, 8),
            "loss" => new Entry.Loss(2 * Min, 5, "sourceLoss"),
            "finish" => new Entry.Finish(),
            "liveStart" => new Entry.LiveStart(new ReplayOptions { ChunkRows = 40, AdvanceEveryNs = Min, LatenessNs = Min, FlushAtEnd = false, Cluster = "synth" }),
            "envelope" => new Entry.Envelope("hub", "{}", "{\"n\":3}"),
            "skipped" => new Entry.Skipped("hub", "{\"n\":4}"),
            "flush" => new Entry.Flush(),
            "sequenceStep" => new Entry.SequenceStep(),
            "commit" => new Entry.Commit(12, new Dictionary<string, string> { ["acme.svc"] = "module acme.svc @ 1.0.0;" }),
            "commitRefused" => new Entry.CommitRefused(),
            _ => new Entry.Header("lab", null, "synth", "operations", "replay", [new Sattle.Language.SourceFile("a.sattle", "module a @ 1.0.0;")]),
        };
        var it = new Interner();
        Assert.Equal(stored, entry.Text(it));
        var back = Entry.Parse(stored, _ => throw new InvalidOperationException("no rows here"), it);
        Assert.Equal(entry.GetType(), back.GetType());
        Assert.Equal(stored, back.Text(it));
    }

    /// <summary>The journal a served space writes (rows, advances, progress, a definition commit, a live input with a flush and a skipped envelope, the end) reads back entry by entry to the text that was stored.</summary>
    [Fact]
    public void Every_entry_of_a_written_journal_reads_back_to_its_stored_text()
    {
        using var d = new Durable("memory");
        var kinds = new HashSet<char>();
        using (var h = SpaceHost.OpenDurable(d.Open(), Files, Options))
        {
            Feed(h);
            var text = File.ReadAllText(Path.Combine(Repo.Definitions, "acme.deployhealth.sattle"));
            h.Commit("acme.deployhealth", text.Replace("param Integer = 0", "param Integer = 1"), h.CatalogRevision);
            h.StartLive(new ReplayOptions { ChunkRows = 40, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth", FlushAtEnd = false });
            var envelope = File.ReadLines(Directory.EnumerateFiles(Fixture, "*.ndjson", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First()).First();
            h.LiveEnvelope("hub", envelope, "{\"n\":0}");
            h.LiveEnvelope("hub", envelope, "{\"n\":1}");                 // the same key again: skipped
            h.LiveFlush();
            h.Finish();
            var journal = (IJournalStore)d.Open();
            foreach (var stored in journal.JournalRead())
            {
                kinds.Add(Entry.KindOf(stored));
                Assert.Equal(stored, Entry.Parse(stored, h.RowLayout, h.Interner).Text(h.Interner));
            }
        }
        Assert.Equal("FLacefghpr", new string([.. kinds.Order()]));                 // every kind but a draft's sequence step and a refused commit, which the theory above covers
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
