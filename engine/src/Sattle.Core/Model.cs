using System.Collections.Immutable;
using System.Text;

namespace Sattle.Core;

/// <summary>A finite map from property ids to values (N8): sorted by ordinal key, no duplicates.</summary>
public sealed class Props : IEquatable<Props>
{
    readonly KeyValuePair<string, Value>[] items;
    public static readonly Props Empty = new([]);
    Props(KeyValuePair<string, Value>[] items) { this.items = items; }

    public static Props Of(IEnumerable<KeyValuePair<string, Value>> kv)
    {
        var a = kv.ToArray();
        Array.Sort(a, static (x, y) => string.CompareOrdinal(x.Key, y.Key));
        for (var i = 1; i < a.Length; i++)
            if (a[i].Key == a[i - 1].Key) throw new ArgumentException($"duplicate property {a[i].Key}");
        return a.Length == 0 ? Empty : new Props(a);
    }
    public static Props Of(params (string, Value)[] kv) => Of(kv.Select(x => KeyValuePair.Create(x.Item1, x.Item2)));

    internal static Props FromSorted(KeyValuePair<string, Value>[] sorted) => sorted.Length == 0 ? Empty : new Props(sorted);
    public IReadOnlyList<KeyValuePair<string, Value>> Items => items;
    public int Count => items.Length;
    public bool TryGet(string key, out Value v)
    {
        var lo = 0; var hi = items.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var c = string.CompareOrdinal(items[mid].Key, key);
            if (c == 0) { v = items[mid].Value; return true; }
            if (c < 0) lo = mid + 1; else hi = mid - 1;
        }
        v = Value.Null;
        return false;
    }
    public Value? Get(string key) => TryGet(key, out var v) ? v : null;
    public string? Text(string key) => TryGet(key, out var v) && v is TextValue t ? t.S : null;
    public Props With(string key, Value v) => Of(items.Where(x => x.Key != key).Append(KeyValuePair.Create(key, v)));
    public Props Without(string key) => items.Any(x => x.Key == key) ? Of(items.Where(x => x.Key != key)) : this;

    public bool Equals(Props? o)
    {
        if (o is null || o.items.Length != items.Length) return false;
        for (var i = 0; i < items.Length; i++)
            if (items[i].Key != o.items[i].Key || !items[i].Value.Equals(o.items[i].Value)) return false;
        return true;
    }
    public override bool Equals(object? o) => o is Props p && Equals(p);
    public override int GetHashCode() { var h = new HashCode(); foreach (var i in items) { h.Add(i.Key); h.Add(i.Value); } return h.ToHashCode(); }
}

/// <summary>Element key: entity, relationship or the single ontology slot (Ch. 3).</summary>
public readonly record struct ElementKey(char Kind, string Id) : IComparable<ElementKey>
{
    public static ElementKey Entity(string id) => new('e', id);
    public static ElementKey Rel(string id) => new('r', id);
    public static readonly ElementKey Ontology = new('o', "");
    public override string ToString() => Kind == 'o' ? "o" : Kind + ":" + Id;
    /// <summary>Order of the key string in UTF-16 code units (Def. 3.5).</summary>
    public int CompareTo(ElementKey o) => string.CompareOrdinal(ToString(), o.ToString());
    /// <summary>Canonical edit order: ontology slot first, then entities, then relationships, by id (Ch. 4, Theorem 4.4).</summary>
    public int EditOrder(ElementKey o)
    {
        var a = Kind == 'o' ? 0 : Kind == 'e' ? 1 : 2; var b = o.Kind == 'o' ? 0 : o.Kind == 'e' ? 1 : 2;
        return a != b ? a.CompareTo(b) : string.CompareOrdinal(Id, o.Id);
    }
}

public abstract record Element { public abstract ElementKey Key { get; } }
public sealed record Entity(string Id, string Type, Props Props) : Element { public override ElementKey Key => ElementKey.Entity(Id); }
/// <summary>Relationship state. Endpoints are entity ids and may dangle (ADR-084). The type is not part of the identity (ADR-086).</summary>
public sealed record Rel(string Id, string Source, string Target, string Type, Props Props) : Element { public override ElementKey Key => ElementKey.Rel(Id); }
public sealed record OntologyState(string Id, string Version, string Digest) : Element { public override ElementKey Key => ElementKey.Ontology; }

/// <summary>Def. 3.6: key, before and after states; null is absence.</summary>
public sealed record Edit(ElementKey Key, Element? Before, Element? After)
{
    public bool Effective => !Equals(Before, After);
    public static Edit Create(Element a) => new(a.Key, null, a);
    public static Edit Remove(Element b) => new(b.Key, b, null);
    public static Edit Change(Element b, Element a) => new(a.Key, b, a);
    public Edit Inverted() => new(Key, After, Before);
    public bool WellKeyed => (Before is null || Before.Key == Key) && (After is null || After.Key == Key);
}

public sealed record Cause(ImmutableArray<string> Inputs, string Configuration)
{
    public static readonly Cause None = new([], "0");
}

public sealed class AlgebraRejected(string message) : Exception(message);

/// <summary>Frontier of a commit (Def. 3.10): source positions, input graph revisions, configuration revision.</summary>
public sealed record Frontier(ImmutableSortedDictionary<string, long> Pos, ImmutableSortedDictionary<string, long> Gin, long Cfg)
{
    public static readonly Frontier Empty = new(ImmutableSortedDictionary<string, long>.Empty, ImmutableSortedDictionary<string, long>.Empty, 0);
    public bool Equals(Frontier? o) => o is not null && Cfg == o.Cfg && Pos.SequenceEqual(o.Pos) && Gin.SequenceEqual(o.Gin);
    public override int GetHashCode() => HashCode.Combine(Cfg, Pos.Count, Gin.Count);
    public bool LessOrEqual(Frontier o) => Cfg <= o.Cfg && Le(Pos, o.Pos) && Le(Gin, o.Gin);
    static bool Le(ImmutableSortedDictionary<string, long> a, ImmutableSortedDictionary<string, long> b) => a.All(kv => b.TryGetValue(kv.Key, out var v) && v >= kv.Value);
    public override string ToString() => $"pos={string.Join(",", Pos.Select(x => x.Key + ":" + x.Value))} gin={string.Join(",", Gin.Select(x => x.Key + ":" + x.Value))} cfg={Cfg}";
}

