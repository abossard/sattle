using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>What is left of the operator examples on a two-module mini world after the differential worlds took the semantics of the operators (Theorem 6.1 against the reference): the lineage of an element through its steps (Theorem 9.7), references (Def. 3.14), the refusals of the checker and the space check, and parameters as constants of the binding (Proposition 5.8). Each moves to its suite (Protocol, Check).</summary>
public class OperatorTests
{
    const string Base = """
        module t.base @ 1.0.0;
        entity Item { key: Text; group: Text; score: Integer; weight?: Decimal; }
        entity Tag { key: Text; label: Text; }
        relationship Has (Item) -> (Tag);
        identity item for Item version "1" { key: Text by exact; }
        identity tag for Tag version "1" { key: Text by exact; }
        rows ItemRow { key: Text; group: Text; score: Integer; weight?: Decimal; }
        rows TagRow { key: Text; label: Text; }
        rows ExtraRow { key: Text; group: Text; score: Integer; weight?: Decimal; }
        """;

    const string Ingest = """
        graph ingest : t.base;
        input items : ItemRow;
        input tags : TagRow;
        input extras : ExtraRow;
        nodes Item on item with (rule = "observed", description = "test") = items | project item = identity("item", key), key, group, score, weight;
        nodes Tag on tag with (rule = "observed") = tags | project tag = identity("tag", key), key, label;
        """;

    /// <summary>The view module: its ontology; its projection `roll` is the KQL text of the file t.view.roll.kql.</summary>
    static string View(string ontology) => $"module t.view @ 1.0.0;\nimport t.base @ 1.0.0 sha256:… as base;\n{ontology}\n";

    /// <summary>The space over the master of the base module, with the given graphs and the imports they need.</summary>
    static string Space(string graphs, string imports = "") => $$"""
        module t.space @ 1.0.0;
        import t.base @ 1.0.0 sha256:… as base;
        import t.view @ 1.0.0 sha256:… as v;
        {{imports}}
        space s {
          source items adapter "sattle.authoring" rows base.ItemRow;
          source tags adapter "sattle.authoring" rows base.TagRow;
          source extras adapter "sattle.authoring" rows base.ExtraRow;
          master graph master = base.ingest(items: items, tags: tags, extras: extras);
          {{graphs}}
        }
        """;

    const string Head = "graph roll : t.view;\ninput master : t.base;\n";

    /// <summary>The items of the master with their properties.</summary>
    const string Matched = "graph(\"master\") | graph-match (i) where labels(i) has \"Item\" project key = i.key, group = i.group, score = i.score, weight = i.weight";

    /// <summary>The one harness: the base module with its KQL ingestion and the given files, loaded with the projections in KQL.</summary>
    static LoadResult Load(out Catalog cat, params SourceFile[] files)
    {
        cat = new Catalog(Repo.Projections);
        return cat.Load([new SourceFile("t.base.sattle", Base), new SourceFile("t.base.ingest.kql", Ingest), .. files], updatePins: true);
    }

    static List<string> Errors(params SourceFile[] files) => [.. Load(out _, files).Diagnostics.Where(d => d.IsError).Select(d => d.ToString())];

    static Engine Start(params SourceFile[] files)
    {
        var res = Load(out var cat, files);
        Assert.Empty(res.Diagnostics.Where(d => d.IsError).Select(d => d.ToString()));
        return new Engine(cat, cat.Spaces.Keys.Single());
    }

    static SourceFile[] ViewFiles(string ontology, string steps, string binding = "") =>
        [new("t.view.sattle", View(ontology)), new("t.view.roll.kql", Head + steps), new("t.space.sattle", Space($"graph rolled = v.roll(master: master{binding});"))];

    static Engine Mini(string ontology, string steps) => Start(ViewFiles(ontology, steps));

    static void Items(Engine e, long seq, params (string Key, string Group, long Score, decimal? Weight, long W)[] rows)
    {
        var rw = new RowWriter(e.RowLayout("items"), e.Interner);
        foreach (var (key, group, score, weight, w) in rows)
        {
            rw.Begin(w); rw.Text("key", key); rw.Text("group", group); rw.Int(rw.Col("score"), score);
            if (weight is { } x) rw.Dec(rw.Col("weight"), x); else rw.Absent(rw.Col("weight"));
        }
        e.Ingest("items", rw.Build()!, seq);
    }

    static Dictionary<string, Value> Summaries(Engine e, string type, string keyProp, string valueProp) =>
        e.Graph("rolled").State.Entities.Where(x => Ids.Name(x.Type) == type)
            .ToDictionary(x => x.Props.Text("d:t.view/prop:" + keyProp)!, x => x.Props.Get("d:t.view/prop:" + valueProp) ?? Value.Null);

    const string Summary = "entity Summary { group: Text; worst?: Integer; best?: Integer; heaviest?: Decimal; }\nidentity summary for Summary version \"1\" { group: Text by exact; }";

