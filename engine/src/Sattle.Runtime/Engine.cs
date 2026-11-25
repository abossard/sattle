using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

public sealed record EngineOptions
{
    public string Tenant { get; init; } = "lab";
    int? preparationWorkers;
    /// <summary>Maximum threads preparing large matches against a committed graph. One runs the serial plan. A host may set SATTLE_PREPARATION_WORKERS to choose its default.</summary>
    public int PreparationWorkers { get => preparationWorkers ?? MatchPreparation.DefaultWorkers(); init => preparationWorkers = value; }
    /// <summary>The store the engine commits its patches to. The default keeps the head number of each graph only (`HeadOnlyGraphStore`); a durable store keeps the history.</summary>
    public Func<string, IGraphStore> StoreFactory { get; init; } = _ => new HeadOnlyGraphStore();
    public Func<IWindowStore> WindowStoreFactory { get; init; } = () => new InMemoryWindowStore();
    /// <summary>The declared retention of every series of the space (Def. 7.13): a closed window is kept while its end is after the logical frontier minus this age. Until the definitions declare it per series it is one age for the space. A lateness or a look-back longer than this is refused.</summary>
    public long WindowRetentionNs { get; init; } = 48L * 3_600_000_000_000;
    /// <summary>When set, only these graphs and the graphs they derive from are activated (a staged evaluation of one view needs no other graph).</summary>
    public IReadOnlySet<string>? Only { get; init; }
    /// <summary>Graphs whose operator outputs are integrated for inspection (a draft preview, `include: rows`): per graph and operator, the rows the operator holds now with their summed weights. A traced graph runs its plan as written. Null: nothing is traced.</summary>
    public IReadOnlySet<string>? TraceGraphs { get; init; }
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

    /// <summary>Element-valued columns of a schema that come from a Match or a Traverse, each with the input whose graph holds the element. `graphOf` says that for a variable of a pattern: a reference (Version 3.28) is an entity of another graph, and no source when no input of the projection holds its type.</summary>
    static (string Role, int Col, bool IsRel, string Port)[] ElementCols(TypedProjection tp, Schema schema, Func<TMatch, string, string?> graphOf)
    {
        var res = new List<(string, int, bool, string)>();
        for (var i = 0; i < schema.Cols.Count; i++)
        {
            var c = schema.Cols[i];
            if (c.Origin is null || c.T.Ty.Tag is not (Tag.Ent or Tag.Rel)) continue;
            var op = c.Origin.Split(':')[0];
            if (tp.Ops.FirstOrDefault(o => o.Name == op) is TMatch m) { if (graphOf(m, c.Origin[(op.Length + 1)..]) is { } port) res.Add((c.Name, i, c.T.Ty.Tag == Tag.Rel, port)); }
            else if (tp.Ops.FirstOrDefault(o => o.Name == op) is TTraverse tv) res.Add((c.Name, i, false, tv.GraphPort));
        }
        return [.. res];
    }

    /// <summary>What the rows of a step say about where they come from: the columns that hold the elements a row was made of, and the `Group` (without windows) a row is a result of, with the columns of its key. A sink writes both down as the lineage of what it emits, a `Group` the first as its members. `Col` is a position in the step's batch; a position after the step's own columns is one a `Map` passes on without naming it.</summary>
    sealed record Sources((string Role, int Col, bool IsRel, string Port)[] Cols, (string Group, int[] KeyCols)? Group);

    /// <summary>The sources of a step's rows, from those of its input (a calculation over the plan). A `Filter` keeps its input's rows, so it keeps their sources. A `Map` makes one row of each input row: a source column it does not hand on by name is carried behind its own columns (`Carry`: the positions in its input). A `Group` starts over: its rows are results of the group. Every other step says what its own columns say.</summary>
    static (Sources Out, int[] Carry) SourcesOf(TypedProjection tp, TOp t, Schema? inSchema, Sources? input, Func<TMatch, string, string?> graphOf)
    {
        var own = ElementCols(tp, t.Out, graphOf);
        switch (t)
        {
            case TGroup { ClosesWindows: false } g:
                var keys = g.Keys.Select(k => g.Out.Cols.ToList().FindIndex(c => c.Name == k.Name)).ToArray();
                return (new(own, keys.Any(c => c < 0) ? null : (g.Name, keys)), []);
            case TFilter when input is not null: return (input, []);
            case TMap m when input is not null && inSchema is not null:
                var carry = new List<int>();
                // where the map's output has an input column: under the name of the assign that passes it on, or else carried
                int Named(int col) => col >= inSchema.Cols.Count ? -1 : m.Out.Cols.ToList().FindIndex(c => m.Assigns.Any(a => a.Name == c.Name && a.Value is TField f && f.Name == inSchema.Cols[col].Name));
                int Carried(int col) { var at = carry.IndexOf(col); if (at < 0) { at = carry.Count; carry.Add(col); } return m.Out.Cols.Count + at; }
                var cols = own.Concat(input.Cols.Where(c => Named(c.Col) < 0).Select(c => (c.Role, Carried(c.Col), c.IsRel, c.Port))).ToArray();
                (string, int[])? group = input.Group is { } gr ? (gr.Group, [.. gr.KeyCols.Select(c => Named(c) is >= 0 and var at ? at : Carried(c))]) : null;
                return (new(cols, group), [.. carry]);
            default: return (new(own, null), []);
        }
    }

