using System.Text;
using Sattle.Core;

namespace Sattle.Language;

public sealed class LowerResult(DefBuilder builder, List<Diagnostic> diags, Dictionary<string, Pos> map, ModuleAst ast)
{
    public DefBuilder Builder { get; } = builder;
    public List<Diagnostic> Diagnostics { get; } = diags;
    public Dictionary<string, Pos> SourceMap { get; } = map;
    public ModuleAst Ast { get; } = ast;
}

/// <summary>Text to graph (research 03 section 7.2): each construct maps to fixed node and edge types, ids from the containment path.</summary>
public sealed class Lowering
{
    readonly ModuleAst ast;
    readonly Graph catalog;
    readonly string file;
    readonly DefBuilder b = new();
    readonly List<Diagnostic> diags = [];
    readonly Dictionary<string, Pos> map = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> alias = new(StringComparer.Ordinal); // alias -> module
    readonly string mid;
    Graph work;
    OntologyView? view;

    readonly string sinkPrefix; readonly bool fragment;
    Lowering(ModuleAst ast, Graph catalog, string file, string sinkPrefix = "", bool fragment = false)
    {
        this.sinkPrefix = sinkPrefix; this.fragment = fragment;
        this.ast = ast; this.catalog = catalog; this.file = file; mid = Ids.Module(ast.Name); work = catalog;
    }

    public static LowerResult Lower(ModuleAst ast, Graph catalog, string file)
    {
        var l = new Lowering(ast, catalog, file);
        l.Run();
        return new LowerResult(l.b, l.diags, l.map, ast);
    }

    /// <summary>Lowers a fragment of a module that is already in the catalog (the expansion of a lift statement): no module node and no import edges (the aliases are read from the imports), sink names carry the prefix. The result holds the facts of the projection (it is merged into the existing one by the caller).</summary>
    public static LowerResult LowerFragment(ModuleAst ast, Graph catalog, string file, string sinkPrefix)
    {
        var l = new Lowering(ast, catalog, file, sinkPrefix, true);
        l.Run();
        return new LowerResult(l.b, l.diags, l.map, ast);
    }

    static Value T(string s) => Value.Text(s);
    void Err(string code, string msg, Pos pos, string focus = "") => diags.Add(new Diagnostic(code, "error", focus, msg, file, pos.Line, pos.Col));
    /// <summary>Records where an element was declared. A second declaration of the same id would replace the first one or merge with it (two key constraints of one type would be one composite key): it is an error.</summary>
    string Reg(string id, Pos pos)
    {
        if (map.TryGetValue(id, out var first) && first != pos)
        {
            var named = id.Split('/').LastOrDefault(s => s.IndexOf(':') is >= 0 and var c && c + 1 < s.Length && s[c + 1] != '#') ?? id;       // a part of an expression has no name: the declaration it belongs to has
            Err("Duplicate", $"{named.Replace(":", " '", StringComparison.Ordinal)}' is declared twice (first at line {first.Line})", pos, id);
        }
        map[id] = pos; return id;
    }

    bool Exists(string id) => b.Nodes.ContainsKey(id) || catalog.Entity(id) is not null;

    string? ModuleOfRef(Ref r)
    {
        if (r.Alias is null) return ast.Name;
        if (alias.TryGetValue(r.Alias, out var m)) return m;
        Err("UnknownAlias", $"unknown import alias '{r.Alias}'", r.Pos);
        return null;
    }

    /// <summary>Resolves a reference to a declaration of one of the given slots, locally or through an import alias (K4).</summary>
    string? Resolve(Ref r, params string[] slots)
    {
        var m = ModuleOfRef(r);
        if (m is null) return null;
        foreach (var s in slots)
        {
            var id = Ids.Child(Ids.Module(m), s, r.Name);
            if (Exists(id)) return id;
        }
        Err("UnknownReference", $"{string.Join(" or ", slots)} '{r}' is not declared" + (r.Alias is null ? " in this module" : $" in module {m}"), r.Pos);
        return null;
    }

    void Run()
    {
        if (!fragment)
        {
            if (ast.Doc is not null) b.Node(mid, "Module", ("name", T(ast.Name)), ("description", T(ast.Doc)));
            else b.Node(mid, "Module", ("name", T(ast.Name)));
            Reg(mid, ast.Pos);
        }
        foreach (var im in ast.Imports)
        {
            var rel = $"d:{im.Module}@{im.Version}";
            if (catalog.Entity(rel) is null) { Err("UnknownImport", $"module {im.Module} @ {im.Version} is not loaded", im.Pos); continue; }
            if (!alias.TryAdd(im.Alias, im.Module)) { Err("DuplicateAlias", $"import alias '{im.Alias}' is declared twice", im.Pos); continue; }
            if (!fragment) b.Edge(mid, "imports", rel, ("alias", T(im.Alias)));
        }
        Ontology();
        work = catalog.Clone();
        var err = work.Apply([.. b.Elements().Select(Edit.Create)]);
        if (err is not null) { Err("Internal", "module facts do not compose: " + err, ast.Pos); return; }
        view = OntologyView.Build(work, Closure());
        foreach (var d in ast.Decls)
            switch (d)
            {
                case ProjectionDecl p: Projection(p); break;
                case SpaceDecl s: Space(s); break;
            }
    }

    HashSet<string> Closure()
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { ast.Name, Ids.KernelModule };
        var todo = new Stack<string>(alias.Values);
        while (todo.Count > 0)
        {
            var m = todo.Pop();
            if (!set.Add(m)) continue;
            var d = new DefView(work);
            foreach (var t in d.Targets(Ids.Module(m), "imports")) todo.Push(Ids.ModuleOf(t));
        }
        return set;
    }

