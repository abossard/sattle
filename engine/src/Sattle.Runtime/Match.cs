using System.Runtime.ExceptionServices;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

interface IGraphView
{
    /// <summary>The type of an entity of the view, without its props (null when it has none), and the entity itself when the view has it already made (the old view's entities of the patch).</summary>
    string? EntityType(string id, out Entity? made);
    /// <summary>The relationships at an entity whose type passes `type`.</summary>
    IEnumerable<Rel> OutRels(string entity, Func<string, bool> type);
    IEnumerable<Rel> InRels(string entity, Func<string, bool> type);
}

sealed class NewView : IGraphView
{
    Graph g = null!; ITextTable? reader;
    public NewView Reset(Graph graph, ITextTable? texts = null) { g = graph; reader = texts; return this; }
    public string? EntityType(string id, out Entity? made) { made = null; return g.EntityType(id, reader); }
    public IEnumerable<Rel> OutRels(string e, Func<string, bool> type) => g.OutEdges(e, type, reader);
    public IEnumerable<Rel> InRels(string e, Func<string, bool> type) => g.InEdges(e, type, reader);
}

/// <summary>The relationships removed or replaced by a patch, indexed at both ends in canonical edit order.</summary>
sealed class BeforeEdges
{
    public readonly Dictionary<string, List<Rel>> Out = new(StringComparer.Ordinal), In = new(StringComparer.Ordinal);
    public void Build(Patch patch)
    {
        foreach (var edit in patch.Edits)
            if (edit.Before is Rel r)
            {
                if (!Out.TryGetValue(r.Source, out var o)) Out[r.Source] = o = [];
                if (!In.TryGetValue(r.Target, out var i)) In[r.Target] = i = [];
                o.Add(r); i.Add(r);
            }
    }
    public void Clear() { Out.Clear(); In.Clear(); }
}

/// <summary>The seed types and before-edges of one large, committed patch, shared by all its Match readers.</summary>
sealed class MatchPatch
{
    readonly HashSet<string> entityTypes = new(StringComparer.Ordinal), relTypes = new(StringComparer.Ordinal);
    public readonly int FirstRelationship;
    public readonly Lazy<BeforeEdges> OldEdges;
    public MatchPatch(Patch patch)
    {
        FirstRelationship = patch.Edits.Length;
        for (int i = 0; i < patch.Edits.Length; i++)
        {
            var e = patch.Edits[i];
            if (e.Key.Kind == 'r' && i < FirstRelationship) FirstRelationship = i;
            if (e.Before is Entity eb) entityTypes.Add(eb.Type);
            else if (e.Before is Rel rb) relTypes.Add(rb.Type);
            if (e.After is Entity ea) entityTypes.Add(ea.Type);
            else if (e.After is Rel ra) relTypes.Add(ra.Type);
        }
        OldEdges = new(() => { var index = new BeforeEdges(); index.Build(patch); return index; });
    }
    public bool CanSeed(bool entity, Func<string, bool> fits)
    {
        foreach (var type in entity ? entityTypes : relTypes) if (fits(type)) return true;
        return false;
    }
}

/// <summary>The graph before a patch: the current graph with the patch's edits undone, without copying the graph.</summary>
sealed class OldView : IGraphView
{
    Graph g = null!; Patch p = null!; ITextTable? reader;
    BeforeEdges? local; bool indexed; Lazy<BeforeEdges>? shared;
    public OldView Reset(Graph graph, Patch patch, ITextTable? texts = null, Lazy<BeforeEdges>? sharedEdges = null)
    {
        g = graph; p = patch; reader = texts; shared = sharedEdges;
        if (indexed) { local!.Clear(); indexed = false; }
        return this;
    }
    public string? EntityType(string id, out Entity? made)
    {
        if (p.TryGet(ElementKey.Entity(id), out var e)) { made = e.Before as Entity; return made?.Type; }
        made = null; return g.EntityType(id, reader);
    }
    public IEnumerable<Rel> OutRels(string e, Func<string, bool> type)
    {
        foreach (var r in g.OutEdges(e, type, reader)) if (!p.TryGet(ElementKey.Rel(r.Id), out _)) yield return r;
        if (Before().Out.TryGetValue(e, out var l)) foreach (var r in l) if (type(r.Type)) yield return r;
    }
    public IEnumerable<Rel> InRels(string e, Func<string, bool> type)
    {
        foreach (var r in g.InEdges(e, type, reader)) if (!p.TryGet(ElementKey.Rel(r.Id), out _)) yield return r;
        if (Before().In.TryGetValue(e, out var l)) foreach (var r in l) if (type(r.Type)) yield return r;
    }
    BeforeEdges Before()
    {
        if (shared is not null) return shared.Value;
        if (!indexed) { local ??= new(); local.Build(p); indexed = true; }
        return local!;
    }
}

