using System.Globalization;
using Sattle.Core;

namespace Sattle.Language;

/// <summary>Typing judgments of research 03 section 3.2 over a definition graph: expressions, operators, wires, projection and direction rules. Produces the typed form the plan compiler consumes.</summary>
public sealed class ProjectionChecker(Graph defs)
{
    readonly DefView d = new(defs);
    readonly List<Diagnostic> diags = [];
    readonly HashSet<string> demands = new(StringComparer.Ordinal);
    readonly Dictionary<string, OntologyView> matchOntology = new(StringComparer.Ordinal);
    OntologyView outView = null!;
    string projectionId = "";
    readonly Dictionary<string, string> paramKinds = new(StringComparer.Ordinal);
    readonly Dictionary<string, Value> paramDefaults = new(StringComparer.Ordinal);
    IReadOnlyDictionary<string, Value>? paramValues;

    void Err(string code, string focus, string msg, string sev = "error") => diags.Add(new Diagnostic(code, sev, focus, msg));

    /// <summary>The ontology a module sees (itself and what it imports), built once per checker: the definition graph does not change while a checker lives, and every Match, Series and Traverse of every pass asks for one.</summary>
    readonly Dictionary<string, OntologyView> views = new(StringComparer.Ordinal);
    OntologyView ViewOf(string module) => views.TryGetValue(module, out var v) ? v : views[module] = OntologyView.Build(defs, Closure(defs, module));

    public static HashSet<string> Closure(Graph g, string module)
    {
        var d = new DefView(g);
        var set = new HashSet<string>(StringComparer.Ordinal) { Ids.KernelModule };
        var todo = new Stack<string>(); todo.Push(module);
        while (todo.Count > 0)
        {
            var m = todo.Pop();
            if (!set.Add(m) && m != module) continue;
            set.Add(m);
            foreach (var t in d.Targets(Ids.Module(m), "imports")) { var im = Ids.ModuleOf(t); if (!set.Contains(im)) todo.Push(im); }
        }
        return set;
    }

    /// <summary>The operators of the projection last checked that typed, by name, with their output row schemas: also when another operator of it did not (an editor shows the columns up to an error).</summary>
    public IReadOnlyDictionary<string, TOp> Typed { get; private set; } = new Dictionary<string, TOp>();

    /// <summary>Checks one projection. With paramValues (the constants of a space binding) a parameter reads as its value; without, as a typed placeholder, which is enough to check the projection on its own.</summary>
    public (TypedProjection? Projection, List<Diagnostic> Diagnostics) Check(string pid, IReadOnlyDictionary<string, Value>? values = null)
    {
        if (LiftExpander.HasLift(defs, pid))
        {
            var ex = LiftExpander.Expand(defs, pid);
            if (ex.Diagnostics.Any(x => x.IsError)) return (null, ex.Diagnostics);
            var inner = new ProjectionChecker(ex.Graph);
            var (tp, dg) = inner.Check(pid, values);
            Typed = inner.Typed;
            var names = ex.Lifts.Select(l => l.Name).ToList();
            bool Of(string lift, string focusOrName, string root) => focusOrName == root + "/op:" + lift || focusOrName.StartsWith(root + "/op:" + lift + "__", StringComparison.Ordinal) || focusOrName.StartsWith(root + "/op:" + lift + "#", StringComparison.Ordinal) || focusOrName.StartsWith(root + "/op:" + lift + ".", StringComparison.Ordinal) || focusOrName.StartsWith(root + "/op:" + lift + "/", StringComparison.Ordinal);
            var mapped = dg.Select(x => names.FirstOrDefault(n => Of(n, x.Focus, pid)) is { } n ? x with { Message = $"lift {n}: {x.Message}" } : x).ToList();
            if (tp is null) return (null, [.. ex.Diagnostics, .. mapped]);
            bool Generated(string lift, string op) => op == lift || op.StartsWith(lift + "__", StringComparison.Ordinal) || op.StartsWith(lift + "#", StringComparison.Ordinal) || op.StartsWith(lift + ".", StringComparison.Ordinal);
            var d0 = new DefView(defs);
            var info = ex.Lifts.Select(l => new TLiftInfo(l.Name, l.Id, Ids.Name(d0.Target(l.Id, "ofType")!), Ids.Name(d0.Target(l.Id, "liftEdge")!), Ids.Name(d0.Target(l.Id, "liftVia")!), d0.Text(l.Id, "images") ?? "function", [.. tp.Ops.Where(o => Generated(l.Name, o.Name)).Select(o => o.Name)])).ToList();
            tp = new TypedProjection { Id = tp.Id, Name = tp.Name, Module = tp.Module, Purpose = tp.Purpose, Forward = tp.Forward, Retain = tp.Retain, OutputModule = tp.OutputModule, Ports = tp.Ports, Ops = tp.Ops, Rules = tp.Rules, Params = tp.Params, Lifts = info };
            return (tp, [.. ex.Diagnostics, .. mapped]);
        }
        projectionId = pid; paramValues = values; paramKinds.Clear(); paramDefaults.Clear(); demands.Clear();
        Dictionary<string, TOp>? ops = null;
        TypedProjection? result = null;
        for (var pass = 0; pass < 6; pass++)
        {
            var before = demands.Count;
            diags.Clear();
            result = Pass(pid, out ops);
            if (demands.Count == before) break;
        }
        Typed = ops ?? [];
        return (diags.Any(x => x.IsError) ? null : result, [.. diags]);
    }

    // ---- ports, wires, ordering ------------------------------------------------------------------------

    Ty KindOf(string? valueKindId, string? itemKindId, string? refersTo, string unit)
    {
        var vk = Ids.Name(valueKindId ?? "");
        return vk switch
        {
            "Text" => Ty.Text, "Integer" => Ty.Int(unit), "Decimal" => Ty.Dec(unit), "Boolean" => Ty.Boolean, "Instant" => Ty.Instant,
            "Entity" => Ty.Ent(refersTo), "List" => new Ty(Tag.List, "1", null, ItemTag(itemKindId is null ? null : Ids.Name(itemKindId))),
            _ => Ty.Text,
        };
    }

    /// <summary>The kind of the items of a list. A list of `Any`, or of a kind the checker has no tag for, has untyped items.</summary>
    static Tag? ItemTag(string? kind) => kind switch { null or "Any" => null, "Entity" => Tag.Ent, _ => Enum.TryParse<Tag>(kind, out var t) ? t : null };

    Schema RowSchema(string rowTypeId)
    {
        var cols = d.Children(rowTypeId, "field").Select(f => new Col(d.Text(f, "name") ?? Ids.Name(f),
            new FType(KindOf(d.Target(f, "valueKind"), d.Target(f, "itemKind"), d.Target(f, "refersTo"), d.Text(f, "unit") ?? "1"), !d.Bool(f, "optional"), Role.Data))).ToList();
        return new Schema(cols);
    }

    TypedProjection? Pass(string pid, out Dictionary<string, TOp> opsByName)
    {
        opsByName = [];
        var module = Ids.ModuleOf(pid);
        var outTarget = d.Target(pid, "outputOntology");
        if (outTarget is null) { Err("P1", pid, "projection has no output ontology"); return null; }
        var outMod = Ids.ModuleOf(outTarget);
        var closure = Closure(defs, module);
        if (!closure.Contains(outMod)) Err("P1", pid, $"output ontology {outMod} is neither this module nor imported");
        outView = ViewOf(outMod);

        // ports
        var ports = new List<TPort>();
        var portSchemas = new Dictionary<string, Schema>();
        foreach (var p in d.Children(pid, "port"))
        {
            var kind = d.Text(p, "kind") ?? "rows";
            var name = d.Text(p, "name") ?? Ids.Name(p);
            if (kind == "param")
            {
                var vk = d.Target(p, "valueKind");
                if (vk is null) { Err("W3", p, $"parameter {name} has no value kind"); continue; }
                paramKinds[name] = Ids.Name(vk);
                if (d.Prop(p, "default") is { } dv && dv is not NullValue) paramDefaults[name] = dv;
                continue;
            }
            if (kind == "rows")
            {
                var rt = d.Target(p, "rowType");
                if (rt is null) { Err("W3", p, $"rows port {name} has no row type"); continue; }
                var sch = RowSchema(rt);
                portSchemas[p] = sch;
                ports.Add(new TPort(name, p, "rows", sch, rt, null));
            }
            else
            {
                var ex = d.Target(p, "expects");
                if (ex is null) { Err("W3", p, $"graph port {name} expects no module"); continue; }
                ports.Add(new TPort(name, p, "graph", null, null, Ids.ModuleOf(ex)));
            }
        }

        // operators and wires
        var opIds = d.Children(pid, "op");
        var producerOf = new Dictionary<string, string>(); // consuming port id -> producing port id
        foreach (var op in opIds)
            foreach (var cp in d.Children(op, "port").Where(x => d.Text(x, "direction") == "in"))
            {
                var inc = defs.InRels(cp).Select(defs.Rel).Where(r => r is not null && Ids.Name(r!.Type) == "wire").ToList();
                if (inc.Count != 1) { Err("W1", cp, $"port {Ids.Name(cp)} of {Ids.Name(op)} must have exactly one incoming wire, found {inc.Count}"); continue; }
                producerOf[cp] = inc[0]!.Source;
                var ps = d.Text(inc[0]!.Source, "kind"); var cs = d.Text(cp, "kind");
                if (ps != cs) Err("W2", cp, $"wire into {Ids.Name(op)}/{Ids.Name(cp)} connects a {ps} port to a {cs} port");
            }
        var opByOutPort = opIds.ToDictionary(op => Ids.Child(op, "port", "out"), op => op);
        // topological order (W4)
        var order = new List<string>(); var state = new Dictionary<string, int>();
        void Visit(string op)
        {
            var s = state.GetValueOrDefault(op);
            if (s == 2) return;
            if (s == 1) { Err("W4", op, "operator graph has a cycle"); return; }
            state[op] = 1;
            foreach (var cp in d.Children(op, "port").Where(x => d.Text(x, "direction") == "in"))
                if (producerOf.TryGetValue(cp, out var prod) && opByOutPort.TryGetValue(prod, out var pop)) Visit(pop);
            state[op] = 2; order.Add(op);
        }
        foreach (var op in opIds) Visit(op);

        var schemas = new Dictionary<string, Schema>(portSchemas); // keyed by producing port id
        var typed = new List<TOp>();
        var ruleOf = new Dictionary<string, string>();
        var rules = new List<TRule>();
        foreach (var r in d.Children(pid, "rule"))
        {
            var sinks = d.Targets(r, "governs").ToList();
            foreach (var s in sinks) ruleOf[s] = d.Text(r, "name") ?? Ids.Name(r);
            rules.Add(new TRule(d.Text(r, "name") ?? Ids.Name(r), d.Text(r, "class") ?? "readonly", d.Text(r, "reason"), sinks.Select(Ids.Name).ToList(), Policy(r), d.Bool(r, "putput")));
        }
        foreach (var op in order)
        {
            var kind = d.TypeName(op);
            var name = d.Text(op, "name") ?? Ids.Name(op);
            var inputs = new Dictionary<string, Src>();
            foreach (var cp in d.Children(op, "port").Where(x => d.Text(x, "direction") == "in"))
                if (producerOf.TryGetValue(cp, out var prod))
                    inputs[Ids.Name(cp)] = opByOutPort.TryGetValue(prod, out var pop) ? new Src(false, d.Text(pop, "name") ?? Ids.Name(pop)) : new Src(true, Ids.Name(prod));
            Schema? In(string port) => inputs.ContainsKey(port) && producerOf.TryGetValue(Ids.Child(op, "port", port), out var pp) ? schemas.GetValueOrDefault(pp) : null;
            TOp? t;
            try { t = TypeOp(op, kind, name, inputs, In, ruleOf.GetValueOrDefault(op), ports); }
            catch (DuplicateColumnException e) { Err("DuplicateColumn", op, $"{name}: {e.Message}"); t = null; }
            if (t is null) continue;
            typed.Add(t); opsByName[name] = t;
            schemas[Ids.Child(op, "port", "out")] = t.Out;
        }
        EmitterChecks(typed, rules, pid);
        Direction(pid, typed, rules, ports);
        var forward = d.Bool(pid, "forward");
        if (forward == (rules.Count > 0)) Err("P3", pid, forward ? "a forward projection must have no rules" : "a projection with no rules must be declared forward");
        if (d.Text(pid, "purpose") == "ingestion" && forward) Err("P3", pid, "an ingestion projection cannot be forward");
        return new TypedProjection
        {
            Id = pid, Name = d.Text(pid, "name") ?? Ids.Name(pid), Module = module, Purpose = d.Text(pid, "purpose") ?? "view", Forward = forward,
            Retain = (int)d.Int(pid, "retain"), OutputModule = outMod, Ports = ports, Ops = Unroll(typed, schemas, pid), Rules = rules, Params = [.. paramKinds.Select(kv => (kv.Key, kv.Value, paramDefaults.GetValueOrDefault(kv.Key)))],
        };
    }

