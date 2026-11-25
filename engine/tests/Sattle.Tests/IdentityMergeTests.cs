using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Ch. 2, Def. 2.5 and Prop. 2.3: the class of a key is a function of the links present now, named by the smallest own id of its keys. Rep is idempotent, uninvolved keys keep their identity, a merge only appends records, the survivor does not depend on the order of the links, and a withdrawn link splits a class unless another path holds it.</summary>
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
    static void L(IdentityCatalog c, Interner it, IdDomain ld, string left, IdDomain rd, string right, long w = 1) => c.Link(ld, [it.Intern(left)], rd, [it.Intern(right)], w);

    [Theory]
    [InlineData(2)] [InlineData(5)] [InlineData(12)]
    public void A_class_is_named_by_the_smallest_own_id_of_its_keys_and_rep_is_idempotent(int n)
    {
        var (c, a, b, it) = Make();
        var hs = Enumerable.Range(0, n).Select(i => R(c, a, it, "k" + i)).ToList();
        for (var i = 1; i < n; i++) { L(c, it, a, "k" + (i - 1), b, "x" + i); L(c, it, a, "k" + i, b, "x" + i); }      // the key x_i denotes k_(i-1) and k_i
        foreach (var h in hs) Assert.Equal(c.Rep(h), c.Rep(c.Rep(h)));                                               // idempotent
        var candidates = hs.Select(h => it.Get(h)).Concat(Enumerable.Range(1, n - 1).Select(i => OwnId(b, "x" + i)));
        Assert.Equal(candidates.Min(StringComparer.Ordinal), it.Get(c.Rep(hs[0])));                                  // one class, the smallest id names it
        Assert.All(hs, h => Assert.Equal(c.Rep(hs[0]), c.Rep(h)));
        Assert.Equal(2 * n - 1, Assert.Single(c.Classes).Value.Count);
    }

    [Fact]
    public void Merge_changes_resolution_only_for_involved_keys_and_modifies_no_binding()
    {
        var (c, a, b, it) = Make();
        var h1 = R(c, a, it, "one"); var h2 = R(c, a, it, "two"); var h3 = R(c, a, it, "three");
        var pendingBefore = c.Pending.ToList();
        L(c, it, a, "one", b, "x"); L(c, it, a, "two", b, "x");
        Assert.Equal(h3, R(c, a, it, "three"));                                                                       // not involved
        Assert.Equal(c.Rep(h1), R(c, a, it, "one")); Assert.Equal(c.Rep(h2), R(c, a, it, "two"));
        Assert.Equal(c.Rep(h1), c.Rep(h2));
        Assert.Equal(pendingBefore, c.Pending.Take(pendingBefore.Count));                                             // earlier records untouched; the merge only appended
        Assert.Contains(c.Pending.Skip(pendingBefore.Count), x => x.Domain == "~forward");
    }

    [Theory]
    [InlineData("012")] [InlineData("021")] [InlineData("102")] [InlineData("120")] [InlineData("201")] [InlineData("210")]
    public void The_survivor_does_not_depend_on_the_order_of_the_merges(string order)
    {
        var (c, a, b, it) = Make();
        var h = new[] { R(c, a, it, "p"), R(c, a, it, "q"), R(c, a, it, "r") };
        string[] keys = ["p", "q", "r"];
        // merges: p~q, q~r, p~r evidence arrives in any order through keys of the second domain
        var links = new (string Key, int E)[] { ("m1", 0), ("m1", 1), ("m2", 1), ("m2", 2), ("m3", 0), ("m3", 2) };
        foreach (var ch in order) foreach (var (key, e) in links.Where((_, i) => i / 2 == ch - '0')) L(c, it, a, keys[e], b, key);
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
        L(c, it, b, "two", b, "x");
        Assert.Equal(c.Rep(h2), R(c, b, it, "x"));
        L(c, it, a, "one", b, "x");
        Assert.Equal(1, c.Conflicts);
        Assert.Equal(h1, c.Rep(h1)); Assert.NotEqual(c.Rep(h1), R(c, b, it, "x"));
        Assert.Single(c.Classes);
    }

    /// <summary>Links over the keys p, q, r, s of one domain (`+pq` present, `-pq` one row of it retracted). After them the classes are the connected parts of the present links, named by their smallest own id, whatever the order; a split reports the part that leaves and its id.</summary>
    [Theory]
    [InlineData("+pq +qr -pq", "p|qr")]                // a chain split at its first link
    [InlineData("+pq +qr -qr", "pq|r")]                // ... and at its last
    [InlineData("+pq +qr +rp -pq", "pqr")]             // a link on a cycle: no split
    [InlineData("+pq +pq -pq", "pq")]                  // another row still holds the link
    [InlineData("+pq -pq +pq", "pq")]                  // withdrawn and asserted again
    [InlineData("+pq +rs +qr -qr", "pq|rs")]           // two classes joined and parted again
    [InlineData("-pq +pq", "p|q")]                     // a retraction before its insertion: the link is not present
    public void A_withdrawn_link_splits_a_class_unless_another_path_holds_it(string changes, string classes)
    {
        var (c, a, _, it) = Make();
        var splits = new List<(string Rep, string Part, string[] Members)>();
        c.OnSplit = (r, n, inPart) => splits.Add((it.Get(r), it.Get(n), [.. "pqrs".Select(k => R(c, a, it, k.ToString())).Where(inPart).Select(it.Get)]));
        foreach (var ch in changes.Split(' ')) L(c, it, a, ch[1].ToString(), a, ch[2].ToString(), ch[0] == '+' ? 1 : -1);
        var own = "pqrs".ToDictionary(k => k, k => OwnId(a, k.ToString()));
        foreach (var part in classes.Split('|'))
            foreach (var k in part) Assert.Equal(part.Select(x => own[x]).Min(StringComparer.Ordinal), it.Get(R(c, a, it, k.ToString())));
        foreach (var (rep, n, members) in splits)
        {
            Assert.DoesNotContain(rep, members);                                                                     // the representative stays
            Assert.Equal(members.Min(StringComparer.Ordinal), n);                                                     // the part that leaves is named by its smallest id
        }
    }
}
