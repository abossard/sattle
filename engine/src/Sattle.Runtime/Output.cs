using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

/// <summary>Series: closed telemetry windows of the matched elements, joined to the structural rows by element handle. Bilinear: changed windows against the rows before the step, then changed rows against the windows after it (section 11.1, section 11.2). It keeps the rows only; the windows are read from the telemetry store, their one copy.</summary>
sealed class SeriesOp : PhysOp
{
    readonly TSeries t; readonly Layout inLayout; readonly int elemCol; readonly int[] propHandles; readonly bool[] readIsDec;
    readonly int outWs, outWe; readonly int[] outReadCol; readonly long within, period;
    /// <summary>A structural row: its columns that are not decimals in `L`, its decimal columns in `D` (each in layout order, at `slot`), which columns are present, and its weight.</summary>
    sealed record Arr(long[] L, decimal[] D, bool[] P, long Weight);
    /// <summary>Per input column, its place in `Arr.L` or `Arr.D`.</summary>
    readonly int[] slot; readonly int decimals;
    /// <summary>The structural rows by element; an element is here while it has a row.</summary>
    readonly Dictionary<int, List<Arr>> arrangement = [];
    /// <summary>Visits the stored windows of elements' read properties in [from, to), in the read graph (set at activation). The read graph steps before this one, so the store holds the windows after this step's changes; and it keeps every window of the look-back until the epoch ends, so a window leaving the look-back can still be read to be retracted.</summary>
    public Func<IReadOnlyList<int>, IReadOnlyList<int>, long, long, WindowVisitor, long> Stored = null!;
    long frontier = long.MinValue;
    /// <summary>The earliest window start of a record this Series wrote, `long.MaxValue` before the first. An advance retracts only records it wrote, so it reads the store for the windows that leave the look-back only once the look-back passes this start.</summary>
    long oldest = long.MaxValue;

    // ---- the windows, read from the store ----
    SortedList<long, decimal?[]>? scanned;
    decimal?[]? point;
    readonly WindowVisitor scanVisit, pointVisit;

    /// <summary>The windows of an element that start in [lo, up), with the values of the reads; null when there are none.</summary>
    SortedList<long, decimal?[]>? Windows(int elem, long lo, long up)
    {
        scanned = null;
        Stored([elem], propHandles, lo, up, scanVisit);
        var res = scanned; scanned = null;
        return res;
    }

    /// <summary>The values of the reads at one window of an element; null when no read has one.</summary>
    decimal?[]? StoredAt(int elem, long start)
    {
        point = null;
        Stored([elem], propHandles, start, start + 1, pointVisit);
        var res = point; point = null;
        return res;
    }

    /// <summary>Working state of a call, emptied by `ChangesOf`: the windows this step changed for the reads of the Series, in the order of their first change, each with its place in `olds`; there, per read, the value the window had before the step, whether the step changed it, and in `news` its value after the step. Calls do not overlap, so every Series of a thread shares one, sized by the largest step.</summary>
    [ThreadStatic] static Dictionary<(int Elem, long Start), int>? changedOfThread;
    [ThreadStatic] static decimal?[]? olds, news, nowOfThread, beforeOfThread;
    [ThreadStatic] static bool[]? oldSet;
    static Dictionary<(int Elem, long Start), int> changed => changedOfThread ??= [];

    /// <summary>Collects the changes of this step into `changed`; false when there are none.</summary>
    bool ChangesOf(Epoch ep)
    {
        changed.Clear();
        var gd = ep.Graphs.GetValueOrDefault(t.GraphPort);
        if (gd is null || gd.Telemetry.Count == 0) return false;
        var n = propHandles.Length;
        foreach (var ch in gd.Telemetry)
        {
            var p = Array.IndexOf(propHandles, ch.Key.Prop);
            if (p < 0) continue;
            if (!changed.TryGetValue((ch.Key.Element, ch.Key.WindowStart), out var slot))
            {
                changed[(ch.Key.Element, ch.Key.WindowStart)] = slot = changed.Count;
                if (olds is null || olds.Length < (slot + 1) * n) { Array.Resize(ref olds, Math.Max(64, (slot + 1) * n * 2)); Array.Resize(ref oldSet, olds.Length); Array.Resize(ref news, olds.Length); }
                Array.Clear(oldSet!, slot * n, n);
            }
            if (!oldSet![slot * n + p]) { olds![slot * n + p] = ch.Old; oldSet[slot * n + p] = true; }     // the first change of a read holds its value before the step
            news![slot * n + p] = ch.New;                                                                   // the last one its value after it, what the store holds now
        }
        return changed.Count > 0;
    }

    /// <summary>The values of the reads at a window this step changed, after the step: the step's own changes when it changed every read (the steady case, a closed window), else the store. Valid until the next call.</summary>
    decimal?[] Now(int elem, long start, int slot)
    {
        var n = propHandles.Length;
        var res = Buffer(ref nowOfThread, n);
        for (var p = 0; p < n; p++)
            if (!oldSet![slot * n + p])
            {
                if (StoredAt(elem, start) is { } stored) Array.Copy(stored, res, n); else Array.Clear(res);
                return res;
            }
        Array.Copy(news!, slot * n, res, 0, n);
        return res;
    }

