using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The synthetic capture `estate` (tests/Sattle.Tests/fixtures/estate, record envelopes, invented names only) has data for every operator of the projection language: the workload model with its scenarios, containment and dependency edges for a closure, a Link that merges, a Union of two sources, and node and cluster telemetry for Series, Window, Threshold and Rollup. It is served with the space arguments of estate/space/acme.space.sattle.</summary>
public class EstateTests
{
    const long Min = 60_000_000_000;
    static string Estate => Path.Combine(Repo.Root, "engine", "tests", "Sattle.Tests", "fixtures", "estate");

    /// <summary>The ontology of the demo projections (`DemoProjections`): the shapes the canvas walkthroughs build, over the workload model and the master of the estate.</summary>
    const string Demo = """
        module acme.estatedemo @ 1.0.0;
        import acme.ops    @ 1.0.0 sha256:… as ops;
        import acme.workload @ 1.0.0 sha256:… as wl;

        /// A part of the workload by its key.
        entity Part { key: Text; kind?: Text; name: Text label; own?: Integer; }
        /// The source is affected by the target, directly or through others.
        relationship Reaches (Part) -> (Part) kind dependency;
        /// The drop-rate state of a node.
        entity Reading { component: Text label; degraded?: Text; }
        identity part for Part version "1" { key: Text by exact; }
        identity reading for Reading version "1" { component: Text by exact; }
        """;

    /// <summary>The projections of the demo module, one KQL text each.</summary>
    static readonly SourceFile[] DemoProjections =
    [
        new("acme.estatedemo.closure.kql", """
            graph closure : acme.estatedemo with (
                description = "What affects each part of the workload, directly or through others? The closure of the roll-up edges.",
                history = 4);
            input st : acme.workload;

            nodes Part on part = graph("st") | graph-match (c) where labels(c) has "Component" project k = c.key, nm = c.name
                | project part = identity("part", k), key = k, name = nm;
            edges Reaches from a to b with (key = "reach") = graph("st")
                | graph-match (c)-[p*1..9]->(d) where labels(c) has "Component" and all(p, labels(p) has "Affects") project ka = c.key, kb = d.key
                | project a = identity("part", ka), b = identity("part", kb);
            """),
        new("acme.estatedemo.both.kql", """
            graph both : acme.estatedemo with (
                description = "Every part once, whether it is a service or not: a union and a distinct.",
                history = 4);
            input st : acme.workload;

            let comps = graph("st") | graph-match (c) where labels(c) has "Component" project k = c.key, kd = c.kind, nm = c.name;
            let services = graph("st") | graph-match (c) where labels(c) has "WorkloadService" project k = c.key, kd = c.kind, nm = c.name;
            nodes Part on part = union comps, services | distinct * | project part = identity("part", k), key = k, ['kind'] = kd, name = nm;
            """),
        new("acme.estatedemo.series.kql", """
            graph series : acme.estatedemo with (
                description = "The drop rate of the nodes over time, and the operations of the activity log per minute.",
                history = 4);
            input master : acme.ops;

            let warn = graph("master") | graph-match (nd) where labels(nd) has "Node" project nd = nd.id, c = nd.name
                | join kind=inner (telemetry("master", 24h) | where property == "dropRate" | project nd = id, windowStart, property, value) on nd
                | project c, windowStart, dropRate = value
                | summarize m = avg(dropRate), n = count(), s = sum(dropRate), v = max(dropRate) by c, windowStart;
            let warnOn = warn
                | project c, windowStart, value = v, above = 0.01, enterAfter = 2, below = 0.005, exitAfter = 2
                | invoke hysteresis("on", "off", "off");
            let acts = graph("master") | graph-match (ev) where labels(ev) has "ActivityEvent" project t = ev.['time'], c = ev.operation
                | summarize n = count() by c, windowStart = bin(t, 1m);
            nodes Reading on r = warnOn | project r = identity("reading", c), component = c, degraded = state;
            nodes Part on part = acts | project part = identity("part", strcat("evt/", c)), windowStart, key = strcat("evt/", c), ['kind'] = "activity", name = c, own = n;
            """),
        new("acme.estatedemo.values.kql", """
            graph values : acme.estatedemo with (
                description = "Two constant rows.",
                history = 4);
            input master : acme.ops;

            nodes Part on part = datatable(k:string, n:long) ["a", 1, "b", 2]
                | project part = identity("part", k), key = k, ['kind'] = "constant", name = k, own = n;
            """),
    ];

