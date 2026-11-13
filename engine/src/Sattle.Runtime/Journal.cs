using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

/// <summary>The ordered journal of everything that entered a served space: input batches, logical-time advances, losses, definition commits, the end marker. It is written before the engine sees an input, so a crash leaves at most one entry the stores have not caught up with. Restart replays it (paper Ch. 9: the input log is the durable source of the derived state).</summary>
public interface IJournalStore
{
    long JournalCount { get; }
    void JournalAppend(string body);
    /// <summary>The entries with a position above `after` (1-based positions, in order); 0 reads the whole journal.</summary>
    IEnumerable<string> JournalRead(long after = 0);
    /// <summary>Stores a seal: the state of the space after the first `position` journal entries (a compressed snapshot) with its description. The latest two are kept.</summary>
    void SealPut(long position, string meta, byte[] data);
    SealRecord? SealLatest();
}

public sealed record SealRecord(long Position, string Meta, byte[] Data);

/// <summary>One entry of the journal, as a value. The journal is the ordered list of these, and everything that enters a space is one of them. `Text` is the stored form and `Parse` reads it back; the stored form of every kind is the one journals have always had.</summary>
public abstract record Entry
{
    /// <summary>The first entry: what the space was started with.</summary>
    public sealed record Header(string Tenant, int? LateHorizon, string Cluster, string Space, string Mode, IReadOnlyList<SourceFile> Files) : Entry;
    /// <summary>A batch of source rows.</summary>
    public sealed record Rows(string Source, long Seq, Batch Batch) : Entry;
    /// <summary>Logical time moves on to `Lambda`.</summary>
    public sealed record Advance(long Lambda, long Seq) : Entry;
    /// <summary>A source has reported everything up to `Until`.</summary>
    public sealed record Progress(string Source, long Until, long Seq) : Entry;
    /// <summary>A source lost `Count` records at `Time`.</summary>
    public sealed record Loss(long Time, long Count, string Reason) : Entry;
    /// <summary>The input has ended.</summary>
    public sealed record Finish : Entry;
    /// <summary>A live input starts, with the options its envelopes are read with.</summary>
    public sealed record LiveStart(ReplayOptions Options) : Entry;
    /// <summary>One envelope of a live source, and the position of the source after it.</summary>
    public sealed record Envelope(string Source, string Body, string After) : Entry;
    /// <summary>An envelope of a live source that was not consumed (a duplicate, or not an envelope): only the position of the source after it.</summary>
    public sealed record Skipped(string Source, string After) : Entry;
    /// <summary>The pending rows of the live input go to the engine here.</summary>
    public sealed record Flush : Entry;
    /// <summary>An event that is not stored used a sequence number (a draft event).</summary>
    public sealed record SequenceStep : Entry;
    /// <summary>A definition commit: the new text of each module, against the catalog revision it expects.</summary>
    public sealed record Commit(long Expected, IReadOnlyDictionary<string, string> Texts) : Entry;
    /// <summary>The definition commit before this entry was refused when it was activated.</summary>
    public sealed record CommitRefused : Entry;

    /// <summary>The options of a live input as stored (in a `LiveStart` and in a seal).</summary>
    public static string OptionsText(ReplayOptions o) => new JsonObject { ["c"] = o.ChunkRows, ["a"] = o.AdvanceEveryNs, ["l"] = o.LatenessNs, ["f"] = o.FlushAtEnd, ["k"] = o.Cluster }.ToJsonString();
    public static ReplayOptions ParseOptions(string text) { var n = JsonNode.Parse(text)!; return new ReplayOptions { ChunkRows = n["c"]!.GetValue<int>(), AdvanceEveryNs = n["a"]!.GetValue<long>(), LatenessNs = n["l"]!.GetValue<long>(), FlushAtEnd = n["f"]!.GetValue<bool>(), Cluster = n["k"]!.GetValue<string>() }; }

