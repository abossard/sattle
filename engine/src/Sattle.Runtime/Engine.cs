using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

public sealed record EngineOptions
{
    public string Tenant { get; init; } = "lab";
    /// <summary>The store of the committed patches. The default keeps the head and a tail of commits, and reads the head from the engine's own graphs (`HeadOnlyGraphStore`); `InMemoryGraphStore` keeps the full history (the test double of the conformance suite).</summary>
    public Func<string, IGraphStore> StoreFactory { get; init; } = _ => new HeadOnlyGraphStore();
    public Func<ITelemetryStore> TelemetryFactory { get; init; } = () => new InMemoryTelemetryStore();
    /// <summary>Closed windows stay revisable for this many window widths (allowed lateness, Def. 7.6). Null is no late horizon: a closed window stays revisable while its telemetry is retained.</summary>
    public int? LateHorizonWindows { get; init; } = 3;
    /// <summary>The declared retention of every telemetry series of the space (Def. 7.13): a closed window is kept while its end is after the logical frontier minus this age. Until the definitions declare it per series it is one age for the space. A window is revisable for the smaller of this and the late horizon.</summary>
    public long TelemetryRetentionNs { get; init; } = 48L * 3_600_000_000_000;
    /// <summary>When set, only these graphs and the graphs they derive from are activated (a staged evaluation of one view needs no other graph).</summary>
    public IReadOnlySet<string>? Only { get; init; }
}

public sealed record PlanInfo(string Projection, string Digest, IReadOnlyList<string> Strategies, IReadOnlyList<IReadOnlyList<string>> FusedChains, string MemoryBound);

/// <summary>A projection compiled for the hot path (research 03 section 11.1): interned ids, column layouts per port, vectorized expressions, one physical operator per typed operator.</summary>
public sealed class CompiledProjection
{
    public required TypedProjection Typed { get; init; }
    public required IReadOnlyList<PhysOp> Ops { get; init; }
    public required PlanInfo Info { get; init; }
    public Layout PortLayout(string port) => Layout.Of(Typed.Port(port).Rows!);
}

public static class PlanCompiler
{
    public const string EngineVersion = "0.1";

    /// <summary>A sink that reads the output of a `Group` directly: the group and the positions of its key columns in the sink's input, so an element emitted from the group can name the rows that make the group up.</summary>
    static (string, int[])? GroupRefOf(TypedProjection tp, TOp sink)
    {
        if (!sink.Inputs.TryGetValue("in", out var src) || src.IsPort) return null;
        if (tp.Ops.First(o => o.Name == src.Name) is not TGroup { ClosesWindows: false } g) return null;
        var cols = g.Keys.Select(k => g.Out.Cols.ToList().FindIndex(c => c.Name == k.Name)).ToArray();
        return cols.Any(c => c < 0) ? null : (g.Name, cols);
    }

    /// <summary>Element-valued columns of a sink's input that come from a Match: the lineage sources of the rows it emits.</summary>
    static (string, int, bool, string)[] LineageCols(TypedProjection tp, TOp sink)
    {
        if (!sink.Inputs.TryGetValue("in", out var src)) return [];
        var schema = src.IsPort ? tp.Port(src.Name).Rows! : tp.Ops.First(o => o.Name == src.Name).Out;
        var res = new List<(string, int, bool, string)>();
        for (var i = 0; i < schema.Cols.Count; i++)
        {
            var c = schema.Cols[i];
            if (c.Origin is null || c.T.Ty.Tag is not (Tag.Ent or Tag.Rel)) continue;
            var op = c.Origin.Split(':')[0];
            if (tp.Ops.FirstOrDefault(o => o.Name == op) is TMatch m) res.Add((c.Name, i, c.T.Ty.Tag == Tag.Rel, m.GraphPort));
            else if (tp.Ops.FirstOrDefault(o => o.Name == op) is TTraverse tv) res.Add((c.Name, i, false, tv.GraphPort));
        }
        return [.. res];
    }

    /// <summary>The Window whose windows a Group closes: the nearest one upstream of it (two windows of one width may have different late policies).</summary>
    static TWindow WindowOf(TypedProjection tp, TOp op)
    {
        for (var cur = op; cur.Inputs.TryGetValue("in", out var src) && !src.IsPort;)
        {
            cur = tp.Ops.First(o => o.Name == src.Name);
            if (cur is TWindow w) return w;
        }
        return tp.Ops.OfType<TWindow>().First(x => x.Out.Window == op.Out.Window);
    }

    /// <summary>The nearest Series upstream of an operator along its `in` inputs (its `within` and its period); null when it reads no Series.</summary>
    static TSeries? SeriesOf(TypedProjection tp, TOp op)
    {
        for (var cur = op; cur.Inputs.TryGetValue("in", out var src) && !src.IsPort;)
        {
            cur = tp.Ops.First(o => o.Name == src.Name);
            if (cur is TSeries s) return s;
        }
        return null;
    }

