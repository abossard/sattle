using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The hot layout (interned handles, byte pools, intrusive adjacency, open-addressing uniqueness index) against the reference model, on random edit sequences.</summary>
public class HotGraphTests
{
    /// <summary>Each seed with the graph's texts in a table of its own and in an engine's interner.</summary>
    public static IEnumerable<object[]> Seeds() => Enumerable.Range(0, 8).SelectMany(i => new[] { new object[] { i, false }, [i, true] });

    [Theory, MemberData(nameof(Seeds))]
    public void The_hot_graph_agrees_with_the_reference_graph_on_every_operation(int group, bool interner)
    {
        var texts = interner ? new Interner() : null;
        int accepted = 0, rejected = 0;
        for (var c = 0; c < 150; c++)
        {
            var gen = new Gen(group * 9973 + c);
            var hot = new Graph(texts); var reference = new ReferenceGraph();
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

    [Theory]
    [InlineData("0")]
    [InlineData("-12.50")]
    [InlineData("1.5000000000")]
    [InlineData("0.0000000000000000000000000001")]
    [InlineData("-18446744073709551615")]
    [InlineData("18446744073709551616")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("-7.9228162514264337593543950335")]
    public void A_decimal_comes_back_bit_for_bit(string text)
    {
        var d = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        var g = new Graph();
        Assert.Null(g.Apply([Edit.Create(new Entity("e1", "T", Props.Of(("v", Value.Decimal(d)))))]));
        var back = Assert.IsType<DecimalValue>(g.Entity("e1")!.Props.Items.Single().Value).Q;
        Assert.Equal(decimal.GetBits(d), decimal.GetBits(back));
    }

    /// <summary>The props pool holds what the live elements have: an element that changes, or goes and comes back, reuses its block.</summary>
    [Theory]
    [InlineData("an update")]
    [InlineData("an update of a list that grows and shrinks")]
    [InlineData("a removal and a create")]
    public void The_props_pool_stays_proportional_to_the_live_elements(string change)
    {
        var g = new Graph();
        var names = Enumerable.Range(0, 200).Select(i => "e" + i).ToList();
        Entity At(string id, int round) => new(id, "T", Props.Of(("n", Value.Integer(round)),
            ("s", change == "an update of a list that grows and shrinks" ? Value.List([.. Enumerable.Range(0, round % 40).Select(i => Value.Integer(i))]) : Value.Text("t" + round % 7))));
        Assert.Null(g.Apply([.. names.Select(n => Edit.Create(At(n, 0)))]));
        long bytesAt100 = 0;
        for (var round = 1; round <= 2000; round++)
        {
            var edits = change == "a removal and a create"
                ? (round % 2 == 1 ? names.Select(n => Edit.Remove(At(n, round - 1))) : names.Select(n => Edit.Create(At(n, round)))).ToList()
                : [.. names.Select(n => Edit.Change(At(n, round - 1), At(n, round)))];
            Assert.Null(g.Apply(edits));
            if (round == 100) bytesAt100 = g.ApproximateBytes;
        }
        Assert.Equal(At("e7", 2000), g.Entity("e7"));
        Assert.True(g.ApproximateBytes <= 2 * bytesAt100, $"{g.ApproximateBytes:N0} bytes after 2,000 rounds against {bytesAt100:N0} after 100");
    }

    [Fact]
    public void The_bytes_of_removed_elements_are_reclaimed()
    {
        var g = new Graph();
        var all = Enumerable.Range(0, 20_000).Select(i => new Entity("e" + i, "T", Props.Of(("a", Value.Integer(i)), ("b", Value.Text("t" + i % 50)), ("c", Value.Decimal(i / 8m))))).ToList();
        Assert.Null(g.Apply([.. all.Select(Edit.Create)]));
        var full = g.ApproximateBytes;
        Assert.Null(g.Apply([.. all.Skip(1000).Select(Edit.Remove)]));
        Assert.True(g.ApproximateBytes < full, $"{g.ApproximateBytes:N0} bytes after removing 19,000 of 20,000 elements, {full:N0} before");
        Assert.All(all.Take(1000), e => Assert.Equal(e, g.Entity(e.Id)));
        Assert.Null(g.Apply([.. all.Skip(1000).Take(10).Select(Edit.Create)]));
        Assert.Equal(all[1005], g.Entity("e1005"));
    }

    /// <summary>A read by an id the graph never had adds no text to the engine's interner.</summary>
    [Theory]
    [InlineData("Entity")]
    [InlineData("Rel")]
    [InlineData("HasEntity")]
    [InlineData("OutRels")]
    [InlineData("FindRel")]
    public void A_read_by_an_unknown_id_adds_nothing_to_the_text_table(string read)
    {
        var texts = new Interner();
        var g = new Graph(texts);
        Assert.Null(g.Apply([Edit.Create(new Entity("a", "T", Props.Empty)), Edit.Create(new Entity("b", "T", Props.Empty)), Edit.Create(new Rel("r", "a", "b", "R", Props.Empty))]));
        var count = texts.Count;
        object? result = read switch
        {
            "Entity" => g.Entity("never"), "Rel" => g.Rel("never"), "HasEntity" => g.HasEntity("never") ? true : null,
            "OutRels" => g.OutRels("never").FirstOrDefault(), _ => g.FindRel("a", "never", "R"),
        };
        Assert.Null(result);
        Assert.Equal(count, texts.Count);
        Assert.Equal(new Rel("r", "a", "b", "R", Props.Empty), g.FindRel("a", "b", "R"));
    }

    /// <summary>`EntityProp` and `EntityType` read one property or the type of an entity without making it: the same as reading the made entity, null where it has none.</summary>
    [Theory]
    [InlineData("a", "n")]
    [InlineData("a", "s")]
    [InlineData("a", "d")]
    [InlineData("a", "l")]
    [InlineData("a", "w")]
    [InlineData("a", "absent")]
    [InlineData("b", "n")]
    [InlineData("gone", "n")]
    [InlineData("nobody", "n")]
    [InlineData("r", "w")]
    public void One_property_or_the_type_reads_as_from_the_entity(string id, string key)
    {
        var g = new Graph(new Interner());
        var a = new Entity("a", "T", Props.Of(("n", Value.Integer(3)), ("s", Value.Text("x")), ("d", Value.Decimal(1.50m)), ("l", Value.List([Value.Entity("b"), Value.Text("y")])), ("w", Value.Boolean(false))));
        Assert.Null(g.Apply([Edit.Create(a), Edit.Create(new Entity("b", "U", Props.Empty)), Edit.Create(new Entity("gone", "T", Props.Of(("n", Value.Integer(1))))), Edit.Create(new Rel("r", "a", "b", "R", Props.Of(("w", Value.Boolean(true)))))]));
        Assert.Null(g.Apply([Edit.Remove(g.Entity("gone")!)]));
        var made = g.Entity(id);
        Assert.Equal(made?.Type, g.EntityType(id));
        Assert.Equal(made is not null && made.Props.TryGet(key, out var v) ? v : null, g.EntityProp(id, key));
    }

    /// <summary>`Holds` compares an element given in interner codes with the graph's, without making a string or an element.</summary>
    [Theory]
    [InlineData("the same entity", true)]
    [InlineData("another type", false)]
    [InlineData("another value", false)]
    [InlineData("a removed entity", false)]
    [InlineData("a decimal of another scale (equal as a value, so the caller compares elements)", false)]
    [InlineData("the same relationship", true)]
    [InlineData("another endpoint", false)]
    [InlineData("an entity without props", true)]
    public void Holds_compares_an_element_given_in_codes(string change, bool want)
    {
        var it = new Interner();
        var g = new Graph(it);
        var a = new Entity("a", "T", Props.Of(("n", Value.Integer(3)), ("s", Value.Text("x")), ("d", Value.Decimal(1.5m)), ("l", Value.List([Value.Entity("b")]))));
        var b = new Entity("b", "T", Props.Empty);
        var r = new Rel("r", "a", "b", "R", Props.Of(("w", Value.Boolean(true))));
        Assert.Null(g.Apply([Edit.Create(a), Edit.Create(b), Edit.Create(r)]));
        if (change == "a removed entity") Assert.Null(g.Apply([Edit.Remove(r), Edit.Remove(a)]));
        var w = new PropBlockWriter();
        int C(string s) => it.Intern(s);
        void BlockOfA(decimal d, long n)      // keys in the order of their texts: d, l, n, s
        {
            w.Clear(); w.Count(4);
            w.Key(C("d")); w.Decimal(d); w.Key(C("l")); w.List(1); w.Entity(C("b")); w.Key(C("n")); w.Integer(n); w.Key(C("s")); w.Text(C("x"));
        }
        void BlockOfR() { w.Clear(); w.Count(1); w.Key(C("w")); w.Boolean(true); }
        bool got;
        switch (change)
        {
            case "the same relationship": BlockOfR(); got = g.Holds(C("r"), C("R"), C("a"), C("b"), w.Block); break;
            case "another endpoint": BlockOfR(); got = g.Holds(C("r"), C("R"), C("b"), C("b"), w.Block); break;
            case "an entity without props": w.Clear(); w.Count(0); got = g.Holds(C("b"), C("T"), -1, -1, w.Block); break;
            default:
                BlockOfA(change.StartsWith("a decimal") ? 1.50m : 1.5m, change == "another value" ? 4 : 3);
                got = g.Holds(C("a"), C(change == "another type" ? "U" : "T"), -1, -1, w.Block);
                break;
        }
        Assert.Equal(want, got);
    }
}
