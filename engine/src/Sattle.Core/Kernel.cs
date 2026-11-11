using System.Collections.Immutable;
using System.Text;

namespace Sattle.Core;

/// <summary>SHACL-like diagnostic (research 03 section 1, section 9): focus element, rule, severity, message and an optional source position.</summary>
public sealed record Diagnostic(string Code, string Severity, string Focus, string Message, string? File = null, int Line = 0, int Col = 0)
{
    public override string ToString() => (File is null ? "" : $"{File}:{Line}:{Col}: ") + $"{Severity} {Code}: {Message}" + (Focus.Length > 0 ? $" [{Focus}]" : "");
    public bool IsError => Severity == "error";
}

/// <summary>Id scheme of research 03 section 2.2. The kernel is module sattle.kernel.</summary>
public static class Ids
{
    public const string KernelModule = "sattle.kernel";
    public static string Module(string m) => "d:" + m;
    public static string Child(string parent, string slot, string name) => parent + "/" + slot + ":" + name;
    public static string Rel(string source, string edgeName, string target) => source + "|" + edgeName + "|" + target;
    public static string ModuleOf(string id)
    {
        var s = id.StartsWith("d:", StringComparison.Ordinal) ? id[2..] : id;
        var cut = s.IndexOfAny(['/', '@', '|']);
        return cut < 0 ? s : s[..cut];
    }
    public static string K(string slot, string name) => Child(Module(KernelModule), slot, name);
    public static string KEntity(string name) => K("entity", name);
    public static string KRel(string name) => K("rel", name);
    public static string KProp(string name) => K("prop", name);
    public static string KBuiltin(string name) => K("builtin", name);
    /// <summary>A value as one id segment (no slash, colon or tilde in text).</summary>
    public static string ValueSegment(Value v) => v switch
    {
        TextValue t => Uri.EscapeDataString(t.S).Replace("~", "%7E").Replace(":", "%3A"),
        IntegerValue i => "~i" + i.N.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DecimalValue d => "~d" + Norm.Decimal(d.Q),
        BooleanValue b => b.B ? "~btrue" : "~bfalse",
        _ => "~null",
    };
    /// <summary>Last path segment name of a definition id, e.g. d:m/entity:Pod gives Pod.</summary>
    public static string Name(string id) { var i = id.LastIndexOf(':'); return i < 0 ? id : id[(i + 1)..]; }
}

public sealed record PropSpec(string Name, string Kind, bool Required, bool Edge = false);
public sealed record TypeSpec(string Name, bool Abstract, string? Super, PropSpec[] Props);
public sealed record EdgeSpec(string Name, string[] Sources, string[] Targets, PropSpec[] Props);
public sealed record FunctionSpec(string Name, string Signature, string Role, string? Inverse)
{
    /// <summary>Scalar and canonicalizer functions of one name are distinct nodes: canonicalizers live under canon.&lt;name&gt;.</summary>
    public string IdName => Role == "canonicalizer" ? "canon." + Name : Name;
}

/// <summary>The kernel table of research 03 sections 2.3 to 2.7, compiled into the engine (section 2.8).</summary>
public static class KernelTable
{
    static PropSpec[] P(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x =>
    {
        var n = x[..x.IndexOf(':')]; var k = x[(x.IndexOf(':') + 1)..];
        var opt = k.EndsWith('?'); if (opt) k = k[..^1];
        return new PropSpec(n, k, !opt);
    }).ToArray();

    static TypeSpec T(string spec)
    {
        var abs = spec.StartsWith('!'); if (abs) spec = spec[1..];
        var bar = spec.IndexOf('|');
        var head = spec[..bar].Trim(); var props = spec[(bar + 1)..];
        var colon = head.IndexOf(':');
        return new TypeSpec(colon < 0 ? head : head[..colon].Trim(), abs, colon < 0 ? null : head[(colon + 1)..].Trim(), P(props));
    }

