using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Kql.Tests;

/// <summary>What the editors ask of a KQL text while it is written (research 11, section 2): the outline, what is wrong and where, completion, hover
/// and the places that name the same let.</summary>
public class KqlAuthoringTests
{
    static readonly string Root = FindRoot();
    static string Definitions => Path.Combine(Root, "engine", "definitions");

    static readonly Lazy<(List<SourceFile> Files, Catalog Catalog)> Defs = new(() =>
    {
        var files = Catalog.ReadDirectory(Definitions).ToList();
        var cat = new Catalog(new KqlSource());
        var res = cat.Load(files, updatePins: true);
        Assert.True(!res.Diagnostics.Any(x => x.IsError), string.Join("\n", res.Diagnostics.Where(x => x.IsError)));
        return (files, cat);
    });

    const string Head = "graph serviceDeps : acme.svc;\ninput master : acme.ops;\n"
        + "let services = graph(\"master\") | graph-match (s) where labels(s) has \"Service\" project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name;\n";

    /// <summary>Every operator the engine runs for a projection is on a card of the outline: a KQL operator, a step's sink, or a table function written out at its call.</summary>
    [Theory, MemberData(nameof(Shipped))]
    public void Every_operator_the_engine_runs_is_on_a_card_of_the_outline(string kql)
    {
        var text = File.ReadAllText(Path.Combine(Definitions, kql)); var projection = kql[..^4];
        var outline = KqlAuthoring.Analyse(Defs.Value.Catalog, projection, text);
        Assert.Empty(outline["diagnostics"]!.AsArray());
        var path = Path.Combine(Definitions, kql); var cat = new Catalog(new KqlSource());
        Assert.DoesNotContain(cat.Load([.. Defs.Value.Files, new SourceFile(path, text)], updatePins: true).Diagnostics, d => d.IsError);
        var places = cat.FileOf.Where(kv => kv.Value == path && kv.Key.Contains("/op:", StringComparison.Ordinal)).ToDictionary();
        var shown = new SortedSet<string>(StringComparer.Ordinal);
        void Walk(JsonNode? n)
        {
            if (n is JsonArray a) foreach (var x in a) Walk(x);
            if (n is not JsonObject o) return;
            foreach (var (k, v) in o)
                if (k is "engine" or "expands") foreach (var id in v!.AsArray()) shown.Add(id!.GetValue<string>());
                else if (k == "sink" && v is not null) shown.Add(v.GetValue<string>());
                else Walk(v);
        }
        Walk(outline["statements"]);
        Assert.NotEmpty(places);
        Assert.Equal(places.Keys.Order(StringComparer.Ordinal), shown);
    }

    /// <summary>At full depth the text is also loaded as the engine loads it; a shipped text loads with nothing wrong.</summary>
    [Fact]
    public void A_shipped_text_loads_as_the_engine_loads_it_with_nothing_wrong()
    {
        var a = KqlAuthoring.Analyse(Defs.Value.Catalog, "acme.svc.serviceDeps", File.ReadAllText(Path.Combine(Definitions, "acme.svc.serviceDeps.kql")), Defs.Value.Files);
        Assert.Equal(("full", 0), (a["depth"]!.GetValue<string>(), a["diagnostics"]!.AsArray().Count));
    }

    /// <summary>A link step in the authored graph: the space's check that only the master links identities refuses it, an error on no operator of the text.</summary>
    const string AuthoredLink = "link podUidIsPodName from byUid to byName with (confidence = 1.0) = realizes | project byUid = identity(\"podUid\", cluster, service), byName = identity(\"podName\", cluster, namespace, service);\n";

    public static TheoryData<string> Shipped => [.. Directory.EnumerateFiles(Definitions, "*.kql").Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];

    static (int Start, int End) At(JsonNode? p) => (p![0]!.GetValue<int>(), p[1]!.GetValue<int>());

