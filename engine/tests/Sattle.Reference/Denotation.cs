using System.Collections.Immutable;
using System.Numerics;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Reference;

/// <summary>What one evaluation of a plan sees: the integrated inputs (Def. 6.2) and the logical-time frontier.</summary>
public sealed record PlanInputs(
    IReadOnlyDictionary<string, Relation> Rows,
    IReadOnlyDictionary<string, GraphFacts> Graphs,
    IReadOnlyDictionary<string, IReadOnlySet<string>> Held,   // per graph port, the entity types the graph bound to it holds
    Func<string, string, string, bool> IsSubtype,   // (graph port, type, of)
    long? Frontier,
    IReadOnlySet<string>? WithPairs = null);        // aggregates read only by sinks: they carry the pair of a mean or a last (Law 7.5 item 3 (i))

/// <summary>The batch semantics of the operator kinds (Def. 6.3, 6.7): each a function from the relations of its in-ports to the relation of its out-port. No state, no order of arrival.</summary>
public static class Denotation
{
    public static Relation Of(TOp op, Func<string, Relation> input, PlanInputs p) => op switch
    {
        TMatch m => Match(m, p.Graphs[m.GraphPort], (t, of) => p.IsSubtype(m.GraphPort, t, of), p.Held[m.GraphPort]),
        TTraverse t => Traverse(t, input("in"), p.Graphs[t.GraphPort], (ty, of) => p.IsSubtype(t.GraphPort, ty, of)),
        TValues v => new Relation(Names(v), ZSet.Of(v.Rows.Select(r => (r.ToImmutableArray(), 1L)))),
        TFilter f => Filter(f, input("in")),
        TMap m => Map(m, input("in")),
        TUnion u => new Relation(Names(u), Side(u, input("in0")).Plus(Side(u, input("in1")))),
        TDistinct d => new Relation(Names(d), ZSet.Of(input("in").Rows.Where(r => r.Weight > 0).Select(r => (r.Row, 1L)))),
        TJoin j => Join(j, input("left"), input("right")),
        TFixpoint f => Fixpoint(f, input("in"), input("edges")),
        TWindow w => Window(w, input("in")),
        TGroup g => Group(g, g.Keys, g.Aggs, input("in"), null, pairs: p.WithPairs?.Contains(g.Name) == true),
        TRollup r => r.NoData ? RollupWithNoData(r, input("in")) : Group(r, WithWindow(r.Keys), r.Aggs, input("in"), r.PeriodNs, pairs: p.WithPairs?.Contains(r.Name) == true),
        TRetain r => Retain(r, input("in"), p.Frontier),
        TSeries s => Series(s, input("in"), p.Graphs[s.GraphPort], p.Frontier, ImmutableSortedDictionary<string, long>.Empty, [.. s.Reads.Select(_ => (long?)null)], [.. s.Reads.Select(_ => 0L)], ImmutableSortedSet<long>.Empty),
        _ => throw new Undefined($"operator kind {op.Kind}"),
    };

    static ImmutableArray<string> Names(TOp op) => [.. op.Out.Cols.Select(c => c.Name)];
    /// <summary>One side of a union, as the union's columns by name: a side may carry more (the properties read from an element), and those stay behind.</summary>
    static ZSet Side(TUnion u, Relation side) => ZSet.Of(side.Rows.Select(r => (u.Out.Cols.Select(c => r.Row[side.Index(c.Name)]).ToImmutableArray(), r.Weight)));
    static long Instant(Value v) => v switch { InstantValue i => i.Ns, IntegerValue n => (long)n.N, _ => throw new Undefined($"{v.Kind} is not an Instant") };
    static bool True(Value v) => v is BooleanValue { B: true };

    static ImmutableArray<Value> Shape(TOp op, IReadOnlyDictionary<string, Value> named) =>
        [.. op.Out.Cols.Select(c => named.TryGetValue(c.Name, out var v) ? v : throw new Undefined($"{op.Kind} {op.Name}: output column {c.Name} has no value"))];

    static Dictionary<string, Value> Named(Relation r, ImmutableArray<Value> row) =>
        r.Columns.Select((c, i) => (c, v: row[i])).ToDictionary(x => x.c, x => x.v, StringComparer.Ordinal);

    // ---- graph patterns ----

    /// <summary>The bindings of the pattern over the facts of the graph, homomorphism semantics. An edge binds only when both endpoint entities are in the graph and fit their node types; a node of a type the graph never holds is a reference and binds the end's id without an entity.</summary>
    static Relation Match(TMatch m, GraphFacts g, Func<string, string, bool> sub, IReadOnlySet<string> held)
    {
        var references = m.References(held, sub);
        IEnumerable<ImmutableDictionary<string, string>> Extend(IEnumerable<ImmutableDictionary<string, string>> partial, TEdge e, int index) =>
            from b in partial
            from r in g.Rels.Values
            where sub(r.Type, e.TypeId)
            where !b.TryGetValue(e.From, out var source) || source == r.Source
            where !b.TryGetValue(e.To, out var target) || target == r.Target
            select b.SetItem(e.From, r.Source).SetItem(e.To, r.Target).SetItem("\u0000edge" + index, r.Id);

        var bindings = m.Edges.Select((e, i) => (e, i)).Aggregate(
            (IEnumerable<ImmutableDictionary<string, string>>)[ImmutableDictionary.Create<string, string>(StringComparer.Ordinal)],
            (acc, x) => Extend(acc, x.e, x.i));
        foreach (var n in m.Nodes.Where(n => !m.Edges.Any(e => e.From == n.Var || e.To == n.Var)))
            bindings = from b in bindings from e in g.Entities.Values select b.SetItem(n.Var, e.Id);
        bindings = bindings.Where(b => m.Nodes.All(n => references.Contains(n.Var) || g.Entities.TryGetValue(b[n.Var], out var e) && sub(e.Type, n.TypeId)));

        var rows = bindings.Select(b =>
        {
            var named = new Dictionary<string, Value>(StringComparer.Ordinal);
            foreach (var n in m.Nodes) named[n.Var] = Value.Entity(b[n.Var]);
            foreach (var (e, i) in m.Edges.Select((e, i) => (e, i))) if (e.Var is not null) named[e.Var] = Value.Entity(b["\u0000edge" + i]);
            foreach (var r in m.Reads)
            {
                var edge = m.Edges.Select((e, i) => (e, i)).FirstOrDefault(x => x.e.Var == r.Var);
                var props = m.Nodes.Any(n => n.Var == r.Var) ? g.Entities[b[r.Var]].Props : g.Rels[b["\u0000edge" + edge.i]].Props;
                named[r.Col] = props.TryGetValue(r.PropId, out var v) ? v : Value.Null;
            }
            return (Shape(m, named), 1L);
        });
        return new Relation(Names(m), ZSet.Of(rows));
    }

