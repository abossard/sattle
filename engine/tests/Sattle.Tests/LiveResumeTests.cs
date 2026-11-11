using Sattle.Cli;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>A live run killed and restarted from its store resumes its source after the last position the space saw: nothing is processed twice (the position is journaled with the envelope, before it is processed), nothing is lost, an envelope delivered again is dropped by its key, and the state is the state of a run that was never interrupted. Tested with a fake source that has the contract of Event Hubs (positions, at least once delivery); against real Event Hubs only the source adapter differs and it is not exercised here.</summary>
[Trait("Category", "Slow")]
public class LiveResumeTests
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
}
