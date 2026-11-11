using System.Collections.Immutable;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Reference;

/// <summary>One recorded input (Def. 6.1): a row of a source with its weight, or a logical-time control.</summary>
public abstract record Input;
public sealed record RowInput(string Source, ImmutableSortedDictionary<string, Value> Fields, long Weight) : Input;
public sealed record AdvanceInput(long Frontier) : Input;

public sealed record Settings(string Tenant = "lab", int LateHorizonWindows = 3);

/// <summary>Every graph of a space after a prefix of the input log.</summary>
public sealed record SpaceFacts(ImmutableSortedDictionary<string, GraphFacts> Graphs, long? Frontier);

/// <summary>
/// The meaning of a graph space on an input log, computed from scratch (Def. 6.4): after each input every graph is evaluated in batch from the integrated inputs.
/// Four things are functions of the log and not of the integrated state, and are carried from one input to the next as plain sets:
/// the rows a windowed Group accepted (lateness is judged against the frontier at the row's position, premise B2),
/// the identity links ever asserted (bindings are timeless, Theorem 1.5),
/// the state of a Threshold key after the windows that left its look-back (Def. 7.10, D33),
/// and, to find what an input added to or took from such an operator, its integrated input after the previous input.
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
        SpaceFacts Facts)
    {
        static readonly ImmutableSortedDictionary<string, ZSet> None = ImmutableSortedDictionary.Create<string, ZSet>(StringComparer.Ordinal);
        public static readonly Position Start = new(None, None, None, ImmutableSortedDictionary.Create<string, LinkFact>(StringComparer.Ordinal),
            ImmutableSortedDictionary.Create<string, ImmutableSortedDictionary<string, Denotation.Carried>>(StringComparer.Ordinal),
            new SpaceFacts(ImmutableSortedDictionary.Create<string, GraphFacts>(StringComparer.Ordinal), null));
    }

    static Position Step(Catalog catalog, TypedSpace space, Settings settings, Position before, Input input)
    {
        var sources = input is RowInput row ? before.Sources.SetItem(row.Source, before.Sources.GetValueOrDefault(row.Source, ZSet.Empty).Add(RowOf(catalog, space, row), row.Weight)) : before.Sources;
        var frontier = input is AdvanceInput a && (before.Facts.Frontier is null || a.Frontier > before.Facts.Frontier) ? a.Frontier : before.Facts.Frontier;
        var at = before with { Sources = sources, Facts = new SpaceFacts(before.Facts.Graphs.Clear(), frontier) };
        return InDependencyOrder(space).Aggregate(at, (p, g) => Graph(catalog, settings, g, before.Facts.Frontier, p));
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
    static Position Graph(Catalog catalog, Settings settings, TGraph graph, long? frontierBefore, Position p)
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
                default: outs = outs.SetItem(op.Name, Denotation.Of(op, In, inputs)); break;
            }
        }

        var ids = new Identities(settings.Tenant, p.Links.Values);
        Relation SinkInput(TOp sink) => sink.Inputs["in"] is { IsPort: true } s ? rowPorts[s.Name] : outs[sink.Inputs["in"].Name];
        var facts = Materialization.Of(plan.Ops.SelectMany(op => Materialization.Fragments(op, SinkInput, ids, settings.Tenant, plan.OutputModule)));
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
        var beyondHorizon = end + w.WidthNs * s.LateHorizonWindows <= frontier;
        return !beyondHorizon && (!closed || w.Late == "revise");
    }
}