    static decimal?[] Buffer(ref decimal?[]? b, int n) { if (b is null || b.Length != n) b = new decimal?[n]; return b; }

    /// <summary>The values of a window before this step: the store now, with the value before the step of each read this step changed put back. Valid until the next call.</summary>
    decimal?[] Before(decimal?[]? now, int slot)
    {
        var n = propHandles.Length;
        var res = Buffer(ref beforeOfThread, n);
        if (now is null) Array.Clear(res); else Array.Copy(now, res, n);
        for (var p = 0; p < n; p++) if (oldSet![slot * n + p]) res[p] = olds![slot * n + p];
        return res;
    }

    Arr ReadRow(Batch rows, int i)
    {
        var l = new long[inLayout.Count - decimals]; var d = decimals == 0 ? [] : new decimal[decimals]; var p = new bool[inLayout.Count];
        for (var col = 0; col < inLayout.Count; col++)
        {
            p[col] = rows.Cols[col].Present(i);
            if (inLayout.IsDec[col]) d[slot[col]] = rows.Cols[col].D[i]; else l[slot[col]] = rows.Cols[col].L[i];
        }
        return new Arr(l, d, p, rows.Weight(i));
    }

    /// <summary>Adds a structural row to the arrangement.</summary>
    void Arrange(int c, Arr row)
    {
        if (!arrangement.TryGetValue(c, out var list)) arrangement[c] = list = [];
        var ex = list.FindIndex(a => SameRow(a, row));
        if (ex >= 0) { var nw = list[ex].Weight + row.Weight; if (nw == 0) list.RemoveAt(ex); else list[ex] = list[ex] with { Weight = nw }; }
        else if (row.Weight != 0) list.Add(row);
    }

    /// <summary>After its row was applied: an element with no row left leaves the arrangement.</summary>
    void LeaveIfEmpty(int c)
    {
        if (arrangement.TryGetValue(c, out var list) && list.Count == 0) arrangement.Remove(c);
    }

    // ---- fill and time to live (Def. 7.14 to 7.16) ----
    /// <summary>Per read, the sources that feed the property it reads (a function of the definitions, set at activation).</summary>
    public string[][] Feeding = [];
    /// <summary>Per read, how much later than its grid window a window of the property ends (a hop window, #111): it is covered that much later.</summary>
    public long[] Lag = [];
    readonly decimal?[] fills; readonly long? ttl; readonly int outNoData = -1;
    /// <summary>The windows with a loss mark, by their start, while they are in the look-back: such a window is not covered and holds no fill (Def. 7.14).</summary>
    readonly SortedSet<long> lost = [];
    bool Lost(long start, long lag) => lost.Count > 0 && lost.GetViewBetween(start, start + period + lag - 1).Count > 0;
    /// <summary>Since which advance an element has had a row here (presence is sampled at advances), and the window of the no-data record an element has.</summary>
    readonly Dictionary<int, long> since = []; readonly Dictionary<int, long> marker = [];
    /// <summary>Per read, the time up to which its windows are covered and closed, as applied to the output so far: min(frontier, coveredUntil).</summary>
    long[] hi;
    /// <summary>The Series fills or has a time to live: its output also depends on the progress of its sources and on the frontier.</summary>
    public bool Fills => ttl is not null || fills.Any(f => f is not null);
    long Floor(long time) => time - ((time % period) + period) % period;

    long[] HiAt(long f, IReadOnlyDictionary<string, long> covered) =>
        [.. fills.Select((fill, i) => fill is null || f == long.MinValue || i >= Feeding.Length || Feeding[i].Length == 0 || !Feeding[i].All(covered.ContainsKey) ? long.MinValue : Math.Min(f, Feeding[i].Min(s => covered[s]) - Lag[i]))];

    /// <summary>True when the progress of a source lets a window become covered that the output does not hold yet.</summary>
    public bool CoverageMoved(long f, IReadOnlyDictionary<string, long> covered) => Fills && HiAt(f, covered).Zip(hi).Any(x => x.First != long.MinValue && (x.Second == long.MinValue || Floor(x.First) != Floor(x.Second)));

