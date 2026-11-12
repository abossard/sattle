using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The synthetic capture `estate` (tests/Sattle.Tests/fixtures/estate, written by build-estate.py, invented names only) has data for every operator of the projection language: the workload model with its scenarios, containment and dependency edges for a closure, a Link that merges, a Union of two sources, and node and cluster telemetry for Series, Window, Threshold and Rollup. It is served with the space arguments of estate/space/acme.space.sattle.</summary>
public class EstateTests
{
    const long Min = 60_000_000_000;
    static string Estate => Path.Combine(Repo.Root, "engine", "tests", "Sattle.Tests", "fixtures", "estate");

    /// <summary>Projections of the shapes the canvas walkthroughs build, over the workload model and the master of the estate. They write their own small ontology.</summary>
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

        /// What affects each part of the workload, directly or through others? The closure of the roll-up edges.
        projection closure view forward output acme.estatedemo {
          in st: graph wl;
          retain 4;
          let parts = match st (c: wl.Component) |> select { k: c.key, nm: c.name };
          emit entity Part from parts identity part(key: k) set { key: k, name: nm };
          let base = match st (a: wl.Component)-[:wl.Affects]->(b: wl.Component) |> select { a: a.key, b: b.key };
          let step = base |> select { x: a, y: b };
          let reach = fixpoint base
            via join reach, step on b == x
              |> select { a: a, b: y }
            max 8;
          emit relationship Reaches from reach key reach: part(key: a) -> part(key: b);
        }

        /// Every part once, whether it is a service or not: a union and a distinct.
        projection both view forward output acme.estatedemo {
          in st: graph wl;
          retain 4;
          let comps = match st (c: wl.Component) |> select { k: c.key, kd: c.kind, nm: c.name };
          let services = match st (c: wl.WorkloadService) |> select { k: c.key, kd: c.kind, nm: c.name };
          let all = union comps, services;
          let once = all |> distinct;
          emit entity Part from once identity part(key: k) set { key: k, kind: kd, name: nm };
        }

        /// The drop rate of the nodes over time, and the operations of the activity log per minute.
        projection series view forward output acme.estatedemo {
          in master: graph ops;
          retain 4;
          let ser = match master (nd: infra.Node)
            |> series master nd.dropRate within 24h;
          let warn = ser
            |> rollup by { c: nd.name } combine { m: mean(dropRate), n: count(), s: sum(dropRate), v: max(dropRate) };
          let warnOn = warn
            |> threshold v by { c: c } above 0.01 for 2 then "on" below 0.005 for 2 then "off" initial "off" as degraded;
          let acts = match master (ev: azure.ActivityEvent)
            |> select { t: ev.time, c: ev.operation }
            |> window on t width 1m late ignore
            |> group by { c: c, windowStart: windowStart } aggregate { n: count() };
          emit entity Reading from warnOn identity reading(component: c) set { component: c, degraded: degraded };
          emit entity Part from acts identity part(key: concat("evt/", c)) set { key: concat("evt/", c), kind: "activity", name: c, own: n };
        }

