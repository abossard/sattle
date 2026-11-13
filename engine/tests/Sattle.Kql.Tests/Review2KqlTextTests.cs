using Sattle.Core;
using Sattle.Language;

namespace Sattle.Kql.Tests;

/// <summary>Code review 2, verifier v-kql: text handling of the KQL front end. Each test fails on 4bbf5d4 for the reason its comment gives.</summary>
public class Review2KqlTextTests
{
    static readonly Lazy<Graph> Catalog = new(() =>
    {
        var cat = new Catalog();
        var res = cat.Load(Language.Catalog.ReadDirectory(Definitions).ToList(), updatePins: true);
        Assert.DoesNotContain(res.Diagnostics, x => x.IsError);
        return cat.Graph;
    });

    static readonly string Root = FindRoot();
    static string Definitions => Path.Combine(Root, "engine", "definitions");
    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "engine", "definitions"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    const string GraphLine = "graph serviceDeps : acme.svc;\n";
    const string Services = "let services = graph(\"master\") | graph-match (s) where labels(s) has \"Service\" project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name;\n";
    static KqlResult LowerSvc(string text) => KqlFrontEnd.Lower(text.Replace(GraphLine, GraphLine + "input master : acme.ops;\n", StringComparison.Ordinal), Catalog.Value, "acme.svc");

    /// <summary>HEAD: a first token `.` is a Kusto command, the root is no query block (InvalidCastException, Lowerer.Run); an empty datatable divides by zero.</summary>
    [Theory]
    [InlineData(".show tables\ngraph serviceDeps : acme.svc;\n")]
    [InlineData("graph serviceDeps : acme.svc;\nnodes ServiceNode on service = datatable()[];\n")]
    public void Text_the_front_end_cannot_translate_gives_diagnostics_not_an_exception(string text)
    {
        var thrown = Record.Exception(() => LowerSvc(text));
        Assert.True(thrown is null, $"{thrown?.GetType().Name}: {thrown?.Message}");
    }

    /// <summary>HEAD: the Refused of WriteRules leaves Lower (after the only catch).</summary>
    [Fact]
    public void A_value_an_onDelete_row_cannot_hold_is_a_diagnostic()
    {
        KqlResult? low = null;
        var thrown = Record.Exception(() => low = LowerSvc(GraphLine + Services
            + "let retired = () { print name = datetime(2026-01-01) };\n"
            + "nodes ServiceNode on service with (rule = \"a\", accept = \"delete\", onDelete = \"retired\") = services;\n"));
        Assert.True(thrown is null, $"{thrown?.GetType().Name}: {thrown?.Message}");
        Assert.Contains(low!.Diagnostics, d => d.IsError);
    }

    static string[] Accept(string with)
    {
        var path = Path.Combine(Definitions, "acme.biz.capabilityView.kql");
        var text = File.ReadAllText(path).Replace("with (rule = \"tree\", accept = \"create, delete\")", with, StringComparison.Ordinal);
        var low = KqlFrontEnd.Lower(text, Catalog.Value, "acme.biz", path);
        Assert.True(low.Diagnostics.Count == 0 || low.Diagnostics.Any(d => d.IsError), "unreachable");
        if (!low.Ok) return ["refused: " + string.Join(" | ", low.Diagnostics.Select(d => d.Message))];
        var rule = low.Facts.Elements().OfType<Entity>().First(e => e.Id.EndsWith("/rule:tree", StringComparison.Ordinal));
        var list = (ListValue)rule.Props.Get(Ids.KProp("accept"))!;
        return [.. list.Items.OfType<TextValue>().Select(t => t.S)];
    }

    /// <summary>HEAD: `accept = create, delete` unquoted gives accept = [create]; `delete` is dropped without a message.</summary>
    [Fact]
    public void An_unquoted_accept_list_keeps_both_words_or_is_refused()
    {
        var accept = Accept("with (rule = \"tree\", accept = create, delete)");
        Assert.True(accept[0].StartsWith("refused", StringComparison.Ordinal) || accept.Contains("delete"), "accept is " + string.Join(",", accept));
    }

