using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>PROTOCOL.md: definitions as graphs, check, configuration edits with re-activation, subscriptions, telemetry, lineage.</summary>
public class HostTests
{
    const long Min = 60_000_000_000;
    static readonly long T0 = 1_794_300_000_000_000_000 / Min * Min;

    static readonly Lazy<(IReadOnlyList<SourceFile> Files, SpaceHost.LoadedDefinitions Loaded)> Shared = new(() => { var f = Catalog.ReadDirectory(Repo.Definitions); return (f, SpaceHost.LoadDefinitions(f)); });

    /// <summary>A host on the example definitions. The checked catalog is loaded once per test run (loading costs more than a host), every host gets its own engine and state.</summary>
    public static SpaceHost NewHost() => new(Shared.Value.Files, new HostOptions { Cluster = "synth" }, Shared.Value.Loaded);

    /// <summary>Services 0 to 2, flows pod-0 to pod-1 with 10 percent drops for 3 windows, then none: the health status degrades and recovers.</summary>
    internal static void Drive(SpaceHost h, int windows = 9)
    {
        new SyntheticWorld2(h).Load();
        for (var win = 0; win < windows; win++) Flows(h, win);
    }

    /// <summary>The ten flows of one window from pod-0 to pod-1, and the advance that closes it.</summary>
    internal static void Flows(SpaceHost h, int window)
    {
        var rw = new RowWriter(h.RowLayout("flows"), h.Interner);
        for (var i = 0; i < 10; i++)
        {
            rw.Begin(); rw.Instant("time", T0 + window * Min + i * 1_000_000_000L); rw.Text("cluster", "synth"); rw.Text("verdict", window < 3 && i == 0 ? "DROPPED" : "FORWARDED");
            rw.Text("srcNamespace", "ns0"); rw.Text("srcPod", "pod-0"); rw.Text("dstNamespace", "ns1"); rw.Text("dstPod", "pod-1");
        }
        h.Ingest("flows", rw.Build()!, 100 + window);
        h.Advance(T0 + (window + 1) * Min + 1, 200 + window);
    }

    sealed class SyntheticWorld2(SpaceHost h)
    {
        public void Load()
        {
            // reuse the engine-level synthetic world through the host's input surface
            var w = new SyntheticWorld(h, 6, 3);
            w.LoadStructure();
        }
    }

    static string Text(string module) => File.ReadAllText(Path.Combine(Repo.Definitions, module + ".sattle"));

    [Fact]
    public void Definitions_are_served_as_a_graph_with_the_kernel_as_ontology()
    {
        using var h = NewHost();
        var snap = h.Snapshot("catalog", null);
        var ents = snap["extent"]!["entities"]!.AsArray();
        Assert.Contains(ents, e => e!["id"]!["value"]!.GetValue<string>() == "d:acme.svc/projection:serviceDeps" && e["type"]!["value"]!.GetValue<string>() == "Projection");
        Assert.Contains(snap["extent"]!["relationships"]!.AsArray(), r => r!["type"]!["value"]!.GetValue<string>() == "wire");
        var onto = h.Ontology("catalog");
        Assert.Contains(onto["entityTypes"]!.AsArray(), t => t!["id"]!["value"]!.GetValue<string>() == "EntityType");
        Assert.Equal(h.CatalogRevision.ToString(), h.SpaceInfo()["catalogRevision"]!.GetValue<string>());
        Assert.Equal(h.CatalogRevision, h.Patches("catalog", 0).Count);
        // the catalog history replays to the catalog graph
        var g = new Graph();
        foreach (var p in h.Catalog.Log.Commits) g.Apply(p.Patch.Edits);
        Assert.True(g.SameAs(h.Catalog.Graph));
    }

