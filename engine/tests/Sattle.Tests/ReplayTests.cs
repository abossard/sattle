using System.Text;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Steps 5 and 7: adapters on the capture envelope, replay, determinism (T7) and batch invariance (T6).</summary>
public class ReplayTests
{
    const long Min = 60_000_000_000;

    public sealed record Run(Engine Engine, ReplayStats Stats, CanvasExport Export, IReadOnlyList<ISourceAdapter> Adapters);

    public static Run Replay(string dir, int chunk = 2000, string cluster = "", long advanceEvery = Min, long lateness = Min, EngineOptions? o = null)
    {
        var cat = Repo.CatalogFor(dir, out var res);
        Assert.Empty(res.Diagnostics);
        var space = cat.Spaces.Keys.Single();
        if (cluster == "") cluster = "synth";
        var adapters = AdapterRegistry.Create(cluster);
        Assert.Empty(AdapterRegistry.Verify(cat, cat.Spaces[space], adapters));
        var engine = new Engine(cat, space, o);
        var export = new CanvasExport(engine, cat); export.Attach();
        var replay = new Replay(engine, cat.Spaces[space], adapters, new ReplayOptions { ChunkRows = chunk, AdvanceEveryNs = advanceEvery, LatenessNs = lateness, Cluster = cluster });
        var stats = replay.Run(dir);
        return new Run(engine, stats, export, adapters);
    }

    /// <summary>The sum of the signed deltas of every patch of a graph, as a delta.</summary>
    static Delta Integrated(Run r, string graph) => r.Export.PatchesOf(graph).Aggregate(new Delta(), (d, p) => d + p.Delta());

    [Fact]
    public void The_estate_replays_into_a_master_graph_and_a_service_tree() => A_capture_replays_into_a_master_graph_and_a_service_tree(Repo.Estate);

    [Trait("Category", "Fixtures")]
    [FixturesFact]
    public void Fixtures_replay_into_a_master_graph_and_a_service_tree() => A_capture_replays_into_a_master_graph_and_a_service_tree(Repo.Fixtures);

    static void A_capture_replays_into_a_master_graph_and_a_service_tree(string dir)
    {
        var r = Replay(dir);
        Assert.Equal(0, r.Stats.Rejected);
        string[] sources = ["flows", "pods", "resources", "metrics", "events", "activity", "resourceMetrics"];
        Assert.All(sources, s => Assert.True(r.Stats.Rows.GetValueOrDefault(s) > 0, $"no rows of source {s}"));
        Assert.True(r.Engine.Master.State.EntityCount > 0);
        Assert.True(r.Engine.Graph("serviceTree").State.EntityCount > 0);
        var cat = Repo.LoadExamples(out _);
        Assert.Empty(Conformance.Check(r.Engine.Master.State, cat.OntologyOf("acme.ops")));
        Assert.Empty(Conformance.Check(r.Engine.Graph("serviceTree").State, cat.OntologyOf("acme.svc")));
    }

    [Fact]
    public void T7_replay_is_deterministic_same_inputs_same_patches_and_digests_on_the_estate() => T7_replay_is_deterministic_same_inputs_same_patches_and_digests(Repo.Estate);

    [Trait("Category", "Fixtures")]
    [FixturesFact]
    public void T7_replay_is_deterministic_same_inputs_same_patches_and_digests_on_the_fixtures() => T7_replay_is_deterministic_same_inputs_same_patches_and_digests(Repo.Fixtures);

    static void T7_replay_is_deterministic_same_inputs_same_patches_and_digests(string dir)
    {
        var a = Replay(dir); var b = Replay(dir);
        Assert.Equal(a.Engine.StateDigest(), b.Engine.StateDigest());
        foreach (var g in a.Engine.Graphs)
        {
            var pa = a.Export.PatchesOf(g.Name); var pb = b.Export.PatchesOf(g.Name);
            Assert.Equal(pa.Count, pb.Count);
            for (var i = 0; i < pa.Count; i++) { Assert.Equal(pa[i], pb[i]); Assert.Equal(pa[i].Delta().Encode(), pb[i].Delta().Encode()); }
        }
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(7)] [InlineData(50)] [InlineData(100000)]
    public void T6_batch_invariance_chunking_changes_patches_but_not_the_result_on_the_estate(int chunk) => T6_batch_invariance_chunking_changes_patches_but_not_the_result(Repo.Estate, chunk);

