using System.Runtime.InteropServices;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

/// <summary>Inputs of one evaluation step of a projection: row batches per rows port, graph deltas per graph port, an optional logical-time advance.</summary>
public sealed class Epoch
{
    public long Id;
    public bool HasAdvance;
    public long Lambda;
    public Dictionary<string, Batch> Rows { get; } = [];
    public Dictionary<string, GraphDelta> Graphs { get; } = [];
    /// <summary>The step was caused by the progress of a source and carries no rows and no advance (Def. 10.9).</summary>
    public bool Progress;
    /// <summary>How far each source has reported (Def. 7.14), at this step.</summary>
    public IReadOnlyDictionary<string, long> Covered = new Dictionary<string, long>();
    /// <summary>The start of a window that was marked with a loss in this step (Def. 7.7, Def. 10.4), or null. A window with a loss mark is not covered.</summary>
    public long? Loss;
}

/// <summary>A committed change of an input graph, as seen by a projection: the patch, the graph after it, and the telemetry windows that changed in the same step.</summary>
public sealed class GraphDelta(Patch? patch, Graph state, IReadOnlyList<WindowChange> telemetry)
{
    public Patch? Patch { get; } = patch;
    public Graph State { get; } = state;
    public IReadOnlyList<WindowChange> Telemetry { get; } = telemetry;

    /// <summary>The places in `Telemetry` of the changes of one property, in their order. The index is built once per delta, on the first call, so each reader of the graph visits only the changes of the properties it reads.</summary>
    public ArraySegment<int> ChangesOf(int prop) => (byProp ??= ByProp(Telemetry)).TryGetValue(prop, out var s) ? s : ArraySegment<int>.Empty;
    volatile Dictionary<int, ArraySegment<int>>? byProp;          // readers on two threads may both build it: the same index, one kept

    static Dictionary<int, ArraySegment<int>> ByProp(IReadOnlyList<WindowChange> changes)
    {
        var slot = new Dictionary<int, int>(); var ends = new List<int>(); var slots = new int[changes.Count];
        for (var i = 0; i < changes.Count; i++)
        {
            ref var s = ref CollectionsMarshal.GetValueRefOrAddDefault(slot, changes[i].Key.Prop, out var found);
            if (!found) { s = ends.Count; ends.Add(0); }
            ends[slots[i] = s]++;
        }
        var starts = new int[ends.Count];
        for (int k = 0, at = 0; k < starts.Length; k++) { starts[k] = at; at += ends[k]; ends[k] = starts[k]; }
        var places = new int[changes.Count];
        for (var i = 0; i < slots.Length; i++) places[ends[slots[i]]++] = i;
        var res = new Dictionary<int, ArraySegment<int>>(slot.Count);
        foreach (var (prop, k) in slot) res[prop] = new ArraySegment<int>(places, starts[k], ends[k] - starts[k]);
        return res;
    }
}

public sealed class OpContext(Interner it, RuntimeFaults faults, IdentityCatalog ids, string tenant, string projectionId, GraphKey graph, OutputBuilder output)
{
    public Interner It { get; } = it;
    public RuntimeFaults Faults { get; } = faults;
    public IdentityCatalog Ids { get; } = ids;
    public string Tenant { get; } = tenant;
    public string ProjectionId { get; } = projectionId;
    public GraphKey Graph { get; } = graph;
    public OutputBuilder Out { get; } = output;
    public long LateDropped, SkippedEmits;
    /// <summary>The late horizon: how many widths after its end a closed window stays revisable. Null is no horizon: the window stays revisable while its telemetry is retained (a recording is finite, so a replay sets none).</summary>
    public long? LateHorizonFactor = 3;
    /// <summary>The retention of telemetry (Def. 7.13). A window is revisable for the smaller of this and the late horizon: a row for a window that is no longer kept must not write it again.</summary>
    public long RetentionNs = long.MaxValue;
    /// <summary>The late horizon of a window of this width as a time; the greatest time when there is none or the product is beyond it.</summary>
    public long HorizonOf(long width) => LateHorizonFactor is { } n ? Clamp((Int128)width * n) : long.MaxValue;
    /// <summary>How long after its end a window of this width stays revisable.</summary>
    public long RevisableFor(long width) => Math.Min(HorizonOf(width), RetentionNs);
    /// <summary>Whether a window of this width that ended at `end` can no longer be revised at this frontier.</summary>
    public bool PastRevision(long end, long width, long frontier) => (Int128)end + RevisableFor(width) <= frontier;
    public static long Clamp(Int128 time) => time > long.MaxValue ? long.MaxValue : (long)time;
}

public abstract class PhysOp
{
    protected PhysOp(TOp t) { Typed = t; OutLayout = Layout.Of(t.Out); }
    public TOp Typed { get; }
    public string Name => Typed.Name;
    public Layout OutLayout { get; protected init; }
    public abstract Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs);
    protected Batch? In(IReadOnlyDictionary<string, Batch?> inputs, string port) => inputs.GetValueOrDefault(port);

    BatchBuilder? output;
    /// <summary>This operator's output batch, emptied for the call in hand. It belongs to the operator and is valid until the operator runs again or the step ends (`WorkingArrays` may cut it then): a reader that keeps rows copies them, as a compiled expression's column is valid until its next evaluation. The engine keeps an operator's output for one step and runs each operator once per step.</summary>
    protected BatchBuilder EmptyOutput() { output ??= new BatchBuilder(OutLayout); output.Reset(); return output; }
}

/// <summary>Row batch assembly with growth, weights always present. The batch is made when it is first needed, so an operator call that writes no row allocates none.</summary>
public sealed class BatchBuilder(Layout layout)
{
    Batch? made;
    public Batch Batch => made ??= new(layout, 64) { W = new long[64] };
    public int Add(long weight)
    {
        var b = Batch;
        var i = b.N;
        if (i >= b.W!.Length) { b.Ensure(i * 2); }
        b.W![i] = weight;
        b.N++;
        // a row starts as in a new batch, zero and present: writers leave columns they do not set, and a reused batch holds the rows of the call before
        foreach (var c in b.Cols) { if (c.IsDec) c.D[i] = 0; else c.L[i] = 0; if (c.P is { } p) p[i] = true; }
        return i;
    }
    public void Absent(int row, int col) { var b = Batch; b.Cols[col].SetAbsent(row, b.N); }
    public Batch? Build() => made is { N: > 0 } ? made : null;

    /// <summary>Empties the batch and keeps its arrays (`PhysOp.EmptyOutput`). In a Debug build the rows given out before are poisoned first, so a reader that kept them past its step reads weights of 0 and values no row has, and the tests fail instead of computing a wrong answer.</summary>
    public void Reset()
    {
        if (made is null) return;
#if DEBUG
        for (var i = 0; i < made.N; i++) made.W![i] = 0;
        foreach (var c in made.Cols) { if (c.IsDec) Array.Fill(c.D, decimal.MinValue, 0, Math.Min(made.N, c.D.Length)); else Array.Fill(c.L, long.MinValue, 0, Math.Min(made.N, c.L.Length)); }
#endif
        made.N = 0; made.Sel = null; made.SelN = 0;
    }
}

// ---- linear operators --------------------------------------------------------------------------------------

sealed class FilterOp : PhysOp
{
    readonly VExpr pred; int[] sel = new int[64];
    public FilterOp(TFilter t, Layout inLayout, OpContext cx) : base(t) { pred = ExprCompiler.Compile(t.Pred, inLayout, cx.It, cx.Faults); }
    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var b = In(inputs, "in"); if (b is null || b.Rows == 0) return null;
        var p = pred.Eval(b);
        if (sel.Length < b.N) sel = new int[b.N];
        var n = 0;
        for (var k = 0; k < b.Rows; k++)
        {
            var i = b.RowAt(k);
            if ((p.P is null || p.P[i]) && p.L[i] != 0) sel[n++] = i;
        }
        return n == 0 ? null : b.WithSelection(sel, n);
    }
}

sealed class MapOp : PhysOp
{
    readonly VExpr?[] compute; readonly int[] passthrough; readonly bool[] isDec; readonly int[] carry;
    /// <summary>`carry`: columns of the input that the output passes on behind its own, for the lineage of what is made of these rows (`PlanCompiler.SourcesOf`).</summary>
    public MapOp(TMap t, Layout inLayout, OpContext cx, int[]? carry = null) : base(t)
    {
        this.carry = carry ?? [];
        compute = new VExpr?[OutLayout.Count]; passthrough = new int[OutLayout.Count]; isDec = OutLayout.IsDec;
        for (var c = 0; c < OutLayout.Count; c++)
        {
            var name = OutLayout.Names[c];
            passthrough[c] = -1;
            var a = t.Assigns.FirstOrDefault(x => x.Name == name);
            if (a.Value is not null)
            {
                if (a.Value is TField f) passthrough[c] = inLayout.Index(f.Name);
                else compute[c] = ExprCompiler.Compile(a.Value, inLayout, cx.It, cx.Faults);
            }
            else
            {
                var parent = t.Assigns.FirstOrDefault(x => name.StartsWith(x.Name + ".", StringComparison.Ordinal) && x.Value is TField);
                if (parent.Value is TField pf) passthrough[c] = inLayout.Index(pf.Name + name[parent.Name.Length..]);
                else throw new InvalidOperationException($"map column {name} has no source");
            }
        }
    }
    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var b = In(inputs, "in"); if (b is null || b.Rows == 0) return null;
        view ??= b.WithColumns(OutLayout, new Column[OutLayout.Count + carry.Length]);
        var cols = view.Cols;
        for (var c = 0; c < OutLayout.Count; c++) cols[c] = passthrough[c] >= 0 ? b.Cols[passthrough[c]] : compute[c]!.Eval(b);
        for (var c = 0; c < carry.Length; c++) cols[OutLayout.Count + c] = b.Cols[carry[c]];
        view.N = b.N; view.W = b.W; view.Sel = b.Sel; view.SelN = b.SelN;
        if (b.N > WorkingArrays.Small) WorkingArrays.Shows(view);
        return view;
    }
    /// <summary>The output: a view of the input's rows with this operator's columns, refilled by every call. Like `EmptyOutput`, it is valid until the operator runs again.</summary>
    Batch? view;
}

sealed class WindowOp : PhysOp
{
    readonly int ts; readonly long width, hop; readonly Column ws = new(false), we = new(false);
    public WindowOp(TWindow t, Layout inLayout, OpContext cx) : base(t) { _ = cx; ts = inLayout.Index(t.TimeField); width = t.WidthNs; hop = t.HopNs; }
    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var b = In(inputs, "in"); if (b is null || b.Rows == 0) return null;
        if (hop != width) return Hop(b);
        ws.Ensure(b.N); we.Ensure(b.N);
        var src = b.Cols[ts].L;
        for (var k = 0; k < b.Rows; k++)
        {
            var i = b.RowAt(k);
            var m = src[i] % width; if (m < 0) m += width;
            ws.L[i] = src[i] - m; we.L[i] = src[i] - m + width;
        }
        var cols = new Column[OutLayout.Count];                 // the input's own columns: ones carried behind them end here
        Array.Copy(b.Cols, cols, cols.Length - 2);
        cols[^2] = ws; cols[^1] = we;
        return b.WithColumns(OutLayout, cols);
    }

    /// <summary>A sliding window: each row goes into the width/hop windows [s, s + width) with s on the grid of the hop and s &lt;= t &lt; s + width, one output row each, with the row's weight.</summary>
    Batch Hop(Batch b)
    {
        var k = (int)(width / hop); var n = b.Rows * k; var own = OutLayout.Count - 2;
        var o = new Batch(OutLayout, n) { W = new long[n] };
        var src = b.Cols[ts].L;
        for (var r = 0; r < b.Rows; r++)
        {
            var i = b.RowAt(r);
            var m = src[i] % hop; if (m < 0) m += hop;
            var last = src[i] - m;                              // the newest window that holds the row
            for (var j = 0; j < k; j++)
            {
                var x = o.N++;
                for (var c = 0; c < own; c++)
                {
                    Column from = b.Cols[c], to = o.Cols[c];
                    if (to.IsDec) to.D[x] = from.D[i]; else to.L[x] = from.L[i];
                    if (!from.Present(i)) to.SetAbsent(x, n);
                }
                o.Cols[^2].L[x] = last - j * hop; o.Cols[^1].L[x] = last - j * hop + width;
                o.W[x] = b.Weight(i);
            }
        }
        return o;
    }
}

// ---- aggregation -----------------------------------------------------------------------------------------