    public static CompiledProjection Compile(TypedProjection tp, Catalog catalog, OpContext cx)
    {
        var ops = new List<PhysOp>(); var byName = new Dictionary<string, PhysOp>();
        var strategies = new List<string>();
        Layout InputLayout(TOp t, string port)
        {
            var src = t.Inputs[port];
            return src.IsPort ? Layout.Of(tp.Port(src.Name).Rows!) : byName[src.Name].OutLayout;
        }
        IdentityCatalog cat = cx.Ids;
        // a lift statement is lowered at check time: its operators are in tp.Ops like any other, the plan names them under their statement
        foreach (var l in tp.Lifts)
            strategies.Add($"{l.Name}: Lift {l.EdgeType} through {l.MapType} to {l.Emit} (images {l.Images}), lowered to {l.Ops.Count} operators: {string.Join(", ", l.Ops)}");
        var sink = 0;
        foreach (var t in tp.Ops)
        {
            Layout inL = t.Inputs.ContainsKey("in") ? InputLayout(t, "in") : new Layout([], []);
            VExpr Compile(TExpr e)
            {
                VExpr IdC(TId id)
                {
                    var dom = cat.Domain(id.DomainId, id.DomainName, [.. id.Comps.Select(c => c.CompName)], [.. id.Comps.Select(c => c.Canonicalizer)], id.Type.Ty.TypeId);
                    return new VId(dom, [.. id.Comps.Select(c => ExprCompiler.Compile(c.Value, inL, cx.It, cx.Faults, IdC))], cat, cx.It, cx.Faults);
                }
                return ExprCompiler.Compile(e, inL, cx.It, cx.Faults, IdC);
            }
            VId MakeId(TId id)
            {
                var dom = cat.Domain(id.DomainId, id.DomainName, [.. id.Comps.Select(c => c.CompName)], [.. id.Comps.Select(c => c.Canonicalizer)], id.Type.Ty.TypeId);
                return new VId(dom, [.. id.Comps.Select(c => Compile(c.Value))], cat, cx.It, cx.Faults);
            }
            PhysOp op;
            // an aggregate whose rows go only to sinks carries the pair of a mean or a last next to it (Law 7.5 item 3 (i)); other readers see the declared columns alone
            var readers = tp.Ops.Where(o => o.Inputs.Values.Any(s => !s.IsPort && s.Name == t.Name)).ToList();
            var withCompanions = readers.Count > 0 && readers.All(o => o is TEmitEntity or TEmitRel);
            switch (t)
            {
                case TMatch m:
                    {
                        var view = catalog.OntologyOf(tp.Port(m.GraphPort).ExpectsModule!);
                        var mo = new MatchOp(m, view); mo.Bind(cx.It); op = mo;
                        strategies.Add($"{m.Name}: Match, bilinear delta against the input graph (new before the seed component, old after it)");
                        break;
                    }
                case TTraverse tr: op = new TraverseOp(tr, inL, catalog.OntologyOf(tp.Port(tr.GraphPort).ExpectsModule!), cx.It); strategies.Add($"{tr.Name}: Traverse, per start element a breadth-first reach over {tr.Direction} edges to depth {tr.Max}; a start is recomputed only when a visited element or edge changed, and only the difference is emitted"); break;
                case TValues v: op = new ValuesOp(v, cx.It); strategies.Add($"{v.Name}: Values, the constant rows are emitted once, in the first evaluation of the graph"); break;
                case TJoin j: op = new JoinOp(j, InputLayout(j, "left"), InputLayout(j, "right"), cx); strategies.Add($"{j.Name}: Join, hash join on {j.Keys.Count} equality key(s), bilinear delta: new left against old right, then new right against the new left"); break;
                case TUnion u: op = new UnionOp(u); strategies.Add($"{u.Name}: Union, linear: both inputs' signed rows concatenated (weights add)"); break;
                case TDistinct ds: op = new DistinctOp(ds, inL); strategies.Add($"{ds.Name}: Distinct, per-row multiplicity counts; emits +1 when a row first appears and -1 when its last copy goes"); break;
                case TFilter f: op = new FilterOp(f, inL, cx); strategies.Add($"{f.Name}: Filter, linear, selection vector"); break;
                case TMap m: op = new MapOp(m, inL, cx); strategies.Add($"{m.Name}: Map, linear, shared columns"); break;
                case TRetain rt: op = new RetainOp(rt, inL); strategies.Add($"{rt.Name}: Retain, buffers rows by {rt.TimeField}; withdraws a row when the frontier passes its time plus {Lexer.DurationText(rt.AgeNs)}"); break;
                case TWindow w: op = new WindowOp(w, inL, cx); strategies.Add($"{w.Name}: Window, linear, window assignment"); break;
                case TGroup g when g.ClosesWindows:
                    {
                        var win = WindowOf(tp, g);
                        op = new WindowedGroupOp(g, win.WidthNs, win.Late, inL, cx, withCompanions);
                        strategies.Add($"{g.Name}: Group over windows, per-window hash accumulators, windows close on the logical frontier, late policy {win.Late}");
                        break;
                    }
                case TGroup g: op = new GroupOp(g, inL, cx, LineageCols(tp, t), withCompanions); strategies.Add($"{g.Name}: Group, per-key accumulators updated by signed weights"); break;
                case TSeries s: op = new SeriesOp(s, inL, cx.It); strategies.Add($"{s.Name}: Series, bilinear: new rows against seen windows, new windows against all rows, retraction at the look-back"); break;
                case TRollup r: op = new RollupOp(r, inL, cx, withCompanions); strategies.Add($"{r.Name}: Rollup, per (key, window) accumulators, decomposable sum"); break;
                case TThreshold th: op = new ThresholdOp(th, inL, cx, SeriesOf(tp, th)); strategies.Add($"{th.Name}: Threshold, per-key state machine over ordered closed windows"); break;
                case TEmitEntity e: op = new EmitEntityOp(e, sink++, inL, cx, Compile, LineageCols(tp, t), GroupRefOf(tp, t)); strategies.Add($"{e.Name}: EmitEntity {(e.Update ? "update" : "create")}, support-counted element layer"); break;
                case TEmitRel e: op = new EmitRelOp(e, sink++, inL, cx, Compile, LineageCols(tp, t), GroupRefOf(tp, t)); strategies.Add($"{e.Name}: EmitRelationship {(e.Update ? "update" : "create")}, support-counted element layer"); break;
                case TLink l: op = new LinkOp(l, MakeId(l.Left), MakeId(l.Right), cat); strategies.Add($"{l.Name}: Link, identity binding (alias) when asserted"); break;
                default: throw new NotSupportedException(t.Kind);
            }
            ops.Add(op); byName[t.Name] = op;
        }
        // fused chains: maximal runs of linear operators with a single consumer
        var consumers = tp.Ops.SelectMany(o => o.Inputs.Values.Where(s => !s.IsPort).Select(s => s.Name)).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        bool Linear(TOp o) => o is TFilter or TMap or TWindow;
        var chains = new List<IReadOnlyList<string>>(); var used = new HashSet<string>();
        foreach (var o in tp.Ops.Where(Linear))
        {
            if (used.Contains(o.Name)) continue;
            var chain = new List<string> { o.Name }; used.Add(o.Name);
            var cur = o;
            while (consumers.GetValueOrDefault(cur.Name) == 1)
            {
                var next = tp.Ops.FirstOrDefault(x => x.Inputs.Values.Any(s => !s.IsPort && s.Name == cur.Name));
                if (next is null || !Linear(next) || used.Contains(next.Name)) break;
                chain.Add(next.Name); used.Add(next.Name); cur = next;
            }
            if (chain.Count > 1) chains.Add(chain);
        }
        var digest = Digests.Of("[\"" + EngineVersion + "\",\"" + catalog.Modules.GetValueOrDefault(tp.Module)?.Digest + "\",\"" + tp.Id + "\"]");
        var bound = "Series and Rollup state is bounded by look-back / period windows per key; Group by open windows plus " + (cx.LateHorizonFactor is { } horizon ? horizon + " closed windows" : "the closed windows that are retained") + "; Match, Join-free, holds no state beyond the input graph.";
        return new CompiledProjection { Typed = tp, Ops = ops, Info = new PlanInfo(tp.Name, digest, strategies, chains, bound) };
    }
}

