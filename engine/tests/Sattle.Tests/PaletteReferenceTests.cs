using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The palette says which references a node carries to other definitions and what each must name, so an editor can check a node from the palette alone and a new operator kind needs no table in the client.</summary>
public class PaletteReferenceTests
{
    static readonly JsonNode Palette = Sattle.Runtime.Palette.Build();
    static JsonNode Operator(string kind) => Palette["operators"]!.AsArray().Single(o => o!["kind"]!.GetValue<string>() == kind)!;
    static string Refs(JsonNode refs) => string.Join(" ", refs.AsArray().Select(r => $"{r!["name"]}:{string.Join("|", r["targets"]!.AsArray().Select(t => t!.GetValue<string>()))}:{(r["required"]!.GetValue<bool>() ? "required" : "optional")}:{r["scope"]}").Order(StringComparer.Ordinal));

    [Theory]
    [InlineData("EmitEntity", "ofType:EntityType|RelationshipType:required:outputOntology")]
    [InlineData("EmitRelationship", "ofType:EntityType|RelationshipType:required:outputOntology")]
    [InlineData("Traverse", "ofType:EntityType|RelationshipType:required:inputGraph")]
    [InlineData("Lift", "liftEdge:RelationshipType:required:inputGraph liftVia:RelationshipType:required:inputGraph ofType:EntityType|RelationshipType:required:outputOntology")]
    [InlineData("Link", "policy:EquivalencePolicy:required:module")]
    [InlineData("Fixpoint", "over:Port:required:step")]
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
        var all = Palette["references"]!.AsObject();
        Assert.Equal("from:PatternNode:required:pattern ofType:EntityType|RelationshipType:required:inputGraph to:PatternNode:required:pattern", Refs(all["PatternEdge"]!));
        Assert.Equal("domain:IdentityDomain:required:module", Refs(all["IdOf"]!));
        Assert.Equal("function:Function:required:kernel", Refs(all["Call"]!));
        Assert.Equal("createKey:IdentityDomain:optional:module governs:EmitEntity|EmitRelationship|Link|Lift:required:projection", Refs(Palette["rules"]!["references"]!));
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