/// <summary>The state of a group operator (core step 3): one row of a `Table` per key, holding the key, the row count, one accumulator per sum, count or mean (a decimal in two 8-byte columns), for a key with more than one value of a `min` or `max` their extreme in the accumulator the aggregate has, and whether the key's aggregate was emitted. A slot is the row number. The values emitted last are not kept: an operator retracts a key's aggregate when it first touches the key in a step, before the step's rows change it. The multisets of `min`, `max` and `countDistinct` are tables of (slot, aggregate, value) with a weight, found through their arrangement by (slot, aggregate); a number is keyed by its value whatever its scale (2.5 and 2.50 are one entry, which keeps the digits it came with first).</summary>
sealed class KeyTable
{
    readonly int k, nL, nD, nIndex;
    readonly Table rows;
    readonly int cnt, accL, accD;
    readonly long[] key;
    readonly Segments<byte> touched = new();
    public readonly List<int> TouchedList = [];
    /// <summary>(slot, aggregate, value): the values of a `min` or `max` over integers and codes, and of a `countDistinct`. The aggregate is named by its accumulator in the key's row, which no two aggregates share.</summary>
    readonly Table? longs;
    /// <summary>(slot, aggregate, value as a number, its digits): the values of a `min` or `max` over decimals.</summary>
    readonly Table? decimals;
    /// <summary>The entries of one aggregate of one key's row, in each of the two tables.</summary>
    readonly Arrangement? longsOf, decimalsOf;
    /// <summary>Per group of `last` aggregates that take their values from one row: (order, the value of each member, which members have a value), then the digits of every decimal. One entry per distinct row of the group. A decimal is compared by its number (two 8-byte halves) and keeps its digits; a code (a text, an element) takes 4 bytes, any other value 8. The rows of a key are reached only through the key: the key's slot holds the first, each row the next. A change walks them, as finding the winner does.</summary>
    readonly RowSet[] rowSets;
    sealed class RowSet
    {
        public readonly Table T;
        /// <summary>Per slot its first row, per row the next row of its slot; row + 1, 0 is none.</summary>
        public readonly Segments<int> Head = new(), Next = new();
        /// <summary>Per value, the order first and then each member: whether it is a decimal, the column it is compared by, and for a decimal the column of its digits.</summary>
        public readonly bool[] Dec; public readonly int[] At, DigitsAt;
        public readonly int HasAt, Members;
        /// <summary>`widths`: per value, the order first, 16 for a decimal, else 4 or 8 bytes.</summary>
        public RowSet(int[] widths)
        {
            var n = widths.Length; Members = n - 1;
            Dec = [.. widths.Select(w => w == 16)]; At = new int[n]; DigitsAt = new int[n];
            var cols = new List<int>();
            for (var v = 0; v < n; v++) { At[v] = cols.Count; if (Dec[v]) { cols.Add(8); cols.Add(8); } else cols.Add(widths[v]); }
            HasAt = cols.Count; cols.Add(Members <= 31 ? 4 : 8);
            for (var v = 0; v < n; v++) if (Dec[v]) { DigitsAt[v] = cols.Count; cols.Add(8); cols.Add(8); }
            T = new Table([.. cols], keyColumns: 0);
        }
        /// <summary>Value `v` (0 the order, 1 + m member m) of a row.</summary>
        public decimal Get(int row, int v) => Dec[v] ? Unpack(T.Get(row, DigitsAt[v]), T.Get(row, DigitsAt[v] + 1)) : T.Get(row, At[v]);
        public int First(int slot) => Head[slot] - 1;
        /// <summary>Adds `w` to the row of a slot whose compared columns are `row`; a new row is first in its slot's chain. The row, -1 when it went, and whether it is new.</summary>
        public (int Row, bool New) Add(int slot, ReadOnlySpan<long> row, long w)
        {
            int prev = -1, r = First(slot);
            for (; r >= 0; prev = r, r = Next[r] - 1)
            {
                var same = true;
                for (var c = 0; c < row.Length && same; c++) same = T.Get(r, c) == row[c];
                if (same) break;
            }
            if (r < 0)
            {
                r = T.Insert(w);
                for (var c = 0; c < row.Length; c++) T.Set(r, c, row[c]);
                Next[r] = Head[slot]; Head[slot] = r + 1;
                return (r, true);
            }
            var after = Next[r];
            if (T.AddAt(r, w) >= 0) return (r, false);
            if (prev < 0) { if (after == 0) Head.Clear(slot); else Head[slot] = after; } else Next[prev] = after;
            Next.Clear(r);
            return (-1, false);
        }
    }
    /// <summary>Counts the changes of the row sets: what was read from one holds until the next change.</summary>
    public long RowChanges { get; private set; }

    /// <summary>`nIndex`: one more than the greatest index an aggregate's multiset is filed under.</summary>
    /// <summary>`keyWidths`: 4 or 8 bytes per key column. `rowSetWidths`: per row set, per value (the order first, then each member) 16 for a decimal, else 4 or 8 bytes.</summary>
    public KeyTable(int[] keyWidths, int nL, int nD, int nIndex, bool longs, bool decimals, int[][] rowSetWidths)
    {
        rowSets = [.. rowSetWidths.Select(w => new RowSet(w))];
        k = keyWidths.Length; this.nL = nL; this.nD = nD; this.nIndex = nIndex;
        cnt = k; accL = cnt + 1; accD = accL + nL;
        rows = new Table([.. keyWidths, .. Enumerable.Repeat(8, accD + 2 * nD - k)], keyColumns: k);
        key = new long[k];
        if (longs) { this.longs = new Table([4, 4, 8], keyColumns: 3); longsOf = this.longs.Arrange(0, 1); }
        if (decimals) { this.decimals = new Table([4, 4, 8, 8, 8, 8], keyColumns: 4); decimalsOf = this.decimals.Arrange(0, 1); }
    }

    public int Count => rows.Count;
    /// <summary>The live slots, oldest first while none was released.</summary>
    public IEnumerable<int> Slots() => rows.Rows();

    /// <summary>The key of a slot, in a buffer the next call overwrites.</summary>
    public ReadOnlySpan<long> KeyAt(int slot) { for (var i = 0; i < k; i++) key[i] = rows.Get(slot, i); return key; }

    public int FindOrAdd(ReadOnlySpan<long> keyOf) { var slot = rows.Find(keyOf); return slot >= 0 ? slot : rows.Add(keyOf, 1); }

    public long Cnt(int slot) => rows.Get(slot, cnt);
    public void AddCnt(int slot, long w) => rows.Set(slot, cnt, rows.Get(slot, cnt) + w);
    public long L(int slot, int i) => rows.Get(slot, accL + i);
    public void AddL(int slot, int i, long v) => rows.Set(slot, accL + i, rows.Get(slot, accL + i) + v);
    public decimal D(int slot, int i) => Unpack(rows.Get(slot, accD + 2 * i), rows.Get(slot, accD + 2 * i + 1));
    public void AddD(int slot, int i, decimal v) => SetD(slot, i, D(slot, i) + v);

    /// <summary>The digits of a decimal in two 64-bit halves, and back.</summary>
    static (long A, long B) Pack(decimal d) { Span<int> bits = stackalloc int[4]; decimal.GetBits(d, bits); return ((uint)bits[0] | (long)bits[1] << 32, (uint)bits[2] | (long)bits[3] << 32); }
    static decimal Unpack(long a, long b) => new([(int)a, (int)(a >> 32), (int)b, (int)(b >> 32)]);

    /// <summary>Marks a slot touched in this step; true the first time, when the operator retracts what the slot emitted.</summary>
    public bool Touch(int slot) { if (touched.Has(slot)) return false; touched[slot] = 1; TouchedList.Add(slot); return true; }
    public void ClearTouched() { foreach (var s in TouchedList) touched.Clear(s); TouchedList.Clear(); }

    /// <summary>Adds a weight to a value of a multiset; the change of the number of distinct values (+1, -1 or 0).</summary>
    public int AddLong(int slot, int a, long v, long w)
    {
        var had = longs!.Find([slot, a, v]) >= 0;
        var has = longs.Add([slot, a, v], w) >= 0;
        return (has ? 1 : 0) - (had ? 1 : 0);
    }
    /// <summary>Adds a weight to a value of a `min` or `max` over integers and keeps the extreme. A key with one value keeps nothing more: that value is the extreme. A key with more values has the extreme in its row, in accumulator `a`: a new value that beats it takes its place, and only when the extreme itself goes are the values of the key walked for the next one.</summary>
    public void AddExtremeL(int slot, int a, long v, long w, bool desc)
    {
        var change = AddLong(slot, a, v, w);
        if (change == 0) return;
        var values = Values(longsOf!, slot, a);
        if (values < 2) { if (L(slot, a) != 0) rows.Set(slot, accL + a, 0); }
        else if (change > 0 && values > 2) { if (desc ? v > L(slot, a) : v < L(slot, a)) rows.Set(slot, accL + a, v); }
        else if (change > 0 || v == L(slot, a))        // the second value came, or the extreme went
        {
            long best = 0; var any = false;
            for (var r = longsOf!.First([slot, a]); r >= 0; r = longsOf.Next(r))
            {
                var x = longs!.Get(r, 2);
                if (!any || (desc ? x > best : x < best)) { best = x; any = true; }
            }
            rows.Set(slot, accL + a, best);
        }
    }
    /// <summary>The same for a `min` or `max` over decimals: 2.5 and 2.50 are one value, kept as it was first written.</summary>
    public void AddExtremeD(int slot, int a, decimal v, long w, bool desc)
    {
        var (lo, hi) = DecimalKey.Of(v);
        var had = decimals!.Find([slot, a, lo, hi]) >= 0;
        var row = decimals.Add([slot, a, lo, hi], w);
        if (row >= 0 == had) return;
        if (row >= 0) { var (x, y) = Pack(v); decimals.Set(row, 4, x); decimals.Set(row, 5, y); }
        var values = Values(decimalsOf!, slot, a);
        if (values < 2) { if (D(slot, a) != 0) SetD(slot, a, 0); }
        else if (row >= 0 && values > 2) { if (desc ? v > D(slot, a) : v < D(slot, a)) SetD(slot, a, v); }
        else if (row >= 0 || v == D(slot, a))
        {
            decimal best = 0; var any = false;
            for (var r = decimalsOf!.First([slot, a]); r >= 0; r = decimalsOf.Next(r))
            {
                var x = Unpack(decimals.Get(r, 4), decimals.Get(r, 5));
                if (!any || (desc ? x > best : x < best)) { best = x; any = true; }
            }
            SetD(slot, a, best);
        }
    }
    /// <summary>How many values aggregate `a` of a key's row has: 0, 1, 2, or 3 for more.</summary>
    static int Values(Arrangement of, int slot, int a)
    {
        var n = 0;
        for (var r = of.First([slot, a]); r >= 0 && n < 3; r = of.Next(r)) n++;
        return n;
    }
    void SetD(int slot, int i, decimal v) { var (a, b) = Pack(v); rows.Set(slot, accD + 2 * i, a); rows.Set(slot, accD + 2 * i + 1, b); }

    /// <summary>The extreme of a `min` or `max` over integers; false when it has no value. One value is the extreme itself, of more the key's row holds it.</summary>
    public bool ExtremeL(int slot, int a, out long v)
    {
        var first = longsOf!.First([slot, a]);
        v = first < 0 ? 0 : longsOf.Next(first) < 0 ? longs!.Get(first, 2) : L(slot, a);
        return first >= 0;
    }
    public bool ExtremeD(int slot, int a, out decimal v)
    {
        var first = decimalsOf!.First([slot, a]);
        v = first < 0 ? 0 : decimalsOf.Next(first) < 0 ? Unpack(decimals!.Get(first, 4), decimals.Get(first, 5)) : D(slot, a);
        return first >= 0;
    }
    /// <summary>Adds a weight to a row of a row set: its order, the value of each member, and `has`, a bit per member that has a value.</summary>
    public void AddRow(int slot, int set, decimal ord, ReadOnlySpan<decimal> values, long has, long w)
    {
        var rs = rowSets[set]; var t = rs.T;
        Span<long> key = stackalloc long[rs.HasAt + 1];
        key.Clear();
        for (var v = 0; v <= rs.Members; v++)
        {
            if (v > 0 && (has >> (v - 1) & 1) == 0) continue;
            var x = v == 0 ? ord : values[v - 1];
            if (rs.Dec[v]) (key[rs.At[v]], key[rs.At[v] + 1]) = DecimalKey.Of(x); else key[rs.At[v]] = (long)x;
        }
        key[rs.HasAt] = has;
        var (row, added) = rs.Add(slot, key, w);
        RowChanges++;
        if (!added) return;
        for (var v = 0; v <= rs.Members; v++)
        {
            if (!rs.Dec[v]) continue;
            var (x, y) = Pack(v == 0 ? ord : (has >> (v - 1) & 1) != 0 ? values[v - 1] : 0);
            t.Set(row, rs.DigitsAt[v], x); t.Set(row, rs.DigitsAt[v] + 1, y);
        }
    }
    /// <summary>The rows of a row set of one key: the first, and the one after `row`; -1 when there is none.</summary>
    public int FirstRow(int slot, int set) => rowSets[set].First(slot);
    public int NextRow(int set, int row) => rowSets[set].Next[row] - 1;
    public decimal RowOrder(int set, int row) => rowSets[set].Get(row, 0);
    public bool RowHas(int set, int row, int member) => (rowSets[set].T.Get(row, rowSets[set].HasAt) >> member & 1) != 0;
    public decimal RowValue(int set, int row, int member) => rowSets[set].Get(row, 1 + member);

    /// <summary>Whether a slot holds nothing: no rows (so nothing emitted), every accumulator and multiset empty.</summary>
    bool Dead(int s)
    {
        if (Cnt(s) != 0) return false;
        for (var i = 0; i < nL; i++) if (L(s, i) != 0) return false;
        for (var i = 0; i < nD; i++) if (D(s, i) != 0) return false;
        for (var a = 0; a < nIndex; a++)
            if (longsOf?.First([s, a]) >= 0 || decimalsOf?.First([s, a]) >= 0) return false;
        foreach (var rs in rowSets) if (rs.First(s) >= 0) return false;
        return true;
    }

