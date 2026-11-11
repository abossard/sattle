using System.Collections.Immutable;
using System.Numerics;
using System.Text;

namespace Sattle.Core;

/// <summary>Ch. 4, Def. 4.1: p = (g, r, r', E, cause). Edits are a set with distinct keys, kept in canonical order.</summary>
public sealed class Patch : IEquatable<Patch>
{
    public string Graph { get; }
    public long From { get; }
    public long To { get; }
    public ImmutableArray<Edit> Edits { get; }
    public Cause Cause { get; }
    readonly Dictionary<ElementKey, Edit> byKey;

    public Patch(string graph, long from, long to, IEnumerable<Edit> edits, Cause? cause = null)
    {
        Graph = graph; From = from; To = to; Cause = cause ?? Cause.None;
        var list = edits.ToList();
        byKey = new Dictionary<ElementKey, Edit>(list.Count);
        foreach (var e in list)
        {
            if (!e.WellKeyed) throw new AlgebraRejected($"edit {e.Key}: state is not under its own key");
            if (!byKey.TryAdd(e.Key, e)) throw new AlgebraRejected($"edit {e.Key}: duplicate key");
        }
        list.Sort(static (a, b) => a.Key.EditOrder(b.Key));
        Edits = [.. list];
    }

    public bool TryGet(ElementKey k, out Edit e) => byKey.TryGetValue(k, out e!);
    public bool IsEmpty => Edits.Length == 0;

    /// <summary>Def. 4.2. Undefined (null) unless to(p) = from(q) and every shared key composes. Guard-only results are kept.</summary>
    public Patch? Then(Patch q)
    {
        if (Graph != q.Graph || To != q.From) return null;
        var merged = new List<Edit>();
        foreach (var e in Edits)
        {
            if (q.byKey.TryGetValue(e.Key, out var f))
            {
                if (!Equals(e.After, f.Before)) return null;
                merged.Add(new Edit(e.Key, e.Before, f.After));
            }
            else merged.Add(e);
        }
        foreach (var f in q.Edits) if (!byKey.ContainsKey(f.Key)) merged.Add(f);
        return new Patch(Graph, From, q.To, merged, new Cause([.. Cause.Inputs, .. q.Cause.Inputs], q.Cause.Configuration));
    }

    public Patch ThenOrThrow(Patch q) => Then(q) ?? throw new AlgebraRejected("p then q is undefined");

    /// <summary>Def. 4.3: swaps from and to, and the before and after state of every edit.</summary>
    public Patch Inverse() => new(Graph, To, From, Edits.Select(e => e.Inverted()), Cause);
    /// <summary>The guard identity: same keys, before = after = before.</summary>
    public Patch Epsilon() => new(Graph, From, From, Edits.Select(e => new Edit(e.Key, e.Before, e.Before)), Cause);
    /// <summary>Def. 4.4: removes guard-only edits.</summary>
    public Patch Net() => new(Graph, From, To, Edits.Where(e => e.Effective), Cause);

    /// <summary>apply(p, G): partial; the result is a new graph, G is untouched.</summary>
    public Graph? ApplyTo(Graph g)
    {
        var c = g.Clone();
        return c.Apply(Edits) is null ? c : null;
    }

    public Delta Delta()
    {
        var d = new Delta();
        foreach (var e in Edits)
        {
            if (e.After is not null) d.Add(new Fact(e.Key, e.After), 1);
            if (e.Before is not null) d.Add(new Fact(e.Key, e.Before), -1);
        }
        return d;
    }

    public bool Equals(Patch? o) => o is not null && Graph == o.Graph && From == o.From && To == o.To && Edits.AsSpan().SequenceEqual(o.Edits.AsSpan());
    public override bool Equals(object? o) => o is Patch p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(Graph, From, To, Edits.Length);
}

/// <summary>Def. 4.5: a fact is a key with a state.</summary>
public readonly record struct Fact(ElementKey Key, Element State);

public sealed record LossReport(bool ChronologyLost, bool IntermediateStatesLost, bool CauseDetailLost);

/// <summary>Signed deltas: finite-support maps from facts to integer weights (Def. 4.6).</summary>
public sealed class Delta : IEquatable<Delta>
{
    readonly Dictionary<Fact, long> w = [];
    public IReadOnlyDictionary<Fact, long> Weights => w;
    public int Support => w.Count;
    public bool IsZero => w.Count == 0;

    public void Add(Fact f, long weight)
    {
        if (weight == 0) return;
        var n = w.GetValueOrDefault(f) + weight;
        if (n == 0) w.Remove(f); else w[f] = n;
    }
    public static Delta operator +(Delta a, Delta b) { var r = a.Copy(); foreach (var (f, x) in b.w) r.Add(f, x); return r; }
    public static Delta operator -(Delta a) { var r = new Delta(); foreach (var (f, x) in a.w) r.Add(f, -x); return r; }
    public static Delta operator -(Delta a, Delta b) => a + -b;
    public Delta Copy() { var r = new Delta(); foreach (var (f, x) in w) r.w[f] = x; return r; }

