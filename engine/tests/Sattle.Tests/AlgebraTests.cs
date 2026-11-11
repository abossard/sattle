using System.Collections.Immutable;
using Sattle.Core;

namespace Sattle.Tests;

/// <summary>Ch. 4 theorems T1 to T4 and the well-formedness invariants of Ch. 3, as properties over seeded random cases (O3.1, O4.1).</summary>
public class AlgebraTests
{
    const int CasesPerSeedGroup = 500;
    public static IEnumerable<object[]> SeedGroups() => Enumerable.Range(0, 8).Select(i => new object[] { i });

    static void ForCases(int group, Action<Gen> body)
    {
        for (var i = 0; i < CasesPerSeedGroup; i++) body(new Gen(group * 100_003 + i));
    }

    [Theory, MemberData(nameof(SeedGroups))]
    public void T1_inverse_law(int group) => ForCases(group, g =>
    {
        var (b, chain) = g.Chain(1);
        if (chain.Count == 0) return;
        var p = chain[0];
        var after = p.ApplyTo(b)!;
        Assert.NotNull(after);
        Assert.True(p.Inverse().ApplyTo(after)!.SameAs(b));
        Assert.Equal(p.Epsilon(), p.Then(p.Inverse()));
        Assert.Equal(p.Inverse().Epsilon(), p.Inverse().Then(p));
        Assert.Equal(p, p.Then(p.Inverse())!.Then(p));
        Assert.Equal(p, p.Inverse().Inverse());
    });

    [Theory, MemberData(nameof(SeedGroups))]
    public void T1_inverse_reverses_composition(int group) => ForCases(group, g =>
    {
        var (_, c) = g.Chain(2);
        if (c.Count < 2) return;
        var pq = c[0].Then(c[1])!;
        Assert.NotNull(pq);
        Assert.Equal(c[1].Inverse().Then(c[0].Inverse()), pq.Inverse());
    });

    [Theory, MemberData(nameof(SeedGroups))]
    public void T2_associativity_and_application_respects_composition(int group) => ForCases(group, g =>
    {
        var (b, c) = g.Chain(3);
        if (c.Count < 3) return;
        var l = c[0].Then(c[1])!.Then(c[2]);
        var r = c[0].Then(c[1].Then(c[2])!);
        Assert.NotNull(l);
        Assert.Equal(l, r);
        var stepwise = c[2].ApplyTo(c[1].ApplyTo(c[0].ApplyTo(b)!)!)!;
        Assert.True(l!.ApplyTo(b)!.SameAs(stepwise));
    });

    [Theory, MemberData(nameof(SeedGroups))]
    public void T2_composition_is_partial_and_patches_do_not_commute(int group) => ForCases(group, g =>
    {
        var (_, c) = g.Chain(2);
        if (c.Count < 2) return;
        Assert.Null(c[1].Then(c[0])); // adjacency fails: to(q) is not from(p)
        var shifted = new Patch("g", 5, 6, c[1].Edits);
        Assert.Null(c[0].Then(shifted));
    });

    [Theory, MemberData(nameof(SeedGroups))]
    public void T3_signed_deltas_form_an_abelian_group(int group) => ForCases(group, g =>
    {
        var (b, c) = g.Chain(3);
        if (c.Count < 3) return;
        var (x, y, z) = (c[0].Delta(), c[1].Delta(), c[2].Delta());
        Assert.Equal(x + y, y + x);
        Assert.Equal((x + y) + z, x + (y + z));
        Assert.True((x + -x).IsZero);
        Assert.Equal(x, x + new Delta());
        _ = b;
    });

    [Theory, MemberData(nameof(SeedGroups))]
    public void T4_delta_is_a_homomorphism_and_materializes(int group) => ForCases(group, g =>
    {
        var (b, c) = g.Chain(2);
        if (c.Count < 2) return;
        var p = c[0];
        var gp = p.ApplyTo(b)!;
        Assert.Equal(Delta.Chi(gp), Delta.Chi(b) + p.Delta());                       // 1
        Assert.Equal(p.Delta() + c[1].Delta(), p.Then(c[1])!.Delta());               // 2
        Assert.Equal(-p.Delta(), p.Inverse().Delta());                               // 3
        Assert.True(p.Epsilon().Delta().IsZero);                                     // 4
        Assert.True(Delta.Materialize(b, p.Delta())!.SameAs(gp));                    // 8.1
        var back = Delta.ToPatch(p.Delta(), b, p.Graph, p.From, p.To)!;
        Assert.Equal(p.Net(), back);                                                 // 8.2
        Assert.Equal(p.Net().Delta(), p.Delta());                                    // 8.3
    });

    [Fact]
    public void T4_delta_is_not_injective_and_not_closed_under_materialization()
    {
        var a = new Entity("a", "T1", Props.Of(("p", Value.Integer(1))));
        var b = new Entity("a", "T1", Props.Of(("p", Value.Integer(2))));
        var p = new Patch("g", 0, 1, [Edit.Create(a)]);
        var q = new Patch("g", 1, 2, [Edit.Change(a, b)]);
        var direct = new Patch("g", 0, 2, [Edit.Create(b)]);
        Assert.Equal(p.Then(q)!.Delta(), direct.Delta());
        var z = new Delta(); z.Add(new Fact(a.Key, a), 1); z.Add(new Fact(b.Key, b), 1);
        Assert.Null(Delta.Materialize(new Graph(), z)); // two facts on one key
        var empty = new Patch("g", 0, 0, []);
        Assert.Equal(empty.Delta(), p.Epsilon().Delta()); // guard-only edits are forgotten by delta
    }

