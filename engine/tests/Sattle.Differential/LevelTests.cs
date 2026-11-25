using System.Collections.Immutable;
using Sattle.Core;
using Sattle.Language;
using Sattle.Reference;
using static Sattle.Differential.Logs;

namespace Sattle.Differential;

/// <summary>Def. 5.17 and 5.18 hold for a level written as the shipped levels are written (Ch. 5, 5.8), on generated graphs. Per seed: items, a membership in which an item has 0, 1 or 2 groups, and calls between items with a count per window of 60 s, with retractions. The level lifts the calls to the groups by two joins and a filter on the pair, once with the join on the number of images of capabilityView (an item with two groups has none) and once without it, as serviceDeps (an item with two groups counts for both). The expectation is computed from the generated rows and not from the engine: the lifted edges are the pairs of images of the edges below, without self-loops and ends outside the image, one edge per pair, with an id that stays while the edge does; each lifted window is the sum of the windows of its fibre; where the image is a function, the lifted windows add up to the windows of the lifted edges below (Proposition 5.11). The engine also equals the reference evaluator on every case.</summary>
public class LevelTests
{
    const int Windows = 8;
    static readonly string[] Groups = ["g0", "g1", "g2", "g3"];

    static World LevelWorld(bool oneImage) => oneImage ? Named("level with one image per item") : Named("level with every image of an item");
    static World Named(string name) => new(name, "Match, Join, Filter, Group, EmitRelationship", Worlds.Files(name), []);
    static readonly World OneImage = LevelWorld(true), EveryImage = LevelWorld(false);

    // ---- the generated input ----

    static RowInput Item(string k) => new("items", Row(("k", Value.Text(k))), 1);
    static RowInput Belongs(string k, string g, long weight = 1) => new("belongs", Row(("k", Value.Text(k)), ("g", Value.Text(g))), weight);
    static RowInput Call(string src, string dst, long n, long second, long weight) => new("calls", Row(("src", Value.Text(src)), ("dst", Value.Text(dst)), ("n", Value.Integer(n)), ("t", Value.Instant(Origin + second * Second))), weight);

    /// <summary>8 to 12 items, every item in 0, 1 or `maxImages` of four groups (k0 in none, k1 in `maxImages`, the others mostly in one), four calls per item in 8 windows (self-loops among them), then changes in random order: two items move to another group and three calls lose a weight; last, the frontier passes every window.</summary>
    static ImmutableArray<Input> Generate(int seed, int maxImages)
    {
        var r = new Random(seed);
        var items = Enumerable.Range(0, r.Next(8, 13)).Select(i => "k" + i).ToArray();
        var groupsOf = items.Select((k, i) => Groups.OrderBy(_ => r.Next()).Take(i == 0 ? 0 : i == 1 ? maxImages : r.Next(5) switch { 0 => 0, 1 => maxImages, _ => 1 }).ToList()).ToArray();
        var memberships = items.SelectMany((k, i) => groupsOf[i].Select(g => Belongs(k, g))).ToList();
        var calls = Enumerable.Range(0, 4 * items.Length).Select(_ => Call(items[r.Next(items.Length)], items[r.Next(items.Length)], r.Next(6), r.Next(Windows * 60), r.Next(1, 3))).ToList();
        var changes = new List<Input>();
        foreach (var i in Enumerable.Range(2, items.Length - 2).Where(i => groupsOf[i].Count > 0).OrderBy(_ => r.Next()).Take(2))
        {
            var left = groupsOf[i][0];
            changes.Add(Belongs(items[i], left, -1));
            changes.Add(Belongs(items[i], Groups.Except(groupsOf[i]).First()));
        }
        changes.AddRange(calls.OrderBy(_ => r.Next()).Take(3).Select(c => c with { Weight = -1 }));
        return [.. items.Select(Item), .. memberships.Concat<Input>(calls).OrderBy(_ => r.Next()), .. changes.OrderBy(_ => r.Next()), new AdvanceInput(Origin + (Windows + 1) * Minute)];
    }

    // ---- Def. 5.17 and 5.18, computed from the rows ----

    /// <summary>The graph below after the rows of `log`: the groups of each item, and per edge (source, target) its count per window. An edge is there while one of its windows is.</summary>
    sealed record Below(ImmutableDictionary<string, ImmutableSortedSet<string>> GroupsOf, ImmutableDictionary<(string S, string T), ImmutableDictionary<long, decimal>> Counter);

