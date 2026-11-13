using Sattle.Cli;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>One quick case each of restart from a journal, restart from a seal, and a live run resumed from its store, on the small deploy-health fixture and the memory store. The full coverage (estate fixture, SQLite, Cosmos, corrupt seals, definition commits, killed live runs, duplicates) is in RestartTests, SealTests and LiveResumeTests, which are in the Slow category.</summary>
public class RestartSmokeTests
{
    const long Min = 60_000_000_000;
    static string Fixture => Path.Combine(Repo.Root, "engine", "tests", "Sattle.Tests", "fixtures", "deploy-health");
    static IReadOnlyList<Sattle.Language.SourceFile> Files => Sattle.Language.Catalog.ReadDirectory(Repo.Definitions);
    static HostOptions Options => new() { Cluster = "synth" };

    [Fact]
    public void A_space_restarts_from_its_journal_and_from_a_seal_with_the_same_digest_and_sequence()
    {
        var store = new InMemoryGraphStore();
        string digest; long seq;
        using (var h = SpaceHost.OpenDurable(store, Files, Options))
        {
            new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 1, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" }).Run(Fixture);
            h.Finish(); h.Seal();
            digest = h.StateDigest(); seq = h.Seq;
        }
        using (var fromSeal = SpaceHost.OpenDurable(store, null, Options)) { Assert.True(fromSeal.Restart!.FromSeal); Assert.Equal(digest, fromSeal.StateDigest()); Assert.Equal(seq, fromSeal.Seq); }
        using (var fromJournal = SpaceHost.OpenDurable(store, null, Options with { UseSeal = false })) { Assert.False(fromJournal.Restart!.FromSeal); Assert.Equal(digest, fromJournal.StateDigest()); Assert.Equal(seq, fromJournal.Seq); }
    }

    /// <summary>A seal of the previous format (before telemetry pairs and per-own support) is refused with both versions named, and the journal is replayed to the same state.</summary>
    [Fact]
    public void A_seal_of_another_format_is_refused_with_both_versions_named()
    {
        var store = new InMemoryGraphStore();
        string digest;
        using (var h = SpaceHost.OpenDurable(store, Files, Options))
        {
            new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 1, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" }).Run(Fixture);
            h.Finish(); h.Seal();
            digest = h.StateDigest();
        }
        var j = (IJournalStore)store; var seal = j.SealLatest()!;
        var meta = System.Text.Json.Nodes.JsonNode.Parse(seal.Meta)!.AsObject(); meta.Remove("format");          // what a build before format 2 wrote
        j.SealPut(seal.Position, meta.ToJsonString(), seal.Data);
        using var restarted = SpaceHost.OpenDurable(store, null, Options);
        Assert.False(restarted.Restart!.FromSeal);
        Assert.Contains($"seal format 1, this build reads seal format {SpaceHost.SealFormat}", restarted.Restart!.SealRefusal);
        Assert.Equal(digest, restarted.StateDigest());
    }

    [Fact]
    public void A_live_run_killed_and_restarted_from_its_store_ends_where_an_uninterrupted_one_does()
    {
        var events = LiveReproducibilityTests.Arrival(Fixture, seed: 2, distance: 4);
        var ro = new ReplayOptions { ChunkRows = 40, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth", FlushAtEnd = false };
        string whole;
        using (var h = SpaceHost.OpenDurable(new InMemoryGraphStore(), Files, Options)) { LiveInput.Run(h, new LiveResumeTests.FakeSource(events), ro, CancellationToken.None, flushEvery: TimeSpan.FromHours(1)); whole = h.StateDigest(); }
        var store = new InMemoryGraphStore(); var killed = events.Count / 2 + 1;
        using (var h = SpaceHost.OpenDurable(store, Files, Options with { SealEvery = 3 }))       // the fixture has 12 envelopes: a seal every 10 entries was never taken before the kill
            Assert.Throws<LiveResumeTests.Crash>(() => LiveInput.Run(h, new LiveResumeTests.FakeSource(events, crashAt: killed), ro, CancellationToken.None, flushEvery: TimeSpan.FromHours(1)));
        using var r = RestartTests.Reopen(store, Options with { SealEvery = 3 });
        Assert.True(r.Restart!.FromSeal, "the run took a seal before it was killed, and the restart did not use it");
        var counts = LiveInput.Run(r, new LiveResumeTests.FakeSource(events), ro, CancellationToken.None, flushEvery: TimeSpan.FromHours(1));
        Assert.Equal(events.Count - killed, counts.Consumed);
        Assert.Equal(whole, r.StateDigest());
    }

    static (string Path, string Digest) SealedSqlite()
    {
        var path = Path.Combine(Path.GetTempPath(), "sattle-verify-" + Guid.NewGuid().ToString("N") + ".db");
        using var store = new SqliteGraphStore(path);
        using var h = SpaceHost.OpenDurable(store, Files, Options with { SealEvery = 8 });
        new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 1, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" }).Run(Fixture);
        h.Finish();
        return (path, h.StateDigest());
    }

    [Fact]
    public void A_restart_says_what_came_from_the_seal_and_what_it_recomputed()
    {
        var (path, digest) = SealedSqlite();
        try
        {
            using var s1 = new SqliteGraphStore(path);
            using var fromSeal = SpaceHost.OpenDurable(s1, null, Options);
            Assert.True(fromSeal.Restart!.FromSeal);
            Assert.True(fromSeal.Restart!.SealedRevisions > 0);
            Assert.Equal(fromSeal.Engine.Graphs.Sum(g => g.Head), fromSeal.Restart!.SealedRevisions + fromSeal.Restart!.Recomputed);        // the heads are the sealed revisions plus those the tail recomputed
            Assert.True(fromSeal.Restart!.Recomputed < fromSeal.Engine.Graphs.Sum(g => g.Head));
            var text = fromSeal.RestoreSummary();
            Assert.Contains("loaded from the seal at journal entry", text); Assert.Contains("were not recomputed", text); Assert.Contains("entries before the seal were not read", text);
            Assert.DoesNotContain("reproduced and equal", text);
            using var s2 = new SqliteGraphStore(path);
            using var verified = SpaceHost.OpenDurable(s2, null, Options with { VerifyJournal = true });
            Assert.False(verified.Restart!.FromSeal); Assert.True(verified.Restart!.SealChecked);
            Assert.Equal(verified.Engine.Graphs.Sum(g => g.Head), verified.Restart!.Recomputed);                                     // everything recomputed
            Assert.Equal(digest, verified.StateDigest());
            Assert.Contains("replayed all", verified.RestoreSummary()); Assert.Contains("equals the seal's digest", verified.RestoreSummary());
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f); }
    }

    [Fact]
    public void An_entry_deleted_from_the_journal_before_the_seal_is_not_seen_by_a_seal_restart_and_is_found_by_the_verifying_one()
    {
        var (path, _) = SealedSqlite();
        try
        {
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM journal WHERE n = (SELECT MIN(n) FROM journal WHERE n > 2 AND body LIKE '{\"k\":\"r\"%')"; Assert.Equal(1, cmd.ExecuteNonQuery());
            }
            using (var s1 = new SqliteGraphStore(path)) using (var seal = SpaceHost.OpenDurable(s1, null, Options)) Assert.True(seal.Restart!.FromSeal);          // the seal and the tail are intact: it starts, and says it did not read the earlier entries
            using var s2 = new SqliteGraphStore(path);
            Assert.ThrowsAny<Exception>(() => SpaceHost.OpenDurable(s2, null, Options with { VerifyJournal = true }));
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f); }
    }
}

