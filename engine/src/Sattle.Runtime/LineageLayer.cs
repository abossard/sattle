using Sattle.Core;

namespace Sattle.Runtime;

/// <summary>What one emit says about where its elements come from, fixed per sink: its name and rule, whether it creates or updates, the element columns of its input rows, and the group whose output it reads, if it reads one.</summary>
sealed record LineageOrigin(string Sink, string? Rule, bool Update, (string Role, int Col, bool IsRel, string Port)[] Cols, (string Group, int[] KeyCols)? Group);

/// <summary>Where an element comes from: the sink and rule that emitted it and the input elements it is made of. With `FromRows` the sources are every input element of the rows that hold it up, and `Contributions` is the weight of those rows; without, they are the elements of the last row that emitted it, and `Total` is how many element columns such a row has.</summary>
sealed record ElementLineage(string? Sink, string? Rule, IReadOnlyList<LineageSource> Sources, bool FromRows, long Contributions, long Total);

/// <summary>Where the elements of a projection come from. The emits tell it per row (`Support`), a `Group` tells it the rows of each of its groups (`Member`), a merge moves what is known about one element to another (`Move`), and `Of` answers for one element. It keeps only the rows that hold something up, as Z-sets of handles (#40): per emit a table of (element, group key, the handle of each element column) arranged by element, per `Group` a table of (group key, member handles) arranged by key. A handle is an interner code in 4 bytes, -1 when its column is absent; a group key part takes 8. A row at weight zero is gone, so nothing outlives what it supports. The sink and rule of an element are read from the element layer when asked. Nothing outside reads its state.</summary>
sealed class LineageLayer(ElementLayer elements)
{
    /// <summary>The support rows of one emit: its origin, its table and the table's arrangement by element (column 0). The last column is the own ids of the row's keys when they differ from what the element implies (packed as a event row's own), else -1: a split moves the rows of the part that leaves by it.</summary>
    sealed class SinkRows(LineageOrigin origin)
    {
        public readonly LineageOrigin Origin = origin;
        public readonly int KeyWidth = origin.Group?.KeyCols.Length ?? 0;
        public readonly Table Rows = new([.. Widths(1, origin.Group?.KeyCols.Length ?? 0, origin.Cols.Length), 8], 2 + (origin.Group?.KeyCols.Length ?? 0) + origin.Cols.Length);
        public Arrangement? byElem;
        public Arrangement ByElem => byElem ??= Rows.Arrange(0);
    }
    /// <summary>The member rows of one `Group`: its element columns, its table and the table's arrangement by group key.</summary>
    sealed class MemberRows
    {
        public readonly (string Role, int Col, bool IsRel, string Port)[] Cols;
        public readonly int KeyWidth;
        public readonly Table Rows;
        public readonly Arrangement ByKey;
        public MemberRows((string Role, int Col, bool IsRel, string Port)[] cols, int keyWidth)
        {
            Cols = cols; KeyWidth = keyWidth;
            Rows = new(Widths(0, keyWidth, cols.Length), keyWidth + cols.Length);
            ByKey = Rows.Arrange([.. Enumerable.Range(0, keyWidth)]);
        }
    }
    /// <summary>Column widths: `elements` element handles first (the element a sink row holds up), then the group key parts, then a handle per element column.</summary>
    static int[] Widths(int elements, int keyParts, int cols) => [.. Enumerable.Repeat(4, elements), .. Enumerable.Repeat(8, keyParts), .. Enumerable.Repeat(4, cols)];

    /// <summary>By sink number (the emit's place in the projection).</summary>
    readonly List<SinkRows?> sinks = [];
    readonly Dictionary<string, MemberRows> groups = [];

    /// <summary>An emit says where its rows come from, once when it is built.</summary>
    public void Register(int sink, LineageOrigin origin)
    {
        while (sinks.Count <= sink) sinks.Add(null);
        if (sinks[sink]?.Origin.Sink != origin.Sink) sinks[sink] = new SinkRows(origin);
    }
    /// <summary>A `Group` with element columns among its input, once when it is built.</summary>
    public void RegisterGroup(string group, int keyWidth, (string Role, int Col, bool IsRel, string Port)[] cols)
    {
        if (cols.Length > 0 && !groups.ContainsKey(group)) groups[group] = new MemberRows(cols, keyWidth);
    }

    /// <summary>Writes the handles of a row's element columns from `at` on, -1 for an absent one; false when every one is absent.</summary>
    static bool Handles((string Role, int Col, bool IsRel, string Port)[] cols, Batch b, int row, Span<long> key, int at)
    {
        var any = false;
        for (var i = 0; i < cols.Length; i++)
        {
            var c = b.Cols[cols[i].Col];
            if (c.Present(row)) { key[at + i] = (int)c.L[row]; any = true; }
            else key[at + i] = -1;
        }
        return any;
    }

    /// <summary>A row of a group came or went: `Group` operators call this for the rows they aggregate.</summary>
    public void Member(string group, ReadOnlySpan<long> groupKey, Batch b, int row, long w)
    {
        if (w == 0 || !groups.TryGetValue(group, out var m)) return;
        Span<long> key = stackalloc long[m.Rows.Columns]; key.Clear();
        groupKey.CopyTo(key);
        if (Handles(m.Cols, b, row, key, m.KeyWidth)) m.Rows.Add(key, w);
    }

