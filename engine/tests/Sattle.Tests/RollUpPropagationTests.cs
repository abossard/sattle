using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>A failure on a leaf of the model reaches what depends on it through the `Affects` edges, two levels up and into the workload that contains it, and nowhere else. On the estate the db pod (leaf of web to api to db) is the only element with warning events, so api, web and the workload are Degraded only by the roll-up.</summary>
public class RollUpPropagationTests
{
    static readonly Lazy<Engine> Estate = new(() => ReplayTests.Replay(EstateTests.EstateDir, cluster: "synth").Engine);

    static Dictionary<string, Entity> Components(string graph) =>
        Estate.Value.Graph(graph).State.Entities.Where(x => Ids.Name(x.Type) == "Component").ToDictionary(c => c.Props.Text("d:acme.health/prop:name")!);

    static string? Text(Entity c, string prop) => c.Props.TryGet("d:acme.health/prop:" + prop, out var v) ? v switch { TextValue t => t.S, IntegerValue i => i.N.ToString(), _ => v.ToString() } : null;

    [Theory]
    [InlineData("db", "own", "1", "Degraded")]            // the leaf: its own warning-events signal is on
    [InlineData("api", "own", null, "Degraded")]          // level one: no own failure (its signals are off or absent), Degraded because db affects it
    [InlineData("web", "own", null, "Degraded")]          // level two: through api
    [InlineData("shop", "own", null, "Degraded")]         // the workload that contains the three services
    [InlineData("node-a", "own", "0", "Healthy")]         // not on the path: nothing spreads to what does not depend on db
    [InlineData("aks-synth", "own", "0", "Healthy")]
    public void A_failure_on_the_leaf_turns_what_depends_on_it_Degraded_through_two_levels_and_nothing_else(string component, string _, string? ownSeverity, string state)
    {
        var c = Components("health")[component];
        Assert.Equal(state, Text(c, "state"));
        var own = Text(c, "own");
        if (ownSeverity is null) Assert.True(own is null or "0", $"{component} has its own severity {own}: its state is not propagated");
        else Assert.Equal(ownSeverity, own);
    }

    [Fact]
    public void The_propagation_path_is_the_chain_of_Affects_edges_from_the_workload_through_web_and_api_to_db()
    {
        var topo = Estate.Value.Graph("healthTopology").State;
        var byId = Components("healthTopology").ToDictionary(x => x.Value.Id, x => x.Key);
        var affects = topo.Rels.Where(r => Ids.Name(r.Type) == "Affects").Select(r => byId[r.Source] + " -> " + byId[r.Target]).ToHashSet();
        Assert.Contains("web -> api", affects);
        Assert.Contains("api -> db", affects);
        Assert.Contains("shop -> web", affects);
    }

    [Fact]
    public void The_warning_events_are_on_the_db_pod_only_so_the_leaf_signal_is_the_only_cause()
    {
        var signals = Estate.Value.Graph("health").State.Entities.Where(x => Ids.Name(x.Type) == "Signal" && x.Props.Text("d:acme.health/prop:degraded") == "on").Select(x => x.Props.Text("d:acme.health/prop:component")).ToList();
        Assert.Equal(["svc/synth/shop/db"], signals);
    }
}
