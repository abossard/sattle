using Sattle.Kql;
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
    static HostOptions Options(long sealEvery) => new() { Projections = new KqlSource(), Cluster = "synth", SealEvery = sealEvery };

    /// <summary>The digest and input rows of the run that was never interrupted, computed once for the class (every case uses the same events).</summary>
    static readonly Lazy<(string Digest, long Rows)> Uninterrupted = new(() =>
    {
        using var d = new RestartTests.Durable("memory");
        using var h = SpaceHost.OpenDurable(d.Open(), EstateTests.Files(demo: true), Options(0));
        LiveInput.Run(h, new FakeSource(Events()), Ro, CancellationToken.None, flushEvery: TimeSpan.FromHours(1));
        return (h.StateDigest(), h.InputRows);
    });

    /// <summary>Every durable store kind without seals and with a seal every 30 envelopes, plus seals every 13 and every 7; each case its own generated position.</summary>
    public static IEnumerable<object[]> Cases()
    {
        var seed = 0;
        foreach (var kind in RestartTests.Stores().Select(x => (string)x[0]))
            foreach (var sealEvery in new[] { 0L, 30L })
                yield return [kind, sealEvery, ++seed];
        yield return ["sqlite", 13L, ++seed];
        yield return ["memory", 7L, ++seed];
    }

    /// <summary>T-RESTART on the live path: the stand-in source kills the run at a generated position (between flushes, with rows of envelopes pending in the chunk), the run restores from its store, resumes after the last position it saw and ends in the uninterrupted state; restarted again, it is still in that state.</summary>
    [Theory, MemberData(nameof(Cases))]
    public void A_live_run_killed_at_a_generated_position_resumes_from_its_store_to_the_uninterrupted_state(string kind, long sealEvery, int seed)
    {
        var events = Events();
        var (digest, rows) = Uninterrupted.Value;
        var killedAt = new Random(seed).Next(1, events.Count - 1);
        using var d = new RestartTests.Durable(kind);
        using (var h = SpaceHost.OpenDurable(d.Open(), EstateTests.Files(demo: true), Options(sealEvery)))
            Assert.Throws<Crash>(() => LiveInput.Run(h, new FakeSource(events, crashAt: killedAt), Ro, CancellationToken.None, flushEvery: TimeSpan.FromHours(1)));
        d.Close();
        using var r = RestartTests.Reopen(d.Open(), Options(sealEvery));
        Assert.NotNull(r.Restart);
        Assert.Equal($"{{\"n\":{killedAt - 1}}}", r.SourcePosition("eventhub:fake"));
        var counts = LiveInput.Run(r, new FakeSource(events), Ro, CancellationToken.None, flushEvery: TimeSpan.FromHours(1));
        Assert.Equal(events.Count - killedAt, counts.Consumed);                                 // exactly the events after the last one it had seen
        Assert.Equal(0, counts.Duplicates);
        Assert.Equal((digest, rows), (r.StateDigest(), r.InputRows));
        // and the resumed run restarts again to the same state
        d.Close();
        using var again = RestartTests.Reopen(d.Open(), Options(sealEvery));
        Assert.Equal(digest, again.StateDigest());
        Assert.Equal($"{{\"n\":{events.Count - 1}}}", again.SourcePosition("eventhub:fake"));
    }

    [Theory]
    [InlineData("memory", 0L)] [InlineData("sqlite", 30L)]
    public void An_envelope_delivered_again_after_a_resume_is_dropped_by_its_key(string kind, long sealEvery)
    {
        var events = Events();
        var (digest, rows) = Uninterrupted.Value;
        using var d = new RestartTests.Durable(kind);
        var killedAt = events.Count / 2;
        using (var h = SpaceHost.OpenDurable(d.Open(), EstateTests.Files(demo: true), Options(sealEvery)))
            Assert.Throws<Crash>(() => LiveInput.Run(h, new FakeSource(events, crashAt: killedAt), Ro, CancellationToken.None, flushEvery: TimeSpan.FromHours(1)));
        d.Close();
        using var r = RestartTests.Reopen(d.Open(), Options(sealEvery));
        var counts = LiveInput.Run(r, new FakeSource(events, redeliver: 9), Ro, CancellationToken.None, flushEvery: TimeSpan.FromHours(1));
        Assert.Equal(9, counts.Duplicates);
        Assert.Equal(events.Count - killedAt, counts.Consumed);
        Assert.Equal(digest, r.StateDigest());
        Assert.Equal(rows, r.InputRows);
    }

    /// <summary>The Node forwarder's Event Hubs SDK encodes the envelope text as a JSON string: the reader takes the envelope out of it, so the state, the input rows and the recording are those of plain envelopes.</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Bodies_sent_as_JSON_strings_end_in_the_state_of_plain_envelopes(bool encoded)
    {
        var events = Events();
        var (digest, rows) = Uninterrupted.Value;
        var bodies = encoded ? events.Select(e => System.Text.Json.JsonSerializer.Serialize(e)).ToList() : events;
        var recording = new Recording(Path.Combine(Path.GetTempPath(), "sattle-body-" + Guid.NewGuid().ToString("N") + ".ndjson"));
        try
        {
            using var d = new RestartTests.Durable("memory");
            using var h = SpaceHost.OpenDurable(d.Open(), EstateTests.Files(demo: true), Options(0));
            var counts = LiveInput.Run(h, new FakeSource(bodies), Ro, CancellationToken.None, recording, flushEvery: TimeSpan.FromHours(1));
            Assert.Equal(new LiveCounts(events.Count, 0, 0), counts);
            Assert.Equal((digest, rows), (h.StateDigest(), h.InputRows));
            Assert.Equal(events, recording.Lines);
        }
        finally { File.Delete(recording.Path); }
    }

    [Theory]
    [InlineData("{\"v\":1}", "{\"v\":1}")]                    // the envelope itself
    [InlineData("\"{\\\"v\\\":1}\"", "{\"v\":1}")]          // the envelope as a JSON string
    [InlineData("\"not closed", "\"not closed")]               // no JSON string: kept, the host rejects it
    [InlineData("not json at all", "not json at all")]
    public void The_envelope_text_of_a_body(string body, string envelope) => Assert.Equal(envelope, LiveInput.EnvelopeText(body));

    /// <summary>A source over the events that, on its k-th read, throws when it reaches the event at `crashes[k]` (0: before it yields anything, -1: never), and records what happened in order.</summary>
    sealed class FlakySource(IReadOnlyList<string> lines, int[] crashes, List<string> log) : ILiveSource
    {
        int reads;
        public string Id => "eventhub:fake";
        public IEnumerable<LiveEvent?> Read(string? after, CancellationToken ct)
        {
            var crashAt = reads < crashes.Length ? crashes[reads] : -1; reads++;
            var start = after is null ? 0 : System.Text.Json.JsonDocument.Parse(after).RootElement.GetProperty("n").GetInt32() + 1;
            log.Add("read");
            for (var i = start; i < lines.Count; i++)
            {
                if (i - start == crashAt) { log.Add("crash"); throw new InvalidOperationException("the hub went away"); }
                if (i == start) log.Add("first yield");
                yield return new LiveEvent(lines[i], $"{{\"n\":{i}}}");
            }
        }
    }

    /// <summary>Review show stopper 1: the input says it is ready only after the source yielded, that is after its first read (Event Hubs resolves `latest` when the link opens on it), so a forwarder started on the ready line is not missed. Show stopper 2: a source that fails is read again from the position the space has seen, with the backoff counting failures in a row, until the run ends in the state of a run that never failed. The counts add up over the failed reads (#102).</summary>
    [Theory]
    [InlineData("never fails", new int[0], "")]
    [InlineData("fails once after reading", new[] { 30 }, "1")]
    [InlineData("fails twice before reading, then after", new[] { 0, 0, 25 }, "1,2,1")]
    [InlineData("fails after reading, twice", new[] { 20, 40 }, "1,1")]
    public void A_failing_source_is_read_again_from_the_saved_position_and_ready_comes_after_each_first_read(string _, int[] crashes, string backoffs)
    {
        var events = Events();
        var (digest, rows) = Uninterrupted.Value;
        var log = new List<string>(); var waits = new List<int>();
        using var d = new RestartTests.Durable("memory");
        using var h = SpaceHost.OpenDurable(d.Open(), EstateTests.Files(demo: true), Options(0));
        var counts = LiveInput.Run(h, new FlakySource(events, crashes, log), Ro, CancellationToken.None,
            retry: (e, n) => { Assert.IsType<InvalidOperationException>(e); waits.Add(n); return TimeSpan.Zero; },
            ready: () => log.Add("ready"), flushEvery: TimeSpan.FromHours(1));
        Assert.Equal((digest, rows), (h.StateDigest(), h.InputRows));
        Assert.Equal(new LiveCounts(events.Count, 0, 0), counts);
        Assert.Equal(backoffs, string.Join(",", waits));
        Assert.Equal(crashes.Length + 1, log.Count(x => x == "read"));
        Assert.All(log.Select((x, i) => (x, i)).Where(t => t.x == "ready"), t => Assert.Equal("first yield", log[t.i - 1]));
        Assert.Equal(log.Count(x => x == "first yield"), log.Count(x => x == "ready"));
    }

    /// <summary>A source that receives in batches of `size` like Event Hubs, through `LiveInput.Batches`, with the lag of each batch (events left after it). At the end of the events it waits for more, and the run is stopped while it waits (`stop`, the receive throws like `ReceiveBatchAsync`).</summary>
    sealed class Batched(IReadOnlyList<string> lines, int size, CancellationTokenSource stop) : ILiveSource
    {
        public string Id => "eventhub:fake";
        public List<LiveLag> Lags { get; } = [];
        public IEnumerable<LiveEvent?> Read(string? after, CancellationToken ct)
        {
            var next = after is null ? 0 : System.Text.Json.JsonDocument.Parse(after).RootElement.GetProperty("n").GetInt32() + 1;
            (IReadOnlyList<LiveEvent>, LiveLag?) Receive()
            {
                if (next == lines.Count) stop.Cancel();
                ct.ThrowIfCancellationRequested();
                var batch = Enumerable.Range(next, Math.Min(size, lines.Count - next)).Select(i => new LiveEvent(lines[i], $"{{\"n\":{i}}}")).ToList();
                next += batch.Count;
                return (batch, new LiveLag(lines.Count - next, 0));
            }
            return LiveInput.Batches(Receive, Lags.Add);
        }
    }

    /// <summary>#102: a run stopped inside a batch (here by the received hook, as Ctrl-C would between two envelopes) finishes the envelope in hand and returns its counts so far, not the length of the recording; only the batches it processed whole report their lag, after their last envelope; the next start continues after the last envelope and ends in the state of a run that was never stopped.</summary>
    [Theory]
    [InlineData(7, 3)] [InlineData(25, 60)] [InlineData(25, 74)] [InlineData(25, 75)]
    public void A_run_stopped_inside_a_batch_returns_its_counts_and_the_lag_of_whole_batches_and_resumes_to_the_uninterrupted_state(int size, int stopAt)
    {
        var events = Events();
        var (digest, rows) = Uninterrupted.Value;
        using var d = new RestartTests.Durable("memory");
        using (var h = SpaceHost.OpenDurable(d.Open(), EstateTests.Files(demo: true), Options(0)))
        {
            using var stop = new CancellationTokenSource(); var source = new Batched(events, size, stop); var got = 0;
            var counts = LiveInput.Run(h, source, Ro, stop.Token, flushEvery: TimeSpan.FromHours(1), received: _ => { if (got++ == stopAt) stop.Cancel(); });
            Assert.Equal(new LiveCounts(stopAt + 1, 0, 0), counts);
            Assert.Equal($"{{\"n\":{stopAt}}}", h.SourcePosition("eventhub:fake"));
            Assert.Equal(Enumerable.Range(1, stopAt / size).Select(b => new LiveLag(events.Count - b * size, 0)), source.Lags);
        }
        d.Close();
        using var r = RestartTests.Reopen(d.Open(), Options(0));
        using var end = new CancellationTokenSource(); var rest = new Batched(events, size, end);
        var again = LiveInput.Run(r, rest, Ro, end.Token, flushEvery: TimeSpan.FromHours(1));
        Assert.Equal(new LiveCounts(events.Count - stopAt - 1, 0, 0), again);
        Assert.Equal((digest, rows), (r.StateDigest(), r.InputRows));
        Assert.Equal(new LiveLag(0, 0), rest.Lags[^1]);
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
        using var r = RestartTests.Reopen(d.Open(), Options(0));
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
        public string Path => path;
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
                var cat = new Catalog(new KqlSource()); Assert.DoesNotContain(cat.Load(files, updatePins: true).Diagnostics, d => d.IsError);
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

    /// <summary>T-LIVE on the recording of `serve --live` (Proposition 10.7, premise LV1): a kill cuts the write of the last envelope in half, after the journal took it. Opening the recording counts only whole lines and cuts the torn one away; the next run appends that envelope again, whole, from the journal, and goes on. The recording then holds every envelope once, in arrival order, and its replay ends in the live digest.</summary>
    [Theory]
    [InlineData(1, 0)] [InlineData(4, 8)]
    public void The_file_recording_does_not_count_a_torn_last_line_and_its_replay_reproduces_the_live_run(int seed, int distance)
    {
        var envelopes = LiveReproducibilityTests.Arrival(EstateTests.EstateDir, seed, distance);
        var arrival = new List<string>(envelopes);
        arrival.Insert(arrival.Count / 3, arrival[0]);                                                  // delivered again
        var dir = Path.Combine(Path.GetTempPath(), "sattle-tlive-file-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "live", "stream.ndjson");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var files = EstateTests.Files(demo: true);
            using var h = SpaceHost.OpenDurable(new InMemoryGraphStore(), files, Options(0));
            var first = arrival.Take(arrival.Count / 2).ToList();
            using (var recording = new FileRecording(path)) LiveInput.Run(h, new Scripted(first, []), Ro, CancellationToken.None, recording, flushEvery: TimeSpan.FromHours(1));
            var whole = File.ReadAllLines(path);
            // the kill: the last line is cut in half and loses its end of line
            var text = File.ReadAllText(path);
            var lastStart = text.LastIndexOf('\n', text.Length - 2) + 1;
            File.WriteAllText(path, text[..(lastStart + (text.Length - 1 - lastStart) / 2)]);
            using (var recording = new FileRecording(path))
            {
                Assert.Equal(whole.Length - 1, recording.Count);
                var counts = LiveInput.Run(h, new Scripted(arrival, []), Ro, CancellationToken.None, recording, flushEvery: TimeSpan.FromHours(1));
                Assert.True(counts.Duplicates > 0, "the second run reads the first half again");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Assert.Equal(arrival.Where(a => seen.Add(a)).ToList(), File.ReadAllLines(path));
            var cat = new Catalog(new KqlSource()); Assert.DoesNotContain(cat.Load(files, updatePins: true).Diagnostics, d => d.IsError);
            var space = cat.Spaces.Keys.Single();
            using var engine = new Engine(cat, space, new EngineOptions { Tenant = "lab", LateHorizonWindows = 3 });
            new Replay(engine, cat.Spaces[space], AdapterRegistry.Create("synth"), Ro with { FlushAtEnd = false }).Run(dir);
            Assert.Equal(h.StateDigest(), engine.StateDigest());
        }
        finally { Directory.Delete(dir, true); }
    }
}
