using System.Collections.Immutable;
using Sattle.Core;
using Sattle.Language;
using Sattle.Reference;
using static Sattle.Differential.Logs;
namespace Sattle.Differential;

/// <summary>What the language expresses with no operator of its own, and what it refuses (Ch. 5, 5.9, claims E2 to E7 and E9): the difference of two relations by tags and a group, the product of two relations by a join on a constant, and a conjunctive pattern of two edges with homomorphism semantics. On generated logs the engine equals the reference evaluator however the log is cut, and the reference result is the set the construction stands for, computed from the integrated rows or from the master graph. A form outside the language is refused when the definitions are loaded, with the reason the paper quotes.</summary>
public class ExpressivenessTests
{
    const string R = "rows R { k: Text; g: Text; }";

    static readonly SourceSpec A = Bag("a"), B = Bag("b");
    static SourceSpec Bag(string name) => new(name, (r, _) => Row(("k", Text(r, "k", 4)), ("g", Text(r, "g", 2))));

    /// <summary>An ingestion of the rows `a` and `b`.</summary>
    static World Rows(string name, string kind) => new(name, kind, Worlds.Files(name), [A, B]);

    /// <summary>A − B on the column `k`: the rows of `a` tagged (1, 0), those of `b` tagged (0, 1), summed per key; a key of `a` that no row of `b` has keeps the tags (n, 0).</summary>
static readonly World Difference = Rows("difference by tags and a group", "Union, Group, Filter") with { Witnesses = ["Only"] };

    /// <summary>A × B on the column `k`: both sides get a constant column, and the join is an equality of the constants.</summary>
    static readonly World Product = Rows("product by a join on a constant", "Map, Join") with { Witnesses = ["Pair"] };

    static readonly SourceSpec Items = new("a", (r, _) => Row(("k", Text(r, "k", 4)), ("g", Value.Text("g0"))), f => ((TextValue)f["k"]).S);
    static readonly SourceSpec Edges = new("edges", (r, _) => Row(("src", Text(r, "k", 4)), ("dst", Text(r, "k", 4))), f => ((TextValue)f["src"]).S + ">" + ((TextValue)f["dst"]).S);

    /// <summary>The pattern x → y → z over `Edge`: one row per pair of edges where the first ends at the start of the second; x and z may be one item (homomorphism semantics).</summary>
    static readonly World TwoEdges = new("pattern of two edges", "Match", Worlds.Files("pattern of two edges"), [Items, Edges, Edges]) { Witnesses = ["Walk"] };

    public static TheoryData<string> WorldNames => [.. new[] { Difference, Product, TwoEdges }.Select(w => w.Name)];
    static World Named(string name) => new[] { Difference, Product, TwoEdges }.Single(w => w.Name == name);

    [Theory, MemberData(nameof(WorldNames))]
    public void The_engine_equals_the_reference_however_the_log_is_cut(string world)
    {
        var report = Differential.Check(Named(world), seeds: 12, length: 14, reordered: false);
        Assert.True(report is null, report);
    }

