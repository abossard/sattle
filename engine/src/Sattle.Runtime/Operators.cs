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
    public long LateDropped, LateRejected, SkippedEmits;
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
}

/// <summary>Row batch assembly with growth, weights always present.</summary>
public sealed class BatchBuilder(Layout layout)
{
    readonly Batch b = new(layout, 64) { W = new long[64] };
    public Batch Batch => b;
    public int Add(long weight)
    {
        var i = b.N;
        if (i >= b.W!.Length) { b.Ensure(i * 2); }
        b.W![i] = weight;
        b.N++;
        for (var c = 0; c < b.Cols.Length; c++) if (b.Cols[c].P is { } p) p[i] = true;
        return i;
    }
    public void Absent(int row, int col) => b.Cols[col].SetAbsent(row, b.N);
    public Batch? Build() => b.N == 0 ? null : b;
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
    readonly VExpr?[] compute; readonly int[] passthrough; readonly bool[] isDec;
    public MapOp(TMap t, Layout inLayout, OpContext cx) : base(t)
    {
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
        var cols = new Column[OutLayout.Count];
        for (var c = 0; c < cols.Length; c++) cols[c] = passthrough[c] >= 0 ? b.Cols[passthrough[c]] : compute[c]!.Eval(b);
        return b.WithColumns(OutLayout, cols);
    }
}

sealed class WindowOp : PhysOp
{
    readonly int ts; readonly long width; readonly Column ws = new(false), we = new(false);
    public WindowOp(TWindow t, Layout inLayout, OpContext cx) : base(t) { _ = cx; ts = inLayout.Index(t.TimeField); width = t.WidthNs; }
    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var b = In(inputs, "in"); if (b is null || b.Rows == 0) return null;
        ws.Ensure(b.N); we.Ensure(b.N);
        var src = b.Cols[ts].L;
        for (var k = 0; k < b.Rows; k++)
        {
            var i = b.RowAt(k);
            var m = src[i] % width; if (m < 0) m += width;
            ws.L[i] = src[i] - m; we.L[i] = src[i] - m + width;
        }
        var cols = new Column[b.Cols.Length + 2];
        Array.Copy(b.Cols, cols, b.Cols.Length);
        cols[^2] = ws; cols[^1] = we;
        return b.WithColumns(OutLayout, cols);
    }
}

// ---- aggregation -----------------------------------------------------------------------------------------

/// <summary>The state of a group operator (core step 3): one row of a `Table` per key, holding the key, the row count, one accumulator per sum, count or mean (a decimal in two 8-byte columns) and whether the key's aggregate was emitted. A slot is the row number. The values emitted last are not kept: an operator retracts a key's aggregate when it first touches the key in a step, before the step's rows change it. The multisets of `min`, `max`, `last` and `countDistinct` are tables of (slot, aggregate, value) with a weight, found through their arrangement by (slot, aggregate); a number is keyed by its value whatever its scale (2.5 and 2.50 are one entry, which keeps the digits it came with first).</summary>
sealed class KeyTable
{
    readonly int k, nL, nD, nIndex;
    readonly Table rows;
    readonly int cnt, accL, accD, emitted;
    readonly long[] key;
    readonly Segments<byte> touched = new();
    public readonly List<int> TouchedList = [];
    /// <summary>(slot, aggregate, value): the values of a `min` or `max` over integers and codes, and of a `countDistinct`.</summary>
    readonly Table? longs;
    /// <summary>(slot, aggregate, value as a number, its digits): the values of a `min` or `max` over decimals.</summary>
    readonly Table? decimals;
    /// <summary>(slot, aggregate, order and value as numbers, their digits): the rows of a `last`.</summary>
    readonly Table? lasts;

