using System.Net;
using System.Runtime.InteropServices;
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

/// <summary>Identity resolution (Ch. 2, Def. 2.5): a key resolves to the smallest own id of its class under the present links. Own ids are minted by hash, so the ids are a function of the set of present links.</summary>
public sealed class IdentityCatalog(string tenant, Interner it)
{
    /// <summary>A key: its domain's index and up to four canonical component codes. A component is a text code of the interner, so it is held in 4 bytes.</summary>
    internal readonly record struct Key(int Dom, int A, int B, int C, int D);
    /// <summary>Per key seen: its own id (Def. 1.9), minted when the key is first seen.</summary>
    readonly Dictionary<Key, int> bound = [];
    readonly List<IdDomain> domains = [];
    public List<IdentityBinding> Pending { get; } = [];
    /// <summary>The store took the pending bindings: the list is emptied, and a list grown past 4,096 by a large step is let go.</summary>
    public void Committed() { Pending.Clear(); if (Pending.Capacity > 4096) Pending.Capacity = 0; }
    public long Minted { get; private set; }
    /// <summary>Links that became present (their rows reached a positive weight).</summary>
    public long Aliased { get; private set; }
    public long Conflicts { get; private set; }
    public long Merges { get; private set; }
    /// <summary>Changes the resolution of some key can see: a key seen, a merge or a split. Equal versions resolve every key alike.</summary>
    public long Version { get; private set; }

    /// <summary>The links (Ch. 2, Def. 2.5) as pairs of own ids, the smaller handle first, with the summed weight of their rows: a link is present while its weight is positive.</summary>
    readonly Dictionary<(int, int), long> links = [];
    /// <summary>Per own id, the own ids it has a present link with.</summary>
    readonly Dictionary<int, List<int>> near = [];
    /// <summary>The classes of two or more own ids: per member its representative (the smallest id of the class, ordinal), and per representative its members. An id in no class is its own representative and is in neither.</summary>
    readonly Dictionary<int, int> rep = [];
    readonly Dictionary<int, List<int>> members = [];
    public IReadOnlyDictionary<int, List<int>> Classes => members;
    /// <summary>Called when two classes merge (loser, survivor): the engine moves what it has emitted under the loser.</summary>
    public Action<int, int>? OnMerge { get; set; }
    /// <summary>Called when a class splits (its representative, the representative of the part that leaves, whether an own id is in that part): the engine moves what it has emitted from keys of that part.</summary>
    public Action<int, int, Func<int, bool>>? OnSplit { get; set; }

    /// <summary>The representative of an identity: the smallest own id of its class.</summary>
    public int Rep(int h) => rep.Count == 0 ? h : rep.GetValueOrDefault(h, h);
    public int Count => bound.Count;

    /// <summary>Every key seen with its domain and canonical component texts, by the representative id of its class. Read-only view for explaining an id (a reference to an element that is not in a graph).</summary>
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

    public int Resolve(IdDomain d, ReadOnlySpan<long> canon) => Rep(Own(d, canon));

    /// <summary>`Resolve`, and in `own` the key's own id: a pure function of tenant, domain and components (Def. 1.9). A sink keeps it beside a row whose id differs, so a split can tell which rows leave (Law 7.5 item 3).</summary>
    public int Resolve(IdDomain d, ReadOnlySpan<long> canon, out int own) => Rep(own = Own(d, canon));

    /// <summary>The own id of a key, minted and recorded when the key is first seen.</summary>
    int Own(IdDomain d, ReadOnlySpan<long> canon)
    {
        var k = K(d, canon);
        ref var e = ref CollectionsMarshal.GetValueRefOrAddDefault(bound, k, out var found);
        if (found) return e;
        e = Mint(d, canon);
        Minted++; Version++;
        Pending.Add(IdentityBinding.Bound(this, k, e));
        return e;
    }

    public int? TryResolve(IdDomain d, ReadOnlySpan<long> canon) => bound.TryGetValue(K(d, canon), out var h) ? Rep(h) : null;

    /// <summary>Rows of a link of two keys (Ch. 2, Def. 2.5) with their summed weight `w`. The link is present while its weight is positive; the classes are the connected parts of the present links, each named by its smallest own id. A link that becomes present between two classes merges them; one that ends may split its class, and the part without the representative gets its own smallest id. Keys of different entity types are a conflict: such a link is never present.</summary>
    public void Link(IdDomain ld, ReadOnlySpan<long> lc, IdDomain rd, ReadOnlySpan<long> rc, long w)
    {
        if (w == 0) return;
        int a = Own(ld, lc), b = Own(rd, rc);
        if (a == b) return;
        if (ld.IdentifiesType is not null && rd.IdentifiesType is not null && ld.IdentifiesType != rd.IdentifiesType) { if (w > 0) Conflicts++; return; }
        var pair = a < b ? (a, b) : (b, a);
        ref var weight = ref CollectionsMarshal.GetValueRefOrAddDefault(links, pair, out _);
        var was = weight > 0; weight += w;
        var now = weight > 0;
        if (weight == 0) links.Remove(pair);
        if (was == now) return;
        if (now) { Near(a).Add(b); Near(b).Add(a); Aliased++; Join(a, b); }
        else { Near(a).Remove(b); Near(b).Remove(a); Part(a, b); }
    }

