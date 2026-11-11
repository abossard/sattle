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
