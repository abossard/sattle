using Sattle.Core;
using Sattle.Kql;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Operators of the engine on a two-module mini world: rows in, a view over the master out. Each test states the semantics it relies on, including retraction.</summary>
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
        cat = new Catalog(new KqlSource());
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

    const string Extremes = "nodes Summary on x = " + Matched + " | summarize worst = max(score), best = min(score), heaviest = max(weight) by group | project x = identity(\"summary\", group), group, worst, best, heaviest;\n";

    [Fact]
    public void Min_and_max_follow_inserts_and_retractions_and_are_null_over_no_value()
    {
        using var e = Mini(Summary, Extremes);
        Items(e, 1, ("a1", "a", 3, 1.5m, 1), ("a2", "a", 7, null, 1), ("b1", "b", 5, 2.5m, 1));
        var worst = Summaries(e, "Summary", "group", "worst"); var best = Summaries(e, "Summary", "group", "best"); var heavy = Summaries(e, "Summary", "group", "heaviest");
        Assert.Equal(Value.Integer(7), worst["a"]); Assert.Equal(Value.Integer(3), best["a"]); Assert.Equal(Value.Integer(5), worst["b"]);
        Assert.Equal(new DecimalValue(1.5m), heavy["a"]);                               // nulls are skipped
        Items(e, 2, ("a2", "a", 7, null, -1));                                          // retract the maximum: the next value takes over
        Assert.Equal(Value.Integer(3), Summaries(e, "Summary", "group", "worst")["a"]);
        Items(e, 3, ("a1", "a", 3, 1.5m, -1));                                          // the group disappears with its last member
        Assert.DoesNotContain("a", Summaries(e, "Summary", "group", "worst").Keys);
        Assert.Equal(Value.Integer(5), Summaries(e, "Summary", "group", "worst")["b"]);
    }

    /// <summary>`min` and `max` against a model under generated inserts and retractions, a few rows per step: the extreme of a group with more than one value is kept with the group and found again among its values only when it goes itself. Scores repeat, so an extreme often has several rows; weights come at two scales (2.5 and 2.50 are one value) and are sometimes missing.</summary>
    [Theory]
    [InlineData(1, 1)] [InlineData(2, 1)] [InlineData(3, 4)] [InlineData(4, 4)] [InlineData(5, 25)]
    public void Min_and_max_equal_the_extremes_of_the_rows_a_group_has_after_every_step(int seed, int rowsPerStep)
    {
        using var e = Mini(Summary, Extremes);
        var rnd = new Random(seed); var live = new Dictionary<string, (string Group, long Score, decimal? Weight)>(); var n = 0;
        for (var step = 1; step <= 120; step++)
        {
            var rows = new List<(string, string, long, decimal?, long)>();
            for (var i = 0; i < rowsPerStep; i++)
            {
                if (live.Count > 0 && rnd.Next(100) < 45)
                {
                    var (key, was) = live.ElementAt(rnd.Next(live.Count));
                    live.Remove(key); rows.Add((key, was.Group, was.Score, was.Weight, -1));
                }
                else
                {
                    var row = (Group: "g" + rnd.Next(3), Score: (long)rnd.Next(-4, 5), Weight: rnd.Next(4) == 0 ? (decimal?)null : rnd.Next(6) / (rnd.Next(2) == 0 ? 2m : 2.00m));
                    var key = "k" + n++; live[key] = row; rows.Add((key, row.Group, row.Score, row.Weight, 1));
                }
            }
            Items(e, step, [.. rows]);
            var worst = Summaries(e, "Summary", "group", "worst"); var best = Summaries(e, "Summary", "group", "best"); var heaviest = Summaries(e, "Summary", "group", "heaviest");
            var groups = live.Values.GroupBy(x => x.Group).ToDictionary(g => g.Key, g => g.ToList());
            Assert.Equal(groups.Keys.Order(), worst.Keys.Order());
            foreach (var (g, of) in groups)
            {
                Assert.Equal(Value.Integer(of.Max(x => x.Score)), worst[g]);
                Assert.Equal(Value.Integer(of.Min(x => x.Score)), best[g]);
                Assert.Equal(of.Max(x => x.Weight) is { } w ? new DecimalValue(w) : Value.Null, heaviest[g]);
            }
        }
    }

    /// <summary>A `min` or `max` and a `count_distinct` of one group keep their values apart: each aggregate has its own multiset.</summary>
    [Theory]
    [InlineData("best = min(score), worst = count_distinct(key)")]
    [InlineData("worst = count_distinct(key), best = min(score)")]
    [InlineData("best = max(score), worst = count_distinct(key)")]
    public void An_extreme_and_a_count_of_distinct_values_in_one_group_do_not_mix_their_values(string aggregates)
    {
        using var e = Mini(Summary, $"nodes Summary on x = {Matched} | summarize {aggregates} by group | project x = identity(\"summary\", group), group, worst, best;\n");
        Items(e, 1, ("a1", "a", 1_000_000_000_000, null, 1), ("a2", "a", -1_000_000_000_000, null, 1), ("a3", "a", -1_000_000_000_000, null, 1));
        var max = aggregates.Contains("max(");
        Assert.Equal(Value.Integer(max ? 1_000_000_000_000 : -1_000_000_000_000), Summaries(e, "Summary", "group", "best")["a"]);
        Assert.Equal(Value.Integer(3), Summaries(e, "Summary", "group", "worst")["a"]);
        Items(e, 2, ("a1", "a", 1_000_000_000_000, null, -1), ("a2", "a", -1_000_000_000_000, null, -1));
        Assert.Equal(Value.Integer(-1_000_000_000_000), Summaries(e, "Summary", "group", "best")["a"]);
        Assert.Equal(Value.Integer(1), Summaries(e, "Summary", "group", "worst")["a"]);
    }

    /// <summary>Code review 2, defect 9: the values of one `arg_max` come from one input row, the one with the greatest order; on a tie the greatest values in the order of the aggregates decide, and a value that row does not have is absent.</summary>
    [Fact]
    public void The_values_of_an_arg_max_come_from_one_input_row()
    {
        using var e = Mini(Summary, "let items = " + Matched + ";\n"
            + "nodes Summary on x = union (items | summarize arg_max(score, weight) by group), (items | extend one = 1 | summarize arg_max(one, score, weight) by group = strcat(group, \"/tie\") | project group, score, weight)"
            + " | project x = identity(\"summary\", group), group, best = score, heaviest = weight;\n");
        string Of(string g) => $"{Summaries(e, "Summary", "group", "best")[g]} {Summaries(e, "Summary", "group", "heaviest")[g]}".Replace("IntegerValue { Kind = Integer, N = ", "").Replace("DecimalValue { Kind = Decimal, Q = ", "").Replace(" }", "").Replace("NullValue { Kind = Null", "none");
        Items(e, 1, ("a1", "a", 1, 9.0m, 1), ("a2", "a", 2, 3.0m, 1));
        Assert.Equal("2 3", Of("a"));
        Assert.Equal("2 3", Of("a/tie"));                                               // every row ties: the row with the greatest score (the first aggregate after the order)
        Items(e, 2, ("a3", "a", 7, null, 1));                                           // the greatest order has no weight
        Assert.Equal("7 none", Of("a"));
        Items(e, 3, ("a3", "a", 7, null, -1), ("a2", "a", 2, 3.0m, -1));                // the winner goes, and the row after it
        Assert.Equal("1 9", Of("a"));
        Assert.Equal("1 9", Of("a/tie"));
        Items(e, 4, ("a1", "a", 1, 9.0m, -1));
        Assert.DoesNotContain("a", Summaries(e, "Summary", "group", "best").Keys);
    }

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

    [Fact]
    public void A_view_reads_the_graph_of_another_view_and_follows_its_changes()
    {
        const string v1 = """
            module t.view @ 1.0.0;
            import t.base @ 1.0.0 sha256:… as base;
            entity Summary { group: Text; worst?: Integer; }
            identity summary for Summary version "1" { group: Text by exact; }
            """;
        const string v2 = """
            module t.top @ 1.0.0;
            import t.view @ 1.0.0 sha256:… as v;
            entity Top { worst?: Integer; groups: Integer; }
            identity top for Top version "1" { name: Text by exact; }
            """;
        const string top = "graph top : t.top;\ninput rolled : t.view;\n"
            + "nodes Top on x = graph(\"rolled\") | graph-match (s) where labels(s) has \"Summary\" project worst = s.worst | summarize worst = max(worst), groups = count() by k = \"all\" | project x = identity(\"top\", k), worst, groups;\n";
        using var e = Start(new("t.view.sattle", v1), new("t.view.roll.kql", Head + "nodes Summary on x = " + Matched + " | summarize worst = max(score) by group | project x = identity(\"summary\", group), group, worst;\n"),
            new("t.top.sattle", v2), new("t.top.top.kql", top), new("t.space.sattle", StackSpace()));
        Items(e, 1, ("a1", "a", 3, null, 1), ("b1", "b", 9, null, 1));
        long Worst() => ((IntegerValue)e.Graph("top").State.Entities.Single().Props.Get("d:t.top/prop:worst")!).N is var n ? (long)n : 0;
        Assert.Equal(9, Worst());
        Items(e, 2, ("b1", "b", 9, null, -1));
        Assert.Equal(3, Worst());
    }

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

    /// <summary>A pattern node of a type its graph never holds is a reference: it binds the end of a matched relationship by its id, without an entity. Here the matched graph holds relationships between items and tags of the master and no entity at all; the pattern over it gives one row per relationship, the ids are the master's, and the rows follow the relationships as they come and go. Before, such a pattern gave no row.</summary>
    [Fact]
    public void A_pattern_binds_the_ends_of_a_relationship_whose_entities_are_in_another_graph()
    {
        using var e = Start(PairsWorld(Pairs + "nodes Top on x = pairs | summarize n = count() by k = \"all\" | project x = identity(\"top\", k), n;\nedges Seen from i to t = pairs;\n"));
        Tags(e, 1, ("g1", "L", 1));
        Items(e, 2, ("a1", "g1", 1, null, 1), ("a2", "g1", 1, null, 1), ("b1", "g2", 1, null, 1));
        Assert.Empty(e.Graph("rolled").State.Entities);
        long Count() => e.Graph("top").State.Entities.SingleOrDefault() is { } top ? (long)((IntegerValue)top.Props.Get("d:t.top/prop:n")!).N : 0;
        string Seen() => Count() + ": " + string.Join(" ", e.Graph("top").State.Rels.Where(r => Ids.Name(r.Type) == "Seen").Select(r => e.Graph("master").State.Entity(r.Source)!.Props.Text("d:t.base/prop:key") + ">" + e.Graph("master").State.Entity(r.Target)!.Props.Text("d:t.base/prop:key")).Order());
        Assert.Equal("2: a1>g1 a2>g1", Seen());
        Items(e, 3, ("a1", "g1", 1, null, -1));                                    // an item goes: its relationship goes, and its row with it
        Assert.Equal("1: a2>g1", Seen());
        Tags(e, 4, ("g2", "M", 1));                                                // a tag comes: b1 is paired
        Assert.Equal("2: a2>g1 b1>g2", Seen());
        Tags(e, 5, ("g1", "L", -1), ("g2", "M", -1));
        Assert.Equal("0: ", Seen());
    }

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

    const string RowsModule = """
        module t.rows @ 1.0.0;
        entity Stat { name: Text; n: Integer; }
        identity stat for Stat version "1" { name: Text by exact; }
        rows ItemRow { key: Text; group: Text; score: Integer; }
        rows OtherRow { key: Text; }
        """;

    const string RowsSpace = """
        module t.space @ 1.0.0;
        import t.rows @ 1.0.0 sha256:… as r;
        space s {
          source a adapter "sattle.authoring" rows r.ItemRow;
          source b adapter "sattle.authoring" rows r.ItemRow;
          master graph master = r.ingest(a: a, b: b);
        }
        """;

    const string RowsIngest = """
        graph ingest : t.rows;
        input a : ItemRow;
        input b : ItemRow;
        let both = union a, b;
        nodes Stat on x with (rule = "observed") = both | summarize n = count() by k = "bag" | project x = identity("stat", k), name = k, n;
        nodes Stat on x with (rule = "observed") = both | distinct * | summarize n = count() by k = "set" | project x = identity("stat", k), name = k, n;
        """;

    static void Put(Engine e, string source, long seq, params (string Key, long W)[] rows)
    {
        var rw = new RowWriter(e.RowLayout(source), e.Interner);
        foreach (var (key, w) in rows) { rw.Begin(w); rw.Text("key", key); rw.Text("group", "g"); rw.Int(rw.Col("score"), 1); }
        e.Ingest(source, rw.Build()!, seq);
    }

    static long Stat(Engine e, string name) => e.Master.State.Entities.Where(x => x.Props.Text("d:t.rows/prop:name") == name).Select(x => (long)((IntegerValue)x.Props.Get("d:t.rows/prop:n")!).N).SingleOrDefault();

    [Fact]
    public void Union_adds_signed_rows_and_distinct_keeps_one_copy_while_any_copy_remains()
    {
        var cat = new Catalog(new KqlSource());
        var res = cat.Load([new SourceFile("t.rows.sattle", RowsModule), new SourceFile("t.rows.ingest.kql", RowsIngest), new SourceFile("t.space.sattle", RowsSpace)], updatePins: true);
        Assert.Empty(res.Diagnostics.Where(d => d.IsError).Select(d => d.ToString()));
        using var e = new Engine(cat, cat.Spaces.Keys.Single());
        Put(e, "a", 1, ("x", 1), ("y", 1));
        Put(e, "b", 2, ("x", 1));
        Assert.Equal(3, Stat(e, "bag")); Assert.Equal(2, Stat(e, "set"));          // x is in both inputs
        Put(e, "a", 3, ("x", -1));
        Assert.Equal(2, Stat(e, "bag")); Assert.Equal(2, Stat(e, "set"));          // b still has x
        Put(e, "b", 4, ("x", -1));
        Assert.Equal(1, Stat(e, "bag")); Assert.Equal(1, Stat(e, "set"));
    }

    [Fact]
    public void Union_of_unequal_rows_is_rejected_with_a_typed_reason()
    {
        var res = new Catalog(new KqlSource()).Load([new SourceFile("t.rows.sattle", RowsModule), new SourceFile("t.rows.ingest.kql", RowsIngest.Replace("input b : ItemRow;", "input b : OtherRow;")),
            new SourceFile("t.space.sattle", RowsSpace.Replace("source b adapter \"sattle.authoring\" rows r.ItemRow;", "source b adapter \"sattle.authoring\" rows r.OtherRow;"))], updatePins: true);
        Assert.Contains(res.Diagnostics, d => d.IsError && d.Message.Contains("the tables of a union have the same columns"));
    }

    /// <summary>Code review 2, defect 20: two rows that write one number at two scales (1.5 and 1.50) are rows with the same value in every step and in the results. When both are retracted, in one chunk or one row at a time, nothing is left of them. Before, the results kept 1.5 at +1 and 1.50 at -1 for ever.</summary>
    [Theory]
    [InlineData("project", false)] [InlineData("project", true)]
    [InlineData("distinct", false)] [InlineData("distinct", true)]
    [InlineData("join", false)] [InlineData("join", true)]
    public void Rows_that_differ_only_in_decimal_scale_leave_nothing_when_they_are_retracted(string shape, bool oneChunk)
    {
        var between = shape switch
        {
            "distinct" => " | distinct *",
            "join" => " | join kind=inner (graph(\"master\") | graph-match (t) where labels(t) has \"Tag\" project tk = t.key) on $left.g == $right.tk",
            _ => "",
        };
        using var e = Mini(Summary, "nodes Summary on x = graph(\"master\") | graph-match (i) where labels(i) has \"Item\" project g = i.group, w = i.weight" + between + " | project x = identity(\"summary\", g), group = g, heaviest = w;\n");
        Tags(e, 1, ("g1", "L", 1));
        // a1 comes at 1.5 and goes at 1.50; a2 comes at 1.50 and goes at 1.5
        (string, string, long, decimal?, long)[] log = [("a1", "g1", 1, 1.5m, 1), ("a2", "g1", 1, 1.50m, 1), ("a1", "g1", 1, 1.50m, -1), ("a2", "g1", 1, 1.5m, -1)];
        if (oneChunk) { Items(e, 2, log[..2]); Assert.Equal(new DecimalValue(1.5m), Summaries(e, "Summary", "group", "heaviest")["g1"]); Items(e, 3, log[2..]); }
        else { var seq = 1L; foreach (var row in log) Items(e, ++seq, row); }
        Assert.DoesNotContain(e.Graph("master").State.Entities, x => Ids.Name(x.Type) == "Item");
        Assert.Empty(Summaries(e, "Summary", "group", "heaviest"));
    }

    const string Paired = "entity Summary { group: Text; }\nidentity summary for Summary version \"1\" { group: Text by exact; }\nrelationship Paired (base.Item) -> (base.Tag);";

    /// <summary>A union takes columns that name elements: both sides have the column with the same element type, and each side may have read other properties of its element before. After the union the column is still an element for a step, and its rows follow insertions and retractions on both sides.</summary>
    [Fact]
    public void A_union_takes_element_columns_and_a_step_uses_them()
    {
        using var e = Mini(Paired, """
            let tags = graph("master") | graph-match (t) where labels(t) has "Tag" project t = t.id, tk = t.key;
            let high = graph("master") | graph-match (i) where labels(i) has "Item" and i.score > 5 project item = i.id, g = i.group;
            let heavy = graph("master") | graph-match (i) where labels(i) has "Item" and coalesce(i.weight, 0.0) > 1.0 project item = i.id, g = i.group;
            edges Paired from item to t = union high, heavy | join kind=inner (tags) on $left.g == $right.tk | project item, t;
            """);
        Tags(e, 1, ("g1", "L", 1));
        Items(e, 2, ("a1", "g1", 9, null, 1), ("a2", "g1", 1, 2.5m, 1), ("a3", "g1", 9, 2.5m, 1), ("a4", "g1", 1, null, 1), ("b1", "g2", 9, 2.5m, 1));
        HashSet<string> Sources() => [.. e.Graph("rolled").State.Rels.Where(r => Ids.Name(r.Type) == "Paired").Select(r => e.Graph("master").State.Entity(r.Source)!.Props.Text("d:t.base/prop:key")!)];
        Assert.Equal(["a1", "a2", "a3"], Sources().Order());                      // a3 comes from both sides and is one relationship
        Items(e, 3, ("a3", "g1", 9, 2.5m, -1), ("a3", "g1", 9, null, 1));        // a3 leaves one side and stays through the other
        Assert.Equal(["a1", "a2", "a3"], Sources().Order());
        Items(e, 4, ("a3", "g1", 9, null, -1), ("a1", "g1", 9, null, -1));
        Assert.Equal(["a2"], Sources().Order());
    }

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

    static string JoinSteps(string on, string after = "") => $$"""
        let items = graph("master") | graph-match (i) where labels(i) has "Item" project g = i.group, k = i.key;
        let tags = graph("master") | graph-match (t) where labels(t) has "Tag" project tk = t.key, label = t.label;
        nodes Summary on x = items | join kind=inner (tags) on {{on}}{{after}} | project x = identity("summary", k), group = k, label;
        """;

    /// <summary>A join follows both sides; a condition beyond the equality is a where after it.</summary>
    [Theory]
    [InlineData("", "a1,a2")]
    [InlineData(" | where k != \"a2\"", "a1")]
    public void Join_follows_inserts_and_retractions_on_both_sides_and_a_where_after_it_filters(string after, string expected)
    {
        using var e = Mini(Labelled, JoinSteps("$left.g == $right.tk", after));
        Items(e, 1, ("a1", "g1", 1, null, 1), ("a2", "g1", 2, null, 1), ("b1", "g2", 3, null, 1));
        Assert.Empty(Summaries(e, "Summary", "group", "label"));                                  // no tag yet: nothing joins
        Tags(e, 2, ("g1", "L1", 1));
        Assert.Equal(expected.Split(','), Summaries(e, "Summary", "group", "label").Keys.Order());
        Assert.All(Summaries(e, "Summary", "group", "label").Values, v => Assert.Equal(new TextValue("L1"), v));
        Items(e, 3, ("a1", "g1", 1, null, -1));                                                   // a left row goes
        Assert.DoesNotContain("a1", Summaries(e, "Summary", "group", "label").Keys);
        Tags(e, 4, ("g1", "L1", -1), ("g1", "L2", 1));                                            // the right row is replaced in one batch
        Assert.Equal(expected.Split(',').Where(k => k != "a1"), Summaries(e, "Summary", "group", "label").Keys.Order());
        Assert.All(Summaries(e, "Summary", "group", "label").Values, v => Assert.Equal(new TextValue("L2"), v));
        Tags(e, 5, ("g1", "L2", -1));
        Assert.Empty(Summaries(e, "Summary", "group", "label"));
    }

    [Fact]
    public void Join_rejects_a_condition_without_an_equality() =>
        Assert.Contains(Errors(ViewFiles(Labelled, JoinSteps("$left.g != $right.tk"))), d => d.Contains("The join condition must be either the name of a column common to both tables or in the form $left.<column> == $right.<column>"));

    const string Stats = "entity Summary { group: Text; avg?: Decimal; avgW?: Decimal; big?: Boolean; small?: Boolean; newest?: Text; }\nidentity summary for Summary version \"1\" { group: Text by exact; }";

    /// <summary>`avg`, `arg_max`, and any and all written as `countif`, over inserts and retractions; an average over no value is null.</summary>
    [Fact]
    public void Avg_countif_and_arg_max_follow_inserts_and_retractions_and_are_null_over_no_value()
    {
        using var e = Mini(Stats, "nodes Summary on x = " + Matched + " | summarize avg = avg(score), avgW = avg(weight), bigN = countif(score > 4), smallN = countif(score >= 10), arg_max(score, key) by group"
            + " | project x = identity(\"summary\", group), group, avg, avgW, big = bigN > 0, small = smallN == 0, newest = key;\n");
        Items(e, 1, ("a1", "a", 3, 1.5m, 1), ("a2", "a", 7, null, 1), ("a3", "a", 5, 2.5m, 1), ("b1", "b", 12, null, 1));
        Value V(string g, string p) => Summaries(e, "Summary", "group", p)[g];
        Assert.Equal(new DecimalValue(5m), V("a", "avg")); Assert.Equal(new DecimalValue(2m), V("a", "avgW"));     // the null weight is skipped
        Assert.Equal(new BooleanValue(true), V("a", "big")); Assert.Equal(new BooleanValue(true), V("a", "small")); Assert.Equal(new TextValue("a2"), V("a", "newest"));
        Assert.Equal(new BooleanValue(false), V("b", "small")); Assert.Equal(Value.Null, V("b", "avgW"));         // no weight at all: null
        Items(e, 2, ("a2", "a", 7, null, -1));                                                                   // the newest row goes: the next one takes over
        Assert.Equal(new TextValue("a3"), V("a", "newest")); Assert.Equal(new DecimalValue(4m), V("a", "avg")); Assert.Equal(new BooleanValue(true), V("a", "big"));
        Items(e, 3, ("a3", "a", 5, 2.5m, -1));
        Assert.Equal(new BooleanValue(false), V("a", "big")); Assert.Equal(new TextValue("a1"), V("a", "newest"));
    }

    const string GraphBase = """
        module t.graph @ 1.0.0;
        entity Node { key: Text; name?: Text; }
        relationship Edge (Node) -> (Node);
        identity node for Node version "1" { key: Text by exact; }
        rows NodeRow { key: Text; name?: Text; }
        rows EdgeRow { src: Text; dst: Text; }
        """;

    const string GraphIngest = """
        graph ingest : t.graph;
        input nodes : NodeRow;
        input edges : EdgeRow;
        nodes Node on n with (rule = "observed") = nodes | project n = identity("node", key), key, name;
        edges Edge from s to d with (rule = "observed") = edges | project s = identity("node", src), d = identity("node", dst);
        """;

    const string Reach = """
        module t.reach @ 1.0.0;
        import t.graph @ 1.0.0 sha256:… as g;
        entity Reached { key: Text; name?: Text; }
        identity reached for Reached version "1" { key: Text by exact; }
        """;

    const string ReachSpace = """
        module t.space @ 1.0.0;
        import t.graph @ 1.0.0 sha256:… as g;
        import t.reach @ 1.0.0 sha256:… as r;
        space s {
          source nodes adapter "sattle.authoring" rows g.NodeRow;
          source edges adapter "sattle.authoring" rows g.EdgeRow;
          master graph master = g.ingest(nodes: nodes, edges: edges);
          graph reached = r.reach(master: master);
        }
        """;

    /// <summary>The nodes a path of `edge` reaches from the node named root.</summary>
    static LoadResult LoadReach(string edge, out Catalog cat)
    {
        cat = new Catalog(new KqlSource());
        return cat.Load([new SourceFile("t.graph.sattle", GraphBase), new SourceFile("t.graph.ingest.kql", GraphIngest), new SourceFile("t.reach.sattle", Reach), new SourceFile("t.space.sattle", ReachSpace),
            new SourceFile("t.reach.reach.kql", "graph reach : t.reach;\ninput master : t.graph;\n"
                + $"nodes Reached on x = graph(\"master\") | graph-match (n){edge}(d) where labels(n) has \"Node\" and all(p, labels(p) has \"Edge\") and n.name == \"root\" project x = identity(\"reached\", d.key), key = d.key, name = d.name;\n")], updatePins: true);
    }

    static void Nodes(Engine e, long seq, params (string Key, string? Name, long W)[] rows)
    {
        var rw = new RowWriter(e.RowLayout("nodes"), e.Interner);
        foreach (var (key, name, w) in rows) { rw.Begin(w); rw.Text("key", key); rw.Text("name", name); }
        e.Ingest("nodes", rw.Build()!, seq);
    }
    static void Edges(Engine e, long seq, params (string Src, string Dst, long W)[] rows)
    {
        var rw = new RowWriter(e.RowLayout("edges"), e.Interner);
        foreach (var (src, dst, w) in rows) { rw.Begin(w); rw.Text("src", src); rw.Text("dst", dst); }
        e.Ingest("edges", rw.Build()!, seq);
    }
    static Dictionary<string, string?> Reached(Engine e) =>
        e.Graph("reached").State.Entities.ToDictionary(x => x.Props.Text("d:t.reach/prop:key")!, x => x.Props.Text("d:t.reach/prop:name"));

    /// <summary>A path of zero to two edges, along the edges or in either direction, reaches the start itself and what is within two edges; it follows edge, node and property changes.</summary>
    [Theory]
    [InlineData("-[p*0..2]->", "a,b,c")]
    [InlineData("-[p*0..2]-", "a,b,c,x")]
    public void Traverse_reaches_to_the_depth_and_follows_edge_node_and_property_changes(string edge, string expected)
    {
        var res = LoadReach(edge, out var cat);
        Assert.Empty(res.Diagnostics.Where(d => d.IsError).Select(d => d.ToString()));
        using var e = new Engine(cat, cat.Spaces.Keys.Single());
        Nodes(e, 1, ("a", "root", 1), ("b", null, 1), ("c", null, 1), ("d", null, 1), ("x", null, 1));
        Edges(e, 2, ("a", "b", 1), ("b", "c", 1), ("c", "d", 1), ("x", "a", 1));
        string Show() => string.Join(",", Reached(e).Keys.Order(StringComparer.Ordinal));
        Assert.Equal(expected, Show());                                                           // d is three edges away
        Edges(e, 3, ("a", "c", 1));
        Assert.Contains("d", Reached(e).Keys);                                                   // a shorter path to c brings d within two edges
        Edges(e, 4, ("a", "c", -1), ("b", "c", -1));
        Assert.DoesNotContain("c", Reached(e).Keys);                                              // the last path to c is gone
        Nodes(e, 5, ("b", null, -1), ("b", "B", 1));                                              // a property of a reached element changes
        Assert.Equal("B", Reached(e)["b"]);
        Nodes(e, 6, ("a", "root", -1));                                                           // the start leaves
        Assert.Empty(Reached(e));
    }

    [Fact]
    public void Traverse_rejects_bad_path_bounds() =>
        Assert.Contains(LoadReach("-[p*3..2]->", out _).Diagnostics, d => d.IsError && d.Message.Contains("a path starts at 0 or 1 edges"));

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
    public void A_datatable_is_constant_rows_that_join_with_data()
    {
        using var e = Mini(Labelled, DatatableSteps());
        Items(e, 1, ("a1", "g1", 1, null, 1), ("b1", "g2", 2, null, 1), ("c1", "g3", 3, null, 1));
        var labels = Summaries(e, "Summary", "group", "label");
        Assert.Equal(new TextValue("L1"), labels["a1"]); Assert.Equal(new TextValue("L2"), labels["b1"]); Assert.DoesNotContain("c1", labels.Keys);
        Items(e, 2, ("a1", "g1", 1, null, -1));
        Assert.DoesNotContain("a1", Summaries(e, "Summary", "group", "label").Keys);
    }

    [Fact]
    public void Datatable_rows_must_have_the_types_of_its_columns() =>
        Assert.Contains(Errors(ViewFiles(Labelled, DatatableSteps("datatable(tk:string, label:string)[\"g1\", \"L1\", \"g2\", 2]"))), d => d.Contains("all rows of values need the same columns of the same types"));
}