    /// <summary>Bounded reach over the relationship facts of one type from the start element of each row: every element whose shortest distance d has min ≤ d ≤ max. A reached id without an entity in the graph is passed through and not emitted.</summary>
    static Relation Traverse(TTraverse t, Relation rows, GraphFacts g, Func<string, string, bool> sub)
    {
        IEnumerable<string> Next(string id) =>
            g.Rels.Values.Where(r => sub(r.Type, t.RelTypeId)).SelectMany(r =>
                (t.Direction is "out" or "either" && r.Source == id ? new[] { r.Target } : []).Concat(t.Direction is "in" or "either" && r.Target == id ? [r.Source] : []));

        IEnumerable<string> Reach(string start)
        {
            var seen = ImmutableSortedSet.Create(StringComparer.Ordinal, start);
            var level = seen;
            for (long d = 0; d <= t.Max && level.Count > 0; d++)
            {
                if (d >= t.Min) foreach (var id in level) yield return id;
                var next = level.SelectMany(Next).Where(id => !seen.Contains(id)).ToImmutableSortedSet(StringComparer.Ordinal);
                seen = seen.Union(next); level = next;
            }
        }

        var result =
            from r in rows.Rows
            let start = rows.Reader(r.Row)(t.Start)
            where start is EntityValue
            from reached in Reach(((EntityValue)start).Id)
            where g.Entities.ContainsKey(reached)
            let named = Named(rows, r.Row)
            select (Shape(t, With(named, [(t.Target, Value.Entity(reached)),
                .. t.Reads.Select(x => (x.Col, g.Entities[reached].Props.TryGetValue(x.PropId, out var v) ? v : Value.Null))])), r.Weight);
        return new Relation(Names(t), ZSet.Of(result));
    }

    static Dictionary<string, Value> With(Dictionary<string, Value> named, IEnumerable<(string Name, Value Value)> more)
    {
        var res = new Dictionary<string, Value>(named, StringComparer.Ordinal);
        foreach (var (n, v) in more) res[n] = v;
        return res;
    }

    // ---- pointwise ----

    static Relation Filter(TFilter f, Relation rows) =>
        new(Names(f), ZSet.Of(rows.Rows.Where(r => True(Expressions.Eval(f.Pred, rows.Reader(r.Row))))));

    /// <summary>An output column is an assignment, or a property column that an assigned element variable carries along (`s: i` carries `i.p` as `s.p`).</summary>
    static Relation Map(TMap m, Relation rows)
    {
        Value Cell(string name, Func<string, Value> read)
        {
            var direct = m.Assigns.FirstOrDefault(a => a.Name == name);
            if (direct.Value is not null) return Expressions.Eval(direct.Value, read);
            var parent = m.Assigns.FirstOrDefault(a => a.Value is TField && name.StartsWith(a.Name + ".", StringComparison.Ordinal));
            return parent.Value is TField pf ? read(pf.Name + name[parent.Name.Length..]) : throw new Undefined($"Map {m.Name}: column {name} has no source");
        }
        return new(Names(m), ZSet.Of(rows.Rows.Select(r => (m.Out.Cols.Select(c => Cell(c.Name, rows.Reader(r.Row))).ToImmutableArray(), r.Weight))));
    }

    static Relation Window(TWindow w, Relation rows) =>
        new(Names(w), ZSet.Of(rows.Rows.SelectMany(r =>
        {
            var t = Instant(rows.Reader(r.Row)(w.TimeField));
            // Def. 7.5 with a hop h: every window [s, s + w) with s on the grid of h that holds t (with h = w, the one window floor(t / w) · w)
            return Enumerable.Range(0, (int)(w.WidthNs / w.HopNs)).Select(j =>
            {
                var start = t - ((t % w.HopNs) + w.HopNs) % w.HopNs - j * w.HopNs;
                return (Shape(w, With(Named(rows, r.Row), [("windowStart", Value.Instant(start)), ("windowEnd", Value.Instant(start + w.WidthNs))])), r.Weight);
            });
        })));

