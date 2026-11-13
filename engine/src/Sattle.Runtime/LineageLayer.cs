using Sattle.Core;

namespace Sattle.Runtime;

/// <summary>What one emit says about where its elements come from, fixed per sink: its name and rule, whether it creates or updates, the element columns of its input rows, and the group whose output it reads, if it reads one.</summary>
sealed record LineageOrigin(string Sink, string? Rule, bool Update, (string Role, int Col, bool IsRel, string Port)[] Cols, (string Group, int[] KeyCols)? Group);

/// <summary>Where an element comes from: the sink and rule that emitted it and the input elements it is made of. With `FromRows` the sources are every input element of the rows that hold it up, and `Contributions` is the weight of those rows; without, they are the elements of the last row that emitted it, and `Total` is how many element columns such a row has.</summary>
sealed record ElementLineage(string? Sink, string? Rule, IReadOnlyList<LineageSource> Sources, bool FromRows, long Contributions, long Total);

/// <summary>Where the elements of a projection come from. The emits tell it per row (`Support`), a `Group` tells it the rows of each of its groups (`Member`), a merge moves what is known about one element to another (`Move`), and `Of` answers for one element. Nothing outside reads its state.</summary>
sealed class LineageLayer
{
    /// <summary>Per emitted element, the sink and the input elements bound by the last contributing row.</summary>
    readonly Dictionary<int, LineageInfo> infos = [];
    /// <summary>Per emitted element, every input row that supports it: a create emit of a sink whose input rows bind elements of a Match (or Traverse) adds the row with its weight and a retraction subtracts it, so what is left is exactly the rows that hold the element up. This is the complete lineage of a lifted element: an edge that stands for many lower edges, a node that stands for many lower nodes. A row is the elements it binds, as text (role, kind, graph port, handle). Elements emitted from an aggregate keep only the group-key lineage in `Lineage`.</summary>
    readonly Dictionary<int, Dictionary<string, long>> supports = [];
    /// <summary>Per `Group` operator and group key, the input rows that make up the group (the rows it aggregates, as `RowKey` text with signed weights). An element emitted from the group's output names them all: `GroupRefs` says which group and key an element was emitted from.</summary>
    readonly Dictionary<string, Dictionary<string, Dictionary<string, long>>> groupMembers = [];
    readonly Dictionary<int, (string Group, string Key)> groupRefs = [];
    sealed record LineageInfo(string Sink, string? Rule, IReadOnlyList<LineageSource> Sources, long Total);

    /// <summary>The elements a row binds, as text (role, kind, graph port, handle); empty when it binds none.</summary>
    static string RowKey((string Role, int Col, bool IsRel, string Port)[] cols, Batch b, int row)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (role, col, isRel, port) in cols)
            if (b.Cols[col].Present(row)) sb.Append(role).Append('\u001f').Append(isRel ? 'r' : 'e').Append('\u001f').Append(port).Append('\u001f').Append((int)b.Cols[col].L[row]).Append('\u001e');
        return sb.ToString();
    }

    /// <summary>A row of a group came or went: `Group` operators call this for the rows they aggregate.</summary>
    public void Member(string group, string key, (string Role, int Col, bool IsRel, string Port)[] cols, Batch b, int row, long w)
    {
        if (w == 0) return;
        var rk = RowKey(cols, b, row); if (rk.Length == 0) return;
        if (!groupMembers.TryGetValue(group, out var byKey)) groupMembers[group] = byKey = [];
        if (!byKey.TryGetValue(key, out var rows)) byKey[key] = rows = [];
        var n = rows.GetValueOrDefault(rk) + w;
        if (n == 0) { rows.Remove(rk); if (rows.Count == 0) byKey.Remove(key); } else rows[rk] = n;
    }

    /// <summary>A row of an emit that says `elem` exists (or, for an update emit, refines it), with its weight.</summary>
    public void Support(int elem, LineageOrigin origin, Batch b, int row, long w)
    {
        if (!origin.Update)
        {
            AddSupport(elem, origin.Cols, b, row, w);
            if (w > 0 && origin.Group is { } gr) groupRefs[elem] = (gr.Group, string.Join(',', gr.KeyCols.Select(c => b.Cols[c].L[row])));
        }
        if (w > 0) infos[elem] = InfoOf(origin.Sink, origin.Rule, origin.Cols, b, row, origin.Update ? infos.GetValueOrDefault(elem) : null);
    }

    void AddSupport(int elem, (string Role, int Col, bool IsRel, string Port)[] cols, Batch b, int row, long w)
    {
        if (cols.Length == 0 || w == 0) return;
        var key = RowKey(cols, b, row);
        if (key.Length == 0) return;
        if (!supports.TryGetValue(elem, out var rows)) supports[elem] = rows = [];
        var n = rows.GetValueOrDefault(key) + w;
        if (n == 0) { rows.Remove(key); if (rows.Count == 0) supports.Remove(elem); } else rows[key] = n;
    }

    /// <summary>The sources of a row: its element-valued columns that come from a Match. An update emit keeps the sources of the create emit it refines.</summary>
    static LineageInfo InfoOf(string sink, string? rule, (string Role, int Col, bool IsRel, string Port)[] cols, Batch b, int row, LineageInfo? previous)
    {
        if (previous is not null) return previous;
        var list = new List<LineageSource>();
        foreach (var (role, col, isRel, port) in cols)
            if (b.Cols[col].Present(row) && list.Count < 16) list.Add(new LineageSource(role, (int)b.Cols[col].L[row], isRel, port));
        if (previous is not null && list.Count == 0) return previous;
        return new LineageInfo(previous?.Sink ?? sink, previous?.Rule ?? rule, list, cols.Length);
    }

    /// <summary>What is known about `from` is now known about `to`: its sink and group replace those of `to`, its rows add to them. A merge of two identities calls this for every element whose rows moved, so the survivor has the lineage of the element that lost.</summary>
    public void Move(int from, int to)
    {
        if (from == to) return;
        if (infos.Remove(from, out var info)) infos[to] = info;
        if (groupRefs.Remove(from, out var gref)) groupRefs[to] = gref;
        if (!supports.Remove(from, out var rows)) return;
        if (!supports.TryGetValue(to, out var into)) supports[to] = into = [];
        foreach (var (k, n) in rows) { var m = into.GetValueOrDefault(k) + n; if (m == 0) into.Remove(k); else into[k] = m; }
    }

    /// <summary>Where `elem` comes from, or null when nothing is known about it.</summary>
    public ElementLineage? Of(int elem)
    {
        var info = infos.GetValueOrDefault(elem);
        var rows = groupRefs.TryGetValue(elem, out var gref) && groupMembers.TryGetValue(gref.Group, out var byKey) && byKey.TryGetValue(gref.Key, out var members) ? members : supports.GetValueOrDefault(elem);
        if (rows is null) return info is null ? null : new(info.Sink, info.Rule, info.Sources, false, info.Sources.Count, info.Total);
        var sources = new HashSet<LineageSource>();
        foreach (var key in rows.Keys)
            foreach (var part in key.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = part.Split('\u001f');
                sources.Add(new LineageSource(f[0], int.Parse(f[3]), f[1] == "r", f[2]));
            }
        return new(info?.Sink, info?.Rule, [.. sources], true, rows.Values.Sum(), info?.Total ?? 0);
    }

    /// <summary>The elements a sink is recorded for.</summary>
    public IEnumerable<int> Elements => infos.Keys;

    /// <summary>How much is held; it changes when lineage is added or removed.</summary>
    public long Size => (long)supports.Count + infos.Count + groupRefs.Count + groupMembers.Sum(m => m.Value.Count);
}
