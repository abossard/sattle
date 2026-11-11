using System.Collections.Immutable;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Reference;

/// <summary>A fragment of a sink (Def. 6.3): for one element key, a create or an update with the data properties it assigns, or one telemetry window value.</summary>
public sealed record Fragment(bool IsRel, bool Update, string Id, string Type, string? Source, string? Target, ImmutableSortedDictionary<string, Value> Props, WindowId? Window, decimal WindowValue)
{
    public string Text => string.Join('\u001f', IsRel, Update, Id, Type, Source, Target, string.Join(',', Props.Select(p => p.Key + "=" + Canon.Of(p.Value))), Window, Window is null ? "" : Norm.Decimal(WindowValue));
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
            foreach (var t in assigned.Where(x => x.Set.Telemetry))
                yield return (new Fragment(isRel, true, id, type, source, target, NoProps, new WindowId(id, t.Set.PropId, ((InstantValue)read("windowStart")).Ns), Canon.Number(t.Value)), weight);
            var props = assigned.Where(x => !x.Set.Telemetry).ToImmutableSortedDictionary(x => x.Set.PropId, x => x.Value, StringComparer.Ordinal);
            if (!update || props.Count > 0) yield return (new Fragment(isRel, update, id, type, source, target, props, null, 0), weight);
        }
    }

    public static GraphFacts Of(IEnumerable<(Fragment Fragment, long Weight)> fragments)
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
                else conflicts.Add($"{id} {p.Key}: {string.Join(" | ", values.Select(Canon.Of).Order(StringComparer.Ordinal))}");
            }
            return props;
        }

        var entities = GraphFacts.Empty.Entities; var rels = GraphFacts.Empty.Rels;
        foreach (var element in positive.Where(f => f.Window is null).GroupBy(f => (f.IsRel, f.Id)))
        {
            var creates = element.Where(f => !f.Update).ToArray();
            if (creates.Length == 0) continue;
            var shapes = creates.Select(f => (f.Type, f.Source, f.Target)).Distinct().ToArray();
            if (shapes.Length > 1) { conflicts.Add($"{element.Key.Id}: {string.Join(" | ", shapes.Select(s => $"{s.Type} {s.Source} -> {s.Target}").Order(StringComparer.Ordinal))}"); continue; }
            var props = Merge(element.Key.Id, element);
            if (element.Key.IsRel) rels = rels.SetItem(element.Key.Id, new RelFact(element.Key.Id, shapes[0].Type, shapes[0].Source!, shapes[0].Target!, props));
            else entities = entities.SetItem(element.Key.Id, new EntityFact(element.Key.Id, shapes[0].Type, props));
        }
        foreach (var same in rels.Values.GroupBy(r => (r.Source, r.Target, r.Type)).Where(g => g.Count() > 1))
        {
            conflicts.Add($"uniqueness ({same.Key.Source}, {same.Key.Target}, {same.Key.Type}): {string.Join(" | ", same.Select(r => r.Id))}");
            rels = rels.RemoveRange(same.Select(r => r.Id).Order(StringComparer.Ordinal).Skip(1));
        }

        var telemetry = GraphFacts.Empty.Telemetry;
        foreach (var window in positive.Where(f => f.Window is not null).GroupBy(f => f.Window!.Value))
        {
            var values = window.Select(f => f.WindowValue).Distinct().ToArray();
            if (values.Length == 1) telemetry = telemetry.SetItem(window.Key, values[0]);
            else conflicts.Add($"window {window.Key.Element} {window.Key.Property} {window.Key.Start}: {string.Join(" | ", values.Order().Select(Norm.Decimal))}");
        }
        return new GraphFacts(entities, rels, telemetry, conflicts.ToImmutable());
    }
}