    /// <summary>The equijoin with weights multiplied (Def. 6.3). A row with an absent key component joins nothing. Keys are equal by value.</summary>
    static Relation Join(TJoin j, Relation left, Relation right)
    {
        var names = Names(j);
        string? Key(IEnumerable<TExpr> exprs, Func<string, Value> read)
        {
            var vs = exprs.Select(e => Expressions.Eval(e, read)).ToImmutableArray();
            return vs.Any(v => v is NullValue) ? null : Canon.Of(vs);
        }
        var pairs =
            from l in left.Rows
            let lk = Key(j.Keys.Select(k => k.Left), left.Reader(l.Row))
            where lk is not null
            from r in right.Rows
            where Key(j.Keys.Select(k => k.Right), right.Reader(r.Row)) == lk
            let row = l.Row.AddRange(r.Row)
            select (row, l.Weight * r.Weight);
        return new Relation(names, ZSet.Of(pairs));
    }

    /// <summary>The rounds of Def. 7.3 (F) from no value until they are constant. A value is its position in the order, 0 for no value (absent or outside the
    /// order). A node's own value counts while the rows of the node with that value weigh more than nothing, an edge while its rows (from, to and image list) do;
    /// the output is a row per node with a node row of positive weight and a value.</summary>
    static Relation Fixpoint(TFixpoint f, Relation nodes, Relation edges)
    {
        var order = f.Order.ToList();
        int Pos(Value v) => order.IndexOf(v) + 1;
        var own = nodes.Rows.Select(r => (Read: nodes.Reader(r.Row), r.Weight)).Where(r => r.Read(f.Key) is not NullValue)
            .GroupBy(r => (Key: r.Read(f.Key), At: Pos(r.Read(f.Value))), r => r.Weight).Where(g => g.Sum() > 0).Select(g => g.Key).ToList();
        int Map(Value by) => by is TextValue { S: var s } && f.Maps.ContainsKey(s) ? f.Maps.Keys.ToList().IndexOf(s) : -1;
        var deps = edges.Rows.Select(r => (Read: edges.Reader(r.Row), r.Weight)).Where(r => r.Read(f.From) is not NullValue && r.Read(f.To) is not NullValue)
            .GroupBy(r => (From: r.Read(f.From), To: r.Read(f.To), Map: f.By is null ? -1 : Map(r.Read(f.By))), r => r.Weight).Where(g => g.Sum() > 0).Select(g => g.Key).ToList();
        var maps = f.Maps.Values.ToList();
        var keys = own.Select(o => o.Key).Concat(deps.Select(e => e.From)).Distinct().ToList();
        var z = keys.ToDictionary(k => k, _ => 0);
        for (var changed = true; changed;)
        {
            var next = keys.ToDictionary(k => k, k => own.Where(o => o.Key.Equals(k)).Select(o => o.At)
                .Concat(deps.Where(e => e.From.Equals(k)).Select(e => z.GetValueOrDefault(e.To) is var at && e.Map >= 0 ? maps[e.Map][at] : z.GetValueOrDefault(e.To))).DefaultIfEmpty(0).Max());
            changed = keys.Any(k => next[k] != z[k]); z = next;
        }
        var rows = own.Select(o => o.Key).Distinct().Where(k => z[k] > 0).Select(k => (ImmutableArray.Create(k, f.Order[z[k] - 1]), 1L));
        return new Relation(Names(f), ZSet.Of(rows));
    }

    // ---- per-key functions of the integrated input ----

    /// <summary>Per key, the aggregates over the rows of the key with weights as multiplicities. A key exists while its rows have a positive total weight. A row with an absent key component belongs to no key. A roll-up key includes the window, and its row carries the window end.</summary>
    static IReadOnlyList<(string Name, TExpr Value)> WithWindow(IReadOnlyList<(string Name, TExpr Value)> keys) => [.. keys, ("windowStart", new TField("windowStart", new FType(Ty.Instant, true, Role.Data)))];

    /// <summary>
    /// Def. 7.16 for a roll-up: the records of members in no data are not aggregated, and a key is in no data iff every member that has a row is.
    /// A member is a row of the input without its window and values. The key in no data has one record, at the greatest window of its members' markers, without values.
    /// </summary>
    static Relation RollupWithNoData(TRollup r, Relation rows)
    {
        bool Marked(ImmutableArray<Value> row) => True(rows.Reader(row)(TSeries.NoDataColumn));
        string? KeyOf(ImmutableArray<Value> row) => r.Keys.Select(k => Expressions.Eval(k.Value, rows.Reader(row))).ToImmutableArray() is var vs && vs.All(v => v is not NullValue) ? Canon.Of(vs) : null;
        string Member(ImmutableArray<Value> row) => Canon.Of([.. r.Members!.Select(c => rows.Reader(row)(c))]);
        var present = rows.Rows.Where(x => x.Weight > 0 && KeyOf(x.Row) is not null).ToList();
        var aggregated = Group(r, WithWindow(r.Keys), r.Aggs, rows with { Rows = ZSet.Of(rows.Rows.Where(x => !Marked(x.Row))) }, r.PeriodNs, [(TSeries.NoDataColumn, Value.Boolean(false))]);
        var silent =
            from g in present.GroupBy(x => KeyOf(x.Row)!, StringComparer.Ordinal)
            let markers = g.Where(x => Marked(x.Row)).ToList()
            let all = g.Select(x => Member(x.Row)).Distinct(StringComparer.Ordinal).Count()
            let marked = markers.Select(x => Member(x.Row)).Distinct(StringComparer.Ordinal).Count()
            where marked > 0 && marked == all
            let read = rows.Reader(markers[0].Row)
            let start = markers.Max(x => Instant(rows.Reader(x.Row)("windowStart")))       // the greatest window of its members' markers (#159 G7)
            select Shape(r, r.Keys.Select(k => (k.Name, Expressions.Eval(k.Value, read))).Concat(r.Aggs.Select(a => (a.Target, Value.Null)))
                .Concat([("windowStart", Value.Instant(start)), ("windowEnd", Value.Instant(start + r.PeriodNs)), (TSeries.NoDataColumn, Value.Boolean(true))]).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal));
        var markerRows = silent.ToList();
        var at = r.Out.Cols.Select((c, i) => (c.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.Ordinal);
        string Place(ImmutableArray<Value> row) => Canon.Of([.. r.Keys.Select(k => row[at[k.Name]]), row[at["windowStart"]]]);
        var taken = markerRows.Select(Place).ToHashSet(StringComparer.Ordinal);
        return new Relation(Names(r), ZSet.Of(aggregated.Rows.Where(x => !taken.Contains(Place(x.Row))).Concat(markerRows.Select(m => (m, 1L)))));
    }

