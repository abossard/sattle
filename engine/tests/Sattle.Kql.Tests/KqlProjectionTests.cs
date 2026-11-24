using Sattle.Core;
using Sattle.Language;

namespace Sattle.Kql.Tests;

/// <summary>A projection written in KQL (research 10, stage 1): what the profile refuses and what it says to write, how a file is named and found, and where the operators and the errors of a text are placed. The state the shipped projections give on the estate is pinned once, for every language, in the scenario pin file of `Sattle.Tests`.</summary>
public class KqlProjectionTests
{
    static readonly string Root = FindRoot();
    static string Definitions => Path.Combine(Root, "engine", "definitions");

    /// <summary>Every operator the front end writes for a projection is placed in the `.kql` file of that projection.</summary>
    [Theory]
    [InlineData("acme.svc.serviceDeps.kql")] [InlineData("acme.deployhealth.deploymentHealth.kql")] [InlineData("acme.report.deploymentReport.kql")] [InlineData("acme.biz.capabilityView.kql")]
    [InlineData("acme.workload.workloadStructure.kql")] [InlineData("acme.workload.workloadHealth.kql")] [InlineData("acme.treereport.healthReport.kql")] [InlineData("acme.tree.treeStructure.kql")]
    [InlineData("acme.tree.treeSignals.kql")] [InlineData("acme.treehealth.treeRollup.kql")] [InlineData("acme.treehealth.healthTree.kql")] [InlineData("acme.ops.ingest.kql")]
    public void Every_operator_of_a_projection_in_KQL_is_placed_in_its_file(string kql)
    {
        var path = Path.Combine(Definitions, kql);
        var pid = Ids.Child(Ids.Module(kql[..kql[..^4].LastIndexOf('.')]), "projection", kql[..^4][(kql[..^4].LastIndexOf('.') + 1)..]);
        var ops = new DefView(Defs.Value.Catalog.Graph).Children(pid, "op").ToList();
        Assert.NotEmpty(ops);
        Assert.All(ops, op => Assert.Equal(path, Defs.Value.Catalog.FileOf.GetValueOrDefault(op)));
    }

    /// <summary>What the profile refuses, and that the message says what to write instead.</summary>
    [Theory]
    [InlineData("nodes ServiceNode on service = services | take 10", "filter or summarize instead")]
    [InlineData("nodes ServiceNode on service = services | sample 10", "filter or summarize instead")]
    [InlineData("nodes ServiceNode on service = services | sort by name asc", "no order")]
    [InlineData("nodes ServiceNode on service = services | top 3 by name", "no order")]
    [InlineData("nodes ServiceNode on service = services | summarize n = dcount(name) by service", "distinct ... | summarize n = count()")]
    [InlineData("nodes ServiceNode on service = services | summarize n = dcountif(name, cluster == \"a\") by service", "distinct ... | summarize n = count()")]
    [InlineData("nodes ServiceNode on service = services | summarize n = count_distinctif(name, cluster == \"a\") by service", "where ... | summarize n = count_distinct(x)")]
    [InlineData("nodes ServiceNode on service = services | summarize n = take_any(name) by service", "min, max or arg_max")]
    [InlineData("nodes ServiceNode on service = services | summarize s = make_set(name) by service", "a row of a projection holds no array")]
    [InlineData("nodes ServiceNode on service = services | summarize arg_min(name, cluster) by service", "arg_min orders by a number")]
    [InlineData("nodes ServiceNode on service = services | extend r = 1 | summarize arg_max(r, service) by cluster", "'service' is an element: project its properties or its id first")]
    [InlineData("nodes ServiceNode on service = services | join (services) on service", "join kind=inner")]
    [InlineData("nodes ServiceNode on service = services | join kind=leftouter (services) on service", "write union and summarize")]
    [InlineData("nodes ServiceNode on service = services | extend t = now()", "never the wall clock")]
    [InlineData("nodes ServiceNode on service = services | extend t = ago(1h)", "never the wall clock")]
    [InlineData("nodes ServiceNode on service = services | mv-expand x = pack_array(name, cluster)", "a row of a projection holds no array")]
    [InlineData("nodes ServiceNode on service = services | mv-apply x = pack_array(name, cluster) to typeof(string) on (project x)", "a row of a projection holds no array")]
    [InlineData("nodes ServiceNode on service = services | scan with (step s: name == \"a\";)", "write a threshold with invoke hysteresis")]
    [InlineData("nodes ServiceNode on service = union services, (services | project service)", "project each to")]
    [InlineData("nodes ServiceNode on service = graph(\"master\") | graph-match (a)-[p*1..3]->(b) where labels(a) has \"Pod\" and all(p, labels(p) has \"Calls\") project service = a.id, hops = p", "project the two ends")]
    [InlineData("nodes ServiceNode on service with (edit = \"create, delete\") = services", "say what may be edited with accept")]
    [InlineData("nodes ServiceNode on service with (description = \"the services\") = services", "name it with rule")]
    [InlineData("nodes ServiceNode on service with (rule = \"a\", accept = \"create\") = services;\nextend nodes ServiceNode on service with (rule = \"a\", accept = \"delete\") = services | project service, name", "give accept different values")]
    [InlineData("nodes ServiceNode on service with (rule = \"a\", accept = \"create, rename\") = services", "'rename' is neither")]
    [InlineData("nodes ServiceNode on service with (rule = \"a\", accept = \"create\") = services", "this step accepts create but says nothing about the new element's key")]
    [InlineData("nodes ServiceNode on service with (rule = \"a\", update = \"owner\") = services", "update names owner, which this step does not set")]
    [InlineData("nodes ServiceNode on service = services | project service, name", "sets every required property")]
    [InlineData("extend nodes ServiceGroup on service = services | project service", "no step makes ServiceGroup")]
    [InlineData("edges DependsOn from service to service = services | project service;\nedges DependsOn from service to service = services | project service", "two steps make DependsOn edges")]
    [InlineData("nodes on service labels name = services", "one step per type")]
    [InlineData("nodes ServiceNode service = services", "starts like a statement of a projection and is none")]
    [InlineData("nodes ServiceNode on service = graph(\"master\") | graph-match (a)-[p*2..3]->(b) where labels(a) has \"Service\" and all(p, labels(p) has \"Calls\") project service = a.id, cluster = a.cluster, namespace = a.namespace, name = a.name", "a path starts at 0 or 1 edges")]
    public void The_profile_refuses_what_the_engine_cannot_keep_up_to_date_and_says_what_to_write(string step, string says)
    {
        var text = "graph serviceDeps : acme.svc;\ninput master : acme.ops;\n"
            + "let services = graph(\"master\") | graph-match (s) where labels(s) has \"Service\" project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name;\n"
            + step + ";\n";
        var low = KqlFrontEnd.Lower(text, Defs.Value.Catalog.Graph, "acme.svc");
        Assert.False(low.Ok);
        Assert.Contains(low.Diagnostics, d => d.Code == "Profile" && d.Message.Contains(says, StringComparison.Ordinal));
    }