    /// <summary>
    /// The plan as run, from the plan as written: a join whose one input is a traverse, directly or through one map, and whose keys on that
    /// side read only the traverse's start rows, is moved below the traverse. Rule: for start rows S, a traverse T, a map M and rows R,
    /// Join(M(T(S)), R) on k(s) = k'(r) is M'(T(Join(S, R) on k(s) = k'(r))), where k reads M's columns through M's assignments and M' is M
    /// with R's columns carried through in the join's column order. It holds row for row and weight for weight: T emits, per start row and per
    /// element the row's start element reaches, the start row's columns with that element, so a key of the start row joins the same R rows
    /// before the walk as after it, and a start row that joins two R rows is walked once per row, as its reach was joined twice. Incrementally
    /// an R row that comes is a start row that comes, walked as any other, and one that goes takes its walk with it. A traverse keeps the walks
    /// of the start rows that join, not of every start row. Applied when the traverse and the map have no other reader and no column of R is
    /// named as a column of the traverse's output; the operators keep their names, and the join of the start rows is named after the join.
    /// A traced preview shows the plan as written; the rewrite applies only to untraced runs.
    /// </summary>
    internal static TypedProjection JoinsBelowTraverses(TypedProjection tp)
    {
        var ops = tp.Ops.ToList();
        int Readers(string name) => ops.Sum(o => o.Inputs.Values.Count(s => !s.IsPort && s.Name == name));
        TOp? Op(Src s) => s.IsPort ? null : ops.FirstOrDefault(o => o.Name == s.Name);
        Schema Rows(Src s) => s.IsPort ? tp.Port(s.Name).Rows! : Op(s)!.Out;
        var changed = false;
        foreach (var j in tp.Ops.OfType<TJoin>())
            foreach (var (side, other) in (ReadOnlySpan<(string, string)>)[("left", "right"), ("right", "left")])
            {
                var x = Op(j.Inputs[side]);
                var m = x as TMap;
                if ((m is null ? x : Op(m.Inputs["in"])) is not TTraverse t || Readers(t.Name) != 1 || (m is not null && Readers(m.Name) != 1)) continue;
                var r = Rows(j.Inputs[other]);
                var walked = t.Out.Cols.Count - 1 - t.Reads.Count;     // the start rows' columns come first, then the reached element and its reads
                var starts = t.Out.Cols.Take(walked).ToList();
                if (r.Cols.Any(c => t.Out.Has(c.Name))) continue;
                TExpr Through(TExpr e) => e switch
                {
                    TField f when m is not null => m.Assigns.First(a => a.Name == f.Name).Value,
                    TCall c => c with { Args = [.. c.Args.Select(Through)] },
                    TId i => i with { Comps = [.. i.Comps.Select(c => c with { Value = Through(c.Value) })] },
                    _ => e,
                };
                static void Fields(TExpr e, HashSet<string> into) { switch (e) { case TField f: into.Add(f.Name); break; case TCall c: foreach (var a in c.Args) Fields(a, into); break; case TId i: foreach (var c in i.Comps) Fields(c.Value, into); break; } }
                var keys = j.Keys.Select(k => side == "left" ? (Through(k.Left), k.Right) : (k.Left, Through(k.Right))).ToList();
                var read = new HashSet<string>(); foreach (var k in keys) Fields(side == "left" ? k.Item1 : k.Item2, read);
                if (!read.IsSubsetOf(starts.Select(c => c.Name))) continue;
                var startsJoined = new TJoin(j.Name + ".starts", j.Id, new Dictionary<string, Src> { [side] = t.Inputs["in"], [other] = j.Inputs[other] }, new Schema(side == "left" ? [.. starts, .. r.Cols] : [.. r.Cols, .. starts]), keys);
                var walk = t with { Inputs = new Dictionary<string, Src>(t.Inputs) { ["in"] = new(false, startsJoined.Name) }, Out = new Schema([.. startsJoined.Out.Cols, .. t.Out.Cols.Skip(walked)]) };
                IEnumerable<(string, TExpr)> Carry(IEnumerable<Col> cols) => cols.Select(c => (c.Name, (TExpr)new TField(c.Name, c.T)));
                var own = m is null ? Carry(t.Out.Cols) : m.Assigns;
                var map = new TMap(j.Name, m?.Id ?? j.Id, new Dictionary<string, Src> { ["in"] = new(false, walk.Name) }, j.Out, side == "left" ? [.. own, .. Carry(r.Cols)] : [.. Carry(r.Cols), .. own]);
                var at = ops.IndexOf(j);
                ops[at] = map; ops.Insert(at, walk); ops.Insert(at, startsJoined);
                ops.Remove(t); if (m is not null) ops.Remove(m);
                changed = true;
                break;
            }
        return changed ? new TypedProjection { Id = tp.Id, Name = tp.Name, Module = tp.Module, Purpose = tp.Purpose, Forward = tp.Forward, Retain = tp.Retain, OutputModule = tp.OutputModule, Ports = tp.Ports, Ops = ops, Rules = tp.Rules, Params = tp.Params } : tp;
    }