    public SeriesOp(TSeries t, Layout inLayout, Interner it) : base(t)
    {
        fills = [.. t.Reads.Select(r => r.Fill)]; ttl = t.TtlNs; hi = [.. t.Reads.Select(_ => long.MinValue)];
        if (t.TtlNs is not null) outNoData = OutLayout.Index(TThreshold.NoDataColumn);
        this.t = t; this.inLayout = inLayout;
        elemCol = inLayout.Index(t.TargetVar);
        propHandles = [.. t.Reads.Select(r => it.Intern(r.PropId))];
        readIsDec = [.. t.Reads.Select(r => r.ValueKind == "Decimal")];
        outWs = OutLayout.Index("windowStart"); outWe = OutLayout.Index("windowEnd");
        outReadCol = [.. t.Reads.Select(r => OutLayout.Index(r.OutName))];
        within = t.WithinNs; period = t.PeriodNs;
        slot = new int[inLayout.Count];
        for (var c = 0; c < inLayout.Count; c++) slot[c] = inLayout.IsDec[c] ? decimals++ : c - decimals;
        scanVisit = (_, prop, start, value) =>
        {
            scanned ??= [];
            if (!scanned.TryGetValue(start, out var vals)) scanned[start] = vals = new decimal?[propHandles.Length];
            vals[Array.IndexOf(propHandles, prop)] = value;
        };
        pointVisit = (_, prop, _, value) => (point ??= new decimal?[propHandles.Length])[Array.IndexOf(propHandles, prop)] = value;
    }

    static bool SameRow(Arr a, Arr b) => a.P.AsSpan().SequenceEqual(b.P) && a.L.AsSpan().SequenceEqual(b.L) && a.D.AsSpan().SequenceEqual(b.D);

    void WriteRow(BatchBuilder bb, Arr r, long ws, decimal?[] vals, long w, bool noData = false)
    {
        if (ws < oldest) oldest = ws;
        var row = bb.Add(w);
        var b = bb.Batch;
        for (var c = 0; c < inLayout.Count; c++)
        {
            if (!r.P[c]) { b.Cols[c].SetAbsent(row, b.N); continue; }
            if (inLayout.IsDec[c]) b.Cols[c].D[row] = r.D[slot[c]]; else b.Cols[c].L[row] = r.L[slot[c]];
        }
        b.Cols[outWs].L[row] = ws; b.Cols[outWe].L[row] = ws + period;
        for (var k = 0; k < outReadCol.Length; k++)
        {
            if (noData) b.Cols[outReadCol[k]].SetAbsent(row, b.N);
            else if (readIsDec[k]) b.Cols[outReadCol[k]].D[row] = vals[k]!.Value; else b.Cols[outReadCol[k]].L[row] = (long)vals[k]!.Value;
        }
        if (outNoData >= 0) b.Cols[outNoData].L[row] = noData ? 1 : 0;
    }

    /// <summary>The values of the record an element has at a window: stored values, and for a read with a fill the fill value where the window is covered for the element. Null when a read has neither, or the window is outside the look-back.</summary>
    decimal?[]? Record(int elem, long start, long[] h, long f) => f != long.MinValue && start < f - within ? null : Record(StoredAt(elem, start), elem, start, h, f);

    /// <summary>The same with the stored values given (the values of a window before this step's changes).</summary>
    decimal?[]? Record(decimal?[]? stored, int elem, long start, long[] h, long f)
    {
        if (f != long.MinValue && start < f - within) return null;
        var res = new decimal?[fills.Length];
        for (var i = 0; i < fills.Length; i++)
        {
            var x = stored?[i];
            if (x is null && fills[i] is { } fill && h[i] != long.MinValue && start + period <= h[i] && since.TryGetValue(elem, out var from) && start >= from && !Lost(start, Lag[i])) x = fill;
            if (x is null) return null;
            res[i] = x;
        }
        return res;
    }

    /// <summary>The window starts in [lo, up) at which an element can have a record: its stored windows and the grid windows a fill can hold.</summary>
    SortedSet<long> Starts(int elem, long lo, long up, long[] h)
    {
        var set = new SortedSet<long>();
        if (Windows(elem, lo, up) is { } ws) foreach (var a in ws.Keys) set.Add(a);
        var top = h.Max();
        if (top != long.MinValue && since.TryGetValue(elem, out var from))
            for (var a = Floor(Math.Max(lo, from) + period - 1); a < up && a + period <= top; a += period) set.Add(a);
        return set;
    }

    /// <summary>Def. 7.16: the later of `since` and the end of the newest record lies more than the time to live before the frontier. So: no record that ends within the time to live, the element has not appeared within it, and it has an older record or a `since`.</summary>
    bool InNoData(int elem)
    {
        var life = ttl!.Value;
        var reachBack = Lag.Length == 0 ? 0 : Lag.Max();      // a hop window ends this much after its grid window (#111)
        for (var a = Floor(frontier - life - reachBack - 1); a + period <= frontier; a += period) if (Record(elem, a, hi, frontier) is not null) return false;
        var known = since.TryGetValue(elem, out var from);
        if (known) return from < frontier - life;
        if (Windows(elem, frontier - within, long.MaxValue) is { } ws) foreach (var (a, stored) in ws) if (Record(stored, elem, a, hi, frontier) is not null) return true;
        return false;
    }

