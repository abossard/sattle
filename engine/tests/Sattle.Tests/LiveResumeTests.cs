using Sattle.Language;
using Sattle.Cli;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>A live run killed and restarted from its store resumes its source after the last position the space saw: nothing is processed twice (the position is journaled with the envelope, before it is processed), nothing is lost, an envelope delivered again is dropped by its key, and the state is the state of a run that was never interrupted. Tested with a fake source that has the contract of Event Hubs (positions, at least once delivery); against real Event Hubs only the source adapter differs and it is not exercised here.</summary>
[Trait("Category", "Slow")]
public class LiveResumeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    const long Min = 60_000_000_000;
    internal sealed class Crash : Exception;

    /// <summary>A source over a list of envelope lines. Positions are `{"n":i}`. It goes idle (null) every `idleEvery` events, crashes when it reaches `crashAt`, and delivers the last `redeliver` events again after a resume.</summary>
    internal sealed class FakeSource(IReadOnlyList<string> lines, int crashAt = -1, int redeliver = 0, int idleEvery = 25) : ILiveSource
    {
        public string Id => "eventhub:fake";
        public IEnumerable<LiveEvent?> Read(string? after, CancellationToken ct)
        {
            var start = after is null ? 0 : System.Text.Json.JsonDocument.Parse(after).RootElement.GetProperty("n").GetInt32() + 1;
            start = Math.Max(0, start - redeliver);
            for (var i = start; i < lines.Count; i++)
            {
                if (i == crashAt) throw new Crash();
                yield return new LiveEvent(lines[i], $"{{\"n\":{i}}}");
                if ((i + 1) % idleEvery == 0) yield return null;
            }
        }
    }

    static IReadOnlyList<string> Events() => LiveReproducibilityTests.Arrival(EstateTests.EstateDir, seed: 4, distance: 12);
    static ReplayOptions Ro => new() { ChunkRows = 40, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth", FlushAtEnd = false };
    static HostOptions Options(long sealEvery) => new() { Cluster = "synth", SealEvery = sealEvery };

    static (string Digest, long Rows) Uninterrupted(IReadOnlyList<string> events)
    {
        using var d = new RestartTests.Durable("memory");
        using var h = SpaceHost.OpenDurable(d.Open(), EstateTests.Files(demo: true), Options(0));
        LiveInput.Run(h, new FakeSource(events), Ro, CancellationToken.None, flushEvery: TimeSpan.FromHours(1));
        return (h.StateDigest(), h.InputRows);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var kind in RestartTests.Stores().Select(x => (string)x[0]))
            foreach (var sealEvery in new[] { 0L, 30L })
                yield return [kind, sealEvery];
    }

    [Theory, MemberData(nameof(Cases))]
    public void A_killed_live_run_resumes_from_its_store_and_ends_in_the_state_of_an_uninterrupted_run(string kind, long sealEvery)
    {
        var events = Events();
        var (digest, rows) = Uninterrupted(events);
        using var d = new RestartTests.Durable(kind);
        var killedAt = events.Count * 6 / 10 + 7;          // between two idle points: rows of envelopes are pending in the chunk when it dies
        using (var h = SpaceHost.OpenDurable(d.Open(), EstateTests.Files(demo: true), Options(sealEvery)))
            Assert.Throws<Crash>(() => LiveInput.Run(h, new FakeSource(events, crashAt: killedAt), Ro, CancellationToken.None, flushEvery: TimeSpan.FromHours(1)));
        d.Close();
        using var r = SpaceHost.OpenDurable(d.Open(), null, Options(sealEvery));
        Assert.True(r.Restored);
        Assert.Equal($"{{\"n\":{killedAt - 1}}}", r.SourcePosition("eventhub:fake"));
        var counts = LiveInput.Run(r, new FakeSource(events), Ro, CancellationToken.None, flushEvery: TimeSpan.FromHours(1));
        Assert.Equal(events.Count - killedAt, counts.Consumed);                                 // exactly the events after the last one it had seen
        Assert.Equal(0, counts.Duplicates);
        Assert.Equal(digest, r.StateDigest());
        Assert.Equal(rows, r.InputRows);
        // and the resumed run restarts again to the same state
        d.Close();
        using var again = SpaceHost.OpenDurable(d.Open(), null, Options(sealEvery));
        Assert.Equal(digest, again.StateDigest());
        Assert.Equal($"{{\"n\":{events.Count - 1}}}", again.SourcePosition("eventhub:fake"));
    }

    /// <summary>T-RESTART on the live path: the stand-in source stops the run at a generated position (between flushes, with rows pending), the run resumes from its store and ends in the uninterrupted state.</summary>
    [Theory]
    [InlineData("memory", 0L, 1)] [InlineData("sqlite", 0L, 2)] [InlineData("sqlite", 13L, 3)] [InlineData("sqlite", 30L, 4)] [InlineData("memory", 7L, 5)]
    public void A_live_run_stopped_at_a_generated_position_resumes_to_the_uninterrupted_state(string kind, long sealEvery, int seed)
    {
        var events = Events();
        var (digest, rows) = Uninterrupted(events);
        var killedAt = new Random(seed).Next(1, events.Count - 1);
        using var d = new RestartTests.Durable(kind);
        using (var h = SpaceHost.OpenDurable(d.Open(), EstateTests.Files(demo: true), Options(sealEvery)))
            Assert.Throws<Crash>(() => LiveInput.Run(h, new FakeSource(events, crashAt: killedAt), Ro, CancellationToken.None, flushEvery: TimeSpan.FromHours(1)));
        d.Close();
        using var r = SpaceHost.OpenDurable(d.Open(), null, Options(sealEvery));
        var counts = LiveInput.Run(r, new FakeSource(events), Ro, CancellationToken.None, flushEvery: TimeSpan.FromHours(1));
        Assert.Equal(events.Count - killedAt, counts.Consumed);
        Assert.Equal(0, counts.Duplicates);
        Assert.Equal((digest, rows), (r.StateDigest(), r.InputRows));
    }

    [Theory]
    [InlineData("memory", 0L)] [InlineData("sqlite", 30L)]
    public void An_envelope_delivered_again_after_a_resume_is_dropped_by_its_key(string kind, long sealEvery)
    {
        var events = Events();
        var (digest, rows) = Uninterrupted(events);
        using var d = new RestartTests.Durable(kind);
        var killedAt = events.Count / 2;
        using (var h = SpaceHost.OpenDurable(d.Open(), EstateTests.Files(demo: true), Options(sealEvery)))
            Assert.Throws<Crash>(() => LiveInput.Run(h, new FakeSource(events, crashAt: killedAt), Ro, CancellationToken.None, flushEvery: TimeSpan.FromHours(1)));
        d.Close();
        using var r = SpaceHost.OpenDurable(d.Open(), null, Options(sealEvery));
        var counts = LiveInput.Run(r, new FakeSource(events, redeliver: 9), Ro, CancellationToken.None, flushEvery: TimeSpan.FromHours(1));
        Assert.Equal(9, counts.Duplicates);
        Assert.Equal(events.Count - killedAt, counts.Consumed);
        Assert.Equal(digest, r.StateDigest());
        Assert.Equal(rows, r.InputRows);
    }

    [Fact]
    public void A_record_that_is_not_an_envelope_is_rejected_and_its_position_is_kept()
    {
        var events = Events().ToList();
        events.Insert(40, "this is not json");
        using var d = new RestartTests.Durable("memory");
        using (var h = SpaceHost.OpenDurable(d.Open(), EstateTests.Files(demo: true), Options(0)))
        {
            var counts = LiveInput.Run(h, new FakeSource(events), Ro, CancellationToken.None, flushEvery: TimeSpan.FromHours(1));
            Assert.Equal(1, counts.Rejected);
            Assert.Equal(events.Count - 1, counts.Consumed);
        }
        d.Close();
        using var r = SpaceHost.OpenDurable(d.Open(), null, Options(0));
        Assert.Equal($"{{\"n\":{events.Count - 1}}}", r.SourcePosition("eventhub:fake"));
        var again = LiveInput.Run(r, new FakeSource(events), Ro, CancellationToken.None, flushEvery: TimeSpan.FromHours(1));
        Assert.Equal(new LiveCounts(0, 0, 0), again);
    }

    /// <summary>A stand-in source over given lines, idle at given points (where the live input flushes its pending chunk).</summary>
    sealed class Scripted(IReadOnlyList<string> lines, HashSet<int> idleAfter) : ILiveSource
    {
        public string Id => "eventhub:scripted";
        public IEnumerable<LiveEvent?> Read(string? after, CancellationToken ct)
        {
            for (var i = 0; i < lines.Count; i++) { yield return new LiveEvent(lines[i], $"{{\"n\":{i}}}"); if (idleAfter.Contains(i)) yield return null; }
        }
    }

    sealed class Recording(string path) : ILiveRecording
    {
        public readonly List<string> Lines = [];
        public long Count => Lines.Count;
        public void Append(string text) { Lines.Add(text); File.AppendAllLines(path, [text]); }
    }

    /// <summary>T-LIVE (Proposition 10.7, premises LV1 to LV3): the real reader loop with a stand-in source: envelopes of the estate in a shuffled arrival order, exact duplicates and records that are no envelopes among them, and flushes at random points. The recording holds each consumed envelope once, in arrival order (LV1); replaying it with the live horizon ends in the live digest (LV2, LV3); with another horizon the replay is either equal or reported as different.</summary>
    [Theory]
    [InlineData(1, 0)] [InlineData(2, 8)] [InlineData(3, 30)]
    public void The_reader_loop_of_Live_cs_is_reproduced_by_a_replay_of_its_recording(int seed, int distance)
    {
        var rnd = new Random(seed);
        var envelopes = LiveReproducibilityTests.Arrival(EstateTests.EstateDir, seed, distance);
        var arrival = new List<string>();
        foreach (var e in envelopes)
        {
            arrival.Add(e);
            if (rnd.Next(25) == 0) arrival.Add(arrival[rnd.Next(arrival.Count)]);                 // delivered again
            if (rnd.Next(40) == 0) arrival.Add(rnd.Next(2) == 0 ? "{\"not\": \"an envelope\"}" : "not json at all");
        }
        var idle = Enumerable.Range(0, arrival.Count).Where(_ => rnd.Next(9) == 0).ToHashSet();
        var dir = Path.Combine(Path.GetTempPath(), "sattle-tlive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "live"));
        try
        {
            var recording = new Recording(Path.Combine(dir, "live", "stream.ndjson"));
            var files = EstateTests.Files(demo: true);
            using var h = SpaceHost.OpenDurable(new InMemoryGraphStore(), files, Options(0));
            var counts = LiveInput.Run(h, new Scripted(arrival, idle), Ro, CancellationToken.None, recording, flushEvery: TimeSpan.FromHours(1));
            var live = h.StateDigest();
            // LV1: every envelope once, in the order it arrived; duplicates dropped, records that are no envelopes rejected
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var expected = arrival.Where(a => envelopes.Contains(a) && seen.Add(a)).ToList();
            Assert.Equal(expected, recording.Lines);
            Assert.Equal(expected.Count, counts.Consumed);
            Assert.True(counts.Duplicates > 0 && counts.Rejected > 0, $"duplicates {counts.Duplicates}, rejected {counts.Rejected}");
            // LV2, LV3: the replay of the recording with the live horizon ends in the live digest
            string Replayed(int horizon)
            {
                var cat = new Catalog(); Assert.DoesNotContain(cat.Load(files, updatePins: true).Diagnostics, d => d.IsError);
                var space = cat.Spaces.Keys.Single();
                using var engine = new Engine(cat, space, new EngineOptions { Tenant = "lab", LateHorizonWindows = horizon });
                new Replay(engine, cat.Spaces[space], AdapterRegistry.Create("synth"), Ro with { FlushAtEnd = false }).Run(dir);
                return engine.StateDigest();
            }
            Assert.Equal(live, Replayed(3));
            var other = Replayed(1);          // a horizon of one window drops rows the live run still revised: the replay is reported equal or different, it is not the live state by definition
            output.WriteLine(other == live ? "a replay with a late horizon of 1 window gives the live digest" : $"a replay with a late horizon of 1 window gives another digest ({other}, live {live})");
        }
        finally { Directory.Delete(dir, true); }
    }
}