    /// <summary>Gives back the row of a slot that holds nothing (a group whose rows all went, a roll-up window that left the look-back); its number may be given to a new key.</summary>
    public void ReleaseIfDead(int slot) { if (Dead(slot)) rows.Add(KeyAt(slot), -1); }
}

enum AggKind { Count, SumL, SumD, Distinct, ExtL, ExtD, Mean, Last }
sealed record AggSpec(AggKind Kind, int Index, VExpr? Value, bool ValueIsDec, bool Desc = false, int Index2 = -1, VExpr? Order = null, bool OutDec = false, bool ValueIsText = false)
{
    /// <summary>For a `last`: the row set it takes its value from, and its place among the members; -1 for any other aggregate.</summary>
    public int RowSet { get; init; } = -1;
    public int Member { get; init; }
}

/// <summary>Group (and the accumulation half of Rollup): per-key accumulators updated by signed weights (DBSP linear aggregates). Output is a retraction plus an insertion per touched key.</summary>
abstract class AggregatingOp : PhysOp
{
    protected readonly VExpr[] keyExprs; protected readonly AggSpec[] aggs; protected readonly int nL, nD; readonly bool hasLongs, hasDecimals; protected Column?[] orderCols = [];
    /// <summary>The key and value columns of the batch in hand (with `orderCols`): working arrays that the next call overwrites (`Evaluate`).</summary>
    protected Column[] keyCols = []; protected Column?[] valueCols = [];
    protected readonly int[] outKeyCol, outAggCol;
    protected readonly long[] keyBuf;
    readonly Interner it;
    protected readonly DecimalCodes decimals = new();
    /// <summary>The key code of a cell: its 64-bit code, or the code of its number for a Decimal.</summary>
    protected long KeyCode(Column c, int row) => c.IsDec ? decimals.Code(c.D[row]) : c.L[row];

    /// <summary>The (order, value) of a last aggregate: those of the row its row set takes. Null when that row has no value for it or the key has no row.</summary>
    (decimal Ord, decimal Val)? LastOf(KeyTable t, int slot, AggSpec s) =>
        Winner(t, slot, s.RowSet) is >= 0 and var w && t.RowHas(s.RowSet, w, s.Member) ? (t.RowOrder(s.RowSet, w), t.RowValue(s.RowSet, w, s.Member)) : null;

    /// <summary>The aggregates of each row set, in their order: the members whose values one row gives.</summary>
    readonly int[][] rowSets = [];
    /// <summary>How a key table stores the key (4 or 8 bytes per column) and the values of each row set (`KeyTable`).</summary>
    readonly int[] keyWidths; readonly int[][] rowSetWidths = [];
    /// <summary>The bytes a value of this type takes in a key table: 16 for a decimal, 4 for a code (a text, an element, a relationship), 8 for any other.</summary>
    static int WidthOf(TExpr e) => e.Type.Ty.Tag switch { Tag.Decimal => 16, Tag.Text or Tag.Ent or Tag.Rel => 4, _ => 8 };
    (KeyTable? Table, long Changes, int Slot, int Set, int Row) winner;

    /// <summary>The row a row set's members take their values from: the greatest order, on a tie the greatest values in the order of the members, where no value is below every value and a text compares as text. -1 when the key has no row. Remembered until a row set changes, since every member of a key asks.</summary>
    int Winner(KeyTable t, int slot, int set)
    {
        if (winner.Table == t && winner.Changes == t.RowChanges && winner.Slot == slot && winner.Set == set) return winner.Row;
        var best = -1;
        for (var r = t.FirstRow(slot, set); r >= 0; r = t.NextRow(set, r))
            if (best < 0 || Above(t, set, r, best)) best = r;
        winner = (t, t.RowChanges, slot, set, best);
        return best;
    }

    bool Above(KeyTable t, int set, int r, int best)
    {
        var c = t.RowOrder(set, r).CompareTo(t.RowOrder(set, best));
        for (var m = 0; c == 0 && m < rowSets[set].Length; m++)
        {
            var (hr, hb) = (t.RowHas(set, r, m), t.RowHas(set, best, m));
            if (!hr || !hb) { c = hr.CompareTo(hb); continue; }
            var (x, y) = (t.RowValue(set, r, m), t.RowValue(set, best, m));
            c = aggs[rowSets[set][m]].ValueIsText ? string.CompareOrdinal(it.Get((int)x), it.Get((int)y)) : x.CompareTo(y);
        }
        return c > 0;
    }

    /// <summary>Companion columns, when the operator feeds only sinks: per `mean` its sum and count (`{target}#sum`, `{target}#count`), per `last` with a numeric order its order (`{target}#order`). A sink that writes the aggregate to a telemetry property stores the pair, so two windows of one class combine as one (Law 7.5 item 3 (i)).</summary>
    readonly (int Agg, int ColA, int ColB)[] companions = [];

    /// <summary>The names of the companion columns an aggregate has, if any.</summary>
    public static string[] CompanionNames(TAgg a) => a.Fn switch { "mean" => [a.Target + "#sum", a.Target + "#count"], "last" when a.Order?.Type.Ty.Tag is not (Tag.Text or Tag.Ent) => [a.Target + "#order"], _ => [] };

    protected AggregatingOp(TOp t, IReadOnlyList<(string Name, TExpr Value)> keys, IReadOnlyList<TAgg> aggSpecs, Layout inLayout, OpContext cx, string[] outKeyNames, bool telemetryInput, bool withCompanions = false) : base(t)
    {
        if (withCompanions)
        {
            var extra = aggSpecs.SelectMany(CompanionNames).ToArray();
            if (extra.Length > 0)
            {
                var baseL = Layout.Of(t.Out);
                OutLayout = new Layout([.. baseL.Names, .. extra], [.. baseL.IsDec, .. extra.Select(n => !n.EndsWith("#count", StringComparison.Ordinal))], [.. baseL.Interned ?? new bool[baseL.Count], .. extra.Select(_ => false)]);
                companions = [.. aggSpecs.Select((a, i) => (a, i)).Where(x => CompanionNames(x.a).Length > 0).Select(x => (x.i, OutLayout.Index(CompanionNames(x.a)[0]), x.a.Fn == "mean" ? OutLayout.Index(CompanionNames(x.a)[1]) : -1))];
            }
        }
        keyExprs = [.. keys.Select(k => ExprCompiler.Compile(k.Value, inLayout, cx.It, cx.Faults))];
        var list = new List<AggSpec>(); int l = 0, d = 0; var sets = new List<(string Mark, List<int> Members, List<int> Widths)>();
        foreach (var a in aggSpecs)
        {
            var v = a.Value is null ? null : ExprCompiler.Compile(a.Value, inLayout, cx.It, cx.Faults);
            var dec = a.Out.Ty.Tag == Tag.Decimal;
            switch (a.Fn)
            {
                case "count": list.Add(new AggSpec(AggKind.Count, -1, null, false)); break;       // the key's count of rows
                case "countDistinct": list.Add(new AggSpec(AggKind.Distinct, l, v, false, false, l++)); hasLongs = true; break;       // its values are filed under the accumulator that holds their number
                case "sum" when dec: list.Add(new AggSpec(AggKind.SumD, d++, v, true)); break;
                case "sum": list.Add(new AggSpec(AggKind.SumL, l++, v, false)); break;
                case "mean": list.Add(new AggSpec(AggKind.Mean, d++, v, false, false, l++)); break;
                case "last":
                    {
                        var order = ExprCompiler.Compile(a.Order!, inLayout, cx.It, cx.Faults);
                        var set = sets.FindIndex(x => x.Mark == a.Row);
                        if (set < 0) { set = sets.Count; sets.Add((a.Row!, [], [WidthOf(a.Order!)])); }
                        list.Add(new AggSpec(AggKind.Last, dec ? d++ : l++, v, false, false, -1, order, dec, a.Value!.Type.Ty.Tag is Tag.Text or Tag.Ent) { RowSet = set, Member = sets[set].Members.Count });
                        sets[set].Members.Add(list.Count - 1); sets[set].Widths.Add(WidthOf(a.Value!));
                        break;
                    }
                case "min" or "max" when dec: list.Add(new AggSpec(AggKind.ExtD, d++, v, true, a.Fn == "max")); hasDecimals = true; break;       // the accumulator holds the extreme of a key with more than one value
                case "min" or "max": list.Add(new AggSpec(AggKind.ExtL, l++, v, false, a.Fn == "max")); hasLongs = true; break;
                default: throw new NotSupportedException($"aggregate {a.Fn}");
            }
        }
        _ = telemetryInput; it = cx.It;
        aggs = [.. list]; nL = l; nD = d; rowSets = [.. sets.Select(x => x.Members.ToArray())]; rowSetWidths = [.. sets.Select(x => x.Widths.ToArray())];
        keyWidths = [.. keys.Select(k => WidthOf(k.Value) is 8 ? 8 : 4)];      // a decimal key is its number's code
        outKeyCol = [.. outKeyNames.Select(n => OutLayout.Index(n))];
        outAggCol = [.. aggSpecs.Select(a => OutLayout.Index(a.Target))];
        keyBuf = new long[keyExprs.Length + 1];
    }

    /// <summary>The state of a group operator with `k` key columns.</summary>
    protected KeyTable NewKeyTable(int k) => new([.. keyWidths, .. Enumerable.Repeat(8, k - keyWidths.Length)], nL, nD, Math.Max(nL, nD), hasLongs, hasDecimals, rowSetWidths);

    /// <summary>Evaluates the key, value and order columns of a batch into the working arrays.</summary>
    protected void Evaluate(Batch b)
    {
        if (keyCols.Length != keyExprs.Length || valueCols.Length != aggs.Length) { keyCols = new Column[keyExprs.Length]; valueCols = new Column?[aggs.Length]; orderCols = new Column?[aggs.Length]; }
        for (var i = 0; i < keyExprs.Length; i++) keyCols[i] = keyExprs[i].Eval(b);
        for (var a = 0; a < aggs.Length; a++) { valueCols[a] = aggs[a].Value?.Eval(b); orderCols[a] = aggs[a].Order?.Eval(b); }
    }

    protected void Accumulate(KeyTable t, int slot, Batch b, Column?[] valueCols, int row, long w)
    {
        t.AddCnt(slot, w);
        for (var a = 0; a < aggs.Length; a++)
        {
            var s = aggs[a];
            switch (s.Kind)
            {
                case AggKind.SumL: { var c = valueCols[a]!; if (c.P is null || c.P[row]) t.AddL(slot, s.Index, w * c.L[row]); break; }
                case AggKind.SumD: { var c = valueCols[a]!; if (c.P is null || c.P[row]) t.AddD(slot, s.Index, w * (c.IsDec ? c.D[row] : c.L[row])); break; }
                case AggKind.ExtL:
                    {
                        var c = valueCols[a]!;
                        if (c.P is not null && !c.P[row]) break;
                        t.AddExtremeL(slot, s.Index, c.L[row], w, s.Desc);
                        break;
                    }
                case AggKind.ExtD:
                    {
                        var c = valueCols[a]!;
                        if (c.P is not null && !c.P[row]) break;
                        t.AddExtremeD(slot, s.Index, c.IsDec ? c.D[row] : c.L[row], w, s.Desc);
                        break;
                    }
                case AggKind.Mean:
                    {
                        var c = valueCols[a]!;
                        if (c.P is not null && !c.P[row]) break;
                        t.AddD(slot, s.Index, w * (c.IsDec ? c.D[row] : c.L[row]));
                        t.AddL(slot, s.Index2, w);
                        break;
                    }
                case AggKind.Last:
                    {
                        // one entry per input row, added with the first member: the order and every member's value, absent ones marked
                        var o = orderCols[a]!;
                        if (s.Member != 0 || o.P is not null && !o.P[row]) break;
                        var members = rowSets[s.RowSet];
                        Span<decimal> values = stackalloc decimal[members.Length]; var has = 0L;
                        for (var m = 0; m < members.Length; m++)
                        {
                            var mc = valueCols[members[m]]!;
                            if (mc.P is not null && !mc.P[row]) continue;
                            values[m] = mc.IsDec ? mc.D[row] : mc.L[row]; has |= 1L << m;
                        }
                        t.AddRow(slot, s.RowSet, o.IsDec ? o.D[row] : o.L[row], values, has, w);
                        break;
                    }
                case AggKind.Distinct:
                    {
                        var c = valueCols[a]!;
                        if (c.P is not null && !c.P[row]) break;
                        var change = t.AddLong(slot, s.Index, KeyCode(c, row), w);
                        if (change != 0) t.AddL(slot, s.Index2, change);                  // the number of distinct values, kept with the accumulators
                        break;
                    }
            }
        }
        _ = b;
    }