    [Trait("Category", "Fixtures")]
    [FixturesTheory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(7)] [InlineData(50)] [InlineData(100000)]
    public void T6_batch_invariance_chunking_changes_patches_but_not_the_result_on_the_fixtures(int chunk) => T6_batch_invariance_chunking_changes_patches_but_not_the_result(Repo.Fixtures, chunk);

    static void T6_batch_invariance_chunking_changes_patches_but_not_the_result(string dir, int chunk)
    {
        var reference = Replay(dir, chunk: 2000);
        var r = Replay(dir, chunk: chunk);
        Assert.Equal(reference.Engine.StateDigest(), r.Engine.StateDigest());
        foreach (var g in r.Engine.Graphs)
        {
            Assert.Equal(g.State.Digest(), reference.Engine.Graph(g.Name).State.Digest());
            Assert.Equal(Integrated(reference, g.Name), Integrated(r, g.Name));                      // integrated output equals batch output (Quine invariant)
            Assert.Equal(Delta.Chi(g.State), Integrated(r, g.Name));                                  // and equals the final graph
        }
    }

    // ---- synthetic captures: no real identifiers ----

    static string Line(string source, string at, long seq, object payload, object? meta = null) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?> { ["v"] = 1, ["source"] = source, ["capturedAt"] = at, ["seq"] = seq, ["key"] = $"{source}/{seq}", ["meta"] = meta, ["payload"] = payload });

    static string Pod(string type, string uid, string name, string phase, string rv) => Line("kubernetes-watch", "2026-11-10T10:00:01.000Z", long.Parse(rv),
        new Dictionary<string, object> { ["type"] = type, ["object"] = new Dictionary<string, object> { ["kind"] = "Pod", ["metadata"] = new Dictionary<string, object> { ["uid"] = uid, ["name"] = name, ["namespace"] = "ns1", ["resourceVersion"] = rv }, ["status"] = new { phase } } });

    static string Flow(string at, string src, string dst, string verdict = "FORWARDED", string? time = null) => Line("hubble", at, 1,
        new { flow = new { time = time ?? at, verdict, source = new { namespace_ = "x", cluster_name = "synth", pod_name = src }, destination = new { namespace_ = "x", cluster_name = "synth", pod_name = dst } }, node_name = "n1", time = at }).Replace("namespace_", "namespace");

    static string Capture(params string[] lines)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "x"));
        File.WriteAllLines(Path.Combine(dir, "x", "a.ndjson"), lines);
        return dir;
    }

    [Fact]
    public void Pod_lifecycle_from_watch_events_creates_updates_and_removes_the_entity_with_before_and_after()
    {
        var dir = Capture(Pod("ADDED", "u1", "web-1", "Pending", "1"), Pod("MODIFIED", "u1", "web-1", "Running", "2"), Pod("DELETED", "u1", "web-1", "Running", "3"));
        var r = Replay(dir, chunk: 1);
        var patches = r.Export.PatchesOf("master");
        Assert.Equal(3, patches.Count);
        Assert.Null(patches[0].Edits.Single(e => e.Key.Kind == 'e').Before);
        var change = patches[1].Edits.Single(e => e.Key.Kind == 'e');
        Assert.Equal("Pending", ((Entity)change.Before!).Props.Text("d:sattle.infra/prop:phase"));
        Assert.Equal("Running", ((Entity)change.After!).Props.Text("d:sattle.infra/prop:phase"));
        Assert.Null(patches[2].Edits.Single(e => e.Key.Kind == 'e').After);
        Assert.Equal(0, r.Engine.Master.State.EntityCount);
    }

    static string K8s(long seq, string type, string kind, string name, string ns, string spec) => Line("kubernetes-watch", $"2026-11-10T10:00:{seq:00}.000Z", seq, System.Text.Json.Nodes.JsonNode.Parse(
        $$$"""{"type": "{{{type}}}", "object": {"kind": "{{{kind}}}", "metadata": {"uid": "u-{{{kind}}}-{{{name}}}", "name": "{{{name}}}", "namespace": "{{{ns}}}", "resourceVersion": "{{{seq}}}"}, "spec": """ + spec + "}}")!);
    static string Deployment(long seq, string labels, string ns = "ns1", string type = "ADDED") => K8s(seq, type, "Deployment", "web", ns, """{"replicas": 0, "template": {"metadata": {"labels": """ + labels + "}}}");
    static List<string> Selects(Engine e) => [.. e.Master.State.Rels.Where(r => Ids.Name(r.Type) == "Selects").Select(r => e.Master.State.Entity(r.Source)!.Props.Text("d:sattle.infra/prop:name") + " -> " + e.Master.State.Entity(r.Target)!.Props.Text("d:sattle.infra/prop:name"))];

    /// <summary>A Service selects a Deployment of its namespace when the Deployment's pod template labels hold every pair of the Service's selector (Kubernetes label selection), whether the Deployment runs pods or not.</summary>
    [Theory]
    [InlineData("labels hold the selector", """{"selector": {"app": "web"}}""", """{"app": "web", "tier": "shop"}""", "ns1", true)]
    [InlineData("a value differs", """{"selector": {"app": "web"}}""", """{"app": "api"}""", "ns1", false)]
    [InlineData("a selector key is missing", """{"selector": {"app": "web", "tier": "shop"}}""", """{"app": "web"}""", "ns1", false)]
    [InlineData("another namespace", """{"selector": {"app": "web"}}""", """{"app": "web"}""", "ns2", false)]
    [InlineData("no selector", """{}""", """{"app": "web"}""", "ns1", false)]
    [InlineData("an empty selector", """{"selector": {}}""", """{"app": "web"}""", "ns1", false)]
    [InlineData("an ExternalName Service", """{"type": "ExternalName", "externalName": "db.example", "selector": {"app": "web"}}""", """{"app": "web"}""", "ns1", false)]
    public void A_Service_selects_the_Deployments_of_its_namespace_whose_pod_template_labels_hold_its_selector(string @case, string serviceSpec, string labels, string ns, bool selects)
    {
        var e = Replay(Capture(K8s(1, "ADDED", "Service", "web", "ns1", serviceSpec), Deployment(2, labels, ns)), chunk: 1).Engine;
        Assert.True(selects == (Selects(e).Count == 1), @case);
    }

    /// <summary>The selection follows the objects: it goes when the selector stops matching or the Deployment is deleted, and comes back when the selector matches again.</summary>
    [Fact]
    public void A_selection_follows_changes_of_the_selector_and_the_Deployment()
    {
        string Service(long seq, string app, string type = "MODIFIED") => K8s(seq, type, "Service", "web", "ns1", $$$"""{"selector": {"app": "{{{app}}}"}}""");
        var steps = new (string Line, string[] Expected)[]
        {
            (Service(1, "web", "ADDED"), []), (Deployment(2, """{"app": "web"}"""), ["web -> web"]), (Service(3, "api"), []), (Service(4, "web"), ["web -> web"]),
            (Deployment(5, """{"app": "web"}""", type: "DELETED"), []),
        };
        for (var i = 1; i <= steps.Length; i++)
            Assert.Equal(steps[i - 1].Expected, Selects(Replay(Capture([.. steps.Take(i).Select(s => s.Line)]), chunk: 1).Engine));
    }

    /// <summary>Selection is matched per namespace and counted per (Service, Deployment) pair (#108): two Services that select one Deployment give two rows, a Deployment of another namespace adds none, deleting one Service takes back only its row, and a Service delivered again unchanged changes nothing.</summary>
    [Fact]
    public void Selections_of_several_Services_and_Deployments_follow_each_object_in_its_namespace()
    {
        string Service(long seq, string name, string type = "ADDED") => K8s(seq, type, "Service", name, "ns1", """{"selector": {"app": "web"}}""");
        var steps = new (string Line, string[] Expected)[]
        {
            (Service(1, "web"), []), (Service(2, "web2"), []), (Deployment(3, """{"app": "web"}"""), ["web -> web", "web2 -> web"]),
            (K8s(4, "ADDED", "Deployment", "web-b", "ns2", """{"replicas": 0, "template": {"metadata": {"labels": {"app": "web"}}}}"""), ["web -> web", "web2 -> web"]),
            (Service(5, "web2", "DELETED"), ["web -> web"]), (Service(6, "web", "MODIFIED"), ["web -> web"]),
        };
        for (var i = 1; i <= steps.Length; i++)
            Assert.Equal(steps[i - 1].Expected, Selects(Replay(Capture([.. steps.Take(i).Select(s => s.Line)]), chunk: 1).Engine).Order(StringComparer.Ordinal));
    }

    /// <summary>The rows of a type no source binds are never read (#108): their texts stay out of the engine's interner, which only grows, so a source the space does not bind costs no memory however much it sends.</summary>
    [Fact]
    public void The_texts_of_dropped_rows_stay_out_of_the_interner()
    {
        var dir = Capture(Flow("2026-11-10T10:00:01.000Z", "a", "b"));
        var cat = Repo.CatalogFor(dir, out _); var space = cat.Spaces.Keys.Single();
        var adapters = AdapterRegistry.Create("synth").Select(a => a is HubbleAdapter ? new UnboundRow(a) : a).ToList();
        var engine = new Engine(cat, space);
        var stats = new Replay(engine, cat.Spaces[space], adapters, new ReplayOptions { ChunkRows = 1, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" }).Run(dir);
        Assert.Equal(1, stats.Dropped["hubble.observe|UnboundRow"]);
        Assert.False(engine.Interner.TryFind("only-in-an-unbound-row", out _));
    }

    /// <summary>An adapter that also delivers a row of a type no source binds, with a text no other row has.</summary>
    sealed class UnboundRow(ISourceAdapter inner) : ISourceAdapter
    {
        public string Id => inner.Id;
        public IReadOnlyList<RowContract> Contracts => [.. inner.Contracts, new RowContract("UnboundRow", [new RowField("note", "Text", false)])];
        public bool Handles(string s) => inner.Handles(s);
        public void Consume(Envelope e, IRowSink sink) { inner.Consume(e, sink); var w = sink.Begin("UnboundRow"); w.Begin(1); w.Text("note", "only-in-an-unbound-row"); }
    }

    /// <summary>A space binds what it reads (Def. 5.8, #107). The space from before the adapter delivered `SelectsRow` (#104: its ingestion reads no selections and no source binds them) replays a capture with a Service that selects a Deployment: the rows of the unbound type are dropped and counted on `GET /space`, and the Service and the Deployment are ingested as before.</summary>
    [Theory]
    [InlineData("the space binds selections", false, "[]", 1)]
    [InlineData("the space from before #104", true, """[{"adapter":"kubernetes.list-watch","rowType":"SelectsRow","rows":"1"}]""", 0)]
    public void A_row_type_no_source_of_the_space_binds_is_dropped_and_counted(string @case, bool older, string dropped, int selects)
    {
        var files = Catalog.ReadDirectory(Repo.Definitions).Select(f => older ? f with { Text = string.Join('\n', f.Text.Replace(" selections: selections,", "").Split('\n').Where(l => !l.Contains("selections", StringComparison.Ordinal))) } : f).ToList();
        using var h = new SpaceHost(files, new HostOptions { Projections = new Sattle.Kql.KqlSource(), Cluster = "synth" });
        var stats = h.RunCapture(Capture(K8s(1, "ADDED", "Service", "web", "ns1", """{"selector": {"app": "web"}}"""), Deployment(2, """{"app": "web"}""")), new ReplayOptions { ChunkRows = 1, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" });
        Assert.Equal(0, stats.Rejected);
        Assert.Equal(dropped, h.SpaceInfo()["input"]!["dropped"]!.ToJsonString());
        Assert.True(selects == Selects(h.Engine).Count, @case);
        Assert.All(new[] { "Service", "Deployment" }, t => Assert.Contains(h.Engine.Master.State.Entities, x => Ids.Name(x.Type) == t));
    }

    [Fact]
    public void Flows_between_unobserved_pods_make_dangling_calls_and_never_a_revision_per_window()
    {
        var lines = new List<string>();
        for (var i = 0; i < 40; i++) lines.Add(Flow($"2026-11-10T10:00:{i + 10:D2}.000Z".Replace(":60", ":59"), "a-1", "b-1"));
        var r = Replay(Capture(lines.ToArray()));
        Assert.Equal(1, r.Engine.Master.State.RelCount);
        Assert.Equal(0, r.Engine.Master.State.EntityCount);              // dangling endpoints (ADR-084)
        Assert.Equal(1, r.Engine.Master.Head);
    }

    /// <summary>Code review 2, defect 4: Hubble reports the answer of a connection as a flow from the callee back to the caller, marked `is_reply`. The adapter writes it from the caller to the callee, so a call and its answer are one `Calls` relationship. Before, the answer made a second relationship in the other direction. A flow that is no answer, or carries no mark, keeps its ends.</summary>
    [Theory]
    [InlineData("true", 1)] [InlineData("false", 2)] [InlineData(null, 2)]
    public void The_answer_of_a_connection_counts_on_the_call_and_makes_no_call_back(string? isReply, int relationships)
    {
        const string at = "2026-11-10T10:00:20.000Z";
        var back = Flow(at, "b-1", "a-1");
        if (isReply is not null) back = back.Replace("\"verdict\":", $"\"is_reply\":{isReply},\"verdict\":");
        var r = Replay(Capture(Flow("2026-11-10T10:00:10.000Z", "a-1", "b-1"), back));
        var oneWay = Replay(Capture(Flow("2026-11-10T10:00:10.000Z", "a-1", "b-1"), Flow(at, "a-1", "b-1")));
        Assert.Equal(relationships, r.Engine.Master.State.RelCount);
        Assert.Equal(relationships == 1, oneWay.Engine.Master.State.SameAs(r.Engine.Master.State));          // the same relationship as two flows from a to b
        Assert.Equal(relationships == 1, oneWay.Engine.StateDigest() == r.Engine.StateDigest());            // and the same telemetry: both flows count on it
    }

    /// <summary>Hubble writes the count of lost events as a JSON string (a uint64); a number is read the same way.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lost_event_records_mark_the_window_partial_and_are_counted(bool asText)
    {
        var lost = Line("hubble", "2026-11-10T10:00:30.000Z", 2, new { lost_events = new { source = "HUBBLE_RING_BUFFER", num_events_lost = asText ? (object)"7" : 7, cpu = 1 }, node_name = "n1", time = "2026-11-10T10:00:30.000Z" });
        var r = Replay(Capture(Flow("2026-11-10T10:00:10.000Z", "a-1", "b-1"), lost, Flow("2026-11-10T10:00:50.000Z", "a-1", "b-1")));
        var hub = r.Adapters.OfType<HubbleAdapter>().Single();
        Assert.Equal(1, hub.LostEventRecords); Assert.Equal(7, hub.LostEvents); Assert.Equal(7, r.Stats.LostEvents);
        var partial = ((InMemoryTelemetryStore)r.Engine.Telemetry).PartialWindows(r.Engine.Master.Key);
        Assert.Single(partial);
        Assert.Equal(("sourceLoss", 7L), partial.Values.Single());
        var read = r.Engine.Telemetry.ReadWindow(r.Engine.Master.Key, [.. ((InMemoryTelemetryStore)r.Engine.Telemetry).Dump(r.Engine.Master.Key).Select(x => x.Element).Distinct()], [.. ((InMemoryTelemetryStore)r.Engine.Telemetry).Dump(r.Engine.Master.Key).Select(x => x.Prop).Distinct()], 0, long.MaxValue);
        Assert.Contains(read, x => x.Completeness == "Partial(sourceLoss)");
    }

    [Fact]
    public void A_late_flow_revises_its_window_and_the_result_equals_the_in_order_run()
    {
        // 10 flows in minute 0 arrive, minute 2 arrives (closing minute 0), then 5 late flows for minute 0
        var inOrder = new List<string>(); var late = new List<string>();
        for (var i = 0; i < 15; i++) inOrder.Add(Flow($"2026-11-10T10:00:{10 + i:D2}.000Z", "a-1", "b-1"));
        inOrder.Add(Flow("2026-11-10T10:03:20.000Z", "a-1", "b-1"));
        late.AddRange(inOrder.Take(10)); late.Add(Flow("2026-11-10T10:03:20.000Z", "a-1", "b-1"));
        late.AddRange(inOrder.Skip(10).Take(5).Select((l, i) => Flow("2026-11-10T10:03:30.000Z", "a-1", "b-1", time: $"2026-11-10T10:00:{25 + i:D2}.000Z")));
        var a = Replay(Capture([.. inOrder]));
        var b = Replay(Capture([.. late]));
        string W(Run r) => string.Join(";", ((InMemoryTelemetryStore)r.Engine.Telemetry).Dump(r.Engine.Master.Key).Select(x => $"{x.Ws}:{r.Engine.Interner.Get(x.Prop)[^5..]}:{x.Value}").Order());
        Assert.Equal(W(a), W(b));
        Assert.Contains("15", W(b));
    }

    [Fact]
    public void Plane_separation_telemetry_for_an_existing_relationship_creates_no_revision()
    {
        var lines = new List<string> { Flow("2026-11-10T10:00:10.000Z", "a-1", "b-1") };
        lines.AddRange(Enumerable.Range(0, 5).Select(i => Flow($"2026-11-10T10:0{i + 1}:10.000Z", "a-1", "b-1")));
        var r = Replay(Capture([.. lines]), chunk: 1);
        Assert.Equal(1, r.Engine.Master.Head);
        Assert.True(((InMemoryTelemetryStore)r.Engine.Telemetry).Dump(r.Engine.Master.Key).Count >= 10);
    }

    [Fact]
    public void Contract_check_rejects_an_adapter_whose_row_contract_differs_from_the_catalog()
    {
        var cat = Repo.LoadExamples(out _);
        var space = cat.Spaces.Values.Single();
        var good = AdapterRegistry.Create("c").ToList();
        Assert.Empty(AdapterRegistry.Verify(cat, space, good));
        Assert.Contains(AdapterRegistry.Verify(cat, space, good.Where(a => a.Id != "hubble.observe")), d => d.Code == "AdapterUnknown");
        var changed = good.Select(a => a is HubbleAdapter ? new ChangedContract(a) : a).ToList();
        Assert.Contains(AdapterRegistry.Verify(cat, space, changed), d => d.Code == "AdapterContract");
    }

    sealed class ChangedContract(ISourceAdapter inner) : ISourceAdapter
    {
        public string Id => inner.Id;
        public IReadOnlyList<RowContract> Contracts => [new RowContract("FlowRow", [new RowField("time", "Instant", false)])];
        public bool Handles(string s) => inner.Handles(s);
        public void Consume(Envelope e, IRowSink sink) => inner.Consume(e, sink);
    }
}
