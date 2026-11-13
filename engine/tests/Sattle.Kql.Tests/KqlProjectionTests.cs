using Sattle.Cli;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Kql.Tests;

/// <summary>Research 10, stage 1: a projection written in KQL gives the state of today's text on the synthetic estate and the recorded captures, and the engine runs the operators the front end wrote.</summary>
public class KqlProjectionTests
{
    static readonly string Root = FindRoot();
    static string Definitions => Path.Combine(Root, "engine", "definitions");
    static string Estate => Path.Combine(Root, "engine", "tests", "Sattle.Tests", "fixtures", "estate");

    [Fact]
    public void The_kernel_is_the_one_the_contract_was_agreed_on() =>
        Assert.Equal("sha256:df107fbdf074b95e5bfca837afbc1bf3948b05ebf45f9288c40c3940abe920a1", Kernel.ComputeDigest());

    [Theory]
    [InlineData("acme.svc.serviceDeps.kql", "serviceTree", "the whole projection")]
    [InlineData("acme.deployhealth.deploymentHealth.kql", "deploymentHealth", "the whole projection")]
    [InlineData("acme.report.deploymentReport.kql", "deploymentReport", "the whole projection")]
    [InlineData("acme.biz.capabilityView.kql", "capabilities", "without its editable views, which come in the second part of the edit stage")]
    [InlineData("acme.workload.workloadStructure.kql", "workloadStructure", "the whole projection")]
    [InlineData("acme.workload.workloadHealth.kql", "workload", "the whole projection")]
    [InlineData("acme.treereport.healthReport.kql", "healthReport", "the whole projection")]
    [InlineData("acme.tree.treeStructure.kql", "treeStructure", "the whole projection")]
    [InlineData("acme.tree.treeSignals.kql", "treeSignals", "the whole projection")]
    [InlineData("acme.treehealth.treeRollup.kql", "treeRollup", "the whole projection")]
    [InlineData("acme.treehealth.healthTree.kql", "healthTree", "the whole projection")]
    [InlineData("acme.ops.ingest.kql", "master", "the whole projection")]
    public void A_projection_in_KQL_gives_the_state_of_its_text_on_the_estate(string kql, string graph, string compared) => SameState(Data(Estate), kql, graph, compared);

    [Trait("Category", "Capture")]
    [CaptureTheory]
    [InlineData("acme.svc.serviceDeps.kql", "serviceTree", "the whole projection")]
    [InlineData("acme.deployhealth.deploymentHealth.kql", "deploymentHealth", "the whole projection")]
    [InlineData("acme.report.deploymentReport.kql", "deploymentReport", "the whole projection")]
    [InlineData("acme.biz.capabilityView.kql", "capabilities", "without its editable views, which come in the second part of the edit stage")]
    [InlineData("acme.workload.workloadStructure.kql", "workloadStructure", "the whole projection")]
    [InlineData("acme.workload.workloadHealth.kql", "workload", "the whole projection")]
    [InlineData("acme.treereport.healthReport.kql", "healthReport", "the whole projection")]
    [InlineData("acme.tree.treeStructure.kql", "treeStructure", "the whole projection")]
    [InlineData("acme.tree.treeSignals.kql", "treeSignals", "the whole projection")]
    [InlineData("acme.treehealth.treeRollup.kql", "treeRollup", "the whole projection")]
    [InlineData("acme.treehealth.healthTree.kql", "healthTree", "the whole projection")]
    [InlineData("acme.ops.ingest.kql", "master", "the whole projection")]
    public void A_projection_in_KQL_gives_the_state_of_its_text_on_every_recorded_capture(string kql, string graph, string compared)
    {
        foreach (var dir in CaptureDirs) SameState(Data(dir), kql, graph, compared);
    }

    /// <summary>The graph a projection writes, element by element and window by window, from today's text and from the KQL text. `compared` names what of the projection the KQL text holds.</summary>
    static void SameState(Recorded data, string kql, string graph, string compared)
    {
        var withKql = WithKql(data, kql);
        var (want, wantGraph) = Observe(data.Today.Value, graph);
        var (got, gotGraph) = Observe(Run(data, withKql, [graph]), graph);
        if (data.Dir == Estate) Assert.NotEqual(0, wantGraph.Count);     // on the estate every graph has content; a real capture may hold none for a graph (no business capabilities)
        var diff = Graph.Diff(wantGraph, gotGraph);
        var tele = want.Windows.Except(got.Windows).Select(w => "  - " + w).Concat(got.Windows.Except(want.Windows).Select(w => "  + " + w)).ToList();
        Assert.True(want.Digest == got.Digest, $"{Path.GetFileName(data.Dir)}, {compared}: {diff.Count} element(s) and {tele.Count} telemetry window(s) differ:\n"
            + string.Join("\n", diff.Take(8).Select(e => $"  {e.Key}: {e.Before} -> {e.After}")) + "\n" + string.Join("\n", tele.Take(60)));
    }

