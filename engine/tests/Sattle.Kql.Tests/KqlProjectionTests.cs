using System.Text.RegularExpressions;
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
    static string KqlDir => Path.Combine(Root, "engine", "tests", "Sattle.Kql.Tests", "kql");

    [Fact]
    public void The_kernel_is_the_one_the_contract_was_agreed_on() =>
        Assert.Equal("sha256:df107fbdf074b95e5bfca837afbc1bf3948b05ebf45f9288c40c3940abe920a1", Kernel.ComputeDigest());

    [Theory]
    [InlineData("service-tree.kql", "acme.svc", "serviceTree", "the whole projection")]
    [InlineData("deployment-health.kql", "acme.deployhealth", "deploymentHealth", "the whole projection")]
    [InlineData("deployment-report.kql", "acme.report", "deploymentReport", "the whole projection")]
    [InlineData("capabilities.kql", "acme.biz", "capabilities", "without its edit rules, which come in a later stage")]
    [InlineData("workload-structure.kql", "acme.workload", "workloadStructure", "the whole projection")]
    [InlineData("workload-health.kql", "acme.workload", "workload", "the whole projection")]
    public void A_projection_in_KQL_gives_the_state_of_its_text_on_the_estate(string kql, string module, string graph, string compared) => SameState(Data(Estate), kql, module, graph, compared);

    [Trait("Category", "Capture")]
    [CaptureTheory]
    [InlineData("service-tree.kql", "acme.svc", "serviceTree", "the whole projection")]
    [InlineData("deployment-health.kql", "acme.deployhealth", "deploymentHealth", "the whole projection")]
    [InlineData("deployment-report.kql", "acme.report", "deploymentReport", "the whole projection")]
    [InlineData("capabilities.kql", "acme.biz", "capabilities", "without its edit rules, which come in a later stage")]
    [InlineData("workload-structure.kql", "acme.workload", "workloadStructure", "the whole projection")]
    [InlineData("workload-health.kql", "acme.workload", "workload", "the whole projection")]
    public void A_projection_in_KQL_gives_the_state_of_its_text_on_every_recorded_capture(string kql, string module, string graph, string compared)
    {
        foreach (var dir in CaptureDirs) SameState(Data(dir), kql, module, graph, compared);
    }

    /// <summary>The graph a projection writes, element by element and window by window, from today's text and from the KQL text. `compared` names what of the projection the KQL text holds.</summary>
    static void SameState(Recorded data, string kql, string module, string graph, string compared)
    {
        var withKql = Replace(data, File.ReadAllText(Path.Combine(KqlDir, kql)), module, kql);
        var (want, wantGraph) = Observe(data.Today.Value, graph);
        var (got, gotGraph) = Observe(Run(data, withKql, [graph]), graph);
        Assert.NotEqual(0, wantGraph.Count);
        var diff = Graph.Diff(wantGraph, gotGraph);
        var tele = want.Windows.Except(got.Windows).Select(w => "  - " + w).Concat(got.Windows.Except(want.Windows).Select(w => "  + " + w)).ToList();
        Assert.True(want.Digest == got.Digest, $"{Path.GetFileName(data.Dir)}, {compared}: {diff.Count} element(s) and {tele.Count} telemetry window(s) differ:\n"
            + string.Join("\n", diff.Take(8).Select(e => $"  {e.Key}: {e.Before} -> {e.After}")) + "\n" + string.Join("\n", tele.Take(60)));
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
    [InlineData("nodes ServiceNode on service = services | extend t = now()", "the windows of the telemetry")]
    [InlineData("nodes ServiceNode on service = services | extend t = ago(1h)", "the windows of the telemetry")]
    [InlineData("nodes ServiceNode on service = services | mv-expand x = pack_array(name, cluster)", "not in the KQL profile")]
    [InlineData("nodes ServiceNode on service = union services, (services | project service)", "project each to")]
    [InlineData("nodes ServiceNode on service = graph(\"master\") | graph-match (a)-[p*1..3]->(b) where labels(a) has \"Pod\" and all(p, labels(p) has \"Calls\") project service = a.id, hops = p", "project the two ends")]
    [InlineData("nodes ServiceNode on service with (edit = \"create, delete\") = services", "later stage")]
    [InlineData("nodes ServiceNode on service = services | project service, name", "sets every required property")]
    [InlineData("extend nodes ServiceGroup on service = services | project service", "no step makes ServiceGroup")]
    [InlineData("edges DependsOn from service to service = services | project service;\nedges DependsOn from service to service = services | project service", "two steps make DependsOn edges")]
    [InlineData("nodes on service labels name = services", "one step per type")]
    public void The_profile_refuses_what_the_engine_cannot_keep_up_to_date_and_says_what_to_write(string step, string says)
    {
        var text = "graph serviceDeps : acme.svc;\n"
            + "let services = graph(\"master\") | graph-match (s) where labels(s) has \"Service\" project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name;\n"
            + step + ";\n";
        var low = KqlFrontEnd.Lower(text, Data(Estate).Catalog.Graph, "acme.svc", new Dictionary<string, string> { ["master"] = "acme.ops" });
        Assert.False(low.Ok);
        Assert.Contains(low.Diagnostics, d => d.Code == "Profile" && d.Message.Contains(says, StringComparison.Ordinal));
    }

    /// <summary>The definitions with the projection of `module` that the KQL text names replaced by the facts the front end writes for it,
    /// printed back to text by the engine's printer. Asserts that loading that text gives exactly those facts.</summary>
    static Catalog Replace(Recorded data, string kql, string module, string file)
    {
        var (files, cat) = (data.Files, data.Catalog);
        var mid = Ids.Module(module);
        var pid = Ids.Child(mid, "projection", Regex.Match(kql, @"(?m)^graph\s+(\w+)").Groups[1].Value);
        var d = new DefView(cat.Graph);
        var inputs = d.Children(pid, "port").Where(p => d.Text(p, "kind") == "graph").ToDictionary(p => d.Text(p, "name")!, p => Ids.ModuleOf(d.Target(p, "expects")!));
        var low = KqlFrontEnd.Lower(kql, cat.Graph, module, inputs, file);
        Assert.True(low.Ok, string.Join("\n", low.Diagnostics));
        Assert.Equal(pid, low.ProjectionId);

        bool Under(string id) => id == pid || id.StartsWith(pid + "/", StringComparison.Ordinal);
        var g = cat.Graph.Clone();
        Assert.Null(g.Apply([.. g.Rels.Where(r => Under(r.Source) || Under(r.Target)).Select(Edit.Remove), .. g.Entities.Where(e => Under(e.Id)).Select(Edit.Remove)]));
        Assert.Null(g.Apply([.. low.Facts.Elements().Select(Edit.Create)]));
        var text = Printer.Print(g, module);
        var i = files.FindIndex(f => Regex.IsMatch(f.Text, $@"(?m)^module\s+{Regex.Escape(module)}\s+@"));
        var result = files.ToList();
        result[i] = files[i] with { Text = text };

        var loaded = Load(result);
        var back = loaded.Graph;
        var mine = low.Facts.Elements().ToDictionary(e => e.Key.ToString());
        var theirs = back.Elements().Where(e => e is Entity en ? Under(en.Id) : e is Rel r && (Under(r.Source) || r.Source == mid && r.Target == pid)).ToDictionary(e => e.Key.ToString());
        var missing = mine.Keys.Where(k => !theirs.TryGetValue(k, out var t) || !Equals(t, mine[k])).ToList();
        var extra = theirs.Keys.Where(k => !mine.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0, $"the printed text loads other facts: {missing.Count} missing or changed, {extra.Count} extra\n"
            + string.Join("\n", missing.Take(5).Select(k => $"  - {mine[k]}\n    {(theirs.TryGetValue(k, out var t) ? t : null)}")) + "\n" + string.Join("\n", extra.Take(5).Select(k => $"  + {theirs[k]}")) + "\n" + text);
        return loaded;
    }

    /// <summary>A recording to replay, the definitions `sattle replay` reads for it (for a real capture the local space overlay or its arguments file) and their catalog.</summary>
    sealed record Recorded(string Dir, string Cluster, List<SourceFile> Files, Catalog Catalog)
    {
        /// <summary>Today's text replayed once for all six graphs.</summary>
        public Lazy<Engine> Today { get; } = new(() => Run(new Recorded(Dir, Cluster, Files, Catalog), Catalog, ["serviceTree", "deploymentHealth", "deploymentReport", "capabilities", "workloadStructure", "workload"]));
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
