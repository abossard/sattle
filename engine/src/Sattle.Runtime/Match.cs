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
    Graph g = null!;
    public NewView Reset(Graph graph) { g = graph; return this; }
    public string? EntityType(string id, out Entity? made) { made = null; return g.EntityType(id); }
    public IEnumerable<Rel> OutRels(string e, Func<string, bool> type) => g.OutEdges(e, type);
    public IEnumerable<Rel> InRels(string e, Func<string, bool> type) => g.InEdges(e, type);
}

/// <summary>The graph before a patch: the current graph with the patch's edits undone, without copying the graph. The relationships the patch changed are indexed by their ends before it at the first walk that needs them.</summary>
sealed class OldView : IGraphView
{
    Graph g = null!; Patch p = null!; bool indexed;
    readonly Dictionary<string, List<Rel>> outBefore = new(StringComparer.Ordinal), inBefore = new(StringComparer.Ordinal);
    public OldView Reset(Graph graph, Patch patch)
    {
        g = graph; p = patch;
        if (indexed) { outBefore.Clear(); inBefore.Clear(); indexed = false; }
        return this;
    }
    public string? EntityType(string id, out Entity? made)
    {
        if (p.TryGet(ElementKey.Entity(id), out var e)) { made = e.Before as Entity; return made?.Type; }
        made = null; return g.EntityType(id);
    }
    public IEnumerable<Rel> OutRels(string e, Func<string, bool> type)
    {
        foreach (var r in g.OutEdges(e, type)) if (!p.TryGet(ElementKey.Rel(r.Id), out _)) yield return r;
        if (Before(outgoing: true).TryGetValue(e, out var l)) foreach (var r in l) if (type(r.Type)) yield return r;
    }
    public IEnumerable<Rel> InRels(string e, Func<string, bool> type)
    {
        foreach (var r in g.InEdges(e, type)) if (!p.TryGet(ElementKey.Rel(r.Id), out _)) yield return r;
        if (Before(outgoing: false).TryGetValue(e, out var l)) foreach (var r in l) if (type(r.Type)) yield return r;
    }
    Dictionary<string, List<Rel>> Before(bool outgoing)
    {
        if (!indexed)
        {
            indexed = true;
            foreach (var e in p.Edits)
                if (e.Before is Rel r)
                {
                    if (!outBefore.TryGetValue(r.Source, out var o)) outBefore[r.Source] = o = [];
                    o.Add(r);
                    if (!inBefore.TryGetValue(r.Target, out var i)) inBefore[r.Target] = i = [];
                    i.Add(r);
                }
        }
        return outgoing ? outBefore : inBefore;
    }
}

/// <summary>Match over a graph port, incremental by the bilinear delta rule: for each changed component i, matches with components before i read from the new graph and components after i from the old graph (DBSP, section 11.1 step 6).</summary>
sealed class MatchOp : PhysOp
{
    readonly TMatch t; readonly OntologyView view;
    readonly int nodeCount; readonly int compCount;
    readonly int[][] order;               // per seed component, the order of the other components
    readonly string[] nodeVars; readonly TNode[] nodes; readonly TEdge[] edges;
    /// <summary>Per component: whether a type is its type or a subtype of it, as asked so far. Keyed by the type alone, so a lookup hashes one string.</summary>
    readonly Dictionary<string, bool>[] fitsType;
    readonly int[] varCol;                // out column per component (entity or rel handle), -1 if the component has no output column
    readonly (int Comp, string PropId, int OutCol, Ty Ty)[] reads;

    /// <summary>Per edge: whether a relationship type fits it, so a walk makes only the relationships that fit.</summary>
    readonly Func<string, bool>[] edgeFits;

    /// <summary>The working state of the call in progress on this thread (calls do not overlap): the two views, the bindings per component, and per component whether its entity was bound from the graph by id and type, so that `Emit` reads its properties from the graph.</summary>
    sealed class Scratch { public readonly NewView New = new(); public readonly OldView Old = new(); public Element?[] Bound = []; public bool[] FromGraph = []; public Graph Graph = null!; }
    [ThreadStatic] static Scratch? scratchOfThread;

    /// <summary>Per node: a reference. It names the end of a matched edge by its id and has no entity in this graph, so it binds without one and never seeds a match.</summary>
    readonly bool[] reference;

