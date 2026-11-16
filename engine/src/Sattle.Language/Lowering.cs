using Sattle.Core;

namespace Sattle.Language;

public sealed class LowerResult(DefBuilder builder, List<Diagnostic> diags, Dictionary<string, Pos> map, ModuleAst ast, ProjectionFacts? projections)
{
    public DefBuilder Builder { get; } = builder;
    public List<Diagnostic> Diagnostics { get; } = diags;
    public Dictionary<string, Pos> SourceMap { get; } = map;
    public ModuleAst Ast { get; } = ast;
    /// <summary>The module's projections from files of their own, when they were read; their facts are not in `Builder`.</summary>
    public ProjectionFacts? Projections { get; } = projections;
}

/// <summary>Text to graph (research 03 section 7.2): each construct maps to fixed node and edge types, ids from the containment path.
/// A module text declares the ontology and the spaces; its projections are files of their own, read after the ontology and before the spaces that bind them.</summary>
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
    readonly Func<Graph, ProjectionFacts>? projections;
    ProjectionFacts? read;
    Graph work;

    Lowering(ModuleAst ast, Graph catalog, string file, Func<Graph, ProjectionFacts>? projections)
    {
        this.ast = ast; this.catalog = catalog; this.file = file; this.projections = projections; mid = Ids.Module(ast.Name); work = catalog;
    }

    /// <param name="projections">reads the module's projections against the catalog with the module's ontology; null when the module has none</param>
    public static LowerResult Lower(ModuleAst ast, Graph catalog, string file, Func<Graph, ProjectionFacts>? projections = null)
    {
        var l = new Lowering(ast, catalog, file, projections);
        l.Run();
        return new LowerResult(l.b, l.diags, l.map, ast, l.read);
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

    bool Exists(string id) => b.Nodes.ContainsKey(id) || work.Entity(id) is not null;

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
        if (ast.Doc is not null) b.Node(mid, "Module", ("name", T(ast.Name)), ("description", T(ast.Doc)));
        else b.Node(mid, "Module", ("name", T(ast.Name)));
        Reg(mid, ast.Pos);
        foreach (var im in ast.Imports)
        {
            var rel = $"d:{im.Module}@{im.Version}";
            if (catalog.Entity(rel) is null) { Err("UnknownImport", $"module {im.Module} @ {im.Version} is not loaded", im.Pos); continue; }
            if (!alias.TryAdd(im.Alias, im.Module)) { Err("DuplicateAlias", $"import alias '{im.Alias}' is declared twice", im.Pos); continue; }
            b.Edge(mid, "imports", rel, ("alias", T(im.Alias)));
        }
        Ontology();
        work = catalog.Clone();
        var err = work.Apply([.. b.Elements().Select(Edit.Create)]);
        if (err is not null) { Err("Internal", "module facts do not compose: " + err, ast.Pos); return; }
        if (diags.Any(x => x.IsError)) return;
        if (projections is not null)
        {
            read = projections(work);
            diags.AddRange(read.Diagnostics);
            if (read.Diagnostics.Any(x => x.IsError)) return;
            err = work.Apply([.. read.Projections.Values.SelectMany(f => f).Select(Edit.Create)]);
            if (err is not null) { Err("Internal", "the projections do not compose with the module: " + err, ast.Pos); return; }
        }
        foreach (var s in ast.Decls.OfType<SpaceDecl>()) Space(s);
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
                        if (r.Rollup) p.Add(("rollup", Value.Boolean(true)));
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
