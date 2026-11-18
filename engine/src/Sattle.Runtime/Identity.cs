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
    /// <summary>A key: its domain's index and up to four canonical component codes. A component is a text code of the interner, so it is held in 4 bytes.</summary>
    internal readonly record struct Key(int Dom, int A, int B, int C, int D);
    readonly Dictionary<Key, int> bound = [];
    readonly List<IdDomain> domains = [];
    public List<IdentityBinding> Pending { get; } = [];
    /// <summary>The store took the pending bindings: the list is emptied, and a list grown past 4,096 by a large step is let go.</summary>
    public void Committed() { Pending.Clear(); if (Pending.Capacity > 4096) Pending.Capacity = 0; }
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
            for (var i = 0; i < comps.Length; i++) comps[i] = it.Get(i switch { 0 => k.A, 1 => k.B, 2 => k.C, _ => k.D });
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

    static Key K(IdDomain d, ReadOnlySpan<long> c) => new(d.Index, c.Length > 0 ? (int)c[0] : 0, c.Length > 1 ? (int)c[1] : 0, c.Length > 2 ? (int)c[2] : 0, c.Length > 3 ? (int)c[3] : 0);

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
    /// <summary>The canonicalizer leaves every text as it is.</summary>
    public static bool KeepsText(string canon) => canon is not ("LowerInvariant" or "Trim" or "IpCanonical" or "UuidLower");

    public int Resolve(IdDomain d, ReadOnlySpan<long> canon)
    {
        var k = K(d, canon);
        if (bound.TryGetValue(k, out var h)) return Rep(h);
        h = Mint(d, canon);
        bound[k] = h;
        Minted++;
        Pending.Add(IdentityBinding.Bound(this, k, h));
        return h;
    }

    public int? TryResolve(IdDomain d, ReadOnlySpan<long> canon) => bound.TryGetValue(K(d, canon), out var h) ? Rep(h) : null;

    readonly Dictionary<Key, int> own = [];
    /// <summary>The id allocated for the key itself, whatever it is bound or forwarded to: a pure function of tenant, domain and components (Def. 1.9). The telemetry sink tells rows written under two ids of one class apart by it (Law 7.5 item 3); computed once per key.</summary>
    public int OwnOf(IdDomain d, ReadOnlySpan<long> canon)
    {
        var k = K(d, canon);
        if (!own.TryGetValue(k, out var h)) own[k] = h = Mint(d, canon);
        return h;
    }

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
            Pending.Add(IdentityBinding.Forward(this, loser, survivor));
            OnMerge?.Invoke(loser, survivor);
            return BindOutcome.Merged;
        }
        // The identity of an entity is the smallest id minted for any key that denotes it, whether or not that key was ever resolved before: the key's own id counts as a candidate even when nothing used it yet. Then the final ids do not depend on the order in which keys are bound (a flow naming the pod first, or the pod row first).
        Span<char> ownId = stackalloc char[KeyText.IdLength];
        Id(d, canon, ownId);
        if (((ReadOnlySpan<char>)ownId).SequenceCompareTo(it.Get(entity)) < 0)
        {
            var own = it.Intern(ownId);
            bound[k] = own; Aliased++;
            Pending.Add(IdentityBinding.Bound(this, k, own));
            forward[entity] = own; Merges++;
            Pending.Add(IdentityBinding.Forward(this, entity, own));
            OnMerge?.Invoke(entity, own);
            return BindOutcome.Merged;
        }
        bound[k] = entity;
        Aliased++;
        Pending.Add(IdentityBinding.Bound(this, k, entity));
        return BindOutcome.Bound;
    }

    /// <summary>The hash of a key: SHA-256 of its canonical text `[tenant, domain, component trees...]`.</summary>
    void KeyHash(IdDomain d, ReadOnlySpan<long> canon, Span<byte> hash)
    {
        var sb = KeyText.Open();
        Jcs.Str(sb, tenant); sb.Append(','); Jcs.Str(sb, d.Id);
        for (var i = 0; i < canon.Length; i++) { sb.Append(','); KeyText.Tree(sb, it.Get((int)canon[i])); }
        sb.Append(']');
        KeyText.Sha256(sb, hash);
    }

    /// <summary>The id minted for a key, into `into` (`KeyText.IdLength` characters).</summary>
    void Id(IdDomain d, ReadOnlySpan<long> canon, Span<char> into)
    {
        Span<byte> hash = stackalloc byte[32];
        KeyHash(d, canon, hash);
        KeyText.Id('e', hash, into);
    }
    /// <summary>The id minted for a key, interned from its characters: no string is made.</summary>
    int Mint(IdDomain d, ReadOnlySpan<long> canon)
    {
        Span<char> id = stackalloc char[KeyText.IdLength];
        Id(d, canon, id);
        return it.Intern(id);
    }

    // ---- the texts of a binding, made only when a store reads them (`IdentityBinding`) ----

    static ReadOnlySpan<long> Canon(in Key k, Span<long> into) { into[0] = k.A; into[1] = k.B; into[2] = k.C; into[3] = k.D; return into; }
    internal string DomainId(in Key k) => domains[k.Dom].Id;
    internal string Text(int h) => it.Get(h);
    /// <summary>The key's SHA-256 as hex.</summary>
    internal string HashHex(in Key k)
    {
        var d = domains[k.Dom]; Span<long> c = stackalloc long[4]; Span<byte> hash = stackalloc byte[32];
        KeyHash(d, Canon(k, c)[..d.CompNames.Length], hash);
        return Convert.ToHexStringLower(hash);
    }
    /// <summary>The key's canonical components as a JSON array.</summary>
    internal string ComponentsJson(in Key k)
    {
        var d = domains[k.Dom]; Span<long> c = stackalloc long[4]; var canon = Canon(k, c)[..d.CompNames.Length];
        var comps = KeyText.Open();
        for (var i = 0; i < canon.Length; i++) { if (i > 0) comps.Append(','); Jcs.Str(comps, it.Get((int)canon[i])); }
        comps.Append(']');
        return comps.ToString();
    }

    /// <summary>Relationship id: hash of tenant, projection, key rule, source and target ids. The type is excluded so a retype keeps the id (ADR-086).</summary>
    public static string RelationshipId(string tenant, string projection, string key, string source, string target)
    {
        Span<char> id = stackalloc char[KeyText.IdLength];
        RelationshipId(tenant, projection, key, source, target, id);
        return new string(id);
    }
    /// <summary>The same into `into` (`KeyText.IdLength` characters), so a caller that interns it makes no string.</summary>
    public static void RelationshipId(string tenant, string projection, string key, string source, string target, Span<char> into)
    {
        var sb = KeyText.Open();
        Jcs.Str(sb, tenant); sb.Append(','); Jcs.Str(sb, projection); sb.Append(','); Jcs.Str(sb, key); sb.Append(','); Jcs.Str(sb, source); sb.Append(','); Jcs.Str(sb, target); sb.Append(']');
        Span<byte> hash = stackalloc byte[32];
        KeyText.Sha256(sb, hash);
        KeyText.Id('r', hash, into);
    }
}