    List<int> Near(int h) { ref var l = ref CollectionsMarshal.GetValueRefOrAddDefault(near, h, out _); return l ??= []; }
    List<int> MembersOf(int r) => members.TryGetValue(r, out var m) ? m : [r];

    /// <summary>A present link joins the classes of `a` and `b`: the larger representative forwards to the smaller.</summary>
    void Join(int a, int b)
    {
        int ra = Rep(a), rb = Rep(b);
        if (ra == rb) return;
        var (survivor, loser) = string.CompareOrdinal(it.Get(ra), it.Get(rb)) <= 0 ? (ra, rb) : (rb, ra);
        var into = members.TryGetValue(survivor, out var m) ? m : members[survivor] = [survivor];
        rep[survivor] = survivor;
        foreach (var x in MembersOf(loser)) { rep[x] = survivor; into.Add(x); }
        members.Remove(loser);
        Merges++; Version++;
        Pending.Add(IdentityBinding.Forward(this, loser, survivor));
        OnMerge?.Invoke(loser, survivor);
    }

    /// <summary>The link of `a` and `b` ended: the ids reachable from `a` over present links. When `b` is not among them the class splits in two, and the part without the representative is named by its smallest id.</summary>
    void Part(int a, int b)
    {
        var r = Rep(a);
        var reach = new HashSet<int> { a }; var todo = new Stack<int>(); todo.Push(a);
        while (todo.Count > 0)
            foreach (var x in near.TryGetValue(todo.Pop(), out var l) ? l : [])
            {
                if (x == b) return;
                if (reach.Add(x)) todo.Push(x);
            }
        var all = members[r];
        var leave = reach.Contains(r) ? [.. all.Where(x => !reach.Contains(x))] : reach.ToList();
        var stay = all.Where(x => !leave.Contains(x)).ToList();
        var n = leave.MinBy(x => it.Get(x), StringComparer.Ordinal);
        Place(r, stay); Place(n, leave);
        Version++;
        foreach (var x in leave) Pending.Add(IdentityBinding.Forward(this, x, n));         // the part's ids forward to its representative, which forwards to itself: the forwarding to `r` ends
        var part = leave.ToHashSet();
        OnSplit?.Invoke(r, n, part.Contains);
    }

    /// <summary>`ids` are one class with representative `r`: a class of one is in no table.</summary>
    void Place(int r, List<int> ids)
    {
        if (ids.Count == 1) { rep.Remove(r); members.Remove(r); return; }
        members[r] = ids;
        foreach (var x in ids) rep[x] = r;
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
    /// <summary>Per component the type of a value that is not held as a text code (a number, an instant, a boolean), else null.</summary>
    readonly Ty?[] scalars;
    readonly long[] buf; readonly Column[] cols;
    /// <summary>When set, `Eval` also fills `Owns` with the own id of each row's key (`IdentityCatalog.Resolve` with `own`).</summary>
    public bool TrackOwn { get; set; }
    public Column? Owns { get; private set; }
    public VId(IdDomain dom, VExpr[] comps, Ty[] types, IdentityCatalog cat, Interner it, RuntimeFaults faults)
    {
        this.dom = dom; this.comps = comps; this.cat = cat; this.it = it; this.faults = faults;
        scalars = [.. types.Select(t => t.Tag is Tag.Integer or Tag.Decimal or Tag.Instant or Tag.Boolean ? t : null)];
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
                var cc = Canonical(cols[k], k, i);
                if (cc < 0) { faults.Overflow++; ok = false; break; }
                buf[k] = cc;
            }
            if (!ok) { Absent(o, i, n); continue; }
            if (TrackOwn) { o.L[i] = cat.Resolve(dom, buf, out var own); Owns!.L[i] = own; }
            else o.L[i] = cat.Resolve(dom, buf);
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
    /// <summary>The code of the canonical text of component `k` at a present row, -1 when it has none. A number, an instant or a boolean is its normal-form text (`Norm.Text`), interned old as every key text is.</summary>
    int Canonical(Column c, int k, int row)
    {
        if (scalars[k] is { } ty) return it.Intern(Norm.Text(Codec.ToValue(c, row, ty, it))!);
        var code = (int)c.L[row];
        if (keepsText[k] && !it.IsYoung(code)) return code;          // an old code is the one code of its text, so the text's exact canonical form is the code itself
        if (caches[k].TryGet(code, out var cc)) return cc;
        var text = it.Get(code);
        var s = IdentityCatalog.Canonicalize(dom.Canonicalizers[k], text);
        cc = s is null ? -1 : ReferenceEquals(s, text) && !it.IsYoung(code) ? code : it.Intern(s);       // a text the canonicalizer leaves as it is: an old code is the one code of its text
        if (!it.IsYoung(code)) caches[k].Set(code, cc);          // a young code may be freed and given to another text
        return cc;
    }
    public bool CanonicalComponents(Column[] cols, int row, long[] into)
    {
        for (var k = 0; k < cols.Length; k++)
        {
            if (cols[k].P is not null && !cols[k].P![row]) return false;
            var cc = Canonical(cols[k], k, row);
            if (cc < 0) return false;
            into[k] = cc;
        }
        return true;
    }
}