    static string Space(string text) => text
        .Replace("import acme.biz ", "import acme.estatedemo @ 1.0.0 sha256:… as demo;\nimport acme.biz ")
        .Replace("  graph workload = wl.workloadHealth(master: master, authored: authored, structure: workloadStructure, workloadNamespace: \"shop\");",
            "  graph workload = wl.workloadHealth(master: master, authored: authored, structure: workloadStructure, workloadNamespace: \"shop\");\n  graph demoClosure = demo.closure(st: workloadStructure);\n  graph demoBoth = demo.both(st: workload);\n  graph demoSeries = demo.series(master: master);\n  graph demoValues = demo.values(master: master);");

    /// <summary>The definitions (their space binds the workload of the estate), and with the demo projections and their graphs when `demo` is set.</summary>
    public static Catalog LoadCatalog(bool demo)
    {
        var files = Files(demo);
        var cat = new Catalog(Repo.Projections);
        var res = cat.Load(files, updatePins: true);
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
        return cat;
    }

    public static List<SourceFile> Files(bool demo)
    {
        var files = Catalog.ReadDirectory(Repo.Definitions).ToList();
        if (!demo) return files;
        var i = files.FindIndex(f => f.Path.EndsWith("acme.space.sattle", StringComparison.Ordinal));
        files[i] = files[i] with { Text = Space(files[i].Text) };
        files.AddRange([new SourceFile("acme.estatedemo.sattle", Demo), .. DemoProjections]);
        return files;
    }

    public static string EstateDir => Estate;

    /// <summary>The estate as the tests read it: the scenario run the assembly shares, and one more replay with the demo module. The tests read the graphs as they are served.</summary>
    static readonly Lazy<ReplayTests.Run> Plain = new(() => Scenarios.Run(Repo.Estate));
    static readonly Lazy<ReplayTests.Run> WithDemo = new(() => ReplayTests.Replay(Estate, 50, o: new EngineOptions { Tenant = "lab", LateHorizonWindows = 3 }, cat: LoadCatalog(demo: true)));

    [Theory]
    [InlineData("master")] [InlineData("serviceTree")] [InlineData("capabilities")] [InlineData("workloadStructure")] [InlineData("workload")] [InlineData("deploymentHealth")] [InlineData("deploymentReport")]
    public void Every_graph_of_the_space_has_entities(string graph)
    {
        Assert.True(Plain.Value.Entities(graph).Count > 0, $"{graph} is empty");
    }

    [Theory]
    [InlineData("workloadStructure", "Contains")] [InlineData("workloadStructure", "DependsOn")] [InlineData("workloadStructure", "Affects")] [InlineData("workloadStructure", "TalksTo")] [InlineData("workload", "Measures")] [InlineData("workload", "EvidenceFor")] [InlineData("serviceTree", "DependsOn")] [InlineData("capabilities", "DependsOn")]
    public void The_graphs_have_relationships_of_the_types_a_closure_walks(string graph, string type)
    {
        Assert.NotEmpty(Plain.Value.Relationships(graph, type));
    }

    [Fact]
    public void A_Link_merges_identities_a_union_has_two_sources_and_the_retained_evidence_is_there()
    {
        var e = Plain.Value;
        Assert.True(e.Engine.Identities.Merges > 0, "no identity merged: the second api pod's row arrives after the flows that name it");          // the counters of identities are served by no file of a replay: read inside
        Assert.Equal(0, e.Engine.Identities.Conflicts);
        Assert.Equal(18, e.Entities("master", "ActivityEvent").Count);  // 10 from the management API and 8 from the diagnostic setting: the two sources of the Union in observedActivityEvents
        Assert.NotEmpty(e.Entities("master", "WarningEvent"));
    }

