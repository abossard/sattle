using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Unit readiness as definitions only (acme.readiness): the state of a Unit follows its ready instances by patches in both directions.</summary>
public class ReadinessTests
{
    const long Min = 60_000_000_000;
    static string Fixture => Path.Combine(Repo.Root, "engine", "tests", "Sattle.Tests", "fixtures", "deploy-health");

    sealed record Timeline(Dictionary<string, List<string>> Nodes, Dictionary<string, List<string>> Edges, Engine Engine, Catalog Catalog);

    /// <summary>Replays the fixture one record per chunk and folds the patches of the readiness graph into the sequence of states each node and edge went through.</summary>
    static Timeline Replay(int minReady)
    {
        var files = Catalog.ReadDirectory(Repo.Definitions).Select(f => f.Path.EndsWith("acme.space.module.gkql.json", StringComparison.Ordinal) ? f with { Text = WithMinReady(f.Text, minReady) } : f).ToList();
        var cat = new Catalog(Repo.Projections);
        var res = cat.Load(files, updatePins: true);
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
        var run = ReplayTests.Replay(Fixture, 1, cat: cat);
        var nodes = new Dictionary<string, List<string>>(); var edges = new Dictionary<string, List<string>>();
        var names = new Dictionary<string, string>();
        foreach (var p in run.Patches("readiness"))
            foreach (var e in p["edits"]!.AsArray())
            {
                if (e!["after"] is not { } after) continue;
                if (e["kind"]!.GetValue<string>() == "entity") { var name = after.Prop("name")!; names[after["id"].Id()] = name; Step(nodes, name, after.Prop("state")); }
                else Step(edges, names.GetValueOrDefault(after["source"].Id(), after["source"].Id()) + ">" + names.GetValueOrDefault(after["target"].Id(), after["target"].Id()), after.Prop("state"));
            }
        return new Timeline(nodes, edges, run.Engine, cat);
    }

    /// <summary>The space document with the argument `minReady` bound on the graph `readiness`.</summary>
    static string WithMinReady(string space, int minReady)
    {
        var doc = JsonNode.Parse(space)!;
        doc["space"]!["graphs"]!.AsArray().Single(g => g!["name"]!.GetValue<string>() == "readiness")!["inputs"]!["minReady"] = new JsonObject { ["lit"] = minReady };
        return doc.ToJsonString();
    }

    static void Step(Dictionary<string, List<string>> d, string key, string? state)
    {
        if (!d.TryGetValue(key, out var l)) d[key] = l = [];
        var s = state ?? "-";
        if (l.Count == 0 || l[^1] != s) l.Add(s);
    }

    [Theory]
    [InlineData(0, "NotReady,Ready,NotReady,Ready", "NotReady,Ready,NotReady,Ready", "Ready,NotReady,Ready")]   // no ready instance seen yet, then ready, then zero ready instances, then recovered; the edge exists once the flows are seen and follows the worse end
    [InlineData(1, "NotReady,Ready,NotReady", "NotReady", "NotReady")]                                 // a higher bound: web is NotReady once one instance is left, db never exceeds one ready instance
    public void A_units_state_follows_its_ready_instances_by_patches_in_both_directions(int minReady, string web, string db, string edge)
    {
        var t = Replay(minReady);
        Scenarios.AssertPinned(minReady == 0 ? "deploy-health" : "deploy-health minReady=" + minReady, t.Engine.StateDigest());          // 0 is the parameter's default: the state of the scenario itself
        Assert.Equal(web, string.Join(",", Clean(t.Nodes["web"])));
        Assert.Equal(db, string.Join(",", Clean(t.Nodes["db"])));
        Assert.Equal(edge, string.Join(",", Clean(t.Edges["web>db"])));
    }

    /// <summary>The creation of an element may first carry no state (the update arrives in the same commit or the next one); only real states count.</summary>
    static IEnumerable<string> Clean(List<string> l) => l.Where(x => x != "-");
}
