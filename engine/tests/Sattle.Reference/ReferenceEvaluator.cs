using System.Collections.Immutable;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Reference;

/// <summary>One recorded input (Def. 6.1): a row of a source with its weight, or a logical-time control.</summary>
public abstract record Input;
public sealed record RowInput(string Source, ImmutableSortedDictionary<string, Value> Fields, long Weight) : Input;
public sealed record AdvanceInput(long Frontier) : Input;
/// <summary>The progress of a source (Def. 10.9): it has reported up to `Until`, the capture time of an envelope less the allowed lateness.</summary>
public sealed record ProgressInput(string Source, long Until) : Input;
/// <summary>A source lost events at a time (Def. 10.4): the window that contains the time carries a loss mark and is not covered (Def. 7.14).</summary>
public sealed record LossInput(long Time) : Input;

/// <summary>`WindowRetentionNs`: the declared retention of the series of the space (Def. 7.13).</summary>
/// <summary>`LateHorizonWindows` null: no late horizon.</summary>
public sealed record Settings(string Tenant = "lab", int? LateHorizonWindows = 3, long WindowRetentionNs = 48L * 3_600_000_000_000)
{
    /// <summary>The late horizon of a window of this width as a time, the greatest time when there is none.</summary>
    public System.Int128 Horizon(long width) => LateHorizonWindows is { } n ? (System.Int128)width * n : long.MaxValue;
}

/// <summary>Every graph of a space after a prefix of the input log.</summary>
public sealed record SpaceFacts(ImmutableSortedDictionary<string, GraphFacts> Graphs, long? Frontier);

/// <summary>
/// The meaning of a graph space on an input log, computed from scratch (Def. 6.4): after each input every graph is evaluated in batch from the integrated inputs.
/// Three things are functions of the log and not of the integrated state, and are carried from one input to the next as plain sets:
/// the rows a windowed Group accepted (lateness is judged against the frontier at the row's position, premise B2),
/// and, to find what an input added to or took from such an operator, its integrated input after the previous input;
/// and per fold (Def. 6.5) its policy rows with the clock each entered at (#151 D4); the fold itself is recomputed from its present rows (D6).
/// Two more are recorded facts (Def. 7.14): how far each source has reported, and since which clock (the arrival of its row, kept while it has a row at the end of each clock) an element has had a row in the structural input of a Series that fills or has a time to live.
/// </summary>
public static class ReferenceEvaluator
{
    public static SpaceFacts Evaluate(Catalog catalog, string spaceId, IEnumerable<Input> log, Settings? settings = null) =>
        Prefixes(catalog, spaceId, log, settings).Last();

    /// <summary>The facts after every input, the empty log first.</summary>
    public static IEnumerable<SpaceFacts> Prefixes(Catalog catalog, string spaceId, IEnumerable<Input> log, Settings? settings = null)
    {
        var position = Position.Start;
        yield return position.Facts;
        foreach (var input in log)
        {
            position = Step(catalog, catalog.Spaces[spaceId], settings ?? new Settings(), position, input);
            yield return position.Facts;
        }
    }

    sealed record Position(
        ImmutableSortedDictionary<string, ZSet> Sources,
        ImmutableSortedDictionary<string, ZSet> SeenInput,
        ImmutableSortedDictionary<string, ZSet> Accepted,
        ImmutableSortedDictionary<string, ImmutableArray<LinkFact>> Links,
        ImmutableSortedDictionary<string, Denotation.Carried> Carried,
        ImmutableSortedDictionary<string, long> Covered,
        ImmutableSortedDictionary<string, ImmutableSortedDictionary<string, long>> Since,
        ImmutableSortedSet<long> Lost,
        SpaceFacts Facts)
    {
        static readonly ImmutableSortedDictionary<string, ZSet> None = ImmutableSortedDictionary.Create<string, ZSet>(StringComparer.Ordinal);
        public static readonly Position Start = new(None, None, None, ImmutableSortedDictionary.Create<string, ImmutableArray<LinkFact>>(StringComparer.Ordinal),
            ImmutableSortedDictionary.Create<string, Denotation.Carried>(StringComparer.Ordinal),
            ImmutableSortedDictionary.Create<string, long>(StringComparer.Ordinal), ImmutableSortedDictionary.Create<string, ImmutableSortedDictionary<string, long>>(StringComparer.Ordinal),
            ImmutableSortedSet<long>.Empty,
            new SpaceFacts(ImmutableSortedDictionary.Create<string, GraphFacts>(StringComparer.Ordinal), null));
    }