    /// <summary>Code review 2, defect 1: a text nested deeper than the parser's limit is answered with a `Syntax` diagnostic. Before, 600 nested parentheses or 450 nested `if(` overflowed the stack, which ends the process. A text nested up to the limit is parsed as before.</summary>
    [Theory]
    [InlineData("paren", 50, false)] [InlineData("if", 50, false)]
    [InlineData("paren", 101, true)] [InlineData("paren", 600, true)] [InlineData("if", 450, true)] [InlineData("paren", 20000, true)] [InlineData("if", 20000, true)]
    [InlineData("row", 50, false)] [InlineData("row", 150, true)] [InlineData("row", 20000, true)]              // a row of operators nests one level each: 20,000 of them overflowed the stack in a later pass
    public void A_text_nested_deeper_than_the_limit_is_a_diagnostic_and_never_a_stack_overflow(string shape, int depth, bool refused)
    {
        using var h = NewHost();
        var nested = shape == "paren"
            ? new string('(', depth) + "workloadNamespace" + new string(')', depth)
            : shape == "row" ? "workloadNamespace" + string.Concat(Enumerable.Repeat(" ++ \"x\"", depth))
            : string.Concat(Enumerable.Repeat("if(true, ", depth)) + "workloadNamespace" + string.Concat(Enumerable.Repeat(", \"x\")", depth));
        var text = Text("acme.workload");
        var changed = text.Replace("when image.namespace == workloadNamespace", "when image.namespace == " + nested);
        Assert.NotEqual(text, changed);
        var answer = h.Check("acme.workload", changed);
        Assert.Equal(!refused, answer["ok"]!.GetValue<bool>());
        if (refused) Assert.Contains(answer["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "Syntax" && d["message"]!.GetValue<string>().Contains($"nested deeper than {Parser.MaxNesting} levels"));
    }

    [Fact]
    public void Check_reports_typed_diagnostics_with_positions_and_the_patch_without_changing_state()
    {
        using var h = NewHost();
        var good = h.Check("acme.svc", Text("acme.svc"));
        Assert.True(good["ok"]!.GetValue<bool>());
        Assert.Empty(good["patch"]!["edits"]!.AsArray());
        var bad = h.Check("acme.svc", Text("acme.svc").Replace("drops / flows", "drops / nosuch"));
        Assert.False(bad["ok"]!.GetValue<bool>());
        var d = bad["diagnostics"]!.AsArray().First(x => x!["code"]!.GetValue<string>() == "UnknownField")!;
        Assert.True(d["line"]!.GetValue<int>() > 0 && d["column"]!.GetValue<int>() > 0);
        Assert.StartsWith("d:acme.svc/", d["element"]!.GetValue<string>());
        var changed = h.Check("acme.svc", Text("acme.svc").Replace("above 0.05", "above 0.5"));
        Assert.True(changed["ok"]!.GetValue<bool>());
        Assert.NotEmpty(changed["patch"]!["edits"]!.AsArray());
        Assert.Contains("acme.svc", changed["affected"]!["modules"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains("serviceTree", changed["affected"]!["graphs"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(h.CatalogRevision.ToString(), h.SpaceInfo()["catalogRevision"]!.GetValue<string>());                 // nothing committed
        Assert.Throws<HostError>(() => h.Check("acme.other", Text("acme.svc")));
    }

    [Fact]
    public void A_definition_edit_reactivates_the_space_and_emits_only_the_final_diff()
    {
        using var h = NewHost();
        Drive(h);
        string Status() => ((Rel)h.Engine.Graph("serviceTree").State.Rels.Single(r => Ids.Name(r.Type) == "DependsOn")).Props.Text("d:acme.svc/prop:status")!;
        Assert.Equal("ok", Status());
        var headBefore = long.Parse(h.SpaceInfo()["graphs"]!.AsArray().First(g => g!["id"]!.GetValue<string>() == "serviceTree")!["head"]!.GetValue<string>());
        var masterBefore = h.Engine.Master.State.Digest();
        var masterHead = h.SpaceInfo()["graphs"]!.AsArray().First(g => g!["id"]!.GetValue<string>() == "master")!["head"]!.GetValue<string>();
        // initial 3 windows of 10 percent drops: lower the entry threshold so the status ends degraded
        var edited = Text("acme.svc").Replace("above 0.05 for 3 then \"degraded\" below 0.02 for 3", "above 0.05 for 3 then \"degraded\" below 0.0 for 30");
        Assert.NotEqual(Text("acme.svc"), edited);
        var rev0 = h.CatalogRevision;
        var res = h.Commit("acme.svc", edited, rev0);
        Assert.Equal((rev0 + 1).ToString(), res["catalogRevision"]!.GetValue<string>());
        var graphs = res["activation"]!["graphs"]!.AsArray();
        Assert.Single(graphs);                                                                     // only serviceTree changed
        Assert.Equal("serviceTree", graphs[0]!["id"]!.GetValue<string>());
        Assert.Equal(headBefore + 1, long.Parse(graphs[0]!["to"]!.GetValue<string>()));
        Assert.Equal(masterBefore, h.Engine.Master.State.Digest());
        Assert.Equal(masterHead, h.SpaceInfo()["graphs"]!.AsArray().First(g => g!["id"]!.GetValue<string>() == "master")!["head"]!.GetValue<string>());
        Assert.Equal("degraded", Status());
        // the result equals a fresh engine with the new definition fed the same inputs
        using var fresh = new SpaceHost(Catalog.ReadDirectory(Repo.Definitions).Select(f => f.Path.EndsWith("acme.svc.sattle") ? f with { Text = edited } : f).ToList(), new HostOptions { Cluster = "synth" });
        Drive(fresh);
        Assert.Equal(fresh.StateDigest(), h.StateDigest());
        // history: the patches of serviceTree, applied in order to the empty graph, give the served snapshot element by element
        var replayed = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in h.Patches("serviceTree", 0))
            foreach (var edit in p["edits"]!.AsArray())
            {
                var key = edit!["kind"]!.GetValue<string>() + " " + edit["id"]!.ToJsonString();
                if (edit["after"] is null) Assert.True(replayed.Remove(key), "a patch removes " + key + ", which no earlier patch created");
                else replayed[key] = edit["after"]!.ToJsonString();
            }
        var extent = h.Snapshot("serviceTree", null)["extent"]!;
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var x in extent["entities"]!.AsArray()) snapshot["entity " + x!["id"]!.ToJsonString()] = x.ToJsonString();
        foreach (var x in extent["relationships"]!.AsArray()) snapshot["relationship " + x!["id"]!.ToJsonString()] = x.ToJsonString();
        Assert.NotEmpty(replayed);
        Assert.Equal(snapshot.Keys, replayed.Keys);
        Assert.Equal(h.Engine.Graph("serviceTree").State.RelCount, extent["relationships"]!.AsArray().Count);
        // stale and invalid edits
        Assert.Equal(409, Assert.Throws<HostError>(() => h.Commit("acme.svc", Text("acme.svc"), rev0)).Status);
        var invalid = Assert.Throws<HostError>(() => h.Commit("acme.svc", edited.Replace("drops / flows", "drops / nosuch"), rev0 + 1));
        Assert.Equal("Invalid", invalid.Code);
        Assert.Equal((rev0 + 1).ToString(), h.SpaceInfo()["catalogRevision"]!.GetValue<string>());
    }

    [Fact]
    public void A_no_op_definition_edit_creates_no_revision()
    {
        using var h = NewHost();
        Drive(h, 3);
        var res = h.Commit("acme.svc", Text("acme.svc"), h.CatalogRevision);
        Assert.Equal(h.CatalogRevision.ToString(), res["catalogRevision"]!.GetValue<string>());
    }

    [Fact]
    public void The_event_stream_is_ordered_by_seq_resumable_and_reports_gap_and_ahead()
    {
        using var h = NewHost();
        new SyntheticWorld(h, 4, 2).LoadStructure();
        var snap = h.Subscribe(["master"], false, null);
        Assert.True(snap.Messages.Reader.TryRead(out var first));
        var f = JsonNode.Parse(first)!;
        Assert.Equal("snapshot", f["type"]!.GetValue<string>());
        var asOf = long.Parse(f["seq"]!.GetValue<string>());
        Assert.Equal(h.Seq, asOf);
        new SyntheticWorld(h, 7, 3).LoadStructure(10);          // more pods and services than before: events after the snapshot
        var seqs = new List<long>();
        while (snap.Messages.Reader.TryRead(out var t)) { var j = JsonNode.Parse(t)!; if (j["seq"] is { } sq) seqs.Add(long.Parse(sq.GetValue<string>())); }
        Assert.NotEmpty(seqs);
        Assert.All(seqs, x => Assert.True(x > asOf));
        Assert.Equal(seqs.Order(), seqs);
        // resume: a second subscription after the snapshot's seq sees exactly the same events
        var resumed = h.Subscribe(["master"], false, asOf);
        var again = new List<long>();
        while (resumed.Messages.Reader.TryRead(out var t)) again.Add(long.Parse(JsonNode.Parse(t)!["seq"]!.GetValue<string>()));
        Assert.Equal(seqs, again);
        var ahead = h.Subscribe(["master"], false, h.Seq + 5);
        Assert.True(ahead.Messages.Reader.TryRead(out var err));
        Assert.Equal("AheadOfHead", JsonNode.Parse(err)!["code"]!.GetValue<string>());
        Assert.Throws<HostError>(() => h.Subscribe(["nope"], false, null));
        // consecutive revisions per graph
        var revs = h.Patches("master", 0).Select(p => long.Parse(p["to"]!["revision"]!["value"]!.GetValue<string>())).ToList();
        Assert.Equal(revs.Select((_, i) => (long)i + 1), revs);
        h.Unsubscribe(snap);
    }

    [Fact]
    public void Telemetry_range_read_and_meters_come_from_the_definitions_and_never_from_revisions()
    {
        using var h = NewHost();
        Drive(h);
        var tr = h.Telemetry("serviceTree", T0, T0 + 3 * Min, "dropRatio", null, batches: false);
        var series = tr["series"]!.AsArray();
        Assert.Single(series);
        var pts = series[0]!["points"]!.AsArray();
        Assert.Equal(3, pts.Count);
        Assert.Equal(Norm.Instant(T0), pts[0]!["windowStart"]!.GetValue<string>());
        Assert.Equal(60, series[0]!["periodSeconds"]!.GetValue<long>());
        var meters = h.Meters("serviceTree");
        var drop = meters["meters"]!.AsArray().First(m => m!["id"]!.GetValue<string>() == "dropRatio")!;
        Assert.Equal(0.05, drop["warn"]!.GetValue<double>());
        Assert.Equal("gauge", drop["temporality"]!.GetValue<string>());
        Assert.Equal("degraded", meters["thresholds"]![0]!["high"]!.GetValue<string>());
        var batches = h.Telemetry("master", null, null, null, null, batches: true).AsArray();
        Assert.NotEmpty(batches);
        Assert.All(batches, b => Assert.NotNull(b!["windowStart"]));
    }

    [Fact]
    public void Lineage_names_the_rule_sink_operators_and_source_elements()
    {
        using var h = NewHost();
        Drive(h);
        var dep = h.Engine.Graph("serviceTree").State.Rels.Single(r => Ids.Name(r.Type) == "DependsOn");
        var l = h.Lineage("serviceTree", dep.Id);
        Assert.Equal("serviceDeps", l["projection"]!.GetValue<string>());
        Assert.Equal("forward", l["direction"]!.GetValue<string>());
        Assert.Contains("dependsOn__edges", l["operators"]!.AsArray().Select(x => x!.GetValue<string>()));          // the operators the lift statement was lowered to
        var src = l["sources"]!.AsArray();
        Assert.Contains(src, s => s!["role"]!.GetValue<string>() == "edge" && s["kind"]!.GetValue<string>() == "relationship" && s["graph"]!.GetValue<string>() == "master");
        var calls = h.Engine.Master.State.Rels.First(r => Ids.Name(r.Type) == "Calls");
        var ml = h.Lineage("master", calls.Id);
        Assert.Equal("observedCalls", ml["rule"]!.GetValue<string>());
        Assert.Contains("FlowRow", ml["inputs"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(404, Assert.Throws<HostError>(() => h.Lineage("master", "e:nope")).Status);
    }
}
