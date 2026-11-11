using System.Text;
using Sattle.Core;

namespace Sattle.Tests;

/// <summary>The first-slice graph (string ids, one record per element), kept as the reference model for differential tests of the hot layout.</summary>
public sealed class ReferenceGraph
{
    readonly Dictionary<string, Entity> entities = new(StringComparer.Ordinal);
    readonly Dictionary<string, Rel> rels = new(StringComparer.Ordinal);
    readonly Dictionary<(string, string, string), string> unique = new();
    readonly Dictionary<string, HashSet<string>> outAdj = new(StringComparer.Ordinal), inAdj = new(StringComparer.Ordinal);
    OntologyState? ontology;

    public int EntityCount => entities.Count;
    public int RelCount => rels.Count;
    public int Count => entities.Count + rels.Count + (ontology is null ? 0 : 1);
    public OntologyState? Ontology => ontology;
    public Entity? Entity(string id) => entities.GetValueOrDefault(id);
    public Rel? Rel(string id) => rels.GetValueOrDefault(id);
    public IEnumerable<Entity> Entities => entities.Values;
    public IEnumerable<Rel> Rels => rels.Values;
    public Rel? FindRel(string source, string target, string type) => unique.TryGetValue((source, target, type), out var id) ? rels[id] : null;
    static readonly HashSet<string> NoRels = [];
    public IEnumerable<string> OutRels(string entityId) => outAdj.GetValueOrDefault(entityId) ?? NoRels;
    public IEnumerable<string> InRels(string entityId) => inAdj.GetValueOrDefault(entityId) ?? NoRels;
    public int OutDegree(string entityId) => outAdj.TryGetValue(entityId, out var s) ? s.Count : 0;
    public int InDegree(string entityId) => inAdj.TryGetValue(entityId, out var s) ? s.Count : 0;

    public Element? Get(ElementKey k) => k.Kind switch
    {
        'e' => entities.GetValueOrDefault(k.Id),
        'r' => rels.GetValueOrDefault(k.Id),
        _ => ontology,
    };

    public ReferenceGraph Clone()
    {
        var g = new ReferenceGraph();
        foreach (var e in entities.Values) g.Put(e);
        foreach (var r in rels.Values) g.Put(r);
        g.ontology = ontology;
        return g;
    }

    /// <summary>Guard check, uniqueness check U and key consistency (Def. 3.7, 3.9). Returns null when the edit set is applicable.</summary>
    public string? Check(IReadOnlyCollection<Edit> edits)
    {
        var seen = new HashSet<ElementKey>();
        foreach (var e in edits)
        {
            if (!e.WellKeyed) return $"edit {e.Key}: state is not under its own key (WF1)";
            if (!seen.Add(e.Key)) return $"edit {e.Key}: duplicate key in edit set";
            var cur = Get(e.Key);
            if (!Equals(cur, e.Before)) return $"edit {e.Key}: guard failed, current state differs from before-state";
        }
        HashSet<string>? gone = null;
        foreach (var e in edits) if (e.Before is Rel b) (gone ??= []).Add(b.Id);
        Dictionary<(string, string, string), string>? added = null;
        foreach (var e in edits)
        {
            if (e.After is not Rel a) continue;
            var key = (a.Source, a.Target, a.Type);
            if (unique.TryGetValue(key, out var other) && other != a.Id && (gone is null || !gone.Contains(other)))
                return $"relationship {a.Id}: duplicates {other} on (source, target, type) (U)";
            added ??= [];
            if (!added.TryAdd(key, a.Id)) return $"relationship {a.Id}: duplicates {added[key]} on (source, target, type) inside the patch (U)";
        }
        return null;
    }

    public string? Apply(IReadOnlyCollection<Edit> edits)
    {
        var err = Check(edits);
        if (err is not null) return err;
        foreach (var e in edits) if (e.Before is not null) Remove(e.Before);
        foreach (var e in edits) if (e.After is not null) Put(e.After);
        return null;
    }

    void Put(Element e)
    {
        switch (e)
        {
            case Entity en: entities[en.Id] = en; break;
            case Rel r:
                rels[r.Id] = r;
                unique[(r.Source, r.Target, r.Type)] = r.Id;
                Adj(outAdj, r.Source).Add(r.Id);
                Adj(inAdj, r.Target).Add(r.Id);
                break;
            case OntologyState o: ontology = o; break;
        }
    }
    static HashSet<string> Adj(Dictionary<string, HashSet<string>> d, string k) { if (!d.TryGetValue(k, out var s)) d[k] = s = []; return s; }
    void Remove(Element e)
    {
        switch (e)
        {
            case Entity en: entities.Remove(en.Id); break;
            case Rel r:
                rels.Remove(r.Id);
                unique.Remove((r.Source, r.Target, r.Type));
                if (outAdj.TryGetValue(r.Source, out var o)) { o.Remove(r.Id); if (o.Count == 0) outAdj.Remove(r.Source); }
                if (inAdj.TryGetValue(r.Target, out var i)) { i.Remove(r.Id); if (i.Count == 0) inAdj.Remove(r.Target); }
                break;
            case OntologyState: ontology = null; break;
        }
    }

    /// <summary>Edits that turn a into b (guard = a's state). Empty when equal.</summary>
    public static List<Edit> Diff(ReferenceGraph a, ReferenceGraph b)
    {
        var edits = new List<Edit>();
        foreach (var e in a.Elements()) { var n = b.Get(e.Key); if (!Equals(e, n)) edits.Add(new Edit(e.Key, e, n)); }
        foreach (var e in b.Elements()) if (a.Get(e.Key) is null) edits.Add(new Edit(e.Key, null, e));
        return edits;
    }

    public IEnumerable<Element> Elements()
    {
        if (ontology is not null) yield return ontology;
        foreach (var e in entities.Values) yield return e;
        foreach (var r in rels.Values) yield return r;
    }

    /// <summary>Def. 3.5: enc(G) = JCS of the pairs sorted by key string.</summary>
    public string Encode()
    {
        var sorted = Elements().OrderBy(e => e.Key.ToString(), StringComparer.Ordinal).ToList();
        var sb = new StringBuilder("[");
        for (var i = 0; i < sorted.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('['); Jcs.Str(sb, sorted[i].Key.ToString()); sb.Append(','); Jcs.Element(sb, sorted[i]); sb.Append(']');
        }
        return sb.Append(']').ToString();
    }
    public string Digest() => Digests.Of(Encode());

    /// <summary>Def. 3.4 graph equality.</summary>
    public bool SameAs(ReferenceGraph o) =>
        Count == o.Count && Equals(ontology, o.ontology) &&
        entities.All(kv => o.entities.TryGetValue(kv.Key, out var x) && x.Equals(kv.Value)) &&
        rels.All(kv => o.rels.TryGetValue(kv.Key, out var x) && x.Equals(kv.Value));

    /// <summary>WF3: the relationship uniqueness invariant holds in the index and in the relationship set.</summary>
    public bool UniquenessHolds()
    {
        var seen = new HashSet<(string, string, string)>();
        foreach (var r in rels.Values) if (!seen.Add((r.Source, r.Target, r.Type))) return false;
        return seen.Count == unique.Count;
    }
}
