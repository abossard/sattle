using Sattle.Core;
using Sattle.Language;
using Sattle.Reference;
using static Sattle.Differential.Logs;

namespace Sattle.Differential;

/// <summary>T-LIFT: the `lift` statement against Def. 5.17 and 5.18 computed here from the facts of the graph below, on generated graphs and mappings (nodes with no, one and two images, retracted edges and images, counters over many windows), for each images policy, both self-loop rules and a `when` on the images; and two stacked lifts against the one-step lift through the composed mapping (Proposition 5.12). The lowering of `lift` is checked against the definitions, not against itself.</summary>
public class LiftDefinitionTests
{
    const string Types = """
        entity Item { k: Text label; }
        entity Group { name: Text label; }
        entity Super { name: Text label; }
        relationship Edge (Item) -> (Item) kind communication { flows?: Integer telemetry delta sum every 60s; drops?: Integer telemetry delta sum every 60s; }
        relationship Belongs (Item) -> (Group) kind composition;
        relationship In (Group) -> (Super) kind composition;
        relationship ItemIn (Item) -> (Super) kind composition;
        relationship Talks (Group) -> (Group) kind communication { flows?: Integer telemetry delta sum every 60s; drops?: Integer telemetry delta sum every 60s; dropRatio?: Decimal telemetry gauge max every 60s; }
        relationship Reaches (Super) -> (Super) kind communication { flows?: Integer telemetry delta sum every 60s; drops?: Integer telemetry delta sum every 60s; }
        identity item for Item version "1" { k: Text by exact; }
        identity group for Group version "1" { name: Text by exact; }
        identity super for Super version "1" { name: Text by exact; }
        rows ItemRow { k: Text; g: Text; sg: Text; }
        rows EdgeRow { src: Text; dst: Text; f: Integer; d: Integer; t: Instant; }
        projection ingest ingestion output w {
          in items: rows ItemRow;
          in edges: rows EdgeRow;
          rule observed readonly "differential" {
            emit entity Item from items identity item(k: k) set { k: k };
            emit entity Group from items identity group(name: g) set { name: g };
            emit entity Super from items identity super(name: sg) set { name: sg };
            emit relationship Belongs from items key belongs: item(k: k) -> group(name: g);
            emit relationship In from items key in: group(name: g) -> super(name: sg);
            emit relationship ItemIn from items key itemIn: item(k: k) -> super(name: sg);
            let ends = edges |> select { src: src, dst: dst } |> distinct;
            emit relationship Edge from ends key edge: item(k: src) -> item(k: dst);
            let w = edges |> window on t width 60s late revise;
            let c = w |> group by { src: src, dst: dst, windowStart: windowStart } aggregate { flows: sum(f), drops: sum(d) };
            emit update relationship Edge from c key edge: item(k: src) -> item(k: dst) set { flows: flows, drops: drops };
          }
        }
        """;

    static World WorldOf(string clauses, bool keyedItems) => new($"lift {clauses}", "Lift", [new SourceFile("w.sattle", $$"""
        module w @ 1.0.0;
        {{Types}}
        projection groups view forward output w {
          in master: graph w;
          let all = match master (g: Group);
          emit entity Group from all identity g set { name: g.name };
          lift talks as Talks from master Edge via master Belongs derive dropRatio = if(flows > 0, drops / flows, 0.0) {{clauses}};
        }
        projection supers view forward output w {
          in master: graph w;
          in level: graph w;
          let all = match master (s: Super);
          emit entity Super from all identity s set { name: s.name };
          lift reachesTwice as Reaches from level Talks via master In images reject;
        }
        projection direct view forward output w {
          in master: graph w;
          let all = match master (s: Super);
          emit entity Super from all identity s set { name: s.name };
          lift reachesOnce as Reaches from master Edge via master ItemIn images reject;
        }
        space s {
          source items adapter "sattle.authoring" rows ItemRow;
          source edges adapter "sattle.authoring" rows EdgeRow;
          master graph master = ingest(items: items, edges: edges);
          graph view = groups(master: master);
          graph twice = supers(master: master, level: view);
          graph once = direct(master: master);
        }
        """)], [
            new("items", (r, _) => { var g = ((TextValue)Text(r, "g", 4)).S; return Row(("k", Text(r, "k", 8)), ("g", Value.Text(g)), ("sg", Value.Text("s" + (g[^1] - '0') % 2))); }, keyedItems ? f => ((TextValue)f["k"]).S : null),
            new("edges", (r, now) => Row(("src", Text(r, "k", 8)), ("dst", Text(r, "k", 8)), ("f", Value.Integer(r.Next(1, 9))), ("d", Value.Integer(r.Next(0, 3))), ("t", Near(r, now)))),
        ], AdvanceShare: 0.25);

