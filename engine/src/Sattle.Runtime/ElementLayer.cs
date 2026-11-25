using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

/// <summary>What every fragment of one emit shares: its sink, entity or relationship, create or update, type, and its graph-data properties in the canonical order (by name, ordinal), each as its name's number, its column among the emit's evaluated columns and its kind. Fixed when the operator is built, so a row is encoded from its columns with no lookup, sort or object.</summary>
public sealed class FragmentShape
{
    internal readonly int Sink, Type; internal readonly bool IsRel, Update;
    internal readonly int[] Names, Cols; internal readonly Tag[] Tags;
    public FragmentShape(Interner it, int sink, bool isRel, bool update, string type, IEnumerable<(string Name, int Col, Tag Tag)> props)
    {
        Sink = sink; IsRel = isRel; Update = update; Type = it.Intern(type);
        var ps = props.ToArray();
        Array.Sort(ps, static (x, y) => string.CompareOrdinal(x.Name, y.Name));            // the order of `Props.Of`
        Names = [.. ps.Select(p => it.Intern(p.Name))]; Cols = [.. ps.Select(p => p.Col)]; Tags = [.. ps.Select(p => p.Tag)];
    }
}

/// <summary>Which elements the rows of a projection say exist, with which type, endpoints and properties. Per (element, fragment) it keeps the summed weight of the rows that say it; `Flush` gives the edits that bring the graph to what the touched elements resolve to (Def. 6.4). Nothing outside reads its tables.</summary>
sealed class ElementLayer(Interner it, ConflictSet conflicts)
{
    readonly Dictionary<int, Func<string, string, string>> relSinks = [];
    /// <summary>The relationship id function of a sink (the id hashes its endpoints), needed to re-key relationships when an identity merges.</summary>
    public void RegisterRelSink(int sink, Func<string, string, string> idOf) => relSinks[sink] = idOf;

    /// <summary>Two identities merged: what was emitted under the loser moves to the survivor (an entity's rows as they are; a relationship with the loser as an endpoint gets the id of its new endpoints). `g` is the graph as the last flush left it. Gives each element whose rows moved, where they went, and the own ids its rows had in it (`Implied`, packed as a event row's own).</summary>
    public List<(int From, int To, long Implied)> Move(int loser, int survivor, Graph g)
    {
        var moved = new List<(int From, int To, long Implied)>();
        foreach (var (from, to, isRel, s, t, _, _) in Relocate(loser, g, (elem, sink, s, t, a, b) => elem == loser ? (survivor, -1, -1) : (Moved(s), Moved(t)) is var (ns, nt) ? (RelId(sink, ns, nt), ns, nt) : null))
            moved.Add((from, to, isRel ? Pack(s, t) : from));
        return moved;
        int Moved(int end) => end == loser ? survivor : end;
    }

    /// <summary>A class split: the rows written under own ids of the part that leaves (`inPart`) move from `r` to `n`, the part's representative; a relationship whose endpoint `r` such a row names gets the id of its new endpoints. Rows under ids that stay, and rows whose own id is the element (no link touched them), stay. Gives each element whose rows moved, where they went, and which own ids of a row (packed as a event row's own) went there.</summary>
    public List<(int From, int To, Func<long, bool> Which)> Split(int r, int n, Func<int, bool> inPart, Graph g)
    {
        var moved = new List<(int From, int To, Func<long, bool> Which)>();
        (int S, int T) Ends(int s, int t, int a, int b) => (s == r && inPart(a) ? n : s, t == r && inPart(b) ? n : t);
        (int Elem, int S, int T)? Dest(int elem, int sink, int s, int t, int a, int b)
        {
            if (elem == r) return inPart(a) ? (n, -1, -1) : null;
            var (ns, nt) = Ends(s, t, a, b);
            return (ns, nt) == (s, t) ? null : (RelId(sink, ns, nt), ns, nt);
        }
        foreach (var (from, to, isRel, s, t, ns, nt) in Relocate(r, g, Dest))
            moved.Add((from, to, isRel ? o => Ends(s, t, (int)(o >> 32), (int)o) == (ns, nt) : o => inPart((int)o)));
        return moved;
    }

    /// <summary>The own ids of a row as one number: an entity's key's own id, or a relationship's source and target own ids.</summary>
    static long Pack(int s, int t) => ((long)s << 32) | (uint)t;
    int RelId(int sink, int s, int t) => it.Intern(relSinks[sink](it.Get(s), it.Get(t)));

    /// <summary>Moves the rows of `at` and of the relationships at it: `dest(element, source, target, a, b)` gives, for a row with own ids (a, b), the element and endpoints it goes to, or null when it stays. A row with no entry in `owned` has the own ids the element implies: an entity's own id, or a relationship's endpoints. Gives each (from, to) with whether it is a relationship.</summary>
    List<(int From, int To, bool IsRel, int S, int T, int NS, int NT)> Relocate(int at, Graph g, Func<int, int, int, int, int, int, (int Elem, int S, int T)?> dest)
    {
        var moved = new List<(int From, int To, bool IsRel, int S, int T, int NS, int NT)>();
        var lid = it.Get(at);
        Materialize(g, at);
        if (known.Has(at) && !rels.Has(at) && (ByElement.First([at]) >= 0 || ByOwner.First([at]) >= 0 || dirty.Has(at))) Relocate(at, false, dest, moved);
        // the relationships at `at`, by their numbers: the order they were first named in, whatever rows they have now. Those the graph holds at `at` are in its adjacency, the others in `ends`
        var near = ByEnd.Rows([at]).Select(r => (int)ends.Get(r, 1)).ToList();
        foreach (var id in g.OutRels(lid).Concat(g.InRels(lid))) if (it.TryFind(id, out var c)) near.Add(c);
        foreach (var elem in near.Distinct().Order().ToList())
        {
            Materialize(g, elem);
            if (!rels.Has(elem) || ByElement.First([elem]) < 0 && ByOwner.First([elem]) < 0) continue;
            Relocate(elem, true, dest, moved);
        }
        return moved;
    }