    public static readonly TypeSpec[] Types =
    [
        T("!Declaration | name:T description:T?"), T("!Operator | name:T"), T("!Expr |"),
        T("Module | name:T description:T?"), T("Release | version:T digest:T revision:T status:T"),
        T("EntityType : Declaration | abstract:B"), T("RelationshipType : Declaration | abstract:B hierarchy:B?"),
        T("PropertyType : Declaration | nullable:B unit:T? role:T temporality:T? combine:T? period:I? treeRole:T?"), T("ValueRole | value:Any role:T"),
        T("Extension : Declaration |"),
        T("Constraint : Declaration | kind:T severity:T values:L? min:D? max:D? pattern:T? side:T?"),
        T("ValueKind | name:T"),
        T("IdentityDomain : Declaration | canonicalizationVersion:T"), T("KeyComponent | name:T"),
        T("EquivalencePolicy : Declaration | mode:T threshold:D"), T("RowType : Declaration |"), T("Field | name:T optional:B unit:T?"),
        T("Projection : Declaration | purpose:T retain:I forward:B"), T("Port | name:T direction:T kind:T default:Any?"),
        T("Rule | name:T class:T putput:B reason:T? accept:L? onDelete:T? sourceVar:T? targetVar:T?"),
        T("Match : Operator |"), T("Filter : Operator |"), T("Map : Operator |"), T("Join : Operator |"),
        T("Union : Operator | conflict:T"), T("Distinct : Operator |"), T("Group : Operator |"),
        T("Traverse : Operator | direction:T minDepth:I maxDepth:I start:T target:T"), T("Window : Operator | timestampField:T width:I late:T"), T("Retain : Operator | timestampField:T age:I"),
        T("Fixpoint : Operator | maxFacts:I maxIterations:I"), T("Values : Operator |"),
        T("EmitEntity : Operator | mode:T"), T("EmitRelationship : Operator | mode:T key:T retype:T"), T("Link : Operator |"),
        T("Series : Operator | within:I"), T("Rollup : Operator |"), T("Delta : Operator | target:T"),
        T("Threshold : Operator | target:T above:D enterAfter:I high:T below:D exitAfter:I low:T initial:T"),
        T("Lift : Operator | within:I images:T? selfLoops:T"),
        T("LiftState | target:T of:T above:D enterAfter:I high:T below:D exitAfter:I low:T initial:T"),
        T("PatternNode | var:T side:T? create:B?"), T("PatternEdge | var:T? minHops:I? maxHops:I? side:T? create:B?"),
        T("FieldRef : Expr | path:T"), T("Literal : Expr | value:Any"), T("Call : Expr |"), T("IdOf : Expr |"),
        T("Assign | target:T"), T("Aggregate | target:T fn:T"), T("SetProperty |"), T("SetComponent |"), T("RowValue |"),
        T("Function | name:T signature:T role:T inverse:T?"),
        T("GraphSpace : Declaration |"), T("Graph | name:T"), T("Source | name:T adapter:T"), T("Binding | value:Any?"),
        // system vocabulary, section 2.7
        T("Key | domain:T canonicalizationVersion:T components:L digest:T"), T("Identity | domain:T"),
        T("Evidence | rule:T confidence:D inputs:L decision:T"), T("!Correspondence | rule:T ruleVersion:T sourceRefs:L targetRefs:L"),
        T("NoType |"),
    ];

    static EdgeSpec E(string spec)
    {
        // name : A B -> C D | props
        var bar = spec.IndexOf('|');
        var head = bar < 0 ? spec : spec[..bar];
        var props = bar < 0 ? [] : P(spec[(bar + 1)..]).Select(p => p with { Edge = true }).ToArray();
        var colon = head.IndexOf(':'); var arrow = head.IndexOf("->", StringComparison.Ordinal);
        string[] L(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x != "*").ToArray();
        return new EdgeSpec(head[..colon].Trim(), L(head[(colon + 1)..arrow]), L(head[(arrow + 2)..]), props);
    }

