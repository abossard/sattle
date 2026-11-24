using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The synthetic capture `estate` (tests/Sattle.Tests/fixtures/estate, record envelopes, invented names only) has data for every operator of the projection language: instances, services and the calls between them for a closure, a Link that merges, a Union of two sources, and host and resource telemetry for Series, Window, Threshold and Rollup.</summary>
public class EstateTests
{
    const long Min = 60_000_000_000;
    static string Estate => Path.Combine(Repo.Root, "engine", "tests", "Sattle.Tests", "fixtures", "estate");

    /// <summary>The ontology of the demo projections (`DemoProjections`): the shapes the canvas walkthroughs build over the master of the estate.</summary>
    const string Demo = """
        {"module": "acme.estatedemo", "version": "1.0.0",
         "imports": [{"module": "acme.ops", "alias": "ops", "version": "1.0.0", "digest": null}],
         "entities": [{"name": "Part", "properties": [{"name": "key", "type": "Text"}, {"name": "kind", "type": "Text", "optional": true}, {"name": "name", "type": "Text", "label": true}, {"name": "own", "type": "Integer", "optional": true}]},
                      {"name": "Reading", "properties": [{"name": "component", "type": "Text", "label": true}, {"name": "degraded", "type": "Text", "optional": true}]}],
         "relationships": [{"name": "Reaches", "from": "Part", "to": "Part", "kind": "dependency"}],
         "identities": [{"name": "part", "type": "Part", "components": [{"name": "key", "type": "Text"}]},
                        {"name": "reading", "type": "Reading", "components": [{"name": "component", "type": "Text"}]}]}
        """;

