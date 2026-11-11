using System.Text.RegularExpressions;

namespace Sattle.Core;

/// <summary>Typed read access to a definition graph: children by slot, reference edges by name, properties by kernel name.</summary>
public sealed class DefView(Graph graph)
{
    readonly Graph g = graph;
    public Graph Graph => g;
    public Entity? Node(string id) => g.Entity(id);
    static string EdgeName(Rel r) => Ids.Name(r.Type);

    public IEnumerable<Rel> Out(string id, string edge) => g.OutRels(id).Select(g.Rel).Where(r => r is not null && EdgeName(r!) == edge).Select(r => r!);
    public IEnumerable<string> Targets(string id, string edge) => Out(id, edge).Select(r => r.Target).OrderBy(x => x, StringComparer.Ordinal);
    public string? Target(string id, string edge) => Out(id, edge).Select(r => r.Target).FirstOrDefault();
    public string? Container(string id) => g.InRels(id).Select(g.Rel).Where(r => r is not null && EdgeName(r!) == "contains").Select(r => r!.Source).FirstOrDefault();

    /// <summary>Children of a slot, ordered by ordinal when present, else by id.</summary>
    public List<string> Children(string id, string slot) =>
        Out(id, "contains").Where(r => SlotOf(r) == slot)
            .OrderBy(r => OrdOf(r)).ThenBy(r => r.Target, StringComparer.Ordinal).Select(r => r.Target).ToList();
    public static string SlotOf(Rel contains) => contains.Props.Text(Ids.KProp("slot")) ?? "";
    public static int OrdOf(Rel contains) => contains.Props.TryGet(Ids.KProp("ordinal"), out var v) && v is IntegerValue i ? (int)i.N : int.MaxValue;

    public string TypeName(string id) => Ids.Name(g.Entity(id)?.Type ?? "");
    public Value? Prop(string id, string name) => g.Entity(id)?.Props.Get(Ids.KProp(name));
    public string? Text(string id, string name) => Prop(id, name) is TextValue t ? t.S : null;
    public bool Bool(string id, string name, bool dflt = false) => Prop(id, name) is BooleanValue b ? b.B : dflt;
    public long Int(string id, string name, long dflt = 0) => Prop(id, name) is IntegerValue i ? (long)i.N : dflt;
    public decimal Dec(string id, string name, decimal dflt = 0) => Prop(id, name) is DecimalValue d ? d.Q : dflt;
    public IEnumerable<string> Nodes() => g.Entities.Select(e => e.Id);
}

/// <summary>An ontology compiled from a definition graph: the types, properties, constraints and subtype closure that a data graph is checked against.</summary>
public sealed class OntologyView
{
    public sealed record PropInfo(string Id, string Name, string ValueKind, string? ItemKind, bool Nullable, string? Unit, string Role, string? Temporality, string? Combine, long Period, IReadOnlyList<(Value Value, string Role)>? Presentation = null);
    public sealed class TypeInfo(string id, string name, bool isAbstract, bool isRel)
    {
        public string Id { get; } = id; public string Name { get; } = name; public bool Abstract { get; } = isAbstract; public bool IsRel { get; } = isRel;
        public List<string> Supers { get; } = [];
        public Dictionary<string, bool> OwnProps { get; } = new(StringComparer.Ordinal); // property id -> required
        public HashSet<string> Sources { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Targets { get; } = new(StringComparer.Ordinal);
    }
    public sealed record ConstraintInfo(string Id, string Kind, string Severity, string TypeId, string? PropId, string[] Values, decimal? Min, decimal? Max, string? Pattern, string? Side);

