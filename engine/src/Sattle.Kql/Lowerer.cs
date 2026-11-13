using System.Collections.Immutable;
using System.Globalization;
using Kusto.Language;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;
using Sattle.Core;
using Diagnostic = Sattle.Core.Diagnostic;

namespace Sattle.Kql;

/// <summary>An expression as the engine's definition graph holds it.</summary>
abstract record X;
sealed record FieldX(string Path) : X;
sealed record LitX(Value V) : X;
sealed record CallX(string Fn, params X[] Args) : X;
sealed record IdX(string Domain, (string Component, X Value)[] Parts) : X;

/// <summary>A lowered table: the output port that carries its rows and what the lowering knows about its columns.</summary>
sealed class Rel
{
    public required string Port;
    public List<string> Cols = [];
    /// <summary>The element type of a column that holds an element, absent for data.</summary>
    public Dictionary<string, string> Types = new(StringComparer.Ordinal);
    /// <summary>Columns made by identity(...): not a value in a row but its parts, carried as the columns `name__1`, `name__2`, ...</summary>
    public Dictionary<string, IdX> Idents = new(StringComparer.Ordinal);
    /// <summary>Columns the last projection set to a constant.</summary>
    public Dictionary<string, Value> Lits = new(StringComparer.Ordinal);
    /// <summary>The value kind of a telemetry column, from its property in the ontology.</summary>
    public Dictionary<string, string> Kinds = new(StringComparer.Ordinal);
    /// <summary>Rows of event-time windows, `bin(time, width)`: a summary keyed by windowStart groups them.</summary>
    public bool EventWindows;
    /// <summary>One row per telemetry window (after a series or a roll-up).</summary>
    public bool Windowed;
    /// <summary>The rows of a telemetry join: one column per property in place of KQL's `property` and `value` columns.</summary>
    public (string Property, string Value, string[] Props)? Long;

    public Rel Next(string port) => new() { Port = port, Cols = [.. Cols], Types = new(Types), Idents = new(Idents), Kinds = new(Kinds), Windowed = Windowed, EventWindows = EventWindows, Long = Long };
    public static bool Hidden(string c) => c.Contains("__", StringComparison.Ordinal);
    public IEnumerable<string> Visible => Cols.Where(c => !Hidden(c)).Concat(Idents.Keys);
}

sealed class Refused(string message, SyntaxNode at) : Exception(message) { public SyntaxNode At { get; } = at; }