    // ---- ontology declarations ----------------------------------------------------------------------------

    void Ontology()
    {
        var props = new Dictionary<string, (string Kind, string? Item, bool Nullable, string? Unit, TeleMod? Tele, string Id)>();
        string PropNode(Member m)
        {
            var seg = m.Was ?? m.Name;
            var id = Ids.Child(mid, "prop", seg);
            var vk = m.Type.Kind;
            var cur = (vk, m.Type.Item, m.Nullable, m.Unit, m.Tele, id);
            if (props.TryGetValue(seg, out var old))
            {
                if (old.Kind != vk || old.Item != m.Type.Item || old.Nullable != m.Nullable || old.Unit != m.Unit || !Equals(old.Tele, m.Tele))
                    Err("PropertyDisagrees", $"property '{m.Name}' is declared with different kind, unit, role or nullability than an earlier declaration in this module", m.Pos);
                return id;
            }
            props[seg] = cur;
            var p = new List<(string, Value)> { ("name", T(m.Name)), ("nullable", Value.Boolean(m.Nullable)), ("role", T(m.Tele is null ? "graphData" : "telemetry")) };
            if (m.Doc is not null) p.Add(("description", T(m.Doc)));
            if (m.Unit is not null) p.Add(("unit", T(m.Unit)));
            if (m.TreeRole is not null) p.Add(("treeRole", T(m.TreeRole)));
            if (m.Tele is { } tl) { p.Add(("temporality", T(tl.Temporality))); p.Add(("combine", T(tl.Combine))); p.Add(("period", Value.Integer(tl.PeriodNs))); }
            b.Contain(mid, "prop", seg, "PropertyType", null, [.. p]);
            Reg(id, m.Pos);
            foreach (var (rv, rr) in m.Roles ?? [])
                b.Contain(id, "presentation", Ids.ValueSegment(rv), "ValueRole", null, ("value", rv), ("role", T(rr)));
            b.Edge(id, "valueKind", Ids.KBuiltin(vk));
            if (m.Type.Item is { } it) b.Edge(id, "itemKind", Ids.KBuiltin(it));
            if (m.Type.Target is { } tr && Resolve(tr, "entity") is { } te) b.Edge(id, "refersTo", te);
            return id;
        }
        void Members(string owner, List<Member> members)
        {
            foreach (var m in members)
            {
                if (m.Label) b.Edge(owner, "hasProperty", PropNode(m), ("required", Value.Boolean(!m.Optional)), ("label", Value.Boolean(true)));
                else b.Edge(owner, "hasProperty", PropNode(m), ("required", Value.Boolean(!m.Optional)));
                if (m.Label && (m.Type.Kind != "Text" || m.Tele is not null)) Err("Label", $"the label '{m.Name}' must be a Text property of the graph data", m.Pos);
            }
            if (members.Where(m => m.Label).Skip(1).FirstOrDefault() is { } second) Err("Label", $"a type has one label: '{second.Name}' is a second one", second.Pos);
        }
        foreach (var d in ast.Decls)
        {
            switch (d)
            {
                case EntityDecl e:
                    {
                        var seg = e.Was ?? e.Name;
                        var p = new List<(string, Value)> { ("name", T(e.Name)), ("abstract", Value.Boolean(e.Abstract)) };
                        if (e.Doc is not null) p.Add(("description", T(e.Doc)));
                        var id = b.Contain(mid, "entity", seg, "EntityType", null, [.. p]); Reg(id, e.Pos);
                        foreach (var s in e.Supers) if (Resolve(s, "entity") is { } sid) b.Edge(id, "extends", sid);
                        Members(id, e.Members);
                        break;
                    }
                case RelDecl r:
                    {
                        var seg = r.Was ?? r.Name;
                        var p = new List<(string, Value)> { ("name", T(r.Name)), ("abstract", Value.Boolean(r.Abstract)) };
                        if (r.Doc is not null) p.Add(("description", T(r.Doc)));
                        if (r.Hierarchy) p.Add(("hierarchy", Value.Boolean(true)));
                        if (r.Kind is not null) p.Add(("kind", T(r.Kind)));
                        var id = b.Contain(mid, "rel", seg, "RelationshipType", null, [.. p]); Reg(id, r.Pos);
                        if (r.Super is not null && Resolve(r.Super, "rel") is { } sid) b.Edge(id, "extends", sid);
                        foreach (var s in r.Sources) if (Resolve(s, "entity") is { } x) b.Edge(id, "sourceType", x);
                        foreach (var t in r.Targets) if (Resolve(t, "entity") is { } x) b.Edge(id, "targetType", x);
                        Members(id, r.Members);
                        break;
                    }
                case ExtendDecl x:
                    {
                        var target = Resolve(x.Target, "entity", "rel");
                        if (target is null) break;
                        var p = new List<(string, Value)> { ("name", T(x.Target.ToString())) };
                        if (x.Doc is not null) p.Add(("description", T(x.Doc)));
                        var id = b.Contain(mid, "extend", x.Target.ToString(), "Extension", null, [.. p]); Reg(id, x.Pos);
                        b.Edge(id, "extendsType", target);
                        Members(id, x.Members);
                        break;
                    }
            }
        }
        // second pass: declarations that refer to types declared above
        var local = new Graph();
        local.Apply([.. b.Elements().Select(Edit.Create)]);
        var tmp = catalog.Clone(); tmp.Apply([.. b.Elements().Select(Edit.Create)]);
        var tview = OntologyView.Build(tmp, Closure());
        foreach (var d in ast.Decls)
        {
            switch (d)
            {
                case ConstraintDecl c: Constraint(c, tview); break;
                case IdentityDecl i: Identity(i); break;
                case EquivalenceDecl q:
                    {
                        var id = b.Contain(mid, "equivalence", q.Name, "EquivalencePolicy", null, ("name", T(q.Name)), ("mode", T(q.Mode)), ("threshold", Value.Decimal(q.Threshold)));
                        Reg(id, q.Pos);
                        var a = Resolve(q.A, "identity"); var z = Resolve(q.B, "identity");
                        if (a is not null) b.Edge(id, "between", a, ("ordinal", Value.Integer(0)));
                        if (z is not null) b.Edge(id, "between", z, ("ordinal", Value.Integer(1)));
                        break;
                    }
                case RowsDecl r:
                    {
                        var id = b.Contain(mid, "rows", r.Name, "RowType", null, [.. DocName(r.Name, r.Doc)]); Reg(id, r.Pos);
                        var k = 0;
                        foreach (var f in r.Fields)
                        {
                            var p = new List<(string, Value)> { ("name", T(f.Name)), ("optional", Value.Boolean(f.Optional)) };
                            if (f.Unit is not null) p.Add(("unit", T(f.Unit)));
                            var fid = b.Contain(id, "field", f.Name, "Field", k++, [.. p]); Reg(fid, f.Pos);
                            b.Edge(fid, "valueKind", Ids.KBuiltin(f.Type.Kind));
                            if (f.Type.Item is { } it) b.Edge(fid, "itemKind", Ids.KBuiltin(it));
                        }
                        break;
                    }
            }
        }
    }

