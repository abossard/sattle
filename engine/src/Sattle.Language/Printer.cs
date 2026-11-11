using System.Globalization;
using System.Text;
using Sattle.Core;

namespace Sattle.Language;

/// <summary>The definition graph has no text form here (for example an operator input without a wire). Names the element.</summary>
public sealed class UnprintableException(string element, string message) : Exception(message) { public string Element { get; } = element; }

/// <summary>Canonical printer (research 03 section 7.2): graph to text with parse(print(G)) = G on the module's facts. Layout is canonical, names are the stored names.</summary>
public sealed class Printer(Graph g, string module)
{
    readonly DefView d = new(g);
    readonly string mid = Ids.Module(module);
    readonly StringBuilder sb = new();
    readonly Dictionary<string, string> aliasOf = new(StringComparer.Ordinal); // module -> alias
    string Name(string id) => d.Text(id, "name") ?? Ids.Name(id);

    public static string Print(Graph g, string module) => new Printer(g, module).Run();

    /// <summary>The text of one expression node of a module (aliases as the module's imports declare them).</summary>
    public static string ExprText(Graph g, string module, string exprNode)
    {
        var p = new Printer(g, module);
        foreach (var r in p.d.Out(p.mid, "imports")) p.aliasOf[Ids.ModuleOf(r.Target)] = r.Props.Text(Ids.KProp("alias")) ?? "";
        return p.Expr(exprNode, 0);
    }

    string Run()
    {
        Docs(d.Text(mid, "description"), "");
        var rels = d.Out(mid, "imports").OrderBy(r => r.Props.Text(Ids.KProp("alias")), StringComparer.Ordinal).ToList();
        var version = "1.0.0";
        var rel = g.Entities.FirstOrDefault(e => e.Id.StartsWith($"d:{module}@", StringComparison.Ordinal));
        if (rel is not null) version = rel.Props.Text(Ids.KProp("version")) ?? version;
        sb.Append($"module {module} @ {version};\n");
        foreach (var r in rels)
        {
            var alias = r.Props.Text(Ids.KProp("alias")) ?? "";
            var im = Ids.ModuleOf(r.Target);
            aliasOf[im] = alias;
            var re = g.Entity(r.Target);
            sb.Append($"import {im} @ {re?.Props.Text(Ids.KProp("version"))} {re?.Props.Text(Ids.KProp("digest"))} as {alias};\n");
        }
        sb.Append('\n');
        var blocks = new List<string>();
        foreach (var slot in new[] { "entity", "rel", "extend", "constraint", "identity", "equivalence", "rows", "projection", "space" })
            foreach (var id in d.Children(mid, slot).OrderBy(x => x, StringComparer.Ordinal))
            {
                try
                {
                    switch (slot)
                {
                    case "entity": Entity(id); break;
                    case "rel": Relationship(id); break;
                    case "extend": Extension(id); break;
                    case "constraint": Constraint(id); break;
                    case "identity": Identity(id); break;
                    case "equivalence": Equivalence(id); break;
                    case "rows": Rows(id); break;
                    case "projection": Projection(id); break;
                    case "space": Space(id); break;
                }
                }
                catch (UnprintableException) { throw; }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException or NullReferenceException or InvalidCastException or IndexOutOfRangeException or FormatException)
                { throw new UnprintableException(id, $"{d.TypeName(id)} {Name(id)} is incomplete: a required slot, reference or value is missing"); }
            }
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    void Docs(string? doc, string indent)
    {
        if (doc is null) return;
        foreach (var line in doc.Split('\n')) sb.Append(indent).Append("///").Append(line.Length > 0 ? " " + line : "").Append('\n');
    }

    string Ref(string id)
    {
        var m = Ids.ModuleOf(id); var n = Ids.Name(id);
        return m == module ? n : aliasOf.TryGetValue(m, out var a) ? a + "." + n : m + "." + n;
    }

    static string Q(string s) { var b = new StringBuilder(); Jcs.Str(b, s); return b.ToString(); }
    static string Lower(string s) => char.ToLowerInvariant(s[0]) + s[1..];

    string VType(string prop)
    {
        var vk = Ids.Name(d.Target(prop, "valueKind") ?? "");
        return vk switch
        {
            "List" => $"List<{Ids.Name(d.Target(prop, "itemKind") ?? "")}>",
            "Entity" => d.Target(prop, "refersTo") is { } r ? $"Ref<{Ref(r)}>" : "Ref<Any>",
            _ => vk,
        };
    }

