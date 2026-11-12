using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

public sealed record ReplayOptions
{
    public int ChunkRows { get; init; } = 2000;
    /// <summary>Logical time advances in steps of this many nanoseconds of capture time.</summary>
    public long AdvanceEveryNs { get; init; } = 60_000_000_000;
    /// <summary>Allowed lateness: the frontier trails the capture clock by this much.</summary>
    public long LatenessNs { get; init; } = 60_000_000_000;
    public bool FlushAtEnd { get; init; } = true;
    /// <summary>Capture seconds per wall second. 0 means as fast as possible.</summary>
    public double Speed { get; init; }
    public string Cluster { get; init; } = "";
    public long? UntilNs { get; init; }
}

public sealed class ReplayStats
{
    public Dictionary<string, long> Records { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> Rows { get; } = new(StringComparer.Ordinal);
    public long Skipped, Rejected, Advances, Chunks, LostEvents;
    public List<string> RejectionSamples { get; } = [];
    public TimeSpan Elapsed;
    public long FirstCaptureNs, LastCaptureNs;
}

/// <summary>Reads a capture directory (or lab/fixtures) in capture-time order and feeds the engine: adapters make rows, chunks go in order, explicit Advance controls are inserted at data-defined positions.</summary>
public static class CaptureReader
{
    static bool IsMirror(string dir, string file)
    {
        var rel = Path.GetRelativePath(dir, file);
        return rel.StartsWith("eventhub", StringComparison.Ordinal) && Path.GetFileNameWithoutExtension(file) is "sattle-ingest" or "sattle-hubble";
    }

    /// <summary>All envelopes of the capture, merged by (capturedAt, file order, line). Streaming: one line per file in memory.</summary>
    public static IEnumerable<Envelope> Read(string dir)
    {
        var files = Directory.EnumerateFiles(dir, "*.ndjson", SearchOption.AllDirectories)
            .Where(f => !IsMirror(dir, f)) // the hubs sattle-ingest and sattle-hubble mirror the direct sources; the hubs written by Azure (sattle-activity, sattle-aks) are sources of their own
            .Order(StringComparer.Ordinal).ToList();
        var its = files.Select((f, i) => (Index: i, It: ReadFile(f).GetEnumerator())).ToList();
        var pq = new PriorityQueue<(int File, Envelope E), (long, int, long)>();
        foreach (var (i, it) in its) if (it.MoveNext()) pq.Enqueue((i, it.Current), (it.Current.CapturedNs, i, it.Current.Seq));
        while (pq.TryDequeue(out var top, out _))
        {
            yield return top.E;
            var it = its[top.File].It;
            if (it.MoveNext()) pq.Enqueue((top.File, it.Current), (it.Current.CapturedNs, top.File, it.Current.Seq));
        }
    }

    static IEnumerable<Envelope> ReadFile(string path)
    {
        long line = 0;
        foreach (var text in File.ReadLines(path))
        {
            line++;
            if (Parse(text, path, line) is { } e) yield return e;
        }
    }

    /// <summary>One envelope line of lab/CAPTURE-FORMAT.md; null when it is empty, not JSON or not an envelope.</summary>
    public static Envelope? Parse(string text, string file, long line)
    {
        if (text.Length == 0) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); } catch (JsonException) { return null; }
        var r = doc.RootElement;
        var cap = r.S("capturedAt");
        if (cap is null || !Norm.TryParseInstant(cap, out var ns) || r.P("payload") is not { } payload) return null;
        var seq = r.P("seq") is { ValueKind: JsonValueKind.Number } s ? s.GetInt64() : line;
        var meta = r.P("meta");
        return new Envelope(r.S("source") ?? "", ns, seq, file, payload, meta ?? default, meta is not null);
    }
}

