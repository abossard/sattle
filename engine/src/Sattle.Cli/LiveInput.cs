using System.Diagnostics;
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

    /// <summary>`ready` is called once, when the source yields its first event or idle mark: a source yields only after it reads, so from then on nothing sent to it is missed (Event Hubs resolves `latest` when its link opens on the first read).</summary>
    public static LiveCounts Run(SpaceHost host, ILiveSource source, ReplayOptions options, CancellationToken ct, ILiveRecording? recording = null, TimeSpan? flushEvery = null, Action<string>? received = null, Action? ready = null)
    {
        host.StartLive(options);
        if (recording is not null) { var journaled = host.LiveEnvelopeTexts(); for (var i = recording.Count; i < journaled.Count; i++) recording.Append(journaled[(int)i]); }
        var after = host.SourcePosition(source.Id);
        long consumed = 0, dup = 0, bad = 0; var since = Stopwatch.StartNew(); var every = flushEvery ?? TimeSpan.FromSeconds(2);
        foreach (var ev in source.Read(after, ct))
        {
            if (ready is not null) { ready(); ready = null; }
            if (ev is null) { host.LiveFlush(); since.Restart(); continue; }
            received?.Invoke("received");
            switch (host.LiveEnvelope(source.Id, EnvelopeText(ev.Text), ev.Position, recording is null ? null : recording.Append))
            {
                case SpaceHost.LiveResult.Consumed: consumed++; break;
                case SpaceHost.LiveResult.Duplicate: dup++; break;
                default: bad++; break;
            }
            if (since.Elapsed > every) { host.LiveFlush(); since.Restart(); }
        }
        host.LiveFlush();
        return new LiveCounts(consumed, dup, bad);
    }

    /// <summary>`Run` that survives a failing source (a network error after a laptop slept, an expired credential): after an error it waits `backoff(n)` for the n-th failure in a row and reads again from the position the space has seen, so nothing is lost or processed twice. A source that got ready before it failed starts the count again. Only the cancellation ends it.</summary>
    public static LiveCounts RunRetrying(SpaceHost host, ILiveSource source, ReplayOptions options, CancellationToken ct, Func<int, TimeSpan> backoff, Action<Exception, TimeSpan> failed, ILiveRecording? recording = null, Action? ready = null, TimeSpan? flushEvery = null)
    {
        var failures = 0;
        while (true)
        {
            var gotReady = false;
            try { return Run(host, source, options, ct, recording, flushEvery, ready: () => { gotReady = true; ready?.Invoke(); }); }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failures = gotReady ? 1 : failures + 1;
                var wait = backoff(failures);
                failed(e, wait);
                if (ct.WaitHandle.WaitOne(wait)) ct.ThrowIfCancellationRequested();
            }
        }
    }
}
