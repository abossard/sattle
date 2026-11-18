using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The checker on whole definitions (Ch. 2 and 5; Prop. 5.8, Def. 3.14, Def. 7.15): a wrong definition is refused with a diagnostic of a stated code that says what is wrong, a right one loads, and no input is answered with an exception. Definitions in, diagnostics and the definition graph out. The cases come from the code reviews of the checker.</summary>
public class CheckerTests
{
    const string Base = """
        module t @ 1.0.0;
        entity Item { k: Text; g: Text; hits?: Integer telemetry delta sum every 60s; level?: Decimal telemetry gauge max every 60s; }
        entity Other { k: Text; }
        identity item for Item version "1" { k: Text by exact; }
        identity other for Other version "1" { k: Text by exact; }
        rows R { k: Text; g: Text; n: Integer; t: Instant; }
        """;

    const string Ingest = """
        graph ingest : t;
        input a : R;
        nodes Item on i with (rule = "observed", description = "test") = a | project i = identity("item", k), k, g;
        nodes Other on o with (rule = "observed") = a | project o = identity("other", g), k = g;
        extend nodes Item on i with (rule = "observed") = a | summarize hits = sum(n) by k, windowStart = bin(t, 60s) | project i = identity("item", k), windowStart, hits;
        """;

    /// <summary>A view over the base module: `types` are extra declarations of the view's module, `body` the steps of its projection `v`, `space` the graphs of the space beside the master.</summary>
    static SourceFile[] View(string body, string types = "", string space = "graph view = v.v(master: master);") =>
    [
        new("t.sattle", Base), new("t.ingest.kql", Ingest), new("v.sattle", $$"""
            module v @ 1.0.0;
            import t @ 1.0.0 sha256:… as t;
            entity Out { k: Text; flag?: Text; owner?: Ref<t.Other>; }
            identity out for Out version "1" { k: Text by exact; }
            {{types}}
            """),
        new("v.v.kql", "graph v : v;\ninput master : t;\n" + body),
        new("s.sattle", $$"""
            module s @ 1.0.0;
            import t @ 1.0.0 sha256:… as t;
            import v @ 1.0.0 sha256:… as v;
            space s {
              source a adapter "sattle.authoring" rows t.R;
              master graph master = t.ingest(a: a);
              {{space}}
            }
            """),
    ];

    /// <summary>A lens `top` over the lens view `view`: `view` edits the master's items by `k`, `top` edits the elements of `view` by `k`.</summary>
    static SourceFile[] LensOverLens()
    {
        var files = View("nodes Out on o with (rule = \"items\", update = \"k\") = " + "graph(\"master\") | graph-match (i) where labels(i) has \"Item\" project o = i.id, k = i.k;\n",
            space: "graph view = v.v(master: master);\n  graph top = w.w(g: view);");
        var space = files.Single(f => f.Path == "s.sattle");
        return
        [
            .. files.Where(f => f != space),
            new("w.sattle", "module w @ 1.0.0;\nimport v @ 1.0.0 sha256:… as v;\nentity Top { k: Text; }\n"),
            new("w.w.kql", "graph w : w;\ninput g : v;\nnodes Top on o with (rule = \"tops\", update = \"k\") = graph(\"g\") | graph-match (x) where labels(x) has \"Out\" project o = x.id, k = x.k;\n"),
            space with { Text = space.Text.Replace("import v @ 1.0.0 sha256:… as v;", "import v @ 1.0.0 sha256:… as v;\nimport w @ 1.0.0 sha256:… as w;") },
        ];
    }

    const string Items = "graph(\"master\") | graph-match (i) where labels(i) has \"Item\" project i = i.id, k = i.k, g = i.g";
    const string Plain = "nodes Out on o = " + Items + " | project o = identity(\"out\", k), k;\n";
    /// <summary>The windows of the items' hits within three minutes.</summary>
    const string Signals = Items + " | join kind=inner (telemetry(\"master\", 3m) | where property == \"hits\" | project i = id, windowStart, property, value) on i";
    const string Said = ", \"silent\"";
    /// <summary>The windows of `value` from the telemetry call `series`, a threshold with the given no-data state over them, and a sink.</summary>
    static string Quiet(string series, string noData, string value = "hits")
    {
        var nd = series.StartsWith("telemetry_filled", StringComparison.Ordinal) ? ", noData" : "";
        return $"nodes Out on o = {Items} | join kind=inner ({series} | where property == \"{value}\" | project i = id, windowStart, property, value{nd}) on i\n"
            + $"    | project c = k, windowStart, value, above = 3.0, enterAfter = 2, below = 1.0, exitAfter = 2{nd} | invoke hysteresis(\"on\", \"off\", \"off\"{noData})\n    | project o = identity(\"out\", c), k = c, flag = state;\n";
    }

