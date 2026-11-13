using System.Buffers;
using System.Numerics;
using System.Text;

namespace Sattle.Core;

/// <summary>Append-only table of element ids stored as UTF-8 in one byte pool, with an open-addressing hash index. An id keeps its dense index for the life of the table (research 02 section 5.2, interning).</summary>
sealed class IdPool
{
    byte[] data = new byte[1024]; int pos;
    int[] off = new int[64], len = new int[64], hash = new int[64];
    int[] table = new int[128]; int mask = 127;
    public int Count { get; private set; }

    static int Hash(ReadOnlySpan<byte> b)
    {
        uint h = 2166136261;
        foreach (var x in b) { h ^= x; h *= 16777619; }
        h ^= h >> 15; h *= 0x2c1b3c6d; h ^= h >> 12;
        return (int)(h & 0x7fffffff);
    }

    static Span<byte> Utf8(string s, Span<byte> stack, out byte[]? rented)
    {
        rented = null;
        var max = Encoding.UTF8.GetMaxByteCount(s.Length);
        if (max <= stack.Length) return stack[..Encoding.UTF8.GetBytes(s, stack)];
        rented = ArrayPool<byte>.Shared.Rent(max);
        return rented.AsSpan(0, Encoding.UTF8.GetBytes(s, rented));
    }

    public int Find(string s)
    {
        Span<byte> stack = stackalloc byte[256];
        var b = Utf8(s, stack, out var rented);
        var r = Find(b);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        return r;
    }

    int Find(ReadOnlySpan<byte> b)
    {
        var h = Hash(b); var i = h & mask;
        while (true)
        {
            var t = table[i];
            if (t == 0) return -1;
            var ix = t - 1;
            if (hash[ix] == h && len[ix] == b.Length && data.AsSpan(off[ix], len[ix]).SequenceEqual(b)) return ix;
            i = (i + 1) & mask;
        }
    }

    public int GetOrAdd(string s)
    {
        Span<byte> stack = stackalloc byte[256];
        var b = Utf8(s, stack, out var rented);
        var f = Find(b);
        if (f < 0) f = Add(b);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        return f;
    }

    int Add(ReadOnlySpan<byte> b)
    {
        if (pos + b.Length > data.Length) Array.Resize(ref data, Math.Max(data.Length * 2, pos + b.Length));
        if (Count == off.Length) { var n = off.Length * 2; Array.Resize(ref off, n); Array.Resize(ref len, n); Array.Resize(ref hash, n); }
        b.CopyTo(data.AsSpan(pos));
        var ix = Count++;
        off[ix] = pos; len[ix] = b.Length; hash[ix] = Hash(b); pos += b.Length;
        if (Count * 2 > table.Length) { table = new int[table.Length * 2]; mask = table.Length - 1; for (var k = 0; k < Count; k++) Insert(k); }
        else Insert(ix);
        return ix;
    }

    void Insert(int ix)
    {
        var i = hash[ix] & mask;
        while (table[i] != 0) i = (i + 1) & mask;
        table[i] = ix + 1;
    }

    public string Get(int ix) => Encoding.UTF8.GetString(data, off[ix], len[ix]);

    public IdPool Clone()
    {
        var c = new IdPool { pos = pos, Count = Count, mask = mask };
        c.data = (byte[])data.Clone(); c.off = (int[])off.Clone(); c.len = (int[])len.Clone(); c.hash = (int[])hash.Clone(); c.table = (int[])table.Clone();
        return c;
    }
}

sealed class StrTable
{
    readonly Dictionary<string, int> map = new(StringComparer.Ordinal);
    List<string> list = [];
    public int Intern(string s) { if (!map.TryGetValue(s, out var i)) { i = list.Count; list.Add(s); map[s] = i; } return i; }
    public string Get(int i) => list[i];
    public bool TryFind(string s, out int i) => map.TryGetValue(s, out i);
    public StrTable Clone() { var c = new StrTable(); foreach (var s in list) c.Intern(s); return c; }
}

