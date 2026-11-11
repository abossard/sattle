using Sattle.Core;

namespace Sattle.Tests;

/// <summary>Seeded generators for the property-style tests. Every case is reproducible from its seed.</summary>
public sealed class Gen(int seed)
{
    public readonly Random R = new(seed);
    static readonly string[] EntityIds = ["a", "b", "c", "d", "e", "f"];
    static readonly string[] Types = ["T1", "T2", "T3"];
    static readonly string[] RelTypes = ["R1", "R2"];
    static readonly string[] Props = ["p", "q", "r"];

    public Value Value(int depth = 0)
    {
        switch (R.Next(depth > 1 ? 6 : 7))
        {
            case 0: return Core.Value.Text(R.Next(3) == 0 ? "ü😀\n\"\\" : "t" + R.Next(5));
            case 1: return Core.Value.Integer(R.Next(-3, 4));
            case 2: return Core.Value.Decimal(R.Next(-300, 300) / 100m);
            case 3: return Core.Value.Boolean(R.Next(2) == 0);
            case 4: return Core.Value.Instant(R.NextInt64(-2_000_000_000_000_000_000, 2_000_000_000_000_000_000));
            case 5: return R.Next(2) == 0 ? Core.Value.Null : Core.Value.Entity(EntityIds[R.Next(EntityIds.Length)]);
            default: return Core.Value.List(Enumerable.Range(0, R.Next(3)).Select(_ => Value(depth + 1)));
        }
    }

    public Props PropMap() => Core.Props.Of(Props.Where(_ => R.Next(2) == 0).Select(p => KeyValuePair.Create(p, Value())));
    public Entity Entity(string? id = null) => new(id ?? EntityIds[R.Next(EntityIds.Length)], Types[R.Next(Types.Length)], PropMap());
    public Rel Rel(string? id = null) => new(id ?? "x" + R.Next(6), EntityIds[R.Next(EntityIds.Length)], EntityIds[R.Next(EntityIds.Length)], RelTypes[R.Next(RelTypes.Length)], PropMap());

    /// <summary>A random edit against g: create, change, retype or remove. May produce a patch that violates U; callers filter by Check.</summary>
    Edit RandomEdit(Graph g)
    {
        var existing = g.Elements().Where(e => e is not OntologyState).ToList();
        if (existing.Count > 0 && R.Next(3) > 0)
        {
            var cur = existing[R.Next(existing.Count)];
            if (R.Next(3) == 0) return Edit.Remove(cur);
            return cur switch
            {
                Entity e => Edit.Change(e, e with { Type = R.Next(4) == 0 ? Types[R.Next(Types.Length)] : e.Type, Props = PropMap() }),
                Rel r => Edit.Change(r, r with { Type = R.Next(3) == 0 ? RelTypes[R.Next(RelTypes.Length)] : r.Type, Target = R.Next(4) == 0 ? EntityIds[R.Next(EntityIds.Length)] : r.Target, Props = PropMap() }),
                _ => throw new InvalidOperationException(),
            };
        }
        if (R.Next(2) == 0) { var id = EntityIds[R.Next(EntityIds.Length)]; return g.Entity(id) is null ? Edit.Create(Entity(id)) : Edit.Create(Entity("n" + R.Next(100))); }
        var rid = "x" + R.Next(6);
        return g.Rel(rid) is null ? Edit.Create(Rel(rid)) : Edit.Create(Rel("y" + R.Next(100)));
    }

    /// <summary>A patch applicable to g (possibly with guard-only edits), or null when none was found.</summary>
    /// <summary>`unchecked`: the first candidate is returned whether or not the graph accepts it, so a test can compare how two graphs judge it (with the default only accepted patches come back, and a rejection is never seen).</summary>
    public Patch? PatchFor(Graph g, long from, bool guardOnly = true, bool @unchecked = false)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var edits = new Dictionary<ElementKey, Edit>();
            for (var n = R.Next(1, 5); n > 0; n--) { var e = RandomEdit(g); edits[e.Key] = e; }
            if (guardOnly && R.Next(3) == 0 && g.Elements().FirstOrDefault(e => e is not OntologyState && !edits.ContainsKey(e.Key)) is { } ge)
                edits[ge.Key] = new Edit(ge.Key, ge, ge);
            var list = edits.Values.ToList();
            if (@unchecked || g.Check(list) is null) return new Patch("g", from, from + 1, list);
        }
        return null;
    }

    public Graph RandomGraph()
    {
        var g = new Graph();
        for (var i = 0; i < R.Next(0, 8); i++) if (PatchFor(g, 0, false) is { } p) g.Apply(p.Edits);
        return g;
    }

    /// <summary>A graph and a chain of n composable patches on it.</summary>
    public (Graph Base, List<Patch> Chain) Chain(int n)
    {
        var g0 = RandomGraph();
        var g = g0.Clone();
        var list = new List<Patch>();
        for (var i = 0; i < n; i++)
        {
            var p = PatchFor(g, i);
            if (p is null) break;
            g.Apply(p.Edits);
            list.Add(p);
        }
        return (g0, list);
    }
}