    protected long AggL(KeyTable t, int slot, int a) => aggs[a].Kind switch
    {
        AggKind.Count => t.Cnt(slot),
        AggKind.SumL => t.L(slot, aggs[a].Index),
        AggKind.ExtL => t.ExtremeL(slot, aggs[a].Index, out var v) ? v : 0,
        AggKind.Distinct => t.L(slot, aggs[a].Index2),
        AggKind.Last when !aggs[a].OutDec => LastOf(t, slot, aggs[a]) is { } l ? (long)l.Val : 0,
        _ => 0,
    };
    protected decimal AggD(KeyTable t, int slot, int a) => aggs[a].Kind switch
    {
        AggKind.ExtD => t.ExtremeD(slot, aggs[a].Index, out var v) ? v : 0,
        AggKind.Mean => t.L(slot, aggs[a].Index2) is var n && n != 0 ? t.D(slot, aggs[a].Index) / n : 0,
        AggKind.Last => LastOf(t, slot, aggs[a]) is { } l ? l.Val : 0,
        _ => t.D(slot, aggs[a].Index),
    };
    /// <summary>A min or max over no non-null value is null.</summary>
    protected bool AggAbsent(KeyTable t, int slot, int a) => aggs[a].Kind switch
    {
        AggKind.ExtL => !t.ExtremeL(slot, aggs[a].Index, out _),
        AggKind.ExtD => !t.ExtremeD(slot, aggs[a].Index, out _),
        AggKind.Mean => t.L(slot, aggs[a].Index2) == 0,
        AggKind.Last => LastOf(t, slot, aggs[a]) is null,
        _ => false,
    };

    /// <summary>A row of the key's aggregate as it is now: a retraction is written when a step first touches the key, before its rows change it, so it retracts what was emitted.</summary>
    protected void WriteRow(BatchBuilder bb, KeyTable t, int slot, long weight, long? windowStart, long? windowEnd, int wsCol, int weCol)
    {
        var row = bb.Add(weight);
        var bt = bb.Batch;
        var key = t.KeyAt(slot);
        for (var i = 0; i < outKeyCol.Length; i++) { var kc = bt.Cols[outKeyCol[i]]; if (kc.IsDec) kc.D[row] = decimals.Value(key[i]); else kc.L[row] = key[i]; }
        if (windowStart is { } ws && wsCol >= 0) bt.Cols[wsCol].L[row] = ws;
        if (windowEnd is { } we && weCol >= 0) bt.Cols[weCol].L[row] = we;
        foreach (var (a, ca, cb) in companions)
        {
            var absent = AggAbsent(t, slot, a);
            if (absent) { bt.Cols[ca].SetAbsent(row, bt.N); if (cb >= 0) bt.Cols[cb].SetAbsent(row, bt.N); continue; }
            var (pa, pb) = AuxOf(t, slot, a);
            bt.Cols[ca].D[row] = pa; if (cb >= 0) bt.Cols[cb].L[row] = (long)pb;
        }
        for (var a = 0; a < aggs.Length; a++)
        {
            var col = bt.Cols[outAggCol[a]];
            if (aggs[a].Kind is AggKind.ExtL or AggKind.ExtD or AggKind.Mean or AggKind.Last && AggAbsent(t, slot, a)) { col.SetAbsent(row, bt.N); continue; }
            if (aggs[a].Kind is AggKind.SumD or AggKind.ExtD or AggKind.Mean || aggs[a].Kind == AggKind.Last && aggs[a].OutDec) col.D[row] = AggD(t, slot, a);
            else col.L[row] = AggL(t, slot, a);
        }
    }

    /// <summary>The pair of a `mean` (sum, count) or a `last` (order, unused) of one slot now.</summary>
    (decimal A, decimal B) AuxOf(KeyTable t, int slot, int a) => aggs[a].Kind == AggKind.Mean
        ? (t.D(slot, aggs[a].Index), t.L(slot, aggs[a].Index2))
        : (LastOf(t, slot, aggs[a])!.Value.Ord, 0);
}

/// <summary>Group by keys (no window): retract and insert per touched key at the end of each batch.</summary>
sealed class GroupOp : AggregatingOp
{
    readonly KeyTable table; readonly (string Role, int Col, bool IsRel, string Port)[] members;
    public GroupOp(TGroup t, Layout inLayout, OpContext cx, (string, int, bool, string)[]? members = null, bool withCompanions = false) : base(t, t.Keys, t.Aggs, inLayout, cx, [.. t.Keys.Select(k => k.Name)], false, withCompanions)
    {
        this.members = members ?? [];
        table = NewKeyTable(keyExprs.Length);
        cx.Out.Lineage.RegisterGroup(Name, t.Keys.Count, this.members);
        if (t.ClosesWindows) throw new InvalidOperationException("windowed group uses WindowedGroupOp");
    }
    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var b = In(inputs, "in"); if (b is null || b.Rows == 0) return null;
        Evaluate(b);
        var bb = EmptyOutput();
        for (var k = 0; k < b.Rows; k++)
        {
            var row = b.RowAt(k);
            var ok = true;
            for (var i = 0; i < keyCols.Length; i++) { if (keyCols[i].P is not null && !keyCols[i].P![row]) { ok = false; break; } keyBuf[i] = KeyCode(keyCols[i], row); }
            if (!ok) continue;
            var slot = table.FindOrAdd(keyBuf.AsSpan(0, keyCols.Length));
            if (table.Touch(slot) && table.Cnt(slot) > 0) WriteRow(bb, table, slot, -1, null, null, -1, -1);       // a key emitted what it had at the end of the last step: a row iff its count was positive
            Accumulate(table, slot, b, valueCols, row, b.Weight(row));
            if (members.Length > 0) cx.Out.Lineage.Member(Name, keyBuf.AsSpan(0, keyCols.Length), b, row, b.Weight(row));
        }
        foreach (var slot in table.TouchedList) if (table.Cnt(slot) > 0) WriteRow(bb, table, slot, 1, null, null, -1, -1);
        foreach (var slot in table.TouchedList) table.ReleaseIfDead(slot);       // groups whose rows all went give their rows back
        table.ClearTouched();
        return bb.Build();
    }
}

/// <summary>Group over a Window whose key includes windowStart: windows close when the logical frontier passes windowEnd (research 03 section 10.1). Closed windows emit once; a late row revises its closed window.</summary>
sealed class WindowedGroupOp : AggregatingOp
{
    sealed class W(KeyTable t, long end) { public KeyTable Table = t; public long End = end; public bool Closed; }
    readonly SortedDictionary<long, W> windows = [];
    readonly long width; readonly int wsKey, outWs;
    readonly int[] keyOutNoWs;
    long frontier = long.MinValue;
    readonly int nKeys;
    readonly List<(W Win, long Ws)> revised = [];
    readonly string name;

    public WindowedGroupOp(TGroup t, long width, Layout inLayout, OpContext cx, bool withCompanions = false)
        : base(t, t.Keys.Where(k => !(k.Name == "windowStart")).ToList(), t.Aggs, inLayout, cx, [.. t.Keys.Where(k => k.Name != "windowStart").Select(k => k.Name)], false, withCompanions)
    {
        this.width = width; name = t.Name;
        nKeys = keyExprs.Length;
        wsKey = inLayout.Index("windowStart");
        outWs = OutLayout.Index("windowStart");
        keyOutNoWs = [];
    }

    KeyTable NewTable() => NewKeyTable(nKeys);
    /// <summary>A loop, not a lambda: a lambda over the current window would make its closure on every call.</summary>
    bool Revising(W win) { foreach (var r in revised) if (r.Win == win) return true; return false; }

    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var bb = EmptyOutput();
        var b = In(inputs, "in");
        if (b is not null && b.Rows > 0)
        {
            Evaluate(b);
            var wsCol = b.Cols[wsKey].L;
            W? cur = null; long curWs = long.MinValue;
            for (var k = 0; k < b.Rows; k++)
            {
                var row = b.RowAt(k);
                var ws = wsCol[row];
                if (cur is null || ws != curWs)
                {
                    if (!windows.TryGetValue(ws, out cur))
                    {
                        // beyond the allowed lateness the window's state is gone: a row for it is dropped, never a new window that would replace what was emitted
                        if (cx.PastRevision(ws + width, width, frontier)) { cx.LateDropped++; cur = null; curWs = long.MinValue; continue; }
                        cur = new W(NewTable(), ws + width); windows[ws] = cur; if (cur.End <= frontier) cur.Closed = true;
                    }
                    curWs = ws;
                }
                var ok = true;
                for (var i = 0; i < nKeys; i++) { if (keyCols[i].P is not null && !keyCols[i].P![row]) { ok = false; break; } keyBuf[i] = KeyCode(keyCols[i], row); }
                if (!ok) continue;
                var slot = cur.Table.FindOrAdd(keyBuf.AsSpan(0, nKeys));
                if (cur.Closed)
                {
                    // a revision of a closed window: what the key emitted is retracted before the row changes it; a key of a closed window has emitted a row iff its count is positive
                    if (cur.Table.Touch(slot) && cur.Table.Cnt(slot) > 0) WriteRow(bb, cur.Table, slot, -1, ws, ws + width, outWs, OutLayout.Has("windowEnd") ? OutLayout.Index("windowEnd") : -1);
                    if (!Revising(cur)) revised.Add((cur, ws));
                }
                Accumulate(cur.Table, slot, b, valueCols, row, b.Weight(row));
            }
        }
        // revisions of closed windows: insert the new aggregate
        foreach (var (win, ws) in revised)
        {
            var t = win.Table;
            foreach (var slot in t.TouchedList) if (t.Cnt(slot) > 0) WriteRow(bb, t, slot, 1, ws, ws + width, outWs, -1);
            t.ClearTouched();
        }
        revised.Clear();
        if (ep.HasAdvance && ep.Lambda > frontier)
        {
            frontier = ep.Lambda;
            foreach (var (ws, win) in windows)
            {
                if (win.End > frontier) break;
                if (win.Closed) continue;
                win.Closed = true;
                var t = win.Table;
                foreach (var slot in t.Slots())
                    if (t.Cnt(slot) > 0) WriteRow(bb, t, slot, 1, ws, ws + width, outWs, -1);
            }
            foreach (var ws in windows.Where(w => cx.PastRevision(w.Value.End, width, frontier)).Select(w => w.Key).ToList())
            {
                windows.Remove(ws);
            }
        }
        return bb.Build();
    }
}

/// <summary>Rollup: sums of telemetry rows per key and window. Inputs are already closed windows, so every change is a retraction plus an insertion.</summary>
sealed class RollupOp : AggregatingOp
{
    readonly KeyTable table; readonly int inWs, outWs, outWe; readonly long period;
    readonly List<long> slotWindow = [];

    // ---- no data (Def. 7.16) ----
    /// <summary>Per key (without the window) of a roll-up over a Series with a time to live: the marker records of its members by window, per member the weight of its normal rows and of its marker records, and the window of the marker record the key has in the output. A member is a row of the input without its window and values (`TRollup.Members`).</summary>
    sealed class Silence { public long[] Key = []; public readonly SortedDictionary<long, long> Markers = []; public readonly Dictionary<string, (long Normal, long Marked)> Members = []; public long? Emitted; }
    readonly Dictionary<string, Silence>? silence; readonly int inNoData = -1, outNoData = -1; readonly int[] memberCols = [];

    /// <summary>The key of a roll-up over a Series with a time to live is in no data iff every member that has a row has a marker record.</summary>
    public RollupOp(TRollup t, Layout inLayout, OpContext cx, bool withCompanions = false) : base(t, t.Keys, t.Aggs, inLayout, cx, [.. t.Keys.Select(k => k.Name)], true, withCompanions)
    {
        table = NewKeyTable(keyExprs.Length + 1);
        inWs = inLayout.Index("windowStart"); outWs = OutLayout.Index("windowStart"); outWe = OutLayout.Index("windowEnd"); period = t.PeriodNs;
        if (t.NoData) { silence = []; inNoData = inLayout.Index(TThreshold.NoDataColumn); outNoData = OutLayout.Index(TThreshold.NoDataColumn); memberCols = [.. t.Members!.Select(inLayout.Index)]; }
    }

    string Member(Batch b, int row) => string.Join(',', memberCols.Select(c => !b.Cols[c].Present(row) ? "" : b.Cols[c].IsDec ? "d" + decimals.Code(b.Cols[c].D[row]) : b.Cols[c].L[row].ToString(System.Globalization.CultureInfo.InvariantCulture)));

    static void Count(SortedDictionary<long, long> counts, long window, long weight)
    {
        var n = counts.GetValueOrDefault(window) + weight;
        if (n == 0) counts.Remove(window); else counts[window] = n;
    }

    void WriteMarker(BatchBuilder bb, Silence s, long ws, long weight)
    {
        var row = bb.Add(weight); var bt = bb.Batch;
        for (var i = 0; i < outKeyCol.Length; i++) { var kc = bt.Cols[outKeyCol[i]]; if (kc.IsDec) kc.D[row] = decimals.Value(s.Key[i]); else kc.L[row] = s.Key[i]; }
        bt.Cols[outWs].L[row] = ws; bt.Cols[outWe].L[row] = ws + period;
        foreach (var a in outAggCol) bt.Cols[a].SetAbsent(row, bt.N);
        bt.Cols[outNoData].L[row] = 1;
    }