    /// <summary>A join compares a column of each side for equality. Kusto refuses any other condition before the engine sees the text, so the engine joins on equal keys only.</summary>
    [Fact]
    public void A_join_on_anything_but_an_equality_is_refused()
    {
        var text = "graph serviceDeps : acme.svc;\ninput master : acme.ops;\n"
            + "let services = graph(\"master\") | graph-match (s) where labels(s) has \"Service\" project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name;\n"
            + "nodes ServiceNode on service = services | join kind=inner (services | project other = service) on $left.service != $right.other | project service, name, cluster, namespace;\n";
        var low = KqlFrontEnd.Lower(text, Defs.Value.Catalog.Graph, "acme.svc");
        Assert.False(low.Ok);
        Assert.Contains(low.Diagnostics, d => d.Message.Contains("$left.<column> == $right.<column>", StringComparison.Ordinal));
    }

    /// <summary>Kusto refuses a second column of a name, in a `project` and between a key and an aggregate of a `summarize`, before the engine sees the text.</summary>
    [Theory]
    [InlineData("two columns of one name in a project", "nodes ServiceNode on service = services | project service, name, name = cluster, namespace")]
    [InlineData("a key and an aggregate of one name", "nodes ServiceNode on service = services | summarize name = count() by name | project service = identity(\"service\", name), name")]
    public void Two_columns_of_one_name_are_refused(string what, string step)
    {
        var text = "graph serviceDeps : acme.svc;\ninput master : acme.ops;\n"
            + "let services = graph(\"master\") | graph-match (s) where labels(s) has \"Service\" project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name;\n"
            + step + ";\n";
        var low = KqlFrontEnd.Lower(text, Defs.Value.Catalog.Graph, "acme.svc");
        Assert.False(low.Ok, what);
        Assert.Contains(low.Diagnostics, d => d.Code == "Kusto" && d.Message.Contains("A column with the name 'name' is already declared", StringComparison.Ordinal));
    }

