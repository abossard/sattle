using System.Net;
using System.Security.Cryptography;
using System.Text;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

public sealed class IdDomain(string id, string name, string[] compNames, string[] canonicalizers, string? identifiesType)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string[] CompNames { get; } = compNames;
    public string[] Canonicalizers { get; } = canonicalizers;
    public string? IdentifiesType { get; } = identifiesType;
    public int Index { get; set; }
}

public enum BindOutcome { Bound, AlreadyBound, Merged, Conflict }

/// <summary>Identity resolution (research 03 section 5): key to identity bindings, append-only. A key resolves to its binding or is minted by hash. Same ordered inputs give the same ids.</summary>
public sealed class IdentityCatalog(string tenant, Interner it)
{
    readonly record struct Key(int Dom, long A, long B, long C, long D);
    readonly Dictionary<Key, int> bound = [];
    readonly List<IdDomain> domains = [];
    public List<IdentityBinding> Pending { get; } = [];
    public long Minted { get; private set; }
    public long Aliased { get; private set; }
    public long Conflicts { get; private set; }
    public long Merges { get; private set; }
    /// <summary>Forwarding records of merges (Theorem 1.7): loser handle to survivor handle. Append-only; the survivor is the smaller id, so chains are acyclic and finite. Bindings are never modified: resolution applies Rep.</summary>
    readonly Dictionary<int, int> forward = [];
    public IReadOnlyDictionary<int, int> Forwarding => forward;
    /// <summary>Called when two identities merge (loser, survivor): the engine moves what it has emitted under the loser.</summary>
    public Action<int, int>? OnMerge { get; set; }

    /// <summary>The representative of an identity: follow forwarding records until none applies.</summary>
    public int Rep(int h) { while (forward.TryGetValue(h, out var s)) h = s; return h; }
    public int Count => bound.Count;

    /// <summary>Every key bound to an identity with its domain and canonical component texts, by the representative id of the identity. Read-only view for explaining an id (a reference to an element that is not in a graph).</summary>
    public IEnumerable<(IdDomain Domain, string Id, string[] Components)> Bindings()
    {
        foreach (var (k, h) in bound)
        {
            var d = domains[k.Dom]; var comps = new string[d.CompNames.Length];
            for (var i = 0; i < comps.Length; i++) comps[i] = it.Get((int)(i switch { 0 => k.A, 1 => k.B, 2 => k.C, _ => k.D }));
            yield return (d, it.Get(Rep(h)), comps);
        }
    }

    public IdDomain Domain(string id, string name, string[] comps, string[] canon, string? identifies)
    {
        var d = domains.FirstOrDefault(x => x.Id == id);
        if (d is not null) return d;
        if (comps.Length > 4) throw new NotSupportedException("identity domains with more than 4 components are not supported in this slice");
        d = new IdDomain(id, name, comps, canon, identifies) { Index = domains.Count };
        domains.Add(d);
        return d;
    }

    static Key K(IdDomain d, ReadOnlySpan<long> c) => new(d.Index, c.Length > 0 ? c[0] : 0, c.Length > 1 ? c[1] : 0, c.Length > 2 ? c[2] : 0, c.Length > 3 ? c[3] : 0);

    /// <summary>Canonical text of a component under its canonicalizer; null when the value cannot be canonicalized (malformed IP or UUID).</summary>
    public static string? Canonicalize(string canon, string v) => canon switch
    {
        "Exact" => v,
        "LowerInvariant" => v.ToLowerInvariant(),
        "Trim" => v.Trim(),
        "IpCanonical" => IPAddress.TryParse(v, out var ip) ? ip.ToString() : null,
        "UuidLower" => Guid.TryParse(v, out var g) ? g.ToString("D") : null,
        _ => v,
    };

    public int Resolve(IdDomain d, ReadOnlySpan<long> canon)
    {
        var k = K(d, canon);
        if (bound.TryGetValue(k, out var h)) return Rep(h);
        var (hash, id, comps) = Mint(d, canon);
        h = it.Intern(id);
        bound[k] = h;
        Minted++;
        Pending.Add(new IdentityBinding(d.Id, hash, id, comps));
        return h;
    }

    public int? TryResolve(IdDomain d, ReadOnlySpan<long> canon) => bound.TryGetValue(K(d, canon), out var h) ? Rep(h) : null;

