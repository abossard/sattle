using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Sattle.Runtime;

namespace Sattle.Cli;

/// <summary>One event of a live source: its body as delivered (the envelope text, or that text encoded as a JSON string) and the position of the source after it (an opaque JSON text the source defines and understands).</summary>
public sealed record LiveEvent(string Text, string Position);

/// <summary>A source the live input reads: it yields events after a position (null: where the source starts by its own rule) in order, and null when it is idle for a moment (so the input can flush). It yields nothing before it has read from its medium once, so its first yield means that from then on nothing sent is missed. A source that fails throws; the input reads it again from the position the space has seen. Event Hubs is one; tests use a fake with the same contract.</summary>
public interface ILiveSource
{
    string Id { get; }
    IEnumerable<LiveEvent?> Read(string? after, CancellationToken ct);
}

public sealed record LiveCounts(long Consumed, long Duplicates, long Rejected);

/// <summary>The file (or other medium) that keeps every consumed envelope in order, so a run can be replayed (`sattle replay <live-out> --no-flush --late-horizon 3`).</summary>
public interface ILiveRecording
{
    long Count { get; }
    void Append(string text);
}

/// <summary>The live input loop, independent of the source. For each envelope the order is fixed: receive, journal (with the position of the source after it), record, process. The journal is the truth and is written first, so a kill leaves at most one envelope that is journaled and not processed, and the next start processes it from the journal (exactly once); the recording is written after the journal, so it never holds an envelope the journal lacks, and a start that finds the recording shorter than the journal appends the missing envelopes before it reads the source. After a kill and the next start the recording is therefore exactly the envelopes the space has, in order, and replaying it gives the state of the space. The pending chunk is flushed when the source goes idle or every `flushEvery`; a restart resumes after the last position the space has seen.</summary>
public static class LiveInput
{
    /// <summary>The envelope text of a delivered body. The Node forwarder sends the envelope as text and its Event Hubs SDK encodes a string body as JSON, so the body is a JSON string whose value is the envelope; a body that is the envelope itself is taken as it is.</summary>
    public static string EnvelopeText(string body)
    {
        if (!body.StartsWith('"')) return body;
        try { return System.Text.Json.JsonSerializer.Deserialize<string>(body) ?? body; }
        catch (System.Text.Json.JsonException) { return body; }
    }

    /// <summary>Reads `source` after the position the space has seen until it ends or `ct` is cancelled, and returns the counts so far (a cancellation stops after the envelope in hand and is no error). `ready` is called after each first read of the source, when it yields its first event or idle mark: a source yields only after it reads, so from then on nothing sent to it is missed (Event Hubs resolves `latest` when its link opens on the first read). Without `retry` a failure ends the run with its exception (a kill, for the tests). With `retry` a failure (a network error after a laptop slept, an expired credential) is survived: after the n-th failure in a row the input waits `retry(e, n)` and reads again from the position the space has seen, so nothing is lost or processed twice; a source that got ready before it failed starts the count again.</summary>
    public static LiveCounts Run(SpaceHost host, ILiveSource source, ReplayOptions options, CancellationToken ct, ILiveRecording? recording = null, TimeSpan? flushEvery = null, Action<string>? received = null, Action? ready = null, Func<Exception, int, TimeSpan>? retry = null)
    {
        host.StartLive(options);
        long consumed = 0, dup = 0, bad = 0; var failures = 0; var every = flushEvery ?? TimeSpan.FromSeconds(2);
        while (true)
        {
            var gotReady = false;
            try
            {
                if (recording is not null) { var journaled = host.LiveEnvelopeTexts(); for (var i = recording.Count; i < journaled.Count; i++) recording.Append(journaled[(int)i]); }
                var since = Stopwatch.StartNew();
                foreach (var ev in source.Read(host.SourcePosition(source.Id), ct))
                {
                    if (!gotReady) { gotReady = true; ready?.Invoke(); }
                    if (ev is null) { host.LiveFlush(); since.Restart(); }
                    else
                    {
                        received?.Invoke("received");
                        switch (host.LiveEnvelope(source.Id, EnvelopeText(ev.Text), ev.Position, recording is null ? null : recording.Append))
                        {
                            case SpaceHost.LiveResult.Consumed: consumed++; break;
                            case SpaceHost.LiveResult.Duplicate: dup++; break;
                            default: bad++; break;
                        }
                        if (since.Elapsed > every) { host.LiveFlush(); since.Restart(); }
                    }
                    if (ct.IsCancellationRequested) break;
                }
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) when (retry is not null)
            {
                failures = gotReady ? 1 : failures + 1;
                if (ct.WaitHandle.WaitOne(retry(e, failures))) break;
            }
        }
        host.LiveFlush();
        return new LiveCounts(consumed, dup, bad);
    }