    public static readonly EdgeSpec[] Edges =
    [
        E("contains : * -> * | slot:T ordinal:I?"), E("imports : Module -> Release | alias:T"), E("releaseOf : Release -> Module"),
        E("supersedes : Release -> Release"), E("extends : EntityType RelationshipType -> EntityType RelationshipType"),
        E("extendsType : Extension -> EntityType RelationshipType"),
        E("hasProperty : EntityType RelationshipType Extension -> PropertyType | required:B"),
        E("sourceType : RelationshipType -> EntityType"), E("targetType : RelationshipType -> EntityType"),
        E("valueKind : PropertyType Field KeyComponent Port -> ValueKind"), E("itemKind : PropertyType Field -> ValueKind"),
        E("refersTo : PropertyType Field -> EntityType"), E("constrains : Constraint -> EntityType RelationshipType"),
        E("onProperty : Constraint -> PropertyType | ordinal:I"), E("identifies : IdentityDomain -> EntityType"),
        E("canonicalizer : KeyComponent -> Function"), E("between : EquivalencePolicy -> IdentityDomain | ordinal:I"),
        E("rowType : Port -> RowType"), E("expects : Port -> Module"), E("outputOntology : Projection -> Module"), E("wire : Port -> Port"),
        E("ofType : PatternNode EmitEntity PatternEdge EmitRelationship Traverse Lift -> EntityType RelationshipType"),
        E("liftEdge : Lift -> RelationshipType"), E("liftVia : Lift -> RelationshipType"),
        E("from : PatternEdge -> PatternNode"), E("to : PatternEdge -> PatternNode"), E("domain : IdOf -> IdentityDomain"),
        E("component : SetComponent -> KeyComponent"), E("property : SetProperty -> PropertyType"), E("function : Call -> Function"),
        E("policy : Link -> EquivalencePolicy"), E("over : Fixpoint -> Port"), E("governs : Rule -> EmitEntity EmitRelationship Link Lift"),
        E("createKey : Rule -> IdentityDomain"), E("master : GraphSpace -> Graph"), E("producedBy : Graph -> Projection"),
        E("provides : Source -> RowType"), E("bindsPort : Binding -> Port"), E("boundFrom : Binding -> Graph Source"),
        E("boundTo : Key -> Identity"), E("sameAs : Key -> Key | evidence:Entity"), E("mergedInto : Identity -> Identity | evidence:Entity"),
        E("splitFrom : Identity -> Identity | evidence:Entity"), E("left : Evidence -> Key"), E("right : Evidence -> Key"),
        E("NoType : * -> *"),
    ];

    public static readonly string[] ValueKinds = ["Text", "Integer", "Decimal", "Boolean", "Instant", "Entity", "List", "Any"];