    /// <summary>The columns that carry the pair of an aggregate, as the engine names them: sum and count of a `mean`, the order of a `last` with an order that is no text.</summary>
    public static string[] PairNames(TAgg a) => a.Fn switch { "mean" => [a.Target + "#sum", a.Target + "#count"], "last" when a.Order?.Type.Ty.Tag is not (Tag.Text or Tag.Ent) => [a.Target + "#order"], _ => [] };

    /// <summary>The pair of an aggregate over its rows: the sum and count of a mean, the order of the row a last takes.</summary>
    static IEnumerable<(string Name, Value Value)> PairOf(TAgg a, ImmutableArray<(Func<string, Value> Read, long Weight)> rows, IReadOnlyList<TAgg> aggs)
    {
        var valued = a.Value is null ? [] : rows.Select(r => (Value: Expressions.Eval(a.Value, r.Read), r.Read, r.Weight)).Where(r => r.Value is not NullValue).ToImmutableArray();
        static decimal Number(Value v) => v is InstantValue t ? t.Ns : Canon.Number(v);
        if (a.Fn == "mean")
        {
            var n = valued.Sum(r => r.Weight);
            yield return (a.Target + "#sum", n == 0 ? Value.Null : ExactSum(valued.Select(r => (r.Value, r.Weight))) is { } sum ? Value.Decimal(sum) : Value.Null);
            yield return (a.Target + "#count", n == 0 ? Value.Null : Value.Integer(n));
        }
        else if (PairNames(a).Length > 0)
        {
            var row = RowOf(a.Row!, aggs, rows);
            yield return (a.Target + "#order", row is null || Expressions.Eval(a.Value!, row) is NullValue ? Value.Null : Value.Decimal(Number(Expressions.Eval(a.Order!, row))));
        }
    }