    static JsonArray FilesJson(IEnumerable<SourceFile> files) => new([.. files.Select(f => (JsonNode)new JsonObject { ["p"] = f.Path, ["t"] = f.Text })]);
    /// <summary>Definition files as a header and a seal store them.</summary>
    public static JsonArray FilesText(IEnumerable<SourceFile> files) => FilesJson(files);
    public static List<SourceFile> ParseFiles(JsonNode files) => [.. files.AsArray().Select(f => new SourceFile(f!["p"]!.GetValue<string>(), f["t"]!.GetValue<string>()))];

    /// <summary>The stored form. Rows carry their texts, so the interner that numbered them is needed.</summary>
    public string Text(Interner it) => this switch
    {
        Header h => new JsonObject { ["k"] = "h", ["tenant"] = h.Tenant, ["lateHorizon"] = h.LateHorizon, ["cluster"] = h.Cluster, ["space"] = h.Space, ["mode"] = h.Mode, ["generation"] = 0, ["files"] = FilesJson(h.Files) }.ToJsonString(),
        Rows r => BatchCodec.Encode(r.Source, r.Seq, r.Batch, it),
        Advance a => $"{{\"k\":\"a\",\"l\":{a.Lambda.ToString(CultureInfo.InvariantCulture)},\"q\":{a.Seq.ToString(CultureInfo.InvariantCulture)}}}",
        Progress g => new JsonObject { ["k"] = "g", ["s"] = g.Source, ["u"] = g.Until, ["q"] = g.Seq }.ToJsonString(),
        Loss l => new JsonObject { ["k"] = "l", ["t"] = l.Time, ["n"] = l.Count, ["r"] = l.Reason }.ToJsonString(),
        Finish => "{\"k\":\"f\"}",
        LiveStart s => new JsonObject { ["k"] = "L", ["o"] = OptionsText(s.Options) }.ToJsonString(),
        Envelope e => new JsonObject { ["k"] = "e", ["s"] = e.Source, ["t"] = e.Body, ["p"] = e.After }.ToJsonString(),
        Skipped p => new JsonObject { ["k"] = "p", ["s"] = p.Source, ["p"] = p.After }.ToJsonString(),
        Flush => "{\"k\":\"F\"}",
        SequenceStep => "{\"k\":\"s\"}",
        Commit c => new JsonObject { ["k"] = "c", ["e"] = c.Expected, ["t"] = new JsonObject(c.Texts.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))) }.ToJsonString(),
        CommitRefused => "{\"k\":\"x\"}",
        _ => throw new InvalidOperationException("unknown entry " + GetType().Name),
    };

    /// <summary>The kind letter of a stored entry, without reading the rest of it.</summary>
    public static char KindOf(string text) => text.StartsWith("{\"k\":\"", StringComparison.Ordinal) && text.Length > 6 ? text[6] : throw new InvalidDataException("no journal entry: " + text[..Math.Min(40, text.Length)]);

    /// <summary>Reads a stored entry. Rows are read with the layout their source has now, and their texts get their numbers from `it`.</summary>
    public static Entry Parse(string text, Func<string, Layout> layoutOf, Interner it)
    {
        var kind = KindOf(text);
        if (kind == 'r') { var (source, seq, rows) = BatchCodec.Decode(text, layoutOf, it); return new Rows(source, seq, rows); }
        using var d = JsonDocument.Parse(text); var r = d.RootElement;
        string S(string name) => r.GetProperty(name).GetString()!;
        long N(string name) => r.GetProperty(name).GetInt64();
        return kind switch
        {
            'h' => new Header(S("tenant"), HostOptions.StoredLateHorizon(JsonNode.Parse(text)!), S("cluster"), S("space"), S("mode"), ParseFiles(JsonNode.Parse(text)!["files"]!)),
            'a' => new Advance(N("l"), N("q")),
            'g' => new Progress(S("s"), N("u"), N("q")),
            'l' => new Loss(N("t"), N("n"), S("r")),
            'f' => new Finish(),
            'L' => new LiveStart(ParseOptions(S("o"))),
            'e' => new Envelope(S("s"), S("t"), S("p")),
            'p' => new Skipped(S("s"), S("p")),
            'F' => new Flush(),
            's' => new SequenceStep(),
            'c' => new Commit(N("e"), r.GetProperty("t").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!)),
            'x' => new CommitRefused(),
            _ => throw new InvalidDataException("unknown journal entry " + text[..Math.Min(40, text.Length)]),
        };
    }
}