    /// <summary>Fixpoint by bounded unrolling: R0 = distinct(seed), R(i+1) = distinct(R(i) union step(R(i))), the result is R(max). The step operators are typed once (against the seed schema) and cloned per iteration; every operator involved is incremental, so retractions are handled like any other change. Facts that need more than max steps are not derived; maxFacts is recorded and not enforced.</summary>
    List<TOp> Unroll(List<TOp> typed, Dictionary<string, Schema> schemas, string pid)
    {
        var fixes = typed.OfType<TFixpoint>().ToList();
        if (fixes.Count == 0) return typed;
        var byName = typed.ToDictionary(t => t.Name);
        var remove = new HashSet<string>(fixes.Select(f => f.Name)); var made = new List<TOp>();
        foreach (var fp in fixes)
        {
            var overOp = byName.Values.FirstOrDefault(t => Ids.Child(t.Id, "port", "out") == d.Target(fp.Id, "over"));
            if (overOp is null) { Err("Fixpoint", fp.Id, "the step result of a fixpoint must be an operator output"); continue; }
            // the step: operators between the relation and the result
            var inner = new List<TOp>(); var seen = new HashSet<string>();
            void Walk(TOp t)
            {
                if (t.Name == fp.Name || !seen.Add(t.Name)) return;
                foreach (var s in t.Inputs.Values.Where(s => !s.IsPort && byName.ContainsKey(s.Name) && Reaches(byName[s.Name], fp.Name, byName))) Walk(byName[s.Name]);
                inner.Add(t);
            }
            Walk(overOp);
            foreach (var t in inner) remove.Add(t.Name);
            // F5: a body that computes values (a depth counter) leaves the finite domain, and then only the bound ends the iteration (Theorem 6.5)
            var computed = inner.OfType<TMap>().SelectMany(m => m.Assigns).Where(a => a.Value is not TField and not TLit).Select(a => a.Name).ToList();
            if (computed.Count > 0) { Err("Fixpoint", fp.Id, $"the step computes {string.Join(", ", computed.Select(c => $"'{c}'"))}; a fixpoint body copies fields or uses literals, so its facts stay in a finite domain (F5)"); continue; }
            if (overOp.Out.Window != 0 || overOp.Out.Cols.Count != fp.Out.Cols.Count || overOp.Out.Cols.Zip(fp.Out.Cols).Any(x => x.First.Name != x.Second.Name || x.First.T.Ty != x.Second.T.Ty || !x.First.T.Role.IsData))
            { Err("Fixpoint", fp.Id, $"the step must produce the relation's columns {fp.Out}, found {overOp.Out}"); continue; }
            var seed = fp.Inputs["seed"];
            var prev = $"{fp.Name}@0";
            made.Add(new TDistinct(prev, fp.Id, new Dictionary<string, Src> { ["in"] = seed }, fp.Out));
            for (var i = 1; i <= fp.MaxIterations; i++)
            {
                var rename = inner.ToDictionary(t => t.Name, t => $"{fp.Name}@{i}.{t.Name}");
                Src Remap(Src s) => s.IsPort ? s : s.Name == fp.Name ? new Src(false, prev) : rename.TryGetValue(s.Name, out var r) ? new Src(false, r) : s;
                foreach (var t in inner) made.Add(t with { Name = rename[t.Name], Inputs = t.Inputs.ToDictionary(kv => kv.Key, kv => Remap(kv.Value)) });
                var u = $"{fp.Name}@{i}.union";
                made.Add(new TUnion(u, fp.Id, new Dictionary<string, Src> { ["in0"] = new Src(false, prev), ["in1"] = new Src(false, rename[overOp.Name]) }, fp.Out, "preserveEvidence"));
                prev = i == fp.MaxIterations ? fp.Name : $"{fp.Name}@{i}";
                made.Add(new TDistinct(prev, fp.Id, new Dictionary<string, Src> { ["in"] = new Src(false, u) }, fp.Out));
            }
        }
        // stable topological order: an operator follows the operators it reads
        var all = typed.Where(t => !remove.Contains(t.Name)).Concat(made).ToList();
        var names = all.Select(t => t.Name).ToHashSet(); var done = new HashSet<string>(); var res = new List<TOp>();
        void Place(TOp t)
        {
            if (!done.Add(t.Name)) return;
            foreach (var s in t.Inputs.Values.Where(s => !s.IsPort && names.Contains(s.Name))) Place(all.First(x => x.Name == s.Name));
            res.Add(t);
        }
        foreach (var t in all) Place(t);
        _ = schemas; _ = pid;
        return res;
    }

    static bool Reaches(TOp t, string target, Dictionary<string, TOp> byName)
    {
        var seen = new HashSet<string>(); var todo = new Stack<TOp>(); todo.Push(t);
        while (todo.Count > 0)
        {
            var x = todo.Pop();
            if (!seen.Add(x.Name)) continue;
            foreach (var s in x.Inputs.Values.Where(s => !s.IsPort))
            {
                if (s.Name == target) return true;
                if (byName.TryGetValue(s.Name, out var up)) todo.Push(up);
            }
        }
        return false;
    }

    TPolicy? Policy(string rule)
    {
        var accept = defs.Entity(rule)?.Props.Get(Ids.KProp("accept")) is ListValue l ? l.Items.OfType<TextValue>().Select(x => x.S).ToList() : [];
        var ck = d.Target(rule, "createKey");
        var onDelete = d.Text(rule, "onDelete");
        if (accept.Count == 0 && ck is null && onDelete is null) return null;
        var source = ck is null ? null : d.Target(ck, "identifies");
        var keySchema = new Schema([.. (ck is null ? [] : d.Children(ck, "component")).Select(c => new Col("key." + Ids.Name(c), new FType(Ty.Text, true, Role.Data)))]);
        List<(string, string, TExpr)> Sets(string slot, Schema sch)
        {
            var res = new List<(string, string, TExpr)>();
            foreach (var sp in d.Children(rule, slot))
            {
                var pid = d.Target(sp, "property"); var e = Ex(Child(sp, "value"), sch);
                if (pid is null || e is null) continue;
                res.Add((pid, d.Text(pid, "name") ?? Ids.Name(pid), e));
            }
            return res;
        }
        return new TPolicy(accept, ck, ck is null ? null : d.Text(ck, "name"), source, Sets("createDefault", keySchema), onDelete, Sets("deleteSet", new Schema([])));
    }

    // ---- operators -------------------------------------------------------------------------------------