/// <summary>Limits concurrent match ranges and records completed work. Only the engine writer updates the totals.</summary>
sealed class MatchPreparation
{
    public const int MinEdits = 1024, MinSourceEdits = 8192, MinRange = 256, MaxRange = 8192;
    public const int BytesPerLane = 1 << 20;
    public int Workers { get; }
    public ParallelOptions ParallelOptions { get; }
    public long Ranges, WorkerRanges, ReplayedRanges;
    Interner.MatchReader[]? readers;
    public Interner.MatchReader Reader(int lane, Interner it)
    {
        readers ??= new Interner.MatchReader[Workers];
        return readers[lane] ??= new(it);
    }
    public void ReleaseReaders() { if (readers is not null) foreach (var reader in readers) reader?.Clear(); }
    public MatchPreparation(int workers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        Workers = Math.Min(Environment.ProcessorCount, workers);
        ParallelOptions = new() { MaxDegreeOfParallelism = Workers, TaskScheduler = TaskScheduler.Default };
    }
    public static int DefaultWorkers()
    {
        var setting = Environment.GetEnvironmentVariable("SATTLE_PREPARATION_WORKERS");
        if (string.IsNullOrEmpty(setting)) return 1;
        if (int.TryParse(setting, out var count) && count >= 1) return count;
        throw new ArgumentException("SATTLE_PREPARATION_WORKERS must be a positive integer");
    }
}

/// <summary>Match over a graph port, incremental by the bilinear delta rule: for each changed component i, matches with components before i read from the new graph and components after i from the old graph (DBSP, section 11.1 step 6).</summary>
sealed class MatchOp : PhysOp
{
    readonly TMatch t;
    readonly int nodeCount; readonly int compCount;
    readonly int preparedRowLimit;
    readonly int[][] order;               // per seed component, the order of the other components
    readonly string[] nodeVars; readonly TNode[] nodes; readonly TEdge[] edges;
    /// <summary>The accepted types of each component, fixed by the ontology at compilation.</summary>
    readonly Func<string, bool>[] fitsType;
    readonly int[] varCol;                // out column per component (entity or rel handle), -1 if the component has no output column
    readonly (int Comp, string PropId, int OutCol, Ty Ty)[] reads;

    sealed class Scratch
    {
        public readonly NewView New = new();
        public readonly OldView Old = new();
        public Element?[] Bound = [];
        public bool[] FromGraph = [];
        public Graph Graph = null!;
        public ITextTable Texts = null!;
        public ITextTable? Decoder;
        public int RowLimit = int.MaxValue;
        public void Reset(GraphDelta delta, ITextTable texts, int components, Lazy<BeforeEdges>? shared = null)
        {
            if (Bound.Length < components) { Bound = new Element?[components]; FromGraph = new bool[components]; }
            Graph = delta.State; Texts = texts;
            Decoder = texts is Interner.MatchReader ? texts : null;
            New.Reset(Graph, Decoder); Old.Reset(Graph, delta.Patch!, Decoder, shared);
        }
        public void Clear()
        {
            Array.Clear(Bound); New.Reset(null!); Old.Reset(null!, null!); Graph = null!; Texts = null!; Decoder = null;
        }
    }
    [ThreadStatic] static Scratch? scratchOfThread;