/// <summary>A row batch as JSON, independent of the process: interned columns carry their strings, other columns their numbers. Weights and presence are written only when they are not the default.</summary>
public static class BatchCodec
{
    public static string Encode(string source, long seq, Batch b, Interner it)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("k", "r"); w.WriteString("s", source); w.WriteNumber("q", seq); w.WriteNumber("n", b.Rows);
            if (b.W is not null) { w.WriteStartArray("w"); for (var k = 0; k < b.Rows; k++) w.WriteNumberValue(b.Weight(b.RowAt(k))); w.WriteEndArray(); }
            w.WriteStartArray("c");
            for (var c = 0; c < b.Layout.Count; c++)
            {
                var col = b.Cols[c]; var interned = b.Layout.Interned?[c] ?? false;
                w.WriteStartObject();
                var absent = false;
                for (var k = 0; k < b.Rows; k++) if (!col.Present(b.RowAt(k))) { absent = true; break; }
                if (absent) { w.WriteStartArray("p"); for (var k = 0; k < b.Rows; k++) w.WriteNumberValue(col.Present(b.RowAt(k)) ? 1 : 0); w.WriteEndArray(); }
                w.WriteStartArray("v");
                for (var k = 0; k < b.Rows; k++)
                {
                    var r = b.RowAt(k);
                    if (!col.Present(r)) { w.WriteNullValue(); continue; }
                    if (col.IsDec) w.WriteNumberValue(col.D[r]);
                    else if (interned) w.WriteStringValue(it.Get((int)col.L[r]));
                    else w.WriteNumberValue(col.L[r]);
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    public static (string Source, long Seq, Batch Rows) Decode(string body, Func<string, Layout> layoutOf, Interner it)
    {
        using var d = JsonDocument.Parse(body);
        var r = d.RootElement; var source = r.GetProperty("s").GetString()!; var n = r.GetProperty("n").GetInt32();
        var layout = layoutOf(source);
        var b = new Batch(layout, n) { N = n };
        if (r.TryGetProperty("w", out var ws)) { b.W = new long[Math.Max(n, 1)]; var i = 0; foreach (var x in ws.EnumerateArray()) b.W[i++] = x.GetInt64(); }
        var ci = 0;
        foreach (var cj in r.GetProperty("c").EnumerateArray())
        {
            var col = b.Cols[ci]; var interned = layout.Interned?[ci] ?? false;
            if (cj.TryGetProperty("p", out var ps)) { col.P = new bool[Math.Max(n, 1)]; var i = 0; foreach (var x in ps.EnumerateArray()) col.P[i++] = x.GetInt32() != 0; }
            var k = 0;
            foreach (var x in cj.GetProperty("v").EnumerateArray())
            {
                if (x.ValueKind != JsonValueKind.Null)
                {
                    if (col.IsDec) col.D[k] = x.GetDecimal();
                    else if (interned) col.L[k] = it.Intern(x.GetString()!);
                    else col.L[k] = x.GetInt64();
                }
                k++;
            }
            ci++;
        }
        return (source, r.GetProperty("q").GetInt64(), b);
    }
}

/// <summary>The view of a durable store that one engine generation sees. Graph names of generation 0 are the plain names; a definition commit builds a fresh engine whose logs start empty under `name@generation`. While the journal is replayed (`recovering`), the engine starts from head 0 and each commit it makes is compared with the stored commit at that revision: equal means the commit already happened (nothing is written), different means the log and the recomputation diverge (refused). Past the stored head a commit is written as usual.</summary>
public sealed class DurableView(IGraphStore inner, int generation, Func<bool> recovering) : IGraphStore
{
    sealed record StoredLog(long Base, List<CommittedPatch> Patches);
    readonly Dictionary<string, StoredLog> stored = new(StringComparer.Ordinal);
    public long Verified { get; private set; }
    GraphKey Map(GraphKey g) => generation == 0 ? g : g with { Graph = g.Graph + "@" + generation.ToString(CultureInfo.InvariantCulture) };

    /// <summary>The revision interval and the edits; the graph name differs between generations, the cause is a label.</summary>
    static bool SameChange(Patch stored, Patch recomputed) => stored.From == recomputed.From && stored.To == recomputed.To && stored.Edits.AsSpan().SequenceEqual(recomputed.Edits.AsSpan());

    public HeadInfo Head(GraphKey g) => recovering() ? new HeadInfo(0, 0) : inner.Head(Map(g));

    public CommitResult Commit(CommitBatch b)
    {
        var key = Map(b.Graph);
        if (recovering())
        {
            // the stored commits after the head the engine starts from (the whole log after a restart from nothing, the tail after a seal)
            if (!stored.TryGetValue(key.Graph + "|" + key.Tenant, out var log)) stored[key.Graph + "|" + key.Tenant] = log = new StoredLog(b.ExpectedHead, [.. inner.Follow(key, b.ExpectedHead)]);
            if (b.ExpectedHead - log.Base < log.Patches.Count)
            {
                for (var i = 0; i < b.Patches.Count; i++)
                {
                    var at = b.ExpectedHead + i - log.Base;
                    if (at >= log.Patches.Count) return new CommitResult.Rejected($"the stored log of {key.Graph} ends at revision {log.Base + log.Patches.Count}, the recomputation commits {b.ExpectedHead + b.Patches.Count}");
                    if (!SameChange(log.Patches[(int)at].Patch, b.Patches[i])) return new CommitResult.Rejected($"the commit at revision {b.ExpectedHead + i + 1} of {key.Graph} differs from the stored one");
                }
                Verified += b.Patches.Count;
                return new CommitResult.Committed(b.ExpectedHead + b.Patches.Count);
            }
        }
        return inner.Commit(b with { Graph = key });
    }

    public IReadOnlyList<ReadResult> Read(GraphKey g, long revision, IReadOnlyList<ElementKey> keys) => inner.Read(Map(g), revision, keys);
    public AdjacencyPage ScanAdjacency(GraphKey g, long revision, string endpoint, Direction dir, string? type, int pageSize, string? continuation) => inner.ScanAdjacency(Map(g), revision, endpoint, dir, type, pageSize, continuation);
    public IEnumerable<CommittedPatch> Follow(GraphKey g, long after) => inner.Follow(Map(g), after);
    public SealInfo Seal(GraphKey g, long revision) => inner.Seal(Map(g), revision);
    public IEnumerable<Element> OpenSnapshot(GraphKey g, long revision) => inner.OpenSnapshot(Map(g), revision);
    public void Dispose() { }   // the host owns the durable store
}

public sealed class InMemoryJournal : IJournalStore
{
    readonly List<string> entries = [];
    public long JournalCount { get { lock (entries) return entries.Count; } }
    public void JournalAppend(string body) { lock (entries) entries.Add(body); }
    public IEnumerable<string> JournalRead(long after = 0) { lock (entries) return [.. entries.Skip((int)after)]; }
    readonly List<SealRecord> seals = [];
    public void SealPut(long position, string meta, byte[] data) { lock (entries) { seals.Add(new SealRecord(position, meta, data)); while (seals.Count > 2) seals.RemoveAt(0); } }
    public SealRecord? SealLatest() { lock (entries) return seals.Count == 0 ? null : seals[^1]; }
}