    /// <summary>Code review 2, defect 16: an element names the elements it is made of also when a `project` or a `where` stands between the match and the summary, between the summary and the step, or between the match and the step. Before, only the first shape named anything.</summary>
    [Theory]
    [InlineData("the summary read directly", "items | summarize best = count() by group | project x = identity(\"summary\", group), group, best", "a=a1+a2 b=b1", "a=a1 b=b1")]
    [InlineData("a project and a where before the summary", "items | project grp = group, s = score | where s > 0 | summarize best = count() by group = grp | project x = identity(\"summary\", group), group, best", "a=a1+a2 b=b1", "a=a1 b=b1")]
    [InlineData("a project and a where after the summary", "items | summarize best = count() by group | project name = group, n = best | where n > 0 | project x = identity(\"summary\", name), group = name, best = n", "a=a1+a2 b=b1", "a=a1 b=b1")]
    [InlineData("projects on both sides, the second without the key as it was", "items | project grp = group | summarize best = count() by group = grp | project name = strcat(group, \"!\"), n = best | project name, n | project x = identity(\"summary\", name), group = name, best = n", "a!=a1+a2 b!=b1", "a!=a1 b!=b1")]
    [InlineData("row by row through a project", "items | project k = key, s = score | where s > 0 | project x = identity(\"summary\", k), group = k, best = s", "a1=a1 a2=a2 b1=b1", "a1=a1 b1=b1")]
    public void An_element_names_what_it_is_made_of_through_project_and_where(string shape, string query, string named, string afterOneWent)
    {
        using var e = Mini(Summary, $"let items = {Matched};\nnodes Summary on x = {query};\n");
        string Named()
        {
            var keyOf = e.Graph("master").State.Entities.ToDictionary(x => x.Id, x => x.Props.Text("d:t.base/prop:key")!);
            var reader = new LineageReader(e);
            return string.Join(' ', e.Graph("rolled").State.Entities.Select(x => x.Props.Text("d:t.view/prop:group") + "="
                + string.Join('+', reader.Forward("rolled", x.Id)["sources"]!.AsArray().Select(s => keyOf[s!["element"]!.GetValue<string>()]).Order(StringComparer.Ordinal))).Order(StringComparer.Ordinal));
        }
        Items(e, 1, ("a1", "a", 1, null, 1), ("a2", "a", 2, null, 1), ("b1", "b", 3, null, 1));
        Assert.True(named == Named(), $"{shape}: {Named()}");
        Items(e, 2, ("a2", "a", 2, null, -1));
        Assert.True(afterOneWent == Named(), $"{shape}, after a2 went: {Named()}");
    }

    static string StackSpace(bool masterToo = false) =>
        Space($"graph rolled = v.roll(master: master);\n  graph top = t2.top(rolled: rolled{(masterToo ? ", master: master" : "")});", "import t.top @ 1.0.0 sha256:… as t2;");

    /// <summary>A graph that holds only relationships: its projection pairs items with tags of the master and creates no entity, so both ends of every relationship are entities of another graph.</summary>
    static SourceFile[] PairsWorld(string topSteps, bool masterToo = false)
    {
        const string v2 = """
            module t.top @ 1.0.0;
            import t.base @ 1.0.0 sha256:… as base;
            import t.view @ 1.0.0 sha256:… as v;
            entity Top { n: Integer; }
            identity top for Top version "1" { name: Text by exact; }
            relationship Seen (base.Item) -> (base.Tag);
            """;
        return [new("t.view.sattle", View(Paired)),
            new("t.view.roll.kql", Head + "edges Paired from item to t = " + Pairable + " | project item, t;\n"),
            new("t.top.sattle", v2), new("t.top.top.kql", "graph top : t.top;\ninput rolled : t.view;\n" + (masterToo ? "input master : t.base;\n" : "") + topSteps),
            new("t.space.sattle", StackSpace(masterToo))];
    }

    /// <summary>Items with their group joined to the tag whose key is the group.</summary>
    const string Pairable = "graph(\"master\") | graph-match (i) where labels(i) has \"Item\" project item = i.id, g = i.group"
        + " | join kind=inner (graph(\"master\") | graph-match (t) where labels(t) has \"Tag\" project t = t.id, tk = t.key) on $left.g == $right.tk";

    const string Pairs = "let pairs = graph(\"rolled\") | graph-match (i)-[p]->(t) where labels(i) has \"Item\" and labels(p) has \"Paired\" and labels(t) has \"Tag\" project i = i.id, t = t.id;\n";