    const string LoadType = "entity Load { k: Text; hits?: Integer telemetry delta sum every 60s; }\nidentity load for Load version \"1\" { k: Text by exact; }";
    /// <summary>Loads per item, rolled up by `keys` and written to the item's Load element.</summary>
    static string Loaded(string keys) =>
        $"nodes Load on l = {Items} | project l = identity(\"load\", k), k;\nextend nodes Load on l = {Signals} | summarize v = sum(value) by {keys}, windowStart | project l = identity(\"load\", c), windowStart, hits = v;\n";

    static (Catalog Catalog, List<Diagnostic> Errors) Load(SourceFile[] files)
    {
        var catalog = new Catalog(Repo.Projections);
        return (catalog, [.. catalog.Load(files, updatePins: true).Diagnostics.Where(d => d.IsError)]);
    }

    /// <summary>A graph `view` that holds only relationships between items and others of the master, so both ends are references there, and a projection `w` over it whose steps are `body`.</summary>
    static SourceFile[] OverReferences(string body)
    {
        var files = View("edges Paired from i to o2 = " + Items + " | join kind=inner (graph(\"master\") | graph-match (x) where labels(x) has \"Other\" project o2 = x.id, ok = x.k) on $left.g == $right.ok | project i, o2;\n",
            "relationship Paired (t.Item) -> (t.Other);", "graph view = v.v(master: master);\n  graph top = w.w(g: view);");
        var space = files.Single(f => f.Path == "s.sattle");
        return
        [
            .. files.Where(f => f != space),
            new("w.sattle", "module w @ 1.0.0;\nimport t @ 1.0.0 sha256:… as t;\nimport v @ 1.0.0 sha256:… as v;\nentity Top { n: Integer; }\nidentity top for Top version \"1\" { name: Text by exact; }\n"),
            new("w.w.kql", "graph w : w;\ninput g : v;\n" + body),
            space with { Text = space.Text.Replace("import v @ 1.0.0 sha256:… as v;", "import v @ 1.0.0 sha256:… as v;\nimport w @ 1.0.0 sha256:… as w;") },
        ];
    }

    /// <summary>A view with one text parameter `only`, bound by the space as `binding` says (Prop. 5.8: a parameter is a constant of the binding; `DeployHealthTests` runs one projection under two bindings).</summary>
    static SourceFile[] Bound(string binding) =>
        View("declare query_parameters(only:string);\nnodes Out on o = " + Items + " | where g == only | project o = identity(\"out\", k), k;\n", space: $"graph view = v.v(master: master{binding});");

    /// <summary>Pods with two keys, a uid and a name, that an equivalence joins, and a view `v` over them whose steps are `body`.</summary>
    static SourceFile[] OverLinked(string body) =>
    [
        new("l.sattle", """
            module l @ 1.0.0;
            entity Pod { uid?: Text; name?: Text; }
            identity podUid for Pod version "1" { uid: Text by exact; }
            identity podName for Pod version "1" { name: Text by lowerInvariant; }
            equivalence uidIsName between podUid, podName assert threshold 1.0;
            rows PodRow { uid: Text; name: Text; }
            """),
        new("l.ingest.kql", """
            graph ingest : l;
            input pods : PodRow;
            nodes Pod on p with (rule = "observed") = pods | project p = identity("podUid", uid), uid, name;
            link uidIsName from byUid to byName with (confidence = 1.0, rule = "observed") = pods | project byUid = identity("podUid", uid), byName = identity("podName", name);
            """),
        new("lv.sattle", "module lv @ 1.0.0;\nimport l @ 1.0.0 sha256:… as base;\nentity Tag { name?: Text; }\n"),
        new("lv.v.kql", "graph v : lv;\ninput master : base;\n" + body),
    ];

