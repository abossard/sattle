using System.Text.Json;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

/// <summary>Reads keyed JSON records against the row fields declared by the space.</summary>
public sealed class RecordAdapter : ISourceAdapter, IRedelivery
{
    public const string AdapterId = "record";
    const string Prefix = "records:";
    readonly Dictionary<string, RowContract> rows = new(StringComparer.Ordinal);
    readonly Dictionary<(string Type, string Key), (long At, object?[]? Values)> current = [];
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

    public bool Counts(Envelope envelope) =>
        envelope.Payload.S("key") is not { } key || !current.TryGetValue((envelope.Source, key), out var old) ||
        envelope.CapturedNs >= old.At;

    public void Consume(Envelope envelope, IRowSink sink)
    {
        var type = envelope.Source[Prefix.Length..];
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
                if (!Read(field, values.P(field.Name), out next[i]))
                {
                    sink.Rejected(Id, $"Expected {field.Kind} at {type}.{field.Name}");
                    return;
                }
            }
        }
        bool same = previous.Values is null ? next is null : next is not null && previous.Values.SequenceEqual(next);
        if (exists && previous.At == envelope.CapturedNs && !same)
        {
            sink.Rejected(Id, $"Conflicting values for {type}/{key} at one checkpoint");
            return;
        }
        if (!same)
        {
            if (previous.Values is { } old) Write(sink, row, old, -1);
            if (next is not null) Write(sink, row, next, 1);
        }
        current[id] = (envelope.CapturedNs, next);
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