    /// <summary>`held`: per graph port, the entity types the graph bound to it holds (`TypedProjection.EntityTypes` of the projection that produces it). `traced`: the graph's operators are traced, so its plan runs as written.</summary>
    public static CompiledProjection Compile(TypedProjection tp, Catalog catalog, OpContext cx, IReadOnlyDictionary<string, IReadOnlySet<string>> held, bool traced)
    {
        if (!traced) tp = JoinsBelowTraverses(tp);
        var ops = new List<PhysOp>(); var byName = new Dictionary<string, PhysOp>();
        var joinInputs = new JoinInputs();
        var strategies = new List<string>();
        Layout InputLayout(TOp t, string port)
        {
            var src = t.Inputs[port];
            return src.IsPort ? Layout.Of(tp.Port(src.Name).Rows!) : byName[src.Name].OutLayout;
        }
        IdentityCatalog cat = cx.Ids;
        var sink = 0;
        var sources = new Dictionary<string, Sources>();
        var references = tp.Ops.OfType<TMatch>().ToDictionary(m => m.Name, m => m.References(held[m.GraphPort], catalog.OntologyOf(tp.Port(m.GraphPort).ExpectsModule!).IsSubtype));
        // the input whose graph holds what a pattern variable binds: the pattern's own, and for a reference the first other input whose graph holds its type
        string? GraphOf(TMatch m, string v) => !references[m.Name].Contains(v) ? m.GraphPort
            : tp.Ports.FirstOrDefault(p => p.Name != m.GraphPort && held.TryGetValue(p.Name, out var has) && has.Any(t => catalog.OntologyOf(p.ExpectsModule!).IsSubtype(t, m.Nodes.First(n => n.Var == v).TypeId)))?.Name;
        foreach (var t in tp.Ops)
        {
            Layout inL = t.Inputs.ContainsKey("in") ? InputLayout(t, "in") : new Layout([], []);
            var inSchema = !t.Inputs.TryGetValue("in", out var inSrc) ? null : inSrc.IsPort ? tp.Port(inSrc.Name).Rows : tp.Ops.First(o => o.Name == inSrc.Name).Out;
            var from = inSrc is null ? null : inSrc.IsPort ? (inSchema is null ? null : new Sources(ElementCols(tp, inSchema, GraphOf), null)) : sources[inSrc.Name];
            (sources[t.Name], var carry) = SourcesOf(tp, t, inSchema, from, GraphOf);
            VExpr Compile(TExpr e)
            {
                VExpr IdC(TId id)
                {
                    var dom = cat.Domain(id.DomainId, id.DomainName, [.. id.Comps.Select(c => c.CompName)], [.. id.Comps.Select(c => c.Canonicalizer)], id.Type.Ty.TypeId);
                    return new VId(dom, [.. id.Comps.Select(c => ExprCompiler.Compile(c.Value, inL, cx.It, cx.Faults, IdC))], [.. id.Comps.Select(c => c.Value.Type.Ty)], cat, cx.It, cx.Faults);
                }
                return ExprCompiler.Compile(e, inL, cx.It, cx.Faults, IdC);
            }
            VId MakeId(TId id)
            {
                var dom = cat.Domain(id.DomainId, id.DomainName, [.. id.Comps.Select(c => c.CompName)], [.. id.Comps.Select(c => c.Canonicalizer)], id.Type.Ty.TypeId);
                return new VId(dom, [.. id.Comps.Select(c => Compile(c.Value))], [.. id.Comps.Select(c => c.Value.Type.Ty)], cat, cx.It, cx.Faults);
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
                        var mo = new MatchOp(m, view, references[m.Name]); mo.Bind(cx.It); op = mo;
                        strategies.Add($"{m.Name}: Match, bilinear delta against the input graph (new before the seed component, old after it)");
                        break;
                    }
                case TTraverse tr: op = new TraverseOp(tr, inL, catalog.OntologyOf(tp.Port(tr.GraphPort).ExpectsModule!), cx.It); strategies.Add($"{tr.Name}: Traverse, per start element the depth each element was first reached at, over {tr.Direction} edges to depth {tr.Max}; an added edge lowers depths from its far end for the starts that reached its near end, a lost edge walks its starts again, an edited entity touches the starts that visited it, and only the difference is emitted"); break;
                case TValues v: op = new ValuesOp(v, cx.It); strategies.Add($"{v.Name}: Values, the constant rows are emitted once, in the first evaluation of the graph"); break;
                case TJoin j: op = new JoinOp(j, InputLayout(j, "left"), InputLayout(j, "right"), cx, joinInputs); strategies.Add($"{j.Name}: Join, hash join on {j.Keys.Count} equality key(s), bilinear delta against the rows before the step: new left against old right, new right against old left, new against new; an input read by several joins is stored once"); break;
                case TFixpoint fx: op = new FixpointOp(fx, InputLayout(fx, "in"), InputLayout(fx, "edges"), cx.It); strategies.Add($"{fx.Name}: Fixpoint, per node the net weight of its own values and of its edges; a step evaluates only the nodes that reach a changed node, from no value up, each once and again when a dependency grows, and emits the nodes whose value changed"); break;
                case TUnion u: op = new UnionOp(u, InputLayout(u, "in0"), InputLayout(u, "in1")); strategies.Add($"{u.Name}: Union, linear: both inputs' signed rows concatenated (weights add)"); break;
                case TDistinct ds: op = new DistinctOp(ds, inL); strategies.Add($"{ds.Name}: Distinct, per-row multiplicity counts; emits +1 when a row first appears and -1 when its last copy goes"); break;
                case TFilter f: op = new FilterOp(f, inL, cx); strategies.Add($"{f.Name}: Filter, linear, selection vector"); break;
                case TMap m: op = new MapOp(m, inL, cx, carry); strategies.Add($"{m.Name}: Map, linear, shared columns"); break;
                case TRetain rt: op = new RetainOp(rt, inL); strategies.Add($"{rt.Name}: Retain, buffers rows by {rt.TimeField}; withdraws a row when the frontier passes its time plus {Norm.DurationText(rt.AgeNs)}"); break;
                case TWindow w: op = new WindowOp(w, inL, cx); strategies.Add($"{w.Name}: Window, linear, window assignment"); break;
                case TGroup g when g.ClosesWindows:
                    {
                        var (width, _, lambda) = tp.WindowsOf(g);
                        op = new WindowedGroupOp(g, width, lambda!.Value, inL, cx, withCompanions);
                        strategies.Add($"{g.Name}: Group over windows, per-window hash accumulators, windows close on the logical frontier, a late row revises its window");
                        break;
                    }
                case TGroup g: op = new GroupOp(g, inL, cx, from?.Cols, withCompanions); strategies.Add($"{g.Name}: Group, per-key accumulators updated by signed weights"); break;
                case TSeries s: op = new SeriesOp(s, inL, cx.It); strategies.Add($"{s.Name}: Series, bilinear: new rows against seen windows, new windows against all rows, retraction at the look-back"); break;
                case TRollup r: op = new RollupOp(r, inL, cx, withCompanions); strategies.Add($"{r.Name}: Rollup, per (key, window) accumulators, decomposable sum"); break;
                case TFold fo:
                    {
                        var (width, hop, lambda) = tp.WindowsOf(fo);
                        op = new FoldOp(fo, inL, InputLayout(fo, "policy"), cx, width, hop, lambda!.Value);
                        strategies.Add($"{fo.Name}: Fold, per key a state over its windows in order, selected when the clock reaches a window's close; a change at a closed window steps the key again from that window");
                        break;
                    }
                case TEmitEntity e: op = new EmitEntityOp(e, sink++, inL, cx, Compile, from?.Cols ?? [], from?.Group); strategies.Add($"{e.Name}: EmitEntity {(e.Update ? "update" : "create")}, support-counted element layer"); break;
                case TEmitRel e: op = new EmitRelOp(e, sink++, inL, cx, Compile, from?.Cols ?? [], from?.Group); strategies.Add($"{e.Name}: EmitRelationship {(e.Update ? "update" : "create")}, support-counted element layer"); break;
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
        var bound = "Series and Rollup state is bounded by look-back / period windows per key; Group and Fold by open windows plus the closed windows within the lateness the program declares; Match, Join-free, holds no state beyond the input graph.";
        return new CompiledProjection { Typed = tp, Ops = ops, Info = new PlanInfo(tp.Name, digest, strategies, chains, bound) };
    }
}

public sealed record GraphResult(Patch? Patch, IReadOnlyList<WindowChange> Windows, long Revision);
/// <summary>`Conflicts`: the conflicts of Def. 6.4 that appeared, changed or ended with this commit (an ended one has kind `resolved`).</summary>
public sealed record CommitEvent(string GraphId, Patch? Patch, IReadOnlyList<WindowChange> Windows, long Lambda, long Seq, Interner Interner)
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
    /// <summary>Produced by an ingestion projection: source rows reach it. The master ingests; another graph may too (authored rows), and then writes no windows.</summary>
    public required bool Ingests { get; init; }
    public required OpContext Cx { get; init; }
    public required OutputBuilder Out { get; init; }
    /// <summary>The elements of the graph; its texts are codes of the engine's interner.</summary>
    public required Graph State { get; init; }
    public long Head { get; set; }
    public long WindowPosition { get; set; }
    public Dictionary<string, string> PortSources { get; } = []; // port -> source or upstream graph name
    public long Skipped { get; set; }
    public TypedProjection Typed => Plan.Typed;
    /// <summary>The working state of a step, reused by every step of the graph: the outputs by operator, and per operator (in plan order) its inputs by port. Holds batches only while a step runs.</summary>
    internal Dictionary<string, Batch?> StepOutputs { get; } = [];
    internal Dictionary<string, Batch?>[] StepInputs => stepInputs ??= [.. Plan.Ops.Select(_ => new Dictionary<string, Batch?>())];
    Dictionary<string, Batch?>[]? stepInputs;
    /// <summary>Per operator (in plan order) its ports and what feeds each: the plan's inputs as an array, which a step walks without an enumerator object.</summary>
    internal (string Port, Src Src)[][] StepPorts => stepPorts ??= [.. Plan.Ops.Select(o => o.Typed.Inputs.Select(kv => (kv.Key, kv.Value)).ToArray())];
    (string Port, Src Src)[][]? stepPorts;
    /// <summary>"graph/operator" per operator in plan order, then "graph/(commit)": the keys of the measurements while TrackAllocations is on.</summary>
    internal string[] MeasureKeys => measureKeys ??= [.. Plan.Ops.Select(o => Name + "/" + o.Name), Name + "/(commit)"];
    string[]? measureKeys;
}