    /// <summary>The projections of the demo module, one program each.</summary>
    static readonly SourceFile[] DemoProjections =
    [
        new("acme.estatedemo.closure.gkql.json", """
            {"graph": "closure", "ontology": "acme.estatedemo", "history": 4,
             "description": "What does each part reach, directly or through others? The closure of the edges of the parts.",
             "inputs": [{"name": "st", "ontology": "acme.estatedemo"}], "parameters": [],
             "verbs": [
               {"id": "parts", "verb": "graph-match", "graph": "st", "pattern": {"nodes": [{"var": "c", "label": "Part"}]}},
               {"id": "part", "verb": "nodes", "target": {"nodes": [{"var": "n", "label": "Part", "id": {"identity": "part", "args": [{"ref": "c.key"}]}, "props": {"key": {"ref": "c.key"}, "name": {"ref": "c.name"}}}]}},
               {"id": "reach", "verb": "traverse", "graph": "st", "traverse": {"edge": "Reaches", "from": "c", "to": "d", "direction": "out", "min": 1, "max": 9}},
               {"id": "ends", "verb": "project", "columns": {"ka": {"ref": "c.key"}, "kb": {"ref": "d.key"}}},
               {"id": "reaches", "verb": "edges", "target": {
                 "nodes": [{"var": "a", "label": "Part", "id": {"identity": "part", "args": [{"ref": "ka"}]}}, {"var": "b", "label": "Part", "id": {"identity": "part", "args": [{"ref": "kb"}]}}],
                 "edges": [{"var": "e", "label": "Reaches", "from": "a", "to": "b", "key": "reach"}]}}],
             "wires": [{"from": "parts", "to": "part"}, {"from": "parts", "to": "reach"}, {"from": "reach", "to": "ends"}, {"from": "ends", "to": "reaches"}]}
            """),
        new("acme.estatedemo.both.gkql.json", """
            {"graph": "both", "ontology": "acme.estatedemo", "history": 4,
             "description": "Every instance and every service once, and whom each reaches: an instance the instances it calls and the service it backs. Unions and distincts.",
             "inputs": [{"name": "master", "ontology": "acme.ops"}], "parameters": [],
             "verbs": [
               {"id": "instances", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "i", "label": "Instance"}]}},
               {"id": "instanceParts", "verb": "project", "columns": {"k": {"ref": "i.name"}, "kd": {"lit": "instance"}, "nm": {"ref": "i.name"}}},
               {"id": "services", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "s", "label": "Service"}]}},
               {"id": "serviceParts", "verb": "project", "columns": {"k": {"ref": "s.name"}, "kd": {"lit": "service"}, "nm": {"ref": "s.name"}}},
               {"id": "backs", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "i", "label": "Instance"}, {"var": "s", "label": "Service"}], "edges": [{"var": "b", "label": "Backs", "from": "i", "to": "s"}]}},
               {"id": "backingParts", "verb": "project", "columns": {"k": {"ref": "i.name"}, "kd": {"lit": "instance"}, "nm": {"ref": "i.name"}}},
               {"id": "allParts", "verb": "union"},
               {"id": "partsOnce", "verb": "distinct"},
               {"id": "part", "verb": "nodes", "target": {"nodes": [{"var": "n", "label": "Part", "id": {"identity": "part", "args": [{"ref": "k"}]}, "props": {"key": {"ref": "k"}, "kind": {"ref": "kd"}, "name": {"ref": "nm"}}}]}},
               {"id": "calls", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "x", "label": "Instance"}, {"var": "y", "label": "Instance"}], "edges": [{"var": "c", "label": "Calls", "from": "x", "to": "y"}]}},
               {"id": "callEnds", "verb": "project", "columns": {"ka": {"ref": "x.name"}, "kb": {"ref": "y.name"}}},
               {"id": "backEnds", "verb": "project", "columns": {"ka": {"ref": "i.name"}, "kb": {"ref": "s.name"}}},
               {"id": "allEnds", "verb": "union"},
               {"id": "endsOnce", "verb": "distinct"},
               {"id": "reaches", "verb": "edges", "target": {
                 "nodes": [{"var": "a", "label": "Part", "id": {"identity": "part", "args": [{"ref": "ka"}]}}, {"var": "b", "label": "Part", "id": {"identity": "part", "args": [{"ref": "kb"}]}}],
                 "edges": [{"var": "e", "label": "Reaches", "from": "a", "to": "b", "key": "reach"}]}}],
             "wires": [{"from": "instances", "to": "instanceParts"}, {"from": "services", "to": "serviceParts"}, {"from": "backs", "to": "backingParts"},
                       {"from": "instanceParts", "to": "allParts"}, {"from": "serviceParts", "to": "allParts"}, {"from": "backingParts", "to": "allParts"},
                       {"from": "allParts", "to": "partsOnce"}, {"from": "partsOnce", "to": "part"},
                       {"from": "calls", "to": "callEnds"}, {"from": "backs", "to": "backEnds"}, {"from": "callEnds", "to": "allEnds"}, {"from": "backEnds", "to": "allEnds"},
                       {"from": "allEnds", "to": "endsOnce"}, {"from": "endsOnce", "to": "reaches"}]}
            """),
        new("acme.estatedemo.series.gkql.json", """
            {"graph": "series", "ontology": "acme.estatedemo", "history": 4,
             "description": "The drop rate of the hosts over time, and the operations of the activity log.",
             "inputs": [{"name": "master", "ontology": "acme.ops"}], "parameters": [],
             "verbs": [
               {"id": "hosts", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "nd", "label": "Host"}]}},
               {"id": "named", "verb": "project", "columns": {"nd": {"ref": "nd"}, "c": {"ref": "nd.name"}}},
               {"id": "drops", "verb": "series", "graph": "master", "series": {"element": "nd", "property": "dropRate", "lookback": "24h"}},
               {"id": "warn", "verb": "summarize", "by": {"c": {"ref": "c"}, "windowStart": {"ref": "windowStart"}},
                "aggregates": {"m": {"fn": "avg", "args": [{"ref": "dropRate"}]}, "n": {"fn": "count"}, "s": {"fn": "sum", "args": [{"ref": "dropRate"}]}, "v": {"fn": "max", "args": [{"ref": "dropRate"}]}}},
               {"id": "warnOn", "verb": "hysteresis", "hysteresis": {"by": ["c"], "value": "v", "above": 0.01, "enterAfter": 2, "below": 0.005, "exitAfter": 2, "high": "on", "low": "off", "initial": "off"}},
               {"id": "reading", "verb": "nodes", "target": {"nodes": [{"var": "r", "label": "Reading", "id": {"identity": "reading", "args": [{"ref": "c"}]}, "props": {"component": {"ref": "c"}, "degraded": {"ref": "state"}}}]}},
               {"id": "events", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "ev", "label": "ActivityEvent"}]}},
               {"id": "operations", "verb": "project", "columns": {"c": {"ref": "ev.operation"}}},
               {"id": "acts", "verb": "summarize", "by": {"c": {"ref": "c"}}, "aggregates": {"n": {"fn": "count"}}},
               {"id": "act", "verb": "nodes", "target": {"nodes": [{"var": "p", "label": "Part", "id": {"identity": "part", "args": [{"fn": "concat", "args": [{"lit": "evt/"}, {"ref": "c"}]}]},
                 "props": {"key": {"fn": "concat", "args": [{"lit": "evt/"}, {"ref": "c"}]}, "kind": {"lit": "activity"}, "name": {"ref": "c"}, "own": {"ref": "n"}}}]}}],
             "wires": [{"from": "hosts", "to": "named"}, {"from": "named", "to": "drops"}, {"from": "drops", "to": "warn"}, {"from": "warn", "to": "warnOn"}, {"from": "warnOn", "to": "reading"},
                       {"from": "events", "to": "operations"}, {"from": "operations", "to": "acts"}, {"from": "acts", "to": "act"}]}
            """),
        new("acme.estatedemo.values.gkql.json", """
            {"graph": "values", "ontology": "acme.estatedemo", "history": 4,
             "description": "Two constant rows.",
             "inputs": [{"name": "master", "ontology": "acme.ops"}], "parameters": [],
             "verbs": [
               {"id": "constants", "verb": "values", "values": {"columns": {"k": "Text", "n": "Integer"}, "rows": [{"k": "a", "n": 1}, {"k": "b", "n": 2}]}},
               {"id": "part", "verb": "nodes", "target": {"nodes": [{"var": "p", "label": "Part", "id": {"identity": "part", "args": [{"ref": "k"}]}, "props": {"key": {"ref": "k"}, "kind": {"lit": "constant"}, "name": {"ref": "k"}, "own": {"ref": "n"}}}]}}],
             "wires": [{"from": "constants", "to": "part"}]}
            """),
    ];

