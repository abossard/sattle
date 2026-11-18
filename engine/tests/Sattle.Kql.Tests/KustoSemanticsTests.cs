using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Kql.Tests;

/// <summary>What the front end accepts, the engine computes as Kusto defines it: a text the front end lowers loads in the catalog, and its result on given rows is the result Kusto gives for the text. The cases come from code review 2 (research 12) and its verifier. The semantics of the operators themselves, on generated logs, are the worlds of the Differential.</summary>
public class KustoSemanticsTests
{
    static readonly string Root = FindRoot();
    static string Definitions => Path.Combine(Root, "engine", "definitions");

    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "engine", "definitions"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    /// <summary>The definitions, checked once.</summary>
    static readonly Lazy<Catalog> Examples = new(() => { var cat = new Catalog(new KqlSource()); cat.Load(Catalog.ReadDirectory(Definitions), updatePins: true); return cat; });

    /// <summary>The definitions with one projection in KQL given as text in place of its file: what the front end says, and what a catalog with the KQL source says of the text (null when the front end refused).</summary>
    static (KqlFiles Put, Catalog? Catalog, List<Diagnostic> LoadErrors) Loaded(string file, string text)
    {
        var definitions = Catalog.ReadDirectory(Definitions); var path = Path.Combine(Definitions, file);
        var put = KqlFiles.Of([.. definitions.Where(f => f.Path.EndsWith(".sattle", StringComparison.Ordinal))], [new SourceFile(path, text)], Examples.Value);
        if (!put.Ok) return (put, null, []);
        var cat = new Catalog(new KqlSource());
        var res = cat.Load([.. put.Files, .. definitions.Where(f => f.Path.EndsWith(".kql", StringComparison.Ordinal) && f.Path != path)], updatePins: true);
        return (put, cat, [.. res.Diagnostics.Where(x => x.IsError)]);
    }

    const string Services = "graph serviceDeps : acme.svc;\n"
        + "let services = graph(\"master\") | graph-match (s) where labels(s) has \"Service\" project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name;\n";
    const string Nodes = "nodes ServiceNode on service = services | project service, name, cluster, namespace;\n";

