using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The health model (acme.health, acme.healthmodel): signals with thresholds, worst-of roll-up along containment and dependency edges, recovery by retraction. The model names one workload; the synthetic runs widen it to every namespace of the synthetic world.</summary>
public class HealthTests
{
    static SpaceHost Host(int windows)
    {
        var sources = Catalog.ReadDirectory(Repo.Definitions).Select(f => f.Path.EndsWith("acme.healthmodel.sattle", StringComparison.Ordinal) ? f with { Text = f.Text.Replace("namespace == workloadNamespace", "namespace != \"\"") } : f).ToList();
        var h = new SpaceHost(sources, new HostOptions { Cluster = "synth" });
        HostTests.Drive(h, windows);
        return h;
    }

    static Dictionary<string, (string? State, long? Severity)> States(SpaceHost h) =>
        h.Engine.Graph("health").State.Entities.Where(e => Ids.Name(e.Type) == "Component").ToDictionary(
            e => e.Props.Text("d:acme.health/prop:name")!, e => (e.Props.Text("d:acme.health/prop:state"), e.Props.Get("d:acme.health/prop:severity") is IntegerValue i ? (long?)(long)i.N : null));

    [Fact]
    public void A_degraded_dependency_rolls_up_through_dependency_and_containment_edges_and_recovers_by_retraction()
    {
        using var degraded = Host(3);
        var s = States(degraded);
        Assert.Equal("Unhealthy", s["svc-1"].State);                       // the callee carries the signal
        Assert.Equal("Unhealthy", s["ns1"].State);                         // its workload contains it
        Assert.Equal("Unhealthy", s["ns0"].State);                         // ns0 contains svc-0, which depends on svc-1: three edges away
        Assert.NotEqual("Unhealthy", s["ns2"].State);                     // svc-2 and its workload do not depend on svc-1
        using var recovered = Host(9);
        var r = States(recovered);
        Assert.All(r.Values, v => Assert.Contains(v.State, new[] { "Healthy", "Unknown" }));       // recovered, and what never had data stays Unknown
        Assert.Equal("Healthy", r["ns0"].State);
        Assert.Equal(s.Keys.Order(), r.Keys.Order());
    }

    [Fact]
    public void Health_graphs_conform_to_their_ontology_and_the_run_is_deterministic()
    {
        using var a = Host(9); using var b = Host(9);
        Assert.Equal(a.Engine.StateDigest(), b.Engine.StateDigest());
        var cat = a.Catalog;
        foreach (var g in a.Engine.Graphs.Where(g => g.Name is "health" or "healthTopology" or "healthSignals" or "healthState"))
            Assert.Empty(Conformance.Check(g.State, cat.OntologyOf("acme.health")));
    }

    [Trait("Category", "Capture")]
    [CaptureFact(overlay: true)]
    public void On_the_newest_real_capture_the_node_and_its_virtual_machine_are_one_component_and_every_component_has_a_state()
    {
        var c = Path.Combine(Repo.Root, "lab", "captures");
        var dir = Directory.Exists(c) ? Directory.EnumerateDirectories(c).Order(StringComparer.Ordinal).LastOrDefault() : null;
        Assert.NotNull(dir);
        var r = ReplayTests.Shared(dir, 2000, Repo.ClusterOf(dir));
        var comps = r.Engine.Graph("health").State.Entities.Where(e => Ids.Name(e.Type) == "Component").ToList();
        Assert.Contains(comps, e => e.Props.Text("d:acme.health/prop:kind") == "node" && e.Props.Text("d:acme.health/prop:azureType") is not null);   // Kubernetes node and virtual machine merged by the shared identity
        Assert.Equal(comps.Count, comps.Select(e => e.Props.Text("d:acme.health/prop:key")).Distinct().Count());
        Assert.Contains(comps, e => e.Props.Text("d:acme.health/prop:kind") == "workload");
        Assert.All(comps, e => Assert.NotNull(e.Props.Text("d:acme.health/prop:state")));
    }

    [Fact]
    public void A_health_state_traces_back_through_every_layer_to_the_service_tree_and_the_master()
    {
        using var h = Host(3);
        var workload = h.Engine.Graph("health").State.Entities.First(e => e.Props.Text("d:acme.health/prop:name") == "ns0");
        var graphs = new List<string> { "health" }; var element = workload.Id; var graph = "health";
        for (var i = 0; i < 12; i++)
        {
            var sources = h.Lineage(graph, element)["sources"]!.AsArray();
            if (sources.Count == 0) break;
            graph = sources[0]!["graph"]!.GetValue<string>(); element = sources[0]!["element"]!.GetValue<string>(); graphs.Add(graph);
        }
        Assert.Equal(["health", "healthState", "healthTopology", "serviceTree"], graphs.Take(4));
    }
}
