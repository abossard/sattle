using System.Diagnostics;
using Sattle.Runtime;

namespace Sattle.Cli;

/// <summary>One event of a live source: the envelope text and the position of the source after it (an opaque JSON text the source defines and understands).</summary>
public sealed record LiveEvent(string Text, string Position);

/// <summary>A source the live input reads: it yields events after a position (null: where the source starts by its own rule) in order, and null when it is idle for a moment (so the input can flush). Event Hubs is one; tests use a fake with the same contract.</summary>
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
    public static LiveCounts Run(SpaceHost host, ILiveSource source, ReplayOptions options, CancellationToken ct, ILiveRecording? recording = null, TimeSpan? flushEvery = null, Action<string>? received = null)
    {
        host.StartLive(options);
        if (recording is not null) { var journaled = host.LiveEnvelopeTexts(); for (var i = recording.Count; i < journaled.Count; i++) recording.Append(journaled[(int)i]); }
        var after = host.SourcePosition(source.Id);
        long consumed = 0, dup = 0, bad = 0; var since = Stopwatch.StartNew(); var every = flushEvery ?? TimeSpan.FromSeconds(2);
        foreach (var ev in source.Read(after, ct))
        {
            if (ev is null) { host.LiveFlush(); since.Restart(); continue; }
            received?.Invoke("received");
            switch (host.LiveEnvelope(source.Id, ev.Text, ev.Position, recording is null ? null : recording.Append))
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
}
