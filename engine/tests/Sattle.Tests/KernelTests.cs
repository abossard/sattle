using Sattle.Core;

namespace Sattle.Tests;

/// <summary>Research 03 section 2.8 and open question Q2: the kernel is a fixed point of the generic checker.</summary>
public class KernelTests
{
    [Fact]
    public void Kernel_table_has_the_counts_of_section_2_3()
    {
        var concreteDefinition = KernelTable.Types.Take(KernelTable.Types.Length - 5).Where(t => !t.Abstract).ToList();
        Assert.Equal(54, concreteDefinition.Count);                       // the 50 of section 2.3, ValueRole (presentation roles of property values), the operator Retain (evidence retention), the operator Lift and its LiftState
        Assert.Equal(3, KernelTable.Types.Take(3).Count(t => t.Abstract));
        Assert.Equal(20, KernelTable.OperatorSet.Count);
        Assert.Equal(4, KernelTable.ExprSet.Count);
        Assert.Equal(46, KernelTable.Edges.Length);                        // 37 in section 2.3; the assertion counted `Take(37)` and so held for any table of at least 37, while the table grew to 46
    }

    [Fact]
    public void Kernel_conforms_to_itself() => Assert.Empty(Conformance.ConformsKernelToItself());

    [Fact]
    public void Kernel_passes_the_compiled_kernel_checks() => Assert.Empty(KernelChecks.Run(Kernel.Graph));

    [Fact]
    public void Entity_type_is_its_own_type()
    {
        var et = Kernel.Graph.Entity(Ids.KEntity("EntityType"))!;
        Assert.Equal(Ids.KEntity("EntityType"), et.Type);
    }

    [Fact]
    public void Kernel_digest_equals_the_compiled_constant() => Assert.Equal(Kernel.CompiledDigest, Kernel.ComputeDigest());

    [Fact]
    public void Kernel_encoding_is_deterministic() => Assert.Equal(Kernel.Graph.Encode(), new DefBuilderRoundTrip().Rebuild().Encode());

    sealed class DefBuilderRoundTrip { public Graph Rebuild() { var g = new Graph(); g.Apply(Kernel.Graph.Elements().Select(Edit.Create).ToList()); return g; } }

    static object[] M(string name, Func<Graph, List<Edit>> f) => [name, f];
    public static IEnumerable<object[]> Mutations() =>
    [
        M("drop the hasProperty edge type", g => Remove(g, Ids.KRel("hasProperty"))),
        M("mark EntityType abstract", g => Change(g, Ids.KEntity("EntityType"), e => e with { Props = e.Props.With(Ids.KProp("abstract"), Value.Boolean(true)) })),
        M("change the kind of the required property of hasProperty to Text", g => Retarget(g, Ids.Rel(Ids.KProp("required"), "valueKind", Ids.KBuiltin("Boolean")), Ids.KBuiltin("Text"))),
        M("remove name from EntityType", g => Remove(g, Ids.Rel(Ids.KEntity("Declaration"), "hasProperty", Ids.KProp("name")))),
        M("narrow targetType of contains to EntityType only", g => [Edit.Create(new Rel(Ids.Rel(Ids.KRel("contains"), "targetType", Ids.KEntity("EntityType")), Ids.KRel("contains"), Ids.KEntity("EntityType"), Ids.KRel("targetType"), Props.Empty))]),
        M("drop the Constraint entity type", g => Remove(g, Ids.KEntity("Constraint"))),
    ];

    [Theory, MemberData(nameof(Mutations))]
    public void A_broken_kernel_fails_conformance(string name, Func<Graph, List<Edit>> mutate)
    {
        _ = name;
        var k = Kernel.Graph.Clone();
        var edits = mutate(k);
        Assert.Null(k.Apply(edits));
        Assert.NotEmpty(Conformance.ConformsKernelToItself(k));
    }

    static List<Edit> Remove(Graph g, string nodeOrRelId) =>
        g.Entity(nodeOrRelId) is { } e ? [Edit.Remove(e)] : [Edit.Remove(g.Rel(nodeOrRelId) ?? throw new InvalidOperationException(nodeOrRelId))];
    static List<Edit> Change(Graph g, string id, Func<Entity, Entity> f) => [Edit.Change(g.Entity(id)!, f(g.Entity(id)!))];
    static List<Edit> Retarget(Graph g, string relId, string newTarget)
    {
        var r = g.Rel(relId)!;
        var n = r with { Id = Ids.Rel(r.Source, "valueKind", newTarget), Target = newTarget };
        return [Edit.Remove(r), Edit.Create(n)];
    }
}