    [Fact]
    public void Node_and_cluster_telemetry_has_windows_and_the_threshold_signals_exist()
    {
        var e = Plain.Value;
        Assert.All(new[] { "dropRate", "forwardRate", "cpuPercent" }, p => Assert.NotEmpty(e.Windows("master", p)));
        Assert.True(e.Windows("master", "dropRate").Count >= 8);     // several windows per node, so a Series has a history to walk
        var signals = e.Entities("workload", "ComponentSignal");
        Assert.True(signals.Count >= 5);
        Assert.Contains(signals, s => s.Prop("signalState") == "nodata");             // at the end the storage account's meter has been silent for longer than its time to live (the thresholds that fire on the way are WorkloadTests)
    }

    [Fact]
    public void The_workload_roll_up_has_a_component_that_is_not_healthy_because_of_something_it_depends_on()
    {
        var states = Plain.Value.Entities("workload").Select(c => c.Prop("state")).Where(s => s is not null).ToList();
        Assert.Contains("Healthy", states);
        Assert.True(states.Any(s => s is "No data" or "Degraded" or "Unhealthy"), "every component is healthy or unknown: the roll-up shows nothing");      // at the end: the storage account in no data, and what depends on it
    }

    [Theory]
    [InlineData("demoClosure", 0, 1)] [InlineData("demoBoth", 10, 0)] [InlineData("demoSeries", 3, 0)] [InlineData("demoValues", 2, 0)]
    public void The_operator_shapes_of_the_canvas_walkthroughs_produce_output(string graph, int minEntities, int minRels)
    {
        var (entities, relationships) = (WithDemo.Value.Entities(graph).Count, WithDemo.Value.Relationships(graph).Count);
        Assert.True(entities >= minEntities, $"{graph}: {entities} entities");
        Assert.True(relationships >= minRels, $"{graph}: {relationships} relationships");
    }

    [Fact]
    public void The_closure_is_larger_than_its_base_so_the_traversal_walked_more_than_one_step()
    {
        var e = WithDemo.Value;
        var (baseEdges, closure) = (e.Relationships("workloadStructure", "Affects").Count, e.Relationships("demoClosure").Count);
        Assert.True(closure > baseEdges, $"closure {closure} against {baseEdges} edges");
    }

    /// <summary>What a viewer needs to name and draw a graph is ontology data: the label of an entity type, the kind of a relationship type, and what the graph is in the words of its projection.</summary>
    [Fact]
    public void The_ontology_endpoint_serves_the_label_of_each_entity_type_and_the_kind_of_each_relationship_type_and_the_space_describes_each_graph()
    {
        using var h = new SpaceHost(Files(demo: false), new HostOptions { Projections = Repo.Projections});
        var onto = h.Ontology("workload");
        string? Of(string list, string type, string field, bool id) => onto[list]!.AsArray().Single(t => t!["id"]!["value"]!.GetValue<string>().Split('.')[^1] == type)![field] is { } v ? (id ? v["value"] : v)!.GetValue<string>() : null;
        Assert.Equal("name", Of("entityTypes", "WorkloadService", "label", true)!.Split('.')[^1]);                          // the label of the supertype Component
        Assert.Equal("detail", Of("entityTypes", "DependencyEvidence", "label", true)!.Split('.')[^1]);
        Assert.Equal(("communication", "composition"), (Of("relationshipTypes", "TalksTo", "kind", false), Of("relationshipTypes", "HostedOn", "kind", false)));
        // the tree view reads the state of a component through the tree roles of the supertype Component
        foreach (var type in new[] { "Workload", "WorkloadService", "WorkloadDeployment", "WorkloadPod", "WorkloadNode" })
        {
            var treeRoles = onto["entityTypes"]!.AsArray().Single(t => t!["id"]!["value"]!.GetValue<string>().Split('.')[^1] == type)!["treeState"]!;
            Assert.Equal(("own", "state", "severity", "kind"), (treeRoles["own"]!.GetValue<string>().Split('.')[^1], treeRoles["rolledUp"]!.GetValue<string>().Split('.')[^1], treeRoles["severity"]!.GetValue<string>().Split('.')[^1], treeRoles["kind"]!.GetValue<string>().Split('.')[^1]));
        }
        Assert.Equal(("composition", "dependency", "support"), (Of("relationshipTypes", "Contains", "kind", false), Of("relationshipTypes", "DependsOn", "kind", false), Of("relationshipTypes", "Measures", "kind", false)));
        var tree = h.Ontology("healthTree");
        var contains = tree["relationshipTypes"]!.AsArray().Single(t => t!["id"]!["value"]!.GetValue<string>().Split('.')[^1] == "Contains")!;
        Assert.Equal((true, "composition"), (contains["hierarchy"]!.GetValue<bool>(), contains["kind"]!.GetValue<string>()));      // two fields: a hierarchy is a composition, a composition need not be a hierarchy
        Assert.Equal(["Affects"], onto["relationshipTypes"]!.AsArray().Where(t => t!["rollup"]?.GetValue<bool>() == true).Select(t => t!["id"]!["value"]!.GetValue<string>().Split('.')[^1]));      // engine 3.49: the roll-up edge restates the others
        Assert.Null(h.Ontology("master")["relationshipTypes"]!.AsArray().Single(t => t!["id"]!["value"]!.GetValue<string>() == "NoType")!["kind"]);
        var graphs = h.SpaceInfo()["graphs"]!.AsArray().Where(g => g!["role"]!.GetValue<string>() != "catalog").ToList();
        Assert.All(graphs, g => Assert.False(string.IsNullOrWhiteSpace(g!["description"]?.GetValue<string>()), $"graph {g["id"]} has no description"));
        Assert.StartsWith("The service tree", graphs.Single(g => g!["id"]!.GetValue<string>() == "serviceTree")!["description"]!.GetValue<string>());
    }

