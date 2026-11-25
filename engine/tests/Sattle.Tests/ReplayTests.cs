using System.Text;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The scenarios replayed (Theorem 6.2, T6, and 6.3, T7): each tracked scenario ends in its pinned state digest at every chunk size, and a replay is deterministic patch by patch.</summary>
public class ReplayTests
{
    const long Min = 60_000_000_000;

    public sealed record Run(Engine Engine, ReplayStats Stats, CanvasExport Export, IReadOnlyList<ISourceAdapter> Adapters);

    /// <summary>A replay of `dir` as `sattle replay` builds it: an engine with its export. On the definitions `sattle replay` reads for `dir`, or on `cat` (a test's own definitions); up to the capture time `until`, or all of it.</summary>
    public static Run Replay(string dir, int chunk = 2000, long advanceEvery = Min, long lateness = Min, EngineOptions? o = null, Catalog? cat = null, long? until = null)
    {
        if (cat is null) { cat = Repo.CatalogFor(dir, out var res); Assert.Empty(res.Diagnostics); }
        var space = cat.Spaces.Keys.Single();
        var adapters = AdapterRegistry.Create(cat.Spaces[space], cat);
        Assert.Empty(AdapterRegistry.Verify(cat, cat.Spaces[space], adapters));
        var engine = new Engine(cat, space, o);
        var export = new CanvasExport(engine, cat); export.Attach();
        var replay = new Replay(engine, cat.Spaces[space], adapters, new ReplayOptions { ChunkRows = chunk, AdvanceEveryNs = advanceEvery, LatenessNs = lateness, UntilNs = until });
        var stats = replay.Run(dir);
        return new Run(engine, stats, export, adapters);
    }

    /// <summary>The sum of the signed deltas of every patch of a graph, as a delta.</summary>
    static Delta Integrated(Run r, string graph) => r.Export.PatchesOf(graph).Aggregate(new Delta(), (d, p) => d + p.Delta());

    [Fact]
    public void The_estate_replays_into_a_master_graph_and_a_service_tree() => A_capture_replays_into_a_master_graph_and_a_service_tree(Repo.Estate);

    static void A_capture_replays_into_a_master_graph_and_a_service_tree(string dir)
    {
        var r = Scenarios.Run(dir);
        Assert.Equal(0, r.Stats.Rejected);
        string[] sources = ["flows", "instances", "resources", "metrics", "events", "activity", "resourceMetrics"];
        Assert.All(sources, s => Assert.True(r.Stats.Rows.GetValueOrDefault(s) > 0, $"no rows of source {s}"));
        Assert.True(r.Engine.Master.State.EntityCount > 0);
        Assert.True(r.Engine.Graph("serviceTree").State.EntityCount > 0);
        var cat = Repo.LoadExamples(out _);
        Assert.Empty(Conformance.Check(r.Engine.Master.State, cat.OntologyOf("acme.ops")));
        Assert.Empty(Conformance.Check(r.Engine.Graph("serviceTree").State, cat.OntologyOf("acme.svc")));
    }

    [Fact]
    public void T7_replay_is_deterministic_same_inputs_same_patches_and_digests_on_the_estate() => T7_replay_is_deterministic_same_inputs_same_patches_and_digests(Repo.Estate);

    static void T7_replay_is_deterministic_same_inputs_same_patches_and_digests(string dir)
    {
        var a = Scenarios.Run(dir); var b = Scenarios.Again(dir);
        using var second = b.Engine;
        Assert.Equal(a.Engine.StateDigest(), b.Engine.StateDigest());
        foreach (var g in a.Engine.Graphs)
        {
            var pa = a.Export.PatchesOf(g.Name); var pb = b.Export.PatchesOf(g.Name);
            Assert.Equal(pa.Count, pb.Count);
            for (var i = 0; i < pa.Count; i++) { Assert.Equal(pa[i], pb[i]); Assert.Equal(pa[i].Delta().Encode(), pb[i].Delta().Encode()); }
        }
    }