    bool Suppressed(int slot, int n) => silence is not null && silence.TryGetValue(string.Join(',', table.KeyAt(slot)[..n].ToArray()), out var s) && s.Emitted == table.KeyAt(slot)[n];

    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var b = In(inputs, "in"); if (b is null || b.Rows == 0) return null;
        var quiet = silence is null ? null : new List<Silence>();
        Evaluate(b);
        var wsCol = b.Cols[inWs].L;
        var n = keyCols.Length;
        var bb = EmptyOutput();
        for (var k = 0; k < b.Rows; k++)
        {
            var row = b.RowAt(k);
            var ok = true;
            for (var i = 0; i < n; i++) { if (keyCols[i].P is not null && !keyCols[i].P![row]) { ok = false; break; } keyBuf[i] = KeyCode(keyCols[i], row); }
            if (!ok) continue;
            keyBuf[n] = wsCol[row];
            Silence? s = null;
            if (silence is not null)
            {
                var id = string.Join(',', keyBuf[..n]);
                if (!silence.TryGetValue(id, out s)) silence[id] = s = new Silence { Key = keyBuf[..n] };
                quiet!.Add(s);
                var member = Member(b, row); var marked = b.Cols[inNoData].L[row] != 0;
                var had = s.Members.GetValueOrDefault(member);
                var has = marked ? (had.Normal, had.Marked + b.Weight(row)) : (had.Normal + b.Weight(row), had.Marked);
                if (has == (0, 0)) s.Members.Remove(member); else s.Members[member] = has;
                if (marked) { Count(s.Markers, wsCol[row], b.Weight(row)); continue; }       // the record of a member in no data is not aggregated
            }
            Retract(bb, table.FindOrAdd(keyBuf.AsSpan(0, n + 1)), n, out var slot);
            Accumulate(table, slot, b, valueCols, row, b.Weight(row));
        }
        if (quiet is not null)
            foreach (var s in quiet.Distinct())
            {
                var marked = s.Markers.Where(m => m.Value > 0).Select(m => (long?)m.Key).LastOrDefault();
                var reporting = s.Members.Values.Any(m => m.Normal > 0 && m.Marked <= 0);       // a member with rows that is not in no data
                var want = marked is not null && !reporting ? marked : null;
                if (want != s.Emitted)
                {
                    // the marker record takes the place of the key's aggregate at its window: that slot is written again without or with it
                    foreach (var at in new[] { s.Emitted, want }) if (at is { } ws) { s.Key.CopyTo(keyBuf, 0); keyBuf[n] = ws; Retract(bb, table.FindOrAdd(keyBuf.AsSpan(0, n + 1)), n, out _); }
                    if (s.Emitted is { } old) WriteMarker(bb, s, old, -1);
                    if (want is { } now) WriteMarker(bb, s, now, 1);
                    s.Emitted = want;
                }
                if (s.Markers.Count == 0 && s.Members.Count == 0 && s.Emitted is null) silence!.Remove(string.Join(',', s.Key));
            }
        foreach (var slot in table.TouchedList)
        {
            var ws = table.KeyAt(slot)[n];
            if (Emitted(slot, n)) WriteRow(bb, table, slot, 1, ws, ws + period, outWs, outWe);
        }
        foreach (var slot in table.TouchedList) table.ReleaseIfDead(slot);       // windows that left the look-back give their rows back
        table.ClearTouched();
        return bb.Build();
    }

    /// <summary>Whether a slot has a row in the output: it has rows and no marker record takes its place. Read before a step changes either, it is what the slot emitted.</summary>
    bool Emitted(int slot, int n) => table.Cnt(slot) > 0 && !Suppressed(slot, n);

    /// <summary>The first time a step touches a slot, the aggregate it emitted is retracted, before the step changes it.</summary>
    void Retract(BatchBuilder bb, int slot, int n, out int touched)
    {
        touched = slot;
        if (!table.Touch(slot) || !Emitted(slot, n)) return;
        var ws = table.KeyAt(slot)[n];
        WriteRow(bb, table, slot, -1, ws, ws + period, outWs, outWe);
    }
}

// ---- hysteresis ------------------------------------------------------------------------------------------

/// <summary>Threshold: the only operator whose data output depends on telemetry. Per key, a state machine over the ordered closed windows; the output row changes only on a state change.</summary>
sealed class ThresholdOp : PhysOp
{
    /// <summary>Per key with a window in the look-back: those windows, and one carried state: the state machine after the newest window that left the look-back (`State`, `Run`, `Last` its start), so a state never changes because a window grew old (Def. 7.10, D33).</summary>
    sealed class KeyState { public readonly SortedList<long, (decimal V, int P)> Windows = []; public string? Emitted; public string Id = ""; public long[] Key = []; public string State = ""; public long Run; public long Last = long.MinValue;
        /// <summary>The marker records the key has (Def. 7.16): while there is one, the key is in no data.</summary>
        public long Silent; }
    /// <summary>The parameters of one window: the constants of the statement, or what the row of the window gives. Rules are few and windows many, so a window holds an index into `parameters`.</summary>
    readonly record struct Params(decimal Above, decimal Below, long Enter, long Exit);
    readonly List<Params> parameters = []; readonly Dictionary<Params, int> parameterIndex = [];
    int IndexOf(Params p) { if (!parameterIndex.TryGetValue(p, out var i)) { parameterIndex[p] = i = parameters.Count; parameters.Add(p); } return i; }
    readonly TThreshold t; readonly VExpr[] keys; readonly VExpr value; readonly int inWs; readonly int[] outKey; readonly int outStatus; readonly int inNoData = -1;
    readonly VExpr? aboveBy, enterBy, belowBy, exitBy;
    readonly Dictionary<string, KeyState> states = [];
    readonly long[] buf;
    readonly long? lookBack; readonly long horizon; long frontier = long.MinValue;
    readonly DecimalCodes decimals = new();
    /// <summary>Keys without a window in the look-back, by the frontier at which their carried state is dropped: a late window inside the horizon may still continue their episode.</summary>
    readonly SortedSet<(long At, string Id)> idle = [];
    /// <summary>`series`: the Series this operator reads through, if any. Windows older than the frontier minus its `within` leave the input as retractions; here they join the carried state instead.</summary>
    public ThresholdOp(TThreshold t, Layout inLayout, OpContext cx, TSeries? series = null) : base(t)
    {
        this.t = t; lookBack = series?.WithinNs; horizon = series is null ? 0 : cx.HorizonOf(series.PeriodNs);
        keys = [.. t.Keys.Select(k => ExprCompiler.Compile(k.Value, inLayout, cx.It, cx.Faults))];
        value = ExprCompiler.Compile(t.Value, inLayout, cx.It, cx.Faults);
        inWs = inLayout.Index("windowStart");
        if (t.ReadsNoData) inNoData = inLayout.Index(TThreshold.NoDataColumn);
        VExpr? P(TExpr? e) => e is null ? null : ExprCompiler.Compile(e, inLayout, cx.It, cx.Faults);
        aboveBy = P(t.AboveBy); enterBy = P(t.EnterBy); belowBy = P(t.BelowBy); exitBy = P(t.ExitBy);
        outKey = [.. t.Keys.Select(k => OutLayout.Index(k.Name))]; outStatus = OutLayout.Index(t.Target);
        buf = new long[keys.Length];
    }

    /// <summary>One window of the state machine: a run of `Enter` windows at or above `Above` switches to high, a run of `Exit` windows at or below `Below` switches to low; the run restarts at a switch and at a window that does not extend it. A window that starts `within` or more after the one before begins a new episode in `initial`.</summary>
    void Step(ref string state, ref long run, ref long last, long start, (decimal V, int P) window)
    {
        if (last != long.MinValue && lookBack is { } within && start - last >= within) { state = t.Initial; run = 0; }
        last = start;
        var p = parameters[window.P];
        var low = state == t.Low;
        var extends = low ? window.V >= p.Above : window.V <= p.Below;
        run = extends ? run + 1 : 0;
        if (extends && run >= (low ? p.Enter : p.Exit)) { state = low ? t.High : t.Low; run = 0; }
    }

    /// <summary>The frontier from which no accepted window can start less than `within` after the last one of the key.</summary>
    long DropAt(KeyState st) => st.Last == long.MinValue || lookBack is not { } within ? long.MinValue : OpContext.Clamp((Int128)st.Last + within + horizon);

    string Run(KeyState st)
    {
        var state = st.State; var run = st.Run; var last = st.Last;
        var (starts, windows) = (st.Windows.Keys, st.Windows.Values);
        for (var i = 0; i < starts.Count; i++) Step(ref state, ref run, ref last, starts[i], windows[i]);
        return state;
    }

    /// <summary>The state of a key, found by its text (the key's numbers joined by commas, as `string.Join` writes them) without making the text; made with its text when absent.</summary>
    KeyState StateOf(Dictionary<string, KeyState>.AlternateLookup<ReadOnlySpan<char>> byText, Span<char> chars)
    {
        var n = 0;
        for (var i = 0; i < buf.Length; i++)
        {
            if (i > 0) chars[n++] = ',';
            buf[i].TryFormat(chars[n..], out var w); n += w;
        }
        if (byText.TryGetValue(chars[..n], out var st)) return st;
        var id = new string(chars[..n]);
        return states[id] = new KeyState { Id = id, Key = [.. buf], State = t.Initial };
    }

    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        if (ep.HasAdvance && ep.Lambda > frontier) frontier = ep.Lambda;
        var edge = lookBack is { } within && frontier != long.MinValue ? frontier - within : long.MinValue;
        while (idle.Count > 0 && idle.Min is var due && due.At <= frontier)
        {
            idle.Remove(due);
            if (states.TryGetValue(due.Id, out var gone) && gone.Windows.Count == 0 && gone.Silent == 0 && DropAt(gone) <= frontier) states.Remove(due.Id);
        }
        var b = In(inputs, "in"); if (b is null || b.Rows == 0) return null;
        var kc = keys.Select(k => k.Eval(b)).ToArray(); var vc = value.Eval(b);
        Column? ac = aboveBy?.Eval(b), ec = enterBy?.Eval(b), bc = belowBy?.Eval(b), xc = exitBy?.Eval(b);
        static decimal Num(Column c, int row) => c.IsDec ? c.D[row] : c.L[row];
        var touched = new List<KeyState>(); var seen = new HashSet<KeyState>();
        var byText = states.GetAlternateLookup<ReadOnlySpan<char>>(); Span<char> chars = stackalloc char[21 * buf.Length];
        for (var k = 0; k < b.Rows; k++)
        {
            var row = b.RowAt(k);
            var silent = inNoData >= 0 && b.Cols[inNoData].L[row] != 0;       // the record of a key in no data: it has no value and is no window of the episode
            var ok = silent || vc.P is null || vc.P[row];
            for (var i = 0; i < kc.Length && ok; i++) { if (kc[i].P is not null && !kc[i].P![row]) ok = false; else buf[i] = kc[i].IsDec ? decimals.Code(kc[i].D[row]) : kc[i].L[row]; }
            if (!ok) continue;
            if (silent)
            {
                if (t.NoData is null) continue;       // `nodata ignore`
                var quiet = StateOf(byText, chars);
                quiet.Silent += b.Weight(row);
                if (seen.Add(quiet)) touched.Add(quiet);
                continue;
            }
            // thresholds read from data: each window is read with the parameters of its own row. The checker cannot see them: a row whose parameters are no state machine is dropped and counted
            var p = new Params(ac is not null && (ac.P is null || ac.P[row]) ? Num(ac, row) : t.Above, bc is not null && (bc.P is null || bc.P[row]) ? Num(bc, row) : t.Below,
                ec is not null && (ec.P is null || ec.P[row]) ? (long)Num(ec, row) : t.EnterAfter, xc is not null && (xc.P is null || xc.P[row]) ? (long)Num(xc, row) : t.ExitAfter);
            if (p.Above <= p.Below || p.Enter < 1 || p.Exit < 1) { if (b.Weight(row) > 0) cx.Faults.InvalidParameter++; continue; }
            var st = StateOf(byText, chars);
            var ws = b.Cols[inWs].L[row]; var window = (V: vc.IsDec ? vc.D[row] : vc.L[row], P: IndexOf(p));
            if (ws < edge) { if (b.Weight(row) > 0) cx.LateDropped++; }     // behind the look-back: a late row is dropped and counted, and the row that withdraws a window because it grew old withdraws nothing here
            else if (b.Weight(row) > 0) st.Windows[ws] = window;
            else if (st.Windows.TryGetValue(ws, out var cur) && cur == window) st.Windows.Remove(ws);
            if (seen.Add(st)) touched.Add(st);
        }
        var bb = EmptyOutput();
        foreach (var st in touched)
        {
            while (st.Windows.Count > 0 && st.Windows.Keys[0] < edge) { Step(ref st.State, ref st.Run, ref st.Last, st.Windows.Keys[0], st.Windows.Values[0]); st.Windows.RemoveAt(0); }
            var next = st.Windows.Count == 0 ? null : Run(st);
            if (next is null) idle.Add((DropAt(st), st.Id));     // no window in the look-back: the row goes; the carried state stays until no late window can continue the episode
            if (st.Silent > 0) next = t.NoData;                  // no data is an output over the state machine, which goes on as the episode rule says
            if (next == st.Emitted) continue;
            if (st.Emitted is not null) Write(bb, st, st.Emitted, -1, cx);
            if (next is not null) Write(bb, st, next, 1, cx);
            st.Emitted = next;
        }
        return bb.Build();
    }

    void Write(BatchBuilder bb, KeyState st, string status, long w, OpContext cx)
    {
        var row = bb.Add(w);
        for (var i = 0; i < outKey.Length; i++) { var kc = bb.Batch.Cols[outKey[i]]; if (kc.IsDec) kc.D[row] = decimals.Value(st.Key[i]); else kc.L[row] = st.Key[i]; }
        bb.Batch.Cols[outStatus].L[row] = cx.It.Intern(status);
    }
}