    static Position Step(Catalog catalog, TypedSpace space, Settings settings, Position before, Input input)
    {
        var sources = input is RowInput row ? before.Sources.SetItem(row.Source, before.Sources.GetValueOrDefault(row.Source, ZSet.Empty).Add(RowOf(catalog, space, row), row.Weight)) : before.Sources;
        var frontier = input is AdvanceInput a && (before.Facts.Frontier is null || a.Frontier > before.Facts.Frontier) ? a.Frontier : before.Facts.Frontier;
        var covered = input is ProgressInput progress && progress.Until > before.Covered.GetValueOrDefault(progress.Source, long.MinValue) ? before.Covered.SetItem(progress.Source, progress.Until) : before.Covered;
        var at = before with { Sources = sources, Covered = covered, Accepted = input is RowInput arrived ? OnTimeAtPorts(catalog, space, before, arrived) : before.Accepted, Lost = input is LossInput loss ? before.Lost.Add(loss.Time) : before.Lost, Facts = new SpaceFacts(before.Facts.Graphs.Clear(), frontier) };
        return InDependencyOrder(space).Aggregate(at, (p, g) => Graph(catalog, space, settings, g, before.Facts.Frontier, p));
    }

    /// <summary>Def. 7.14: the sources that feed a event property of a graph. From every sink that sets it, everything upstream: a rows port gives the source bound to it, a Series gives the sources that feed what it reads, a graph that is only matched gives none.</summary>
    /// The lag is how much later than its grid window a window of the property ends: w − h for a window of length w on the grid of its hop h (#111).
    static (ImmutableSortedSet<string> Sources, long Lag) Feeding(Catalog catalog, TypedSpace space, string graphName, string propId)
    {
        long lag = 0;
        var graph = space.Graphs.First(g => g.Name == graphName);
        var plan = PlanOf(catalog, graph);
        var found = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(TOp op)
        {
            if (!seen.Add(op.Name)) return;
            if (op is TWindow w) lag = Math.Max(lag, w.WidthNs - w.HopNs);
            if (op is TSeries s) foreach (var read in s.Reads) { var (f, l) = Feeding(catalog, space, graph.Bindings[s.GraphPort], read.PropId); found.UnionWith(f); lag = Math.Max(lag, l); }
            foreach (var src in op.Inputs.Values)
                if (!src.IsPort) Walk(plan.Ops.First(o => o.Name == src.Name));
                else if (plan.Ports.First(p => p.Name == src.Name).Kind == "rows") found.Add(graph.Bindings[src.Name]);
        }
        foreach (var sink in plan.Ops)
            if ((sink switch { TEmitEntity e => e.Sets, TEmitRel r => r.Sets, _ => null })?.Any(x => x.PropId == propId) == true) Walk(sink);
        return (found.ToImmutable(), lag);
    }

    static TypedProjection PlanOf(Catalog catalog, TGraph g) => g.Instance ?? catalog.Projections[g.ProjectionId];

    static ImmutableArray<Value> RowOf(Catalog catalog, TypedSpace space, RowInput row)
    {
        var graph = space.Graphs.First(g => g.Bindings.Values.Contains(row.Source) && PlanOf(catalog, g).Ports.Any(p => p.Kind == "rows" && g.Bindings.GetValueOrDefault(p.Name) == row.Source));
        var port = PlanOf(catalog, graph).Ports.First(p => p.Kind == "rows" && graph.Bindings.GetValueOrDefault(p.Name) == row.Source);
        return [.. port.Rows!.Cols.Select(c => row.Fields.GetValueOrDefault(c.Name, Value.Null))];
    }

    static IEnumerable<TGraph> InDependencyOrder(TypedSpace space)
    {
        var done = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
        var names = space.Graphs.Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        var left = space.Graphs.ToImmutableList();
        while (left.Count > 0)
        {
            var next = left.First(g => g.Bindings.Values.Where(names.Contains).All(done.Contains));
            yield return next;
            done = done.Add(next.Name); left = left.Remove(next);
        }
    }

