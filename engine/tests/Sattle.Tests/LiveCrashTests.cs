using Sattle.Cli;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>A live run is killed at each step between receiving an envelope and having recorded and processed it, at several envelopes, and started again. The order is receive, journal, record, process. After the next start the space is in the state of a run that was never killed, and the recording holds exactly the envelopes of the space, each once, in order.</summary>
[Trait("Category", "Slow")]
public class LiveCrashTests
{
    const long Min = 60_000_000_000;
    sealed class Killed : Exception;
    sealed class ListRecording : ILiveRecording
    {
        public readonly List<string> Lines = [];
        public long Count => Lines.Count;
        public void Append(string text) => Lines.Add(text);
    }

    static string Fixture => EstateTests.EstateDir;
    static ReplayOptions Ro => new() { ChunkRows = 20, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth", FlushAtEnd = false };
    static HostOptions Options => new() { Cluster = "synth" };
    static IReadOnlyList<Sattle.Language.SourceFile> Files => Sattle.Language.Catalog.ReadDirectory(Repo.Definitions);
    static readonly Lazy<IReadOnlyList<string>> Events = new(() => LiveReproducibilityTests.Arrival(Fixture, seed: 2, distance: 4).Take(80).ToList());
    static readonly Lazy<string> Whole = new(() =>
    {
        using var h = SpaceHost.OpenDurable(new InMemoryGraphStore(), Files, Options);
        LiveInput.Run(h, new LiveResumeTests.FakeSource(Events.Value), Ro, CancellationToken.None, flushEvery: TimeSpan.FromHours(1));
        return h.StateDigest();
    });

    public static IEnumerable<object[]> Points() =>
        from step in new[] { "received", "journaled", "recorded", "processed" }
        from k in new[] { 2, 25, 60 }
        select new object[] { step, k };

    [Theory, MemberData(nameof(Points))]
    public void A_kill_at_any_step_of_any_envelope_leaves_a_state_and_a_recording_that_the_next_start_makes_whole(string step, int k)
    {
        var events = Events.Value; Assert.True(events.Count > k + 3);
        var store = new InMemoryGraphStore(); var recording = new ListRecording();
        using (var h = SpaceHost.OpenDurable(store, Files, Options))
        {
            int journaled = 0, got = 0;
            h.LiveStep = s => { if (s == "journaled") journaled++; if (s == step && journaled - 1 == k) throw new Killed(); };
            Assert.Throws<Killed>(() => LiveInput.Run(h, new LiveResumeTests.FakeSource(events), Ro, CancellationToken.None, recording, TimeSpan.FromHours(1), received: _ => { if (++got - 1 == k && step == "received") throw new Killed(); }));
        }
        using var r = RestartTests.Reopen(store, Options);
        LiveInput.Run(r, new LiveResumeTests.FakeSource(events), Ro, CancellationToken.None, recording, TimeSpan.FromHours(1));
        Assert.Equal(Whole.Value, r.StateDigest());
        Assert.Equal(events, recording.Lines);                                                     // every envelope once, in order
        Assert.Equal(events.Count, r.LiveEnvelopeTexts().Count);
        // a second kill-free restart changes nothing
        using var again = RestartTests.Reopen(store, Options);
        LiveInput.Run(again, new LiveResumeTests.FakeSource(events), Ro, CancellationToken.None, recording, TimeSpan.FromHours(1));
        Assert.Equal(Whole.Value, again.StateDigest());
        Assert.Equal(events, recording.Lines);
    }

    [Fact]
    public void A_recording_that_lost_its_last_envelopes_to_a_kill_is_completed_from_the_journal_before_the_source_is_read()
    {
        var events = Events.Value; var store = new InMemoryGraphStore(); var recording = new ListRecording();
        using (var h = SpaceHost.OpenDurable(store, Files, Options)) LiveInput.Run(h, new LiveResumeTests.FakeSource(events.Take(30).ToList()), Ro, CancellationToken.None, recording, TimeSpan.FromHours(1));
        recording.Lines.RemoveRange(25, 5);                                                         // the file lost its last five lines
        using var r = RestartTests.Reopen(store, Options);
        LiveInput.Run(r, new LiveResumeTests.FakeSource(events.Take(30).ToList()), Ro, CancellationToken.None, recording, TimeSpan.FromHours(1));
        Assert.Equal(events.Take(30), recording.Lines);
    }
}