    void UpdateMarker(BatchBuilder bb, int elem)
    {
        var list = arrangement.GetValueOrDefault(elem);
        long? want = list is { Count: > 0 } && frontier != long.MinValue && InNoData(elem) ? Floor(frontier) - period : null;
        long? old = marker.TryGetValue(elem, out var m) ? m : null;
        if (old == want) return;
        if (old is { } was && list is not null) foreach (var r in list) WriteRow(bb, r, was, [], -r.Weight, true);
        if (want is { } now) { foreach (var r in list!) WriteRow(bb, r, now, [], r.Weight, true); marker[elem] = now; } else marker.Remove(elem);
    }

    static bool Same(decimal?[]? a, decimal?[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    /// <summary>The Series with a fill or a time to live. Every change of the output is the difference between the record an element had at a window and the one it has now: after a structural row, a stored window, an advance, or the progress of a source.</summary>
    Batch? RunFilled(Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var bb = EmptyOutput();
        var touched = new HashSet<int>();
        var edge = frontier == long.MinValue ? long.MinValue : frontier - within;
        var reachBack = Lag.Length == 0 ? 0 : Lag.Max();      // a window ends this much after its grid window (a hop window, #111)
        ChangesOf(ep);
        // the record of a window before this step's window changes (the store holds it after them)
        decimal?[]? RecordBefore(int elem, long a) => changed.TryGetValue((elem, a), out var slot) ? Record(Before(StoredAt(elem, a), slot), elem, a, hi, frontier) : Record(elem, a, hi, frontier);
        if (ep.Loss is { } mark && (frontier == long.MinValue || mark + period + reachBack > edge) && !lost.Contains(mark))
        {
            // a loss mark on a window: the fills of the windows that contain it go (with a hop, every window that reaches the mark)
            var starts = new List<long>();
            for (var a = Floor(mark) - reachBack; a <= Floor(mark); a += period) starts.Add(a);
            var held = arrangement.Where(x => x.Value.Count > 0).Select(x => (Elem: x.Key, Rows: x.Value, Before: starts.Select(a => RecordBefore(x.Key, a)).ToArray())).ToList();
            lost.Add(mark);
            foreach (var (elem, list, before) in held)
            {
                touched.Add(elem);
                for (var j = 0; j < starts.Count; j++)
                {
                    var after = RecordBefore(elem, starts[j]);
                    if (Same(before[j], after)) continue;
                    foreach (var r in list)
                    {
                        if (before[j] is { } was) WriteRow(bb, r, starts[j], was, -r.Weight);
                        if (after is not null) WriteRow(bb, r, starts[j], after, r.Weight);
                    }
                }
            }
        }
        // changed windows against the rows before the step
        foreach (var ((elem, wstart), slot) in changed)
        {
            if (frontier != long.MinValue && wstart < frontier - within) continue;
            if (!arrangement.TryGetValue(elem, out var list)) continue;     // no row: nothing to join
            var now = Now(elem, wstart, slot);
            var before = Record(Before(now, slot), elem, wstart, hi, frontier);
            var after = Record(now, elem, wstart, hi, frontier);
            touched.Add(elem);
            if (Same(before, after)) continue;
            foreach (var r in list)
            {
                if (before is not null) WriteRow(bb, r, wstart, before, -r.Weight);
                if (after is not null) WriteRow(bb, r, wstart, after, r.Weight);
            }
        }
        // changed rows against the windows after the step
        var rows = In(inputs, "in");
        if (rows is not null)
            for (var k = 0; k < rows.Rows; k++)
            {
                var i = rows.RowAt(k);
                var c = (int)rows.Cols[elemCol].L[i];
                var row = ReadRow(rows, i); var w = row.Weight;
                Arrange(c, row);
                foreach (var a in Starts(c, edge, long.MaxValue, hi)) if (Record(c, a, hi, frontier) is { } vals) WriteRow(bb, row, a, vals, w);
                if (marker.TryGetValue(c, out var m)) WriteRow(bb, row, m, [], w, true);
                LeaveIfEmpty(c);
                touched.Add(c);
            }
        var advanced = ep.HasAdvance && ep.Lambda > frontier;
        if (advanced)
        {
            var edgeAfter = ep.Lambda - within;
            // the records that leave the look-back
            if (edgeAfter > oldest)
                foreach (var (elem, list) in arrangement)
                    foreach (var a in Starts(elem, edge, edgeAfter, hi))
                        if (Record(elem, a, hi, frontier) is { } vals) foreach (var r in list) WriteRow(bb, r, a, vals, -r.Weight);
            // presence is sampled at the advance (Def. 7.14)
            foreach (var elem in since.Keys.Where(e => !arrangement.TryGetValue(e, out var l) || l.Count == 0).ToList()) since.Remove(elem);
            foreach (var (elem, list) in arrangement) if (list.Count > 0) since.TryAdd(elem, ep.Lambda);
            frontier = ep.Lambda;
            lost.RemoveWhere(m => m + period + (Lag.Length == 0 ? 0 : Lag.Max()) <= edgeAfter);
        }
        var reach = HiAt(frontier, ep.Covered);
        var moved = reach.Zip(hi).Any(x => x.First != x.Second);
        if (moved)
        {
            // the windows that became covered: for some read the end of the window lies in (old reach, new reach]
            var lowest = fills.Select((f, i) => (f, i)).Where(x => x.f is not null).Select(x => hi[x.i]).DefaultIfEmpty(long.MinValue).Min();
            var top = reach.Max();
            if (top != long.MinValue)
                foreach (var (elem, list) in arrangement)
                {
                    if (list.Count == 0 || !since.TryGetValue(elem, out var from)) continue;
                    var lo = Math.Max(frontier - within, from);
                    if (lowest != long.MinValue) lo = Math.Max(lo, lowest - period + 1);
                    for (var a = Floor(lo + period - 1); a + period <= top; a += period)
                    {
                        var before = Record(elem, a, hi, frontier); var after = Record(elem, a, reach, frontier);
                        if (Same(before, after)) continue;
                        foreach (var r in list)
                        {
                            if (before is not null) WriteRow(bb, r, a, before, -r.Weight);
                            if (after is not null) WriteRow(bb, r, a, after, r.Weight);
                        }
                    }
                }
            hi = reach;
        }
        if (ttl is not null)
        {
            foreach (var elem in advanced || moved || ep.Loss is not null ? [.. arrangement.Keys] : touched.ToList()) UpdateMarker(bb, elem);
            foreach (var elem in marker.Keys.Where(e => !arrangement.TryGetValue(e, out var l) || l.Count == 0).ToList()) marker.Remove(elem);
        }
        return bb.Build();
    }

    static bool Complete(decimal?[] v) => v.All(x => x.HasValue);

    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        if (Fills) return RunFilled(ep, inputs);
        var bb = EmptyOutput();
        var edge = frontier == long.MinValue ? long.MinValue : frontier - within;
        // step B: changed telemetry windows against the rows before this step
        if (ChangesOf(ep))
            foreach (var ((elem, wstart), slot) in changed)
            {
                if (wstart < edge) continue;      // outside the look-back: not joined (Def. 6.7)
                if (!arrangement.TryGetValue(elem, out var list)) continue;     // no row: nothing to join
                var after = Now(elem, wstart, slot);
                var before = Before(after, slot);
                foreach (var r in list)
                {
                    if (Complete(before)) WriteRow(bb, r, wstart, before, -r.Weight);
                    if (Complete(after)) WriteRow(bb, r, wstart, after, r.Weight);
                }
            }
        // step A: changed structural rows against the windows after this step
        var rows = In(inputs, "in");
        if (rows is not null)
            for (var k = 0; k < rows.Rows; k++)
            {
                var i = rows.RowAt(k);
                var c = (int)rows.Cols[elemCol].L[i];
                var row = ReadRow(rows, i);
                Arrange(c, row);
                if (Windows(c, edge, long.MaxValue) is { } ws)
                    foreach (var (wstart, vals) in ws) if (Complete(vals)) WriteRow(bb, row, wstart, vals, row.Weight);
                LeaveIfEmpty(c);
            }
        // advance: windows that leave the look-back are retracted (the store still holds them in this epoch)
        if (ep.HasAdvance && ep.Lambda > frontier)
        {
            frontier = ep.Lambda;
            if (frontier - within > oldest)
                foreach (var (elem, list) in arrangement)
                    if (Windows(elem, edge, frontier - within) is { } ws)
                        foreach (var (wstart, vals) in ws)
                            if (Complete(vals)) foreach (var r in list) WriteRow(bb, r, wstart, vals, -r.Weight);
        }
        return bb.Build();
    }
}

// ---- output: element layers and telemetry puts ----------------------------------------------------------------

/// <summary>Collects emitted contributions with their weights, resolves them to element states per flush (support counting: an element exists while some create row supports it), and holds the telemetry samples of the step.</summary>
public sealed record LineageSource(string Role, int Handle, bool IsRel, string GraphPort);
/// <summary>A conflict of Def. 6.4 (D34) that holds in a graph: fragments that disagree. `Kind` is `property` (the property is absent), `element` (creates that disagree on type or endpoints: the element is absent), `uniqueness` (several relationships on one source, target and type: `Element` is the one that is present) or `window` (several values for one telemetry window: it has no value). `Values` are the disagreeing values as canonical text, sorted.</summary>
/// <summary>A telemetry value and the pair it may carry: for a `mean` its sum and count, for a `last` its order (Law 7.5 item 3 (i)); null for every other value.</summary>
public readonly record struct TVal(decimal V, (decimal A, decimal B)? Aux = null);

public sealed record Conflict(string Kind, string Element, string? Property, long? WindowStart, IReadOnlyList<string> Values)
{
    public string Text => $"{Kind} {Element} {Property} {WindowStart}: {string.Join(" | ", Values)}";
}

/// <summary>The conflicts that hold now in one graph, by what they are about (Def. 6.4): state that goes with the graph. `TakeChanged` gives the ones a commit reports.</summary>
sealed class ConflictSet
{
    readonly Dictionary<string, Conflict> now = new(StringComparer.Ordinal);
    readonly HashSet<string> changed = new(StringComparer.Ordinal);
    public IReadOnlyCollection<Conflict> All => [.. now.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Value)];
    /// <summary>Whether a conflict about an element or one of its properties holds: without one, `EndOf` has nothing to clear.</summary>
    public bool AnyAboutElements => aboutElements > 0;
    int aboutElements;