    /// <summary>`nIndex`: one more than the greatest index an aggregate's multiset is filed under.</summary>
    public KeyTable(int k, int nL, int nD, int nIndex, bool longs, bool decimals, bool lasts)
    {
        this.k = k; this.nL = nL; this.nD = nD; this.nIndex = nIndex;
        cnt = k; accL = cnt + 1; accD = accL + nL; emitted = accD + 2 * nD;
        rows = new Table([.. Enumerable.Repeat(8, emitted), 4], keyColumns: k);
        key = new long[k];
        if (longs) this.longs = new Table([4, 4, 8], keyColumns: 3, arrangedPrefix: 2);
        if (decimals) this.decimals = new Table([4, 4, 8, 8, 8, 8], keyColumns: 4, arrangedPrefix: 2);
        if (lasts) this.lasts = new Table([4, 4, 8, 8, 8, 8, 8, 8, 8, 8], keyColumns: 6, arrangedPrefix: 2);
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
    public void AddD(int slot, int i, decimal v) { var (a, b) = Pack(D(slot, i) + v); rows.Set(slot, accD + 2 * i, a); rows.Set(slot, accD + 2 * i + 1, b); }
    public bool Emitted(int slot) => rows.Get(slot, emitted) != 0;
    public void SetEmitted(int slot, bool on) => rows.Set(slot, emitted, on ? 1 : 0);

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
    public void AddDecimal(int slot, int a, decimal v, long w)
    {
        var (lo, hi) = DecimalKey.Of(v);
        var had = decimals!.Find([slot, a, lo, hi]) >= 0;
        var row = decimals.Add([slot, a, lo, hi], w);
        if (row >= 0 && !had) { var (x, y) = Pack(v); decimals.Set(row, 4, x); decimals.Set(row, 5, y); }
    }
    public void AddLast(int slot, int a, decimal ord, decimal val, long w)
    {
        var (o1, o2) = DecimalKey.Of(ord); var (v1, v2) = DecimalKey.Of(val);
        var had = lasts!.Find([slot, a, o1, o2, v1, v2]) >= 0;
        var row = lasts.Add([slot, a, o1, o2, v1, v2], w);
        if (row >= 0 && !had)
        {
            var (p, q) = Pack(ord); var (x, y) = Pack(val);
            lasts.Set(row, 6, p); lasts.Set(row, 7, q); lasts.Set(row, 8, x); lasts.Set(row, 9, y);
        }
    }

    /// <summary>The smallest (or with `desc` the greatest) value of a `min` or `max` over integers; false when it has none.</summary>
    public bool ExtremeL(int slot, int a, bool desc, out long v)
    {
        v = 0; var any = false;
        for (var r = longs!.ByPrefix!.First([slot, a]); r >= 0; r = longs.ByPrefix.Next(r))
        {
            var x = longs.Get(r, 2);
            if (!any || (desc ? x > v : x < v)) { v = x; any = true; }
        }
        return any;
    }
    public bool ExtremeD(int slot, int a, bool desc, out decimal v)
    {
        v = 0; var any = false;
        for (var r = decimals!.ByPrefix!.First([slot, a]); r >= 0; r = decimals.ByPrefix.Next(r))
        {
            var x = Unpack(decimals.Get(r, 4), decimals.Get(r, 5));
            if (!any || (desc ? x > v : x < v)) { v = x; any = true; }
        }
        return any;
    }
    /// <summary>The (order, value) rows of a `last`, in no particular order.</summary>
    public List<(decimal Ord, decimal Val)> LastRows(int slot, int a)
    {
        var res = new List<(decimal, decimal)>();
        for (var r = lasts!.ByPrefix!.First([slot, a]); r >= 0; r = lasts.ByPrefix.Next(r))
            res.Add((Unpack(lasts.Get(r, 6), lasts.Get(r, 7)), Unpack(lasts.Get(r, 8), lasts.Get(r, 9))));
        return res;
    }

    /// <summary>Whether a slot holds nothing: no rows, nothing emitted, every accumulator and multiset empty.</summary>
    bool Dead(int s)
    {
        if (Cnt(s) != 0 || Emitted(s)) return false;
        for (var i = 0; i < nL; i++) if (L(s, i) != 0) return false;
        for (var i = 0; i < nD; i++) if (D(s, i) != 0) return false;
        for (var a = 0; a < nIndex; a++)
            if (longs?.ByPrefix!.First([s, a]) >= 0 || decimals?.ByPrefix!.First([s, a]) >= 0 || lasts?.ByPrefix!.First([s, a]) >= 0) return false;
        return true;
    }

    /// <summary>Gives back the row of a slot that holds nothing (a group whose rows all went, a roll-up window that left the look-back); its number may be given to a new key.</summary>
    public void ReleaseIfDead(int slot) { if (Dead(slot)) rows.Add(KeyAt(slot), -1); }
}

enum AggKind { Count, SumL, SumD, Distinct, ExtL, ExtD, Mean, AnyAll, Last, WMean }
sealed record AggSpec(AggKind Kind, int Index, VExpr? Value, bool ValueIsDec, bool Desc = false, int Index2 = -1, VExpr? Order = null, bool OutDec = false, bool All = false, bool ValueIsText = false, bool OrderIsText = false);

/// <summary>Group (and the accumulation half of Rollup): per-key accumulators updated by signed weights (DBSP linear aggregates). Output is a retraction plus an insertion per touched key.</summary>
abstract class AggregatingOp : PhysOp
{
    protected readonly VExpr[] keyExprs; protected readonly AggSpec[] aggs; protected readonly int nL, nD, nDist, nExtL, nExtD, nLast; protected Column?[] orderCols = [];
    protected readonly int[] outKeyCol, outAggCol;
    protected readonly long[] keyBuf;
    readonly Interner it;
    protected readonly DecimalCodes decimals = new();
    /// <summary>The key code of a cell: its 64-bit code, or the code of its number for a Decimal.</summary>
    protected long KeyCode(Column c, int row) => c.IsDec ? decimals.Code(c.D[row]) : c.L[row];

    /// <summary>The winning (order, value) of a last aggregate: the greatest order, a tie goes to the greater value. Where the order or the value is text its code says nothing about its order, so those entries are compared by their text. Null when it has no rows.</summary>
    (decimal Ord, decimal Val)? LastOf(KeyTable t, int slot, AggSpec s)
    {
        var rows = t.LastRows(slot, s.Index2);
        if (rows.Count == 0) return null;
        var first = rows.Max();
        if (!s.OrderIsText && !s.ValueIsText) return first;
        int Text(decimal x, decimal y) => string.CompareOrdinal(it.Get((int)x), it.Get((int)y));
        var best = first;
        foreach (var k in rows)
        {
            if (!s.OrderIsText && k.Ord != first.Ord) continue;
            var byOrder = s.OrderIsText ? Text(k.Ord, best.Ord) : 0;
            if (byOrder > 0 || byOrder == 0 && (s.ValueIsText ? Text(k.Val, best.Val) : k.Val.CompareTo(best.Val)) > 0) best = k;
        }
        return best;
    }

    /// <summary>Companion columns, when the operator feeds only sinks: per `mean` its sum and count (`{target}#sum`, `{target}#count`), per `last` with a numeric order its order (`{target}#order`). A sink that writes the aggregate to a telemetry property stores the pair, so two windows of one class combine as one (Law 7.5 item 3 (i)).</summary>
    readonly (int Agg, int ColA, int ColB)[] companions = [];