/// <summary>Compact binary form of property maps (sorted by key), so an element costs bytes instead of an object graph.</summary>
static class PropCodec
{
    static void Varint(List<byte> o, ulong v) { while (v >= 0x80) { o.Add((byte)(v | 0x80)); v >>= 7; } o.Add((byte)v); }
    static ulong ReadVarint(byte[] b, ref int p) { ulong v = 0; var s = 0; while (true) { var x = b[p++]; v |= (ulong)(x & 0x7f) << s; if (x < 0x80) return v; s += 7; } }
    static ulong Zig(long v) => (ulong)((v << 1) ^ (v >> 63));
    static long Unzig(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);
    static void Str(List<byte> o, string s) { var b = Encoding.UTF8.GetBytes(s); Varint(o, (ulong)b.Length); o.AddRange(b); }
    static string ReadStr(byte[] b, ref int p) { var n = (int)ReadVarint(b, ref p); var s = Encoding.UTF8.GetString(b, p, n); p += n; return s; }

    static void Val(List<byte> o, Value v)
    {
        switch (v)
        {
            case NullValue: o.Add(0); break;
            case TextValue t: o.Add(1); Str(o, t.S); break;
            case IntegerValue i when i.N >= long.MinValue && i.N <= long.MaxValue: o.Add(2); Varint(o, Zig((long)i.N)); break;
            case IntegerValue i: o.Add(3); Str(o, i.N.ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
            case DecimalValue d: o.Add(4); foreach (var x in decimal.GetBits(d.Q)) o.AddRange(BitConverter.GetBytes(x)); break;
            case BooleanValue b: o.Add(b.B ? (byte)6 : (byte)5); break;
            case InstantValue n: o.Add(7); Varint(o, Zig(n.Ns)); break;
            case EntityValue e: o.Add(8); Str(o, e.Id); break;
            case ListValue l: o.Add(9); Varint(o, (ulong)l.Items.Length); foreach (var x in l.Items) Val(o, x); break;
        }
    }

    static Value ReadVal(byte[] b, ref int p)
    {
        switch (b[p++])
        {
            case 0: return Value.Null;
            case 1: return Value.Text(ReadStr(b, ref p));
            case 2: return Value.Integer(Unzig(ReadVarint(b, ref p)));
            case 3: return Value.Integer(BigInteger.Parse(ReadStr(b, ref p), System.Globalization.CultureInfo.InvariantCulture));
            case 4: { var bits = new int[4]; for (var i = 0; i < 4; i++) { bits[i] = BitConverter.ToInt32(b, p); p += 4; } return Value.Decimal(new decimal(bits)); }
            case 5: return Value.Boolean(false);
            case 6: return Value.Boolean(true);
            case 7: return Value.Instant(Unzig(ReadVarint(b, ref p)));
            case 8: return Value.Entity(ReadStr(b, ref p));
            default: { var n = (int)ReadVarint(b, ref p); var items = new Value[n]; for (var i = 0; i < n; i++) items[i] = ReadVal(b, ref p); return Value.List(items); }
        }
    }

    public static byte[] Encode(Props props, StrTable keys)
    {
        var o = new List<byte>(32);
        Varint(o, (ulong)props.Count);
        foreach (var (k, v) in props.Items) { Varint(o, (ulong)keys.Intern(k)); Val(o, v); }
        return [.. o];
    }

    public static Props Decode(byte[] b, int p, StrTable keys)
    {
        var n = (int)ReadVarint(b, ref p);
        if (n == 0) return Props.Empty;
        var kv = new KeyValuePair<string, Value>[n];
        for (var i = 0; i < n; i++) { var k = keys.Get((int)ReadVarint(b, ref p)); kv[i] = KeyValuePair.Create(k, ReadVal(b, ref p)); }
        return Props.FromSorted(kv);
    }
}

/// <summary>A well-formed graph in the sense of Def. 3.3, in the hot layout of research 02 section 5.2: interned element handles, struct-of-arrays columns, props as bytes, intrusive adjacency lists, an open-addressing uniqueness index. The API is the string-id API of the reference model; elements are materialized on read.</summary>
public sealed class Graph
{
    IdPool ents = new(), rels = new();
    StrTable types = new(), propIds = new();
    // entity columns, indexed by entity handle (dangling endpoints have handles too, with kind 0)
    byte[] eKind = new byte[64]; int[] eType = new int[64], eProps = new int[64], eOut = new int[64], eIn = new int[64];
    // relationship columns, indexed by relationship handle
    byte[] rKind = new byte[64]; int[] rType = new int[64], rProps = new int[64], rSrc = new int[64], rTgt = new int[64], rOutNext = new int[64], rInNext = new int[64];
    byte[] pool = new byte[256]; int poolLen = 1;   // props blocks; offset 0 means no props
    int[] uq = new int[64]; int uqMask = 63, uqLive, uqUsed;
    int entityCount, relCount;
    OntologyState? ontology;

