using System.Text.Json.Nodes;
using Sattle.Core;

namespace Sattle.Runtime;

/// <summary>What the engine can evaluate, against what the kernel declares (research 03 section 2). The palette is derived from the kernel table and this list.</summary>
public static class Support
{
    public static readonly Dictionary<string, string> Operators = new()
    {
        ["Match"] = "bilinear delta against the input graph",
        ["Filter"] = "linear: selection vector",
        ["Map"] = "linear: shared columns",
        ["Window"] = "linear: window assignment",
        ["Retain"] = "per-row buffer by event time: a row is withdrawn (negative weight) when the logical frontier passes its time plus the age; a row already older than that on arrival never passes",
        ["Group"] = "per-key accumulators with signed weights; closing windows when keyed by windowStart",
        ["Series"] = "bilinear: new rows against seen windows, new windows against all rows",
        ["Rollup"] = "per (key, window) accumulators",
        ["Threshold"] = "per-key state machine over ordered closed windows",
        ["Values"] = "constant rows, emitted once when the graph first evaluates",
        ["Join"] = "bilinear delta over per-side hash indexes on the equality keys",
        ["Traverse"] = "per start element the depth each element was first reached at; an added edge lowers depths from its far end, a lost edge walks the starts that visited its ends again, an edited entity touches the starts that visited it; emits the difference",
        ["Union"] = "linear: concatenation of both inputs' signed rows",
        ["Distinct"] = "per-row multiplicity counts, set semantics over signed rows",
        ["EmitEntity"] = "support-counted element layer",
        ["EmitRelationship"] = "support-counted element layer",
        ["Link"] = "identity binding",
    };
    public static readonly Dictionary<string, string> Unsupported = new()
    {
        ["Delta"] = "not built: no property in the definitions or the captures is cumulative (sources arrive as gauges or deltas), so it would ship without real data to test against",
    };
    /// <summary>Where an aggregate may be used, as the checker decides it: a `Group` aggregates data columns, a `Rollup` aggregates telemetry values (and is limited to the aggregates that combine windows). An aggregate in a context not listed is a typed error at that position.</summary>
    public sealed record AggregateContext(string Operator, string Inputs, string Output, string? Requires = null);
    public static readonly Dictionary<string, AggregateContext[]> AggregateContexts = new()
    {
        ["count"] = [new("Group", "no argument", "Integer"), new("Rollup", "no argument", "Integer")],
        ["countDistinct"] = [new("Group", "any data value", "Integer")],
        ["sum"] = [new("Group", "numeric data value", "same as the value"), new("Rollup", "numeric telemetry value", "same as the value", "not cumulative")],
        ["min"] = [new("Group", "numeric data value", "same as the value"), new("Rollup", "numeric telemetry value", "same as the value", "not cumulative")],
        ["max"] = [new("Group", "numeric data value", "same as the value"), new("Rollup", "numeric telemetry value", "same as the value", "not cumulative")],
        ["mean"] = [new("Group", "numeric data value", "Decimal"), new("Rollup", "numeric telemetry value", "Decimal", "gauge")],
        ["last"] = [new("Group", "data value (not an element) and an ordering of data Integer, Decimal or Instant, marked with its row: the last aggregates of one row take their values from it (arg_max)", "same as the value")],
    };
    public static readonly HashSet<string> Aggregates = [.. AggregateContexts.Keys];
    /// <summary>Constraint kinds: the entity or relationship type they constrain, the Constraint properties they use, and the text form.</summary>
    public static readonly Dictionary<string, (string Target, string[] Uses, string Text)> ConstraintKinds = new()
    {
        ["key"] = ("entity", ["onProperty"], "constraint key T(p1, p2);"),
        ["enum"] = ("entity or relationship property", ["onProperty", "values"], "constraint enum T.p in [\"a\", \"b\"];"),
        ["range"] = ("entity or relationship property", ["onProperty", "min", "max"], "constraint range T.p 0 .. 100;"),
        ["pattern"] = ("entity or relationship property", ["onProperty", "pattern"], "constraint pattern T.p \"regex\";"),
        ["cardinality"] = ("relationship", ["side", "min", "max"], "constraint cardinality R per source|target 0 .. 1|*;"),
        ["acyclic"] = ("relationship", [], "constraint acyclic R;"),
    };
    /// <summary>Edit policies of an editable rule, as settings of a step: `with (rule = "...", ...)`.</summary>
    public static readonly Dictionary<string, string> RulePolicies = new()
    {
        ["accept = \"create, delete\", update = \"p, q\""] = "which view edits are accepted and which properties may change",
        ["onCreate = \"f\""] = "a function whose parameters are the key of a created element and whose row gives the properties the view does not show",
        ["onDelete = \"f\""] = "deleting retires the source element by setting the properties the function's row gives",
        ["accept = \"delete\" without onDelete"] = "deleting removes the source element",
    };
    public static readonly HashSet<string> Functions =
    [
        "Equal", "NotEqual", "LessThan", "LessOrEqual", "GreaterThan", "GreaterOrEqual", "And", "Or", "Not", "Add", "Subtract", "Shift", "Multiply", "Divide", "Negate",
        "ToDecimal", "Concat", "Coalesce", "IsPresent", "If", "Rate", "LowerInvariant", "Trim", "EntityId",
    ];
    public static readonly Dictionary<string, string> RuleClasses = new() { ["readonly"] = "", ["bijective"] = "", ["lens"] = "putput", ["sync"] = "" };
    /// <summary>What the children of a slot hold, for an editor: `fieldRef` (a path), `expr` (an expression), with the roots a path may start with and where the offered properties come from.</summary>
    public sealed record SlotValue(string Kind, string[] Roots, string Offers);
    public static readonly Dictionary<string, SlotValue> SlotValues = new()
    {
        ["Series.read"] = new("fieldRef", [], "var.property of the pattern variables of the graph the series reads"),
    };
    /// <summary>What a reference of a definition node must name, for an editor (`Type.edge`; the kernel's edge table gives the edge's name and target types). `Scope` says where the target is looked up: `outputOntology` (the projection's output ontology), `inputGraph` (the ontology of the graph port the operator reads), `module` (a declaration in scope of the module: this one or an import), `pattern` (a node of the same pattern), `domain` (a component of the identity domain the node names), `emittedType` (a property of the type the enclosing emit writes), `constrainedType` (a property of the type the constraint names), `kernel` (a built-in). The checker refuses a node whose required reference is missing or out of scope.</summary>
    public sealed record Reference(bool Required, string Multiplicity, string Scope, string Meaning);
    public static readonly Dictionary<string, Reference> References = new()
    {
        ["EmitEntity.ofType"] = new(true, "1", "outputOntology", "the entity type the sink writes (not abstract)"),
        ["EmitRelationship.ofType"] = new(true, "1", "outputOntology", "the relationship type the sink writes (not abstract)"),
        ["Traverse.ofType"] = new(true, "1", "inputGraph", "the relationship type the traversal follows"),
        ["Link.policy"] = new(true, "1", "module", "the equivalence policy whose two identity domains the link binds"),
        ["PatternNode.ofType"] = new(true, "1", "inputGraph", "the entity type the pattern variable ranges over"),
        ["PatternEdge.ofType"] = new(true, "1", "inputGraph", "the relationship type of the pattern edge"),
        ["PatternEdge.from"] = new(true, "1", "pattern", "the pattern node the edge starts at"),
        ["PatternEdge.to"] = new(true, "1", "pattern", "the pattern node the edge ends at"),
        ["IdOf.domain"] = new(true, "1", "module", "the identity domain that computes the id"),
        ["SetComponent.component"] = new(true, "1", "domain", "the key component of that identity domain this value is for"),
        ["SetProperty.property"] = new(true, "1", "emittedType", "the property of the emitted type this value sets"),
        ["Call.function"] = new(true, "1", "kernel", "the built-in function called"),
        ["Rule.governs"] = new(true, "+", "projection", "the sinks of the rule"),
        ["Rule.createKey"] = new(false, "1", "module", "the identity domain the rule creates keys in"),
        ["Constraint.constrains"] = new(true, "1", "module", "the entity or relationship type the constraint applies to"),
        ["Constraint.onProperty"] = new(false, "+", "constrainedType", "the properties the constraint uses, in order (ordinal 0, 1, ...): one, several for a key, none for a kind that does not use onProperty"),
        ["EquivalencePolicy.between"] = new(true, "+", "module", "the two identity domains the policy binds, in order (ordinal 0 and 1)"),
        ["PropertyType.valueKind"] = new(true, "1", "kernel", "the value kind of the property"),
        ["Field.valueKind"] = new(true, "1", "kernel", "the value kind of the field"),
        ["KeyComponent.valueKind"] = new(true, "1", "kernel", "the value kind of the key component"),
    };

