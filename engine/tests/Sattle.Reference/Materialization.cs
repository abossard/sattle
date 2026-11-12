using System.Collections.Immutable;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Reference;

/// <summary>A fragment of a sink (Def. 6.3): for one element key, a create or an update with the data properties it assigns, or one telemetry window value.
/// A telemetry fragment also names the id it was written under (`Own`: the id allocated for the key of the row, before any link; for a relationship, those of its two endpoints) and the combine rule of its property: rows written under two ids of one class are two contributions (Law 7.5 item 3).
/// `Pair`: for a window of a `mean` its sum and count, of a `last` its order (B unused), when the aggregate that wrote it carries them.</summary>
public sealed record Fragment(bool IsRel, bool Update, string Id, string Type, string? Source, string? Target, ImmutableSortedDictionary<string, Value> Props, WindowId? Window, decimal WindowValue, long WindowWidth = 0, string Own = "", string? Combine = null, (decimal A, decimal B)? Pair = null)
{
    public string Text => string.Join('\u001f', IsRel, Update, Id, Type, Source, Target, string.Join(',', Props.Select(p => p.Key + "=" + Canon.Of(p.Value))), Window, Window is null ? "" : Norm.Decimal(WindowValue), Own, Pair is { } q ? Norm.Decimal(q.A) + "/" + Norm.Decimal(q.B) : "");
}

/// <summary>Def. 6.4 as ruled (D34): materialization is a function of the multiset of positive fragments per key, whatever their sink or order. An element is present iff a create fragment for its key has positive support; its state merges the assignments of its positive fragments. A property with more than one value is absent and reported; of several relationships on one (source, target, type) the smallest id is present; a telemetry window with more than one value has none (premise B1 broken). Creates of one key that disagree on type or endpoints leave the element absent, reported.</summary>
public static class Materialization
{
    static readonly ImmutableSortedDictionary<string, Value> NoProps = ImmutableSortedDictionary.Create<string, Value>(StringComparer.Ordinal);

    public static IEnumerable<(Fragment Fragment, long Weight)> Fragments(TOp op, Func<TOp, Relation> inputOf, Identities ids, string tenant, string outputModule)
    {
        if (op is not (TEmitEntity or TEmitRel)) yield break;
        var rows = inputOf(op);
        foreach (var (row, weight) in rows.Rows)
        {
            var read = rows.Reader(row);
            string? Id(TExpr e) => Expressions.Eval(e, read, ids) is EntityValue v ? v.Id : null;
            // the id a row writes under before links: that of its own key, or the entity it names
            string? Own(TExpr e) => e is TId key ? (Expressions.KeyOf(key, read) is { } k ? Identities.Allocate(tenant, k) : null) : Id(e);
            var (isRel, update, type, sets) = op switch { TEmitEntity e => (false, e.Update, e.TypeId, e.Sets), TEmitRel r => (true, r.Update, r.TypeId, r.Sets), _ => throw new Undefined(op.Kind) };
            var (source, target) = op is TEmitRel rel ? (Id(rel.Source), Id(rel.Target)) : (null, null);
            var id = op switch
            {
                TEmitEntity e => Id(e.EntityId),
                TEmitRel r when source is not null && target is not null => Identities.RelationshipId(tenant, outputModule, r.Key, source, target),
                _ => null,
            };
            if (id is null) continue;                                  // an absent identity skips the row (Def. 5.9)
            var assigned = sets.Select(s => (Set: s, Value: Expressions.Eval(s.Value, read, ids))).Where(x => x.Value is not NullValue).ToArray();
            var own = op switch { TEmitEntity e => Own(e.EntityId), TEmitRel r => Own(r.Source) + "|" + Own(r.Target), _ => null } ?? id;
            (decimal, decimal)? PairOf(TSet s) => s.Value is not TField f ? null
                : rows.Columns.Contains(f.Name + "#sum") && read(f.Name + "#sum") is DecimalValue sum && read(f.Name + "#count") is IntegerValue n ? (sum.Q, (decimal)n.N)
                : rows.Columns.Contains(f.Name + "#order") && read(f.Name + "#order") is DecimalValue o ? (o.Q, 0m) : null;
            foreach (var t in assigned.Where(x => x.Set.Telemetry))
                yield return (new Fragment(isRel, true, id, type, source, target, NoProps, new WindowId(id, t.Set.PropId, ((InstantValue)read("windowStart")).Ns), Canon.Number(t.Value), t.Set.Period, own, t.Set.Combine, PairOf(t.Set)), weight);
            var props = assigned.Where(x => !x.Set.Telemetry).ToImmutableSortedDictionary(x => x.Set.PropId, x => x.Value, StringComparer.Ordinal);
            if (!update || props.Count > 0) yield return (new Fragment(isRel, update, id, type, source, target, props, null, 0), weight);
        }
    }

    /// <summary>`frontier`, `retention`: a telemetry window [a, b) is part of the graph while b is after the frontier minus the retention (Def. 7.13). A window that is no longer kept takes nothing else with it.</summary>
    /// <summary>A conflict as one line: its kind, what it is about, and the disagreeing values sorted by code point.</summary>
    static string Conflict(string kind, string element, string? property, long? windowStart, IEnumerable<string> values) =>
        $"{kind} {element} {property} {windowStart}: {string.Join(" | ", values.Distinct().Order(StringComparer.Ordinal))}";
    static string Plain(Value v) => v switch { DecimalValue d => Norm.Decimal(d.Q), IntegerValue i => i.N.ToString(System.Globalization.CultureInfo.InvariantCulture), TextValue t => t.S, BooleanValue b => b.B ? "true" : "false", InstantValue n => Norm.Instant(n.Ns), EntityValue e => e.Id, _ => Canon.Of(v) };