/// <summary>The counters of a run (`Engine.Counters`).</summary>
public readonly record struct RunCounters(long Minted, long Aliased, long Merged, long Conflicts, long LateRows, long DivisionByZero, long Overflow, long InvalidParameter);

/// <summary>The activated graph space: master ingestion, derived graphs in dependency order, ordered deterministic evaluation with explicit logical time, per-graph atomic commit through the port.</summary>
public sealed class Engine : IDisposable, IEngineInput
{
    internal long ConfigurationRevision { get; set; }
    readonly EngineOptions options;
    readonly IGraphStore store;
    readonly Dictionary<string, GraphRuntime> byName = [];
    readonly List<GraphRuntime> graphs = [];
    readonly Dictionary<string, (string GraphId, string Port)> sourcePort = new(StringComparer.Ordinal);
    long epochId;
    long lambda = long.MinValue;

    public Interner Interner { get; }
    public RuntimeFaults Faults { get; } = new();
    public IdentityCatalog Identities { get; }
    public IWindowStore Windows { get; }
    public IReadOnlyList<GraphRuntime> Graphs => graphs;

    public GraphRuntime Master => graphs.First(g => g.IsMaster);
    public string SpaceName { get; }
    public string Tenant => options.Tenant;
    public IGraphStore Store => store;
    public event Action<CommitEvent>? Committed;
    /// <summary>Diagnostic: bytes allocated per operator, accumulated over all steps (PERF.md method).</summary>
    public bool TrackAllocations { get; set; }

    /// <summary>The traced rows, by graph name and operator name: each row by its cells (null absent, decimal, a text or element id, or the raw integer of an Integer, Instant or Boolean) with its weight.</summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, (long Weight, object?[] Cells)>>> Traced { get; } = [];