public sealed record GraphResult(Patch? Patch, IReadOnlyList<WindowChange> Telemetry, long Revision);
/// <summary>`Conflicts`: the conflicts of Def. 6.4 that appeared, changed or ended with this commit (an ended one has kind `resolved`).</summary>
public sealed record CommitEvent(string Graph, Patch? Patch, IReadOnlyList<WindowChange> Telemetry, long Lambda, long Seq, Interner Interner)
{
    public IReadOnlyList<Conflict> Conflicts { get; init; } = [];
}

/// <summary>One graph of the space with its compiled projection, hot graph, and storage.</summary>
public sealed class GraphRuntime
{
    public required string Name { get; init; }
    public required GraphKey Key { get; init; }
    public required CompiledProjection Plan { get; init; }
    public required bool IsMaster { get; init; }
    public required OpContext Cx { get; init; }
    public required OutputBuilder Out { get; init; }
    public Graph State { get; } = new();
    public long Head { get; set; }
    public long TelemetryPosition { get; set; }
    public Dictionary<string, string> PortSources { get; } = []; // port -> source or upstream graph name
    public long Skipped { get; set; }
    public TypedProjection Typed => Plan.Typed;
}

/// <summary>The activated graph space: master ingestion, derived graphs in dependency order, ordered deterministic evaluation with explicit logical time, per-graph atomic commit through the port.</summary>
public sealed class Engine : IDisposable, IEngineInput
{
    readonly Catalog catalog;
    readonly EngineOptions options;
    readonly IGraphStore store;
    readonly Dictionary<string, GraphRuntime> byName = [];
    readonly List<GraphRuntime> graphs = [];
    readonly Dictionary<string, (string Graph, string Port)> sourcePort = new(StringComparer.Ordinal);
    long epochId;
    long lambda = long.MinValue;

