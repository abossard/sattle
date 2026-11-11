using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Deployment health as definitions only (acme.deployhealth): the state of a Deployment follows its ready pods by patches in both directions, and the ontology carries the value roles a client colors by.</summary>
public class DeployHealthTests
{
    const long Min = 60_000_000_000;
    static string Fixture => Path.Combine(Repo.Root, "engine", "tests", "Sattle.Tests", "fixtures", "deploy-health");

    sealed record Timeline(Dictionary<string, List<string>> Nodes, Dictionary<string, List<string>> Edges, Engine Engine, Catalog Catalog);

    /// <summary>Replays the fixture one record per chunk and folds the patches of the health graph into the sequence of states each node and edge went through.</summary>
    static Timeline Replay(int minReady)
    {
        var files = Catalog.ReadDirectory(Repo.Definitions).Select(f => f.Path.EndsWith("acme.space.sattle", StringComparison.Ordinal) ? f with { Text = f.Text.Replace("dh.deploymentHealth(master: master)", $"dh.deploymentHealth(master: master, minReady: {minReady})") } : f).ToList();
        var cat = new Catalog();
        var res = cat.Load(files, updatePins: true);
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
        var space = cat.Spaces.Keys.Single();
        var engine = new Engine(cat, space);
        var export = new CanvasExport(engine, cat); export.Attach();
        new Replay(engine, cat.Spaces[space], AdapterRegistry.Create("synth"), new ReplayOptions { ChunkRows = 1, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" }).Run(Fixture);
        var nodes = new Dictionary<string, List<string>>(); var edges = new Dictionary<string, List<string>>();
        var names = new Dictionary<string, string>();
        foreach (var p in export.PatchesOf("deploymentHealth"))
            foreach (var e in p.Edits)
            {
                if (e.After is Entity en) { var name = en.Props.Text("d:acme.deployhealth/prop:name")!; names[en.Id] = name; Step(nodes, name, en.Props.Text("d:acme.deployhealth/prop:state")); }
                else if (e.After is Rel r) Step(edges, names.GetValueOrDefault(r.Source, r.Source) + ">" + names.GetValueOrDefault(r.Target, r.Target), r.Props.Text("d:acme.deployhealth/prop:state"));
            }
        return new Timeline(nodes, edges, engine, cat);
    }

    static void Step(Dictionary<string, List<string>> d, string key, string? state)
    {
        if (!d.TryGetValue(key, out var l)) d[key] = l = [];
        var s = state ?? "-";
        if (l.Count == 0 || l[^1] != s) l.Add(s);
    }

    [Theory]
    [InlineData(0, "Unhealthy,Healthy,Unhealthy,Healthy", "Unhealthy,Healthy,Unhealthy,Healthy", "Healthy,Unhealthy,Healthy")]   // no ready pod seen yet, then ready, then zero ready pods, then recovered; the edge exists once the flows are seen and follows the worse end
    [InlineData(1, "Unhealthy,Healthy,Unhealthy", "Unhealthy", "Unhealthy")]                                 // a higher bound: web is Unhealthy once one pod is left, db never exceeds one ready pod
    public void A_deployments_state_follows_its_ready_pods_by_patches_in_both_directions(int minReady, string web, string db, string edge)
    {
        var t = Replay(minReady);
        Assert.Equal(web, string.Join(",", Clean(t.Nodes["web"])));
        Assert.Equal(db, string.Join(",", Clean(t.Nodes["db"])));
        Assert.Equal(edge, string.Join(",", Clean(t.Edges["web>db"])));
    }

    /// <summary>The creation of an element may first carry no state (the update arrives in the same commit or the next one); only real states count.</summary>
    static IEnumerable<string> Clean(List<string> l) => l.Where(x => x != "-");

    [Fact]
    public void Nothing_about_health_is_in_the_engine_the_state_property_declares_value_roles_the_ontology_serves()
    {
        var t = Replay(0);
        var onto = new CanvasExport(t.Engine, t.Catalog).Ontology(t.Engine.Graph("deploymentHealth"));
        var state = onto["properties"]!.AsArray().Where(p => p!["id"]!["value"]!.GetValue<string>() == "acme.deployhealth.state").ToList();
        Assert.NotEmpty(state);
        Assert.All(state, p => Assert.Equal(["Healthy:ok", "Unhealthy:danger"], p!["presentation"]!.AsArray().Select(x => x!["value"]!["value"]!.GetValue<string>() + ":" + x["role"]!.GetValue<string>()).Order()));
        Assert.All(onto["properties"]!.AsArray().Where(p => p!["id"]!["value"]!.GetValue<string>() == "acme.deployhealth.name"), p => Assert.Null(p!["presentation"]));
    }

    [Theory]
    [InlineData("""state: Text roles { "a": ok, 1: warning, 2.5: danger, true: unknown, "z": neutral };""", true)]
    [InlineData("""state: Text roles { "a": fine };""", false)]
    [InlineData("""state: Text roles { };""", false)]
    public void Value_roles_parse_print_and_round_trip_and_reject_unknown_roles(string member, bool valid)
    {
        var text = $"module t.roles @ 1.0.0;\nentity N {{ {member} }}\n";
        var cat = new Catalog();
        var res = cat.Load([new SourceFile("t.roles.sattle", text)], updatePins: true);
        Assert.Equal(valid, !res.HasErrors);
        if (!valid) return;
        var printed = Printer.Print(cat.Graph, "t.roles");
        Assert.Contains("roles { \"a\": ok, \"z\": neutral, true: unknown, 2.5: danger, 1: warning }", printed.Replace("  ", " ").Replace("\n", " "));
        var again = new Catalog();
        Assert.False(again.Load([new SourceFile("t.roles.sattle", printed)], updatePins: true).HasErrors);
        Assert.Equal(cat.Modules["t.roles"].Digest, again.Modules["t.roles"].Digest);
    }
}