    /// <summary>Enumerated kernel properties (K8): "Type.prop" to allowed values.</summary>
    public static readonly (string Type, string Prop, string[] Values)[] Enums =
    [
        ("Release", "status", ["draft", "active", "deprecated", "withdrawn"]),
        ("PropertyType", "role", ["graphData", "telemetry"]),
        ("PropertyType", "temporality", ["gauge", "delta", "cumulative"]),
        ("PropertyType", "combine", ["sum", "min", "max", "mean", "last"]),
        ("Constraint", "kind", ["key", "enum", "range", "pattern", "cardinality", "acyclic"]),
        ("Constraint", "severity", ["error", "warning"]),
        ("Constraint", "side", ["source", "target"]),
        ("EquivalencePolicy", "mode", ["assert", "review"]),
        ("Projection", "purpose", ["ingestion", "view", "migration", "verification"]),
        ("Port", "direction", ["in", "out"]),
        ("Port", "kind", ["rows", "graph", "param"]),
        ("Rule", "class", ["bijective", "lens", "sync", "readonly"]),
        ("Rule", "onDelete", ["remove", "set"]),
        ("ValueRole", "role", ["ok", "warning", "danger", "unknown", "neutral"]),
        ("Union", "conflict", ["requireEqual", "preferFirst", "preferLast", "preserveEvidence"]),
        ("Traverse", "direction", ["out", "in", "either"]),
        ("Lift", "images", ["reject", "each"]),
        ("Lift", "selfLoops", ["drop", "keep"]),
        ("Window", "late", ["ignore", "revise", "reject"]),
        ("EmitEntity", "mode", ["create", "update"]),
        ("EmitRelationship", "mode", ["create", "update"]),
        ("EmitRelationship", "retype", ["reject", "keepExisting", "takeNew"]),
        ("Aggregate", "fn", ["count", "countDistinct", "sum", "min", "max", "any", "all", "mean", "weightedMean", "last"]),
        ("PatternNode", "side", ["source", "target"]),
        ("PatternEdge", "side", ["source", "target"]),
        ("Function", "role", ["scalar", "canonicalizer"]),
        ("Evidence", "decision", ["proposed", "accepted", "rejected"]),
    ];

    public static readonly FunctionSpec[] Functions =
    [
        new("Equal", "(a, a) -> Boolean", "scalar", null), new("NotEqual", "(a, a) -> Boolean", "scalar", null),
        new("LessThan", "(o, o) -> Boolean", "scalar", null), new("LessOrEqual", "(o, o) -> Boolean", "scalar", null),
        new("GreaterThan", "(o, o) -> Boolean", "scalar", null), new("GreaterOrEqual", "(o, o) -> Boolean", "scalar", null),
        new("And", "(Boolean, Boolean, ...) -> Boolean", "scalar", null), new("Or", "(Boolean, Boolean, ...) -> Boolean", "scalar", null),
        new("Not", "Boolean -> Boolean", "scalar", "Not"),
        new("Add", "(n, n) -> n", "scalar", null), new("Subtract", "(n, n) -> n", "scalar", null),
        new("Multiply", "(n, m) -> n*m", "scalar", null), new("Divide", "(n, m) -> Decimal", "scalar", null),
        new("Negate", "n -> n", "scalar", "Negate"), new("ToDecimal", "Integer -> Decimal", "scalar", "ToInteger"),
        new("Concat", "(Text, Text, ...) -> Text", "scalar", null), new("Coalesce", "(a, ..., a) -> a", "scalar", null),
        new("IsPresent", "a -> Boolean", "scalar", null), new("If", "(Boolean, a, a) -> a", "scalar", null),
        new("Rate", "n(delta) -> Decimal(gauge)", "scalar", null), new("InList", "(a, List<a>) -> Boolean", "scalar", null),
        new("LowerInvariant", "Text -> Text", "scalar", null), new("Trim", "Text -> Text", "scalar", null), new("EntityId", "Entity -> Text", "scalar", null),
        new("Exact", "Text -> Text", "canonicalizer", null), new("LowerInvariant", "Text -> Text", "canonicalizer", null), new("Trim", "Text -> Text", "canonicalizer", null),
        new("IpCanonical", "Text -> Text", "canonicalizer", null), new("UuidLower", "Text -> Text", "canonicalizer", null),
    ];

