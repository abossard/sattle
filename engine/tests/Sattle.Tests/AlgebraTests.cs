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

    // Def. 3.7: every guard reads the same original finite map. The typed inputs use
    // the property and value constructors (WF2). Structural admission additionally tests
    // WF3 on the entire resulting map, without consulting the graph's uniqueness index.
    static ImmutableDictionary<ElementKey, Element>? ApplyEdits(
        ImmutableDictionary<ElementKey, Element> before, IReadOnlyList<Edit> edits)
    {
        if (edits.Select(e => e.Key).Distinct().Count() != edits.Count) return null;

        var after = before;
        foreach (var edit in edits)
        {
            if (!Fits(edit.Key, edit.Before) || !Fits(edit.Key, edit.After)) return null;
            if (!Equals(before.GetValueOrDefault(edit.Key), edit.Before)) return null;
            after = edit.After is null ? after.Remove(edit.Key) : after.SetItem(edit.Key, edit.After);
        }
        return after;

        static bool Fits(ElementKey key, Element? element) => element switch
        {
            null => true,
            Entity entity => key.Kind == 'e' && key.Id == entity.Id,
            Rel rel => key.Kind == 'r' && key.Id == rel.Id,
            OntologyState => key.Kind == 'o' && key.Id == "",
            _ => false
        };
    }

    static bool StructurallyValid(ImmutableDictionary<ElementKey, Element>? result) =>
        result is not null && result.Values.OfType<Rel>()
            .GroupBy(r => (r.Source, r.Target, r.Type)).All(group => group.Count() == 1);

    static void AssertMap(ImmutableDictionary<ElementKey, Element> expected, Graph actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (key, element) in expected)
        {
            Assert.Equal(element, actual.Get(key));
            if (element is Rel r) Assert.Equal(r, actual.FindRel(r.Source, r.Target, r.Type));
        }
    }

    static (bool Applied, bool Committed) CompareWithOracle(
        GraphLog log, List<ImmutableDictionary<ElementKey, Element>> history, IReadOnlyList<Edit> edits)
    {
        var before = history[^1];
        var result = ApplyEdits(before, edits);
        var applicable = StructurallyValid(result);
        var graph = log.State.Clone();
        Assert.Equal(applicable, graph.Apply(edits) is null);
        AssertMap(applicable ? result! : before, graph);

        // GraphLog accepts proposals and strips guard-only edits. Def. 3.9 applies
        // to the effective set submitted to the log, which must also be nonempty.
        var effective = edits.Where(e => !Equals(e.Before, e.After)).ToList();
        var recorded = ApplyEdits(before, effective);
        var admissible = effective.Count > 0 && StructurallyValid(recorded);
        var (patch, error) = log.Commit(edits);
        Assert.Equal(admissible, error is null);
        Assert.Equal(admissible, patch is not null);
        if (admissible)
        {
            Assert.Equal(history.Count - 1L, patch!.From);
            Assert.Equal(history.Count, patch.To);
            Assert.Equal(effective.OrderBy(e => e.Key), patch.Edits.OrderBy(e => e.Key));
            history.Add(recorded!);
        }

        Assert.Equal(history.Count - 1L, log.Head);
        AssertMap(history[^1], log.State);
        for (var r = 0; r < history.Count; r++) AssertMap(history[r], log.StateAt(r));
        return (applicable, admissible);
    }

    [Theory, MemberData(nameof(SeedGroups))]
    public void Ch3_committed_states_are_well_formed_and_history_is_immutable(int group) => ForCases(group, gen =>
    {
        var log = new GraphLog("g");
        var history = new List<ImmutableDictionary<ElementKey, Element>> { ImmutableDictionary<ElementKey, Element>.Empty };
        for (var i = 0; i < 6; i++)
        {
            var proposal = gen.PatchFor(log.State, log.Head, @unchecked: true)!;
            CompareWithOracle(log, history, proposal.Edits);
        }
    });

    [Fact]
    public void Ch3_guarded_application_and_commit_admission_at_their_boundaries()
    {
        var log = new GraphLog("g");
        var history = new List<ImmutableDictionary<ElementKey, Element>> { ImmutableDictionary<ElementKey, Element>.Empty };
        var entity = new Entity("a", "T", Props.Empty);
        var edge = new Rel("a", "a", "missing", "R", Props.Empty);
        var other = edge with { Id = "b" };
        var ontology = new OntologyState("schema", "1", "digest");

        void Check(bool applied, bool committed, params Edit[] edits) =>
            Assert.Equal((applied, committed), CompareWithOracle(log, history, edits));

        Check(true, false);
        Check(true, true, Edit.Create(ontology), Edit.Create(entity), Edit.Create(edge));
        Check(true, false, new Edit(entity.Key, entity, entity));
        Check(false, false, Edit.Create(other));                         // collides with existing edge
        Check(false, false, Edit.Remove(edge), Edit.Create(other), Edit.Create(other with { Id = "c" }));
        Check(false, false, Edit.Remove(edge), Edit.Create(edge));       // repeated key, even if it cancels
        Check(false, false, Edit.Remove(entity), new Edit(entity.Key, null, edge));
        Check(false, false, new Edit(ElementKey.Entity("wrong"), null, entity));
        Check(false, false, new Edit(entity.Key, edge, null));           // entity and edge share the id
        Check(false, false, Edit.Remove(entity), Edit.Remove(other));   // second guard fails atomically
        Check(true, true, Edit.Create(other), Edit.Remove(edge));       // uniqueness is on the final graph
        Check(true, true, Edit.Remove(entity), Edit.Change(ontology, ontology with { Version = "2" }));
        Check(false, true, new Edit(entity.Key, entity, entity), Edit.Remove(other)); // stale guard-only proposal
    }

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

    /// <summary>The digest is a set digest of the written pairs: the same pairs stored in two orders give one digest, also with text outside the BMP.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4000)]
    public void Def_3_5_digest_does_not_depend_on_storage_order(int entities)
    {
        var made = Enumerable.Range(0, entities).Select(i => Edit.Create(new Entity($"e{i}", "T", Props.Of(("name", Value.Text($"ä{i}😀")))))).ToList();
        var (a, b) = (new Graph(), new Graph());
        Assert.Null(a.Apply([.. made]));
        foreach (var e in Enumerable.Reverse(made)) Assert.Null(b.Apply([e]));
        Assert.Equal(entities > 1, !a.Entities.Select(e => e.Id).SequenceEqual(b.Entities.Select(e => e.Id)));
        Assert.Equal(a.Encode(), b.Encode());
        Assert.Equal(a.Digest(), b.Digest());
    }

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