    /// <summary>A row of an emit that says `elem` exists, with its weight. An update emit refines an element and holds nothing up.</summary>
    public void Support(int sink, int elem, long own, Batch b, int row, long w)
    {
        var s = sinks[sink]!;
        if (s.Origin.Update || w == 0) return;
        Span<long> key = stackalloc long[s.Rows.Columns]; key.Clear();
        key[0] = elem;
        if (s.Origin.Group is { } gr) for (var i = 0; i < gr.KeyCols.Length; i++) key[1 + i] = b.Cols[gr.KeyCols[i]].L[row];
        key[^1] = own;
        if (Handles(s.Origin.Cols, b, row, key, 1 + s.KeyWidth) || s.Origin.Group is not null) s.Rows.Add(key, w);
    }

    /// <summary>What is known about `from` is now known about `to`: its rows add to those of `to`, a row under the own ids `from` implies now under `implied`. A merge of two identities calls this for every element whose rows moved, so the survivor has the lineage of the element that lost.</summary>
    public void Move(int from, int to, long implied) => Move(from, to, _ => true, implied);

    /// <summary>A split: the rows of `from` whose own ids `which` holds move to `to`. A row under the ids `from` implies stays: the representative stays with them.</summary>
    public void Split(int from, int to, Func<long, bool> which) => Move(from, to, o => o != -1 && which(o), -1);

    void Move(int from, int to, Func<long, bool> which, long implied)
    {
        if (from == to) return;
        foreach (var s in sinks)
        {
            if (s is null) continue;
            var rows = s.ByElem.Rows([from]);
            if (rows.Count == 0) continue;
            var key = new long[s.Rows.Columns];         // a merge is rare
            foreach (var r in rows)
            {
                for (var c = 0; c < key.Length; c++) key[c] = s.Rows.Get(r, c);
                if (!which(key[^1])) continue;
                var w = s.Rows.Weight(r);
                s.Rows.Add(key, -w);
                key[0] = to;
                if (key[^1] == -1) key[^1] = implied;
                s.Rows.Add(key, w);
            }
        }
    }

    static IEnumerable<LineageSource> Sources((string Role, int Col, bool IsRel, string Port)[] cols, Table t, int r, int at)
    {
        for (var i = 0; i < cols.Length; i++)
            if (t.Get(r, at + i) is var h and >= 0) yield return new LineageSource(cols[i].Role, (int)h, cols[i].IsRel, cols[i].Port);
    }

    /// <summary>Where `elem` comes from, or null when nothing is known about it: the sink and rule that create it (the smallest sink with a create, as the element layer resolves it), and the input elements of the rows that hold it up. An element emitted from a group's output names the members of its group keys; one whose sink binds no element column names none.</summary>
    public ElementLineage? Of(int elem)
    {
        var create = elements.CreateSink(elem);
        var info = create >= 0 && create < sinks.Count ? sinks[create]?.Origin : null;
        var sources = new HashSet<LineageSource>(); long rows = 0, members = 0;
        var memberSources = new HashSet<LineageSource>(); var keys = new HashSet<(string, string)>();
        foreach (var s in sinks)
        {
            if (s is null) continue;
            var key = new long[s.KeyWidth];
            for (var r = s.ByElem.First([elem]); r >= 0; r = s.ByElem.Next(r))
            {
                var any = false;
                foreach (var x in Sources(s.Origin.Cols, s.Rows, r, 1 + s.KeyWidth)) { sources.Add(x); any = true; }
                if (any) rows += s.Rows.Weight(r);
                if (s.Origin.Group is not { } gr || !groups.TryGetValue(gr.Group, out var m)) continue;
                for (var i = 0; i < key.Length; i++) key[i] = s.Rows.Get(r, 1 + i);
                if (!keys.Add((gr.Group, string.Join(',', key.ToArray())))) continue;
                for (var mr = m.ByKey.First(key); mr >= 0; mr = m.ByKey.Next(mr))
                {
                    members += m.Rows.Weight(mr);
                    foreach (var x in Sources(m.Cols, m.Rows, mr, m.KeyWidth)) memberSources.Add(x);
                }
            }
        }
        if (members != 0) return new(info?.Sink, info?.Rule, [.. memberSources], true, members, info?.Cols.Length ?? 0);
        if (rows != 0 && sources.Count > 0) return new(info?.Sink, info?.Rule, [.. sources], true, rows, info?.Cols.Length ?? 0);
        return info is null ? null : new(info.Sink, info.Rule, [], false, 0, info.Cols.Length);
    }

    /// <summary>The elements that some row holds up.</summary>
    public IEnumerable<int> Elements
    {
        get
        {
            var seen = new HashSet<int>();
            foreach (var s in sinks)
                if (s is not null) for (var r = 0; r < s.Rows.RowLimit; r++) if (s.Rows.Live(r) && seen.Add((int)s.Rows.Get(r, 0))) yield return (int)s.Rows.Get(r, 0);
        }
    }

    /// <summary>How much is held; it changes when lineage is added or removed.</summary>
    public long Size => sinks.Sum(s => (long)(s?.Rows.Count ?? 0)) + groups.Values.Sum(m => (long)m.Rows.Count);
}