    public int EntityCount => entityCount;
    public int RelCount => relCount;
    public int Count => entityCount + relCount + (ontology is null ? 0 : 1);
    public OntologyState? Ontology => ontology;

    // ---- storage helpers ----
    void GrowEnt(int ix) { if (ix < eKind.Length) return; var n = Math.Max(eKind.Length * 2, ix + 1); Array.Resize(ref eKind, n); Array.Resize(ref eType, n); Array.Resize(ref eProps, n); Array.Resize(ref eOut, n); Array.Resize(ref eIn, n); }
    void GrowRel(int ix) { if (ix < rKind.Length) return; var n = Math.Max(rKind.Length * 2, ix + 1); Array.Resize(ref rKind, n); Array.Resize(ref rType, n); Array.Resize(ref rProps, n); Array.Resize(ref rSrc, n); Array.Resize(ref rTgt, n); Array.Resize(ref rOutNext, n); Array.Resize(ref rInNext, n); }

    int StoreProps(Props p, int old)
    {
        if (p.Count == 0) return 0;
        var enc = PropCodec.Encode(p, propIds);
        if (old > 0)
        {
            var cap = BitConverter.ToInt32(pool, old - 4);
            if (enc.Length <= cap) { enc.CopyTo(pool, old); return old; }
        }
        if (poolLen + 4 + enc.Length > pool.Length) Array.Resize(ref pool, Math.Max(pool.Length * 2, poolLen + 4 + enc.Length));
        BitConverter.TryWriteBytes(pool.AsSpan(poolLen), enc.Length);
        enc.CopyTo(pool, poolLen + 4);
        var at = poolLen + 4; poolLen += 4 + enc.Length;
        return at;
    }
    Props LoadProps(int at) => at == 0 ? Props.Empty : PropCodec.Decode(pool, at, propIds);

    Entity MakeEntity(int ix) => new(ents.Get(ix), types.Get(eType[ix]), LoadProps(eProps[ix]));
    Rel MakeRel(int ix) => new(rels.Get(ix), ents.Get(rSrc[ix]), ents.Get(rTgt[ix]), types.Get(rType[ix]), LoadProps(rProps[ix]));

    // ---- reads ----
    public Entity? Entity(string id) { var ix = ents.Find(id); return ix >= 0 && eKind[ix] == 1 ? MakeEntity(ix) : null; }
    public Rel? Rel(string id) { var ix = rels.Find(id); return ix >= 0 && rKind[ix] == 1 ? MakeRel(ix) : null; }
    public bool HasEntity(string id) { var ix = ents.Find(id); return ix >= 0 && eKind[ix] == 1; }
    public IEnumerable<Entity> Entities { get { for (var i = 0; i < ents.Count; i++) if (eKind[i] == 1) yield return MakeEntity(i); } }
    public IEnumerable<Rel> Rels { get { for (var i = 0; i < rels.Count; i++) if (rKind[i] == 1) yield return MakeRel(i); } }

    public Rel? FindRel(string source, string target, string type)
    {
        var s = ents.Find(source); var t = ents.Find(target);
        if (s < 0 || t < 0 || !TryType(type, out var ty)) return null;
        var r = Lookup(s, t, ty);
        return r < 0 ? null : MakeRel(r);
    }
    bool TryType(string type, out int ty) => types.TryFind(type, out ty);