    TOp? TypeOp(string op, string kind, string name, Dictionary<string, Src> inputs, Func<string, Schema?> input, string? rule, List<TPort> ports)
    {
        Schema? R = input("in");
        switch (kind)
        {
            case "Match": return TypeMatch(op, name, inputs, ports);
            case "Filter":
                {
                    if (R is null) return null;
                    var e = Ex(Child(op, "predicate"), R);
                    if (e is null) return null;
                    if (e.Type.Ty.Tag != Tag.Boolean || !e.Type.Req || !e.Type.Role.IsData) { Err("Filter", op, $"filter predicate must be (Boolean, req, data), found {e.Type}"); return null; }
                    var present = new HashSet<string>();
                    void Conj(TExpr x) { if (x is TCall { Fn: "And" } a) foreach (var s in a.Args) Conj(s); else if (x is TCall { Fn: "IsPresent", Args: [TField f] }) present.Add(f.Name); }
                    Conj(e);
                    var refined = new Schema([.. R.Cols.Select(c => present.Contains(c.Name) ? c with { T = c.T with { Req = true } } : c)], R.Window);
                    return new TFilter(name, op, inputs, refined, e);
                }
            case "Map":
                {
                    if (R is null) return null;
                    var assigns = new List<(string, TExpr)>(); var cols = new List<Col>(); var seen = new HashSet<string>();
                    foreach (var a in d.Children(op, "assign"))
                    {
                        var target = d.Text(a, "target") ?? "";
                        var e = Ex(Child(a, "value"), R);
                        if (e is null) continue;
                        if (!seen.Add(target)) { Err("Map", a, $"duplicate target '{target}'"); continue; }
                        assigns.Add((target, e));
                        Carry(cols, target, e, R);
                    }
                    var win = assigns.Any(x => x.Item1 == "windowStart" && x.Item2 is TField { Name: "windowStart" }) ? R.Window : 0;
                    return new TMap(name, op, inputs, new Schema(cols, win), assigns);
                }
            case "Window":
                {
                    if (R is null) return null;
                    var ts = d.Text(op, "timestampField") ?? "";
                    var c = R.Find(ts);
                    if (c is null || c.T.Ty.Tag != Tag.Instant || !c.T.Req || !c.T.Role.IsData) { Err("Window", op, $"window field '{ts}' must be a required data Instant"); return null; }
                    var w = d.Int(op, "width");
                    if (w <= 0) { Err("Window", op, "window width must be positive"); return null; }
                    if (R.Has("windowStart") || R.Has("windowEnd")) { Err("Window", op, "windowStart or windowEnd already exists"); return null; }
                    var inst = new FType(Ty.Instant, true, Role.Data);
                    return new TWindow(name, op, inputs, R.With([new Col("windowStart", inst), new Col("windowEnd", inst)], w), ts, w, d.Text(op, "late") ?? "revise");
                }
            case "Group":
                {
                    if (R is null) return null;
                    var keys = Keys(op, R, true, out var kcols, out var keyCarry);
                    var aggs = Aggs(op, R, false);
                    var cols = kcols.Concat(aggs.Select(a => new Col(a.Target, a.Out))).Concat(keyCarry).ToList();
                    var dup = cols.GroupBy(c => c.Name).FirstOrDefault(g => g.Count() > 1);
                    if (dup is not null) Err("Group", op, $"duplicate output name '{dup.Key}'");
                    var closes = R.Window > 0 && keys.Any(k => k.Name == "windowStart" && k.Value is TField { Name: "windowStart" });
                    return new TGroup(name, op, inputs, new Schema(cols, closes ? R.Window : 0), keys, aggs, closes);
                }
            case "Join": return TypeJoin(op, name, inputs, input("left"), input("right"));
            case "Values":
                {
                    var rows = new List<Dictionary<string, TLit>>();
                    foreach (var row in d.Children(op, "row").OrderBy(x => x, StringComparer.Ordinal))
                    {
                        var vals = new Dictionary<string, TLit>();
                        foreach (var a in d.Children(row, "assign"))
                        {
                            var e = Ex(Child(a, "value"), new Schema([]));
                            if (e is null) continue;
                            if (e is not TLit lit || lit.Value is NullValue) { Err("Values", a, "a values row holds constants"); continue; }
                            vals[d.Text(a, "target") ?? Ids.Name(a)] = lit;
                        }
                        rows.Add(vals);
                    }
                    if (rows.Count == 0) { Err("Values", op, "values needs at least one row"); return null; }
                    var names = rows[0].Keys.Order(StringComparer.Ordinal).ToList();
                    foreach (var r in rows.Skip(1))
                        if (!r.Keys.Order(StringComparer.Ordinal).SequenceEqual(names) || names.Any(n => r[n].Type.Ty != rows[0][n].Type.Ty)) { Err("Values", op, "all rows of values need the same columns of the same types"); return null; }
                    var cols = names.Select(n => new Col(n, new FType(rows[0][n].Type.Ty, true, Role.Data))).ToList();
                    return new TValues(name, op, inputs, new Schema(cols), [.. rows.Select(r => names.Select(n => r[n].Value).ToArray())]);
                }
            case "Fixpoint":
                {
                    var seed = input("seed");
                    if (seed is null) return null;
                    if (seed.Window != 0 || seed.Cols.Any(c => !c.T.Role.IsData || c.Origin is not null)) { Err("Fixpoint", op, "a fixpoint relation is plain data rows (select ids and values first)"); return null; }
                    long max = d.Int(op, "maxIterations");
                    if (max < 1 || max > 64) { Err("Fixpoint", op, "maxIterations must be between 1 and 64"); return null; }
                    var over = d.Target(op, "over");
                    if (over is null) { Err("Fixpoint", op, "a fixpoint has no step result"); return null; }
                    return new TFixpoint(name, op, inputs, seed, max, over);
                }
            case "Distinct": return R is null ? null : new TDistinct(name, op, inputs, R);
            case "Retain":
                {
                    if (R is null) return null;
                    var ts = d.Text(op, "timestampField") ?? "";
                    var c = R.Find(ts);
                    if (c is null || c.T.Ty.Tag != Tag.Instant || !c.T.Req || !c.T.Role.IsData) { Err("Retain", op, $"retention field '{ts}' must be a required data Instant"); return null; }
                    if (d.Int(op, "age") <= 0) { Err("Retain", op, "retention age must be positive"); return null; }
                    if (R.Window != 0) { Err("Retain", op, "retain applies to event rows, not to windowed rows"); return null; }
                    return new TRetain(name, op, inputs, R, ts, d.Int(op, "age"));
                }
            case "Union":
                {
                    var a = input("in0"); var b = input("in1");
                    if (a is null || b is null) return null;
                    var conflict = d.Text(op, "conflict") ?? "preserveEvidence";
                    if (conflict != "preserveEvidence") { Err("Union", op, $"conflict policy {conflict} needs a row key, which a Union of plain rows does not have; only preserveEvidence is supported"); return null; }
                    if (a.Cols.Any(c => c.Origin is not null) || b.Cols.Any(c => c.Origin is not null)) { Err("Union", op, "union rows must be plain data columns; select a property or an id of the element first"); return null; }
                    if (a.Window != b.Window || a.Cols.Count != b.Cols.Count || a.Cols.Zip(b.Cols).Any(x => x.First.Name != x.Second.Name || x.First.T.Ty != x.Second.T.Ty || x.First.T.Role != x.Second.T.Role))
                    { Err("Union", op, $"union inputs must have the same columns: {{{string.Join(", ", a.Cols.Select(c => c.Name + ": " + c.T))}}} against {{{string.Join(", ", b.Cols.Select(c => c.Name + ": " + c.T))}}}"); return null; }
                    var cols = a.Cols.Zip(b.Cols).Select(x => x.First with { T = x.First.T with { Req = x.First.T.Req && x.Second.T.Req, Prov = x.First.T.Prov == x.Second.T.Prov ? x.First.T.Prov : null } }).ToList();
                    return new TUnion(name, op, inputs, new Schema(cols, a.Window), conflict);
                }
            case "Traverse": return R is null ? null : TypeTraverse(op, name, inputs, R, ports);
            case "Series": return TypeSeries(op, name, inputs, R, input("graph"), ports);
            case "Rollup":
                {
                    if (R is null) return null;
                    if (R.Window == 0) { Err("Rollup", op, "rollup needs a windowed input (Series, Window)"); return null; }
                    var keys = Keys(op, R, true, out var kcols, out var carry);
                    var aggs = Aggs(op, R, true);
                    var inst = new FType(Ty.Instant, true, Role.Data);
                    var cols = kcols.Concat(carry).Concat([new Col("windowStart", inst), new Col("windowEnd", inst)]).Concat(aggs.Select(a => new Col(a.Target, a.Out))).ToList();
                    var marked = R.Find(TThreshold.NoDataColumn) is { } nd && nd.T.Ty.Tag == Tag.Boolean && !nd.T.Role.IsData;       // the input comes from a Series with a time to live
                    var any = d.Text(op, "nodata") == "any";
                    if (any && !marked) Err("Rollup", op, "`nodata any` needs a series with a time to live (`ttl`) upstream");
                    if (marked) cols.Add(new Col(TThreshold.NoDataColumn, R.Find(TThreshold.NoDataColumn)!.T));
                    return new TRollup(name, op, inputs, new Schema(cols, R.Window), keys, aggs, R.Window, marked, any, [.. R.Cols.Where(c => c.T.Role.IsData && c.Name is not ("windowStart" or "windowEnd")).Select(c => c.Name)]);
                }
            case "Threshold":
                {
                    if (R is null) return null;
                    if (R.Window == 0) { Err("Threshold", op, "threshold needs a windowed input"); return null; }
                    var v = Ex(Child(op, "value"), R);
                    var keys = Keys(op, R, true, out var kcols, out var carry);
                    if (v is null) return null;
                    if (v.Type.Ty.Tag != Tag.Decimal && v.Type.Ty.Tag != Tag.Integer || !v.Type.Req || v.Type.Role.IsData || v.Type.Role.Temporality == "cumulative")
                    { Err("Threshold", op, $"threshold value must be a required numeric gauge or delta telemetry value, found {v.Type}"); return null; }
                    var above = d.Dec(op, "above"); var below = d.Dec(op, "below");
                    TExpr? ParamExpr(string slot)
                    {
                        if (d.Children(op, slot) is not { Count: > 0 } c) return null;
                        var e = Ex(c[0], R);
                        if (e is not null && (e.Type.Ty.Tag is not (Tag.Decimal or Tag.Integer) || !e.Type.Req || !e.Type.Role.IsData)) { Err("Threshold", op, $"{slot[..^2]} must be a required numeric data value of the row, found {e.Type}"); return null; }
                        return e;
                    }
                    var aboveBy = ParamExpr("aboveBy"); var enterBy = ParamExpr("enterBy"); var belowBy = ParamExpr("belowBy"); var exitBy = ParamExpr("exitBy");
                    if (aboveBy is null && belowBy is null && above <= below) Err("Threshold", op, "above must be greater than below");
                    if ((enterBy is null && d.Int(op, "enterAfter") < 1) || (exitBy is null && d.Int(op, "exitAfter") < 1)) Err("Threshold", op, "enterAfter and exitAfter must be at least 1");
                    var (high, low, init) = (d.Text(op, "high") ?? "", d.Text(op, "low") ?? "", d.Text(op, "initial") ?? "");
                    if (init != high && init != low) Err("Threshold", op, "initial must be one of the two states");
                    var (noData, ignore) = (d.Text(op, "nodata"), d.Bool(op, "nodataIgnore"));
                    var marked = R.Find(TThreshold.NoDataColumn) is { } nd && nd.T.Ty.Tag == Tag.Boolean && !nd.T.Role.IsData;
                    if (marked && noData is null && !ignore) Err("Threshold", op, "the series has a time to live: the threshold must say what no data gives (`nodata then \"...\"`) or ignore it (`nodata ignore`)");
                    if (!marked && (noData is not null || ignore)) Err("Threshold", op, "a `nodata` clause needs a series with a time to live (`ttl`) upstream, and its `noData` column in the row");
                    if (noData is not null && (noData == high || noData == low)) Err("Threshold", op, "the value for no data must differ from the two states");
                    var target = d.Text(op, "target") ?? "status";
                    var cols = kcols.Concat(carry).Append(new Col(target, new FType(Ty.Text, true, Role.Data))).ToList();
                    return new TThreshold(name, op, inputs, new Schema(cols, 0), v, keys, target, above, d.Int(op, "enterAfter"), high, below, d.Int(op, "exitAfter"), low, init, aboveBy, enterBy, belowBy, exitBy, noData, ignore);
                }
            case "EmitEntity": return R is null ? null : TypeEmitEntity(op, name, inputs, R, rule);
            case "EmitRelationship": return R is null ? null : TypeEmitRel(op, name, inputs, R, rule);
            case "Link": return R is null ? null : TypeLink(op, name, inputs, R, rule);
            default:
                Err("Unsupported", op, $"operator {kind} is not supported in this engine slice");
                return null;
        }
    }

    static void FieldNames(TExpr e, HashSet<string> into)
    {
        switch (e)
        {
            case TField f: into.Add(f.Name); break;
            case TCall c: foreach (var a in c.Args) FieldNames(a, into); break;
            case TId i: foreach (var c in i.Comps) FieldNames(c.Value, into); break;
        }
    }

    TOp? TypeTraverse(string op, string name, Dictionary<string, Src> inputs, Schema R, List<TPort> ports)
    {
        if (!inputs.TryGetValue("graph", out var gsrc) || !gsrc.IsPort) { Err("W2", op, "Traverse graph input must come from a graph port"); return null; }
        var gp = ports.FirstOrDefault(p => p.Name == gsrc.Name && p.Kind == "graph");
        if (gp is null) { Err("W2", op, "Traverse graph input must come from a graph port"); return null; }
        if (R.Window != 0 || R.Cols.Any(c => !c.T.Role.IsData)) { Err("Traverse", op, "traverse takes data rows"); return null; }
        var view = ViewOf(gp.ExpectsModule!);
        matchOntology[name] = view;
        var rel = d.Target(op, "ofType");
        if (rel is null || !view.Types.TryGetValue(rel, out var ti) || !ti.IsRel) { Err("Traverse", op, "traverse needs a relationship type of the graph's ontology"); return null; }
        var dir = d.Text(op, "direction") ?? "out"; long min = d.Int(op, "minDepth"), max = d.Int(op, "maxDepth");
        if (min < 0 || max < min || max > 64) { Err("Traverse", op, "depth must satisfy 0 <= min <= max <= 64"); return null; }
        var start = d.Text(op, "start") ?? ""; var target = d.Text(op, "target") ?? "";
        var sc = R.Find(start);
        if (sc is null || sc.T.Ty.Tag != Tag.Ent || sc.T.Ty.TypeId is null || sc.Origin is null) { Err("Traverse", op, $"traverse start '{start}' must be an element column of the input"); return null; }
        if (target.Length == 0 || R.Has(target) || R.Has(target + "_depth")) { Err("Traverse", op, $"traverse output name '{target}' is empty or already a column"); return null; }
        var ends = dir switch { "out" => ti.Targets, "in" => ti.Sources, _ => ti.Targets.Union(ti.Sources).ToHashSet() };
        if (ends.Count != 1) { Err("Traverse", op, $"traverse over {ti.Name} {dir} reaches {(ends.Count == 0 ? "any type" : "several types")}; the reached type must be one entity type"); return null; }
        var reached = ends.First();
        var cols = new List<Col>(R.Cols) { new(target, new FType(Ty.Ent(reached), true, Role.Data), name + ":" + target), new(target + "_depth", new FType(Ty.Int(), true, Role.Data)) };
        var reads = new List<TRead>();
        foreach (var dem in demands.Where(x => x.StartsWith(name + "|", StringComparison.Ordinal)).OrderBy(x => x, StringComparer.Ordinal))
        {
            var parts = dem.Split('|'); var pn = parts[2];
            if (parts[1] != target) continue;
            var hit = view.PropsOf(reached).Where(kv => view.Props.TryGetValue(kv.Key, out var pi) && pi.Name == pn).Select(kv => (Id: kv.Key, Req: kv.Value)).ToList();
            if (hit.Count != 1 || view.Props[hit[0].Id].Role == "telemetry") continue;
            var pi = view.Props[hit[0].Id];
            reads.Add(new TRead(target, hit[0].Id, target + "." + pn));
            cols.Add(new Col(target + "." + pn, new FType(KindOfProp(pi, view), hit[0].Req, Role.Data)));
        }
        return new TTraverse(name, op, inputs, new Schema(cols), gsrc.Name, rel, dir, min, max, start, target, reads);
    }