// ---- set operators ---------------------------------------------------------------------------------------

/// <summary>Union: the signed rows of both inputs one after the other (a Z-set sum). Linear, no state. Each column is taken from the input's column of the same name: an input may carry columns the union leaves behind (the properties read from an element before the union).</summary>
sealed class UnionOp : PhysOp
{
    readonly int[][] from;
    public UnionOp(TUnion t, Layout in0, Layout in1) : base(t) => from = [.. new[] { in0, in1 }.Select(l => OutLayout.Names.Select(l.Index).ToArray())];

    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var bb = EmptyOutput();
        for (var p = 0; p < 2; p++)
        {
            var b = In(inputs, p == 0 ? "in0" : "in1"); if (b is null || b.Rows == 0) continue;
            for (var k = 0; k < b.Rows; k++)
            {
                var src = b.RowAt(k); var row = bb.Add(b.Weight(src));
                for (var c = 0; c < OutLayout.Count; c++)
                {
                    var col = b.Cols[from[p][c]]; var dst = bb.Batch.Cols[c];
                    if (!col.Present(src)) { bb.Absent(row, c); continue; }
                    if (dst.IsDec) dst.D[row] = col.D[src]; else dst.L[row] = col.L[src];
                }
            }
        }
        return bb.Build();
    }
}

/// <summary>Distinct: a row is present while its multiplicity is positive. Counts per whole row; a transition from absent to present emits the row once, from present to absent retracts it once.</summary>
/// <summary>Decimals as key codes: equal numbers get one code whatever their scale, so a Decimal can be a group, roll-up or threshold key and the argument of countDistinct in tables keyed by 64-bit codes.</summary>
sealed class DecimalCodes
{
    readonly Dictionary<decimal, long> codes = []; readonly List<decimal> values = [];
    public long Code(decimal d) { if (!codes.TryGetValue(d, out var c)) { codes[d] = c = values.Count; values.Add(d); } return c; }
    public decimal Value(long code) => values[(int)code];
}

/// <summary>The two halves of a decimal as a key: equal numbers give equal halves whatever their scale (2.5 and 2.50).</summary>
static class DecimalKey
{
    public static (long Lo, long Hi) Of(decimal d)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(d / 1.0000000000000000000000000000m, bits);      // dividing by one at full scale drops trailing zeros
        return (((long)bits[0] << 32) | (uint)bits[1], ((long)bits[2] << 32) | (uint)bits[3]);
    }
}

/// <summary>Distinct: a row is in the output while its multiplicity is positive. The multiplicities are a Z-set (`Table`) whose key is the whole row: every column as its 8-byte code (a decimal as the two halves of its number, so 2.5 and 2.50 are one row) and a bit per absent column.</summary>
sealed class DistinctOp : PhysOp
{
    readonly int n; readonly int[] at; readonly Table counts;
    public DistinctOp(TDistinct t, Layout inL) : base(t)
    {
        n = inL.Count; at = new int[n + 1];
        for (var c = 0; c < n; c++) at[c + 1] = at[c] + (inL.IsDec[c] ? 2 : 1);
        var width = at[n] + ((n + 63) >> 6);
        counts = new Table([.. Enumerable.Repeat(8, width)], keyColumns: width);
    }

    /// <summary>The stored form of a batch row: each column at its place, then the absent bits.</summary>
    void Encode(Batch b, int row, Span<long> key)
    {
        key.Clear();
        for (var c = 0; c < n; c++)
        {
            var col = b.Cols[c];
            if (!col.Present(row)) key[at[n] + (c >> 6)] |= 1L << (c & 63);
            else if (col.IsDec) (key[at[c]], key[at[c] + 1]) = DecimalKey.Of(col.D[row]);
            else key[at[c]] = col.L[row];
        }
    }

    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var b = In(inputs, "in"); if (b is null || b.Rows == 0) return null;
        // decide per row in order: a row crossing zero is emitted at that moment, so batch-internal cancellation is visible
        var bb = EmptyOutput();
        Span<long> key = stackalloc long[counts.Columns];
        for (var k = 0; k < b.Rows; k++)
        {
            var src = b.RowAt(k);
            Encode(b, src, key);
            var before = counts.Weight(counts.Find(key)); var after = before + b.Weight(src);
            counts.Add(key, b.Weight(src));
            var was = before > 0; var now = after > 0;
            if (was == now) continue;
            var row = bb.Add(now ? 1 : -1);
            for (var c = 0; c < n; c++)
            {
                var col = b.Cols[c]; var dst = bb.Batch.Cols[c];
                if (!col.Present(src)) { bb.Absent(row, c); continue; }
                if (dst.IsDec) dst.D[row] = col.D[src]; else dst.L[row] = col.L[src];
            }
        }
        return bb.Build();
    }
}

// ---- join ------------------------------------------------------------------------------------------------

/// <summary>Equi-join of two data inputs. Each side keeps its input's rows as a Z-set (`Table`): a row is its join key, every column as its code (a decimal as its number in the graph's `DecimalCodes`) and a bit per absent column, all of it the key of the row, with the multiplicity as its weight; an `Arrangement` by the join key gives the rows a probe matches. The joins of a graph that read one input with keys that are its columns keep its rows once (`JoinInputs`). A step joins a left batch with the right rows before the step, a right batch with the left rows before the step, and the two batches with each other; then the last reader of each input in plan order adds the input's batch. Rows with an absent key component never join and are not kept.</summary>
sealed class JoinOp : PhysOp
{
    /// <summary>The rows of one side as a Z-set (`Table`), arranged by the join key. A stored row is the join key's parts that are not a column of the row, then every column, then a bit per absent number in words of 31 bits; a part of the key that is a column is read from that column. A column of codes (a text, an element, a decimal as its number in `DecimalCodes`) takes 4 bytes and is -1 when absent; a number (an integer, an instant, a boolean) takes 8 bytes and its absence is a bit. A side whose key parts are all columns stores just the input's rows, and the table is the input's, shared with the other joins of the graph that read it.</summary>
    internal sealed class Side
    {
        /// <summary>The join key's expressions, which of them compare as decimals, and the number of columns of a row.</summary>
        public readonly VExpr[] Keys; public readonly bool[] KeyDec; public readonly int N;
        /// <summary>Per part of the key: the stored column that holds it, and whether it is a column of the row (then nothing else holds it).</summary>
        public readonly int[] KeyAt; public readonly bool[] KeyIsCol;
        /// <summary>Per column: its absent bit, or -1 for a column of codes.</summary>
        public readonly int[] Bit;
        /// <summary>Where the row's columns and the absent bits start.</summary>
        public readonly int ColsAt, BitsAt;
        public readonly Table Rows; public readonly Arrangement ByKey;
        /// <summary>This side adds its input's batch to `Rows`: it is the last reader of the table in plan order.</summary>
        public bool Adds;
        /// <summary>The key of the batch in hand, evaluated once per call.</summary>
        public readonly Column[] KeyCols;
        public Side(Layout layout, VExpr[] keys, bool[] keyDec, int[] keyField, bool[] keyCode, Src input, JoinInputs inputs)
        {
            Keys = keys; KeyDec = keyDec; N = layout.Count; KeyCols = new Column[keys.Length];
            var widths = new List<int>();
            KeyAt = new int[keys.Length]; KeyIsCol = new bool[keys.Length];
            for (var i = 0; i < keys.Length; i++) if (keyField[i] < 0) { KeyAt[i] = widths.Count; widths.Add(keyDec[i] || keyCode[i] ? 4 : 8); }
            ColsAt = widths.Count;
            Bit = new int[N]; var bits = 0;
            for (var c = 0; c < N; c++)
            {
                var code = layout.IsDec[c] || layout.Interned?[c] == true;
                widths.Add(code ? 4 : 8); Bit[c] = code ? -1 : bits++;
            }
            BitsAt = widths.Count;
            for (var w = 0; w < Words(bits); w++) widths.Add(4);
            for (var i = 0; i < keys.Length; i++) if (keyField[i] >= 0) { KeyAt[i] = ColsAt + keyField[i]; KeyIsCol[i] = true; }
            Rows = inputs.Rows(this, ColsAt == 0 ? input : null, [.. widths]);
            ByKey = Rows.Arrange(KeyAt);
        }

        public bool Absent(int r, int c) => Bit[c] < 0 ? Rows.Get(r, ColsAt + c) < 0 : (Rows.Get(r, BitsAt + Bit[c] / 31) >> (Bit[c] % 31) & 1) != 0;
    }
    static int Words(int n) => (n + 30) / 31;
    readonly Side left, right;
    readonly DecimalCodes decimals;
    // working state, empty between calls: the right batch's rows by the hash of their key, to join the two batches
    readonly Dictionary<ulong, int> crossHead = [];
    readonly List<int> crossNext = [];
    readonly List<long> crossKeys = [];

    public JoinOp(TJoin t, Layout lL, Layout rL, OpContext cx, JoinInputs inputs) : base(t)
    {
        VExpr[] C(IEnumerable<TExpr> es, Layout l) => [.. es.Select(e => ExprCompiler.Compile(e, l, cx.It, cx.Faults))];
        static int[] Fields(IEnumerable<TExpr> es, Layout l) => [.. es.Select(e => e is TField f ? l.Index(f.Name) : -1)];
        static bool[] Codes(IEnumerable<TExpr> es) => [.. es.Select(e => e.Type.Ty.Tag is Tag.Text or Tag.Ent or Tag.Rel)];
        var ls = t.Keys.Select(k => k.Left).ToList(); var rs = t.Keys.Select(k => k.Right).ToList();
        decimals = inputs.Decimals;
        left = new Side(lL, C(ls, lL), [.. ls.Select(e => e.Type.Ty.Tag == Tag.Decimal)], Fields(ls, lL), Codes(ls), t.Inputs["left"], inputs);
        right = new Side(rL, C(rs, rL), [.. rs.Select(e => e.Type.Ty.Tag == Tag.Decimal)], Fields(rs, rL), Codes(rs), t.Inputs["right"], inputs);
    }

    /// <summary>The join key of a batch row (`key`). False when a part of it is absent: such a row never joins. `s.KeyCols` holds the key evaluated over the batch.</summary>
    bool Key(Side s, int row, Span<long> key)
    {
        for (var i = 0; i < s.Keys.Length; i++)
        {
            var c = s.KeyCols[i];
            if (!c.Present(row)) return false;
            key[i] = s.KeyDec[i] || c.IsDec ? decimals.Code(c.IsDec ? c.D[row] : c.L[row]) : c.L[row];
        }
        return true;
    }

    /// <summary>The stored form of a batch row whose key is `key`.</summary>
    void Encode(Side s, Batch b, int row, ReadOnlySpan<long> key, Span<long> stored)
    {
        for (var i = 0; i < s.Keys.Length; i++) if (!s.KeyIsCol[i]) stored[s.KeyAt[i]] = key[i];
        stored[s.BitsAt..].Clear();
        for (var c = 0; c < s.N; c++)
        {
            var col = b.Cols[c];
            if (!col.Present(row)) { var bit = s.Bit[c]; if (bit < 0) stored[s.ColsAt + c] = -1; else { stored[s.ColsAt + c] = 0; stored[s.BitsAt + bit / 31] |= 1L << (bit % 31); } }
            else stored[s.ColsAt + c] = col.IsDec ? decimals.Code(col.D[row]) : col.L[row];
        }
    }

    /// <summary>The columns of a batch row into output row `o`, from column `at` on.</summary>
    static void Copy(BatchBuilder bb, int o, Side s, Batch b, int row, int at)
    {
        var cols = bb.Batch.Cols;
        for (var c = 0; c < s.N; c++)
        {
            var src = b.Cols[c]; var dst = cols[at + c];
            if (!src.Present(row)) { bb.Absent(o, at + c); continue; }
            if (dst.IsDec) dst.D[o] = src.D[row]; else dst.L[o] = src.L[row];
        }
    }

    /// <summary>One output row: the batch row of `self` and the stored row `r` of `other`, left columns first.</summary>
    void Emit(BatchBuilder bb, Side self, Batch b, int row, Side other, int r, bool selfIsLeft, long weight)
    {
        var o = bb.Add(weight); var cols = bb.Batch.Cols;
        var (mine, theirs) = selfIsLeft ? (0, self.N) : (other.N, 0);
        Copy(bb, o, self, b, row, mine);
        var t = other.Rows;
        for (var c = 0; c < other.N; c++)
        {
            var dst = cols[theirs + c];
            if (other.Absent(r, c)) { bb.Absent(o, theirs + c); continue; }
            var v = t.Get(r, other.ColsAt + c);
            if (dst.IsDec) dst.D[o] = decimals.Value(v); else dst.L[o] = v;
        }
    }

    /// <summary>A batch of `self` against the stored rows of `other`, as they were before this step.</summary>
    void Probe(Batch? b, Side self, Side other, bool selfIsLeft, BatchBuilder bb)
    {
        if (b is null || b.Rows == 0) return;
        for (var i = 0; i < self.Keys.Length; i++) self.KeyCols[i] = self.Keys[i].Eval(b);
        Span<long> key = stackalloc long[self.Keys.Length];
        for (var k = 0; k < b.Rows; k++)
        {
            var row = b.RowAt(k);
            if (!Key(self, row, key)) continue;
            var w = b.Weight(row);
            for (var r = other.ByKey.First(key); r >= 0; r = other.ByKey.Next(r)) Emit(bb, self, b, row, other, r, selfIsLeft, w * other.Rows.Weight(r));
        }
    }