    void Members(string owner, string indent)
    {
        var props = d.Out(owner, "hasProperty").OrderBy(r => r.Target, StringComparer.Ordinal).ToList();
        if (props.Count == 0) { sb.Append(";\n"); return; }
        sb.Append(" {\n");
        foreach (var r in props)
        {
            Docs(d.Text(r.Target, "description"), indent + "  ");
            var name = Name(r.Target); var seg = Ids.Name(r.Target);
            sb.Append(indent).Append("  ").Append(name);
            if (seg != name) sb.Append(" was ").Append(seg);
            if (r.Props.Get(Ids.KProp("required")) is BooleanValue { B: false }) sb.Append('?');
            sb.Append(": ").Append(VType(r.Target));
            if (d.Bool(r.Target, "nullable")) sb.Append(" nullable");
            if (d.Text(r.Target, "unit") is { } u) sb.Append(" unit ").Append(Q(u));
            if (d.Text(r.Target, "role") == "telemetry")
                sb.Append($" telemetry {d.Text(r.Target, "temporality")} {d.Text(r.Target, "combine")} every {Lexer.DurationText(d.Int(r.Target, "period"))}");
            if (d.Text(r.Target, "treeRole") is { } treeRole) sb.Append(" treerole ").Append(treeRole);
            if (d.Children(r.Target, "presentation") is { Count: > 0 } roles)
                sb.Append(" roles { ").Append(string.Join(", ", roles.OrderBy(x => x, StringComparer.Ordinal).Select(x => $"{Lit(d.Prop(x, "value") ?? Value.Null)}: {d.Text(x, "role")}"))).Append(" }");
            sb.Append(";\n");
        }
        sb.Append(indent).Append("}\n");
    }

    void Entity(string id)
    {
        Docs(d.Text(id, "description"), "");
        var name = Name(id); var seg = Ids.Name(id);
        sb.Append(d.Bool(id, "abstract") ? "abstract entity " : "entity ").Append(name);
        if (seg != name) sb.Append(" was ").Append(seg);
        var sup = d.Targets(id, "extends").ToList();
        if (sup.Count > 0) sb.Append(" : ").Append(string.Join(", ", sup.Select(Ref)));
        Members(id, "");
        sb.Append('\n');
    }

    void Relationship(string id)
    {
        Docs(d.Text(id, "description"), "");
        var name = Name(id); var seg = Ids.Name(id);
        sb.Append(d.Bool(id, "abstract") ? "abstract relationship " : "relationship ").Append(name);
        if (seg != name) sb.Append(" was ").Append(seg);
        if (d.Target(id, "extends") is { } s) sb.Append(" : ").Append(Ref(s));
        sb.Append(" (").Append(string.Join(" | ", d.Targets(id, "sourceType").Select(Ref))).Append(") -> (").Append(string.Join(" | ", d.Targets(id, "targetType").Select(Ref))).Append(')');
        if (d.Bool(id, "hierarchy")) sb.Append(" hierarchy");
        Members(id, "");
        sb.Append('\n');
    }

    void Extension(string id)
    {
        Docs(d.Text(id, "description") is { } desc && desc != Name(id) ? desc : null, "");
        sb.Append("extend ").Append(Ref(d.Target(id, "extendsType")!));
        Members(id, "");
        sb.Append('\n');
    }

    void Constraint(string id)
    {
        Docs(d.Text(id, "description"), "");
        var kind = d.Text(id, "kind") ?? "";
        var target = d.Target(id, "constrains")!;
        var props = d.Out(id, "onProperty").OrderBy(r => r.Props.Get(Ids.KProp("ordinal")) is IntegerValue i ? i.N : 0).Select(r => r.Target).ToList();
        var pname = props.Count > 0 ? Name(props[0]) : null;
        var defName = pname is null || kind == "key" ? $"{kind}-{Ids.Name(target)}" : $"{kind}-{Ids.Name(target)}.{pname}";
        sb.Append("constraint ");
        var name = d.Text(id, "name") ?? defName;
        if (name != defName) sb.Append(name).Append(' ');
        sb.Append(kind).Append(' ');
        switch (kind)
        {
            case "key": sb.Append(Ref(target)).Append(" (").Append(string.Join(", ", props.Select(Name))).Append(')'); break;
            case "enum":
                sb.Append(Ref(target)).Append('.').Append(pname).Append(" in [")
                    .Append(string.Join(", ", (g.Entity(id)!.Props.Get(Ids.KProp("values")) as ListValue)!.Items.OfType<TextValue>().Select(t => Q(t.S)))).Append(']'); break;
            case "pattern": sb.Append(Ref(target)).Append('.').Append(pname).Append(' ').Append(Q(d.Text(id, "pattern")!)); break;
            case "range":
                sb.Append(Ref(target)).Append('.').Append(pname).Append(' ');
                if (d.Prop(id, "min") is DecimalValue mn) sb.Append(Norm.Decimal(mn.Q)).Append(' ');
                sb.Append("..");
                if (d.Prop(id, "max") is DecimalValue mx) sb.Append(' ').Append(Norm.Decimal(mx.Q));
                break;
            case "cardinality":
                sb.Append(Ref(target)).Append(" per ").Append(d.Text(id, "side") ?? "source").Append(' ').Append(Norm.Decimal(d.Dec(id, "min"))).Append(" .. ")
                    .Append(d.Prop(id, "max") is DecimalValue cm ? Norm.Decimal(cm.Q) : "*"); break;
            case "acyclic": sb.Append(Ref(target)); break;
        }
        if (d.Text(id, "severity") == "warning") sb.Append(" warning");
        sb.Append(";\n\n");
    }

