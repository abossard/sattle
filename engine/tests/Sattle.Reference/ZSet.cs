using System.Collections;
using System.Collections.Immutable;
using System.Numerics;
using Sattle.Core;

namespace Sattle.Reference;

/// <summary>One equality and one order for values: numbers by value (2.5 equals 2.50, an Integer equals the Decimal of the same value), text by code point, absent as its own value.</summary>
public static class Canon
{
    public static string Of(Value v) => v switch
    {
        NullValue => "-",
        TextValue t => "t:" + t.S,
        IntegerValue i => "n:" + i.N.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DecimalValue d => "n:" + Norm.Decimal(d.Q),
        BooleanValue b => b.B ? "b:1" : "b:0",
        InstantValue n => "i:" + n.Ns.ToString(System.Globalization.CultureInfo.InvariantCulture),
        EntityValue e => "e:" + e.Id,
        _ => throw new Undefined($"no canonical form for {v.Kind}"),
    };

    public static string Of(ImmutableArray<Value> row) => string.Join('\u001f', row.Select(Of));

    public static bool IsNumber(Value v) => v is IntegerValue or DecimalValue;
    public static decimal Number(Value v) => v switch { IntegerValue i => (decimal)i.N, DecimalValue d => d.Q, _ => throw new Undefined($"{v.Kind} is not a number") };

    public static int Compare(Value a, Value b) => (a, b) switch
    {
        (IntegerValue x, IntegerValue y) => x.N.CompareTo(y.N),
        _ when IsNumber(a) && IsNumber(b) => Number(a).CompareTo(Number(b)),
        (TextValue x, TextValue y) => string.CompareOrdinal(x.S, y.S),
        (BooleanValue x, BooleanValue y) => x.B.CompareTo(y.B),
        (InstantValue x, InstantValue y) => x.Ns.CompareTo(y.Ns),
        (EntityValue x, EntityValue y) => string.CompareOrdinal(x.Id, y.Id),
        _ => throw new Undefined($"{a.Kind} and {b.Kind} are not comparable"),
    };

    public static BigInteger Integer(Value v) => v is IntegerValue i ? i.N : throw new Undefined($"{v.Kind} is not an Integer");
}

/// <summary>The reference has no answer: the paper does not define the case. Never a guess.</summary>
public sealed class Undefined(string message) : Exception(message);

/// <summary>A multiset of rows with integer weights (Def. 6.2), kept sorted by the canonical text of the row. Rows of weight zero do not exist.</summary>
public sealed class ZSet : IEnumerable<(ImmutableArray<Value> Row, long Weight)>
{
    readonly ImmutableSortedDictionary<string, (ImmutableArray<Value> Row, long Weight)> items;
    ZSet(ImmutableSortedDictionary<string, (ImmutableArray<Value> Row, long Weight)> items) { this.items = items; }

    public static readonly ZSet Empty = new(ImmutableSortedDictionary.Create<string, (ImmutableArray<Value>, long)>(StringComparer.Ordinal));

    public static ZSet Of(IEnumerable<(ImmutableArray<Value> Row, long Weight)> rows) => rows.Aggregate(Empty, (z, r) => z.Add(r.Row, r.Weight));

    public ZSet Add(ImmutableArray<Value> row, long weight)
    {
        var key = Canon.Of(row);
        var sum = (items.TryGetValue(key, out var cur) ? cur.Weight : 0) + weight;
        return new(sum == 0 ? items.Remove(key) : items.SetItem(key, (row, sum)));
    }

    public ZSet Plus(ZSet other) => other.Aggregate(this, (z, r) => z.Add(r.Row, r.Weight));
    public ZSet Minus(ZSet other) => other.Aggregate(this, (z, r) => z.Add(r.Row, -r.Weight));
    public int Count => items.Count;

    public IEnumerator<(ImmutableArray<Value> Row, long Weight)> GetEnumerator() => items.Values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A relation: named columns and a multiset of rows over them.</summary>
public sealed record Relation(ImmutableArray<string> Columns, ZSet Rows)
{
    public static Relation Empty(IEnumerable<string> columns) => new([.. columns], ZSet.Empty);
    public int Index(string column) => Columns.IndexOf(column) is >= 0 and var i ? i : throw new Undefined($"column {column} is not in [{string.Join(", ", Columns)}]");
    public Func<string, Value> Reader(ImmutableArray<Value> row) => name => row[Index(name)];
}