    /// <summary>Sets or clears the conflict about one subject (an element, one of its properties, a tuple, a window).</summary>
    public void Note(string subject, Conflict? conflict)
    {
        var had = now.TryGetValue(subject, out var old);
        if (conflict is null ? !had : had && old!.Text == conflict.Text) return;
        if (had != conflict is not null && (subject.StartsWith("element ", StringComparison.Ordinal) || subject.StartsWith("property ", StringComparison.Ordinal))) aboutElements += had ? -1 : 1;
        if (conflict is null) now.Remove(subject); else now[subject] = conflict;
        changed.Add(subject);
    }

    /// <summary>Clears the conflicts about an element and about its properties.</summary>
    public void EndOf(string id)
    {
        if (now.Count == 0) return;
        foreach (var subject in now.Keys.Where(k => k.StartsWith("property " + id + " ", StringComparison.Ordinal)).ToList()) Note(subject, null);
        Note("element " + id, null);
    }

    /// <summary>The conflicts that appeared, changed or ended since the last call: what a commit reports (an ended conflict comes with no values).</summary>
    public IReadOnlyList<Conflict> TakeChanged()
    {
        if (changed.Count == 0) return [];
        var res = changed.Order(StringComparer.Ordinal).Select(s => now.TryGetValue(s, out var c) ? c : new Conflict("resolved", s, null, null, [])).ToList();
        changed.Clear();
        return res;
    }
}