    public static GraphFacts Of(IEnumerable<(Fragment Fragment, long Weight)> fragments, long? frontier = null, long retention = long.MaxValue)
    {
        var positive = fragments.GroupBy(f => f.Fragment.Text, StringComparer.Ordinal).Where(g => g.Sum(f => f.Weight) > 0).Select(g => g.First().Fragment).ToImmutableArray();
        var conflicts = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);

        ImmutableSortedDictionary<string, Value> Merge(string id, IEnumerable<Fragment> parts)
        {
            var props = NoProps;
            foreach (var p in parts.SelectMany(f => f.Props).GroupBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var values = p.Select(kv => kv.Value).DistinctBy(Canon.Of).ToArray();
                if (values.Length == 1) props = props.SetItem(p.Key, values[0]);
                else conflicts.Add(Conflict("property", id, p.Key, null, values.Select(Plain)));
            }
            return props;
        }

        var entities = GraphFacts.Empty.Entities; var rels = GraphFacts.Empty.Rels;
        foreach (var element in positive.Where(f => f.Window is null).GroupBy(f => (f.IsRel, f.Id)))
        {
            var creates = element.Where(f => !f.Update).ToArray();
            if (creates.Length == 0) continue;
            var shapes = creates.Select(f => (f.Type, f.Source, f.Target)).Distinct().ToArray();
            if (shapes.Length > 1) { conflicts.Add(Conflict("element", element.Key.Id, null, null, shapes.Select(s => $"{s.Type} {s.Source} -> {s.Target}"))); continue; }
            var props = Merge(element.Key.Id, element);
            if (element.Key.IsRel) rels = rels.SetItem(element.Key.Id, new RelFact(element.Key.Id, shapes[0].Type, shapes[0].Source!, shapes[0].Target!, props));
            else entities = entities.SetItem(element.Key.Id, new EntityFact(element.Key.Id, shapes[0].Type, props));
        }
        foreach (var same in rels.Values.GroupBy(r => (r.Source, r.Target, r.Type)).Where(g => g.Count() > 1))
        {
            conflicts.Add(Conflict("uniqueness", same.Select(r => r.Id).Order(StringComparer.Ordinal).First(), null, null, same.Select(r => r.Id)));
            rels = rels.RemoveRange(same.Select(r => r.Id).Order(StringComparer.Ordinal).Skip(1));
        }

        var telemetry = GraphFacts.Empty.Telemetry;
        foreach (var window in positive.Where(f => f.Window is not null && (frontier is null || f.Window.Value.Start + f.WindowWidth > frontier - retention)).GroupBy(f => f.Window!.Value))
        {
            string Line(IEnumerable<decimal> values) => $"window {window.Key.Element} {window.Key.Property} {window.Key.Start}: {string.Join(" | ", values.Distinct().Order().Select(Norm.Decimal))}";
            // premise B1 under each id the window was written under: one value (with its pair), or none and a conflict
            var perOwn = window.GroupBy(f => f.Own, StringComparer.Ordinal).Select(g => g.Select(f => (f.WindowValue, f.Pair)).Distinct().ToArray()).ToArray();
            foreach (var broken in perOwn.Where(v => v.Length > 1)) conflicts.Add(Line(broken.Select(x => x.WindowValue)));
            if (perOwn.Any(v => v.Length > 1)) continue;                          // a window without a value keeps none under further combination
            // Law 7.5 item 3: the values of different ids of one class combine by the property's rule. A sum, minimum or maximum in its monoid; a mean with
            // its sum and count by adding both; a last with its order by the greater order, a tie by the greater value; any other value only with an equal one.
            var values = perOwn.Select(v => v[0]).ToArray();
            var rule = window.First().Combine;
            (decimal V, (decimal A, decimal B)? P)? Combine((decimal V, (decimal A, decimal B)? P) x, (decimal V, (decimal A, decimal B)? P) y) => rule switch
            {
                "sum" => (x.V + y.V, null),
                "min" => (Math.Min(x.V, y.V), null),
                "max" => (Math.Max(x.V, y.V), null),
                "mean" when x.P is { } p && y.P is { } q && p.B + q.B != 0 => ((p.A + q.A) / (p.B + q.B), (p.A + q.A, p.B + q.B)),
                "last" when x.P is { } p && y.P is { } q => p.A > q.A || p.A == q.A && x.V >= y.V ? x : y,
                _ => x.V == y.V && x.P == y.P ? x : null,
            };
            (decimal V, (decimal A, decimal B)? P)? combined = (values[0].WindowValue, values[0].Pair);
            foreach (var next in values.Skip(1)) combined = combined is { } acc ? Combine(acc, (next.WindowValue, next.Pair)) : null;
            if (combined is { } c) telemetry = telemetry.SetItem(window.Key, c.V);
            else conflicts.Add(Line(values.Select(x => x.WindowValue)));
        }
        return new GraphFacts(entities, rels, telemetry, conflicts.ToImmutable());
    }
}
