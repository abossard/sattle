using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;
using Sattle.Cli;

namespace Sattle.Tests;

/// <summary>PROTOCOL.md on a host in process, each test on the answers of one family of endpoints: definitions served as a graph (Theorem 9.1), check, a definition commit with its re-activation, the event stream (Theorem 12.1, Proposition 12.2), telemetry and meters (Theorem 7.1), the hysteresis of a threshold (Theorem 7.4), lineage. A test that only reads shares one host of the class.</summary>
public class HostTests
{
    const long Min = 60_000_000_000;
    static readonly long T0 = 1_794_300_000_000_000_000 / Min * Min;

    static readonly Lazy<(IReadOnlyList<SourceFile> Files, SpaceHost.LoadedDefinitions Loaded)> Shared = new(() => { var f = Catalog.ReadDirectory(Repo.Definitions); return (f, SpaceHost.LoadDefinitions(f, Repo.Projections)); });

    /// <summary>A host on the example definitions. The checked catalog is loaded once per test run (loading costs more than a host), every host gets its own engine and state.</summary>
    public static SpaceHost NewHost() => NewHost(lateHorizon: 3);

    /// <summary>The same with another late horizon: 1,000,000 windows for a recorded input whose every window stays revisable, as `sattle replay` sets it.</summary>
    public static SpaceHost NewHost(int? lateHorizon) => new(Shared.Value.Files, new HostOptions { Projections = Repo.Projections, LateHorizonWindows = lateHorizon }, Shared.Value.Loaded);

    /// <summary>Services 0 to 2, flows instance-0 to instance-1 with 10 percent drops for 3 windows, then none: the health status degrades and recovers.</summary>
    internal static void Drive(SpaceHost h, int windows = 9)
    {
        new SyntheticWorld2(h).Load();
        for (var win = 0; win < windows; win++) Flows(h, win);
    }

    /// <summary>The ten flows of one window from instance-0 to instance-1, and the advance that closes it.</summary>
    internal static void Flows(SpaceHost h, int window)
    {
        var rw = new RowWriter(h.RowLayout("flows"), h.Interner);
        for (var i = 0; i < 10; i++)
        {
            rw.Begin(); rw.Instant("time", T0 + window * Min + i * 1_000_000_000L); rw.Text("site", "synth"); rw.Text("verdict", window < 3 && i == 0 ? "DROPPED" : "FORWARDED");
            rw.Text("srcNamespace", "ns0"); rw.Text("srcInstance", "instance-0"); rw.Text("dstNamespace", "ns1"); rw.Text("dstInstance", "instance-1");
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

    static string Text(string file) => File.ReadAllText(Path.Combine(Repo.Definitions, file));

    /// <summary>A host nothing was fed to and one after `Drive`, for the tests that only read.</summary>
    static readonly Lazy<SpaceHost> Fresh = new(NewHost);
    static readonly Lazy<SpaceHost> Driven = new(() => { var h = NewHost(); Drive(h); return h; });

    static List<JsonNode> Drain(Subscription s) { var l = new List<JsonNode>(); while (s.Messages.Reader.TryRead(out var m)) l.Add(JsonNode.Parse(m)!); return l; }
    static string Head(SpaceHost h, string graph) => h.SpaceInfo()["graphs"]!.AsArray().First(g => g!["id"]!.GetValue<string>() == graph)!["head"]!.GetValue<string>();

    /// <summary>The patches of a graph, applied in order to the empty graph, give the served snapshot element by element (Theorem 12.1 on what the protocol answers).</summary>
    static void AssertHistoryGivesTheSnapshot(SpaceHost h, string graph)
    {
        var replayed = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in h.Patches(graph, 0))
            foreach (var edit in p["edits"]!.AsArray())
            {
                var key = edit!["kind"]!.GetValue<string>() + " " + edit["id"]!.ToJsonString();
                if (edit["after"] is null) Assert.True(replayed.Remove(key), "a patch removes " + key + ", which no earlier patch created");
                else replayed[key] = edit["after"]!.ToJsonString();
            }
        var extent = h.Snapshot(graph, null)["extent"]!;
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var x in extent["entities"]!.AsArray()) snapshot["entity " + x!["id"]!.ToJsonString()] = x.ToJsonString();
        foreach (var x in extent["relationships"]!.AsArray()) snapshot["relationship " + x!["id"]!.ToJsonString()] = x.ToJsonString();
        Assert.NotEmpty(replayed);
        Assert.Equal(snapshot.Keys, replayed.Keys);
    }

