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
        var cat = new Catalog(Repo.Projections);
        var res = cat.Load(files, updatePins: true);
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
        var run = ReplayTests.Replay(Fixture, 1, "synth", cat: cat);
        var nodes = new Dictionary<string, List<string>>(); var edges = new Dictionary<string, List<string>>();
        var names = new Dictionary<string, string>();
        foreach (var p in run.Patches("deploymentHealth"))
            foreach (var e in p["edits"]!.AsArray())
            {
                if (e!["after"] is not { } after) continue;
                if (e["kind"]!.GetValue<string>() == "entity") { var name = after.Prop("name")!; names[after["id"].Id()] = name; Step(nodes, name, after.Prop("state")); }
                else Step(edges, names.GetValueOrDefault(after["source"].Id(), after["source"].Id()) + ">" + names.GetValueOrDefault(after["target"].Id(), after["target"].Id()), after.Prop("state"));
            }
        return new Timeline(nodes, edges, run.Engine, cat);
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
        Scenarios.AssertPinned(minReady == 0 ? "deploy-health" : "deploy-health minReady=" + minReady, t.Engine.StateDigest());          // 0 is the parameter's default: the state of the scenario itself
        Assert.Equal(web, string.Join(",", Clean(t.Nodes["web"])));
        Assert.Equal(db, string.Join(",", Clean(t.Nodes["db"])));
        Assert.Equal(edge, string.Join(",", Clean(t.Edges["web>db"])));
    }

    /// <summary>The creation of an element may first carry no state (the update arrives in the same commit or the next one); only real states count.</summary>
    static IEnumerable<string> Clean(List<string> l) => l.Where(x => x != "-");

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