    static (string, Value)[] DocName(string name, string? doc) => doc is null ? [("name", T(name))] : [("name", T(name)), ("description", T(doc))];

    void Constraint(ConstraintDecl c, OntologyView tv)
    {
        var target = Resolve(c.Target, "entity", "rel");
        if (target is null) return;
        var name = c.Name ?? (c.Prop is null ? $"{c.Kind}-{c.Target.Name}" : $"{c.Kind}-{c.Target.Name}.{c.Prop}");
        var p = new List<(string, Value)> { ("name", T(name)), ("kind", T(c.Kind)), ("severity", T(c.Warning ? "warning" : "error")) };
        if (c.Doc is not null) p.Add(("description", T(c.Doc)));
        if (c.Kind == "enum") p.Add(("values", Value.List(c.Values.Select(Value.Text))));
        if (c.Kind == "pattern") p.Add(("pattern", T(c.Pattern!)));
        if (c.Kind == "range") { if (c.Min is { } mn) p.Add(("min", Value.Decimal(mn))); if (c.Max is { } mx) p.Add(("max", Value.Decimal(mx))); }
        if (c.Kind == "cardinality") { p.Add(("side", T(c.Side ?? "source"))); p.Add(("min", Value.Decimal(c.CardMin))); if (c.CardMax >= 0) p.Add(("max", Value.Decimal(c.CardMax))); }
        var id = b.Contain(mid, "constraint", name, "Constraint", null, [.. p]); Reg(id, c.Pos);
        b.Edge(id, "constrains", target);
        if (c.Prop is not null)
        {
            var pid = PropByName(tv, target, c.Prop, c.Pos);
            if (pid is not null) b.Edge(id, "onProperty", pid, ("ordinal", Value.Integer(0)));
        }
        var ord = 0;
        foreach (var kp in c.KeyProps)
            if (PropByName(tv, target, kp, c.Pos) is { } kid) b.Edge(id, "onProperty", kid, ("ordinal", Value.Integer(ord++)));
    }

    string? PropByName(OntologyView v, string typeId, string name, Pos pos)
    {
        if (!v.Types.ContainsKey(typeId)) { Err("UnknownReference", $"type {Ids.Name(typeId)} has no definition in the closure", pos); return null; }
        var hits = v.PropsOf(typeId).Keys.Where(k => v.Props.TryGetValue(k, out var pi) && pi.Name == name).ToList();
        if (hits.Count == 1) return hits[0];
        Err(hits.Count == 0 ? "UnknownProperty" : "AmbiguousProperty", $"{(hits.Count == 0 ? "no property" : "several properties")} named '{name}' on {Ids.Name(typeId)}", pos);
        return null;
    }

