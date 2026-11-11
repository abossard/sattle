using System.Numerics;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

/// <summary>Append-only string to dense int table. Text values, entity ids and relationship ids share one table per engine, so a handle names the same element in every graph.</summary>
public sealed class Interner
{
    readonly Dictionary<string, int> map = new(StringComparer.Ordinal);
    readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> alt;
    readonly List<string> strings = [];
    public Interner() { alt = map.GetAlternateLookup<ReadOnlySpan<char>>(); }
    public int Count => strings.Count;
    public int Intern(string s)
    {
        if (map.TryGetValue(s, out var i)) return i;
        i = strings.Count; strings.Add(s); map[s] = i; return i;
    }
    public int Intern(ReadOnlySpan<char> s)
    {
        if (alt.TryGetValue(s, out var i)) return i;
        return Intern(s.ToString());
    }
    public bool TryFind(string s, out int i) => map.TryGetValue(s, out i);
    /// <summary>Replaces the whole table (restoring a snapshot): the same strings get the same codes.</summary>
    public void ReplaceAll(IReadOnlyList<string> all) { map.Clear(); strings.Clear(); foreach (var x in all) { map[x] = strings.Count; strings.Add(x); } }
    public string Get(int i) => strings[i];
}

/// <summary>Column layout of a row type: names, and which columns hold decimals (all others hold 64-bit codes: Integer, Instant, Boolean, interned Text, element handles).</summary>
public sealed class Layout
{
    public string[] Names { get; }
    public bool[] IsDec { get; }
    readonly Dictionary<string, int> index;
    /// <summary>Per column: true when the 64-bit code is an index into the interner (Text and entity references), false for raw numbers. Null when the layout was built without types.</summary>
    public bool[]? Interned { get; }
    public Layout(string[] names, bool[] isDec, bool[]? interned = null) { Names = names; IsDec = isDec; Interned = interned; index = names.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.Ordinal); }
    public static Layout Of(Schema s) => new([.. s.Cols.Select(c => c.Name)], [.. s.Cols.Select(c => c.T.Ty.Tag == Tag.Decimal)], [.. s.Cols.Select(c => c.T.Ty.Tag is Tag.Text or Tag.Ent)]);
    public int Count => Names.Length;
    public int Index(string name) => index.TryGetValue(name, out var i) ? i : throw new KeyNotFoundException($"column {name} not in layout [{string.Join(", ", Names)}]");
    public bool Has(string name) => index.ContainsKey(name);
}

public sealed class Column
{
    public long[] L = [];
    public decimal[] D = [];
    public bool[]? P; // presence, null means every row is present
    public bool IsDec;
    public Column(bool isDec) { IsDec = isDec; }
    public void Ensure(int n)
    {
        if (IsDec) { if (D.Length < n) Array.Resize(ref D, Math.Max(n, 64)); }
        else if (L.Length < n) Array.Resize(ref L, Math.Max(n, 64));
        if (P is not null && P.Length < n) { var old = P.Length; Array.Resize(ref P, Math.Max(n, 64)); Array.Fill(P, true, old, P.Length - old); }
    }
    public bool Present(int i) => P is null || P[i];
    public void ClearPresence() => P = null;
    public void SetAbsent(int i, int n) { if (P is null) { P = new bool[Math.Max(n, IsDec ? D.Length : L.Length)]; Array.Fill(P, true); } P[i] = false; }
}

/// <summary>A signed row batch (a Z-set slice): columns, weights (null means +1 for every row), optional selection vector.</summary>
public sealed class Batch
{
    public Layout Layout { get; }
    public Column[] Cols { get; }
    public int N;
    public long[]? W;
    public int[]? Sel;   // selected row indexes in ascending order; null = all
    public int SelN;
    public Batch(Layout layout, int capacity = 0)
    {
        Layout = layout; Cols = new Column[layout.Count];
        for (var i = 0; i < Cols.Length; i++) { Cols[i] = new Column(layout.IsDec[i]); if (capacity > 0) Cols[i].Ensure(capacity); }
    }
    Batch(Layout layout, Column[] cols, int n, long[]? w) { Layout = layout; Cols = cols; N = n; W = w; }

    public int Rows => Sel is null ? N : SelN;
    public int RowAt(int k) => Sel is null ? k : Sel[k];
    public long Weight(int row) => W is null ? 1 : W[row];
    public void Ensure(int n) { foreach (var c in Cols) c.Ensure(n); if (W is not null && W.Length < n) Array.Resize(ref W, Math.Max(n, 64)); }
    public void Clear() { N = 0; Sel = null; SelN = 0; foreach (var c in Cols) c.P = null; }