    /// <summary>The left batch against the right batch: the pairs of rows new in this step. The keys of both are in their sides' `KeyCols`.</summary>
    void Cross(Batch lb, Batch rb, BatchBuilder bb)
    {
        var m = left.Keys.Length;
        Span<long> key = stackalloc long[m];
        crossHead.Clear(); crossNext.Clear(); crossKeys.Clear();
        for (var k = 0; k < rb.Rows; k++)
        {
            var row = rb.RowAt(k);
            var ok = Key(right, row, key);
            crossNext.Add(-1);
            for (var i = 0; i < m; i++) crossKeys.Add(key[i]);
            if (!ok) continue;
            var h = Table.HashOf(key);
            if (crossHead.TryGetValue(h, out var first)) crossNext[k] = first;
            crossHead[h] = k;
        }
        var keys = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(crossKeys);
        for (var k = 0; k < lb.Rows; k++)
        {
            var row = lb.RowAt(k);
            if (!Key(left, row, key) || !crossHead.TryGetValue(Table.HashOf(key), out var j)) continue;
            for (; j >= 0; j = crossNext[j])
            {
                if (!keys.Slice(j * m, m).SequenceEqual(key)) continue;
                var rrow = rb.RowAt(j);
                var o = bb.Add(lb.Weight(row) * rb.Weight(rrow));
                Copy(bb, o, left, lb, row, 0); Copy(bb, o, right, rb, rrow, left.N);
            }
        }
        if (crossNext.Count > 4096) { crossHead.Clear(); crossHead.TrimExcess(); crossNext.Clear(); crossNext.TrimExcess(); crossKeys.Clear(); crossKeys.TrimExcess(); }      // a large step's index does not stay
    }

    /// <summary>Adds a batch to the rows of its side.</summary>
    void Store(Batch? b, Side s)
    {
        if (b is null || b.Rows == 0) return;
        for (var i = 0; i < s.Keys.Length; i++) s.KeyCols[i] = s.Keys[i].Eval(b);
        Span<long> stored = stackalloc long[s.Rows.Columns];
        Span<long> key = stackalloc long[s.Keys.Length];
        for (var k = 0; k < b.Rows; k++)
        {
            var row = b.RowAt(k);
            if (!Key(s, row, key)) continue;
            Encode(s, b, row, key, stored);
            s.Rows.Add(stored, b.Weight(row));
        }
    }

    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var bb = EmptyOutput();
        var lb = In(inputs, "left"); var rb = In(inputs, "right");
        Probe(lb, left, right, true, bb);
        Probe(rb, right, left, false, bb);
        if (lb is { Rows: > 0 } && rb is { Rows: > 0 })
        {
            for (var i = 0; i < left.Keys.Length; i++) { left.KeyCols[i] = left.Keys[i].Eval(lb); right.KeyCols[i] = right.Keys[i].Eval(rb); }
            Cross(lb, rb, bb);
        }
        if (left.Adds) Store(lb, left);
        if (right.Adds) Store(rb, right);
        return bb.Build();
    }
}

/// <summary>What the joins of one graph store, built with its plan: one `DecimalCodes` for all of them, and per input read with keys that are its columns one table of its rows, shared by the joins that read it. The last side to take a table in plan order adds each step's batch to it, after every reader has joined against it.</summary>
sealed class JoinInputs
{
    public readonly DecimalCodes Decimals = new();
    readonly Dictionary<Src, (Table Rows, JoinOp.Side Last)> shared = [];

    /// <summary>The table for `side`: the input's shared one when `input` is given and the layout agrees, else one of its own. The side becomes the one that adds.</summary>
    public Table Rows(JoinOp.Side side, Src? input, int[] widths)
    {
        side.Adds = true;
        if (input is null) return new Table(widths, keyColumns: widths.Length);
        if (shared.TryGetValue(input, out var s))
        {
            if (s.Rows.Columns != widths.Length) throw new InvalidOperationException($"input {input.Name} is stored with two layouts");
            s.Last.Adds = false;
            shared[input] = (s.Rows, side);
            return s.Rows;
        }
        var rows = new Table(widths, keyColumns: widths.Length);
        shared[input] = (rows, side);
        return rows;
    }
}

// ---- traversal -------------------------------------------------------------------------------------------

/// <summary>Bounded traversal: for every input row, the entities reachable from its start element over one relationship type (and its sub-types) in the direction given, at a depth from `Min` to `Max`. Per start, the operator keeps the depth at which its walk first reached each id; what the start reaches is read from those depths and the graph. A row delta is emitted against that reach. A patch is applied in increments: a start with no walk yet, or whose walk visited an end of a lost edge, is walked again and the difference emitted; an added edge only lowers depths, so from each start that visited its near end the depths are lowered from its far end on and only the ids whose depth fell are emitted again; an edited entity touches the starts that visited it. Reached elements that are not in the graph are skipped (they appear when the entity does).
/// State: the rows as a Z-set (`Table`: every column as its 8-byte code, a decimal as its number in `DecimalCodes`, and a bit per absent column) arranged by start; and per (start, visited id) the depth (`Table`, arranged both ways), so a patch finds the starts it reaches through its ids. Properties are read from the graph when a row is emitted; nothing of an entity is kept. The walk's set, lists and queue belong to the operator and are emptied after each call.</summary>
sealed class TraverseOp : PhysOp
{
    readonly TTraverse t; readonly Interner it; readonly OntologyView view;
    readonly int inN, startCol, reachedCol;
    readonly (string PropId, int OutCol)[] reads;
    /// <summary>The input's rows as a Z-set, arranged by start. A stored row is every column, then a bit per absent number in words of 31 bits. A column of codes (a text, an element, a decimal as its number in `decimals`) takes 4 bytes and is -1 when absent; a number (an integer, an instant, a boolean) takes 8 bytes and its absence is a bit.</summary>
    readonly Table rows; readonly Arrangement rowsOf;
    /// <summary>Per column: its absent bit, or -1 for a column of codes.</summary>
    readonly int[] bit; readonly int words;
    readonly DecimalCodes decimals = new();
    /// <summary>The walk of each start: per (start, visited id) the depth the id was first reached at, for every id within `Max`, an entity or not. What a start reaches is its visited entities at depth `Min` or more; their properties are read from the graph when a row is emitted, and nothing of an entity is kept.</summary>
    readonly Table depths = new([4, 4, 4], keyColumns: 2); readonly Arrangement startsOf, visitsOf;
    const int Depth = 2;
    /// <summary>Starts with rows and no walk yet: they are walked when the graph is there.</summary>
    readonly List<long> pending = [];
    Graph? graph;
    // working state, empty between calls: the entities as they were before this step's patch (by id, the ones the patch edits), the walk, the reach before and after a walk
    readonly Dictionary<string, Entity?> before = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> seen = new(StringComparer.Ordinal); List<string> frontier = [], next = [];
    /// <summary>The reach before and after a walk, by id code: an entity the patch edited as it was or is, null for an entity of the graph the patch did not edit (the same before and after; its properties are read from the graph when a row is emitted).</summary>
    readonly Dictionary<int, Entity?> was = [], now = [];
    readonly List<long> todo = [];
    readonly HashSet<long> walked = [];
    readonly List<(string From, string To)> added = [];
    readonly List<(long Start, int Depth)> from = [];
    readonly Queue<(string Id, int Depth)> queue = [];
    /// <summary>Per (start, id) that this step's patch touched without a walk: its depth before the patch, -1 when it was not visited.</summary>
    readonly Dictionary<(long Start, int Id), int> touched = [];

    public TraverseOp(TTraverse t, Layout inL, OntologyView view, Interner it) : base(t)
    {
        this.t = t; this.it = it; this.view = view;
        inN = inL.Count; startCol = inL.Index(t.Start); reachedCol = OutLayout.Index(t.Target);
        reads = [.. t.Reads.Select(r => (r.PropId, OutLayout.Index(r.Col)))];
        var widths = new List<int>(); bit = new int[inN]; var bits = 0;
        for (var c = 0; c < inN; c++)
        {
            var code = inL.IsDec[c] || inL.Interned?[c] == true;
            widths.Add(code ? 4 : 8); bit[c] = code ? -1 : bits++;
        }
        words = (bits + 30) / 31;
        for (var w = 0; w < words; w++) widths.Add(4);
        rows = new Table([.. widths], keyColumns: widths.Count); rowsOf = rows.Arrange(startCol);
        startsOf = depths.Arrange(0); visitsOf = depths.Arrange(1);
    }

    /// <summary>The stored form of a batch row: every column, then the absent bits.</summary>
    void Encode(Batch b, int row, Span<long> key)
    {
        key[inN..].Clear();
        for (var c = 0; c < inN; c++)
        {
            var col = b.Cols[c];
            if (!col.Present(row)) { if (bit[c] < 0) key[c] = -1; else { key[c] = 0; key[inN + bit[c] / 31] |= 1L << (bit[c] % 31); } }
            else key[c] = col.IsDec ? decimals.Code(col.D[row]) : col.L[row];
        }
    }

    bool Absent(int r, int c) => bit[c] < 0 ? rows.Get(r, c) < 0 : (rows.Get(r, inN + bit[c] / 31) >> (bit[c] % 31) & 1) != 0;

    /// <summary>Whether two entities give a row the same values: absent and null alike, a decimal with its scale (2.5 and 2.50 are emitted as they are written).</summary>
    bool SameReads(Entity a, Entity b)
    {
        foreach (var (propId, _) in reads)
        {
            Value? x = a.Props.TryGet(propId, out var va) && va is not NullValue ? va : null, y = b.Props.TryGet(propId, out var vb) && vb is not NullValue ? vb : null;
            if (x is null || y is null) { if (x != y) return false; continue; }
            if (x is DecimalValue dx && y is DecimalValue dy ? !decimal.GetBits(dx.Q).AsSpan().SequenceEqual(decimal.GetBits(dy.Q)) : !x.Equals(y)) return false;
        }
        return true;
    }

    /// <summary>The breadth-first walk from a start: every id reached within `Max`, with its depth, into `seen`.</summary>
    void Walk(string startId, Graph g)
    {
        seen.Clear(); frontier.Clear();
        seen[startId] = 0; frontier.Add(startId);
        for (var depth = 1; depth <= t.Max && frontier.Count > 0; depth++)
        {
            next.Clear();
            foreach (var id in frontier)
            {
                if (t.Direction is "out" or "either")
                    foreach (var (type, other) in g.Neighbors(id, true)) if (view.IsSubtype(type, t.RelTypeId) && seen.TryAdd(other, depth)) next.Add(other);
                if (t.Direction is "in" or "either")
                    foreach (var (type, other) in g.Neighbors(id, false)) if (view.IsSubtype(type, t.RelTypeId) && seen.TryAdd(other, depth)) next.Add(other);
            }
            (frontier, next) = (next, frontier);
        }
    }

    /// <summary>An entity as it was before this step's patch.</summary>
    Entity? Before(string id) => before.TryGetValue(id, out var e) ? e : graph!.Entity(id);

    /// <summary>Whether an entity was there before this step's patch, and the entity when the patch edited it (null for one it did not edit).</summary>
    bool WasThere(string id, out Entity? made)
    {
        if (before.TryGetValue(id, out made)) return made is not null;
        return graph!.HasEntity(id);
    }

    /// <summary>Whether an entity is there after this step's patch, and the entity when the patch edited it (null for one it did not edit).</summary>
    bool IsThere(string id, out Entity? made)
    {
        made = null;
        if (before.ContainsKey(id)) return (made = graph!.Entity(id)) is not null;
        return graph!.HasEntity(id);
    }

    /// <summary>Whether an id reached before and after gives a row the same values: an entity the patch did not edit always does.</summary>
    bool Same(int n, Entity? a, Entity? b) => a is null && b is null || SameReads(a ?? graph!.Entity(it.Get(n))!, b ?? graph!.Entity(it.Get(n))!);

    /// <summary>What a start reached when it was last walked, as the entities were then.</summary>
    void Reached(long start, Dictionary<int, Entity?> into)
    {
        into.Clear();
        for (var r = startsOf.First([start]); r >= 0; r = startsOf.Next(r))
            if (depths.Get(r, Depth) >= t.Min && WasThere(it.Get((int)depths.Get(r, 1)), out var e)) into[(int)depths.Get(r, 1)] = e;
    }

    /// <summary>Forgets a start's walk.</summary>
    void Unvisit(long start) { for (int r; (r = startsOf.First([start])) >= 0;) depths.Add([start, depths.Get(r, 1)], -1); }

