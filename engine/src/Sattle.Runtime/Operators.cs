using System.Runtime.InteropServices;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

/// <summary>Inputs of one evaluation step of a projection: row batches per rows port, graph deltas per graph port, an optional logical-time advance.</summary>
public sealed class Epoch
{
    public long Id;
    internal MatchPreparation? Preparation;
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

/// <summary>A committed change of an input graph, as seen by a projection: the patch, the graph after it, and the windows that changed in the same step.</summary>
public sealed class GraphDelta(Patch? patch, Graph state, IReadOnlyList<WindowChange> windows)
{
    public Patch? Patch { get; } = patch;
    public Graph State { get; } = state;
    public IReadOnlyList<WindowChange> Windows { get; } = windows;

    MatchPatch? matchInput;
    internal MatchPatch MatchInput => matchInput ??= new(Patch!);

    /// <summary>The places in `Windows` of the changes of one property, in their order. The index is built once per delta, on the first call, so each reader of the graph visits only the changes of the properties it reads.</summary>
    public ArraySegment<int> ChangesOf(int prop) => (byProp ??= ByProp(Windows)).TryGetValue(prop, out var s) ? s : ArraySegment<int>.Empty;
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
    /// <summary>Whether a window that ended at `end` can no longer be revised at this frontier: its end plus the lateness its program declares (Ch. 6, Cor. 6.5, #169 D1).</summary>
    public static bool PastRevision(long end, long frontier, long lateness) => (Int128)end + lateness <= frontier;
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
    /// <summary>Append a prepared range, including weights and presence bits, in its original order.</summary>
    internal void Append(Batch source)
    {
        var b = Batch;
        var end = checked(b.N + source.N);
        if (end > b.W!.Length) b.Ensure((int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)end));
        source.W!.AsSpan(0, source.N).CopyTo(b.W.AsSpan(b.N));
        for (var c = 0; c < b.Cols.Length; c++)
        {
            var from = source.Cols[c]; var to = b.Cols[c];
            if (from.IsDec) from.D.AsSpan(0, source.N).CopyTo(to.D.AsSpan(b.N));
            else from.L.AsSpan(0, source.N).CopyTo(to.L.AsSpan(b.N));
            if (from.P is { } present)
            {
                if (to.P is null) { to.P = new bool[b.W.Length]; Array.Fill(to.P, true, 0, b.N); }
                present.AsSpan(0, source.N).CopyTo(to.P.AsSpan(b.N));
            }
            else if (to.P is not null) Array.Fill(to.P, true, b.N, source.N);
        }
        b.N = end;
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

/// <summary>The state of a group operator (core step 3): one row of a `Table` per key, holding the key, the row count, one accumulator per sum, count or mean (a decimal in two 8-byte columns), for a key with more than one value of a `min` or `max` their extreme in the accumulator the aggregate has, and whether the key's aggregate was emitted. A slot is the row number. The values emitted last are not kept: an operator retracts a key's aggregate when it first touches the key in a step, before the step's rows change it. The multisets of `min`, `max`, `percentile` and `countDistinct` are tables of (slot, aggregate, value) with a weight, found through their arrangement by (slot, aggregate); a number is keyed by its value whatever its scale (2.5 and 2.50 are one entry, kept in its normal form, `DecimalKey.Normal`, whatever arrived first).</summary>
sealed class KeyTable
{
    readonly int k, nL, nD, nIndex;
    readonly Table rows;
    readonly int cnt, accL, accD;
    readonly long[] key;
    readonly Segments<byte> touched = new();
    public readonly List<int> TouchedList = [];
    /// <summary>(slot, aggregate, value): the values of a `min`, `max` or `percentile` over integers and codes, and of a `countDistinct`. The aggregate is named by its accumulator in the key's row, which no two aggregates share.</summary>
    readonly Table? longs;
    /// <summary>(slot, aggregate, value as a number, its digits): the values of a `min`, `max` or `percentile` over decimals.</summary>
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
    /// <summary>An integer sum, exact: a 128-bit integer in the accumulators `i` and `i + 1` (#159 G4).</summary>
    public Int128 Sum(int slot, int i) => new((ulong)L(slot, i + 1), (ulong)L(slot, i));
    public void AddSum(int slot, int i, Int128 v) { var s = Sum(slot, i) + v; rows.Set(slot, accL + i, (long)(ulong)s); rows.Set(slot, accL + i + 1, (long)(ulong)(s >> 64)); }
    /// <summary>A decimal sum, exact: the accumulators `i` to `i + 3` (`ExactSum`).</summary>
    public void AddSum(int slot, int i, decimal v, long w)
    {
        Span<ulong> acc = stackalloc ulong[4];
        for (var j = 0; j < 4; j++) acc[j] = (ulong)L(slot, i + j);
        ExactSum.Add(acc, v, w);
        for (var j = 0; j < 4; j++) rows.Set(slot, accL + i + j, (long)acc[j]);
    }
    public bool DecimalSum(int slot, int i, out decimal d)
    {
        Span<ulong> acc = stackalloc ulong[4];
        for (var j = 0; j < 4; j++) acc[j] = (ulong)L(slot, i + j);
        return ExactSum.ToDecimal(acc, out d);
    }