    public Dictionary<string, TypeInfo> Types { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, PropInfo> Props { get; } = new(StringComparer.Ordinal);
    public List<ConstraintInfo> Constraints { get; } = [];
    readonly Dictionary<string, Dictionary<string, bool>> allProps = new(StringComparer.Ordinal);
    readonly Dictionary<string, HashSet<string>> ancestors = new(StringComparer.Ordinal);
    public HashSet<string> Modules { get; } = new(StringComparer.Ordinal);

    /// <summary>Builds the view from the nodes of the given modules in a definition graph.</summary>
    public static OntologyView Build(Graph defs, IEnumerable<string> modules)
    {
        var mods = modules.ToHashSet(StringComparer.Ordinal);
        var v = new OntologyView();
        foreach (var m in mods) v.Modules.Add(m);
        var d = new DefView(defs);
        bool In(string id) => mods.Contains(Ids.ModuleOf(id));
        foreach (var e in defs.Entities.Where(e => In(e.Id)))
        {
            switch (Ids.Name(e.Type))
            {
                case "EntityType" or "RelationshipType":
                    {
                        var rel = Ids.Name(e.Type) == "RelationshipType";
                        var t = new TypeInfo(e.Id, d.Text(e.Id, "name") ?? Ids.Name(e.Id), d.Bool(e.Id, "abstract"), rel);
                        foreach (var s in d.Targets(e.Id, "extends")) t.Supers.Add(s);
                        foreach (var r in d.Out(e.Id, "hasProperty")) t.OwnProps[r.Target] = r.Props.Get(Ids.KProp("required")) is BooleanValue { B: true };
                        foreach (var s in d.Targets(e.Id, "sourceType")) t.Sources.Add(s);
                        foreach (var s in d.Targets(e.Id, "targetType")) t.Targets.Add(s);
                        v.Types[e.Id] = t;
                        break;
                    }
                case "PropertyType":
                    v.Props[e.Id] = new PropInfo(e.Id, d.Text(e.Id, "name") ?? "", Ids.Name(d.Target(e.Id, "valueKind") ?? ""), d.Target(e.Id, "itemKind") is { } ik ? Ids.Name(ik) : null,
                        d.Bool(e.Id, "nullable"), d.Text(e.Id, "unit"), d.Text(e.Id, "role") ?? "graphData", d.Text(e.Id, "temporality"), d.Text(e.Id, "combine"), d.Int(e.Id, "period"),
                        [.. d.Children(e.Id, "presentation").OrderBy(x => x, StringComparer.Ordinal).Select(x => (d.Prop(x, "value") ?? Value.Null, d.Text(x, "role") ?? "neutral"))]);
                    break;
                case "Constraint":
                    {
                        var kind = d.Text(e.Id, "kind") ?? "";
                        var target = d.Target(e.Id, "constrains");
                        if (target is null) break;
                        var prop = d.Out(e.Id, "onProperty").OrderBy(r => r.Props.Get(Ids.KProp("ordinal")) is IntegerValue i ? i.N : 0).Select(r => r.Target).FirstOrDefault();
                        var values = e.Props.Get(Ids.KProp("values")) is ListValue lv ? lv.Items.OfType<TextValue>().Select(x => x.S).ToArray() : [];
                        v.Constraints.Add(new ConstraintInfo(e.Id, kind, d.Text(e.Id, "severity") ?? "error", target, prop, values,
                            e.Props.Get(Ids.KProp("min")) is DecimalValue mn ? mn.Q : null, e.Props.Get(Ids.KProp("max")) is DecimalValue mx ? mx.Q : null,
                            d.Text(e.Id, "pattern"), d.Text(e.Id, "side")));
                        break;
                    }
            }
        }
        // extensions add properties to an extended type (visible only when the extending module is in the closure)
        foreach (var e in defs.Entities.Where(e => In(e.Id) && Ids.Name(e.Type) == "Extension"))
            if (d.Target(e.Id, "extendsType") is { } ext && v.Types.TryGetValue(ext, out var t))
                foreach (var r in d.Out(e.Id, "hasProperty")) t.OwnProps[r.Target] = r.Props.Get(Ids.KProp("required")) is BooleanValue { B: true };
        return v;
    }

    /// <summary>The type and all supertypes (reflexive, transitive).</summary>
    public HashSet<string> Ancestors(string type) { lock (ancestors) return AncestorsLocked(type); }
    HashSet<string> AncestorsLocked(string type)
    {
        if (ancestors.TryGetValue(type, out var a)) return a;
        a = new HashSet<string>(StringComparer.Ordinal) { type };
        ancestors[type] = a; // guards against cycles; K6 reports them
        if (Types.TryGetValue(type, out var t)) foreach (var s in t.Supers) a.UnionWith(AncestorsLocked(s));
        return a;
    }
    public bool IsSubtype(string t, string s) => Ancestors(t).Contains(s);

    /// <summary>props_Σ(T): own, inherited and extension properties, with the required flag (required wins over optional).</summary>
    public IReadOnlyDictionary<string, bool> PropsOf(string type) { lock (allProps) return PropsOfLocked(type); }
    IReadOnlyDictionary<string, bool> PropsOfLocked(string type)
    {
        if (allProps.TryGetValue(type, out var m)) return m;
        m = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var a in Ancestors(type))
            if (Types.TryGetValue(a, out var t)) foreach (var (p, req) in t.OwnProps) m[p] = m.GetValueOrDefault(p) || req;
        allProps[type] = m;
        return m;
    }
}

/// <summary>conforms(G, O): the generic checker used for data graphs against ontologies and for definition graphs against the kernel (section 2.8).</summary>
public static class Conformance
{
    /// <summary>Checks every element of g against the ontology view. Dangling relationship endpoints are not an error (ADR-084).</summary>
    public static List<Diagnostic> Check(Graph g, OntologyView o)
    {
        var diags = new List<Diagnostic>();
        void Err(string code, string focus, string msg, string sev = "error") => diags.Add(new Diagnostic(code, sev, focus, msg));
        foreach (var e in g.Entities.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            if (!o.Types.TryGetValue(e.Type, out var t) || t.IsRel) { Err("UnknownType", e.Id, $"entity type {e.Type} is not an entity type of the ontology"); continue; }
            if (t.Abstract) { Err("AbstractType", e.Id, $"entity type {t.Name} is abstract"); continue; }
            CheckProps(e.Id, e.Props, e.Type, o, Err);
        }
        foreach (var r in g.Rels.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            if (!o.Types.TryGetValue(r.Type, out var t) || !t.IsRel) { Err("UnknownType", r.Id, $"relationship type {r.Type} is not a relationship type of the ontology"); continue; }
            if (t.Abstract) { Err("AbstractType", r.Id, $"relationship type {t.Name} is abstract"); continue; }
            CheckProps(r.Id, r.Props, r.Type, o, Err);
            Endpoint(r.Id, r.Source, t.Sources, "source");
            Endpoint(r.Id, r.Target, t.Targets, "target");
        }
        void Endpoint(string rid, string end, HashSet<string> allowed, string side)
        {
            if (allowed.Count == 0 || g.Entity(end) is not { } ent) return;
            if (!allowed.Any(a => o.IsSubtype(ent.Type, a))) Err("EndpointType", rid, $"{side} {end} has type {Ids.Name(ent.Type)}, not allowed by the relationship type");
        }
        GraphConstraints(g, o, Err);
        return diags;
    }