    void Identity(string id)
    {
        Docs(d.Text(id, "description"), "");
        sb.Append("identity ").Append(Name(id));
        if (d.Target(id, "identifies") is { } t) sb.Append(" for ").Append(Ref(t));
        sb.Append(" version ").Append(Q(d.Text(id, "canonicalizationVersion") ?? "1")).Append(" {");
        foreach (var c in d.Children(id, "component"))
        {
            var canon = d.Target(c, "canonicalizer");
            sb.Append(' ').Append(Name(c)).Append(": ").Append(Ids.Name(d.Target(c, "valueKind") ?? ""));
            if (canon is not null) sb.Append(" by ").Append(Lower(Ids.Name(canon)["canon.".Length..]));
            sb.Append(';');
        }
        sb.Append(" }\n\n");
    }

    void Equivalence(string id)
    {
        Docs(d.Text(id, "description"), "");
        var between = d.Out(id, "between").OrderBy(r => r.Props.Get(Ids.KProp("ordinal")) is IntegerValue i ? i.N : 0).Select(r => Ref(r.Target)).ToList();
        sb.Append($"equivalence {Name(id)} between {string.Join(", ", between)} {d.Text(id, "mode")} threshold {Norm.Decimal(d.Dec(id, "threshold"))};\n\n");
    }

    void Rows(string id)
    {
        Docs(d.Text(id, "description"), "");
        sb.Append("rows ").Append(Name(id)).Append(" {\n");
        foreach (var f in d.Children(id, "field"))
        {
            sb.Append("  ").Append(Name(f)).Append(d.Bool(f, "optional") ? "?" : "").Append(": ").Append(VType(f));
            if (d.Text(f, "unit") is { } u) sb.Append(" unit ").Append(Q(u));
            sb.Append(";\n");
        }
        sb.Append("}\n\n");
    }

    // ---- projections ----