    TOp? TypeJoin(string op, string name, Dictionary<string, Src> inputs, Schema? l, Schema? r)
    {
        if (l is null || r is null) return null;
        if (l.Window != 0 || r.Window != 0 || l.Cols.Concat(r.Cols).Any(c => !c.T.Role.IsData)) { Err("Join", op, "join takes data rows: no windowed or telemetry input (roll it up and cross it with a threshold first)"); return null; }
        var clash = l.Cols.Select(c => c.Name).Intersect(r.Cols.Select(c => c.Name)).FirstOrDefault();
        if (clash is not null) { Err("Join", op, $"both inputs have a column named '{clash}'; rename one with select"); return null; }
        var both = new Schema([.. l.Cols, .. r.Cols]);
        var on = Ex(Child(op, "on"), both);
        if (on is null) return null;
        if (on.Type.Ty.Tag != Tag.Boolean || !on.Type.Role.IsData) { Err("Join", op, $"join condition must be a Boolean, found {on.Type}"); return null; }
        var left = new HashSet<string>(l.Cols.Select(c => c.Name)); var right = new HashSet<string>(r.Cols.Select(c => c.Name));
        var pairs = new List<(TExpr, TExpr)>(); var rest = new List<TExpr>();
        void Conj(TExpr x)
        {
            if (x is TCall { Fn: "And" } a) { foreach (var s in a.Args) Conj(s); return; }
            if (x is TCall { Fn: "Equal", Args: [var p, var q] })
            {
                HashSet<string> fp = [], fq = [];
                FieldNames(p, fp); FieldNames(q, fq);
                if (fp.Count > 0 && fq.Count > 0 && p.Type.Ty == q.Type.Ty)
                {
                    if (fp.IsSubsetOf(left) && fq.IsSubsetOf(right)) { pairs.Add((p, q)); return; }
                    if (fq.IsSubsetOf(left) && fp.IsSubsetOf(right)) { pairs.Add((q, p)); return; }
                }
            }
            rest.Add(x);
        }
        Conj(on);
        if (pairs.Count == 0) { Err("Join", op, "join needs at least one equality between a left and a right expression (a cross product is not built)"); return null; }
        TExpr? residual = rest.Count == 0 ? null : rest.Count == 1 ? rest[0] : new TCall("And", rest, on.Type);
        return new TJoin(name, op, inputs, new Schema([.. l.Cols, .. r.Cols]), pairs, residual);
    }

    string Child(string parent, string slot) => d.Children(parent, slot).FirstOrDefault() ?? "";

    /// <summary>Output column for an assignment: a FieldRef keeps role, provenance, origin, and carries the read columns of an entity.</summary>
    static void Carry(List<Col> cols, string target, TExpr e, Schema R)
    {
        if (e is TField f && R.Find(f.Name) is { } src)
        {
            cols.Add(new Col(target, src.T, src.Origin));
            if (src.Origin is not null)
                foreach (var c in R.Cols.Where(c => c.Name.StartsWith(f.Name + ".", StringComparison.Ordinal))) cols.Add(c with { Name = target + c.Name[f.Name.Length..] });
        }
        else cols.Add(new Col(target, e.Type));
    }

    List<(string Name, TExpr Value)> Keys(string op, Schema R, bool requireData, out List<Col> cols, out List<Col> carry)
    {
        var keys = new List<(string, TExpr)>(); cols = []; carry = [];
        foreach (var k in d.Children(op, "key"))
        {
            var target = d.Text(k, "target") ?? Ids.Name(k);
            var e = Ex(Child(k, "value"), R);
            if (e is null) continue;
            if (requireData && (!e.Type.Req || !e.Type.Role.IsData)) { Err("RoleInterference", k, $"key '{target}' must be (κ, req, data), found {e.Type}"); continue; }
            keys.Add((target, e));
            var tmp = new List<Col>();
            Carry(tmp, target, e, R);
            cols.Add(tmp[0] with { T = tmp[0].T with { Prov = null } }); // read columns of an entity key are not carried past an aggregation
        }
        return keys;
    }

    List<TAgg> Aggs(string op, Schema R, bool telemetry)
    {
        var res = new List<TAgg>();
        foreach (var a in d.Children(op, "agg"))
        {
            var target = d.Text(a, "target") ?? Ids.Name(a); var fn = d.Text(a, "fn") ?? "";
            var ve = d.Children(a, "value").FirstOrDefault(); var we = d.Children(a, fn == "last" ? "order" : "weight").FirstOrDefault();
            var v = ve is null ? null : Ex(ve, R); var w = we is null ? null : Ex(we, R);
            if (ve is not null && v is null) continue;
            FType? ft = null;
            if (fn == "count") { if (v is not null) Err("Aggregate", a, "count takes no argument"); ft = new FType(Ty.Int(), true, Role.Data); }
            else if (v is null) Err("Aggregate", a, $"{fn} needs an argument");
            else if (!telemetry)
            {
                if (!v.Type.Role.IsData) Err("Aggregate", a, "group aggregates take data; use rollup for telemetry");
                else switch (fn)
                    {
                        case "countDistinct": ft = new FType(Ty.Int(), true, Role.Data); break;
                        case "sum": if (!v.Type.Ty.IsNumeric) Err("Aggregate", a, "sum needs a numeric argument"); else ft = new FType(v.Type.Ty, v.Type.Req, Role.Data); break;
                        case "mean": if (!v.Type.Ty.IsNumeric) Err("Aggregate", a, "mean needs a numeric argument"); else ft = new FType(Ty.Dec(), false, Role.Data); break;
                        case "weightedMean":
                            if (!v.Type.Ty.IsNumeric) Err("Aggregate", a, "weightedMean needs a numeric argument");
                            else if (w is null || !w.Type.Role.IsData || !w.Type.Ty.IsNumeric) Err("Aggregate", a, "weightedMean needs a numeric weight: weightedMean(value, weight)");
                            else ft = new FType(Ty.Dec(), false, Role.Data);
                            break;
                        case "any" or "all": if (v.Type.Ty.Tag != Tag.Boolean) Err("Aggregate", a, $"{fn} needs a Boolean argument"); else ft = new FType(Ty.Boolean, false, Role.Data); break;
                        case "last":
                            if (w is null) Err("Aggregate", a, "last needs an ordering: last(value, order)");
                            else if (!w.Type.Role.IsData || w.Type.Ty.Tag is not (Tag.Integer or Tag.Decimal or Tag.Instant)) Err("Aggregate", a, $"the ordering of last must be a data Integer, Decimal or Instant, found {w.Type}");
                            else if (v.Type.Ty.Tag is Tag.Ent or Tag.Rel or Tag.Null) Err("Aggregate", a, "last takes a value column, not an element");
                            else ft = new FType(v.Type.Ty, v.Type.Req, Role.Data);
                            break;
                        case "min" or "max": if (!v.Type.Ty.IsNumeric) Err("Aggregate", a, $"{fn} needs a numeric argument"); else ft = new FType(v.Type.Ty, v.Type.Req, Role.Data); break;
                        default: Err("Unsupported", a, $"aggregate {fn} is not supported in this engine slice (count, countDistinct, sum, min, max, mean, weightedMean, any, all, last)"); break;
                    }
            }
            else
            {
                var role = v.Type.Role;
                if (role.IsData || !v.Type.Ty.IsNumeric) Err("Aggregate", a, $"rollup aggregate {fn} needs a numeric telemetry value, found {v.Type}");
                else if (fn is not ("sum" or "min" or "max" or "mean")) Err("Unsupported", a, $"rollup aggregate {fn} is not supported (sum, min, max, mean)");
                else if (role.Temporality == "cumulative") Err("Aggregate", a, $"{fn} over a cumulative value is an error, apply Delta first");
                else if (fn == "mean" && role.Temporality != "gauge") Err("Aggregate", a, "mean over a delta value is not defined; use sum, or rate it first");
                else ft = new FType(fn == "mean" ? Ty.Dec() : v.Type.Ty, v.Type.Req, role, fn);
            }
            if (ft is not null) res.Add(new TAgg(target, fn, v, w, ft));
        }
        return res;
    }

    TOp? TypeMatch(string op, string name, Dictionary<string, Src> inputs, List<TPort> ports)
    {
        if (!inputs.TryGetValue("graph", out var gsrc) || !gsrc.IsPort) { Err("W2", op, "Match graph input must come from a graph port"); return null; }
        var gp = ports.FirstOrDefault(p => p.Name == gsrc.Name && p.Kind == "graph");
        if (gp is null) { Err("W2", op, "Match graph input must come from a graph port"); return null; }
        var view = ViewOf(gp.ExpectsModule!);
        matchOntology[name] = view;
        var nodes = new List<TNode>(); var cols = new List<Col>(); var edges = new List<TEdge>();
        var nodeTypes = new Dictionary<string, string>();
        foreach (var n in d.Children(op, "node"))
        {
            var v = d.Text(n, "var") ?? Ids.Name(n); var t = d.Target(n, "ofType");
            if (t is null || !view.Types.TryGetValue(t, out var ti) || ti.IsRel) { Err("Match", n, $"pattern variable '{v}' has a type outside the graph's ontology"); continue; }
            nodes.Add(new TNode(v, t)); nodeTypes[v] = t;
            cols.Add(new Col(v, new FType(Ty.Ent(t), true, Role.Data), name + ":" + v));
        }
        foreach (var e in d.Children(op, "edge"))
        {
            var v = d.Text(e, "var"); var t = d.Target(e, "ofType");
            var from = Ids.Name(d.Target(e, "from") ?? ""); var to = Ids.Name(d.Target(e, "to") ?? "");
            if (t is null || !view.Types.TryGetValue(t, out var ti) || !ti.IsRel) { Err("Match", e, "edge pattern has a relationship type outside the graph's ontology"); continue; }
            if (!nodeTypes.TryGetValue(from, out var ft) || !nodeTypes.TryGetValue(to, out var tt)) { Err("Match", e, "edge endpoints must be pattern nodes"); continue; }
            bool Compat(string x, HashSet<string> allowed) => allowed.Count == 0 || allowed.Any(s => view.IsSubtype(x, s) || view.IsSubtype(s, x));
            if (!Compat(ft, ti.Sources) || !Compat(tt, ti.Targets)) Err("Match", e, $"relationship {ti.Name} does not connect {Ids.Name(ft)} to {Ids.Name(tt)}");
            edges.Add(new TEdge(v, t, from, to));
            if (v is not null) cols.Add(new Col(v, new FType(Ty.Rel(t), true, Role.Data), name + ":" + v));
        }
        if (nodes.Count == 0) { Err("Match", op, "pattern has no nodes"); return null; }
        // connectivity
        var comp = nodes.Select(n => n.Var).ToDictionary(x => x, x => x);
        string Find(string x) => comp[x] == x ? x : comp[x] = Find(comp[x]);
        foreach (var e in edges) if (comp.ContainsKey(e.From) && comp.ContainsKey(e.To)) comp[Find(e.From)] = Find(e.To);
        if (nodes.Select(n => Find(n.Var)).Distinct().Count() > 1) Err("Match", op, "pattern is not connected");
        // read set: every v.p demanded downstream
        var reads = new List<TRead>();
        foreach (var dem in demands.Where(x => x.StartsWith(name + "|", StringComparison.Ordinal)).OrderBy(x => x, StringComparer.Ordinal))
        {
            var parts = dem.Split('|'); var v = parts[1]; var pn = parts[2];
            var tid = nodeTypes.GetValueOrDefault(v) ?? edges.FirstOrDefault(e => e.Var == v)?.TypeId;
            if (tid is null) continue;
            var hit = view.PropsOf(tid).Where(kv => view.Props.TryGetValue(kv.Key, out var pi) && pi.Name == pn).Select(kv => (Id: kv.Key, Req: kv.Value)).ToList();
            if (hit.Count != 1 || view.Props[hit[0].Id].Role == "telemetry") continue;
            var pi = view.Props[hit[0].Id];
            reads.Add(new TRead(v, hit[0].Id, v + "." + pn));
            cols.Add(new Col(v + "." + pn, new FType(KindOfProp(pi, view), nodes.Any(n => n.Var == v) ? hit[0].Req : hit[0].Req, Role.Data)));
        }
        return new TMatch(name, op, inputs, new Schema(cols), nodes, edges, reads, gsrc.Name);
    }