    public Interner Interner { get; }
    public RuntimeFaults Faults { get; } = new();
    public IdentityCatalog Identities { get; }
    public ITelemetryStore Telemetry { get; }
    public IReadOnlyList<GraphRuntime> Graphs => graphs;

    /// <summary>Fixpoints whose last unrolled step still derived facts: R(max) differs from R(max - 1), so the result may lack facts that need more steps than the bound (Def. 5.19, Theorem 6.5). Per graph and fixpoint (its definition id), the number of facts the last step added. Computed from the operators' state, nothing extra is kept.</summary>
    public IEnumerable<(string Graph, string Fixpoint, long Gained)> FixpointsAtBound()
    {
        foreach (var g in graphs)
        {
            var distinct = g.Plan.Ops.OfType<DistinctOp>().ToList();
            foreach (var last in distinct.Where(d => !d.Name.Contains('@')))
            {
                var steps = distinct.Where(d => d.Typed.Id == last.Typed.Id && d.Name.StartsWith(last.Name + "@", StringComparison.Ordinal) && int.TryParse(d.Name[(last.Name.Length + 1)..], out _)).ToList();
                if (steps.Count == 0) continue;
                var prev = steps.MaxBy(d => int.Parse(d.Name[(last.Name.Length + 1)..]))!;
                var gained = last.Held - prev.Held;
                if (gained > 0) yield return (g.Name, last.Typed.Id, gained);
            }
        }
    }
    public GraphRuntime Master => graphs.First(g => g.IsMaster);
    public string SpaceName { get; }
    public string Tenant => options.Tenant;
    public IGraphStore Store => store;
    public event Action<CommitEvent>? Committed;
    /// <summary>Diagnostic: bytes allocated per operator, accumulated over all steps (PERF.md method).</summary>
    public bool TrackAllocations { get; set; }

    /// <summary>Graphs whose operator outputs are integrated for inspection (a draft preview, `include: rows`): per graph and operator, the rows the operator holds now with their summed weights. Null: nothing is traced.</summary>
    public HashSet<string>? TraceGraphs { get; set; }
    /// <summary>The traced rows, by graph name and operator name: each row by its cells (null absent, decimal, a text or element id, or the raw integer of an Integer, Instant or Boolean) with its weight.</summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, (long Weight, object?[] Cells)>>> Traced { get; } = [];

    void Trace(GraphRuntime g, PhysOp op, Batch? b)
    {
        if (b is null || TraceGraphs is null || !TraceGraphs.Contains(g.Name)) return;
        if (!Traced.TryGetValue(g.Name, out var byOp)) Traced[g.Name] = byOp = [];
        if (!byOp.TryGetValue(op.Name, out var rows)) byOp[op.Name] = rows = new(StringComparer.Ordinal);
        var layout = op.OutLayout;
        for (var k = 0; k < b.Rows; k++)
        {
            var row = b.RowAt(k);
            var cells = new object?[layout.Count];
            for (var c = 0; c < layout.Count; c++)
            {
                var col = b.Cols[c];
                cells[c] = !col.Present(row) ? null : col.IsDec ? col.D[row] : layout.Interned?[c] == true ? Interner.Get((int)col.L[row]) : col.L[row];
            }
            var key = string.Join("\u001f", cells.Select(x => x switch { null => "\u0000", decimal d => "d" + Norm.Decimal(d), long l => "i" + l.ToString(System.Globalization.CultureInfo.InvariantCulture), var o => "t" + o }));
            var w = (rows.TryGetValue(key, out var had) ? had.Weight : 0) + b.Weight(row);
            if (w == 0) rows.Remove(key); else rows[key] = (w, cells);
        }
    }
    public Dictionary<string, long> OperatorAllocations { get; } = [];
    /// <summary>Milliseconds per operator, and "(commit)" per graph for the flush, patch apply and store commit, while TrackAllocations is on.</summary>
    public Dictionary<string, double> OperatorMillis { get; } = [];
    /// <summary>Milliseconds, allocated bytes and calls per operator kind (the physical operator type), while TrackAllocations is on.</summary>
    public Dictionary<string, (double Ms, long Bytes, long Calls)> KindStats { get; } = [];
    public long InputRows { get; private set; }
    public long Epochs => epochId;
    public long LogicalTime => lambda;

