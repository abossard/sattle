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

    /// <summary>An ingestion of the rows `a` and `b`: `types` are its entity types, `body` its statements.</summary>
    static World Rows(string name, string kind, string types, string body) => new(name, kind,
        [
            new SourceFile("w.sattle", $"module w @ 1.0.0;\n{types}\n{R}\n"),
            Worlds.Kql("w", "ingest", $"graph ingest : w;\ninput a : R;\ninput b : R;\n{body}\n"),
            Worlds.Space("import w @ 1.0.0 sha256:… as base;", """
                  source a adapter "sattle.authoring" rows base.R;
                  source b adapter "sattle.authoring" rows base.R;
                  master graph master = base.ingest(a: a, b: b);
                """),
        ], [A, B]);

    /// <summary>A − B on the column `k`: the rows of `a` tagged (1, 0), those of `b` tagged (0, 1), summed per key; a key of `a` that no row of `b` has keeps the tags (n, 0).</summary>
    static readonly World Difference = Rows("difference by tags and a group", "Union, Group, Filter", """
        entity Only { k: Text; }
        identity only for Only version "1" { k: Text by exact; }
        """, """
        let fromA = a | project k, ina = 1, inb = 0;
        let fromB = b | project k, ina = 0, inb = 1;
        nodes Only on o with (rule = "observed") = union fromA, fromB
            | summarize ina = sum(ina), inb = sum(inb) by k
            | where ina > 0 and inb == 0
            | project o = identity("only", k), k;
        """) with { Witnesses = ["Only"] };

    /// <summary>A × B on the column `k`: both sides get a constant column, and the join is an equality of the constants.</summary>
    static readonly World Product = Rows("product by a join on a constant", "Map, Join", """
        entity Pair { left: Text; right: Text; }
        identity pair for Pair version "1" { left: Text by exact; right: Text by exact; }
        """, """
        let x = a | project left = k, one = 1
            | join kind=inner (b | project right = k, other = 1) on $left.one == $right.other;
        nodes Pair on p with (rule = "observed") = x | project p = identity("pair", left, right), left, right;
        """) with { Witnesses = ["Pair"] };

    static readonly SourceSpec Items = new("a", (r, _) => Row(("k", Text(r, "k", 4)), ("g", Value.Text("g0"))), f => ((TextValue)f["k"]).S);
    static readonly SourceSpec Edges = new("edges", (r, _) => Row(("src", Text(r, "k", 4)), ("dst", Text(r, "k", 4))), f => ((TextValue)f["src"]).S + ">" + ((TextValue)f["dst"]).S);

    /// <summary>The pattern x → y → z over `Edge`: one row per pair of edges where the first ends at the start of the second; x and z may be one item (homomorphism semantics).</summary>
    static readonly World TwoEdges = new("pattern of two edges", "Match",
        [
            new SourceFile("w.sattle", """
                module w @ 1.0.0;
                entity Item { k: Text; g: Text; }
                relationship Edge (Item) -> (Item);
                identity item for Item version "1" { k: Text by exact; }
                rows R { k: Text; g: Text; }
                rows EdgeRow { src: Text; dst: Text; }
                """),
            Worlds.Kql("w", "ingest", """
                graph ingest : w;
                input a : R;
                input edges : EdgeRow;
                nodes Item on i with (rule = "observed") = a | project i = identity("item", k), k, g;
                edges Edge from s to d with (rule = "observed") = edges | project s = identity("item", src), d = identity("item", dst);
                """),
            new SourceFile("w.view.sattle", """
                module w.view @ 1.0.0;
                import w @ 1.0.0 sha256:… as base;
                entity Walk { x: Text; y: Text; z: Text; }
                identity walk for Walk version "1" { x: Text by exact; y: Text by exact; z: Text by exact; }
                """),
            Worlds.Kql("w.view", "v", """
                graph v : w.view;
                input master : base;
                nodes Walk on t = graph("master") | graph-match (x)-[e]->(y)-[f]->(z) where labels(e) has "Edge" and labels(f) has "Edge" project x = x.k, y = y.k, z = z.k
                    | project t = identity("walk", x, y, z), x, y, z;
                """),
            Worlds.Space("import w @ 1.0.0 sha256:… as base;\nimport w.view @ 1.0.0 sha256:… as view;", """
                  source a adapter "sattle.authoring" rows base.R;
                  source edges adapter "sattle.authoring" rows base.EdgeRow;
                  master graph master = base.ingest(a: a, edges: edges);
                  graph view = view.v(master: master);
                """),
        ], [Items, Edges, Edges]) { Witnesses = ["Walk"] };

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

    const string ViewIngest = """
        graph ingest : w.base;
        input a : R;
        input b : R;
        input edges : EdgeRow;
        nodes Item on i with (rule = "observed") = a | project i = identity("item", k), k, g, n;
        edges Edge from s to d with (rule = "observed") = edges | project s = identity("item", src), d = identity("item", dst);
        extend nodes Item on i with (rule = "observed") = b | summarize hits = sum(n) by k, windowStart = bin(t, 60s) | project i = identity("item", k), windowStart, hits;
        """;

    /// <summary>The master of items, edges and hits, and a view `v` with the statements `body`; `input` is the view's graph port, `graphs` the view's graphs in the space.</summary>
    static SourceFile[] View(string body, string input = "input master : base;", string graphs = "  graph view = view.v(master: master);") =>
    [
        new SourceFile("w.base.sattle", Worlds.BaseModule), Worlds.Kql("w.base", "ingest", ViewIngest),
        new SourceFile("w.view.sattle", "module w.view @ 1.0.0;\nimport w.base @ 1.0.0 sha256:… as base;\nentity Out { k: Text; }\nidentity out for Out version \"1\" { k: Text by exact; }\n"),
        Worlds.Kql("w.view", "v", $"graph v : w.view;\n{input}\n{body}\n"),
        Worlds.Space("import w.base @ 1.0.0 sha256:… as base;\nimport w.view @ 1.0.0 sha256:… as view;", """
              source a adapter "sattle.authoring" rows base.R;
              source b adapter "sattle.authoring" rows base.R;
              source edges adapter "sattle.authoring" rows base.EdgeRow;
              master graph master = base.ingest(a: a, b: b, edges: edges);
            """ + graphs),
    ];

    const string ItemRows = """graph("master") | graph-match (i) where labels(i) has "Item" project i = i.id, k = i.k, n = i.n""";
    const string Hits = """graph("master") | graph-match (i) where labels(i) has "Item" project i = i.id, k = i.k | join kind=inner (telemetry("master", 3m) | where property == "hits" | project i = id, windowStart, value) on i""";
    const string Out = """ | project o = identity("out", k), k;""";
    static string Path(string path) => $$"""nodes Out on o = graph("master") | graph-match {{path}} where labels(i) has "Item" and all(p, labels(p) has "Edge") project k = d.k""" + Out;

    public static TheoryData<string, SourceFile[], string> Refused => new()
    {
        { "E4: a path without an upper bound", View(Path("(i)-[p*1..]->(d)")), "Missing expression" },
        { "E4: a path deeper than the kernel's bound", View(Path("(i)-[p*1..65]->(d)")), "depth must satisfy 0 <= min <= max <= 64" },
        { "E4: two paths in one pattern", View("""nodes Out on o = graph("master") | graph-match (i)-[p*1..2]->(d)-[q*1..2]->(e) where labels(i) has "Item" and all(p, labels(p) has "Edge") and all(q, labels(q) has "Edge") project k = e.k""" + Out), "a pattern has at most one path of several edges" },
        { "E3: an edge from a node to itself in a pattern", View("""nodes Out on o = graph("master") | graph-match (i)-[e]->(i) where labels(e) has "Edge" project k = i.k""" + Out), "an edge from a node to itself is not kept apart from the others of its type" },
        { "E1: an order between two elements", View("""nodes Out on o = graph("master") | graph-match (x)-[e]->(y) where labels(e) has "Edge" project a = x.id, b = y.id, k = x.k | where a < b""" + Out), "is not defined for the operand types string and string" },
        { "E2: a filter on a measurement (P4)", View("nodes Out on o = " + Hits + " | where value > 2" + Out), "filter predicate must be (Boolean, req, data)" },
        { "E2: a join on a measurement (P4)", View("nodes Out on o = " + Hits + " | project k, v = value | join kind=inner (" + ItemRows + " | project k2 = k, v2 = real(1)) on $left.v == $right.v2" + Out), "join takes data rows: no windowed or telemetry input" },
        { "E2: a group key that is a measurement (P4)", View("nodes Out on o = " + Hits + " | summarize c = count() by k, value, windowStart" + Out), "aggregate the telemetry column 'value'" },
        { "E6: a let that reads a later let", View("let x = y | project k;\nlet y = x | project k;\nnodes Out on o = y" + Out), "The name 'y' does not refer to any known table" },
        { "E6: a function that calls itself", View("let f = (n:long) { f(n) };\nnodes Out on o = " + ItemRows + Out), "The name 'f' does not refer to any known function" },
        { "E6: two graphs that read each other", View("""nodes Out on o = graph("g") | graph-match (i) where labels(i) has "Out" project k = i.k""" + Out, "input g : w.view;", "  graph x = view.v(g: y);\n  graph y = view.v(g: x);"), "graph production has a cycle" },
        { "E6: a graph that reads itself", View("""nodes Out on o = graph("g") | graph-match (i) where labels(i) has "Out" project k = i.k""" + Out, "input g : w.view;", "  graph x = view.v(g: x);"), "graph production has a cycle" },
        { "E9: an order over rows", View("nodes Out on o = " + ItemRows + " | serialize | extend p = prev(k)" + Out), "serialize is not in the KQL profile" },
        { "E9: a percentile", View("nodes Out on o = " + ItemRows + " | summarize p = percentile(n, 50) by k" + Out), "percentile is not an aggregate of the profile" },
        { "E9: an identity used as a value", View("nodes Out on o = " + ItemRows + """ | project p = identity("out", k), k | where p == p | project o = p, k;"""), "is an identity: it names a node or an end of an edge, it is not a value" },
    };

    [Theory, MemberData(nameof(Refused))]
    public void A_form_outside_the_language_is_refused_with_its_reason(string what, SourceFile[] files, string says)
    {
        var errors = new Catalog(EngineRun.Projections).Load(files, updatePins: true).Diagnostics.Where(d => d.IsError).Select(d => d.Message).ToArray();
        Assert.True(errors.Any(e => e.Contains(says, StringComparison.Ordinal)), $"{what}: expected \"{says}\", found {(errors.Length == 0 ? "no error" : string.Join("; ", errors))}");
    }
}