    /// <summary>The names of the companion columns an aggregate has, if any.</summary>
    public static string[] CompanionNames(TAgg a) => a.Fn switch { "mean" => [a.Target + "#sum", a.Target + "#count"], "last" when a.Weight?.Type.Ty.Tag is not (Tag.Text or Tag.Ent) => [a.Target + "#order"], _ => [] };

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
        var list = new List<AggSpec>(); int l = 0, d = 0, dist = 0, last = 0; bool hasExtL = false, hasExtD = false;
        foreach (var a in aggSpecs)
        {
            var v = a.Value is null ? null : ExprCompiler.Compile(a.Value, inLayout, cx.It, cx.Faults);
            var dec = a.Out.Ty.Tag == Tag.Decimal;
            switch (a.Fn)
            {
                case "count": list.Add(new AggSpec(AggKind.Count, l++, null, false)); break;
                case "countDistinct": list.Add(new AggSpec(AggKind.Distinct, dist++, v, false, false, l++)); break;       // Index2: where the last emitted count is remembered
                case "sum" when dec: list.Add(new AggSpec(AggKind.SumD, d++, v, true)); break;
                case "sum": list.Add(new AggSpec(AggKind.SumL, l++, v, false)); break;
                case "mean": list.Add(new AggSpec(AggKind.Mean, d++, v, false, false, l++)); break;
                case "weightedMean": list.Add(new AggSpec(AggKind.WMean, d++, v, false, false, d++, ExprCompiler.Compile(a.Weight!, inLayout, cx.It, cx.Faults))); break;
                case "any" or "all": list.Add(new AggSpec(AggKind.AnyAll, l++, v, false, false, l++, null, false, a.Fn == "all")); break;
                case "last":
                    {
                        var order = ExprCompiler.Compile(a.Weight!, inLayout, cx.It, cx.Faults);
                        list.Add(new AggSpec(AggKind.Last, dec ? d++ : l++, v, false, false, last++, order, dec, false, a.Value!.Type.Ty.Tag is Tag.Text or Tag.Ent, a.Weight!.Type.Ty.Tag is Tag.Text or Tag.Ent)); break;
                    }
                case "min" or "max" when dec: list.Add(new AggSpec(AggKind.ExtD, d++, v, true, a.Fn == "max")); hasExtD = true; break;
                case "min" or "max": list.Add(new AggSpec(AggKind.ExtL, l++, v, false, a.Fn == "max")); hasExtL = true; break;
                default: throw new NotSupportedException($"aggregate {a.Fn}");
            }
        }
        _ = telemetryInput; it = cx.It;
        aggs = [.. list]; nL = l; nD = d; nDist = dist; nExtL = hasExtL ? l : 0; nExtD = hasExtD ? d : 0; nLast = last;   // min and max multisets are indexed like the accumulators they reserve
        outKeyCol = [.. outKeyNames.Select(n => OutLayout.Index(n))];
        outAggCol = [.. aggSpecs.Select(a => OutLayout.Index(a.Target))];
        keyBuf = new long[keyExprs.Length + 1];
    }

    /// <summary>The state of a group operator with `k` key columns.</summary>
    protected KeyTable NewKeyTable(int k) => new(k, nL, nD, Math.Max(Math.Max(nL, nD), Math.Max(nDist, nLast)), nExtL > 0 || nDist > 0, nExtD > 0, nLast > 0);