    [Theory, MemberData(nameof(SeedGroups))]
    public void Squash_reports_loss_and_equals_the_net_composition(int group) => ForCases(group, g =>
    {
        var (b, c) = g.Chain(3);
        if (c.Count < 2) return;
        var (net, loss) = Delta.Squash(c)!.Value;
        Assert.True(loss.ChronologyLost);
        var final = c.Aggregate(b, (acc, p) => p.ApplyTo(acc)!);
        Assert.True(net.ApplyTo(b)!.SameAs(final));
        Assert.Equal(c.Aggregate(new Delta(), (d, p) => d + p.Delta()), net.Delta());
    });

    [Theory, MemberData(nameof(SeedGroups))]
    public void T4_4_edit_order_is_irrelevant(int group) => ForCases(group, g =>
    {
        var (b, c) = g.Chain(1);
        if (c.Count == 0) return;
        var edits = c[0].Edits.ToList();
        var rev = edits.AsEnumerable().Reverse().ToList();
        var x = b.Clone(); var y = b.Clone();
        Assert.Null(x.Apply(edits));
        Assert.Null(y.Apply(rev));
        Assert.True(x.SameAs(y));
        Assert.Equal(x.Digest(), y.Digest());
    });

    [Theory, MemberData(nameof(SeedGroups))]
    public void Ch3_committed_states_are_well_formed_and_history_is_immutable(int group) => ForCases(group, g =>
    {
        var log = new GraphLog("g");
        var digests = new List<string> { log.State.Digest() };
        for (var i = 0; i < 6; i++)
        {
            var p = g.PatchFor(log.State, log.Head, guardOnly: false);
            if (p is null) break;
            var (c, err) = log.Commit(p.Edits);
            if (c is null) { Assert.Equal("empty edit set creates no revision", err); continue; }
            Assert.Equal(log.Head, c.To);
            Assert.True(log.State.UniquenessHolds());                               // WF3
            foreach (var r in log.State.Rels) Assert.Equal(r, log.State.FindRel(r.Source, r.Target, r.Type));
            digests.Add(log.State.Digest());
            for (var r = 0; r <= log.Head; r++) Assert.Equal(digests[(int)r], log.StateAt(r).Digest()); // Theorem 3.2
        }
        // Def. 3.9: an ineffective set creates no revision
        var head = log.Head;
        var existing = log.State.Elements().FirstOrDefault();
        if (existing is not null) Assert.NotNull(log.Commit([new Edit(existing.Key, existing, existing)]).Error);
        Assert.Equal(head, log.Head);
    });

    [Fact]
    public void Ch3_uniqueness_rejects_parallel_relationships_but_allows_other_types_and_orientations()
    {
        var g = new Graph();
        Assert.Null(g.Apply([Edit.Create(new Rel("r1", "a", "b", "R1", Props.Empty))]));
        Assert.NotNull(g.Apply([Edit.Create(new Rel("r2", "a", "b", "R1", Props.Empty))]));      // same (s,t,type)
        Assert.Null(g.Apply([Edit.Create(new Rel("r3", "a", "b", "R2", Props.Empty))]));          // other type
        Assert.Null(g.Apply([Edit.Create(new Rel("r4", "b", "a", "R1", Props.Empty))]));          // other orientation
        // swap inside one patch is fine: r1 leaves the key, r2 takes it
        var r1 = g.Rel("r1")!;
        Assert.Null(g.Apply([Edit.Remove(r1), Edit.Create(new Rel("r2", "a", "b", "R1", Props.Empty))]));
        // dangling endpoints are well-formed
        Assert.Null(g.Apply([Edit.Create(new Rel("r5", "zz", "yy", "R1", Props.Empty))]));
        Assert.Null(g.Entity("zz"));
    }

    [Fact]
    public void Ch3_retype_keeps_the_relationship_id()
    {
        var g = new Graph();
        var r = new Rel("r1", "a", "b", "R1", Props.Empty);
        g.Apply([Edit.Create(r)]);
        var t = r with { Type = "R2" };
        Assert.Null(g.Apply([Edit.Change(r, t)]));
        Assert.Equal("R2", g.Rel("r1")!.Type);
        Assert.Null(g.FindRel("a", "b", "R1"));
    }

    [Theory, MemberData(nameof(SeedGroups))]
    public void Theorem_3_3_equality_by_digest(int group) => ForCases(group, g =>
    {
        var a = g.RandomGraph();
        var b = a.Clone();
        Assert.True(a.SameAs(b)); Assert.Equal(a.Encode(), b.Encode());
        var (b0, c) = g.Chain(1);
        if (c.Count == 0) return;
        var moved = c[0].ApplyTo(b0)!;
        Assert.Equal(b0.SameAs(moved), b0.Digest() == moved.Digest());
    });

    [Fact]
    public void Ontology_slot_is_an_ordinary_edit()
    {
        var g = new GraphLog("g");
        var o = new OntologyState("o1", "1", "sha256:00");
        var (p, err) = g.Commit([Edit.Create(o), Edit.Create(new Entity("a", "T", Props.Empty))]);
        Assert.Null(err);
        Assert.Equal(ElementKey.Ontology, p!.Edits[0].Key);
        Assert.Equal(o, g.State.Ontology);
    }

    [Fact]
    public void Frontier_order()
    {
        var a = Frontier.Empty with { Pos = ImmutableSortedDictionary<string, long>.Empty.Add("s", 1) };
        var b = Frontier.Empty with { Pos = ImmutableSortedDictionary<string, long>.Empty.Add("s", 2).Add("t", 0), Cfg = 1 };
        Assert.True(a.LessOrEqual(b));
        Assert.False(b.LessOrEqual(a));
    }
}
