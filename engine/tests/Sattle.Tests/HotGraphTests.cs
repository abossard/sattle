using Sattle.Core;

namespace Sattle.Tests;

/// <summary>The hot layout (interned handles, byte pools, intrusive adjacency, open-addressing uniqueness index) against the reference model, on random edit sequences.</summary>
public class HotGraphTests
{
    public static IEnumerable<object[]> Seeds() => Enumerable.Range(0, 8).Select(i => new object[] { i });

    [Theory, MemberData(nameof(Seeds))]
    public void The_hot_graph_agrees_with_the_reference_graph_on_every_operation(int group)
    {
        int accepted = 0, rejected = 0;
        for (var c = 0; c < 150; c++)
        {
            var gen = new Gen(group * 9973 + c);
            var hot = new Graph(); var reference = new ReferenceGraph();
            for (var i = 0; i < 12; i++)
            {
                var p = gen.PatchFor(hot, i, guardOnly: true, @unchecked: true)!;      // not filtered by the graph under test: patches that must be rejected are compared too
                var edits = p.Edits.ToList();
                var verdict = reference.Check(edits);
                Assert.Equal(verdict, hot.Check(edits));
                if (verdict is null) accepted++; else rejected++;
                Assert.Equal(reference.Apply(edits), hot.Apply(edits));
                // invalid applications are rejected identically and leave both untouched
                var bad = new List<Edit> { Edit.Create(gen.Rel("x1")), Edit.Create(gen.Rel("x1")) };
                Assert.Equal(reference.Apply(bad) is null, hot.Apply(bad) is null);
                Assert.Equal(reference.Digest(), hot.Digest());
                Assert.Equal(reference.EntityCount, hot.EntityCount); Assert.Equal(reference.RelCount, hot.RelCount); Assert.Equal(reference.Count, hot.Count);
                Assert.True(hot.UniquenessHolds());
                foreach (var e in reference.Entities)
                {
                    Assert.Equal(e, hot.Entity(e.Id));
                    Assert.Equal(reference.OutRels(e.Id).Order(), hot.OutRels(e.Id).Order());
                    Assert.Equal(reference.InRels(e.Id).Order(), hot.InRels(e.Id).Order());
                    Assert.Equal(reference.OutDegree(e.Id), hot.OutDegree(e.Id));
                }
                foreach (var r in reference.Rels)
                {
                    Assert.Equal(r, hot.Rel(r.Id));
                    Assert.Equal(r, hot.FindRel(r.Source, r.Target, r.Type));
                    Assert.Equal(reference.OutRels(r.Source).Order(), hot.OutRels(r.Source).Order());
                    Assert.Equal(reference.InRels(r.Target).Order(), hot.InRels(r.Target).Order());
                }
                Assert.Null(hot.Entity("never")); Assert.Null(hot.Rel("never")); Assert.Empty(hot.OutRels("never"));
                var clone = hot.Clone();
                Assert.True(clone.SameAs(hot));
                Assert.Equal(hot.Digest(), clone.Digest());
            }
        }
        Assert.True(accepted > 0 && rejected > 0, $"{accepted} accepted and {rejected} rejected patches: both kinds must be compared");
    }

    [Fact]
    public void Props_survive_every_value_kind_and_in_place_updates_and_growth()
    {
        var g = new Graph();
        var big = Value.Integer(System.Numerics.BigInteger.Parse("123456789012345678901234567890"));
        var props = Props.Of(("a", Value.Text("ü😀")), ("b", big), ("c", Value.Decimal(-12.50m)), ("d", Value.Boolean(true)), ("e", Value.Instant(-5)), ("f", Value.Null), ("g", Value.Entity("x")), ("h", Value.List([Value.Integer(1), Value.List([Value.Text("n")])])));
        var e = new Entity("e1", "T", props);
        Assert.Null(g.Apply([Edit.Create(e)]));
        Assert.Equal(e, g.Entity("e1"));
        var smaller = e with { Props = Props.Of(("a", Value.Text("s"))) };
        Assert.Null(g.Apply([Edit.Change(e, smaller)]));
        Assert.Equal(smaller, g.Entity("e1"));
        var larger = smaller with { Props = props.With("z", Value.Text(new string('q', 5000))) };
        Assert.Null(g.Apply([Edit.Change(smaller, larger)]));
        Assert.Equal(larger, g.Entity("e1"));
        Assert.Null(g.Apply([Edit.Remove(larger)]));
        Assert.Null(g.Entity("e1"));
    }

    [Fact]
    public void Many_relationships_grow_the_uniqueness_index_and_removal_keeps_it_consistent()
    {
        var g = new Graph();
        var rels = Enumerable.Range(0, 5000).Select(i => new Rel("r" + i, "a" + i % 100, "b" + i / 100, i % 2 == 0 ? "T1" : "T2", Props.Empty)).ToList();
        Assert.Null(g.Apply([.. rels.Select(Edit.Create)]));
        Assert.True(g.UniquenessHolds());
        Assert.NotNull(g.Apply([Edit.Create(new Rel("dup", rels[7].Source, rels[7].Target, rels[7].Type, Props.Empty))]));
        Assert.Null(g.Apply([.. rels.Where((_, i) => i % 3 == 0).Select(Edit.Remove)]));
        Assert.True(g.UniquenessHolds());
        Assert.Null(g.Apply([Edit.Create(new Rel("dup2", rels[0].Source, rels[0].Target, rels[0].Type, Props.Empty))])); // key freed by removal
        Assert.Equal(rels.Count - rels.Count(r => rels.IndexOf(r) % 3 == 0) + 1, g.RelCount);
    }
}