    void Relocate(int elem, bool isRel, Func<int, int, int, int, int, int, (int Elem, int S, int T)?> dest, List<(int From, int To, bool IsRel, int S, int T, int NS, int NT)> moved)
    {
        // per fragment: its summed weight, and the rows of it under own ids the element does not imply; the rest of the weight is under the implied ids
        var rows = new Dictionary<int, (long Total, List<(int A, int B, long W)> Owned)>();
        foreach (var (code, w) in RowsOf(elem)) rows[code] = (w, []);
        for (var r = ByOwner.First([elem]); r >= 0; r = ByOwner.Next(r))
        {
            var code = (int)owned.Get(r, 1);
            if (!rows.TryGetValue(code, out var x)) rows[code] = x = (0, []);
            x.Owned.Add(((int)owned.Get(r, 2), (int)owned.Get(r, 3), owned.Weight(r)));
        }
        var any = false;
        foreach (var (code, (total, own)) in rows.OrderBy(x => x.Key).ToList())
        {
            var fragment = values.Bytes(code).ToArray(); var f = Decode(code);
            var (s, t) = (Code(f.Source), Code(f.Target));
            var implied = total - own.Sum(x => x.W);
            foreach (var (a, b, w, explicitly) in own.Select(x => (x.A, x.B, x.W, true)).Append(isRel ? (s, t, implied, false) : (elem, -1, implied, false)))
            {
                if (w == 0 || dest(elem, f.Sink, s, t, a, b) is not { } d) continue;
                if (explicitly) Own(elem, fragment, a, b, -w);
                AddFragment(elem, fragment, -w, s, t);
                var into = isRel ? CanonicalFragment(f.Sink, f.Update, f.Type, d.S, d.T, f.Props).ToArray() : fragment;
                if (known.Add(d.Elem) && isRel) rels.Add(d.Elem);
                AddFragment(d.Elem, into, w, d.S, d.T);
                if (isRel ? (a, b) != (d.S, d.T) : a != d.Elem) Own(d.Elem, into, a, b, w);
                Touch(d.Elem);
                if (!moved.Contains((elem, d.Elem, isRel, s, t, d.S, d.T))) moved.Add((elem, d.Elem, isRel, s, t, d.S, d.T));
                any = true;
            }
        }
        if (!isRel && !any && dest(elem, -1, -1, -1, elem, -1) is { } e) moved.Add((elem, e.Elem, false, -1, -1, -1, -1));      // an entity with no row left, touched in this step: what else it holds (windows, lineage) follows
        Touch(elem);
    }

    /// <summary>Per (element, fragment, own id of the row's key or of its source, own id of its target or -1) the summed weight of the rows whose own ids differ from what the element implies (an entity: its own id; a relationship: its endpoints). Only keys of a class of two or more get here, so a split moves exactly the rows of the part that leaves. Each row holds its fragment in `values`.</summary>
    readonly Table owned = new([4, 4, 4, 4], keyColumns: 4);
    Arrangement? byOwner;
    Arrangement ByOwner => byOwner ??= owned.Arrange(0);

    void Own(int elem, ReadOnlySpan<byte> fragment, int a, int b, long w)
    {
        if (w == 0) return;
        var code = values.Find(fragment);
        if (code < 0 || owned.Find([elem, code, a, b]) < 0) { owned.Add([elem, values.Hold(fragment), a, b], w); return; }
        if (owned.Add([elem, code, a, b], w) < 0) values.Release(code);
    }

    /// <summary>What one emit says about an element: its sink, create or update, type, endpoints and properties.</summary>
    readonly record struct Fragment(int Sink, bool Update, string Type, string? Source, string? Target, Props Props);

    /// <summary>The sink table: per (element, fragment) the summed weight of the rows that contribute the fragment; a row at weight zero is gone. Each fragment is kept once in `values`, in a canonical form (equal fragments have equal bytes: a number without its scale, every text and name as the engine's number for it), and only while a row holds it.</summary>
    readonly Table contributions = new([4, 4], keyColumns: 2);
    readonly Values values = new();
    Arrangement? byElement;
    /// <summary>The contributions of one element: every row of it is wanted when the element is resolved.</summary>
    Arrangement ByElement => byElement ??= contributions.Arrange(0);
    /// <summary>The element handles the sink has seen, and of these the relationships: entity or relationship, from its first fragment. One bit per handle each.</summary>
    readonly Bits known = new(), rels = new();
    /// <summary>(endpoint, relationship) while a fragment of the relationship names the endpoint and the graph does not hold the relationship at it, so a merge visits those and the graph's adjacency at the endpoint, not every element. A relationship the graph holds at the endpoints its fragments name, the normal case, has no row here once flushed.</summary>
    readonly Table ends = new([4, 4], keyColumns: 2);
    Arrangement? byEnd;
    /// <summary>The relationships at one endpoint: every one of them is wanted when the endpoint merges.</summary>
    Arrangement ByEnd => byEnd ??= ends.Arrange(0);
    /// <summary>The elements whose one row is a create the graph holds byte for byte: per element its sink and the row's weight, and no row in `contributions` and no fragment in `values` (the graph has the fragment). A row that arrives for such an element is kept as usual beside it; before anything reads the element's rows, the fragment is written back from the graph (`Materialize`). An element enters at the flush after the one that wrote it to the graph (`Collapse`).</summary>
    readonly Table ones = new([4, 4], keyColumns: 1);
    /// <summary>The elements the last flush resolved: each may enter `ones` at the next flush, when the graph holds what it resolved to.</summary>
    readonly List<int> resolved = [];
    /// <summary>The elements touched since the last flush, in the order they were first touched, and the same as a bitmap.</summary>
    readonly List<int> touched = [];
    readonly Bits dirty = new();
    List<byte> buffer = [];

