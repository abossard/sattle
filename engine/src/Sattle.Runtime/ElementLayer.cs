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

    /// <summary>Two identities merged: what was emitted under the loser moves to the survivor (an entity's rows as they are; a relationship with the loser as an endpoint gets the id of its new endpoints). Gives the elements whose rows moved, old and new. The old elements resolve to nothing and are removed by the next flush.</summary>
    public List<(int From, int To)> Move(int loser, int survivor)
    {
        var moved = new List<(int From, int To)>();
        var lid = it.Get(loser); var sid = it.Get(survivor);
        if (kinds[loser] == Entity && (ByElement.First([loser]) >= 0 || dirty.Has(loser)))
        {
            foreach (var (code, w) in RowsOf(loser))
            {
                var fragment = values.Bytes(code).ToArray();                                    // an entity's fragment names no endpoint, so it is the same fragment under the survivor
                AddFragment(loser, fragment, -w, -1, -1);
                if (!kinds.Has(survivor)) kinds[survivor] = Entity;
                AddFragment(survivor, fragment, w, -1, -1);
                Touch(survivor);
            }
            moved.Add((loser, survivor));
            Touch(loser);
        }
        // the relationships at the loser, by their numbers: the order they were first named in, whatever rows they have now
        foreach (var elem in ByEnd.Rows([loser]).Select(r => (int)ends.Get(r, 1)).Order().ToList())
        {
            var at = RowsOf(elem).Select(x => (Code: x.Code, Weight: x.Weight, F: Decode(x.Code))).Where(x => x.F.Source == lid || x.F.Target == lid).ToList();
            if (kinds[elem] != Relationship || at.Count == 0) continue;
            foreach (var (code, w, f) in at)
            {
                AddFragment(elem, values.Bytes(code).ToArray(), -w, Code(f.Source), Code(f.Target));
                var ns = f.Source == lid ? sid : f.Source; var nt = f.Target == lid ? sid : f.Target;
                var idOf = relSinks[f.Sink];
                var ne = it.Intern(idOf(ns!, nt!));
                Contribute(ne, true, f.Sink, f.Update, f.Type, ns, nt, f.Props, w);
                if (ne != elem && !moved.Contains((elem, ne))) moved.Add((elem, ne));
            }
            Touch(elem);
        }
        return moved;
    }

    /// <summary>What one emit says about an element: its sink, create or update, type, endpoints and properties.</summary>
    readonly record struct Fragment(int Sink, bool Update, string Type, string? Source, string? Target, Props Props);

    /// <summary>The sink table: per (element, fragment) the summed weight of the rows that contribute the fragment; a row at weight zero is gone. Each fragment is kept once in `values`, in a canonical form (equal fragments have equal bytes: a number without its scale, every text and name as the engine's number for it), and only while a row holds it.</summary>
    readonly Table contributions = new([4, 4], keyColumns: 2);
    readonly Values values = new();
    Arrangement? byElement;
    /// <summary>The contributions of one element: every row of it is wanted when the element is resolved.</summary>
    Arrangement ByElement => byElement ??= contributions.Arrange(0);
    /// <summary>Per element handle: entity or relationship, from its first fragment.</summary>
    readonly Segments<byte> kinds = new();
    const byte Entity = 1, Relationship = 2;
    /// <summary>(endpoint, relationship) while a fragment of the relationship names the endpoint, so a merge visits those and not every element.</summary>
    readonly Table ends = new([4, 4], keyColumns: 2);
    Arrangement? byEnd;
    /// <summary>The relationships at one endpoint: every one of them is wanted when the endpoint merges.</summary>
    Arrangement ByEnd => byEnd ??= ends.Arrange(0);
    /// <summary>The elements touched since the last flush, in the order they were first touched, and the same as a bitmap.</summary>
    readonly List<int> touched = [];
    readonly Segments<byte> dirty = new();
    readonly List<byte> buffer = [];

    void Touch(int elem) { if (!dirty.Has(elem)) { dirty[elem] = 1; touched.Add(elem); } }

    static void Varint(List<byte> o, ulong v) { while (v >= 0x80) { o.Add((byte)(v | 0x80)); v >>= 7; } o.Add((byte)v); }
    static ulong ReadVarint(byte[] b, ref int p) { ulong v = 0; var s = 0; while (true) { var x = b[p++]; v |= (ulong)(x & 0x7f) << s; if (x < 0x80) return v; s += 7; } }

    /// <summary>What the sink holds now: rows of contributions, stored fragments and their bytes. Nothing for elements that came and went.</summary>
    public (int Contributions, int Fragments, long FragmentBytes) Held => (contributions.Count, values.Count, values.HeldBytes);

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
            case 1: return Value.Text(it.Get((int)ReadVarint(b, ref p)));
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
        if (end < 0) return;
        var name = it.Get(end);
        if (!RowsOf(elem).Any(x => EndsOf(x.Code) is var e && (e.Source == name || e.Target == name))) ends.Add([end, elem], -1);
    }

    /// <summary>The sink that creates an element: the smallest sink with a create fragment of positive weight, as `Resolve` picks it; -1 when none does.</summary>
    public int CreateSink(int elem)
    {
        var best = -1;
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

    /// <summary>A row of an emit, read from its evaluated columns at `row` in the order of `shape`: the same bytes as the properties of the row through `Contribute(..., Props, ...)`. An update that sets no property says nothing and gives false.</summary>
    public bool Contribute(FragmentShape shape, int elem, int source, int target, Column[] cols, int row, long weight)
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
        Add(elem, shape.IsRel, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer), weight, source, target);
        return true;
    }

    void Add(int elem, bool isRel, ReadOnlySpan<byte> fragment, long weight, int source, int target)
    {
        if (!kinds.Has(elem)) kinds[elem] = isRel ? Relationship : Entity;
        AddFragment(elem, fragment, weight, source, target);             // a retraction may arrive before the insertion it cancels: the weights add up in any order
        Touch(elem);
    }

    Element? Resolve(int elem)
    {
        var all = RowsOf(elem).Select(x => (F: Decode(x.Code), x.Weight)).ToList();
        var creates = all.Where(c => !c.F.Update && c.Weight > 0).Select(c => c.F).OrderBy(c => c.Sink).ToList();
        var id = it.Get(elem);
        conflicts.EndOf(id);
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
        return kinds[elem] == Relationship ? new Rel(id, first.Source!, first.Target!, first.Type, props) : new Entity(id, first.Type, props);
    }

    /// <summary>The (source, target, type) tuples that more than one relationship with positive support claims, with their claimants. Of these the smallest id is present (Def. 6.4, D34); the others are held back and come back when it goes. A tuple with one claimant, the normal case, is not here.</summary>
    readonly Dictionary<(string Source, string Target, string Type), HashSet<string>> contested = [];
    readonly Dictionary<string, (string Source, string Target, string Type)> contestedBy = new(StringComparer.Ordinal);

    /// <summary>Edits that bring the graph to the resolved states of the touched elements. Where several relationships claim one (source, target, type), the one with the smallest id is present, whatever arrived first; each such tuple is counted.</summary>
    public List<Edit> Flush(Graph g)
    {
        var want = new Dictionary<ElementKey, Element?>();
        var claims = new Dictionary<(string, string, string), string>();          // tuples claimed in this flush, by the first claimant seen
        var affected = new HashSet<(string, string, string)>();
        foreach (var elem in touched)
        {
            var isRel = kinds[elem] == Relationship;
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
        foreach (var e in touched) dirty.Clear(e);
        touched.Clear();
        foreach (var tuple in affected)
        {
            var set = contested[tuple];
            if (set.Count > 1) Rejections++;
            var winner = set.Order(StringComparer.Ordinal).FirstOrDefault();
            conflicts.Note($"uniqueness {tuple.Item1} {tuple.Item2} {tuple.Item3}", set.Count > 1 ? new Conflict("uniqueness", winner!, null, null, [.. set.Order(StringComparer.Ordinal)]) : null);
            foreach (var claimant in set)
                want[ElementKey.Rel(claimant)] = claimant == winner && it.TryFind(claimant, out var h) && kinds.Has(h) ? Resolve(h) : null;
            if (set.Count <= 1) { contested.Remove(tuple); foreach (var claimant in set) contestedBy.Remove(claimant); }
        }
        var edits = new List<Edit>();
        foreach (var (key, after) in want)
        {
            var before = g.Get(key);
            if (!Equals(before, after)) edits.Add(new Edit(key, before, after));
        }
        return edits;
    }
}
