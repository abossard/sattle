using System.Buffers;
using System.Numerics;
using System.Text;

namespace Sattle.Core;

/// <summary>Where a graph keeps the texts of its property values: a code per text and the text of a code, stable while the graph holds it. The engine's interner for the graphs of an engine; a table of the graph's own otherwise.</summary>
public interface ITextTable
{
    int Intern(string s);
    /// <summary>The code of a text the table has; adds nothing.</summary>
    bool TryFind(string s, out int code);
    string Get(int code);
}

/// <summary>The elements of one kind of a graph by dense handle: per handle the code of its id in the graph's text table, and an open-addressing index from code to handle at a load of at most 1/2. A handle is kept for the life of the graph.</summary>
sealed class HandleTable(ITextTable texts)
{
    int[] codes = new int[64], index = new int[128]; int mask = 127;
    public int Count { get; private set; }

    static int Slot(int code) { var h = (uint)code * 0x9E3779B1u; return (int)(h ^ h >> 16); }
    int Find(int code)
    {
        for (var i = Slot(code) & mask; ; i = (i + 1) & mask)
        {
            var t = index[i];
            if (t == 0) return -1;
            if (codes[t - 1] == code) return t - 1;
        }
    }
    void Place(int h) { var i = Slot(codes[h]) & mask; while (index[i] != 0) i = (i + 1) & mask; index[i] = h + 1; }

    /// <summary>The handle of an id's code, or -1.</summary>
    public int Handle(int code) => Find(code);
    public int Code(int h) => codes[h];
    /// <summary>The handle of an id, or -1 when the graph never had it.</summary>
    public int Find(string id) => texts.TryFind(id, out var code) ? Find(code) : -1;
    public int GetOrAdd(string id)
    {
        var code = texts.Intern(id);
        var h = Find(code);
        if (h >= 0) return h;
        h = Count++;
        if (h == codes.Length) Array.Resize(ref codes, h * 2);
        codes[h] = code;
        if (Count * 2 > index.Length) { index = new int[index.Length * 2]; mask = index.Length - 1; for (var k = 0; k < Count; k++) Place(k); }
        else Place(h);
        return h;
    }
    public string Get(int h) => texts.Get(codes[h]);
    public HandleTable Clone() => new(texts) { codes = (int[])codes.Clone(), index = (int[])index.Clone(), mask = mask, Count = Count };
}

/// <summary>Append-only table of texts stored as UTF-8 in one byte pool, with an open-addressing hash index: the text table of a graph that is not an engine's. A text keeps its code for the life of the table (research 02 section 5.2, interning).</summary>
sealed class IdPool : ITextTable
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
    public int Intern(string s) => GetOrAdd(s);
    public bool TryFind(string s, out int code) { code = Find(s); return code >= 0; }

    public IdPool Clone()
    {
        var c = new IdPool { pos = pos, Count = Count, mask = mask };
        c.data = (byte[])data.Clone(); c.off = (int[])off.Clone(); c.len = (int[])len.Clone(); c.hash = (int[])hash.Clone(); c.table = (int[])table.Clone();
        return c;
    }
}

/// <summary>
/// Writes a props block in a graph's encoding, from codes of the graph's text table: the count, then per property its key and its value, the
/// properties in the order `Props` keeps them (ordinal by key text). A text, an entity id and a key are codes; an integer or instant a zigzag
/// varint; a decimal whose mantissa fits 64 bits its scale and sign and a varint, else its 16 bytes. What `Graph.Holds` compares with.
/// </summary>
public sealed class PropBlockWriter
{
    readonly List<byte> o = new(64);
    public ReadOnlySpan<byte> Block => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(o);
    public void Clear() => o.Clear();
    public void Count(int n) => Varint((ulong)n);
    public void Key(int code) => Varint((ulong)code);
    public void Null() => o.Add(0);
    public void Text(int code) { o.Add(1); Varint((ulong)code); }
    public void Integer(long v) { o.Add(2); Varint(Zig(v)); }
    internal void BigInteger(BigInteger v) { o.Add(3); var b = Encoding.UTF8.GetBytes(v.ToString(System.Globalization.CultureInfo.InvariantCulture)); Varint((ulong)b.Length); o.AddRange(b); }
    public void Decimal(decimal d)
    {
        var bits = decimal.GetBits(d);
        if (bits[2] == 0) { o.Add(10); o.Add((byte)((bits[3] >> 16) & 0x7f | (bits[3] < 0 ? 0x80 : 0))); Varint((ulong)(uint)bits[1] << 32 | (uint)bits[0]); }
        else { o.Add(4); foreach (var x in bits) o.AddRange(BitConverter.GetBytes(x)); }
    }
    public void Boolean(bool b) => o.Add(b ? (byte)6 : (byte)5);
    public void Instant(long ns) { o.Add(7); Varint(Zig(ns)); }
    public void Entity(int code) { o.Add(8); Varint((ulong)code); }
    /// <summary>A list of `n` items; the items follow.</summary>
    public void List(int n) { o.Add(9); Varint((ulong)n); }
    void Varint(ulong v) { while (v >= 0x80) { o.Add((byte)(v | 0x80)); v >>= 7; } o.Add((byte)v); }
    static ulong Zig(long v) => (ulong)((v << 1) ^ (v >> 63));
}