    void Touch(int elem) { if (dirty.Add(elem)) touched.Add(elem); }

    static void Varint(List<byte> o, ulong v) { while (v >= 0x80) { o.Add((byte)(v | 0x80)); v >>= 7; } o.Add((byte)v); }
    static ulong ReadVarint(byte[] b, ref int p) { ulong v = 0; var s = 0; while (true) { var x = b[p++]; v |= (ulong)(x & 0x7f) << s; if (x < 0x80) return v; s += 7; } }

    /// <summary>What the sink holds now: rows of contributions, stored fragments and their bytes. Nothing for elements that came and went.</summary>
    public (int Contributions, int Fragments, long FragmentBytes) Held => (contributions.Count + ones.Count, values.Count, values.HeldBytes);

    static ulong Zig(long v) => (ulong)((v << 1) ^ (v >> 63));
    static long Unzig(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);
    void Number(long v) { Span<byte> b = stackalloc byte[8]; BitConverter.TryWriteBytes(b, v); foreach (var x in b) buffer.Add(x); }
    static long ReadNumber(byte[] b, ref int p) { var v = BitConverter.ToInt64(b, p); p += 8; return v; }

    /// <summary>A value in its canonical form: equal values give equal bytes. A decimal is written by its number (2.5 and 2.50 are one), a text and an entity id as the engine's number for it.</summary>
    void WriteCanonical(Value v)
    {
        switch (v)
        {
            case NullValue: buffer.Add(0); break;
            case TextValue t: buffer.Add(1); Varint(buffer, (ulong)it.Intern(t.S)); break;
            case IntegerValue i when i.N >= long.MinValue && i.N <= long.MaxValue: buffer.Add(2); Varint(buffer, Zig((long)i.N)); break;
            case IntegerValue i: { buffer.Add(3); var big = i.N.ToByteArray(); Varint(buffer, (ulong)big.Length); buffer.AddRange(big); break; }
            case DecimalValue d: { buffer.Add(4); var (lo, hi) = DecimalKey.Of(d.Q); Number(lo); Number(hi); break; }
            case BooleanValue b: buffer.Add(b.B ? (byte)6 : (byte)5); break;
            case InstantValue n: buffer.Add(7); Varint(buffer, Zig(n.Ns)); break;
            case EntityValue e: buffer.Add(8); Varint(buffer, (ulong)it.Intern(e.Id)); break;
            case ListValue l: buffer.Add(9); Varint(buffer, (ulong)l.Items.Length); foreach (var x in l.Items) WriteCanonical(x); break;
            default: throw new InvalidOperationException($"a {v.Kind} value has no canonical form");
        }
    }

    Value ReadValue(byte[] b, ref int p)
    {
        switch (b[p++])
        {
            case 0: return Value.Null;
            case 1: return it.Text((int)ReadVarint(b, ref p));
            case 2: return Value.Integer(Unzig(ReadVarint(b, ref p)));
            case 3: { var n = (int)ReadVarint(b, ref p); var big = new System.Numerics.BigInteger(b.AsSpan(p, n)); p += n; return Value.Integer(big); }
            case 4: { var lo = ReadNumber(b, ref p); var hi = ReadNumber(b, ref p); return Value.Decimal(new decimal([(int)(lo >> 32), (int)lo, (int)(hi >> 32), (int)hi])); }
            case 5: return Value.Boolean(false);
            case 6: return Value.Boolean(true);
            case 7: return Value.Instant(Unzig(ReadVarint(b, ref p)));
            case 8: return Value.Entity(it.Get((int)ReadVarint(b, ref p)));
            default: { var n = (int)ReadVarint(b, ref p); var items = new Value[n]; for (var i = 0; i < n; i++) items[i] = ReadValue(b, ref p); return Value.List(items); }
        }
    }

    /// <summary>The cell of a column at `row` in its canonical form: the bytes `WriteCanonical` gives for the value `Codec.ToValue` reads from it, written from the cell itself (a text and an entity id are already the engine's numbers for them).</summary>
    void WriteCanonical(Column c, int row, Tag tag)
    {
        switch (tag)
        {
            case Tag.Text: buffer.Add(1); Varint(buffer, (ulong)(int)c.L[row]); break;
            case Tag.Integer: buffer.Add(2); Varint(buffer, Zig(c.L[row])); break;
            case Tag.Decimal: { buffer.Add(4); var (lo, hi) = DecimalKey.Of(c.D[row]); Number(lo); Number(hi); break; }
            case Tag.Boolean: buffer.Add(c.L[row] != 0 ? (byte)6 : (byte)5); break;
            case Tag.Instant: buffer.Add(7); Varint(buffer, Zig(c.L[row])); break;
            case Tag.Ent: buffer.Add(8); Varint(buffer, (ulong)(int)c.L[row]); break;
            default: throw new InvalidOperationException($"no value for {tag}");
        }
    }