    Ty KindOfProp(OntologyView.PropInfo p, OntologyView v)
    {
        var refers = d.Target(p.Id, "refersTo");
        return p.ValueKind switch
        {
            "Text" => Ty.Text, "Integer" => Ty.Int(p.Unit ?? "1"), "Decimal" => Ty.Dec(p.Unit ?? "1"), "Boolean" => Ty.Boolean, "Instant" => Ty.Instant,
            "Entity" => Ty.Ent(refers), "List" => new Ty(Tag.List, "1", null, ItemTag(p.ItemKind)), _ => Ty.Text,
        };
    }

    TOp? TypeSeries(string op, string name, Dictionary<string, Src> inputs, Schema? R, Schema? G, List<TPort> ports)
    {
        _ = G;
        if (R is null) return null;
        if (!inputs.TryGetValue("graph", out var gsrc) || !gsrc.IsPort) { Err("W2", op, "Series graph input must come from a graph port"); return null; }
        var gp = ports.First(p => p.Name == gsrc.Name);
        var view = ViewOf(gp.ExpectsModule!);
        var reads = new List<TSeriesRead>(); long period = 0; string? target = null;
        foreach (var rd in d.Children(op, "read"))
        {
            var path = d.Text(rd, "path") ?? "";
            var dot = path.IndexOf('.');
            if (dot < 0) { Err("Series", rd, $"series read '{path}' must be v.p"); continue; }
            var (v, pn) = (path[..dot], path[(dot + 1)..]);
            var col = R.Find(v);
            if (col is null || col.T.Ty.Tag is not (Tag.Ent or Tag.Rel) || col.T.Ty.TypeId is null || col.Origin is null) { Err("Series", rd, $"series read '{path}': '{v}' is not an element variable"); continue; }
            if (target is not null && target != v) { Err("Series", rd, "all series reads must name the same element variable"); continue; }
            target = v;
            var hit = view.PropsOf(col.T.Ty.TypeId).Keys.Where(k => view.Props.TryGetValue(k, out var pi) && pi.Name == pn).ToList();
            if (hit.Count != 1 || view.Props[hit[0]].Role != "telemetry") { Err("Series", rd, $"'{pn}' is not a telemetry property of {Ids.Name(col.T.Ty.TypeId)}"); continue; }
            var p = view.Props[hit[0]];
            if (period != 0 && p.Period != period) { Err("Series", rd, "series reads have different periods"); continue; }
            period = p.Period;
            if (R.Has(pn) || pn is "windowStart" or "windowEnd") { Err("Series", rd, $"name '{pn}' already exists in the row"); continue; }
            var fill = d.Prop(rd, "fill") is DecimalValue fv ? fv.Q : (decimal?)null;
            // a fill is a value for a window in which nothing happened: that is a value only where the windows of a property add up (Def. 7.15)
            if (fill is not null && p.Temporality != "delta") { Err("Series", rd, $"'{pn}' is a {p.Temporality} property: a missing reading of it is not a value, so it takes no fill"); continue; }
            if (fill is { } whole && p.ValueKind == "Integer" && whole != decimal.Truncate(whole)) { Err("Series", rd, $"the fill of the Integer property '{pn}' must be an integer"); continue; }
            reads.Add(new TSeriesRead(v, hit[0], pn, p.ValueKind, fill));
        }
        var within = d.Int(op, "within");
        if (period > 0 && within < period) Err("Series", op, "within must be at least the series period");
        var ttl = d.Prop(op, "ttl") is IntegerValue tv ? (long)tv.N : (long?)null;
        if (ttl is null && reads.Any(r => r.Fill is not null)) Err("Series", op, "a fill needs a time to live (`ttl`) on the same series: silence must have a declared outcome");
        // at most `within` less one window: a record that ends within the time to live is then a window of the look-back
        if (ttl is { } life && period > 0 && (life < period || life > within - period)) Err("Series", op, "the time to live must be at least one window and at most `within` less one window");
        if (ttl is not null && R.Has(TThreshold.NoDataColumn)) Err("Series", op, $"name '{TThreshold.NoDataColumn}' already exists in the row");
        if (reads.Count == 0 || target is null) return null;
        var inst = new FType(Ty.Instant, true, Role.Data);
        var cols = new List<Col> { new("windowStart", inst), new("windowEnd", inst) };
        foreach (var r in reads)
        {
            var p = view.Props[r.PropId];
            cols.Add(new Col(r.OutName, new FType(KindOfProp(p, view), true, Role.Tele(p.Temporality!, p.Period))));
        }
        if (ttl is not null) cols.Add(new Col(TThreshold.NoDataColumn, new FType(Ty.Boolean, true, Role.Tele("gauge", period))));
        return new TSeries(name, op, inputs, R.With(cols, period), gsrc.Name, reads, within, period, target, ttl);
    }

    // ---- emits -----------------------------------------------------------------------------------------

    List<TSet> Sets(string op, string typeId, Schema R, bool update, bool isRule)
    {
        var res = new List<TSet>();
        var props = outView.PropsOf(typeId);
        foreach (var s in d.Children(op, "set"))
        {
            var pid = d.Target(s, "property");
            if (pid is null) { Err("Set", s, "set has no property"); continue; }
            if (!props.TryGetValue(pid, out var required) || !outView.Props.TryGetValue(pid, out var pi)) { Err("P1", s, $"property {Ids.Name(pid)} is not declared for {Ids.Name(typeId)} in the output ontology"); continue; }
            var e = Ex(Child(s, "value"), R);
            if (e is null) continue;
            var target = KindOfProp(pi, outView);
            if (!Types.Sub(e.Type.Ty, target, outView)) { Err("Set", s, $"property {pi.Name} expects {target}, found {e.Type.Ty}"); continue; }
            if (required && !e.Type.Req) { Err("Set", s, $"required property {pi.Name} cannot take an optional value"); continue; }
            var tele = pi.Role == "telemetry";
            if (tele)
            {
                if (R.Window != pi.Period) { Err("Set", s, $"telemetry property {pi.Name} has period {Lexer.DurationText(pi.Period)}, the row window is {(R.Window == 0 ? "undefined" : Lexer.DurationText(R.Window))}"); continue; }
                if (!e.Type.Role.IsData && (e.Type.Role.Temporality != pi.Temporality || e.Type.Role.Period != pi.Period)) { Err("Set", s, $"telemetry property {pi.Name} is {pi.Temporality}, the value is {e.Type.Role}"); continue; }
                if (e.Type.Prov is { } f && pi.Combine is { } c && f != c) { Err("Set", s, $"property {pi.Name} combines with {c}, the value was rolled up with {f}"); continue; }
                if (isRule) Err("Set", s, $"a bijective, lens or sync rule cannot set telemetry property {pi.Name}");
            }
            else if (!e.Type.Role.IsData) { Err("RoleInterference", s, $"graph data property {pi.Name} cannot take a telemetry value (only Threshold crosses to data)"); continue; }
            res.Add(new TSet(pid, pi.Name, e, tele, pi.Period, pi.Combine));
        }
        if (!update)
            foreach (var (p, req) in props)
                if (req && outView.Props.TryGetValue(p, out var pi) && pi.Role != "telemetry" && !res.Any(x => x.PropId == p))
                    Err("P5", op, $"create emit of {Ids.Name(typeId)} must set required property {pi.Name}");
        return res;
    }

    TOp? TypeEmitEntity(string op, string name, Dictionary<string, Src> inputs, Schema R, string? rule)
    {
        var t = d.Target(op, "ofType");
        if (t is null || !outView.Types.TryGetValue(t, out var ti) || ti.IsRel) { Err("P1", op, "emitted entity type is not in the output ontology"); return null; }
        if (ti.Abstract) { Err("P1", op, $"cannot emit abstract type {ti.Name}"); return null; }
        var id = IdentityEx(Child(op, "id"), R);
        if (id is null) return null;
        if (id.Type.Ty.Tag != Tag.Ent || !id.Type.Role.IsData) { Err("EmitEntity", op, $"entity id must be an entity reference (data), found {id.Type}"); return null; }
        var update = d.Text(op, "mode") == "update";
        var sets = Sets(op, t, R, update, rule is not null && RuleClass(rule) != "readonly");
        return new TEmitEntity(name, op, inputs, new Schema([]), t, update, id, sets, rule);
    }

    TOp? TypeEmitRel(string op, string name, Dictionary<string, Src> inputs, Schema R, string? rule)
    {
        var t = d.Target(op, "ofType");
        if (t is null || !outView.Types.TryGetValue(t, out var ti) || !ti.IsRel) { Err("P1", op, "emitted relationship type is not in the output ontology"); return null; }
        if (ti.Abstract) { Err("P1", op, $"cannot emit abstract type {ti.Name}"); return null; }
        var src = IdentityEx(Child(op, "source"), R); var tgt = IdentityEx(Child(op, "target"), R);
        if (src is null || tgt is null) return null;
        foreach (var (e, side) in new[] { (src, "source"), (tgt, "target") })
            if (e.Type.Ty.Tag != Tag.Ent || !e.Type.Role.IsData) { Err("EmitRelationship", op, $"relationship {side} must be an entity reference (data), found {e.Type}"); return null; }
        var update = d.Text(op, "mode") == "update";
        var sets = Sets(op, t, R, update, rule is not null && RuleClass(rule) != "readonly");
        return new TEmitRel(name, op, inputs, new Schema([]), t, update, d.Text(op, "key") ?? "", src, tgt, sets, d.Text(op, "retype") ?? "reject", rule);
    }