    /// <summary>`sattle replay --projections kql` replays the estate with every projection that has a `.kql` file loaded from it, and ends in the state digest of today's text.</summary>
    [Fact]
    public void Replaying_with_the_projections_in_KQL_ends_in_the_state_digest_of_todays_text()
    {
        var output = new StringWriter(); var saved = Console.Out;
        Console.SetOut(output);
        int code;
        try { code = Program.Main(["replay", Estate, "--cluster", "synth", "--defs", Definitions, "--projections", "kql"]); }
        finally { Console.SetOut(saved); }
        Assert.True(code == 0, output.ToString());
        var digest = output.ToString().Split('\n').Select(l => l.Trim()).Last(l => l.StartsWith("state digest ", StringComparison.Ordinal))["state digest ".Length..];
        Assert.Equal(Data(Estate).Today.Value.StateDigest(), digest);
    }

    /// <summary>What the profile refuses, and that the message says what to write instead.</summary>
    [Theory]
    [InlineData("nodes ServiceNode on service = services | take 10", "filter or summarize instead")]
    [InlineData("nodes ServiceNode on service = services | sample 10", "filter or summarize instead")]
    [InlineData("nodes ServiceNode on service = services | sort by name asc", "no order")]
    [InlineData("nodes ServiceNode on service = services | top 3 by name", "no order")]
    [InlineData("nodes ServiceNode on service = services | summarize n = dcount(name) by service", "distinct ... | summarize n = count()")]
    [InlineData("nodes ServiceNode on service = services | summarize n = count_distinct(name) by service", "distinct ... | summarize n = count()")]
    [InlineData("nodes ServiceNode on service = services | summarize n = take_any(name) by service", "min, max or arg_max")]
    [InlineData("nodes ServiceNode on service = services | join (services) on service", "join kind=inner")]
    [InlineData("nodes ServiceNode on service = services | join kind=leftouter (services) on service", "write union and summarize")]
    [InlineData("nodes ServiceNode on service = services | extend t = now()", "never the wall clock")]
    [InlineData("nodes ServiceNode on service = services | extend t = ago(1h)", "never the wall clock")]
    [InlineData("nodes ServiceNode on service = services | mv-expand x = pack_array(name, cluster)", "not in the KQL profile")]
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
    public void The_profile_refuses_what_the_engine_cannot_keep_up_to_date_and_says_what_to_write(string step, string says)
    {
        var text = "graph serviceDeps : acme.svc;\ninput master : acme.ops;\n"
            + "let services = graph(\"master\") | graph-match (s) where labels(s) has \"Service\" project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name;\n"
            + step + ";\n";
        var low = KqlFrontEnd.Lower(text, Data(Estate).Catalog.Graph, "acme.svc");
        Assert.False(low.Ok);
        Assert.Contains(low.Diagnostics, d => d.Code == "Profile" && d.Message.Contains(says, StringComparison.Ordinal));
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
        var low = KqlFrontEnd.Lower(text, Data(Estate).Catalog.Graph, "acme.svc");
        Assert.False(low.Ok);
        Assert.Contains(low.Diagnostics, d => d.Message.Contains(says, StringComparison.Ordinal));
    }