    /// <summary>One output row: the input columns of a batch row (`b` not null) or of stored row `r`, then the reached entity `n` and the properties read, from `item` or, when it is null, from the graph.</summary>
    void Emit(BatchBuilder bb, Batch? b, int r, int n, Entity? item, long weight)
    {
        var row = bb.Add(weight); var o = bb.Batch;
        for (var c = 0; c < inN; c++)
        {
            var dst = o.Cols[c];
            if (b is not null)
            {
                var src = b.Cols[c];
                if (!src.Present(r)) { bb.Absent(row, c); continue; }
                if (dst.IsDec) dst.D[row] = src.D[r]; else dst.L[row] = src.L[r];
                continue;
            }
            if (Absent(r, c)) { bb.Absent(row, c); continue; }
            var v = rows.Get(r, c);
            if (dst.IsDec) dst.D[row] = decimals.Value(v); else dst.L[row] = v;
        }
        o.Cols[reachedCol].L[row] = n;
        foreach (var (propId, outCol) in reads)
        {
            var col = o.Cols[outCol];
            var v = item is null ? graph!.EntityProp(it.Get(n), propId) : item.Props.TryGet(propId, out var x) ? x : null;
            if (v is not null and not NullValue) { if (col.IsDec) col.D[row] = ((DecimalValue)v).Q; else col.L[row] = Codec.ToCode(v, it); }
            else col.SetAbsent(row, o.N);
        }
    }

    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var bb = EmptyOutput();
        Patch? patch = null;
        if (ep.Graphs.TryGetValue(t.GraphPort, out var gd)) { graph = gd.State; patch = gd.Patch; }
        if (patch is not null) foreach (var e in patch.Edits) if (e.Key.Kind == 'e') before[e.Key.Id] = e.Before as Entity;
        // (a) row deltas against the reach emitted so far
        var b = In(inputs, "in");
        if (b is not null && b.Rows > 0)
        {
            Span<long> key = stackalloc long[inN + words];
            for (var k = 0; k < b.Rows; k++)
            {
                var src = b.RowAt(k);
                if (!b.Cols[startCol].Present(src)) continue;
                var start = b.Cols[startCol].L[src]; var w = b.Weight(src);
                var walked = startsOf.First([start]) >= 0;
                if (walked) { Reached(start, was); foreach (var (n, item) in was) Emit(bb, b, src, n, item, w); }
                var first = rowsOf.First([start]) < 0;
                Encode(b, src, key);
                rows.Add(key, w);
                if (rowsOf.First([start]) < 0) Unvisit(start);
                else if (first && !walked) pending.Add(start);
            }
        }
        // (b) what the patch changed. A start without a walk yet, or whose walk visited an end of a lost edge, is walked again and the difference emitted. An added edge only lowers depths: from each start that visited its near end, the depths are lowered from its far end on, and only the ids whose depth fell are touched. An edited entity touches the starts that visited it. A relationship the walk does not follow changes nothing.
        if (graph is not null)
        {
            todo.AddRange(pending); pending.Clear();
            if (patch is not null)
                foreach (var e in patch.Edits)
                {
                    if (e.Key.Kind == 'e') { Touch(e.Key.Id); continue; }
                    var (lost, got) = (Edge(e.Before), Edge(e.After));
                    if (lost == got) continue;
                    if (lost is { } l) { Visited(l.Source); Visited(l.Target); }
                    if (got is { } g)
                    {
                        if (t.Direction is "out" or "either") added.Add((g.Source, g.Target));
                        if (t.Direction is "in" or "either") added.Add((g.Target, g.Source));
                    }
                }
            todo.Sort();
            for (var i = 0; i < todo.Count; i++)
            {
                var start = todo[i];
                if (i > 0 && todo[i - 1] == start || rowsOf.First([start]) < 0) continue;
                walked.Add(start);
                Reached(start, was);
                Walk(it.Get((int)start), graph);
                now.Clear();
                foreach (var (id, depth) in seen) if (depth >= t.Min && IsThere(id, out var e)) now[it.Intern(id)] = e;
                for (var r = rowsOf.First([start]); r >= 0; r = rowsOf.Next(r))
                {
                    var w = rows.Weight(r);
                    foreach (var (n, e) in was) if (!now.TryGetValue(n, out var x) || !Same(n, e, x)) Emit(bb, null, r, n, e, -w);
                    foreach (var (n, e) in now) if (!was.TryGetValue(n, out var x) || !Same(n, x, e)) Emit(bb, null, r, n, e, w);
                }
                Unvisit(start);
                foreach (var (id, depth) in seen) depths.Set(depths.Add([start, it.Intern(id)], 1), Depth, depth);
            }
            foreach (var (near, far) in added)
            {
                from.Clear();
                if (it.TryFind(near, out var code))
                    for (var r = visitsOf.First([code]); r >= 0; r = visitsOf.Next(r))
                        if (depths.Get(r, Depth) < t.Max && !walked.Contains(depths.Get(r, 0))) from.Add((depths.Get(r, 0), (int)depths.Get(r, Depth)));
                foreach (var (start, depth) in from) Lower(start, far, depth + 1);
            }
            foreach (var ((start, n), old) in touched)
            {
                if (walked.Contains(start)) continue;
                var row = depths.Find([start, n]); var id = it.Get(n);
                var wasItem = old >= t.Min ? Before(id) : null;
                var nowItem = row >= 0 && depths.Get(row, Depth) >= t.Min ? graph.Entity(id) : null;
                if (wasItem is not null && nowItem is not null && SameReads(wasItem, nowItem)) continue;
                for (var r = rowsOf.First([start]); r >= 0; r = rowsOf.Next(r))
                {
                    var w = rows.Weight(r);
                    if (wasItem is not null) Emit(bb, null, r, n, wasItem, -w);
                    if (nowItem is not null) Emit(bb, null, r, n, nowItem, w);
                }
            }
        }
        walked.Clear(); added.Clear(); from.Clear(); queue.Clear(); touched.Clear();
        todo.Clear(); seen.Clear(); frontier.Clear(); next.Clear(); was.Clear(); now.Clear(); before.Clear();
        Trim();
        return bb.Build();
    }

    /// <summary>A large step's working state does not stay: a structure that grew past 4,096 entries (a walk from the root of a tree reaches all of it) is let go.</summary>
    void Trim()
    {
        const int Keep = 4096;
        if (seen.EnsureCapacity(0) > Keep) seen.TrimExcess();
        if (was.EnsureCapacity(0) > Keep) was.TrimExcess();
        if (now.EnsureCapacity(0) > Keep) now.TrimExcess();
        if (before.EnsureCapacity(0) > Keep) before.TrimExcess();
        if (touched.EnsureCapacity(0) > Keep) touched.TrimExcess();
        if (walked.EnsureCapacity(0) > Keep) walked.TrimExcess();
        if (frontier.Capacity > Keep) frontier.TrimExcess();
        if (next.Capacity > Keep) next.TrimExcess();
        if (todo.Capacity > Keep) todo.TrimExcess();
        if (added.Capacity > Keep) added.TrimExcess();
    }

    /// <summary>The ends of a relationship the walk follows; null for any other element.</summary>
    (string Source, string Target)? Edge(Element? x) => x is Rel r && view.IsSubtype(r.Type, t.RelTypeId) ? (r.Source, r.Target) : null;

    /// <summary>An entity the patch edits: every start that visited it is touched there, at the depth it has.</summary>
    void Touch(string id)
    {
        if (!it.TryFind(id, out var code)) return;
        for (var r = visitsOf.First([code]); r >= 0; r = visitsOf.Next(r)) touched.TryAdd((depths.Get(r, 0), code), (int)depths.Get(r, Depth));
    }

    /// <summary>An added edge reached `id` from `start` at `depth`: breadth first from there, every id whose depth falls gets the lower depth and is touched with the depth it had before the patch.
    /// An id may be queued more than once in one call, once per neighbour whose depth fell. That is harmless: the queue holds depths in rising order, so the first time an id is taken it gets its least depth, and every later copy finds a depth no greater than its own and is skipped. A later added edge of the same patch lowers an id again only where its depth is less; the touched depth stays the one from before the patch, since only the first `TryAdd` counts. A copy costs one lookup, as a check when queuing would.</summary>
    void Lower(long start, string id, int depth)
    {
        queue.Clear(); queue.Enqueue((id, depth));
        while (queue.TryDequeue(out var x))
        {
            var code = it.Intern(x.Id);
            var row = depths.Find([start, code]);
            var old = row >= 0 ? (int)depths.Get(row, Depth) : -1;
            if (old >= 0 && old <= x.Depth) continue;
            touched.TryAdd((start, code), old);
            if (row < 0) row = depths.Add([start, code], 1);
            depths.Set(row, Depth, x.Depth);
            if (x.Depth == t.Max) continue;
            if (t.Direction is "out" or "either")
                foreach (var (type, other) in graph!.Neighbors(x.Id, true)) if (view.IsSubtype(type, t.RelTypeId)) queue.Enqueue((other, x.Depth + 1));
            if (t.Direction is "in" or "either")
                foreach (var (type, other) in graph!.Neighbors(x.Id, false)) if (view.IsSubtype(type, t.RelTypeId)) queue.Enqueue((other, x.Depth + 1));
        }
    }

    /// <summary>The starts whose last walk visited `id` go on the list of starts to walk.</summary>
    void Visited(string id)
    {
        if (!it.TryFind(id, out var code)) return;
        for (var r = visitsOf.First([code]); r >= 0; r = visitsOf.Next(r)) todo.Add(depths.Get(r, 0));
    }
}

// ---- constants -------------------------------------------------------------------------------------------

/// <summary>Values: constant rows from the definition, emitted once (weight +1) in the first evaluation of the graph.</summary>
sealed class ValuesOp(TValues t, Interner it) : PhysOp(t)
{
    bool emitted;
    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        if (emitted) return null;
        emitted = true;
        var bb = EmptyOutput();
        foreach (var r in t.Rows)
        {
            var row = bb.Add(1);
            for (var c = 0; c < r.Length; c++)
            {
                var col = bb.Batch.Cols[c];
                if (col.IsDec) col.D[row] = ((DecimalValue)r[c]).Q; else col.L[row] = Codec.ToCode(r[c], it);
            }
        }
        return bb.Build();
    }
}

// ---- retention -------------------------------------------------------------------------------------------

/// <summary>Retain: rows are event facts that stay in the graph for a bounded span of logical time. The rows that passed are held as a multiset (a row and the sum of its weights, in any order of arrival); when an advance moves the frontier to a point at or past `time + age`, what is held of the row is withdrawn, so whatever was derived from it goes with it. A row that is already that old on arrival never passes, and its retraction is absorbed. The buffer holds the rows of one span, nothing older.</summary>
sealed class RetainOp : PhysOp
{
    sealed class Held(bool[] present, long[] l, decimal[] d, long weight) { public readonly bool[] Present = present; public readonly long[] L = l; public readonly decimal[] D = d; public long Weight = weight; }
    readonly SortedDictionary<long, List<Held>> byTime = [];
    readonly int ts, n; readonly long age; readonly bool[] isDec;
    long lambda = long.MinValue;
    public long Expired { get; private set; }

    public RetainOp(TRetain t, Layout inL) : base(t) { ts = inL.Index(t.TimeField); age = t.AgeNs; n = inL.Count; isDec = inL.IsDec; }

    static bool Same(Held h, Batch b, int row, bool[] isDec)
    {
        for (var c = 0; c < isDec.Length; c++)
        {
            var col = b.Cols[c]; var p = col.Present(row);
            if (p != h.Present[c]) return false;
            if (p && (isDec[c] ? col.D[row] != h.D[c] : col.L[row] != h.L[c])) return false;
        }
        return true;
    }

    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var bb = EmptyOutput();
        void Emit(Held h, long weight)
        {
            var row = bb.Add(weight);
            for (var c = 0; c < n; c++)
            {
                var dst = bb.Batch.Cols[c];
                if (!h.Present[c]) { bb.Absent(row, c); continue; }
                if (isDec[c]) dst.D[row] = h.D[c]; else dst.L[row] = h.L[c];
            }
        }
        if (In(inputs, "in") is { } b && b.Rows > 0)
            for (var k = 0; k < b.Rows; k++)
            {
                var src = b.RowAt(k); var w = b.Weight(src);
                if (!b.Cols[ts].Present(src)) { cx.Faults.MissingRequired++; continue; }      // the checker requires the column; a row without it is neither kept nor expired later
                var time = b.Cols[ts].L[src];
                if (lambda != long.MinValue && time + age <= lambda) { if (w > 0) Expired++; continue; }  // expired on arrival; the retraction of a row that was withdrawn is absorbed
                if (!byTime.TryGetValue(time, out var list)) byTime[time] = list = [];
                var i = list.FindIndex(h => Same(h, b, src, isDec));
                Held held;
                if (i >= 0)
                {
                    held = list[i]; held.Weight += w;
                    if (held.Weight == 0) { list.RemoveAt(i); if (list.Count == 0) byTime.Remove(time); }
                }
                else
                {
                    held = new Held(new bool[n], new long[n], new decimal[n], w);
                    for (var c = 0; c < n; c++)
                    {
                        var col = b.Cols[c]; held.Present[c] = col.Present(src);
                        if (!held.Present[c]) continue;
                        if (isDec[c]) held.D[c] = col.D[src]; else held.L[c] = col.L[src];
                    }
                    list.Add(held);
                }
                Emit(held, w);
            }
        if (ep.HasAdvance && ep.Lambda > lambda)
        {
            lambda = ep.Lambda;
            foreach (var time in byTime.Keys.TakeWhile(t => t + age <= lambda).ToList())
            {
                foreach (var h in byTime[time]) { Emit(h, -h.Weight); Expired++; }
                byTime.Remove(time);
            }
        }
        return bb.Build();
    }
}