    static Below Integrate(IEnumerable<Input> log)
    {
        var held = log.OfType<RowInput>()
            .GroupBy(x => x.Source + "\u001e" + string.Join('\u001f', x.Fields.Select(f => f.Key + "=" + Canon.Of(f.Value))))
            .Select(g => (Row: g.First(), Weight: g.Sum(x => x.Weight)))
            .Where(x => x.Weight > 0).ToArray();
        static string T(RowInput x, string f) => ((TextValue)x.Fields[f]).S;
        var groupsOf = held.Where(x => x.Row.Source == "belongs").GroupBy(x => T(x.Row, "k"))
            .ToImmutableDictionary(g => g.Key, g => g.Select(x => T(x.Row, "g")).ToImmutableSortedSet(StringComparer.Ordinal));
        var counter = held.Where(x => x.Row.Source == "calls")
            .GroupBy(x => (T(x.Row, "src"), T(x.Row, "dst")))
            .ToImmutableDictionary(e => e.Key, e => e
                .GroupBy(x => ((InstantValue)x.Row.Fields["t"]).Ns / Minute * Minute)
                .ToImmutableDictionary(w => w.Key, w => w.Sum(x => (decimal)((IntegerValue)x.Row.Fields["n"]).N * x.Weight)));
        return new(groupsOf, counter);
    }

    /// <summary>The images of an item: its group where it has exactly one (`oneImage`), else every group it has.</summary>
    static ImmutableSortedSet<string> Images(Below below, string item, bool oneImage) =>
        below.GroupsOf.GetValueOrDefault(item) is { } gs && (!oneImage || gs.Count == 1) ? gs : [];

    /// <summary>Def. 5.17: the fibre of each lifted edge `x -> y`, the edges below whose ends have the images x and y, x and y distinct. An edge with an end that has no image is in no fibre.</summary>
    static ImmutableSortedDictionary<string, ImmutableArray<(string S, string T)>> Fibres(Below below, bool oneImage) =>
        (from e in below.Counter.Keys
         from x in Images(below, e.S, oneImage)
         from y in Images(below, e.T, oneImage)
         where x != y
         group e by x + " -> " + y).ToImmutableSortedDictionary(f => f.Key, f => f.ToImmutableArray(), StringComparer.Ordinal);

    /// <summary>Def. 5.17 and 5.18: the lifted edges are the pairs that have a fibre, and the lifted count of an edge and a window is the sum over its fibre.</summary>
    static (ImmutableArray<string> Edges, ImmutableArray<string> Windows) Lift(Below below, bool oneImage)
    {
        var fibres = Fibres(below, oneImage);
        return ([.. fibres.Keys], [.. fibres.SelectMany(f => f.Value.SelectMany(e => below.Counter[e]).GroupBy(w => w.Key).Select(w => Window(f.Key, w.Key, w.Sum(x => x.Value)))).Order(StringComparer.Ordinal)]);
    }

    /// <summary>Proposition 5.11 for an image that is a function: per window, the sum of the counts of the edges below whose two ends have distinct images.</summary>
    static ImmutableArray<string> Conserved(Below below, bool oneImage) =>
    [
        .. below.Counter
            .Where(e => Images(below, e.Key.S, oneImage) is { Count: 1 } x && Images(below, e.Key.T, oneImage) is { Count: 1 } y && x[0] != y[0])
            .SelectMany(e => e.Value).GroupBy(w => w.Key).OrderBy(w => w.Key).Select(w => Total(w.Key, w.Sum(x => x.Value))),
    ];

    static string Window(string edge, long start, decimal value) => $"{edge} @ {(start - Origin) / Second:+0;-0}s = {Norm.Decimal(value)}";
    static string Total(long start, decimal value) => $"@ {(start - Origin) / Second:+0;-0}s = {Norm.Decimal(value)}";

    // ---- what the engine holds ----

    /// <summary>The edges of `type` in `graph`, named by the key of an item or the name of a group at their ends (one line per relationship, so two relationships on one pair show twice), and their `calls` windows.</summary>
    static (ImmutableArray<string> Edges, ImmutableArray<string> Windows, ImmutableDictionary<string, string> Ids) Read(SpaceFacts facts, string graph, string type)
    {
        var names = facts.Graphs.Values.SelectMany(g => g.Entities.Values)
            .Select(e => (e.Id, Name: e.Props.Where(p => p.Key.EndsWith("prop:k", StringComparison.Ordinal) || p.Key.EndsWith("prop:name", StringComparison.Ordinal)).Select(p => ((TextValue)p.Value).S).SingleOrDefault()))
            .Where(e => e.Name is not null).DistinctBy(e => e.Id).ToDictionary(e => e.Id, e => e.Name!, StringComparer.Ordinal);
        var g = facts.Graphs[graph];
        var rels = g.Rels.Values.Where(r => r.Type.EndsWith(type, StringComparison.Ordinal)).ToDictionary(r => r.Id, r => names[r.Source] + " -> " + names[r.Target], StringComparer.Ordinal);
        return (
            [.. rels.Values.Order(StringComparer.Ordinal)],
            [.. g.Windows.Where(t => rels.ContainsKey(t.Key.Element) && t.Key.Property.EndsWith("prop:calls", StringComparison.Ordinal)).Select(t => Window(rels[t.Key.Element], t.Key.Start, t.Value)).Order(StringComparer.Ordinal)],
            rels.ToImmutableDictionary(kv => kv.Value, kv => kv.Key));
    }

