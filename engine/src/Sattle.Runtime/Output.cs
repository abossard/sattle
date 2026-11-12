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
        var bb = new BatchBuilder(OutLayout);
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
        var bb = new BatchBuilder(OutLayout);
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
public sealed record LineageInfo(string Sink, string? Rule, IReadOnlyList<LineageSource> Sources, long Total);

public sealed class OutputBuilder(Interner it)
{
    /// <summary>Per emitted element, the sink and the input elements bound by the last contributing row.</summary>
    public Dictionary<int, LineageInfo> Lineage { get; } = [];
    /// <summary>Per emitted element, every input row that supports it: a create emit of a sink whose input rows bind elements of a Match (or Traverse) adds the row with its weight and a retraction subtracts it, so what is left is exactly the rows that hold the element up. This is the complete lineage of a lifted element: an edge that stands for many lower edges, a node that stands for many lower nodes. A row is the elements it binds, as text (role, kind, graph port, handle). Elements emitted from an aggregate keep only the group-key lineage in `Lineage`.</summary>
    public Dictionary<int, Dictionary<string, long>> Supports { get; } = [];

    /// <summary>The elements a row binds, as text (role, kind, graph port, handle); empty when it binds none.</summary>
    public static string RowKey((string Role, int Col, bool IsRel, string Port)[] cols, Batch b, int row)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (role, col, isRel, port) in cols)
            if (b.Cols[col].Present(row)) sb.Append(role).Append('\u001f').Append(isRel ? 'r' : 'e').Append('\u001f').Append(port).Append('\u001f').Append((int)b.Cols[col].L[row]).Append('\u001e');
        return sb.ToString();
    }

    /// <summary>Per `Group` operator and group key, the input rows that make up the group (the rows it aggregates, as `RowKey` text with signed weights). An element emitted from the group's output names them all: `GroupRefs` says which group and key an element was emitted from.</summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, long>>> GroupMembers { get; } = [];
    public Dictionary<int, (string Group, string Key)> GroupRefs { get; } = [];

    public void AddMember(string group, string key, (string Role, int Col, bool IsRel, string Port)[] cols, Batch b, int row, long w)
    {
        if (w == 0) return;
        var rk = RowKey(cols, b, row); if (rk.Length == 0) return;
        if (!GroupMembers.TryGetValue(group, out var byKey)) GroupMembers[group] = byKey = [];
        if (!byKey.TryGetValue(key, out var rows)) byKey[key] = rows = [];
        var n = rows.GetValueOrDefault(rk) + w;
        if (n == 0) { rows.Remove(rk); if (rows.Count == 0) byKey.Remove(key); } else rows[rk] = n;
    }

    public void AddSupport(int elem, (string Role, int Col, bool IsRel, string Port)[] cols, Batch b, int row, long w)
    {
        if (cols.Length == 0 || w == 0) return;
        var key = RowKey(cols, b, row);
        if (key.Length == 0) return;
        if (!Supports.TryGetValue(elem, out var rows)) Supports[elem] = rows = [];
        var n = rows.GetValueOrDefault(key) + w;
        if (n == 0) { rows.Remove(key); if (rows.Count == 0) Supports.Remove(elem); } else rows[key] = n;
    }

    void MoveSupport(int from, int to)
    {
        if (from != to && GroupRefs.Remove(from, out var gref)) GroupRefs[to] = gref;
        if (from == to || !Supports.Remove(from, out var rows)) return;
        if (!Supports.TryGetValue(to, out var into)) Supports[to] = into = [];
        foreach (var (k, n) in rows) { var m = into.GetValueOrDefault(k) + n; if (m == 0) into.Remove(k); else into[k] = m; }
    }
    readonly Dictionary<int, Func<string, string, string>> relSinks = [];
    /// <summary>The relationship id function of a sink (the id hashes its endpoints), needed to re-key relationships when an identity merges.</summary>
    public void RegisterRelSink(int sink, Func<string, string, string> idOf) => relSinks[sink] = idOf;

    /// <summary>Two identities merged: what was emitted under the loser moves to the survivor (entity layers merge, relationships with the loser as an endpoint get the id of the survivor's endpoints). The old elements resolve to nothing and are removed by the next flush.</summary>
    public IReadOnlyList<(int From, int To)> Remap(int loser, int survivor)
    {
        var moved = new List<(int From, int To)> { (loser, survivor) };          // the entity's own telemetry follows it, whether or not this graph emitted it
        var lid = it.Get(loser); var sid = it.Get(survivor);
        if (elems.TryGetValue(loser, out var ls) && !ls.IsRel)
        {
            foreach (var c in ls.Contribs.ToList()) Contribute(survivor, false, c.Sink, c.Update, c.Type, null, null, c.Props, c.Weight);
            ls.Contribs.Clear();
            MoveSupport(loser, survivor);
            if (touchedSet.Add(loser)) touched.Add(loser);
        }
        var atLoser = relsAt.TryGetValue(lid, out var at) ? at.ToList() : [];
        foreach (var (elem, st) in atLoser.Where(elems.ContainsKey).Select(e => (e, elems[e])).Where(x => x.Item2.IsRel && x.Item2.Contribs.Any(c => c.Source == lid || c.Target == lid)).ToList())
        {
            foreach (var c in st.Contribs.Where(c => c.Source == lid || c.Target == lid).ToList())
            {
                st.Contribs.Remove(c);
                var ns = c.Source == lid ? sid : c.Source; var nt = c.Target == lid ? sid : c.Target;
                var idOf = relSinks[c.Sink];
                var ne = it.Intern(idOf(ns!, nt!));
                Contribute(ne, true, c.Sink, c.Update, c.Type, ns, nt, c.Props, c.Weight);
                if (Lineage.TryGetValue(elem, out var li)) Lineage[ne] = li;
                if (ne != elem && !moved.Contains((elem, ne))) { moved.Add((elem, ne)); MoveSupport(elem, ne); }
            }
            if (touchedSet.Add(elem)) touched.Add(elem);
        }
        // telemetry of a re-keyed relationship that has not reached the store yet follows it
        foreach (var (from, to) in moved)
            for (var i = 0; i < tele.Count; i++) if (tele[i].Key.Element == from) tele[i] = tele[i] with { Key = tele[i].Key with { Element = to } };
        Merged++;
        return moved;
    }
    public long Merged { get; private set; }

    sealed record Contribution(int Sink, bool Update, string Type, string? Source, string? Target, Props Props, long Weight);
    sealed class ElemState { public bool IsRel; public readonly List<Contribution> Contribs = []; }
    readonly Dictionary<int, ElemState> elems = [];
    /// <summary>The relationship elements that ever had an identity as an endpoint, so a merge visits those and not every element (entries go stale, Remap checks the contributions).</summary>
    readonly Dictionary<string, HashSet<int>> relsAt = [];
    readonly List<int> touched = [];
    readonly HashSet<int> touchedSet = [];
    readonly List<(WindowKey Key, TVal Value, long Weight, long Period, long Own)> tele = [];
    public long Rejections { get; private set; }
    /// <summary>Properties that fragments with different values assign (counted each time such an element is resolved): they are absent from the element.</summary>
    public long PropertyConflicts { get; private set; }
    /// <summary>The conflicts that hold now, by what they are about (Def. 6.4): the state that goes with the graph. `TakeChanged` gives the ones a commit reports.</summary>
    readonly Dictionary<string, Conflict> conflicts = new(StringComparer.Ordinal);
    readonly HashSet<string> changedConflicts = new(StringComparer.Ordinal);
    public IReadOnlyCollection<Conflict> Conflicts => [.. conflicts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Value)];

    /// <summary>Sets or clears the conflict about one subject (an element, one of its properties, a tuple, a window).</summary>
    void Note(string subject, Conflict? conflict)
    {
        var had = conflicts.TryGetValue(subject, out var old);
        if (conflict is null ? !had : had && old!.Text == conflict.Text) return;
        if (conflict is null) conflicts.Remove(subject); else conflicts[subject] = conflict;
        changedConflicts.Add(subject);
    }

    /// <summary>The conflicts that appeared, changed or ended since the last call: what a commit reports (an ended conflict comes with no values).</summary>
    public List<Conflict> TakeChanged()
    {
        var res = changedConflicts.Order(StringComparer.Ordinal).Select(s => conflicts.TryGetValue(s, out var c) ? c : new Conflict("resolved", s, null, null, [])).ToList();
        changedConflicts.Clear();
        return res;
    }
    static string Canonical(Value v) => v switch { DecimalValue d => Norm.Decimal(d.Q), IntegerValue i => i.N.ToString(System.Globalization.CultureInfo.InvariantCulture), TextValue t => t.S, BooleanValue b => b.B ? "true" : "false", InstantValue n => Norm.Instant(n.Ns), EntityValue e => e.Id, _ => v.ToString() ?? "" };
    /// <summary>Elements whose create fragments disagree on type or endpoints (counted each time such an element is resolved): they are absent.</summary>
    public long ElementConflicts { get; private set; }

    public void Contribute(int elem, bool isRel, int sink, bool update, string type, string? source, string? target, Props props, long weight)
    {
        if (!elems.TryGetValue(elem, out var st)) elems[elem] = st = new ElemState { IsRel = isRel };
        var i = st.Contribs.FindIndex(c => c.Sink == sink && c.Update == update && c.Type == type && c.Source == source && c.Target == target && c.Props.Equals(props));
        if (i >= 0)
        {
            var nw = st.Contribs[i].Weight + weight;
            if (nw == 0) st.Contribs.RemoveAt(i); else st.Contribs[i] = st.Contribs[i] with { Weight = nw };
        }
        else if (weight != 0)                // a retraction may arrive before the insertion it cancels: the weights add up in any order
        {
            st.Contribs.Add(new Contribution(sink, update, type, source, target, props, weight));
            if (isRel) foreach (var end in new[] { source, target }) if (end is not null) { if (!relsAt.TryGetValue(end, out var set)) relsAt[end] = set = []; set.Add(elem); }
        }
        if (touchedSet.Add(elem)) touched.Add(elem);
    }

    /// <summary>A telemetry row of the step: the window it writes, its value and weight, and the id it was written under before any link (`own`, Law 7.5 item 3); `combine` is the property's combine rule.</summary>
    public void Telemetry(WindowKey key, TVal value, long weight, long period, long own, string? combine)
    {
        tele.Add((key, value, weight, period, own));
        combineOf.TryAdd(key.Prop, combine);
    }

    Element? Resolve(int elem, ElemState st)
    {
        var creates = st.Contribs.Where(c => !c.Update && c.Weight > 0).OrderBy(c => c.Sink).ToList();
        var id = it.Get(elem);
        if (conflicts.Count > 0)
        {
            foreach (var subject in conflicts.Keys.Where(k => k.StartsWith("property " + id + " ", StringComparison.Ordinal)).ToList()) Note(subject, null);
            Note("element " + id, null);
        }
        if (creates.Count == 0) return null;
        var first = creates[0];
        if (creates.Any(c => c.Type != first.Type || c.Source != first.Source || c.Target != first.Target))        // creates that disagree on type or endpoints: the element is absent
        {
            ElementConflicts++;
            Note("element " + id, new Conflict("element", id, null, null, [.. creates.Select(c => $"{c.Type} {c.Source} -> {c.Target}").Distinct().Order(StringComparer.Ordinal)]));
            return null;
        }
        // Def. 6.4 (D34): the state is a function of the positive fragments, whatever their sink or order. A property with more than one value is absent and counted.
        var values = new Dictionary<string, Value>(StringComparer.Ordinal); HashSet<string>? conflicting = null;
        foreach (var c in st.Contribs) if (c.Weight > 0) foreach (var (k, v) in c.Props.Items)
            if (!values.TryAdd(k, v) && !values[k].Equals(v)) (conflicting ??= []).Add(k);
        if (conflicting is not null)
        {
            PropertyConflicts += conflicting.Count;
            foreach (var k in conflicting)
            {
                values.Remove(k);
                Note($"property {id} {k}", new Conflict("property", id, k, null, [.. st.Contribs.Where(c => c.Weight > 0).SelectMany(c => c.Props.Items).Where(x => x.Key == k).Select(x => Canonical(x.Value)).Distinct().Order(StringComparer.Ordinal)]));
            }
        }
        var props = Props.Of(values);
        return st.IsRel ? new Rel(id, first.Source!, first.Target!, first.Type, props) : new Entity(id, first.Type, props);
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
            var st = elems[elem];
            var after = Resolve(elem, st);
            var id = it.Get(elem);
            want[st.IsRel ? ElementKey.Rel(id) : ElementKey.Entity(id)] = after;
            if (after is null && st.Contribs.Count == 0 && !contestedBy.ContainsKey(id)) elems.Remove(elem);
            if (!st.IsRel) continue;
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
        touched.Clear(); touchedSet.Clear();
        foreach (var tuple in affected)
        {
            var set = contested[tuple];
            if (set.Count > 1) Rejections++;
            var winner = set.Order(StringComparer.Ordinal).FirstOrDefault();
            Note($"uniqueness {tuple.Item1} {tuple.Item2} {tuple.Item3}", set.Count > 1 ? new Conflict("uniqueness", winner!, null, null, [.. set.Order(StringComparer.Ordinal)]) : null);
            foreach (var claimant in set)
                want[ElementKey.Rel(claimant)] = claimant == winner && it.TryFind(claimant, out var h) && elems.TryGetValue(h, out var st) ? Resolve(h, st) : null;
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

    /// <summary>Windows that more than one row writes, or one row with a weight other than one: the rows that support them, value to weight. A window written by exactly one row (premise B1, the normal case) is not here: the store holds its value and that is its whole support.</summary>
    readonly Dictionary<WindowKey, Dictionary<TVal, long>> sharedWindows = [];
    /// <summary>Windows that rows with different values write at the same time (premise B1 broken): such a window has no value (D34).</summary>
    public long WindowConflicts { get; private set; }

    /// <summary>Law 7.5 item 3: rows written under two ids of one class are two contributions to a window, not two writes of one (premise B1). Per (element, property) the one own id that has written it, while there is one; once a second own id writes it (or a merge brings one), its windows keep their support per own id in `byOwn`.</summary>
    readonly Dictionary<(int Elem, int Prop), long> soleOwn = [];
    readonly HashSet<(int Elem, int Prop)> multiOwn = [];
    readonly Dictionary<WindowKey, Dictionary<long, Dictionary<TVal, long>>> byOwn = [];
    readonly Dictionary<int, string?> combineOf = [];
    /// <summary>Windows that two ids of one class write with different values under a rule that does not combine them (a mean stored as one number, a last without its order): such a window has no value.</summary>
    public long MergeConflicts { get; private set; }
    /// <summary>Windows written under more than one own id: the measure of how often Law 7.5 item 3 combines.</summary>
    public long WindowsOfTwoIds => byOwn.Count(w => w.Value.Count > 1);

    static TVal? StoredOf(ITelemetryStore store, GraphKey g, WindowKey key) => store.ReadWindow(g, [key.Element], [key.Prop], key.WindowStart, key.WindowStart + 1)[0].Points is { Count: 1 } p ? new TVal(p[0].Value, store.AuxOf(g, key)) : null;

    /// <summary>The support of each window of (element, property) by value, all written under `own`: what `sharedWindows` holds, else the stored value as one row.</summary>
    IEnumerable<(WindowKey Key, Dictionary<TVal, long> Support)> SupportsOf(ITelemetryStore store, GraphKey g, int elem, int prop)
    {
        var keys = new HashSet<WindowKey>(sharedWindows.Keys.Where(k => k.Element == elem && k.Prop == prop));
        foreach (var pt in store.ReadWindow(g, [elem], [prop], long.MinValue, long.MaxValue)[0].Points) keys.Add(new WindowKey(elem, prop, pt.WindowStart));
        foreach (var k in keys.OrderBy(k => k.WindowStart))
            yield return (k, sharedWindows.TryGetValue(k, out var s) ? s : StoredOf(store, g, k) is { } v ? new() { [v] = 1 } : []);
    }

    /// <summary>(element, property) gets a second own id: its windows so far were all written under `sole`.</summary>
    void BecomeMulti(ITelemetryStore store, GraphKey g, (int Elem, int Prop) ep, long sole)
    {
        foreach (var (k, support) in SupportsOf(store, g, ep.Elem, ep.Prop).ToList())
        {
            sharedWindows.Remove(k);
            if (support.Count > 0) byOwn[k] = new() { [sole] = support };
        }
        multiOwn.Add(ep); soleOwn.Remove(ep);
    }

    /// <summary>Before the store moves the windows of a merged element (`MoveElement`), the support of its windows follows it: under one own id when only one ever wrote the pair, else per own id.</summary>
    public void MoveTelemetry(ITelemetryStore store, GraphKey g, int from, int to)
    {
        var props = soleOwn.Keys.Concat(multiOwn).Where(ep => ep.Elem == from).Select(ep => ep.Prop).Distinct().ToList();
        foreach (var p in props)
        {
            (int, int) f = (from, p), t = (to, p);
            var fromSupports = multiOwn.Contains(f)
                ? byOwn.Where(w => w.Key.Element == from && w.Key.Prop == p).Select(w => (w.Key, w.Value)).ToList()
                : [.. SupportsOf(store, g, from, p).Where(x => x.Support.Count > 0).Select(x => (x.Key, new Dictionary<long, Dictionary<TVal, long>> { [soleOwn[f]] = x.Support }))];
            foreach (var (k, _) in fromSupports) { byOwn.Remove(k); sharedWindows.Remove(k); }
            if (!multiOwn.Contains(f) && !multiOwn.Contains(t) && (!soleOwn.TryGetValue(t, out var st) || st == soleOwn[f]))
            {
                // one own id on both sides: plain supports, re-keyed
                soleOwn[t] = soleOwn[f];
                foreach (var (k, owns) in fromSupports)
                {
                    var support = owns.Values.Single(); var nk = k with { Element = to };
                    if (sharedWindows.TryGetValue(nk, out var there) || StoredOf(store, g, nk) is { } _) { var into = there ?? (StoredOf(store, g, nk) is { } sv ? new() { [sv] = 1 } : []); foreach (var (v, w) in support) into[v] = into.GetValueOrDefault(v) + w; sharedWindows[nk] = into; }
                    else if (!(support.Count == 1 && support.Values.First() == 1)) sharedWindows[nk] = support;
                }
            }
            else
            {
                if (!multiOwn.Contains(t)) { if (soleOwn.TryGetValue(t, out var sole)) BecomeMulti(store, g, t, sole); else multiOwn.Add(t); }
                foreach (var (k, owns) in fromSupports)
                {
                    var nk = k with { Element = to };
                    if (!byOwn.TryGetValue(nk, out var into)) byOwn[nk] = into = [];
                    foreach (var (o, support) in owns)
                    {
                        if (!into.TryGetValue(o, out var sup)) into[o] = sup = [];
                        foreach (var (v, w) in support) { var n = sup.GetValueOrDefault(v) + w; if (n == 0) sup.Remove(v); else sup[v] = n; }
                    }
                }
            }
            soleOwn.Remove(f); multiOwn.Remove(f);
        }
    }

    /// <summary>The telemetry writes of the step: per window the weights of the step are added to what supports the window, and the window is set to the value its supporting rows give, or deleted when none is left.</summary>
    public List<Sample> FlushTelemetry(ITelemetryStore store, GraphKey g, long inputId, long widthNs)
    {
        var res = new List<Sample>();
        var step = new Dictionary<WindowKey, (Dictionary<(long Own, TVal Value), long> Net, long Period)>();
        var order = new List<WindowKey>();
        foreach (var (key, value, weight, period, own) in tele)
        {
            var ep = (key.Element, key.Prop);
            if (!multiOwn.Contains(ep))
            {
                if (!soleOwn.TryGetValue(ep, out var sole)) soleOwn[ep] = own;
                else if (sole != own) BecomeMulti(store, g, ep, sole);
            }
            if (!step.TryGetValue(key, out var cur)) { step[key] = cur = ([], period); order.Add(key); }
            var n = cur.Net.GetValueOrDefault((own, value)) + weight;
            if (n == 0) cur.Net.Remove((own, value)); else cur.Net[(own, value)] = n;
        }
        foreach (var key in order)
        {
            var (byRow, per) = step[key];
            if (byRow.Count == 0) continue;
            TVal? Stored() => StoredOf(store, g, key);
            void Set(TVal v) => res.Add(new Sample(key.Element, key.Prop, key.WindowStart, per, v.V, inputId, AuxA: v.Aux?.A, AuxB: v.Aux?.B));
            void Delete(TVal v) => res.Add(new Sample(key.Element, key.Prop, key.WindowStart, per, v.V, inputId, Deleted: true));
            if (multiOwn.Contains((key.Element, key.Prop))) { FlushOfTwoIds(key, byRow, Stored(), Set, Delete); continue; }
            var net = new Dictionary<TVal, long>();
            foreach (var ((_, v), w) in byRow) net[v] = net.GetValueOrDefault(v) + w;
            foreach (var v in net.Where(x => x.Value == 0).Select(x => x.Key).ToList()) net.Remove(v);
            if (net.Count == 0) continue;
            var shared = sharedWindows.TryGetValue(key, out var support);
            // one row out and one row in: the revision of a window that one row writes
            if (!shared && net.Count == 2 && net.Values.Sum() == 0 && net.Values.Max() == 1) { Set(net.First(x => x.Value == 1).Key); continue; }
            var stored = Stored();
            support ??= stored is { } one ? new() { [one] = 1 } : [];
            foreach (var (v, w) in net) { var n = support.GetValueOrDefault(v) + w; if (n == 0) support.Remove(v); else support[v] = n; }
            var values = support.Where(x => x.Value > 0).Select(x => x.Key).ToList();
            if (values.Count > 1) WindowConflicts++;
            Note($"window {key.Element} {key.Prop} {key.WindowStart}", values.Count > 1 ? new Conflict("window", it.Get(key.Element), it.Get(key.Prop), key.WindowStart, [.. values.OrderBy(x => x.V).Select(x => Norm.Decimal(x.V))]) : null);
            if (values.Count != 1) { if (stored is { } gone) Delete(gone); }
            else if (values[0] != stored) Set(values[0]);
            // only a window that is not written by exactly one row keeps its support here (a negative support is kept: its insertion may still come)
            if (support.Count == 0 || support.Count == 1 && support.Values.First() == 1) sharedWindows.Remove(key); else sharedWindows[key] = support;
        }
        tele.Clear();
        return res;
    }

    /// <summary>A window that rows under more than one own id write: under each own id premise B1 (one value, or none and a conflict); across them the property's rule, `sum`, `min` and `max` in their monoid, any other rule only equal values. A window without a value under one id has none (Law 7.5 item 3).</summary>
    void FlushOfTwoIds(WindowKey key, Dictionary<(long Own, TVal Value), long> byRow, TVal? stored, Action<TVal> set, Action<TVal> delete)
    {
        if (!byOwn.TryGetValue(key, out var owns)) byOwn[key] = owns = [];
        foreach (var ((o, v), w) in byRow)
        {
            if (!owns.TryGetValue(o, out var sup)) owns[o] = sup = [];
            var n = sup.GetValueOrDefault(v) + w; if (n == 0) sup.Remove(v); else sup[v] = n;
        }
        foreach (var o in owns.Where(x => x.Value.Count == 0).Select(x => x.Key).ToList()) owns.Remove(o);
        if (owns.Count == 0) byOwn.Remove(key);
        var perOwn = owns.OrderBy(x => x.Key).Select(x => x.Value.Where(v => v.Value > 0).Select(v => v.Key).ToList()).Where(vs => vs.Count > 0).ToList();
        TVal? value = null; Conflict? conflict = null;
        var subject = $"window {key.Element} {key.Prop} {key.WindowStart}";
        if (perOwn.FirstOrDefault(vs => vs.Count > 1) is { } broken) { WindowConflicts++; conflict = new Conflict("window", it.Get(key.Element), it.Get(key.Prop), key.WindowStart, [.. broken.OrderBy(x => x.V).Select(x => Norm.Decimal(x.V))]); }
        else if (perOwn.Count > 0)
        {
            var values = perOwn.Select(vs => vs[0]).ToList();
            // the values of the own ids, combined pairwise in their order: every rule is commutative and associative, and "no value" absorbs
            var rule = combineOf.GetValueOrDefault(key.Prop);
            value = values[0];
            foreach (var next in values.Skip(1))
                value = value is { } acc && InMemoryTelemetryStore.Combine(rule, acc.V, acc.Aux, next.V, next.Aux) is { } c ? new TVal(c.Value, c.Aux) : null;
            if (value is null) { MergeConflicts++; conflict = new Conflict("window", it.Get(key.Element), it.Get(key.Prop), key.WindowStart, [.. values.Distinct().OrderBy(x => x.V).Select(x => Norm.Decimal(x.V))]); }
        }
        Note(subject, conflict);
        if (value is { } v1) { if (v1 != stored) set(v1); }
        else if (stored is { } gone) delete(gone);
    }
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
    readonly (string Role, int Col, bool IsRel, string Port)[] lineage; readonly (string Group, int[] KeyCols)? groupRef;
    public EmitEntityOp(TEmitEntity t, int sink, Layout inLayout, OpContext cx, Func<TExpr, VExpr> compile, (string, int, bool, string)[] lineage, (string, int[])? groupRef = null) : base(t)
    {
        this.t = t; this.sink = sink; it = cx.It; id = compile(t.EntityId); this.lineage = lineage; this.groupRef = groupRef;
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
            if (!t.Update) { cx.Out.AddSupport(elem, lineage, b, row, w); if (w > 0 && groupRef is { } gr) cx.Out.GroupRefs[elem] = (gr.Group, string.Join(',', gr.KeyCols.Select(c => b.Cols[c].L[row]))); }
            if (w > 0) cx.Out.Lineage[elem] = Lineage.Of(t.Name, t.Rule, lineage, b, row, t.Update ? cx.Out.Lineage.GetValueOrDefault(elem) : null);
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
    readonly (string Role, int Col, bool IsRel, string Port)[] lineage; readonly (string Group, int[] KeyCols)? groupRef;
    public EmitRelOp(TEmitRel t, int sink, Layout inLayout, OpContext cx, Func<TExpr, VExpr> compile, (string, int, bool, string)[] lineage, (string, int[])? groupRef = null) : base(t)
    {
        this.t = t; this.sink = sink; it = cx.It; src = compile(t.Source); tgt = compile(t.Target); this.lineage = lineage; this.groupRef = groupRef;
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
            if (!t.Update) { cx.Out.AddSupport(rel, lineage, b, row, w); if (w > 0 && groupRef is { } gr) cx.Out.GroupRefs[rel] = (gr.Group, string.Join(',', gr.KeyCols.Select(c => b.Cols[c].L[row]))); }
            if (w > 0) cx.Out.Lineage[rel] = Lineage.Of(t.Name, t.Rule, lineage, b, row, t.Update ? cx.Out.Lineage.GetValueOrDefault(rel) : null);
        }
        return null;
    }
}

/// <summary>Link: an equivalence between two identity keys. An asserted policy with confidence at or above its threshold binds the right key to the left identity (rule B2).</summary>
sealed class LinkOp : SinkOp
{
    readonly TLink t; readonly VId left, right; readonly IdentityCatalog cat;
    public LinkOp(TLink t, VId left, VId right, IdentityCatalog cat) : base(t) { this.t = t; this.left = left; this.right = right; this.cat = cat; }
    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        var b = In(inputs, "in"); if (b is null || b.Rows == 0) return null;
        if (t.Mode != "assert" || t.Confidence < t.Threshold) return null;
        var lc = left.Eval(b);
        var rcols = right.EvalComponents(b, [], out _);
        var buf = new long[rcols.Length];
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

static class Lineage
{
    /// <summary>The sources of a row: its element-valued columns that come from a Match. An update emit keeps the sources of the create emit it refines.</summary>
    public static LineageInfo Of(string sink, string? rule, (string Role, int Col, bool IsRel, string Port)[] cols, Batch b, int row, LineageInfo? previous)
    {
        if (previous is not null) return previous;
        var list = new List<LineageSource>();
        foreach (var (role, col, isRel, port) in cols)
            if (b.Cols[col].Present(row) && list.Count < 16) list.Add(new LineageSource(role, (int)b.Cols[col].L[row], isRel, port));
        if (previous is not null && list.Count == 0) return previous;
        return new LineageInfo(previous?.Sink ?? sink, previous?.Rule ?? rule, list, cols.Length);
    }
}
