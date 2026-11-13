using Sattle.Core;
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
        projection ingest ingestion output t.base {
          in items: rows ItemRow;
          in tags: rows TagRow;
          in extras: rows ExtraRow;
          rule observed readonly "test" {
            emit entity Item from items identity item(key: key) set { key: key, group: group, score: score, weight: weight };
            emit entity Tag from tags identity tag(key: key) set { key: key, label: label };
          }
        }
        """;

    static string View(string body, string ontology) => $$"""
        module t.view @ 1.0.0;
        import t.base @ 1.0.0 sha256:… as base;
        {{ontology}}
        projection roll view forward output t.view {
          in master: graph base;
          {{body}}
        }
        space s {
          source items adapter "sattle.authoring" rows base.ItemRow;
          source tags adapter "sattle.authoring" rows base.TagRow;
          source extras adapter "sattle.authoring" rows base.ExtraRow;
          master graph master = base.ingest(items: items, tags: tags, extras: extras);
          graph rolled = roll(master: master);
        }
        """;

    static Engine Mini(string ontology, string body)
    {
        var cat = new Catalog();
        var res = cat.Load([new SourceFile("t.base.sattle", Base), new SourceFile("t.view.sattle", View(body, ontology))], updatePins: true);
        Assert.Empty(res.Diagnostics.Where(d => d.IsError).Select(d => d.ToString()));
        return new Engine(cat, cat.Spaces.Keys.Single());
    }

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

    [Fact]
    public void Min_and_max_follow_inserts_and_retractions_and_are_null_over_no_value()
    {
        using var e = Mini(Summary, """
            let items = match master (i: base.Item);
            let g = items |> group by { group: i.group } aggregate { worst: max(i.score), best: min(i.score), heaviest: max(i.weight) };
            emit entity Summary from g identity summary(group: group) set { group: group, worst: worst, best: best, heaviest: heaviest };
            """);
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

    const string Stacked = """
        module t.top @ 1.0.0;
        import t.view @ 1.0.0 sha256:… as v;
        entity Top { worst: Integer; groups: Integer; }
        identity top for Top version "1" { name: Text by exact; }
        projection top view forward output t.top {
          in rolled: graph v;
          let all = match rolled (s: v.Summary);
          let g = all |> group by { k: "all" } aggregate { worst: max(s.worst), groups: count() };
          emit entity Top from g identity top(name: k) set { worst: worst, groups: groups };
        }
        """;

    [Fact]
    public void A_view_reads_the_graph_of_another_view_and_follows_its_changes()
    {
        const string v1 = """
            module t.view @ 1.0.0;
            import t.base @ 1.0.0 sha256:… as base;
            entity Summary { group: Text; worst?: Integer; }
            identity summary for Summary version "1" { group: Text by exact; }
            projection roll view forward output t.view {
              in master: graph base;
              let items = match master (i: base.Item);
              let g = items |> group by { group: i.group } aggregate { worst: max(i.score) };
              emit entity Summary from g identity summary(group: group) set { group: group, worst: worst };
            }
            """;
        const string v2 = """
            module t.top @ 1.0.0;
            import t.view @ 1.0.0 sha256:… as v;
            entity Top { worst?: Integer; groups: Integer; }
            identity top for Top version "1" { name: Text by exact; }
            projection top view forward output t.top {
              in rolled: graph v;
              let all = match rolled (s: v.Summary);
              let g = all |> group by { k: "all" } aggregate { worst: max(s.worst), groups: count() };
              emit entity Top from g identity top(name: k) set { worst: worst, groups: groups };
            }
            """;
        const string sp = """
            module t.space @ 1.0.0;
            import t.base @ 1.0.0 sha256:… as base;
            import t.view @ 1.0.0 sha256:… as v;
            import t.top @ 1.0.0 sha256:… as t2;
            space s {
              source items adapter "sattle.authoring" rows base.ItemRow;
              source tags adapter "sattle.authoring" rows base.TagRow;
              source extras adapter "sattle.authoring" rows base.ExtraRow;
              master graph master = base.ingest(items: items, tags: tags, extras: extras);
              graph rolled = v.roll(master: master);
              graph top = t2.top(rolled: rolled);
            }
            """;
        var cat = new Catalog();
        var res = cat.Load([new SourceFile("t.base.sattle", Base), new SourceFile("t.view.sattle", v1), new SourceFile("t.top.sattle", v2), new SourceFile("t.space.sattle", sp)], updatePins: true);
        Assert.Empty(res.Diagnostics.Where(d => d.IsError).Select(d => d.ToString()));
        using var e = new Engine(cat, cat.Spaces.Keys.Single());
        Items(e, 1, ("a1", "a", 3, null, 1), ("b1", "b", 9, null, 1));
        long Worst() => ((IntegerValue)e.Graph("top").State.Entities.Single().Props.Get("d:t.top/prop:worst")!).N is var n ? (long)n : 0;
        Assert.Equal(9, Worst());
        Items(e, 2, ("b1", "b", 9, null, -1));
        Assert.Equal(3, Worst());
    }

    const string RowsModule = """
        module t.rows @ 1.0.0;
        entity Stat { name: Text; n: Integer; }
        identity stat for Stat version "1" { name: Text by exact; }
        rows ItemRow { key: Text; group: Text; score: Integer; }
        projection ingest ingestion output t.rows {
          in a: rows ItemRow;
          in b: rows ItemRow;
          rule observed readonly "test" {
            let both = union a, b;
            let sets = both |> distinct;
            let c1 = both |> group by { k: "bag" } aggregate { n: count() };
            let c2 = sets |> group by { k: "set" } aggregate { n: count() };
            emit entity Stat from c1 identity stat(name: k) set { name: k, n: n };
            emit entity Stat from c2 identity stat(name: k) set { name: k, n: n };
          }
        }
        space s {
          source a adapter "sattle.authoring" rows ItemRow;
          source b adapter "sattle.authoring" rows ItemRow;
          master graph master = ingest(a: a, b: b);
        }
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
        var cat = new Catalog();
        var res = cat.Load([new SourceFile("t.rows.sattle", RowsModule)], updatePins: true);
        Assert.Empty(res.Diagnostics.Where(d => d.IsError).Select(d => d.ToString()));
        var printed = Printer.Print(cat.Graph, "t.rows");
        Assert.Contains("let both = union a, b;", printed); Assert.Contains("|> distinct", printed);
        var again = new Catalog(); Assert.DoesNotContain(again.Load([new SourceFile("t.rows.sattle", printed)], updatePins: true).Diagnostics, d => d.IsError);
        Assert.Equal(cat.Modules["t.rows"].Digest, again.Modules["t.rows"].Digest);          // the canonical text round-trips
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
    public void Union_of_unequal_rows_and_an_unsupported_conflict_policy_are_rejected_with_a_typed_reason()
    {
        var unequal = RowsModule.Replace("rows ItemRow { key: Text; group: Text; score: Integer; }", "rows ItemRow { key: Text; group: Text; score: Integer; }\nrows OtherRow { key: Text; }").Replace("in b: rows ItemRow;", "in b: rows OtherRow;").Replace("source b adapter \"sattle.authoring\" rows ItemRow;", "source b adapter \"sattle.authoring\" rows OtherRow;");
        Assert.Contains(Load(unequal), d => d.Contains("same columns"));
        Assert.Contains(Load(RowsModule.Replace("union a, b;", "union a, b conflict preferFirst;")), d => d.Contains("needs a row key"));
    }

    static List<string> Load(string text) => new Catalog().Load([new SourceFile("t.rows.sattle", text)], updatePins: true).Diagnostics.Where(d => d.IsError).Select(d => d.ToString()).ToList();

    [Fact]
    public void A_call_with_more_than_two_arguments_survives_print_and_parse()
    {
        var text = RowsModule.Replace("identity stat(name: k) set { name: k, n: n };", "identity stat(name: k) set { name: concat(k, \"/\", \"x\", \"/\", k), n: n };");
        var cat = new Catalog();
        Assert.DoesNotContain(cat.Load([new SourceFile("t.rows.sattle", text)], updatePins: true).Diagnostics, d => d.IsError);
        var printed = Printer.Print(cat.Graph, "t.rows");
        Assert.Contains("concat(k, \"/\", \"x\", \"/\", k)", printed);
        var again = new Catalog();
        Assert.DoesNotContain(again.Load([new SourceFile("t.rows.sattle", printed)], updatePins: true).Diagnostics, d => d.IsError);
        Assert.Equal(cat.Modules["t.rows"].Digest, again.Modules["t.rows"].Digest);
    }

    /// <summary>Code review 2, defect 20: two rows that write one number at two scales (1.5 and 1.50) are rows with the same value in every step and in the results. When both are retracted, in one chunk or one row at a time, nothing is left of them. Before, the results kept 1.5 at +1 and 1.50 at -1 for ever.</summary>
    [Theory]
    [InlineData("select", false)] [InlineData("select", true)]
    [InlineData("distinct", false)] [InlineData("distinct", true)]
    [InlineData("join", false)] [InlineData("join", true)]
    public void Rows_that_differ_only_in_decimal_scale_leave_nothing_when_they_are_retracted(string shape, bool oneChunk)
    {
        var body = "let items = match master (i: base.Item) |> select { g: i.group, w: i.weight };\n" + shape switch
        {
            "distinct" => "let d = items |> distinct;\nemit entity Summary from d identity summary(group: g) set { group: g, heaviest: w };",
            "join" => "let tags = match master (t: base.Tag) |> select { tk: t.key };\nlet j = join items, tags on g == tk;\nemit entity Summary from j identity summary(group: g) set { group: g, heaviest: w };",
            _ => "emit entity Summary from items identity summary(group: g) set { group: g, heaviest: w };",
        };
        using var e = Mini(Summary, body);
        Tags(e, 1, ("g1", "L", 1));
        // a1 comes at 1.5 and goes at 1.50; a2 comes at 1.50 and goes at 1.5
        (string, string, long, decimal?, long)[] log = [("a1", "g1", 1, 1.5m, 1), ("a2", "g1", 1, 1.50m, 1), ("a1", "g1", 1, 1.50m, -1), ("a2", "g1", 1, 1.5m, -1)];
        if (oneChunk) { Items(e, 2, log[..2]); Assert.Equal(new DecimalValue(1.5m), Summaries(e, "Summary", "group", "heaviest")["g1"]); Items(e, 3, log[2..]); }
        else { var seq = 1L; foreach (var row in log) Items(e, ++seq, row); }
        Assert.DoesNotContain(e.Graph("master").State.Entities, x => Ids.Name(x.Type) == "Item");
        Assert.Empty(Summaries(e, "Summary", "group", "heaviest"));
    }

    const string Labelled = "entity Summary { group: Text; label?: Text; }\nidentity summary for Summary version \"1\" { group: Text by exact; }";

    static void Tags(Engine e, long seq, params (string Key, string Label, long W)[] rows)
    {
        var rw = new RowWriter(e.RowLayout("tags"), e.Interner);
        foreach (var (key, label, w) in rows) { rw.Begin(w); rw.Text("key", key); rw.Text("label", label); }
        e.Ingest("tags", rw.Build()!, seq);
    }

    static string JoinBody(string on) => $$"""
        let items = match master (i: base.Item) |> select { g: i.group, k: i.key };
        let tags = match master (t: base.Tag) |> select { tk: t.key, label: t.label };
        let j = join items, tags on {{on}};
        emit entity Summary from j identity summary(group: k) set { group: k, label: label };
        """;

    [Theory]
    [InlineData("g == tk", "a1,a2")]
    [InlineData("g == tk and k != \"a2\"", "a1")]
    public void Join_follows_inserts_and_retractions_on_both_sides_and_applies_the_residual(string on, string expected)
    {
        using var e = Mini(Labelled, JoinBody(on));
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
    public void Join_prints_canonically_and_rejects_clashing_columns_and_conditions_without_an_equality()
    {
        var text = View(JoinBody("g == tk"), Labelled);
        var cat = new Catalog();
        Assert.DoesNotContain(cat.Load([new SourceFile("t.base.sattle", Base), new SourceFile("t.view.sattle", text)], updatePins: true).Diagnostics, d => d.IsError);
        var printed = Printer.Print(cat.Graph, "t.view");
        Assert.Contains("join items, tags on g == tk", printed);
        var again = new Catalog();
        Assert.DoesNotContain(again.Load([new SourceFile("t.base.sattle", Base), new SourceFile("t.view.sattle", printed)], updatePins: true).Diagnostics, d => d.IsError);
        Assert.Equal(cat.Modules["t.view"].Digest, again.Modules["t.view"].Digest);
        string Errors(string body) { var c = new Catalog(); return string.Join("|", c.Load([new SourceFile("t.base.sattle", Base), new SourceFile("t.view.sattle", View(body, Labelled))], updatePins: true).Diagnostics.Where(d => d.IsError).Select(d => d.Message)); }
        Assert.Contains("needs at least one equality", Errors(JoinBody("g != tk")));
        Assert.Contains("both inputs have a column named", Errors(JoinBody("g == tk").Replace("tk: t.key", "g: t.key").Replace("g == tk", "g == g")));
    }

    const string Stats = "entity Summary { group: Text; avg?: Decimal; wavg?: Decimal; avgW?: Decimal; big?: Boolean; small?: Boolean; newest?: Text; }\nidentity summary for Summary version \"1\" { group: Text by exact; }";

    [Fact]
    public void Mean_any_all_and_last_follow_inserts_and_retractions_and_are_null_over_no_value()
    {
        using var e = Mini(Stats, """
            let items = match master (i: base.Item);
            let g = items |> group by { group: i.group } aggregate { avg: mean(i.score), avgW: mean(i.weight), wavg: weightedMean(i.score, i.weight), big: any(i.score > 4), small: all(i.score < 10), newest: last(i.key, i.score) };
            emit entity Summary from g identity summary(group: group) set { group: group, avg: avg, avgW: avgW, wavg: wavg, big: big, small: small, newest: newest };
            """);
        Items(e, 1, ("a1", "a", 3, 1.5m, 1), ("a2", "a", 7, null, 1), ("a3", "a", 5, 2.5m, 1), ("b1", "b", 12, null, 1));
        Value V(string g, string p) => Summaries(e, "Summary", "group", p)[g];
        Assert.Equal(new DecimalValue(5m), V("a", "avg")); Assert.Equal(new DecimalValue(2m), V("a", "avgW"));     // the null weight is skipped
        Assert.Equal(new DecimalValue(4.25m), V("a", "wavg")); Assert.Equal(Value.Null, V("b", "wavg"));
        Assert.Equal(new BooleanValue(true), V("a", "big")); Assert.Equal(new BooleanValue(true), V("a", "small")); Assert.Equal(new TextValue("a2"), V("a", "newest"));
        Assert.Equal(new BooleanValue(false), V("b", "small")); Assert.Equal(Value.Null, V("b", "avgW"));         // no weight at all: null
        Items(e, 2, ("a2", "a", 7, null, -1));                                                                   // the newest row goes: the next one takes over
        Assert.Equal(new TextValue("a3"), V("a", "newest")); Assert.Equal(new DecimalValue(4m), V("a", "avg")); Assert.Equal(new BooleanValue(true), V("a", "big"));
        Items(e, 3, ("a3", "a", 5, 2.5m, -1));
        Assert.Equal(new BooleanValue(false), V("a", "big")); Assert.Equal(new TextValue("a1"), V("a", "newest")); Assert.Equal(new DecimalValue(3m), V("a", "wavg"));
    }

    [Fact]
    public void Aggregates_with_an_ordering_round_trip_through_the_printer()
    {
        var text = View("""
            let items = match master (i: base.Item);
            let g = items |> group by { group: i.group } aggregate { newest: last(i.key, i.score), avg: mean(i.score) };
            emit entity Summary from g identity summary(group: group) set { group: group, avg: avg, newest: newest };
            """, Stats);
        var cat = new Catalog();
        Assert.DoesNotContain(cat.Load([new SourceFile("t.base.sattle", Base), new SourceFile("t.view.sattle", text)], updatePins: true).Diagnostics, d => d.IsError);
        var printed = Printer.Print(cat.Graph, "t.view");
        Assert.Contains("last(i.key, i.score)", printed);
        var again = new Catalog();
        Assert.DoesNotContain(again.Load([new SourceFile("t.base.sattle", Base), new SourceFile("t.view.sattle", printed)], updatePins: true).Diagnostics, d => d.IsError);
        Assert.Equal(cat.Modules["t.view"].Digest, again.Modules["t.view"].Digest);
        var bad = new Catalog().Load([new SourceFile("t.base.sattle", Base), new SourceFile("t.view.sattle", text.Replace("last(i.key, i.score)", "last(i.key)"))], updatePins: true);
        Assert.Contains(bad.Diagnostics, d => d.Message.Contains("last needs an ordering"));
    }

    const string GraphBase = """
        module t.graph @ 1.0.0;
        entity Node { key: Text; name?: Text; }
        relationship Edge (Node) -> (Node);
        identity node for Node version "1" { key: Text by exact; }
        rows NodeRow { key: Text; name?: Text; }
        rows EdgeRow { src: Text; dst: Text; }
        projection ingest ingestion output t.graph {
          in nodes: rows NodeRow;
          in edges: rows EdgeRow;
          rule observed readonly "test" {
            emit entity Node from nodes identity node(key: key) set { key: key, name: name };
            emit relationship Edge from edges key edge: node(key: src) -> node(key: dst);
          }
        }
        """;

    static string ReachModules(string step) => $$"""
        module t.reach @ 1.0.0;
        import t.graph @ 1.0.0 sha256:… as g;
        entity Reached { key: Text; depth: Integer; name?: Text; }
        identity reached for Reached version "1" { key: Text by exact; }
        projection reach view forward output t.reach {
          in master: graph g;
          let roots = match master (n: g.Node) |> where coalesce(n.name, "") == "root";
          let r = roots
            |> {{step}};
          emit entity Reached from r identity reached(key: d.key) set { key: d.key, depth: d_depth, name: d.name };
        }
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

    static Engine ReachEngine(string step, out Catalog cat, out LoadResult res)
    {
        cat = new Catalog();
        res = cat.Load([new SourceFile("t.graph.sattle", GraphBase), new SourceFile("t.reach.sattle", ReachModules(step)), new SourceFile("t.space.sattle", ReachSpace)], updatePins: true);
        return res.HasErrors ? null! : new Engine(cat, cat.Spaces.Keys.Single());
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
    static Dictionary<string, (long Depth, string? Name)> Reached(Engine e) =>
        e.Graph("reached").State.Entities.ToDictionary(x => x.Props.Text("d:t.reach/prop:key")!, x => ((long)((IntegerValue)x.Props.Get("d:t.reach/prop:depth")!).N, x.Props.Text("d:t.reach/prop:name")));

    [Theory]
    [InlineData("out", "a:0,b:1,c:2")]
    [InlineData("either", "a:0,b:1,c:2,x:1")]
    public void Traverse_reaches_to_the_depth_and_follows_edge_node_and_property_changes(string direction, string expected)
    {
        using var e = ReachEngine($"traverse master via g.Edge {direction} depth 0 .. 2 from n as d", out _, out var res)!;
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
        Nodes(e, 1, ("a", "root", 1), ("b", null, 1), ("c", null, 1), ("d", null, 1), ("x", null, 1));
        Edges(e, 2, ("a", "b", 1), ("b", "c", 1), ("c", "d", 1), ("x", "a", 1));
        string Show() => string.Join(",", Reached(e).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + ":" + kv.Value.Depth));
        Assert.Equal(expected, Show());                                                           // d is three edges away
        Edges(e, 3, ("a", "c", 1));
        Assert.Contains("c:1", Show());                                                           // a shorter path wins
        Edges(e, 4, ("a", "c", -1), ("b", "c", -1));
        Assert.DoesNotContain("c:", Show());                                                      // the last path to c is gone
        Nodes(e, 5, ("b", null, -1), ("b", "B", 1));                                              // a property of a reached element changes
        Assert.Equal("B", Reached(e)["b"].Name);
        Nodes(e, 6, ("a", "root", -1));                                                           // the start leaves
        Assert.Empty(Reached(e));
    }

    [Fact]
    public void Traverse_prints_canonically_and_rejects_bad_depth_and_unknown_start()
    {
        var e = ReachEngine("traverse master via g.Edge out depth 1 .. 4 from n as d", out var cat, out var res);
        e.Dispose();
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
        var printed = Printer.Print(cat.Graph, "t.reach");
        Assert.Contains("traverse master via g.Edge out depth 1 .. 4 from n as d", printed);
        var again = new Catalog();
        Assert.DoesNotContain(again.Load([new SourceFile("t.graph.sattle", GraphBase), new SourceFile("t.reach.sattle", printed), new SourceFile("t.space.sattle", ReachSpace)], updatePins: true).Diagnostics, d => d.IsError);
        Assert.Equal(cat.Modules["t.reach"].Digest, again.Modules["t.reach"].Digest);
        _ = ReachEngine("traverse master via g.Edge out depth 3 .. 2 from n as d", out _, out var bad);
        Assert.Contains(bad.Diagnostics, d => d.Message.Contains("0 <= min <= max"));
        _ = ReachEngine("traverse master via g.Edge out depth 0 .. 2 from nope as d", out _, out var bad2);
        Assert.Contains(bad2.Diagnostics, d => d.Message.Contains("must be an element column"));
    }

    static string ParamModules(string binding, string port = "in minScore: param Integer;") => $$"""
        module t.view @ 1.0.0;
        import t.base @ 1.0.0 sha256:… as base;
        entity Summary { group: Text; worst?: Integer; }
        identity summary for Summary version "1" { group: Text by exact; }
        projection roll view forward output t.view {
          in master: graph base;
          {{port}}
          let items = match master (i: base.Item) |> where i.score >= minScore;
          let g = items |> group by { group: i.group } aggregate { worst: max(i.score) };
          emit entity Summary from g identity summary(group: group) set { group: group, worst: worst };
        }
        space s {
          source items adapter "sattle.authoring" rows base.ItemRow;
          source tags adapter "sattle.authoring" rows base.TagRow;
          source extras adapter "sattle.authoring" rows base.ExtraRow;
          master graph master = base.ingest(items: items, tags: tags, extras: extras);
          graph rolled = roll(master: master{{binding}});
        }
        """;

    static (Catalog Cat, LoadResult Res) LoadParam(string binding, string port = "in minScore: param Integer;")
    {
        var cat = new Catalog();
        return (cat, cat.Load([new SourceFile("t.base.sattle", Base), new SourceFile("t.view.sattle", ParamModules(binding, port))], updatePins: true));
    }

    [Theory]
    [InlineData(", minScore: 5", "a")]
    [InlineData(", minScore: 1", "a,b")]
    public void A_parameter_is_a_constant_of_the_space_binding(string binding, string groups)
    {
        var (cat, res) = LoadParam(binding);
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
        using var e = new Engine(cat, cat.Spaces.Keys.Single());
        Items(e, 1, ("a1", "a", 7, null, 1), ("b1", "b", 3, null, 1));
        Assert.Equal(groups.Split(','), Summaries(e, "Summary", "group", "worst").Keys.Order());
        var printed = Printer.Print(cat.Graph, "t.view");
        Assert.Contains("in minScore: param Integer;", printed); Assert.Contains(binding.TrimStart(',', ' '), printed);
        var again = new Catalog();
        Assert.DoesNotContain(again.Load([new SourceFile("t.base.sattle", Base), new SourceFile("t.view.sattle", printed)], updatePins: true).Diagnostics, d => d.IsError);
        Assert.Equal(cat.Modules["t.view"].Digest, again.Modules["t.view"].Digest);
    }

    [Theory]
    [InlineData("", "parameter minScore of roll is not bound")]
    [InlineData(", minScore: \"five\"", "parameter minScore is Integer, the binding gives Text")]
    [InlineData(", minScore: 5, other: 1", "has no port 'other'")]
    public void A_parameter_binding_must_give_every_parameter_a_constant_of_its_kind(string binding, string message)
        => Assert.Contains(LoadParam(binding).Res.Diagnostics, d => d.Message.Contains(message));

    static string FixModule(int max) => $$"""
        module t.fix @ 1.0.0;
        entity Pair { a: Text; b: Text; }
        identity pair for Pair version "1" { a: Text by exact; b: Text by exact; }
        rows EdgeRow { src: Text; dst: Text; }
        projection ingest ingestion output t.fix {
          in edges: rows EdgeRow;
          rule observed readonly "test" {
            let base = edges |> select { a: src, b: dst };
            let step = base |> select { x: a, y: b };
            let closure = fixpoint base
              via join closure, step on b == x
                |> select { a: a, b: y }
              max {{max}};
            emit entity Pair from closure identity pair(a: a, b: b) set { a: a, b: b };
          }
        }
        space s {
          source edges adapter "sattle.authoring" rows EdgeRow;
          master graph master = ingest(edges: edges);
        }
        """;

    static HashSet<string> Pairs(Engine e) => e.Master.State.Entities.Select(x => x.Props.Text("d:t.fix/prop:a") + ">" + x.Props.Text("d:t.fix/prop:b")).ToHashSet();

    [Theory]
    [InlineData(3, "a>b,a>c,a>d,b>c,b>d,c>d", "a>b,c>d")]
    [InlineData(1, "a>b,a>c,b>c,b>d,c>d", "a>b,c>d")]
    public void Fixpoint_derives_the_closure_within_its_bound_and_retracts_what_loses_its_support(int max, string full, string afterCut)
    {
        var cat = new Catalog();
        var res = cat.Load([new SourceFile("t.fix.sattle", FixModule(max))], updatePins: true);
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
        using var e = new Engine(cat, cat.Spaces.Keys.Single());
        void Put(long seq, params (string, string, long)[] rows)
        {
            var rw = new RowWriter(e.RowLayout("edges"), e.Interner);
            foreach (var (a, b, w) in rows) { rw.Begin(w); rw.Text("src", a); rw.Text("dst", b); }
            e.Ingest("edges", rw.Build()!, seq);
        }
        Put(1, ("a", "b", 1), ("b", "c", 1)); Put(2, ("c", "d", 1));
        Assert.Equal(full.Split(',').Order(), Pairs(e).Order());
        Put(3, ("b", "c", -1));                                                                // cutting the middle edge removes every pair that went through it
        Assert.Equal(afterCut.Split(',').Order(), Pairs(e).Order());
        Put(4, ("b", "c", 1));
        Assert.Equal(full.Split(',').Order(), Pairs(e).Order());
        // the canonical text round-trips
        var printed = Printer.Print(cat.Graph, "t.fix");
        Assert.Contains("fixpoint base", printed); Assert.Contains($"max {max};", printed);
        var again = new Catalog();
        Assert.DoesNotContain(again.Load([new SourceFile("t.fix.sattle", printed)], updatePins: true).Diagnostics, d => d.IsError);
        Assert.Equal(cat.Modules["t.fix"].Digest, again.Modules["t.fix"].Digest);
    }

    [Fact]
    public void A_fixpoint_step_that_changes_the_columns_is_rejected()
    {
        var bad = FixModule(3).Replace("|> select { a: a, b: y }", "|> select { a: a }");
        Assert.Contains(new Catalog().Load([new SourceFile("t.fix.sattle", bad)], updatePins: true).Diagnostics, d => d.Message.Contains("the step must produce the relation's columns"));
    }

    /// <summary>T-FIX: a closure by Fixpoint on random directed graphs with cycles equals the transitive closure, also after each retraction of an edge; with a bound below the longest derivation the missing facts are reported (Theorems 6.5 and 6.6, Def. 5.19).</summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void Fixpoint_on_cyclic_input_reaches_the_least_fixpoint_or_reports_the_bound(int seed)
    {
        var rng = new Random(seed);
        var n = rng.Next(5, 31);
        var nodes = Enumerable.Range(0, n).Select(i => "v" + i).ToArray();
        var edges = new HashSet<(string, string)>();
        var cycle = nodes.OrderBy(_ => rng.Next()).Take(3).ToArray();
        for (var i = 0; i < 3; i++) edges.Add((cycle[i], cycle[(i + 1) % 3]));
        while (edges.Count < n + rng.Next(n)) edges.Add((nodes[rng.Next(n)], nodes[rng.Next(n)]));
        static HashSet<string> Closure(IEnumerable<(string A, string B)> es)
        {
            var next = es.ToLookup(x => x.A, x => x.B); var res = new HashSet<string>();
            foreach (var start in es.Select(x => x.A).Distinct())
            {
                var seen = new HashSet<string>(); var todo = new Stack<string>(next[start]);
                while (todo.Count > 0) { var v = todo.Pop(); if (!seen.Add(v)) continue; res.Add(start + ">" + v); foreach (var w in next[v]) todo.Push(w); }
            }
            return res;
        }
        Engine Run(int max)
        {
            var cat = new Catalog();
            Assert.DoesNotContain(cat.Load([new SourceFile("t.fix.sattle", FixModule(max))], updatePins: true).Diagnostics, d => d.IsError);
            var e = new Engine(cat, cat.Spaces.Keys.Single());
            Put(e, 1, [.. edges.Select(x => (x.Item1, x.Item2, 1L))]);
            return e;
        }
        static void Put(Engine e, long seq, (string, string, long)[] rows)
        {
            var rw = new RowWriter(e.RowLayout("edges"), e.Interner);
            foreach (var (a, b, w) in rows) { rw.Begin(w); rw.Text("src", a); rw.Text("dst", b); }
            e.Ingest("edges", rw.Build()!, seq);
        }
        // a bound at least the node count: the least fixpoint, and no report
        using (var e = Run(n))
        {
            Assert.Equal(Closure(edges).Order(), Pairs(e).Order());
            Assert.Empty(e.FixpointsAtBound());
            var seq = 2L;
            foreach (var cut in edges.OrderBy(_ => rng.Next()).Take(Math.Min(8, edges.Count - 1)).ToList())
            {
                Put(e, seq++, [(cut.Item1, cut.Item2, -1)]);
                edges.Remove(cut);
                Assert.Equal(Closure(edges).Order(), Pairs(e).Order());          // retraction on a cycle removes what lost its last derivation
            }
        }
        // a bound of one step: part of the closure, and whenever a fact is missing the bound is reported
        using (var e = Run(1))
        {
            var full = Closure(edges); var got = Pairs(e);
            Assert.Subset(full, got);
            var reported = e.FixpointsAtBound().ToList();
            if (!got.SetEquals(full)) Assert.Single(reported, r => r.Fixpoint.Contains("/op:closure", StringComparison.Ordinal) && r.Gained > 0);
        }
    }

    [Fact]
    public void A_fixpoint_body_that_computes_a_value_is_refused()
    {
        var bad = FixModule(3)
            .Replace("let base = edges |> select { a: src, b: dst };", "let base = edges |> select { a: src, b: dst, d: 1 };")
            .Replace("|> select { a: a, b: y }", "|> select { a: a, b: y, d: d + 1 }");
        Assert.Contains(new Catalog().Load([new SourceFile("t.fix.sattle", bad)], updatePins: true).Diagnostics, d => d.IsError && d.Message.Contains("(F5)") && d.Message.Contains("'d'"));
    }

    const string ValuesBody = """
        let items = match master (i: base.Item) |> select { g: i.group, k: i.key };
        let labels = values { tk: "g1", label: "L1" }, { tk: "g2", label: "L2" };
        let j = join items, labels on g == tk;
        emit entity Summary from j identity summary(group: k) set { group: k, label: label };
        """;

    [Fact]
    public void Values_are_constant_rows_that_join_with_data_and_print_canonically()
    {
        using var e = Mini(Labelled, ValuesBody);
        Items(e, 1, ("a1", "g1", 1, null, 1), ("b1", "g2", 2, null, 1), ("c1", "g3", 3, null, 1));
        var labels = Summaries(e, "Summary", "group", "label");
        Assert.Equal(new TextValue("L1"), labels["a1"]); Assert.Equal(new TextValue("L2"), labels["b1"]); Assert.DoesNotContain("c1", labels.Keys);
        Items(e, 2, ("a1", "g1", 1, null, -1));
        Assert.DoesNotContain("a1", Summaries(e, "Summary", "group", "label").Keys);
        var cat = new Catalog();
        Assert.DoesNotContain(cat.Load([new SourceFile("t.base.sattle", Base), new SourceFile("t.view.sattle", View(ValuesBody, Labelled))], updatePins: true).Diagnostics, d => d.IsError);
        var printed = Printer.Print(cat.Graph, "t.view");
        Assert.Contains("values { label: \"L1\", tk: \"g1\" }, { label: \"L2\", tk: \"g2\" }", printed);
        var again = new Catalog();
        Assert.DoesNotContain(again.Load([new SourceFile("t.base.sattle", Base), new SourceFile("t.view.sattle", printed)], updatePins: true).Diagnostics, d => d.IsError);
        Assert.Equal(cat.Modules["t.view"].Digest, again.Modules["t.view"].Digest);
    }

    [Theory]
    [InlineData("values { tk: \"g1\" }, { tk: 2 }", "same columns of the same types")]
    [InlineData("values { tk: \"g1\" }, { other: \"g1\" }", "same columns of the same types")]
    [InlineData("values { tk: items }", "holds constants")]
    public void Values_rows_must_be_constants_of_one_shape(string source, string message)
    {
        var body = ValuesBody.Replace("values { tk: \"g1\", label: \"L1\" }, { tk: \"g2\", label: \"L2\" }", source);
        var res = new Catalog().Load([new SourceFile("t.base.sattle", Base), new SourceFile("t.view.sattle", View(body, Labelled))], updatePins: true);
        Assert.Contains(res.Diagnostics, d => d.Message.Contains(message) || d.Message.Contains("not a field"));
    }
}