    /// <summary>The shape (`oneImage`: capabilityView, else serviceDeps), the seed, and the most groups an item has.</summary>
    public static TheoryData<bool, int, int> Cases => new()
    {
        { true, 2, 2 }, { true, 6, 2 }, { true, 8, 2 },
        { false, 3, 2 }, { false, 4, 2 },
        { false, 5, 1 },
    };

    [Theory, MemberData(nameof(Cases))]
    public void A_level_equals_definitions_5_17_and_5_18_on_generated_graphs(bool oneImage, int seed, int maxImages)
    {
        var world = oneImage ? OneImage : EveryImage;
        var log = Generate(seed, maxImages);
        var below = Integrate(log);
        var (edges, windows) = Lift(below, oneImage);
        // the case reaches every rule of Def. 5.17: a fibre of several edges, an edge inside one image, an edge with an end that has no image, and an edge at an item with two groups
        Assert.Contains(Fibres(below, oneImage).Values, f => f.Length > 1);
        Assert.Contains(below.Counter.Keys, e => Images(below, e.S, oneImage).Overlaps(Images(below, e.T, oneImage)));
        Assert.Contains(below.Counter.Keys, e => Images(below, e.S, oneImage).IsEmpty || Images(below, e.T, oneImage).IsEmpty);
        if (maxImages == 2) Assert.Contains(below.Counter.Keys, e => below.GroupsOf.GetValueOrDefault(e.S)?.Count == 2 || below.GroupsOf.GetValueOrDefault(e.T)?.Count == 2);

        // after every input, one row per batch: the lifted edges are Def. 5.17 on the rows so far, and an edge keeps its id while it is there
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        SpaceFacts? last = null;
        foreach (var (consumed, facts) in EngineRun.States(world.Catalog, log, Chunking.OneByOne))
        {
            var level = Read(facts, "level", "Talks");
            var expected = Lift(Integrate(log.Take(consumed)), oneImage).Edges;
            if (!expected.SequenceEqual(level.Edges)) Assert.Fail($"after input {consumed - 1}:\n{Print(log.Take(consumed))}\nDef. 5.17: {string.Join("; ", expected)}\nengine: {string.Join("; ", level.Edges)}");
            foreach (var (pair, id) in level.Ids) Assert.Equal(ids.TryAdd(pair, id) ? id : ids[pair], id);
            last = facts;
        }

        foreach (var final in new[] { last!, EngineRun.Final(world.Catalog, log, Chunking.Largest) })
        {
            // the graph below is the one the rows describe
            var master = Read(final, "master", "Edge");
            Assert.Equal<string>(below.Counter.Keys.Select(e => e.S + " -> " + e.T).Order(StringComparer.Ordinal), master.Edges);
            Assert.Equal<string>(below.Counter.SelectMany(e => e.Value.Select(w => Window(e.Key.S + " -> " + e.Key.T, w.Key, w.Value))).Order(StringComparer.Ordinal), master.Windows);

            // Def. 5.17 and 5.18
            var level = Read(final, "level", "Talks");
            Assert.Equal<string>(edges, level.Edges);
            Assert.Equal<string>(windows, level.Windows);

            // Proposition 5.11, where the image is a function
            if (oneImage || below.GroupsOf.Values.All(gs => gs.Count <= 1))
            {
                var lifted = final.Graphs["level"].Windows.Where(t => level.Ids.ContainsValue(t.Key.Element) && t.Key.Property.EndsWith("prop:calls", StringComparison.Ordinal))
                    .GroupBy(t => t.Key.Start).OrderBy(w => w.Key).Select(w => Total(w.Key, w.Sum(x => x.Value)));
                Assert.Equal<string>(Conserved(below, oneImage), lifted);
            }
        }

        // the engine equals the reference evaluator
        var reference = ReferenceEvaluator.Evaluate(world.Catalog, world.Catalog.Spaces.Keys.Single(), log);
        foreach (var (graph, expected) in reference.Graphs)
            Assert.Equal<string>(expected.Lines(), last!.Graphs[graph].Lines());
    }
}