    public Engine(Catalog catalog, string spaceId, EngineOptions? options = null, Interner? interner = null)
    {
        Interner = interner ?? new Interner();
        this.catalog = catalog; this.options = options ?? new EngineOptions();
        var space = catalog.Spaces.TryGetValue(spaceId, out var s) ? s : throw new ArgumentException($"space {spaceId} is not activated");
        SpaceName = space.Name;
        store = this.options.StoreFactory(space.Name);
        Telemetry = this.options.TelemetryFactory();
        Identities = new IdentityCatalog(this.options.Tenant, Interner);
        // topological order of the graphs (S3)
        var active = space.Graphs.ToList();
        if (this.options.Only is { } only)
        {
            var need = new HashSet<string>(only);
            for (var grew = true; grew;) { grew = false; foreach (var g in active.Where(g => need.Contains(g.Name))) foreach (var up in g.Bindings.Values) grew |= active.Any(x => x.Name == up) && need.Add(up); }
            active = [.. active.Where(g => need.Contains(g.Name))];
        }
        var remaining = active.ToList(); var done = new HashSet<string>();
        while (remaining.Count > 0)
        {
            var next = remaining.First(g => g.Bindings.Where(kv => active.Any(x => x.Name == kv.Value)).All(kv => done.Contains(kv.Value)));
            remaining.Remove(next); done.Add(next.Name);
            var tp = next.Instance ?? catalog.Projections[next.ProjectionId];
            var key = new GraphKey(this.options.Tenant, next.Name);
            var outb = new OutputBuilder(Interner);
            var cx = new OpContext(Interner, Faults, Identities, this.options.Tenant, tp.OutputModule, key, outb) { LateHorizonFactor = this.options.LateHorizonWindows, RetentionNs = this.options.TelemetryRetentionNs };
            foreach (var series in tp.Ops.OfType<TSeries>().Where(x => x.WithinNs > this.options.TelemetryRetentionNs))
                throw new InvalidOperationException($"graph {next.Name}: {series.Name} reads {Lexer.DurationText(series.WithinNs)} back, and telemetry is retained for {Lexer.DurationText(this.options.TelemetryRetentionNs)} (Def. 7.13)");
            var gr = new GraphRuntime { Name = next.Name, Key = key, Plan = PlanCompiler.Compile(tp, catalog, cx), IsMaster = next.Master, Cx = cx, Out = outb };
            foreach (var (port, from) in next.Bindings) gr.PortSources[port] = from;
            graphs.Add(gr); byName[next.Name] = gr;
            gr.Head = store.Head(key).Head;
            if (store is HeadOnlyGraphStore headOnly) headOnly.Attach(key, gr.State);
            if (next.Master) foreach (var (port, from) in next.Bindings) sourcePort[from] = (next.Name, port);
        }
        // Def. 7.14: the sources that feed what a filling Series reads are fixed here
        foreach (var g in graphs)
            foreach (var series in g.Plan.Ops.OfType<SeriesOp>().Where(s => s.Fills))
            {
                var st = (TSeries)series.Typed;
                // the time to live is at most `within` (the checker), and `within` at most the retention (above): Def. 7.16 asks no more
                series.Feeding = [.. st.Reads.Select(r => Feeding(g.PortSources[st.GraphPort], r.PropId).ToArray())];
                if (st.Reads.Select((r, i) => (r, i)).FirstOrDefault(x => x.r.Fill is not null && series.Feeding[x.i].Length == 0) is { r: not null } unfed)
                    throw new InvalidOperationException($"graph {g.Name}: {st.Name} fills '{unfed.r.OutName}', and no source feeds that property: a window cannot be known to be covered (Def. 7.15)");
                // authored rows come when someone writes them: their source does not report, so nothing it feeds is ever covered
                var authored = catalog.Spaces[spaceId].Sources.Where(s => s.Adapter == "sattle.authoring").Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
                if (st.Reads.Select((r, i) => (r, i)).FirstOrDefault(x => x.r.Fill is not null && series.Feeding[x.i].Any(authored.Contains)) is { r: not null } written)
                    throw new InvalidOperationException($"graph {g.Name}: {st.Name} fills '{written.r.OutName}', which an authored source feeds ({string.Join(", ", series.Feeding[st.Reads.ToList().IndexOf(written.r)].Where(authored.Contains))}): authored data records no progress (Def. 7.15)");
            }
        Identities.OnMerge = (loser, survivor) =>
        {
            if (graphs.FirstOrDefault(g => g.IsMaster) is not { } master) return;
            var properties = catalog.OntologyOf(master.Typed.OutputModule).Props;
            foreach (var (from, to) in master.Out.Remap(loser, survivor))
            {
                master.Out.MoveTelemetry(Telemetry, master.Key, from, to);
                Telemetry.MoveElement(master.Key, from, to, prop => properties.TryGetValue(Interner.Get(prop), out var p) ? p.Combine : null);
            }
        };
    }

