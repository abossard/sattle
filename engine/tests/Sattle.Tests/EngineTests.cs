using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Step 4: activation and evaluation of the example space on synthetic rows.</summary>
public class EngineTests
{
    const long Min = 60_000_000_000;
    static readonly long T0 = 1_794_300_000_000_000_000 / Min * Min; // a window boundary

    public static Engine NewEngine(EngineOptions? o = null)
    {
        var cat = Repo.LoadExamples(out var res);
        Assert.Empty(res.Diagnostics);
        return new Engine(cat, cat.Spaces.Keys.Single(), o);
    }

    static int Count(Graph g, string typeName) => g.Entities.Count(e => Ids.Name(e.Type) == typeName) + g.Rels.Count(r => Ids.Name(r.Type) == typeName);

    /// <summary>3 services of 2 pods each, flows between two of them.</summary>
    static void Scenario(Engine e, int windows = 5, int dropEvery = 0)
    {
        var w = new SyntheticWorld(e, 6, 3);
        w.LoadStructure();
        var it = e.Interner;
        var rw = new RowWriter(e.RowLayout("flows"), it);
        // pod-0 (svc-0, ns0) -> pod-1 (svc-1, ns1): 10 flows per window, pod-1 -> pod-2 (svc-2, ns2): 5 per window
        for (var win = 0; win < windows; win++)
            for (var i = 0; i < 15; i++)
            {
                var a = i < 10 ? 0 : 1; var b = i < 10 ? 1 : 2;
                rw.Begin();
                rw.Instant("time", T0 + win * Min + i * 1_000_000_000L); rw.Text("cluster", "synth");
                rw.Text("verdict", dropEvery > 0 && i % dropEvery == 0 ? "DROPPED" : "FORWARDED");
                rw.Text("srcNamespace", $"ns{a % 3}"); rw.Text("srcPod", $"pod-{a}"); rw.Text("dstNamespace", $"ns{b % 3}"); rw.Text("dstPod", $"pod-{b}");
            }
        e.Ingest("flows", rw.Build()!, 10);
        e.Advance(T0 + windows * Min + 1);
    }

    [Fact]
    public void Structure_is_ingested_into_the_master_graph()
    {
        using var e = NewEngine();
        new SyntheticWorld(e, 6, 3).LoadStructure();
        var m = e.Master.State;
        Assert.Equal(6, Count(m, "Pod")); Assert.Equal(3, Count(m, "Service")); Assert.Equal(6, Count(m, "Backs"));
        Assert.Equal(3, e.Master.Head);
        Assert.Empty(Conformance.Check(m, OntologyView.Build(Repo.LoadExamples(out _).Graph, ["acme.ops", "sattle.infra", "sattle.kernel"])));
    }

    [Fact]
    public void Flows_become_Calls_relationships_with_closed_window_telemetry_and_no_revision_per_sample()
    {
        using var e = NewEngine();
        var w = new SyntheticWorld(e, 6, 3); w.LoadStructure();
        var head = e.Master.Head;
        Scenario2(e);
        var m = e.Master.State;
        Assert.Equal(2, Count(m, "Calls"));
        Assert.Equal(head + 1, e.Master.Head);                     // one structural commit for the new relationships, none per window
        var dump = ((InMemoryTelemetryStore)e.Telemetry).Dump(e.Master.Key);
        Assert.Equal(2 * 5 * 2, dump.Count);                       // 2 relationships x 5 windows x (flows, drops)
        var flows = dump.Where(x => e.Interner.Get(x.Prop).EndsWith("prop:flows")).ToList();
        Assert.Equal(5 * 10 + 5 * 5, flows.Sum(x => (int)x.Value));
    }

    static void Scenario2(Engine e) => Scenario(e);

    [Fact]
    public void Service_dependency_tree_is_derived_with_rolled_up_telemetry()
    {
        using var e = NewEngine();
        Scenario(e, dropEvery: 5);
        var t = e.Graph("serviceTree");
        Assert.Equal(3, Count(t.State, "ServiceNode"));
        Assert.Equal(3, Count(t.State, "ServiceGroup"));
        Assert.Equal(3, Count(t.State, "Contains"));
        Assert.Equal(2, Count(t.State, "DependsOn"));
        var dump = ((InMemoryTelemetryStore)e.Telemetry).Dump(t.Key);
        string P(int p) => e.Interner.Get(p);
        var depFlows = dump.Where(x => P(x.Prop).EndsWith("prop:flows") && e.Interner.Get(x.Element).StartsWith("r:")).Sum(x => x.Value);
        Assert.Equal(5 * 15, depFlows);
        var drops = dump.Where(x => P(x.Prop).EndsWith("prop:drops") && P(x.Element).StartsWith("r:")).Sum(x => x.Value);
        Assert.Equal(5 * 3, drops);   // i % 5 == 0 over i = 0..14: two drops on the first pair, one on the second, per window
        Assert.Contains(dump, x => P(x.Prop).EndsWith("prop:flowsOut"));
        Assert.Contains(dump, x => P(x.Prop).EndsWith("prop:flowRate"));
    }

