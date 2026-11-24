using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

/// <summary>One line of a capture: the envelope of lab/CAPTURE-FORMAT.md. Its elements point into the token index of the record (`CaptureReader.Parse`), which is valid until the envelope is disposed: a reader that keeps a value copies it.</summary>
public sealed record Envelope(string Source, long CapturedNs, long Seq, string File, JsonElement Payload, JsonElement Meta, bool HasMeta) : IDisposable
{
    JsonDocument? index; string? key; bool keyRead;
    internal Envelope Owning(JsonDocument doc) { index = doc; return this; }
    /// <summary>The delivery key of the envelope (`key`), read when first asked: only live input deduplicates by it.</summary>
    public string? Key { get { if (!keyRead) { key = index?.RootElement.S("key"); keyRead = true; } return key; } }
    /// <summary>Returns the token index to the pool.</summary>
    public void Dispose() => index?.Dispose();
}

/// <summary>Where adapters write rows. Rows of one type accumulate into a chunk; a row of another type flushes the current chunk first, so input order is kept across row types.</summary>
public interface IRowSink
{
    RowWriter Begin(string rowType);
    void Rejected(string source, string reason);
    /// <summary>The source lost events at timeNs (count 0 when unknown). reason names the kind of loss; the telemetry windows containing the time become Partial(reason).</summary>
    void SourceLoss(long timeNs, long count, string reason = "sourceLoss");
}

public sealed record RowField(string Name, string Kind, bool Optional);
public sealed record RowContract(string RowType, RowField[] Fields);

/// <summary>A compiled adapter: capture records in, typed rows out. Declares the row contracts it produces; activation checks them against the catalog (section 3.4).</summary>
public interface ISourceAdapter
{
    string Id { get; }
    IReadOnlyList<RowContract> Contracts { get; }
    bool Handles(string envelopeSource);
    void Consume(Envelope e, IRowSink sink);
}

/// <summary>An adapter for which a record that is delivered again can still say something. Live input drops an envelope whose key it has seen; it asks first whether this delivery counts.</summary>
public interface IRedelivery
{
    /// <summary>True when the record is to be consumed although its key was seen before. Reads the adapter's state, changes nothing.</summary>
    bool Counts(Envelope e);
}

public static class AdapterRegistry
{
    /// <summary>The adapters of a space: the record adapter, the one way rows enter the engine (lab/CAPTURE-FORMAT.md, keyed records).</summary>
    public static IReadOnlyList<ISourceAdapter> Create(TypedSpace? space = null, Catalog? catalog = null) => [new RecordAdapter(catalog, space)];

    /// <summary>Every Source.adapter must be registered and the row contract it declares must equal the contract in the catalog (the digest check of section 3.4, as a shape check so a definition change needs no rebuild).</summary>
    public static List<Diagnostic> Verify(Catalog catalog, TypedSpace space, IEnumerable<ISourceAdapter> adapters)
    {
        var res = new List<Diagnostic>();
        var d = new DefView(catalog.Graph);
        var byId = adapters.ToDictionary(a => a.Id);
        foreach (var s in space.Sources)
        {
            if (!byId.TryGetValue(s.Adapter, out var ad)) { res.Add(new Diagnostic("AdapterUnknown", "error", s.RowTypeId, $"source {s.Name}: adapter '{s.Adapter}' is not registered")); continue; }
            var name = Ids.Name(s.RowTypeId);
            var c = ad.Contracts.FirstOrDefault(x => x.RowType == name);
            if (c is null) { res.Add(new Diagnostic("AdapterContract", "error", s.RowTypeId, $"adapter {ad.Id} does not produce rows of type {name}")); continue; }
            var fields = d.Children(s.RowTypeId, "field").Select(f => new RowField(d.Text(f, "name")!, Ids.Name(d.Target(f, "valueKind")!), d.Bool(f, "optional"))).ToArray();
            if (!fields.SequenceEqual(c.Fields)) res.Add(new Diagnostic("AdapterContract", "error", s.RowTypeId, $"adapter {ad.Id} declares row contract {name}({string.Join(", ", c.Fields.Select(f => f.Name + (f.Optional ? "?" : "") + ":" + f.Kind))}) but the catalog has ({string.Join(", ", fields.Select(f => f.Name + (f.Optional ? "?" : "") + ":" + f.Kind))})"));
        }
        return res;
    }
}

static class J
{
    public static JsonElement? P(this JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;
    public static string? S(this JsonElement e, string name) => e.P(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    public static JsonElement? Path(this JsonElement e, params ReadOnlySpan<string> path) { JsonElement? cur = e; foreach (var p in path) { cur = cur?.P(p); if (cur is null) return null; } return cur; }
    public static string? PathS(this JsonElement e, params ReadOnlySpan<string> path) => e.Path(path) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    public static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>The text of a string value: decoded into `buffer` when it fits and has no escapes (then its UTF-8 bytes are its text), else made a string.</summary>
    public static ReadOnlySpan<char> Chars(JsonElement s, Span<char> buffer)
    {
        var utf8 = JsonMarshal.GetRawUtf8Value(s)[1..^1];
        return utf8.Length <= buffer.Length && utf8.IndexOf((byte)'\\') < 0 ? buffer[..Encoding.UTF8.GetChars(utf8, buffer)] : s.GetString();
    }

    /// <summary>Writes a string value straight into a row: decoded on the stack (`Chars`) and looked up in the interner by span, so a text the interner has costs no allocation. Not a string: `orElse`; an empty string is absent too unless `keepEmpty`.</summary>
    public static void Text(this RowWriter w, int col, JsonElement? v, string? orElse = null, bool keepEmpty = false)
    {
        if (v is not { ValueKind: JsonValueKind.String } s) { w.Text(col, orElse); return; }
        var t = Chars(s, stackalloc char[256]);
        if (t.IsEmpty && !keepEmpty) w.Text(col, orElse); else w.Text(col, t);
    }

    /// <summary>The instant a string value holds (`Norm.TryParseInstant`), read without making a string.</summary>
    public static bool Instant(JsonElement? v, out long ns)
    {
        ns = 0;
        return v is { ValueKind: JsonValueKind.String } s && Norm.TryParseInstant(Chars(s, stackalloc char[64]), out ns);
    }
}

public static class RowDef
{
    public static RowField F(string n, string k, bool opt = false) => new(n, k, opt);
}