/// <summary>Compact binary form of property maps (sorted by key), so an element costs bytes instead of an object graph: written by `PropBlockWriter`, read here.</summary>
static class PropCodec
{
    static ulong ReadVarint(byte[] b, ref int p) { ulong v = 0; var s = 0; while (true) { var x = b[p++]; v |= (ulong)(x & 0x7f) << s; if (x < 0x80) return v; s += 7; } }
    static long Unzig(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);
    static string ReadStr(byte[] b, ref int p) { var n = (int)ReadVarint(b, ref p); var s = Encoding.UTF8.GetString(b, p, n); p += n; return s; }

    static void Val(PropBlockWriter w, Value v, ITextTable texts)
    {
        switch (v)
        {
            case NullValue: w.Null(); break;
            case TextValue t: w.Text(texts.Intern(t.S)); break;
            case IntegerValue i when i.N >= long.MinValue && i.N <= long.MaxValue: w.Integer((long)i.N); break;
            case IntegerValue i: w.BigInteger(i.N); break;
            case DecimalValue d: w.Decimal(d.Q); break;
            case BooleanValue b: w.Boolean(b.B); break;
            case InstantValue n: w.Instant(n.Ns); break;
            case EntityValue e: w.Entity(texts.Intern(e.Id)); break;
            case ListValue l: w.List(l.Items.Length); foreach (var x in l.Items) Val(w, x, texts); break;
        }
    }

    static Value ReadVal(byte[] b, ref int p, ITextTable texts)
    {
        switch (b[p++])
        {
            case 0: return Value.Null;
            case 1: return Value.Text(texts.Get((int)ReadVarint(b, ref p)));
            case 2: return Value.Integer(Unzig(ReadVarint(b, ref p)));
            case 3: return Value.Integer(BigInteger.Parse(ReadStr(b, ref p), System.Globalization.CultureInfo.InvariantCulture));
            case 4: { var bits = new int[4]; for (var i = 0; i < 4; i++) { bits[i] = BitConverter.ToInt32(b, p); p += 4; } return Value.Decimal(new decimal(bits)); }
            case 5: return Value.Boolean(false);
            case 6: return Value.Boolean(true);
            case 7: return Value.Instant(Unzig(ReadVarint(b, ref p)));
            case 8: return Value.Entity(texts.Get((int)ReadVarint(b, ref p)));
            case 10: { var f = b[p++]; var m = ReadVarint(b, ref p); return Value.Decimal(new decimal((int)(uint)m, (int)(uint)(m >> 32), 0, (f & 0x80) != 0, (byte)(f & 0x7f))); }
            default: { var n = (int)ReadVarint(b, ref p); var items = new Value[n]; for (var i = 0; i < n; i++) items[i] = ReadVal(b, ref p, texts); return Value.List(items); }
        }
    }

    /// <summary>Writes the block of `props` into `w` (cleared first).</summary>
    public static void Encode(PropBlockWriter w, Props props, ITextTable texts)
    {
        w.Clear(); w.Count(props.Count);
        foreach (var (k, v) in props.Items) { w.Key(texts.Intern(k)); Val(w, v, texts); }
    }