    /// <summary>The lineage of a row that binds a reference: the reference is an entity of another graph, so it is named in the input of the projection whose graph holds its type, and not at all when the projection reads no such graph. The relationship is named in the graph it was matched in.</summary>
    [Theory]
    [InlineData(true, "i@master p@rolled t@master")]
    [InlineData(false, "p@rolled")]
    public void A_reference_is_named_in_the_graph_that_holds_it(bool masterToo, string named)
    {
        using var e = Start(PairsWorld(Pairs + "edges Seen from i to t = pairs;\n", masterToo));
        Tags(e, 1, ("g1", "L", 1));
        Items(e, 2, ("a1", "g1", 1, null, 1));
        var reader = new LineageReader(e);
        var seen = e.Graph("top").State.Rels.Single(r => Ids.Name(r.Type) == "Seen");
        var sources = reader.Forward("top", seen.Id)["sources"]!.AsArray();
        Assert.Equal(named, string.Join(' ', sources.Select(s => s!["role"] + "@" + s["graph"]).Order(StringComparer.Ordinal)));
        foreach (var s in sources)                                                  // each named element is in the graph it is named in, and knows what it is part of
            Assert.Contains(reader.Reverse(s!["graph"]!.GetValue<string>(), s["element"]!.GetValue<string>())["partOf"]!.AsArray(), p => p!["element"]!.GetValue<string>() == seen.Id);
    }

    /// <summary>A reference has no properties: reading one is refused when the space is checked, with the graph and the type in the message.</summary>
    [Fact]
    public void A_property_read_of_a_reference_is_refused_by_the_space_check()
    {
        var res = Load(out _, PairsWorld("nodes Top on x = graph(\"rolled\") | graph-match (i)-[p]->(t) where labels(i) has \"Item\" and labels(p) has \"Paired\" and labels(t) has \"Tag\" project g = i.group"
            + " | summarize n = count() by k = g | project x = identity(\"top\", k), n;\n"));
        var s4 = Assert.Single(res.Diagnostics, d => d.IsError);
        Assert.Equal("S4", s4.Code);
        Assert.Contains("graph rolled holds no Item", s4.Message);
    }

    const string Paired = "entity Summary { group: Text; }\nidentity summary for Summary version \"1\" { group: Text by exact; }\nrelationship Paired (base.Item) -> (base.Tag);";

    /// <summary>What a union refuses about element columns.</summary>
    [Fact]
    public void A_union_refuses_element_columns_of_two_types() =>
        Assert.Contains(Errors(ViewFiles(Paired, "let a = graph(\"master\") | graph-match (i) where labels(i) has \"Item\" project x = i;\nlet b = graph(\"master\") | graph-match (t) where labels(t) has \"Tag\" project x = t;\n"
            + "nodes Summary on s = union a, b | project s = identity(\"summary\", \"k\"), group = \"k\";\n")), d => d.Contains("union inputs must have the same columns"));

    const string Labelled = "entity Summary { group: Text; label?: Text; }\nidentity summary for Summary version \"1\" { group: Text by exact; }";

    static void Tags(Engine e, long seq, params (string Key, string Label, long W)[] rows)
    {
        var rw = new RowWriter(e.RowLayout("tags"), e.Interner);
        foreach (var (key, label, w) in rows) { rw.Begin(w); rw.Text("key", key); rw.Text("label", label); }
        e.Ingest("tags", rw.Build()!, seq);
    }

    const string Param = "entity Summary { group: Text; worst?: Integer; }\nidentity summary for Summary version \"1\" { group: Text by exact; }";

    const string ParamSteps = "declare query_parameters(minScore:long);\nnodes Summary on x = " + Matched + " | where score >= minScore | summarize worst = max(score) by group | project x = identity(\"summary\", group), group, worst;\n";

    [Theory]
    [InlineData(", minScore: 5", "a")]
    [InlineData(", minScore: 1", "a,b")]
    public void A_parameter_is_a_constant_of_the_space_binding(string binding, string groups)
    {
        using var e = Start(ViewFiles(Param, ParamSteps, binding));
        Items(e, 1, ("a1", "a", 7, null, 1), ("b1", "b", 3, null, 1));
        Assert.Equal(groups.Split(','), Summaries(e, "Summary", "group", "worst").Keys.Order());
    }

    [Theory]
    [InlineData("", "parameter minScore of roll is not bound")]
    [InlineData(", minScore: \"five\"", "parameter minScore is Integer, the binding gives Text")]
    [InlineData(", minScore: 5, other: 1", "has no port 'other'")]
    public void A_parameter_binding_must_give_every_parameter_a_constant_of_its_kind(string binding, string message)
        => Assert.Contains(Load(out _, ViewFiles(Param, ParamSteps, binding)).Diagnostics, d => d.Message.Contains(message));

    static string DatatableSteps(string table = "datatable(tk:string, label:string)[\"g1\", \"L1\", \"g2\", \"L2\"]") =>
        $"let names = {table};\nnodes Summary on x = graph(\"master\") | graph-match (i) where labels(i) has \"Item\" project g = i.group, k = i.key | join kind=inner (names) on $left.g == $right.tk | project x = identity(\"summary\", k), group = k, label;\n";

    [Fact]
    public void Datatable_rows_must_have_the_types_of_its_columns() =>
        Assert.Contains(Errors(ViewFiles(Labelled, DatatableSteps("datatable(tk:string, label:string)[\"g1\", \"L1\", \"g2\", 2]"))), d => d.Contains("all rows of values need the same columns of the same types"));
}