    /// <summary>A decimal in two 64-bit halves, by its number: 2.5 and 2.50 are kept alike, so no value depends on which digits came first (#161); and back.</summary>
    static (long A, long B) Pack(decimal d) { Span<int> bits = stackalloc int[4]; decimal.GetBits(DecimalKey.Normal(d), bits); return ((uint)bits[0] | (long)bits[1] << 32, (uint)bits[2] | (long)bits[3] << 32); }
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
    /// <summary>Adds a weight to a decimal of a multiset: 2.5 and 2.50 are one value, kept by its number. The change of the number of distinct values (+1, -1 or 0).</summary>
    public int AddDecimal(int slot, int a, decimal v, long w)
    {
        var (lo, hi) = DecimalKey.Of(v);
        var had = decimals!.Find([slot, a, lo, hi]) >= 0;
        var row = decimals.Add([slot, a, lo, hi], w);
        if (row >= 0 && !had) { var (x, y) = Pack(v); decimals.Set(row, 4, x); decimals.Set(row, 5, y); }
        return (row >= 0 ? 1 : 0) - (had ? 1 : 0);
    }
    /// <summary>The same as `AddExtremeL` for a `min` or `max` over decimals.</summary>
    public void AddExtremeD(int slot, int a, decimal v, long w, bool desc)
    {
        var change = AddDecimal(slot, a, v, w);
        if (change == 0) return;
        var values = Values(decimalsOf!, slot, a);
        if (values < 2) { if (D(slot, a) != 0) SetD(slot, a, 0); }
        else if (change > 0 && values > 2) { if (desc ? v > D(slot, a) : v < D(slot, a)) SetD(slot, a, v); }
        else if (change > 0 || v == D(slot, a))
        {
            decimal best = 0; var any = false;
            for (var r = decimalsOf!.First([slot, a]); r >= 0; r = decimalsOf.Next(r))
            {
                var x = Unpack(decimals!.Get(r, 4), decimals.Get(r, 5));
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
    /// <summary>The value of nearest rank `percent` of a `percentile` over integers or decimals: of its values with a positive weight, each counted as often as its weight (n in all), the r-th smallest, r = max(1, ⌈percent · n / 100⌉); a decimal as its number without trailing zeros. False when no value has a positive weight. One weighted selection over the key's distinct values, copied to a scratch array the table reuses and grows only for a key with more values than any before.</summary>
    public bool Percentile(int slot, int a, bool dec, decimal percent, out decimal v)
    {
        var (t, of) = dec ? (decimals!, decimalsOf!) : (longs!, longsOf!);
        var m = 0; long n = 0;
        for (var r = of.First([slot, a]); r >= 0; r = of.Next(r))
        {
            var w = t.Weight(r);
            if (w <= 0) continue;
            if (m == scratch.Length) Array.Resize(ref scratch, Math.Max(16, 2 * m));
            scratch[m++] = (dec ? DecimalKey.Number(t.Get(r, 2), t.Get(r, 3)) : t.Get(r, 2), w);
            n += w;
        }
        v = n == 0 ? 0 : Select(scratch.AsSpan(0, m), (long)Math.Max(1m, Math.Ceiling(percent * n / 100m)));
        return n > 0;
    }
    (decimal Value, long Weight)[] scratch = [];
    ulong pivots = 0x9E3779B97F4A7C15UL;

    /// <summary>The r-th smallest of distinct values counted by their weights (1 ≤ r ≤ the sum of the weights): a weighted quickselect, in place, expected linear in the number of values. The pivots come from a xorshift, the same run to run.</summary>
    decimal Select(Span<(decimal Value, long Weight)> xs, long r)
    {
        while (true)
        {
            pivots ^= pivots << 13; pivots ^= pivots >> 7; pivots ^= pivots << 17;
            var p = (int)(pivots % (ulong)xs.Length);
            (xs[p], xs[^1]) = (xs[^1], xs[p]);
            var pivot = xs[^1]; var below = 0; long wBelow = 0;
            for (var i = 0; i < xs.Length - 1; i++)
                if (xs[i].Value < pivot.Value) { wBelow += xs[i].Weight; (xs[i], xs[below]) = (xs[below], xs[i]); below++; }
            if (r <= wBelow) xs = xs[..below];
            else if (r <= wBelow + pivot.Weight) return pivot.Value;
            else { r -= wBelow + pivot.Weight; xs = xs[below..^1]; }
        }
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

/// <summary>An exact sum of decimals (#159 G4, Def. 7.3): a signed 256-bit integer in four words, least first, that is the sum times 10^28. A decimal has at most
/// 96 bits of digits and scale 28, so a row is below 2^190 and a weight below 2^63 times that: no sum of rows leaves the 256 bits, and the sum is the same in any
/// order of the rows. It is a decimal again only when it is one exactly.</summary>
static class ExactSum
{
    static readonly ulong[] Tens = [.. Enumerable.Range(0, 20).Select(Pow10)];
    static ulong Pow10(int k) { ulong p = 1; for (var i = 0; i < k; i++) p *= 10; return p; }

    /// <summary>`acc += w · v`.</summary>
    public static void Add(Span<ulong> acc, decimal v, long w)
    {
        if (v == 0 || w == 0) return;
        Span<int> bits = stackalloc int[4]; decimal.GetBits(v, bits);
        Span<ulong> x = [(uint)bits[0] | (ulong)(uint)bits[1] << 32, (uint)bits[2], 0, 0];
        var up = 28 - (bits[3] >> 16 & 0xFF);
        Mul(x, Tens[Math.Min(up, 19)]); if (up > 19) Mul(x, Tens[up - 19]);
        Mul(x, w < 0 ? (ulong)-(w + 1) + 1 : (ulong)w);
        if ((v < 0) != (w < 0)) Negate(x);
        ulong carry = 0;
        for (var i = 0; i < 4; i++) { var s = acc[i] + x[i]; var c1 = s < acc[i] ? 1UL : 0; acc[i] = s + carry; carry = c1 | (acc[i] < s ? 1UL : 0); }
    }

    /// <summary>The sum as a decimal, false when no decimal is it exactly (more than 96 bits of digits at the least scale that holds it).</summary>
    public static bool ToDecimal(ReadOnlySpan<ulong> acc, out decimal d)
    {
        Span<ulong> x = stackalloc ulong[4]; acc.CopyTo(x);
        var neg = (long)x[3] < 0; if (neg) Negate(x);
        if ((x[0] | x[1] | x[2] | x[3]) == 0) { d = 0; return true; }
        var scale = 28;
        Span<ulong> q = stackalloc ulong[4];
        while (scale > 0) { x.CopyTo(q); if (DivRem10(q) != 0) break; q.CopyTo(x); scale--; }
        if (x[3] != 0 || x[2] != 0 || x[1] > uint.MaxValue) { d = 0; return false; }
        d = new decimal((int)x[0], (int)(x[0] >> 32), (int)x[1], neg, (byte)scale);
        return true;
    }

    static void Mul(Span<ulong> x, ulong f)
    {
        ulong carry = 0;
        for (var i = 0; i < 4; i++) { var hi = Math.BigMul(x[i], f, out var lo); lo += carry; x[i] = lo; carry = hi + (lo < carry ? 1UL : 0); }
    }
    static void Negate(Span<ulong> x) { var carry = 1UL; for (var i = 0; i < 4; i++) { x[i] = ~x[i] + carry; carry = x[i] == 0 && carry == 1 ? 1UL : 0; } }
    static ulong DivRem10(Span<ulong> x)
    {
        UInt128 rem = 0;
        for (var i = 3; i >= 0; i--) { var cur = rem << 64 | x[i]; x[i] = (ulong)(cur / 10); rem = cur % 10; }
        return (ulong)rem;
    }
}

enum AggKind { Count, SumL, SumD, Distinct, ExtL, ExtD, Mean, Last, PctL, PctD }
sealed record AggSpec(AggKind Kind, int Index, VExpr? Value, bool ValueIsDec, bool Desc = false, int Index2 = -1, VExpr? Order = null, bool OutDec = false, bool ValueIsText = false)
{
    /// <summary>For a `last`: the row set it takes its value from, and its place among the members; -1 for any other aggregate.</summary>
    public int RowSet { get; init; } = -1;
    public int Member { get; init; }
    /// <summary>For a `percentile`: its p, from 0 to 100.</summary>
    public decimal Percent { get; init; }
}

/// <summary>Group (and the accumulation half of Rollup): per-key accumulators updated by signed weights (DBSP linear aggregates). Output is a retraction plus an insertion per touched key.</summary>
abstract class AggregatingOp : PhysOp
{
    protected readonly VExpr[] keyExprs; protected readonly AggSpec[] aggs; protected readonly int nL, nD; readonly bool hasLongs, hasDecimals; protected Column?[] orderCols = [];
    readonly RuntimeFaults faults;
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

    /// <summary>Companion columns, when the operator feeds only sinks: per `mean` its sum and count (`{target}#sum`, `{target}#count`), per `last` with a numeric order its order (`{target}#order`). A sink that writes the aggregate to a event property stores the pair, so two windows of one class combine as one (Law 7.5 item 3 (i)).</summary>
    readonly (int Agg, int ColA, int ColB)[] companions = [];

    /// <summary>The names of the companion columns an aggregate has, if any.</summary>
    public static string[] CompanionNames(TAgg a) => a.Fn switch { "mean" => [a.Target + "#sum", a.Target + "#count"], "last" when a.Order?.Type.Ty.Tag is not (Tag.Text or Tag.Ent) => [a.Target + "#order"], _ => [] };

    protected AggregatingOp(TOp t, IReadOnlyList<(string Name, TExpr Value)> keys, IReadOnlyList<TAgg> aggSpecs, Layout inLayout, OpContext cx, string[] outKeyNames, bool withCompanions = false) : base(t)
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
                case "sum" when dec: list.Add(new AggSpec(AggKind.SumD, l, v, true)); l += 4; break;       // exact sums (#159 G4): a decimal sum in four accumulators, an integer sum in two
                case "sum": list.Add(new AggSpec(AggKind.SumL, l, v, false)); l += 2; break;
                case "mean": list.Add(new AggSpec(AggKind.Mean, l, v, false, false, l + 4)); l += 5; break;
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
                case "percentile" when dec: list.Add(new AggSpec(AggKind.PctD, d++, v, true) { Percent = a.Percent!.Value }); hasDecimals = true; break;       // its values are filed under an accumulator it does not use
                case "percentile": list.Add(new AggSpec(AggKind.PctL, l++, v, false) { Percent = a.Percent!.Value }); hasLongs = true; break;
                default: throw new NotSupportedException($"aggregate {a.Fn}");
            }
        }
        it = cx.It; faults = cx.Faults;
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
                case AggKind.SumL: { var c = valueCols[a]!; if (c.P is null || c.P[row]) t.AddSum(slot, s.Index, (Int128)w * c.L[row]); break; }
                case AggKind.SumD: { var c = valueCols[a]!; if (c.P is null || c.P[row]) t.AddSum(slot, s.Index, c.IsDec ? c.D[row] : c.L[row], w); break; }
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
                case AggKind.PctL or AggKind.PctD:
                    {
                        var c = valueCols[a]!;
                        if (c.P is not null && !c.P[row]) break;
                        if (s.Kind == AggKind.PctL) t.AddLong(slot, s.Index, c.L[row], w); else t.AddDecimal(slot, s.Index, c.IsDec ? c.D[row] : c.L[row], w);
                        break;
                    }
                case AggKind.Mean:
                    {
                        var c = valueCols[a]!;
                        if (c.P is not null && !c.P[row]) break;
                        t.AddSum(slot, s.Index, c.IsDec ? c.D[row] : c.L[row], w);
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
        AggKind.SumL => (long)t.Sum(slot, aggs[a].Index),
        AggKind.ExtL => t.ExtremeL(slot, aggs[a].Index, out var v) ? v : 0,
        AggKind.Distinct => t.L(slot, aggs[a].Index2),
        AggKind.Last when !aggs[a].OutDec => LastOf(t, slot, aggs[a]) is { } l ? (long)l.Val : 0,
        _ => 0,
    };
    protected decimal AggD(KeyTable t, int slot, int a) => aggs[a].Kind switch
    {
        AggKind.ExtD => t.ExtremeD(slot, aggs[a].Index, out var v) ? v : 0,
        AggKind.Mean => t.L(slot, aggs[a].Index2) is var n && n != 0 && t.DecimalSum(slot, aggs[a].Index, out var s) ? s / n : 0,
        AggKind.Last => LastOf(t, slot, aggs[a]) is { } l ? l.Val : 0,
        AggKind.SumD => t.DecimalSum(slot, aggs[a].Index, out var s) ? s : 0,
        _ => t.D(slot, aggs[a].Index),
    };
    /// <summary>A sum, or the sum of a mean, that the output type cannot hold exactly (#159 G4): absent, and counted under overflow.</summary>
    protected bool OutOfRange(KeyTable t, int slot, int a) => aggs[a].Kind switch
    {
        AggKind.SumL => t.Sum(slot, aggs[a].Index) is var s && (s < long.MinValue || s > long.MaxValue),
        AggKind.SumD or AggKind.Mean => !t.DecimalSum(slot, aggs[a].Index, out _),
        _ => false,
    };
    /// <summary>A min or max over no non-null value is null.</summary>
    protected bool AggAbsent(KeyTable t, int slot, int a) => aggs[a].Kind switch
    {
        AggKind.ExtL => !t.ExtremeL(slot, aggs[a].Index, out _),
        AggKind.ExtD => !t.ExtremeD(slot, aggs[a].Index, out _),
        AggKind.Mean => t.L(slot, aggs[a].Index2) == 0 || OutOfRange(t, slot, a),
        AggKind.Last => LastOf(t, slot, aggs[a]) is null,
        AggKind.SumL or AggKind.SumD => OutOfRange(t, slot, a),
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
            if (aggs[a].Kind is AggKind.PctL or AggKind.PctD)
            {
                if (!t.Percentile(slot, aggs[a].Index, aggs[a].Kind == AggKind.PctD, aggs[a].Percent, out var p)) col.SetAbsent(row, bt.N);
                else if (col.IsDec) col.D[row] = p; else col.L[row] = (long)p;
                continue;
            }
            if (aggs[a].Kind is AggKind.ExtL or AggKind.ExtD or AggKind.Mean or AggKind.Last or AggKind.SumL or AggKind.SumD && AggAbsent(t, slot, a))
            {
                if (weight > 0 && OutOfRange(t, slot, a)) faults.Overflow++;
                col.SetAbsent(row, bt.N); continue;
            }
            if (aggs[a].Kind is AggKind.SumD or AggKind.ExtD or AggKind.Mean || aggs[a].Kind == AggKind.Last && aggs[a].OutDec) col.D[row] = AggD(t, slot, a);
            else col.L[row] = AggL(t, slot, a);
        }
    }

    /// <summary>The pair of a `mean` (sum, count) or a `last` (order, unused) of one slot now.</summary>
    (decimal A, decimal B) AuxOf(KeyTable t, int slot, int a) => aggs[a].Kind == AggKind.Mean
        ? (t.DecimalSum(slot, aggs[a].Index, out var s) ? s : 0, t.L(slot, aggs[a].Index2))
        : (LastOf(t, slot, aggs[a])!.Value.Ord, 0);
}

/// <summary>Group by keys (no window): retract and insert per touched key at the end of each batch.</summary>
sealed class GroupOp : AggregatingOp
{
    readonly KeyTable table; readonly (string Role, int Col, bool IsRel, string Port)[] members;
    public GroupOp(TGroup t, Layout inLayout, OpContext cx, (string, int, bool, string)[]? members = null, bool withCompanions = false) : base(t, t.Keys, t.Aggs, inLayout, cx, [.. t.Keys.Select(k => k.Name)], withCompanions)
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
    readonly long width, lateness; readonly int wsKey, outWs;
    readonly int[] keyOutNoWs;
    long frontier = long.MinValue;
    readonly int nKeys;
    readonly List<(W Win, long Ws)> revised = [];
    readonly string name;
    /// <summary>Read by folds only: a window is given from the first row accepted for it, not from its end, so a fold reads it before the advance that drops it (#172 D2).</summary>
    readonly bool open;

    public WindowedGroupOp(TGroup t, long width, long lateness, Layout inLayout, OpContext cx, bool withCompanions = false, bool open = false)
        : base(t, t.Keys.Where(k => !(k.Name == "windowStart")).ToList(), t.Aggs, inLayout, cx, [.. t.Keys.Where(k => k.Name != "windowStart").Select(k => k.Name)], withCompanions)
    {
        this.width = width; this.lateness = lateness; name = t.Name; this.open = open;
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
                        if (OpContext.PastRevision(ws + width, frontier, lateness)) { cx.LateDropped++; cur = null; curWs = long.MinValue; continue; }
                        cur = new W(NewTable(), ws + width); windows[ws] = cur; if (cur.End <= frontier) cur.Closed = true;
                    }
                    curWs = ws;
                }
                var ok = true;
                for (var i = 0; i < nKeys; i++) { if (keyCols[i].P is not null && !keyCols[i].P![row]) { ok = false; break; } keyBuf[i] = KeyCode(keyCols[i], row); }
                if (!ok) continue;
                var slot = cur.Table.FindOrAdd(keyBuf.AsSpan(0, nKeys));
                if (cur.Closed || open)
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
                if (open) continue;
                var t = win.Table;
                foreach (var slot in t.Slots())
                    if (t.Cnt(slot) > 0) WriteRow(bb, t, slot, 1, ws, ws + width, outWs, -1);
            }
            foreach (var ws in windows.Where(w => OpContext.PastRevision(w.Value.End, frontier, lateness)).Select(w => w.Key).ToList())
            {
                windows.Remove(ws);
            }
        }
        return bb.Build();
    }
}

