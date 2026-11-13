using System.Text.Json.Nodes;
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
        var cat = new Catalog();
        var res = cat.Load(files, updatePins: true);
        Assert.True(!res.Diagnostics.Any(x => x.IsError), string.Join("\n", res.Diagnostics.Where(x => x.IsError)));
        return (files, cat);
    });

    const string Head = "graph serviceDeps : acme.svc;\ninput master : acme.ops;\n"
        + "let services = graph(\"master\") | graph-match (s) where labels(s) has \"Service\" project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name;\n";

    /// <summary>Every operator the engine runs for a projection is on a card of the outline: a KQL operator, a step's sink, or a table function written out at its call.</summary>
    [Theory]
    [InlineData("acme.svc.serviceDeps.kql")]
    [InlineData("acme.biz.capabilityView.kql")]
    [InlineData("acme.deployhealth.deploymentHealth.kql")]
    [InlineData("acme.report.deploymentReport.kql")]
    [InlineData("acme.workload.workloadStructure.kql")]
    [InlineData("acme.workload.workloadHealth.kql")]
    [InlineData("acme.treereport.healthReport.kql")]
    [InlineData("acme.tree.treeStructure.kql")]
    [InlineData("acme.tree.treeSignals.kql")]
    [InlineData("acme.treehealth.treeRollup.kql")]
    [InlineData("acme.treehealth.healthTree.kql")]
    [InlineData("acme.ops.ingest.kql")]
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

    /// <summary>What is wrong with a text is placed on the words it is about.</summary>
    [Theory]
    [InlineData("nodes ServiceNode on service = services | take 10;\n", "profile", "take 10")]
    [InlineData("nodes ServiceNode on service = services | where nosuch > 1;\n", "kusto", "nosuch")]
    [InlineData("input pods : k8s.NoSuchRow;\nnodes ServiceNode on service = services;\n", "profile", "input pods : k8s.NoSuchRow;")]
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
    [InlineData("nodes ", "ServiceNode", true)]
    [InlineData("let x = graph(\"master\") | graph-match (p) project v = identity(\"", "podUid", true)]
    [InlineData("let x = services | ", "where", true)]
    [InlineData("let x = services | ", "take", false)]
    public void Completion_offers_what_the_ontology_knows_and_leaves_out_what_the_profile_refuses(string before, string item, bool offered)
    {
        var text = Head + before;
        var items = KqlAuthoring.Complete(Defs.Value.Catalog, "acme.svc.serviceDeps", text, text.Length)["items"]!.AsArray().Select(i => i!["text"]!.GetValue<string>()).ToList();
        Assert.Equal(offered, items.Contains(item));
    }

    /// <summary>Hover gives a type's description from the ontology, and Kusto's description of a let.</summary>
    [Theory]
    [InlineData("labels(s) has \"Ser", "ontology")]
    [InlineData("= servi", "kusto")]
    public void Hover_tells_what_a_name_is(string at, string from)
    {
        var text = Head + "nodes ServiceNode on service = services;\n";
        var h = KqlAuthoring.Hover(Defs.Value.Catalog, "acme.svc.serviceDeps", text, text.IndexOf(at, StringComparison.Ordinal) + at.Length);
        Assert.NotNull(h[from]);
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
