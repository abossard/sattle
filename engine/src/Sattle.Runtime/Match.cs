using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

interface IGraphView
{
    Entity? Entity(string id);
    Rel? Rel(string id);
    IEnumerable<Rel> OutRels(string entity);
    IEnumerable<Rel> InRels(string entity);
}

sealed class NewView(Graph g) : IGraphView
{
    public Entity? Entity(string id) => g.Entity(id);
    public Rel? Rel(string id) => g.Rel(id);
    public IEnumerable<Rel> OutRels(string e) => g.OutRels(e).Select(g.Rel).Where(r => r is not null).Select(r => r!);
    public IEnumerable<Rel> InRels(string e) => g.InRels(e).Select(g.Rel).Where(r => r is not null).Select(r => r!);
}

/// <summary>The graph before a patch: the current graph with the patch's edits undone, without copying the graph.</summary>
sealed class OldView : IGraphView
{
    readonly Graph g; readonly Patch p;
    readonly Dictionary<string, List<Rel>> outBefore = new(StringComparer.Ordinal), inBefore = new(StringComparer.Ordinal);
    public OldView(Graph g, Patch p)
    {
        this.g = g; this.p = p;
        foreach (var e in p.Edits)
            if (e.Before is Rel r)
            {
                if (!outBefore.TryGetValue(r.Source, out var o)) outBefore[r.Source] = o = [];
                o.Add(r);
                if (!inBefore.TryGetValue(r.Target, out var i)) inBefore[r.Target] = i = [];
                i.Add(r);
            }
    }
    public Entity? Entity(string id) => p.TryGet(ElementKey.Entity(id), out var e) ? e.Before as Entity : g.Entity(id);
    public Rel? Rel(string id) => p.TryGet(ElementKey.Rel(id), out var e) ? e.Before as Rel : g.Rel(id);
    public IEnumerable<Rel> OutRels(string e)
    {
        foreach (var id in g.OutRels(e)) if (!p.TryGet(ElementKey.Rel(id), out _)) yield return g.Rel(id)!;
        if (outBefore.TryGetValue(e, out var l)) foreach (var r in l) yield return r;
    }
    public IEnumerable<Rel> InRels(string e)
    {
        foreach (var id in g.InRels(e)) if (!p.TryGet(ElementKey.Rel(id), out _)) yield return g.Rel(id)!;
        if (inBefore.TryGetValue(e, out var l)) foreach (var r in l) yield return r;
    }
}

/// <summary>Match over a graph port, incremental by the bilinear delta rule: for each changed component i, matches with components before i read from the new graph and components after i from the old graph (DBSP, section 11.1 step 6).</summary>
sealed class MatchOp : PhysOp
{
    readonly TMatch t; readonly OntologyView view;
    readonly int nodeCount; readonly int compCount;
    readonly int[][] order;               // per seed component, the order of the other components
    readonly string[] nodeVars; readonly TNode[] nodes; readonly TEdge[] edges;
    readonly Dictionary<(string, string), bool> subtype = [];
    readonly int[] varCol;                // out column per component (entity or rel handle), -1 if the component has no output column
    readonly (int Comp, string PropId, int OutCol, Ty Ty)[] reads;

    public MatchOp(TMatch t, OntologyView view) : base(t)
    {
        this.t = t; this.view = view;
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

    bool Sub(string type, string of)
    {
        if (!subtype.TryGetValue((type, of), out var r)) subtype[(type, of)] = r = view.IsSubtype(type, of);
        return r;
    }

    public override Batch? Run(OpContext cx, Epoch ep, IReadOnlyDictionary<string, Batch?> inputs)
    {
        if (!ep.Graphs.TryGetValue(t.GraphPort, out var gd) || gd.Patch is null) return null;
        var patch = gd.Patch;
        var newView = new NewView(gd.State); var oldView = new OldView(gd.State, patch);
        var bb = new BatchBuilder(OutLayout);
        var bound = new Element?[compCount];
        for (var seed = 0; seed < compCount; seed++)
        {
            var isNode = seed < nodeCount;
            foreach (var edit in patch.Edits)
            {
                if (isNode ? edit.Key.Kind != 'e' : edit.Key.Kind != 'r') continue;
                foreach (var (state, sign) in new[] { (edit.Before, -1L), (edit.After, 1L) })
                {
                    if (state is null || !Fits(seed, state)) continue;
                    Array.Clear(bound);
                    bound[seed] = state;
                    Extend(0, seed, sign, bound, newView, oldView, bb);
                }
            }
        }
        return bb.Build();
    }

    bool Fits(int comp, Element e) => comp < nodeCount
        ? e is Entity en && Sub(en.Type, nodes[comp].TypeId)
        : e is Rel r && Sub(r.Type, edges[comp - nodeCount].TypeId);

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
            if (view.Entity(id) is { } e && Fits(comp, e)) { bound[comp] = e; Extend(step + 1, seed, sign, bound, nv, ov, bb); bound[comp] = null; }
            return;
        }
        var edge = edges[comp - nodeCount];
        var fromNode = NodeIndex(edge.From); var toNode = NodeIndex(edge.To);
        IEnumerable<Rel> candidates;
        if (bound[fromNode] is Entity f) candidates = view.OutRels(f.Id);
        else if (bound[toNode] is Entity to) candidates = view.InRels(to.Id);
        else return;
        foreach (var r in candidates.ToList())
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
        foreach (var (comp, propId, outCol, ty) in reads)
        {
            var props = bound[comp] switch { Entity e => e.Props, Rel r => r.Props, _ => Props.Empty };
            var col = b.Cols[outCol];
            if (props.TryGet(propId, out var v) && v is not NullValue) { if (col.IsDec) col.D[row] = ((DecimalValue)v).Q; else col.L[row] = Codec.ToCode(v, it); }
            else col.SetAbsent(row, b.N);
        }
    }

    Interner it = null!;
    public void Bind(Interner interner) { it = interner; }
}