    /// <summary>The slot table (K2): container type, slot, allowed child types, multiplicity (1, ?, *, +), ordered.</summary>
    public sealed record Slot(string Container, string Name, string[] Children, string Mult, bool Ordered);
    static Slot S(string c, string n, string kids, string m, bool ord = false) => new(c, n, kids.Split(','), m, ord);
    public static readonly Slot[] Slots =
    [
        S("Module", "entity", "EntityType", "*"), S("Module", "rel", "RelationshipType", "*"), S("Module", "prop", "PropertyType", "*"),
        S("Module", "extend", "Extension", "*"), S("Module", "constraint", "Constraint", "*"), S("Module", "identity", "IdentityDomain", "*"),
        S("Module", "equivalence", "EquivalencePolicy", "*"), S("Module", "rows", "RowType", "*"), S("Module", "projection", "Projection", "*"),
        S("Module", "space", "GraphSpace", "*"), S("Module", "builtin", "ValueKind,Function", "*"),
        S("PropertyType", "presentation", "ValueRole", "*"), S("IdentityDomain", "component", "KeyComponent", "+", true), S("RowType", "field", "Field", "*", true),
        S("Projection", "port", "Port", "*"), S("Projection", "op", "Operator", "*"), S("Projection", "rule", "Rule", "*"),
        S("Rule", "createDefault", "SetProperty", "*"), S("Rule", "deleteSet", "SetProperty", "*"), S("Rule", "node", "PatternNode", "*"),
        S("Rule", "edge", "PatternEdge", "*"), S("Rule", "id", "IdOf", "?"), S("Rule", "equate", "Expr", "*", true), S("Rule", "keep", "FieldRef", "*", true),
        S("Operator", "port", "Port", "*"),
        S("Match", "node", "PatternNode", "+"), S("Match", "edge", "PatternEdge", "*"),
        S("Filter", "predicate", "Expr", "1"), S("Map", "assign", "Assign", "+", true), S("Join", "on", "Expr", "1"),
        S("Group", "key", "Assign", "+"), S("Group", "agg", "Aggregate", "*"),
        S("Fixpoint", "relation", "RowType", "?"), S("Fixpoint", "op", "Operator", "*"), S("Values", "row", "RowValue", "+", true),
        S("EmitEntity", "id", "Expr", "1"), S("EmitEntity", "set", "SetProperty", "*"),
        S("EmitRelationship", "source", "Expr", "1"), S("EmitRelationship", "target", "Expr", "1"), S("EmitRelationship", "set", "SetProperty", "*"),
        S("Link", "left", "IdOf", "1"), S("Link", "right", "IdOf", "1"), S("Link", "confidence", "Expr", "1"),
        S("Series", "read", "FieldRef", "+", true), S("Rollup", "key", "Assign", "+"), S("Rollup", "agg", "Aggregate", "+"),
        S("Delta", "key", "Assign", "+"), S("Delta", "value", "Expr", "1"), S("Threshold", "key", "Assign", "+"), S("Threshold", "value", "Expr", "1"), S("Threshold", "aboveBy", "Expr", "?"), S("Threshold", "enterBy", "Expr", "?"), S("Threshold", "belowBy", "Expr", "?"), S("Threshold", "exitBy", "Expr", "?"),
        S("Lift", "carry", "Assign", "*", true), S("Lift", "derive", "Assign", "*", true), S("Lift", "keep", "Assign", "*", true), S("Lift", "when", "Expr", "?"), S("Lift", "state", "LiftState", "*", true),
        S("Call", "arg", "Expr", "*", true), S("IdOf", "component", "SetComponent", "+"), S("Assign", "value", "Expr", "1"),
        S("Aggregate", "value", "Expr", "?"), S("Aggregate", "weight", "Expr", "?"), S("Aggregate", "order", "Expr", "?"),
        S("SetProperty", "value", "Expr", "1"), S("SetComponent", "value", "Expr", "1"), S("RowValue", "assign", "Assign", "+"),
        S("GraphSpace", "graph", "Graph", "+"), S("GraphSpace", "source", "Source", "*"), S("GraphSpace", "binding", "Binding", "*"),
    ];