    /// <summary>HEAD: `rule: "tree"` is no pair the pattern reads, so the step is in no rule, nothing is said, and the step has no rule fact.</summary>
    [Fact]
    public void A_setting_written_with_a_colon_is_refused_or_read()
    {
        var path = Path.Combine(Definitions, "acme.biz.capabilityView.kql");
        var text = File.ReadAllText(path).Replace("with (rule = \"tree\", accept = \"create, delete\")", "with (rule: \"tree\")", StringComparison.Ordinal);
        var low = KqlFrontEnd.Lower(text, Catalog.Value, "acme.biz", path);
        Assert.True(!low.Ok || low.Facts.Elements().OfType<Entity>().Any(e => e.Id.EndsWith("/rule:tree", StringComparison.Ordinal)),
            "accepted without a message, and no rule tree: " + string.Join(" | ", low.Diagnostics.Select(d => d.Message)));
    }

    /// <summary>HEAD: a file named after a module of one part reads name[(module.Length + 1)..] of a name that is only the module.</summary>
    [Fact]
    public void A_KQL_file_of_a_module_named_in_one_part_gets_a_diagnostic_not_an_exception()
    {
        var files = Language.Catalog.ReadDirectory(Definitions).ToList();
        files.Add(new SourceFile("w.sattle", "module w @ 1.0.0;\n"));
        KqlFiles? put = null;
        var thrown = Record.Exception(() => put = KqlFiles.Of(files, [new SourceFile("w.kql", "graph p : w;\n")]));
        Assert.True(thrown is null, $"{thrown?.GetType().Name}: {thrown?.Message}");
        Assert.False(put!.Ok);
    }

    /// <summary>HEAD: `module w;` (the parser reads a module without a version) is not found by the header pattern: "there is no module w".</summary>
    [Fact]
    public void A_module_declared_without_a_version_is_found_by_its_KQL_file()
    {
        var files = Language.Catalog.ReadDirectory(Definitions).ToList();
        files.Add(new SourceFile("w.sattle", "module w;\n"));
        var put = KqlFiles.Of(files, [new SourceFile("w.p.kql", "graph p : w;\n")]);
        Assert.DoesNotContain(put.Diagnostics, d => d.Message.Contains("there is no module w", StringComparison.Ordinal));
    }

    /// <summary>HEAD: a rule name with `#` makes the printer throw (Suffix parses what follows `#`).</summary>
    [Fact]
    public void A_rule_name_with_a_hash_is_a_diagnostic_not_an_exception()
    {
        var path = Path.Combine(Definitions, "acme.biz.capabilityView.kql");
        var text = File.ReadAllText(path).Replace("with (rule = \"tree\", accept", "with (rule = \"a#b\", accept", StringComparison.Ordinal);
        var thrown = Record.Exception(() => KqlFiles.Of(Language.Catalog.ReadDirectory(Definitions).ToList(), [new SourceFile(path, text)]));
        Assert.True(thrown is null, $"{thrown?.GetType().Name}: {thrown?.Message}");
    }

    /// <summary>HEAD: a let named like the rows table it reads recurses without end (Tab, Let, Pipe, Tab). Kills the test host; run alone.</summary>
    [Fact]
    public void A_let_may_shadow_the_table_it_reads()
    {
        var text = "graph ingest : acme.ops;\ninput pods : k8s.PodRow;\n"
            + "let pods = pods | where isnotnull(phase);\n"
            + "nodes Pod on pod with (rule = \"observedPods\", description = \"observed by the Kubernetes adapter\") = pods | project pod = identity(\"podUid\", cluster, uid), cluster, namespace, name, phase, team = teamLabel, ready, serviceAccount;\n";
        var low = KqlFrontEnd.Lower(text, Catalog.Value, "acme.ops");
        Assert.True(low.Ok, string.Join("\n", low.Diagnostics));
    }

    /// <summary>HEAD: both texts lower without a message (the loop pattern to a Match whose edge has one node at both ends, `cycles=none` and a path of exactly two edges to a Traverse of depth 2 to 2 that is tested against the shortest distance).</summary>
    [Theory]
    [InlineData("graph(\"master\") | graph-match (p)-[c]->(p) where labels(c) has \"Calls\" project service = p.id, cluster = p.cluster, namespace = p.namespace, name = p.name")]
    [InlineData("graph(\"master\") | graph-match cycles=none (a)-[p*2..2]->(b) where labels(a) has \"Service\" and all(p, labels(p) has \"Calls\") project service = b.id, cluster = b.cluster, namespace = b.namespace, name = b.name")]
    public void A_graph_match_whose_meaning_the_engine_does_not_have_is_refused(string query)
    {
        var low = LowerSvc(GraphLine + "nodes ServiceNode on service = " + query + ";\n");
        Assert.False(low.Ok, "accepted without a message");
    }
}