    /// <summary>Premise B1 as a checker rule (O6.7): one row per element and window writes a telemetry property. A group or roll-up whose key has a column the identity of the emitted element does not read gives several rows for one element and window: refused.</summary>
    void TelemetryKeyCheck(List<TOp> ops)
    {
        static IEnumerable<string> Fields(TExpr e) => e switch
        {
            TField f => [f.Name],
            TCall c => c.Args.SelectMany(Fields),
            TId i => i.Comps.SelectMany(x => Fields(x.Value)),
            _ => [],
        };
        var byName = ops.GroupBy(o => o.Name).ToDictionary(g => g.Key, g => g.First());
        foreach (var (emit, typeId, ids, sets) in ops.OfType<TEmitEntity>().Select(e => ((TOp)e, e.TypeId, new[] { e.EntityId }, e.Sets))
                     .Concat(ops.OfType<TEmitRel>().Select(e => ((TOp)e, e.TypeId, new[] { e.Source, e.Target }, e.Sets))))
        {
            if (!sets.Any(x => x.Telemetry) || !emit.Inputs.TryGetValue("in", out var src) || src.IsPort || !byName.TryGetValue(src.Name, out var up)) continue;
            var keys = up switch { TGroup g => g.Keys.Select(k => k.Name), TRollup r => r.Keys.Select(k => k.Name), _ => null };
            if (keys is null) continue;
            var read = ids.SelectMany(Fields).ToHashSet(StringComparer.Ordinal);
            var finer = keys.Where(k => k != "windowStart" && !read.Contains(k)).ToList();
            if (finer.Count > 0)
                Err("B1", emit.Id, $"the telemetry of {Ids.Name(typeId)} comes from {up.Name}, grouped by {string.Join(", ", finer.Select(k => $"'{k}'"))} besides what its identity reads: several rows would write one window of one element (premise B1)");
        }
    }

    string RuleClass(string ruleName) => d.Children(projectionId, "rule").Where(r => d.Text(r, "name") == ruleName).Select(r => d.Text(r, "class")).FirstOrDefault() ?? "readonly";

    TOp? TypeLink(string op, string name, Dictionary<string, Src> inputs, Schema R, string? rule)
    {
        var pol = d.Target(op, "policy");
        if (pol is null) { Err("Link", op, "link has no equivalence policy"); return null; }
        var between = d.Out(pol, "between").OrderBy(r => r.Props.Get(Ids.KProp("ordinal")) is IntegerValue i ? i.N : 0).Select(r => r.Target).ToList();
        var l = IdentityEx(Child(op, "left"), R) as TId; var r = IdentityEx(Child(op, "right"), R) as TId;
        if (l is null || r is null) { Err("Link", op, "link sides must be identity expressions"); return null; }
        if (between.Count != 2 || l.DomainId != between[0] || r.DomainId != between[1]) { Err("Link", op, "link sides must use the two domains of the policy in order"); return null; }
        var conf = Ex(Child(op, "confidence"), R);
        if (conf is not TLit { Value: DecimalValue cv } || cv.Q is < 0 or > 1) { Err("Link", op, "confidence must be a decimal literal in [0, 1]"); return null; }
        return new TLink(name, op, inputs, new Schema([]), pol, d.Text(pol, "name") ?? Ids.Name(pol), l, r, cv.Q, d.Text(pol, "mode") ?? "assert", d.Dec(pol, "threshold"), rule);
    }

    // ---- projection judgments --------------------------------------------------------------------------

    void EmitterChecks(List<TOp> ops, List<TRule> rules, string pid)
    {
        TelemetryKeyCheck(ops);
        var creates = ops.OfType<TEmitEntity>().Where(e => !e.Update).ToList();
        var relCreates = ops.OfType<TEmitRel>().Where(e => !e.Update).ToList();
        foreach (var e in ops.OfType<TEmitEntity>().Where(e => e.Update))
            if (!creates.Any(c => c.TypeId == e.TypeId)) Err("EmitEntity", e.Id, $"update emit of {Ids.Name(e.TypeId)} needs a create emit of the same type in this projection");
        foreach (var e in ops.OfType<TEmitRel>())
        {
            if (e.Update && !relCreates.Any(c => c.TypeId == e.TypeId && c.Key == e.Key)) Err("EmitRelationship", e.Id, $"update emit needs a create emit of {Ids.Name(e.TypeId)} with key '{e.Key}'");
            if (!e.Update && relCreates.Count(c => c.Key == e.Key) > 1) Err("EmitRelationship", e.Id, $"relationship key '{e.Key}' is used by two create emits");
            var ti = outView.Types[e.TypeId];
            foreach (var (x, allowed, side) in new[] { (e.Source, ti.Sources, "source"), (e.Target, ti.Targets, "target") })
            {
                if (x.Type.Ty.TypeId is not { } st) continue;
                var candidates = creates.Where(c => x.Type.Ty.TypeId is not null && Types.Comparable(c.EntityId.Type.Ty, x.Type.Ty, outView)).Select(c => c.TypeId).Distinct().ToList();
                if (candidates.Count == 0) { Err("EndpointDeferred", e.Id, $"{side} of {Ids.Name(e.TypeId)} is emitted by no create emit of this projection", "warning"); continue; }
                if (allowed.Count > 0 && !candidates.Any(c => allowed.Any(a => outView.IsSubtype(c, a)))) Err("EmitRelationship", e.Id, $"{side} {Ids.Name(st)} resolves to {string.Join(", ", candidates.Select(Ids.Name))}, which relationship type {ti.Name} does not allow");
            }
        }
        // P2 ownership: a (type, property) or relationship type governed by one rule of class bijective, lens or sync
        var owner = new Dictionary<string, string>();
        foreach (var e in ops.Where(o => o is TEmitEntity or TEmitRel))
        {
            var rule = e is TEmitEntity ee ? ee.Rule : ((TEmitRel)e).Rule;
            var cls = rules.FirstOrDefault(r => r.Name == rule)?.Class;
            var keys = e is TEmitEntity en ? en.Sets.Select(s => en.TypeId + "." + s.PropId) : [((TEmitRel)e).TypeId];
            foreach (var k in keys)
            {
                if (owner.TryGetValue(k, out var prev) && prev != (rule ?? "") && (cls is "bijective" or "lens" or "sync" || rules.FirstOrDefault(r => r.Name == prev)?.Class is "bijective" or "lens" or "sync"))
                    Err("P2", e.Id, $"{Ids.Name(k)} is set by rules {prev} and {rule}; a bijective, lens or sync rule must own it exclusively");
                owner[k] = rule ?? "";
            }
        }
        foreach (var r in rules)
        {
            if (r.Class == "sync") Err("Unsupported", pid, $"rule {r.Name}: class sync is later work");
            if (r.Class == "lens") LensPolicy(pid, r, ops);
        }
    }

    /// <summary>P5 for lens rules: the create key and defaults, with the rule's own sets, cover every required property of the master entity type; delete sets and accepted updates name real properties.</summary>
    void LensPolicy(string pid, TRule r, List<TOp> ops)
    {
        var p = r.Policy;
        var emits = ops.OfType<TEmitEntity>().Where(e => e.Rule == r.Name && !e.Update).ToList();
        var accept = p?.Accept ?? [];
        if (accept.Contains("create") && emits.Count > 0 && p?.CreateDomainId is null) Err("P5", pid, $"rule {r.Name} accepts create but has no 'on create key <domain>'");
        if (p?.DeleteMode == "set" && p.DeleteSets.Count == 0) Err("P5", pid, $"rule {r.Name}: 'on delete set' sets nothing");
        if (accept.Contains("delete") && p?.DeleteMode is null && emits.Count > 0) Err("P5", pid, $"rule {r.Name} accepts delete but has no 'on delete' action");
        if (p?.SourceTypeId is { } src)
        {
            var view = ViewOf(Ids.ModuleOf(src));
            var covered = new HashSet<string>(p.Defaults.Select(x => x.PropId));
            foreach (var e in emits)
                foreach (var s in e.Sets) if (s.Value is TField f && f.Name.Contains('.'))
                {
                    var pn = f.Name[(f.Name.IndexOf('.') + 1)..];
                    foreach (var kv in view.PropsOf(src)) if (view.Props.TryGetValue(kv.Key, out var pi) && pi.Name == pn) covered.Add(kv.Key);
                }
            foreach (var (propId, req) in view.PropsOf(src))
                if (req && view.Props.TryGetValue(propId, out var pi) && pi.Role != "telemetry" && !covered.Contains(propId) && accept.Contains("create"))
                    Err("P5", pid, $"rule {r.Name}: create must provide required property {pi.Name} of {Ids.Name(src)} (from the view or 'on create default')");
            foreach (var x in p.DeleteSets) if (!view.PropsOf(src).ContainsKey(x.PropId)) Err("P5", pid, $"rule {r.Name}: on delete sets {x.Name}, which {Ids.Name(src)} does not have");
        }
        var viewProps = emits.SelectMany(e => e.Sets).Where(s => s.Value is TField).Select(s => s.PropName).ToHashSet();
        foreach (var a in accept.Where(a => a.StartsWith("update(", StringComparison.Ordinal)))
            foreach (var n in a[7..^1].Split(','))
                if (!viewProps.Contains(n)) Err("P5", pid, $"rule {r.Name}: accept update({n}) names a property that no FieldRef of the rule sets");
    }

    /// <summary>Direction judgment (section 4.3): bijective needs δ = iso, one input stream, every input field recovered.</summary>
    void Direction(string pid, List<TOp> ops, List<TRule> rules, List<TPort> ports)
    {
        var byName = ops.ToDictionary(o => o.Name);
        foreach (var r in rules.Where(r => r.Class == "lens"))
        {
            var reached = new HashSet<string>();
            foreach (var sinkName in r.Sinks)
            {
                if (!byName.TryGetValue(sinkName, out var sink)) continue;
                if (NotLens(sink) is { } sw) Err("DirectionOverclaim", pid, $"rule {r.Name} claims lens but operator {sink.Name} is {sw}");
                void Trace(TOp op)
                {
                    foreach (var (_, src) in op.Inputs)
                    {
                        if (src.IsPort) { reached.Add(src.Name); continue; }
                        var up = byName[src.Name];
                        if (NotLens(up) is { } why) Err("DirectionOverclaim", pid, $"rule {r.Name} claims lens but operator {up.Name} ({up.Kind}) is {why}");
                        Trace(up);
                    }
                }
                Trace(sink);
            }
            if (reached.Count > 1) Err("DirectionOverclaim", pid, $"rule {r.Name} claims lens but reaches {reached.Count} input ports");
        }
        foreach (var r in rules.Where(r => r.Class == "bijective"))
        {
            var reached = new HashSet<string>(); var recovered = new HashSet<string>();
            foreach (var sinkName in r.Sinks)
            {
                if (!byName.TryGetValue(sinkName, out var sink)) continue;
                void Trace(TOp op)
                {
                    foreach (var (_, src) in op.Inputs)
                    {
                        if (src.IsPort) { reached.Add(src.Name); continue; }
                        var up = byName[src.Name];
                        var why = NotIso(up);
                        if (why is not null) { Err("DirectionOverclaim", pid, $"rule {r.Name} claims bijective but operator {up.Name} ({up.Kind}) is {why}"); }
                        Trace(up);
                    }
                }
                var sinkWhy = NotIso(sink);
                if (sinkWhy is not null) Err("DirectionOverclaim", pid, $"rule {r.Name} claims bijective but operator {sink.Name} is {sinkWhy}");
                Trace(sink);
                CollectFields(sink, recovered);
            }
            if (reached.Count > 1) Err("DirectionOverclaim", pid, $"rule {r.Name} claims bijective but reads {reached.Count} input streams");
            foreach (var pn in reached)
                foreach (var f in ports.First(p => p.Name == pn).Rows?.Cols ?? [])
                    if (!recovered.Contains(f.Name)) Err("DirectionOverclaim", pid, $"rule {r.Name} claims bijective but input field '{f.Name}' of {pn} is not recovered by any emit");
        }
    }