    public MatchOp(TMatch t, OntologyView view, IReadOnlySet<string> references) : base(t)
    {
        this.t = t; this.view = view;
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
        fitsType = [.. Enumerable.Range(0, compCount).Select(_ => new Dictionary<string, bool>(StringComparer.Ordinal))];
        edgeFits = [.. edges.Select((_, i) => (Func<string, bool>)(type => Sub(type, nodeCount + i)))];
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

    /// <summary>Whether `type` is the type of component `comp` or a subtype of it.</summary>
    bool Sub(string type, int comp)
    {
        var known = fitsType[comp];
        if (!known.TryGetValue(type, out var r)) known[type] = r = view.IsSubtype(type, comp < nodeCount ? nodes[comp].TypeId : edges[comp - nodeCount].TypeId);
        return r;
    }

    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        if (!ep.Graphs.TryGetValue(t.GraphPort, out var gd) || gd.Patch is null) return null;
        var patch = gd.Patch;
        var s = scratchOfThread ??= new();
        if (s.Bound.Length < compCount) { s.Bound = new Element?[compCount]; s.FromGraph = new bool[compCount]; }
        var newView = s.New.Reset(gd.State); var oldView = s.Old.Reset(gd.State, patch); s.Graph = gd.State;
        var bb = EmptyOutput();
        var bound = s.Bound;
        try
        {
            for (var seed = 0; seed < compCount; seed++)
            {
                var isNode = seed < nodeCount;
                if (isNode && reference[seed]) continue;
                foreach (var edit in patch.Edits)
                {
                    if (isNode ? edit.Key.Kind != 'e' : edit.Key.Kind != 'r') continue;
                    Seed(seed, edit.Before, -1, bound, newView, oldView, bb);
                    Seed(seed, edit.After, 1, bound, newView, oldView, bb);
                }
            }
        }
        finally { Array.Clear(bound); newView.Reset(null!); oldView.Reset(null!, null!); s.Graph = null!; }        // holds no graph, patch or element between calls
        return bb.Build();
    }

    void Seed(int seed, Element? state, long sign, Element?[] bound, IGraphView nv, IGraphView ov, BatchBuilder bb)
    {
        if (state is null || !Fits(seed, state)) return;
        Array.Clear(bound); Array.Clear(scratchOfThread!.FromGraph);
        bound[seed] = state;
        Extend(0, seed, sign, bound, nv, ov, bb);
    }

    bool Fits(int comp, Element e) => comp < nodeCount
        ? e is Entity en && Sub(en.Type, comp)
        : e is Rel r && Sub(r.Type, comp);

    void Extend(int step, int seed, long sign, Element?[] bound, IGraphView nv, IGraphView ov, BatchBuilder bb)
    {
        if (step == order[seed].Length) { Emit(bound, sign, bb); return; }
        var comp = order[seed][step];
        var view = comp < seed ? nv : ov; // components before the seed read the new graph, after it the old graph
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
            Entity? e; var fromGraph = scratchOfThread!.FromGraph;
            if (reference[comp]) { e = new Entity(id, nodes[comp].TypeId, Props.Empty); fromGraph[comp] = false; }
            else
            {
                var type = view.EntityType(id, out var made);
                e = made ?? (type is not null && Sub(type, comp) ? new Entity(id, type, Props.Empty) : null);
                fromGraph[comp] = made is null;
            }
            if (e is not null && Fits(comp, e)) { bound[comp] = e; Extend(step + 1, seed, sign, bound, nv, ov, bb); bound[comp] = null; }
            return;
        }
        var edge = edges[comp - nodeCount];
        var fromNode = NodeIndex(edge.From); var toNode = NodeIndex(edge.To);
        IEnumerable<Rel> candidates;
        if (bound[fromNode] is Entity f) candidates = view.OutRels(f.Id, edgeFits[comp - nodeCount]);
        else if (bound[toNode] is Entity to) candidates = view.InRels(to.Id, edgeFits[comp - nodeCount]);
        else return;
        foreach (var r in candidates)
        {
            if (!Fits(comp, r)) continue;
            if (bound[fromNode] is Entity bf && r.Source != bf.Id) continue;
            if (bound[toNode] is Entity bt && r.Target != bt.Id) continue;
            bound[comp] = r;
            Extend(step + 1, seed, sign, bound, nv, ov, bb);
            bound[comp] = null;
        }
    }

    void Emit(Element?[] bound, long sign, BatchBuilder bb)
    {
        var row = bb.Add(sign);
        var b = bb.Batch;
        for (var c = 0; c < compCount; c++)
            if (varCol[c] >= 0) b.Cols[varCol[c]].L[row] = bound[c] switch { Entity e => it.Intern(e.Id), Rel r => it.Intern(r.Id), _ => 0 };
        var s = scratchOfThread!;
        foreach (var (comp, propId, outCol, ty) in reads)
        {
            var v = bound[comp] switch
            {
                Entity e when s.FromGraph[comp] => s.Graph.EntityProp(e.Id, propId),
                Entity e => e.Props.TryGet(propId, out var x) ? x : null,
                Rel r => r.Props.TryGet(propId, out var x) ? x : null,
                _ => null,
            };
            var col = b.Cols[outCol];
            if (v is not null and not NullValue) { if (col.IsDec) col.D[row] = ((DecimalValue)v).Q; else col.L[row] = Codec.ToCode(v, it); }
            else col.SetAbsent(row, b.N);
        }
    }

    Interner it = null!;
    public void Bind(Interner interner) { it = interner; }
}