    /// <summary>Kusto orders no texts: a less-than between two texts is refused, so the engine compares texts only for equality.</summary>
    [Fact]
    public void A_text_ordered_with_less_than_is_refused()
    {
        var text = "graph serviceDeps : acme.svc;\ninput master : acme.ops;\n"
            + "let services = graph(\"master\") | graph-match (s) where labels(s) has \"Service\" project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name;\n"
            + "nodes ServiceNode on service = services | where name < \"m\";\n";
        var low = KqlFrontEnd.Lower(text, Defs.Value.Catalog.Graph, "acme.svc");
        Assert.False(low.Ok);
        Assert.Contains(low.Diagnostics, d => d.Code == "Kusto" && d.Message.Contains("not defined for the operand types string and string", StringComparison.Ordinal));
    }

    /// <summary>What the graph line and the input lines refuse, and that the message says what to write instead.</summary>
    [Theory]
    [InlineData("graph serviceDeps : acme.svc;\n", "say which ontology the graph master has: input master : <ontology>;")]
    [InlineData("graph serviceDeps : acme.svc;\ninput master : acme.nowhere;\n", "acme.nowhere is neither an ontology nor a row type of this module or its imports")]
    [InlineData("graph serviceDeps : acme.svc;\ninput master : acme.ops;\ninput master : k8s.PodRow;\n", "master is an input already")]
    [InlineData("graph serviceDeps : acme.svc with (owner = \"me\");\ninput master : acme.ops;\n", "'owner' is not a setting of the graph line")]
    [InlineData("graph serviceDeps : acme.svc with (history = recent);\ninput master : acme.ops;\n", "history is a whole number")]
    public void The_graph_line_and_the_inputs_say_what_to_write(string head, string says)
    {
        var text = head + "nodes ServiceNode on service = graph(\"master\") | graph-match (s) where labels(s) has \"Service\" project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name;\n";
        var low = KqlFrontEnd.Lower(text, Defs.Value.Catalog.Graph, "acme.svc");
        Assert.False(low.Ok);
        Assert.Contains(low.Diagnostics, d => d.Message.Contains(says, StringComparison.Ordinal));
    }

    /// <summary>A table function is written out once per distinct call: two equal calls share its operators.</summary>
    [Theory]
    [InlineData("services | invoke busy()", 1)]
    [InlineData("union (services | invoke busy()), (services | invoke busy())", 1)]
    [InlineData("union (services | invoke busy()), (services | where cluster != \"x\" | invoke busy())", 3)]
    public void Equal_calls_of_a_table_function_share_its_operators(string query, int filters)
    {
        var text = "graph serviceDeps : acme.svc;\ninput master : acme.ops;\n"
            + "let services = graph(\"master\") | graph-match (s) where labels(s) has \"Service\" project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name;\n"
            + "let busy = (T:(service:string, cluster:string, namespace:string, name:string)) { T | where name != \"idle\" };\n"
            + $"nodes ServiceNode on service = {query};\n";
        var low = KqlFrontEnd.Lower(text, Defs.Value.Catalog.Graph, "acme.svc");
        Assert.True(low.Ok, string.Join("\n", low.Diagnostics));
        Assert.Equal(filters, low.Facts.Elements().OfType<Entity>().Count(e => Ids.Name(e.Type) == "Filter"));
    }

    /// <summary>A let may take the name of the table it reads (Kusto binds the name inside to the table), and is never read into itself.</summary>
    [Fact]
    public void A_let_may_take_the_name_of_the_table_it_reads()
    {
        var text = "graph ingest : acme.ops;\ninput pods : k8s.PodRow;\n"
            + "let pods = pods | where isnotnull(phase);\n"
            + "nodes Pod on pod with (rule = \"observedPods\", description = \"observed by the Kubernetes adapter\") = pods | project pod = identity(\"podUid\", cluster, uid), cluster, namespace, name, phase, team = teamLabel, ready, serviceAccount;\n";
        var low = KqlFrontEnd.Lower(text, Defs.Value.Catalog.Graph, "acme.ops");
        Assert.True(low.Ok, string.Join("\n", low.Diagnostics));
    }

    /// <summary>A KQL file is named after its module and projection, `module.projection.kql`, and its graph line names the same projection.</summary>
    [Fact]
    public void A_KQL_file_is_named_after_its_module_and_projection()
    {
        var path = Path.Combine(Definitions, "acme.svc.noSuchProjection.kql");
        var res = new Catalog(new KqlSource()).Load(With(path, "graph serviceDeps : acme.svc;\n"), updatePins: true);
        Assert.Contains(res.Diagnostics, d => d.IsError && d.File == path && d.Message.Contains("the graph line names serviceDeps, the file name noSuchProjection", StringComparison.Ordinal));
    }