    /// <summary>The events of a source that receives in batches (Event Hubs): an empty batch is an idle mark (null, the input flushes), a batch is its events in order, and `processed` gets the batch's lag once the input has asked for the event after the last one, that is after it processed the whole batch. An input that stops inside a batch never reports that batch's lag.</summary>
    public static IEnumerable<LiveEvent?> Batches(Func<(IReadOnlyList<LiveEvent> Events, LiveLag? Lag)> receive, Action<LiveLag> processed)
    {
        while (true)
        {
            var (events, lag) = receive();
            if (events.Count == 0) { yield return null; continue; }
            foreach (var e in events) yield return e;
            if (lag is not null) processed(lag);
        }
    }

    /// <summary>The next stop state and what the edge does on the way (Grokking Simplicity: a calculation; `RunUntilStopped` runs the effects). The first stop event names the reason, later ones change nothing: Ctrl-C, SIGTERM or anything else that stops the server cancels the input; the deadline (`--seconds`) cancels it and stops the server; the input ending stops the server unless it is already stopping.</summary>
    public static (LiveStop State, LiveStopEffects Effects) Next(LiveStop state, LiveStopEvent e) => (state, e) switch
    {
        (LiveStop.Running, LiveStopEvent.HostStopping) => (new LiveStop.Stopping("the server was stopped (Ctrl-C or SIGTERM)"), LiveStopEffects.CancelInput),
        (LiveStop.Running, LiveStopEvent.Deadline) => (new LiveStop.Stopping("--seconds ran out"), LiveStopEffects.CancelInput | LiveStopEffects.StopHost),
        (LiveStop.Running, LiveStopEvent.InputEnded) => (new LiveStop.Stopped("the input ended"), LiveStopEffects.StopHost),
        (LiveStop.Stopping s, LiveStopEvent.InputEnded) => (new LiveStop.Stopped(s.Reason), LiveStopEffects.None),
        _ => (state, LiveStopEffects.None),
    };

    /// <summary>Runs `input` under the stop state machine (`Next`) and returns its counts and why it stopped. The host's stopping (ASP.NET's console lifetime turns Ctrl-C and SIGTERM into it) and the deadline (a token cancelled when it passes: `--seconds` in the CLI, the test itself in a test, so no test waits on the clock) cancel the input's token; when the input ends, by itself, cancelled or failing, the server is stopped, so it never goes on serving a state that no longer moves. A failure of the input is rethrown after the server was told to stop.</summary>
    public static (LiveCounts Counts, string Reason) RunUntilStopped(IHostApplicationLifetime lifetime, CancellationToken deadline, Func<CancellationToken, LiveCounts> input)
    {
        using var cancel = new CancellationTokenSource();
        var gate = new object(); LiveStop state = new LiveStop.Running();
        void Fire(LiveStopEvent e)
        {
            LiveStopEffects fx;
            lock (gate) (state, fx) = Next(state, e);
            if (fx.HasFlag(LiveStopEffects.CancelInput)) cancel.Cancel();
            if (fx.HasFlag(LiveStopEffects.StopHost)) lifetime.StopApplication();
        }
        using var passed = deadline.Register(() => Fire(LiveStopEvent.Deadline));
        using var stopping = lifetime.ApplicationStopping.Register(() => Fire(LiveStopEvent.HostStopping));
        LiveCounts counts;
        try { counts = input(cancel.Token); }
        finally { Fire(LiveStopEvent.InputEnded); }
        lock (gate) return (counts, ((LiveStop.Stopped)state).Reason);
    }
}

/// <summary>How far the space is behind the newest event of its source after a batch was processed (events, seconds of enqueued time). One immutable value, replaced whole, so a reader on another thread sees one batch's lag or the next one's.</summary>
public sealed record LiveLag(long Events, double Seconds);

/// <summary>The states of stopping a live run: running, stopping for a reason (the input is cancelled and finishes its envelope, flushes and returns), stopped (the input returned; the reason is the first stop event's).</summary>
public abstract record LiveStop
{
    public sealed record Running : LiveStop;
    public sealed record Stopping(string Reason) : LiveStop;
    public sealed record Stopped(string Reason) : LiveStop;
}

public enum LiveStopEvent { HostStopping, Deadline, InputEnded }

[Flags]
public enum LiveStopEffects { None = 0, CancelInput = 1, StopHost = 2 }