/// <summary>Rollup: sums of event rows per key and window. Inputs are already closed windows, so every change is a retraction plus an insertion.</summary>
sealed class RollupOp : AggregatingOp
{
    readonly KeyTable table; readonly int inWs, outWs, outWe; readonly long period;
    readonly List<long> slotWindow = [];

    // ---- no data (Def. 7.16) ----
    /// <summary>Per key (without the window) of a roll-up over a Series with a time to live: the marker records of its members by window, per member the weight of its normal rows and of its marker records, and the window of the marker record the key has in the output. A member is a row of the input without its window and values (`TRollup.Members`).</summary>
    sealed class Silence { public long[] Key = []; public readonly SortedDictionary<long, long> Markers = []; public readonly Dictionary<string, (long Normal, long Marked)> Members = []; public long? Emitted; }
    readonly Dictionary<string, Silence>? silence; readonly int inNoData = -1, outNoData = -1; readonly int[] memberCols = [];

    /// <summary>The key of a roll-up over a Series with a time to live is in no data iff every member that has a row has a marker record.</summary>
    public RollupOp(TRollup t, Layout inLayout, OpContext cx, bool withCompanions = false) : base(t, t.Keys, t.Aggs, inLayout, cx, [.. t.Keys.Select(k => k.Name)], withCompanions)
    {
        table = NewKeyTable(keyExprs.Length + 1);
        inWs = inLayout.Index("windowStart"); outWs = OutLayout.Index("windowStart"); outWe = OutLayout.Index("windowEnd"); period = t.PeriodNs;
        if (t.NoData) { silence = []; inNoData = inLayout.Index(TSeries.NoDataColumn); outNoData = OutLayout.Index(TSeries.NoDataColumn); memberCols = [.. t.Members!.Select(inLayout.Index)]; }
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

// ---- fold ------------------------------------------------------------------------------------------------

/// <summary>Fold (Ch. 6, Def. 6.5): per key a state over its windows in order, silent ones too, selected at the clock (Def. 6.2, s = close, u = ∞).
/// Windows follow Def. 6.4 on the grid of the hop of the nearest Window upstream, or the period of a Series: window `ws` is [ws, ws + width) and closes at
/// `close = ws + width + λ`, λ the lateness its program declares for these windows (#169 D1). The output at a clock is the transitions of the present rows whose window has
/// closed: a key's first window that is not silent and has a policy, and every window whose shown state differs from the one before. The policy of a window
/// is the present policy row (told apart as a whole row) of greatest `since` below its close, `since` being its stamp (Def. 6.6): the clock it entered at, 0 before
/// the clock first moves, kept while the row is present at every clock end. A step that moves the clock has its changes at the new clock.
/// The fold has a look-back (Def. 6.7): window `ws` leaves it at `ws + lookback`, the window of a Series when the Series drops it and never before it closes,
/// that of a Window at its close (#172 D1). At the advance that drops windows, a key is stepped through them with what the fold read before this step, their
/// transitions stay in the output and are no longer held, the state after them is the key's seed, and no row of them is read again. A windowed Group read by
/// folds only gives a window from its first row on (#172 D2). At a step that moves the clock, a key with no policy row and no window row in the look-back has
/// the seed none (#180), and once it holds no row it is forgotten.
/// Within the look-back the fold is a function of its present rows (D6): a row added or withdrawn at a closed window, or a policy row withdrawn, changes the key's
/// transitions from that window on (Prop. 6.7 (2)), and the operator retracts and writes only the rows that differ. Per key it keeps its seed, its policy rows, the
/// observations of its windows in the look-back, its states where they change and the transitions it wrote there. A key whose state a silent window leaves as it
/// is has reached its `rest` and is stepped again at its next observed window or the next window a policy row enters.</summary>
sealed class FoldOp : PhysOp
{
    /// <summary>One value of a column: its number or code, or absent.</summary>
    readonly record struct Cell(long L, decimal D, bool P)
    {
        public static Cell Of(Column c, int row) => c.Present(row) ? new(c.IsDec ? 0 : c.L[row], c.IsDec ? c.D[row] : 0, true) : default;
    }
    sealed class Cells : IEqualityComparer<Cell[]>
    {
        public bool Equals(Cell[]? a, Cell[]? b) => a.AsSpan().SequenceEqual(b);
        public int GetHashCode(Cell[] a) { var h = new HashCode(); foreach (var c in a) h.Add(c); return h.ToHashCode(); }
    }
    sealed class Policy(Cell[] whole, Cell[] zeta, long since) { public readonly Cell[] Whole = whole, Zeta = zeta; public readonly long Since = since; public long Count; }

    /// <summary>Entries kept by window start, ascending, `stride` values per window. A window is found, and the windows from one on are reached, by binary
    /// search; windows leave at the front without moving the others and arrive mostly at the end. Memory follows the windows held.</summary>
    sealed class ByWindow<T>(int stride)
    {
        long[] keys = []; T[] values = []; int head, count;
        public int Count => count;
        public long Key(int i) => keys[head + i];
        public Span<T> At(int i) => values.AsSpan((head + i) * stride, stride);
        public ref T this[int i] => ref values[(head + i) * stride];
        /// <summary>The index of window `ws`, else the complement of the index it would have.</summary>
        public int Find(long ws) { var i = Array.BinarySearch(keys, head, count, ws); return i < 0 ? ~(~i - head) : i - head; }
        /// <summary>The index of the first window at or after `ws`.</summary>
        public int From(long ws) { var i = Find(ws); return i < 0 ? ~i : i; }
        /// <summary>Makes window `ws` at index `i` (from `Find`), its values the default.</summary>
        public void Insert(int i, long ws)
        {
            if (head + count == keys.Length && count * 2 >= keys.Length) Resize(keys.Length == 0 ? 4 : keys.Length * 2);
            else if (head + count == keys.Length)          // room at the front: the windows move there
            {
                Array.Copy(keys, head, keys, 0, count); Array.Copy(values, head * stride, values, 0, count * stride);
                Array.Clear(values, count * stride, head * stride); head = 0;
            }
            Array.Copy(keys, head + i, keys, head + i + 1, count - i);
            Array.Copy(values, (head + i) * stride, values, (head + i + 1) * stride, (count - i) * stride);
            keys[head + i] = ws; Array.Clear(values, (head + i) * stride, stride);
            count++;
        }
        /// <summary>Sets window `ws` to `v` (stride 1).</summary>
        public void Set(long ws, T v) { var i = Find(ws); if (i < 0) Insert(i = ~i, ws); this[i] = v; }
        public void RemoveAt(int i)
        {
            Array.Copy(keys, head + i + 1, keys, head + i, count - i - 1);
            Array.Copy(values, (head + i + 1) * stride, values, (head + i) * stride, (count - i - 1) * stride);
            Array.Clear(values, (head + count - 1) * stride, stride);
            count--; Shrink();
        }
        /// <summary>Removes the windows from index `i` on.</summary>
        public void Cut(int i) { Array.Clear(values, (head + i) * stride, (count - i) * stride); count = i; Shrink(); }
        /// <summary>Removes the first `n` windows.</summary>
        public void Drop(int n) { Array.Clear(values, head * stride, n * stride); head += n; count -= n; Shrink(); }
        void Shrink()
        {
            if (count == 0) head = 0;
            if (keys.Length > 16 && count * 4 <= keys.Length) Resize(Math.Max(16, count * 2));
        }
        void Resize(int size)
        {
            var k = new long[size]; var v = new T[size * stride];
            Array.Copy(keys, head, k, 0, count); Array.Copy(values, head * stride, v, 0, count * stride);
            (keys, values, head) = (k, v, 0);
        }
    }

    /// <summary>A value a window has seen and its weight; empty at weight 0. A present window is `1 + aggregates` of these in place in `KeyState.Obs`: first the
    /// weight of its rows (silent at 0), then per observe aggregate one value and its weight. The other values of an aggregate are in `KeyState.Spill`, only while
    /// it has two or more; an aggregate with no value in place has no others.</summary>
    record struct Seen(Cell Value, long N);

    /// <summary>A key's mode in the fold. Between steps a key is Inactive (seed none and no n_k: it waits for a window that starts it), Moving (its last window
    /// changed its state: it is due at the close of the next window), Resting (a silent window left its state as it is: it is due at its next event) or Stopped
    /// (resting since `next` kept changing its state over `MaxQuiet` silent windows). Within a step a key that a change reached at a closed window, or whose close
    /// came, is Dirty: it is stepped from `From`, or from the window after `At` when that is earlier. `At` is the newest window stepped. Where a key is
    /// scheduled is a function of its mode (`DueWindow`).</summary>
    enum Phase { Inactive, Moving, Resting, Stopped, Dirty }
    /// <summary>What happens to a key: a change reaches closed window `ws`; its close comes; a job ends at window `ws` moving, at rest or stopped; no window
    /// starts it up to `ws`; the look-back drops windows up to `ws`; it has the seed none.</summary>
    enum Event { Changed, Due, Moved, Rested, Stopped, Unstarted, Dropped, Emptied }
    readonly record struct Mode(Phase Phase, long At, long From)
    {
        public static readonly Mode New = new(Phase.Inactive, long.MinValue, long.MaxValue);
        /// <summary>The transition function.</summary>
        public static Mode Next(Mode m, Event e, long ws) => e switch
        {
            Event.Changed => m with { Phase = Phase.Dirty, From = m.Phase == Phase.Dirty ? Math.Min(m.From, ws) : ws },
            Event.Due => m.Phase == Phase.Dirty ? m : m with { Phase = Phase.Dirty, From = long.MaxValue },
            Event.Moved => new(Phase.Moving, ws, long.MaxValue),
            Event.Rested => new(Phase.Resting, ws, long.MaxValue),
            Event.Stopped => new(Phase.Stopped, ws, long.MaxValue),
            Event.Unstarted => new(Phase.Inactive, ws, long.MaxValue),
            Event.Dropped => m with { At = Math.Max(m.At, ws) },
            Event.Emptied => m with { Phase = Phase.Inactive },
            _ => throw new ArgumentOutOfRangeException(nameof(e)),
        };
    }

    sealed class KeyState(int id, Cell[] key, int aggs)
    {
        public readonly int Id = id; public readonly Cell[] Key = key;
        /// <summary>The policy rows of the key, told apart by their whole row; one that left keeps its place until the clock advances.</summary>
        public readonly List<Policy> Policies = [];
        /// <summary>The windows in the look-back with a row or a value (`Seen`), and how many of them have a positive weight.</summary>
        public readonly ByWindow<Seen> Obs = new(1 + aggs);
        public int Present;
        /// <summary>Per window and observe aggregate with two or more values, the ones not in place.</summary>
        public Dictionary<(long Ws, int Agg), Dictionary<Cell, long>>? Spill;
        /// <summary>The state after the dropped windows (Def. 6.7), null for none.</summary>
        public Cell[]? Seed;
        /// <summary>With the seed none, the window that started the key (n_k), null while none did; the states σ_n there and at every window where the state
        /// changes, up to `Mode.At`.</summary>
        public long? Start; public readonly ByWindow<Cell[]> Trace = new(1);
        /// <summary>The transitions written, by window.</summary>
        public readonly ByWindow<Cell[]> Out = new(1);
        public Mode Mode = Mode.New;
        /// <summary>Whether the key was stopped once: its fault is counted once.</summary>
        public bool Faulted;
        /// <summary>The close it is due at in `due`; its least window, its place in `held`.</summary>
        public long Due = long.MaxValue, HeldAt = long.MaxValue;
    }
    /// <summary>A key being stepped from window `N` to `Last`, from the state `State` (σ_{N−1}; null at n_k); `Then`: how its last window left it.</summary>
    sealed class Job(KeyState st, long from, long n, long last, Cell[]? state)
    {
        public readonly KeyState St = st; public readonly long From = from, Last = last; public long N = n; public Cell[]? State = state;
        public int Quiet; public Event Then = Event.Moved;
        public readonly List<(long Ws, Cell[] Row)> Rows = [];
        public (Cell[] Zeta, long Since)? Zeta; public Cell[] Obs = []; public bool Silent;
    }
    /// <summary>Def. 6.5 item 3 asks a fold to come to rest after finitely many silent windows. A fold whose `next` keeps changing the state over this many silent
    /// windows in a row is stopped there and counted once per key under invalid parameters, so a jump of the clock cannot make it step without end.</summary>
    const int MaxQuiet = 1 << 16;

    /// <summary>Def. 6.5 for one window of a job, a calculation: from the state before and after `next` (or `start` at n_k, `first`), whether the window writes
    /// a transition (the shown state changed, or n_k), whether the state is traced (it changed, or n_k), the silent windows in a row, and how it leaves the key.</summary>
    static (bool Writes, bool Traces, int Quiet, Event Then) StepWindow(bool first, Cell[] before, Cell[] after, int show, bool silent, int quiet)
    {
        var same = after.AsSpan().SequenceEqual(before);
        quiet = silent ? quiet + 1 : 0;
        var then = silent && same ? Event.Rested : quiet >= MaxQuiet ? Event.Stopped : Event.Moved;
        return (first || after[show] != before[show], first || !same, quiet, then);
    }

    readonly TFold t; readonly long width, hop, lambda, lookback;
    readonly VExpr[] keys, observed, starts, nexts; readonly int[] polKey, polZeta;
    readonly int inWs, inNoData = -1, show;
    readonly int[] outKey, outObs, outZeta, stepZeta, stepState, stepObs; readonly int outWs, outWe, outFrom, outTo, outSince;
    readonly Dictionary<Cell[], KeyState> states = new(new Cells());
    /// <summary>The keys by id; the id of a forgotten key is free for the next new key.</summary>
    readonly List<KeyState?> byId = []; readonly Stack<int> free = [];
    readonly SortedSet<(long Close, int Id)> due = [];
    /// <summary>Per key that holds a window, its least one, so the advance that drops windows finds their keys.</summary>
    readonly SortedSet<(long Ws, int Id)> held = [];
    readonly HashSet<KeyState> pending = [], left = [];
    /// <summary>The keys whose rows changed since the clock last moved, which may now hold none.</summary>
    readonly HashSet<KeyState> suspects = [];
    /// <summary>The policy rows a step changed, with their key and whether they were present before it.</summary>
    readonly Dictionary<Policy, (KeyState St, bool Was)> changed = [];
    readonly BatchBuilder stepRows;
    long frontier = long.MinValue;
    /// <summary>first(τ) of Def. 6.7 at the clock: the least window in the look-back.</summary>
    long first = long.MinValue;
    /// <summary>The windows a job reached: stepped with `next` or `start`, or found with no policy in force.</summary>
    long steps;

    public FoldOp(TFold t, Layout inLayout, Layout policyLayout, OpContext cx, long width, long hop, long lambda, long lookback) : base(t)
    {
        this.t = t; this.width = width; this.hop = hop; this.lambda = lambda; this.lookback = lookback;
        keys = [.. t.Keys.Select(k => ExprCompiler.Compile(k.Value, inLayout, cx.It, cx.Faults))];
        observed = [.. t.Observe.Select(o => ExprCompiler.Compile(o.Value!, inLayout, cx.It, cx.Faults))];
        var step = Layout.Of(t.Step);
        starts = [.. t.States.Select(s => ExprCompiler.Compile(s.Start, step, cx.It, cx.Faults))];
        nexts = [.. t.States.Select(s => ExprCompiler.Compile(s.Next, step, cx.It, cx.Faults))];
        polKey = [.. t.Keys.Select(k => policyLayout.Index(k.Name))]; polZeta = [.. t.Policy.Select(c => policyLayout.Index(c.Name))];
        inWs = inLayout.Index("windowStart");
        if (t.NoData) inNoData = inLayout.Index(TSeries.NoDataColumn);
        show = t.States.Select(s => s.Name).ToList().IndexOf(t.Show);
        outKey = [.. t.Keys.Select(k => OutLayout.Index(k.Name))]; outObs = [.. t.Observe.Select(o => OutLayout.Index(o.Target))]; outZeta = [.. t.Policy.Select(c => OutLayout.Index(c.Name))];
        (outWs, outWe, outFrom, outTo, outSince) = (OutLayout.Index("windowStart"), OutLayout.Index("windowEnd"), OutLayout.Index("from"), OutLayout.Index("to"), OutLayout.Index(TFold.Since));
        stepZeta = [.. t.Policy.Select(c => step.Index(c.Name))]; stepState = [.. t.States.Select(s => step.Index(s.Name))]; stepObs = [.. t.Observe.Select(o => step.Index(o.Target))];
        stepRows = new BatchBuilder(step);
    }

    long Close(long ws) => OpContext.Clamp((Int128)ws + width + lambda);
    long After(long ws) => OpContext.Clamp((Int128)ws + hop);
    /// <summary>The start of the newest window on the grid of the hop that has closed, if one has.</summary>
    long? LastClosed()
    {
        if (frontier == long.MinValue) return null;
        var bound = (Int128)frontier - width - lambda;
        if (bound < long.MinValue) return null;
        var q = bound / hop; if (bound % hop < 0) q--;
        return (long)(q * hop);
    }
    /// <summary>The first window on the grid that closes after `since`: where a policy row of that `since` starts to count.</summary>
    long Enters(long since)
    {
        var bound = (Int128)since - width - lambda;
        var q = bound / hop; if (bound % hop < 0) q--;
        return OpContext.Clamp((q + 1) * hop);
    }
    /// <summary>first(τ) of Def. 6.7: the least window on the grid of the hop with ws + lookback > τ.</summary>
    long FirstOf(long tau)
    {
        var bound = (Int128)tau - lookback;
        var q = bound / hop; if (bound % hop < 0) q--;
        return OpContext.Clamp((q + 1) * hop);
    }

    KeyState StateOf(Cell[] key)
    {
        if (states.TryGetValue(key, out var st)) return st;
        var id = free.Count > 0 ? free.Pop() : byId.Count;
        st = new KeyState(id, key, observed.Length); states[key] = st;
        if (id == byId.Count) byId.Add(st); else byId[id] = st;
        suspects.Add(st);
        return st;
    }

    /// <summary>The policy in force at window `ws`: the present rows with `since` below its close and the greatest `since` among them, when they hold one policy.</summary>
    (Cell[] Zeta, long Since)? InForce(KeyState st, long ws)
    {
        var close = Close(ws);
        Policy? best = null; var tie = false;
        foreach (var p in st.Policies)
        {
            if (p.Count <= 0 || p.Since >= close) continue;
            if (best is null || p.Since > best.Since) { best = p; tie = false; }
            else if (p.Since == best.Since && !p.Zeta.AsSpan().SequenceEqual(best.Zeta)) tie = true;
        }
        return best is null || tie ? null : (best.Zeta, best.Since);
    }

    /// <summary>The first window after `ws` where a present policy row enters. The policy in force changes only there, so a window with none in force keeps
    /// none up to it.</summary>
    long NextPolicy(KeyState st, long ws)
    {
        var next = long.MaxValue;
        foreach (var p in st.Policies) if (p.Count > 0 && Enters(p.Since) is var e && e > ws && e < next) next = e;
        return next;
    }

    /// <summary>The next window after `ws` where something can move a key at rest: its next window that is not silent, or the next where a policy row enters.</summary>
    long NextEvent(KeyState st, long ws)
    {
        var next = long.MaxValue;
        for (var i = st.Obs.From(OpContext.Clamp((Int128)ws + 1)); i < st.Obs.Count && st.Present > 0; i++)
            if (st.Obs.At(i)[0].N > 0) { next = st.Obs.Key(i); break; }
        return Math.Min(next, NextPolicy(st, ws));
    }

    /// <summary>The window whose close a key waits for: the next one while it moves, else its next event; none while it is dirty, or inactive with no
    /// policy row (a policy row that comes touches the key).</summary>
    long DueWindow(KeyState st) => st.Mode.Phase switch
    {
        Phase.Dirty => long.MaxValue,
        Phase.Inactive when !st.Policies.Any(p => p.Count > 0) => long.MaxValue,
        Phase.Moving => After(st.Mode.At),
        _ => NextEvent(st, st.Mode.At),
    };

    void Schedule(KeyState st)
    {
        if (st.Due != long.MaxValue) due.Remove((st.Due, st.Id));
        var ws = DueWindow(st);
        st.Due = ws == long.MaxValue ? long.MaxValue : Close(ws);
        if (st.Due != long.MaxValue) due.Add((st.Due, st.Id));
    }

    /// <summary>Keeps the key's place in `held` at its least window.</summary>
    void Hold(KeyState st)
    {
        var least = Math.Min(st.Obs.Count > 0 ? st.Obs.Key(0) : long.MaxValue, Math.Min(st.Trace.Count > 0 ? st.Trace.Key(0) : long.MaxValue, st.Out.Count > 0 ? st.Out.Key(0) : long.MaxValue));
        if (least == st.HeldAt) return;
        if (st.HeldAt != long.MaxValue) held.Remove((st.HeldAt, st.Id));
        st.HeldAt = least;
        if (least != long.MaxValue) held.Add((least, st.Id));
    }

    void Touch(KeyState st, long ws)
    {
        ws = Math.Max(ws, first);       // the windows below the look-back are dropped: no change reaches them
        if (LastClosed() is { } last && ws <= last) { st.Mode = Mode.Next(st.Mode, Event.Changed, ws); pending.Add(st); }
        Schedule(st);
    }

    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var bb = EmptyOutput();
        var advanced = ep.HasAdvance && ep.Lambda > frontier;
        if (advanced)
        {
            // Def. 6.6: a policy row keeps its stamp while it has a row at every clock end; one that left during the clock that ends here is gone
            foreach (var st in left) { st.Policies.RemoveAll(x => x.Count <= 0); suspects.Add(st); }
            left.Clear();
            frontier = ep.Lambda;
            if (FirstOf(frontier) is var to && to > first) Drop(bb, cx, to);
        }
        if (In(inputs, "policy") is { Rows: > 0 } pb)
        {
            for (var k = 0; k < pb.Rows; k++)
            {
                var row = pb.RowAt(k);
                var key = polKey.Select(i => Cell.Of(pb.Cols[i], row)).ToArray();
                if (key.Any(c => !c.P)) continue;
                var whole = pb.Cols.Select(c => Cell.Of(c, row)).ToArray();
                var st = StateOf(key); var w = pb.Weight(row);
                var p = st.Policies.Find(x => x.Whole.AsSpan().SequenceEqual(whole));
                if (p is null)
                {
                    if (w <= 0) continue;
                    st.Policies.Add(p = new Policy(whole, [.. polZeta.Select(i => whole[i])], frontier == long.MinValue ? 0 : frontier));        // stamped with the clock after this step's advance, 0 before the clock first moves
                }
                changed.TryAdd(p, (st, p.Count > 0));
                p.Count += w;
                if (p.Count <= 0) { left.Add(st); suspects.Add(st); }
            }
            // a row that leaves or comes back changes the key from the first window it is in force at; one that leaves and comes back in the step changes nothing
            foreach (var (p, (st, was)) in changed) if (was != p.Count > 0) Touch(st, Enters(p.Since));
            changed.Clear();
        }
        Read(In(inputs, "in"));
        while (due.Count > 0 && due.Min.Close <= frontier) Due();
        if (pending.Count > 0 && LastClosed() is { } closed) Step(bb, cx, closed);
        pending.Clear();
        Forget(bb, advanced);
        return bb.Build();
    }

    /// <summary>Takes the key that is due first off the schedule, to be stepped.</summary>
    void Due()
    {
        var (close, id) = due.Min; due.Remove((close, id));
        var st = byId[id]!; st.Due = long.MaxValue;
        st.Mode = Mode.Next(st.Mode, Event.Due, 0); pending.Add(st);
    }

    /// <summary>Reads window rows: the observations of the windows in the look-back; a row of a dropped window is not read (Def. 6.7).</summary>
    void Read(Batch? b)
    {
        if (b is not { Rows: > 0 }) return;
        var kc = keys.Select(k => k.Eval(b)).ToArray();
        var oc = observed.Select(o => o.Eval(b)).ToArray();
        for (var k = 0; k < b.Rows; k++)
        {
            var row = b.RowAt(k); var w = b.Weight(row);
            if (inNoData >= 0 && b.Cols[inNoData].L[row] != 0) continue;           // a series' no-data marker is no observation (#151 D3)
            var ws = b.Cols[inWs].L[row];
            if (ws < first) continue;
            var key = kc.Select(c => Cell.Of(c, row)).ToArray();
            if (key.Any(c => !c.P)) continue;
            var st = StateOf(key);
            var i = st.Obs.Find(ws);
            if (i < 0) st.Obs.Insert(i = ~i, ws);
            var win = st.Obs.At(i);
            var was = win[0].N > 0;
            win[0].N += w;
            for (var a = 0; a < oc.Length; a++)
                if (Cell.Of(oc[a], row) is { P: true } v) See(st, ws, a, ref win[1 + a], v, w);
            if (was != win[0].N > 0) st.Present += was ? -1 : 1;
            if (win[0].N <= 0) suspects.Add(st);
            var empty = true;
            foreach (var s in win) empty &= s.N == 0;
            if (empty) st.Obs.RemoveAt(i);
            Hold(st);
            Touch(st, ws);
        }
    }

    /// <summary>Adds weight `w` of value `v` to observe aggregate `agg` of window `ws`, whose value in place is `at`.</summary>
    static void See(KeyState st, long ws, int agg, ref Seen at, Cell v, long w)
    {
        if (w == 0) return;
        if (at.N == 0) { at = new(v, w); return; }
        if (at.Value == v)
        {
            at.N += w;
            if (at.N == 0 && st.Spill is { } sp && sp.Remove((ws, agg), out var rest))         // another value takes its place
            {
                var (nv, nn) = rest.First(); rest.Remove(nv);
                at = new(nv, nn);
                if (rest.Count > 0) sp[(ws, agg)] = rest;
                else if (sp.Count == 0) st.Spill = null;
            }
            return;
        }
        var others = (st.Spill ??= []).TryGetValue((ws, agg), out var o) ? o : st.Spill[(ws, agg)] = [];
        var n = others.GetValueOrDefault(v) + w;
        if (n != 0) others[v] = n;
        else if (others.Remove(v) && others.Count == 0 && st.Spill.Remove((ws, agg)) && st.Spill.Count == 0) st.Spill = null;
    }

    /// <summary>What the observe aggregates read of window `ws`: the number of its distinct values, or the least or greatest of them, a decimal without trailing
    /// zeros; absent in a window that is not held.</summary>
    Cell[] Observed(KeyState st, long ws, out bool silent)
    {
        var i = st.Obs.Find(ws);
        silent = i < 0 || st.Obs.At(i)[0].N <= 0;
        var r = new Cell[observed.Length];
        for (var a = 0; a < r.Length; a++)
            r[a] = i < 0 ? Observe(t.Observe[a].Fn, default, null) : Observe(t.Observe[a].Fn, st.Obs.At(i)[1 + a], st.Spill?.GetValueOrDefault((ws, a)));
        return r;
    }

    static Cell Observe(string fn, Seen at, Dictionary<Cell, long>? others)
    {
        if (fn == "count") return new((at.N > 0 ? 1 : 0) + (others?.Count(v => v.Value > 0) ?? 0), 0, true);
        Cell? best = at.N > 0 ? at.Value : null;
        foreach (var (v, n) in others ?? [])
            if (n > 0 && (best is not { } b || (fn == "min" ? Less(v, b) : Less(b, v)))) best = v;
        return best is { } x ? x with { D = DecimalKey.Normal(x.D) } : default;
        static bool Less(Cell a, Cell b) => a.D != b.D ? a.D < b.D : a.L < b.L;
    }

    /// <summary>The job of a key that changed or is due: from the first window a change reached, else from the window after the newest stepped one, up to
    /// `last`; from its seed, or with the seed none from n_k afresh when the change reached n_k or the key is not active. Null when there is nothing to step.</summary>
    Job? JobOf(KeyState st, long last)
    {
        var from = Math.Max(Math.Min(st.Mode.From, After(st.Mode.At)), first);
        if (st.Seed is not null || st.Start is { } nk && from > nk)
        {
            st.Trace.Cut(st.Trace.From(from));
            var before = st.Trace.Count > 0 ? st.Trace[st.Trace.Count - 1] : st.Seed;
            return new Job(st, from, from, last, before);
        }
        st.Start = null; st.Trace.Cut(0);
        // n_k: the first window that is not silent with a policy in force, at or after the first window a present policy row enters
        var enters = long.MaxValue;
        foreach (var p in st.Policies) if (p.Count > 0) enters = Math.Min(enters, Enters(p.Since));
        for (var i = st.Obs.From(enters); i < st.Obs.Count && st.Obs.Key(i) <= last; i++)
            if (st.Obs.At(i)[0].N > 0 && InForce(st, st.Obs.Key(i)) is not null) { st.Start = st.Obs.Key(i); return new Job(st, long.MinValue, st.Obs.Key(i), last, null); }
        st.Mode = Mode.Next(st.Mode, Event.Unstarted, last);
        return null;
    }

    /// <summary>Steps the changed and due keys to the newest closed window, in rounds: each round steps one window of every key that needs `start` or `next`,
    /// with one evaluation of them over all of these keys.</summary>
    void Step(BatchBuilder bb, OpContext cx, long last)
    {
        var jobs = new List<Job>();
        foreach (var st in pending)
            if (JobOf(st, last) is { } job) jobs.Add(job);
            else { Retract(st, long.MinValue, [], bb); Schedule(st); Hold(st); }
        var round = new List<Job>();
        while (jobs.Count > 0)
        {
            round.Clear();
            foreach (var job in jobs)
            {
                // with no policy in force the state stays up to the next window a policy row enters, which needs no evaluation
                while (job.N <= job.Last && job.State is not null && (job.Zeta = InForce(job.St, job.N)) is null)
                {
                    steps++; job.Quiet = 0; job.Then = Event.Rested;
                    job.N = Math.Min(NextPolicy(job.St, job.N), After(job.Last));
                }
                if (job.N > job.Last) { Finish(job, bb); continue; }
                job.Zeta ??= InForce(job.St, job.N);
                job.Obs = Observed(job.St, job.N, out job.Silent);
                round.Add(job);
            }
            jobs.RemoveAll(j => j.N > j.Last);
            if (round.Count == 0) continue;
            stepRows.Reset();
            for (var j = 0; j < round.Count; j++)
            {
                var job = round[j];
                var r = stepRows.Add(1);
                for (var i = 0; i < stepZeta.Length; i++) Put(stepRows, r, stepZeta[i], job.Zeta!.Value.Zeta[i]);
                if (job.State is not null) for (var i = 0; i < stepState.Length; i++) Put(stepRows, r, stepState[i], job.State[i]);
                for (var i = 0; i < stepObs.Length; i++) Put(stepRows, r, stepObs[i], job.Obs[i]);
            }
            var sb = stepRows.Batch;
            steps += round.Count;
            if (round.Any(x => x.State is null))
                for (var i = 0; i < starts.Length; i++)
                {
                    var c = starts[i].Eval(sb);
                    var start = new Cell[round.Count];
                    for (var j = 0; j < round.Count; j++) if (round[j].State is null) start[j] = Cell.Of(c, j);
                    for (var j = 0; j < round.Count; j++) if (round[j].State is null) Put(stepRows, j, stepState[i], start[j]);
                }
            var before = new Cell[round.Count][];
            for (var j = 0; j < round.Count; j++) before[j] = [.. stepState.Select(s => Cell.Of(sb.Cols[s], j))];
            var after = new Cell[round.Count][];
            for (var j = 0; j < round.Count; j++) after[j] = new Cell[nexts.Length];
            for (var i = 0; i < nexts.Length; i++) { var c = nexts[i].Eval(sb); for (var j = 0; j < round.Count; j++) after[j][i] = Cell.Of(c, j); }
            for (var j = 0; j < round.Count; j++)
            {
                var job = round[j]; var st = job.St; var ws = job.N;
                var (writes, traces, quiet, then) = StepWindow(job.State is null, before[j], after[j], show, job.Silent, job.Quiet);
                if (writes) job.Rows.Add((ws, Row(st, ws, before[j][show], after[j][show], job.Obs, job.Zeta!.Value)));
                if (traces) st.Trace.Set(ws, after[j]);
                if (then == Event.Stopped && !st.Faulted) { st.Faulted = true; cx.Faults.InvalidParameter++; }
                (job.Quiet, job.Then, job.State) = (quiet, then, after[j]);
                job.N = then == Event.Moved ? After(ws) : Math.Min(NextEvent(st, ws), After(job.Last));
            }
        }
    }

    /// <summary>Def. 6.7 at an advance that moves the first window of the look-back to `to`: the keys are stepped through the windows below it with what the
    /// fold read before this step, the transitions of those windows stay in the output and are no longer held, the state after them is the key's seed, and their
    /// observations are forgotten.</summary>
    void Drop(BatchBuilder bb, OpContext cx, long to)
    {
        var last = OpContext.Clamp((Int128)to - hop);
        while (due.Count > 0 && due.Min.Close <= Close(last)) Due();
        if (pending.Count > 0) Step(bb, cx, last);
        pending.Clear();
        while (held.Count > 0 && held.Min.Ws < to)
        {
            var st = byId[held.Min.Id]!;
            if (st.Trace.From(to) is var n and > 0) { st.Seed = st.Trace[n - 1]; st.Trace.Drop(n); st.Start = null; }
            var o = st.Obs.From(to);
            for (var i = 0; i < o; i++)
            {
                if (st.Obs.At(i)[0].N > 0) st.Present--;
                if (st.Spill is { } sp) { for (var a = 0; a < observed.Length; a++) sp.Remove((st.Obs.Key(i), a)); if (sp.Count == 0) st.Spill = null; }
            }
            st.Obs.Drop(o);
            st.Out.Drop(st.Out.From(to));
            st.Mode = Mode.Next(st.Mode, Event.Dropped, last);
            suspects.Add(st);
            Hold(st);
        }
        first = to;
    }

    /// <summary>Def. 6.7: at a step that moves the clock, a key with no policy row and no window row in the look-back has the seed none; while the clock
    /// stands a key keeps its seed, so how the rows between two moves of the clock are cut does not change it (#180). A key that holds no row is forgotten.</summary>
    void Forget(BatchBuilder bb, bool advanced)
    {
        suspects.RemoveWhere(st =>
        {
            if (advanced && st.Present == 0 && !st.Policies.Any(p => p.Count > 0) && (st.Seed is not null || st.Start is not null))
            {
                st.Seed = null; st.Start = null; st.Trace.Cut(0);
                st.Mode = Mode.Next(st.Mode, Event.Emptied, 0);
                Retract(st, long.MinValue, [], bb);
                Schedule(st); Hold(st);
            }
            if (st.Policies.Count > 0 || st.Obs.Count > 0 || st.Seed is not null || st.Start is not null || st.Out.Count > 0) return advanced;
            if (st.Due != long.MaxValue) due.Remove((st.Due, st.Id));
            if (st.HeldAt != long.MaxValue) held.Remove((st.HeldAt, st.Id));
            states.Remove(st.Key); byId[st.Id] = null; free.Push(st.Id);
            return true;
        });
    }

    /// <summary>Ends a job: the key's transitions from the job's first window on are replaced by the ones it found, writing only the rows that differ.</summary>
    void Finish(Job job, BatchBuilder bb)
    {
        var st = job.St;
        Retract(st, job.From, job.Rows, bb);
        st.Mode = Mode.Next(st.Mode, job.Then, job.Last);
        Schedule(st); Hold(st);
    }

    /// <summary>Replaces the key's transitions from window `from` on by `rows` (ascending by window): retracts the old rows that differ, then writes the new ones.</summary>
    void Retract(KeyState st, long from, List<(long Ws, Cell[] Row)> rows, BatchBuilder bb)
    {
        var i0 = st.Out.From(from);
        for (int i = i0, j = 0; i < st.Out.Count; i++)
        {
            var ws = st.Out.Key(i);
            while (j < rows.Count && rows[j].Ws < ws) j++;
            if (j == rows.Count || rows[j].Ws != ws || !rows[j].Row.AsSpan().SequenceEqual(st.Out[i])) Write(bb, st.Out[i], -1);
        }
        for (int i = i0, j = 0; j < rows.Count; j++)
        {
            while (i < st.Out.Count && st.Out.Key(i) < rows[j].Ws) i++;
            if (i == st.Out.Count || st.Out.Key(i) != rows[j].Ws || !rows[j].Row.AsSpan().SequenceEqual(st.Out[i])) Write(bb, rows[j].Row, 1);
        }
        st.Out.Cut(i0);
        foreach (var (ws, row) in rows) st.Out.Set(ws, row);
    }

    static void Put(BatchBuilder bb, int row, int col, Cell v)
    {
        var c = bb.Batch.Cols[col];
        if (c.IsDec) c.D[row] = v.D; else c.L[row] = v.L;
        if (!v.P) bb.Absent(row, col);
    }

    /// <summary>A transition row in the order of the output columns: key, window start and end, from, to, observation, policy, since.</summary>
    Cell[] Row(KeyState st, long ws, Cell from, Cell to, Cell[] seen, (Cell[] Zeta, long Since) zeta) =>
        [.. st.Key, new(ws, 0, true), new(OpContext.Clamp((Int128)ws + width), 0, true), from, to, .. seen, .. zeta.Zeta, new(zeta.Since, 0, true)];

    void Write(BatchBuilder bb, Cell[] cells, long weight)
    {
        var row = bb.Add(weight); var c = 0;
        foreach (var i in outKey) Put(bb, row, i, cells[c++]);
        Put(bb, row, outWs, cells[c++]); Put(bb, row, outWe, cells[c++]); Put(bb, row, outFrom, cells[c++]); Put(bb, row, outTo, cells[c++]);
        foreach (var i in outObs) Put(bb, row, i, cells[c++]);
        foreach (var i in outZeta) Put(bb, row, i, cells[c++]);
        Put(bb, row, outSince, cells[c]);
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
/// <summary>Decimals as key codes: equal numbers get one code whatever their scale, so a Decimal can be a group, roll-up or fold key and the argument of countDistinct in tables keyed by 64-bit codes.</summary>
sealed class DecimalCodes
{
    readonly Dictionary<decimal, long> codes = []; readonly List<decimal> values = [];
    public long Code(decimal d) { if (!codes.TryGetValue(d, out var c)) { codes[d] = c = values.Count; values.Add(DecimalKey.Normal(d)); } return c; }
    public decimal Value(long code) => values[(int)code];
}

/// <summary>The two halves of a decimal as a key: equal numbers give equal halves whatever their scale (2.5 and 2.50).</summary>
static class DecimalKey
{
    public static (long Lo, long Hi) Of(decimal d)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(Normal(d), bits);
        return (((long)bits[0] << 32) | (uint)bits[1], ((long)bits[2] << 32) | (uint)bits[3]);
    }
    /// <summary>The decimal without trailing zeros: dividing by one at full scale drops them.</summary>
    public static decimal Normal(decimal d) => d / 1.0000000000000000000000000000m;
    /// <summary>The number of a key: the decimal without trailing zeros.</summary>
    public static decimal Number(long lo, long hi) => new([(int)(lo >> 32), (int)lo, (int)(hi >> 32), (int)hi]);
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
                if (dst.IsDec) dst.D[row] = DecimalKey.Normal(col.D[src]); else dst.L[row] = col.L[src];
            }
        }
        return bb.Build();
    }
}