    /// <summary>Every list of an outline that an editor changes (research 11, section 5) with the separator between its items: the statements of the file,
    /// the operators of a pipeline, the items of a project, extend, summarize or distinct, the keys of a join, the legs of a union, the patterns of a
    /// graph-match, the settings of a statement, the parameters of the file and of a function. Each with the place it lies in.</summary>
    static IEnumerable<(string What, string Separator, (int Start, int End) Within, List<(int Start, int End)> Items)> Lists(JsonObject o, int length)
    {
        var top = new List<(int, int)> { At(o["graph"]!["place"]) };
        top.AddRange(o["inputs"]!.AsArray().Select(i => At(i!["place"])));
        if (o["parameters"] is JsonObject ps) { top.Add(At(ps["place"])); yield return ("parameters", ",", At(ps["place"]), [.. ps["items"]!.AsArray().Select(i => At(i!["place"]))]); }
        foreach (var f in o["functions"]!.AsArray()) { top.Add(At(f!["place"])); yield return ("function parameters", ",", At(f["place"]), [.. f["parameters"]!.AsArray().Select(x => At(x!["place"]))]); }
        yield return ("graph settings", ",", At(o["graph"]!["place"]), [.. o["graph"]!["settings"]!.AsArray().Select(x => At(x!["place"]))]);
        foreach (var st in o["statements"]!.AsArray())
        {
            top.Add(At(st!["place"]));
            yield return ("settings", ",", At(st["place"]), [.. st["settings"]!.AsArray().Select(x => At(x!["place"]))]);
            foreach (var l in Pipeline(st["operators"]!.AsArray(), At(st["query"]))) yield return l;
        }
        yield return ("statements", ";", (0, length), [.. top.Order()]);
    }

    static IEnumerable<(string, string, (int, int), List<(int, int)>)> Pipeline(JsonArray ops, (int, int) within)
    {
        yield return ("operators", "|", within, [.. ops.Select(x => At(x!["place"]))]);
        foreach (var op in ops)
        {
            var place = At(op!["place"]); var parts = op["parts"]!.AsObject();
            foreach (var key in new[] { "project", "by", "aggregates" })
                if (parts[key] is JsonArray items) yield return (op["kind"] + " " + key, ",", place, [.. items.Select(i => At(i!["place"]))]);
            foreach (var key in new[] { "on", "legs", "pattern" })
                if (parts[key] is JsonArray items) yield return (op["kind"] + " " + key, ",", place, [.. items.Select(At)]);
            foreach (var inner in op["inner"]!.AsArray()) foreach (var l in Pipeline(inner!.AsArray(), place)) yield return l;
        }
    }

    /// <summary>The columns the outline lists after an operator are columns of its rows: a `where` that names each of them, put after the operator,
    /// reads without an error in its statement (a `where` keeps the columns, so what follows reads as before). A step's last project is left out: its
    /// columns are the sink's, and an operator is added before it. So are the columns the outline marks as read elsewhere: an identity (the sink reads
    /// it) and a telemetry join's `property` and `value` (a summarize's aggregates read them).</summary>
    [Theory, MemberData(nameof(Shipped))]
    public void Every_column_the_outline_lists_after_an_operator_can_be_named_there(string kql)
    {
        var text = File.ReadAllText(Path.Combine(Definitions, kql));
        var outline = KqlAuthoring.Analyse(Defs.Value.Catalog, kql[..^4], text);
        var wrong = new List<string>();
        foreach (var st in outline["statements"]!.AsArray())
            foreach (var op in st!["operators"]!.AsArray().Where(o => !o!["inSink"]!.GetValue<bool>()))
            {
                var names = op!["columns"]!.AsArray().Where(c => c!["identity"] is null && !(c["telemetry"] is not null && c["name"]!.GetValue<string>() is "property" or "value"))
                    .Select(c => c!["name"]!.GetValue<string>()).ToList();
                if (names.Count == 0) continue;
                var (end, place) = (At(op["place"]).End, At(st["place"]));
                var edited = text[..end] + " | where " + string.Join(" and ", names.Select(n => $"isnotnull(['{n}'])")) + text[end..];
                var shift = edited.Length - text.Length;
                wrong.AddRange(KqlAuthoring.Analyse(Defs.Value.Catalog, kql[..^4], edited)["diagnostics"]!.AsArray()
                    .Where(d => d!["start"]!.GetValue<int>() >= place.Start && d["start"]!.GetValue<int>() <= place.End + shift)
                    .Select(d => $"{st["kind"]} {st["name"] ?? st["type"]} after {op["kind"]}: {d!["message"]}"));
            }
        Assert.Empty(wrong);
    }

