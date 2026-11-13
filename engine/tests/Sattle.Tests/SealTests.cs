using Microsoft.Data.Sqlite;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>A seal is the state of the whole space after some journal entries. A restart loads the latest seal, checks its digests and replays only the entries after it; a seal it cannot use is refused and the journal is replayed instead.</summary>
[Trait("Category", "Slow")]
public class SealTests
{
    const long Min = 60_000_000_000;
    static HostOptions Options(long sealEvery) => new() { Cluster = "synth", SealEvery = sealEvery };
    static List<SourceFile> Files => EstateTests.Files(demo: true);

    public static IEnumerable<object[]> Stores() => RestartTests.Stores();

    static void Feed(SpaceHost h) => new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 30, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" }).Run(EstateTests.EstateDir);

    static string Space(SpaceHost h) => h.SpaceInfo().ToJsonString();

    /// <summary>A few more inputs after a restart, the same for every host that is compared.</summary>
    static void MoreInput(SpaceHost h)
    {
        var w = new RowWriter(h.RowLayout("flows"), h.Interner);
        var t = new DateTimeOffset(2026, 1, 1, 10, 20, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1_000_000L;
        for (var i = 0; i < 6; i++)
        {
            w.Begin(); w.Instant("time", t + i * 1_000_000_000L); w.Text("cluster", "synth"); w.Text("verdict", i % 2 == 0 ? "FORWARDED" : "DROPPED");
            w.Text("srcNamespace", "shop"); w.Text("srcPod", "web-7d4-1"); w.Text("dstNamespace", "shop"); w.Text("dstPod", "api-5c9-1");
        }
        h.Ingest("flows", w.Build()!, 9_000_000);
        h.Advance(t + 5 * Min, 9_000_001);
    }

    /// <summary>Rewrite 1, step 6: projections written in KQL are definition files of the space like the module texts. A host that is given them and a source that reads them keeps them through a definition commit of another module and through a restart, from the journal alone and from a seal: the catalog after it is the catalog before it. A host that is given them without a source refuses to start, by the name of the file.</summary>
    [Theory]
    [InlineData(0)] [InlineData(25)]
    public void Projections_in_KQL_stay_with_the_space_through_a_definition_commit_and_a_restart(long sealEvery)
    {
        List<SourceFile> files = [.. Catalog.ReadDirectory(Repo.Definitions), .. Sattle.Kql.KqlFiles.ReadDirectory(Repo.Definitions)];
        Assert.Contains(files, f => f.Path.EndsWith(".kql", StringComparison.Ordinal));
        var options = Options(sealEvery) with { Projections = new Sattle.Kql.KqlSource() };
        string plainCatalog, plainState;
        using (var plain = new SpaceHost(Catalog.ReadDirectory(Repo.Definitions), Options(0))) { Feed(plain); plainCatalog = plain.Catalog.Graph.Digest(); plainState = plain.StateDigest(); }
        using var d = new RestartTests.Durable("memory");
        string catalog, state;
        var edited = files.First(f => f.Path.EndsWith("acme.deployhealth.sattle", StringComparison.Ordinal)).Text.Replace("param Integer = 0", "param Integer = 1");
        using (var h = SpaceHost.OpenDurable(d.Open(), files, options))
        {
            Feed(h);
            Assert.NotEqual(plainCatalog, h.Catalog.Graph.Digest());                               // the projections came from the .kql files
            Assert.Equal(plainState, h.StateDigest());                                             // and give the state of today's text
            var svc = h.Catalog.Modules["acme.svc"].Digest;
            h.Commit("acme.deployhealth", edited, h.CatalogRevision);
            Assert.Equal(svc, h.Catalog.Modules["acme.svc"].Digest);                               // another module's commit keeps them
            catalog = h.Catalog.Graph.Digest(); state = h.StateDigest();
        }
        d.Close();
        using (var r = RestartTests.Reopen(d.Open(), options))
        {
            Assert.Equal(sealEvery > 0, r.Restart!.FromSeal);
            Assert.Equal((catalog, state), (r.Catalog.Graph.Digest(), r.StateDigest()));
        }
        d.Close();
        Assert.Contains("no module read", Assert.ThrowsAny<Exception>(() => SpaceHost.OpenDurable(d.Open(), null, Options(sealEvery) with { UseSeal = false })).Message);      // the same store, opened without a source
        Assert.Contains("no module read", Assert.Throws<InvalidOperationException>(() => new SpaceHost(files, Options(0))).Message);
    }

    /// <summary>Six flows between two pods at a given minute, with the given verdicts.</summary>
    static void Flows(SpaceHost h, int minute, long seq, params string[] verdicts)
    {
        var w = new RowWriter(h.RowLayout("flows"), h.Interner);
        var t = new DateTimeOffset(2026, 1, 1, 10, minute, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1_000_000L;
        for (var i = 0; i < verdicts.Length; i++)
        {
            w.Begin(); w.Instant("time", t + i * 1_000_000_000L); w.Text("cluster", "synth"); w.Text("verdict", verdicts[i]);
            w.Text("srcNamespace", "shop"); w.Text("srcPod", "web-7d4-1"); w.Text("dstNamespace", "shop"); w.Text("dstPod", "api-5c9-1");
        }
        h.Ingest("flows", w.Build()!, seq);
        h.Advance(t + 5 * Min, seq + 1);
    }

    /// <summary>Code review 2, defect 2: a host restored from a seal goes on like one that never restarted, also after a definition commit that names a text no input had sent yet. A compiled plan holds the numbers of the texts it names; the restart compiles the plans against the seal's text table. Before, it compiled them first, and the committed text had another number once the seal was loaded: the filter on it matched nothing.</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void A_host_restored_from_a_seal_continues_like_one_that_was_not_restarted(bool afterCommit)
    {
        var edited = Files.First(f => f.Path.EndsWith("acme.ops.sattle", StringComparison.Ordinal)).Text.Replace("if(verdict == \"DROPPED\"", "if(verdict == \"REDIRECTED\"");
        (string Digest, string Space) Run(bool restart)
        {
            using var d = new RestartTests.Durable("memory");
            var h = SpaceHost.OpenDurable(d.Open(), Files, Options(0));
            try
            {
                Feed(h);
                if (afterCommit) h.Commit("acme.ops", edited, h.CatalogRevision);
                Flows(h, 20, 9_000_000, "REDIRECTED", "REDIRECTED", "FORWARDED", "DROPPED");
                if (restart)
                {
                    h.Seal(); h.Dispose(); d.Close();
                    h = RestartTests.Reopen(d.Open(), Options(0));
                    Assert.True(h.Restart!.FromSeal, h.Restart.SealRefusal);
                }
                Flows(h, 40, 9_000_002, "REDIRECTED", "REDIRECTED", "REDIRECTED", "FORWARDED", "DROPPED");
                return (h.StateDigest(), Space(h));
            }
            finally { h.Dispose(); }
        }
        Assert.Equal(Run(restart: false), Run(restart: true));
    }

    /// <summary>Code review 2, defects 3 and 27: after a restart from a seal the inputs are not in memory, and a preview and a definition commit read them back from the journal. A live run journals its envelopes, not rows: they are run through the adapters again. Before, a preview was refused, and a commit on a live space rebuilt every graph from nothing (the master went to 0 entities).</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void After_a_restart_from_a_seal_a_preview_and_a_definition_commit_see_every_input_so_far(bool live)
    {
        using var d = new RestartTests.Durable("memory");
        using (var h = SpaceHost.OpenDurable(d.Open(), Files, Options(30)))
        {
            if (!live) Feed(h);
            else
            {
                var events = LiveReproducibilityTests.Arrival(EstateTests.EstateDir, seed: 4, distance: 12);
                var ro = new ReplayOptions { ChunkRows = 40, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth", FlushAtEnd = false };
                Assert.Throws<LiveResumeTests.Crash>(() => Sattle.Cli.LiveInput.Run(h, new LiveResumeTests.FakeSource(events, crashAt: events.Count / 2), ro, CancellationToken.None, flushEvery: TimeSpan.FromHours(1)));
            }
        }
        d.Close();
        using var r = RestartTests.Reopen(d.Open(), Options(30));
        Assert.True(r.Restart!.FromSeal, r.Restart.SealRefusal);
        var master = r.Engine.Master.State.Digest();
        Assert.True(r.Engine.Master.State.EntityCount > 0);
        var draft = r.CreateDraft(null)["draft"]!.GetValue<string>();
        var preview = r.Preview(draft, new System.Text.Json.Nodes.JsonObject { ["graphs"] = new System.Text.Json.Nodes.JsonArray("master") });
        Assert.True(preview["scope"]!["complete"]!.GetValue<bool>());
        Assert.Equal(master, preview["graphs"]![0]!["digest"]!.GetValue<string>());                // the draft changes nothing: the preview rebuilds the master as it is
        var edited = Files.First(f => f.Path.EndsWith("acme.deployhealth.sattle", StringComparison.Ordinal)).Text.Replace("param Integer = 0", "param Integer = 1");
        r.Commit("acme.deployhealth", edited, r.CatalogRevision);                                  // a parameter of a view: the master must not change
        Assert.Equal(master, r.Engine.Master.State.Digest());
    }

    [Theory, MemberData(nameof(Stores))]
    public void A_restart_from_a_seal_replays_only_the_tail_and_gives_the_same_space(string kind)
    {
        using var d = new RestartTests.Durable(kind);
        string digest, space; long seq, entries;
        var store = d.Open();
        using (var h = SpaceHost.OpenDurable(store, Files, Options(25)))
        {
            Feed(h); h.Finish();
            digest = h.StateDigest(); space = Space(h); seq = h.Seq;
            entries = ((IJournalStore)store).JournalCount;
        }
        d.Close();
        using var r = SpaceHost.OpenDurable(d.Open(), null, Options(25));
        Assert.True(r.Restart!.FromSeal, "the seal was not used");
        Assert.True(r.Restart!.EntriesReplayed <= 25 + 2 && r.Restart!.EntriesReplayed < entries / 2, $"{r.Restart!.EntriesReplayed} entries replayed of {entries}");
        Assert.Equal(digest, r.StateDigest());
        Assert.Equal(space, Space(r));
        Assert.Equal(seq, r.Seq);
        Assert.True(r.Done);
        Assert.All(r.Engine.Graphs, g => Assert.Equal(r.Head(g.Name), g.Head));
        var up = r.Subscribe(null, false, seq); Assert.Empty(Drain(up)); r.Unsubscribe(up);
    }

    static List<string> Drain(Subscription s) { var l = new List<string>(); while (s.Messages.Reader.TryRead(out var m)) l.Add(m); return l; }

    [Theory, MemberData(nameof(Stores))]
    public void The_space_continues_after_a_seal_restore_exactly_as_after_a_full_replay(string kind)
    {
        using var sealedStore = new RestartTests.Durable(kind); using var plainStore = new RestartTests.Durable(kind);
        foreach (var (d, every) in new[] { (sealedStore, 25L), (plainStore, 0L) })
        {
            using var h = SpaceHost.OpenDurable(d.Open(), Files, Options(every));
            Feed(h);
            d.Close();
        }
        string a, b;
        using (var s = SpaceHost.OpenDurable(sealedStore.Open(), null, Options(25))) { Assert.True(s.Restart!.FromSeal); MoreInput(s); a = s.StateDigest(); }
        using (var p = SpaceHost.OpenDurable(plainStore.Open(), null, Options(0))) { Assert.False(p.Restart!.FromSeal); MoreInput(p); b = p.StateDigest(); }
        Assert.Equal(b, a);
        sealedStore.Close();
        using var again = SpaceHost.OpenDurable(sealedStore.Open(), null, Options(25));          // and what was journaled after the restore restarts again
        Assert.Equal(a, again.StateDigest());
    }

    [Theory]
    [InlineData("memory")] [InlineData("sqlite")]
    public void A_seal_that_does_not_check_out_is_refused_and_the_journal_replays(string kind)
    {
        using var d = new RestartTests.Durable(kind);
        string digest;
        using (var h = SpaceHost.OpenDurable(d.Open(), Files, Options(25))) { Feed(h); digest = h.StateDigest(); }
        d.Close();
        var store = d.Open(); var seal = ((IJournalStore)store).SealLatest()!;
        var meta = System.Text.Json.Nodes.JsonNode.Parse(seal.Meta)!;
        var sealedDigest = meta["digest"]!.GetValue<string>();
        // 1: the sealed digest does not match what the snapshot restores to
        meta["digest"] = "sha256:" + new string('0', 64);
        ((IJournalStore)store).SealPut(seal.Position + 1, meta.ToJsonString(), seal.Data);
        d.Close();
        using (var r = SpaceHost.OpenDurable(d.Open(), null, Options(25))) { Assert.False(r.Restart!.FromSeal); Assert.NotNull(r.Restart); Assert.Equal(digest, r.StateDigest()); }
        d.Close();
        // 2: a seal written by another build
        store = d.Open(); meta["digest"] = sealedDigest; meta["build"] = Guid.NewGuid().ToString();      // the true digest: only the build differs
        ((IJournalStore)store).SealPut(seal.Position + 2, meta.ToJsonString(), seal.Data);
        d.Close();
        using (var r = SpaceHost.OpenDurable(d.Open(), null, Options(25))) { Assert.False(r.Restart!.FromSeal); Assert.Equal(digest, r.StateDigest()); }
        d.Close();
        // 3: damaged bytes
        store = d.Open();
        ((IJournalStore)store).SealPut(seal.Position + 3, seal.Meta, [.. seal.Data.Take(seal.Data.Length / 2)]);
        d.Close();
        using (var r = SpaceHost.OpenDurable(d.Open(), null, Options(25))) { Assert.False(r.Restart!.FromSeal); Assert.Equal(digest, r.StateDigest()); }
    }

    [Theory]
    [InlineData("memory")] [InlineData("sqlite")]
    public void A_definition_commit_before_the_seal_survives_the_restart(string kind)
    {
        using var d = new RestartTests.Durable(kind);
        string digest, space; long rev;
        var edited = Files.First(f => f.Path.EndsWith("acme.deployhealth.sattle", StringComparison.Ordinal)).Text.Replace("param Integer = 0", "param Integer = 1");
        using (var h = SpaceHost.OpenDurable(d.Open(), Files, Options(25)))
        {
            Feed(h);
            h.Commit("acme.deployhealth", edited, h.CatalogRevision);
            MoreInput(h);
            h.Seal();
            digest = h.StateDigest(); space = Space(h); rev = h.CatalogRevision;
        }
        d.Close();
        using var r = SpaceHost.OpenDurable(d.Open(), null, Options(25));
        Assert.Equal(digest, r.StateDigest());
        Assert.Equal(rev, r.CatalogRevision);
        Assert.Equal(space, Space(r));
        Assert.Contains("param Integer = 1", r.Definition("acme.deployhealth"));
        // whether the seal was used or the journal replayed, a further definition commit works (it needs the retained inputs, which are read back from the journal)
        r.Commit("acme.deployhealth", edited.Replace("param Integer = 1", "param Integer = 2"), r.CatalogRevision);
        Assert.Contains("param Integer = 2", r.Definition("acme.deployhealth"));
    }

    [Fact]
    public void The_commits_after_the_seal_are_verified_against_the_store()
    {
        using var d = new RestartTests.Durable("sqlite");
        using (var h = SpaceHost.OpenDurable(d.Open(), Files, Options(25)))
        {
            Feed(h);
            // a last input after the last seal that commits: calls between two pods that never called each other make a new relationship of the master
            var w = new RowWriter(h.RowLayout("flows"), h.Interner);
            var t = new DateTimeOffset(2026, 1, 1, 10, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1_000_000L;
            for (var i = 0; i < 3; i++)
            {
                w.Begin(); w.Instant("time", t + i * 1_000_000_000L); w.Text("cluster", "synth"); w.Text("verdict", "FORWARDED");
                w.Text("srcNamespace", "shop"); w.Text("srcPod", "web-7d4-3"); w.Text("dstNamespace", "shop"); w.Text("dstPod", "api-5c9-2");
            }
            h.Ingest("flows", w.Build()!, 9_000_000);
            h.Advance(t + 5 * Min, 9_000_001);
        }
        d.Close();
        using (var c = new SqliteConnection($"Data Source={d.Path_};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM journal WHERE n = (SELECT MAX(n) FROM journal WHERE body LIKE '{\"k\":\"r\"%')"; cmd.ExecuteNonQuery();
        }
        var e = Assert.ThrowsAny<Exception>(() => SpaceHost.OpenDurable(d.Open(), null, Options(25)));
        // either a replayed commit differs from the stored one and the store refuses it, or the store has more revisions than the journal reproduces
        Assert.True(e.Message.Contains("journal reproduces", StringComparison.Ordinal) || e.Message.Contains("store did not commit", StringComparison.Ordinal), e.Message);
    }
}