    static Relation Group(TOp op, IReadOnlyList<(string Name, TExpr Value)> keys, IReadOnlyList<TAgg> aggs, Relation rows, long? period, IReadOnlyList<(string Name, Value Value)>? more = null, bool pairs = false)
    {
        var extra = pairs ? aggs.SelectMany(PairNames).ToArray() : [];
        var members =
            from r in rows.Rows
            let read = rows.Reader(r.Row)
            let key = keys.Select(k => (k.Name, Value: Expressions.Eval(k.Value, read))).ToImmutableArray()
            where key.All(k => k.Value is not NullValue)
            group (Read: read, r.Weight, Key: key) by Canon.Of([.. key.Select(k => k.Value)]);
        var result =
            from g in members
            where g.Sum(x => x.Weight) > 0
            let named = g.First().Key.Concat(aggs.Select(a => (a.Target, Aggregate(a, [.. g.Select(x => (x.Read, x.Weight))], aggs)))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal)
            let pairValues = extra.Length == 0 ? [] : aggs.SelectMany(a => PairOf(a, [.. g.Select(x => (x.Read, x.Weight))], aggs)).ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal)
            select (Shape(op, With(period is { } w ? With(named, [("windowEnd", Value.Instant(Instant(named["windowStart"]) + w))]) : named, more ?? [])).AddRange(extra.Select(n => pairValues[n])), 1L);
        return new Relation([.. Names(op), .. extra], ZSet.Of(result));
    }

    /// <summary>The input row that the `last` aggregates of one row mark take their values from. Among the rows that have an order (taken once per distinct order and member values, with positive weight): the greatest order, and on a tie the greatest values in the order of the members, where no value is below every value. Null when no row has an order.</summary>
    static Func<string, Value>? RowOf(string mark, IReadOnlyList<TAgg> aggs, ImmutableArray<(Func<string, Value> Read, long Weight)> rows)
    {
        var members = aggs.Where(x => x.Row == mark).ToList();
        var entries = rows.Select(r => (Order: Expressions.Eval(members[0].Order!, r.Read), Values: members.Select(m => Expressions.Eval(m.Value!, r.Read)).ToImmutableArray(), r.Read, r.Weight))
            .Where(r => r.Order is not NullValue).GroupBy(r => Canon.Of([r.Order, .. r.Values])).Where(g => g.Sum(r => r.Weight) > 0).Select(g => g.First()).ToArray();
        if (entries.Length == 0) return null;
        int Compare((Value Order, ImmutableArray<Value> Values, Func<string, Value> Read, long Weight) x, (Value Order, ImmutableArray<Value> Values, Func<string, Value> Read, long Weight) y)
        {
            var c = Canon.Compare(x.Order, y.Order);
            for (var i = 0; c == 0 && i < members.Count; i++)
                c = x.Values[i] is NullValue || y.Values[i] is NullValue ? (x.Values[i] is NullValue ? 0 : 1) - (y.Values[i] is NullValue ? 0 : 1) : Canon.Compare(x.Values[i], y.Values[i]);
            return c;
        }
        return entries.Aggregate((x, y) => Compare(y, x) > 0 ? y : x).Read;
    }

    /// <summary>#159 G4: the sum of the rows' numbers, exact, as a decimal when one decimal is that number exactly, else null.</summary>
    static decimal? ExactSum(IEnumerable<(Value Value, long Weight)> rows)
    {
        static BigInteger Scaled(decimal d)
        {
            var bits = decimal.GetBits(d);
            var m = (new BigInteger((uint)bits[2]) << 64) + (new BigInteger((uint)bits[1]) << 32) + (uint)bits[0];
            return (d < 0 ? -m : m) * BigInteger.Pow(10, 28 - (bits[3] >> 16 & 0xFF));
        }
        var sum = rows.Aggregate(BigInteger.Zero, (s, r) => s + r.Weight * Scaled(Canon.Number(r.Value)));
        var digits = BigInteger.Abs(sum).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(29, '0');
        var text = ((sum < 0 ? "-" : "") + digits[..^28] + "." + digits[^28..]).TrimEnd('0').TrimEnd('.');
        return decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) && Scaled(d) == sum ? d : null;
    }

    public static Value Aggregate(TAgg a, ImmutableArray<(Func<string, Value> Read, long Weight)> rows, IReadOnlyList<TAgg>? aggs = null)
    {
        var valued = a.Value is null ? [] : rows.Select(r => (Value: Expressions.Eval(a.Value, r.Read), r.Read, r.Weight)).Where(r => r.Value is not NullValue).ToImmutableArray();
        IEnumerable<Value> Present() => valued.GroupBy(r => Canon.Of(r.Value)).Where(g => g.Sum(r => r.Weight) > 0).Select(g => g.First().Value);
        static Value? Number(Value? v) => v is DecimalValue d ? Value.Decimal(decimal.Parse(Norm.Decimal(d.Q), System.Globalization.CultureInfo.InvariantCulture)) : v;
        var decimalOut = a.Out.Ty.Tag == Tag.Decimal;
        switch (a.Fn)
        {
            case "count": return Value.Integer(rows.Sum(r => r.Weight));
            case "countDistinct": return Value.Integer(Present().Count());
            case "sum":
                {
                    // #159 G4: the exact sum, no value when the output type does not hold it
                    if (decimalOut) return ExactSum(valued.Select(r => (r.Value, r.Weight))) is { } d ? Value.Decimal(d) : Value.Null;
                    var n = valued.Aggregate(BigInteger.Zero, (s, r) => s + r.Weight * Canon.Integer(r.Value));
                    return n < long.MinValue || n > long.MaxValue ? Value.Null : Value.Integer(n);
                }
            case "percentile":
                {
                    // nearest rank: per distinct number its net weight, counted when positive; the r-th smallest, r = max(1, ceil(p n / 100))
                    var counted = valued.GroupBy(r => Canon.Of(r.Value)).Select(g => (Number: Canon.Number(g.First().Value), Weight: g.Sum(r => r.Weight))).Where(x => x.Weight > 0).OrderBy(x => x.Number).ToArray();
                    var n = counted.Sum(x => x.Weight);
                    if (n == 0) return Value.Null;
                    var rank = Math.Max(1m, Math.Ceiling(a.Percent!.Value * n / 100m));
                    var at = counted.First(x => (rank -= x.Weight) <= 0).Number;
                    return decimalOut ? Value.Decimal(decimal.Parse(Norm.Decimal(at), System.Globalization.CultureInfo.InvariantCulture)) : Value.Integer(new BigInteger(at));
                }
            // a decimal extreme is its number, whatever digits came first (#161)
            case "min": return Number(Present().OrderBy(v => v, Comparer<Value>.Create(Canon.Compare)).FirstOrDefault()) ?? Value.Null;
            case "max": return Number(Present().OrderByDescending(v => v, Comparer<Value>.Create(Canon.Compare)).FirstOrDefault()) ?? Value.Null;
            case "mean":
                {
                    var n = valued.Sum(r => r.Weight);
                    return n == 0 || ExactSum(valued.Select(r => (r.Value, r.Weight))) is not { } sum ? Value.Null : Value.Decimal(sum / n);
                }
            case "last":
                {
                    // the value of the one row the mark's aggregates share
                    var value = RowOf(a.Row!, aggs!, rows) is { } row ? Expressions.Eval(a.Value!, row) : Value.Null;
                    return value is NullValue || !decimalOut ? value : Value.Decimal(Canon.Number(value));
                }
            default: throw new Undefined($"aggregate {a.Fn}");
        }
    }

    /// <summary>Proposition 5.9: the rows with time + age beyond the frontier.</summary>
    static Relation Retain(TRetain r, Relation rows, long? frontier) =>
        new(Names(r), ZSet.Of(rows.Rows.Where(x => rows.Reader(x.Row)(r.TimeField) is InstantValue t && (frontier is null || t.Ns + r.AgeNs > frontier))));

    // ---- windows ----

    /// <summary>
    /// Def. 6.7: each row with the closed windows of its element in [frontier − within, frontier) that hold every property read.
    /// Def. 7.15: a read with a fill also holds its fill value in every window of the look-back that is covered for the element (it starts at or after `since` of the element, ends at or before `covered` of the read and contains no time of `lost`) and has no stored value.
    /// Def. 7.16: with a time to live, an element for which the later of its `since` and the end of its newest record lies more than the time to live before the frontier has one more record, at the grid window that holds that later time plus the time to live, marked no data and without values.
    /// </summary>
    public static Relation Series(TSeries s, Relation rows, GraphFacts g, long? frontier, IReadOnlyDictionary<string, long> since, IReadOnlyList<long?> covered, IReadOnlyList<long> lags, IReadOnlySet<long> lost)
    {
        long Floor(long t) => t - ((t % s.PeriodNs) + s.PeriodNs) % s.PeriodNs;
        var result = new List<(ImmutableArray<Value>, long)>();
        foreach (var r in rows.Rows)
        {
            if (rows.Reader(r.Row)(s.TargetVar) is not EntityValue element) continue;
            var id = element.Id;
            long? started = since.TryGetValue(id, out var from) ? from : null;
            decimal? At(int i, long start) =>
                g.Windows.TryGetValue(new WindowId(id, s.Reads[i].PropId, start), out var stored) ? stored
                : s.Reads[i].Fill is { } fill && started is { } k && start >= k && frontier is { } f && start + s.PeriodNs <= f && covered[i] is { } c && start + s.PeriodNs + lags[i] <= c
                    && !lost.Any(t => t >= start && t < start + s.PeriodNs + lags[i]) ? fill       // a window ends `Lag` after its grid window (a hop window); one in which the source lost events is not covered
                : null;
            IEnumerable<long> Grid(long lo, long hi) { for (var a = Floor(lo + s.PeriodNs - 1); a + s.PeriodNs <= hi; a += s.PeriodNs) yield return a; }
            var fillable = frontier is { } now && started is { } first && s.Reads.Any(x => x.Fill is not null) ? Grid(Math.Max(now - s.WithinNs, first), now) : [];
            var records = g.Windows.Keys.Where(k => k.Element == id).Select(k => k.Start).Concat(fillable).Distinct()
                .Where(start => frontier is null || start >= frontier - s.WithinNs).Order()
                .Select(start => (Start: start, Values: s.Reads.Select((_, i) => At(i, start)).ToArray())).Where(x => x.Values.All(v => v is not null)).ToList();
            IEnumerable<(string, Value)> Marker(bool noData) => s.TtlNs is null ? [] : [(TSeries.NoDataColumn, Value.Boolean(noData))];
            foreach (var rec in records)
                result.Add((Shape(s, With(Named(rows, r.Row), [("windowStart", Value.Instant(rec.Start)), ("windowEnd", Value.Instant(rec.Start + s.PeriodNs)),
                    .. s.Reads.Select((x, i) => (x.OutName, x.ValueKind == "Decimal" ? Value.Decimal(rec.Values[i]!.Value) : Value.Integer((BigInteger)rec.Values[i]!.Value))), .. Marker(false)])), r.Weight));
            // e(k): the later of `since` and the end of the newest record (Def. 7.16): an element that returns has existed again only since it returned, whatever old record of it is still in the look-back
            long? newest = records.Count > 0 ? Math.Max(records[^1].Start + s.PeriodNs + lags.DefaultIfEmpty().Max(), started ?? long.MinValue) : started;      // a hop window ends `lag` after its grid window (#111)
            if (s.TtlNs is { } ttl && frontier is { } at && newest is { } e && at - e > ttl)
            {
                var start = Floor(e + ttl);       // the window in which the silence reached the time to live, not one made from the clock (R8, #159 G7)
                result.Add((Shape(s, With(Named(rows, r.Row), [("windowStart", Value.Instant(start)), ("windowEnd", Value.Instant(start + s.PeriodNs)), .. s.Reads.Select(x => (x.OutName, Value.Null)), .. Marker(true)])), r.Weight));
            }
        }
        return new Relation(Names(s), ZSet.Of(result));
    }

    // ---- the keyed fold over ordered windows, seeded at the start of its look-back ----

    /// <summary>What a fold keeps from one input to the next (Def. 6.7). The present policy rows by their canonical text, with their key, their policy `ζ` and
    /// their stamp (Def. 6.6: 0 before the clock first moves, else the clock they entered at), and the rows that left during the present clock with theirs. The
    /// window rows of the input before (`Before`, none before the first input). The first window in the look-back at the clock before, by its start (`First`,
    /// none before the first input with a clock). Per key that is not dropped its seed, the state after its windows below `First`, with the key's values (`Seeds`;
    /// a key without one has the seed none). The transition rows of the dropped windows (`Kept`), which stay.</summary>
    public sealed record Carried(
        ImmutableSortedDictionary<string, (string? Key, ImmutableArray<Value> Policy, long Since)> Policies,
        ImmutableSortedDictionary<string, (string? Key, ImmutableArray<Value> Policy, long Since)> Left,
        Relation? Before,
        Int128? First,
        ImmutableSortedDictionary<string, (ImmutableArray<Value> Values, ImmutableSortedDictionary<string, Value> State)> Seeds,
        ZSet Kept)
    {
        public static readonly Carried None = new(ImmutableSortedDictionary.Create<string, (string?, ImmutableArray<Value>, long)>(StringComparer.Ordinal),
            ImmutableSortedDictionary.Create<string, (string?, ImmutableArray<Value>, long)>(StringComparer.Ordinal), null, null,
            ImmutableSortedDictionary.Create<string, (ImmutableArray<Value>, ImmutableSortedDictionary<string, Value>)>(StringComparer.Ordinal), ZSet.Empty);
    }

    /// <summary>
    /// Def. 6.7 on the windows of Def. 6.4, written from the definition, then selected at the clock (Def. 6.2, s = close, u = ∞). A window is named by its start
    /// `a` on the grid of `hop`; it ends at a + `width`, closes at close(a) = a + `width` + λ (`lateness`) and leaves the look-back at leave(a) = a + `lookback`,
    /// with `lookback` at least `width` + λ (#172 D1). first(τ) is the least start a with leave(a) > τ.
    /// The observation of a key at a window is read through the observe aggregates over the distinct present values of its rows (D1); the no-data marker rows of a
    /// series are no rows of a window (D3); a window without a row of the key is silent. The policy of a key at window a is that of its present policy rows with
    /// the greatest `since` below close(a), when they hold one value (D4); rows are told apart as whole rows.
    /// The states of a key from a seed at a window: the seed none waits for the first window from there that is not silent and has a policy, and starts there from
    /// start(ζ); from a state, every window, silent ones too, steps with its policy, and a window without one keeps the state. At each input with a clock, the
    /// windows the clock dropped (from the first window before to the new one) are stepped from the seeds with the rows and policies of the input before (of this
    /// input, if the fold has none before it), their transitions are kept, and the state after them is the key's new seed; then a key with no policy row and no window row in the
    /// look-back is dropped. The result is the kept rows and the transitions of the windows in the look-back, stepped from the seeds with this input's rows.
    /// </summary>
    public static (Relation Result, Carried Carried) Fold(TFold t, Relation rows, Relation policy, long width, long hop, long lateness, long lookback, long? before, long? frontier, Carried carried)
    {
        Int128 Close(long a) => (Int128)a + width + lateness;
        Int128 Floor(Int128 x) => x - ((x % hop) + hop) % hop;
        static string? KeyOf(IEnumerable<Value> vs) => vs.ToImmutableArray() is var k && k.All(v => v is not NullValue) ? Canon.Of(k) : null;

        // Def. 6.6: a policy row keeps its stamp while it has a row at every clock end; one that is gone at a clock end and comes back is stamped anew. This
        // input's changes are at the clock after its advance: a row it adds is stamped with that clock (0 before the clock first moves), a row that left during
        // the clock before is gone at that clock's end
        var advanced = frontier is { } f && (before is not { } b0 || f > b0);
        var gone = advanced ? carried.Left.Clear() : carried.Left;
        var policies = policy.Rows.Where(r => r.Weight > 0).ToImmutableSortedDictionary(r => Canon.Of(r.Row),
            (string? Key, ImmutableArray<Value> Policy, long Since) (r) => carried.Policies.TryGetValue(Canon.Of(r.Row), out var had) || gone.TryGetValue(Canon.Of(r.Row), out had) ? had
                : (KeyOf(t.Keys.Select(k => policy.Reader(r.Row)(k.Name))), t.Policy.Select(c => policy.Reader(r.Row)(c.Name)).ToImmutableArray(), frontier ?? 0), StringComparer.Ordinal);
        var left = gone.SetItems(carried.Policies.Where(x => !policies.ContainsKey(x.Key))).RemoveRange(policies.Keys);

        bool Marker(Relation r, ImmutableArray<Value> row) => t.NoData && True(r.Reader(row)(TSeries.NoDataColumn));
        ImmutableDictionary<(string Key, long Start), ImmutableArray<(Func<string, Value> Read, ImmutableArray<Value> Values)>> Windows(Relation r) =>
            r.Rows.Where(x => x.Weight > 0 && !Marker(r, x.Row))
                .Select(x => (Read: r.Reader(x.Row), Values: t.Keys.Select(k => Expressions.Eval(k.Value, r.Reader(x.Row))).ToImmutableArray()))
                .Where(x => KeyOf(x.Values) is not null)
                .GroupBy(x => (Key: KeyOf(x.Values)!, Start: Instant(x.Read("windowStart"))))
                .ToImmutableDictionary(g => g.Key, g => g.ToImmutableArray());

        Dictionary<string, Value> Named(ImmutableArray<Value> zeta, IEnumerable<KeyValuePair<string, Value>> more) =>
            t.Policy.Select((c, i) => KeyValuePair.Create(c.Name, zeta[i])).Concat(more).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        ImmutableSortedDictionary<string, Value> Start(ImmutableArray<Value> zeta) =>
            t.States.ToImmutableSortedDictionary(s => s.Name, s => Expressions.Eval(s.Start, n => Named(zeta, [])[n]), StringComparer.Ordinal);
        ImmutableSortedDictionary<string, Value> Next(ImmutableArray<Value> zeta, ImmutableSortedDictionary<string, Value> state, ImmutableArray<(string Name, Value Value)> obs)
        {
            var named = Named(zeta, state.Concat(obs.Select(o => KeyValuePair.Create(o.Name, o.Value))));
            return t.States.ToImmutableSortedDictionary(s => s.Name, s => Expressions.Eval(s.Next, n => named[n]), StringComparer.Ordinal);
        }
        ImmutableArray<Value> Transition(ImmutableArray<Value> key, long a, Value from, Value to, ImmutableArray<(string Name, Value Value)> obs, (ImmutableArray<Value> Policy, long Since) zeta) =>
            Shape(t, t.Keys.Select((k, i) => (k.Name, key[i]))
                .Concat([("windowStart", Value.Instant(a)), ("windowEnd", Value.Instant(a + width)), ("from", from), ("to", to)])
                .Concat(obs).Concat(t.Policy.Select((c, i) => (c.Name, zeta.Policy[i])))
                .Append((TFold.Since, Value.Instant(zeta.Since))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal));

        // The states of `key` from `seed` at the window `from` up to the window `to` over the windows `ws` and policies `pols` (Def. 6.7): their moves as
        // transition rows, and the state after `to`, none if the seed is none and no window started the key.
        (ZSet Moves, ImmutableSortedDictionary<string, Value>? State) Steps(
            ImmutableDictionary<(string Key, long Start), ImmutableArray<(Func<string, Value> Read, ImmutableArray<Value> Values)>> ws,
            ImmutableSortedDictionary<string, (string? Key, ImmutableArray<Value> Policy, long Since)> pols,
            string key, ImmutableArray<Value> values, ImmutableSortedDictionary<string, Value>? seed, Int128 from, Int128 to)
        {
            (ImmutableArray<Value> Policy, long Since)? InForce(long a)
            {
                var held = pols.Values.Where(x => x.Key == key && x.Since < Close(a)).ToList();
                if (held.Count == 0) return null;
                var since = held.Max(x => x.Since);
                var zetas = held.Where(x => x.Since == since).GroupBy(x => Canon.Of(x.Policy), StringComparer.Ordinal).ToList();
                return zetas.Count == 1 ? (zetas[0].First().Policy, since) : null;      // two policies at one greatest `since`: none
            }
            ImmutableArray<(string Name, Value Value)> Observation(long a)
            {
                var present = ws.GetValueOrDefault((key, a), []).Select(x => (x.Read, 1L)).ToImmutableArray();
                return [.. t.Observe.Select(o => (o.Target, Aggregate(o.Fn == "count" ? o with { Fn = "countDistinct" } : o, present)))];       // D1: distinct present values
            }

            var moves = ZSet.Empty;
            var state = seed;
            var a = from;
            if (state is null)
            {
                // the seed none: the least window from `from` that is not silent and has a policy starts the key from start(ζ)
                if (ws.Keys.Where(w => w.Key == key && w.Start >= from && w.Start <= to && InForce(w.Start) is not null).Select(w => (long?)w.Start).Min() is not { } nk)
                    return (moves, null);
                var zeta = InForce(nk)!.Value;
                var obs = Observation(nk);
                var start = Start(zeta.Policy);
                var after = Next(zeta.Policy, start, obs);
                moves = moves.Add(Transition(values, nk, start[t.Show], after[t.Show], obs, zeta), 1);
                state = after;
                a = (Int128)nk + hop;
            }
            for (; a <= to; a += hop)
            {
                var w = (long)a;
                var silent = !ws.ContainsKey((key, w));
                var obs = Observation(w);
                var shown = InForce(w);
                var after = shown is { } z ? Next(z.Policy, state, obs) : state;       // a window without a policy keeps the state
                if (shown is { } p && Canon.Of(after[t.Show]) != Canon.Of(state[t.Show]))
                    moves = moves.Add(Transition(values, w, state[t.Show], after[t.Show], obs, p), 1);
                var still = silent && after.All(f => Canon.Of(f.Value) == Canon.Of(state[f.Key]));
                state = after;
                if (!still) continue;
                // a silent window that keeps the state keeps it until a window is not silent or the policy in force can change (rest(ζ) of Def. 6.5)
                var change = ws.Keys.Where(x => x.Key == key && x.Start > w).Select(x => (Int128)x.Start)
                    .Concat(pols.Values.Where(x => x.Key == key && x.Since >= Close(w)).Select(x => Floor((Int128)x.Since - width - lateness) + hop))
                    .Append(to + hop).Min();
                a = change - hop;
            }
            return (moves, state);
        }

        var windows = Windows(rows);
        var seeds = carried.Seeds;
        var kept = carried.Kept;
        if (frontier is not { } now) return (new Relation(Names(t), ZSet.Empty), carried with { Policies = policies, Left = left, Before = rows });
        var first = Floor((Int128)now - lookback) + hop;       // first(τ): the least start a with leave(a) = a + lookback > τ
        if (carried.First is not { } firstBefore || first > firstBefore)
        {
            // the windows from the first window before up to the new one are dropped: stepped from the seeds with what the input before read (this input, if none)
            var (ws, pols) = carried.Before is { } b ? (Windows(b), carried.Policies) : (windows, policies);
            var from = carried.First ?? ws.Keys.Select(w => (Int128)w.Start).DefaultIfEmpty(first).Min();
            var stepped = ImmutableSortedDictionary.CreateBuilder<string, (ImmutableArray<Value> Values, ImmutableSortedDictionary<string, Value> State)>(StringComparer.Ordinal);
            foreach (var key in ws.Keys.Where(w => w.Start < first).Select(w => w.Key).Concat(seeds.Keys).Distinct(StringComparer.Ordinal).ToList())
            {
                var had = seeds.TryGetValue(key, out var s) ? s : ((ImmutableArray<Value> Values, ImmutableSortedDictionary<string, Value>? State)?)null;
                var values = had?.Values ?? ws.First(w => w.Key.Key == key).Value[0].Values;
                var (moves, state) = Steps(ws, pols, key, values, had?.State, from, first - hop);
                kept = kept.Plus(moves);
                if (state is not null) stepped[key] = (values, state);
            }
            seeds = stepped.ToImmutable();
        }
        // Def. 6.7: a key with no policy row and no window row in the look-back is dropped, and holds nothing
        seeds = seeds.RemoveRange(seeds.Keys.Where(k => !policies.Values.Any(p => p.Key == k) && !windows.Keys.Any(w => w.Key == k && w.Start >= first)).ToList());

        var last = Floor((Int128)now - width - lateness);       // the newest window closed: close(a) ≤ now
        var selected = kept;
        foreach (var key in windows.Keys.Where(w => w.Start >= first).Select(w => w.Key).Concat(seeds.Keys).Distinct(StringComparer.Ordinal).ToList())
        {
            var had = seeds.TryGetValue(key, out var s) ? s : ((ImmutableArray<Value> Values, ImmutableSortedDictionary<string, Value>? State)?)null;
            var values = had?.Values ?? windows.First(w => w.Key.Key == key && w.Key.Start >= first).Value[0].Values;
            selected = selected.Plus(Steps(windows, policies, key, values, had?.State, first, last).Moves);
        }
        return (new Relation(Names(t), selected), new Carried(policies, left, rows, first, seeds, kept));
    }
}