/// <summary>How often the rows of a projection disagreed or were held back, counted since the start. Rejections: relationships held back because another holds their (source, target, type). Property and element conflicts: counted each time such an element is resolved. Window conflicts: windows that rows with different values write. Merge conflicts: windows that two ids of one class write with values their rule does not combine. Windows of two ids: windows written under more than one own id now.</summary>
public readonly record struct OutputCounts(long Rejections, long PropertyConflicts, long ElementConflicts, long WindowConflicts, long MergeConflicts, long WindowsOfTwoIds);

/// <summary>What a projection contributes to its graph, as the one thing its operators hold. Three modules own the state: which elements exist (`ElementLayer`), where they come from (`LineageLayer`) and which rows support each telemetry window (`WindowSupport`). A merge of two identities moves all three.</summary>
public sealed class OutputBuilder
{
    readonly ConflictSet conflicts = new();
    readonly ElementLayer elements;
    readonly WindowSupport windows;
    readonly LineageLayer lineage;
    /// <summary>Where the elements come from: the emits and groups write it, the lineage reader asks it.</summary>
    internal LineageLayer Lineage => lineage;

    public OutputBuilder(Interner it) { elements = new(it, conflicts); windows = new(it, conflicts); lineage = new(elements); }

    /// <summary>A row says something about an element: its type, endpoints and properties, with the row's weight.</summary>
    public void Contribute(int elem, bool isRel, int sink, bool update, string type, string? source, string? target, Props props, long weight) => elements.Contribute(elem, isRel, sink, update, type, source, target, props, weight);
    /// <summary>The same for a row of an emit, read from its evaluated columns at `row` in the order of `shape`; endpoints are text numbers, -1 for none. An update that sets no property says nothing and gives false.</summary>
    public bool Contribute(FragmentShape shape, int elem, int source, int target, Column[] cols, int row, long weight) => elements.Contribute(shape, elem, source, target, cols, row, weight);
    /// <summary>The relationship id function of a sink (the id hashes its endpoints), needed to re-key relationships when an identity merges.</summary>
    public void RegisterRelSink(int sink, Func<string, string, string> idOf) => elements.RegisterRelSink(sink, idOf);
    /// <summary>A telemetry row of the step: the window it writes, its value and weight, and the id it was written under before any link (`own`, Law 7.5 item 3); `combine` is the property's combine rule.</summary>
    public void Telemetry(WindowKey key, TVal value, long weight, long period, long own, string? combine) => windows.Write(key, value, weight, period, own, combine);

    /// <summary>The edits that bring the graph to what the elements touched since the last flush resolve to.</summary>
    public List<Edit> Flush(Graph g) => elements.Flush(g);
    /// <summary>The samples that bring the store's windows to what the rows of the step give; the list holds them until the next flush on this thread.</summary>
    public List<Sample> FlushTelemetry(ITelemetryStore store, GraphKey g, long inputId) => windows.Flush(store, g, inputId);

    /// <summary>Two identities merged: the rows and the lineage of the loser move to the survivor; `g` is the graph as the last flush left it. Gives the elements whose telemetry must follow, old and new: the entity itself, whether or not this graph emitted it, and every relationship that got a new id.</summary>
    public IReadOnlyList<(int From, int To)> Remap(int loser, int survivor, Graph g)
    {
        var moved = elements.Move(loser, survivor, g);
        foreach (var (from, to) in moved) Lineage.Move(from, to);
        return moved.Contains((loser, survivor)) ? moved : [(loser, survivor), .. moved];
    }

