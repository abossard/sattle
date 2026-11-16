using System.Globalization;
using System.Text;
using Sattle.Core;

namespace Sattle.Language;

/// <summary>The definition graph has no text form here (for example an operator input without a wire). Names the element.</summary>
public sealed class UnprintableException(string element, string message) : Exception(message) { public string Element { get; } = element; }

/// <summary>Canonical printer (research 03 section 7.2): graph to text with parse(print(G)) = G on the module's facts. Layout is canonical, names are the stored names.
/// A module text holds no projection: each projection is a file of its own, which is never printed.</summary>
public sealed class Printer(Graph g, string module)
{
    readonly DefView d = new(g);
    readonly string mid = Ids.Module(module);
    readonly StringBuilder sb = new();
    readonly Dictionary<string, string> aliasOf = new(StringComparer.Ordinal); // module -> alias
    string Name(string id) => d.Text(id, "name") ?? Ids.Name(id);

    public static string Print(Graph g, string module) => new Printer(g, module).Run();

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
        foreach (var slot in new[] { "entity", "rel", "extend", "constraint", "identity", "equivalence", "rows", "space" })
            foreach (var id in InDeclarationOrder(d.Children(mid, slot).OrderBy(x => x, StringComparer.Ordinal)))
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
                    case "space": Space(id); break;
                }
                }
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
            if (r.Props.Get(Ids.KProp("label")) is BooleanValue { B: true }) sb.Append(" label");
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

    /// <summary>Types by name, except that a type comes after the types of this module it extends: a module is read in one pass, so a supertype is declared before its subtypes.</summary>
    IEnumerable<string> InDeclarationOrder(IEnumerable<string> byName)
    {
        var all = byName.ToList(); var set = all.ToHashSet(StringComparer.Ordinal); var done = new HashSet<string>(StringComparer.Ordinal); var order = new List<string>();
        void Visit(string id)
        {
            if (!done.Add(id)) return;
            foreach (var sup in d.Targets(id, "extends").Where(set.Contains).OrderBy(x => x, StringComparer.Ordinal)) Visit(sup);
            order.Add(id);
        }
        foreach (var id in all) Visit(id);
        return order;
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
        if (d.Text(id, "kind") is { } kind) sb.Append(" kind ").Append(kind);
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