    sealed record Lower(Dictionary<string, string> Name, Dictionary<string, (string S, string T)> Edges, Dictionary<string, List<string>> Images, Dictionary<(string Edge, string Prop, long W), decimal> Windows);

    static Lower Below(GraphFacts master, string mapType, string? excluded, bool reject)
    {
        var name = master.Entities.Values.ToDictionary(e => e.Id, e => e.Props.Values.OfType<TextValue>().First().S);
        var edges = master.Rels.Values.Where(r => r.Type.EndsWith("rel:Edge", StringComparison.Ordinal)).ToDictionary(r => r.Id, r => (r.Source, r.Target));
        var images = master.Rels.Values.Where(r => r.Type.EndsWith("rel:" + mapType, StringComparison.Ordinal) && (excluded is null || name[r.Target] != excluded))
            .GroupBy(r => r.Source).ToDictionary(g => g.Key, g => g.Select(r => r.Target).Distinct().ToList());
        if (reject) images = images.Where(kv => kv.Value.Count == 1).ToDictionary(kv => kv.Key, kv => kv.Value);
        var windows = master.Telemetry.Where(w => edges.ContainsKey(w.Key.Element)).ToDictionary(w => (w.Key.Element, w.Key.Property[(w.Key.Property.LastIndexOf(':') + 1)..], w.Key.Start), w => w.Value);
        return new(name, edges, images, windows);
    }