    /// <summary>Kernel-fixed port set per operator kind (K9): name, direction, kind.</summary>
    public static (string Name, string Direction, string Kind)[] OperatorPorts(string op) => op switch
    {
        "Match" => [("graph", "in", "graph"), ("out", "out", "rows")],
        "Filter" or "Map" or "Distinct" or "Retain" or "Group" or "Window" or "Rollup" or "Delta" or "Threshold" => [("in", "in", "rows"), ("out", "out", "rows")],
        "Join" => [("left", "in", "rows"), ("right", "in", "rows"), ("out", "out", "rows")],
        "Traverse" => [("graph", "in", "graph"), ("in", "in", "rows"), ("out", "out", "rows")],
        "Fixpoint" => [("seed", "in", "rows"), ("out", "out", "rows")],
        "Values" => [("out", "out", "rows")],
        "EmitEntity" or "EmitRelationship" or "Link" => [("in", "in", "rows")],
        "Series" => [("graph", "in", "graph"), ("in", "in", "rows"), ("out", "out", "rows")],
        "Union" => [("in0", "in", "rows"), ("in1", "in", "rows"), ("out", "out", "rows")],
        "Lift" => [("edges", "in", "graph"), ("map", "in", "graph"), ("out", "out", "rows")],
        _ => [],
    };

    public static bool IsOperator(string type) => OperatorSet.Contains(type);
    public static readonly HashSet<string> OperatorSet = Types.Where(t => t.Super == "Operator").Select(t => t.Name).ToHashSet();
    public static readonly HashSet<string> ExprSet = Types.Where(t => t.Super == "Expr").Select(t => t.Name).ToHashSet();
    public static readonly Dictionary<string, TypeSpec> ByName = Types.ToDictionary(t => t.Name);

    public static bool Extends(string type, string super)
    {
        for (var t = type; t is not null; t = ByName.GetValueOrDefault(t)?.Super) if (t == super) return true;
        return false;
    }
}

/// <summary>Builds definition-graph facts (nodes, containment, references) with the id scheme of section 2.2. Used by the kernel and by the language front end.</summary>
public sealed class DefBuilder
{
    readonly Dictionary<string, Entity> nodes = new(StringComparer.Ordinal);
    readonly Dictionary<string, Rel> rels = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> ordinals = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, Entity> Nodes => nodes;

    public string Node(string id, string kernelType, params (string Prop, Value Val)[] props)
    {
        var p = Props.Of(props.Select(x => KeyValuePair.Create(Ids.KProp(x.Prop), x.Val)));
        nodes[id] = new Entity(id, Ids.KEntity(kernelType), p);
        return id;
    }

    public void Edge(string source, string edgeName, string target, params (string Prop, Value Val)[] props)
    {
        var id = Ids.Rel(source, edgeName, target);
        rels[id] = new Rel(id, source, target, Ids.KRel(edgeName), Props.Of(props.Select(x => KeyValuePair.Create(Ids.KProp(x.Prop), x.Val))));
    }

    /// <summary>Contains edge with a slot name; ordered children pass an ordinal.</summary>
    public string Contain(string parent, string slot, string name, string childType, int? ordinal = null, params (string Prop, Value Val)[] props)
    {
        var id = Ids.Child(parent, slot, name);
        Node(id, childType, props);
        var ps = new List<(string, Value)> { ("slot", Value.Text(slot)) };
        if (ordinal is { } o) ps.Add(("ordinal", Value.Integer(o)));
        Edge(parent, "contains", id, [.. ps]);
        return id;
    }

    public IEnumerable<Element> Elements() => nodes.Values.Cast<Element>().Concat(rels.Values);
    public Graph ToGraph()
    {
        var g = new Graph();
        var err = g.Apply([.. Elements().Select(Edit.Create)]);
        return err is null ? g : throw new InvalidOperationException(err);
    }
}

public static class Kernel
{
    public const string Version = "1.0.0";
    public static string ModuleId => Ids.Module(Ids.KernelModule);

    static Value T(string s) => Value.Text(s);

    static Graph? cached;
    /// <summary>Graph K: module sattle.kernel encoded as ontology data (section 2.8, step 1). Deterministic.</summary>
    public static Graph Graph => cached ??= Build().ToGraph();