    public IEnumerable<string> OutRels(string entityId)
    {
        var ix = ents.Find(entityId);
        if (ix < 0) yield break;
        for (var r = eOut[ix]; r != 0; r = rOutNext[r - 1]) yield return rels.Get(r - 1);
    }
    public IEnumerable<string> InRels(string entityId)
    {
        var ix = ents.Find(entityId);
        if (ix < 0) yield break;
        for (var r = eIn[ix]; r != 0; r = rInNext[r - 1]) yield return rels.Get(r - 1);
    }
    /// <summary>Out-going relationships as materialized elements, without the id round trip.</summary>
    public IEnumerable<Rel> OutEdges(string entityId)
    {
        var ix = ents.Find(entityId);
        if (ix < 0) yield break;
        for (var r = eOut[ix]; r != 0; r = rOutNext[r - 1]) yield return MakeRel(r - 1);
    }
    public IEnumerable<Rel> InEdges(string entityId)
    {
        var ix = ents.Find(entityId);
        if (ix < 0) yield break;
        for (var r = eIn[ix]; r != 0; r = rInNext[r - 1]) yield return MakeRel(r - 1);
    }
    public int OutDegree(string entityId) { var ix = ents.Find(entityId); var n = 0; if (ix >= 0) for (var r = eOut[ix]; r != 0; r = rOutNext[r - 1]) n++; return n; }
    public int InDegree(string entityId) { var ix = ents.Find(entityId); var n = 0; if (ix >= 0) for (var r = eIn[ix]; r != 0; r = rInNext[r - 1]) n++; return n; }

    public Element? Get(ElementKey k) => k.Kind switch { 'e' => Entity(k.Id), 'r' => Rel(k.Id), _ => ontology };

    // ---- uniqueness index ----
    static int Mix(int s, int t, int ty) { unchecked { uint h = (uint)s * 2654435761u; h ^= (uint)t * 2246822519u + 0x9e3779b9 + (h << 6); h ^= (uint)ty * 3266489917u; h ^= h >> 15; return (int)(h & 0x7fffffff); } }

    int Lookup(int s, int t, int ty)
    {
        var i = Mix(s, t, ty) & uqMask;
        while (true)
        {
            var v = uq[i];
            if (v == 0) return -1;
            if (v > 0) { var r = v - 1; if (rSrc[r] == s && rTgt[r] == t && rType[r] == ty) return r; }
            i = (i + 1) & uqMask;
        }
    }
    void UqInsert(int r)
    {
        if ((uqUsed + 1) * 2 > uq.Length) { UqRebuild(); return; } // the relationship is already marked alive, so the rebuild indexes it
        var i = Mix(rSrc[r], rTgt[r], rType[r]) & uqMask;
        while (uq[i] > 0) i = (i + 1) & uqMask;
        if (uq[i] == 0) uqUsed++;
        uq[i] = r + 1; uqLive++;
    }
    void UqRebuild()
    {
        var size = Math.Max(64, (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(uqLive * 4, 16)));
        uq = new int[size]; uqMask = size - 1; uqUsed = 0;
        var live = uqLive; uqLive = 0;
        for (var r = 0; r < rels.Count; r++)
            if (rKind[r] == 1) { var i = Mix(rSrc[r], rTgt[r], rType[r]) & uqMask; while (uq[i] != 0) i = (i + 1) & uqMask; uq[i] = r + 1; uqUsed++; uqLive++; }
        _ = live;
    }
    void UqRemove(int r)
    {
        var i = Mix(rSrc[r], rTgt[r], rType[r]) & uqMask;
        while (true)
        {
            if (uq[i] == r + 1) { uq[i] = -1; uqLive--; return; }
            if (uq[i] == 0) return;
            i = (i + 1) & uqMask;
        }
    }