    /// <summary>Def. 5.17 and 5.18: the lifted edges by the names of their ends, and the lifted counters per (edge, property, window).</summary>
    static (SortedSet<string> Edges, SortedDictionary<string, decimal> Counters) Lift(Lower b, bool keepSelfLoops)
    {
        var lifted = new SortedSet<string>(StringComparer.Ordinal); var counters = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (e, (s, t)) in b.Edges)
            foreach (var x in b.Images.GetValueOrDefault(s) ?? [])
                foreach (var y in b.Images.GetValueOrDefault(t) ?? [])
                {
                    if (x == y && !keepSelfLoops) continue;
                    var pair = b.Name[x] + ">" + b.Name[y];
                    lifted.Add(pair);
                    foreach (var ((edge, prop, w), v) in b.Windows.Where(kv => kv.Key.Edge == e))
                        counters[$"{pair} {prop} {w}"] = counters.GetValueOrDefault($"{pair} {prop} {w}") + v;
                }
        return (lifted, counters);
    }

    static (SortedSet<string> Edges, SortedDictionary<string, decimal> Counters, SortedDictionary<string, decimal> Ratios) Lifted(GraphFacts g, Dictionary<string, string> name, string type)
    {
        var rels = g.Rels.Values.Where(r => r.Type.EndsWith("rel:" + type, StringComparison.Ordinal)).ToDictionary(r => r.Id, r => name[r.Source] + ">" + name[r.Target]);
        var counters = new SortedDictionary<string, decimal>(StringComparer.Ordinal); var ratios = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var w in g.Telemetry.Where(w => rels.ContainsKey(w.Key.Element)))
        {
            var prop = w.Key.Property[(w.Key.Property.LastIndexOf(':') + 1)..];
            (prop == "dropRatio" ? ratios : counters)[$"{rels[w.Key.Element]} {prop} {w.Key.Start}"] = w.Value;
        }
        return (new SortedSet<string>(rels.Values, StringComparer.Ordinal), counters, ratios);
    }

    [Theory]
    [InlineData("images each", false, null, 1)] [InlineData("images each", false, null, 2)]
    [InlineData("images each selfloops keep", true, null, 3)]
    [InlineData("images reject", false, null, 4)] [InlineData("images reject", false, null, 5)]
    [InlineData("images reject selfloops keep", true, null, 6)]
    [InlineData("when image.name != \"g2\" images each", false, "g2", 7)]
    public void A_lift_equals_definitions_5_17_and_5_18_on_generated_graphs(string clauses, bool keepSelfLoops, string? excluded, int seed)
    {
        var w = WorldOf(clauses, keyedItems: false);
        var (twoImages, windows) = (0, 0);
        foreach (var s in Enumerable.Range(seed * 10, 4))
        {
            var facts = EngineRun.Final(w.Catalog, w.Log(s, 120), Chunking.Random(s));
            var below = Below(facts.Graphs["master"], "Belongs", excluded, clauses.Contains("reject"));
            var (edges, counters) = Lift(below, keepSelfLoops);
            var actual = Lifted(facts.Graphs["view"], below.Name, "Talks");
            Assert.Equal(edges, actual.Edges);                        // Def. 5.17
            Assert.Equal(counters, actual.Counters);                  // Def. 5.18: the fibre sums per window
            foreach (var (key, flows) in actual.Counters.Where(kv => kv.Key.Contains(" flows ")))      // the ratio is recomputed from the lifted counters
                Assert.Equal(flows > 0 ? actual.Counters.GetValueOrDefault(key.Replace(" flows ", " drops ")) / flows : 0m, actual.Ratios[key.Replace(" flows ", " dropRatio ")]);
            if (clauses.Contains("reject") && !keepSelfLoops)      // Proposition 5.11: what is lifted is exactly the traffic between two images
                foreach (var prop in new[] { "flows", "drops" })
                    foreach (var start in below.Windows.Keys.Select(k => k.W).Distinct())
                        Assert.Equal(below.Windows.Where(kv => kv.Key.Prop == prop && kv.Key.W == start && below.Images.TryGetValue(below.Edges[kv.Key.Edge].S, out var x) && below.Images.TryGetValue(below.Edges[kv.Key.Edge].T, out var y) && x[0] != y[0]).Sum(kv => kv.Value),
                            actual.Counters.Where(kv => kv.Key.EndsWith($" {prop} {start}", StringComparison.Ordinal)).Sum(kv => kv.Value));
            twoImages += below.Images.Count(kv => kv.Value.Count > 1) + facts.Graphs["master"].Rels.Values.Where(r => r.Type.EndsWith("rel:Belongs", StringComparison.Ordinal)).GroupBy(r => r.Source).Count(g => g.Count() > 1);
            windows = Math.Max(windows, below.Windows.Keys.Select(k => k.W).Distinct().Count());
        }
        Assert.True(windows >= 8, $"counters over {windows} windows");
        Assert.True(twoImages > 0, "no node with two images");
    }

    /// <summary>Proposition 5.12: lifting the edges to groups and the result to supers gives the edges and counters of lifting the edges to supers through the composed mapping.</summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Two_stacked_lifts_equal_the_lift_through_the_composed_mapping(int seed)
    {
        var w = WorldOf("images reject", keyedItems: true);
        var lifted = 0;
        foreach (var s in Enumerable.Range(seed * 10, 6))
        {
            var facts = EngineRun.Final(w.Catalog, w.Log(s, 120), Chunking.Random(s));
            var name = facts.Graphs["master"].Entities.Values.ToDictionary(e => e.Id, e => e.Props.Values.OfType<TextValue>().First().S);
            var twice = Lifted(facts.Graphs["twice"], name, "Reaches"); var once = Lifted(facts.Graphs["once"], name, "Reaches");
            lifted += once.Edges.Count;
            Assert.Equal(once.Edges, twice.Edges);
            Assert.Equal(once.Counters, twice.Counters);
        }
        Assert.True(lifted > 0, "no edge between two supers on these seeds");
    }
}