    /// <summary>χ(G): every element of G with weight 1.</summary>
    public static Delta Chi(Graph g) { var d = new Delta(); foreach (var e in g.Elements()) d.Add(new Fact(e.Key, e), 1); return d; }

    /// <summary>Def. 4.8: materializable iff weights in {0,1}, one fact per key, and U holds. Returns the graph or null.</summary>
    public static Graph? Materialize(Graph b, Delta z)
    {
        var sum = Chi(b) + z;
        var byKey = new Dictionary<ElementKey, Element>();
        foreach (var (f, x) in sum.w)
        {
            if (x != 1 || !byKey.TryAdd(f.Key, f.State)) return null;
        }
        var g = new Graph();
        var edits = byKey.Values.Select(Edit.Create).ToList();
        return g.Apply(edits) is null ? g : null;
    }

    /// <summary>Def. 4.9 toPatch: the canonical net transition, defined iff materialization is.</summary>
    public static Patch? ToPatch(Delta z, Graph b, string graph, long from, long to)
    {
        var m = Materialize(b, z);
        if (m is null) return null;
        var keys = z.w.Keys.Select(f => f.Key).Distinct();
        var edits = keys.Select(k => new Edit(k, b.Get(k), m.Get(k))).Where(e => e.Effective);
        return new Patch(graph, from, to, edits);
    }

    /// <summary>squash(p1..pn) = net(p1 then ... then pn) with a loss report.</summary>
    public static (Patch Net, LossReport Loss)? Squash(IReadOnlyList<Patch> ps)
    {
        if (ps.Count == 0) return null;
        var acc = ps[0];
        for (var i = 1; i < ps.Count; i++) { var n = acc.Then(ps[i]); if (n is null) return null; acc = n; }
        var counts = new Dictionary<ElementKey, int>();
        foreach (var p in ps) foreach (var e in p.Edits) counts[e.Key] = counts.GetValueOrDefault(e.Key) + 1;
        var causes = ps.Count(p => p.Cause.Inputs.Length > 0) > 1;
        return (acc.Net(), new LossReport(ps.Count > 1, counts.Values.Any(c => c >= 2), causes));
    }

    public bool Equals(Delta? o) => o is not null && w.Count == o.w.Count && w.All(kv => o.w.TryGetValue(kv.Key, out var x) && x == kv.Value);
    public override bool Equals(object? o) => o is Delta d && Equals(d);
    public override int GetHashCode() => w.Count;

    public string Encode()
    {
        var sb = new StringBuilder("[");
        var first = true;
        foreach (var (f, x) in w.OrderBy(kv => kv.Key.Key.ToString(), StringComparer.Ordinal).ThenBy(kv => Jcs.Encode(kv.Key.State), StringComparer.Ordinal))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('['); Jcs.Str(sb, f.Key.ToString()); sb.Append(','); Jcs.Element(sb, f.State); sb.Append(','); Jcs.Str(sb, x.ToString()); sb.Append(']');
        }
        return sb.Append(']').ToString();
    }
}

/// <summary>The commit rule of Def. 3.9 and the revision log of Def. 3.8, as a pure model. Stores implement the same rule.</summary>
public sealed class GraphLog(string graph)
{
    readonly List<(Patch Patch, Frontier Frontier)> commits = [];
    readonly Graph head = new();
    public string GraphId { get; } = graph;
    public long Head => commits.Count;
    public Graph State => head;
    public IReadOnlyList<(Patch Patch, Frontier Frontier)> Commits => commits;

    /// <summary>Returns the new patch, or null with an error. Empty or ineffective edit sets create no revision.</summary>
    public (Patch? Patch, string? Error) Commit(IEnumerable<Edit> edits, Frontier? frontier = null, Cause? cause = null)
    {
        var eff = edits.Where(e => e.Effective).ToList();
        if (eff.Count == 0) return (null, "empty edit set creates no revision");
        var err = head.Apply(eff);
        if (err is not null) return (null, err);
        var p = new Patch(GraphId, Head, Head + 1, eff, cause);
        commits.Add((p, frontier ?? Frontier.Empty));
        return (p, null);
    }

    /// <summary>G_r by replay from genesis.</summary>
    public Graph StateAt(long r)
    {
        var g = new Graph();
        for (var i = 0; i < r; i++)
            if (g.Apply(commits[i].Patch.Edits) is { } e) throw new InvalidOperationException(e);
        return g;
    }
}
