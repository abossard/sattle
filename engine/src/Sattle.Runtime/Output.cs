using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

/// <summary>Series: closed telemetry windows of the matched elements, joined to the structural rows by element handle. Bilinear: new rows against the windows seen so far, then new windows against all rows (section 11.1, section 11.2).</summary>
sealed class SeriesOp : PhysOp
{
    readonly TSeries t; readonly Layout inLayout; readonly int elemCol; readonly int[] propHandles; readonly bool[] readIsDec;
    readonly int outWs, outWe; readonly int[] outReadCol; readonly long within, period;
    sealed record Arr(long[] L, decimal[] D, bool[] P, long Weight);
    readonly Dictionary<int, List<Arr>> arrangement = [];
    readonly Dictionary<int, SortedDictionary<long, decimal?[]>> windows = [];
    long frontier = long.MinValue;

    // ---- fill and time to live (Def. 7.14 to 7.16) ----
    /// <summary>Per read, the sources that feed the property it reads (a function of the definitions, set at activation).</summary>
    public string[][] Feeding = [];
    readonly decimal?[] fills; readonly long? ttl; readonly int outNoData = -1;
    /// <summary>The windows with a loss mark, by their start, while they are in the look-back: such a window is not covered and holds no fill (Def. 7.14).</summary>
    readonly SortedSet<long> lost = [];
    bool Lost(long start) => lost.Count > 0 && lost.GetViewBetween(start, start + period - 1).Count > 0;
    /// <summary>Since which advance an element has had a row here (presence is sampled at advances), and the window of the no-data record an element has.</summary>
    readonly Dictionary<int, long> since = []; readonly Dictionary<int, long> marker = [];
    /// <summary>Per read, the time up to which its windows are covered and closed, as applied to the output so far: min(frontier, coveredUntil).</summary>
    long[] hi;
    /// <summary>The Series fills or has a time to live: its output also depends on the progress of its sources and on the frontier.</summary>
    public bool Fills => ttl is not null || fills.Any(f => f is not null);
    long Floor(long time) => time - ((time % period) + period) % period;