// ---- fixpoint ----------------------------------------------------------------------------------------------

/// <summary>Fixpoint (Def. 7.3 (F)): per node the least value of its equation, the greatest position of its own values and of the values of its dependencies, each
/// through the image list of its edge. A value is a position in the order, 0 for no value. State per node: the net weight of its own rows per position, its edges
/// to dependencies with their net weights (by dependency and image list), the nodes with a positive edge to it, its value, and the value it emitted. A step finds the
/// nodes its rows name (`Ch`: the key of a node row, the from of an edge row), the nodes that reach them over positive edges (`Aff`, Theorem 7.6), sets those to no
/// value and evaluates each once, and again when one of its dependencies in `Aff` grows; no other node is read or evaluated. A row without its key, from or to names
/// no node and counts as missing; a decimal key is its normalized number; a value outside the order counts as no value and as an invalid parameter.</summary>
public sealed class FixpointOp : PhysOp
{
    readonly record struct NodeKey(long A, long B);
    sealed class Node(int positions)
    {
        public readonly long[] Own = new long[positions];
        /// <summary>Edges to dependencies: (dependency, image list or -1 for none) to net weight; a positive weight makes it a term of the equation.</summary>
        public readonly Dictionary<(NodeKey To, int Map), long> Out = [];
        /// <summary>The nodes with a positive edge to this one, with the number of such edges.</summary>
        public readonly Dictionary<NodeKey, int> In = [];
        public int Z, Emitted;
        public bool Present() { foreach (var w in Own) if (w > 0) return true; return false; }
        public bool Idle() => Z == 0 && Emitted == 0 && Out.Count == 0 && In.Count == 0 && Array.TrueForAll(Own, w => w == 0);
    }

