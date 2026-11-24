using System.Text.Json;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

/// <summary>Reads keyed JSON records against the row fields declared by the space.</summary>
public sealed class RecordAdapter : ISourceAdapter, IRedelivery
{
    public const string AdapterId = "record";
    public const string Prefix = "records:";
    readonly Dictionary<string, RowContract> rows = new(StringComparer.Ordinal);
    /// <summary>Per record its capture time and the values of its row as text (`Encode`): plain data, so a seal holds it as it holds any text.</summary>
    readonly Dictionary<(string Type, string Key), (long At, string? Values)> current = [];
    public string Id => AdapterId;
    public IReadOnlyList<RowContract> Contracts { get; }

    public RecordAdapter(Catalog? catalog, TypedSpace? space)
    {
        if (catalog is not null && space is not null)
        {
            var view = new DefView(catalog.Graph);
            foreach (var row in space.Sources.Where(s => s.Adapter == Id).Select(s => s.RowTypeId).Distinct())
            {
                var name = Ids.Name(row);
                var fields = view.Children(row, "field").Select(f => new RowField(view.Text(f, "name")!,
                    Ids.Name(view.Target(f, "valueKind")!), view.Bool(f, "optional"))).ToArray();
                rows.Add(name, new(name, fields));
            }
        }
        Contracts = [.. rows.Values];
    }

    public bool Handles(string source) => source.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>A keyed record delivered again counts when it is not older than the record held for its key; a heartbeat or a loss mark has no record key, so its second delivery is a duplicate.</summary>
    public bool Counts(Envelope envelope) =>
        envelope.Payload.S("key") is { } key && (!current.TryGetValue((envelope.Source, key), out var old) || envelope.CapturedNs >= old.At);

    public void Consume(Envelope envelope, IRowSink sink)
    {
        var type = envelope.Source[Prefix.Length..];
        if (envelope.Payload.ValueKind == JsonValueKind.Object && envelope.Payload.P("key") is null)
        {
            // without a key: `{}` is a heartbeat, progress of the source and no row; `{"lost": n}` is a loss mark (Def. 10.4) at `time`, else at the capture time
            if (envelope.Payload.P("lost") is { } lost)
            {
                if (!rows.ContainsKey(type)) { sink.Rejected(Id, $"Expected a declared row type for the loss of {type}"); return; }
                if (lost.ValueKind != JsonValueKind.Number || !lost.TryGetInt64(out var count) || count < 0) { sink.Rejected(Id, $"Expected a count of lost records for {type}"); return; }
                var at = envelope.Payload.S("time") is { } time ? Norm.TryParseInstant(time, out var ns) ? ns : (long?)null : envelope.CapturedNs;
                if (at is null) { sink.Rejected(Id, $"Expected an instant as the time of a loss for {type}"); return; }
                sink.SourceLoss(at.Value, count, envelope.Payload.S("reason") ?? "sourceLoss");
            }
            else if (envelope.Payload.EnumerateObject().Any()) sink.Rejected(Id, $"Expected a key, a count of lost records or an empty heartbeat for {type}");
            return;
        }
        if (!rows.TryGetValue(type, out var row) || envelope.Payload.S("key") is not { Length: > 0 } key)
        {
            sink.Rejected(Id, $"Expected a declared row type and a nonempty key for {type}");
            return;
        }
        var id = (envelope.Source, key);
        var exists = current.TryGetValue(id, out var previous);
        if (exists && previous.At > envelope.CapturedNs) return;
        var deleted = envelope.Payload.P("deleted") is { ValueKind: JsonValueKind.True };
        object?[]? next = null;
        if (!deleted)
        {
            if (envelope.Payload.P("values") is not { ValueKind: JsonValueKind.Object } values)
            {
                sink.Rejected(Id, $"Expected values for {type}");
                return;
            }
            next = new object?[row.Fields.Length];
            for (var i = 0; i < row.Fields.Length; i++)
            {
                var field = row.Fields[i];
                // An opting-in row can preserve the source record identity independently of
                // the payload's grouping columns or its human-facing evidence reference.
                if (field is { Name: "$recordKey", Kind: "Text" }) next[i] = key;
                else if (!Read(field, values.P(field.Name), out next[i]))
                {
                    sink.Rejected(Id, $"Expected {field.Kind} at {type}.{field.Name}");
                    return;
                }
            }
        }
        var old = previous.Values is { } text ? Decode(text, row) : null;
        bool same = old is null ? next is null : next is not null && old.SequenceEqual(next);
        if (exists && previous.At == envelope.CapturedNs && !same)
        {
            sink.Rejected(Id, $"Conflicting values for {type}/{key} at one checkpoint");
            return;
        }
        if (!same)
        {
            if (old is not null) Write(sink, row, old, -1);
            if (next is not null) Write(sink, row, next, 1);
        }
        current[id] = (envelope.CapturedNs, next is null ? null : Encode(next));
    }

    /// <summary>The values of a row as one text: per field a tag and the value (`-` absent, `s` text, `b` boolean, `i` integer or instant, `d` decimal with its digits), separated by U+001F.</summary>
    static string Encode(object?[] values) => string.Join('\u001f', values.Select(v => v switch
    {
        null => "-",
        string t => "s" + t,
        bool b => b ? "b1" : "b0",
        decimal d => "d" + d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        long n => "i" + n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"a record value of type {v.GetType().Name}"),
    }));

    static object?[] Decode(string text, RowContract row)
    {
        var parts = text.Split('\u001f');
        var values = new object?[row.Fields.Length];
        for (var i = 0; i < values.Length; i++)
            values[i] = parts[i][0] switch
            {
                '-' => null,
                's' => parts[i][1..],
                'b' => parts[i][1] == '1',
                'd' => decimal.Parse(parts[i][1..], System.Globalization.CultureInfo.InvariantCulture),
                _ => long.Parse(parts[i][1..], System.Globalization.CultureInfo.InvariantCulture),
            };
        return values;
    }

    static bool Read(RowField field, JsonElement? json, out object? value)
    {
        value = null;
        if (json is not { } token) return field.Optional;
        switch (field.Kind, token.ValueKind)
        {
            case ("Text", JsonValueKind.String): value = token.GetString(); return true;
            case ("Boolean", JsonValueKind.True or JsonValueKind.False): value = token.GetBoolean(); return true;
            case ("Integer", JsonValueKind.Number) when token.TryGetInt64(out var n): value = n; return true;
            case ("Decimal", JsonValueKind.Number) when token.TryGetDecimal(out var d): value = d; return true;
            case ("Instant", JsonValueKind.String) when Norm.TryParseInstant(token.GetString()!, out var ns): value = ns; return true;
            default: return false;
        }
    }

    static void Write(IRowSink sink, RowContract row, object?[] values, long weight)
    {
        var writer = sink.Begin(row.RowType);
        writer.Begin(weight);
        for (var i = 0; i < values.Length; i++)
        {
            switch (values[i])
            {
                case null: writer.Absent(i); break;
                case string text: writer.Text(i, text); break;
                case bool flag: writer.Code(i, flag ? 1 : 0); break;
                case decimal number: writer.Dec(i, number); break;
                case long instant when row.Fields[i].Kind == "Instant": writer.Instant(i, instant); break;
                case long integer: writer.Int(i, integer); break;
            }
        }
    }
}
