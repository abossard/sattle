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

/// <summary>`TelemetryRetentionNs`: the declared retention of the series of the space (Def. 7.13).</summary>
public sealed record Settings(string Tenant = "lab", int LateHorizonWindows = 3, long TelemetryRetentionNs = 48L * 3_600_000_000_000);

/// <summary>Every graph of a space after a prefix of the input log.</summary>
public sealed record SpaceFacts(ImmutableSortedDictionary<string, GraphFacts> Graphs, long? Frontier);

/// <summary>
/// The meaning of a graph space on an input log, computed from scratch (Def. 6.4): after each input every graph is evaluated in batch from the integrated inputs.
/// Four things are functions of the log and not of the integrated state, and are carried from one input to the next as plain sets:
/// the rows a windowed Group accepted (lateness is judged against the frontier at the row's position, premise B2),
/// the identity links ever asserted (bindings are timeless, Theorem 1.5),
/// the state of a Threshold key after the windows that left its look-back (Def. 7.10, D33),
/// and, to find what an input added to or took from such an operator, its integrated input after the previous input.
/// Two more are recorded facts (Def. 7.14): how far each source has reported, and since which advance an element has had a row in the structural input of a Series that fills or has a time to live.
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
        ImmutableSortedDictionary<string, LinkFact> Links,
        ImmutableSortedDictionary<string, ImmutableSortedDictionary<string, Denotation.Carried>> Carried,
        ImmutableSortedDictionary<string, long> Covered,
        ImmutableSortedDictionary<string, ImmutableSortedDictionary<string, long>> Since,
        ImmutableSortedSet<long> Lost,
        SpaceFacts Facts)
    {
        static readonly ImmutableSortedDictionary<string, ZSet> None = ImmutableSortedDictionary.Create<string, ZSet>(StringComparer.Ordinal);
        public static readonly Position Start = new(None, None, None, ImmutableSortedDictionary.Create<string, LinkFact>(StringComparer.Ordinal),
            ImmutableSortedDictionary.Create<string, ImmutableSortedDictionary<string, Denotation.Carried>>(StringComparer.Ordinal),
            ImmutableSortedDictionary.Create<string, long>(StringComparer.Ordinal), ImmutableSortedDictionary.Create<string, ImmutableSortedDictionary<string, long>>(StringComparer.Ordinal),
            ImmutableSortedSet<long>.Empty,
            new SpaceFacts(ImmutableSortedDictionary.Create<string, GraphFacts>(StringComparer.Ordinal), null));
    }

    static Position Step(Catalog catalog, TypedSpace space, Settings settings, Position before, Input input)
    {
        var sources = input is RowInput row ? before.Sources.SetItem(row.Source, before.Sources.GetValueOrDefault(row.Source, ZSet.Empty).Add(RowOf(catalog, space, row), row.Weight)) : before.Sources;
        var frontier = input is AdvanceInput a && (before.Facts.Frontier is null || a.Frontier > before.Facts.Frontier) ? a.Frontier : before.Facts.Frontier;
        var covered = input is ProgressInput progress && progress.Until > before.Covered.GetValueOrDefault(progress.Source, long.MinValue) ? before.Covered.SetItem(progress.Source, progress.Until) : before.Covered;
        var at = before with { Sources = sources, Covered = covered, Lost = input is LossInput loss ? before.Lost.Add(loss.Time) : before.Lost, Facts = new SpaceFacts(before.Facts.Graphs.Clear(), frontier) };
        return InDependencyOrder(space).Aggregate(at, (p, g) => Graph(catalog, space, settings, g, before.Facts.Frontier, p));
    }

    /// <summary>Def. 7.14: the sources that feed a telemetry property of a graph. From every sink that sets it, everything upstream: a rows port gives the source bound to it, a Series gives the sources that feed what it reads, a graph that is only matched gives none.</summary>
    static ImmutableSortedSet<string> Feeding(Catalog catalog, TypedSpace space, string graphName, string propId)
    {
        var graph = space.Graphs.First(g => g.Name == graphName);
        var plan = PlanOf(catalog, graph);
        var found = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(TOp op)
        {
            if (!seen.Add(op.Name)) return;
            if (op is TSeries s) foreach (var read in s.Reads) found.UnionWith(Feeding(catalog, space, graph.Bindings[s.GraphPort], read.PropId));
            foreach (var src in op.Inputs.Values)
                if (!src.IsPort) Walk(plan.Ops.First(o => o.Name == src.Name));
                else if (plan.Ports.First(p => p.Name == src.Name).Kind == "rows") found.Add(graph.Bindings[src.Name]);
        }
        foreach (var sink in plan.Ops)
            if ((sink switch { TEmitEntity e => e.Sets, TEmitRel r => r.Sets, _ => null })?.Any(x => x.PropId == propId) == true) Walk(sink);
        return found.ToImmutable();
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
            x => new Relation([.. x.Rows!.Cols.Select(c => c.Name)], p.Sources.GetValueOrDefault(graph.Bindings[x.Name], ZSet.Empty)), StringComparer.Ordinal);
        var graphPorts = plan.Ports.Where(x => x.Kind == "graph").ToDictionary(x => x.Name, x => p.Facts.Graphs[graph.Bindings[x.Name]], StringComparer.Ordinal);
        var views = plan.Ports.Where(x => x.Kind == "graph").ToDictionary(x => x.Name, x => catalog.OntologyOf(x.ExpectsModule!), StringComparer.Ordinal);
        var inputs = new PlanInputs(rowPorts, graphPorts, (port, type, of) => views[port].IsSubtype(type, of), frontier);

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
                        var rows = In("in");
                        var added = rows.Rows.Minus(p.SeenInput.GetValueOrDefault(slot, ZSet.Empty)).Where(r => r.Weight > 0);
                        var asserted = link.Mode == "assert" && link.Confidence >= link.Threshold
                            ? added.Select(r => (Left: Expressions.KeyOf(link.Left, rows.Reader(r.Row)), Right: Expressions.KeyOf(link.Right, rows.Reader(r.Row)))).Where(k => k.Left is not null && k.Right is not null).Select(k => new LinkFact(k.Left!, k.Right!))
                            : [];
                        p = p with { SeenInput = p.SeenInput.SetItem(slot, rows.Rows), Links = asserted.Aggregate(p.Links, (l, f) => l.SetItem(f.Text, f)) };
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
                case TThreshold threshold:
                    {
                        var rows = In("in");
                        var series = Upstream<TSeries>(plan, threshold);
                        var within = series?.WithinNs;
                        var (result, carried) = Denotation.Threshold(threshold, rows with { Rows = p.SeenInput.GetValueOrDefault(slot, ZSet.Empty) }, rows,
                            within is { } w && frontier is { } f ? f - w : null, within, frontier, (within ?? 0) + (series?.PeriodNs ?? 0) * settings.LateHorizonWindows,
                            p.Carried.GetValueOrDefault(slot, ImmutableSortedDictionary.Create<string, Denotation.Carried>(StringComparer.Ordinal)));
                        p = p with { SeenInput = p.SeenInput.SetItem(slot, rows.Rows), Carried = p.Carried.SetItem(slot, carried) };
                        outs = outs.SetItem(op.Name, result);
                        break;
                    }
                case TSeries series when series.TtlNs is not null || series.Reads.Any(r => r.Fill is not null):
                    {
                        var rows = In("in");
                        var since = p.Since.GetValueOrDefault(slot, ImmutableSortedDictionary.Create<string, long>(StringComparer.Ordinal));
                        if (frontier is { } now && frontier != frontierBefore)      // presence is sampled at an advance: an element with a row keeps its `since` or gets this frontier, one without loses it
                            since = rows.Rows.Where(r => r.Weight > 0).Select(r => rows.Reader(r.Row)(series.TargetVar)).OfType<EntityValue>().Select(e => e.Id).Distinct(StringComparer.Ordinal)
                                .ToImmutableSortedDictionary(id => id, id => since.TryGetValue(id, out var kept) ? kept : now, StringComparer.Ordinal);
                        p = p with { Since = p.Since.SetItem(slot, since) };
                        long? Covered(TSeriesRead read) =>
                            Feeding(catalog, space, graph.Bindings[series.GraphPort], read.PropId) is { Count: > 0 } feeding && feeding.All(p.Covered.ContainsKey) ? feeding.Min(s => p.Covered[s]) : null;
                        outs = outs.SetItem(op.Name, Denotation.Series(series, rows, inputs.Graphs[series.GraphPort], frontier, since, [.. series.Reads.Select(Covered)], p.Lost));
                        break;
                    }
                default: outs = outs.SetItem(op.Name, Denotation.Of(op, In, inputs)); break;
            }
        }

        var ids = new Identities(settings.Tenant, p.Links.Values);
        Relation SinkInput(TOp sink) => sink.Inputs["in"] is { IsPort: true } s ? rowPorts[s.Name] : outs[sink.Inputs["in"].Name];
        var facts = Materialization.Of(plan.Ops.SelectMany(op => Materialization.Fragments(op, SinkInput, ids, settings.Tenant, plan.OutputModule)), frontier, settings.TelemetryRetentionNs);
        return p with { Facts = p.Facts with { Graphs = p.Facts.Graphs.SetItem(graph.Name, facts) } };
    }

    /// <summary>The nearest operator of a kind upstream of an operator along its `in` inputs: the Window whose windows a Group closes, the Series whose look-back a Threshold reads through.</summary>
    static T? Upstream<T>(TypedProjection plan, TOp op) where T : TOp
    {
        for (var cur = op; cur.Inputs.TryGetValue("in", out var src) && !src.IsPort;)
        {
            cur = plan.Ops.First(o => o.Name == src.Name);
            if (cur is T found) return found;
        }
        return null;
    }

    /// <summary>Premise B2 and Def. 7.6: a row counts for its window unless the window was closed at the row's position and the policy does not revise, or the window was beyond the late horizon.</summary>
    static bool OnTime(TWindow w, Settings s, long windowStart, long? frontierAtPosition)
    {
        if (frontierAtPosition is not { } frontier) return true;
        var end = windowStart + w.WidthNs;
        var closed = end <= frontier;
        var beyondHorizon = end + Math.Min(w.WidthNs * s.LateHorizonWindows, s.TelemetryRetentionNs) <= frontier;      // revisable for the smaller of the late horizon and the retention (Def. 7.13)
        return !beyondHorizon && (!closed || w.Late == "revise");
    }
}