    readonly int key, value, from, to, by, positions;
    readonly bool keyDec, valueDec;
    readonly Dictionary<long, int> codeAt = []; readonly Dictionary<decimal, int> decAt = [];
    readonly long[] codes; readonly decimal[] decs;
    readonly Dictionary<long, int> mapOf = []; readonly int[][] images;
    readonly Dictionary<NodeKey, Node> nodes = [];
    // working state, empty between calls
    readonly HashSet<NodeKey> aff = []; readonly Queue<NodeKey> todo = new(); readonly HashSet<NodeKey> queued = [];
    /// <summary>Evaluations of an equation since the operator was made (the work of Theorem 7.6, item 3).</summary>
    public long Evaluations { get; private set; }

    public FixpointOp(TFixpoint t, Layout nodesL, Layout edgesL, Interner it) : base(t)
    {
        key = nodesL.Index(t.Key); value = nodesL.Index(t.Value); from = edgesL.Index(t.From); to = edgesL.Index(t.To); by = t.By is { } b ? edgesL.Index(b) : -1;
        keyDec = nodesL.IsDec[key]; valueDec = nodesL.IsDec[value];
        positions = t.Order.Count + 1; codes = new long[positions]; decs = new decimal[positions];
        for (var i = 1; i < positions; i++)
            if (t.Order[i - 1] is DecimalValue d) decAt[decs[i] = d.Q] = i;
            else codeAt[codes[i] = Codec.ToCode(t.Order[i - 1], it)] = i;
        images = [.. t.Maps.Values];
        var n = 0;
        foreach (var when in t.Maps.Keys) mapOf[Codec.ToCode(Value.Text(when), it)] = n++;
    }

