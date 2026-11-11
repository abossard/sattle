using System.Collections.Immutable;
using System.Text;
using Sattle.Core;

namespace Sattle.Reference;

public sealed record EntityFact(string Id, string Type, ImmutableSortedDictionary<string, Value> Props);
public sealed record RelFact(string Id, string Type, string Source, string Target, ImmutableSortedDictionary<string, Value> Props);

/// <summary>One closed telemetry window of one property of one element (Def. 7.7, without completeness).</summary>
public readonly record struct WindowId(string Element, string Property, long Start) : IComparable<WindowId>
{
    public int CompareTo(WindowId o) =>
        string.CompareOrdinal(Element, o.Element) is not 0 and var e ? e : string.CompareOrdinal(Property, o.Property) is not 0 and var p ? p : Start.CompareTo(o.Start);
}

/// <summary>What a graph is: its elements and its telemetry windows, and beside them the conflicts that are reported with it (Def. 6.4, D34: a conflicting property or window is absent, the event does not fail).</summary>
public sealed record GraphFacts(
    ImmutableSortedDictionary<string, EntityFact> Entities,
    ImmutableSortedDictionary<string, RelFact> Rels,
    ImmutableSortedDictionary<WindowId, decimal> Telemetry,
    ImmutableSortedSet<string> Conflicts)
{
    public static readonly GraphFacts Empty = new(
        ImmutableSortedDictionary.Create<string, EntityFact>(StringComparer.Ordinal),
        ImmutableSortedDictionary.Create<string, RelFact>(StringComparer.Ordinal),
        ImmutableSortedDictionary<WindowId, decimal>.Empty,
        ImmutableSortedSet.Create<string>(StringComparer.Ordinal));

    /// <summary>One line per element and window, sorted: the form two results are compared in. Conflicts are a report, not part of the graph.</summary>
    public ImmutableArray<string> Lines()
    {
        static string Props(ImmutableSortedDictionary<string, Value> p) => string.Join(",", p.Select(kv => kv.Key + "=" + Canon.Of(kv.Value)));
        return
        [
            .. Entities.Values.Select(e => $"entity {e.Id} {e.Type} {{{Props(e.Props)}}}"),
            .. Rels.Values.Select(r => $"rel {r.Id} {r.Type} {r.Source} -> {r.Target} {{{Props(r.Props)}}}"),
            .. Telemetry.Select(t => $"window {t.Key.Element} {t.Key.Property} {t.Key.Start} = {Norm.Decimal(t.Value)}"),
        ];
    }

    public override string ToString() => new StringBuilder().AppendJoin('\n', Lines()).ToString();
}
