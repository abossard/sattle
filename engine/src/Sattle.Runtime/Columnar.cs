using System.Numerics;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

/// <summary>String to dense int table. Text values, entity ids and relationship ids share one table per engine, so a handle names the same element in every graph.
/// A code is young when it is minted for an adapter row (`InternYoung`) or by an expression of a stateless step (`InternComputed` while `MintYoung` is on); every other code is old. A young code becomes old when it is interned old, or when a batch that holds it is escaped: the engine escapes every input of a stateful operator, so every code that reaches state is old. `Collect`, at the end of a step, frees the codes still young (the texts of rows a step dropped); a freed code is reused, the smallest first. An old code is never freed. While a holder (a row writer with rows, a batch not yet ingested) is registered, nothing is freed.</summary>
public sealed class Interner
{
    readonly Dictionary<string, int> map = new(StringComparer.Ordinal);
    readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> alt;
    readonly List<string?> strings = [];
    readonly List<bool> young = [];
    readonly List<int> minted = [];
    readonly PriorityQueue<int, int> free = new();
    readonly HashSet<object> holders = new(ReferenceEqualityComparer.Instance);
    public Interner() { alt = map.GetAlternateLookup<ReadOnlySpan<char>>(); }
    /// <summary>The number of slots: live codes and freed ones.</summary>
    public int Count => strings.Count;
    /// <summary>The number of live texts.</summary>
    public int Live => strings.Count - free.Count;
    /// <summary>On while the engine runs a stateless step (Filter, Map): `InternComputed` mints young.</summary>
    public bool MintYoung { get; set; }
    public int Intern(string s)
    {
        if (map.TryGetValue(s, out var i)) { young[i] = false; return i; }
        return Add(s, false);
    }
    public int Intern(ReadOnlySpan<char> s)
    {
        if (alt.TryGetValue(s, out var i)) { young[i] = false; return i; }
        return Add(s.ToString(), false);
    }
    /// <summary>The code of a text an adapter row carries: young when the text is new.</summary>
    public int InternYoung(string s) => map.TryGetValue(s, out var i) ? i : Add(s, true);
    public int InternYoung(ReadOnlySpan<char> s) => alt.TryGetValue(s, out var i) ? i : Add(s.ToString(), true);
    /// <summary>The code of a text an expression computed: young when new and a stateless step runs, else old.</summary>
    public int InternComputed(string s) => MintYoung ? InternYoung(s) : Intern(s);
    int Add(string s, bool y)
    {
        if (free.TryDequeue(out var i, out _)) { strings[i] = s; young[i] = y; }
        else { i = strings.Count; strings.Add(s); young.Add(y); }
        map[s] = i;
        if (y) minted.Add(i);
        return i;
    }
    public bool IsYoung(int code) => (uint)code < (uint)young.Count && young[code];
    /// <summary>Makes every young code of the batch's selected rows old: the batch is about to reach state. Columns the layout types as numbers are skipped; untyped ones are read as codes, where a false hit only keeps a text.</summary>
    public void Escape(Batch b)
    {
        if (minted.Count == 0) return;
        var typed = b.Layout.Interned;
        for (var c = 0; c < b.Cols.Length; c++)
        {
            var col = b.Cols[c];
            if (col.IsDec || (typed is not null && c < typed.Length && !typed[c])) continue;
            for (var k = 0; k < b.Rows; k++)
            {
                var v = col.L[b.RowAt(k)];
                if ((ulong)v < (ulong)young.Count) young[(int)v] = false;
            }
        }
    }
    /// <summary>Registers something that holds young codes outside the engine (a row writer with rows, a batch not yet ingested): nothing is freed until it is released.</summary>
    public void Hold(object holder) => holders.Add(holder);
    public void Release(object holder) => holders.Remove(holder);
    /// <summary>The end of a step: frees the codes still young, unless a holder is registered.</summary>
    public void Collect()
    {
        if (holders.Count > 0 || minted.Count == 0) return;
        foreach (var i in minted)
        {
            if (!young[i]) continue;
            map.Remove(strings[i]!); strings[i] = null; young[i] = false; free.Enqueue(i, i);
        }
        minted.Clear();
    }
    public bool TryFind(string s, out int i) => map.TryGetValue(s, out i);
    /// <summary>Every slot by code, null where the code is free (a seal).</summary>
    public IReadOnlyList<string?> Slots => strings;
    /// <summary>Replaces the whole table (restoring a snapshot): the same strings get the same codes, the free slots are free again, every code is old.</summary>
    public void ReplaceAll(IReadOnlyList<string?> all)
    {
        map.Clear(); strings.Clear(); young.Clear(); minted.Clear(); free.Clear(); holders.Clear();
        foreach (var x in all)
        {
            var i = strings.Count; strings.Add(x); young.Add(false);
            if (x is null) free.Enqueue(i, i); else map[x] = i;
        }
    }
    public string Get(int i) => strings[i] ?? throw new InvalidOperationException($"text {i} was freed");
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

/// <summary>A signed row batch (a Z-set slice): columns, weights (null means +1 for every row), optional selection vector. `Cols` may hold columns behind those the layout names: the ones a `Map` carries for lineage, which only a `Group` and a sink read; every other reader goes by the layout.</summary>
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

/// <summary>Writes source rows into a batch by field name (cold path) or column index (hot path). Optional fields given null are absent, and so is every column that is not written before the next row begins. Without a text table (`it` null: rows of a type no source binds, which are counted and never read) its texts are not kept.</summary>
public sealed class RowWriter(Layout layout, Interner? it)
{
    BatchBuilder bb = new(layout);
    int row = -1;
    readonly bool[] written = new bool[layout.Count];
    /// <summary>Added to every instant written: a capture replayed again as later history carries its event times forward with its capture clock.</summary>
    public long InstantOffset;
    public Layout Layout { get; } = layout;
    public int Rows => bb.Batch.N;
    public int Col(string name) => Layout.Index(name);
    public void Begin(long weight = 1) { Close(); it?.Hold(this); row = bb.Add(weight); Array.Clear(written); }
    public void Text(int col, string? v) { written[col] = true; if (v is null) bb.Absent(row, col); else bb.Batch.Cols[col].L[row] = it?.InternYoung(v) ?? 0; }
    public void Text(int col, ReadOnlySpan<char> v) { written[col] = true; bb.Batch.Cols[col].L[row] = it is null ? 0 : it.InternYoung(v); }
    public void Code(int col, long code) { written[col] = true; bb.Batch.Cols[col].L[row] = code; }
    public void Int(int col, long v) { written[col] = true; bb.Batch.Cols[col].L[row] = v; }
    public void Instant(int col, long ns) { written[col] = true; bb.Batch.Cols[col].L[row] = ns + InstantOffset; }
    public void Dec(int col, decimal v) { written[col] = true; bb.Batch.Cols[col].D[row] = v; }
    public void Absent(int col) { written[col] = true; bb.Absent(row, col); }
    public void Text(string col, string? v) => Text(Col(col), v);
    public void Instant(string col, long ns) => Instant(Col(col), ns);
    /// <summary>The rows written so far. The batch holds their young codes until the engine ingests it.</summary>
    public Batch? Build() { Close(); var b = bb.Build(); bb = new BatchBuilder(Layout); row = -1; if (b is not null) it?.Hold(b); it?.Release(this); return b; }
    /// <summary>Takes back the rows written after the first `rows` (a record that turned out to be malformed after it had written some).</summary>
    public void Truncate(int rows) { if (rows >= bb.Batch.N) return; bb.Batch.N = rows; row = -1; if (rows == 0) it?.Release(this); }
    void Close() { if (row < 0) return; for (var c = 0; c < written.Length; c++) if (!written[c]) bb.Absent(row, c); }
}