    /// <summary>Per node: a reference. It names the end of a matched edge by its id and has no entity in this graph, so it binds without one and never seeds a match.</summary>
    readonly bool[] reference;

    public MatchOp(TMatch t, OntologyView view, IReadOnlySet<string> references) : base(t)
    {
        this.t = t;
        reference = [.. t.Nodes.Select(n => references.Contains(n.Var))];
        nodes = [.. t.Nodes]; edges = [.. t.Edges]; nodeVars = [.. nodes.Select(n => n.Var)];
        nodeCount = nodes.Length; compCount = nodes.Length + edges.Length;
        order = new int[compCount][];
        for (var seed = 0; seed < compCount; seed++) order[seed] = Order(seed);
        varCol = new int[compCount];
        for (var c = 0; c < compCount; c++)
        {
            var name = c < nodeCount ? nodes[c].Var : edges[c - nodeCount].Var;
            varCol[c] = name is not null && OutLayout.Has(name) ? OutLayout.Index(name) : -1;
        }
        var rs = new List<(int, string, int, Ty)>();
        foreach (var r in t.Reads)
        {
            var comp = Array.IndexOf(nodeVars, r.Var);
            if (comp < 0) comp = nodeCount + Array.FindIndex(edges, e => e.Var == r.Var);
            rs.Add((comp, r.PropId, OutLayout.Index(r.Col), t.Out.Find(r.Col)!.T.Ty));
        }
        reads = [.. rs];
        long rowBytes = sizeof(long) + OutLayout.IsDec.Sum(isDecimal => 1L + (isDecimal ? sizeof(decimal) : sizeof(long)));
        int rowsInBudget = (int)(MatchPreparation.BytesPerLane / rowBytes);
        preparedRowLimit = rowsInBudget < WorkingArrays.Small ? 0 : 1 << System.Numerics.BitOperations.Log2((uint)rowsInBudget);
        fitsType = new Func<string, bool>[compCount];
        for (var c = 0; c < compCount; c++)
        {
            var wanted = c < nodeCount ? nodes[c].TypeId : edges[c - nodeCount].TypeId;
            var accepted = view.Types.Keys.Where(type => view.IsSubtype(type, wanted)).ToHashSet(StringComparer.Ordinal);
            accepted.Add(wanted); // subtype is reflexive even for a type outside this ontology
            fitsType[c] = accepted.Contains;
        }
    }

    int NodeIndex(string v) => Array.IndexOf(nodeVars, v);