    public static TheoryData<string, SourceFile[], string, string> Refused => new()
    {
        { "a union of element columns of two types", View("let a = graph(\"master\") | graph-match (i) where labels(i) has \"Item\" project x = i;\nlet b = graph(\"master\") | graph-match (t) where labels(t) has \"Other\" project x = t;\nnodes Out on o = union a, b | project o = identity(\"out\", \"k\"), k = \"k\";\n"), "Union", "union inputs must have the same columns" },
        { "a row of a table of values with another type than its column", View("let names = datatable(tk:string, label:string)[\"g1\", \"L1\", \"g2\", 2];\nnodes Out on o = " + Items + " | join kind=inner (names) on $left.g == $right.tk | project o = identity(\"out\", k), k, flag = label;\n"), "Values", "all rows of values need the same columns of the same types" },
        { "a property read of a reference: the graph holds the relationship, not its ends (Def. 3.14)", OverReferences("nodes Top on x = graph(\"g\") | graph-match (i)-[p]->(o) where labels(i) has \"Item\" and labels(p) has \"Paired\" and labels(o) has \"Other\" project g = i.g | summarize n = count() by k = g | project x = identity(\"top\", k), n;\n"), "S4", "graph view holds no Item" },
        { "a parameter the space does not bind (Prop. 5.8)", Bound(""), "S2", "parameter only of v is not bound" },
        { "a parameter bound with a constant of another kind (Prop. 5.8)", Bound(", only: 5"), "S2", "parameter only is Text, the binding gives Integer" },
        { "a binding of a parameter the projection does not have (Prop. 5.8)", Bound(", only: \"a\", other: 1"), "UnknownPort", "has no port 'other'" },
        { "a view that names its elements by a key of a domain an equivalence joins: after a link the row that leaves would compute the survivor's id", OverLinked("nodes Tag on t = graph(\"master\") | graph-match (p) where labels(p) has \"Pod\" project p = p.id, name = p.name | project t = identity(\"podName\", name), name;\n"), "IdentityLinked", "keep its id (p.id)" },
        { "the condition of an if is telemetry, so its result is: it is no value of a data property", View("nodes Out on o = " + Signals + " | project o = identity(\"out\", k), k, flag = iff(value > 2, \"hot\", \"cold\");\n"), "RoleInterference", "telemetry value" },
        { "a key and the state of a threshold of one name", View("nodes Out on o = " + Signals + " | project state = k, windowStart, value, above = 3.0, enterAfter = 2, below = 1.0, exitAfter = 2 | invoke hysteresis(\"on\", \"off\", \"off\")\n    | project o = identity(\"out\", state), k = state;\n"), "DuplicateColumn", "'state'" },
        { "two declarations of one id: two key constraints would be one key over both properties", View(Plain, "constraint key Out (k);\nconstraint key Out (flag);"), "Duplicate", "constraint 'key-Out'" },
        { "two labels on one type", View(Plain, "entity Named { k: Text label; title: Text label; }"), "Label", "second" },
        { "a label that is no text", View(Plain, "entity Named { k: Text; rank: Integer label; }"), "Label", "must be a Text property" },
        { "a relationship kind that is none of the four", View(Plain, "entity Named { k: Text; }\nrelationship Uses (Named) -> (Named) kind friendship;"), "Syntax", "relationship kind" },
        { "a fill on a gauge", View(Quiet("telemetry_filled(\"master\", 3m, 2m, 0.0)", Said, "level")), "Series", "takes no fill" },
        { "a fill of an Integer property that is no integer", View(Quiet("telemetry_filled(\"master\", 3m, 2m, 0.5)", Said)), "Series", "must be an integer" },
        { "a time to live below one window", View(Quiet("telemetry_filled(\"master\", 3m, 30s, real(null))", Said)), "Series", "at least one window" },
        { "a time to live above the look-back", View(Quiet("telemetry_filled(\"master\", 3m, 4m, real(null))", Said)), "Series", "at most the look-back less one window" },
        { "a time to live as long as the look-back", View(Quiet("telemetry_filled(\"master\", 3m, 3m, real(null))", Said)), "Series", "at most the look-back less one window" },
        { "a threshold over a series with a time to live that does not say what no data gives", View(Quiet("telemetry_filled(\"master\", 3m, 2m, real(null))", "")), "Threshold", "what no data gives" },
        { "a no-data state without a time to live upstream", View(Quiet("telemetry(\"master\", 3m)", Said)), "Threshold", "needs telemetry with a time to live" },
        { "a telemetry roll-up keyed finer than the element it writes (premise B1)", View(Loaded("c = k, g"), LoadType), "B1", "grouped by 'g'" },
        { "a reference to another entity type than the property names", View("nodes Out on o = " + Items + " | project o = identity(\"out\", k), k, owner = i;\n"), "Set", "owner expects" },
        { "a lens over a lens view: the engine runs one lens over a bijective rule (Ch. 8)", LensOverLens(), "Unsupported", "rule tops: a lens over the lens view view is later work" },
        { "a lens rule whose rows pass through a join: a join is not a lens step", View("nodes Out on o with (rule = \"items\", update = \"k\") = " + Items + " | join kind=inner (graph(\"master\") | graph-match (x) where labels(x) has \"Other\" project g = x.k) on g | project o = i, k;\n"), "DirectionOverclaim", "(Join) is many-to-one or lossy" },
    };