    /// <summary>The catalog of the definitions with one projection in KQL in place of today's text, as `sattle replay --projections kql` loads it.
    /// Asserts that the catalog holds exactly the operators the front end wrote, and that the KQL text states what today's text states of the projection:
    /// its purpose, history, description and inputs, so the module's statement of it can go; and that the operator places are keyed by the loaded operators' ids.</summary>
    static Catalog WithKql(Recorded data, string kql)
    {
        var path = Path.Combine(Definitions, kql);
        var put = KqlFiles.Of(data.Files, [new SourceFile(path, File.ReadAllText(path))], data.Catalog);
        Assert.True(put.Ok, string.Join("\n", put.Diagnostics));
        var (pid, facts) = put.Facts.Single();
        var mid = Ids.Module(Ids.ModuleOf(pid));
        var loaded = Load(put.Files);
        bool Under(string id) => id == pid || id.StartsWith(pid + "/", StringComparison.Ordinal);
        var mine = facts.ToDictionary(e => e.Key.ToString());
        var theirs = loaded.Graph.Elements().Where(e => e is Entity en ? Under(en.Id) : e is Rel r && (Under(r.Source) || r.Source == mid && r.Target == pid)).ToDictionary(e => e.Key.ToString());
        var missing = mine.Keys.Where(k => !theirs.TryGetValue(k, out var t) || !Equals(t, mine[k])).ToList();
        var extra = theirs.Keys.Where(k => !mine.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0, $"the printed text loads other facts: {missing.Count} missing or changed, {extra.Count} extra\n"
            + string.Join("\n", missing.Take(5).Select(k => $"  - {mine[k]}\n    {(theirs.TryGetValue(k, out var t) ? t : null)}")) + "\n" + string.Join("\n", extra.Take(5).Select(k => $"  + {theirs[k]}")));
        // the places the editors read are keyed by the ids the loaded catalog gives the operators
        Assert.Equal(new DefView(loaded.Graph).Children(pid, "op").Order(StringComparer.Ordinal), put.Places.Keys.Order(StringComparer.Ordinal));
        var (stated, today) = (Stated(loaded.Graph, pid), Stated(data.Catalog.Graph, pid));
        Assert.True(stated.Count == today.Count && stated.All(kv => today.TryGetValue(kv.Key, out var t) && Equals(t, kv.Value)),
            $"the KQL text states other things of the projection than today's text:\n  {string.Join("\n  ", stated.Values.Where(e => !today.ContainsValue(e)))}\n  today: {string.Join("\n  ", today.Values.Where(e => !stated.ContainsValue(e)))}");
        return loaded;
    }

    /// <summary>The projection node, its inputs and parameters, and what each of them expects (not the wires to the operators that read them).</summary>
    static Dictionary<string, Element> Stated(Graph g, string pid)
    {
        var d = new DefView(g); var ports = d.Children(pid, "port").ToList();
        return ports.SelectMany(p => g.OutRels(p).Select(g.Rel).OfType<Rel>().Where(r => Ids.Name(r.Type) != "wire")).Concat<Element>([g.Entity(pid)!, .. ports.Select(p => g.Entity(p)!)]).ToDictionary(e => e.Key.ToString()!);
    }

    /// <summary>A let may take the name of the table it reads (Kusto binds the name inside to the table), and is never read into itself.</summary>
    [Fact]
    public void A_let_may_take_the_name_of_the_table_it_reads()
    {
        var text = "graph ingest : acme.ops;\ninput pods : k8s.PodRow;\n"
            + "let pods = pods | where isnotnull(phase);\n"
            + "nodes Pod on pod with (rule = \"observedPods\", description = \"observed by the Kubernetes adapter\") = pods | project pod = identity(\"podUid\", cluster, uid), cluster, namespace, name, phase, team = teamLabel, ready, serviceAccount;\n";
        var low = KqlFrontEnd.Lower(text, Data(Estate).Catalog.Graph, "acme.ops");
        Assert.True(low.Ok, string.Join("\n", low.Diagnostics));
    }

    /// <summary>A KQL file is named after its module and projection: `module.projection.kql`.</summary>
    [Theory]
    [InlineData("acme.svc.noSuchProjection.kql", "the graph line names serviceDeps, the file name noSuchProjection")]
    [InlineData("acme.nowhere.serviceDeps.kql", "there is no module acme.nowhere")]
    public void A_KQL_file_is_named_after_its_module_and_projection(string file, string says)
    {
        var put = KqlFiles.Of(Data(Estate).Files, [new SourceFile(Path.Combine(Definitions, file), "graph serviceDeps : acme.svc;\n")], Data(Estate).Catalog);
        Assert.Contains(put.Diagnostics, d => d.IsError && d.Message.Contains(says, StringComparison.Ordinal));
    }

    /// <summary>A projection the module's text does not declare is made from its KQL text alone; a graph named after an ontology the module imports
    /// (acme.svc imports acme.ops as ops) needs no input line.</summary>
    [Fact]
    public void A_new_projection_is_made_from_its_KQL_text_alone()
    {
        var text = "graph serviceNames : acme.svc with (description = \"The services of each namespace, by name.\", history = 2);\n"
            + "nodes ServiceNode on service = graph(\"ops\") | graph-match (s) where labels(s) has \"Service\" project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name;\n";
        var put = KqlFiles.Of(Data(Estate).Files, [new SourceFile(Path.Combine(Definitions, "acme.svc.serviceNames.kql"), text)], Data(Estate).Catalog);
        Assert.True(put.Ok, string.Join("\n", put.Diagnostics));
        var g = Load(put.Files).Graph; var d = new DefView(g); var pid = Ids.Child(Ids.Module("acme.svc"), "projection", "serviceNames");
        Assert.Equal(("view", 2L, "The services of each namespace, by name."), (d.Text(pid, "purpose"), d.Int(pid, "retain"), d.Text(pid, "description")));
        Assert.Equal("acme.ops", Ids.ModuleOf(d.Target(Ids.Child(pid, "port", "ops"), "expects")!));
    }