    /// <summary>The telemetry windows of a merged element follow it, in the support and in the store; the samples that set each moved window to what its rows give are appended to the store.</summary>
    public void MoveTelemetry(ITelemetryStore store, GraphKey g, int from, int to, Func<int, string?> combine, long inputId)
    {
        var corrections = windows.Move(store, g, from, to, combine, inputId);
        if (corrections.Count > 0) store.Append(g, [.. corrections]);
    }

    public IReadOnlyCollection<Conflict> Conflicts => conflicts.All;
    /// <summary>The conflicts that appeared, changed or ended since the last call: what a commit reports.</summary>
    public IReadOnlyList<Conflict> TakeChanged() => conflicts.TakeChanged();
    public OutputCounts Counts => new(elements.Rejections, elements.PropertyConflicts, elements.ElementConflicts, windows.WindowConflicts, windows.MergeConflicts, windows.WindowsOfTwoIds);
    /// <summary>What the sink holds now: rows of contributions, stored fragments and their bytes. Nothing for elements that came and went.</summary>
    public (int Contributions, int Fragments, long FragmentBytes) Held => elements.Held;
}

abstract class SinkOp : PhysOp
{
    protected SinkOp(TOp t) : base(t) { }

    /// <summary>Per set: the companion columns of the aggregate it writes, if its value is one read directly (`{name}#sum` and `#count` of a mean, `#order` of a last), else -1.</summary>
    protected static (int A, int B)[] PairColumns(IReadOnlyList<TSet> sets, Layout inLayout) => [.. sets.Select(s =>
        !s.Telemetry || s.Value is not TField f ? (-1, -1)
        : inLayout.Has(f.Name + "#sum") ? (inLayout.Index(f.Name + "#sum"), inLayout.Index(f.Name + "#count"))
        : inLayout.Has(f.Name + "#order") ? (inLayout.Index(f.Name + "#order"), -1) : (-1, -1))];

    /// <summary>The graph-data properties of an emit's sets, by their column among the evaluated sets: what its fragments carry.</summary>
    protected static IEnumerable<(string, int, Tag)> GraphProps(IReadOnlyList<TSet> sets) =>
        sets.Select((s, i) => (s.PropId, i, s.Value.Type.Ty.Tag, s.Telemetry)).Where(x => !x.Telemetry).Select(x => (x.PropId, x.i, x.Tag));

    protected static TVal ValueOf(Column col, int row, Batch b, (int A, int B) pair) =>
        new(col.IsDec ? col.D[row] : col.L[row], pair.A >= 0 && b.Cols[pair.A].Present(row) ? (b.Cols[pair.A].D[row], pair.B >= 0 ? b.Cols[pair.B].L[row] : 0) : null);
}

/// <summary>Emit entity: contributions per row; graph-data properties into the element state, telemetry properties into the telemetry plane at the row's window.</summary>
sealed class EmitEntityOp : SinkOp
{
    readonly (int A, int B)[] pairs;
    readonly TEmitEntity t; readonly VExpr id; readonly (TSet Set, VExpr Value)[] sets; readonly int ws; readonly Interner it;
    readonly LineageOrigin origin; readonly FragmentShape shape; readonly Column[] cols;
    public EmitEntityOp(TEmitEntity t, int sink, Layout inLayout, OpContext cx, Func<TExpr, VExpr> compile, (string, int, bool, string)[] lineage, (string, int[])? groupRef = null) : base(t)
    {
        this.t = t; it = cx.It; id = compile(t.EntityId); origin = new(t.Name, t.Rule, t.Update, lineage, groupRef);
        sets = [.. t.Sets.Select(s => (s, compile(s.Value)))]; cols = new Column[sets.Length];
        shape = new(it, sink, false, t.Update, t.TypeId, GraphProps(t.Sets));
        cx.Out.Lineage.Register(sink, origin);
        ws = inLayout.Has("windowStart") ? inLayout.Index("windowStart") : -1;
        if (id is VId v && t.Sets.Any(s => s.Telemetry)) v.TrackOwn = true;
        pairs = PairColumns(t.Sets, inLayout);
    }
    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var b = In(inputs, "in"); if (b is null || b.Rows == 0) return null;
        var idc = id.Eval(b); for (var s = 0; s < sets.Length; s++) cols[s] = sets[s].Value.Eval(b);
        var owns = (id as VId)?.Owns;
        for (var k = 0; k < b.Rows; k++)
        {
            var row = b.RowAt(k);
            if (idc.P is not null && !idc.P[row]) { cx.SkippedEmits++; continue; }
            var elem = (int)idc.L[row]; var w = b.Weight(row);
            for (var s = 0; s < sets.Length; s++)
            {
                var set = sets[s].Set; var col = cols[s];
                if (set.Telemetry && col.Present(row))
                    cx.Out.Telemetry(new WindowKey(elem, it.Intern(set.PropId), b.Cols[ws].L[row]), ValueOf(col, row, b, pairs[s]), w, set.Period, owns is null ? elem : owns.L[row], set.Combine);
            }
            if (!cx.Out.Contribute(shape, elem, -1, -1, cols, row, w)) continue;
            cx.Out.Lineage.Support(shape.Sink, elem, b, row, w);
        }
        return null;
    }
}