    [Fact]
    public void Pods_per_service_and_the_namespace_group_are_structural_facts()
    {
        using var e = NewEngine();
        new SyntheticWorld(e, 6, 3).LoadStructure();
        var n = e.Graph("serviceTree").State.Entities.Where(x => Ids.Name(x.Type) == "ServiceNode").ToList();
        Assert.All(n, x => Assert.Equal(2, (long)((IntegerValue)x.Props.Get("d:acme.svc/prop:pods")!).N));
    }

    [Fact]
    public void Threshold_hysteresis_changes_the_structural_status_only_on_state_changes()
    {
        using var e = NewEngine();
        new SyntheticWorld(e, 6, 3).LoadStructure();
        var export = new CanvasExport(e, Repo.LoadExamples(out _)); export.Attach();
        var rw = new RowWriter(e.RowLayout("flows"), e.Interner);
        // windows 0..2: 10 percent drops, windows 3..8: none. Each window closes before the next one is ingested.
        for (var win = 0; win < 9; win++)
        {
            for (var i = 0; i < 10; i++)
            {
                rw.Begin(); rw.Instant("time", T0 + win * Min + i * 1_000_000_000L); rw.Text("cluster", "synth"); rw.Text("verdict", win < 3 && i == 0 ? "DROPPED" : "FORWARDED");
                rw.Text("srcNamespace", "ns0"); rw.Text("srcPod", "pod-0"); rw.Text("dstNamespace", "ns1"); rw.Text("dstPod", "pod-1");
            }
            e.Ingest("flows", rw.Build()!, 100 + win);
            e.Advance(T0 + (win + 1) * Min + 1);
        }
        var statuses = export.PatchesOf("serviceTree").SelectMany(p => p.Edits).Where(x => x.After is Rel r && Ids.Name(r.Type) == "DependsOn")
            .Select(x => ((Rel)x.After!).Props.Text("d:acme.svc/prop:status")).Where(x => x is not null).ToList();
        Assert.Equal(["ok", "degraded", "ok"], statuses);          // at most floor(N / n) = 3 toggles for N = 9 windows, n = 3 (Theorem 7.4)
    }

    [Fact]
    public void A_flow_before_its_pod_mints_a_provisional_identity_and_the_late_link_merges_it_into_the_pod()
    {
        using var e = NewEngine();
        var rw = new RowWriter(e.RowLayout("flows"), e.Interner);
        rw.Begin(); rw.Instant("time", T0); rw.Text("cluster", "synth"); rw.Text("verdict", "FORWARDED");
        rw.Text("srcNamespace", "ns0"); rw.Text("srcPod", "pod-0"); rw.Text("dstNamespace", "ns1"); rw.Text("dstPod", "pod-1");
        e.Ingest("flows", rw.Build()!, 1);
        e.Advance(T0 + 2 * Min);                       // the window closes: the relationship is emitted with provisional endpoints
        var before = e.Master.State.Rels.Single(r => Ids.Name(r.Type) == "Calls");
        Assert.Null(e.Master.State.Entity(before.Source));                                  // dangling: no pod yet
        new SyntheticWorld(e, 2, 2).LoadStructure(10);
        Assert.Equal(0, e.Identities.Conflicts);
        Assert.True(e.Identities.Merges >= 2);                                              // pod-0 and pod-1: the provisional identity and the pod's identity are one entity now
        var after = e.Master.State.Rels.Single(r => Ids.Name(r.Type) == "Calls");
        Assert.NotNull(e.Master.State.Entity(after.Source)); Assert.NotNull(e.Master.State.Entity(after.Target));   // the relationship follows the survivor
        Assert.Null(e.Master.State.Rel(before.Id) is { } old && old.Source != after.Source ? old : null);           // no stale relationship under the old endpoints
        Assert.Equal(2, e.Master.State.Entities.Count(x => Ids.Name(x.Type) == "Pod" && x.Props.Text("d:sattle.infra/prop:name") is "pod-0" or "pod-1"));
    }

    [Fact]
    public void Identity_resolution_is_a_pure_function_of_the_key_and_aliases_share_one_entity()
    {
        using var a = NewEngine(); using var b = NewEngine();
        new SyntheticWorld(a, 4, 2).LoadStructure();
        new SyntheticWorld(b, 4, 2).LoadStructure(50);
        Assert.Equal(a.Master.State.Entities.Select(x => x.Id).Order(), b.Master.State.Entities.Select(x => x.Id).Order()); // same keys, same ids, regardless of sequence numbers
        Assert.Equal(4, a.Identities.Aliased);                                                                                // each pod: podName key bound to the podUid identity
    }
}