    /// <summary>What the front end accepts, the catalog loads: a text is refused by the front end in the author's terms, or its operators check.
    /// Each of these texts is valid KQL that the front end accepts; the catalog must not then refuse it.</summary>
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
        Assert.True(errors.Count == 0, $"{what}: the front end accepted the text, and the catalog refuses it:\n" + string.Join("\n", errors.Take(2)));
    }

    const string Base = """
        module t.base @ 1.0.0;
        entity Summary { key: Text; bucket: Text; best: Integer; other?: Integer; note?: Text; }
        entity KA { key: Text; bucket: Text; }
        entity KB { key: Text; bucket: Text; }
        identity summary for Summary version "1" { key: Text by exact; }
        identity domA for KA version "1" { key: Text by exact; bucket: Text by exact; }
        identity domB for KB version "1" { key: Text by exact; bucket: Text by exact; }
        rows ItemRow { key: Text; bucket: Text; rank: Integer; x: Integer; y: Integer; note?: Text; }
        """;

    const string Space = """
        module t.space @ 1.0.0;
        import t.base @ 1.0.0 sha256:… as b;
        space s {
          source items adapter "sattle.authoring" rows b.ItemRow;
          master graph master = b.ingest(items: items);
        }
        """;

    /// <summary>The mini world with its ingestion projection given in KQL: the front end's result, and the engine after the rows are ingested (null when the front end refused).</summary>
    static (KqlFiles Put, Engine? Engine) Run(string kql, params (string Key, string Bucket, long Rank, long X, long Y, string? Note)[] rows) => Run(kql, null, rows);

    /// <summary>A view module with one type, for a projection `kept` that reads the master: given as KQL text by a test.</summary>
    const string View = """
        module t.view @ 1.0.0;
        import t.base @ 1.0.0 sha256:… as b;
        entity Kept { key: Text; }
        identity kept for Kept version "1" { key: Text by exact; }
        """;

    /// <summary>The mini world, and with `view` the graph `kept` that the projection of that text derives from the master.</summary>
    static (KqlFiles Put, Engine? Engine) Run(string kql, string? view, (string Key, string Bucket, long Rank, long X, long Y, string? Note)[] rows)
    {
        var put = view is null ? KqlFiles.Of([new SourceFile("t.base.sattle", Base)], [new SourceFile("t.base.ingest.kql", kql)])
            : KqlFiles.Of([new SourceFile("t.base.sattle", Base), new SourceFile("t.view.sattle", View)], [new SourceFile("t.base.ingest.kql", kql), new SourceFile("t.view.kept.kql", view)]);
        if (!put.Ok) return (put, null);
        var cat = new Catalog(new KqlSource());
        var space = view is null ? Space : Space.Replace("import t.base @ 1.0.0 sha256:… as b;", "import t.base @ 1.0.0 sha256:… as b;\nimport t.view @ 1.0.0 sha256:… as v;").Replace("master graph master = b.ingest(items: items);", "master graph master = b.ingest(items: items);\n  graph kept = v.kept(master: master);");
        var res = cat.Load([.. put.Files, new SourceFile("t.space.sattle", space)], updatePins: true);
        Assert.Empty(res.Diagnostics.Where(d => d.IsError).Select(d => d.ToString()));
        var e = new Engine(cat, cat.Spaces.Keys.Single());
        var rw = new RowWriter(e.RowLayout("items"), e.Interner);
        foreach (var (key, bucket, rank, x, y, note) in rows)
        {
            rw.Begin(1); rw.Text("key", key); rw.Text("bucket", bucket);
            rw.Int(rw.Col("rank"), rank); rw.Int(rw.Col("x"), x); rw.Int(rw.Col("y"), y);
            if (note is null) rw.Absent(rw.Col("note")); else rw.Text("note", note);
        }
        e.Ingest("items", rw.Build()!, 1);
        return (put, e);
    }

    const string Ingest = "graph ingest : t.base;\ninput items : ItemRow;\n";

    /// <summary>The number of elements of the master that pass a predicate over `note`, their optional text property, read through a pattern: three elements, two of them without the property.</summary>
    static int Kept(string predicate)
    {
        var (put, e) = Run(Ingest + "nodes Summary on s with (rule = \"observed\") = items | project s = identity(\"summary\", key), key, bucket, best = x, note;\n",
            "graph kept : t.view;\ninput master : t.base;\n"
            + $"nodes Kept on k = graph(\"master\") | graph-match (x) where labels(x) has \"Summary\" project key = x.key, note = x.note | where {predicate} | project k = identity(\"kept\", key), key;\n",
            [("k1", "a", 0, 1, 0, null), ("k2", "a", 0, 2, 0, null), ("k3", "a", 0, 3, 0, "n")]);
        Assert.True(put.Ok, string.Join("\n", put.Diagnostics));
        using var _ = e;
        Assert.Equal(3, e!.Master.State.EntityCount);
        return e.Graph("kept").State.EntityCount;
    }

    /// <summary>A property an element does not have reads as absent through a pattern, and a test on it selects the rows Kusto selects. In Kusto a text is never null: a missing text is the empty text, so `isnull` of a text is false for every row, `p != "x"` keeps the rows without p, and `p == ""` finds them.
    /// The engine reads the missing property as absent, and the front end wrote these as tests on presence: each gave the opposite rows. On three elements of a small master, two of them without the property.</summary>
    [Theory]
    [InlineData("true", 3)]
    [InlineData("coalesce(note, \"<none>\") != \"<none>\"", 1)]      // counted without any test on a missing value
    [InlineData("isnull(note)", 0)]
    [InlineData("isnotnull(note)", 3)]
    [InlineData("note != \"x\"", 3)]
    [InlineData("note == \"\"", 2)]
    public void A_test_on_a_missing_text_selects_the_rows_Kusto_selects(string predicate, int kusto) => Assert.Equal(kusto, Kept(predicate));

    /// <summary>Two identity domains in one union column: refused, or the ids stay apart (Kusto's identity() is a different string per domain). HEAD: both branches are minted in domain A, 4 rows make 2 elements.</summary>
    [Fact]
    public void A_union_of_two_identity_domains_is_refused_or_keeps_the_ids_apart()
    {
        var (put, e) = Run(Ingest
            + "nodes KA on g with (rule = \"observed\") = union (items | project g = identity(\"domA\", key, bucket), key, bucket), (items | project g = identity(\"domB\", key, bucket), key, bucket);\n",
            ("k1", "a", 0, 0, 0, null), ("k2", "b", 0, 0, 0, null));
        if (!put.Ok) return;
        using var _ = e;
        var ids = e!.Master.State.Entities.Select(x => x.Id).ToList();
        Assert.True(ids.Count == 4, $"{ids.Count} elements from 2 rows in 2 domains: " + string.Join(", ", ids));
    }
}