sealed class EmitRelOp : SinkOp
{
    readonly (int A, int B)[] pairs;
    readonly TEmitRel t; readonly VExpr src, tgt; readonly (TSet Set, VExpr Value)[] sets; readonly int ws; readonly Interner it;
    readonly Dictionary<(int, int), int> relIds = [];
    readonly LineageOrigin origin; readonly FragmentShape shape; readonly Column[] cols;
    public EmitRelOp(TEmitRel t, int sink, Layout inLayout, OpContext cx, Func<TExpr, VExpr> compile, (string, int, bool, string)[] lineage, (string, int[])? groupRef = null) : base(t)
    {
        this.t = t; it = cx.It; src = compile(t.Source); tgt = compile(t.Target); origin = new(t.Name, t.Rule, t.Update, lineage, groupRef);
        sets = [.. t.Sets.Select(s => (s, compile(s.Value)))]; cols = new Column[sets.Length];
        shape = new(it, sink, true, t.Update, t.TypeId, GraphProps(t.Sets));
        cx.Out.Lineage.Register(sink, origin);
        ws = inLayout.Has("windowStart") ? inLayout.Index("windowStart") : -1;
        if (t.Sets.Any(s => s.Telemetry)) foreach (var e in new[] { src, tgt }.OfType<VId>()) e.TrackOwn = true;
        pairs = PairColumns(t.Sets, inLayout);
        cx.Out.RegisterRelSink(sink, (s, d) => IdentityCatalog.RelationshipId(cx.Tenant, cx.ProjectionId, t.Key, s, d));
    }
    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var b = In(inputs, "in"); if (b is null || b.Rows == 0) return null;
        var sc = src.Eval(b); var tc = tgt.Eval(b); for (var i = 0; i < sets.Length; i++) cols[i] = sets[i].Value.Eval(b);
        var (so, to) = ((src as VId)?.Owns, (tgt as VId)?.Owns);
        Span<char> id = stackalloc char[KeyText.IdLength];
        for (var k = 0; k < b.Rows; k++)
        {
            var row = b.RowAt(k);
            if ((sc.P is not null && !sc.P[row]) || (tc.P is not null && !tc.P[row])) { cx.SkippedEmits++; continue; }
            var s = (int)sc.L[row]; var d = (int)tc.L[row]; var w = b.Weight(row);
            if (!relIds.TryGetValue((s, d), out var rel))
            {
                IdentityCatalog.RelationshipId(cx.Tenant, cx.ProjectionId, t.Key, it.Get(s), it.Get(d), id);
                relIds[(s, d)] = rel = it.Intern(id);         // a known id is found from its characters: no string is made
            }
            for (var i = 0; i < sets.Length; i++)
            {
                var set = sets[i].Set; var col = cols[i];
                if (set.Telemetry && col.Present(row)) cx.Out.Telemetry(new WindowKey(rel, it.Intern(set.PropId), b.Cols[ws].L[row]), ValueOf(col, row, b, pairs[i]), w, set.Period,
                    ((so is null ? s : so.L[row]) << 32) | (uint)(to is null ? d : to.L[row]), set.Combine);          // a relationship is written under the own ids of its two endpoints
            }
            if (!cx.Out.Contribute(shape, rel, s, d, cols, row, w)) continue;
            cx.Out.Lineage.Support(shape.Sink, rel, b, row, w);
        }
        return null;
    }
}

/// <summary>Link: an equivalence between two identity keys. An asserted policy with confidence at or above its threshold binds the right key to the left identity (rule B2).</summary>
sealed class LinkOp : SinkOp
{
    readonly TLink t; readonly VId left, right; readonly IdentityCatalog cat; readonly long[] buf;
    public LinkOp(TLink t, VId left, VId right, IdentityCatalog cat) : base(t) { this.t = t; this.left = left; this.right = right; this.cat = cat; buf = new long[right.Components]; }
    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var b = In(inputs, "in"); if (b is null || b.Rows == 0) return null;
        if (t.Mode != "assert" || t.Confidence < t.Threshold) return null;
        var lc = left.Eval(b);
        var rcols = right.EvalComponents(b, [], out _);
        for (var k = 0; k < b.Rows; k++)
        {
            var row = b.RowAt(k);
            if (b.Weight(row) <= 0 || (lc.P is not null && !lc.P[row])) continue;
            if (!right.CanonicalComponents(rcols, row, buf)) continue;
            cat.Bind(right.Domain, buf, (int)lc.L[row], left.Domain);
        }
        return null;
    }
}
