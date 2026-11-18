using Sattle.Core;

namespace Sattle.Runtime;

/// <summary>Which rows support each telemetry window a projection writes. `Write` takes the rows of a step, `Flush` adds them to what supports each window and gives the samples that set or delete the window in the store, `Move` lets the support follow a merged element. A window that exactly one row writes, the normal case, keeps nothing here: the store holds its value.</summary>
sealed class WindowSupport(Interner it, ConflictSet conflicts)
{
    readonly List<(WindowKey Key, TVal Value, long Weight, long Period, long Own)> tele = [];

    /// <summary>A telemetry row of the step: the window it writes, its value and weight, and the id it was written under before any link (`own`, Law 7.5 item 3); `combine` is the property's combine rule.</summary>
    public void Write(WindowKey key, TVal value, long weight, long period, long own, string? combine)
    {
        tele.Add((key, value, weight, period, own));
        combineOf.TryAdd(key.Prop, combine); periodOf.TryAdd(key.Prop, period);
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
    readonly Dictionary<int, long> periodOf = [];
    /// <summary>Windows that two ids of one class write with different values under a rule that does not combine them (a mean stored as one number, a last without its order): such a window has no value.</summary>
    public long MergeConflicts { get; private set; }
    /// <summary>Windows written under more than one own id: the measure of how often Law 7.5 item 3 combines.</summary>
    public long WindowsOfTwoIds => byOwn.Count(w => w.Value.Count > 1);

    static TVal? StoredOf(ITelemetryStore store, GraphKey g, WindowKey key) => store.ValueOf(g, key) is { } v ? new TVal(v, store.AuxOf(g, key)) : null;

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

    /// <summary>The windows of a merged element follow it. First the support of each window moves: under one own id when only one ever wrote the pair, else per own id. Then the store moves the values (`MoveElement`). Last each moved window is set to what its supporting rows give, the same rule a write follows (`Settle`), so the result does not depend on whether the link came before the rows or after them. Returns the samples that correct the store.</summary>
    public List<Sample> Move(ITelemetryStore store, GraphKey g, int from, int to, Func<int, string?> combine, long inputId)
    {
        var moved = MoveSupport(store, g, from, to);
        store.MoveElement(g, from, to, combine);
        var res = new List<Sample>(); List<(long Own, TVal Value, long Weight)> none = []; List<(TVal Value, long Weight)> scratch = [];
        foreach (var key in moved) Settle(store, g, key, none, periodOf.GetValueOrDefault(key.Prop), inputId, res, moved: true, scratch);
        return res;
    }

    List<WindowKey> MoveSupport(ITelemetryStore store, GraphKey g, int from, int to)
    {
        var moved = new List<WindowKey>();
        // a row of this step that has not reached the store yet follows its element
        for (var i = 0; i < tele.Count; i++) if (tele[i].Key.Element == from) tele[i] = tele[i] with { Key = tele[i].Key with { Element = to } };
        var props = soleOwn.Keys.Concat(multiOwn).Where(ep => ep.Elem == from).Select(ep => ep.Prop).Distinct().ToList();
        foreach (var p in props)
        {
            (int, int) f = (from, p), t = (to, p);
            var fromSupports = multiOwn.Contains(f)
                ? byOwn.Where(w => w.Key.Element == from && w.Key.Prop == p).Select(w => (w.Key, w.Value)).ToList()
                : [.. SupportsOf(store, g, from, p).Where(x => x.Support.Count > 0).Select(x => (x.Key, new Dictionary<long, Dictionary<TVal, long>> { [soleOwn[f]] = x.Support }))];
            foreach (var (k, _) in fromSupports)
            {
                byOwn.Remove(k); sharedWindows.Remove(k);
                conflicts.Note($"window {k.Element} {k.Prop} {k.WindowStart}", null);          // a conflict of the window is one of the survivor's window now
                moved.Add(k with { Element = to });
            }
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
        return moved;
    }

    /// <summary>The telemetry writes of the step: per window the weights of the step are added to what supports the window, and the window is set to the value its supporting rows give, or deleted when none is left. The windows settle in the order their first row came.</summary>
    public List<Sample> Flush(ITelemetryStore store, GraphKey g, long inputId)
    {
        var res = new List<Sample>();
        if (tele.Count == 0) return res;
        // the windows of the step, and per window its rows in order as a chain: first row, last row, and per row the next one
        var windowOf = new Dictionary<WindowKey, int>();
        var windows = new List<(WindowKey Key, long Period, int First, int Last)>();
        var next = System.Buffers.ArrayPool<int>.Shared.Rent(tele.Count);
        try
        {
            for (var i = 0; i < tele.Count; i++)
            {
                var (key, _, _, period, own) = tele[i];
                var ep = (key.Element, key.Prop);
                if (!multiOwn.Contains(ep))
                {
                    if (!soleOwn.TryGetValue(ep, out var sole)) soleOwn[ep] = own;
                    else if (sole != own) BecomeMulti(store, g, ep, sole);
                }
                next[i] = -1;
                if (windowOf.TryGetValue(key, out var w)) { next[windows[w].Last] = i; windows[w] = windows[w] with { Last = i }; }
                else { windowOf[key] = windows.Count; windows.Add((key, period, i, i)); }
            }
            List<(long Own, TVal Value, long Weight)> byRow = []; List<(TVal Value, long Weight)> scratch = [];
            foreach (var (key, period, first, _) in windows)
            {
                byRow.Clear();
                for (var i = first; i >= 0; i = next[i])
                {
                    var (_, value, weight, _, own) = tele[i];
                    var at = -1;
                    for (var k = 0; k < byRow.Count && at < 0; k++) if (byRow[k].Own == own && byRow[k].Value == value) at = k;
                    if (at < 0) byRow.Add((own, value, weight));
                    else if (byRow[at].Weight + weight == 0) byRow.RemoveAt(at);
                    else byRow[at] = byRow[at] with { Weight = byRow[at].Weight + weight };
                }
                Settle(store, g, key, byRow, period, inputId, res, moved: false, scratch);
            }
        }
        finally { System.Buffers.ArrayPool<int>.Shared.Return(next); }
        tele.Clear();
        return res;
    }

    /// <summary>Adds `weight` to `value` in a list of value and weight; a value whose weight becomes 0 leaves the list.</summary>
    static void AddWeight(List<(TVal Value, long Weight)> to, TVal value, long weight)
    {
        for (var i = 0; i < to.Count; i++)
            if (to[i].Value == value)
            {
                if (to[i].Weight + weight == 0) to.RemoveAt(i); else to[i] = (value, to[i].Weight + weight);
                return;
            }
        if (weight != 0) to.Add((value, weight));
    }

    static void Set(List<Sample> res, WindowKey key, long per, long inputId, TVal v) => res.Add(new Sample(key.Element, key.Prop, key.WindowStart, per, v.V, inputId, AuxA: v.Aux?.A, AuxB: v.Aux?.B));
    static void Delete(List<Sample> res, WindowKey key, long per, long inputId, TVal v) => res.Add(new Sample(key.Element, key.Prop, key.WindowStart, per, v.V, inputId, Deleted: true));
    static string Subject(WindowKey key) => $"window {key.Element} {key.Prop} {key.WindowStart}";

    /// <summary>One window after rows came or went (`byRow`: own id, value and weight, no weight 0), or after a merge moved its support (`moved`, no rows): the rows are added to what supports the window, and the window is set to the value its supporting rows give, or deleted when they give none. `scratch` is a working list.
    /// A window has a conflict only while its support is kept here: a conflict needs two values, and a support of two values is kept. So a window without kept support has no conflict to clear, and its subject is not written.</summary>
    void Settle(ITelemetryStore store, GraphKey g, WindowKey key, List<(long Own, TVal Value, long Weight)> byRow, long per, long inputId, List<Sample> res, bool moved, List<(TVal Value, long Weight)> scratch)
    {
        if (byRow.Count == 0 && !moved) return;
        if (multiOwn.Contains((key.Element, key.Prop)))
        {
            var rows = new Dictionary<(long Own, TVal Value), long>();
            foreach (var (o, v, w) in byRow) rows[(o, v)] = w;
            FlushOfTwoIds(key, rows, StoredOf(store, g, key), v => Set(res, key, per, inputId, v), v => Delete(res, key, per, inputId, v));
            return;
        }
        var net = scratch; net.Clear();
        foreach (var (_, v, w) in byRow) AddWeight(net, v, w);
        if (net.Count == 0 && !moved) return;
        var shared = sharedWindows.TryGetValue(key, out var support);
        // one row out and one row in: the revision of a window that one row writes
        if (!shared && net.Count == 2 && net[0].Weight + net[1].Weight == 0 && Math.Max(net[0].Weight, net[1].Weight) == 1) { Set(res, key, per, inputId, net[0].Weight == 1 ? net[0].Value : net[1].Value); return; }
        var stored = StoredOf(store, g, key);
        // the support after the step: what is kept here, else the stored value as one row; plus the step
        if (support is not null) { foreach (var (v, w) in net) { var n = support.GetValueOrDefault(v) + w; if (n == 0) support.Remove(v); else support[v] = n; } net.Clear(); foreach (var (v, w) in support) net.Add((v, w)); }
        else if (stored is { } one) AddWeight(net, one, 1);
        TVal? value = null; var values = 0;
        foreach (var (v, w) in net) if (w > 0) { values++; value = v; }
        if (values > 1) WindowConflicts++;
        if (shared || moved || values > 1) conflicts.Note(Subject(key), values > 1 ? new Conflict("window", it.Get(key.Element), it.Get(key.Prop), key.WindowStart, [.. net.Where(x => x.Weight > 0).Select(x => x.Value).OrderBy(x => x.V).Select(x => Norm.Decimal(x.V))]) : null);
        if (values != 1) { if (stored is { } gone) Delete(res, key, per, inputId, gone); }
        else if (value != stored) Set(res, key, per, inputId, value!.Value);
        // only a window that is not written by exactly one row keeps its support here (a negative support is kept: its insertion may still come)
        if (net.Count == 0 || net.Count == 1 && net[0].Weight == 1) sharedWindows.Remove(key);
        else if (!shared) sharedWindows[key] = net.ToDictionary(x => x.Value, x => x.Weight);
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
        var subject = Subject(key);
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
        conflicts.Note(subject, conflict);
        if (value is { } v1) { if (v1 != stored) set(v1); }
        else if (stored is { } gone) delete(gone);
    }
}