    /// <summary>One graph after one input: the operators in plan order, then the materialization of the sinks.</summary>
    static Position Graph(Catalog catalog, TypedSpace space, Settings settings, TGraph graph, long? frontierBefore, Position p)
    {
        var plan = PlanOf(catalog, graph);
        var frontier = p.Facts.Frontier;
        var rowPorts = plan.Ports.Where(x => x.Kind == "rows").ToDictionary(x => x.Name,
            x => new Relation([.. x.Rows!.Cols.Select(c => c.Name)], x.LatenessNs is null ? p.Sources.GetValueOrDefault(graph.Bindings[x.Name], ZSet.Empty) : p.Accepted.GetValueOrDefault(PortSlot(graph, x.Name), ZSet.Empty)), StringComparer.Ordinal);
        var graphPorts = plan.Ports.Where(x => x.Kind == "graph").ToDictionary(x => x.Name, x => p.Facts.Graphs[graph.Bindings[x.Name]], StringComparer.Ordinal);
        var views = plan.Ports.Where(x => x.Kind == "graph").ToDictionary(x => x.Name, x => catalog.OntologyOf(x.ExpectsModule!), StringComparer.Ordinal);
        // as in the engine: an aggregate read only by sinks carries the pair of a mean or a last
        var withPairs = plan.Ops.Where(op => plan.Ops.Where(o => o.Inputs.Values.Any(s => !s.IsPort && s.Name == op.Name)).ToList() is { Count: > 0 } readers && readers.All(o => o is TEmitEntity or TEmitRel)).Select(op => op.Name).ToHashSet(StringComparer.Ordinal);
        var held = plan.Ports.Where(x => x.Kind == "graph").ToDictionary(x => x.Name, x => PlanOf(catalog, space.Graphs.First(g => g.Name == graph.Bindings[x.Name])).EntityTypes, StringComparer.Ordinal);
        var inputs = new PlanInputs(rowPorts, graphPorts, held, (port, type, of) => views[port].IsSubtype(type, of), frontier, withPairs);

        var outs = ImmutableDictionary.Create<string, Relation>(StringComparer.Ordinal);
        foreach (var op in plan.Ops)
        {
            Relation In(string port) => op.Inputs[port] is { IsPort: true } s ? inputs.Rows[s.Name] : outs[op.Inputs[port].Name];
            var slot = graph.Name + "/" + op.Name;
            switch (op)
            {
                case TEmitEntity or TEmitRel: break;
                case TLink link:
                    {
                        // the links of the step are those of its rows now (Ch. 2, Def. 2.5): a link whose rows are all retracted is withdrawn
                        var rows = In("in");
                        var live = link.Mode == "assert" && link.Confidence >= link.Threshold
                            ? rows.Rows.Where(r => r.Weight > 0).Select(r => (Left: Expressions.KeyOf(link.Left, rows.Reader(r.Row)), Right: Expressions.KeyOf(link.Right, rows.Reader(r.Row)))).Where(k => k.Left is not null && k.Right is not null).Select(k => new LinkFact(k.Left!, k.Right!)).DistinctBy(f => f.Text).ToImmutableArray()
                            : [];
                        p = p with { Links = p.Links.SetItem(slot, live) };
                        break;
                    }
                case TGroup { ClosesWindows: true } group:
                    {
                        var window = Upstream<TWindow>(plan, group) ?? throw new Undefined($"Group {group.Name} closes windows and has no Window upstream");
                        var rows = In("in");
                        var accepted = rows.Rows.Minus(p.SeenInput.GetValueOrDefault(slot, ZSet.Empty))
                            .Where(r => OnTime(window, settings, ((InstantValue)rows.Reader(r.Row)("windowStart")).Ns, frontierBefore))
                            .Aggregate(p.Accepted.GetValueOrDefault(slot, ZSet.Empty), (z, r) => z.Add(r.Row, r.Weight));
                        p = p with { SeenInput = p.SeenInput.SetItem(slot, rows.Rows), Accepted = p.Accepted.SetItem(slot, accepted) };
                        var closed = ZSet.Of(accepted.Where(r => frontier is not null && ((InstantValue)rows.Reader(r.Row)("windowStart")).Ns + window.WidthNs <= frontier));
                        outs = outs.SetItem(op.Name, Denotation.Of(group, _ => rows with { Rows = closed }, inputs));
                        break;
                    }
                case TFold fold:
                    {
                        var (width, hop, lateness) = Windows(plan, fold, settings);
                        var (result, carried) = Denotation.Fold(fold, In("in"), In("policy"), width, hop, lateness, frontierBefore, frontier, p.Carried.GetValueOrDefault(slot, Denotation.Carried.None));
                        p = p with { Carried = p.Carried.SetItem(slot, carried) };
                        outs = outs.SetItem(op.Name, result);
                        break;
                    }
                case TSeries series when series.TtlNs is not null || series.Reads.Any(r => r.Fill is not null):
                    {
                        var rows = In("in");
                        var since = p.Since.GetValueOrDefault(slot, ImmutableSortedDictionary.Create<string, long>(StringComparer.Ordinal));
                        // #159 G3: an element with a row keeps its `since` or gets the clock its row arrived at (the first clock for a row before it);
                        // presence is sampled at the end of a clock, so one without a row loses its `since` only at an advance
                        if (frontier is { } now)
                        {
                            var present = rows.Rows.Where(r => r.Weight > 0).Select(r => rows.Reader(r.Row)(series.TargetVar)).OfType<EntityValue>().Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
                            var kept = frontier != frontierBefore ? since.Where(kv => present.Contains(kv.Key)) : since;
                            since = kept.Concat(present.Where(id => !since.ContainsKey(id)).Select(id => KeyValuePair.Create(id, now))).ToImmutableSortedDictionary(StringComparer.Ordinal);
                        }
                        p = p with { Since = p.Since.SetItem(slot, since) };
                        var fed = series.Reads.Select(read => Feeding(catalog, space, graph.Bindings[series.GraphPort], read.PropId)).ToArray();
                        long? Covered(int i) => fed[i].Sources is { Count: > 0 } feeding && feeding.All(p.Covered.ContainsKey) ? feeding.Min(s => p.Covered[s]) : null;
                        outs = outs.SetItem(op.Name, Denotation.Series(series, rows, inputs.Graphs[series.GraphPort], frontier, since, [.. series.Reads.Select((_, i) => Covered(i))], [.. fed.Select(x => x.Lag)], p.Lost));
                        break;
                    }
                default: outs = outs.SetItem(op.Name, Denotation.Of(op, In, inputs)); break;
            }
        }

        var ids = new Identities(settings.Tenant, p.Links.Values.SelectMany(l => l));
        Relation SinkInput(TOp sink) => sink.Inputs["in"] is { IsPort: true } s ? rowPorts[s.Name] : outs[sink.Inputs["in"].Name];
        var facts = Materialization.Of(plan.Ops.SelectMany(op => Materialization.Fragments(op, SinkInput, ids, settings.Tenant, plan.OutputModule)), frontier, settings.WindowRetentionNs);
        return p with { Facts = p.Facts with { Graphs = p.Facts.Graphs.SetItem(graph.Name, facts) } };
    }