    int[] Order(int seed)
    {
        var bound = new bool[compCount]; bound[seed] = true;
        var res = new List<int>();
        bool Adjacent(int c)
        {
            if (c < nodeCount) return edges.Select((e, i) => (e, i)).Any(x => bound[nodeCount + x.i] && (x.e.From == nodes[c].Var || x.e.To == nodes[c].Var));
            var e = edges[c - nodeCount];
            return bound[NodeIndex(e.From)] || bound[NodeIndex(e.To)];
        }
        while (res.Count < compCount - 1)
        {
            var next = Enumerable.Range(0, compCount).FirstOrDefault(c => !bound[c] && Adjacent(c), -1);
            if (next < 0) throw new InvalidOperationException("pattern is not connected");
            bound[next] = true; res.Add(next);
        }
        return [.. res];
    }

    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        if (!ep.Graphs.TryGetValue(t.GraphPort, out var gd) || gd.Patch is null) return null;
        var output = EmptyOutput();
        var input = gd.Patch.Edits.Length >= MatchPreparation.MinEdits ? gd.MatchInput : null;
        if (input is not null && ep.Preparation is { } preparation && preparedRowLimit > 0
            && (ep.HasAdvance || ep.Progress || gd.Patch.Edits.Length >= MatchPreparation.MinSourceEdits) && gd.State.Uses(it))
        {
            Prepare(gd, input, output, preparation);
            return output.Build();
        }
        var s = scratchOfThread ??= new();
        s.Reset(gd, it, compCount, input?.OldEdges);
        try
        {
            for (var seed = 0; seed < compCount; seed++)
            {
                if (seed < nodeCount && reference[seed] || input is not null && !input.CanSeed(seed < nodeCount, fitsType[seed])) continue;
                int start = input is not null && seed >= nodeCount ? input.FirstRelationship : 0;
                int end = input is not null && seed < nodeCount ? input.FirstRelationship : gd.Patch.Edits.Length;
                Scan(s, seed, gd.Patch, start, end, output);
            }
        }
        finally { s.Clear(); }
        return output.Build();
    }

    void Scan(Scratch s, int seed, Patch patch, int start, int end, BatchBuilder output)
    {
        var kind = seed < nodeCount ? 'e' : 'r';
        foreach (var edit in patch.Edits.AsSpan(start, end - start))
        {
            if (edit.Key.Kind != kind) continue;
            Seed(s, seed, edit.Before, -1, output);
            Seed(s, seed, edit.After, 1, output);
        }
    }

    sealed class RangeFull : Exception;

    /// <summary>Walk bounded ranges of the committed patch concurrently. A range owns its bindings, text cache and rows.
    /// The writer appends completed ranges in seed/edit order before this operator returns to the plan.</summary>
    void Prepare(GraphDelta delta, MatchPatch input, BatchBuilder output, MatchPreparation preparation)
    {
        var patch = delta.Patch!;
        bool CanSeed(int seed) => (seed >= nodeCount || !reference[seed]) && input.CanSeed(seed < nodeCount, fitsType[seed]);
        int first = 0;
        while (first < compCount && !CanSeed(first)) first++;
        if (first == compCount) return;
        int lanes = Math.Min(preparation.Workers, (patch.Edits.Length - 1) / MatchPreparation.MinRange + 1);
        var states = new Scratch[lanes];
        var rows = new BatchBuilder[lanes];
        for (var lane = 0; lane < lanes; lane++)
        {
            states[lane] = new() { RowLimit = preparedRowLimit };
            states[lane].Reset(delta, preparation.Reader(lane, it), compCount, input.OldEdges);
            rows[lane] = new(OutLayout);
        }
        var errors = new ExceptionDispatchInfo?[lanes];
        var full = new bool[lanes];
        var writer = Environment.CurrentManagedThreadId;
        var offWriter = 0;
        try
        {
            for (var seed = first; seed < compCount; seed++)
            {
                if (seed != first && !CanSeed(seed)) continue;
                var start = seed < nodeCount ? 0 : input.FirstRelationship;
                var end = seed < nodeCount ? input.FirstRelationship : patch.Edits.Length;
                int rangeSize = Math.Clamp((end - start - 1) / lanes + 1, MatchPreparation.MinRange, MatchPreparation.MaxRange);
                while (start < end)
                {
                    var count = Math.Min(lanes, (end - start - 1) / rangeSize + 1);
                    Parallel.For(0, count, preparation.ParallelOptions, lane =>
                    {
                        try
                        {
                            using var pause = new WorkingArrays.Pause();
                            rows[lane].Reset();
                            var at = start + lane * rangeSize;
                            Scan(states[lane], seed, patch, at, at + Math.Min(rangeSize, end - at), rows[lane]);
                            if (Environment.CurrentManagedThreadId != writer) Interlocked.Increment(ref offWriter);
                        }
                        catch (RangeFull) { full[lane] = true; }
                        catch (Exception ex) { errors[lane] = ExceptionDispatchInfo.Capture(ex); }
                    });
                    for (var lane = 0; lane < count; lane++)
                    {
                        errors[lane]?.Throw();
                        if (full[lane])
                        {
                            // A single edit can produce arbitrarily many matches. Replay this range into its final position
                            // after the join, discarding the private prefix, so fan-out cannot grow the staging buffers.
                            var at = start + lane * rangeSize;
                            states[lane].RowLimit = int.MaxValue;
                            Scan(states[lane], seed, patch, at, at + Math.Min(rangeSize, end - at), output);
                            states[lane].RowLimit = preparedRowLimit;
                            full[lane] = false;
                            preparation.ReplayedRanges++;
                        }
                        else if (rows[lane].Build() is { } batch) output.Append(batch);
                    }
                    preparation.Ranges += count;
                    start += Math.Min(end - start, count * rangeSize);
                }
            }
        }
        finally { preparation.ReleaseReaders(); }
        preparation.WorkerRanges += offWriter;
    }

    void Seed(Scratch s, int seed, Element? state, long sign, BatchBuilder bb)
    {
        if (state is null || !Fits(seed, state)) return;
        Array.Clear(s.Bound); Array.Clear(s.FromGraph);
        s.Bound[seed] = state;
        Extend(s, 0, seed, sign, bb);
    }

    bool Fits(int comp, Element e) => comp < nodeCount
        ? e is Entity en && fitsType[comp](en.Type)
        : e is Rel r && fitsType[comp](r.Type);

    void Extend(Scratch s, int step, int seed, long sign, BatchBuilder bb)
    {
        if (step == order[seed].Length) { Emit(s, sign, bb); return; }
        var bound = s.Bound;
        var comp = order[seed][step];
        IGraphView view = comp < seed ? s.New : s.Old; // components before the seed read the new graph, after it the old graph
        if (comp < nodeCount)
        {
            // a node adjacent to a bound edge
            var v = nodes[comp].Var;
            string? id = null;
            for (var i = 0; i < edges.Length && id is null; i++)
                if (bound[nodeCount + i] is Rel r)
                {
                    if (edges[i].From == v) id = r.Source; else if (edges[i].To == v) id = r.Target;
                }
            if (id is null) return;
            Entity? e; var fromGraph = s.FromGraph;
            if (reference[comp]) { e = new Entity(id, nodes[comp].TypeId, Props.Empty); fromGraph[comp] = false; }
            else
            {
                var type = view.EntityType(id, out var made);
                e = made ?? (type is not null && fitsType[comp](type) ? new Entity(id, type, Props.Empty) : null);
                fromGraph[comp] = made is null;
            }
            if (e is not null && Fits(comp, e)) { bound[comp] = e; Extend(s, step + 1, seed, sign, bb); bound[comp] = null; }
            return;
        }
        var edge = edges[comp - nodeCount];
        var fromNode = NodeIndex(edge.From); var toNode = NodeIndex(edge.To);
        IEnumerable<Rel> candidates;
        if (bound[fromNode] is Entity f) candidates = view.OutRels(f.Id, fitsType[comp]);
        else if (bound[toNode] is Entity to) candidates = view.InRels(to.Id, fitsType[comp]);
        else return;
        foreach (var r in candidates)
        {
            if (!Fits(comp, r)) continue;
            if (bound[fromNode] is Entity bf && r.Source != bf.Id) continue;
            if (bound[toNode] is Entity bt && r.Target != bt.Id) continue;
            bound[comp] = r;
            Extend(s, step + 1, seed, sign, bb);
            bound[comp] = null;
        }
    }

    void Emit(Scratch s, long sign, BatchBuilder bb)
    {
        var bound = s.Bound;
        var b = bb.Batch;
        if (b.N >= s.RowLimit) throw new RangeFull();
        var row = bb.Add(sign);
        for (var c = 0; c < compCount; c++)
            if (varCol[c] >= 0) b.Cols[varCol[c]].L[row] = bound[c] switch { Entity e => s.Texts.Intern(e.Id), Rel r => s.Texts.Intern(r.Id), _ => 0 };
        foreach (var (comp, propId, outCol, ty) in reads)
        {
            var v = bound[comp] switch
            {
                Entity e when s.FromGraph[comp] => s.Graph.EntityProp(e.Id, propId, s.Decoder),
                Entity e => e.Props.TryGet(propId, out var x) ? x : null,
                Rel r => r.Props.TryGet(propId, out var x) ? x : null,
                _ => null,
            };
            var col = b.Cols[outCol];
            if (v is not null and not NullValue) { if (col.IsDec) col.D[row] = ((DecimalValue)v).Q; else col.L[row] = Codec.ToCode(v, s.Texts); }
            else col.SetAbsent(row, b.N);
        }
    }

    Interner it = null!;
    public void Bind(Interner interner) { it = interner; }
}
