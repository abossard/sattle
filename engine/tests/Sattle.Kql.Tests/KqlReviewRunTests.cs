using Sattle.Cli;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Kql.Tests;

/// <summary>Code review 2 (research 12): KQL texts the front end accepts and the catalog cannot load, or whose result on the estate differs from what Kusto defines for them.</summary>
public class KqlReviewRunTests
{
    static readonly string Root = FindRoot();
    static string Definitions => Path.Combine(Root, "engine", "definitions");
    static string Estate => Path.Combine(Root, "engine", "tests", "Sattle.Tests", "fixtures", "estate");

    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "engine", "definitions"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    /// <summary>The definitions with one projection in KQL given as text in place of its file: what the front end says, and what the catalog says of the text it printed (null when the front end refused).</summary>
    static (KqlFiles Put, Catalog? Catalog, List<Diagnostic> LoadErrors) Loaded(string file, string text)
    {
        var put = KqlFiles.Of(Catalog.ReadDirectory(Definitions).ToList(), [new SourceFile(Path.Combine(Definitions, file), text)]);
        if (!put.Ok) return (put, null, []);
        var cat = new Catalog();
        var res = cat.Load(put.Files, updatePins: true);
        return (put, cat, [.. res.Diagnostics.Where(x => x.IsError)]);
    }

    /// <summary>The graph a loaded catalog writes on the estate.</summary>
    static Graph Replayed(Catalog cat, string graph)
    {
        var space = cat.Spaces.Keys.Single();
        var o = Program.Options.Parse(["replay", Estate, "--cluster", "synth"]);
        var engine = new Engine(cat, space, new EngineOptions { Tenant = "lab", LateHorizonWindows = Program.LateHorizonFor(o), Only = new HashSet<string> { graph } });
        new Replay(engine, cat.Spaces[space], AdapterRegistry.Create("synth"), Program.ReplayOptionsOf(o, "synth")).Run(Estate);
        return engine.Graph(graph).State;
    }

    const string Services = "graph serviceDeps : acme.svc;\n"
        + "let services = graph(\"master\") | graph-match (s) where labels(s) has \"Service\" project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name;\n";
    const string Nodes = "nodes ServiceNode on service = services | project service, name, cluster, namespace;\n";

    /// <summary>What the front end accepts, the catalog loads: a text is refused by the front end in the author's terms, or the operators it printed parse and check.
    /// Each of these texts is valid KQL that the front end accepts; the catalog then refuses the printed module with a position in text the author did not write.</summary>
    [Theory]
    [InlineData("a summarize without by (one row in Kusto, also over no rows)", "let n = services | summarize total = count();\n" + Nodes + "extend nodes ServiceNode on service = services | join kind=inner (n | project pods = total, k = \"x\") on $left.name == $right.k | project service, pods;\n")]
    [InlineData("a summarize with keys and no aggregate", "nodes ServiceNode on service = services | summarize by service, name, cluster, namespace;\n")]
    [InlineData("a datatable without rows", Nodes + "let d = datatable(nm:string)[];\nextend nodes ServiceNode on service = services | join kind=inner (d) on $left.name == $right.nm | project service;\n")]
    [InlineData("a negative default of a query parameter", "declare query_parameters(limit:long = -1);\nnodes ServiceNode on service = services | where limit < 0;\n")]
    [InlineData("a rule name that is no identifier", "nodes ServiceNode on service with (rule = \"my rule\") = services;\n")]
    [InlineData("an edge key that is no identifier", Nodes + "edges DependsOn from service to service with (key = \"my key\") = services | project service;\n")]
    [InlineData("a quoted column name with a dot", "nodes ServiceNode on service = services | extend ['a.b'] = name | project service, name = ['a.b'], cluster, namespace;\n")]
    [InlineData("a quoted let name with a space", "let ['my let'] = services | where name == \"a\";\nnodes ServiceNode on service = ['my let'];\n")]
    [InlineData("arg_max over text (Kusto orders text)", "nodes ServiceNode on service = services | summarize arg_max(name, cluster, namespace) by service;\n")]
    [InlineData("strcat of text and a number (Kusto converts)", "nodes ServiceNode on service = services | project service, name = strcat(name, 5), cluster, namespace;\n")]
    [InlineData("a union of rows that hold elements", "nodes ServiceNode on service = union services, services;\n")]
    [InlineData("a node id that is a text column", Nodes + "nodes ServiceGroup on g = services | project g = name, cluster, namespace;\n")]
    public void What_the_front_end_accepts_the_catalog_loads(string what, string steps)
    {
        var (put, _, errors) = Loaded("acme.svc.serviceDeps.kql", Services + steps);
        if (!put.Ok) return;
        Assert.True(errors.Count == 0, $"{what}: the front end accepted the text, and the catalog refuses what it printed:\n" + string.Join("\n", errors.Take(2)));
    }

    static readonly Lazy<(int All, int WithExternalName)> EstateServices = new(() =>
    {
        // counted without any test on a missing value: coalesce gives the fallback for a missing text in Kusto and in the engine alike
        var all = Marked("true"); var with = Marked("coalesce(ext, \"<none>\") != \"<none>\"");
        Assert.True(with > 0 && with < all, $"the estate has services with and without an externalName ({with} of {all})");
        return (all, with);
    });

    /// <summary>The number of services of the estate that pass a predicate over `ext`, the optional text property externalName.</summary>
    static int Marked(string predicate)
    {
        var (put, cat, errors) = Loaded("acme.svc.serviceDeps.kql", "graph serviceDeps : acme.svc;\ninput master : acme.ops;\n"
            + "let services = graph(\"master\") | graph-match (s) where labels(s) has \"Service\" project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name, ext = s.externalName;\n"
            + "nodes ServiceNode on service = services | project service, name, cluster, namespace;\n"
            + $"extend nodes ServiceNode on service = services | where {predicate} | summarize pods = count() by service;\n");
        Assert.True(put.Ok && errors.Count == 0, string.Join("\n", put.Diagnostics.Concat(errors)));
        return Replayed(cat!, "serviceTree").Entities.Count(e => e.Props.Items.Any(kv => Ids.Name(kv.Key) == "pods"));
    }

    /// <summary>In Kusto a text is never null: a missing text is the empty text, so `isnull` of a text is false for every row, `p != "x"` keeps the rows without p, and `p == ""` finds them.
    /// The engine reads a missing property as absent, and the front end writes these three as tests on presence: each gives the opposite rows.</summary>
    [Theory]
    [InlineData("isnull(ext)", "none")]
    [InlineData("isnotnull(ext)", "all")]
    [InlineData("ext != \"x\"", "all")]
    [InlineData("ext == \"\"", "without")]
    public void A_test_on_a_missing_text_selects_the_rows_Kusto_selects(string predicate, string kusto)
    {
        var (all, with) = EstateServices.Value;
        var want = kusto switch { "none" => 0, "all" => all, "without" => all - with, _ => with };
        Assert.Equal(want, Marked(predicate));
    }
}