    /// <summary>A recording to replay, the definitions `sattle replay` reads for it (for a real capture the local space overlay or its arguments file) and their catalog.</summary>
    sealed record Recorded(string Dir, string Cluster, List<SourceFile> Files, Catalog Catalog)
    {
        /// <summary>Today's text replayed once for every graph a test compares.</summary>
        public Lazy<Engine> Today { get; } = new(() => Run(new Recorded(Dir, Cluster, Files, Catalog), Catalog, ["serviceTree", "deploymentHealth", "deploymentReport", "capabilities", "workloadStructure", "workload", "healthReport", "treeStructure", "treeSignals", "treeRollup", "healthTree", "master"]));
    }
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Recorded>> Loaded = new();

    static Recorded Data(string dir) => Loaded.GetOrAdd(dir, d => new(() =>
    {
        var files = Catalog.ReadDirectory(Definitions).ToList();
        var overlay = Path.Combine(Captures, "space-overlay.sattle");
        if (d != Estate && File.Exists(overlay)) { var i = files.FindIndex(f => f.Path.EndsWith("acme.space.sattle", StringComparison.Ordinal)); files[i] = files[i] with { Text = File.ReadAllText(overlay) }; }
        else if (d != Estate && Program.ArgumentsOf(d) is { } arguments) files = Program.WithArguments(files, arguments);
        return new Recorded(d, d == Estate ? "synth" : Program.DetectCluster(d), files, Load(files));
    })).Value;

    static string Captures => Path.Combine(Environment.GetEnvironmentVariable("SATTLE_LAB") is { Length: > 0 } lab ? lab : Path.Combine(Root, "lab"), "captures");
    static IReadOnlyList<string> CaptureDirs => Program.Captures(Captures);

    /// <summary>A theory on the recorded captures, skipped with the reason when there is none (they are not in the repository).</summary>
    public sealed class CaptureTheoryAttribute : TheoryAttribute
    {
        public CaptureTheoryAttribute() { Skip = CaptureDirs.Count > 0 ? null : "no capture under lab/captures (the captures are not in the repository)"; }
    }

    static Catalog Load(List<SourceFile> files)
    {
        var cat = new Catalog();
        var res = cat.Load(files, updatePins: true);
        Assert.True(!res.Diagnostics.Any(x => x.IsError), string.Join("\n", res.Diagnostics.Where(x => x.IsError)));
        return cat;
    }

    /// <summary>The graphs (and the graphs they read) after replaying a recording as `sattle replay` does.</summary>
    static Engine Run(Recorded data, Catalog cat, string[] graphs)
    {
        var space = cat.Spaces.Keys.Single();
        var o = Program.Options.Parse(["replay", data.Dir, "--cluster", data.Cluster]);
        var engine = new Engine(cat, space, new EngineOptions { Tenant = "lab", LateHorizonWindows = Program.LateHorizonFor(o), Only = graphs.ToHashSet() });
        new Replay(engine, cat.Spaces[space], AdapterRegistry.Create(data.Cluster), Program.ReplayOptionsOf(o, data.Cluster)).Run(data.Dir);
        return engine;
    }

    /// <summary>One graph's digest and telemetry windows (with the pair of a `mean` or `last` window), as the state digest reads them.</summary>
    static ((string Digest, HashSet<string> Windows), Graph State) Observe(Engine engine, string graph)
    {
        var g = engine.Graph(graph); var mem = (InMemoryTelemetryStore)engine.Telemetry;
        var windows = mem.Dump(g.Key).Select(w => $"{engine.Interner.Get(w.Element)} {engine.Interner.Get(w.Prop)} {w.Ws} {w.Value} {mem.AuxOf(g.Key, new WindowKey(w.Element, w.Prop, w.Ws))}").ToHashSet();
        var digest = g.State.Digest() + "|" + string.Join("\n", windows.Order(StringComparer.Ordinal));
        return ((digest, windows), g.State);
    }

    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "engine", "definitions"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