    [Theory, MemberData(nameof(Refused))]
    public void A_wrong_definition_is_refused_with_a_diagnostic(string what, SourceFile[] files, string code, string says)
    {
        var (_, errors) = Load(files);
        Assert.True(errors.Any(e => e.Code == code && e.Message.Contains(says, StringComparison.Ordinal)), $"{what}: expected {code} saying \"{says}\", found {(errors.Count == 0 ? "no error" : string.Join("; ", errors.Select(e => e.ToString())))}");
    }

    /// <summary>A view with a window (in the base), a roll-up, the last aggregates of one row and a join of two row sets: each a form that KQL writes.</summary>
    static readonly Lazy<Graph> Written = new(() =>
    {
        var (catalog, errors) = Load(View(Loaded("c = k")
            + "nodes Out on o = " + Items + " | summarize n = count() by g | summarize arg_max(n, g) by c = g | project o = identity(\"out\", c), k = c;\n"
            + "extend nodes Out on o = " + Items + " | join kind=inner (graph(\"master\") | graph-match (x) where labels(x) has \"Other\" project g = x.k, ok = x.k) on g | project o = identity(\"out\", k), flag = ok;\n", LoadType));
        Assert.Empty(errors);
        return catalog.Graph;
    });

    static Entity First(Graph g, string type, Func<Entity, bool>? where = null) => g.Entities.First(e => Ids.Name(e.Type) == type && (where?.Invoke(e) ?? true));
    static List<Edit> Set(Entity e, Props props) => [Edit.Change(e, e with { Props = props })];

    /// <summary>The forms no KQL text writes (#81) and what refuses each when its facts come another way: the kernel refuses a late policy other than revise and a no-data rule on a roll-up, the checker a last without its row mark and a join condition that is no key.</summary>
    public static TheoryData<string, Func<Graph, List<Edit>>, bool, string, string> Unwritable => new()
    {
        { "a window that ignores late rows", g => { var w = First(g, "Window"); return Set(w, w.Props.With(Ids.KProp("late"), Value.Text("ignore"))); }, true, "Enum", "late = \"ignore\" is not one of revise" },
        { "a roll-up that gives no data when one member has none", g => { var r = First(g, "Rollup"); return Set(r, r.Props.With(Ids.KProp("nodata"), Value.Text("any"))); }, true, "UnknownProperty", "nodata is not declared for Rollup" },
        { "a last without its row mark", g => { var a = First(g, "Aggregate", e => e.Props.Text(Ids.KProp("fn")) == "last"); return Set(a, a.Props.Without(Ids.KProp("row"))); }, false, "Unsupported", "last needs a row mark" },
        { "a join condition that reads the left rows only", g =>
            {
                var join = First(g, "Join");
                return [.. g.Entities.Where(e => Ids.Name(e.Type) == "FieldRef" && e.Id.StartsWith(join.Id + "/", StringComparison.Ordinal)).SelectMany(f => Set(f, f.Props.With(Ids.KProp("path"), Value.Text("k"))))];
            }, false, "Join", "a join condition is equalities between a left and a right expression" },
    };

    [Theory, MemberData(nameof(Unwritable))]
    public void A_form_that_KQL_cannot_write_is_refused_when_its_facts_come_another_way(string what, Func<Graph, List<Edit>> edit, bool kernel, string code, string says)
    {
        var g = Written.Value.Clone();
        Assert.Null(g.Apply(edit(g)));
        var found = kernel ? Conformance.Check(g, OntologyView.Build(g, [Ids.KernelModule])) : new ProjectionChecker(g).Check("d:v/projection:v").Diagnostics;
        Assert.True(found.Any(d => d.IsError && d.Code == code && d.Message.Contains(says, StringComparison.Ordinal)), $"{what}: expected {code} saying \"{says}\", found {string.Join("; ", found.Select(d => d.ToString()))}");
    }