/// <summary>The canonical text of a key, built and hashed in buffers of the thread that are reused: minting an id allocates the id and nothing per character.</summary>
static class KeyText
{
    [ThreadStatic] static StringBuilder? key, tree;
    [ThreadStatic] static char[]? chars;
    [ThreadStatic] static byte[]? bytes;

    /// <summary>The key's text, emptied, after its opening bracket.</summary>
    public static StringBuilder Open() => (key ??= new StringBuilder(256)).Clear().Append('[');

    /// <summary>Appends the canonical tree of a text component (Rule 1.2) as a JSON string.</summary>
    public static void Tree(StringBuilder sb, string component)
    {
        var t = (tree ??= new StringBuilder(128)).Clear();
        Jcs.Tree(t, Value.Text(component));
        Jcs.Str(sb, Chars(t));
    }

    public static void Sha256(StringBuilder sb, Span<byte> hash)
    {
        var c = Chars(sb);
        var max = Encoding.UTF8.GetMaxByteCount(c.Length);
        if (bytes is null || bytes.Length < max) bytes = new byte[Math.Max(512, max)];
        var n = Encoding.UTF8.GetBytes(c, bytes);
        SHA256.HashData(bytes.AsSpan(0, n), hash);
    }

    public const int IdLength = 28;
    /// <summary>`kind:` and the first 26 base32 characters of the hash (130 bits), into `into` (`IdLength` characters).</summary>
    public static void Id(char kind, ReadOnlySpan<byte> hash, Span<char> into) { into[0] = kind; into[1] = ':'; Digests.Base32(hash, into[2..IdLength]); }