        /// Two constant rows.
        projection values view forward output acme.estatedemo {
          in master: graph ops;
          retain 4;
          let rows = values { k: "a", n: 1 }, { k: "b", n: 2 };
          emit entity Part from rows identity part(key: k) set { key: k, kind: "constant", name: k, own: n };
        }
        """;

    static string Space(string text) => text
        .Replace("import acme.biz ", "import acme.estatedemo @ 1.0.0 sha256:… as demo;\nimport acme.biz ")
        .Replace("  graph workload = wl.workloadHealth(master: master, structure: workloadStructure, workloadNamespace: \"shop\");",
            "  graph workload = wl.workloadHealth(master: master, structure: workloadStructure, workloadNamespace: \"shop\");\n  graph demoClosure = demo.closure(st: workloadStructure);\n  graph demoBoth = demo.both(st: workload);\n  graph demoSeries = demo.series(master: master);\n  graph demoValues = demo.values(master: master);");

    /// <summary>The definitions with the estate's space binding, and with the demo projections and their graphs when `demo` is set.</summary>
    public static Catalog LoadCatalog(bool demo)
    {
        var files = Files(demo);
        var cat = new Catalog();
        var res = cat.Load(files, updatePins: true);
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
        return cat;
    }

    public static List<SourceFile> Files(bool demo)
    {
        var files = Catalog.ReadDirectory(Repo.Definitions).ToList();
        var spaceText = File.ReadAllText(Path.Combine(Estate, "space", "acme.space.sattle"));
        var i = files.FindIndex(f => f.Path.EndsWith("acme.space.sattle", StringComparison.Ordinal));
        files[i] = files[i] with { Text = demo ? Space(spaceText) : spaceText };
        if (demo) files.Add(new SourceFile("acme.estatedemo.sattle", Demo.Replace("import acme.ops    @ 1.0.0 sha256:… as ops;", "import acme.ops    @ 1.0.0 sha256:… as ops;\nimport sattle.infra @ 1.0.0 sha256:… as infra;\nimport sattle.azure @ 1.0.0 sha256:… as azure;")));
        return files;
    }

    public static string EstateDir => Estate;

    static Engine Run(bool demo, out Catalog cat)
    {
        cat = LoadCatalog(demo);
        var space = cat.Spaces.Keys.Single();
        var engine = new Engine(cat, space, new EngineOptions { Tenant = "lab", LateHorizonWindows = 3 });
        new Replay(engine, cat.Spaces[space], AdapterRegistry.Create("synth"), new ReplayOptions { ChunkRows = 50, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" }).Run(Estate);
        return engine;
    }

    static readonly Lazy<(Engine Engine, Catalog Cat)> Plain = new(() => { var e = Run(false, out var c); return (e, c); });
    static readonly Lazy<(Engine Engine, Catalog Cat)> WithDemo = new(() => { var e = Run(true, out var c); return (e, c); });

    [Theory]
    [InlineData("master")] [InlineData("serviceTree")] [InlineData("capabilities")] [InlineData("workloadStructure")] [InlineData("workload")] [InlineData("deploymentHealth")] [InlineData("deploymentReport")]
    public void Every_graph_of_the_space_has_entities(string graph)
    {
        var g = Plain.Value.Engine.Graph(graph).State;
        Assert.True(g.EntityCount > 0, $"{graph} is empty");
    }

    [Theory]
    [InlineData("workloadStructure", "Contains")] [InlineData("workloadStructure", "DependsOn")] [InlineData("workloadStructure", "Affects")] [InlineData("workloadStructure", "TalksTo")] [InlineData("workload", "Measures")] [InlineData("workload", "EvidenceFor")] [InlineData("serviceTree", "DependsOn")] [InlineData("capabilities", "DependsOn")]
    public void The_graphs_have_relationships_of_the_types_a_closure_walks(string graph, string type)
    {
        Assert.Contains(Plain.Value.Engine.Graph(graph).State.Rels, r => Ids.Name(r.Type) == type);
    }

    [Fact]
    public void A_Link_merges_identities_a_union_has_two_sources_and_the_retained_evidence_is_there()
    {
        var e = Plain.Value.Engine;
        Assert.True(e.Identities.Merges > 0, "no identity merged: the second api pod's row arrives after the flows that name it");
        Assert.Equal(0, e.Identities.Conflicts);
        var types = e.Master.State.Entities.GroupBy(x => Ids.Name(x.Type)).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(18, types["ActivityEvent"]);                       // 10 from the management API and 8 from the diagnostic setting: the two sources of the Union in observedActivityEvents
        Assert.True(types["WarningEvent"] > 0);
    }

    [Fact]
    public void Node_and_cluster_telemetry_has_windows_and_the_threshold_signals_exist()
    {
        var e = Plain.Value.Engine;
        var tel = ((InMemoryTelemetryStore)e.Telemetry).Dump(e.Master.Key);
        var props = tel.Select(x => e.Interner.Get(x.Prop)).Distinct().Select(p => p[(p.LastIndexOf(':') + 1)..]).ToHashSet();
        Assert.Contains("dropRate", props); Assert.Contains("forwardRate", props); Assert.Contains("cpuPercent", props);
        Assert.True(tel.Count(x => e.Interner.Get(x.Prop).EndsWith("prop:dropRate", StringComparison.Ordinal)) >= 8);     // several windows per node, so a Series has a history to walk
        var signals = e.Graph("workload").State.Entities.Where(x => Ids.Name(x.Type) == "ComponentSignal").ToList();
        Assert.True(signals.Count >= 5);
        Assert.Contains(signals, s => s.Props.Text("d:acme.workload/prop:signalState") == "nodata");             // at the end the storage account's meter has been silent for longer than its time to live (the thresholds that fire on the way are WorkloadTests)
    }

    [Fact]
    public void The_workload_roll_up_has_a_component_that_is_not_healthy_because_of_something_it_depends_on()
    {
        var e = Plain.Value.Engine;
        var states = e.Graph("workload").State.Entities.Select(c => c.Props.Text("d:acme.workload/prop:state")).Where(s => s is not null).ToList();
        Assert.Contains("Healthy", states);
        Assert.True(states.Any(s => s is "No data" or "Degraded" or "Unhealthy"), "every component is healthy or unknown: the roll-up shows nothing");      // at the end: the storage account in no data, and what depends on it
    }

    [Theory]
    [InlineData("demoClosure", 0, 1)] [InlineData("demoBoth", 10, 0)] [InlineData("demoSeries", 3, 0)] [InlineData("demoValues", 2, 0)]
    public void The_operator_shapes_of_the_canvas_walkthroughs_produce_output(string graph, int minEntities, int minRels)
    {
        var g = WithDemo.Value.Engine.Graph(graph).State;
        Assert.True(g.EntityCount >= minEntities, $"{graph}: {g.EntityCount} entities");
        Assert.True(g.RelCount >= minRels, $"{graph}: {g.RelCount} relationships");
    }

    [Fact]
    public void The_closure_is_larger_than_its_base_so_the_fixpoint_walked_more_than_one_step()
    {
        var e = WithDemo.Value.Engine;
        var baseEdges = e.Graph("workloadStructure").State.Rels.Count(r => Ids.Name(r.Type) == "Affects");
        Assert.True(e.Graph("demoClosure").State.RelCount > baseEdges, $"closure {e.Graph("demoClosure").State.RelCount} against {baseEdges} edges");
    }

    /// <summary>What a viewer needs to name and draw a graph is ontology data: the label of an entity type, the kind of a relationship type, and what the graph is in the words of its projection.</summary>
    [Fact]
    public void The_ontology_endpoint_serves_the_label_of_each_entity_type_and_the_kind_of_each_relationship_type_and_the_space_describes_each_graph()
    {
        using var h = new SpaceHost(Files(demo: false), new HostOptions { Cluster = "synth" });
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
        Assert.Null(h.Ontology("master")["relationshipTypes"]!.AsArray().Single(t => t!["id"]!["value"]!.GetValue<string>() == "NoType")!["kind"]);
        var graphs = h.SpaceInfo()["graphs"]!.AsArray().Where(g => g!["role"]!.GetValue<string>() != "catalog").ToList();
        Assert.All(graphs, g => Assert.False(string.IsNullOrWhiteSpace(g!["description"]?.GetValue<string>()), $"graph {g["id"]} has no description"));
        Assert.StartsWith("The service tree", graphs.Single(g => g!["id"]!.GetValue<string>() == "serviceTree")!["description"]!.GetValue<string>());
    }

    [Fact]
    public void The_ontology_endpoint_of_the_workload_graph_serves_value_roles_for_the_state_properties()
    {
        using var h = new SpaceHost(Files(demo: false), new HostOptions { Cluster = "synth" });
        var onto = h.Ontology("workload");
        string Roles(string property) => string.Join(",", onto["properties"]!.AsArray().Where(p => p!["id"]!["value"]!.GetValue<string>() == property).Select(p => p!["presentation"]).Single()!.AsArray()
            .Select(x => x!["value"]!["value"]!.GetValue<string>() + ":" + x["role"]!.GetValue<string>()).Order());
        var state = onto["properties"]!.AsArray().Select(p => p!["id"]!["value"]!.GetValue<string>()).Single(id => id.Split('.')[^1] == "state");
        Assert.Equal("Degraded:warning,Healthy:ok,No data:unknown,Unhealthy:danger,Unknown:unknown", Roles(state));
        var signalState = onto["properties"]!.AsArray().Select(p => p!["id"]!["value"]!.GetValue<string>()).Single(id => id.Split('.')[^1] == "signalState");
        Assert.Equal("nodata:unknown,off:ok,on:warning", Roles(signalState));
        // properties without a closed set of states carry none
        Assert.DoesNotContain(onto["properties"]!.AsArray(), p => p!["presentation"] is not null && p["id"]!["value"]!.GetValue<string>() is var id && id != state && id != signalState);
        // and the roll-up on the estate really holds states of more than one role
        var roles = Plain.Value.Engine.Graph("workload").State.Entities.Select(c => c.Props.Text("d:acme.workload/prop:state")).Where(s => s is not null).Distinct().ToList();
        Assert.True(roles.Count >= 2, string.Join(",", roles));
    }

    [Fact]
    public void A_preview_of_the_estate_shows_the_identity_merge_of_its_Link_so_a_walkthrough_can_assert_it()
    {
        using var h = new SpaceHost(Files(demo: false), new HostOptions { Cluster = "synth" });
        new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 40, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" }).Run(Estate);
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var p = h.Preview(d, new System.Text.Json.Nodes.JsonObject { ["graphs"] = new System.Text.Json.Nodes.JsonArray("serviceTree") });
        var merged = long.Parse(p["identity"]!["preview"]!["merged"]!.GetValue<string>());
        Assert.True(merged >= 1, "the second api pod's row arrives after the flows that name it: the Link of the pod identities merges");
        Assert.Equal(h.Engine.Identities.Merges, merged);
        Assert.Equal("0", p["identity"]!["difference"]!["merged"]!.GetValue<string>());
    }
}