    static readonly Lazy<SpaceHost> Examples = new(HostTests.NewHost);

    /// <summary>PROTOCOL 3.2: nothing about health is in the engine. A property with a closed set of states declares a value role for each state, and the ontology endpoint of every graph that carries the property serves them; a property without such a set carries none.</summary>
    [Theory]
    [InlineData("workload", "state", "Degraded:warning,Healthy:ok,No data:unknown,Unhealthy:danger,Unknown:unknown")]
    [InlineData("workload", "signalState", "nodata:unknown,off:ok,on:warning")]
    [InlineData("healthTree", "rolledState", "Degraded:warning,Healthy:ok,Unhealthy:danger,Unknown:unknown")]
    [InlineData("deploymentHealth", "acme.deployhealth.state", "Healthy:ok,Unhealthy:danger")]
    public void The_ontology_endpoint_serves_the_value_roles_of_a_state_property(string graph, string property, string roles)
    {
        var properties = Examples.Value.Ontology(graph)["properties"]!.AsArray();
        static string Id(JsonNode? p) => p!["id"]!["value"]!.GetValue<string>();
        var served = properties.Single(p => Id(p) == property || !property.Contains('.') && Id(p).Split('.')[^1] == property)!;
        Assert.Equal(roles, string.Join(",", served["presentation"]!.AsArray().Select(x => x!["value"]!["value"]!.GetValue<string>() + ":" + x["role"]!.GetValue<string>()).Order()));
        Assert.All(properties.Where(p => Id(p).Split('.')[^1] == "name"), p => Assert.Null(p!["presentation"]));          // a property without a closed set of states carries none
    }

    [Fact]
    public void On_the_estate_the_roll_up_holds_states_of_more_than_one_value_role()
    {
        var roles = Plain.Value.Entities("workload").Select(c => c.Prop("state")).Where(s => s is not null).Distinct().ToList();
        Assert.True(roles.Count >= 2, string.Join(",", roles));
    }

    [Fact]
    public void A_preview_of_the_estate_shows_the_identity_merge_of_its_Link_so_a_walkthrough_can_assert_it()
    {
        using var h = new SpaceHost(Files(demo: false), new HostOptions { Projections = Repo.Projections});
        new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 40, AdvanceEveryNs = Min, LatenessNs = Min}).Run(Estate);
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var p = h.Preview(d, new System.Text.Json.Nodes.JsonObject { ["graphs"] = new System.Text.Json.Nodes.JsonArray("serviceTree") });
        var merged = long.Parse(p["identity"]!["preview"]!["merged"]!.GetValue<string>());
        Assert.True(merged >= 1, "the second api pod's row arrives after the flows that name it: the Link of the pod identities merges");
        Assert.Equal(h.Engine.Identities.Merges, merged);
        Assert.Equal("0", p["identity"]!["difference"]!["merged"]!.GetValue<string>());
    }
}