    public Layout RowLayout(string source)
    {
        var (g, port) = sourcePort.TryGetValue(source, out var sp) ? sp : throw new ArgumentException($"unknown source {source}");
        return byName[g].Plan.PortLayout(port);
    }
    public IEnumerable<string> Sources => sourcePort.Keys;
    public GraphRuntime Graph(string name) => byName[name];

    /// <summary>Ingests one batch of source rows (an ordered input). Deterministic: the same ordered inputs give the same graphs.</summary>
    public void Ingest(string source, Batch rows, long seq)
    {
        var (gname, port) = sourcePort[source];
        var ep = new Epoch { Id = ++epochId };
        ep.Rows[port] = rows;
        InputRows += rows.Rows;
        Run(ep, seq, source);
        _ = gname;
    }

    /// <summary>Def. 7.14: the sources that feed a telemetry property of a graph. From every sink that sets it, everything upstream: a rows port gives the source bound to it, a Series gives the sources that feed what it reads, a graph that is only matched gives none.</summary>
    SortedSet<string> Feeding(string graph, string propId)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        if (!byName.TryGetValue(graph, out var g)) return found;
        var plan = g.Typed; var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(TOp op)
        {
            if (!seen.Add(op.Name)) return;
            if (op is TSeries s) foreach (var read in s.Reads) found.UnionWith(Feeding(g.PortSources[s.GraphPort], read.PropId));
            foreach (var src in op.Inputs.Values)
                if (!src.IsPort) Walk(plan.Ops.First(o => o.Name == src.Name));
                else if (plan.Port(src.Name).Kind == "rows") found.Add(g.PortSources[src.Name]);
        }
        foreach (var sink in plan.Ops)
            if ((sink switch { TEmitEntity e => e.Sets, TEmitRel r => r.Sets, _ => null })?.Any(x => x.PropId == propId) == true) Walk(sink);
        return found;
    }

    public bool WantsProgress => graphs.Any(g => g.Plan.Ops.OfType<SeriesOp>().Any(s => s.Fills));

    /// <summary>How far each source has reported (Def. 7.14): the greatest `until` of its progress records.</summary>
    readonly Dictionary<string, long> covered = new(StringComparer.Ordinal);

    /// <summary>The progress of a source (Def. 10.9): it has reported up to `untilNs`, the capture time of an envelope less the allowed lateness. A recorded input like a row: a window that ends at or before it is covered by this source. The graphs step only when a window of a filling Series becomes covered.</summary>
    public void Progress(string source, long untilNs, long seq)
    {
        if (covered.TryGetValue(source, out var had) && untilNs <= had) return;
        covered[source] = untilNs;
        if (!graphs.Any(g => g.Plan.Ops.OfType<SeriesOp>().Any(s => s.CoverageMoved(lambda, covered)))) return;
        Run(new Epoch { Id = ++epochId, Progress = true }, seq, "progress");
    }

    /// <summary>A source reported lost events at an observed time: the master telemetry window containing it becomes Partial(sourceLoss).</summary>
    public void RecordLoss(long timeNs, long count, string reason = "sourceLoss")
    {
        var periods = Master.Typed.Ops.SelectMany(o => o switch { TEmitEntity e => e.Sets, TEmitRel r => r.Sets, _ => [] }).Where(x => x.Telemetry).Select(x => x.Period).ToList();
        var period = periods.Count == 0 ? 60_000_000_000 : periods.Min();
        var ws = timeNs - ((timeNs % period) + period) % period;
        Telemetry.MarkPartial(Master.Key, ws, reason, count);
        // a window with a loss mark is not covered (Def. 7.14): the graphs that fill take their fills of it back
        if (WantsProgress) Run(new Epoch { Id = ++epochId, Progress = true, Loss = ws }, 0, "loss");
    }

    /// <summary>Explicit logical-time control: closes windows whose end is at or before the frontier (ADR-048).</summary>
    public void Advance(long lambdaNs, long seq)
    {
        if (lambdaNs <= lambda) return;
        lambda = lambdaNs;
        var ep = new Epoch { Id = ++epochId, HasAdvance = true, Lambda = lambdaNs };
        Run(ep, seq, "advance");
    }

    /// <summary>Epochs in which two identities merged, and the most merges one epoch had (Law 7.5 item 3 combines their telemetry at the sink).</summary>
    public long EpochsWithMerge { get; private set; }
    public long MostMergesInOneEpoch { get; private set; }

    void Run(Epoch master, long seq, string cause)
    {
        var mergesBefore = Identities.Merges;
        try { RunEpoch(master, seq, cause); }
        finally
        {
            var merged = Identities.Merges - mergesBefore;
            if (merged > 0) { EpochsWithMerge++; MostMergesInOneEpoch = Math.Max(MostMergesInOneEpoch, merged); }
        }
    }

    void RunEpoch(Epoch master, long seq, string cause)
    {
        var results = new Dictionary<string, GraphResult>();
        foreach (var g in graphs)
        {
            Epoch ep;
            var filling = master.Progress && g.Plan.Ops.OfType<SeriesOp>().Any(s => s.Fills);
            if (master.Progress && g.IsMaster && !filling) continue;       // the progress of a source concerns the graphs that fill
            if (g.IsMaster)
            {
                ep = new Epoch { Id = master.Id, HasAdvance = master.HasAdvance, Lambda = master.Lambda, Progress = master.Progress, Covered = covered, Loss = master.Loss };
                foreach (var (port, b) in master.Rows) ep.Rows[port] = b;
            }
            else
            {
                ep = new Epoch { Id = master.Id, HasAdvance = master.HasAdvance, Lambda = master.Lambda, Progress = master.Progress, Covered = covered, Loss = master.Loss };
                var any = master.HasAdvance || filling;
                foreach (var (port, from) in g.PortSources)
                    if (results.TryGetValue(from, out var r))
                    {
                        ep.Graphs[port] = new GraphDelta(r.Patch, byName[from].State, r.Telemetry);
                        any |= r.Patch is not null || r.Telemetry.Count > 0;
                    }
                if (!any) continue;
            }
            results[g.Name] = Execute(g, ep, seq, cause);
        }
    }

    GraphResult Execute(GraphRuntime g, Epoch ep, long seq, string cause)
    {
        var outputs = new Dictionary<string, Batch?>();
        foreach (var op in g.Plan.Ops)
        {
            var inputs = new Dictionary<string, Batch?>();
            foreach (var (port, src) in op.Typed.Inputs)
                inputs[port] = src.IsPort ? ep.Rows.GetValueOrDefault(src.Name) : outputs.GetValueOrDefault(src.Name);
            if (TrackAllocations)
            {
                var a0 = GC.GetAllocatedBytesForCurrentThread(); var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                outputs[op.Name] = op.Run(g.Cx, ep, inputs);
                Trace(g, op, outputs[op.Name]);
                OperatorAllocations[g.Name + "/" + op.Name] = OperatorAllocations.GetValueOrDefault(g.Name + "/" + op.Name) + GC.GetAllocatedBytesForCurrentThread() - a0;
                OperatorMillis[g.Name + "/" + op.Name] = OperatorMillis.GetValueOrDefault(g.Name + "/" + op.Name) + System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                var kd = op.GetType().Name; var ks = KindStats.GetValueOrDefault(kd);
                KindStats[kd] = (ks.Ms + System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds, ks.Bytes + GC.GetAllocatedBytesForCurrentThread() - a0, ks.Calls + 1);
            }
            else { outputs[op.Name] = op.Run(g.Cx, ep, inputs); Trace(g, op, outputs[op.Name]); }
        }
        var tCommit = System.Diagnostics.Stopwatch.GetTimestamp();
        var edits = g.Out.Flush(g.State);
        var samples = g.Out.FlushTelemetry(Telemetry, g.Key, ep.Id, 0);
        Patch? patch = null;
        if (edits.Count > 0)
        {
            patch = new Patch(g.Name, g.Head, g.Head + 1, edits, new Cause([cause + "#" + seq], catalog.Log.Head.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var err = g.State.Apply(patch.Edits);
            if (err is not null) throw new InvalidOperationException($"graph {g.Name}: engine produced an inapplicable patch: {err}");
            var gin = ImmutableSortedDictionary.CreateBuilder<string, long>(StringComparer.Ordinal);
            foreach (var (_, from) in g.PortSources) if (byName.TryGetValue(from, out var up)) gin[from] = up.Head;
            var pos = ImmutableSortedDictionary<string, long>.Empty.Add(cause, seq);
            var fr = new Frontier(pos, gin.ToImmutable(), catalog.Log.Head);
            var bindings = g.IsMaster ? [.. Identities.Pending] : new List<IdentityBinding>();
            if (g.IsMaster) Identities.Pending.Clear();
            var res = store.Commit(new CommitBatch(g.Key, g.Head, [patch], [fr], bindings));
            if (res is not CommitResult.Committed c) throw new InvalidOperationException($"graph {g.Name}: store did not commit: {res}");
            g.Head = c.Head;
        }
        if (TrackAllocations) OperatorMillis[g.Name + "/(commit)"] = OperatorMillis.GetValueOrDefault(g.Name + "/(commit)") + System.Diagnostics.Stopwatch.GetElapsedTime(tCommit).TotalMilliseconds;
        if (TrackAllocations) { var ks = KindStats.GetValueOrDefault("(flush, apply, store commit)"); KindStats["(flush, apply, store commit)"] = (ks.Ms + System.Diagnostics.Stopwatch.GetElapsedTime(tCommit).TotalMilliseconds, 0, ks.Calls + 1); }
        IReadOnlyList<WindowChange> changes = [];
        if (samples.Count > 0)
        {
            Telemetry.Append(g.Key, [.. samples]);
            changes = Telemetry.ClosedWindows(g.Key, g.TelemetryPosition, out var np);
            g.TelemetryPosition = np;
        }
        else if (g.TelemetryPosition == 0) { _ = Telemetry.ClosedWindows(g.Key, 0, out var np); g.TelemetryPosition = np; }
        if (ep.HasAdvance) Telemetry.Retain(g.Key, ep.Lambda, options.TelemetryRetentionNs);
        var result = new GraphResult(patch, changes, g.Head);
        var conflicts = g.Out.TakeChanged();
        if (patch is not null || changes.Count > 0 || conflicts.Count > 0) Committed?.Invoke(new CommitEvent(g.Name, patch, changes, ep.Lambda, seq, Interner) { Conflicts = conflicts });
        return result;
    }

    /// <summary>A digest of the observable output: every graph's digest, then every telemetry window in canonical order. Same ordered inputs give the same digest (T7).</summary>
    public string StateDigest()
    {
        var sb = new StringBuilder();
        foreach (var g in graphs.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            sb.Append(g.Name).Append('=').Append(g.State.Digest()).Append('\n');
            if (Telemetry is InMemoryTelemetryStore mem)
                foreach (var (e, p, ws, v) in mem.Dump(g.Key).OrderBy(x => Interner.Get(x.Element), StringComparer.Ordinal).ThenBy(x => Interner.Get(x.Prop), StringComparer.Ordinal).ThenBy(x => x.Ws))
                {
                    sb.Append(Interner.Get(e)).Append('|').Append(Interner.Get(p)).Append('|').Append(ws).Append('|').Append(Norm.Decimal(v));
                    if (mem.AuxOf(g.Key, new WindowKey(e, p, ws)) is { } aux) sb.Append('|').Append(Norm.Decimal(aux.A)).Append('|').Append(Norm.Decimal(aux.B));          // only windows of `mean` and `last` with their pair
                    sb.Append('\n');
                }
        }
        return Digests.Of(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))));
    }

    public void Advance(long lambdaNs) => Advance(lambdaNs, 0);

    // ---- state snapshot (seals) ----

    /// <summary>The state of one graph: its hot elements, output layers, counters and the state of every operator of its plan.</summary>
    public sealed class GraphStateRoot
    {
        public string Name = ""; public long Head, TelemetryPosition, Skipped;
        public Graph State = null!; public OutputBuilder Out = null!; public OpContext Cx = null!; public List<PhysOp> Ops = [];
    }
    public sealed class StateRoot
    {
        public Interner Interner = null!; public RuntimeFaults Faults = null!; public IdentityCatalog Identities = null!; public ITelemetryStore Telemetry = null!;
        public long EpochId, Lambda, InputRows; public List<GraphStateRoot> Graphs = [];
        public Dictionary<string, long> Covered = null!;
    }

    /// <summary>The objects that hold this engine's state, for a snapshot. Live objects, not a copy.</summary>
    public StateRoot StateRootOf() => new()
    {
        Interner = Interner, Faults = Faults, Identities = Identities, Telemetry = Telemetry, EpochId = epochId, Lambda = lambda, InputRows = InputRows, Covered = covered,
        Graphs = [.. graphs.Select(g => new GraphStateRoot { Name = g.Name, Head = g.Head, TelemetryPosition = g.TelemetryPosition, Skipped = g.Skipped, State = g.State, Out = g.Out, Cx = g.Cx, Ops = [.. g.Plan.Ops] })],
    };

    /// <summary>After a snapshot was loaded into `StateRootOf()` of this (freshly built) engine: the scalars the root carried go back to the engine.</summary>
    public void AdoptState(StateRoot root)
    {
        epochId = root.EpochId; lambda = root.Lambda; InputRows = root.InputRows;
        foreach (var gs in root.Graphs)
        {
            var g = byName[gs.Name]; g.Head = gs.Head; g.TelemetryPosition = gs.TelemetryPosition; g.Skipped = gs.Skipped;
            if (store is HeadOnlyGraphStore headOnly) { headOnly.Resume(g.Key, g.Head); headOnly.Attach(g.Key, g.State); }          // the store's history starts at the restored head
        }
    }
    public void Dispose() => store.Dispose();
}