    /// <summary>A view sharing the column arrays with a new layout and column mapping (no data copy).</summary>
    public Batch Project(Layout layout, int[] source)
    {
        var cols = new Column[source.Length];
        for (var i = 0; i < cols.Length; i++) cols[i] = Cols[source[i]];
        return new Batch(layout, cols, N, W) { Sel = Sel, SelN = SelN };
    }
    public Batch WithSelection(int[] sel, int n) => new(Layout, Cols, N, W) { Sel = sel, SelN = n };
    public Batch WithColumns(Layout layout, Column[] cols) => new(layout, cols, N, W) { Sel = Sel, SelN = SelN };

    public bool IsEmpty => Rows == 0;
}

/// <summary>Per-engine element id handles and a few conversions between values and columns.</summary>
public static class Codec
{
    public static long ToCode(Value v, Interner it) => v switch
    {
        TextValue t => it.Intern(t.S),
        IntegerValue i => (long)i.N,
        BooleanValue b => b.B ? 1 : 0,
        InstantValue n => n.Ns,
        EntityValue e => it.Intern(e.Id),
        _ => throw new InvalidOperationException($"no 64-bit code for {v.Kind}"),
    };

    public static Value ToValue(long code, Ty ty, Interner it) => ty.Tag switch
    {
        Tag.Text => Value.Text(it.Get((int)code)),
        Tag.Integer => Value.Integer(code),
        Tag.Boolean => Value.Boolean(code != 0),
        Tag.Instant => Value.Instant(code),
        Tag.Ent => Value.Entity(it.Get((int)code)),
        _ => throw new InvalidOperationException($"no value for {ty}"),
    };

    public static Value ToValue(Column c, int row, Ty ty, Interner it) =>
        !c.Present(row) ? Value.Null : ty.Tag == Tag.Decimal ? Value.Decimal(c.D[row]) : ToValue(c.L[row], ty, it);

    public static void Set(Column c, int row, Value v, Interner it)
    {
        if (v is NullValue) { c.SetAbsent(row, row + 1); return; }
        if (c.IsDec) c.D[row] = v is DecimalValue d ? d.Q : throw new InvalidOperationException($"decimal column cannot hold {v.Kind}");
        else c.L[row] = ToCode(v, it);
    }
}

/// <summary>Writes source rows into a batch by field name (cold path) or column index (hot path). Optional fields given null are absent, and so is every column that is not written before the next row begins.</summary>
public sealed class RowWriter(Layout layout, Interner it)
{
    BatchBuilder bb = new(layout);
    int row = -1;
    readonly bool[] written = new bool[layout.Count];
    /// <summary>Added to every instant written: a capture replayed again as later history carries its event times forward with its capture clock.</summary>
    public long InstantOffset;
    public Layout Layout { get; } = layout;
    public int Rows => bb.Batch.N;
    public int Col(string name) => Layout.Index(name);
    public void Begin(long weight = 1) { Close(); row = bb.Add(weight); Array.Clear(written); }
    public void Text(int col, string? v) { written[col] = true; if (v is null) bb.Absent(row, col); else bb.Batch.Cols[col].L[row] = it.Intern(v); }
    public void Text(int col, ReadOnlySpan<char> v) { written[col] = true; bb.Batch.Cols[col].L[row] = it.Intern(v); }
    public void Code(int col, long code) { written[col] = true; bb.Batch.Cols[col].L[row] = code; }
    public void Int(int col, long v) { written[col] = true; bb.Batch.Cols[col].L[row] = v; }
    public void Instant(int col, long ns) { written[col] = true; bb.Batch.Cols[col].L[row] = ns + InstantOffset; }
    public void Dec(int col, decimal v) { written[col] = true; bb.Batch.Cols[col].D[row] = v; }
    public void Absent(int col) { written[col] = true; bb.Absent(row, col); }
    public void Text(string col, string? v) => Text(Col(col), v);
    public void Instant(string col, long ns) => Instant(Col(col), ns);
    public Batch? Build() { Close(); var b = bb.Build(); bb = new BatchBuilder(Layout); row = -1; return b; }
    void Close() { if (row < 0) return; for (var c = 0; c < written.Length; c++) if (!written[c]) bb.Absent(row, c); }
}