    void Trace(GraphRuntime g, PhysOp op, Batch? b)
    {
        if (b is null || options.TraceGraphs is not { } traced || !traced.Contains(g.Name)) return;
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
    readonly MatchPreparation preparation;
    /// <summary>Completed parallel match ranges, including any run on the writer.</summary>
    public long PreparedRanges => preparation.Ranges;
    /// <summary>Completed match ranges run off the engine writer.</summary>
    public long WorkerPreparedRanges => preparation.WorkerRanges;
    /// <summary>Ranges replayed directly into final output after reaching their private row buffer limit.</summary>
    public long ReplayedPreparationRanges => preparation.ReplayedRanges;
    public long InputRows { get; private set; }
    public long Epochs => epochId;
    public long LogicalTime => lambda;

    public Engine(Catalog catalog, string spaceId, EngineOptions? options = null, Interner? interner = null)
    {
        Interner = interner ?? new Interner();
        ConfigurationRevision = catalog.Log.Head; this.options = options ?? new EngineOptions();
        preparation = new(this.options.PreparationWorkers);
        var space = catalog.Spaces.TryGetValue(spaceId, out var s) ? s : throw new ArgumentException($"space {spaceId} is not activated");
        SpaceName = space.Name;
        store = this.options.StoreFactory(space.Name);
        Windows = this.options.WindowStoreFactory();
        Identities = new IdentityCatalog(this.options.Tenant, Interner);
        // topological order of the graphs (S3)
        var active = space.Graphs.ToList();
        if (this.options.Only is { } only)
        {
            // every graph that ingests stays: the rows of each source have a graph to go to
            var need = new HashSet<string>(only.Concat(active.Where(g => (g.Instance ?? catalog.Projections[g.ProjectionId]).Purpose == "ingestion").Select(g => g.Name)));
            for (var grew = true; grew;) { grew = false; foreach (var g in active.Where(g => need.Contains(g.Name))) foreach (var up in g.Bindings.Values) grew |= active.Any(x => x.Name == up) && need.Add(up); }
            active = [.. active.Where(g => need.Contains(g.Name))];
        }
        var remaining = active.OrderBy(g => g.Master ? 0 : 1).ToList(); var done = new HashSet<string>();     // the master first: the other graphs that ingest step after it
        while (remaining.Count > 0)
        {
            var next = remaining.First(g => g.Bindings.Where(kv => active.Any(x => x.Name == kv.Value)).All(kv => done.Contains(kv.Value)));
            remaining.Remove(next); done.Add(next.Name);
            TypedProjection ProjectionOf(TGraph g) => g.Instance ?? catalog.Projections[g.ProjectionId];
            var tp = ProjectionOf(next);
            var key = new GraphKey(this.options.Tenant, next.Name);
            var outb = new OutputBuilder(Interner);
            var cx = new OpContext(Interner, Faults, Identities, this.options.Tenant, tp.OutputModule, key, outb);
            foreach (var series in tp.Ops.OfType<TSeries>().Where(x => x.WithinNs > this.options.WindowRetentionNs))
                throw new InvalidOperationException($"graph {next.Name}: {series.Name} reads {Norm.DurationText(series.WithinNs)} back, and windows are retained for {Norm.DurationText(this.options.WindowRetentionNs)} (Def. 7.13)");
            foreach (var op in tp.Ops.Where(x => x is TFold or TGroup { ClosesWindows: true }))
                if (tp.WindowsOf(op).Lateness is { } late && late > this.options.WindowRetentionNs)
                    throw new InvalidOperationException($"graph {next.Name}: {op.Name} keeps a window revisable for {Norm.DurationText(late)} after its end, and windows are retained for {Norm.DurationText(this.options.WindowRetentionNs)} (Def. 7.13)");
            var gr = new GraphRuntime { Name = next.Name, Key = key, Plan = PlanCompiler.Compile(tp, catalog, cx, next.Bindings.Where(kv => active.Any(x => x.Name == kv.Value)).ToDictionary(kv => kv.Key, kv => ProjectionOf(active.First(x => x.Name == kv.Value)).EntityTypes), this.options.TraceGraphs?.Contains(next.Name) == true), IsMaster = next.Master, Ingests = tp.Purpose == "ingestion", Cx = cx, Out = outb, State = new Graph(Interner) };
            foreach (var (port, from) in next.Bindings) gr.PortSources[port] = from;
            graphs.Add(gr); byName[next.Name] = gr;
            gr.Head = store.Head(key).Head;
            if (gr.Ingests) foreach (var (port, from) in next.Bindings) sourcePort[from] = (next.Name, port);
        }
        WantsProgress = graphs.Any(g => g.Plan.Ops.OfType<SeriesOp>().Any(s => s.Fills));
        // a Series reads its windows from the store of the graph it reads
        foreach (var g in graphs)
            foreach (var series in g.Plan.Ops.OfType<SeriesOp>())
            {
                var read = new GraphKey(this.options.Tenant, g.PortSources[((TSeries)series.Typed).GraphPort]);
                series.Stored = (elems, props, from, to, visit) => Windows.ScanWindow(read, elems, props, from, to, visit);
            }
        // Def. 7.14: the sources that feed what a filling Series reads are fixed here
        foreach (var g in graphs)
            foreach (var series in g.Plan.Ops.OfType<SeriesOp>().Where(s => s.Fills))
            {
                var st = (TSeries)series.Typed;
                // the time to live is at most `within` (the checker), and `within` at most the retention (above): Def. 7.16 asks no more
                var fed = st.Reads.Select(r => Feeding(g.PortSources[st.GraphPort], r.PropId)).ToArray();
                series.Feeding = [.. fed.Select(x => x.Sources.ToArray())]; series.Lag = [.. fed.Select(x => x.Lag)];
                if (st.Reads.Select((r, i) => (r, i)).FirstOrDefault(x => x.r.Fill is not null && series.Feeding[x.i].Length == 0) is { r: not null } unfed)
                    throw new InvalidOperationException($"graph {g.Name}: {st.Name} fills '{unfed.r.OutName}', and no source feeds that property: a window cannot be known to be covered (Def. 7.15)");
            }
        // the rows of a graph that ingests are not evaluated again: each one's sink follows a merge or a split
        var ingesting = graphs.Where(g => g.Ingests).Select(g => (Graph: g, Properties: catalog.OntologyOf(g.Typed.OutputModule).Props)).ToList();      // a function of the catalog, which an engine never changes: built once, not per merge
        Identities.OnMerge = (loser, survivor) =>
        {
            foreach (var (g, properties) in ingesting)
            {
                foreach (var (from, to) in g.Out.Remap(loser, survivor, g.State))
                    g.Out.MoveWindows(Windows, g.Key, from, to, prop => properties.TryGetValue(Interner.Get(prop), out var p) ? p.Combine : null, epochId);
            }
        };
        Identities.OnSplit = (r, n, inPart) =>
        {
            foreach (var (g, _) in ingesting)
                foreach (var (from, to, which) in g.Out.Split(r, n, inPart, g.State))
                    g.Out.MoveWindows(Windows, g.Key, from, to, _ => null, epochId, which);
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
        if (OnTime(byName[gname], port, rows) is { } kept) ep.Rows[port] = kept;
        InputRows += rows.Rows;
        Interner.Release(rows);
        Run(ep, seq, source, gname);
    }

    /// <summary>Ch. 6, Def. 6.1: the rows of a port that declares a lateness, less the late ones, counted. A row arrives at the clock when it enters, a withdrawal
    /// too: a withdrawal that comes later than its time plus the lateness is late itself and is left out.</summary>
    Batch? OnTime(GraphRuntime g, string port, Batch rows)
    {
        if (g.Typed.Port(port) is not { TimeField: { } time, LatenessNs: { } lateness } || lambda == long.MinValue) return rows;
        var at = rows.Cols[g.Plan.PortLayout(port).Index(time)].L;
        var sel = new int[rows.Rows]; var n = 0;
        for (var k = 0; k < rows.Rows; k++)
        {
            var i = rows.RowAt(k);
            if ((Int128)at[i] + lateness >= lambda) sel[n++] = i;
            else g.Cx.LateDropped++;
        }
        return n == rows.Rows ? rows : n == 0 ? null : rows.WithSelection(sel, n);
    }

    /// <summary>Def. 7.14: the sources that feed a event property of a graph. From every sink that sets it, everything upstream: a rows port gives the source bound to it, a Series gives the sources that feed what it reads, a graph that is only matched gives none.</summary>
    /// The lag is how much later than its grid window a window of the property ends: a window of length w on the grid of its hop h ends w − h after its grid window (#111), so it is covered that much later.
    (SortedSet<string> Sources, long Lag) Feeding(string graphId, string propId)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal); long lag = 0;
        if (!byName.TryGetValue(graphId, out var g)) return (found, lag);
        var plan = g.Typed; var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(TOp op)
        {
            if (!seen.Add(op.Name)) return;
            if (op is TWindow w) lag = Math.Max(lag, w.WidthNs - w.HopNs);
            if (op is TSeries s) foreach (var read in s.Reads) { var (f, l) = Feeding(g.PortSources[s.GraphPort], read.PropId); found.UnionWith(f); lag = Math.Max(lag, l); }
            foreach (var src in op.Inputs.Values)
                if (!src.IsPort) Walk(plan.Ops.First(o => o.Name == src.Name));
                else if (plan.Port(src.Name).Kind == "rows") found.Add(g.PortSources[src.Name]);
        }
        foreach (var sink in plan.Ops)
            if ((sink switch { TEmitEntity e => e.Sets, TEmitRel r => r.Sets, _ => null })?.Any(x => x.PropId == propId) == true) Walk(sink);
        return (found, lag);
    }