    static void CheckProps(string id, Props props, string type, OntologyView o, Action<string, string, string, string> err)
    {
        var allowed = o.PropsOf(type);
        foreach (var (p, v) in props.Items)
        {
            if (!allowed.ContainsKey(p) || !o.Props.TryGetValue(p, out var pi)) { err("UnknownProperty", id, $"property {p} is not declared for {Ids.Name(type)}", "error"); continue; }
            if (pi.Role == "telemetry") { err("TelemetryInGraph", id, $"telemetry property {pi.Name} cannot be graph data (WF4)", "error"); continue; }
            if (!KindOk(v, pi, o)) err("ValueKind", id, $"property {pi.Name} expects {pi.ValueKind}{(pi.ItemKind is null ? "" : "<" + pi.ItemKind + ">")}, got {v.Kind}", "error");
            foreach (var c in o.Constraints.Where(c => c.PropId == p && o.IsSubtype(type, c.TypeId))) ValueConstraint(id, pi, v, c, err);
        }
        foreach (var (p, req) in allowed)
            if (req && !props.TryGet(p, out _) && o.Props.TryGetValue(p, out var pi) && pi.Role != "telemetry")
                err("RequiredProperty", id, $"required property {pi.Name} of {Ids.Name(type)} is missing", "error");
    }

    static bool KindOk(Value v, OntologyView.PropInfo p, OntologyView o)
    {
        if (p.ValueKind == "Any") return true;
        if (v is NullValue) return p.Nullable;
        return (p.ValueKind, v) switch
        {
            ("Text", TextValue) or ("Integer", IntegerValue) or ("Decimal", DecimalValue) or ("Boolean", BooleanValue) or ("Instant", InstantValue) or ("Entity", EntityValue) => true,
            ("List", ListValue l) => p.ItemKind is null || l.Items.All(i => ItemOk(i, p.ItemKind)),
            _ => false,
        };
    }
    static bool ItemOk(Value i, string kind) => (kind, i) switch
    {
        ("Any", _) or ("Text", TextValue) or ("Integer", IntegerValue) or ("Decimal", DecimalValue) or ("Boolean", BooleanValue) or ("Instant", InstantValue) or ("Entity", EntityValue) => true,
        _ => false,
    };