    /// <summary>The places of an outline nest: each item of a list lies inside what holds it, the items follow each other in the text, and none overlap.</summary>
    [Theory, MemberData(nameof(Shipped))]
    public void The_items_of_every_list_lie_in_order_inside_what_holds_them(string kql)
    {
        var text = File.ReadAllText(Path.Combine(Definitions, kql));
        var outline = KqlAuthoring.Analyse(Defs.Value.Catalog, kql[..^4], text);
        Assert.Empty(outline["diagnostics"]!.AsArray());
        foreach (var (what, _, within, items) in Lists(outline, text.Length))
        {
            Assert.All(items, i => Assert.True(within.Start <= i.Start && i.Start <= i.End && i.End <= within.End, $"{what}: {i} is not inside {within}"));
            for (var k = 1; k < items.Count; k++) Assert.True(items[k - 1].End <= items[k].Start, $"{what}: {items[k - 1]} and {items[k]} overlap or are out of order");
        }
        // a step's head runs from its first token to the `=` before its query
        foreach (var st in outline["statements"]!.AsArray().Where(s => s!["head"] is not null))
        {
            var (head, place, query) = (At(st!["head"]), At(st["place"]), At(st["query"]));
            Assert.Equal((place.Start, "="), (head.Start, Regex.Replace(text[head.End..query.Start], @"//[^\n]*", "").Trim()));
            Assert.DoesNotMatch(@"\s$", text[head.Start..head.End]);
        }
    }

    /// <summary>Between two items of a list stand only its separator, white space and comments: the text an editor carries with an item when it removes,
    /// moves or copies it (research 11, section 5).</summary>
    [Theory, MemberData(nameof(Shipped))]
    public void Between_two_items_of_a_list_stand_only_its_separator_white_space_and_comments(string kql)
    {
        var text = File.ReadAllText(Path.Combine(Definitions, kql));
        foreach (var (what, separator, _, items) in Lists(KqlAuthoring.Analyse(Defs.Value.Catalog, kql[..^4], text), text.Length))
            for (var k = 1; k < items.Count; k++)
            {
                var between = Regex.Replace(text[items[k - 1].End..items[k].Start], @"//[^\n]*", "").Trim();
                Assert.True(between == separator, $"{what}: between {items[k - 1]} and {items[k]} stands '{between}'");
            }
    }

    /// <summary>At full depth every error of the engine's load is reported: one that names no operator of the text is placed at the start of the text and names its element.</summary>
    [Fact]
    public void At_full_depth_an_error_that_names_no_operator_of_the_text_is_reported_at_its_start()
    {
        var text = File.ReadAllText(Path.Combine(Definitions, "acme.ops.authored.kql")) + AuthoredLink;
        var d = KqlAuthoring.Analyse(Defs.Value.Catalog, "acme.ops.authored", text, Defs.Value.Files)["diagnostics"]!.AsArray().Single()!;
        Assert.Equal(("engine", "S1", 0, 0), (d["source"]!.GetValue<string>(), d["code"]!.GetValue<string>(), d["start"]!.GetValue<int>(), d["end"]!.GetValue<int>()));
        Assert.StartsWith("d:acme.space/space:operations: ", d["message"]!.GetValue<string>());
    }

    /// <summary>The text takes the place of the definition file of its name, however the files' paths are written (a host names a committed file by its
    /// name alone): the error of the file's old text is gone with it.</summary>
    [Fact]
    public void At_full_depth_the_text_replaces_the_file_of_its_name()
    {
        const string file = "acme.ops.authored.kql";
        var shipped = File.ReadAllText(Path.Combine(Definitions, file));
        var files = Defs.Value.Files.Select(f => Path.GetFileName(f.Path) == file ? new SourceFile(file, shipped + AuthoredLink) : f).ToList();
        Assert.Empty(KqlAuthoring.Analyse(Defs.Value.Catalog, "acme.ops.authored", shipped, files)["diagnostics"]!.AsArray());
    }

    /// <summary>What is wrong with a text is placed on the words it is about.</summary>
    [Theory]
    [InlineData("nodes ServiceNode on service = services | take 10;\n", "profile", "take 10")]
    [InlineData("nodes ServiceNode on service = services | where nosuch > 1;\n", "kusto", "nosuch")]
    [InlineData("input pods : k8s.NoSuchRow;\nnodes ServiceNode on service = services;\n", "profile", "input pods : k8s.NoSuchRow;")]
    [InlineData("nodes ServiceNode on service = services | summarize n = dcount(cluster) by service;\n", "profile", "dcount(cluster)")]
    public void What_is_wrong_is_placed_on_the_words_it_is_about(string tail, string source, string placed)
    {
        var text = Head + tail;
        var d = KqlAuthoring.Analyse(Defs.Value.Catalog, "acme.svc.serviceDeps", text)["diagnostics"]!.AsArray().Select(x => x!.AsObject()).First();
        Assert.Equal((source, placed), (d["source"]!.GetValue<string>(), text[d["start"]!.GetValue<int>()..d["end"]!.GetValue<int>()]));
    }