    /// <summary>Rule B2 (alias): binds an unbound key to an existing identity. A key already bound to another identity of the same entity type means late evidence that two identities denote one entity: they merge (forwarding record, survivor the smaller id). Identities of different entity types are a conflict and the link is ignored.</summary>
    public BindOutcome Bind(IdDomain d, ReadOnlySpan<long> canon, int entity, IdDomain? left = null)
    {
        var k = K(d, canon);
        entity = Rep(entity);
        if (bound.TryGetValue(k, out var h))
        {
            var rh = Rep(h);
            if (rh == entity) return BindOutcome.AlreadyBound;
            if (left is not null && left.IdentifiesType != d.IdentifiesType && left.IdentifiesType is not null && d.IdentifiesType is not null) { Conflicts++; return BindOutcome.Conflict; }
            var (survivor, loser) = string.CompareOrdinal(it.Get(entity), it.Get(rh)) <= 0 ? (entity, rh) : (rh, entity);
            forward[loser] = survivor; Merges++;
            Pending.Add(new IdentityBinding("~forward", it.Get(loser), it.Get(survivor), "[]"));
            OnMerge?.Invoke(loser, survivor);
            return BindOutcome.Merged;
        }
        // The identity of an entity is the smallest id minted for any key that denotes it, whether or not that key was ever resolved before: the key's own id counts as a candidate even when nothing used it yet. Then the final ids do not depend on the order in which keys are bound (a flow naming the pod first, or the pod row first).
        var (hash, ownId, comps) = Mint(d, canon);
        if (string.CompareOrdinal(ownId, it.Get(entity)) < 0)
        {
            var own = it.Intern(ownId);
            bound[k] = own; Aliased++;
            Pending.Add(new IdentityBinding(d.Id, hash, ownId, comps));
            forward[entity] = own; Merges++;
            Pending.Add(new IdentityBinding("~forward", it.Get(entity), ownId, "[]"));
            OnMerge?.Invoke(entity, own);
            return BindOutcome.Merged;
        }
        bound[k] = entity;
        Aliased++;
        Pending.Add(new IdentityBinding(d.Id, hash, it.Get(entity), comps));
        return BindOutcome.Bound;
    }

    (string Hash, string Id, string Comps) Mint(IdDomain d, ReadOnlySpan<long> canon)
    {
        var sb = new StringBuilder("[");
        Jcs.Str(sb, tenant); sb.Append(','); Jcs.Str(sb, d.Id);
        var comps = new StringBuilder("[");
        for (var i = 0; i < canon.Length; i++)
        {
            var enc = Jcs.Encode(Value.Text(it.Get((int)canon[i])));
            sb.Append(','); Jcs.Str(sb, enc);
            if (i > 0) comps.Append(','); Jcs.Str(comps, it.Get((int)canon[i]));
        }
        sb.Append(']'); comps.Append(']');
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return (Convert.ToHexStringLower(bytes), "e:" + Digests.Base32(bytes)[..26], comps.ToString());
    }

    /// <summary>Relationship id: hash of tenant, projection, key rule, source and target ids. The type is excluded so a retype keeps the id (ADR-086).</summary>
    public static string RelationshipId(string tenant, string projection, string key, string source, string target)
    {
        var sb = new StringBuilder("[");
        Jcs.Str(sb, tenant); sb.Append(','); Jcs.Str(sb, projection); sb.Append(','); Jcs.Str(sb, key); sb.Append(','); Jcs.Str(sb, source); sb.Append(','); Jcs.Str(sb, target); sb.Append(']');
        return "r:" + Digests.Base32(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..26];
    }
}

/// <summary>Vectorized identity expression: canonicalize components, resolve through the catalog, yield element handles. Absent when a component is absent or cannot be canonicalized.</summary>
sealed class VId : VExpr
{
    readonly IdDomain dom; readonly VExpr[] comps; readonly IdentityCatalog cat; readonly Interner it; readonly RuntimeFaults faults;
    readonly Dictionary<int, int>[] caches;
    readonly long[] buf;
    public VId(IdDomain dom, VExpr[] comps, IdentityCatalog cat, Interner it, RuntimeFaults faults)
    {
        this.dom = dom; this.comps = comps; this.cat = cat; this.it = it; this.faults = faults;
        caches = [.. comps.Select(_ => new Dictionary<int, int>())];
        buf = new long[comps.Length];
    }
    public override Column Eval(Batch b)
    {
        var cols = comps.Select(c => c.Eval(b)).ToArray();
        var n = b.N; var o = Scratch(n);
        for (var r = 0; r < b.Rows; r++)        // only the selected rows: resolving a key mints and records an identity
        {
            var i = b.RowAt(r);
            var ok = true;
            for (var k = 0; k < cols.Length && ok; k++)
            {
                if (cols[k].P is not null && !cols[k].P![i]) { ok = false; break; }
                var code = (int)cols[k].L[i];
                if (!caches[k].TryGetValue(code, out var cc))
                {
                    var s = IdentityCatalog.Canonicalize(dom.Canonicalizers[k], it.Get(code));
                    cc = s is null ? -1 : it.Intern(s);
                    caches[k][code] = cc;
                }
                if (cc < 0) { faults.Overflow++; ok = false; break; }
                buf[k] = cc;
            }
            if (!ok) { Absent(o, i, n); continue; }
            o.L[i] = cat.Resolve(dom, buf);
        }
        return o;
    }
    public IdDomain Domain => dom;
    public Column[] EvalComponents(Batch b, long[][] scratch, out int n)
    {
        n = b.N;
        _ = scratch;
        return [.. comps.Select(c => c.Eval(b))];
    }
    public bool CanonicalComponents(Column[] cols, int row, long[] into)
    {
        for (var k = 0; k < cols.Length; k++)
        {
            if (cols[k].P is not null && !cols[k].P![row]) return false;
            var code = (int)cols[k].L[row];
            if (!caches[k].TryGetValue(code, out var cc)) { var s = IdentityCatalog.Canonicalize(dom.Canonicalizers[k], it.Get(code)); cc = s is null ? -1 : it.Intern(s); caches[k][code] = cc; }
            if (cc < 0) return false;
            into[k] = cc;
        }
        return true;
    }
}