    [Fact]
    public void Definitions_are_served_as_a_graph_with_the_kernel_as_ontology()
    {
        var h = Fresh.Value;
        var snap = h.Snapshot("catalog", null);
        var ents = snap["extent"]!["entities"]!.AsArray();
        Assert.Contains(ents, e => e!["id"]!["value"]!.GetValue<string>() == "d:acme.svc/projection:serviceDeps" && e["type"]!["value"]!.GetValue<string>() == "Projection");
        Assert.Contains(snap["extent"]!["relationships"]!.AsArray(), r => r!["type"]!["value"]!.GetValue<string>() == "wire");
        var onto = h.Ontology("catalog");
        Assert.Contains(onto["entityTypes"]!.AsArray(), t => t!["id"]!["value"]!.GetValue<string>() == "EntityType");
        Assert.Equal(h.CatalogRevision.ToString(), h.SpaceInfo()["catalogRevision"]!.GetValue<string>());
        Assert.Equal(h.CatalogRevision, h.Patches("catalog", 0).Count);
        AssertHistoryGivesTheSnapshot(h, "catalog");
    }

    [Fact]
    public void Check_reports_typed_diagnostics_with_positions_and_the_patch_without_changing_state()
    {
        var h = Fresh.Value;
        var File = h.ProjectionFile("acme.svc.serviceDeps");
        var good = h.Check(File, Text(File));
        Assert.True(good["ok"]!.GetValue<bool>());
        Assert.Empty(good["patch"]!["edits"]!.AsArray());
        var bad = h.Check(File, Text(File).Replace("{\"ref\": \"drops\"}, {\"ref\": \"flows\"}", "{\"ref\": \"drops\"}, {\"ref\": \"nosuch\"}"));
        Assert.False(bad["ok"]!.GetValue<bool>());
        var d = bad["diagnostics"]!.AsArray().First(x => x!["severity"]!.GetValue<string>() == "error")!;      // the column is unknown; which code names it is the language's
        Assert.True(d["line"]!.GetValue<int>() > 0 && d["column"]!.GetValue<int>() > 0);
        Assert.Equal(File, d["file"]!.GetValue<string>());
        var changed = h.Check(File, Text(File).Replace("\"above\": 0.05", "\"above\": 0.5"));
        Assert.True(changed["ok"]!.GetValue<bool>());
        Assert.NotEmpty(changed["patch"]!["edits"]!.AsArray());
        Assert.Contains("acme.svc", changed["affected"]!["modules"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains("serviceTree", changed["affected"]!["graphs"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(h.CatalogRevision.ToString(), h.SpaceInfo()["catalogRevision"]!.GetValue<string>());                 // nothing committed
        Assert.Throws<HostError>(() => h.Check("acme.other", Text("acme.svc.module.gkql.json")));
    }

    [Fact]
    public void A_definition_edit_reactivates_the_space_and_emits_only_the_final_diff()
    {
        using var h = NewHost();
        Drive(h);
        string Status() => h.Relationships("serviceTree", "DependsOn").Single().Prop("status")!;
        Assert.Equal("ok", Status());
        var headBefore = long.Parse(Head(h, "serviceTree"));
        var masterBefore = h.Snapshot("master", null).ToJsonString();
        var masterHead = Head(h, "master");
        // initial 3 windows of 10 percent drops: lower the entry threshold so the status ends degraded
        var File = h.ProjectionFile("acme.svc.serviceDeps");
        var edited = Text(File).Replace("\"above\": 0.05, \"enterAfter\": 3, \"below\": 0.02, \"exitAfter\": 3", "\"above\": 0.05, \"enterAfter\": 3, \"below\": 0.0, \"exitAfter\": 30");
        Assert.NotEqual(Text(File), edited);
        var rev0 = h.CatalogRevision;
        var res = h.Commit(File, edited, rev0);
        Assert.Equal((rev0 + 1).ToString(), res["catalogRevision"]!.GetValue<string>());
        var graphs = res["activation"]!["graphs"]!.AsArray();
        Assert.Single(graphs);                                                                     // only serviceTree changed
        Assert.Equal("serviceTree", graphs[0]!["id"]!.GetValue<string>());
        Assert.Equal(headBefore + 1, long.Parse(graphs[0]!["to"]!.GetValue<string>()));
        Assert.Equal(masterBefore, h.Snapshot("master", null).ToJsonString());
        Assert.Equal(masterHead, Head(h, "master"));
        Assert.Equal("degraded", Status());
        // the result equals a fresh engine with the new definition fed the same inputs
        using var fresh = new SpaceHost(Catalog.ReadDirectory(Repo.Definitions).Select(f => f.Path.EndsWith(File) ? f with { Text = edited } : f).ToList(), new HostOptions { Projections = Repo.Projections});
        Drive(fresh);
        Assert.Equal(fresh.StateDigest(), h.StateDigest());
        AssertHistoryGivesTheSnapshot(h, "serviceTree");
        // stale and invalid edits
        Assert.Equal(409, Assert.Throws<HostError>(() => h.Commit("acme.svc", Text("acme.svc.module.gkql.json"), rev0)).Status);
        var invalid = Assert.Throws<HostError>(() => h.Commit(File, edited.Replace("{\"ref\": \"drops\"}, {\"ref\": \"flows\"}", "{\"ref\": \"drops\"}, {\"ref\": \"nosuch\"}"), rev0 + 1));
        Assert.Equal("Invalid", invalid.Code);
        Assert.Equal((rev0 + 1).ToString(), h.SpaceInfo()["catalogRevision"]!.GetValue<string>());
    }

    /// <summary>A definition commit builds a new engine and replays into it; the host must let go of the engine it replaced, or every edit keeps one engine's state alive.</summary>
    [Fact, Trait("Category", "Slow")]
    public void Engines_a_definition_commit_replaced_are_collected()
    {
        using var h = NewHost();
        Drive(h, 3);
        var file = h.ProjectionFile("acme.svc.serviceDeps");
        var texts = new[] { Text(file), Text(file).Replace("\"above\": 0.05, \"enterAfter\": 3, \"below\": 0.02, \"exitAfter\": 3", "\"above\": 0.05, \"enterAfter\": 3, \"below\": 0.0, \"exitAfter\": 30") };
        var replaced = new List<WeakReference>(); var rev0 = h.CatalogRevision;
        for (var i = 1; i <= 3; i++) replaced.Add(CommitAndForget(h, file, texts[i % 2]));
        Assert.Equal(rev0 + 3, h.CatalogRevision);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.Equal(0, replaced.Count(w => w.IsAlive));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference CommitAndForget(SpaceHost h, string file, string text)
    {
        var before = new WeakReference(h.Engine);
        h.Commit(file, text, h.CatalogRevision);
        return before;
    }

    [Fact]
    public void A_no_op_definition_edit_creates_no_revision()
    {
        using var h = NewHost();
        Drive(h, 3);
        var res = h.Commit("acme.svc", Text("acme.svc.module.gkql.json"), h.CatalogRevision);
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
        new SyntheticWorld(h, 7, 3).LoadStructure(10);          // more instances and services than before: events after the snapshot
        var seqs = new List<long>();
        // the snapshot event and the patch events after it fold into a replica of the graph (Ch. 12, structural replica)
        static IEnumerable<(string Key, JsonNode? State)> Extent(JsonNode extent) =>
            extent["entities"]!.AsArray().Select(x => ("entity " + x!["id"]!.ToJsonString(), (JsonNode?)x)).Concat(extent["relationships"]!.AsArray().Select(x => ("relationship " + x!["id"]!.ToJsonString(), (JsonNode?)x)));
        var replica = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, state) in Extent(f["snapshot"]!["extent"]!)) replica[key] = state!.ToJsonString();
        var patches = 0;
        while (snap.Messages.Reader.TryRead(out var t))
        {
            var j = JsonNode.Parse(t)!;
            if (j["seq"] is { } sq) seqs.Add(long.Parse(sq.GetValue<string>()));
            if (j["type"]!.GetValue<string>() != "patch") continue;
            patches++;
            foreach (var edit in j["patch"]!["edits"]!.AsArray())
            {
                var key = edit!["kind"]!.GetValue<string>() + " " + edit["id"]!.ToJsonString();
                if (edit["after"] is null) Assert.True(replica.Remove(key), "a patch removes " + key + ", which the replica does not hold");
                else replica[key] = edit["after"]!.ToJsonString();
            }
        }
        Assert.True(patches > 0);
        var final = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, state) in Extent(h.Snapshot("master", null)["extent"]!)) final[key] = state!.ToJsonString();
        Assert.Equal(final, replica);
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

    /// <summary>The service tree of the example definitions on six instances of three services in three namespaces, with flows between two of them: a node per service with its two instances as a structural fact, a group per namespace that contains its service, one dependency.</summary>
    [Fact]
    public void The_service_tree_has_a_node_per_service_with_its_instances_a_group_per_namespace_and_the_dependency_the_flows_show()
    {
        var h = Driven.Value;
        var nodes = h.Entities("serviceTree", "ServiceNode");
        Assert.Equal(3, nodes.Count);
        Assert.All(nodes, x => Assert.Equal("2", x.Prop("instances")));
        Assert.Equal((3, 3, 1), (h.Entities("serviceTree", "ServiceGroup").Count, h.Relationships("serviceTree", "Contains").Count, h.Relationships("serviceTree", "DependsOn").Count));
    }

    /// <summary>Theorem 7.4 (bounded churn) on served patches: the drop ratio is over the threshold in windows 0 to 2 and under it in windows 3 to 8, and the status of the dependency changes only when the state machine of the threshold changes state. Its served history is ok, degraded, ok: at most floor(N / n) = 3 changes for N = 9 windows and runs of n = 3.</summary>
    [Fact]
    public void Threshold_hysteresis_changes_the_structural_status_only_on_state_changes()
    {
        var statuses = Driven.Value.Patches("serviceTree", 0).SelectMany(p => p["edits"]!.AsArray()).Where(e => e!["after"] is { } after && after.Is("DependsOn"))
            .Select(e => e!["after"]!.Prop("status")).Where(s => s is not null).ToList();
        Assert.Equal(["ok", "degraded", "ok"], statuses);
    }

    [Fact]
    public void Telemetry_range_read_and_meters_come_from_the_definitions_and_never_from_revisions()
    {
        var h = Driven.Value;
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
        var h = Driven.Value;
        var l = h.Lineage("serviceTree", h.Relationships("serviceTree", "DependsOn").Single()["id"].Id());
        Assert.Equal("serviceDeps", l["projection"]!.GetValue<string>());
        Assert.Equal("forward", l["direction"]!.GetValue<string>());
        Assert.Equal(l["sink"]!.GetValue<string>(), l["operators"]!.AsArray().Last()!.GetValue<string>());          // the operators the step's query was lowered to, ending at its sink
        var src = l["sources"]!.AsArray();
        Assert.Contains(src, s => s!["role"]!.GetValue<string>() == "call" && s["kind"]!.GetValue<string>() == "relationship" && s["graph"]!.GetValue<string>() == "master");
        var ml = h.Lineage("master", h.Relationships("master", "Calls").First()["id"].Id());
        Assert.Equal("observedCalls", ml["rule"]!.GetValue<string>());
        Assert.Contains("FlowRow", ml["inputs"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(404, Assert.Throws<HostError>(() => h.Lineage("master", "e:nope")).Status);
    }

    // ---- /space, meters and versions (corrections the canvas asked for in version 3) ----

    [Fact]
    public void Space_carries_the_event_sequence()
    {
        using var h = NewHost();
        var before = long.Parse(h.SpaceInfo()["seq"]!.GetValue<string>());
        Drive(h, 2);
        Assert.True(long.Parse(h.SpaceInfo()["seq"]!.GetValue<string>()) > before);
        Assert.Equal(h.Seq.ToString(), h.SpaceInfo()["seq"]!.GetValue<string>());
    }

    [Fact]
    public void The_catalog_has_empty_meters() => Assert.Empty(Fresh.Value.Meters("catalog")["meters"]!.AsArray());

    /// <summary>Review: Export.cs:296. The thresholds document states the thresholds that apply: a threshold of the estate enters after one window or more, and has no parameter read from a row.</summary>
    [Theory]
    [InlineData("serviceTree")] [InlineData("capabilities")]
    public void An_exported_threshold_never_claims_to_enter_after_zero_windows(string graph)
    {
        var thresholds = Fresh.Value.Meters(graph)["thresholds"]!.AsArray();
        Assert.NotEmpty(thresholds);
        Assert.All(thresholds, t => Assert.True(t!["enterAfter"]!.GetValue<long>() >= 1 && t["byRow"] is null, t.ToJsonString()));
    }

    [Fact]
    public void A_version_bump_commits_with_the_import_versions_and_pins_of_its_dependents_rewritten()
    {
        using var h = NewHost();
        var text = h.Definition("acme.svc").Replace("\"module\": \"acme.svc\",\n  \"version\": \"1.0.0\"", "\"module\": \"acme.svc\",\n  \"version\": \"1.1.0\"");
        Assert.Contains("\"version\": \"1.1.0\"", text);
        h.Commit("acme.svc", text, h.CatalogRevision);
        var served = h.Definitions().AsArray().Single(m => m!["name"]!.GetValue<string>() == "acme.svc")!;
        Assert.Equal("1.1.0", served["version"]!.GetValue<string>());
        var import = JsonNode.Parse(h.Definition("acme.space"))!["imports"]!.AsArray().Single(i => i!["module"]!.GetValue<string>() == "acme.svc")!;
        Assert.Equal(("1.1.0", served["digest"]!.GetValue<string>()), (import["version"]!.GetValue<string>(), import["digest"]!.GetValue<string>()));
    }

    /// <summary>#57: every revision of a graph has its commit time, the engine's logical time when it was committed: served by the revisions route and sent with the revision's patch event. Revisions before the first advance have none; a revision an advance commits is at that advance's time.</summary>
    [Fact]
    public void Every_revision_is_served_with_its_commit_time()
    {
        using var h = NewHost();
        var sub = h.Subscribe(["master", "serviceTree"], false, null);
        Drain(sub);
        new SyntheticWorld2(h).Load();
        var sent = new List<JsonNode>(); var atTheirAdvance = 0;
        for (var w = 0; w < 9; w++)
        {
            Flows(h, w);                                                                          // the window's rows, then the advance that closes it
            var now = Drain(sub).Where(m => m["type"]!.GetValue<string>() == "patch").ToList();
            atTheirAdvance += now.Count(m => m["time"]?.GetValue<string>() == Norm.Instant(T0 + (w + 1) * Min + 1));
            sent.AddRange(now);
        }
        Assert.NotEmpty(sent);
        Assert.True(atTheirAdvance > 0, "no revision an advance committed is at that advance's time");
        foreach (var graph in new[] { "master", "serviceTree" })
        {
            var revisions = h.Revisions(graph, 0);
            Assert.Equal(Enumerable.Range(1, (int)h.Head(graph)).Select(r => r.ToString()), revisions.Select(r => r["revision"]!.GetValue<string>()));
            var times = revisions.Select(r => r["time"]?.GetValue<string>()).ToList();
            var known = times.Where(t => t is not null).Select(t => DateTimeOffset.Parse(t!)).ToList();
            Assert.Equal(known.Order(), known);                                                   // a later revision is never earlier
            Assert.DoesNotContain(null, times.SkipWhile(t => t is null));                  // no time only before the first advance
            foreach (var m in sent.Where(m => m["graph"]!.GetValue<string>() == graph))
                Assert.Equal(times[int.Parse(m["patch"]!["to"]!["revision"]!["value"]!.GetValue<string>()) - 1], m["time"]?.GetValue<string>());
            Assert.Equal([revisions[^1].ToJsonString()], h.Revisions(graph, h.Head(graph) - 1).Select(r => r.ToJsonString()));
        }
        Assert.Null(h.Revisions("master", 0)[0]["time"]);                                         // the structure is loaded before any advance
    }

    // ---- what a subscriber receives ----

    /// <summary>Review: Host.cs:542. A subscriber that reads every message is never a slow consumer, however many messages it has read.</summary>
    [Fact]
    public void A_subscriber_that_reads_everything_is_not_closed_as_a_slow_consumer()
    {
        using var h = NewHost();
        var sub = h.Subscribe(["master"], false, null);
        long received = Drain(sub).Count;
        for (var i = 0; i < 10_100 && !sub.Closed; i++) { h.Finish(); received += Drain(sub).Count; }
        Assert.True(!sub.Closed, $"closed after {received} messages, all of them read");
    }

    /// <summary>Review: Output.cs:295-306 with Host.cs:490. A subscriber learns that a telemetry window went, not only that one has a new value.</summary>
    [Fact]
    public void A_subscriber_is_told_when_a_telemetry_window_is_retracted()
    {
        using var h = NewHost();
        new SyntheticWorld(h, 4, 2).LoadStructure();
        var sub = h.Subscribe(["master"], false, null);
        Drain(sub);
        h.Ingest("flows", new SyntheticWorld(h, 4, 2).Flows(20, T0, Min / 2), 10);
        h.Advance(T0 + 2 * Min, 11);
        var published = Drain(sub).Count(m => m["type"]!.GetValue<string>() == "telemetry");
        Assert.True(published > 0, "the windows were not published in the first place");
        var retraction = new SyntheticWorld(h, 4, 2).Flows(20, T0, Min / 2);
        retraction.W = [.. Enumerable.Range(0, retraction.N).Select(i => -retraction.Weight(i))];
        h.Ingest("flows", retraction, 12);                                                    // inside the late horizon: every window of these flows is retracted
        var after = Drain(sub);
        Assert.True(after.Any(m => m["type"]!.GetValue<string>() == "telemetry"), $"{published} telemetry messages announced the windows, none their retraction; received: {string.Join(", ", after.Select(m => m["type"]!.GetValue<string>()))}");
    }

    /// <summary>The windows a batch of the feed or of the range read names, by window, property and element; a batch of the feed also removes.</summary>
    static void Apply(SortedDictionary<string, double> windows, JsonNode batch)
    {
        var key = $"{batch["windowStart"]} {batch["property"]} ";
        var (ids, values) = (batch["ids"]!.AsArray(), batch["values"]!.AsArray());
        for (var i = 0; i < ids.Count; i++) windows[key + ids[i]] = values[i]!.GetValue<double>();
        foreach (var gone in batch["removed"]?.AsArray() ?? []) windows.Remove(key + gone);
    }

    /// <summary>PROTOCOL 3.33, Law 7.5 item 3, on the estate of the tests, where identities merge: a subscriber that applies every telemetry batch of the feed holds at the end exactly the windows the range read answers. The windows a merge moves are in the feed of the step that merged; before, 32 window changes of this estate reached no subscriber (code review 2, defect 22).</summary>
    [Fact]
    public async Task A_subscriber_that_applies_every_telemetry_batch_holds_the_windows_the_range_read_answers()
    {
        using var h = new SpaceHost(EstateTests.Files(demo: false), new HostOptions { Projections = Repo.Projections, LateHorizonWindows = 3 });
        var sub = h.Subscribe(["master"], false, null);
        var replica = new SortedDictionary<string, double>(StringComparer.Ordinal);
        var reading = Task.Run(async () =>
        {
            await foreach (var text in sub.Messages.Reader.ReadAllAsync())
                if (JsonNode.Parse(text)! is { } m && m["type"]!.GetValue<string>() == "telemetry") Apply(replica, m["batch"]!);
        });
        new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 50, AdvanceEveryNs = Min, LatenessNs = Min}).Run(EstateTests.EstateDir);
        h.Unsubscribe(sub);
        await reading;
        Assert.True(h.Engine.Identities.Merges > 0, "no identity merged on the estate");          // the protocol does not serve the number of merges: the one read inside the engine
        var read = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var batch in h.Telemetry("master", null, null, null, null, batches: true).AsArray()) Apply(read, batch!);
        Assert.NotEmpty(read);
        Assert.Equal(read, replica);
    }
}