    static ReadOnlySpan<char> Chars(StringBuilder sb)
    {
        if (chars is null || chars.Length < sb.Length) chars = new char[Math.Max(256, sb.Length * 2)];
        sb.CopyTo(0, chars, sb.Length);
        return chars.AsSpan(0, sb.Length);
    }
}

/// <summary>Vectorized identity expression: canonicalize components, resolve through the catalog, yield element handles. Absent when a component is absent or cannot be canonicalized.</summary>
sealed class VId : VExpr
{
    readonly IdDomain dom; readonly VExpr[] comps; readonly IdentityCatalog cat; readonly Interner it; readonly RuntimeFaults faults;
    readonly CodeCache[] caches; readonly bool[] keepsText;
    readonly long[] buf; readonly Column[] cols;
    /// <summary>When set, `Eval` also fills `Owns` with the own id of each row's key (`IdentityCatalog.OwnOf`).</summary>
    public bool TrackOwn { get; set; }
    public Column? Owns { get; private set; }
    public VId(IdDomain dom, VExpr[] comps, IdentityCatalog cat, Interner it, RuntimeFaults faults)
    {
        this.dom = dom; this.comps = comps; this.cat = cat; this.it = it; this.faults = faults;
        caches = [.. comps.Select(_ => new CodeCache())]; keepsText = [.. dom.Canonicalizers.Select(IdentityCatalog.KeepsText)];
        buf = new long[comps.Length]; cols = new Column[comps.Length];
    }
    public override Column Eval(Batch b)
    {
        for (var k = 0; k < comps.Length; k++) cols[k] = comps[k].Eval(b);
        var n = b.N; var o = Scratch(n);
        if (TrackOwn) { Owns ??= new Column(false); Owns.Ensure(n); if (Owns.P is not null) Array.Fill(Owns.P, true, 0, n); }
        for (var r = 0; r < b.Rows; r++)        // only the selected rows: resolving a key mints and records an identity
        {
            var i = b.RowAt(r);
            var ok = true;
            for (var k = 0; k < cols.Length && ok; k++)
            {
                if (cols[k].P is not null && !cols[k].P![i]) { ok = false; break; }
                var code = (int)cols[k].L[i];
                if (keepsText[k] && !it.IsYoung(code)) { buf[k] = code; continue; }          // an old code is the one code of its text, so the text's exact canonical form is the code itself
                if (!caches[k].TryGet(code, out var cc))
                {
                    var s = IdentityCatalog.Canonicalize(dom.Canonicalizers[k], it.Get(code));
                    cc = s is null ? -1 : it.Intern(s);
                    if (!it.IsYoung(code)) caches[k].Set(code, cc);          // a young code may be freed and given to another text
                }
                if (cc < 0) { faults.Overflow++; ok = false; break; }
                buf[k] = cc;
            }
            if (!ok) { Absent(o, i, n); continue; }
            o.L[i] = cat.Resolve(dom, buf);
            if (TrackOwn) Owns!.L[i] = cat.OwnOf(dom, buf);
        }
        return o;
    }
    public IdDomain Domain => dom;
    public int Components => comps.Length;
    /// <summary>The component columns of a batch, in an array the next call overwrites.</summary>
    public Column[] EvalComponents(Batch b, long[][] scratch, out int n)
    {
        n = b.N;
        _ = scratch;
        for (var k = 0; k < comps.Length; k++) cols[k] = comps[k].Eval(b);
        return cols;
    }
    public bool CanonicalComponents(Column[] cols, int row, long[] into)
    {
        for (var k = 0; k < cols.Length; k++)
        {
            if (cols[k].P is not null && !cols[k].P![row]) return false;
            var code = (int)cols[k].L[row];
            if (keepsText[k] && !it.IsYoung(code)) { into[k] = code; continue; }
            if (!caches[k].TryGet(code, out var cc)) { var s = IdentityCatalog.Canonicalize(dom.Canonicalizers[k], it.Get(code)); cc = s is null ? -1 : it.Intern(s); if (!it.IsYoung(code)) caches[k].Set(code, cc); }
            if (cc < 0) return false;
            into[k] = cc;
        }
        return true;
    }
}