sealed class Lowerer(Graph catalog, string module, IReadOnlyDictionary<string, string> inputs, IReadOnlyDictionary<string, string> rows, string graphName, string outModule,
    string? description, long history, List<StepHeader> steps, KustoCode code, Faults faults)
{
    readonly DefBuilder b = new();
    readonly string mid = Ids.Module(module);
    string pid = "";
    // names are Kusto's symbols: Kusto has bound every name of the text, so a name means what Kusto says it means, wherever it is read
    readonly Dictionary<Symbol, LetStatement> letSyntax = [];
    readonly Dictionary<Symbol, FunctionDeclaration> functions = [];
    readonly Dictionary<Symbol, Rel> lets = [];
    readonly Dictionary<string, string> ports = new(StringComparer.Ordinal);
    readonly HashSet<string> parms = new(StringComparer.Ordinal);
    readonly Dictionary<string, OntologyView> views = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> opNo = new(StringComparer.Ordinal);
    int emits;
    /// <summary>The place in the text of what is being written: a KQL operator, or a step for its sink. Each operator keeps the place it came from.</summary>
    (int Start, int End) at;
    readonly Dictionary<string, (int Start, int End)> places = new(StringComparer.Ordinal);
    /// <summary>Where a name is read. A table function's body is written in a scope of its own: the table and the constants it was called with, by the symbol of
    /// each parameter, and the place of the call, which each operator written there keeps. `Prefix` names the operators; `Paths` are the path variables of the
    /// graph-match being written. Immutable: entering a call, a let, a step or a graph-match gives a new scope.</summary>
    sealed record Scope(ImmutableDictionary<Symbol, Rel> Tables, ImmutableDictionary<Symbol, object?> Values, string Prefix, (int Start, int End)? Call, ImmutableHashSet<string> Paths)
    {
        public static readonly Scope Top = new(ImmutableDictionary<Symbol, Rel>.Empty, ImmutableDictionary<Symbol, object?>.Empty, "", null, []);
    }

    // the only state of the lowering that changes while it reads names: swapped in In and restored in its finally; the rest only collects the result
    Scope scope = Scope.Top;

    TOut In<TOut>(Scope s, Func<TOut> write)
    {
        var saved = scope; scope = s;
        try { return write(); } finally { scope = saved; }
    }
    readonly Dictionary<string, (int Start, int End)> calls = new(StringComparer.Ordinal);
    readonly Dictionary<(int Start, int End), Rel> shapes = new();

    TOut At<TOut>(SyntaxNode n, Func<TOut> write)
    {
        var saved = at; at = (n.TextStart, n.End);
        try { var r = write(); if (r is Rel rel && scope.Call is null) shapes[at] = rel; return r; } finally { at = saved; }
    }

    /// <summary>A fault of a step, or of a rule at its first step: at the step's head.</summary>
    void Fault(int pos, string message) => faults.Add(pos, steps.FirstOrDefault(s => s.Pos == pos)?.HeadEnd ?? pos, "Profile", message);

    static Value T(string s) => Value.Text(s);
    static FieldX F(string path) => new(path);
    static string P(string op, string port) => Ids.Child(op, "port", port);

    /// <summary>The rules of the projection, from the steps' settings: name, class and description.</summary>
    /// <summary>A rule of the projection, from the settings of its steps: what it says about itself and what its steps make.</summary>
    sealed class RuleSpec
    {
        public string? Description, Update, OnCreate, OnDelete;
        public List<string> Accept = [];
        public string? MasterType;                       // the type of the element a creating node step of the rule reads, for the key of a create
        public HashSet<string> Sets = new(StringComparer.Ordinal);
        public HashSet<string> FromMaster = new(StringComparer.Ordinal);    // properties of the element read, that the step sets from it
        public bool Entities;
        public required int Pos;
    }
    readonly Dictionary<string, RuleSpec> rules = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> ruleSinks = new(StringComparer.Ordinal);
    static readonly string[] RuleSettings = ["description", "accept", "update", "onCreate", "onDelete"];

    /// <summary>A step in a rule (`with (rule = "name")`) is governed by it, and its operator is named after it. The rule's settings (description, accept, update,
    /// onCreate, onDelete) may be given on any one of its steps. What may be edited is said with accept and update; the kind of rule follows.</summary>
    void Rules()
    {
        foreach (var g in steps.Select(h => (Step: h, Settings: h.With.ToDictionary(s => s.Name, s => s.Value))).GroupBy(x => x.Settings.GetValueOrDefault("rule")))
        {
            if (g.Key is null)
            {
                foreach (var (h, st) in g.Where(x => RuleSettings.Any(x.Settings.ContainsKey)))
                    Fault(h.Pos, $"{string.Join(", ", RuleSettings.Where(st.ContainsKey))} belong to a rule: name it with rule = \"...\"");
                continue;
            }
            var spec = new RuleSpec { Pos = g.First().Step.Pos };
            string? One(string key)
            {
                var values = g.Select(x => x.Settings.GetValueOrDefault(key)).OfType<string>().Distinct().ToList();
                if (values.Count > 1) Fault(spec.Pos, $"the steps of rule {g.Key} give {key} different values");
                return values.FirstOrDefault();
            }
            spec.Description = One("description"); spec.Update = One("update"); spec.OnCreate = One("onCreate"); spec.OnDelete = One("onDelete");
            foreach (var w in (One("accept") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (w is "create" or "delete") spec.Accept.Add(w);
                else Fault(spec.Pos, $"accept lists create and delete; '{w}' is neither (an update is said with update = \"property, ...\")");
            rules[g.Key] = spec;
        }
    }

    string SinkName(StepHeader h) => h.Get("rule") is { } rule ? $"{rule}.{ruleSinks[rule] = ruleSinks.GetValueOrDefault(rule) + 1}" : $"emit.{++emits}";

    void Govern(StepHeader h, string sink)
    {
        if (h.Get("rule") is { } rule) b.Edge(Ids.Child(pid, "rule", rule), "governs", sink);
    }

    /// <summary>The rule nodes, once every step is lowered. A rule nothing may edit is readonly. An editable rule over the rows of a source is written back one
    /// for one (class bijective): it accepts create and delete and updates every property it sets. An editable rule over graphs is a view edited by its stated
    /// rules (class lens): onCreate names a function whose parameters are the key of the new element and whose row gives its defaults; onDelete names a function
    /// whose row gives what a delete sets, and without it a delete removes the element.</summary>
    void WriteRules()
    {
        foreach (var (name, spec) in rules)
        {
            void Fail(string message) => Fault(spec.Pos, message);
            var updates = (spec.Update ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            var editable = spec.Accept.Count > 0 || updates.Count > 0;
            var cls = !editable ? "readonly" : rows.Count > 0 ? "bijective" : "lens";
            if (!editable && (spec.OnCreate is not null || spec.OnDelete is not null)) Fail("onCreate and onDelete need accept = \"create\" or \"delete\"");
            var props = new List<(string, Value)> { ("name", T(name)), ("class", T(cls)), ("putput", Value.Boolean(false)) };
            if (spec.Description is not null) props.Add(("reason", T(spec.Description)));
            if (cls == "bijective")
            {
                if (!spec.Accept.Contains("create") || !spec.Accept.Contains("delete") || !spec.Sets.SetEquals(updates))
                    Fail($"this step reads the rows of a source, so what it makes is written back one for one: accept = \"create, delete\" and update = \"{string.Join(", ", spec.Sets.Order(StringComparer.Ordinal))}\"");
                if (spec.OnCreate is not null || spec.OnDelete is not null) Fail("what a step over the rows of a source makes is written back one for one: it takes no onCreate or onDelete");
                b.Contain(pid, "rule", name, "Rule", null, [.. props]);
                continue;
            }
            if (cls == "lens")
            {
                // the pieces an edit through a view needs, said in the author's terms before the engine's checker sees them
                foreach (var u in updates.Where(u => !spec.Sets.Contains(u))) Fail($"update names {u}, which this step does not set");
                if (spec.Accept.Contains("create") && spec.Entities && spec.OnCreate is null)
                    Fail("this step accepts create but says nothing about the new element's key: name a function with onCreate = \"...\" whose parameters are the key and whose row gives the other values");
                else if (spec.Accept.Contains("create") && spec.MasterType is { } mt && spec.OnCreate is { } ocf)
                {
                    var mv = View(Ids.ModuleOf(mt));
                    var given = FunctionNamed(ocf) is { } ofd && ofd.Body.Expression is PrintOperator pr ? pr.Expressions.Select(x => (x.Element as SimpleNamedExpression)?.Name.SimpleName).OfType<string>().ToHashSet() : [];
                    var missing = mv.PropsOf(mt).Where(p => p.Value && mv.Props[p.Key].Role != "telemetry").Select(p => mv.Props[p.Key].Name).Where(p => !spec.FromMaster.Contains(p) && !given.Contains(p)).Order(StringComparer.Ordinal).ToList();
                    if (missing.Count > 0) Fail($"a {Ids.Name(mt)} created through this step needs {string.Join(", ", missing)}: read it from the {Ids.Name(mt)} in the step, or give it in the function onCreate names");
                }
                props.Add(("accept", Value.List([.. spec.Accept.Select(Value.Text), .. updates.Count > 0 ? [Value.Text("update(" + string.Join(",", updates) + ")")] : Array.Empty<Value>()])));
                var deletes = spec.Accept.Contains("delete") && spec.Entities;
                if (spec.OnDelete is not null) props.Add(("onDelete", T("set")));
                else if (deletes) props.Add(("onDelete", T("remove")));
            }
            var rid = b.Contain(pid, "rule", name, "Rule", null, [.. props]);
            if (cls != "lens") continue;
            if (spec.OnCreate is not null || spec.OnDelete is not null)
            {
                if (spec.MasterType is not { } type) { Fail("onCreate and onDelete belong to a step that makes nodes from the elements of a graph"); continue; }
                var view = View(Ids.ModuleOf(type));
                string? Prop(string p) => view.PropsOf(type).Keys.FirstOrDefault(k => view.Props[k].Name == p);
                void Defaults(string fn, string slot, IReadOnlyList<string> key)
                {
                    if (FunctionNamed(fn) is not { } fd || fd.Body.Expression is not PrintOperator print) { Fail($"{fn} is a function that prints one row: let {fn} = (...) {{ print p = ..., q = ... }}"); return; }
                    var ps = fd.Parameters.Parameters.Select(p => p.Element.NameAndType.Name.SimpleName).ToList();
                    if (!ps.ToHashSet().SetEquals(key)) { Fail($"the parameters of {fn} are {(key.Count == 0 ? "none" : string.Join(", ", key))}"); return; }
                    foreach (var it in print.Expressions.Select(x => x.Element))
                    {
                        if (it is not SimpleNamedExpression { Expression: var e } sn) { Fail($"{fn} names each value: p = ..."); continue; }
                        if (Prop(sn.Name.SimpleName) is not { } pid2) { Fail($"{Ids.Name(type)} has no property '{sn.Name.SimpleName}'"); continue; }
                        X x = e is NameReference r && ps.Contains(r.SimpleName) ? F("key." + r.SimpleName) : e is LiteralExpression || e.IsConstant ? new LitX(FromObject(Const(e), e)) : null!;
                        if (x is null) { Fail($"{fn} gives a constant or a part of the key, not '{e}'"); continue; }
                        var sid = b.Contain(rid, slot, sn.Name.SimpleName, "SetProperty");
                        b.Edge(sid, "property", pid2);
                        Write(x, sid, "value", 0);
                    }
                }
                if (spec.OnCreate is { } oc)
                {
                    var domains = catalog.Entities.Where(e => Ids.Name(e.Type) == "IdentityDomain" && new DefView(catalog).Target(e.Id, "identifies") == type).Select(e => e.Id).ToList();
                    if (domains.Count != 1) { Fail($"{Ids.Name(type)} has {domains.Count} identity domains; a create needs one"); continue; }
                    b.Edge(rid, "createKey", domains[0]);
                    Defaults(oc, "createDefault", [.. new DefView(catalog).Children(domains[0], "component").Select(Ids.Name)]);
                }
                if (spec.OnDelete is { } od) Defaults(od, "deleteSet", []);
            }
        }
    }

    public KqlResult Run()
    {
        Rules();
        // a projection that reads rows is the ingestion of its graph, one that reads graphs a view
        var props = new List<(string, Value)> { ("name", T(graphName)), ("purpose", T(rows.Count > 0 ? "ingestion" : "view")), ("retain", Value.Integer(history)), ("forward", Value.Boolean(rules.Count == 0)) };
        if (description is not null) props.Add(("description", T(description)));
        pid = b.Contain(mid, "projection", graphName, "Projection", null, [.. props]);
        b.Edge(pid, "outputOntology", Ids.Module(outModule));
        // every input is a port, read or not, so the space can give it a graph or a source
        foreach (var g in inputs.Keys) GraphPort(g, code.Syntax);
        foreach (var r in rows.Keys) RowsPort(r);
        foreach (var se in ((QueryBlock)code.Syntax).Statements)
        {
            try
            {
                switch (se.Element)
                {
                    case QueryParametersStatement q: Params(q); break;
                    case LetStatement { Expression: FunctionDeclaration fd } l: functions[l.Name.ReferencedSymbol!] = fd; break;
                    case LetStatement l when l.Name.SimpleName.StartsWith("__s", StringComparison.Ordinal):
                        var h = steps[int.Parse(l.Name.SimpleName[3..], CultureInfo.InvariantCulture)];
                        In(Scope.Top with { Prefix = h.Kind == "edges" ? h.Key : char.ToLowerInvariant(h.Type[0]) + h.Type[1..] }, () => { Step(h, l.Expression); return 0; });
                        break;
                    case LetStatement l: letSyntax[l.Name.ReferencedSymbol!] = l; break;
                    case ExpressionStatement: break;     // the closing `print 0`
                    default: throw new Refused("only lets, query parameters and steps belong in a projection", se.Element);
                }
            }
            catch (Refused r) { faults.Add(r.At, "Profile", r.Message); }
        }
        WriteRules();
        return new KqlResult(pid, b, faults.List) { DiagnosticPlaces = faults.Places, OperatorPlaces = places, OperatorCalls = calls, Shapes = shapes };
    }

    // ---- ports, operators, names ----

    OntologyView View(string m) => views.TryGetValue(m, out var v) ? v : views[m] = OntologyView.Build(catalog, KqlFrontEnd.Closure(catalog, m));

    void Params(QueryParametersStatement q)
    {
        foreach (var pe in q.Parameters)
        {
            var p = pe.Element; var name = p.NameAndType.Name.SimpleName;
            var kind = p.NameAndType.Type.ToString().Trim() switch { "string" => "Text", "long" or "int" => "Integer", "real" or "double" or "decimal" => "Decimal", "bool" => "Boolean", var t => throw new Refused($"a parameter is string, long, real or bool, not {t}", p) };
            var props = new List<(string, Value)> { ("name", T(name)), ("direction", T("in")), ("kind", T("param")) };
            if (p.DefaultValue?.Value is { } dv) props.Add(("default", Lit(dv)));
            var id = b.Contain(pid, "port", name, "Port", null, [.. props]);
            b.Edge(id, "valueKind", Ids.KBuiltin(kind));
            parms.Add(name);
        }
    }

    string GraphPort(Expression e)
    {
        if (e is not FunctionCallExpression { Name.SimpleName: "graph" } f || f.ArgumentList.Expressions.Count != 1 || f.ArgumentList.Expressions[0].Element.LiteralValue is not string g)
            throw new Refused("a pattern reads a graph by name: graph(\"name\") | graph-match ...", e);
        return GraphPort(g, e);
    }

    string GraphPort(string g, SyntaxNode at)
    {
        if (ports.TryGetValue(g, out var id)) return id;
        if (!inputs.TryGetValue(g, out var m)) throw new Refused($"'{g}' is not a graph this projection reads", at);
        id = b.Contain(pid, "port", g, "Port", null, ("name", T(g)), ("direction", T("in")), ("kind", T("graph")));
        b.Edge(id, "expects", Ids.Module(m));
        return ports[g] = id;
    }

    /// <summary>A table of rows from a source, read by its name.</summary>
    string RowsPort(string name)
    {
        if (ports.TryGetValue(name, out var id)) return id;
        id = b.Contain(pid, "port", name, "Port", null, ("name", T(name)), ("direction", T("in")), ("kind", T("rows")));
        b.Edge(id, "rowType", rows[name]);
        return ports[name] = id;
    }

    string Op(string kind, params (string, Value)[] props)
    {
        var n = opNo[scope.Prefix] = opNo.GetValueOrDefault(scope.Prefix) + 1;
        return OpNamed(kind, $"{scope.Prefix}_{n}", props);
    }

    string OpNamed(string kind, string name, params (string, Value)[] props)
    {
        var id = b.Contain(pid, "op", name, kind, null, [("name", T(name)), .. props]);
        places[id] = at;
        if (scope.Call is { } c) calls[id] = c;
        foreach (var (pn, dir, pk) in KernelTable.OperatorPorts(kind)) b.Contain(id, "port", pn, "Port", null, ("name", T(pn)), ("direction", T(dir)), ("kind", T(pk)));
        return id;
    }

    void Wire(string from, string op, string port) => b.Edge(from, "wire", P(op, port));

    // ---- steps ----

    void Step(StepHeader h, Expression query)
    {
        // a step's last `project` or `extend` is the step's own expressions over the rows before it, as today's text writes them in its emits:
        // the step reads those rows, and every element they hold is its lineage
        Rel r; List<(string Name, X Value)> cols;
        if (query is PipeExpression { Operator: ProjectOperator or ExtendOperator } last)
        {
            r = Tab(last.Expression);
            cols = Fuse(r, last.Operator is ProjectOperator po ? [.. po.Expressions.Select(x => x.Element)] : [.. ((ExtendOperator)last.Operator).Expressions.Select(x => x.Element)], last.Operator is ExtendOperator);
        }
        else
        {
            r = Tab(query);
            cols = [.. r.Visible.Select(c => (c, r.Idents.TryGetValue(c, out var id) ? (X)id : F(c)))];
        }
        at = (h.Pos, query.End);
        if (h.Kind == "link") { Link(h, r, cols, query); return; }
        var key = h.Key;
        var view = View(outModule);
        var type = TypeNamed(view, h.Type, h.Kind == "edges", outModule);
        if (type.Count != 1) throw new Refused($"{h.Type} is {(type.Count == 0 ? "not a" : "more than one")} {(h.Kind == "edges" ? "relationship" : "entity")} type of {outModule}", query);
        var ends = h.Kind == "nodes" ? new[] { (h.Id!, "id") } : [(h.Src!, "source"), (h.Dst!, "target")];
        var sets = new List<(string Col, X Value, string Prop)>();
        foreach (var (c, x) in cols.Where(c => !ends.Any(e => e.Item1 == c.Name) && c.Name is not ("windowStart" or "windowEnd")))
        {
            if (x is IdX) throw new Refused($"'{c}' is an identity: it names a node or an end of an edge, not a property", query);
            var props = view.PropsOf(type[0]).Keys.Where(k => view.Props[k].Name == c).ToList();
            if (props.Count == 1) { sets.Add((c, x, props[0])); continue; }
            if (x is FieldX { Path: var path } && !path.Contains('.') && r.Types.ContainsKey(path)) continue;     // an element: lineage, not a property
            throw new Refused($"{h.Type} has no property '{c}': project it away", query);
        }
        // `nodes` and `edges` make elements; `extend nodes` and `extend edges` add properties to elements another step makes
        var what = h.Kind == "nodes" ? h.Type : $"{h.Type} edges with the key '{key}'";
        if (h.Extend && !steps.Any(o => !o.Extend && o.Kind == h.Kind && o.Type == h.Type && (h.Kind == "nodes" || o.Key == key)))
            throw new Refused($"no step makes {what}: extend {h.Kind} adds properties to {h.Kind} another step of this projection makes", query);
        if (!h.Extend && view.PropsOf(type[0]).Where(p => p.Value).Select(p => p.Key).Where(p => !sets.Any(s => s.Prop == p)).Select(p => view.Props[p].Name).ToList() is [_, ..] missing)
            throw new Refused($"a step that makes {h.Type} sets every required property, and {string.Join(", ", missing)} is not set: set it, or write extend {h.Kind} to add properties to {h.Kind} another step makes", query);
        if (!h.Extend && h.Kind == "edges" && steps.Count(o => !o.Extend && o.Kind == "edges" && o.Type == h.Type && o.Key == key) > 1)
            throw new Refused($"two steps make {what}: give one its own key with (key = \"...\"), or write extend edges", query);
        var mode = ("mode", T(h.Extend ? "update" : "create"));
        var sink = OpNamed(h.Kind == "nodes" ? "EmitEntity" : "EmitRelationship", SinkName(h), h.Kind == "nodes" ? [mode] : [mode, ("key", T(key)), ("retype", T("reject"))]);
        Govern(h, sink);
        b.Edge(sink, "ofType", type[0]);
        Wire(r.Port, sink, "in");
        foreach (var (col, slot) in ends) Write(End(cols, col, query), sink, slot, 0);
        if (h.Get("rule") is { } ruleName && rules.TryGetValue(ruleName, out var spec))
        {
            foreach (var (c, _, _) in sets) spec.Sets.Add(c);
            if (h.Kind == "nodes" && !h.Extend)
            {
                spec.Entities = true;
                if (End(cols, h.Id!, query) is FieldX { Path: var idPath } && !idPath.Contains('.') && r.Types.TryGetValue(idPath, out var master))
                {
                    spec.MasterType = master;
                    foreach (var (_, x, _) in sets) if (x is FieldX { Path: var sp } && sp.StartsWith(idPath + ".", StringComparison.Ordinal)) spec.FromMaster.Add(sp[(idPath.Length + 1)..]);
                }
            }
        }
        foreach (var (c, x, prop) in sets)
        {
            var set = b.Contain(sink, "set", c, "SetProperty");
            b.Edge(set, "property", prop);
            Write(x, set, "value", 0);
        }
    }

    static X End(List<(string Name, X Value)> cols, string col, SyntaxNode at) =>
        cols.FirstOrDefault(c => c.Name == col) is { Value: { } x } ? x : throw new Refused($"the rows have no column '{col}'", at);

    /// <summary>The columns of a `project` (or `extend`, which keeps the others) as expressions over the rows before it, with no operator of their own.</summary>
    List<(string Name, X Value)> Fuse(Rel r, IReadOnlyList<Expression> items, bool keepAll)
    {
        if (r.Long is { Props.Length: > 1 }) throw new Refused("summarize the telemetry rows of several properties by their window before projecting them: summarize v = sumif(value, property == \"p\") by ..., windowStart", items[0]);
        var res = new List<(string Name, X Value)>();
        void Put(string name, X x) { var i = res.FindIndex(c => c.Name == name); if (i >= 0) res[i] = (name, x); else res.Add((name, x)); }
        if (keepAll) foreach (var c in r.Visible) Put(c, r.Idents.TryGetValue(c, out var id) ? id : F(c));
        foreach (var it in items)
        {
            var (name, e) = it switch
            {
                SimpleNamedExpression sn => (sn.Name.SimpleName, sn.Expression),
                NameReference n => (n.SimpleName, (Expression)n),
                _ => throw new Refused("name the column: name = expression", it),
            };
            Put(name, e switch
            {
                NameReference nr when r.Idents.TryGetValue(nr.SimpleName, out var id) => id,
                FunctionCallExpression { Name.SimpleName: "identity" } ic => Identity(ic, r),
                _ => Scalar(e, r),
            });
        }
        return res;
    }


    /// <summary>The types of a name: the module's own type when it declares one, else every type of that name it sees.</summary>
    static List<string> TypeNamed(OntologyView view, string name, bool rel, string module)
    {
        var hits = view.Types.Values.Where(t => t.Name == name && t.IsRel == rel).Select(t => t.Id).ToList();
        var own = hits.Where(id => Ids.ModuleOf(id) == module).ToList();
        return own.Count == 1 ? own : hits;
    }

    /// <summary>`link P from a to b with (confidence = c) = q`: the identities in the columns a and b name the same thing, as the equivalence policy P allows.</summary>
    void Link(StepHeader h, Rel r, List<(string Name, X Value)> cols, Expression query)
    {
        if (h.Extend) throw new Refused("a link adds no properties: write link P from a to b = ...", query);
        if (h.Get("confidence") is not { } c || !decimal.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence))
            throw new Refused("a link says how sure it is: with (confidence = 1.0)", query);
        var pol = KqlFrontEnd.Closure(catalog, module).Select(m => Ids.Child(Ids.Module(m), "equivalence", h.Type)).Where(id => catalog.Entity(id) is not null).ToList();
        if (pol.Count != 1) throw new Refused($"'{h.Type}' is no equivalence policy of {module}: a link needs one that says which two identity domains may name the same thing", query);
        if (cols.FirstOrDefault(c => c.Name == h.Src).Value is not IdX left || cols.FirstOrDefault(c => c.Name == h.Dst).Value is not IdX right)
            throw new Refused("a link joins two identities: project both with identity(...)", query);
        if (cols.Any(c => c.Name != h.Src && c.Name != h.Dst && !(c.Value is FieldX { Path: var p } && r.Types.ContainsKey(p)))) throw new Refused("a link has no properties: project only the two identities", query);
        var sink = OpNamed("Link", SinkName(h));
        Govern(h, sink);
        b.Edge(sink, "policy", pol[0]);
        Wire(r.Port, sink, "in");
        Write(left, sink, "left", 0);
        Write(right, sink, "right", 0);
        Write(new LitX(Value.Decimal(confidence)), sink, "confidence", 0);
    }

    // ---- tables ----

    Rel Tab(Expression e) => e switch
    {
        ParenthesizedExpression p => Tab(p.Expression),
        NameReference { ReferencedSymbol: { } s } when scope.Tables.TryGetValue(s, out var t) => t,
        // `let pods = pods | ...`: the name inside is the table pods, the name after it the let
        NameReference { ReferencedSymbol: { } s } when letSyntax.TryGetValue(s, out var l) => Let(s, l),
        NameReference { ReferencedSymbol: TableSymbol } n when rows.ContainsKey(n.SimpleName) => new Rel { Port = RowsPort(n.SimpleName), Cols = [.. KqlFrontEnd.Fields(catalog, rows[n.SimpleName]).Select(f => f.Name)] },
        FunctionCallExpression f when TableFunction(f.ReferencedSymbol) is { } fd => Apply(fd, Tab(f.ArgumentList.Expressions[0].Element), [.. f.ArgumentList.Expressions.Skip(1).Select(x => x.Element)], f),
        PipeExpression p => Pipe(p),
        DataTableExpression d => At(d, () => DataTable(d)),
        UnionOperator u => At(u, () => Union(null, u)),
        _ => throw new Refused($"'{Short(e)}' is not a table the profile can keep up to date", e),
    };

    /// <summary>A let is written once, in the scope of the text, wherever it is first read: inside a function's body it reads no parameter of the call.</summary>
    Rel Let(Symbol s, LetStatement l)
    {
        if (lets.TryGetValue(s, out var r)) return r;
        return lets[s] = In(Scope.Top with { Prefix = l.Name.SimpleName }, () => Tab(l.Expression));
    }

    Rel Pipe(PipeExpression p)
    {
        // `... | project ..., value = v, above = ... | invoke hysteresis(...)`: the projection is the threshold's own expressions, so the threshold reads the author's column
        if (p.Operator is InvokeOperator { Function: FunctionCallExpression { Name.SimpleName: "hysteresis" } } hy && p.Expression is PipeExpression { Operator: ProjectOperator or ExtendOperator } pj)
        {
            var inner = Tab(pj.Expression);
            return At(hy, () => Hysteresis(inner, Fuse(inner, pj.Operator is ProjectOperator po ? [.. po.Expressions.Select(x => x.Element)] : [.. ((ExtendOperator)pj.Operator).Expressions.Select(x => x.Element)], pj.Operator is ExtendOperator), hy));
        }
        if (p.Operator is GraphMatchOperator gm) return At(gm, () => GraphMatch(gm, p.Expression));
        var input = Tab(p.Expression);
        return At(p.Operator, () => p.Operator switch
        {
            FilterOperator { Condition: BinaryExpression { Operator.Text: ">", Left: NameReference t, Right: FunctionCallExpression { Name.SimpleName: "ago" } ago } } f when ago.ArgumentList.Expressions is [var span] && Const(span.Element) is TimeSpan age
                => Retain(input, t.SimpleName, age.Ticks * 100, f),
            FilterOperator f => Filter(input, [f.Condition]),
            ProjectOperator po => Project(input, [.. po.Expressions.Select(x => x.Element)], false),
            ExtendOperator eo => Project(input, [.. eo.Expressions.Select(x => x.Element)], true),
            DistinctOperator d => Distinct(input, d),
            SummarizeOperator s => Summarize(input, s),
            JoinOperator j => Join(input, j),
            UnionOperator u => Union(input, u),
            InvokeOperator { Function: FunctionCallExpression f } when TableFunction(f.ReferencedSymbol) is { } fd => Apply(fd, input, [.. f.ArgumentList.Expressions.Select(x => x.Element)], f),
            InvokeOperator i => Hysteresis(input, [.. input.Visible.Select(c => (c, input.Idents.TryGetValue(c, out var id) ? (X)id : F(c)))], i),
            var op => throw new Refused(Refusal(op), op),
        });
    }

    /// <summary>A user function whose first parameter is a table: `let f = (T:(a:string, ...), p:string) { T | ... }`.</summary>
    /// <summary>A function a setting names (onCreate, onDelete): settings are text, so the name is looked up among the text's functions.</summary>
    FunctionDeclaration? FunctionNamed(string name) => functions.Where(kv => kv.Key.Name == name).Select(kv => kv.Value).FirstOrDefault();

    FunctionDeclaration? TableFunction(Symbol? s) =>
        s is not null && functions.TryGetValue(s, out var fd) && fd.Parameters.Parameters.Count > 0 && fd.Parameters.Parameters[0].Element.NameAndType.Type is SchemaTypeExpression ? fd : null;

    /// <summary>A table function is written out where it is called: its table parameter is the table it is called with, its other parameters are constants.</summary>
    Rel Apply(FunctionDeclaration fd, Rel input, IReadOnlyList<Expression> args, SyntaxNode at)
    {
        var ps = fd.Parameters.Parameters.Select(p => p.Element.NameAndType.Name.ReferencedSymbol!).ToList();
        if (fd.Body.Statements.Count > 0 || fd.Body.Expression is null) throw new Refused("a function of a projection is one expression", at);
        if (args.Count != ps.Count - 1) throw new Refused($"the function takes a table and {ps.Count - 1} value(s)", at);
        var values = ImmutableDictionary<Symbol, object?>.Empty;
        for (var k = 0; k < args.Count; k++)
        {
            if (!(args[k].IsConstant || args[k].IsLiteral || args[k] is NameReference { ReferencedSymbol: { } a } && scope.Values.ContainsKey(a))) throw new Refused("the values a table function takes are constants", args[k]);
            values = values.SetItem(ps[k + 1], Const(args[k]));
        }
        // the body reads its parameters and the names of the text, never the caller's: a new scope, not the caller's extended
        return In(Scope.Top with { Tables = ImmutableDictionary<Symbol, Rel>.Empty.Add(ps[0], input), Values = values, Prefix = scope.Prefix, Call = scope.Call ?? (at.TextStart, at.End) },
            () => Tab(fd.Body.Expression));
    }

    /// <summary>The value of a constant: a literal, or a parameter of the table function being written out.</summary>
    object? Const(Expression e) => e is NameReference { ReferencedSymbol: { } s } && scope.Values.TryGetValue(s, out var v) ? v : e.IsConstant ? e.ConstantValue : e.LiteralValue;

    static Value FromObject(object? o, SyntaxNode at) => o switch
    {
        null => Value.Null,
        string s => Value.Text(s),
        bool v => Value.Boolean(v),
        long or int => Value.Integer(Convert.ToInt64(o, CultureInfo.InvariantCulture)),
        double or decimal or float => Value.Decimal(Convert.ToDecimal(o, CultureInfo.InvariantCulture)),
        _ => throw new Refused($"a constant of type {o.GetType().Name} is not in the profile", at),
    };

    /// <summary>Why an operator is not in the profile, and what to write instead.</summary>
    static string Refusal(QueryOperator op) => op switch
    {
        TakeOperator or SampleOperator or SampleDistinctOperator => "take and sample pick arbitrary rows, so the result would depend on the order rows arrive in: filter or summarize instead",
        SortOperator or TopOperator or TopNestedOperator or TopHittersOperator => "rows of a graph have no order: summarize with min, max or arg_max instead",
        ScanOperator => "a general scan is not kept up to date: write a threshold with invoke hysteresis(...)",
        EvaluateOperator => "plugins are not in the profile",
        _ => $"{op.GetType().Name.Replace("Operator", "", StringComparison.Ordinal).ToLowerInvariant()} is not in the KQL profile",
    };

    Rel GraphMatch(GraphMatchOperator gm, Expression graph)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var port = GraphPort(graph);
        var view = View(inputs[((FunctionCallExpression)graph).ArgumentList.Expressions[0].Element.LiteralValue!.ToString()!]);
        var nodes = new List<string>(); var edges = new List<(string Var, string From, string To, GraphMatchPatternEdge At)>();
        (string Var, string Start, string Target, bool Out, long Min, long Max, GraphMatchPatternEdge At)? path = null;
        static string NameOf(GraphMatchPatternNotation n) => (n as GraphMatchPatternNode)?.Name?.SimpleName ?? throw new Refused("name every node of a pattern: (x)", n);
        foreach (var pat in gm.Patterns)
        {
            var els = pat.Element.PatternElements;
            for (var i = 0; i < els.Count; i++)
            {
                if (els[i] is GraphMatchPatternNode) { var v = NameOf(els[i]); if (!nodes.Contains(v)) nodes.Add(v); continue; }
                var ed = (GraphMatchPatternEdge)els[i];
                var v2 = ed.Name?.SimpleName ?? throw new Refused("name every edge of a pattern: -[e]->", ed);
                var (first, last) = (ed.FirstToken.Text, ed.LastToken.Text);
                var fwd = !first.StartsWith('<') && last.EndsWith('>'); var back = first.StartsWith('<') && !last.EndsWith('>');
                if (fwd == back) throw new Refused("an edge of a pattern has one direction: -[e]-> or <-[e]-", ed);
                if (ed.Range is { } range)
                {
                    // a path of several edges: reachability from the node before it to the node after it, which no other edge may touch
                    if (path is not null || i + 2 < els.Count) throw new Refused("a pattern has at most one path of several edges, and it ends the pattern: (a)-[p*1..16]->(b)", ed);
                    if (range.RangeStart?.LiteralValue is not long min || range.RangeEnd?.LiteralValue is not long max) throw new Refused("a path has fixed bounds: -[p*1..16]->", ed);
                    path = (v2, NameOf(els[i - 1]), NameOf(els[i + 1]), fwd, min, max, ed);
                    paths.Add(v2);
                    i++;
                    continue;
                }
                edges.Add((v2, fwd ? NameOf(els[i - 1]) : NameOf(els[i + 1]), fwd ? NameOf(els[i + 1]) : NameOf(els[i - 1]), ed));
            }
        }
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        var rest = new List<Expression>();
        foreach (var c in gm.WhereClause is { } w ? Conjuncts(w.Condition) : [])
        {
            if (c is BinaryExpression { Operator.Text: "has", Left: FunctionCallExpression { Name.SimpleName: "labels" } lf, Right: var tn }
                && lf.ArgumentList.Expressions is [{ Element: NameReference v }] && tn.LiteralValue is string tname)
            {
                var isEdge = edges.Any(e => e.Var == v.SimpleName);
                var hits = TypeNamed(view, tname, isEdge, inputs[((FunctionCallExpression)graph).ArgumentList.Expressions[0].Element.LiteralValue!.ToString()!]);
                if (hits.Count != 1) throw new Refused($"'{tname}' is {(hits.Count == 0 ? "no" : "more than one")} {(isEdge ? "relationship" : "entity")} type of the graph", c);
                types[v.SimpleName] = hits[0];
            }
            else if (c is FunctionCallExpression { Name.SimpleName: "all" } all && path is { } pa && all.ArgumentList.Expressions.Select(x => x.Element).ToList() is [NameReference pv, BinaryExpression { Operator.Text: "has", Left: FunctionCallExpression { Name.SimpleName: "labels" }, Right.LiteralValue: string ptype }] && pv.SimpleName == pa.Var)
            {
                var hits = TypeNamed(view, ptype, true, inputs[((FunctionCallExpression)graph).ArgumentList.Expressions[0].Element.LiteralValue!.ToString()!]);
                if (hits.Count != 1) throw new Refused($"'{ptype}' is no relationship type of the graph", c);
                types[pa.Var] = hits[0];
            }
            else rest.Add(c);
        }
        if (path is { } pt)
        {
            if (!types.ContainsKey(pt.Var)) throw new Refused($"give the edges of the path their type: where all({pt.Var}, labels({pt.Var}) has \"T\")", pt.At);
            if (nodes.Contains(pt.Target) || edges.Any(e => e.From == pt.Target || e.To == pt.Target)) throw new Refused("the node a path reaches is touched by no other edge of the pattern", pt.At);
            if (types.ContainsKey(pt.Target)) throw new Refused("the node a path reaches takes no label", pt.At);
        }
        foreach (var e in edges)
            if (!types.ContainsKey(e.Var)) throw new Refused($"give the edge '{e.Var}' its type: where labels({e.Var}) has \"T\"", e.At);
        foreach (var v in nodes.Where(v => !types.ContainsKey(v)))
        {
            // a node without a label takes the declared end type of an edge it touches
            var ends = edges.SelectMany(e => new[] { (e.From == v, view.Types[types[e.Var]].Sources), (e.To == v, view.Types[types[e.Var]].Targets) }).Where(x => x.Item1).SelectMany(x => x.Item2).Distinct().ToList();
            if (ends.Count != 1) throw new Refused($"give the node '{v}' its type: where labels({v}) has \"T\"", gm);
            types[v] = ends[0];
        }
        var op = Op("Match");
        Wire(port, op, "graph");
        foreach (var v in nodes) b.Edge(b.Contain(op, "node", v, "PatternNode", null, ("var", T(v))), "ofType", types[v]);
        foreach (var e in edges)
        {
            var id = b.Contain(op, "edge", e.Var, "PatternEdge", null, ("var", T(e.Var)));
            b.Edge(id, "ofType", types[e.Var]);
            b.Edge(id, "from", Ids.Child(op, "node", e.From));
            b.Edge(id, "to", Ids.Child(op, "node", e.To));
        }
        var r = new Rel { Port = P(op, "out") };
        foreach (var v in nodes.Concat(edges.Select(e => e.Var))) { r.Cols.Add(v); r.Types[v] = types[v]; }
        if (path is { } tp)
        {
            var tr = Op("Traverse", ("direction", T(tp.Out ? "out" : "in")), ("minDepth", Value.Integer(tp.Min)), ("maxDepth", Value.Integer(tp.Max)), ("start", T(tp.Start)), ("target", T(tp.Target)));
            Wire(port, tr, "graph");
            b.Edge(tr, "ofType", types[tp.Var]);
            Wire(r.Port, tr, "in");
            r = r.Next(P(tr, "out"));
            r.Cols.Add(tp.Target); r.Types[tp.Target] = types[tp.Start];
        }
        // the condition and the projection read the path variables only to be refused: the edges of a path are not kept
        return In(scope with { Paths = [.. paths] }, () =>
        {
            var m = rest.Count > 0 ? Filter(r, rest) : r;
            return gm.ProjectClause is { } pc ? Project(m, [.. pc.Expressions.Select(x => x.Element)], false) : m;
        });
    }

    /// <summary>`where t > ago(span)`: rows stay while their time is within the span before the newest input time, the data's time.</summary>
    Rel Retain(Rel r, string col, long ageNs, SyntaxNode at)
    {
        if (!r.Cols.Contains(col)) throw new Refused($"the rows have no column '{col}'", at);
        var op = Op("Retain", ("timestampField", T(col)), ("age", Value.Integer(ageNs)));
        Wire(r.Port, op, "in");
        return r.Next(P(op, "out"));
    }

    /// <summary>`bin(time, width)` as a key of a summary: the rows go into windows of their own time; a late row updates its window.</summary>
    Rel Window(Rel r, string col, long widthNs, SyntaxNode at)
    {
        if (!r.Cols.Contains(col)) throw new Refused($"the rows have no column '{col}'", at);
        var op = Op("Window", ("timestampField", T(col)), ("width", Value.Integer(widthNs)), ("late", T("revise")));
        Wire(r.Port, op, "in");
        var next = r.Next(P(op, "out"));
        next.Cols.AddRange(["windowStart", "windowEnd"]);
        return next;
    }

    static IEnumerable<Expression> Conjuncts(Expression e) =>
        e is BinaryExpression { Operator.Text: "and" } a ? Conjuncts(a.Left).Concat(Conjuncts(a.Right)) : e is ParenthesizedExpression p ? Conjuncts(p.Expression) : [e];

    Rel Filter(Rel r, IReadOnlyList<Expression> conds)
    {
        var x = conds.Select(c => Scalar(c, r)).Aggregate((a, z) => new CallX("And", a, z));
        var op = Op("Filter");
        Write(x, op, "predicate", 0);
        Wire(r.Port, op, "in");
        return r.Next(P(op, "out"));
    }

    /// <summary>`project` (only the named columns) and `extend` (every column, then the new ones) as a Map.</summary>
    Rel Project(Rel r, IReadOnlyList<Expression> items, bool keepAll)
    {
        if (r.Long is { Props.Length: > 1 }) throw new Refused("summarize the telemetry rows of several properties by their window before projecting them: summarize v = sumif(value, property == \"p\") by ..., windowStart", items[0]);
        var assigns = new List<(string Name, X Value)>();
        var next = new Rel { Port = "", Windowed = false };
        void Put(string name, X x, string? type)
        {
            var i = assigns.FindIndex(a => a.Name == name);
            if (i >= 0) assigns[i] = (name, x); else { assigns.Add((name, x)); next.Cols.Add(name); }
            if (type is null) next.Types.Remove(name); else next.Types[name] = type;
            if (x is LitX l) next.Lits[name] = l.V; else next.Lits.Remove(name);
        }
        void Carry(string from, string to)
        {
            if (r.Idents.TryGetValue(from, out var id))
            {
                for (var k = 0; k < id.Parts.Length; k++) Put($"{to}__{k + 1}", id.Parts[k].Value, null);
                next.Idents[to] = new IdX(id.Domain, [.. id.Parts.Select((p, k) => (p.Component, (X)F($"{to}__{k + 1}")))]);
            }
            else { Put(to, F(from), r.Types.GetValueOrDefault(from)); if (r.Kinds.TryGetValue(from, out var kind)) next.Kinds[to] = kind; }
        }
        if (keepAll) { foreach (var c in r.Cols.Where(c => !Rel.Hidden(c))) Carry(c, c); foreach (var c in r.Idents.Keys) Carry(c, c); }
        foreach (var it in items)
        {
            var (name, e) = it switch
            {
                SimpleNamedExpression s => (s.Name.SimpleName, s.Expression),
                NameReference n => (n.SimpleName, (Expression)n),
                _ => throw new Refused("name the column: name = expression", it),
            };
            if (e is NameReference nr && (r.Idents.ContainsKey(nr.SimpleName) || r.Cols.Contains(nr.SimpleName))) { Carry(nr.SimpleName, name); continue; }
            if (e is FunctionCallExpression { Name.SimpleName: "identity" } ic)
            {
                var id = Identity(ic, r);
                for (var k = 0; k < id.Parts.Length; k++) Put($"{name}__{k + 1}", id.Parts[k].Value, null);
                next.Idents[name] = new IdX(id.Domain, [.. id.Parts.Select((p, k) => (p.Component, (X)F($"{name}__{k + 1}")))]);
                continue;
            }
            var x = Scalar(e, r);
            Put(name, x, x is FieldX f && !f.Path.Contains('.') ? r.Types.GetValueOrDefault(f.Path) : null);
            if (x is FieldX fk && r.Kinds.TryGetValue(fk.Path, out var kind)) next.Kinds[name] = kind;
        }
        var op = Op("Map");
        for (var k = 0; k < assigns.Count; k++)
            Write(assigns[k].Value, b.Contain(op, "assign", assigns[k].Name, "Assign", k, ("target", T(assigns[k].Name))), "value", 0);
        Wire(r.Port, op, "in");
        next.Port = P(op, "out");
        next.Windowed = r.Windowed && next.Cols.Contains("windowStart");
        next.EventWindows = r.EventWindows && next.Cols.Contains("windowStart");
        return next;
    }

    IdX Identity(FunctionCallExpression f, Rel r)
    {
        var args = f.ArgumentList.Expressions.Select(a => a.Element).ToList();
        if (args.Count == 0 || args[0].LiteralValue is not string name) throw new Refused("identity names its domain first: identity(\"domain\", key, ...)", f);
        var dom = KqlFrontEnd.Closure(catalog, outModule).Select(m => Ids.Child(Ids.Module(m), "identity", name)).Where(id => catalog.Entity(id) is not null).ToList();
        if (dom.Count != 1) throw new Refused($"'{name}' is not an identity domain of {outModule}", f);
        var comps = new DefView(catalog).Children(dom[0], "component");
        var vals = args.Skip(1).Where(a => !(a.LiteralValue is "")).ToList();
        if (vals.Count != comps.Count) throw new Refused($"identity domain {name} has {comps.Count} key parts, the call gives {vals.Count}", f);
        return new IdX(dom[0], [.. comps.Select((c, k) => (c, Scalar(vals[k], r)))]);
    }

    Rel Distinct(Rel r, DistinctOperator d)
    {
        var items = d.Expressions.Select(x => x.Element).ToList();
        if (items is not [StarExpression]) r = Project(r, items, false);
        var op = Op("Distinct");
        Wire(r.Port, op, "in");
        return r.Next(P(op, "out"));
    }

    Rel Summarize(Rel r, SummarizeOperator s)
    {
        var keys = new List<(string Name, X Value)>(); var byWindow = false; var binned = false;
        var next = new Rel { Port = "" };
        foreach (var it in s.ByClause?.Expressions.Select(x => x.Element) ?? [])
        {
            var (name, e) = it switch { SimpleNamedExpression n => (n.Name.SimpleName, n.Expression), NameReference n => (n.SimpleName, (Expression)n), _ => throw new Refused("name each key: k = expression", it) };
            if (e is FunctionCallExpression { Name.SimpleName: "bin" } bin)
            {
                if (name != "windowStart" || bin.ArgumentList.Expressions.Select(a => a.Element).ToList() is not [NameReference tc, var width] || Const(width) is not TimeSpan w)
                    throw new Refused("a window of the rows' own time is the key windowStart = bin(time, 60s), with a fixed width", it);
                r = Window(r, tc.SimpleName, w.Ticks * 100, it);
                byWindow = binned = true; continue;
            }
            if (name == "windowStart" && e is NameReference { SimpleName: "windowStart" }) { byWindow = true; continue; }
            if (e is NameReference nr && r.Idents.TryGetValue(nr.SimpleName, out var id))
            {
                for (var k = 0; k < id.Parts.Length; k++) { keys.Add(($"{name}__{k + 1}", id.Parts[k].Value)); next.Cols.Add($"{name}__{k + 1}"); }
                next.Idents[name] = new IdX(id.Domain, [.. id.Parts.Select((p, k) => (p.Component, (X)F($"{name}__{k + 1}")))]);
                continue;
            }
            var x = Scalar(e, r);
            keys.Add((name, x)); next.Cols.Add(name);
            if (x is FieldX f && !f.Path.Contains('.') && r.Types.TryGetValue(f.Path, out var t)) next.Types[name] = t;
        }
        var events = byWindow && (binned || r.EventWindows);
        var telemetry = !events && (r.Long is not null || (r.Windowed && byWindow));
        if (telemetry && !byWindow) throw new Refused("a summary of telemetry keeps its windows: summarize ... by ..., windowStart", s);
        if (byWindow && !telemetry && !events) throw new Refused("only telemetry rows and rows in windows of bin(time, 60s) have windows", s);
        if (events) { keys.Add(("windowStart", F("windowStart"))); next.Cols.Add("windowStart"); next.EventWindows = true; }
        var aggs = new List<(string Target, string Fn, X? Value, X? Weight)>();
        foreach (var it in s.Aggregates.Select(x => x.Element))
        {
            // arg_max(order, a, b): the values of the row with the greatest order, and that order
            if (!telemetry && it is FunctionCallExpression { Name.SimpleName: "arg_max" } am && am.ArgumentList.Expressions.Select(a => a.Element).ToList() is [NameReference order, .. var values] && values.All(v => v is NameReference))
            {
                aggs.Add((order.SimpleName, "last", Scalar(order, r), Scalar(order, r))); next.Cols.Add(order.SimpleName);
                foreach (NameReference v in values) { aggs.Add((v.SimpleName, "last", Scalar(v, r), Scalar(order, r))); next.Cols.Add(v.SimpleName); }
                continue;
            }
            if (it is not SimpleNamedExpression { Expression: FunctionCallExpression call } named) throw new Refused("name each aggregate: n = count()", it);
            aggs.Add(Aggregate(named.Name.SimpleName, call, r, telemetry));
            next.Cols.Add(named.Name.SimpleName);
        }
        var op = Op(telemetry ? "Rollup" : "Group");
        foreach (var (name, x) in keys) Write(x, b.Contain(op, "key", name, "Assign", null, ("target", T(name))), "value", 0);
        foreach (var (target, fn, value, weight) in aggs)
        {
            var a = b.Contain(op, "agg", target, "Aggregate", null, ("target", T(target)), ("fn", T(fn)));
            if (value is not null) Write(value, a, "value", 0);
            if (weight is not null) Write(weight, a, fn == "last" ? "order" : "weight", 0);
        }
        Wire(r.Port, op, "in");
        next.Port = P(op, "out");
        if (telemetry) { next.Cols.Insert(keys.Count, "windowStart"); next.Windowed = true; }
        return next;
    }

    (string, string, X?, X?) Aggregate(string target, FunctionCallExpression call, Rel r, bool telemetry)
    {
        var args = call.ArgumentList.Expressions.Select(a => a.Element).ToList();
        var fn = call.Name.SimpleName;
        if (r.Long is { } lg)
        {
            // over the rows of a telemetry join: `sumif(value, property == "p")` is the sum of p in the window
            string Prop(int at)
            {
                if (args.Count > at && args[at] is BinaryExpression { Operator.Text: "==", Left: NameReference pn, Right: var pv } && pn.SimpleName == lg.Property && pv.LiteralValue is string ps && lg.Props.Contains(ps)) return ps;
                if (args.Count == at && lg.Props.Length == 1) return lg.Props[0];
                throw new Refused($"aggregate one property of the telemetry: {fn}(value, property == \"p\")", call);
            }
            if (args.Count == 0 || args[0] is not NameReference vn || vn.SimpleName != lg.Value) throw new Refused($"aggregate the telemetry column '{lg.Value}'", call);
            return fn switch
            {
                "sumif" => (target, "sum", F(Prop(1)), null), "maxif" => (target, "max", F(Prop(1)), null), "minif" => (target, "min", F(Prop(1)), null),
                "sum" => (target, "sum", F(Prop(1)), null), "max" => (target, "max", F(Prop(1)), null), "min" => (target, "min", F(Prop(1)), null),
                _ => throw new Refused($"{fn} does not combine telemetry windows: use sum, min or max (with if)", call),
            };
        }
        X A(int k) => k < args.Count ? Scalar(args[k], r) : throw new Refused($"{fn} needs {k + 1} arguments", call);
        return fn switch
        {
            "count" when args.Count == 0 => (target, "count", null, null),
            "countif" => (target, "sum", new CallX("If", A(0), new LitX(Value.Integer(1)), new LitX(Value.Integer(0))), null),
            "sum" or "min" or "max" => (target, fn, A(0), null),
            "avg" => (target, "mean", A(0), null),
            "arg_max" when !telemetry && args.Count == 2 => (target, "last", A(1), A(0)),
            "dcount" or "dcountif" => throw new Refused("dcount is an estimate that may vary between runs: write distinct ... | summarize n = count()", call),
            "count_distinct" or "count_distinctif" => throw new Refused("count_distinct is a preview function: write distinct ... | summarize n = count()", call),
            "take_any" or "take_anyif" or "any" => throw new Refused("take_any picks an arbitrary value: write min, max or arg_max", call),
            _ => throw new Refused($"{fn} is not an aggregate of the profile (count, countif, sum, min, max, avg, arg_max)", call),
        };
    }

    Rel Join(Rel left, JoinOperator j)
    {
        var kind = j.Parameters.FirstOrDefault(p => p.Name.SimpleName == "kind")?.Expression.ToString().Trim();
        if (kind is null) throw new Refused("state the kind of every join: join kind=inner (KQL's default, innerunique, drops duplicate rows of the left side)", j);
        if (kind != "inner") throw new Refused($"join kind={kind} is not in the profile: write union and summarize, or kind=inner", j);
        var pairs = new List<(string L, string R)>();
        if (j.ConditionClause is not JoinOnClause on) throw new Refused("join on columns: on a, $left.b == $right.c", j);
        foreach (var c in on.Expressions.Select(x => x.Element))
            pairs.Add(c switch
            {
                NameReference n => (n.SimpleName, n.SimpleName),
                BinaryExpression { Operator.Text: "==", Left: PathExpression { Expression: NameReference { SimpleName: "$left" }, Selector: NameReference l }, Right: PathExpression { Expression: NameReference { SimpleName: "$right" }, Selector: NameReference rr } } => (l.SimpleName, rr.SimpleName),
                _ => throw new Refused("join on equal columns: on a, $left.b == $right.c", c),
            });
        if (Telemetry(j.Expression) is { } t) return Series(left, t, pairs, j);
        var right = Tab(j.Expression);
        // a right column the left side has too is renamed as KQL renames it: name1, name2, ...
        var taken = new HashSet<string>(left.Cols.Concat(left.Idents.Keys).Concat(right.Cols).Concat(right.Idents.Keys), StringComparer.Ordinal);
        var rename = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in right.Visible.Where(c => left.Cols.Contains(c) || left.Idents.ContainsKey(c)))
        {
            var k = 1; while (taken.Contains(c + k)) k++;
            rename[c] = c + k; taken.Add(c + k);
        }
        if (rename.Count > 0)
        {
            var items = right.Visible.Select(c => (c, rename.GetValueOrDefault(c, c))).ToList();
            right = Rename(right, items);
        }
        var conds = new List<X>();
        foreach (var (l, rr0) in pairs)
        {
            var rr = rename.GetValueOrDefault(rr0, rr0);
            if (left.Idents.TryGetValue(l, out var li) && right.Idents.TryGetValue(rr, out var ri))
            {
                if (li.Domain != ri.Domain) throw new Refused($"'{l}' and '{rr0}' are identities of different domains", on);
                for (var k = 0; k < li.Parts.Length; k++) conds.Add(new CallX("Equal", li.Parts[k].Value, ri.Parts[k].Value));
                continue;
            }
            if (left.Idents.ContainsKey(l) || right.Idents.ContainsKey(rr)) throw new Refused($"join an identity with an identity of the same domain", on);
            if (!left.Cols.Contains(l)) throw new Refused($"the left side has no column '{l}'", on);
            if (!right.Cols.Contains(rr)) throw new Refused($"the right side has no column '{rr0}'", on);
            var (lt, rt) = (left.Types.GetValueOrDefault(l), right.Types.GetValueOrDefault(rr));
            X Side(string c, string? t, string? other) => t is not null && t != other ? new CallX("EntityId", F(c)) : F(c);
            conds.Add(new CallX("Equal", Side(l, lt, rt), Side(rr, rt, lt)));
        }
        var op = Op("Join");
        Wire(left.Port, op, "left"); Wire(right.Port, op, "right");
        Write(conds.Aggregate((a, z) => new CallX("And", a, z)), op, "on", 0);
        var next = left.Next(P(op, "out"));
        next.Cols.AddRange(right.Cols);
        foreach (var (k, v) in right.Types) next.Types[k] = v;
        foreach (var (k, v) in right.Idents) next.Idents[k] = v;
        next.Windowed = left.Windowed || right.Windowed;
        return next;
    }

    /// <summary>A Map that renames columns (identity columns with their parts).</summary>
    Rel Rename(Rel r, List<(string From, string To)> items)
    {
        var next = new Rel { Port = "" };
        var assigns = new List<(string, X)>();
        foreach (var (from, to) in items)
        {
            if (r.Idents.TryGetValue(from, out var id))
            {
                for (var k = 0; k < id.Parts.Length; k++) { assigns.Add(($"{to}__{k + 1}", id.Parts[k].Value)); next.Cols.Add($"{to}__{k + 1}"); }
                next.Idents[to] = new IdX(id.Domain, [.. id.Parts.Select((p, k) => (p.Component, (X)F($"{to}__{k + 1}")))]);
            }
            else { assigns.Add((to, F(from))); next.Cols.Add(to); if (r.Types.TryGetValue(from, out var t)) next.Types[to] = t; }
        }
        var op = Op("Map");
        for (var k = 0; k < assigns.Count; k++)
            Write(assigns[k].Item2, b.Contain(op, "assign", assigns[k].Item1, "Assign", k, ("target", T(assigns[k].Item1))), "value", 0);
        Wire(r.Port, op, "in");
        next.Port = P(op, "out");
        next.Windowed = r.Windowed && next.Cols.Contains("windowStart");
        return next;
    }

    sealed record TeleSource(string Graph, long WithinNs, long? TtlNs, decimal? Fill, string Id, string Property, string Value, string? NoData, string[] Props, SyntaxNode At);

    /// <summary>`telemetry("g", lookback) [| where property in (...)] | project k = id, windowStart, property, value`, or null.</summary>
    TeleSource? Telemetry(Expression e)
    {
        while (e is ParenthesizedExpression p) e = p.Expression;
        var ops = new List<QueryOperator>();
        while (e is PipeExpression pe) { ops.Insert(0, pe.Operator); e = pe.Expression; }
        if (e is not FunctionCallExpression { Name.SimpleName: "telemetry" or "telemetry_filled" } f) return null;
        var args = f.ArgumentList.Expressions.Select(a => a.Element).ToList();
        long? ttl = null; decimal? fill = null; string? noData = null;
        if (f.Name.SimpleName == "telemetry_filled")
        {
            // telemetry_filled(g, lookback, ttl, fill): a window without a value takes the fill; an element silent for ttl is in no data
            if (args.Select(Const).ToList() is not [string, TimeSpan, TimeSpan life, var fv]) throw new Refused("telemetry_filled(\"graph\", lookback, ttl, fill)", f);
            ttl = life.Ticks * 100;
            fill = fv is null ? null : Convert.ToDecimal(fv, CultureInfo.InvariantCulture);
        }
        else if (args.Count != 2) throw new Refused("telemetry(\"graph\", lookback)", f);
        if (args.Select(Const).ToList() is not [string g, TimeSpan look, ..]) throw new Refused("telemetry(\"graph\", lookback)", f);
        var props = new List<string>(); string id = "id", prop = "property", val = "value";
        foreach (var op in ops)
            switch (op)
            {
                case FilterOperator { Condition: InExpression { Operator.Text: "in", Left: NameReference { SimpleName: "property" } } ie }:
                    props.AddRange(ie.Right.Expressions.Select(x => Const(x.Element) as string ?? throw new Refused("name telemetry properties as text", x.Element))); break;
                case FilterOperator { Condition: BinaryExpression { Operator.Text: "==", Left: NameReference { SimpleName: "property" }, Right: var pr } } when Const(pr) is string one:
                    props.Add(one); break;
                case ProjectOperator po:
                    foreach (var it in po.Expressions.Select(x => x.Element))
                    {
                        var (to, from) = it switch { SimpleNamedExpression { Expression: NameReference n } s => (s.Name.SimpleName, n.SimpleName), NameReference n => (n.SimpleName, n.SimpleName), _ => throw new Refused("the telemetry is projected by renaming its columns", it) };
                        switch (from) { case "id": id = to; break; case "property": prop = to; break; case "value": val = to; break; case "windowStart" when to == "windowStart": break; case "noData" when to == "noData" && ttl is not null: noData = to; break; default: throw new Refused($"the telemetry has no column '{from}' to project as '{to}'", it); }
                    }
                    break;
                default: throw new Refused("filter the telemetry by property and project it; join the rest", op);
            }
        return new TeleSource(g, look.Ticks * 100, ttl, fill, id, prop, val, noData, [.. props.Distinct()], f);
    }

    /// <summary>A join with the telemetry of a graph is a series: per row, the windows of the element the join column holds.</summary>
    Rel Series(Rel left, TeleSource t, List<(string L, string R)> pairs, SyntaxNode at)
    {
        if (pairs is not [var (l, rr)] || rr != t.Id) throw new Refused($"join the telemetry on its element: on $left.x == $right.{t.Id}", at);
        if (!left.Types.ContainsKey(l)) throw new Refused($"'{l}' is not an element: join the telemetry on a column that holds an element of the graph", at);
        if (t.Props.Length == 0) throw new Refused("name the telemetry properties: | where property in (\"p\", \"q\")", t.At);
        var op = t.TtlNs is { } life ? Op("Series", ("within", Value.Integer(t.WithinNs)), ("ttl", Value.Integer(life))) : Op("Series", ("within", Value.Integer(t.WithinNs)));
        Wire(GraphPort(t.Graph, t.At), op, "graph");
        Wire(left.Port, op, "in");
        for (var k = 0; k < t.Props.Length; k++)
            if (t.Fill is { } fv) b.Contain(op, "read", "#" + k, "FieldRef", k, ("path", T($"{l}.{t.Props[k]}")), ("fill", Value.Decimal(fv)));
            else b.Contain(op, "read", "#" + k, "FieldRef", k, ("path", T($"{l}.{t.Props[k]}")));
        var next = left.Next(P(op, "out"));
        next.Cols.AddRange(["windowStart", "windowEnd", .. t.Props]);
        var tv = View(inputs[t.Graph]);
        foreach (var prop in t.Props)
            if (tv.PropsOf(left.Types[l]).Keys.Select(k => tv.Props[k]).FirstOrDefault(pi => pi.Name == prop) is { } pi) next.Kinds[prop] = pi.ValueKind;
        if (t.NoData is not null) next.Cols.Add("noData");
        next.Windowed = true;
        next.Long = (t.Property, t.Value, t.Props);
        return next;
    }

    static readonly string[] ThresholdCols = ["windowStart", "windowEnd", "value", "above", "enterAfter", "below", "exitAfter", "noData"];

    /// <summary>`invoke hysteresis(high, low, initial[, nodata])` over windowed rows with the columns value, above, enterAfter, below, exitAfter: a Threshold keyed by every other column.</summary>
    Rel Hysteresis(Rel r, List<(string Name, X Value)> cols, InvokeOperator i)
    {
        if (i.Function is not FunctionCallExpression { Name.SimpleName: "hysteresis" } f) throw new Refused("the one function a projection invokes is hysteresis(...)", i);
        var args = f.ArgumentList.Expressions.Select(a => Const(a.Element) as string ?? throw new Refused("hysteresis takes the states as text", a.Element)).ToList();
        if (args.Count is < 3 or > 4) throw new Refused("hysteresis(high, low, initial[, nodata])", f);
        if (!r.Windowed) throw new Refused("hysteresis reads telemetry windows", i);
        X Col(string c) => cols.FirstOrDefault(x => x.Name == c).Value ?? throw new Refused($"hysteresis reads the column '{c}'", i);
        foreach (var c in ThresholdCols[2..7]) _ = Col(c);
        var keys = new List<(string Name, X Value)>(); var next = new Rel { Port = "" };
        foreach (var (name, x) in cols.Where(c => !ThresholdCols.Contains(c.Name)))
        {
            if (x is IdX id)
            {
                for (var k = 0; k < id.Parts.Length; k++) keys.Add(($"{name}__{k + 1}", id.Parts[k].Value));
                next.Idents[name] = new IdX(id.Domain, [.. id.Parts.Select((p, k) => (p.Component, (X)F($"{name}__{k + 1}")))]);
            }
            else { keys.Add((name, x)); if (x is FieldX { Path: var path } && !path.Contains('.') && r.Types.TryGetValue(path, out var t)) next.Types[name] = t; }
        }
        decimal Dec(string c) => Col(c) is LitX { V: var v } ? v switch { DecimalValue d => d.Q, IntegerValue n => (decimal)n.N, _ => 0 } : 0;
        var props = new List<(string, Value)>
        {
            ("target", T("state")), ("above", Value.Decimal(Dec("above"))), ("enterAfter", Value.Integer((long)Dec("enterAfter"))), ("high", T(args[0])),
            ("below", Value.Decimal(Dec("below"))), ("exitAfter", Value.Integer((long)Dec("exitAfter"))), ("low", T(args[1])), ("initial", T(args[2])),
        };
        if (args.Count == 4) props.Add(("nodata", T(args[3])));
        var op = Op("Threshold", [.. props]);
        foreach (var (k, x) in keys) Write(x, b.Contain(op, "key", k, "Assign", null, ("target", T(k))), "value", 0);
        Write(Col("value"), op, "value", 0);
        foreach (var (c, slot) in new[] { ("above", "aboveBy"), ("enterAfter", "enterBy"), ("below", "belowBy"), ("exitAfter", "exitBy") })
            if (Col(c) is not LitX) Write(Col(c), op, slot, 0);
        Wire(r.Port, op, "in");
        next.Port = P(op, "out");
        next.Cols = [.. keys.Select(k => k.Name), "state"];
        return next;
    }

    Rel Union(Rel? first, UnionOperator u)
    {
        if (u.Parameters.Count > 0) throw new Refused("union without parameters", u);
        var parts = new List<Rel>();
        if (first is not null) parts.Add(first);
        parts.AddRange(u.Expressions.Select(x => Tab(x.Element)));
        var cols = parts[0].Visible.ToList();
        for (var k = 1; k < parts.Count; k++)
        {
            if (!parts[k].Visible.ToHashSet().SetEquals(cols)) throw new Refused($"the tables of a union have the same columns: project each to {string.Join(", ", cols)}", u);
            if (!parts[k].Visible.SequenceEqual(cols)) parts[k] = Rename(parts[k], [.. cols.Select(c => (c, c))]);
        }
        var acc = parts[0];
        foreach (var p in parts.Skip(1))
        {
            var op = Op("Union", ("conflict", T("preserveEvidence")));
            Wire(acc.Port, op, "in0"); Wire(p.Port, op, "in1");
            acc = acc.Next(P(op, "out"));
        }
        return acc;
    }

    Rel DataTable(DataTableExpression d)
    {
        var names = d.Schema.Columns.Select(c => c.Element.Name.SimpleName).ToList();
        var vals = d.Values.Select(v => v.Element).ToList();
        if (vals.Count % names.Count != 0) throw new Refused("a datatable row has a value per column", d);
        var op = Op("Values");
        for (var r = 0; r < vals.Count / names.Count; r++)
        {
            var row = b.Contain(op, "row", "#" + r, "RowValue", r);
            for (var c = 0; c < names.Count; c++)
                Write(new LitX(Lit(vals[r * names.Count + c])), b.Contain(row, "assign", names[c], "Assign", null, ("target", T(names[c]))), "value", 0);
        }
        return new Rel { Port = P(op, "out"), Cols = names };
    }

    // ---- expressions ----

    X Scalar(Expression e, Rel r, IReadOnlyDictionary<Symbol, X>? args = null)
    {
        switch (e)
        {
            case ParenthesizedExpression p: return Scalar(p.Expression, r, args);
            case LiteralExpression: return new LitX(Lit(e));
            case NameReference n when scope.Paths.Contains(n.SimpleName):
            case PathExpression { Expression: NameReference pn } when scope.Paths.Contains(pn.SimpleName):
                throw new Refused("the edges of a path are not kept: every path would be listed, which is not kept up to date; project the two ends", e);
            case NameReference n:
                if (args is not null && n.ReferencedSymbol is { } asym && args.TryGetValue(asym, out var a)) return a;
                if (n.ReferencedSymbol is { } ns && scope.Values.TryGetValue(ns, out var c)) return new LitX(FromObject(c, e));
                // a let of a constant is its value; Kusto has told it apart from a column of the same name
                if (n.ReferencedSymbol is VariableSymbol vs && letSyntax.TryGetValue(vs, out var sl) && (sl.Expression.IsConstant || sl.Expression.IsLiteral)) return new LitX(FromObject(Const(sl.Expression), e));
                if (r.Idents.ContainsKey(n.SimpleName)) throw new Refused($"'{n.SimpleName}' is an identity: it names a node or an end of an edge, it is not a value", e);
                if (r.Long is { Props: [var only] } lg && n.SimpleName == lg.Value) return F(only);
                if (r.Long is { Props: [var one] } lp && n.SimpleName == lp.Property) return new LitX(T(one));
                if (r.Cols.Contains(n.SimpleName) || parms.Contains(n.SimpleName)) return F(n.SimpleName);
                throw new Refused($"the rows have no column '{n.SimpleName}'", e);
            case PathExpression { Expression: NameReference v, Selector: NameReference s } when r.Types.ContainsKey(v.SimpleName):
                return F(s.SimpleName == "id" ? v.SimpleName : $"{v.SimpleName}.{s.SimpleName}");
            case PrefixUnaryExpression { Operator.Text: "-", Expression: LiteralExpression } u when Lit(u.Expression) is IntegerValue or DecimalValue:
                return new LitX(Lit(u.Expression) switch { IntegerValue i => Value.Integer(-i.N), DecimalValue d => Value.Decimal(-d.Q), var v => v });
            case PrefixUnaryExpression { Operator.Text: "-" } u: return new CallX("Negate", Scalar(u.Expression, r, args));
            case InExpression { Operator.Text: "in" } ie:
                return ie.Right.Expressions.Select(x => (X)new CallX("Equal", Scalar(ie.Left, r, args), Scalar(x.Element, r, args))).Aggregate((x, z) => new CallX("Or", x, z));
            case BinaryExpression be:
                {
                    var fn = be.Operator.Text switch
                    {
                        "==" => "Equal", "!=" => "NotEqual", "<" => "LessThan", "<=" => "LessOrEqual", ">" => "GreaterThan", ">=" => "GreaterOrEqual",
                        "and" => "And", "or" => "Or", "+" => "Add", "-" => "Subtract", "*" => "Multiply", "/" => "Divide",
                        var o => throw new Refused($"the operator '{o}' is not in the profile", be),
                    };
                    return new CallX(fn, Scalar(be.Left, r, args), Scalar(be.Right, r, args));
                }
            case FunctionCallExpression f: return Call(f, r, args);
            default: throw new Refused($"'{Short(e)}' is not an expression of the profile", e);
        }
    }

    X Call(FunctionCallExpression f, Rel r, IReadOnlyDictionary<Symbol, X>? args)
    {
        if (f.Name.SimpleName is "now" or "ago") throw new Refused("in a projection now() and ago() mean the data's time, the newest input time, never the wall clock; they are accepted only where the engine keeps the result up to date: where time > ago(24h), and the look-back of telemetry", f);
        var a = f.ArgumentList.Expressions.Select(x => Scalar(x.Element, r, args)).ToList();
        // a function of the author's text is the author's, whatever its name: Kusto binds the call to it over a library function of the same name
        if (f.ReferencedSymbol is { } own && functions.TryGetValue(own, out var mine))
        {
            var ps = mine.Parameters.Parameters.Select(p => p.Element.NameAndType.Name.ReferencedSymbol!).ToList();
            if (ps.Count != a.Count || mine.Body.Expression is null || mine.Body.Statements.Count > 0) throw new Refused($"{f.Name.SimpleName} takes {ps.Count} values and is one expression", f);
            return Scalar(mine.Body.Expression, r, ps.Zip(a).ToDictionary(x => x.First, x => x.Second));
        }
        X Nest(string fn) => a.Count < 2 ? throw new Refused($"{f.Name.SimpleName} takes two or more values", f) : a.SkipLast(1).Reverse().Aggregate(a[^1], (acc, x) => new CallX(fn, x, acc));
        X One(string fn) => a.Count == 1 ? new CallX(fn, a[0]) : throw new Refused($"{f.Name.SimpleName} takes one value", f);
        switch (f.Name.SimpleName)
        {
            case "iff" or "iif": return a.Count == 3 ? new CallX("If", a[0], a[1], a[2]) : throw new Refused("iff(condition, then, else)", f);
            case "case":
                if (a.Count % 2 == 0) throw new Refused("case(condition, value, ..., else)", f);
                return Enumerable.Range(0, a.Count / 2).Reverse().Aggregate(a[^1], (acc, k) => new CallX("If", a[2 * k], a[2 * k + 1], acc));
            case "coalesce": return Nest("Coalesce");
            case "strcat": return Nest("Concat");
            case "tolower": return One("LowerInvariant");
            case "not": return One("Not");
            case "isnotnull": return One("IsPresent");
            case "isnull": return new CallX("Not", One("IsPresent"));
            case "toreal" or "todouble" or "todecimal":
                // the engine converts an Integer; a value that is already Decimal is itself (in KQL every telemetry value is real)
                return a is [FieldX { Path: var path }] && r.Kinds.GetValueOrDefault(path) == "Decimal" ? a[0] : One("ToDecimal");
            case "rate": return One("Rate");
            case "identity": throw new Refused("identity(...) names a node: assign it to a column and use the column as the node or an end of an edge", f);
            case var other: throw new Refused($"{other}() is not a function of the profile", f);
        }
    }

    static Value Lit(Expression e) => e.LiteralValue switch
    {
        null => Value.Null,
        string s => Value.Text(s),
        bool v => Value.Boolean(v),
        long or int => Value.Integer(Convert.ToInt64(e.LiteralValue, CultureInfo.InvariantCulture)),
        double or decimal => Value.Decimal(decimal.Parse(e.ToString().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture)),
        var v => throw new Refused($"a constant of type {v.GetType().Name} is not in the profile", e),
    };

    string Write(X x, string parent, string slot, int ordinal, bool ordered = false)
    {
        var seg = "#" + ordinal; int? ord = ordered ? ordinal : null;
        switch (x)
        {
            case FieldX f: return b.Contain(parent, slot, seg, "FieldRef", ord, ("path", T(f.Path)));
            case LitX l: return b.Contain(parent, slot, seg, "Literal", ord, ("value", l.V));
            case CallX c:
                {
                    var id = b.Contain(parent, slot, seg, "Call", ord);
                    b.Edge(id, "function", Ids.KBuiltin(c.Fn));
                    for (var i = 0; i < c.Args.Length; i++) Write(c.Args[i], id, "arg", i, true);
                    return id;
                }
            case IdX d:
                {
                    var id = b.Contain(parent, slot, seg, "IdOf", ord);
                    b.Edge(id, "domain", d.Domain);
                    foreach (var (comp, v) in d.Parts)
                    {
                        var sc = b.Contain(id, "component", Ids.Name(comp), "SetComponent");
                        b.Edge(sc, "component", comp);
                        Write(v, sc, "value", 0);
                    }
                    return id;
                }
            default: throw new InvalidOperationException();
        }
    }

    static string Short(SyntaxNode e) { var s = e.ToString().Trim(); return s.Length > 60 ? s[..60] + "..." : s; }
}