    static string? NotLens(TOp o) => o switch
    {
        TMatch or TFilter => null,
        TMap m => m.Assigns.All(a => a.Value is TField) ? null : "computing a value",
        TEmitEntity e => e.Update ? "an update emit" : IsFieldId(e.EntityId) && e.Sets.All(s => s.Value is TField or TLit) ? null : "computing its identity or values",
        TEmitRel e => e.Update ? "an update emit" : IsFieldId(e.Source) && IsFieldId(e.Target) && e.Sets.All(s => s.Value is TField or TLit) ? null : "computing its endpoints",
        _ => "many-to-one or lossy",
    };

    static string? NotIso(TOp o) => o switch
    {
        TEmitEntity e => e.Update ? "an update emit" : e.EntityId is TField || IsFieldId(e.EntityId) ? (e.Sets.All(s => s.Value is TField) ? null : "computing a value") : "computing its identity",
        TEmitRel e => e.Update ? "an update emit" : IsFieldId(e.Source) && IsFieldId(e.Target) && e.Sets.All(s => s.Value is TField) ? null : "computing its endpoints",
        TMap m => m.Assigns.All(a => a.Value is TField) ? null : "computing a value",
        TFilter => "a filter (lens, needs a complement)",
        _ => "many-to-one or lossy",
    };
    static bool IsFieldId(TExpr e) => e is TField || (e is TId i && i.Comps.All(c => c.Value is TField or TLit));
    static void CollectFields(TOp sink, HashSet<string> into)
    {
        void Ex(TExpr e) { switch (e) { case TField f: into.Add(f.Name); break; case TId i: foreach (var c in i.Comps) Ex(c.Value); break; case TCall c: foreach (var a in c.Args) Ex(a); break; } }
        if (sink is TEmitEntity en) { Ex(en.EntityId); foreach (var s in en.Sets) Ex(s.Value); }
        if (sink is TEmitRel er) { Ex(er.Source); Ex(er.Target); foreach (var s in er.Sets) Ex(s.Value); }
    }

    // ---- expressions -----------------------------------------------------------------------------------

    TExpr? Ex(string node, Schema R)
    {
        if (node.Length == 0) { Err("Expr", projectionId, "missing expression"); return null; }
        switch (d.TypeName(node))
        {
            case "FieldRef":
                {
                    var path = d.Text(node, "path") ?? "";
                    var c = R.Find(path);
                    if (c is not null) return new TField(path, c.T);
                    if (paramKinds.TryGetValue(path, out var pk))
                    {
                        var v = paramValues is not null && paramValues.TryGetValue(path, out var given) ? given : paramDefaults.TryGetValue(path, out var dflt) ? dflt : pk switch { "Integer" => Value.Integer(0), "Decimal" => Value.Decimal(0m), "Boolean" => Value.Boolean(false), _ => Value.Text("") };
                        return new TLit(v, new FType(pk switch { "Integer" => Ty.Int(), "Decimal" => Ty.Dec(), "Boolean" => Ty.Boolean, _ => Ty.Text }, true, Role.Data));
                    }
                    var dot = path.IndexOf('.');
                    if (dot > 0 && R.Find(path[..dot]) is { Origin: { } origin } ec && ec.T.Ty.Tag is Tag.Ent or Tag.Rel)
                    {
                        var parts = origin.Split(':'); // matchOp:var
                        if (!matchOntology.TryGetValue(parts[0], out var view)) { Err("E-Prop", node, $"cannot resolve {path}"); return null; }
                        var tid = ec.T.Ty.TypeId!;
                        var pn = path[(dot + 1)..];
                        var hit = view.PropsOf(tid).Where(kv => view.Props.TryGetValue(kv.Key, out var pi) && pi.Name == pn).Select(kv => (Id: kv.Key, Req: kv.Value)).ToList();
                        if (hit.Count != 1) { Err("E-Prop", node, $"{Ids.Name(tid)} has no property '{pn}' in the graph's ontology"); return null; }
                        var p = view.Props[hit[0].Id];
                        if (p.Role == "telemetry") { Err("E-Prop", node, $"telemetry property {pn} is read only through Series"); return null; }
                        var known = !demands.Add($"{parts[0]}|{parts[1]}|{pn}");
                        if (known && !R.Has(path)) { Err("E-Prop", node, $"{path} is read after an aggregation that dropped it; read the property before grouping"); return null; }
                        return new TField(path, new FType(KindOfProp(p, view), ec.T.Req && hit[0].Req, Role.Data));
                    }
                    Err("UnknownField", node, $"'{path}' is not a field of the row {R}");
                    return null;
                }
            case "Literal":
                {
                    var v = d.Prop(node, "value") ?? Value.Null;
                    var ty = v switch { TextValue => Ty.Text, IntegerValue => Ty.Int(), DecimalValue => Ty.Dec(), BooleanValue => Ty.Boolean, InstantValue => Ty.Instant, _ => Ty.Null };
                    return new TLit(v, new FType(ty, v is not NullValue, Role.Data));       // `null` is the absent value: it is optional, whatever kind it stands for
                }
            case "Call": return CallExpr(node, R);
            case "IdOf": return IdExpr(node, R);
            default: Err("Expr", node, $"unsupported expression node {d.TypeName(node)}"); return null;
        }
    }

    /// <summary>Rule IdOf-in-sinks: an identity expression stands only where an element is named (the identity and endpoints of an emit, the two sides of a link). Elsewhere a row would carry an identity that a later merge leaves stale in operator state, and Law 7.5 item 3 would no longer hold; a view reads identities through `match`, whose rows a merge updates.</summary>
    bool identityAllowed;
    TExpr? IdentityEx(string node, Schema R) { var was = identityAllowed; identityAllowed = true; try { return Ex(node, R); } finally { identityAllowed = was; } }

    TExpr? IdExpr(string node, Schema R)
    {
        if (!identityAllowed) { Err("IdentityOutsideSink", node, "an identity expression stands only in the identity or the endpoints of an emit and in a link (rule IdOf-in-sinks): a row that carries an identity into another operator would keep it after a merge. To group, join or filter by an element, emit it and read it through a `match` on a view of the graph"); return null; }
        var was = identityAllowed; identityAllowed = false;          // its components are plain values
        try { return IdExprIn(node, R); } finally { identityAllowed = was; }
    }

    TExpr? IdExprIn(string node, Schema R)
    {
        var dom = d.Target(node, "domain");
        if (dom is null) { Err("E-Id", node, "identity expression has no domain"); return null; }
        var comps = d.Children(dom, "component");
        var res = new List<TComp>(); var req = true; var ok = true;
        var provided = d.Children(node, "component");
        foreach (var sc in provided)
        {
            var kc = d.Target(sc, "component");
            var e = Ex(Child(sc, "value"), R);
            if (kc is null || e is null) { ok = false; continue; }
            var want = KindOf(d.Target(kc, "valueKind"), null, null, "1");
            if (!Types.Sub(e.Type.Ty, want, null)) { Err("E-Id", sc, $"component {Ids.Name(kc)} expects {want}, found {e.Type.Ty}"); ok = false; continue; }
            if (!e.Type.Role.IsData) { Err("RoleInterference", sc, "identity components must be data"); ok = false; continue; }
            req &= e.Type.Req;
            var canon = d.Target(kc, "canonicalizer");
            res.Add(new TComp(kc, Ids.Name(kc), canon is null ? "Exact" : Ids.Name(canon)["canon.".Length..], e));
        }
        if (comps.Count != provided.Count || comps.Any(c => !res.Any(r => r.CompId == c))) { if (ok) Err("E-Id", node, $"identity domain {Ids.Name(dom)} needs exactly its components ({string.Join(", ", comps.Select(Ids.Name))})"); return null; }
        if (!ok) return null;
        var ident = d.Target(dom, "identifies");
        var ordered = comps.Select(c => res.First(r => r.CompId == c)).ToList();
        return new TId(dom, Ids.Name(dom), ordered, new FType(Ty.Ent(ident), req, Role.Data));
    }