    void Projection(string pid)
    {
        Docs(d.Text(pid, "description"), "");
        var outMod = Ids.ModuleOf(d.Target(pid, "outputOntology")!);
        sb.Append($"projection {Name(pid)} {d.Text(pid, "purpose")}{(d.Bool(pid, "forward") ? " forward" : "")} output {(outMod == module ? module : aliasOf.GetValueOrDefault(outMod, outMod))} {{\n");
        var ports = d.Children(pid, "port").OrderBy(x => Name(x), StringComparer.Ordinal).ToList();
        foreach (var p in ports)
        {
            var isRows = d.Text(p, "kind") == "rows";
            if (d.Text(p, "kind") == "param") { sb.Append("  in ").Append(Name(p)).Append(": param ").Append(Ids.Name(d.Target(p, "valueKind")!)).Append(d.Prop(p, "default") is { } dv && dv is not NullValue ? " = " + Lit(dv) : "").Append(";\n"); continue; }
            sb.Append("  in ").Append(Name(p)).Append(": ").Append(isRows ? "rows " + Ref(d.Target(p, "rowType")!) : "graph " + (Ids.ModuleOf(d.Target(p, "expects")!) is var m && m == module ? module : aliasOf.GetValueOrDefault(m, m))).Append(";\n");
        }
        if (d.Int(pid, "retain") > 0) sb.Append("  retain ").Append(d.Int(pid, "retain")).Append(";\n");
        var ops = d.Children(pid, "op");
        bool IsSink(string o) => d.TypeName(o) is "EmitEntity" or "EmitRelationship" or "Link";
        string Producer(string inPort)
        {
            var w = g.InRels(inPort).Select(g.Rel).FirstOrDefault(r => r is not null && Ids.Name(r!.Type) == "wire") ?? throw new UnprintableException(inPort, $"port {Ids.Name(inPort)} has no wire (W1)");
            var src = w.Source; var parent = src[..src.LastIndexOf("/port:", StringComparison.Ordinal)];
            return parent == pid ? Name(src) : Base(Name(parent));
        }
        // let groups
        var governed = new HashSet<string>(d.Children(pid, "rule").SelectMany(r => d.Targets(r, "governs")), StringComparer.Ordinal);
        var groups = ops.Where(o => !IsSink(o) && !(d.TypeName(o) == "Lift" && governed.Contains(o))).GroupBy(o => Base(Name(o))).ToDictionary(x => x.Key, x => x.OrderBy(o => Suffix(Name(o))).ToList());
        var printed = new HashSet<string>(); var order = new List<string>();
        void Visit(string b)
        {
            if (!printed.Add(b)) return;
            foreach (var o in groups[b])
                foreach (var p in d.Children(o, "port").Where(x => d.Text(x, "direction") == "in" && d.Text(x, "kind") == "rows")) { var pr = Producer(p); if (pr != b && groups.ContainsKey(pr)) Visit(pr); }
            foreach (var o in groups[b].Skip(0).Where(o => d.TypeName(o) is "Series")) { _ = o; }
            order.Add(b);
        }
        foreach (var b in groups.Keys.Order(StringComparer.Ordinal)) Visit(b);
        foreach (var b in order) Let(pid, b, groups[b], Producer);
        // sinks
        var ruleOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in d.Children(pid, "rule")) foreach (var s in d.Targets(r, "governs")) ruleOf[s] = r;
        foreach (var s in ops.Where(o => IsSink(o) && !ruleOf.ContainsKey(o)).OrderBy(o => Suffix(Name(o)))) Sink(s, "  ", Producer);
        foreach (var r in d.Children(pid, "rule").OrderBy(x => x, StringComparer.Ordinal))
        {
            var cls = d.Text(r, "class");
            sb.Append("  rule ").Append(Name(r)).Append(' ').Append(cls);
            if (d.Bool(r, "putput")) sb.Append(" putput");
            if (d.Text(r, "reason") is { } reason) sb.Append(' ').Append(Q(reason));
            sb.Append(" {\n");
            Policies(r);
            foreach (var lf in d.Targets(r, "governs").Where(o => d.TypeName(o) == "Lift").OrderBy(o => Name(o), StringComparer.Ordinal)) Lift(lf, "    ");
            foreach (var s in d.Targets(r, "governs").Where(o => d.TypeName(o) != "Lift").OrderBy(o => Suffix(Name(o)))) Sink(s, "    ", Producer);
            sb.Append("  }\n");
        }
        sb.Append("}\n\n");
    }

    void Policies(string rule)
    {
        if (g.Entity(rule)!.Props.Get(Ids.KProp("accept")) is ListValue l && l.Items.Length > 0)
            sb.Append("    accept ").Append(string.Join(", ", l.Items.OfType<TextValue>().Select(x => x.S.StartsWith("update(", StringComparison.Ordinal) ? "update(" + x.S[7..^1].Replace(",", ", ") + ")" : x.S))).Append(";\n");
        string Rec(string slot) => "{ " + string.Join(", ", d.Children(rule, slot).OrderBy(x => Name(d.Target(x, "property")!), StringComparer.Ordinal).Select(x => $"{Name(d.Target(x, "property")!)}: {Expr(d.Children(x, "value")[0], 0)}")) + " }";
        if (d.Target(rule, "createKey") is { } ck)
        {
            sb.Append("    on create key ").Append(Ref(ck));
            if (d.Children(rule, "createDefault").Count > 0) sb.Append(" default ").Append(Rec("createDefault"));
            sb.Append(";\n");
        }
        if (d.Text(rule, "onDelete") == "remove") sb.Append("    on delete remove;\n");
        else if (d.Text(rule, "onDelete") == "set") sb.Append("    on delete set ").Append(Rec("deleteSet")).Append(";\n");
    }

    static string Base(string name) { var i = name.IndexOf('#'); return i < 0 ? name : name[..i]; }
    static int Suffix(string name)
    {
        var i = name.IndexOf('#'); if (i >= 0) return int.Parse(name[(i + 1)..], CultureInfo.InvariantCulture);
        var j = name.LastIndexOf('.'); return j >= 0 && int.TryParse(name[(j + 1)..], out var k) ? k : int.MaxValue;
    }

    /// <summary>The lift statement of a Lift operator: only the clauses that differ from the defaults are printed.</summary>
    void Lift(string op, string indent = "  ")
    {
        if (d.Target(op, "ofType") is null || d.Target(op, "liftEdge") is null || d.Target(op, "liftVia") is null) throw new UnprintableException(op, $"lift {Name(op)} needs the relationship to lift to, the edge type and the mapping");
        string GraphOf(string port) => Name((g.InRels(Ids.Child(op, "port", port)).Select(g.Rel).FirstOrDefault(r => r is not null && Ids.Name(r!.Type) == "wire") ?? throw new UnprintableException(op, $"lift {Name(op)}: the port {port} has no wire (W1)"))!.Source);
        sb.Append(indent).Append("lift ").Append(Name(op)).Append(" as ").Append(Ref(d.Target(op, "ofType")!))
            .Append(" from ").Append(GraphOf("edges")).Append(' ').Append(Ref(d.Target(op, "liftEdge")!)).Append(" via ").Append(GraphOf("map")).Append(' ').Append(Ref(d.Target(op, "liftVia")!));
        if (d.Children(op, "when").Count > 0) sb.Append("\n" + indent + "  when ").Append(Expr(d.Children(op, "when")[0], 0));
        var carry = d.Children(op, "carry");
        if (carry.Count > 0) sb.Append("\n" + indent + "  carry ").Append(string.Join(", ", carry.Select(a => $"{d.Text(a, "target")} = {Expr(d.Children(a, "value")[0], 0)}")));
        var keep = d.Children(op, "keep");
        if (keep.Count > 0) sb.Append("\n" + indent + "  keep ").Append(string.Join(", ", keep.Select(a => $"{d.Text(a, "target")} = {Expr(d.Children(a, "value")[0], 0)}")));
        var derive = d.Children(op, "derive");
        if (derive.Count > 0) sb.Append("\n" + indent + "  derive ").Append(string.Join(", ", derive.Select(a => $"{d.Text(a, "target")} = {Expr(d.Children(a, "value")[0], 0)}")));
        foreach (var st in d.Children(op, "state"))
            sb.Append($"\n{indent}  state {d.Text(st, "target")} of {d.Text(st, "of")} above {Norm.Decimal(d.Dec(st, "above"))} for {d.Int(st, "enterAfter")} then {Q(d.Text(st, "high")!)} below {Norm.Decimal(d.Dec(st, "below"))} for {d.Int(st, "exitAfter")} then {Q(d.Text(st, "low")!)} initial {Q(d.Text(st, "initial")!)}");
        if (d.Text(op, "images") is { } images) sb.Append("\n" + indent + "  images ").Append(images);
        if (d.Text(op, "selfLoops") != "drop") sb.Append("\n" + indent + "  selfloops ").Append(d.Text(op, "selfLoops"));
        if (d.Int(op, "within") != LiftDefaults.WithinNs) sb.Append("\n" + indent + "  within ").Append(Lexer.DurationText(d.Int(op, "within")));
        sb.Append(";\n");
    }

    void Let(string pid, string name, List<string> ops, Func<string, string> producer)
    {
        if (d.TypeName(ops[0]) == "Lift") { Lift(ops[0]); return; }
        sb.Append("  let ").Append(name).Append(" = ");
        if (d.TypeName(ops[0]) == "Fixpoint")
        {
            sb.Append("fixpoint ").Append(producer(Ids.Child(ops[0], "port", "seed"))).Append("\n    via ");
            Chain(pid, [.. ops.Skip(1)], producer);
            sb.Append("\n    max ").Append(d.Int(ops[0], "maxIterations")).Append(";\n");
            return;
        }
        Chain(pid, ops, producer);
        sb.Append(";\n");
    }

    void Chain(string pid, List<string> ops, Func<string, string> producer)
    {
        var steps = ops;
        var first = ops[0];
        try { ChainSource(pid, ops, producer, ref steps); }
        catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException or NullReferenceException or KeyNotFoundException)
        { throw new UnprintableException(first, $"{d.TypeName(first)} {Name(first)} is missing a required slot or reference ({e.Message})"); }
        foreach (var op in steps)
        {
            sb.Append("\n    |> ");
            try { PrintStep(op); }
            catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException or NullReferenceException or KeyNotFoundException)
            { throw new UnprintableException(op, $"{d.TypeName(op)} {Name(op)} is missing a required slot or reference ({e.Message})"); }
        }
    }

    void ChainSource(string pid, List<string> ops, Func<string, string> producer, ref List<string> steps)
    {
        var first = ops[0];
        if (d.TypeName(first) == "Match") { MatchSource(first, pid); steps = ops.Skip(1).ToList(); }
        else if (d.TypeName(first) == "Values")
        {
            sb.Append("values ").Append(string.Join(", ", d.Children(first, "row").OrderBy(r => r, StringComparer.Ordinal).Select(r => Record(d.Children(r, "assign").OrderBy(a => d.Text(a, "target"), StringComparer.Ordinal).Select(a => (d.Text(a, "target")!, d.Children(a, "value")[0]))))));
            steps = ops.Skip(1).ToList();
        }
        else if (d.TypeName(first) == "Join")
        {
            sb.Append("join ").Append(producer(Ids.Child(first, "port", "left"))).Append(", ").Append(producer(Ids.Child(first, "port", "right"))).Append(" on ").Append(Expr(d.Children(first, "on")[0], 0));
            steps = ops.Skip(1).ToList();
        }
        else if (d.TypeName(first) == "Union")
        {
            sb.Append("union ").Append(producer(Ids.Child(first, "port", "in0"))).Append(", ").Append(producer(Ids.Child(first, "port", "in1")));
            if (d.Text(first, "conflict") is { } c && c != "preserveEvidence") sb.Append(" conflict ").Append(c);
            steps = ops.Skip(1).ToList();
        }
        else sb.Append(producer(Ids.Child(first, "port", "in")));
    }

    string ThresholdPart(string op, string slot, string number) => d.Children(op, slot) is { Count: > 0 } c ? Expr(c[0], 0) : number;

    void PrintStep(string op)
    {
        {
            switch (d.TypeName(op))
            {
                case "Filter": sb.Append("where ").Append(Expr(d.Children(op, "predicate")[0], 0)); break;
                case "Distinct": sb.Append("distinct"); break;
                case "Retain": sb.Append($"retain on {d.Text(op, "timestampField")} for {Lexer.DurationText(d.Int(op, "age"))}"); break;
                case "Traverse":
                    {
                        var gp = g.InRels(Ids.Child(op, "port", "graph")).Select(g.Rel).First(r => r is not null && Ids.Name(r!.Type) == "wire")!;
                        sb.Append($"traverse {Name(gp.Source)} via {Ref(d.Target(op, "ofType")!)} {d.Text(op, "direction")} depth {d.Int(op, "minDepth")} .. {d.Int(op, "maxDepth")} from {d.Text(op, "start")} as {d.Text(op, "target")}");
                        break;
                    }
                case "Map": sb.Append("select ").Append(Record(d.Children(op, "assign").Select(a => (d.Text(a, "target")!, d.Children(a, "value")[0])))); break;
                case "Window": sb.Append($"window on {d.Text(op, "timestampField")} width {Lexer.DurationText(d.Int(op, "width"))} late {d.Text(op, "late")}"); break;
                case "Group": sb.Append("group by ").Append(KeyRecord(op)).Append(" aggregate ").Append(Aggs(op)); break;
                case "Rollup": sb.Append("rollup by ").Append(KeyRecord(op)).Append(" combine ").Append(Aggs(op)); break;
                case "Series":
                    {
                        var gp = g.InRels(Ids.Child(op, "port", "graph")).Select(g.Rel).First(r => r is not null && Ids.Name(r!.Type) == "wire")!;
                        sb.Append("series ").Append(Name(gp.Source)).Append(' ').Append(string.Join(", ", d.Children(op, "read").Select(r => d.Text(r, "path")))).Append(" within ").Append(Lexer.DurationText(d.Int(op, "within")));
                        break;
                    }
                case "Threshold":
                    sb.Append("threshold ").Append(Expr(d.Children(op, "value")[0], 0)).Append(" by ").Append(KeyRecord(op))
                        .Append($" above {ThresholdPart(op, "aboveBy", Norm.Decimal(d.Dec(op, "above")))} for {ThresholdPart(op, "enterBy", d.Int(op, "enterAfter").ToString())} then {Q(d.Text(op, "high")!)} below {ThresholdPart(op, "belowBy", Norm.Decimal(d.Dec(op, "below")))} for {ThresholdPart(op, "exitBy", d.Int(op, "exitAfter").ToString())} then {Q(d.Text(op, "low")!)} initial {Q(d.Text(op, "initial")!)} as {d.Text(op, "target")}");
                    break;
                default: throw new InvalidOperationException($"cannot print operator {d.TypeName(op)}");
            }
        }
    }

    void MatchSource(string op, string pid)
    {
        if (d.Children(op, "node").Count == 0) throw new UnprintableException(op, $"Match {Name(op)} has no pattern node");
        var gp = g.InRels(Ids.Child(op, "port", "graph")).Select(g.Rel).First(r => r is not null && Ids.Name(r!.Type) == "wire")!;
        sb.Append("match ").Append(Name(gp.Source)).Append(' ');
        var seen = new HashSet<string>();
        string Node(string nid)
        {
            var v = d.Text(nid, "var") ?? Ids.Name(nid);
            if (seen.Add(v)) return $"({v}: {Ref(d.Target(nid, "ofType")!)})";
            return $"({v})";
        }
        var paths = new List<string>(); var used = new HashSet<string>();
        var edges = d.Children(op, "edge").OrderBy(e => d.Text(e, "var") is null ? 0 : 1).ThenBy(e => Ids.Name(e) is var n && n.StartsWith('#') ? int.Parse(n[1..], CultureInfo.InvariantCulture) : 0).ThenBy(e => e, StringComparer.Ordinal).ToList();
        foreach (var e in edges)
        {
            var from = d.Target(e, "from")!; var to = d.Target(e, "to")!;
            used.Add(from); used.Add(to);
            var v = d.Text(e, "var");
            paths.Add($"{Node(from)}-[{(v is null ? "" : v)}:{Ref(d.Target(e, "ofType")!)}]->{Node(to)}");
        }
        foreach (var n in d.Children(op, "node").Where(n => !used.Contains(n))) paths.Add(Node(n));
        sb.Append(string.Join(",\n      ", paths));
    }

    string KeyRecord(string op) => Record(d.Children(op, "key").OrderBy(k => d.Text(k, "target"), StringComparer.Ordinal).Select(k => (d.Text(k, "target")!, d.Children(k, "value")[0])));
    string Aggs(string op) => "{ " + string.Join(", ", d.Children(op, "agg").OrderBy(a => d.Text(a, "target"), StringComparer.Ordinal).Select(a =>
    {
        var args = new List<string>();
        if (d.Children(a, "value").FirstOrDefault() is { } v) args.Add(Expr(v, 0));
        if ((d.Children(a, "weight").FirstOrDefault() ?? d.Children(a, "order").FirstOrDefault()) is { } w) args.Add(Expr(w, 0));
        return $"{d.Text(a, "target")}: {d.Text(a, "fn")}({string.Join(", ", args)})";
    })) + " }";
    string Record(IEnumerable<(string Name, string Node)> items) => "{ " + string.Join(", ", items.Select(x => $"{x.Name}: {Expr(x.Node, 0)}")) + " }";

    void Sink(string op, string indent, Func<string, string> producer)
    {
        var mark = sb.Length;
        try { PrintSink(op, indent, producer); }
        catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException or NullReferenceException or KeyNotFoundException)
        { sb.Length = mark; throw new UnprintableException(op, $"{d.TypeName(op)} {Name(op)} is missing a required slot or reference ({e.Message})"); }
    }

    void PrintSink(string op, string indent, Func<string, string> producer)
    {
        var from = producer(Ids.Child(op, "port", "in"));
        switch (d.TypeName(op))
        {
            case "EmitEntity":
                sb.Append(indent).Append(d.Text(op, "mode") == "update" ? "emit update entity " : "emit entity ").Append(Ref(d.Target(op, "ofType")!)).Append(" from ").Append(from)
                    .Append("\n").Append(indent).Append("  identity ").Append(Expr(d.Children(op, "id")[0], 0));
                Sets(op, indent);
                break;
            case "EmitRelationship":
                sb.Append(indent).Append(d.Text(op, "mode") == "update" ? "emit update relationship " : "emit relationship ").Append(Ref(d.Target(op, "ofType")!)).Append(" from ").Append(from)
                    .Append(" key ").Append(d.Text(op, "key")).Append(":\n").Append(indent).Append("  ").Append(Expr(d.Children(op, "source")[0], 0)).Append(" -> ").Append(Expr(d.Children(op, "target")[0], 0));
                Sets(op, indent);
                if (d.Text(op, "retype") is { } rt && rt != "reject") sb.Append(" on retype ").Append(rt);
                break;
            case "Link":
                sb.Append(indent).Append("link ").Append(Ref(d.Target(op, "policy")!)).Append(" from ").Append(from)
                    .Append("\n").Append(indent).Append("  left ").Append(Expr(d.Children(op, "left")[0], 0))
                    .Append("\n").Append(indent).Append("  right ").Append(Expr(d.Children(op, "right")[0], 0))
                    .Append("\n").Append(indent).Append("  confidence ").Append(Expr(d.Children(op, "confidence")[0], 0));
                break;
        }
        sb.Append(";\n");
    }

    void Sets(string op, string indent)
    {
        var sets = d.Children(op, "set").OrderBy(s => Name(d.Target(s, "property")!), StringComparer.Ordinal).ToList();
        if (sets.Count == 0) return;
        sb.Append('\n').Append(indent).Append("  set { ").Append(string.Join(", ", sets.Select(s => $"{Name(d.Target(s, "property")!)}: {Expr(d.Children(s, "value")[0], 0)}"))).Append(" }");
    }

    // ---- expressions ----
    // precedence: or 1, and 2, not 3, compare 4, sum 5, product 6, unary 7, primary 8

    string Expr(string node, int min)
    {
        string Wrap(int prec, string text) => prec < min ? "(" + text + ")" : text;
        switch (d.TypeName(node))
        {
            case "FieldRef": return d.Text(node, "path")!;
            case "Literal": { var v = d.Prop(node, "value")!; var t = Lit(v); return v is IntegerValue { N.Sign: < 0 } or DecimalValue { Q: < 0 } ? Wrap(7, t) : t; }
            case "IdOf":
                {
                    var dom = d.Target(node, "domain")!;
                    var comps = d.Children(node, "component").ToDictionary(c => d.Target(c, "component")!);
                    var ordered = d.Children(dom, "component").Where(comps.ContainsKey).Select(c => $"{Name(c)}: {Expr(d.Children(comps[c], "value")[0], 0)}");
                    return Ref(dom) + "(" + string.Join(", ", ordered) + ")";
                }
            case "Call":
                {
                    var fn = Ids.Name(d.Target(node, "function")!);
                    var args = d.Children(node, "arg");
                    string A(int i, int m) => Expr(args[i], m);
                    switch (fn)
                    {
                        case "Equal" or "NotEqual" or "LessThan" or "LessOrEqual" or "GreaterThan" or "GreaterOrEqual":
                            return Wrap(4, $"{A(0, 5)} {fn switch { "Equal" => "==", "NotEqual" => "!=", "LessThan" => "<", "LessOrEqual" => "<=", "GreaterThan" => ">", _ => ">=" }} {A(1, 5)}");
                        case "Add" or "Subtract" or "Concat" when args.Count == 2:
                            return Wrap(5, $"{A(0, 5)} {fn switch { "Add" => "+", "Subtract" => "-", _ => "++" }} {A(1, 6)}");
                        case "Multiply" or "Divide": return Wrap(6, $"{A(0, 6)} {(fn == "Multiply" ? "*" : "/")} {A(1, 7)}");
                        case "And": return Wrap(2, string.Join(" and ", args.Select(a => Expr(a, 3))));
                        case "Or": return Wrap(1, string.Join(" or ", args.Select(a => Expr(a, 2))));
                        case "Not": return Wrap(3, "not " + A(0, 4));
                        case "Negate": return Wrap(7, "-" + A(0, 8));
                        default: return Lower(fn) + "(" + string.Join(", ", args.Select(a => Expr(a, 0))) + ")";
                    }
                }
            default: throw new InvalidOperationException("cannot print " + d.TypeName(node));
        }
    }

    static string Lit(Value v) => v switch
    {
        TextValue t => Q(t.S),
        IntegerValue i => i.N.ToString(CultureInfo.InvariantCulture),
        DecimalValue q => Norm.Decimal(q.Q) is var s && s.Contains('.') ? s : s + ".0",
        BooleanValue b => b.B ? "true" : "false",
        InstantValue n => "@" + Norm.Instant(n.Ns),
        _ => "null",
    };

    // ---- spaces ----
    void Space(string id)
    {
        Docs(d.Text(id, "description"), "");
        sb.Append("space ").Append(Name(id)).Append(" {\n");
        foreach (var s in d.Children(id, "source")) sb.Append($"  source {Name(s)} adapter {Q(d.Text(s, "adapter")!)} rows {Ref(d.Target(s, "provides")!)};\n");
        var master = d.Target(id, "master");
        foreach (var gr in d.Children(id, "graph"))
        {
            var proj = d.Target(gr, "producedBy")!;
            var args = d.Children(id, "binding").Where(b => Ids.Name(b).StartsWith(Name(gr) + ".", StringComparison.Ordinal))
                .Select(b => (Port: Ids.Name(d.Target(b, "bindsPort")!), From: d.Prop(b, "value") is { } lit && lit is not NullValue ? Lit(lit) : Name(d.Target(b, "boundFrom")!))).OrderBy(x => x.Port, StringComparer.Ordinal);
            sb.Append($"  {(gr == master ? "master " : "")}graph {Name(gr)} = {Ref(proj)}({string.Join(", ", args.Select(a => $"{a.Port}: {a.From}"))});\n");
        }
        sb.Append("}\n\n");
    }
}