/// <summary>What the replay driver and the live input need from the engine: row layouts, ordered inputs, logical time, loss marks.</summary>
public interface IEngineInput
{
    Interner Interner { get; }
    Layout RowLayout(string source);
    void Ingest(string source, Batch rows, long seq);
    void Advance(long lambdaNs, long seq);
    void RecordLoss(long timeNs, long count, string reason = "sourceLoss");
    /// <summary>The progress of a source (Def. 10.9): it has reported up to `untilNs`. A recorded input.</summary>
    void Progress(string source, long untilNs, long seq);
    /// <summary>Whether a graph of the space reads source progress (a Series that fills or has a time to live). Without one the progress of the sources is not recorded, and the inputs are those of a space without it.</summary>
    bool WantsProgress { get; }
}

public sealed class Replay : IRowSink
{
    readonly IEngineInput engine; readonly ReplayOptions opt; readonly IReadOnlyList<ISourceAdapter> adapters;
    readonly Dictionary<string, string> sourceOfRowType = new(StringComparer.Ordinal);
    /// <summary>The sources of the space an adapter feeds, and how far each source has reported since its progress was last recorded: the capture time of its newest envelope less the lateness.</summary>
    readonly Dictionary<string, List<string>> sourcesOfAdapter = new(StringComparer.Ordinal);
    readonly Dictionary<string, long> reported = new(StringComparer.Ordinal);       // a plain dictionary: it is part of the state a live host seals
    readonly Dictionary<string, RowWriter> writers = new(StringComparer.Ordinal);
    string curAdapter = ""; string? current; long seq; long lastLambda = long.MinValue;
    public ReplayStats Stats { get; } = new();
    public event Action<long>? Progress;

    /// <summary>Records the progress the sources made since the last advance, before the next one: coverage is then a function of the envelopes and the advances, not of where a chunk was cut.</summary>
    void RecordProgress()
    {
        foreach (var (source, until) in reported.OrderBy(x => x.Key, StringComparer.Ordinal)) engine.Progress(source, until, ++seq);
        reported.Clear();
    }

    public long LogicalTime => lastLambda;
    /// <summary>Added to the event times of the rows written from now on (see `RowWriter.InstantOffset`).</summary>
    public long TimeShiftNs { get; set; }
    /// <summary>Rows read from envelopes that have not been sent to the engine yet (the current chunk).</summary>
    public int PendingRows => current is not null && writers.TryGetValue(current, out var w) ? w.Rows : 0;
    readonly object gate;
    /// <summary>`gate`: the lock of the engine input when other threads use it too (a served host). Adapters intern text and write rows into the input's tables, so each envelope is consumed while the lock is held.</summary>
    public Replay(IEngineInput engine, TypedSpace space, IReadOnlyList<ISourceAdapter> adapters, ReplayOptions options, object? gate = null)
    {
        this.engine = engine; this.adapters = adapters; opt = options; this.gate = gate ?? new object();
        foreach (var s in space.Sources) sourceOfRowType[s.Adapter + "|" + Ids.Name(s.RowTypeId)] = s.Name;   // the same row type can come from two adapters into two sources
        foreach (var s in space.Sources) { if (!sourcesOfAdapter.TryGetValue(s.Adapter, out var names)) sourcesOfAdapter[s.Adapter] = names = []; names.Add(s.Name); }
    }

    // ---- IRowSink ----
    public RowWriter Begin(string rowType)
    {
        var key = curAdapter + "|" + rowType;
        if (current is not null && current != key) FlushCurrent();
        current = key;
        if (!writers.TryGetValue(key, out var w)) writers[key] = w = new RowWriter(engine.RowLayout(sourceOfRowType[key]), engine.Interner);
        w.InstantOffset = TimeShiftNs;
        return w;
    }
    public void Rejected(string source, string reason)
    {
        Stats.Rejected++;
        if (Stats.RejectionSamples.Count < 10) Stats.RejectionSamples.Add($"{source}: {reason}");
    }
    public void SourceLoss(long timeNs, long count, string reason = "sourceLoss") { Stats.LostEvents += count; engine.RecordLoss(timeNs, count, reason); }

