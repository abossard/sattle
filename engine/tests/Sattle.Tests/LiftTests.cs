using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The `lift` statement (README, "Edge lifting"): the service tree and the capabilities are written with it and give what the hand-written statements gave (the hand-written modules are kept under fixtures/handwritten), every clause does what it says, the checker refuses what it cannot lift, and the result does not depend on the chunk size.</summary>
public class LiftTests
{
    static string Estate => EstateTests.EstateDir;
    static string HandWritten(string module) => File.ReadAllText(Path.Combine(Repo.Root, "engine", "tests", "Sattle.Tests", "fixtures", "handwritten", module + ".sattle"));

    /// <summary>The definitions for a capture directory (with the overlay `sattle replay` applies), optionally with the hand-written service tree and capability modules, and an edit of one module.</summary>
    static List<SourceFile> Files(string dir, bool handWritten = false, string? module = null, Func<string, string>? edit = null)
    {
        var files = Repo.DefinitionFilesFor(dir);
        for (var i = 0; i < files.Count; i++)
        {
            var name = Path.GetFileNameWithoutExtension(files[i].Path);
            if (handWritten && name is "acme.svc" or "acme.biz") files[i] = files[i] with { Text = HandWritten(name) };
            if (module == name && edit is not null) files[i] = files[i] with { Text = edit(files[i].Text) };
        }
        return files;
    }