    protected void Accumulate(KeyTable t, int slot, Batch b, Column?[] valueCols, int row, long w)
    {
        t.AddCnt(slot, w);
        for (var a = 0; a < aggs.Length; a++)
        {
            var s = aggs[a];
            switch (s.Kind)
            {
                case AggKind.Count: t.AddL(slot, s.Index, w); break;
                case AggKind.SumL: { var c = valueCols[a]!; if (c.P is null || c.P[row]) t.AddL(slot, s.Index, w * c.L[row]); break; }
                case AggKind.SumD: { var c = valueCols[a]!; if (c.P is null || c.P[row]) t.AddD(slot, s.Index, w * (c.IsDec ? c.D[row] : c.L[row])); break; }
                case AggKind.ExtL:
                    {
                        var c = valueCols[a]!;
                        if (c.P is not null && !c.P[row]) break;
                        t.AddLong(slot, s.Index, c.L[row], w);
                        break;
                    }
                case AggKind.ExtD:
                    {
                        var c = valueCols[a]!;
                        if (c.P is not null && !c.P[row]) break;
                        t.AddDecimal(slot, s.Index, c.IsDec ? c.D[row] : c.L[row], w);
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
                case AggKind.WMean:
                    {
                        var c = valueCols[a]!; var wc = orderCols[a]!;
                        if (c.P is not null && !c.P[row] || wc.P is not null && !wc.P[row]) break;
                        var wv = wc.IsDec ? wc.D[row] : wc.L[row];
                        t.AddD(slot, s.Index, w * (c.IsDec ? c.D[row] : c.L[row]) * wv);
                        t.AddD(slot, s.Index2, w * wv);
                        break;
                    }
                case AggKind.AnyAll:
                    {
                        var c = valueCols[a]!;
                        if (c.P is not null && !c.P[row]) break;
                        if (c.L[row] != 0) t.AddL(slot, s.Index, w);
                        t.AddL(slot, s.Index2, w);
                        break;
                    }
                case AggKind.Last:
                    {
                        var c = valueCols[a]!; var o = orderCols[a]!;
                        if (c.P is not null && !c.P[row] || o.P is not null && !o.P[row]) break;
                        t.AddLast(slot, s.Index2, o.IsDec ? o.D[row] : o.L[row], c.IsDec ? c.D[row] : c.L[row], w);
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
        AggKind.Count or AggKind.SumL => t.L(slot, aggs[a].Index),
        AggKind.Distinct => t.L(slot, aggs[a].Index2),
        AggKind.ExtL => t.ExtremeL(slot, aggs[a].Index, aggs[a].Desc, out var v) ? v : 0,
        AggKind.AnyAll => aggs[a].All ? (t.L(slot, aggs[a].Index) == t.L(slot, aggs[a].Index2) ? 1 : 0) : (t.L(slot, aggs[a].Index) > 0 ? 1 : 0),
        AggKind.Last when !aggs[a].OutDec => LastOf(t, slot, aggs[a]) is { } l ? (long)l.Val : 0,
        _ => 0,
    };
    protected decimal AggD(KeyTable t, int slot, int a) => aggs[a].Kind switch
    {
        AggKind.ExtD => t.ExtremeD(slot, aggs[a].Index, aggs[a].Desc, out var v) ? v : 0,
        AggKind.Mean => t.L(slot, aggs[a].Index2) is var n && n != 0 ? t.D(slot, aggs[a].Index) / n : 0,
        AggKind.Last => LastOf(t, slot, aggs[a]) is { } l ? l.Val : 0,
        AggKind.WMean => t.D(slot, aggs[a].Index2) is var sw && sw != 0 ? t.D(slot, aggs[a].Index) / sw : 0,
        _ => t.D(slot, aggs[a].Index),
    };
    /// <summary>A min or max over no non-null value is null.</summary>
    protected bool AggAbsent(KeyTable t, int slot, int a) => aggs[a].Kind switch
    {
        AggKind.ExtL => !t.ExtremeL(slot, aggs[a].Index, false, out _),
        AggKind.ExtD => !t.ExtremeD(slot, aggs[a].Index, false, out _),
        AggKind.Mean => t.L(slot, aggs[a].Index2) == 0,
        AggKind.WMean => t.D(slot, aggs[a].Index2) == 0,
        AggKind.AnyAll => t.L(slot, aggs[a].Index2) == 0,
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
            if (aggs[a].Kind is AggKind.ExtL or AggKind.ExtD or AggKind.Mean or AggKind.WMean or AggKind.AnyAll or AggKind.Last && AggAbsent(t, slot, a)) { col.SetAbsent(row, bt.N); continue; }
            if (aggs[a].Kind is AggKind.SumD or AggKind.ExtD or AggKind.Mean or AggKind.WMean || aggs[a].Kind == AggKind.Last && aggs[a].OutDec) col.D[row] = AggD(t, slot, a);
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
        if (t.ClosesWindows) throw new InvalidOperationException("windowed group uses WindowedGroupOp");
    }
    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var b = In(inputs, "in"); if (b is null || b.Rows == 0) return null;
        var keyCols = keyExprs.Select(e => e.Eval(b)).ToArray();
        var valueCols = aggs.Select(a => a.Value?.Eval(b)).ToArray(); orderCols = [.. aggs.Select(a => a.Order?.Eval(b))];
        var bb = new BatchBuilder(OutLayout);
        for (var k = 0; k < b.Rows; k++)
        {
            var row = b.RowAt(k);
            var ok = true;
            for (var i = 0; i < keyCols.Length; i++) { if (keyCols[i].P is not null && !keyCols[i].P![row]) { ok = false; break; } keyBuf[i] = KeyCode(keyCols[i], row); }
            if (!ok) continue;
            var slot = table.FindOrAdd(keyBuf.AsSpan(0, keyCols.Length));
            if (table.Touch(slot) && table.Emitted(slot)) WriteRow(bb, table, slot, -1, null, null, -1, -1);
            Accumulate(table, slot, b, valueCols, row, b.Weight(row));
            if (members.Length > 0) cx.Out.AddMember(Name, string.Join(',', keyBuf.AsSpan(0, keyCols.Length).ToArray()), members, b, row, b.Weight(row));
        }
        foreach (var slot in table.TouchedList)
        {
            var on = table.Cnt(slot) > 0;
            if (on) WriteRow(bb, table, slot, 1, null, null, -1, -1);
            table.SetEmitted(slot, on);
        }
        foreach (var slot in table.TouchedList) table.ReleaseIfDead(slot);       // groups whose rows all went give their rows back
        table.ClearTouched();
        return bb.Build();
    }
}

/// <summary>Group over a Window whose key includes windowStart: windows close when the logical frontier passes windowEnd (research 03 section 10.1). Closed windows emit once; late rows follow the window's late policy.</summary>
sealed class WindowedGroupOp : AggregatingOp
{
    sealed class W(KeyTable t, long end) { public KeyTable Table = t; public long End = end; public bool Closed; }
    readonly SortedDictionary<long, W> windows = [];
    readonly long width; readonly string late; readonly int wsKey, outWs;
    readonly int[] keyOutNoWs;
    long frontier = long.MinValue;
    readonly int nKeys;
    readonly List<(W Win, long Ws)> revised = [];
    readonly string name;

    public WindowedGroupOp(TGroup t, long width, string late, Layout inLayout, OpContext cx, bool withCompanions = false)
        : base(t, t.Keys.Where(k => !(k.Name == "windowStart")).ToList(), t.Aggs, inLayout, cx, [.. t.Keys.Where(k => k.Name != "windowStart").Select(k => k.Name)], false, withCompanions)
    {
        this.width = width; this.late = late; name = t.Name;
        nKeys = keyExprs.Length;
        wsKey = inLayout.Index("windowStart");
        outWs = OutLayout.Index("windowStart");
        keyOutNoWs = [];
    }

    KeyTable NewTable() => NewKeyTable(nKeys);

    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var bb = new BatchBuilder(OutLayout);
        var b = In(inputs, "in");
        if (b is not null && b.Rows > 0)
        {
            var keyCols = keyExprs.Select(e => e.Eval(b)).ToArray();
            var valueCols = aggs.Select(a => a.Value?.Eval(b)).ToArray(); orderCols = [.. aggs.Select(a => a.Order?.Eval(b))];
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
                        if (cx.PastRevision(ws + width, width, frontier)) { if (late == "reject") cx.LateRejected++; else cx.LateDropped++; cur = null; curWs = long.MinValue; continue; }
                        cur = new W(NewTable(), ws + width); windows[ws] = cur; if (cur.End <= frontier) cur.Closed = true;
                    }
                    curWs = ws;
                }
                if (cur.Closed)
                {
                    if (late == "ignore") { cx.LateDropped++; continue; }
                    if (late == "reject") { cx.LateRejected++; continue; }
                }
                var ok = true;
                for (var i = 0; i < nKeys; i++) { if (keyCols[i].P is not null && !keyCols[i].P![row]) { ok = false; break; } keyBuf[i] = KeyCode(keyCols[i], row); }
                if (!ok) continue;
                var slot = cur.Table.FindOrAdd(keyBuf.AsSpan(0, nKeys));
                if (cur.Closed)
                {
                    // a revision of a closed window: what the key emitted is retracted before the row changes it
                    if (cur.Table.Touch(slot) && cur.Table.Emitted(slot)) WriteRow(bb, cur.Table, slot, -1, ws, ws + width, outWs, OutLayout.Has("windowEnd") ? OutLayout.Index("windowEnd") : -1);
                    if (!revised.Any(r => r.Win == cur)) revised.Add((cur, ws));
                }
                Accumulate(cur.Table, slot, b, valueCols, row, b.Weight(row));
            }
        }
        // revisions of closed windows: insert the new aggregate
        foreach (var (win, ws) in revised)
        {
            var t = win.Table;
            foreach (var slot in t.TouchedList)
            {
                var on = t.Cnt(slot) > 0;
                if (on) WriteRow(bb, t, slot, 1, ws, ws + width, outWs, -1);
                t.SetEmitted(slot, on);
            }
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
                    if (t.Cnt(slot) > 0) { WriteRow(bb, t, slot, 1, ws, ws + width, outWs, -1); t.SetEmitted(slot, true); }
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
    readonly Dictionary<string, Silence>? silence; readonly int inNoData = -1, outNoData = -1; readonly bool any; readonly int[] memberCols = [];

    /// <summary>The key of a roll-up over a Series with a time to live is in no data iff every member that has a row has a marker record (`nodata any`: iff one has).</summary>
    public RollupOp(TRollup t, Layout inLayout, OpContext cx, bool withCompanions = false) : base(t, t.Keys, t.Aggs, inLayout, cx, [.. t.Keys.Select(k => k.Name)], true, withCompanions)
    {
        table = NewKeyTable(keyExprs.Length + 1);
        inWs = inLayout.Index("windowStart"); outWs = OutLayout.Index("windowStart"); outWe = OutLayout.Index("windowEnd"); period = t.PeriodNs;
        if (t.NoData) { silence = []; inNoData = inLayout.Index(TThreshold.NoDataColumn); outNoData = OutLayout.Index(TThreshold.NoDataColumn); any = t.NoDataAny; memberCols = [.. t.Members!.Select(inLayout.Index)]; }
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
        var keyCols = keyExprs.Select(e => e.Eval(b)).ToArray();
        var valueCols = aggs.Select(a => a.Value?.Eval(b)).ToArray(); orderCols = [.. aggs.Select(a => a.Order?.Eval(b))];
        var wsCol = b.Cols[inWs].L;
        var n = keyCols.Length;
        var bb = new BatchBuilder(OutLayout);
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
                var reporting = !any && s.Members.Values.Any(m => m.Normal > 0 && m.Marked <= 0);       // a member with rows that is not in no data
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
            var on = table.Cnt(slot) > 0 && !Suppressed(slot, n);
            if (on) WriteRow(bb, table, slot, 1, ws, ws + period, outWs, outWe);
            table.SetEmitted(slot, on);
        }
        foreach (var slot in table.TouchedList) table.ReleaseIfDead(slot);       // windows that left the look-back give their rows back
        table.ClearTouched();
        return bb.Build();
    }