    static DefBuilder Build()
    {
        var b = new DefBuilder();
        var m = ModuleId;
        b.Node(m, "Module", ("name", T(Ids.KernelModule)), ("description", T("Kernel meta-ontology. Compiled into the engine.")));
        // value kinds and functions
        foreach (var k in KernelTable.ValueKinds) b.Contain(m, "builtin", k, "ValueKind", null, ("name", T(k)));
        foreach (var f in KernelTable.Functions)
        {
            var props = new List<(string, Value)> { ("name", T(f.Name)), ("signature", T(f.Signature)), ("role", T(f.Role)) };
            if (f.Inverse is not null) props.Add(("inverse", T(f.Inverse)));
            b.Contain(m, "builtin", f.IdName, "Function", null, [.. props]);
        }
        // property types, one per (name, kind); a name must keep one kind in the kernel
        var propKinds = new Dictionary<string, string>();
        foreach (var p in KernelTable.Types.SelectMany(t => t.Props).Concat(KernelTable.Edges.SelectMany(e => e.Props)))
            if (propKinds.TryGetValue(p.Name, out var old) && old != p.Kind) throw new InvalidOperationException($"kernel property {p.Name} has two kinds {old}, {p.Kind}");
            else propKinds[p.Name] = p.Kind;
        foreach (var (name, kind) in propKinds)
        {
            var id = b.Contain(m, "prop", name, "PropertyType", null,
                ("name", T(name)), ("description", T($"kernel property {name}")), ("nullable", Value.Boolean(false)), ("role", T("graphData")));
            b.Edge(id, "valueKind", Ids.KBuiltin(kind switch { "T" => "Text", "I" => "Integer", "D" => "Decimal", "B" => "Boolean", "L" => "List", "Any" => "Any", var x => x }));
            if (kind == "L") b.Edge(id, "itemKind", Ids.KBuiltin("Text"));
        }
        // entity types
        foreach (var t in KernelTable.Types)
        {
            var id = b.Contain(m, "entity", t.Name, "EntityType", null, ("name", T(t.Name)), ("description", T($"kernel type {t.Name}")), ("abstract", Value.Boolean(t.Abstract)));
            if (t.Super is not null) b.Edge(id, "extends", Ids.KEntity(t.Super));
            foreach (var p in t.Props) b.Edge(id, "hasProperty", Ids.KProp(p.Name), ("required", Value.Boolean(p.Required)));
        }
        // relationship types
        foreach (var e in KernelTable.Edges)
        {
            var id = b.Contain(m, "rel", e.Name, "RelationshipType", null, ("name", T(e.Name)), ("description", T($"kernel edge {e.Name}")), ("abstract", Value.Boolean(false)));
            foreach (var s in e.Sources) b.Edge(id, "sourceType", Ids.KEntity(s));
            foreach (var t in e.Targets) b.Edge(id, "targetType", Ids.KEntity(t));
            foreach (var p in e.Props) b.Edge(id, "hasProperty", Ids.KProp(p.Name), ("required", Value.Boolean(p.Required)));
        }
        // enumerations (K8) as constraints
        foreach (var (type, prop, values) in KernelTable.Enums)
        {
            var name = $"enum-{type}.{prop}";
            var id = b.Contain(m, "constraint", name, "Constraint", null,
                ("name", T(name)), ("description", T("enumeration")), ("kind", T("enum")), ("severity", T("error")), ("values", Value.List(values.Select(T))));
            b.Edge(id, "constrains", Ids.KEntity(type));
            b.Edge(id, "onProperty", Ids.KProp(prop), ("ordinal", Value.Integer(0)));
        }
        return b;
    }

    /// <summary>Digest of the kernel graph. The compiled constant is checked by a test (section 2.8, step 2).</summary>
    public static string ComputeDigest() => Graph.Digest();
    public const string CompiledDigest = "sha256:5470d105efa32d95dabed98df408abf30c79bfa72bd8eb424c0a959e7e520bb8";
}