    static Engine Run(List<SourceFile> files, string dir, string cluster = "synth", int chunk = 2000)
    {
        var cat = new Catalog(); var res = cat.Load(files, updatePins: true);
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
        var space = cat.Spaces.Keys.Single();
        var engine = new Engine(cat, space, new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 });
        new Replay(engine, cat.Spaces[space], AdapterRegistry.Create(cluster), new ReplayOptions { ChunkRows = chunk, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000, Cluster = cluster }).Run(dir);
        return engine;
    }

    /// <summary>The telemetry of a graph as sorted text: element, property, window, value (the order inside a batch is not part of the result).</summary>
    static List<string> Telemetry(Engine e, string graph) =>
        [.. ((InMemoryTelemetryStore)e.Telemetry).Dump(e.Graph(graph).Key).Select(x => $"{e.Interner.Get(x.Element)}|{e.Interner.Get(x.Prop)}|{x.Ws}|{x.Value}").Order(StringComparer.Ordinal)];

    static void AssertSame(Engine lifted, Engine handWritten)
    {
        Assert.Equal(handWritten.StateDigest(), lifted.StateDigest());
        Assert.Equal(handWritten.Graphs.Select(g => g.Name), lifted.Graphs.Select(g => g.Name));
        foreach (var g in lifted.Graphs)
        {
            Assert.True(handWritten.Graph(g.Name).State.SameAs(g.State), $"{g.Name}: entities and relationships differ");
            Assert.Equal(Telemetry(handWritten, g.Name), Telemetry(lifted, g.Name));
        }
    }

    // ---- the rewrite gives what the hand-written statements gave ----------------------------------------------------------------------

    [Fact]
    public void On_the_estate_the_lift_statements_give_the_graphs_and_telemetry_of_the_hand_written_ones()
    {
        var lifted = Run(Files(Estate), Estate); var hand = Run(Files(Estate, handWritten: true), Estate);
        AssertSame(lifted, hand);
        Assert.Equal(2, lifted.Graph("capabilities").State.Rels.Count(r => Ids.Name(r.Type) == "DependsOn"));
        Assert.Equal(2, lifted.Graph("serviceTree").State.Rels.Count(r => Ids.Name(r.Type) == "DependsOn"));
    }

    [Fact]
    public void On_lab_fixtures_the_lift_statements_give_the_graphs_and_telemetry_of_the_hand_written_ones()
    {
        var dir = Repo.Fixtures; var cluster = Repo.ClusterOf(dir);
        AssertSame(Run(Files(dir), dir, cluster), Run(Files(dir, handWritten: true), dir, cluster));
    }

    [Trait("Category", "Capture")]
    [CaptureFact]
    public void On_the_newest_real_capture_the_lift_statements_give_the_graphs_and_telemetry_of_the_hand_written_ones()
    {
        var dir = Directory.Exists(Repo.Captures) ? Directory.EnumerateDirectories(Repo.Captures).Order(StringComparer.Ordinal).LastOrDefault() : null;
        Assert.NotNull(dir);
        var cluster = Repo.ClusterOf(dir);
        AssertSame(Run(Files(dir), dir, cluster), Run(Files(dir, handWritten: true), dir, cluster));
    }

    [Theory]
    [InlineData(1)] [InlineData(50)] [InlineData(100000)]
    public void The_state_digest_of_the_lifted_graphs_does_not_depend_on_the_chunk_size(int chunk)
    {
        var reference = Run(Files(Estate), Estate, chunk: 2000);
        var e = Run(Files(Estate), Estate, chunk: chunk);
        Assert.Equal(reference.StateDigest(), e.StateDigest());
        Assert.Equal(Telemetry(reference, "capabilities"), Telemetry(e, "capabilities"));
        Assert.Equal(Telemetry(reference, "serviceTree"), Telemetry(e, "serviceTree"));
    }

    [Fact]
    public void The_kept_values_ride_on_the_windows_the_group_flows_are_the_sum_of_the_service_flows()
    {
        var e = Run(Files(Estate), Estate);
        var tree = e.Graph("serviceTree");
        var dump = ((InMemoryTelemetryStore)e.Telemetry).Dump(tree.Key).Where(x => e.Interner.Get(x.Prop).EndsWith("prop:flowsOut", StringComparison.Ordinal)).ToList();
        var groups = tree.State.Entities.Where(x => Ids.Name(x.Type) == "ServiceGroup").Select(x => x.Id).ToHashSet();
        var perWindow = dump.GroupBy(x => x.Ws).ToList();
        Assert.NotEmpty(perWindow);
        foreach (var w in perWindow)
            Assert.Equal(w.Where(x => !groups.Contains(e.Interner.Get(x.Element))).Sum(x => x.Value), w.Where(x => groups.Contains(e.Interner.Get(x.Element))).Sum(x => x.Value));
    }

    // ---- conservation: nothing is counted twice, nothing appears ---------------------------------------------------------------------------

    static string ServiceName(Engine e, string id) => e.Graph("serviceTree").State.Entities.First(x => x.Id == id).Props.Text("d:acme.svc/prop:name")!;
    static string CapName(Engine e, string id) => e.Graph("capabilities").State.Entities.FirstOrDefault(x => x.Id == id)?.Props.Text("d:acme.biz/prop:name") ?? e.Master.State.Entities.First(x => x.Id == id).Props.Text("d:acme.ops/prop:name")!;

    static Dictionary<string, decimal> Summed(Engine e, string graph, string prop, Func<string, string> name)
    {
        var rels = e.Graph(graph).State.Rels.Where(r => Ids.Name(r.Type) == "DependsOn").ToDictionary(r => r.Id, r => name(r.Source) + " -> " + name(r.Target));
        var sums = new Dictionary<string, decimal>();
        foreach (var s in ((InMemoryTelemetryStore)e.Telemetry).Dump(e.Graph(graph).Key))
            if (e.Interner.Get(s.Prop).EndsWith("/prop:" + prop, StringComparison.Ordinal) && rels.TryGetValue(e.Interner.Get(s.Element), out var pair)) sums[pair] = sums.GetValueOrDefault(pair) + s.Value;
        return sums;
    }

    [Theory]
    [InlineData("storefront,ordering,data-platform", "web -> api|api -> db")]               // every service has one image, the images differ
    [InlineData("ordering,ordering,data-platform", "api -> db")]                            // web and api share one: the call between them is a self-loop and is not lifted
    [InlineData("storefront,ordering,", "web -> api")]                                      // db has no image: its edge is not lifted
    [InlineData("storefront|ordering,ordering,data-platform", "api -> db")]                 // web has two images: it is not lifted at all
    public void The_lifted_flows_are_exactly_the_flows_of_the_service_edges_whose_ends_have_one_image_each_and_different_ones(string realizes, string liftedEdges)
    {
        var dir = ProjectionEdgesTests.CopyEstate([.. realizes.Split(',')], extraWebToDb: false, retired: null);
        try
        {
            var e = Run(Files(dir), dir);
            var service = Summed(e, "serviceTree", "flows", id => ServiceName(e, id));
            var lifted = Summed(e, "capabilities", "callFlows", id => CapName(e, id));
            Assert.Equal(liftedEdges.Split('|').Sum(k => service[k]), lifted.Values.Sum());
            Assert.True(lifted.Values.Sum() <= service.Values.Sum());
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- each clause does what it says ---------------------------------------------------------------------------------------------------

    static HashSet<string> LiftedPairs(string dir, Func<string, string>? edit)
    {
        var e = Run(Files(dir, module: "acme.biz", edit: edit), dir);
        return [.. Summed(e, "capabilities", "callFlows", id => CapName(e, id)).Keys];
    }

    static string Images(string text) => text.Replace("initial \"ok\";\n  }", "initial \"ok\"\n      images each;\n  }");

    [Theory]
    [InlineData(false, "Ordering -> Data platform")]                                                       // images reject (the default): web has two images and is not lifted
    [InlineData(true, "Ordering -> Data platform|Storefront -> Ordering")]                                // images each: the call counts for both images; web to api from Ordering is a self-loop
    public void The_images_clause_chooses_between_rejecting_a_node_with_two_images_and_counting_it_for_each(bool each, string expected)
    {
        var dir = ProjectionEdgesTests.CopyEstate(["storefront|ordering", "ordering", "data-platform"], false, null);
        try { Assert.Equal(expected.Split('|').Order(), LiftedPairs(dir, each ? Images : null).Order()); }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(false, "Ordering -> Data platform")]                                                       // self-loops are dropped
    [InlineData(true, "Ordering -> Data platform|Ordering -> Ordering")]                                  // selfloops keep: the call inside one capability is an edge of the capability to itself
    public void The_selfloops_clause_drops_or_keeps_the_calls_inside_one_image(bool keep, string expected)
    {
        var dir = ProjectionEdgesTests.CopyEstate(["ordering", "ordering", "data-platform"], false, null);
        try { Assert.Equal(expected.Split('|').Order(), LiftedPairs(dir, keep ? t => t.Replace("initial \"ok\";\n  }", "initial \"ok\"\n      selfloops keep;\n  }") : null).Order()); }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(true, "Ordering -> Data platform")]                                                        // when image.status != "retired": a retired capability is no image
    [InlineData(false, "Ordering -> Data platform|Storefront -> Ordering")]                               // without the clause the edge to the retired capability is lifted
    public void The_when_clause_decides_which_images_count(bool withClause, string expected)
    {
        var dir = ProjectionEdgesTests.CopyEstate(["storefront", "ordering", "data-platform"], false, "storefront");     // storefront is retired: web has no image
        try
        {
            // with the clause a retired capability is no image; without it web maps to a retired capability and its edge is lifted
            var pairs = LiftedPairs(dir, withClause ? null : t => t.Replace("      when image.status != \"retired\"\n", ""));
            Assert.Equal(withClause ? ["Ordering -> Data platform"] : ["Ordering -> Data platform", "Storefront -> Ordering"], pairs.Order());
            _ = expected;
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_within_clause_sets_the_look_back_of_the_series_the_lift_is_lowered_to()
    {
        TimeSpan Within(string text)
        {
            var cat = new Catalog(); Assert.DoesNotContain(cat.Load(Files(Estate, module: "acme.biz", edit: _ => text), updatePins: true).Diagnostics, d => d.IsError);
            var pid = "d:acme.biz/projection:capabilityView";
            var (tp, diags) = new ProjectionChecker(cat.Graph).Check(pid);
            Assert.DoesNotContain(diags, d => d.IsError);
            return TimeSpan.FromTicks(tp!.Ops.OfType<TSeries>().Single(x => x.Name.StartsWith("dependsOn__", StringComparison.Ordinal)).WithinNs / 100);
        }
        var original = File.ReadAllText(Path.Combine(Repo.Definitions, "acme.biz.sattle"));
        Assert.Equal(TimeSpan.FromHours(24), Within(original));
        Assert.Equal(TimeSpan.FromMinutes(2), Within(original.Replace("initial \"ok\";\n  }", "initial \"ok\"\n      within 2m;\n  }")));
    }

    [Fact]
    public void A_lift_statement_prints_back_as_the_text_it_was_parsed_from_and_names_only_what_differs_from_the_defaults()
    {
        var cat = new Catalog(); Assert.DoesNotContain(cat.Load(Files(Estate), updatePins: true).Diagnostics, d => d.IsError);
        var svc = Printer.Print(cat.Graph, "acme.svc");
        Assert.Contains("lift dependsOn as DependsOn from master infra.Calls via master infra.Backs\n    keep cluster = src.cluster, namespace = src.namespace\n    derive dropRatio = if(flows > 0, drops / flows, 0.0), flowRate = rate(flows)\n    state status of dropRatio above 0.05 for 3 then \"degraded\" below 0.02 for 3 then \"ok\" initial \"ok\"\n    images each;", svc);
        Assert.DoesNotContain("selfloops", svc); Assert.DoesNotContain("within", svc);
        var biz = Printer.Print(cat.Graph, "acme.biz");
        Assert.Contains("    lift dependsOn as DependsOn from tree svc.DependsOn via master ops.Realizes\n      when image.status != \"retired\"\n      carry callFlows = flows, callDrops = drops\n", biz);
        Assert.DoesNotContain("images ", biz);
    }

    // ---- the checker refuses what it cannot lift, naming the statement and the reason ----------------------------------------------

    public static TheoryData<string, string, string, string> Refusals => new()
    {
        { "acme.svc", "\n    images each;", ";", "Backs is not declared a function" },                                                          // a mapping that is not a function needs a policy
        { "acme.svc", "from master infra.Calls via", "from master DependsOn via", "DependsOn is not a relationship type of the edge graph master" },
        { "acme.svc", "via master infra.Backs", "via master DependsOn", "DependsOn is not a relationship type of the mapping graph master" },
        { "acme.svc", "dependsOn as DependsOn from", "dependsOn as Contains from", "dropRatio is not a telemetry property of Contains" },                   // Contains has no telemetry to carry
        { "acme.svc", "    keep cluster", "    carry status = flows\n    keep cluster", "status is not a telemetry property of DependsOn" },
        { "acme.svc", "    keep cluster", "    carry flows = nosuch\n    keep cluster", "nosuch is not a telemetry property of Calls" },
        { "acme.svc", "    keep cluster", "    carry dropRatio = flows\n    keep cluster", "dropRatio is Decimal and flows is Integer" },
        { "acme.svc", "keep cluster = src.cluster", "keep cluster = cluster", "a kept value is src.property or dst.property" },
        { "acme.svc", "keep cluster = src.cluster", "keep cluster = src.nosuch", "has no data property nosuch" },
        { "acme.svc", "derive dropRatio", "derive status = 1, dropRatio", "status is not a telemetry property of DependsOn" },
        { "acme.svc", "state status of dropRatio", "state flows of dropRatio", "flows is not a property of DependsOn (it holds the state, not a measure)" },
        { "acme.svc", "state status of dropRatio", "state status of nosuch", "nosuch is neither carried nor derived" },
        { "acme.svc", "flows?: Integer telemetry delta sum every 60s;\n  drops?", "flows?: Integer telemetry cumulative last every 60s;\n  drops?", "declares combine last" },
        { "acme.svc", "if(flows > 0, drops / flows, 0.0)", "if(nosuch > 0, drops / flows, 0.0)", "nosuch" },
        { "acme.svc", "from master infra.Calls", "from nope infra.Calls", "'nope' is not a graph port" },
        { "acme.biz", "  rule lifted readonly", "  rule lifted bijective", "not in a bijective or lens rule" },
    };

    [Theory, MemberData(nameof(Refusals))]
    public void The_checker_refuses_a_lift_it_cannot_lower_and_the_message_names_the_statement_and_the_reason(string module, string from, string to, string reason)
    {
        var files = Files(Estate, module: module, edit: t => { Assert.Contains(from, t); return t.Replace(from, to); });
        var diags = new Catalog().Load(files, updatePins: true).Diagnostics.Where(d => d.IsError).ToList();
        Assert.Contains(diags, d => d.Message.Contains(reason, StringComparison.Ordinal) && (d.Message.StartsWith("lift ", StringComparison.Ordinal) || d.Code is "Syntax" or "Lift" or "UnknownName"));
    }

    [Fact]
    public void A_refusal_of_the_checker_names_the_lift_statement_that_caused_it()
    {
        var files = Files(Estate, module: "acme.svc", edit: t => t.Replace("if(flows > 0, drops / flows, 0.0)", "if(nosuch > 0, drops / flows, 0.0)"));
        var diags = new Catalog().Load(files, updatePins: true).Diagnostics.Where(d => d.IsError).ToList();
        Assert.Contains(diags, d => d.Message.StartsWith("lift dependsOn: ", StringComparison.Ordinal) && d.Focus.Contains("/op:dependsOn", StringComparison.Ordinal));
    }
}