    Node Get(NodeKey k) => nodes.TryGetValue(k, out var x) ? x : nodes[k] = new Node(positions);
    NodeKey KeyOf(Column c, int row)
    {
        if (!keyDec) return new(c.L[row], 0);
        var (lo, hi) = DecimalKey.Of(c.D[row]);
        return new(lo, hi);
    }

    int Eval(Node n)
    {
        Evaluations++;
        var z = 0;
        for (var p = positions - 1; p > 0; p--) if (n.Own[p] > 0) { z = p; break; }
        foreach (var ((dep, map), w) in n.Out)
        {
            if (w <= 0 || z == positions - 1) continue;
            var at = nodes.TryGetValue(dep, out var d) ? d.Z : 0;
            z = Math.Max(z, map < 0 || at == 0 ? at : images[map][at]);
        }
        return z;
    }

    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var nb = In(inputs, "in"); var eb = In(inputs, "edges");
        if ((nb is null || nb.Rows == 0) && (eb is null || eb.Rows == 0)) return null;
        for (var r = 0; nb is not null && r < nb.Rows; r++)
        {
            var row = nb.RowAt(r); var w = nb.Weight(row);
            if (!nb.Cols[key].Present(row)) { if (w > 0) cx.Faults.MissingRequired++; continue; }
            var k = KeyOf(nb.Cols[key], row); var n = Get(k);
            var v = nb.Cols[value]; var p = 0;
            if (v.Present(row) && !(valueDec ? decAt.TryGetValue(v.D[row], out p) : codeAt.TryGetValue(v.L[row], out p)) && w > 0) cx.Faults.InvalidParameter++;
            n.Own[p] += w; aff.Add(k);
        }
        for (var r = 0; eb is not null && r < eb.Rows; r++)
        {
            var row = eb.RowAt(r); var w = eb.Weight(row);
            if (!eb.Cols[from].Present(row) || !eb.Cols[to].Present(row)) { if (w > 0) cx.Faults.MissingRequired++; continue; }
            NodeKey f = KeyOf(eb.Cols[from], row), d = KeyOf(eb.Cols[to], row);
            var map = by >= 0 && eb.Cols[by].Present(row) && mapOf.TryGetValue(eb.Cols[by].L[row], out var m) ? m : -1;
            var fn = Get(f); var dn = Get(d);
            var before = fn.Out.GetValueOrDefault((d, map)); var after = before + w;
            if (after == 0) fn.Out.Remove((d, map)); else fn.Out[(d, map)] = after;
            if (before <= 0 && after > 0) dn.In[f] = dn.In.GetValueOrDefault(f) + 1;
            else if (before > 0 && after <= 0 && --dn.In[f] == 0) dn.In.Remove(f);
            aff.Add(f);
            if (dn.Idle() && !aff.Contains(d)) nodes.Remove(d);
        }
        // Aff: the changed nodes and every node that reaches one over positive edges (a lost edge names its from, so the edges after the step suffice)
        foreach (var k in aff) todo.Enqueue(k);
        while (todo.TryDequeue(out var k))
            foreach (var p in nodes[k].In.Keys) if (aff.Add(p)) todo.Enqueue(p);
        foreach (var k in aff) { nodes[k].Z = 0; todo.Enqueue(k); queued.Add(k); }
        while (todo.TryDequeue(out var k))
        {
            queued.Remove(k);
            var n = nodes[k]; var z = Eval(n);
            if (z <= n.Z) continue;
            n.Z = z;
            foreach (var p in n.In.Keys) if (aff.Contains(p) && queued.Add(p)) todo.Enqueue(p);
        }
        var bb = EmptyOutput();
        foreach (var k in aff)
        {
            var n = nodes[k]; var now = n.Present() ? n.Z : 0;
            if (now != n.Emitted)
            {
                if (n.Emitted > 0) Write(bb, k, n.Emitted, -1);
                if (now > 0) Write(bb, k, now, 1);
                n.Emitted = now;
            }
            if (n.Idle()) nodes.Remove(k);
        }
        aff.Clear();
        return bb.Build();
    }

    void Write(BatchBuilder bb, NodeKey k, int at, long weight)
    {
        var row = bb.Add(weight); var cols = bb.Batch.Cols;
        if (keyDec) cols[0].D[row] = DecimalKey.Number(k.A, k.B); else cols[0].L[row] = k.A;
        if (valueDec) cols[1].D[row] = decs[at]; else cols[1].L[row] = codes[at];
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