    /// <summary>The command line is the same function: `sattle replay` of the estate with a copy of the definitions folder prints the pinned state digest, and prints another one after the title in the projection file of the readiness report changed. So a replay reads the definition files of the folder it is given, the projection files of whatever language among them, and a test on a replay cannot pass without them.</summary>
    [Fact]
    public void Sattle_replay_prints_the_pinned_digest_of_the_estate_and_another_one_after_a_projection_file_changed()
    {
        var dir = Scenarios.Tracked("estate");
        var defs = Directory.CreateTempSubdirectory("sattle-defs").FullName;
        try
        {
            foreach (var f in Directory.EnumerateFiles(Repo.Definitions)) File.Copy(f, Path.Combine(defs, Path.GetFileName(f)));
            string Digest()
            {
                var output = new StringWriter();
                var code = Sattle.Cli.Program.Run(["replay", dir, "--defs", defs], output);
                Assert.True(code == 0, output.ToString());
                return output.ToString().Split('\n').Select(l => l.Trim()).Last(l => l.StartsWith("state digest ", StringComparison.Ordinal))["state digest ".Length..];
            }
            var pinned = Digest();
            Scenarios.AssertPinned(dir, pinned);
            var report = Directory.EnumerateFiles(defs, "acme.report.readinessReport.*").Single();
            var text = File.ReadAllText(report);
            Assert.Contains("\"Unit readiness\"", text);
            File.WriteAllText(report, text.Replace("\"Unit readiness\"", "\"Unit readiness, changed\""));
            Assert.NotEqual(pinned, Digest());
        }
        finally { Directory.Delete(defs, true); }
    }

    /// <summary>T6 and T7 on a tracked scenario, with the settings of `sattle replay`: chunk sizes 1 and 50 give the state digest, the graph digests and the integrated output of chunk size 100000, the integrated output of every graph is its final state (the sum of the signed deltas of its patches), and the state digest is the pinned one.</summary>
    [Theory]
    [InlineData("estate")] [InlineData("estate-wide")] [InlineData("deploy-health")]
    public void A_tracked_scenario_ends_in_its_pinned_state_digest_at_chunk_sizes_1_50_and_100000(string scenario)
    {
        var dir = Scenarios.Tracked(scenario);
        Scenarios.AssertPinned(dir, Scenarios.Of(dir, Scenarios.Full).StateDigest);
        One_state_at_chunk_sizes_1_50_and_100000(dir);
    }

    /// <summary>Theorem 7.1 (plane separation) on a tracked scenario: no element of a graph carries a property its ontology declares as an event property, and every revision of a graph changes an element. So no revision is made by a event value, however many windows the scenario writes. The count of revisions against windows on one relationship is in `AdapterTests`, the store's side of the claim in `StoreTests`.</summary>
    [Theory]
    [InlineData("estate")] [InlineData("estate-wide")] [InlineData("deploy-health")]
    public void On_a_tracked_scenario_no_element_carries_an_event_property_and_every_revision_changes_an_element(string scenario)
    {
        var r = Scenarios.Run(Scenarios.Tracked(scenario));
        var windows = 0;
        foreach (var g in r.Engine.Graphs)
        {
            var meters = r.Read(x => x.Meters(g)).AsArray().Select(m => m!["id"]!.GetValue<string>()).ToHashSet();
            var carried = r.Entities(g.Name).Concat(r.Relationships(g.Name)).SelectMany(x => x["properties"]!.AsArray()).Select(p => p!["property"].Id()).ToHashSet();
            Assert.Empty(carried.Intersect(meters));
            Assert.All(r.Export.PatchesOf(g.Name), p => Assert.NotEmpty(p.Edits));
            windows += r.Read(x => x.WindowBatches(g).Count());
        }
        Assert.True(windows > 0, "the scenario writes no window: the claim says nothing here");
    }

    static void One_state_at_chunk_sizes_1_50_and_100000(string dir)
    {
        var reference = Scenarios.Of(dir, Scenarios.Full);
        foreach (var chunk in new[] { 1, 50 })
        {
            var r = Scenarios.Of(dir, chunk);
            Assert.Equal(reference.StateDigest, r.StateDigest);
            Assert.Equal(reference.GraphDigests, r.GraphDigests);
            Assert.Equal(reference.Integrated, r.Integrated);                                       // integrated output equals batch output (Quine invariant)
            Assert.All(r.IntegratedIsFinal.Concat(reference.IntegratedIsFinal), x => Assert.True(x.Value, $"{x.Key} at chunk size {chunk}: the integrated output is not the final graph"));
        }
    }
}