    /// <summary>Ch. 6, Def. 6.1: a row enters every rows port bound to its source that declares a lateness, unless the clock when it arrives is beyond its time plus
    /// the lateness. A withdrawal is judged the same way, by the clock when it arrives.</summary>
    static ImmutableSortedDictionary<string, ZSet> OnTimeAtPorts(Catalog catalog, TypedSpace space, Position before, RowInput row)
    {
        var accepted = before.Accepted;
        foreach (var graph in space.Graphs)
            foreach (var (port, from) in graph.Bindings)
                if (from == row.Source && PlanOf(catalog, graph).Ports.FirstOrDefault(x => x.Name == port) is { TimeField: { } time, LatenessNs: { } lateness }
                    && (before.Facts.Frontier is not { } clock || (System.Int128)((InstantValue)row.Fields[time]).Ns + lateness >= clock))
                    accepted = accepted.SetItem(PortSlot(graph, port), accepted.GetValueOrDefault(PortSlot(graph, port), ZSet.Empty).Add(RowOf(catalog, space, row), row.Weight));
        return accepted;
    }
    static string PortSlot(TGraph graph, string port) => graph.Name + "/port:" + port;

    /// <summary>The nearest operator of a kind upstream of an operator along its `in` inputs: the Window whose windows a Group closes, the Window or Series whose windows a fold reads.</summary>
    static T? Upstream<T>(TypedProjection plan, TOp op, Func<T, bool>? fits = null) where T : TOp
    {
        for (var cur = op; cur.Inputs.TryGetValue("in", out var src) && !src.IsPort;)
        {
            cur = plan.Ops.First(o => o.Name == src.Name);
            if (cur is T found && (fits is null || fits(found))) return found;
        }
        return null;
    }

    /// <summary>Def. 6.4 for a fold: the windows of its nearest Window (width, hop, windows start on the grid of the hop) or Series (width and hop its period), and
    /// the lateness λ that closes them, the time the engine keeps such a window revisable: the Window's lateness, else the late horizon, at most the retention.</summary>
    static (long Width, long Hop, long Lateness) Windows(TypedProjection plan, TFold fold, Settings s)
    {
        long Revisable(long width, long? lateness) => (long)System.Int128.Min(lateness ?? s.Horizon(width), s.WindowRetentionNs);
        return Upstream<TOp>(plan, fold, o => o is TWindow or TSeries) switch
        {
            TWindow w => (w.WidthNs, w.HopNs, Revisable(w.WidthNs, w.LatenessNs)),
            TSeries x => (x.PeriodNs, x.PeriodNs, Revisable(x.PeriodNs, null)),
            _ => throw new Undefined($"Fold {fold.Name} has no Window or Series upstream"),
        };
    }

    /// <summary>Premise B2 and Def. 7.6: a row counts for its window unless the window was beyond the late horizon; a row for a closed window revises it.</summary>
    static bool OnTime(TWindow w, Settings s, long windowStart, long? frontierAtPosition)
    {
        if (frontierAtPosition is not { } frontier) return true;
        var end = windowStart + w.WidthNs;
        var beyondHorizon = end + System.Int128.Min(w.LatenessNs ?? s.Horizon(w.WidthNs), s.WindowRetentionNs) <= frontier;      // revisable for the smaller of the inputs' lateness (Cor. 6.5), else the late horizon, and the retention (Def. 7.13)
        return !beyondHorizon;
    }
}