    long[] HiAt(long f, IReadOnlyDictionary<string, long> covered) =>
        [.. fills.Select((fill, i) => fill is null || f == long.MinValue || i >= Feeding.Length || Feeding[i].Length == 0 || !Feeding[i].All(covered.ContainsKey) ? long.MinValue : Math.Min(f, Feeding[i].Min(s => covered[s])))];

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
    }

    static bool SameRow(Arr a, long[] l, decimal[] d, bool[] p)
    {
        for (var i = 0; i < l.Length; i++) { if (a.P[i] != p[i]) return false; if (a.L[i] != l[i] || a.D[i] != d[i]) return false; }
        return true;
    }

    void WriteRow(BatchBuilder bb, Arr r, long ws, decimal?[] vals, long w, bool noData = false)
    {
        var row = bb.Add(w);
        var b = bb.Batch;
        for (var c = 0; c < inLayout.Count; c++)
        {
            if (!r.P[c]) { b.Cols[c].SetAbsent(row, b.N); continue; }
            if (inLayout.IsDec[c]) b.Cols[c].D[row] = r.D[c]; else b.Cols[c].L[row] = r.L[c];
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
    decimal?[]? Record(int elem, long start, long[] h, long f)
    {
        if (f != long.MinValue && start < f - within) return null;
        var stored = windows.TryGetValue(elem, out var ws) && ws.TryGetValue(start, out var v) ? v : null;
        var res = new decimal?[fills.Length];
        for (var i = 0; i < fills.Length; i++)
        {
            var x = stored?[i];
            if (x is null && fills[i] is { } fill && h[i] != long.MinValue && start + period <= h[i] && since.TryGetValue(elem, out var from) && start >= from && !Lost(start)) x = fill;
            if (x is null) return null;
            res[i] = x;
        }
        return res;
    }

    /// <summary>The window starts in [lo, up) at which an element can have a record: its stored windows and the grid windows a fill can hold.</summary>
    SortedSet<long> Starts(int elem, long lo, long up, long[] h)
    {
        var set = new SortedSet<long>();
        if (windows.TryGetValue(elem, out var ws)) foreach (var a in ws.Keys) if (a >= lo && a < up) set.Add(a);
        var top = h.Max();
        if (top != long.MinValue && since.TryGetValue(elem, out var from))
            for (var a = Floor(Math.Max(lo, from) + period - 1); a < up && a + period <= top; a += period) set.Add(a);
        return set;
    }

    /// <summary>Def. 7.16: the later of `since` and the end of the newest record lies more than the time to live before the frontier. So: no record that ends within the time to live, the element has not appeared within it, and it has an older record or a `since`.</summary>
    bool InNoData(int elem)
    {
        var life = ttl!.Value;
        for (var a = Floor(frontier - life - 1); a + period <= frontier; a += period) if (Record(elem, a, hi, frontier) is not null) return false;
        var known = since.TryGetValue(elem, out var from);
        if (known) return from < frontier - life;
        if (windows.TryGetValue(elem, out var ws)) foreach (var a in ws.Keys) if (Record(elem, a, hi, frontier) is not null) return true;
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
        if (ep.Loss is { } mark && (frontier == long.MinValue || mark + period > edge) && !lost.Contains(mark))
        {
            // a loss mark on a window: the fills of the window that contains it go
            var start = Floor(mark);
            var held = arrangement.Where(x => x.Value.Count > 0).Select(x => (Elem: x.Key, Rows: x.Value, Before: Record(x.Key, start, hi, frontier))).ToList();
            lost.Add(mark);
            foreach (var (elem, list, before) in held)
            {
                var after = Record(elem, start, hi, frontier);
                touched.Add(elem);
                if (Same(before, after)) continue;
                foreach (var r in list)
                {
                    if (before is not null) WriteRow(bb, r, start, before, -r.Weight);
                    if (after is not null) WriteRow(bb, r, start, after, r.Weight);
                }
            }
        }
        var rows = In(inputs, "in");
        if (rows is not null)
            for (var k = 0; k < rows.Rows; k++)
            {
                var i = rows.RowAt(k);
                var c = (int)rows.Cols[elemCol].L[i];
                var l = new long[inLayout.Count]; var d = new decimal[inLayout.Count]; var p = new bool[inLayout.Count];
                for (var col = 0; col < inLayout.Count; col++)
                {
                    p[col] = rows.Cols[col].Present(i);
                    if (inLayout.IsDec[col]) d[col] = rows.Cols[col].D[i]; else l[col] = rows.Cols[col].L[i];
                }
                var w = rows.Weight(i);
                if (!arrangement.TryGetValue(c, out var list)) arrangement[c] = list = [];
                var ex = list.FindIndex(a => SameRow(a, l, d, p));
                if (ex >= 0) { var nw = list[ex].Weight + w; if (nw == 0) list.RemoveAt(ex); else list[ex] = list[ex] with { Weight = nw }; }
                else if (w != 0) list.Add(new Arr(l, d, p, w));
                var row = new Arr(l, d, p, w);
                foreach (var a in Starts(c, edge, long.MaxValue, hi)) if (Record(c, a, hi, frontier) is { } vals) WriteRow(bb, row, a, vals, w);
                if (marker.TryGetValue(c, out var m)) WriteRow(bb, row, m, [], w, true);
                touched.Add(c);
            }
        var gd = ep.Graphs.GetValueOrDefault(t.GraphPort);
        if (gd is not null && gd.Telemetry.Count > 0)
        {
            var byWindow = new Dictionary<(int, long), List<WindowChange>>();
            foreach (var ch in gd.Telemetry)
            {
                if (Array.IndexOf(propHandles, ch.Key.Prop) < 0) continue;
                if (!byWindow.TryGetValue((ch.Key.Element, ch.Key.WindowStart), out var l)) byWindow[(ch.Key.Element, ch.Key.WindowStart)] = l = [];
                l.Add(ch);
            }
            foreach (var ((elem, wstart), changes) in byWindow)
            {
                if (frontier != long.MinValue && wstart < frontier - within) continue;
                var before = Record(elem, wstart, hi, frontier);
                if (!windows.TryGetValue(elem, out var ws)) windows[elem] = ws = [];
                var stored = ws.TryGetValue(wstart, out var cur) ? (decimal?[])cur.Clone() : new decimal?[propHandles.Length];
                foreach (var ch in changes) stored[Array.IndexOf(propHandles, ch.Key.Prop)] = ch.New;
                if (stored.All(x => !x.HasValue)) ws.Remove(wstart); else ws[wstart] = stored;
                var after = Record(elem, wstart, hi, frontier);
                touched.Add(elem);
                if (Same(before, after) || !arrangement.TryGetValue(elem, out var list)) continue;
                foreach (var r in list)
                {
                    if (before is not null) WriteRow(bb, r, wstart, before, -r.Weight);
                    if (after is not null) WriteRow(bb, r, wstart, after, r.Weight);
                }
            }
        }
        var advanced = ep.HasAdvance && ep.Lambda > frontier;
        if (advanced)
        {
            var edgeAfter = ep.Lambda - within;
            // the records that leave the look-back
            foreach (var (elem, list) in arrangement)
                foreach (var a in Starts(elem, edge, edgeAfter, hi))
                    if (Record(elem, a, hi, frontier) is { } vals) foreach (var r in list) WriteRow(bb, r, a, vals, -r.Weight);
            foreach (var (elem, ws) in windows.ToList())
            {
                foreach (var a in ws.Keys.Where(x => x < edgeAfter).ToList()) ws.Remove(a);
                if (ws.Count == 0) windows.Remove(elem);
            }
            // presence is sampled at the advance (Def. 7.14)
            foreach (var elem in since.Keys.Where(e => !arrangement.TryGetValue(e, out var l) || l.Count == 0).ToList()) since.Remove(elem);
            foreach (var (elem, list) in arrangement) if (list.Count > 0) since.TryAdd(elem, ep.Lambda);
            frontier = ep.Lambda;
            lost.RemoveWhere(m => m + period <= edgeAfter);
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
        var rows = In(inputs, "in");
        // step A: changed structural rows against the windows seen before this step
        if (rows is not null)
            for (var k = 0; k < rows.Rows; k++)
            {
                var i = rows.RowAt(k);
                var c = (int)rows.Cols[elemCol].L[i];
                var l = new long[inLayout.Count]; var d = new decimal[inLayout.Count]; var p = new bool[inLayout.Count];
                for (var col = 0; col < inLayout.Count; col++)
                {
                    p[col] = rows.Cols[col].Present(i);
                    if (inLayout.IsDec[col]) d[col] = rows.Cols[col].D[i]; else l[col] = rows.Cols[col].L[i];
                }
                var w = rows.Weight(i);
                if (!arrangement.TryGetValue(c, out var list)) arrangement[c] = list = [];
                var ex = list.FindIndex(a => SameRow(a, l, d, p));
                if (ex >= 0) { var nw = list[ex].Weight + w; if (nw == 0) list.RemoveAt(ex); else list[ex] = list[ex] with { Weight = nw }; }
                else if (w != 0) list.Add(new Arr(l, d, p, w));
                if (windows.TryGetValue(c, out var ws))
                    foreach (var (wstart, vals) in ws)
                        if (Complete(vals) && (frontier == long.MinValue || wstart >= frontier - within)) WriteRow(bb, new Arr(l, d, p, w), wstart, vals, w);
            }
        // step B: changed telemetry windows against every row (after step A)
        var gd = ep.Graphs.GetValueOrDefault(t.GraphPort);
        if (gd is not null && gd.Telemetry.Count > 0)
        {
            var byWindow = new Dictionary<(int, long), List<WindowChange>>();
            foreach (var ch in gd.Telemetry)
            {
                var pi = Array.IndexOf(propHandles, ch.Key.Prop);
                if (pi < 0) continue;
                if (!byWindow.TryGetValue((ch.Key.Element, ch.Key.WindowStart), out var l)) byWindow[(ch.Key.Element, ch.Key.WindowStart)] = l = [];
                l.Add(ch);
            }
            foreach (var ((elem, wstart), changes) in byWindow)
            {
                if (frontier != long.MinValue && wstart < frontier - within) continue;      // outside the look-back: neither kept nor joined (Def. 6.7)
                if (!windows.TryGetValue(elem, out var ws)) windows[elem] = ws = [];
                var before = ws.TryGetValue(wstart, out var cur) ? (decimal?[])cur.Clone() : new decimal?[propHandles.Length];
                var after = (decimal?[])before.Clone();
                foreach (var ch in changes) after[Array.IndexOf(propHandles, ch.Key.Prop)] = ch.New;
                if (after.All(x => !x.HasValue)) ws.Remove(wstart); else ws[wstart] = after;
                if (!arrangement.TryGetValue(elem, out var list)) continue;
                foreach (var r in list)
                {
                    if (Complete(before)) WriteRow(bb, r, wstart, before, -r.Weight);
                    if (Complete(after)) WriteRow(bb, r, wstart, after, r.Weight);
                }
            }
        }
        // advance: windows that leave the look-back are retracted
        if (ep.HasAdvance && ep.Lambda > frontier)
        {
            frontier = ep.Lambda;
            foreach (var (elem, ws) in windows.ToList())
            {
                foreach (var wstart in ws.Keys.Where(x => x < frontier - within).ToList())
                {
                    var vals = ws[wstart];
                    if (Complete(vals) && arrangement.TryGetValue(elem, out var list)) foreach (var r in list) WriteRow(bb, r, wstart, vals, -r.Weight);
                    ws.Remove(wstart);
                }
                if (ws.Count == 0) windows.Remove(elem);
            }
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

    /// <summary>Sets or clears the conflict about one subject (an element, one of its properties, a tuple, a window).</summary>
    public void Note(string subject, Conflict? conflict)
    {
        var had = now.TryGetValue(subject, out var old);
        if (conflict is null ? !had : had && old!.Text == conflict.Text) return;
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
    public List<Conflict> TakeChanged()
    {
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
    readonly LineageLayer lineage = new();
    /// <summary>Where the elements come from: the emits and groups write it, the lineage reader asks it.</summary>
    internal LineageLayer Lineage => lineage;

    public OutputBuilder(Interner it) { elements = new(it, conflicts); windows = new(it, conflicts); }

    /// <summary>A row says something about an element: its type, endpoints and properties, with the row's weight.</summary>
    public void Contribute(int elem, bool isRel, int sink, bool update, string type, string? source, string? target, Props props, long weight) => elements.Contribute(elem, isRel, sink, update, type, source, target, props, weight);
    /// <summary>The relationship id function of a sink (the id hashes its endpoints), needed to re-key relationships when an identity merges.</summary>
    public void RegisterRelSink(int sink, Func<string, string, string> idOf) => elements.RegisterRelSink(sink, idOf);
    /// <summary>A telemetry row of the step: the window it writes, its value and weight, and the id it was written under before any link (`own`, Law 7.5 item 3); `combine` is the property's combine rule.</summary>
    public void Telemetry(WindowKey key, TVal value, long weight, long period, long own, string? combine) => windows.Write(key, value, weight, period, own, combine);

    /// <summary>The edits that bring the graph to what the elements touched since the last flush resolve to.</summary>
    public List<Edit> Flush(Graph g) => elements.Flush(g);
    /// <summary>The samples that bring the store's windows to what the rows of the step give.</summary>
    public List<Sample> FlushTelemetry(ITelemetryStore store, GraphKey g, long inputId) => windows.Flush(store, g, inputId);

    /// <summary>Two identities merged: the rows and the lineage of the loser move to the survivor. Gives the elements whose telemetry must follow, old and new: the entity itself, whether or not this graph emitted it, and every relationship that got a new id.</summary>
    public IReadOnlyList<(int From, int To)> Remap(int loser, int survivor)
    {
        var moved = elements.Move(loser, survivor);
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
    public List<Conflict> TakeChanged() => conflicts.TakeChanged();
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

    protected static TVal ValueOf(Column col, int row, Batch b, (int A, int B) pair) =>
        new(col.IsDec ? col.D[row] : col.L[row], pair.A >= 0 && b.Cols[pair.A].Present(row) ? (b.Cols[pair.A].D[row], pair.B >= 0 ? b.Cols[pair.B].L[row] : 0) : null);
}

/// <summary>Emit entity: contributions per row; graph-data properties into the element state, telemetry properties into the telemetry plane at the row's window.</summary>
sealed class EmitEntityOp : SinkOp
{
    readonly (int A, int B)[] pairs;
    readonly TEmitEntity t; readonly int sink; readonly VExpr id; readonly (TSet Set, VExpr Value)[] sets; readonly int ws; readonly Interner it;
    readonly LineageOrigin origin;
    public EmitEntityOp(TEmitEntity t, int sink, Layout inLayout, OpContext cx, Func<TExpr, VExpr> compile, (string, int, bool, string)[] lineage, (string, int[])? groupRef = null) : base(t)
    {
        this.t = t; this.sink = sink; it = cx.It; id = compile(t.EntityId); origin = new(t.Name, t.Rule, t.Update, lineage, groupRef);
        sets = [.. t.Sets.Select(s => (s, compile(s.Value)))];
        ws = inLayout.Has("windowStart") ? inLayout.Index("windowStart") : -1;
        if (id is VId v && t.Sets.Any(s => s.Telemetry)) v.TrackOwn = true;
        pairs = PairColumns(t.Sets, inLayout);
    }
    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var b = In(inputs, "in"); if (b is null || b.Rows == 0) return null;
        var idc = id.Eval(b); var cols = sets.Select(s => s.Value.Eval(b)).ToArray();
        var owns = (id as VId)?.Owns;
        for (var k = 0; k < b.Rows; k++)
        {
            var row = b.RowAt(k);
            if (idc.P is not null && !idc.P[row]) { cx.SkippedEmits++; continue; }
            var elem = (int)idc.L[row]; var w = b.Weight(row);
            var props = new List<KeyValuePair<string, Value>>();
            for (var s = 0; s < sets.Length; s++)
            {
                var set = sets[s].Set; var col = cols[s];
                if (!col.Present(row)) continue;
                if (set.Telemetry)
                {
                    cx.Out.Telemetry(new WindowKey(elem, it.Intern(set.PropId), b.Cols[ws].L[row]), ValueOf(col, row, b, pairs[s]), w, set.Period, owns is null ? elem : owns.L[row], set.Combine);
                }
                else props.Add(KeyValuePair.Create(set.PropId, ToValue(col, row, set.Value.Type.Ty)));
            }
            if (t.Update && props.Count == 0) continue;
            cx.Out.Contribute(elem, false, sink, t.Update, t.TypeId, null, null, Props.Of(props), w);
            cx.Out.Lineage.Support(elem, origin, b, row, w);
        }
        return null;
    }
    Value ToValue(Column c, int row, Ty ty) => Codec.ToValue(c, row, ty, it);
}

sealed class EmitRelOp : SinkOp
{
    readonly (int A, int B)[] pairs;
    readonly TEmitRel t; readonly int sink; readonly VExpr src, tgt; readonly (TSet Set, VExpr Value)[] sets; readonly int ws; readonly Interner it;
    readonly Dictionary<(int, int), int> relIds = [];
    readonly LineageOrigin origin;
    public EmitRelOp(TEmitRel t, int sink, Layout inLayout, OpContext cx, Func<TExpr, VExpr> compile, (string, int, bool, string)[] lineage, (string, int[])? groupRef = null) : base(t)
    {
        this.t = t; this.sink = sink; it = cx.It; src = compile(t.Source); tgt = compile(t.Target); origin = new(t.Name, t.Rule, t.Update, lineage, groupRef);
        sets = [.. t.Sets.Select(s => (s, compile(s.Value)))];
        ws = inLayout.Has("windowStart") ? inLayout.Index("windowStart") : -1;
        if (t.Sets.Any(s => s.Telemetry)) foreach (var e in new[] { src, tgt }.OfType<VId>()) e.TrackOwn = true;
        pairs = PairColumns(t.Sets, inLayout);
        cx.Out.RegisterRelSink(sink, (s, d) => IdentityCatalog.RelationshipId(cx.Tenant, cx.ProjectionId, t.Key, s, d));
    }
    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var b = In(inputs, "in"); if (b is null || b.Rows == 0) return null;
        var sc = src.Eval(b); var tc = tgt.Eval(b); var cols = sets.Select(s => s.Value.Eval(b)).ToArray();
        var (so, to) = ((src as VId)?.Owns, (tgt as VId)?.Owns);
        for (var k = 0; k < b.Rows; k++)
        {
            var row = b.RowAt(k);
            if ((sc.P is not null && !sc.P[row]) || (tc.P is not null && !tc.P[row])) { cx.SkippedEmits++; continue; }
            var s = (int)sc.L[row]; var d = (int)tc.L[row]; var w = b.Weight(row);
            if (!relIds.TryGetValue((s, d), out var rel))
                relIds[(s, d)] = rel = it.Intern(IdentityCatalog.RelationshipId(cx.Tenant, cx.ProjectionId, t.Key, it.Get(s), it.Get(d)));
            var props = new List<KeyValuePair<string, Value>>();
            for (var i = 0; i < sets.Length; i++)
            {
                var set = sets[i].Set; var col = cols[i];
                if (!col.Present(row)) continue;
                if (set.Telemetry) cx.Out.Telemetry(new WindowKey(rel, it.Intern(set.PropId), b.Cols[ws].L[row]), ValueOf(col, row, b, pairs[i]), w, set.Period,
                    ((so is null ? s : so.L[row]) << 32) | (uint)(to is null ? d : to.L[row]), set.Combine);          // a relationship is written under the own ids of its two endpoints
                else props.Add(KeyValuePair.Create(set.PropId, Codec.ToValue(col, row, set.Value.Type.Ty, it)));
            }
            if (t.Update && props.Count == 0) continue;
            cx.Out.Contribute(rel, true, sink, t.Update, t.TypeId, it.Get(s), it.Get(d), Props.Of(props), w);
            cx.Out.Lineage.Support(rel, origin, b, row, w);
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