    /// <summary>Whether a graph reads source progress (a filling Series). Fixed when the graphs are built: the replay asks it for every record.</summary>
    public bool WantsProgress { get; }

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

    /// <summary>A source reported lost events at an observed time: the master window containing it becomes Partial(sourceLoss).</summary>
    public void RecordLoss(long timeNs, long count, string reason = "sourceLoss")
    {
        var periods = Master.Typed.Ops.SelectMany(o => o switch { TEmitEntity e => e.Sets, TEmitRel r => r.Sets, _ => [] }).Where(x => x.Events).Select(x => x.Period).ToList();
        var period = periods.Count == 0 ? 60_000_000_000 : periods.Min();
        var ws = timeNs - ((timeNs % period) + period) % period;
        Windows.MarkPartial(Master.Key, ws, reason, count);
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

    /// <summary>Epochs in which two identities merged, and the most merges one epoch had (Law 7.5 item 3 combines their windows at the sink).</summary>
    public long EpochsWithMerge { get; private set; }
    public long MostMergesInOneEpoch { get; private set; }

    /// <summary>`rowsFor`: the graph that ingests the epoch's rows, null when it carries none.</summary>
    void Run(Epoch master, long seq, string cause, string? rowsFor = null)
    {
        var mergesBefore = Identities.Merges;
        try { RunEpoch(master, seq, cause, rowsFor); Interner.Collect(); }       // the texts that no stateful step took in this step are freed
        finally
        {
            var merged = Identities.Merges - mergesBefore;
            if (merged > 0) { EpochsWithMerge++; MostMergesInOneEpoch = Math.Max(MostMergesInOneEpoch, merged); }
        }
    }

    void RunEpoch(Epoch master, long seq, string cause, string? rowsFor)
    {
        var results = new Dictionary<string, GraphResult>();
        var deltas = new Dictionary<string, GraphDelta>();          // one per graph that stepped, shared by every graph that reads it: so is its index of changes
        foreach (var g in graphs)
        {
            Epoch ep;
            var filling = master.Progress && g.Plan.Ops.OfType<SeriesOp>().Any(s => s.Fills);
            if (master.Progress && g.Ingests && !filling) continue;       // the progress of a source concerns the graphs that fill
            if (g.Ingests)
            {
                // every graph that ingests steps with the master, after it: a merge the master's link makes in this step reaches their sinks before the views read them
                ep = new Epoch { Id = master.Id, HasAdvance = master.HasAdvance, Lambda = master.Lambda, Progress = master.Progress, Covered = covered, Loss = master.Loss };
                if (g.Name == rowsFor) foreach (var (port, b) in master.Rows) ep.Rows[port] = b;
            }
            else
            {
                ep = new Epoch { Id = master.Id, HasAdvance = master.HasAdvance, Lambda = master.Lambda, Progress = master.Progress, Covered = covered, Loss = master.Loss };
                var any = master.HasAdvance || filling;
                foreach (var (port, from) in g.PortSources)
                    if (results.TryGetValue(from, out var r))
                    {
                        if (!deltas.TryGetValue(from, out var d)) deltas[from] = d = new GraphDelta(r.Patch, byName[from].State, r.Windows);
                        ep.Graphs[port] = d;
                        any |= r.Patch is not null || r.Windows.Count > 0;
                    }
                if (!any) continue;
            }
            if (preparation.Workers > 1) ep.Preparation = preparation;
            results[g.Name] = Execute(g, ep, seq, cause);
        }
        // retention after every graph stepped: a Series retracts a window that leaves its look-back by reading it from the store of the graph it reads, which stepped before it
        if (master.HasAdvance) foreach (var name in results.Keys) Windows.Retain(byName[name].Key, master.Lambda, options.WindowRetentionNs);
    }

    GraphResult Execute(GraphRuntime g, Epoch ep, long seq, string cause)
    {
        var outputs = g.StepOutputs; var ops = g.Plan.Ops; var keys = TrackAllocations ? g.MeasureKeys : null;
        long a = TrackAllocations ? GC.GetAllocatedBytesForCurrentThread() : 0, t = System.Diagnostics.Stopwatch.GetTimestamp();
        WorkingArrays.Open();
        try
        {
            for (var i = 0; i < ops.Count; i++)
            {
                var op = ops[i]; var inputs = g.StepInputs[i];
                foreach (var (port, src) in g.StepPorts[i])
                    inputs[port] = src.IsPort ? ep.Rows.GetValueOrDefault(src.Name) : outputs.GetValueOrDefault(src.Name);
                // the one place a code reaches state: the input of a stateful step. A stateless step mints the texts it computes young.
                var stateless = op is FilterOp or MapOp;
                if (!stateless) foreach (var b in inputs.Values) if (b is not null) Interner.Escape(b);
                Interner.MintYoung = stateless;
                Charge("(step dispatch)", ref a, ref t);
                var output = op.Run(g.Cx, ep, inputs);
                if (keys is not null)
                {
                    var bytes = GC.GetAllocatedBytesForCurrentThread() - a; var ms = System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds;     // read before the output is stored, which would count as the operator's
                    OperatorAllocations[keys[i]] = OperatorAllocations.GetValueOrDefault(keys[i]) + bytes;
                    OperatorMillis[keys[i]] = OperatorMillis.GetValueOrDefault(keys[i]) + ms;
                    var kd = op.GetType().Name; var ks = KindStats.GetValueOrDefault(kd);
                    KindStats[kd] = (ks.Ms + ms, ks.Bytes + bytes, ks.Calls + 1);
                    a = GC.GetAllocatedBytesForCurrentThread(); t = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                outputs[op.Name] = output;
                Trace(g, op, output);
            }
        }
        finally
        {
            Interner.MintYoung = false;       // a step that throws leaves no young minting behind
            // the step's batches belong to their operators: the reused views let go of them
            outputs.Clear();
            for (var i = 0; i < ops.Count; i++) foreach (var (port, _) in g.StepPorts[i]) g.StepInputs[i][port] = null;
            WorkingArrays.Close();
        }
        Charge("(step dispatch)", ref a, ref t);
        var tCommit = System.Diagnostics.Stopwatch.GetTimestamp();
        var edits = g.Out.Flush(g.State);
        Charge("(flush elements)", ref a, ref t);
        g.Out.FlushWindows(Windows, g.Key, ep.Id);
        Charge("(flush windows)", ref a, ref t);
        Patch? patch = null;
        if (edits.Count > 0)
        {
            patch = new Patch(g.Name, g.Head, g.Head + 1, edits, new Cause([cause + "#" + seq], ConfigurationRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var err = g.State.Apply(patch.Edits);
            if (err is not null) throw new InvalidOperationException($"graph {g.Name}: engine produced an inapplicable patch: {err}");
            Charge("(patch and apply)", ref a, ref t);
            var gin = ImmutableSortedDictionary.CreateBuilder<string, long>(StringComparer.Ordinal);
            foreach (var (_, from) in g.PortSources) if (byName.TryGetValue(from, out var up)) gin[from] = up.Head;
            var pos = ImmutableSortedDictionary<string, long>.Empty.Add(cause, seq);
            var fr = new Frontier(pos, gin.ToImmutable(), ConfigurationRevision);
            var res = store.Commit(new CommitBatch(g.Key, g.Head, [patch], [fr], g.IsMaster ? Identities.Pending : []));
            if (g.IsMaster) Identities.Committed();
            if (res is not CommitResult.Committed c) throw new InvalidOperationException($"graph {g.Name}: store did not commit: {res}");
            g.Head = c.Head;
            Charge("(store commit)", ref a, ref t);
        }
        if (keys is not null) OperatorMillis[keys[^1]] = OperatorMillis.GetValueOrDefault(keys[^1]) + System.Diagnostics.Stopwatch.GetElapsedTime(tCommit).TotalMilliseconds;
        // every step reads what its graph's windows changed by: its own samples, and the windows a merge of two identities moved in this step, which no sample announces
        var changes = Windows.ClosedWindows(g.Key, g.WindowPosition, out var read);
        g.WindowPosition = read;
        Charge("(window read)", ref a, ref t);
        var result = new GraphResult(patch, changes, g.Head);
        var conflicts = g.Out.TakeChanged();
        if (patch is not null || changes.Count > 0 || conflicts.Count > 0) Committed?.Invoke(new CommitEvent(g.Name, patch, changes, ep.Lambda, seq, Interner) { Conflicts = conflicts });
        Charge("(commit event)", ref a, ref t);
        return result;
    }

    /// <summary>While TrackAllocations is on: adds the time and the allocation since the mark (`a`, `t`) to `kind`, and moves the mark to now.</summary>
    void Charge(string kind, ref long a, ref long t)
    {
        if (!TrackAllocations) return;
        var a1 = GC.GetAllocatedBytesForCurrentThread(); var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        var ks = KindStats.GetValueOrDefault(kind);
        KindStats[kind] = (ks.Ms + System.Diagnostics.Stopwatch.GetElapsedTime(t, t1).TotalMilliseconds, ks.Bytes + a1 - a, ks.Calls + 1);
        a = GC.GetAllocatedBytesForCurrentThread(); t = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// A digest of the observable output (T7): per graph, a set digest (AdHash) of its written pairs and its windows, tagged with the
    /// graph's name; the engine's is the AdHash of the graphs' digests. No order of graphs, elements or windows matters.
    /// </summary>
    public string StateDigest() => StateDigests().State;

    /// <summary>The state digest and, from the same pass, per graph dig(G) of Def. 3.5 (its written pairs, no windows), the digest a preview gives per graph.</summary>
    public (string State, IReadOnlyList<(string GraphId, string Digest)> Graphs) StateDigests()
    {
        var all = new Digests.Set(); var sb = new StringBuilder(); var perGraph = new List<(string, string)>(graphs.Count);
        foreach (var g in graphs)
        {
            var set = g.State.PairSet();
            perGraph.Add((g.Name, set.Digest("")));
            var key = g.Key;
            Windows.ScanGraph(key, (e, p, ws, v) =>
            {
                sb.Clear().Append("w|").Append(Interner.Get(e)).Append('|').Append(Interner.Get(p)).Append('|').Append(ws).Append('|').Append(Norm.Decimal(v));
                if (Windows.AuxOf(key, new WindowKey(e, p, ws)) is { } aux) sb.Append('|').Append(Norm.Decimal(aux.A)).Append('|').Append(Norm.Decimal(aux.B));          // only windows of `mean` and `last` with their pair
                set.Add(sb);
            });
            all.Add(Encoding.UTF8.GetBytes(set.Digest(g.Name)));
        }
        return (all.Digest(""), perGraph);
    }

    /// <summary>The counters of a run, the one calculation `replay` prints and the liveness route serves: identities minted, aliased through links, merged and refused as conflicts; rows dropped as late (PROTOCOL 3.3); rows dropped by a runtime fault.</summary>
    public RunCounters Counters() => new(Identities.Minted, Identities.Aliased, Identities.Merges, Identities.Conflicts, graphs.Sum(g => g.Cx.LateDropped), Faults.DivisionByZero, Faults.Overflow, Faults.InvalidParameter);

    public void Advance(long lambdaNs) => Advance(lambdaNs, 0);

    // ---- state snapshot (seals) ----

    /// <summary>The state of one graph: its hot elements, output layers, counters and the state of every operator of its plan.</summary>
    public sealed class GraphStateRoot
    {
        public string Name = ""; public long Head, WindowPosition, Skipped;
        public Graph State = null!; public OutputBuilder Out = null!; public OpContext Cx = null!; public List<PhysOp> Ops = [];
    }
    public sealed class StateRoot
    {
        public Interner Interner = null!; public RuntimeFaults Faults = null!; public IdentityCatalog Identities = null!; public IWindowStore Windows = null!;
        public long EpochId, Lambda, InputRows; public List<GraphStateRoot> Graphs = [];
        public Dictionary<string, long> Covered = null!;
    }

    /// <summary>The objects that hold this engine's state, for a snapshot. Live objects, not a copy.</summary>
    public StateRoot StateRootOf() => new()
    {
        Interner = Interner, Faults = Faults, Identities = Identities, Windows = Windows, EpochId = epochId, Lambda = lambda, InputRows = InputRows, Covered = covered,
        Graphs = [.. graphs.Select(g => new GraphStateRoot { Name = g.Name, Head = g.Head, WindowPosition = g.WindowPosition, Skipped = g.Skipped, State = g.State, Out = g.Out, Cx = g.Cx, Ops = [.. g.Plan.Ops] })],
    };

    /// <summary>After a snapshot was loaded into `StateRootOf()` of this (freshly built) engine: the scalars the root carried go back to the engine.</summary>
    public void AdoptState(StateRoot root)
    {
        epochId = root.EpochId; lambda = root.Lambda; InputRows = root.InputRows;
        foreach (var gs in root.Graphs)
        {
            var g = byName[gs.Name]; g.Head = gs.Head; g.WindowPosition = gs.WindowPosition; g.Skipped = gs.Skipped;
        }
    }
    public void Dispose() => store.Dispose();
}