    /// <summary>The first time a step touches a slot, the aggregate it emitted is retracted, before the step changes it.</summary>
    void Retract(BatchBuilder bb, int slot, int n, out int touched)
    {
        touched = slot;
        if (!table.Touch(slot) || !table.Emitted(slot)) return;
        var ws = table.KeyAt(slot)[n];
        WriteRow(bb, table, slot, -1, ws, ws + period, outWs, outWe);
    }
}

// ---- hysteresis ------------------------------------------------------------------------------------------

/// <summary>Threshold: the only operator whose data output depends on telemetry. Per key, a state machine over the ordered closed windows; the output row changes only on a state change.</summary>
sealed class ThresholdOp : PhysOp
{
    /// <summary>Per key with a window in the look-back: those windows, and one carried state: the state machine after the newest window that left the look-back (`State`, `Run`, `Last` its start), so a state never changes because a window grew old (Def. 7.10, D33).</summary>
    sealed class KeyState { public readonly SortedDictionary<long, (decimal V, int P)> Windows = []; public string? Emitted; public string Id = ""; public long[] Key = []; public string State = ""; public long Run; public long Last = long.MinValue;
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
        foreach (var (start, window) in st.Windows) Step(ref state, ref run, ref last, start, window);
        return state;
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
                var sid = string.Join(',', buf);
                if (!states.TryGetValue(sid, out var quiet)) states[sid] = quiet = new KeyState { Id = sid, Key = [.. buf], State = t.Initial };
                quiet.Silent += b.Weight(row);
                if (seen.Add(quiet)) touched.Add(quiet);
                continue;
            }
            // thresholds read from data: each window is read with the parameters of its own row. The checker cannot see them: a row whose parameters are no state machine is dropped and counted
            var p = new Params(ac is not null && (ac.P is null || ac.P[row]) ? Num(ac, row) : t.Above, bc is not null && (bc.P is null || bc.P[row]) ? Num(bc, row) : t.Below,
                ec is not null && (ec.P is null || ec.P[row]) ? (long)Num(ec, row) : t.EnterAfter, xc is not null && (xc.P is null || xc.P[row]) ? (long)Num(xc, row) : t.ExitAfter);
            if (p.Above <= p.Below || p.Enter < 1 || p.Exit < 1) { if (b.Weight(row) > 0) cx.Faults.InvalidParameter++; continue; }
            var id = string.Join(',', buf);
            if (!states.TryGetValue(id, out var st)) states[id] = st = new KeyState { Id = id, Key = [.. buf], State = t.Initial };
            var ws = b.Cols[inWs].L[row]; var window = (V: vc.IsDec ? vc.D[row] : vc.L[row], P: IndexOf(p));
            if (ws < edge) { if (b.Weight(row) > 0) cx.LateDropped++; }     // behind the look-back: a late row is dropped and counted, and the row that withdraws a window because it grew old withdraws nothing here
            else if (b.Weight(row) > 0) st.Windows[ws] = window;
            else if (st.Windows.TryGetValue(ws, out var cur) && cur == window) st.Windows.Remove(ws);
            if (seen.Add(st)) touched.Add(st);
        }
        var bb = new BatchBuilder(OutLayout);
        foreach (var st in touched)
        {
            while (st.Windows.Count > 0 && st.Windows.First() is var oldest && oldest.Key < edge) { Step(ref st.State, ref st.Run, ref st.Last, oldest.Key, oldest.Value); st.Windows.Remove(oldest.Key); }
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

/// <summary>Union: both inputs have the same columns, the output is their signed rows one after the other (a Z-set sum). Linear, no state.</summary>
sealed class UnionOp(TUnion t) : PhysOp(t)
{
    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var bb = new BatchBuilder(OutLayout);
        foreach (var port in new[] { "in0", "in1" })
        {
            var b = In(inputs, port); if (b is null || b.Rows == 0) continue;
            for (var k = 0; k < b.Rows; k++)
            {
                var src = b.RowAt(k); var row = bb.Add(b.Weight(src));
                for (var c = 0; c < OutLayout.Count; c++)
                {
                    var col = b.Cols[c]; var dst = bb.Batch.Cols[c];
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
        var bits = decimal.GetBits(d / 1.0000000000000000000000000000m);      // dividing by one at full scale drops trailing zeros
        return (((long)bits[0] << 32) | (uint)bits[1], ((long)bits[2] << 32) | (uint)bits[3]);
    }
}

sealed class DistinctOp : PhysOp
{
    sealed class Key(long[] v) : IEquatable<Key>
    {
        readonly long[] v = v;
        public bool Equals(Key? o) => o is not null && v.AsSpan().SequenceEqual(o.v);
        public override bool Equals(object? o) => o is Key k && Equals(k);
        public override int GetHashCode() { var h = new HashCode(); foreach (var x in v) h.Add(x); return h.ToHashCode(); }
    }
    readonly Dictionary<Key, long> counts = [];
    readonly int n;
    public DistinctOp(TDistinct t, Layout inL) : base(t) { n = inL.Count; }
    /// <summary>The number of distinct rows held now (rows of positive count).</summary>
    public int Held => counts.Values.Count(c => c > 0);

    long[] KeyOf(Batch b, int row)
    {
        var res = new long[n * 3];
        for (var c = 0; c < n; c++)
        {
            var col = b.Cols[c];
            if (!col.Present(row)) { res[c * 3] = 1; continue; }
            if (col.IsDec) (res[c * 3 + 1], res[c * 3 + 2]) = DecimalKey.Of(col.D[row]);
            else res[c * 3 + 1] = col.L[row];
        }
        return res;
    }

    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var b = In(inputs, "in"); if (b is null || b.Rows == 0) return null;
        // decide per row in order: a row crossing zero is emitted at that moment, so batch-internal cancellation is visible
        var bb = new BatchBuilder(OutLayout);
        for (var k = 0; k < b.Rows; k++)
        {
            var src = b.RowAt(k); var key = new Key(KeyOf(b, src));
            var before = counts.GetValueOrDefault(key); var after = before + b.Weight(src);
            if (after == 0) counts.Remove(key); else counts[key] = after;
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

/// <summary>Equi-join of two data inputs. Each side keeps its rows (with signed multiplicity) in a hash index on the join keys. A left batch is joined with the old right state, then added to the left state; a right batch is joined with the new left state, so a pair of new rows is produced once. Rows with an absent key component never join. A residual predicate filters the joined rows.</summary>
sealed class JoinOp : PhysOp
{
    readonly struct Cell(bool present, long l, decimal d) { public readonly bool Present = present; public readonly long L = l; public readonly decimal D = d; }
    sealed class Entry(Cell[] cells) { public readonly Cell[] Cells = cells; public long Weight; }
    sealed class Key(long[] v) : IEquatable<Key>
    {
        public readonly long[] V = v;
        public bool Equals(Key? o) => o is not null && V.AsSpan().SequenceEqual(o.V);
        public override bool Equals(object? o) => o is Key k && Equals(k);
        public override int GetHashCode() { var h = new HashCode(); foreach (var x in V) h.Add(x); return h.ToHashCode(); }
    }
    sealed class Side(Layout layout, VExpr[] keys, bool[] keyDec)
    {
        public readonly Layout Layout = layout; public readonly VExpr[] Keys = keys; public readonly bool[] KeyDec = keyDec;
        public readonly Dictionary<Key, Dictionary<Key, Entry>> Index = [];
    }
    readonly Side left, right; readonly VExpr? residual;

    public JoinOp(TJoin t, Layout lL, Layout rL, OpContext cx) : base(t)
    {
        VExpr[] C(IEnumerable<TExpr> es, Layout l) => [.. es.Select(e => ExprCompiler.Compile(e, l, cx.It, cx.Faults))];
        left = new Side(lL, C(t.Keys.Select(k => k.Left), lL), [.. t.Keys.Select(k => k.Left.Type.Ty.Tag == Tag.Decimal)]);
        right = new Side(rL, C(t.Keys.Select(k => k.Right), rL), [.. t.Keys.Select(k => k.Right.Type.Ty.Tag == Tag.Decimal)]);
        residual = t.Residual is null ? null : ExprCompiler.Compile(t.Residual, OutLayout, cx.It, cx.Faults);
    }

    static Key? KeyOf(Side s, Column[] cols, int row)
    {
        var v = new long[cols.Length * 2];
        for (var i = 0; i < cols.Length; i++)
        {
            if (!cols[i].Present(row)) return null;
            if (s.KeyDec[i] || cols[i].IsDec) (v[i * 2], v[i * 2 + 1]) = DecimalKey.Of(cols[i].IsDec ? cols[i].D[row] : cols[i].L[row]);
            else v[i * 2] = cols[i].L[row];
        }
        return new Key(v);
    }

    static Cell[] CellsOf(Batch b, int row)
    {
        var cells = new Cell[b.Cols.Length];
        for (var c = 0; c < cells.Length; c++) { var col = b.Cols[c]; cells[c] = col.Present(row) ? new Cell(true, col.IsDec ? 0 : col.L[row], col.IsDec ? col.D[row] : 0) : default; }
        return cells;
    }

    static Key RowKey(Cell[] cells)
    {
        var v = new long[cells.Length * 4];
        for (var c = 0; c < cells.Length; c++)
        {
            if (!cells[c].Present) { v[c * 4] = 1; continue; }
            v[c * 4 + 1] = cells[c].L; (v[c * 4 + 2], v[c * 4 + 3]) = DecimalKey.Of(cells[c].D);
        }
        return new Key(v);
    }

    void Emit(BatchBuilder bb, Cell[] l, Cell[] r, long weight)
    {
        var row = bb.Add(weight);
        var n = l.Length;
        for (var c = 0; c < OutLayout.Count; c++)
        {
            var cell = c < n ? l[c] : r[c - n]; var dst = bb.Batch.Cols[c];
            if (!cell.Present) { bb.Absent(row, c); continue; }
            if (dst.IsDec) dst.D[row] = cell.D; else dst.L[row] = cell.L;
        }
    }

    void Update(Side s, Key jk, Cell[] cells, long w)
    {
        if (!s.Index.TryGetValue(jk, out var rows)) s.Index[jk] = rows = [];
        var rk = RowKey(cells);
        if (!rows.TryGetValue(rk, out var e)) rows[rk] = e = new Entry(cells);
        e.Weight += w;
        if (e.Weight == 0) { rows.Remove(rk); if (rows.Count == 0) s.Index.Remove(jk); }
    }

    void Half(Batch? b, Side self, Side other, bool selfIsLeft, BatchBuilder bb)
    {
        if (b is null || b.Rows == 0) return;
        var keyCols = self.Keys.Select(k => k.Eval(b)).ToArray();
        for (var k = 0; k < b.Rows; k++)
        {
            var row = b.RowAt(k);
            if (KeyOf(self, keyCols, row) is not { } jk) continue;
            var cells = CellsOf(b, row); var w = b.Weight(row);
            if (other.Index.TryGetValue(jk, out var matches))
                foreach (var m in matches.Values) { if (selfIsLeft) Emit(bb, cells, m.Cells, w * m.Weight); else Emit(bb, m.Cells, cells, w * m.Weight); }
            Update(self, jk, cells, w);
        }
    }

    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var bb = new BatchBuilder(OutLayout);
        Half(In(inputs, "left"), left, right, true, bb);
        Half(In(inputs, "right"), right, left, false, bb);
        var res = bb.Build();
        if (res is null || residual is null) return res;
        var keep = residual.Eval(res); var sel = new int[res.N]; var n = 0;
        for (var i = 0; i < res.N; i++) if (keep.Present(i) && keep.L[i] != 0) sel[n++] = i;
        return n == 0 ? null : res.WithSelection(sel, n);
    }
}

// ---- traversal -------------------------------------------------------------------------------------------

/// <summary>Bounded traversal: for every input row, the elements reachable from its start element over one relationship type, with the shortest depth. Input rows are kept per start element together with the reach last emitted for it. A row delta is joined with the old reach; a start is recomputed when a visited element or edge appears in the graph patch, and the difference between old and new reach is emitted once per row. Reached elements that are not in the graph are skipped (they appear when the entity does).</summary>
sealed class TraverseOp : PhysOp
{
    readonly struct Cell(bool present, long l, decimal d) { public readonly bool Present = present; public readonly long L = l; public readonly decimal D = d; }
    sealed class Row(Cell[] cells) { public readonly Cell[] Cells = cells; public long Weight; }
    sealed class RowKey(long[] v) : IEquatable<RowKey>
    {
        readonly long[] v = v;
        public bool Equals(RowKey? o) => o is not null && v.AsSpan().SequenceEqual(o.v);
        public override bool Equals(object? o) => o is RowKey k && Equals(k);
        public override int GetHashCode() { var h = new HashCode(); foreach (var x in v) h.Add(x); return h.ToHashCode(); }
    }
    sealed class Reach { public List<(Entity E, int Depth)> Items = []; public HashSet<string> Touched = []; public string Signature = ""; }

    readonly TTraverse t; readonly Interner it; readonly OntologyView view;
    readonly int inN, startCol, reachedCol, depthCol;
    readonly (string PropId, int OutCol)[] reads;
    readonly Dictionary<long, Dictionary<RowKey, Row>> rows = [];
    readonly Dictionary<long, Reach> reach = [];
    Graph? graph;

    public TraverseOp(TTraverse t, Layout inL, OntologyView view, Interner it) : base(t)
    {
        this.t = t; this.it = it; this.view = view;
        inN = inL.Count; startCol = inL.Index(t.Start); reachedCol = OutLayout.Index(t.Target); depthCol = OutLayout.Index(t.Target + "_depth");
        reads = [.. t.Reads.Select(r => (r.PropId, OutLayout.Index(r.Col)))];
    }

    static RowKey KeyOf(Cell[] cells)
    {
        var v = new long[cells.Length * 4];
        for (var c = 0; c < cells.Length; c++)
        {
            if (!cells[c].Present) { v[c * 4] = 1; continue; }
            v[c * 4 + 1] = cells[c].L; (v[c * 4 + 2], v[c * 4 + 3]) = DecimalKey.Of(cells[c].D);
        }
        return new RowKey(v);
    }

    /// <summary>Whether the reach touched an id of the patch; walks the smaller set (a big patch against many small reaches stays linear).</summary>
    static bool Overlaps(HashSet<string> touched, HashSet<string> changed)
    {
        var (small, big) = touched.Count <= changed.Count ? (touched, changed) : (changed, touched);
        foreach (var id in small) if (big.Contains(id)) return true;
        return false;
    }

    Reach Bfs(string startId, Graph g)
    {
        var res = new Reach { Touched = [startId] };
        var seen = new HashSet<string> { startId }; var frontier = new List<string> { startId };
        for (var depth = 0; ; depth++)
        {
            foreach (var id in frontier.Order(StringComparer.Ordinal))
                if (depth >= t.Min && g.Entity(id) is { } e) res.Items.Add((e, depth));
            if (depth == t.Max) { foreach (var id in frontier) res.Touched.Add(id); break; }      // an entity that appears or changes at the last depth changes the reach
            var next = new List<string>();
            foreach (var id in frontier)
            {
                res.Touched.Add(id);
                if (t.Direction is "out" or "either")
                    foreach (var rid in g.OutRels(id)) if (g.Rel(rid) is { } r && view.IsSubtype(r.Type, t.RelTypeId) && seen.Add(r.Target)) next.Add(r.Target);
                if (t.Direction is "in" or "either")
                    foreach (var rid in g.InRels(id)) if (g.Rel(rid) is { } r && view.IsSubtype(r.Type, t.RelTypeId) && seen.Add(r.Source)) next.Add(r.Source);
            }
            if (next.Count == 0) break;
            frontier = next;
        }
        res.Signature = string.Join(";", res.Items.Select(x => x.E.Id + "@" + x.Depth + ":" + string.Join(",", reads.Select(r => x.E.Props.Get(r.PropId)?.ToString() ?? "-"))));
        return res;
    }

    void Emit(BatchBuilder bb, Cell[] cells, (Entity E, int Depth) item, long weight)
    {
        var row = bb.Add(weight); var b = bb.Batch;
        for (var c = 0; c < inN; c++)
        {
            var dst = b.Cols[c];
            if (!cells[c].Present) { bb.Absent(row, c); continue; }
            if (dst.IsDec) dst.D[row] = cells[c].D; else dst.L[row] = cells[c].L;
        }
        b.Cols[reachedCol].L[row] = it.Intern(item.E.Id); b.Cols[depthCol].L[row] = item.Depth;
        foreach (var (propId, outCol) in reads)
        {
            var col = b.Cols[outCol];
            if (item.E.Props.TryGet(propId, out var v) && v is not NullValue) { if (col.IsDec) col.D[row] = ((DecimalValue)v).Q; else col.L[row] = Codec.ToCode(v, it); }
            else col.SetAbsent(row, b.N);
        }
    }

    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var bb = new BatchBuilder(OutLayout);
        HashSet<string>? changed = null;
        if (ep.Graphs.TryGetValue(t.GraphPort, out var gd)) { graph = gd.State; if (gd.Patch is { } p) { changed = []; foreach (var e in p.Edits) { changed.Add(e.Key.Id); foreach (var s in new[] { e.Before, e.After }) if (s is Rel r) { changed.Add(r.Source); changed.Add(r.Target); } } } }
        // (a) row deltas against the reach emitted so far
        var b = In(inputs, "in");
        if (b is not null && b.Rows > 0)
            for (var k = 0; k < b.Rows; k++)
            {
                var src = b.RowAt(k);
                if (!b.Cols[startCol].Present(src)) continue;
                var cells = new Cell[inN];
                for (var c = 0; c < inN; c++) { var col = b.Cols[c]; cells[c] = col.Present(src) ? new Cell(true, col.IsDec ? 0 : col.L[src], col.IsDec ? col.D[src] : 0) : default; }
                var start = cells[startCol].L; var w = b.Weight(src);
                if (reach.TryGetValue(start, out var old)) foreach (var item in old.Items) Emit(bb, cells, item, w);
                if (!rows.TryGetValue(start, out var set)) rows[start] = set = [];
                var key = KeyOf(cells);
                if (!set.TryGetValue(key, out var row)) set[key] = row = new Row(cells);
                row.Weight += w;
                if (row.Weight == 0) set.Remove(key);
                if (set.Count == 0) { rows.Remove(start); reach.Remove(start); }
            }
        // (b) starts whose reach changed: retract the old reach and insert the new one for every row
        if (graph is not null)
            foreach (var start in rows.Keys.Order().ToList())
            {
                var had = reach.TryGetValue(start, out var old);
                if (had && (changed is null || !Overlaps(old!.Touched, changed))) continue;
                var now = Bfs(it.Get((int)start), graph);
                if (had && old!.Signature == now.Signature) { old.Touched = now.Touched; continue; }
                foreach (var row in rows[start].Values)
                {
                    if (had) foreach (var item in old!.Items) Emit(bb, row.Cells, item, -row.Weight);
                    foreach (var item in now.Items) Emit(bb, row.Cells, item, row.Weight);
                }
                reach[start] = now;
            }
        return bb.Build();
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
        var bb = new BatchBuilder(OutLayout);
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
        var bb = new BatchBuilder(OutLayout);
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
