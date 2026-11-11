using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Theorem 1.7 (paper ch. 1): merge is an additive forwarding record from the larger id to the smaller; chains are acyclic and finite, rep is idempotent, uninvolved keys keep their identity, no binding is modified; the survivor does not depend on the order of merges.</summary>
public class IdentityMergeTests
{
    static (IdentityCatalog Cat, IdDomain A, IdDomain B, Interner It) Make()
    {
        var it = new Interner(); var cat = new IdentityCatalog("t", it);
        return (cat, cat.Domain("d:a", "a", ["k"], ["Exact"], "T"), cat.Domain("d:b", "b", ["k"], ["Exact"], "T"), it);
    }

    /// <summary>The id a key mints on its own (a pure function of tenant, domain and components).</summary>
    static string OwnId(IdDomain d, string key)
    {
        var it2 = new Interner(); var c2 = new IdentityCatalog("t", it2);
        var d2 = c2.Domain(d.Id, d.Name, d.CompNames, d.Canonicalizers, d.IdentifiesType);
        return it2.Get(c2.Resolve(d2, [it2.Intern(key)]));
    }

    static int R(IdentityCatalog c, IdDomain d, Interner it, string key) => c.Resolve(d, [it.Intern(key)]);
    static BindOutcome B(IdentityCatalog c, IdDomain d, Interner it, string key, int entity, IdDomain? left = null) => c.Bind(d, [it.Intern(key)], entity, left);

    [Theory]
    [InlineData(2)] [InlineData(5)] [InlineData(12)]
    public void Forwarding_chains_are_acyclic_finite_and_decrease_the_id_and_rep_is_idempotent(int n)
    {
        var (c, a, b, it) = Make();
        var hs = Enumerable.Range(0, n).Select(i => R(c, a, it, "k" + i)).ToList();
        for (var i = 1; i < n; i++)
        {
            Assert.NotEqual(BindOutcome.Conflict, B(c, b, it, "x" + i, hs[i - 1]));           // the key x_i denotes hs[i-1] ...
            Assert.NotEqual(BindOutcome.Conflict, B(c, b, it, "x" + i, hs[i]));               // ... and late evidence says it denotes hs[i] too
        }
        foreach (var h in hs)
        {
            var rep = c.Rep(h);
            Assert.Equal(rep, c.Rep(rep));                                                    // idempotent
            var steps = 0; var cur = h;
            while (c.Forwarding.TryGetValue(cur, out var next)) { Assert.True(string.CompareOrdinal(it.Get(next), it.Get(cur)) < 0); cur = next; Assert.True(++steps <= n); }   // decreasing, finite
        }
        var candidates = hs.Select(h => it.Get(h)).Concat(Enumerable.Range(1, n - 1).Select(i => OwnId(b, "x" + i)));   // the ids of the entities and the own ids of the keys that denote them
        Assert.Equal(candidates.Min(StringComparer.Ordinal), it.Get(c.Rep(hs[0])));       // one class, the smallest id survives
        Assert.All(hs, h => Assert.Equal(c.Rep(hs[0]), c.Rep(h)));
    }

    [Fact]
    public void Merge_changes_resolution_only_for_involved_keys_and_modifies_no_binding()
    {
        var (c, a, b, it) = Make();
        var h1 = R(c, a, it, "one"); var h2 = R(c, a, it, "two"); var h3 = R(c, a, it, "three");
        var pendingBefore = c.Pending.ToList();
        B(c, b, it, "x", h1); Assert.Equal(BindOutcome.Merged, B(c, b, it, "x", h2));
        Assert.Equal(h3, R(c, a, it, "three"));                                               // not involved
        Assert.Equal(c.Rep(h1), R(c, a, it, "one")); Assert.Equal(c.Rep(h2), R(c, a, it, "two"));
        Assert.Equal(c.Rep(h1), c.Rep(h2));
        Assert.Equal(pendingBefore, c.Pending.Take(pendingBefore.Count));                     // earlier records untouched; the merge only appended
        Assert.Contains(c.Pending.Skip(pendingBefore.Count), x => x.Domain == "~forward");
    }

    [Theory]
    [InlineData("012")] [InlineData("021")] [InlineData("102")] [InlineData("120")] [InlineData("201")] [InlineData("210")]
    public void The_survivor_does_not_depend_on_the_order_of_the_merges(string order)
    {
        var (c, a, b, it) = Make();
        var h = new[] { R(c, a, it, "p"), R(c, a, it, "q"), R(c, a, it, "r") };
        // merges: p~q, q~r, p~r evidence arrives in any order through keys of the second domain
        var links = new (string Key, int E)[] { ("m1", 0), ("m1", 1), ("m2", 1), ("m2", 2), ("m3", 0), ("m3", 2) };
        foreach (var ch in order) foreach (var (key, e) in links.Where((_, i) => i / 2 == ch - '0')) B(c, b, it, key, h[e]);
        Assert.All(h, x => Assert.Equal(c.Rep(h[0]), c.Rep(x)));
        var candidates = h.Select(x => it.Get(x)).Concat(new[] { "m1", "m2", "m3" }.Select(k => OwnId(b, k)));
        Assert.Equal(candidates.Min(StringComparer.Ordinal), it.Get(c.Rep(h[0])));        // the same survivor in every order
    }

    [Fact]
    public void Identities_of_different_entity_types_are_a_conflict_not_a_merge()
    {
        var it = new Interner(); var c = new IdentityCatalog("t", it);
        var a = c.Domain("d:a", "a", ["k"], ["Exact"], "T1"); var b = c.Domain("d:b", "b", ["k"], ["Exact"], "T2");
        var h1 = R(c, a, it, "one"); var h2 = R(c, b, it, "two");
        B(c, b, it, "x", h2);
        var forwarding = c.Forwarding.Count;
        Assert.Equal(BindOutcome.Conflict, B(c, b, it, "x", h1, a));
        Assert.Equal(1, c.Conflicts); Assert.Equal(forwarding, c.Forwarding.Count);
    }
}