    static void ValueConstraint(string id, OntologyView.PropInfo p, Value v, OntologyView.ConstraintInfo c, Action<string, string, string, string> err)
    {
        switch (c.Kind)
        {
            case "enum" when v is TextValue t && !c.Values.Contains(t.S):
                err("Enum", id, $"{p.Name} = \"{t.S}\" is not one of {string.Join(", ", c.Values)}", c.Severity); break;
            case "pattern" when v is TextValue t && c.Pattern is not null && !Regex.IsMatch(t.S, c.Pattern, RegexOptions.CultureInvariant):
                err("Pattern", id, $"{p.Name} = \"{t.S}\" does not match {c.Pattern}", c.Severity); break;
            case "range":
                var x = v switch { IntegerValue i => (decimal?)(decimal)i.N, DecimalValue d => d.Q, _ => null };
                if (x is { } xv && ((c.Min is { } mn && xv < mn) || (c.Max is { } mx && xv > mx))) err("Range", id, $"{p.Name} = {xv} is outside [{c.Min}, {c.Max}]", c.Severity);
                break;
        }
    }

    static void GraphConstraints(Graph g, OntologyView o, Action<string, string, string, string> err)
    {
        foreach (var c in o.Constraints.Where(c => c.Kind is "cardinality" or "acyclic"))
        {
            if (!o.Types.TryGetValue(c.TypeId, out var t) || !t.IsRel) continue;
            var rels = g.Rels.Where(r => o.IsSubtype(r.Type, c.TypeId)).ToList();
            if (c.Kind == "cardinality")
            {
                var by = rels.GroupBy(r => c.Side == "target" ? r.Target : r.Source).ToList();
                foreach (var grp in by)
                    if ((c.Max is { } mx && grp.Count() > mx) || (c.Min is { } mn && grp.Count() < mn))
                        err("Cardinality", grp.Key, $"{t.Name} per {c.Side ?? "source"} has {grp.Count()} relationships, expected {c.Min}..{c.Max}", c.Severity);
                if (c.Min is > 0)
                {
                    var endpoints = rels.Select(r => c.Side == "target" ? r.Target : r.Source).ToHashSet();
                    var pool = g.Entities.Where(e => o.Types.TryGetValue(e.Type, out var et) && (c.Side == "target" ? t.Targets : t.Sources) is { Count: > 0 } tt && tt.Any(a => o.IsSubtype(e.Type, a)));
                    foreach (var e in pool) if (!endpoints.Contains(e.Id)) err("Cardinality", e.Id, $"{t.Name} per {c.Side ?? "source"} has 0 relationships, expected {c.Min}..{c.Max}", c.Severity);
                }
            }
            else if (HasCycle(rels)) err("Acyclic", c.TypeId, $"relationship type {t.Name} has a cycle", c.Severity);
        }
    }

    static bool HasCycle(List<Rel> rels)
    {
        var next = rels.GroupBy(r => r.Source).ToDictionary(x => x.Key, x => x.Select(r => r.Target).ToList());
        var state = new Dictionary<string, int>();
        bool Visit(string n)
        {
            if (state.GetValueOrDefault(n) == 1) return true;
            if (state.GetValueOrDefault(n) == 2) return false;
            state[n] = 1;
            if (next.TryGetValue(n, out var ns)) foreach (var m in ns) if (Visit(m)) return true;
            state[n] = 2;
            return false;
        }
        return next.Keys.Any(Visit);
    }