    // ---- mutation ----
    public Graph Clone()
    {
        var g = new Graph
        {
            ents = ents.Clone(), rels = rels.Clone(), types = types.Clone(), propIds = propIds.Clone(),
            eKind = (byte[])eKind.Clone(), eType = (int[])eType.Clone(), eProps = (int[])eProps.Clone(), eOut = (int[])eOut.Clone(), eIn = (int[])eIn.Clone(),
            rKind = (byte[])rKind.Clone(), rType = (int[])rType.Clone(), rProps = (int[])rProps.Clone(), rSrc = (int[])rSrc.Clone(), rTgt = (int[])rTgt.Clone(), rOutNext = (int[])rOutNext.Clone(), rInNext = (int[])rInNext.Clone(),
            pool = (byte[])pool.Clone(), poolLen = poolLen, uq = (int[])uq.Clone(), uqMask = uqMask, uqLive = uqLive, uqUsed = uqUsed, entityCount = entityCount, relCount = relCount, ontology = ontology,
        };
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
            if (FindRel(a.Source, a.Target, a.Type) is { } other && other.Id != a.Id && (gone is null || !gone.Contains(other.Id)))
                return $"relationship {a.Id}: duplicates {other.Id} on (source, target, type) (U)";
            added ??= [];
            var key = (a.Source, a.Target, a.Type);
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
            case Entity en:
                {
                    var ix = ents.GetOrAdd(en.Id); GrowEnt(ix);
                    if (eKind[ix] == 0) entityCount++;
                    eKind[ix] = 1; eType[ix] = types.Intern(en.Type); eProps[ix] = StoreProps(en.Props, eProps[ix]);
                    break;
                }
            case Rel r:
                {
                    var ix = rels.GetOrAdd(r.Id); GrowRel(ix);
                    var s = ents.GetOrAdd(r.Source); var t = ents.GetOrAdd(r.Target); GrowEnt(Math.Max(s, t));
                    rKind[ix] = 1; rSrc[ix] = s; rTgt[ix] = t; rType[ix] = types.Intern(r.Type); rProps[ix] = StoreProps(r.Props, rProps[ix]);
                    rOutNext[ix] = eOut[s]; eOut[s] = ix + 1;
                    rInNext[ix] = eIn[t]; eIn[t] = ix + 1;
                    UqInsert(ix); relCount++;
                    break;
                }
            case OntologyState o: ontology = o; break;
        }
    }

    void Remove(Element e)
    {
        switch (e)
        {
            case Entity en:
                {
                    var ix = ents.Find(en.Id);
                    if (ix >= 0 && eKind[ix] == 1) { eKind[ix] = 0; eProps[ix] = 0; entityCount--; }
                    break;
                }
            case Rel r:
                {
                    var ix = rels.Find(r.Id);
                    if (ix < 0 || rKind[ix] != 1) break;
                    UqRemove(ix);
                    Unlink(ref eOut[rSrc[ix]], ix, rOutNext);
                    Unlink(ref eIn[rTgt[ix]], ix, rInNext);
                    rKind[ix] = 0; rProps[ix] = 0; relCount--;
                    break;
                }
            case OntologyState: ontology = null; break;
        }
    }

    static void Unlink(ref int head, int ix, int[] next)
    {
        if (head == ix + 1) { head = next[ix]; return; }
        for (var r = head; r != 0; r = next[r - 1])
            if (next[r - 1] == ix + 1) { next[r - 1] = next[ix]; return; }
    }

    // ---- whole-graph views ----
    public IEnumerable<Element> Elements()
    {
        if (ontology is not null) yield return ontology;
        foreach (var e in Entities) yield return e;
        foreach (var r in Rels) yield return r;
    }

    /// <summary>Edits that turn a into b (guard = a's state). Empty when equal.</summary>
    public static List<Edit> Diff(Graph a, Graph b)
    {
        var edits = new List<Edit>();
        foreach (var e in a.Elements()) { var n = b.Get(e.Key); if (!Equals(e, n)) edits.Add(new Edit(e.Key, e, n)); }
        foreach (var e in b.Elements()) if (a.Get(e.Key) is null) edits.Add(new Edit(e.Key, null, e));
        return edits;
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
    public bool SameAs(Graph o) => Count == o.Count && Equals(ontology, o.ontology) && Entities.All(e => e.Equals(o.Entity(e.Id))) && Rels.All(r => r.Equals(o.Rel(r.Id)));

    /// <summary>WF3: no two relationships share (source, target, type), and the index agrees with the relationship set.</summary>
    public bool UniquenessHolds()
    {
        var seen = new HashSet<(string, string, string)>();
        foreach (var r in Rels) if (!seen.Add((r.Source, r.Target, r.Type))) return false;
        return seen.Count == uqLive && uqLive == relCount;
    }

    /// <summary>Approximate managed bytes held by the columns and pools.</summary>
    public long ApproximateBytes => (long)eKind.Length * 17 + (long)rKind.Length * 29 + pool.Length + uq.Length * 4L;
}