    public static readonly HashSet<string> SinkKinds = ["EmitEntity", "EmitRelationship", "Link"];
    public static readonly HashSet<string> TelemetryKinds = ["Series", "Rollup", "Delta", "Threshold"];
}

/// <summary>The editor palette, built from the kernel table (never hard-coded in the UI).</summary>
public static class Palette
{
    static string KindName(string k) => k switch { "T" => "Text", "I" => "Integer", "D" => "Decimal", "B" => "Boolean", "L" => "List", var x => x };
    /// <summary>What a numeric parameter measures, for an editor: a duration is an integer of nanoseconds on the wire and a number with a unit suffix in the text (`24h`); a count says what it counts; a value is in the unit of the series it is compared with. Every Integer and Decimal parameter of the kernel is listed (a test enforces it).</summary>
    public sealed record Quantity(string Kind, string Unit);
    static readonly Dictionary<string, Quantity> Quantities = new()
    {
        ["width"] = new("duration", "ns"), ["hop"] = new("duration", "ns"), ["within"] = new("duration", "ns"), ["period"] = new("duration", "ns"), ["age"] = new("duration", "ns"), ["ttl"] = new("duration", "ns"), ["fill"] = new("value", "unit of the property read"),
        ["enterAfter"] = new("count", "windows"), ["exitAfter"] = new("count", "windows"),
        ["above"] = new("value", "unit of the thresholded value"), ["below"] = new("value", "unit of the thresholded value"),
        ["minDepth"] = new("count", "hops"), ["maxDepth"] = new("count", "hops"), ["minHops"] = new("count", "hops"), ["maxHops"] = new("count", "hops"),
        ["retain"] = new("count", "retained inputs per element"),
        ["min"] = new("value", "unit of the constrained property"), ["max"] = new("value", "unit of the constrained property"),
        ["threshold"] = new("ratio", "fraction from 0 to 1"),
    };
    public static IReadOnlyDictionary<string, Quantity> NumericQuantities => Quantities;