    static string Pascal(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    void Identity(IdentityDecl i)
    {
        var id = b.Contain(mid, "identity", i.Name, "IdentityDomain", null, [.. DocName(i.Name, i.Doc), ("canonicalizationVersion", T(i.Version))]); Reg(id, i.Pos);
        if (i.For is not null && Resolve(i.For, "entity") is { } t) b.Edge(id, "identifies", t);
        var k = 0;
        foreach (var c in i.Components)
        {
            var cid = b.Contain(id, "component", c.Name, "KeyComponent", k++, ("name", T(c.Name))); Reg(cid, c.Pos);
            b.Edge(cid, "valueKind", Ids.KBuiltin(c.Kind));
            var canon = Ids.KBuiltin("canon." + Pascal(c.Canonicalizer ?? "exact"));
            if (Kernel.Graph.Entity(canon) is null) Err("UnknownCanonicalizer", $"unknown canonicalizer '{c.Canonicalizer}'", c.Pos);
            else b.Edge(cid, "canonicalizer", canon);
        }
    }

    // ---- projections --------------------------------------------------------------------------------------

    sealed class ProjCtx
    {
        public required string Id;
        public required string Name;
        public Dictionary<string, string> Out = new(StringComparer.Ordinal);   // name -> producing port id
        public Dictionary<string, string> GraphPorts = new(StringComparer.Ordinal);
        public int EmitCount;
        public Dictionary<string, int> RuleCount = new(StringComparer.Ordinal);
        public Dictionary<string, string> RuleClass = new(StringComparer.Ordinal);
    }

    void Projection(ProjectionDecl pd)
    {
        var pid = Ids.Child(mid, "projection", pd.Name);
        var outMod = pd.Output == ast.Name ? ast.Name : alias.GetValueOrDefault(pd.Output);
        if (outMod is null) { Err("UnknownAlias", $"output '{pd.Output}' is neither this module nor an import alias", pd.Pos); outMod = ast.Name; }
        var p = new List<(string, Value)> { ("name", T(pd.Name)), ("purpose", T(pd.Purpose)), ("retain", Value.Integer(pd.Retain ?? 0)), ("forward", Value.Boolean(pd.Forward)) };
        if (pd.Doc is not null) p.Add(("description", T(pd.Doc)));
        b.Contain(mid, "projection", pd.Name, "Projection", null, [.. p]); Reg(pid, pd.Pos);
        b.Edge(pid, "outputOntology", Ids.Module(outMod));
        var cx = new ProjCtx { Id = pid, Name = pd.Name };
        foreach (var port in pd.Ports)
        {
            var id = port.Default is null ? b.Contain(pid, "port", port.Name, "Port", null, ("name", T(port.Name)), ("direction", T("in")), ("kind", T(port.Kind)))
                : b.Contain(pid, "port", port.Name, "Port", null, ("name", T(port.Name)), ("direction", T("in")), ("kind", T(port.Kind)), ("default", port.Default)); Reg(id, port.Pos);
            if (port.Kind == "param")
            {
                if (port.Target.Alias is not null || port.Target.Name is not ("Text" or "Integer" or "Decimal" or "Boolean")) Err("ParamKind", $"parameter type '{port.Target}' must be Text, Integer, Decimal or Boolean", port.Pos);
                else b.Edge(id, "valueKind", Ids.KBuiltin(port.Target.Name));
            }
            else if (port.Kind == "rows") { if (Resolve(port.Target, "rows") is { } rt) b.Edge(id, "rowType", rt); cx.Out[port.Name] = id; }
            else
            {
                // `in master: graph ops;` names an import alias (or this module)
                var mname = alias.GetValueOrDefault(port.Target.Name) ?? (port.Target.Name == ast.Name ? ast.Name : null);
                if (mname is null) Err("UnknownAlias", $"graph port expects '{port.Target}', which is neither this module nor an import alias", port.Target.Pos);
                else b.Edge(id, "expects", Ids.Module(mname));
                cx.GraphPorts[port.Name] = id; cx.Out[port.Name] = id;
            }
        }
        foreach (var item in pd.Items)
        {
            try
            {
                switch (item)
                {
                    case Stmt s: Statement(cx, s, null); break;
                    case RuleItem r: Rule(cx, r); break;
                }
            }
            catch (LowerError e) { Err(e.Code, e.Message, e.Pos); }
        }
    }

    sealed class LowerError(string code, string message, Pos pos) : Exception(message) { public string Code { get; } = code; public Pos Pos { get; } = pos; }

    void Rule(ProjCtx cx, RuleItem r)
    {
        var p = new List<(string, Value)> { ("name", T(r.Name)), ("class", T(r.Class)), ("putput", Value.Boolean(r.PutPut)) };
        if (r.Reason is not null) p.Add(("reason", T(r.Reason)));
        if (r.Policy is { } pol)
        {
            if (pol.Accept.Count > 0) p.Add(("accept", Value.List(pol.Accept.Select(Value.Text))));
            if (pol.OnDelete is not null) p.Add(("onDelete", T(pol.OnDelete)));
        }
        var rid = b.Contain(cx.Id, "rule", r.Name, "Rule", null, [.. p]); Reg(rid, r.Pos);
        cx.RuleClass[r.Name] = r.Class;
        if (r.Policy is { CreateKey: { } ck } pl)
        {
            var dom = Resolve(ck, "identity");
            if (dom is not null)
            {
                b.Edge(rid, "createKey", dom);
                var target = work.Entity(dom) is null ? null : new DefView(work).Target(dom, "identifies");
                void Sets(string slot, List<(string Name, Expr Value, Pos Pos)> items)
                {
                    foreach (var (name, value, pos) in items)
                    {
                        var pid = target is null || view is null ? null : PropByName(view, target, name, pos);
                        var sid = b.Contain(rid, slot, name, "SetProperty"); Reg(sid, pos);
                        if (pid is not null) b.Edge(sid, "property", pid);
                        Expr(value, sid, "value", 0, cx);
                    }
                }
                Sets("createDefault", pl.CreateDefault);
                Sets("deleteSet", pl.DeleteSet);
            }
        }
        else if (r.Policy is { OnDelete: not null } pl2 && pl2.DeleteSet.Count > 0) Err("Policy", "on delete set needs an 'on create key <domain>' to name the master entity type", r.Policy.Pos);
        foreach (var s in r.Stmts)
        {
            try { Statement(cx, s, (rid, r.Name)); }
            catch (LowerError e) { Err(e.Code, e.Message, e.Pos); }
        }
    }

    string Op(ProjCtx cx, string kind, string name, Pos pos, params (string, Value)[] props)
    {
        var id = b.Contain(cx.Id, "op", name, kind, null, [("name", T(name)), .. props]); Reg(id, pos);
        foreach (var (pn, dir, pk) in KernelTable.OperatorPorts(kind)) b.Contain(id, "port", pn, "Port", null, ("name", T(pn)), ("direction", T(dir)), ("kind", T(pk)));
        return id;
    }
    static string Port(string op, string name) => Ids.Child(op, "port", name);

    string Source(ProjCtx cx, string name, Pos pos) =>
        cx.Out.TryGetValue(name, out var port) ? port : throw new LowerError("UnknownName", $"'{name}' is not a port or a let in this projection", pos);

    void Statement(ProjCtx cx, Stmt s, (string Id, string Name)? rule)
    {
        string SinkName() => rule is { } r ? $"{r.Name}.{cx.RuleCount[r.Name] = cx.RuleCount.GetValueOrDefault(r.Name) + 1}" : $"{sinkPrefix}emit.{++cx.EmitCount}";
        void Govern(string sink) { if (rule is { } r) b.Edge(r.Id, "governs", sink); }
        switch (s)
        {
            case LetStmt l: Let(cx, l); break;
            case EmitEntityStmt e:
                {
                    var type = Resolve(e.Type, "entity") ?? throw new LowerError("Skipped", "unresolved", e.Pos);
                    var name = SinkName();
                    var op = Op(cx, "EmitEntity", name, e.Pos, ("mode", T(e.Update ? "update" : "create")));
                    b.Edge(op, "ofType", type);
                    b.Edge(Source(cx, e.From, e.Pos), "wire", Port(op, "in"));
                    Expr(e.Id, op, "id", 0, cx);
                    Sets(op, type, e.Set, cx);
                    Govern(op);
                    break;
                }
            case EmitRelStmt e:
                {
                    var type = Resolve(e.Type, "rel") ?? throw new LowerError("Skipped", "unresolved", e.Pos);
                    var name = SinkName();
                    var op = Op(cx, "EmitRelationship", name, e.Pos, ("mode", T(e.Update ? "update" : "create")), ("key", T(e.KeyName)), ("retype", T(e.Retype ?? "reject")));
                    b.Edge(op, "ofType", type);
                    b.Edge(Source(cx, e.From, e.Pos), "wire", Port(op, "in"));
                    Expr(e.Source, op, "source", 0, cx);
                    Expr(e.Target, op, "target", 0, cx);
                    Sets(op, type, e.Set, cx);
                    Govern(op);
                    break;
                }
            case LiftStmt l:
                {
                    if (rule is { } lr && cx.RuleClass.GetValueOrDefault(lr.Name) != "readonly") throw new LowerError("Lift", "a lift statement is allowed in a forward projection or in a readonly rule, not in a bijective or lens rule (its edges cannot be edited back)", l.Pos);
                    var emitType = Resolve(l.Emit, "rel") ?? throw new LowerError("Skipped", "unresolved", l.Pos);
                    var edgeType = Resolve(l.EdgeType, "rel") ?? throw new LowerError("Skipped", "unresolved", l.Pos);
                    var mapType = Resolve(l.MapType, "rel") ?? throw new LowerError("Skipped", "unresolved", l.Pos);
                    var liftProps = new List<(string, Value)> { ("within", Value.Integer(l.WithinNs)), ("selfLoops", T(l.SelfLoops)) };
                    if (l.Images is not null) liftProps.Add(("images", T(l.Images)));
                    var op = Op(cx, "Lift", l.Name, l.Pos, [.. liftProps]);
                    b.Edge(op, "ofType", emitType); b.Edge(op, "liftEdge", edgeType); b.Edge(op, "liftVia", mapType);
                    if (!cx.GraphPorts.TryGetValue(l.EdgesGraph, out var eg)) throw new LowerError("UnknownName", $"'{l.EdgesGraph}' is not a graph port", l.Pos);
                    if (!cx.GraphPorts.TryGetValue(l.MapGraph, out var mg)) throw new LowerError("UnknownName", $"'{l.MapGraph}' is not a graph port", l.Pos);
                    b.Edge(eg, "wire", Port(op, "edges")); b.Edge(mg, "wire", Port(op, "map"));
                    cx.Out[l.Name] = Port(op, "out");
                    Govern(op);
                    if (l.When is not null) Expr(l.When, op, "when", 0, cx);
                    var k = 0;
                    foreach (var (target, source, pos) in l.Carry)
                    {
                        var a = b.Contain(op, "carry", target, "Assign", k++, ("target", T(target))); Reg(a, pos);
                        Expr(new PathExpr(source, pos), a, "value", 0, cx);
                    }
                    k = 0;
                    foreach (var (kname, kpath, pos) in l.Keep)
                    {
                        var a = b.Contain(op, "keep", kname, "Assign", k++, ("target", T(kname))); Reg(a, pos);
                        Expr(new PathExpr(kpath, pos), a, "value", 0, cx);
                    }
                    k = 0;
                    foreach (var (target, value, pos) in l.Derive)
                    {
                        var a = b.Contain(op, "derive", target, "Assign", k++, ("target", T(target))); Reg(a, pos);
                        Expr(value, a, "value", 0, cx);
                    }
                    k = 0;
                    foreach (var st in l.States)
                    {
                        var sid = b.Contain(op, "state", st.Target, "LiftState", k++, ("target", T(st.Target)), ("of", T(st.Of)), ("above", Value.Decimal(st.Above)), ("enterAfter", Value.Integer(st.EnterAfter)), ("high", T(st.High)),
                            ("below", Value.Decimal(st.Below)), ("exitAfter", Value.Integer(st.ExitAfter)), ("low", T(st.Low)), ("initial", T(st.Initial)));
                        Reg(sid, st.Pos);
                    }
                    break;
                }
            case LinkStmt l:
                {
                    var pol = Resolve(l.Policy, "equivalence") ?? throw new LowerError("Skipped", "unresolved", l.Pos);
                    var name = SinkName();
                    var op = Op(cx, "Link", name, l.Pos);
                    b.Edge(op, "policy", pol);
                    b.Edge(Source(cx, l.From, l.Pos), "wire", Port(op, "in"));
                    if (l.Left is not IdExpr) throw new LowerError("Syntax", "link left must be an identity expression", l.Left.Pos);
                    if (l.Right is not IdExpr) throw new LowerError("Syntax", "link right must be an identity expression", l.Right.Pos);
                    Expr(l.Left, op, "left", 0, cx); Expr(l.Right, op, "right", 0, cx); Expr(l.Confidence, op, "confidence", 0, cx);
                    Govern(op);
                    break;
                }
        }
    }

    void Sets(string op, string typeId, List<(string Name, Expr Value, Pos Pos)> set, ProjCtx cx)
    {
        foreach (var (name, value, pos) in set)
        {
            var pid = view is null ? null : PropByName(view, typeId, name, pos);
            var sid = b.Contain(op, "set", name, "SetProperty"); Reg(sid, pos);
            if (pid is not null) b.Edge(sid, "property", pid);
            Expr(value, sid, "value", 0, cx);
        }
    }

    void Let(ProjCtx cx, LetStmt l)
    {
        var pipe = l.Pipe;
        if (pipe.Source is FixpointSource fs)
        {
            // the fixpoint operator is the first op of the let group, the step chain follows it; the let's own name is the relation inside the step
            if (Count(fs.Step) == 0) throw new LowerError("Fixpoint", "the step of a fixpoint needs at least one operator", fs.Pos);
            if (pipe.Steps.Count > 0) throw new LowerError("Fixpoint", $"steps after the `max` of a fixpoint are not part of it: bind the fixpoint with its own let and continue from '{l.Name}'", pipe.Steps[0].Pos);
            var n = 1 + Count(fs.Step);
            string Name(int i) => i == n - 1 ? l.Name : $"{l.Name}#{i}";
            var fp = Op(cx, "Fixpoint", Name(0), fs.Pos, ("maxFacts", Value.Integer(1_000_000)), ("maxIterations", Value.Integer(fs.Max)));
            b.Edge(Source(cx, fs.Seed, fs.Pos), "wire", Port(fp, "seed"));
            cx.Out[l.Name] = Port(fp, "out");
            var idx = 1;
            var last = Chain(cx, fs.Step, Name, n, ref idx);
            b.Edge(fp, "over", last);
            return;
        }
        var count = Count(pipe);
        if (count == 0) { cx.Out[l.Name] = Source(cx, ((IdentSource)pipe.Source).Name, pipe.Pos); return; }
        var i0 = 0;
        cx.Out[l.Name] = Chain(cx, pipe, i => i == count - 1 ? l.Name : $"{l.Name}#{i}", count, ref i0);
    }

    static int Count(Pipeline pipe) => (pipe.Source is MatchSource or UnionSource or JoinSource or ValuesSource ? 1 : 0) + pipe.Steps.Count;

    /// <summary>Lowers a source and its steps to operators named by opName; returns the last output port (or the source port when there are no operators).</summary>
    string Chain(ProjCtx cx, Pipeline pipe, Func<int, string> OpName, int n, ref int idx)
    {
        string? prev;
        if (pipe.Source is MatchSource m)
        {
            var op = Op(cx, "Match", OpName(idx++), m.Pos);
            if (!cx.GraphPorts.TryGetValue(m.Graph, out var gp)) throw new LowerError("UnknownName", $"'{m.Graph}' is not a graph port", m.Pos);
            b.Edge(gp, "wire", Port(op, "graph"));
            MatchPatterns(op, m);
            prev = Port(op, "out");
        }
        else if (pipe.Source is ValuesSource vs)
        {
            var op = Op(cx, "Values", OpName(idx++), vs.Pos);
            for (var r = 0; r < vs.Rows.Count; r++)
            {
                var row = b.Contain(op, "row", "#" + r, "RowValue", r);
                foreach (var (an, av) in vs.Rows[r])
                {
                    var asg = b.Contain(row, "assign", an, "Assign", null, ("target", T(an)));
                    Expr(av, asg, "value", 0, cx);
                }
            }
            prev = Port(op, "out");
        }
        else if (pipe.Source is JoinSource js)
        {
            var op = Op(cx, "Join", OpName(idx++), js.Pos);
            b.Edge(Source(cx, js.A, js.Pos), "wire", Port(op, "left"));
            b.Edge(Source(cx, js.B, js.Pos), "wire", Port(op, "right"));
            Expr(js.On, op, "on", 0, cx);
            prev = Port(op, "out");
        }
        else if (pipe.Source is UnionSource u)
        {
            var op = Op(cx, "Union", OpName(idx++), u.Pos, ("conflict", T(u.Conflict)));
            b.Edge(Source(cx, u.A, u.Pos), "wire", Port(op, "in0"));
            b.Edge(Source(cx, u.B, u.Pos), "wire", Port(op, "in1"));
            prev = Port(op, "out");
        }
        else prev = Source(cx, ((IdentSource)pipe.Source).Name, pipe.Pos);
        foreach (var step in pipe.Steps)
        {
            var name = OpName(idx++);
            string op;
            switch (step)
            {
                case WhereStep w:
                    op = Op(cx, "Filter", name, w.Pos); Expr(w.Pred, op, "predicate", 0, cx); break;
                case SelectStep sel:
                    op = Op(cx, "Map", name, sel.Pos);
                    for (var k = 0; k < sel.Record.Count; k++)
                    {
                        var a = b.Contain(op, "assign", sel.Record[k].Name, "Assign", k, ("target", T(sel.Record[k].Name)));
                        Expr(sel.Record[k].Value, a, "value", 0, cx);
                    }
                    break;
                case GroupStep g:
                    op = Op(cx, "Group", name, g.Pos); Keys(op, g.Keys, cx); Aggs(op, g.Aggs, cx); break;
                case TraverseStep tr:
                    {
                        op = Op(cx, "Traverse", name, tr.Pos, ("direction", T(tr.Direction)), ("minDepth", Value.Integer(tr.Min)), ("maxDepth", Value.Integer(tr.Max)), ("start", T(tr.From)), ("target", T(tr.As)));
                        if (!cx.GraphPorts.TryGetValue(tr.Graph, out var tgp)) throw new LowerError("UnknownName", $"'{tr.Graph}' is not a graph port", tr.Pos);
                        b.Edge(tgp, "wire", Port(op, "graph"));
                        if (Resolve(tr.Rel, "rel") is { } rt) b.Edge(op, "ofType", rt); else throw new LowerError("Skipped", "unresolved", tr.Pos);
                        break;
                    }
                case DistinctStep ds:
                    op = Op(cx, "Distinct", name, ds.Pos); break;
                case RetainStep rs:
                    op = Op(cx, "Retain", name, rs.Pos, ("timestampField", T(rs.On)), ("age", Value.Integer(rs.AgeNs))); break;
                case WindowStep w:
                    op = Op(cx, "Window", name, w.Pos, ("timestampField", T(w.On)), ("width", Value.Integer(w.WidthNs)), ("late", T(w.Late))); break;
                case SeriesStep sr:
                    op = sr.TtlNs is { } ttl ? Op(cx, "Series", name, sr.Pos, ("within", Value.Integer(sr.WithinNs)), ("ttl", Value.Integer(ttl))) : Op(cx, "Series", name, sr.Pos, ("within", Value.Integer(sr.WithinNs)));
                    if (!cx.GraphPorts.TryGetValue(sr.Graph, out var gp)) throw new LowerError("UnknownName", $"'{sr.Graph}' is not a graph port", sr.Pos);
                    b.Edge(gp, "wire", Port(op, "graph"));
                    for (var k = 0; k < sr.Paths.Count; k++)
                    {
                        var r = sr.Fills?[k] is { } fill ? b.Contain(op, "read", "#" + k, "FieldRef", k, ("path", T(sr.Paths[k])), ("fill", Value.Decimal(fill))) : b.Contain(op, "read", "#" + k, "FieldRef", k, ("path", T(sr.Paths[k])));
                        Reg(r, sr.Pos);
                    }
                    break;
                case RollupStep ru:
                    op = ru.NoDataAny ? Op(cx, "Rollup", name, ru.Pos, ("nodata", T("any"))) : Op(cx, "Rollup", name, ru.Pos); Keys(op, ru.Keys, cx); Aggs(op, ru.Aggs, cx); break;
                case ThresholdStep th:
                    op = Op(cx, "Threshold", name, th.Pos,
                    [
                        ("target", T(th.As)), ("above", Value.Decimal(th.Above)), ("enterAfter", Value.Integer(th.EnterAfter)), ("high", T(th.High)),
                        ("below", Value.Decimal(th.Below)), ("exitAfter", Value.Integer(th.ExitAfter)), ("low", T(th.Low)), ("initial", T(th.Initial)),
                        .. th.NoData is { } nd ? new[] { ("nodata", T(nd)) } : [], .. th.NoDataIgnore ? new[] { ("nodataIgnore", Value.Boolean(true)) } : [],
                    ]);
                    Keys(op, th.Keys, cx); Expr(th.Value, op, "value", 0, cx);
                    foreach (var (e, slot) in new[] { (th.AboveE, "aboveBy"), (th.EnterE, "enterBy"), (th.BelowE, "belowBy"), (th.ExitE, "exitBy") }) if (e is not null) Expr(e, op, slot, 0, cx);
                    break;
                default: throw new LowerError("Unsupported", "unsupported step", step.Pos);
            }
            b.Edge(prev, "wire", Port(op, "in"));
            prev = Port(op, "out");
        }
        return prev;
    }

    void Keys(string op, List<(string Name, Expr Value)> keys, ProjCtx cx)
    {
        foreach (var (name, value) in keys)
        {
            var a = b.Contain(op, "key", name, "Assign", null, ("target", T(name)));
            Expr(value, a, "value", 0, cx);
        }
    }

    void Aggs(string op, List<AggDecl> aggs, ProjCtx cx)
    {
        foreach (var a in aggs)
        {
            var id = b.Contain(op, "agg", a.Target, "Aggregate", null, ("target", T(a.Target)), ("fn", T(a.Fn))); Reg(id, a.Pos);
            if (a.Value is not null) Expr(a.Value, id, "value", 0, cx);
            if (a.Weight is not null) Expr(a.Weight, id, a.Fn == "last" ? "order" : "weight", 0, cx);
        }
    }

    void MatchPatterns(string op, MatchSource m)
    {
        var nodeTypes = new Dictionary<string, string?>();
        var k = 0;
        foreach (var path in m.Paths)
        {
            foreach (var n in path.Nodes)
            {
                string? type = n.Type is null ? null : Resolve(n.Type, "entity");
                if (nodeTypes.TryGetValue(n.Var, out var old))
                {
                    if (type is not null && old is not null && old != type) Err("PatternType", $"variable '{n.Var}' is bound to two types", n.Pos);
                    if (type is not null && old is null) nodeTypes[n.Var] = type;
                }
                else nodeTypes[n.Var] = type;
            }
        }
        foreach (var (v, t) in nodeTypes)
        {
            var id = b.Contain(op, "node", v, "PatternNode", null, ("var", T(v)));
            if (t is null) Err("PatternType", $"variable '{v}' has no type in the pattern", m.Pos);
            else b.Edge(id, "ofType", t);
        }
        foreach (var path in m.Paths)
            for (var i = 0; i < path.Edges.Count; i++)
            {
                var e = path.Edges[i];
                var seg = e.Var ?? "#" + k++;
                var props = e.Var is null ? [] : new[] { ("var", T(e.Var)) };
                var id = b.Contain(op, "edge", seg, "PatternEdge", null, props); Reg(id, e.Pos);
                if (Resolve(e.Type, "rel") is { } rt) b.Edge(id, "ofType", rt);
                var (from, to) = e.Forward ? (path.Nodes[i], path.Nodes[i + 1]) : (path.Nodes[i + 1], path.Nodes[i]);
                b.Edge(id, "from", Ids.Child(op, "node", from.Var));
                b.Edge(id, "to", Ids.Child(op, "node", to.Var));
            }
    }

    // ---- expressions ----

    string Expr(Expr e, string parent, string slot, int ordinal, ProjCtx cx, string? name = null, bool ordered = false)
    {
        var seg = name ?? "#" + ordinal;
        switch (e)
        {
            case PathExpr p:
                return Reg(b.Contain(parent, slot, seg, "FieldRef", ordered ? ordinal : null, ("path", T(p.Path))), e.Pos);
            case LitExpr l:
                return Reg(b.Contain(parent, slot, seg, "Literal", ordered ? ordinal : null, ("value", l.Value)), e.Pos);
            case CallExpr c:
                {
                    var fid = Ids.KBuiltin(c.Fn);
                    if (Kernel.Graph.Entity(fid) is null) { Err("UnknownFunction", $"unknown function '{char.ToLowerInvariant(c.Fn[0]) + c.Fn[1..]}'", c.Pos); }
                    var id = b.Contain(parent, slot, seg, "Call", ordered ? ordinal : null); Reg(id, e.Pos);
                    if (Kernel.Graph.Entity(fid) is not null) b.Edge(id, "function", fid);
                    for (var i = 0; i < c.Args.Count; i++) Expr(c.Args[i], id, "arg", i, cx, null, true);
                    return id;
                }
            case IdExpr x:
                {
                    var id = b.Contain(parent, slot, seg, "IdOf", ordered ? ordinal : null); Reg(id, e.Pos);
                    var dom = Resolve(x.Domain, "identity");
                    if (dom is not null) b.Edge(id, "domain", dom);
                    foreach (var (cname, cexpr, cpos) in x.Components)
                    {
                        var sc = b.Contain(id, "component", cname, "SetComponent"); Reg(sc, cpos);
                        if (dom is not null)
                        {
                            var kc = Ids.Child(dom, "component", cname);
                            if (Exists(kc)) b.Edge(sc, "component", kc); else Err("UnknownComponent", $"identity domain {x.Domain} has no component '{cname}'", cpos);
                        }
                        Expr(cexpr, sc, "value", 0, cx);
                    }
                    return id;
                }
            default: throw new InvalidOperationException();
        }
    }

    // ---- graph spaces ----

    void Space(SpaceDecl s)
    {
        var sid = b.Contain(mid, "space", s.Name, "GraphSpace", null, [.. DocName(s.Name, s.Doc)]); Reg(sid, s.Pos);
        foreach (var src in s.Sources)
        {
            var id = b.Contain(sid, "source", src.Name, "Source", null, ("name", T(src.Name)), ("adapter", T(src.Adapter))); Reg(id, src.Pos);
            if (Resolve(src.Rows, "rows") is { } rt) b.Edge(id, "provides", rt);
        }
        foreach (var g in s.Graphs)
        {
            var id = b.Contain(sid, "graph", g.Name, "Graph", null, ("name", T(g.Name))); Reg(id, g.Pos);
            var proj = Resolve(g.Projection, "projection");
            if (proj is not null) b.Edge(id, "producedBy", proj);
            if (g.Master) b.Edge(sid, "master", id);
            foreach (var (port, from, literal) in g.Args)
            {
                var bid = literal is null ? b.Contain(sid, "binding", $"{g.Name}.{port}", "Binding") : b.Contain(sid, "binding", $"{g.Name}.{port}", "Binding", null, ("value", literal)); Reg(bid, g.Pos);
                if (proj is not null)
                {
                    var pp = Ids.Child(proj, "port", port);
                    if (Exists(pp)) b.Edge(bid, "bindsPort", pp); else Err("UnknownPort", $"projection {g.Projection} has no port '{port}'", g.Pos);
                }
                if (literal is not null) continue;
                var target = Ids.Child(sid, "source", from);
                if (!b.Nodes.ContainsKey(target)) target = Ids.Child(sid, "graph", from);
                if (!b.Nodes.ContainsKey(target) && !s.Graphs.Any(x => x.Name == from)) Err("UnknownName", $"'{from}' is neither a source nor a graph of the space", g.Pos);
                else b.Edge(bid, "boundFrom", target);
            }
        }
    }
}