    /// <summary>The construction denotes the set it stands for, after every prefix of every default log, and is not empty on some of them.</summary>
    [Theory, MemberData(nameof(WorldNames))]
    public void The_construction_denotes_its_set(string world)
    {
        var w = Named(world);
        var nonEmpty = 0;
        foreach (var seed in Enumerable.Range(0, 12))
        {
            var log = w.Log(seed, 14);
            var result = ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log);
            var (expected, actual) =
                w == Difference ? (Keys(log, "a").Except(Keys(log, "b")), Props(result.Graphs["master"], "k"))
                : w == Product ? ((from l in Keys(log, "a") from r in Keys(log, "b") select l + "|" + r).ToImmutableSortedSet(StringComparer.Ordinal), Props(result.Graphs["master"], "left", "right"))
                : (Walks(result.Graphs["master"]), Props(result.Graphs["view"], "x", "y", "z"));
            Assert.Equal(expected, actual);
            if (!actual.IsEmpty) nonEmpty++;
        }
        Assert.True(nonEmpty > 0, "every default log gives the empty set");
    }

    /// <summary>The values of `k` of the rows of `source` with positive integrated weight.</summary>
    static ImmutableSortedSet<string> Keys(IEnumerable<Input> log, string source) =>
        log.OfType<RowInput>().Where(r => r.Source == source)
            .GroupBy(r => string.Join(",", r.Fields.Select(f => f.Key + "=" + Canon.Of(f.Value))))
            .Where(g => g.Sum(r => r.Weight) > 0)
            .Select(g => ((TextValue)g.First().Fields["k"]).S)
            .ToImmutableSortedSet(StringComparer.Ordinal);

    /// <summary>The text properties `names` of every entity of the graph that has them, joined by '|'.</summary>
    static ImmutableSortedSet<string> Props(GraphFacts g, params string[] names) =>
        g.Entities.Values.Where(e => names.All(n => Prop(e, n) is not null))
            .Select(e => string.Join("|", names.Select(n => Prop(e, n))))
            .ToImmutableSortedSet(StringComparer.Ordinal);

    /// <summary>The text of the property `name` of an entity (a property is stored by its id, which ends in `prop:name`).</summary>
    static string? Prop(EntityFact e, string name) =>
        e.Props.Where(kv => kv.Key.EndsWith("prop:" + name, StringComparison.Ordinal)).Select(kv => ((TextValue)kv.Value).S).SingleOrDefault();

    /// <summary>The homomorphisms of x → y → z into the `Edge` relationships between items of the master, as the keys of x, y and z.</summary>
    static ImmutableSortedSet<string> Walks(GraphFacts master)
    {
        var key = master.Entities.Values.Where(e => Prop(e, "k") is not null).ToDictionary(e => e.Id, e => Prop(e, "k")!, StringComparer.Ordinal);
        var edges = master.Rels.Values.Where(r => key.ContainsKey(r.Source) && key.ContainsKey(r.Target)).ToList();
        return (from e in edges from f in edges where e.Target == f.Source select key[e.Source] + "|" + key[e.Target] + "|" + key[f.Target])
            .ToImmutableSortedSet(StringComparer.Ordinal);
    }

    // ---- what the language refuses ----

    /// <summary>The world "match nodes" with the view `v` made of the gkql `verbs` and `wires`, over the master of items and their hits; `input` is what `v` reads, `graphs` the graphs of the space that run it.</summary>
    static SourceFile[] View(string verbs, string wires, string input = """{"name": "master", "ontology": "w.base"}""", string graphs = """{"name": "view", "projection": "view.v", "master": false, "inputs": {"master": "master"}}""")
    {
        var files = Worlds.Files("match nodes");
        var space = files.Single(f => f.Path == "w.space.module.gkql.json");
        return [.. files.Where(f => f.Path is not ("w.view.v.gkql.json" or "w.space.module.gkql.json")),
            new("w.view.v.gkql.json", $$"""{"graph": "v", "ontology": "w.view", "history": 0, "inputs": [{{input}}], "parameters": [], "verbs": [{{verbs}}], "wires": [{{wires}}]}"""),
            space with { Text = space.Text.Replace("""{"name": "view", "projection": "view.v", "master": false, "inputs": {"master": "master"}}""", graphs, StringComparison.Ordinal) }];
    }

    const string ItemMatch = """{"id": "items", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "i", "label": "Item"}]}}""";
    const string HitSeries = ItemMatch + """, {"id": "hits", "verb": "series", "graph": "master", "series": {"element": "i", "property": "hits", "lookback": "3m"}}""";
    /// <summary>A sink of `Copy` named by the column `k`, with `g` and `n`.</summary>
    const string Copy = """{"id": "copy", "verb": "nodes", "target": {"nodes": [{"var": "c", "label": "Copy", "id": {"identity": "copy", "args": [{"ref": "k"}]}, "props": {"k": {"ref": "k"}, "g": {"ref": "g"}, "n": {"ref": "n"}}}]}}""";
    static string Wire(string from, string to, string port = "in") => $$"""{"from": "{{from}}", "to": "{{to}}", "port": "{{port}}"}""";
    static string Columns(string id, string columns) => $$"""{"id": "{{id}}", "verb": "project", "columns": """ + "{" + columns + "}}";
    const string KGN = """ "k": {"ref": "d.k"}, "g": {"ref": "d.g"}, "n": {"ref": "d.n"}""";

    public static TheoryData<string, SourceFile[], string> Refused => new()
    {
        { "E4: a path deeper than the kernel's bound", View(ItemMatch + """, {"id": "reach", "verb": "traverse", "graph": "master", "traverse": {"edge": "Edge", "from": "i", "to": "d", "min": 1, "max": 65}}, """ + Columns("keys", KGN) + ", " + Copy,
            Wire("items", "reach") + ", " + Wire("reach", "keys") + ", " + Wire("keys", "copy")), "depth must satisfy 0 <= min <= max <= 64" },
        { "E3: an edge from a node to itself in a pattern", View("""{"id": "loop", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "i", "label": "Item"}], "edges": [{"var": "e", "label": "Edge", "from": "i", "to": "i"}]}}, """ + Columns("keys", """ "k": {"ref": "i.k"}, "g": {"ref": "i.g"}, "n": {"ref": "i.n"}""") + ", " + Copy,
            Wire("loop", "keys") + ", " + Wire("keys", "copy")), "an edge from a node to itself" },
        { "E1: an order between two elements", View("""{"id": "pairs", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "x", "label": "Item"}, {"var": "d", "label": "Item"}], "edges": [{"var": "e", "label": "Edge", "from": "x", "to": "d"}]}}, {"id": "ordered", "verb": "where", "condition": {"fn": "<", "args": [{"ref": "x"}, {"ref": "d"}]}}, """ + Columns("keys", KGN) + ", " + Copy,
            Wire("pairs", "ordered") + ", " + Wire("ordered", "keys") + ", " + Wire("keys", "copy")), "LessThan needs two ordered operands" },
        { "E2: a filter on a measurement (P4)", View(HitSeries + """, {"id": "high", "verb": "where", "condition": {"fn": ">", "args": [{"ref": "hits"}, {"lit": 2}]}}, """ + Columns("keys", """ "k": {"ref": "i.k"}, "g": {"ref": "i.g"}, "n": {"ref": "i.n"}""") + ", " + Copy,
            Wire("items", "hits") + ", " + Wire("hits", "high") + ", " + Wire("high", "keys") + ", " + Wire("keys", "copy")), "filter predicate must be (Boolean, req, data)" },
        { "E2: a join on a measurement (P4)", View(HitSeries + ", " + Columns("measured", """ "k": {"ref": "i.k"}, "v": {"ref": "hits"}""") + ", " + Columns("ones", """ "k2": {"ref": "i.k"}, "g": {"ref": "i.g"}, "n": {"ref": "i.n"}, "v2": {"lit": 1}""")
            + """, {"id": "same", "verb": "join", "kind": "inner", "on": [{"left": "v", "right": "v2"}]}, """ + Copy,
            Wire("items", "hits") + ", " + Wire("hits", "measured") + ", " + Wire("items", "ones") + ", " + Wire("measured", "same", "left") + ", " + Wire("ones", "same", "right") + ", " + Wire("same", "copy")), "join takes data rows: no windowed or telemetry input" },
        { "E2: a group key that is a measurement (P4)", View(HitSeries + """, {"id": "byValue", "verb": "summarize", "by": {"k": {"ref": "i.k"}, "hits": {"ref": "hits"}, "windowStart": {"ref": "windowStart"}}, "aggregates": {"c": {"fn": "count"}}}""",
            Wire("items", "hits") + ", " + Wire("hits", "byValue")), "must be (κ, req, data)" },
        { "E6: verbs that read each other", View(Columns("x", """ "k": {"ref": "k"}""") + ", " + Columns("y", """ "k": {"ref": "k"}"""), Wire("x", "y") + ", " + Wire("y", "x")), "the wires make a loop" },
        { "E6: two graphs that read each other", View("""{"id": "outs", "verb": "graph-match", "graph": "g", "pattern": {"nodes": [{"var": "i", "label": "Copy"}]}}, """ + Columns("keys", """ "k": {"ref": "i.k"}, "g": {"ref": "i.g"}, "n": {"ref": "i.n"}""") + ", " + Copy,
            Wire("outs", "keys") + ", " + Wire("keys", "copy"), """{"name": "g", "ontology": "w.view"}""",
            """{"name": "x", "projection": "view.v", "master": false, "inputs": {"g": "y"}}, {"name": "y", "projection": "view.v", "master": false, "inputs": {"g": "x"}}"""), "graph production has a cycle" },
        { "E6: a graph that reads itself", View("""{"id": "outs", "verb": "graph-match", "graph": "g", "pattern": {"nodes": [{"var": "i", "label": "Copy"}]}}, """ + Columns("keys", """ "k": {"ref": "i.k"}, "g": {"ref": "i.g"}, "n": {"ref": "i.n"}""") + ", " + Copy,
            Wire("outs", "keys") + ", " + Wire("keys", "copy"), """{"name": "g", "ontology": "w.view"}""", """{"name": "x", "projection": "view.v", "master": false, "inputs": {"g": "x"}}"""), "graph production has a cycle" },
        { "E9: an order over rows", View(ItemMatch + """, {"id": "previous", "verb": "serialize"}""", Wire("items", "previous")), "unknown verb 'serialize'" },
        { "E9: a group by an identity (#114)", View(ItemMatch + """, {"id": "named", "verb": "project", "columns": {"p": {"identity": "copy", "args": [{"ref": "i.k"}]}}}, {"id": "byName", "verb": "summarize", "by": {"p": {"ref": "p"}}, "aggregates": {"c": {"fn": "count"}}}""",
            Wire("items", "named") + ", " + Wire("named", "byName")), "identity(...) stands only in the key of a nodes statement" },
    };

    [Theory, MemberData(nameof(Refused))]
    public void A_form_outside_the_language_is_refused_with_its_reason(string what, SourceFile[] files, string says)
    {
        var errors = new Catalog(EngineRun.Projections).Load(files, updatePins: true).Diagnostics.Where(d => d.IsError).Select(d => d.Message).ToArray();
        Assert.True(errors.Any(e => e.Contains(says, StringComparison.Ordinal)), $"{what}: expected \"{says}\", found {(errors.Length == 0 ? "no error" : string.Join("; ", errors))}");
    }

    // ---- a group by an element that a link merges (#114) ----

    /// <summary>Instances by uid, each linked to the name it is seen under, so a uid seen under two names merges them into one instance; calls name their instance by name. A view counts the calls of each instance by the instance it matches (Def. 5.9). A group by the identity column `identity("podName", src)` in the master would stay split into one group per name, and the front end refuses it.</summary>
    static readonly World GroupByMergedInstance = new("a group by an instance whose names a link merges", "Link, Match, Group, EmitEntity in a view", Worlds.Files("a group by an instance whose names a link merges"),
        [
            new("instances", (r, _) => Row(("uid", Text(r, "u", 2)), ("name", Text(r, "a", 3)))),
            new("calls", (r, _) => Row(("src", Text(r, "a", 3)), ("at", Text(r, "t", 5)))),
            new("calls", (r, _) => Row(("src", Text(r, "a", 3)), ("at", Text(r, "t", 5)))),
        ]) { Witnesses = ["prop:calls="] };

    [Fact]
    public void A_group_by_a_merged_instance_agrees_with_the_reference_however_the_log_is_cut()
    {
        var report = Differential.Check(GroupByMergedInstance, seeds: 12, length: 14, reordered: false);
        Assert.True(report is null, report);
    }

    /// <summary>Def. 5.9: a merge re-keys a group by an element. After every default log, the calls of the instances add up to the calls whose name belongs to an instance, counted once per instance over all its names, and no instance has two values for its calls; some logs merge two names of one pod.</summary>
    [Fact]
    public void A_group_by_a_merged_instance_counts_it_once_over_all_its_names()
    {
        var w = GroupByMergedInstance;
        var merged = 0;
        foreach (var seed in Enumerable.Range(0, 12))
        {
            var log = w.Log(seed, 14);
            var view = ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log).Graphs["view"];
            // the links present at the end (Ch. 2, Def. 2.5): every instances row of the integrated input joins its uid and its name
            var parent = new Dictionary<string, string>(StringComparer.Ordinal);
            string Find(string x) => parent.TryGetValue(x, out var p) && p != x ? parent[x] = Find(p) : x;
            foreach (var f in Integrated(log, "instances"))
            {
                var (u, n) = (Find("uid:" + ((TextValue)f["uid"]).S), Find("name:" + ((TextValue)f["name"]).S));
                if (u != n) parent[u] = n;
            }
            var present = Integrated(log, "instances").Select(f => Find("uid:" + ((TextValue)f["uid"]).S)).ToHashSet(StringComparer.Ordinal);
            var expected = Integrated(log, "calls").Select(f => (Src: ((TextValue)f["src"]).S, At: ((TextValue)f["at"]).S)).Distinct()
                .Count(c => present.Contains(Find("name:" + c.Src)));
            if (parent.Keys.Concat(parent.Values).Where(k => k.StartsWith("name:", StringComparison.Ordinal)).Distinct().GroupBy(Find).Any(g => g.Count() > 1)) merged++;
            var calls = view.Entities.Values.SelectMany(e => e.Props.Where(kv => kv.Key.EndsWith("prop:calls", StringComparison.Ordinal))).Sum(kv => (long)((IntegerValue)kv.Value).N);
            Assert.True(view.Conflicts.IsEmpty, $"seed {seed}: conflicts {string.Join("; ", view.Conflicts)}");
            Assert.Equal(expected, calls);
        }
        Assert.True(merged > 0, "no default log merges two names of one instance");
    }

    /// <summary>The rows of `source` with positive integrated weight, each as often as its weight.</summary>
    static IEnumerable<ImmutableSortedDictionary<string, Value>> Integrated(IEnumerable<Input> log, string source) =>
        log.OfType<RowInput>().Where(r => r.Source == source)
            .GroupBy(r => string.Join(",", r.Fields.Select(f => f.Key + "=" + Canon.Of(f.Value))))
            .SelectMany(g => Enumerable.Repeat(g.First().Fields, (int)Math.Max(0, g.Sum(r => r.Weight))));
}