    TExpr? CallExpr(string node, Schema R)
    {
        var fnId = d.Target(node, "function");
        if (fnId is null) { Err("E-Call", node, "call has no function"); return null; }
        var fn = Ids.Name(fnId);
        var args = new List<TExpr>();
        foreach (var a in d.Children(node, "arg")) { var e = Ex(a, R); if (e is null) return null; args.Add(e); }
        var roles = args.Select(a => a.Type.Role).ToList();
        FType Bool(bool req = true, Role? r = null) => new(Ty.Boolean, req, r ?? Role.Data);
        string? e1 = null;
        bool Arity(int min, int max = int.MaxValue) { if (args.Count < min || args.Count > max) { e1 = $"{fn} takes {(max == min ? min.ToString() : $"at least {min}")} argument(s), found {args.Count}"; return false; } return true; }
        if (fn is not ("Coalesce" or "IsPresent") && args.Any(a => !a.Type.Req)) { Err("E-Call", node, $"{fn} does not accept an optional argument; use isPresent or coalesce"); return null; }
        Role? role;
        string? je;
        switch (fn)
        {
            case "Equal" or "NotEqual":
                if (!Arity(2, 2)) break;
                if (!Types.Comparable(args[0].Type.Ty, args[1].Type.Ty, outView) && !Types.Comparable(args[0].Type.Ty, args[1].Type.Ty, null)) { Err("E-Call", node, $"{fn} compares {args[0].Type.Ty} with {args[1].Type.Ty}"); return null; }
                role = Types.Join(fn, roles, out je); if (role is null) { Err("E-Call", node, je!); return null; }
                return new TCall(fn, args, Bool(true, role));
            case "LessThan" or "LessOrEqual" or "GreaterThan" or "GreaterOrEqual":
                if (!Arity(2, 2)) break;
                if (args[0].Type.Ty != args[1].Type.Ty || args[0].Type.Ty.Tag is not (Tag.Integer or Tag.Decimal or Tag.Instant or Tag.Text)) { Err("E-Call", node, $"{fn} needs two ordered operands of one kind and unit, found {args[0].Type.Ty} and {args[1].Type.Ty}"); return null; }
                role = Types.Join(fn, roles, out je); if (role is null) { Err("E-Call", node, je!); return null; }
                return new TCall(fn, args, Bool(true, role));
            case "And" or "Or":
                if (!Arity(2)) break;
                if (args.Any(a => a.Type.Ty.Tag != Tag.Boolean)) { Err("E-Call", node, $"{fn} takes Boolean arguments"); return null; }
                role = Types.Join(fn, roles, out je); if (role is null) { Err("E-Call", node, je!); return null; }
                return new TCall(fn, args, Bool(true, role));
            case "Not":
                if (!Arity(1, 1)) break;
                if (args[0].Type.Ty.Tag != Tag.Boolean) { Err("E-Call", node, "Not takes a Boolean"); return null; }
                return new TCall(fn, args, Bool(true, args[0].Type.Role));
            case "Add" or "Subtract":
                {
                    if (!Arity(2, 2)) break;
                    if (!args[0].Type.Ty.IsNumeric || args[0].Type.Ty != args[1].Type.Ty) { Err("E-Call", node, $"{fn} needs two numbers of one kind and unit, found {args[0].Type.Ty} and {args[1].Type.Ty}"); return null; }
                    role = Types.Join(fn, roles, out je); if (role is null) { Err("E-Call", node, je!); return null; }
                    return new TCall(fn, args, new FType(args[0].Type.Ty, true, role));
                }
            case "Multiply":
                {
                    if (!Arity(2, 2)) break;
                    var (a, b) = (args[0].Type.Ty, args[1].Type.Ty);
                    if (!a.IsNumeric || a.Tag != b.Tag) { Err("E-Call", node, "Multiply needs two numbers of one kind"); return null; }
                    role = Types.Join(fn, roles, out je); if (role is null) { Err("E-Call", node, je!); return null; }
                    return new TCall(fn, args, new FType(new Ty(a.Tag, Types.UnitMul(a.Unit, b.Unit)), true, role));
                }
            case "Divide":
                {
                    if (!Arity(2, 2)) break;
                    var (a, b) = (args[0].Type.Ty, args[1].Type.Ty);
                    if (!a.IsNumeric || !b.IsNumeric) { Err("E-Call", node, "Divide needs two numbers"); return null; }
                    role = Types.Join(fn, roles, out je); if (role is null) { Err("E-Call", node, je!); return null; }
                    return new TCall(fn, args, new FType(Ty.Dec(Types.UnitDiv(a.Unit, b.Unit)), true, role));
                }
            case "Negate":
                if (!Arity(1, 1)) break;
                if (!args[0].Type.Ty.IsNumeric) { Err("E-Call", node, "Negate needs a number"); return null; }
                return new TCall(fn, args, args[0].Type with { Req = true });
            case "ToDecimal":
                if (!Arity(1, 1)) break;
                if (args[0].Type.Ty.Tag != Tag.Integer) { Err("E-Call", node, "ToDecimal needs an Integer"); return null; }
                return new TCall(fn, args, new FType(Ty.Dec(args[0].Type.Ty.Unit), true, args[0].Type.Role));
            case "Concat":
                if (!Arity(2)) break;
                if (args.Any(a => a.Type.Ty.Tag != Tag.Text || !a.Type.Role.IsData)) { Err("E-Call", node, "Concat takes data Text arguments"); return null; }
                return new TCall(fn, args, new FType(Ty.Text, true, Role.Data));
            case "EntityId":
                if (!Arity(1, 1)) break;
                if (args[0].Type.Ty.Tag != Tag.Ent) { Err("E-Call", node, "EntityId takes an entity"); return null; }
                return new TCall(fn, args, new FType(Ty.Text, true, Role.Data));
            case "LowerInvariant" or "Trim":
                if (!Arity(1, 1)) break;
                if (args[0].Type.Ty.Tag != Tag.Text) { Err("E-Call", node, $"{fn} takes Text"); return null; }
                return new TCall(fn, args, new FType(Ty.Text, true, args[0].Type.Role));
            case "Coalesce":
                {
                    if (!Arity(2)) break;
                    if (args.Any(a => !Types.Sub(a.Type.Ty, args[0].Type.Ty, null))) { Err("E-Call", node, "Coalesce takes arguments of one kind"); return null; }
                    role = Types.Join(fn, roles, out je); if (role is null) { Err("E-Call", node, je!); return null; }
                    return new TCall(fn, args, new FType(args[0].Type.Ty, args[^1].Type.Req, role));
                }
            case "IsPresent":
                if (!Arity(1, 1)) break;
                return new TCall(fn, args, Bool(true, args[0].Type.Role));
            case "If":
                {
                    if (!Arity(3, 3)) break;
                    if (args[0].Type.Ty.Tag != Tag.Boolean) { Err("E-Call", node, "If needs a Boolean condition"); return null; }
                    if (!Types.Comparable(args[1].Type.Ty, args[2].Type.Ty, null)) { Err("E-Call", node, $"If branches differ: {args[1].Type.Ty} and {args[2].Type.Ty}"); return null; }
                    role = Types.Join("If", roles, out je);       // the condition decides the value: a telemetry condition makes a telemetry result
                    if (role is null) { Err("E-Call", node, je!); return null; }
                    return new TCall(fn, args, new FType(args[1].Type.Ty, true, role));
                }
            case "Rate":
                {
                    if (!Arity(1, 1)) break;
                    var a = args[0];
                    if (!a.Type.Ty.IsNumeric || a.Type.Role.Temporality != "delta") { Err("E-Call", node, $"Rate needs a numeric delta telemetry value, found {a.Type}"); return null; }
                    if (R.Window != a.Type.Role.Period) { Err("E-Call", node, "Rate: the row window differs from the value period"); return null; }
                    return new TCall(fn, args, new FType(Ty.Dec(Types.UnitDiv(a.Type.Ty.Unit, "s") is var u && a.Type.Ty.Unit == "1" ? "1/s" : u), true, Role.Tele("gauge", a.Type.Role.Period)));
                }
            default: Err("Unsupported", node, $"function {fn} is not supported in this engine slice"); return null;
        }
        Err("E-Call", node, e1 ?? $"bad call to {fn}");
        return null;
    }
}

/// <summary>Whole-space checks S1 to S4 and per-binding typing, over the checked projections.</summary>
public sealed class SpaceChecker(Graph defs)
{
    readonly DefView d = new(defs);

    public (TypedSpace? Space, List<Diagnostic> Diagnostics) Check(string spaceId, IReadOnlyDictionary<string, TypedProjection> projections)
    {
        var diags = new List<Diagnostic>();
        void Err(string code, string focus, string msg) => diags.Add(new Diagnostic(code, "error", focus, msg));
        var sources = d.Children(spaceId, "source").Select(s => new TSource(d.Text(s, "name") ?? Ids.Name(s), d.Text(s, "adapter") ?? "", d.Target(s, "provides") ?? "")).ToList();
        var graphIds = d.Children(spaceId, "graph");
        var master = d.Target(spaceId, "master");
        if (master is null) Err("S1", spaceId, "graph space has no master graph");
        var graphs = new List<TGraph>();
        var producedBy = new Dictionary<string, string>();
        foreach (var g in graphIds)
        {
            var proj = d.Target(g, "producedBy");
            if (proj is null) { Err("S2", g, "graph has no producing projection"); continue; }
            producedBy[g] = proj;
        }
        var allBindings = d.Children(spaceId, "binding");
        foreach (var g in graphIds)
        {
            if (!producedBy.TryGetValue(g, out var proj)) continue;
            var bindings = allBindings.Where(b => Ids.Name(b).StartsWith(Ids.Name(g) + ".", StringComparison.Ordinal)).ToList();       // a binding is named by its graph and port: two graphs may bind one projection
            if (!projections.TryGetValue(proj, out var tp)) { Err("S2", g, $"projection {Ids.Name(proj)} did not check"); continue; }
            var bind = new Dictionary<string, string>();
            foreach (var b in bindings)
            {
                var port = d.Target(b, "bindsPort"); var from = d.Target(b, "boundFrom");
                if (port is null || from is null || !port.StartsWith(proj + "/port:", StringComparison.Ordinal)) continue;
                var pn = Ids.Name(port);
                if (!bind.TryAdd(pn, Ids.Name(from))) { Err("S2", b, $"port {pn} is bound twice"); continue; }
                var tport = tp.Ports.FirstOrDefault(p => p.Name == pn);
                if (tport is null) continue;
                if (tport.Kind == "rows")
                {
                    if (d.TypeName(from) != "Source") { Err("S2", b, $"rows port {pn} must bind a source"); continue; }
                    if (d.Target(from, "provides") != tport.RowTypeId) Err("S2", b, $"source {Ids.Name(from)} provides {Ids.Name(d.Target(from, "provides") ?? "")}, port {pn} expects {Ids.Name(tport.RowTypeId ?? "")}");
                }
                else
                {
                    if (d.TypeName(from) != "Graph") { Err("S2", b, $"graph port {pn} must bind a graph"); continue; }
                    var upstream = d.Target(from, "producedBy");
                    var outMod = upstream is null ? null : projections.GetValueOrDefault(upstream)?.OutputModule;
                    if (outMod != tport.ExpectsModule) Err("S2", b, $"graph port {pn} expects module {tport.ExpectsModule}, graph {Ids.Name(from)} is produced under {outMod}");
                }
            }
            foreach (var p in tp.Ports.Where(p => !bind.ContainsKey(p.Name))) Err("S2", g, $"port {p.Name} of {tp.Name} is not bound");
            // parameters: constants bound in the space; the projection is checked again with their values
            var values = new Dictionary<string, Value>();
            foreach (var b in bindings)
            {
                var port = d.Target(b, "bindsPort");
                if (port is null || !port.StartsWith(proj + "/port:", StringComparison.Ordinal) || d.Target(b, "boundFrom") is not null) continue;
                var pn = Ids.Name(port); var lit = d.Prop(b, "value");
                if (tp.Params.All(x => x.Name != pn)) { Err("S2", b, $"port {pn} of {tp.Name} is not a parameter, it needs a source or a graph"); continue; }
                if (lit is null || lit is NullValue) { Err("S2", b, $"parameter {pn} needs a constant"); continue; }
                var want = tp.Params.First(x => x.Name == pn).Kind;
                if (lit.Kind.ToString() != want) { Err("S2", b, $"parameter {pn} is {want}, the binding gives {lit.Kind}"); continue; }
                values[pn] = lit;
            }
            foreach (var (pn, _, dflt) in tp.Params.Where(x => !values.ContainsKey(x.Name)).ToList())
            {
                if (dflt is not null) values[pn] = dflt;
                else Err("S2", g, $"parameter {pn} of {tp.Name} is not bound");
            }
            TypedProjection? instance = null;
            if (tp.Params.Count > 0 && !diags.Any(x => x.Severity == "error"))
            {
                var (inst, idiags) = new ProjectionChecker(defs).Check(proj, values);
                diags.AddRange(idiags.Where(x => x.IsError)); instance = inst;
            }
            graphs.Add(new TGraph(d.Text(g, "name") ?? Ids.Name(g), proj, g == master, bind, values, instance));
        }
        if (master is not null && producedBy.TryGetValue(master, out var mp) && projections.GetValueOrDefault(mp)?.Purpose != "ingestion") Err("S1", master, "the master graph must be produced by an ingestion projection");
        if (graphs.Count(g => g.Master) > 1) Err("S1", spaceId, "more than one master graph");
        // S3: DAG rooted at master
        var edges = graphs.ToDictionary(g => g.Name, g => g.Bindings.Where(kv => graphs.Any(x => x.Name == kv.Value)).Select(kv => kv.Value).ToList());
        var seen = new HashSet<string>(); var onPath = new HashSet<string>();
        bool Cyc(string n) { if (onPath.Contains(n)) return true; if (!seen.Add(n)) return false; onPath.Add(n); var c = edges.GetValueOrDefault(n)?.Any(Cyc) ?? false; onPath.Remove(n); return c; }
        if (graphs.Any(g => Cyc(g.Name))) Err("S3", spaceId, "graph production has a cycle");
        var masterName = graphs.FirstOrDefault(g => g.Master)?.Name;
        bool Reaches(string n, HashSet<string> v) { if (n == masterName) return true; if (!v.Add(n)) return false; return edges.GetValueOrDefault(n)?.Any(x => Reaches(x, v)) ?? false; }
        foreach (var g in graphs.Where(g => !g.Master && !Reaches(g.Name, []))) Err("S3", spaceId, $"graph {g.Name} is not reachable from the master");
        return (diags.Any(x => x.Severity == "error") ? null : new TypedSpace { Id = spaceId, Name = d.Text(spaceId, "name") ?? Ids.Name(spaceId), Sources = sources, Graphs = graphs }, diags);
    }
}
