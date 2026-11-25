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
        bool Marked(ImmutableArray<Value> row) => True(rows.Reader(row)(TThreshold.NoDataColumn));
        string? KeyOf(ImmutableArray<Value> row) => r.Keys.Select(k => Expressions.Eval(k.Value, rows.Reader(row))).ToImmutableArray() is var vs && vs.All(v => v is not NullValue) ? Canon.Of(vs) : null;
        string Member(ImmutableArray<Value> row) => Canon.Of([.. r.Members!.Select(c => rows.Reader(row)(c))]);
        var present = rows.Rows.Where(x => x.Weight > 0 && KeyOf(x.Row) is not null).ToList();
        var aggregated = Group(r, WithWindow(r.Keys), r.Aggs, rows with { Rows = ZSet.Of(rows.Rows.Where(x => !Marked(x.Row))) }, r.PeriodNs, [(TThreshold.NoDataColumn, Value.Boolean(false))]);
        var silent =
            from g in present.GroupBy(x => KeyOf(x.Row)!, StringComparer.Ordinal)
            let markers = g.Where(x => Marked(x.Row)).ToList()
            let all = g.Select(x => Member(x.Row)).Distinct(StringComparer.Ordinal).Count()
            let marked = markers.Select(x => Member(x.Row)).Distinct(StringComparer.Ordinal).Count()
            where marked > 0 && marked == all
            let read = rows.Reader(markers[0].Row)
            let start = markers.Max(x => Instant(rows.Reader(x.Row)("windowStart")))       // the greatest window of its members' markers (#159 G7)
            select Shape(r, r.Keys.Select(k => (k.Name, Expressions.Eval(k.Value, read))).Concat(r.Aggs.Select(a => (a.Target, Value.Null)))
                .Concat([("windowStart", Value.Instant(start)), ("windowEnd", Value.Instant(start + r.PeriodNs)), (TThreshold.NoDataColumn, Value.Boolean(true))]).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal));
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

    // ---- telemetry ----

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
                g.Telemetry.TryGetValue(new WindowId(id, s.Reads[i].PropId, start), out var stored) ? stored
                : s.Reads[i].Fill is { } fill && started is { } k && start >= k && frontier is { } f && start + s.PeriodNs <= f && covered[i] is { } c && start + s.PeriodNs + lags[i] <= c
                    && !lost.Any(t => t >= start && t < start + s.PeriodNs + lags[i]) ? fill       // a window ends `Lag` after its grid window (a hop window); one in which the source lost events is not covered
                : null;
            IEnumerable<long> Grid(long lo, long hi) { for (var a = Floor(lo + s.PeriodNs - 1); a + s.PeriodNs <= hi; a += s.PeriodNs) yield return a; }
            var fillable = frontier is { } now && started is { } first && s.Reads.Any(x => x.Fill is not null) ? Grid(Math.Max(now - s.WithinNs, first), now) : [];
            var records = g.Telemetry.Keys.Where(k => k.Element == id).Select(k => k.Start).Concat(fillable).Distinct()
                .Where(start => frontier is null || start >= frontier - s.WithinNs).Order()
                .Select(start => (Start: start, Values: s.Reads.Select((_, i) => At(i, start)).ToArray())).Where(x => x.Values.All(v => v is not null)).ToList();
            IEnumerable<(string, Value)> Marker(bool noData) => s.TtlNs is null ? [] : [(TThreshold.NoDataColumn, Value.Boolean(noData))];
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

    /// <summary>The state machine of one key after the newest of its windows that left the look-back (Def. 7.10, deviation D33): its state, the length of the current run, and the start of that window.</summary>
    public sealed record Carried(string State, long Run, long LastStart);

    /// <summary>
    /// Def. 7.10 as ruled (D33). The windows of a key form episodes: runs in which each window starts less than `within` after the one before. The state of a key is the state machine folded over its current episode, started in `initial`.
    /// A key has a row while it has a window in the look-back (`current`). Windows that left the look-back with this input (`previous` holds them, `edge` is the new lower bound) are folded into the carried state with the values they had then.
    /// The carried state of a key without a window in the look-back is kept while a late window could still continue its episode: until the frontier reaches the start of its last window plus `within` plus the late horizon (`keep`).
    /// </summary>
    public static (Relation Result, ImmutableSortedDictionary<string, Carried> Carried) Threshold(TThreshold t, Relation previous, Relation current, long? edge, long? within, long? frontier, long keep, ImmutableSortedDictionary<string, Carried> carried)
    {
        string? Key(Relation rel, ImmutableArray<Value> row)
        {
            var vs = t.Keys.Select(k => Expressions.Eval(k.Value, rel.Reader(row))).ToImmutableArray();
            return vs.Any(v => v is NullValue) || Expressions.Eval(t.Value, rel.Reader(row)) is NullValue ? null : Canon.Of(vs);
        }
        decimal Parameter(TExpr? by, decimal constant, Func<string, Value> read) => by is not null && Expressions.Eval(by, read) is var v && v is not NullValue ? Canon.Number(v) : constant;
        // parameters given by the row are not checked when the definitions are: a row whose parameters are no state machine (`above` not greater than `below`, a run of less than one window) is no window of the key
        bool IsMachine(Func<string, Value> read) =>
            Parameter(t.AboveBy, t.Above, read) > Parameter(t.BelowBy, t.Below, read) && (long)Parameter(t.EnterBy, t.EnterAfter, read) >= 1 && (long)Parameter(t.ExitBy, t.ExitAfter, read) >= 1;

        Carried Step(Carried? before, Func<string, Value> read)
        {
            var start = Instant(read("windowStart"));
            var (state, run) = before is null || within is { } w && start - before.LastStart >= w ? (t.Initial, 0L) : (before.State, before.Run);     // a gap of `within` or more starts a new episode
            var v = Canon.Number(Expressions.Eval(t.Value, read));
            var low = state == t.Low;
            var extends = low ? v >= Parameter(t.AboveBy, t.Above, read) : v <= Parameter(t.BelowBy, t.Below, read);
            run = extends ? run + 1 : 0;
            var needed = low ? (long)Parameter(t.EnterBy, t.EnterAfter, read) : (long)Parameter(t.ExitBy, t.ExitAfter, read);
            return extends && run >= needed ? new Carried(low ? t.High : t.Low, 0, start) : new Carried(state, run, start);
        }

        bool Silent(Relation rel, ImmutableArray<Value> row) => t.ReadsNoData && True(rel.Reader(row)(TThreshold.NoDataColumn));      // the record of a key in no data: no window of the episode
        ILookup<string, ImmutableArray<Value>> Windows(Relation rel, Func<long, bool> keep) =>
            rel.Rows.Where(r => r.Weight > 0 && !Silent(rel, r.Row) && Key(rel, r.Row) is not null && IsMachine(rel.Reader(r.Row)) && keep(Instant(rel.Reader(r.Row)("windowStart"))))
                .SelectMany(r => Enumerable.Repeat(r, (int)r.Weight)).ToLookup(r => Key(rel, r.Row)!, r => r.Row, StringComparer.Ordinal);
        Carried? Fold(Carried? from, Relation rel, IEnumerable<ImmutableArray<Value>> rows)
        {
            // #159 G2: a window that two rows hold has no reading, as a gap has none
            var byWindow = rows.GroupBy(row => Instant(rel.Reader(row)("windowStart"))).Where(w => w.Count() == 1).OrderBy(w => w.Key).ToArray();
            return byWindow.Aggregate(from, (c, w) => Step(c, rel.Reader(w.Single())));
        }

        var live = Windows(current, _ => true);
        var left = Windows(previous, start => edge is { } e && start < e);
        var next = ImmutableSortedDictionary.CreateBuilder<string, Carried>(StringComparer.Ordinal);
        var rows = new List<(ImmutableArray<Value>, long)>();
        foreach (var key in carried.Keys.Concat(left.Select(k => k.Key)).Concat(live.Select(k => k.Key)).Distinct(StringComparer.Ordinal))
        {
            var afterLeft = Fold(carried.GetValueOrDefault(key), previous, left[key]);
            var isLive = live.Contains(key);
            if (afterLeft is not null && (isLive || frontier is null || frontier - afterLeft.LastStart < keep)) next[key] = afterLeft;
            if (!isLive || !live[key].GroupBy(row => Instant(current.Reader(row)("windowStart"))).Any(w => w.Count() == 1)) continue;       // a key whose windows are all shared has no reading (G2)
            var first = live[key].First();
            rows.Add((Shape(t, t.Keys.Select(x => (x.Name, Expressions.Eval(x.Value, current.Reader(first)))).Append((t.Target, Value.Text(Fold(afterLeft, current, live[key])!.State)))
                .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal)), 1L));
        }
        if (t.NoData is not { } noData) return (new Relation(Names(t), ZSet.Of(rows)), next.ToImmutable());
        // Def. 7.16: a key in no data gives the declared value. It is an output over the state machine, which goes on as the episode rule says
        var silent = current.Rows.Where(r => r.Weight > 0 && Silent(current, r.Row))
            .Select(r => t.Keys.Select(x => (x.Name, Value: Expressions.Eval(x.Value, current.Reader(r.Row)))).ToList()).Where(k => k.All(x => x.Value is not NullValue))
            .GroupBy(k => Canon.Of([.. k.Select(x => x.Value)]), StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.First(), StringComparer.Ordinal);
        var at = t.Keys.Select(k => Names(t).IndexOf(k.Name)).ToArray();
        var spoken = rows.Where(r => !silent.ContainsKey(Canon.Of([.. at.Select(i => r.Item1[i])])));
        var overlay = silent.Values.Select(k => (Shape(t, k.Append((t.Target, (Value)Value.Text(noData))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal)), 1L));
        return (new Relation(Names(t), ZSet.Of(spoken.Concat(overlay))), next.ToImmutable());
    }
}