    public static Props Decode(byte[] b, int p, ITextTable texts)
    {
        var n = (int)ReadVarint(b, ref p);
        if (n == 0) return Props.Empty;
        var kv = new KeyValuePair<string, Value>[n];
        for (var i = 0; i < n; i++) { var k = texts.Get((int)ReadVarint(b, ref p)); kv[i] = KeyValuePair.Create(k, ReadVal(b, ref p, texts)); }
        return Props.FromSorted(kv);
    }
}

/// <summary>A well-formed graph in the sense of Def. 3.3, in the hot layout of research 02 section 5.2: interned element handles, struct-of-arrays columns, props as bytes, intrusive adjacency lists, an open-addressing uniqueness index. The API is the string-id API of the reference model; elements are materialized on read.</summary>
public sealed class Graph
{
    /// <summary>A graph whose texts are in a table of its own.</summary>
    public Graph() : this(null) { }
    /// <summary>A graph whose property texts are codes of `texts` (the engine's interner); a table of its own when null.</summary>
    public Graph(ITextTable? texts)
    {
        this.texts = texts ?? new IdPool();
        ents = new(this.texts); rels = new(this.texts);
    }

    readonly ITextTable texts;
    HandleTable ents, rels;   // element ids are codes of `texts`
    readonly PropBlockWriter enc = new();   // the block being stored; types and property keys are codes of `texts` too
    // entity columns, indexed by entity handle (dangling endpoints have handles too, with kind 0)
    byte[] eKind = new byte[64]; int[] eType = new int[64], eProps = new int[64], eOut = new int[64], eIn = new int[64];
    // relationship columns, indexed by relationship handle
    byte[] rKind = new byte[64]; int[] rType = new int[64], rProps = new int[64], rSrc = new int[64], rTgt = new int[64], rOutNext = new int[64], rInNext = new int[64];
    // props blocks, each after its 4-byte capacity; offset 0 means no props. A removed element keeps its block's offset: its next put reuses the block when the
    // new props fit. `dead`: bytes of blocks no live element has; when more bytes are dead than live, the live blocks are copied together
    byte[] pool = new byte[256]; int poolLen = 1, dead;
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

    int Block(int at) => 4 + BitConverter.ToInt32(pool, at - 4);

    /// <summary>Stores the props of an element that is not live; `old` is its former block (dead), reused when they fit.</summary>
    int StoreProps(Props p, int old)
    {
        if (p.Count == 0) return 0;
        PropCodec.Encode(enc, p, texts);
        var block = enc.Block;
        if (old > 0 && block.Length <= Block(old) - 4) { dead -= Block(old); block.CopyTo(pool.AsSpan(old)); return old; }
        if (poolLen + 4 + block.Length > pool.Length) Array.Resize(ref pool, Math.Max(pool.Length * 2, poolLen + 4 + block.Length));
        BitConverter.TryWriteBytes(pool.AsSpan(poolLen), block.Length);
        block.CopyTo(pool.AsSpan(poolLen + 4));
        var at = poolLen + 4; poolLen += 4 + block.Length;
        return at;
    }
    Props LoadProps(int at) => at == 0 ? Props.Empty : PropCodec.Decode(pool, at, texts);

    /// <summary>Copies the blocks of the live elements together; a removed element forgets its block.</summary>
    void CompactProps()
    {
        var next = new byte[Math.Max(256, (int)BitOperations.RoundUpToPowerOf2((uint)(poolLen - dead)))]; var len = 1;
        int Move(int at)
        {
            if (at == 0) return 0;
            var n = Block(at);
            Array.Copy(pool, at - 4, next, len, n); len += n;
            return len - n + 4;
        }
        for (var i = 0; i < ents.Count; i++) eProps[i] = eKind[i] == 1 ? Move(eProps[i]) : 0;
        for (var i = 0; i < rels.Count; i++) rProps[i] = rKind[i] == 1 ? Move(rProps[i]) : 0;
        pool = next; poolLen = len; dead = 0;
    }

    Entity MakeEntity(int ix) => new(ents.Get(ix), texts.Get(eType[ix]), LoadProps(eProps[ix]));
    Rel MakeRel(int ix) => new(rels.Get(ix), ents.Get(rSrc[ix]), ents.Get(rTgt[ix]), texts.Get(rType[ix]), LoadProps(rProps[ix]));