    public static JsonNode Build()
    {
        var enums = KernelTable.Enums.ToLookup(e => e.Type + "." + e.Prop, e => e.Values);
        JsonArray Params(string type)
        {
            var res = new JsonArray();
            for (var t = type; t is not null; t = KernelTable.ByName[t].Super)
                foreach (var p in KernelTable.ByName[t].Props.Where(p => p.Name != "name" || t != "Operator"))
                {
                    if (res.Any(x => x!["name"]!.GetValue<string>() == p.Name)) continue;
                    var o = new JsonObject { ["name"] = p.Name, ["kind"] = KindName(p.Kind), ["required"] = p.Required };
                    if (enums[type + "." + p.Name].FirstOrDefault() is { } v) o["values"] = new JsonArray([.. v.Select(x => (JsonNode)x)]);
                    if (Quantities.TryGetValue(p.Name, out var q))
                    {
                        o["quantity"] = q.Kind; o["unit"] = q.Unit;
                        if (q.Kind == "duration") { o["literal"] = "<integer><ms|s|m|h>"; o["example"] = "24h"; }
                    }
                    res.Add(o);
                }
            return res;
        }
        // the references a node of a type carries to other definitions: the kernel's edges from the type, with what each must name
        JsonArray References(string type)
        {
            var res = new JsonArray();
            for (var t = type; t is not null; t = KernelTable.ByName.TryGetValue(t, out var spec) ? spec.Super : null)
                foreach (var e in KernelTable.Edges.Where(e => e.Sources.Contains(t) && Support.References.ContainsKey(type + "." + e.Name) || e.Sources.Contains(t) && Support.References.ContainsKey(t + "." + e.Name)))
                {
                    if (res.Any(x => x!["name"]!.GetValue<string>() == e.Name)) continue;
                    var r = Support.References.GetValueOrDefault(type + "." + e.Name) ?? Support.References[t + "." + e.Name];
                    res.Add(new JsonObject
                    {
                        ["name"] = e.Name, ["targets"] = new JsonArray([.. e.Targets.Select(x => (JsonNode)x)]), ["required"] = r.Required, ["multiplicity"] = r.Multiplicity, ["scope"] = r.Scope, ["meaning"] = r.Meaning,
                    });
                }
            return res;
        }
        JsonArray Slots(string type)
        {
            var res = new JsonArray();
            for (var t = type; t is not null; t = KernelTable.ByName[t].Super)
                foreach (var s in KernelTable.Slots.Where(s => s.Container == t))
                {
                    var o = new JsonObject { ["name"] = s.Name, ["children"] = new JsonArray([.. s.Children.Select(c => (JsonNode)c)]), ["multiplicity"] = s.Mult, ["ordered"] = s.Ordered };
                    o["childTypes"] = new JsonArray([.. s.Children.Where(KernelTable.ByName.ContainsKey).Select(c => (JsonNode)new JsonObject { ["type"] = c, ["params"] = Params(c), ["references"] = References(c) })]);
                    if (Support.SlotValues.TryGetValue(s.Container + "." + s.Name, out var sv))
                        o["value"] = new JsonObject { ["kind"] = sv.Kind, ["roots"] = new JsonArray([.. sv.Roots.Select(r => (JsonNode)r)]), ["offers"] = sv.Offers };
                    res.Add(o);
                }
            return res;
        }
        var ops = new JsonArray();
        foreach (var kind in KernelTable.OperatorSet.Order())
        {
            var supported = Support.Operators.TryGetValue(kind, out var strategy);
            var o = new JsonObject
            {
                ["kind"] = kind, ["supported"] = supported, ["role"] = Support.SinkKinds.Contains(kind) ? "sink" : Support.TelemetryKinds.Contains(kind) ? "telemetry" : kind is "Match" ? "source" : "linear",
                ["container"] = "Projection", ["slot"] = "op",
                ["ports"] = new JsonArray([.. KernelTable.OperatorPorts(kind).Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["direction"] = p.Direction, ["kind"] = p.Kind })]),
                ["params"] = Params(kind), ["references"] = References(kind), ["slots"] = Slots(kind), ["strategy"] = supported ? strategy : null, ["sink"] = Support.SinkKinds.Contains(kind), ["telemetry"] = Support.TelemetryKinds.Contains(kind),
            };
            if (!supported) o["unsupportedReason"] = Support.Unsupported.GetValueOrDefault(kind, "not built");
            ops.Add(o);
        }
        JsonObject Declaration(string slot, string type) => new() { ["slot"] = slot, ["type"] = type, ["container"] = "Module", ["params"] = Params(type), ["references"] = References(type), ["slots"] = Slots(type) };
        var decls = new JsonArray();
        foreach (var (slot, type) in new[] { ("entity", "EntityType"), ("rel", "RelationshipType"), ("extend", "Extension"), ("identity", "IdentityDomain"), ("equivalence", "EquivalencePolicy"), ("rows", "RowType"), ("projection", "Projection"), ("space", "GraphSpace") })
            decls.Add(Declaration(slot, type));
        var constraint = new JsonObject
        {
            ["slot"] = "constraint", ["type"] = "Constraint", ["container"] = "Module", ["params"] = Params("Constraint"), ["references"] = References("Constraint"), ["slots"] = Slots("Constraint"),
            ["kinds"] = new JsonArray([.. Support.ConstraintKinds.Select(k => (JsonNode)new JsonObject { ["kind"] = k.Key, ["target"] = k.Value.Target, ["uses"] = new JsonArray([.. k.Value.Uses.Select(u => (JsonNode)u)]), ["text"] = k.Value.Text })]),
        };
        decls.Add(constraint);
        var rowType = new JsonObject { ["type"] = "RowType", ["fieldSlot"] = "field", ["field"] = new JsonObject { ["type"] = "Field", ["params"] = Params("Field"), ["references"] = References("Field"), ["valueKinds"] = new JsonArray([.. KernelTable.ValueKinds.Where(k => k is not ("Any" or "List" or "Entity")).Select(k => (JsonNode)k)]) } };
        var equivalence = new JsonObject { ["type"] = "EquivalencePolicy", ["modes"] = new JsonArray([.. enums["EquivalencePolicy.mode"].First().Select(x => (JsonNode)x)]), ["between"] = "two identity domains (edge between)", ["params"] = Params("EquivalencePolicy") };
        var rule = new JsonObject
        {
            ["type"] = "Rule", ["params"] = Params("Rule"), ["references"] = References("Rule"), ["slots"] = Slots("Rule"), ["onDelete"] = new JsonArray([.. enums["Rule.onDelete"].First().Select(x => (JsonNode)x)]),
            ["policies"] = new JsonArray([.. Support.RulePolicies.Select(x => (JsonNode)new JsonObject { ["text"] = x.Key, ["meaning"] = x.Value })]),
        };
        var projectionPorts = new JsonArray([.. enums["Port.kind"].First().Select(k => (JsonNode)new JsonObject { ["kind"] = k, ["text"] = k switch { "rows" => "in name: rows <RowType>;", "graph" => "in name: graph <module alias>;", _ => "in name: param <Text|Integer|Decimal|Boolean> [= constant];" } })]);
        var aggs = enums["Aggregate.fn"].First().Select(a => (JsonNode)new JsonObject
        {
            ["name"] = a, ["supported"] = Support.Aggregates.Contains(a),
            ["contexts"] = new JsonArray([.. (Support.AggregateContexts.GetValueOrDefault(a) ?? []).Select(c => (JsonNode)new JsonObject { ["operator"] = c.Operator, ["inputs"] = c.Inputs, ["output"] = c.Output, ["requires"] = c.Requires })]),
        });
        return new JsonObject
        {
            ["kernelDigest"] = Kernel.CompiledDigest, ["version"] = "3", ["operators"] = ops,
            ["ruleClasses"] = new JsonArray([.. Support.RuleClasses.Select(kv => (JsonNode)new JsonObject { ["name"] = kv.Key, ["params"] = new JsonArray([.. kv.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => (JsonNode)x)]), ["policies"] = new JsonArray([.. (kv.Key is "lens" ? new[] { "accept", "update", "onCreate", "onDelete" } : []).Select(x => (JsonNode)x)]), ["supported"] = kv.Key != "sync", ["unsupportedReason"] = kv.Key == "sync" ? "not built: needs the stored correspondence graph and the FWD and BWD translators of Ch. 8, none of which exist" : null })]),
            ["aggregates"] = new JsonArray([.. aggs]),
            // every node type that carries references, also those only reached as expressions (Call, IdOf) or inside them (SetComponent): an editor needs no table of its own
            ["references"] = new JsonObject(Support.References.Keys.Select(k => k[..k.IndexOf('.')]).Distinct().Order(StringComparer.Ordinal).Select(t => KeyValuePair.Create(t, (JsonNode?)References(t)))),
            ["declarations"] = decls, ["rowTypes"] = rowType, ["equivalencePolicies"] = equivalence, ["rules"] = rule, ["projectionPorts"] = projectionPorts,
            ["valueRoles"] = new JsonArray([.. enums["ValueRole.role"].First().Select(x => (JsonNode)x)]), ["valueRoleSlot"] = new JsonObject { ["container"] = "PropertyType", ["slot"] = "presentation", ["type"] = "ValueRole", ["params"] = Params("ValueRole") },
            ["functions"] = new JsonArray([.. KernelTable.Functions.Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["signature"] = f.Signature, ["role"] = f.Role, ["inverse"] = f.Inverse, ["supported"] = f.Role == "canonicalizer" || Support.Functions.Contains(f.Name) })]),
            ["valueKinds"] = new JsonArray([.. KernelTable.ValueKinds.Where(k => k != "Any").Select(k => (JsonNode)k)]),
            ["idScheme"] = "d:<module>/<slot>:<name>/..., relationship id <source>|<edge>|<target>, unnamed ordered child slot:#n",
        };
    }
}