    /// <summary>Starts a fragment in `buffer`: sink, create or update, type, endpoints (-1 for none) and the number of properties that follow, each by its name's number.</summary>
    void Header(int sink, bool update, int type, int source, int target, int count)
    {
        buffer.Clear();
        Varint(buffer, (ulong)sink); buffer.Add(update ? (byte)1 : (byte)0); Varint(buffer, (ulong)type);
        Varint(buffer, source < 0 ? 0UL : (ulong)source + 1); Varint(buffer, target < 0 ? 0UL : (ulong)target + 1);
        Varint(buffer, (ulong)count);
    }

    int Code(string? s) => s is null ? -1 : it.Intern(s);

    /// <summary>Writes a fragment into `buffer` in its canonical form.</summary>
    ReadOnlySpan<byte> CanonicalFragment(int sink, bool update, string type, int source, int target, Props props)
    {
        Header(sink, update, it.Intern(type), source, target, props.Count);
        foreach (var (k, v) in props.Items) { Varint(buffer, (ulong)it.Intern(k)); WriteCanonical(v); }
        return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer);
    }

    Fragment Decode(int code)
    {
        var (b, p) = values.Slice(code);
        var sink = (int)ReadVarint(b, ref p); var update = b[p++] == 1; var type = it.Get((int)ReadVarint(b, ref p));
        var s = (int)ReadVarint(b, ref p); var t = (int)ReadVarint(b, ref p);
        var n = (int)ReadVarint(b, ref p); var props = new KeyValuePair<string, Value>[n];
        for (var i = 0; i < n; i++) { var key = it.Get((int)ReadVarint(b, ref p)); props[i] = KeyValuePair.Create(key, ReadValue(b, ref p)); }
        return new(sink, update, type, s == 0 ? null : it.Get(s - 1), t == 0 ? null : it.Get(t - 1), Props.Of(props));
    }

    /// <summary>The endpoints a fragment names (null for an entity's).</summary>
    (string? Source, string? Target) EndsOf(int code)
    {
        var (b, p) = values.Slice(code);
        ReadVarint(b, ref p); p++; ReadVarint(b, ref p);
        var s = (int)ReadVarint(b, ref p); var t = (int)ReadVarint(b, ref p);
        return (s == 0 ? null : it.Get(s - 1), t == 0 ? null : it.Get(t - 1));
    }

    /// <summary>Adds `w` to the weight of a fragment of an element. A new row holds its fragment, a row that goes releases it. The endpoint index follows: an endpoint is in it while some fragment of the relationship names it.</summary>
    void AddFragment(int elem, ReadOnlySpan<byte> fragment, long w, int source, int target)
    {
        if (w == 0) return;
        var code = values.Find(fragment);
        if (code < 0 || contributions.Find([elem, code]) < 0)
        {
            code = values.Hold(fragment);
            if (source >= 0 && ends.Find([source, elem]) < 0) ends.Add([source, elem], 1);
            if (target >= 0 && ends.Find([target, elem]) < 0) ends.Add([target, elem], 1);
            contributions.Add([elem, code], w);
            return;
        }
        if (contributions.Add([elem, code], w) >= 0) return;
        Unend(elem, source);
        if (target != source) Unend(elem, target);
        values.Release(code);
    }

    /// <summary>An endpoint leaves the index when no fragment of the relationship names it any more.</summary>
    void Unend(int elem, int end)
    {
        if (end < 0 || ends.Find([end, elem]) < 0) return;
        var name = it.Get(end);
        if (!RowsOf(elem).Any(x => EndsOf(x.Code) is var e && (e.Source == name || e.Target == name))) ends.Add([end, elem], -1);
    }

    /// <summary>The graph will hold relationship `elem` (`held`) or not: an endpoint its fragments name is in `ends` exactly when the graph does not hold the relationship there. A relationship the graph holds is at the endpoints of each of its positive create fragments (they agree, or it would be absent).</summary>
    void Settle(int elem, bool held)
    {
        int s = -1, t = -1;
        if (held)
            for (var row = ByElement.First([elem]); row >= 0 && s < 0; row = ByElement.Next(row))
                if (contributions.Weight(row) > 0 && EndCodes((int)contributions.Get(row, 1)) is var (source, target, update) && !update) (s, t) = (source, target);
        for (var row = ByElement.First([elem]); row >= 0; row = ByElement.Next(row))
        {
            var (source, target, _) = EndCodes((int)contributions.Get(row, 1));
            foreach (var end in (ReadOnlySpan<int>)[source, target])
            {
                if (end < 0) continue;
                var there = end == s || end == t; var listed = ends.Find([end, elem]) >= 0;
                if (there && listed) ends.Add([end, elem], -1);
                else if (!there && !listed) ends.Add([end, elem], 1);
            }
        }
    }

    /// <summary>The endpoints a fragment names, as text numbers (-1 for none), and whether it is an update.</summary>
    (int Source, int Target, bool Update) EndCodes(int code)
    {
        var (b, p) = values.Slice(code);
        ReadVarint(b, ref p); var update = b[p++] == 1; ReadVarint(b, ref p);
        return ((int)ReadVarint(b, ref p) - 1, (int)ReadVarint(b, ref p) - 1, update);
    }

    /// <summary>Writes into `buffer` the create fragment of sink `sink` that is what `g` holds for `elem`, in the canonical form; false when the graph does not have the element or a value has no canonical form there.</summary>
    bool FromGraph(Graph g, int elem, int sink, out int source, out int target)
    {
        if (!g.TryRead(elem, rels.Has(elem), out var type, out source, out target, out var props)) return false;
        var p = 0; var n = (int)ReadVarint(props, ref p);
        Header(sink, false, type, source, target, n);
        for (var i = 0; i < n; i++) { Varint(buffer, ReadVarint(props, ref p)); if (!Canonical(props, ref p)) return false; }
        return true;
    }

    static ulong ReadVarint(ReadOnlySpan<byte> b, ref int p) { ulong v = 0; var s = 0; while (true) { var x = b[p++]; v |= (ulong)(x & 0x7f) << s; if (x < 0x80) return v; s += 7; } }

    /// <summary>One value of a graph's props block, written into `buffer` in the canonical form; false for a big integer.</summary>
    bool Canonical(ReadOnlySpan<byte> b, ref int p)
    {
        var tag = b[p++];
        switch (tag)
        {
            case 0 or 5 or 6: buffer.Add(tag); return true;
            case 1 or 2 or 7 or 8: buffer.Add(tag); Varint(buffer, ReadVarint(b, ref p)); return true;
            case 4: { Span<int> bits = stackalloc int[4]; for (var i = 0; i < 4; i++) { bits[i] = BitConverter.ToInt32(b[p..]); p += 4; } Decimal(new decimal(bits)); return true; }
            case 10: { var f = b[p++]; var m = ReadVarint(b, ref p); Decimal(new decimal((int)(uint)m, (int)(uint)(m >> 32), 0, (f & 0x80) != 0, (byte)(f & 0x7f))); return true; }
            case 9: { buffer.Add(9); var n = ReadVarint(b, ref p); Varint(buffer, n); for (var i = 0UL; i < n; i++) if (!Canonical(b, ref p)) return false; return true; }
            default: return false;
        }
    }
    void Decimal(decimal d) { buffer.Add(4); var (lo, hi) = DecimalKey.Of(d); Number(lo); Number(hi); }

    /// <summary>The elements the last flush resolved whose one row is a positive create the graph now holds byte for byte keep only their sink and weight (`ones`).</summary>
    void Collapse(Graph g)
    {
        foreach (var elem in resolved)
        {
            var row = ByElement.First([elem]);
            if (row < 0 || ByElement.Next(row) >= 0 || ones.Find([elem]) >= 0) continue;
            var w = contributions.Weight(row); var code = (int)contributions.Get(row, 1);
            var (b, p) = values.Slice(code);
            var sink = (int)ReadVarint(b, ref p);
            if (w <= 0 || b[p] != 0) continue;
            if (contested.Count > 0 && rels.Has(elem) && contestedBy.ContainsKey(it.Get(elem))) continue;
            if (conflicts.AnyAboutElements && conflicts.About(it.Get(elem))) continue;            // the graph holds it without its conflicting properties: its one row may equal that and still have a conflict to clear
            if (!FromGraph(g, elem, sink, out _, out _) || !values.Bytes(code).SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer))) continue;
            contributions.Add([elem, code], -w);
            values.Release(code);
            ones.Set(ones.Add([elem], w), 1, sink);
        }
    }

    /// <summary>An element of `ones` gets its fragment back as a row, written from what `g` holds for it (the graph has not changed it since it entered).</summary>
    void Materialize(Graph g, int elem)
    {
        var row = ones.Find([elem]);
        if (row < 0) return;
        var sink = (int)ones.Get(row, 1); var w = ones.Weight(row);
        ones.AddAt(row, -w);
        if (!FromGraph(g, elem, sink, out var source, out var target)) throw new InvalidOperationException($"the graph lost element {it.Get(elem)}, which its sink keeps only there");
        AddFragment(elem, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer), w, source, target);
    }

    /// <summary>The sink that creates an element: the smallest sink with a create fragment of positive weight, as `Resolve` picks it; -1 when none does.</summary>
    public int CreateSink(int elem)
    {
        var one = ones.Find([elem]);
        var best = one >= 0 ? (int)ones.Get(one, 1) : -1;
        for (var r = ByElement.First([elem]); r >= 0; r = ByElement.Next(r))
        {
            if (contributions.Weight(r) <= 0) continue;
            var (b, p) = values.Slice((int)contributions.Get(r, 1));
            var sink = (int)ReadVarint(b, ref p);
            if (b[p] == 0 && (best < 0 || sink < best)) best = sink;
        }
        return best;
    }

    /// <summary>The fragments of an element with their weights, as fragment codes.</summary>
    List<(int Code, long Weight)> RowsOf(int elem)
    {
        var res = new List<(int, long)>();
        for (var r = ByElement.First([elem]); r >= 0; r = ByElement.Next(r)) res.Add(((int)contributions.Get(r, 1), contributions.Weight(r)));
        return res;
    }
    public long Rejections { get; private set; }
    /// <summary>Properties that fragments with different values assign (counted each time such an element is resolved): they are absent from the element.</summary>
    public long PropertyConflicts { get; private set; }
    static string Canonical(Value v) => v switch { DecimalValue d => Norm.Decimal(d.Q), IntegerValue i => i.N.ToString(System.Globalization.CultureInfo.InvariantCulture), TextValue t => t.S, BooleanValue b => b.B ? "true" : "false", InstantValue n => Norm.Instant(n.Ns), EntityValue e => e.Id, _ => v.ToString() ?? "" };
    /// <summary>Elements whose create fragments disagree on type or endpoints (counted each time such an element is resolved): they are absent.</summary>
    public long ElementConflicts { get; private set; }

    public void Contribute(int elem, bool isRel, int sink, bool update, string type, string? source, string? target, Props props, long weight)
    {
        var (s, t) = (Code(source), Code(target));
        Add(elem, isRel, CanonicalFragment(sink, update, type, s, t, props), weight, s, t);
    }

    /// <summary>A row of an emit, read from its evaluated columns at `row` in the order of `shape`: the same bytes as the properties of the row through `Contribute(..., Props, ...)`. An update that sets no property says nothing and gives false. `ownA`, `ownB`: the own ids of the row's key (and -1), or of its source and target keys.</summary>
    public bool Contribute(FragmentShape shape, int elem, int source, int target, Column[] cols, int row, long weight, int ownA, int ownB)
    {
        var n = 0;
        foreach (var c in shape.Cols) if (cols[c].Present(row)) n++;
        if (shape.Update && n == 0) return false;
        Header(shape.Sink, shape.Update, shape.Type, source, target, n);
        var last = -1;
        for (var i = 0; i < shape.Names.Length; i++)
        {
            var col = cols[shape.Cols[i]];
            if (!col.Present(row)) continue;
            if (shape.Names[i] == last) throw new ArgumentException($"duplicate property {it.Get(last)}");
            Varint(buffer, (ulong)(last = shape.Names[i])); WriteCanonical(col, row, shape.Tags[i]);
        }
        var fragment = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer);
        if (shape.IsRel ? (ownA, ownB) != (source, target) : ownA != elem) Own(elem, fragment, ownA, ownB, weight);         // before `Add`, which may write the graph's fragment into `buffer`
        Add(elem, shape.IsRel, fragment, weight, source, target);
        return true;
    }

    void Add(int elem, bool isRel, ReadOnlySpan<byte> fragment, long weight, int source, int target)
    {
        if (known.Add(elem) && isRel) rels.Add(elem);
        if (HeldAsOne(elem, fragment, weight)) return;
        AddFragment(elem, fragment, weight, source, target);             // a retraction may arrive before the insertion it cancels: the weights add up in any order
        Touch(elem);
    }

    /// <summary>The graph the last flush brought to what its elements resolve to, while it holds texts as this layer's numbers: what an element of `ones` is.</summary>
    Graph? flushed;
    /// <summary>The other buffer: `HeldAsOne` swaps it with `buffer`, so the row's bytes stay where they are while the graph's element is written.</summary>
    List<byte> spare = [];

    /// <summary>A row of an element in `ones` with the fragment the graph holds for it, and a weight that leaves the row positive: the element's positive fragments stay what they were, so it resolves as before and needs no flush. The weight goes to `ones`. The steady case: a closed window of a windowed group adds one row to an element its first window created. `fragment` stays valid.</summary>
    bool HeldAsOne(int elem, ReadOnlySpan<byte> fragment, long weight)
    {
        if (flushed is null || ones.Count == 0) return false;
        var row = ones.Find([elem]);
        if (row < 0 || ones.Weight(row) + weight <= 0) return false;
        (buffer, spare) = (spare, buffer);
        if (!FromGraph(flushed, elem, (int)ones.Get(row, 1), out _, out _) || !fragment.SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer))) return false;
        ones.AddAt(row, weight);
        return true;
    }

    Element? Resolve(int elem)
    {
        var id = it.Get(elem);
        conflicts.EndOf(id);
        var isRel = rels.Has(elem);
        switch (Merge(elem, isRel))
        {
            case Merged.NoCreate: return null;
            case Merged.Clean:
            {
                var items = new KeyValuePair<string, Value>[merged.Count];
                for (var i = 0; i < items.Length; i++) { var (key, b, start, _) = merged[i]; var p = start; items[i] = KeyValuePair.Create(it.Get(key), ReadValue(b, ref p)); }
                var props = Props.FromSorted(items);             // `merged` is in the order of the key texts
                return isRel ? new Rel(id, it.Get(mSource), it.Get(mTarget), it.Get(mType), props) : new Entity(id, it.Get(mType), props);
            }
            default: return Disputed(elem, id);
        }
    }

    /// <summary>The state of an element whose fragments disagree (or that the merge in codes does not take): absent with a conflict noted, or present without the properties that have two values.</summary>
    Element? Disputed(int elem, string id)
    {
        var all = RowsOf(elem).Select(x => (F: Decode(x.Code), x.Weight)).ToList();
        var creates = all.Where(c => !c.F.Update && c.Weight > 0).Select(c => c.F).OrderBy(c => c.Sink).ToList();
        if (creates.Count == 0) return null;
        var first = creates[0];
        if (creates.Any(c => c.Type != first.Type || c.Source != first.Source || c.Target != first.Target))        // creates that disagree on type or endpoints: the element is absent
        {
            ElementConflicts++;
            conflicts.Note("element " + id, new Conflict("element", id, null, null, [.. creates.Select(c => $"{c.Type} {c.Source} -> {c.Target}").Distinct().Order(StringComparer.Ordinal)]));
            return null;
        }
        // Def. 6.4 (D34): the state is a function of the positive fragments, whatever their sink or order. A property with more than one value is absent and counted.
        var values = new Dictionary<string, Value>(StringComparer.Ordinal); HashSet<string>? conflicting = null;
        foreach (var (c, w) in all) if (w > 0) foreach (var (k, v) in c.Props.Items)
            if (!values.TryAdd(k, v) && !values[k].Equals(v)) (conflicting ??= []).Add(k);
        if (conflicting is not null)
        {
            PropertyConflicts += conflicting.Count;
            foreach (var k in conflicting)
            {
                values.Remove(k);
                conflicts.Note($"property {id} {k}", new Conflict("property", id, k, null, [.. all.Where(c => c.Weight > 0).SelectMany(c => c.F.Props.Items).Where(x => x.Key == k).Select(x => Canonical(x.Value)).Distinct().Order(StringComparer.Ordinal)]));
            }
        }
        var props = Props.Of(values);
        return rels.Has(elem) ? new Rel(id, first.Source!, first.Target!, first.Type, props) : new Entity(id, first.Type, props);
    }

    readonly PropBlockWriter block = new();
    /// <summary>The properties of the positive fragments of the element being compared, by key in the order of the key texts: the fragment's bytes and where the value starts and ends.</summary>
    readonly List<(int Key, byte[] B, int Start, int End)> merged = [];

    int mType, mSource, mTarget;
    enum Merged { Clean, NoCreate, Disputed }

    /// <summary>
    /// Whether `g` holds exactly what `Resolve(elem)` gives, decided in codes: the graph's element has the merged type, endpoints and prop block.
    /// False when it differs or the codes cannot tell (a decimal stored at another scale, say): `Resolve` then decides. Makes no string and no element.
    /// </summary>
    bool Unchanged(Graph g, int elem, bool isRel)
    {
        if (Merge(elem, isRel) != Merged.Clean) return false;
        block.Clear(); block.Count(merged.Count);
        foreach (var (key, b, start, _) in merged) { block.Key(key); var p = start; Copy(b, ref p); }
        return g.Holds(elem, mType, mSource, mTarget, block.Block);
    }

    /// <summary>
    /// The positive fragments of an element merged in codes: clean when its create fragments agree on type and endpoints (in `mType`, `mSource`,
    /// `mTarget`, -1 for an entity's) and no key has two values (the props in `merged`); no create when none has positive weight; disputed when they
    /// disagree, a key has two values or a value is a big integer (whose canonical form is not the graph's).
    /// </summary>
    Merged Merge(int elem, bool isRel)
    {
        int type = -1, source = -1, target = -1, fragments = 0; var create = false;
        merged.Clear();
        for (var r = ByElement.First([elem]); r >= 0; r = ByElement.Next(r))
        {
            if (contributions.Weight(r) <= 0) continue;
            var (b, p) = values.Slice((int)contributions.Get(r, 1));
            ReadVarint(b, ref p); var update = b[p++] == 1; var ty = (int)ReadVarint(b, ref p);
            var s = (int)ReadVarint(b, ref p) - 1; var t = (int)ReadVarint(b, ref p) - 1;
            if (!update)
            {
                if (!create) { create = true; type = ty; source = s; target = t; }
                else if (ty != type || s != source || t != target) return Merged.Disputed;
            }
            var n = (int)ReadVarint(b, ref p);
            var first = fragments++ == 0;
            for (var i = 0; i < n; i++)
            {
                var key = (int)ReadVarint(b, ref p); var start = p;
                if (!Skip(b, ref p)) return Merged.Disputed;
                if (first) { merged.Add((key, b, start, p)); continue; }
                var at = merged.Count - 1;
                while (at >= 0 && merged[at].Key != key) at--;
                if (at >= 0)
                {
                    var o = merged[at];
                    if (!b.AsSpan(start, p - start).SequenceEqual(o.B.AsSpan(o.Start, o.End - o.Start))) return Merged.Disputed;
                    continue;
                }
                var text = it.Get(key); var j = merged.Count;
                while (j > 0 && string.CompareOrdinal(it.Get(merged[j - 1].Key), text) > 0) j--;
                merged.Insert(j, (key, b, start, p));
            }
        }
        if (!create) return Merged.NoCreate;
        if (source >= 0 != isRel || target >= 0 != isRel) return Merged.Disputed;
        (mType, mSource, mTarget) = (type, source, target);
        return Merged.Clean;
    }

    /// <summary>Moves past one canonical value; false for a big integer, whose canonical form is not the graph's.</summary>
    static bool Skip(byte[] b, ref int p)
    {
        switch (b[p++])
        {
            case 0 or 5 or 6: return true;
            case 1 or 2 or 7 or 8: ReadVarint(b, ref p); return true;
            case 4: p += 16; return true;
            case 9: { var n = (int)ReadVarint(b, ref p); for (var i = 0; i < n; i++) if (!Skip(b, ref p)) return false; return true; }
            default: return false;
        }
    }

    /// <summary>Writes one canonical value into `block` as the graph stores the value `ReadValue` gives (a decimal at the scale its canonical number has).</summary>
    void Copy(byte[] b, ref int p)
    {
        switch (b[p++])
        {
            case 0: block.Null(); break;
            case 1: block.Text((int)ReadVarint(b, ref p)); break;
            case 2: block.Integer(Unzig(ReadVarint(b, ref p))); break;
            case 4: { var lo = ReadNumber(b, ref p); var hi = ReadNumber(b, ref p); block.Decimal(new decimal((int)(lo >> 32), (int)lo, (int)(hi >> 32), (int)hi < 0, (byte)((int)hi >> 16))); break; }
            case 5: block.Boolean(false); break;
            case 6: block.Boolean(true); break;
            case 7: block.Instant(Unzig(ReadVarint(b, ref p))); break;
            case 8: block.Entity((int)ReadVarint(b, ref p)); break;
            default: { var n = (int)ReadVarint(b, ref p); block.List(n); for (var i = 0; i < n; i++) Copy(b, ref p); break; }
        }
    }

    /// <summary>The (source, target, type) tuples that more than one relationship with positive support claims, with their claimants. Of these the smallest id is present (Def. 6.4, D34); the others are held back and come back when it goes. A tuple with one claimant, the normal case, is not here.</summary>
    readonly Dictionary<(string Source, string Target, string Type), HashSet<string>> contested = [];
    readonly Dictionary<string, (string Source, string Target, string Type)> contestedBy = new(StringComparer.Ordinal);
    /// <summary>The working sets of a flush: what each touched element resolves to, the tuples claimed in it by the first claimant seen, the tuples whose claimants changed. Cleared, not made, by each flush; let go after a flush that grew them past 4,096.</summary>
    Dictionary<ElementKey, Element?> want = [];
    Dictionary<(string, string, string), string> claims = [];
    HashSet<(string, string, string)> affected = [];

    /// <summary>Edits that bring the graph to the resolved states of the touched elements. Where several relationships claim one (source, target, type), the one with the smallest id is present, whatever arrived first; each such tuple is counted.</summary>
    public List<Edit> Flush(Graph g)
    {
        want.Clear(); claims.Clear(); affected.Clear();
        var inCodes = g.Uses(it);
        flushed = inCodes ? g : null;
        if (inCodes) Collapse(g);
        resolved.Clear();
        foreach (var elem in touched)
        {
            if (ones.Find([elem]) >= 0)
            {
                if (ByElement.First([elem]) < 0) continue;                  // its rows cancelled: it is what the graph holds
                Materialize(g, elem);
            }
            resolved.Add(elem);
            var isRel = rels.Has(elem);
            // the graph holds what the element resolves to, and the element has no conflict to clear and no contested tuple: no edit, nothing to note
            if (inCodes && !conflicts.AnyAboutElements && (!isRel || contested.Count == 0) && Unchanged(g, elem, isRel)) { if (isRel) Settle(elem, true); continue; }
            var after = Resolve(elem);
            var id = it.Get(elem);
            want[isRel ? ElementKey.Rel(id) : ElementKey.Entity(id)] = after;
            if (!isRel) continue;
            if (contestedBy.Remove(id, out var old)) { contested[old].Remove(id); affected.Add(old); }
            if (after is not Rel r) continue;
            var tuple = (r.Source, r.Target, r.Type);
            // who else holds the tuple: a relationship of the graph that this flush does not move away, or one resolved earlier in this flush
            var holder = g.FindRel(r.Source, r.Target, r.Type) is { } inGraph && inGraph.Id != id && (!want.TryGetValue(ElementKey.Rel(inGraph.Id), out var moved) || moved is Rel m && (m.Source, m.Target, m.Type) == tuple) ? inGraph.Id
                : claims.TryGetValue(tuple, out var first) && first != id ? first : null;
            claims.TryAdd(tuple, id);
            if (holder is null && !contested.ContainsKey(tuple)) continue;
            if (!contested.TryGetValue(tuple, out var set)) contested[tuple] = set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var claimant in holder is null ? [id] : new[] { id, holder }) { set.Add(claimant); contestedBy[claimant] = tuple; }
            affected.Add(tuple);
        }
        foreach (var e in touched) dirty.Remove(e);
        touched.Clear();
        foreach (var tuple in affected)
        {
            var set = contested[tuple];
            if (set.Count > 1) Rejections++;
            var winner = set.Order(StringComparer.Ordinal).FirstOrDefault();
            conflicts.Note($"uniqueness {tuple.Item1} {tuple.Item2} {tuple.Item3}", set.Count > 1 ? new Conflict("uniqueness", winner!, null, null, [.. set.Order(StringComparer.Ordinal)]) : null);
            foreach (var claimant in set) if (it.TryFind(claimant, out var c)) { Materialize(g, c); resolved.Add(c); }        // the graph may drop it: its fragment comes back from the graph first
            foreach (var claimant in set)
                want[ElementKey.Rel(claimant)] = claimant == winner && it.TryFind(claimant, out var h) && known.Has(h) ? Resolve(h) : null;
            if (set.Count <= 1) { contested.Remove(tuple); foreach (var claimant in set) contestedBy.Remove(claimant); }
        }
        var edits = new List<Edit>(want.Count);
        foreach (var (key, after) in want)
        {
            if (key.Kind == 'r' && it.TryFind(key.Id, out var elem)) Settle(elem, after is not null);
            var before = g.Get(key);
            if (!Equals(before, after)) edits.Add(new Edit(key, before, after));
        }
        if (want.Count > 4096 || claims.Count > 4096 || affected.Count > 4096) (want, claims, affected) = ([], [], []);
        else { want.Clear(); claims.Clear(); affected.Clear(); }
        return edits;
    }
}
