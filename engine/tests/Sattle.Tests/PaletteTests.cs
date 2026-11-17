using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>PROTOCOL.md v3, `/palette`: what an editor may offer is read from the kernel (Def. 5.6 for the aggregates): operators with ports and parameters and what the engine cannot run, declarations, aggregates with their contexts, the quantity and unit of every numeric parameter, and the references of every node.</summary>
public class PaletteTests
{
    static readonly JsonNode Built = Palette.Build();

    [Fact]
    public void The_palette_is_derived_from_the_kernel_and_marks_what_the_engine_cannot_run()
    {
        var p = Built;
        var ops = p["operators"]!.AsArray();
        Assert.Equal(18, ops.Count);
        var window = ops.First(o => o!["kind"]!.GetValue<string>() == "Window")!;
        Assert.Equal(["in", "out"], window["ports"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()));
        Assert.Equal(["revise"], window["params"]!.AsArray().First(x => x!["name"]!.GetValue<string>() == "late")!["values"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.True(window["supported"]!.GetValue<bool>());
        Assert.True(ops.First(o => o!["kind"]!.GetValue<string>() == "Values")!["supported"]!.GetValue<bool>());
        Assert.False(ops.First(o => o!["kind"]!.GetValue<string>() == "Delta")!["supported"]!.GetValue<bool>());
        Assert.Contains(p["functions"]!.AsArray(), f => f!["name"]!.GetValue<string>() == "Divide" && f["signature"]!.GetValue<string>().Contains("Decimal"));
        Assert.Contains(p["ruleClasses"]!.AsArray(), c => c!["name"]!.GetValue<string>() == "lens");
        Assert.Contains(p["aggregates"]!.AsArray(), a => a!["name"]!.GetValue<string>() == "countDistinct");
    }

    [Fact]
    public void The_palette_describes_declarations_constraint_kinds_row_types_rules_and_value_roles_from_the_kernel()
    {
        var p = Built;
        Assert.Equal(["key", "enum", "range", "pattern", "cardinality", "acyclic"], p["declarations"]!.AsArray().First(x => x!["slot"]!.GetValue<string>() == "constraint")!["kinds"]!.AsArray().Select(k => k!["kind"]!.GetValue<string>()));
        Assert.Contains(p["declarations"]!.AsArray(), x => x!["slot"]!.GetValue<string>() == "rows" && x["type"]!.GetValue<string>() == "RowType");
        Assert.Equal(["assert", "review"], p["equivalencePolicies"]!["modes"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains(p["rowTypes"]!["field"]!["params"]!.AsArray(), x => x!["name"]!.GetValue<string>() == "optional");
        Assert.Contains(p["rules"]!["slots"]!.AsArray(), x => x!["name"]!.GetValue<string>() == "createDefault");
        Assert.Equal(["remove", "set"], p["rules"]!["onDelete"]!.AsArray().Select(x => x!.GetValue<string>()).Order());
        Assert.Equal(["ok", "warning", "danger", "unknown", "neutral"], p["valueRoles"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains(p["projectionPorts"]!.AsArray(), x => x!["kind"]!.GetValue<string>() == "param");
    }

    // ---- aggregates: where each may be used (`Group` over data, `Rollup` over telemetry) ----

    static readonly string[] All = ["count", "countDistinct", "sum", "min", "max", "mean", "last"];

    [Fact]
    public void The_palette_carries_the_contexts_of_every_aggregate_and_marks_unlisted_ones_unsupported()
    {
        var aggs = Built["aggregates"]!.AsArray();
        Assert.All(All, fn =>
        {
            var a = aggs.First(x => x!["name"]!.GetValue<string>() == fn)!;
            Assert.True(a["supported"]!.GetValue<bool>());
            Assert.Equal(Support.AggregateContexts[fn].Select(c => c.Operator), a["contexts"]!.AsArray().Select(c => c!["operator"]!.GetValue<string>()));
        });
        Assert.Equal(["Group"], aggs.First(x => x!["name"]!.GetValue<string>() == "last")!["contexts"]!.AsArray().Select(c => c!["operator"]!.GetValue<string>()));
        Assert.Equal(["Group", "Rollup"], aggs.First(x => x!["name"]!.GetValue<string>() == "sum")!["contexts"]!.AsArray().Select(c => c!["operator"]!.GetValue<string>()));
    }

    // ---- numeric parameters say what they measure, so an editor shows `24h` for a duration and not a number of nanoseconds ----

    static IEnumerable<JsonObject> Params(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o:
                if (o.ContainsKey("name") && o.ContainsKey("kind") && o.ContainsKey("required")) yield return o;
                foreach (var kv in o) foreach (var p in Params(kv.Value)) yield return p;
                break;
            case JsonArray a:
                foreach (var x in a) foreach (var p in Params(x)) yield return p;
                break;
        }
    }

    [Fact]
    public void Every_Integer_and_Decimal_parameter_of_the_palette_has_a_quantity_and_a_unit()
    {
        var numeric = Params(Built).Where(p => p["kind"]!.GetValue<string>() is "Integer" or "Decimal").ToList();
        Assert.True(numeric.Count >= 12);
        Assert.True(numeric.All(p => p["quantity"] is not null && p["unit"] is not null), string.Join(", ", numeric.Where(p => p["quantity"] is null).Select(p => p["name"]!.GetValue<string>())));
    }

    [Theory]
    [InlineData("width")] [InlineData("within")] [InlineData("age")]
    public void A_duration_parameter_is_nanoseconds_on_the_wire_and_says_how_it_is_written(string name)
    {
        var p = Params(Built).First(x => x["name"]!.GetValue<string>() == name);
        Assert.Equal("duration", p["quantity"]!.GetValue<string>());
        Assert.Equal("ns", p["unit"]!.GetValue<string>());
        Assert.Equal("<integer><ms|s|m|h>", p["literal"]!.GetValue<string>());
        Assert.Equal("24h", p["example"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("enterAfter", "windows")] [InlineData("exitAfter", "windows")] [InlineData("minDepth", "hops")] [InlineData("maxDepth", "hops")]
    public void A_count_parameter_names_what_it_counts(string name, string unit)
    {
        var p = Params(Built).First(x => x["name"]!.GetValue<string>() == name);
        Assert.Equal("count", p["quantity"]!.GetValue<string>());
        Assert.Equal(unit, p["unit"]!.GetValue<string>());
    }

    // ---- references: which definitions a node names and what each must be, so an editor checks a node from the palette alone ----

    static JsonNode Operator(string kind) => Built["operators"]!.AsArray().Single(o => o!["kind"]!.GetValue<string>() == kind)!;
    static string Refs(JsonNode refs) => string.Join(" ", refs.AsArray().Select(r => $"{r!["name"]}:{string.Join("|", r["targets"]!.AsArray().Select(t => t!.GetValue<string>()))}:{(r["required"]!.GetValue<bool>() ? "required" : "optional")}:{r["scope"]}").Order(StringComparer.Ordinal));

    [Theory]
    [InlineData("EmitEntity", "ofType:EntityType|RelationshipType:required:outputOntology")]
    [InlineData("EmitRelationship", "ofType:EntityType|RelationshipType:required:outputOntology")]
    [InlineData("Traverse", "ofType:EntityType|RelationshipType:required:inputGraph")]
    [InlineData("Link", "policy:EquivalencePolicy:required:module")]
    [InlineData("Filter", "")]
    public void Each_operator_lists_its_references_with_their_target_types_and_scope(string kind, string expected)
    {
        Assert.Equal(expected, Refs(Operator(kind)["references"]!));
    }

    [Fact]
    public void The_children_of_a_pattern_and_the_expression_nodes_carry_their_references_too()
    {
        var node = Operator("Match")["slots"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == "node")!["childTypes"]![0]!["references"]!;
        Assert.Equal("ofType:EntityType|RelationshipType:required:inputGraph", Refs(node));
        var all = Built["references"]!.AsObject();
        Assert.Equal("from:PatternNode:required:pattern ofType:EntityType|RelationshipType:required:inputGraph to:PatternNode:required:pattern", Refs(all["PatternEdge"]!));
        Assert.Equal("domain:IdentityDomain:required:module", Refs(all["IdOf"]!));
        Assert.Equal("function:Function:required:kernel", Refs(all["Call"]!));
        Assert.Equal("createKey:IdentityDomain:optional:module governs:EmitEntity|EmitRelationship|Link:required:projection", Refs(Built["rules"]!["references"]!));
    }

    /// <summary>The relationships that connect a declaration's parts are in the palette, so an editor builds a constraint, a policy or a field without naming them itself (#118).</summary>
    [Theory]
    [InlineData("constraint", "constrains:EntityType|RelationshipType:required:module onProperty:PropertyType:optional:constrainedType")]
    [InlineData("equivalence", "between:IdentityDomain:required:module")]
    [InlineData("rows", "")]
    public void Each_declaration_lists_the_references_that_connect_its_parts(string slot, string expected)
    {
        Assert.Equal(expected, Refs(Built["declarations"]!.AsArray().Single(d => d!["slot"]!.GetValue<string>() == slot)!["references"]!));
    }

    [Theory]
    [InlineData("Field", "valueKind:ValueKind:required:kernel")]
    [InlineData("PropertyType", "valueKind:ValueKind:required:kernel")]
    [InlineData("KeyComponent", "valueKind:ValueKind:required:kernel")]
    public void The_parts_of_a_declaration_name_their_value_kind_through_a_reference(string type, string expected)
    {
        Assert.Equal(expected, Refs(Built["references"]![type]!));
        if (type == "Field") Assert.Equal(expected, Refs(Built["rowTypes"]!["field"]!["references"]!));
    }

    /// <summary>Every edge the kernel draws from an operator or a node inside one is described: a new edge without a description fails here, not in an editor.</summary>
    [Fact]
    public void Every_kernel_edge_from_a_definition_node_of_a_projection_has_a_description()
    {
        var inside = KernelTable.OperatorSet.Concat(KernelTable.Slots.Where(s => KernelTable.OperatorSet.Contains(s.Container)).SelectMany(s => s.Children))
            .Concat(["PatternNode", "PatternEdge", "IdOf", "Call", "SetComponent", "SetProperty", "Rule"]).ToHashSet();
        var structural = new HashSet<string> { "contains", "wire", "NoType" };
        var undescribed = KernelTable.Edges.Where(e => !structural.Contains(e.Name)).SelectMany(e => e.Sources.Where(inside.Contains).Select(s => s + "." + e.Name))
            .Where(k => !Support.References.ContainsKey(k)).ToList();
        Assert.Empty(undescribed);
    }
}