    /// <summary>A place begins at its item's first token and ends at its last: indents, separators and comments around it belong to the text between items.</summary>
    [Fact]
    public void A_place_begins_and_ends_with_its_own_tokens()
    {
        var text = "  graph serviceDeps : acme.svc;\n  input master : acme.ops;\n  // the services\n" + Head[(Head.IndexOf("let", StringComparison.Ordinal))..] + "  nodes ServiceNode on service = services;\n";
        var outline = KqlAuthoring.Analyse(Defs.Value.Catalog, "acme.svc.serviceDeps", text);
        var places = new List<JsonArray> { outline["graph"]!["place"]!.AsArray(), outline["inputs"]![0]!["place"]!.AsArray() };
        places.AddRange(outline["statements"]!.AsArray().Select(s => s!["place"]!.AsArray()));
        Assert.All(places, p => Assert.Matches(@"^\S(.*\S)?$", text[p[0]!.GetValue<int>()..p[1]!.GetValue<int>()].Replace("\n", " ")));
        Assert.All(places, p => Assert.False(text[p[0]!.GetValue<int>()..p[1]!.GetValue<int>()].StartsWith("//", StringComparison.Ordinal)));
    }

    /// <summary>A step's last project has no operator of its own: its expressions are the sink's.</summary>
    [Fact]
    public void A_steps_last_project_belongs_to_its_sink()
    {
        var outline = KqlAuthoring.Analyse(Defs.Value.Catalog, "acme.svc.serviceDeps", Head + "nodes ServiceNode on service = services | project service, cluster, namespace, name = strcat(name, \"\");\n");
        var step = outline["statements"]!.AsArray().Last()!;
        Assert.Equal(("nodes", true), (step["kind"]!.GetValue<string>(), step["operators"]!.AsArray().Last()!["inSink"]!.GetValue<bool>()));
        Assert.NotNull(step["sink"]);
    }

    /// <summary>Completion offers what the ontology knows where Kusto cannot, and leaves out what the profile refuses.</summary>
    [Theory]
    [InlineData("let x = graph(\"master\") | graph-match (p) where labels(p) has \"", "Pod", true)]
    [InlineData("", "nodes", true)]
    [InlineData("", "declare query_parameters", true)]
    [InlineData("nodes ", "ServiceNode", true)]
    [InlineData("let x = graph(\"master\") | graph-match (p) project v = identity(\"", "podUid", true)]
    [InlineData("let x = services | ", "where", true)]
    [InlineData("let x = services | ", "take", false)]
    [InlineData("let x = graph(\"master\") | graph-match (s) where labels(s) has \"Service\" project v = s.", "cluster", true)]
    [InlineData("let x = graph(\"master\") | graph-match (s) where labels(s) has \"Service\" project v = s.", "podUid", false)]
    public void Completion_offers_what_the_ontology_knows_and_leaves_out_what_the_profile_refuses(string before, string item, bool offered)
    {
        var text = Head + before;
        var items = KqlAuthoring.Complete(Defs.Value.Catalog, "acme.svc.serviceDeps", text, text.Length)["items"]!.AsArray().Select(i => i!["text"]!.GetValue<string>()).ToList();
        Assert.Equal(offered, items.Contains(item));
    }

    /// <summary>Hover gives a type's description from the ontology, Kusto's description of a let, and the type of a column.</summary>
    [Theory]
    [InlineData("labels(s) has \"Ser", "ontology", "sattle.infra.Service")]
    [InlineData("= servi", "kusto", "services")]
    [InlineData("project service, clu", "kusto", "string")]
    public void Hover_tells_what_a_name_is(string at, string from, string says)
    {
        var text = Head + "nodes ServiceNode on service = services | project service, cluster;\n";
        var h = KqlAuthoring.Hover(Defs.Value.Catalog, "acme.svc.serviceDeps", text, text.IndexOf(at, StringComparison.Ordinal) + at.Length);
        Assert.Contains(says, h[from]!.ToJsonString());
    }

    /// <summary>The places that name a let are its declaration and each use.</summary>
    [Fact]
    public void A_let_is_named_where_it_is_declared_and_where_it_is_used()
    {
        var text = Head + "nodes ServiceNode on service = services;\nextend nodes ServiceNode on service = services | project service, name;\n";
        var places = KqlAuthoring.Related(Defs.Value.Catalog, "acme.svc.serviceDeps", text, text.IndexOf("services", StringComparison.Ordinal) + 2)["places"]!.AsArray();
        Assert.Equal(["services", "services", "services"], places.Select(p => text[p![0]!.GetValue<int>()..p[1]!.GetValue<int>()]));
    }

    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "engine", "definitions"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