    /// <summary>
    /// Whether the graph has exactly this element, given in codes of its text table: an entity (`source` and `target` -1) or a relationship of
    /// that type and endpoints, with these props (a block from `PropBlockWriter`). True means equal. False means it differs or the codes cannot
    /// tell (a decimal of another scale, say): the caller then compares the elements themselves. Makes no string and no element.
    /// </summary>
    public bool Holds(int id, int type, int source, int target, ReadOnlySpan<byte> props)
    {
        if (source < 0)
        {
            var e = ents.Handle(id);
            return e >= 0 && eKind[e] == 1 && eType[e] == type && SameProps(eProps[e], props);
        }
        var r = rels.Handle(id);
        return r >= 0 && rKind[r] == 1 && rType[r] == type && ents.Code(rSrc[r]) == source && ents.Code(rTgt[r]) == target && SameProps(rProps[r], props);
    }
    // a block is self-delimiting, so a stored block (perhaps in a larger reused one) equals `props` when its first bytes do
    bool SameProps(int at, ReadOnlySpan<byte> props) => at == 0 ? props.SequenceEqual((ReadOnlySpan<byte>)[0]) : props.Length <= Block(at) - 4 && props.SequenceEqual(pool.AsSpan(at, props.Length));

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
    bool TryType(string type, out int ty) => texts.TryFind(type, out ty);

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
    /// <summary>The relationships at an entity, out-going or in-coming, as their type and the entity at the other end: what a walk reads, without making the relationships.</summary>
    public IEnumerable<(string Type, string Other)> Neighbors(string entityId, bool outgoing)
    {
        var ix = ents.Find(entityId);
        if (ix < 0) yield break;
        if (outgoing) for (var r = eOut[ix]; r != 0; r = rOutNext[r - 1]) yield return (texts.Get(rType[r - 1]), ents.Get(rTgt[r - 1]));
        else for (var r = eIn[ix]; r != 0; r = rInNext[r - 1]) yield return (texts.Get(rType[r - 1]), ents.Get(rSrc[r - 1]));
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
        var g = new Graph(texts)
        {
            ents = ents.Clone(), rels = rels.Clone(),
            eKind = (byte[])eKind.Clone(), eType = (int[])eType.Clone(), eProps = (int[])eProps.Clone(), eOut = (int[])eOut.Clone(), eIn = (int[])eIn.Clone(),
            rKind = (byte[])rKind.Clone(), rType = (int[])rType.Clone(), rProps = (int[])rProps.Clone(), rSrc = (int[])rSrc.Clone(), rTgt = (int[])rTgt.Clone(), rOutNext = (int[])rOutNext.Clone(), rInNext = (int[])rInNext.Clone(),
            pool = (byte[])pool.Clone(), poolLen = poolLen, dead = dead, uq = (int[])uq.Clone(), uqMask = uqMask, uqLive = uqLive, uqUsed = uqUsed, entityCount = entityCount, relCount = relCount, ontology = ontology,
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
        if (dead > 65_536 && dead > poolLen - dead) CompactProps();
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
                    else if (eProps[ix] > 0) dead += Block(eProps[ix]);           // a put over a live entity: its block is free to reuse
                    eKind[ix] = 1; eType[ix] = texts.Intern(en.Type); eProps[ix] = StoreProps(en.Props, eProps[ix]);
                    break;
                }
            case Rel r:
                {
                    var ix = rels.GetOrAdd(r.Id); GrowRel(ix);
                    var s = ents.GetOrAdd(r.Source); var t = ents.GetOrAdd(r.Target); GrowEnt(Math.Max(s, t));
                    rKind[ix] = 1; rSrc[ix] = s; rTgt[ix] = t; rType[ix] = texts.Intern(r.Type); rProps[ix] = StoreProps(r.Props, rProps[ix]);
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
                    if (ix >= 0 && eKind[ix] == 1) { eKind[ix] = 0; if (eProps[ix] > 0) dead += Block(eProps[ix]); entityCount--; }
                    break;
                }
            case Rel r:
                {
                    var ix = rels.Find(r.Id);
                    if (ix < 0 || rKind[ix] != 1) break;
                    UqRemove(ix);
                    Unlink(ref eOut[rSrc[ix]], ix, rOutNext);
                    Unlink(ref eIn[rTgt[ix]], ix, rInNext);
                    rKind[ix] = 0; if (rProps[ix] > 0) dead += Block(rProps[ix]); relCount--;
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