    /// <summary>A KQL file that no module reads is an error with its name: its module is missing, or no source reads KQL.</summary>
    [Theory]
    [InlineData("acme.nowhere.serviceDeps.kql", true)]
    [InlineData("acme.svc.serviceDeps.kql", false)]
    public void A_KQL_file_no_module_reads_is_an_error(string file, bool withSource)
    {
        var path = Path.Combine(Definitions, file);
        var res = new Catalog(withSource ? new KqlSource() : null).Load(With(path, File.ReadAllText(Path.Combine(Definitions, "acme.svc.serviceDeps.kql"))), updatePins: true);
        Assert.Contains(res.Diagnostics, d => d.IsError && d.Code == "UnreadFile" && d.File == path);
    }

    /// <summary>A projection the module's text does not declare is made from its KQL text alone; a graph named after an ontology the module imports
    /// (acme.svc imports acme.ops as ops) needs no input line.</summary>
    [Fact]
    public void A_new_projection_is_made_from_its_KQL_text_alone()
    {
        var text = "graph serviceNames : acme.svc with (description = \"The services of each namespace, by name.\", history = 2);\n"
            + "nodes ServiceNode on service = graph(\"ops\") | graph-match (s) where labels(s) has \"Service\" project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name;\n";
        var g = Load(With(Path.Combine(Definitions, "acme.svc.serviceNames.kql"), text), new KqlSource()).Graph;
        var d = new DefView(g); var pid = Ids.Child(Ids.Module("acme.svc"), "projection", "serviceNames");
        Assert.Equal(("view", 2L, "The services of each namespace, by name."), (d.Text(pid, "purpose"), d.Int(pid, "retain"), d.Text(pid, "description")));
        Assert.Equal("acme.ops", Ids.ModuleOf(d.Target(Ids.Child(pid, "port", "ops"), "expects")!));
    }

    const string RowsModule = """
        module t.rows @ 1.0.0;
        entity Stat { name: Text; n: Integer; }
        identity stat for Stat version "1" { name: Text by exact; }
        rows ItemRow { key: Text; group: Text; score: Integer; }
        space s {
          source a adapter "record" rows ItemRow;
          master graph master = ingest(a: a);
        }
        """;

    /// <summary>A space binds a projection in KQL of its own module: the projections are read before the module's spaces.</summary>
    [Fact]
    public void A_space_binds_a_projection_in_KQL_of_its_own_module()
    {
        var kql = "graph ingest : t.rows;\ninput a : ItemRow;\n"
            + "nodes Stat on stat with (rule = \"observed\", description = \"test\") = a | summarize n = count() by name = group | project stat = identity(\"stat\", name), name, n;\n";
        var cat = Load([new SourceFile("t.rows.sattle", RowsModule), new SourceFile("t.rows.ingest.kql", kql)], new KqlSource());
        var pid = Ids.Child(Ids.Module("t.rows"), "projection", "ingest");
        Assert.Single(cat.Spaces);
        Assert.All(new DefView(cat.Graph).Children(pid, "op"), op => Assert.Equal("t.rows.ingest.kql", cat.FileOf[op]));
    }

    /// <summary>What the engine's checker refuses in a projection written in KQL is reported in the `.kql` file, at the step it is about.</summary>
    [Fact]
    public void A_checker_error_in_a_KQL_projection_is_placed_in_the_KQL_file()
    {
        var path = Path.Combine(Definitions, "acme.svc.serviceDeps.kql");
        var text = "graph serviceDeps : acme.svc;\ninput master : acme.ops;\n"
            + "let services = graph(\"master\") | graph-match (s) where labels(s) has \"Service\" project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name;\n"
            + "nodes ServiceNode on service = services | project service, name = strcat(name, 5), cluster, namespace;\n";
        var res = new Catalog(new KqlSource()).Load(With(path, text), updatePins: true);
        var mine = res.Diagnostics.Where(d => d.IsError && Ids.ModuleOf(d.Focus) == "acme.svc").ToList();
        Assert.NotEmpty(mine);
        Assert.All(mine, d => Assert.Equal((path, 4), (d.File, d.Line)));
    }

    /// <summary>The definitions and their catalog, checked once.</summary>
    static readonly Lazy<(List<SourceFile> Files, Catalog Catalog)> Defs = new(() => { var files = Catalog.ReadDirectory(Definitions); return (files, Load(files, new KqlSource())); });

    /// <summary>The definitions with `text` as the file at `path`, in place of the file there or added.</summary>
    static List<SourceFile> With(string path, string text) => [.. Defs.Value.Files.Where(f => f.Path != path), new SourceFile(path, text)];

    static Catalog Load(List<SourceFile> files, IProjectionSource? projections = null)
    {
        var cat = new Catalog(projections);
        var res = cat.Load(files, updatePins: true);
        Assert.True(!res.Diagnostics.Any(x => x.IsError), string.Join("\n", res.Diagnostics.Where(x => x.IsError)));
        return cat;
    }

    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "engine", "definitions"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