    /// <summary>The space document with the demo module imported and its four graphs bound.</summary>
    static string Space(string text)
    {
        var space = JsonNode.Parse(text)!;
        space["imports"]!.AsArray().Add(new JsonObject { ["module"] = "acme.estatedemo", ["alias"] = "demo", ["version"] = "1.0.0", ["digest"] = null });
        JsonObject Graph(string name, string projection, string input, string from) =>
            new() { ["name"] = name, ["projection"] = projection, ["inputs"] = new JsonObject { [input] = from } };
        var graphs = space["space"]!["graphs"]!.AsArray();
        graphs.Add(Graph("demoBoth", "demo.both", "master", "master"));
        graphs.Add(Graph("demoClosure", "demo.closure", "st", "demoBoth"));
        graphs.Add(Graph("demoSeries", "demo.series", "master", "master"));
        graphs.Add(Graph("demoValues", "demo.values", "master", "master"));
        return space.ToJsonString();
    }

    /// <summary>The definitions, and with the demo projections and their graphs when `demo` is set.</summary>
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
        var i = files.FindIndex(f => f.Path.EndsWith("acme.space.module.gkql.json", StringComparison.Ordinal));
        files[i] = files[i] with { Text = Space(files[i].Text) };
        files.AddRange([new SourceFile("acme.estatedemo.module.gkql.json", Demo), .. DemoProjections]);
        return files;
    }

    public static string EstateDir => Estate;

    /// <summary>The estate as the tests read it: the scenario run the assembly shares, and one more replay with the demo module. The tests read the graphs as they are served.</summary>
    static readonly Lazy<ReplayTests.Run> Plain = new(() => Scenarios.Run(Repo.Estate));
    static readonly Lazy<ReplayTests.Run> WithDemo = new(() => ReplayTests.Replay(Estate, 50, o: new EngineOptions { Tenant = "lab", LateHorizonWindows = 3 }, cat: LoadCatalog(demo: true)));

    [Theory]
    [InlineData("master")] [InlineData("serviceTree")] [InlineData("capabilities")] [InlineData("readiness")] [InlineData("readinessReport")]
    public void Every_graph_of_the_space_has_entities(string graph)
    {
        Assert.True(Plain.Value.Entities(graph).Count > 0, $"{graph} is empty");
    }

    [Theory]
    [InlineData("serviceTree", "DependsOn")] [InlineData("capabilities", "DependsOn")]
    public void The_graphs_have_relationships_of_the_types_a_closure_walks(string graph, string type)
    {
        Assert.NotEmpty(Plain.Value.Relationships(graph, type));
    }

    [Fact]
    public void A_Link_merges_identities_a_union_has_two_sources_and_the_retained_evidence_is_there()
    {
        var e = Plain.Value;
        Assert.True(e.Engine.Identities.Merges > 0, "no identity merged: the second api instance's row arrives after the flows that name it");          // the counters of identities are served by no file of a replay: read inside
        Assert.Equal(0, e.Engine.Identities.Conflicts);
        Assert.Equal(18, e.Entities("master", "ActivityEvent").Count);  // 10 from the management API and 8 from the diagnostic setting: the two sources of the Union in observedActivityEvents
        Assert.NotEmpty(e.Entities("master", "WarningEvent"));
    }

    [Fact]
    public void Host_and_resource_telemetry_has_windows()
    {
        var e = Plain.Value;
        Assert.All(new[] { "dropRate", "forwardRate", "cpuPercent" }, p => Assert.NotEmpty(e.Windows("master", p)));
        Assert.True(e.Windows("master", "dropRate").Count >= 8);     // several windows per host, so a Series has a history to walk
    }

    [Theory]
    [InlineData("demoClosure", 0, 1)] [InlineData("demoBoth", 8, 1)] [InlineData("demoSeries", 3, 0)] [InlineData("demoValues", 2, 0)]
    public void The_operator_shapes_of_the_canvas_walkthroughs_produce_output(string graph, int minEntities, int minRels)
    {
        var (entities, relationships) = (WithDemo.Value.Entities(graph).Count, WithDemo.Value.Relationships(graph).Count);
        Assert.True(entities >= minEntities, $"{graph}: {entities} entities");
        Assert.True(relationships >= minRels, $"{graph}: {relationships} relationships");
    }

    /// <summary>A web instance reaches the api instance it calls in one step and the api service in two: the closure has more edges than its base.</summary>
    [Fact]
    public void The_closure_is_larger_than_its_base_so_the_traversal_walked_more_than_one_step()
    {
        var e = WithDemo.Value;
        var (baseEdges, closure) = (e.Relationships("demoBoth", "Reaches").Count, e.Relationships("demoClosure").Count);
        Assert.True(closure > baseEdges, $"closure {closure} against {baseEdges} edges");
    }

    /// <summary>What a viewer needs to name and draw a graph is ontology data: the label of an entity type, the kind of a relationship type, and what the graph is in the words of its projection.</summary>
    [Fact]
    public void The_ontology_endpoint_serves_the_label_of_each_entity_type_and_the_kind_of_each_relationship_type_and_the_space_describes_each_graph()
    {
        using var h = new SpaceHost(Files(demo: false), new HostOptions { Projections = Repo.Projections});
        var onto = h.Ontology("master");
        string? Of(string list, string type, string field, bool id) => onto[list]!.AsArray().Single(t => t!["id"]!["value"]!.GetValue<string>().Split('.')[^1] == type)![field] is { } v ? (id ? v["value"] : v)!.GetValue<string>() : null;
        Assert.Equal("name", Of("entityTypes", "Instance", "label", true)!.Split('.')[^1]);
        Assert.Equal("address", Of("entityTypes", "Destination", "label", true)!.Split('.')[^1]);
        Assert.Equal(("communication", "composition", "support"), (Of("relationshipTypes", "Calls", "kind", false), Of("relationshipTypes", "RunsOn", "kind", false), Of("relationshipTypes", "About", "kind", false)));
        Assert.Null(onto["relationshipTypes"]!.AsArray().Single(t => t!["id"]!["value"]!.GetValue<string>() == "NoType")!["kind"]);
        var tree = h.Ontology("serviceTree");
        var contains = tree["relationshipTypes"]!.AsArray().Single(t => t!["id"]!["value"]!.GetValue<string>().Split('.')[^1] == "Contains")!;
        Assert.Equal("composition", contains["kind"]!.GetValue<string>());
        var graphs = h.SpaceInfo()["graphs"]!.AsArray().Where(g => g!["role"]!.GetValue<string>() != "catalog").ToList();
        Assert.All(graphs, g => Assert.False(string.IsNullOrWhiteSpace(g!["description"]?.GetValue<string>()), $"graph {g["id"]} has no description"));
        Assert.StartsWith("The service tree", graphs.Single(g => g!["id"]!.GetValue<string>() == "serviceTree")!["description"]!.GetValue<string>());
    }

    static readonly Lazy<SpaceHost> Examples = new(HostTests.NewHost);

    /// <summary>PROTOCOL 3.2: no domain state is in the engine. A property with a closed set of states declares a value role for each state, and the ontology endpoint of every graph that carries the property serves them; a property without such a set carries none.</summary>
    [Theory]
    [InlineData("readiness", "state", "NotReady:danger,Ready:ok")]
    public void The_ontology_endpoint_serves_the_value_roles_of_a_state_property(string graph, string property, string roles)
    {
        var properties = Examples.Value.Ontology(graph)["properties"]!.AsArray();
        static string Id(JsonNode? p) => p!["id"]!["value"]!.GetValue<string>();
        var served = properties.Single(p => Id(p) == property || !property.Contains('.') && Id(p).Split('.')[^1] == property)!;
        Assert.Equal(roles, string.Join(",", served["presentation"]!.AsArray().Select(x => x!["value"]!["value"]!.GetValue<string>() + ":" + x["role"]!.GetValue<string>()).Order()));
        Assert.All(properties.Where(p => Id(p).Split('.')[^1] == "name"), p => Assert.Null(p!["presentation"]));          // a property without a closed set of states carries none
    }

    [Fact]
    public void A_preview_of_the_estate_shows_the_identity_merge_of_its_Link_so_a_walkthrough_can_assert_it()
    {
        using var h = new SpaceHost(Files(demo: false), new HostOptions { Projections = Repo.Projections});
        new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 40, AdvanceEveryNs = Min, LatenessNs = Min}).Run(Estate);
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var p = h.Preview(d, new System.Text.Json.Nodes.JsonObject { ["graphs"] = new System.Text.Json.Nodes.JsonArray("serviceTree") });
        var merged = long.Parse(p["identity"]!["preview"]!["merged"]!.GetValue<string>());
        Assert.True(merged >= 1, "the second api instance's row arrives after the flows that name it: the Link of the instance identities merges");
        Assert.Equal(h.Engine.Identities.Merges, merged);
        Assert.Equal("0", p["identity"]!["difference"]!["merged"]!.GetValue<string>());
    }
}