    /// <summary>conforms(K, K): the kernel graph against the ontology built from the kernel graph itself.</summary>
    public static List<Diagnostic> ConformsKernelToItself(Graph? k = null)
    {
        k ??= Kernel.Graph;
        return Check(k, OntologyView.Build(k, [Ids.KernelModule]));
    }
}

/// <summary>Compiled kernel checks K1 to K3, K6, K8 to K11 (section 2.6), which operate on a definition graph generically. K4, K5, K7 are in the language front end (they need imports and releases).</summary>
public static class KernelChecks
{
    public static List<Diagnostic> Run(Graph defs)
    {
        var d = new DefView(defs);
        var diags = new List<Diagnostic>();
        void Err(string code, string focus, string msg, string sev = "error") => diags.Add(new Diagnostic(code, sev, focus, msg));
        var rootTypes = new HashSet<string> { "Module", "Release" };
        foreach (var e in defs.Entities.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            var tn = Ids.Name(e.Type);
            // K1: exactly one container
            var containers = defs.InRels(e.Id).Select(defs.Rel).Count(r => r is not null && Ids.Name(r!.Type) == "contains");
            if (!rootTypes.Contains(tn) && containers != 1) Err("K1", e.Id, $"{tn} must have exactly one container, found {containers}");
            if (rootTypes.Contains(tn) && containers != 0) Err("K1", e.Id, $"{tn} must not be contained");
            // K9: operator port set
            if (KernelTable.IsOperator(tn))
            {
                var want = KernelTable.OperatorPorts(tn).Select(p => p.Name).OrderBy(x => x).ToArray();
                var have = d.Children(e.Id, "port").Select(Ids.Name).OrderBy(x => x).ToArray();
                if (!want.SequenceEqual(have)) Err("K9", e.Id, $"{tn} must have ports [{string.Join(", ", want)}], has [{string.Join(", ", have)}]");
                else
                    foreach (var (name, dir, kind) in KernelTable.OperatorPorts(tn))
                    {
                        var pid = Ids.Child(e.Id, "port", name);
                        if (d.Text(pid, "direction") != dir || d.Text(pid, "kind") != kind) Err("K9", pid, $"port {name} of {tn} must be {dir} {kind}");
                    }
            }
            // K10 / K11: telemetry properties
            if (tn == "PropertyType")
            {
                var role = d.Text(e.Id, "role");
                var tele = role == "telemetry";
                foreach (var f in new[] { "temporality", "combine", "period" })
                    if ((d.Prop(e.Id, f) is not null) != tele) Err("K11", e.Id, $"{f} must be present iff role is telemetry");
                if (tele)
                {
                    var vk = Ids.Name(d.Target(e.Id, "valueKind") ?? "");
                    if (vk is not ("Integer" or "Decimal")) Err("K11", e.Id, "telemetry value kind must be Integer or Decimal");
                    if (d.Text(e.Id, "combine") == "sum" && d.Text(e.Id, "temporality") == "cumulative") Err("K11", e.Id, "combine sum is not allowed with temporality cumulative (use Delta first)");
                }
            }
        }
        // K2/K3: slot typing, sibling names, ordinals
        var slots = KernelTable.Slots.GroupBy(s => s.Container).ToDictionary(g => g.Key, g => g.ToDictionary(s => s.Name));
        foreach (var e in defs.Entities.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            var tn = Ids.Name(e.Type);
            var kids = d.Out(e.Id, "contains").ToList();
            var applicable = new Dictionary<string, KernelTable.Slot>();
            foreach (var anc in Lineage(tn)) if (slots.TryGetValue(anc, out var sm)) foreach (var (n, s) in sm) applicable[n] = s;
            foreach (var grp in kids.GroupBy(DefView.SlotOf))
            {
                if (!applicable.TryGetValue(grp.Key, out var slot)) { Err("K2", e.Id, $"{tn} has no slot {grp.Key}"); continue; }
                var names = new HashSet<string>();
                foreach (var k in grp)
                {
                    var ct = d.TypeName(k.Target);
                    if (!slot.Children.Any(c => KernelTable.Extends(ct, c) || c == ct)) Err("K2", k.Target, $"slot {slot.Name} of {tn} does not admit {ct}");
                    if (!names.Add(Ids.Name(k.Target))) Err("K3", k.Target, $"duplicate name {Ids.Name(k.Target)} in slot {slot.Name}");
                }
                var n = grp.Count();
                if ((slot.Mult == "1" && n != 1) || (slot.Mult == "?" && n > 1)) Err("K2", e.Id, $"slot {slot.Name} of {tn} takes {(slot.Mult == "1" ? "exactly one" : "at most one")} child, found {n}");
                if (slot.Ordered && !grp.Select(DefView.OrdOf).Order().SequenceEqual(Enumerable.Range(0, n))) Err("K3", e.Id, $"ordinals of ordered slot {slot.Name} are not dense from 0");
            }
            foreach (var (n, s) in applicable)
                if ((s.Mult is "1" or "+") && !kids.Any(k => DefView.SlotOf(k) == n)) Err("K2", e.Id, $"{tn} requires at least one child in slot {n}");
        }
        // K6: extends is acyclic and within one metatype
        foreach (var e in defs.Entities.Where(e => Ids.Name(e.Type) is "EntityType" or "RelationshipType"))
        {
            var seen = new HashSet<string> { e.Id };
            var frontier = d.Targets(e.Id, "extends").ToList();
            while (frontier.Count > 0)
            {
                var n = frontier[^1]; frontier.RemoveAt(frontier.Count - 1);
                if (d.TypeName(n) != Ids.Name(e.Type)) Err("K6", e.Id, "extends crosses metatypes");
                if (n == e.Id) { Err("K6", e.Id, "extends is cyclic"); break; }
                if (seen.Add(n)) frontier.AddRange(d.Targets(n, "extends"));
            }
        }
        return diags;
    }

    static IEnumerable<string> Lineage(string t)
    {
        var chain = new List<string>();
        for (var x = t; x is not null; x = KernelTable.ByName.GetValueOrDefault(x)?.Super) chain.Add(x);
        chain.Reverse();
        return chain;
    }
}