    void FlushCurrent()
    {
        if (current is null) return;
        var w = writers[current];
        if (w.Rows == 0) return;
        var b = w.Build();
        if (b is null) return;
        var src = sourceOfRowType[current];
        Stats.Rows[src] = Stats.Rows.GetValueOrDefault(src) + b.Rows;
        Stats.Chunks++;
        engine.Ingest(src, b, ++seq);
    }

    long first, last, wallStart;
    readonly Stopwatch wall = Stopwatch.StartNew();

    /// <summary>One envelope: adapter routing, explicit logical-time control (a function of the capture clock), optional pacing, rows into the current chunk. Shared by replay and the live input.</summary>
    public bool Process(Envelope e, bool pace = true)
    {
        if (opt.UntilNs is { } until && e.CapturedNs > until) return false;
        if (first == 0) { first = e.CapturedNs; wallStart = wall.ElapsedTicks; }
        last = e.CapturedNs;
        Stats.Records[e.Source] = Stats.Records.GetValueOrDefault(e.Source) + 1;
        var adapter = adapters.FirstOrDefault(a => a.Handles(e.Source));
        if (adapter is null) { Stats.Skipped++; return true; }
        if (engine.WantsProgress && sourcesOfAdapter.TryGetValue(adapter.Id, out var fed))
            foreach (var source in fed) reported[source] = Math.Max(reported.GetValueOrDefault(source, long.MinValue), e.CapturedNs - opt.LatenessNs);       // every envelope of an adapter is progress of its sources, a marker or a heartbeat without rows as well
        var lambda = e.CapturedNs / opt.AdvanceEveryNs * opt.AdvanceEveryNs - opt.LatenessNs;
        if (lambda > lastLambda)
        {
            FlushCurrent(); current = null; RecordProgress(); engine.Advance(lambda, ++seq); Stats.Advances++;
            lastLambda = lambda;
            Progress?.Invoke(e.CapturedNs);
        }
        if (pace && opt.Speed > 0)
        {
            var target = TimeSpan.FromSeconds((e.CapturedNs - first) / 1e9 / opt.Speed);
            var wait = target - wall.Elapsed;
            if (wait > TimeSpan.FromMilliseconds(5)) { FlushCurrent(); current = null; Thread.Sleep(wait); }
        }
        lock (gate)
        {
            curAdapter = adapter.Id;
            var (writer, written) = current is not null && writers.TryGetValue(current, out var before) ? (current, before.Rows) : (null, 0);
            try { adapter.Consume(e, this); } // the rows of one record are never split across chunks, so a retraction and its insertion stay together
            catch (Exception x) when (x is InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException or System.Text.Json.JsonException or OverflowException)
            {
                // A record whose values have another JSON type than its adapter reads (a number written as text) is that record's fault: it is rejected and counted, and the records around it go on.
                // An exception here would stop a replay, and a live space could never start again, because the record is in its journal. The rows the record wrote into the current chunk are taken back.
                Rejected(e.Source, $"malformed record ({x.GetType().Name}: {x.Message})");
                if (current is not null && writers.TryGetValue(current, out var now)) now.Truncate(current == writer ? written : 0);
            }
            if (current is not null && writers[current].Rows >= opt.ChunkRows) FlushCurrent();
        }
        return true;
    }

    /// <summary>Sends the pending chunk to the engine (live input calls this when the stream pauses).</summary>
    public void FlushPending() { FlushCurrent(); current = null; }

    public ReplayStats Run(string captureDir)
    {
        var sw = Stopwatch.StartNew();
        foreach (var e in CaptureReader.Read(captureDir)) if (!Process(e)) break;
        FlushPending();
        if (opt.FlushAtEnd && last != 0)
        {
            RecordProgress();
            engine.Advance(last / opt.AdvanceEveryNs * opt.AdvanceEveryNs + opt.AdvanceEveryNs + opt.LatenessNs, ++seq);
            Stats.Advances++;
        }
        Stats.FirstCaptureNs = first; Stats.LastCaptureNs = last;
        Stats.Elapsed = sw.Elapsed;
        return Stats;
    }
}