    /// <summary>Def. 7.15: a window can only be known to be covered when a source feeds the property. A fill of a property that nothing writes is refused when the space is activated.</summary>
    [Fact]
    public void A_fill_of_a_property_that_no_source_feeds_is_refused_at_activation()
    {
        var files = View("nodes Beat on b = " + Items + " | project b = identity(\"beat\", k), k;\n", """
            entity Beat { k: Text; beats?: Integer telemetry delta sum every 60s; }
            identity beat for Beat version "1" { k: Text by exact; }
            """, "graph view = v.v(master: master);\n  graph second = v.read(view: view);");
        files = [.. files, new("v.read.kql", """
            graph read : v;
            input view : v;
            nodes Out on o = graph("view") | graph-match (b) where labels(b) has "Beat" project b = b.id, k = b.k
                | join kind=inner (telemetry_filled("view", 3m, 2m, 0) | where property == "beats" | project b = id, windowStart, property, value, noData) on b
                | project c = k, windowStart, value, above = 3.0, enterAfter = 2, below = 1.0, exitAfter = 2, noData | invoke hysteresis("on", "off", "off", "silent")
                | project o = identity("out", c), k = c, flag = state;
            """)];
        var (catalog, errors) = Load(files);
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => e.ToString())));
        var refused = Assert.Throws<InvalidOperationException>(() => new Engine(catalog, catalog.Spaces.Keys.Single()));
        Assert.Contains("no source feeds", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Def. 7.15: authored rows come when someone writes them, so their source does not report and nothing it feeds is ever covered. A fill of such a property is refused when the space is activated; the same definitions over a source that reports are accepted.</summary>
    [Theory]
    [InlineData("sattle.authoring", "authored data records no progress")]
    [InlineData("test.reporting", null)]
    public void A_fill_of_a_property_that_an_authored_source_feeds_is_refused_at_activation(string adapter, string? refusal)
    {
        var files = View(Quiet("telemetry_filled(\"master\", 3m, 2m, 0)", Said));
        files = [.. files.Select(f => f.Path == "s.sattle" ? f with { Text = f.Text.Replace("source a adapter \"sattle.authoring\"", $"source a adapter \"{adapter}\"") } : f)];
        var (catalog, errors) = Load(files);
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => e.ToString())));
        if (refusal is null) { using var engine = new Engine(catalog, catalog.Spaces.Keys.Single()); Assert.True(engine.WantsProgress); }
        else Assert.Contains(refusal, Assert.Throws<InvalidOperationException>(() => new Engine(catalog, catalog.Spaces.Keys.Single())).Message, StringComparison.Ordinal);
    }

    /// <summary>A Match reads the properties its own projection uses. One checker serves every projection of a module and keeps its demand set; before, a Match also read what a same-named operator of an earlier projection demanded.</summary>
    [Fact]
    public void A_match_reads_only_the_properties_its_own_projection_uses()
    {
        const string Copies = "nodes Out on o = graph(\"master\") | graph-match (i) where labels(i) has \"Item\" project ";
        var (catalog, errors) = Load([.. View(Copies + "k = i.k, g = i.g | project o = identity(\"out\", k), k, flag = g;\n"),
            new SourceFile("v.second.kql", "graph second : v;\ninput master : t;\n" + Copies + "k = i.k | project o = identity(\"out\", k), k;\n")]);
        Assert.Empty(errors);
        var second = catalog.Projections.Values.Single(p => p.Name == "second").Ops.OfType<TMatch>().Single();
        Assert.Equal(["k"], second.Reads.Select(r => Ids.Name(r.PropId)));
    }

    [Fact]
    public void Two_graphs_of_one_projection_both_hold_its_output()
    {
        var (catalog, errors) = Load(View(Plain, space: "graph first = v.v(master: master);\n  graph second = v.v(master: master);"));
        Assert.Empty(errors);
        using var engine = new Engine(catalog, catalog.Spaces.Keys.Single());
        var w = new RowWriter(engine.RowLayout("a"), engine.Interner);
        w.Begin(1); w.Text("k", "k0"); w.Text("g", "g0"); w.Int(w.Col("n"), 1); w.Instant("t", 0);
        engine.Ingest("a", w.Build()!, 1);
        Assert.Equal(["first", "master", "second"], engine.Graphs.Select(g => g.Name).Order());
        Assert.All(engine.Graphs.Where(g => g.Name != "master"), g => Assert.Single(g.State.Entities));
    }
}
