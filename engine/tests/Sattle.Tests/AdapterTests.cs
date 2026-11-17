using System.Text.Json;
using System.Text.Json.Nodes;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The adapters of the lab's sources (Ch. 10, lab/CAPTURE-FORMAT.md): envelopes of a capture in, served graph out. Each test writes a small capture with invented names, a host on the example definitions replays it as `sattle serve DIR` does, and the test reads what the host serves: snapshots, patches, the telemetry range read and the counters of the run. Sections: Kubernetes objects and selection, Hubble flows, Azure and Kubernetes evidence, authored rows, Prometheus, the forwarder's markers, the contract check.</summary>
public class AdapterTests
{
    const long Min = 60_000_000_000;

    static string Line(string source, string at, long seq, object payload, object? meta = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["v"] = 1, ["source"] = source, ["capturedAt"] = at, ["seq"] = seq, ["key"] = $"{source}/{seq}", ["meta"] = meta, ["payload"] = payload });

    static string Capture(IEnumerable<string> lines)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-adapters-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "x"));
        File.WriteAllLines(Path.Combine(dir, "x", "a.ndjson"), lines);
        return dir;
    }

    static ReplayOptions Options(int chunk) => new() { ChunkRows = chunk, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" };

    /// <summary>The host replays a capture of these envelopes.</summary>
    static ReplayStats Run(SpaceHost h, IEnumerable<string> lines, int chunk = 1)
    {
        var dir = Capture(lines);
        try { return h.RunCapture(dir, Options(chunk)); }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A host on the example definitions that has replayed a capture of these envelopes.</summary>
    static SpaceHost Serve(IEnumerable<string> lines, out ReplayStats stats, int chunk = 1)
    {
        var h = HostTests.NewHost();
        stats = Run(h, lines, chunk);
        return h;
    }

    static SpaceHost Serve(IEnumerable<string> lines, int chunk = 1) => Serve(lines, out _, chunk);

    // what was observed is in the master, what people wrote (signal rules, capabilities) in `authored`
    static List<JsonNode> Of(SpaceHost h, string type) => [.. h.Entities("master", type), .. h.Entities("authored", type)];

    static JsonNode Telemetry(SpaceHost h) => h.Telemetry("master", null, null, null, null, batches: false);

    // ---- Kubernetes objects and the selection of Deployments by Services ----------------------------------------------------------------

    static string Pod(string type, string uid, string name, string phase, string rv) => Line("kubernetes-watch", "2026-11-10T10:00:01.000Z", long.Parse(rv),
        new Dictionary<string, object> { ["type"] = type, ["object"] = new Dictionary<string, object> { ["kind"] = "Pod", ["metadata"] = new Dictionary<string, object> { ["uid"] = uid, ["name"] = name, ["namespace"] = "ns1", ["resourceVersion"] = rv }, ["status"] = new { phase } } });

    [Fact]
    public void Pod_lifecycle_from_watch_events_creates_updates_and_removes_the_entity_with_before_and_after()
    {
        using var h = Serve([Pod("ADDED", "u1", "web-1", "Pending", "1"), Pod("MODIFIED", "u1", "web-1", "Running", "2"), Pod("DELETED", "u1", "web-1", "Running", "3")]);
        var edits = h.Patches("master", 0).Select(p => p["edits"]!.AsArray().Single(e => e!["kind"]!.GetValue<string>() == "entity")!).ToList();
        Assert.Equal(3, edits.Count);
        Assert.Null(edits[0]["before"]);
        Assert.Equal("Pending", edits[1]["before"]!.Prop("phase"));
        Assert.Equal("Running", edits[1]["after"]!.Prop("phase"));
        Assert.Null(edits[2]["after"]);
        Assert.Empty(h.Entities("master"));
    }

    static string K8s(long seq, string type, string kind, string name, string ns, string spec) => Line("kubernetes-watch", $"2026-11-10T10:00:{seq:00}.000Z", seq, JsonNode.Parse(
        $$$"""{"type": "{{{type}}}", "object": {"kind": "{{{kind}}}", "metadata": {"uid": "u-{{{kind}}}-{{{name}}}", "name": "{{{name}}}", "namespace": "{{{ns}}}", "resourceVersion": "{{{seq}}}"}, "spec": """ + spec + "}}")!);
    static string Deployment(long seq, string labels, string ns = "ns1", string type = "ADDED") => K8s(seq, type, "Deployment", "web", ns, """{"replicas": 0, "template": {"metadata": {"labels": """ + labels + "}}}");

    /// <summary>The selections the master serves, as `Service -> Deployment` by name.</summary>
    static List<string> Selects(SpaceHost h)
    {
        var names = h.Entities("master").ToDictionary(e => e["id"].Id(), e => e.Prop("name"));
        return [.. h.Relationships("master", "Selects").Select(r => names[r["source"].Id()] + " -> " + names[r["target"].Id()])];
    }

    static List<string> SelectsAfter(IEnumerable<string> lines) { using var h = Serve(lines); return Selects(h); }

    /// <summary>A Service selects a Deployment of its namespace when the Deployment's pod template labels hold every pair of the Service's selector (Kubernetes label selection), whether the Deployment runs pods or not.</summary>
    [Theory]
    [InlineData("labels hold the selector", """{"selector": {"app": "web"}}""", """{"app": "web", "tier": "shop"}""", "ns1", true)]
    [InlineData("a value differs", """{"selector": {"app": "web"}}""", """{"app": "api"}""", "ns1", false)]
    [InlineData("a selector key is missing", """{"selector": {"app": "web", "tier": "shop"}}""", """{"app": "web"}""", "ns1", false)]
    [InlineData("another namespace", """{"selector": {"app": "web"}}""", """{"app": "web"}""", "ns2", false)]
    [InlineData("no selector", """{}""", """{"app": "web"}""", "ns1", false)]
    [InlineData("an empty selector", """{"selector": {}}""", """{"app": "web"}""", "ns1", false)]
    [InlineData("an ExternalName Service", """{"type": "ExternalName", "externalName": "db.example", "selector": {"app": "web"}}""", """{"app": "web"}""", "ns1", false)]
    public void A_Service_selects_the_Deployments_of_its_namespace_whose_pod_template_labels_hold_its_selector(string @case, string serviceSpec, string labels, string ns, bool selects) =>
        Assert.True(selects == (SelectsAfter([K8s(1, "ADDED", "Service", "web", "ns1", serviceSpec), Deployment(2, labels, ns)]).Count == 1), @case);

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
            Assert.Equal(steps[i - 1].Expected, SelectsAfter(steps.Take(i).Select(s => s.Line)));
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
            Assert.Equal(steps[i - 1].Expected, SelectsAfter(steps.Take(i).Select(s => s.Line)).Order(StringComparer.Ordinal));
    }

    /// <summary>A space binds what it reads (Def. 5.8, #107). The space from before the adapter delivered `SelectsRow` (#104: its ingestion reads no selections and no source binds them) replays a capture with a Service that selects a Deployment: the rows of the unbound type are dropped and counted on `GET /space`, and the Service and the Deployment are ingested as before.</summary>
    [Theory]
    [InlineData("the space binds selections", false, "[]", 1)]
    [InlineData("the space from before #104", true, """[{"adapter":"kubernetes.list-watch","rowType":"SelectsRow","rows":"1"}]""", 0)]
    public void A_row_type_no_source_of_the_space_binds_is_dropped_and_counted(string @case, bool older, string dropped, int selects)
    {
        var files = Catalog.ReadDirectory(Repo.Definitions).Select(f => older ? f with { Text = string.Join('\n', f.Text.Replace(" selections: selections,", "").Split('\n').Where(l => !l.Contains("selections", StringComparison.Ordinal))) } : f).ToList();
        using var h = new SpaceHost(files, new HostOptions { Projections = Repo.Projections, Cluster = "synth" });
        var stats = Run(h, [K8s(1, "ADDED", "Service", "web", "ns1", """{"selector": {"app": "web"}}"""), Deployment(2, """{"app": "web"}""")]);
        Assert.Equal(0, stats.Rejected);
        Assert.Equal(dropped, h.SpaceInfo()["input"]!["dropped"]!.ToJsonString());
        Assert.True(selects == Selects(h).Count, @case);
        Assert.All(new[] { "Service", "Deployment" }, t => Assert.Single(h.Entities("master", t)));
    }

    /// <summary>The rows of a type no source binds are never read (#108): their texts stay out of the engine's interner, which only grows, so a source the space does not bind costs no memory however much it sends. The one test of this class that looks inside: no answer of the protocol shows which texts the engine holds.</summary>
    [Fact]
    public void The_texts_of_dropped_rows_stay_out_of_the_interner()
    {
        var dir = Capture([Flow("2026-11-10T10:00:01.000Z", "a", "b")]);
        var cat = Repo.CatalogFor(dir, out _); var space = cat.Spaces.Keys.Single();
        var adapters = AdapterRegistry.Create("synth").Select(a => a is HubbleAdapter ? new UnboundRow(a) : a).ToList();
        using var engine = new Engine(cat, space);
        var stats = new Replay(engine, cat.Spaces[space], adapters, Options(1)).Run(dir);
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

    // ---- Hubble flows ---------------------------------------------------------------------------------------------------------------------

    static string Flow(string at, string src, string dst, string verdict = "FORWARDED", string? time = null) => Line("hubble", at, 1,
        new { flow = new { time = time ?? at, verdict, source = new { namespace_ = "x", cluster_name = "synth", pod_name = src }, destination = new { namespace_ = "x", cluster_name = "synth", pod_name = dst } }, node_name = "n1", time = at }).Replace("namespace_", "namespace");

    /// <summary>Flows between pods no source has reported make one `Calls` relationship whose ends dangle (ADR-084). Its flows of six minutes are telemetry of that relationship: the master has one revision, the one that created it, and none per window (Theorem 7.1 at the adapter; the scenarios assert it on recorded input).</summary>
    [Fact]
    public void Flows_between_unobserved_pods_make_one_dangling_call_and_never_a_revision_per_window()
    {
        using var h = Serve(Enumerable.Range(0, 24).Select(i => Flow($"2026-11-10T10:0{i / 4}:{10 + i % 4 * 10}.000Z", "a-1", "b-1")));
        Assert.Single(h.Relationships("master"));
        Assert.Empty(h.Entities("master"));
        Assert.Equal(1, h.Head("master"));
        var flows = h.Windows("master", "flows");
        Assert.True(flows.Count >= 5, $"{flows.Count} windows");
        Assert.All(flows.Take(5), x => Assert.Equal(4m, x.Value));
    }

    /// <summary>Code review 2, defect 4: Hubble reports the answer of a connection as a flow from the callee back to the caller, marked `is_reply`. The adapter writes it from the caller to the callee, so a call and its answer are one `Calls` relationship. Before, the answer made a second relationship in the other direction. A flow that is no answer, or carries no mark, keeps its ends.</summary>
    [Theory]
    [InlineData("true", 1)] [InlineData("false", 2)] [InlineData(null, 2)]
    public void The_answer_of_a_connection_counts_on_the_call_and_makes_no_call_back(string? isReply, int relationships)
    {
        const string at = "2026-11-10T10:00:20.000Z";
        var back = Flow(at, "b-1", "a-1");
        if (isReply is not null) back = back.Replace("\"verdict\":", $"\"is_reply\":{isReply},\"verdict\":");
        using var h = Serve([Flow("2026-11-10T10:00:10.000Z", "a-1", "b-1"), back], chunk: 2000);
        using var oneWay = Serve([Flow("2026-11-10T10:00:10.000Z", "a-1", "b-1"), Flow(at, "a-1", "b-1")], chunk: 2000);
        static string Extent(SpaceHost x) => x.Snapshot("master", null)["extent"]!.ToJsonString();
        Assert.Equal(relationships, h.Relationships("master").Count);
        Assert.Equal(relationships == 1, Extent(oneWay) == Extent(h));                                        // the same relationship as two flows from a to b
        Assert.Equal(relationships == 1, oneWay.StateDigest() == h.StateDigest());                            // and the same telemetry: both flows count on it
    }

    /// <summary>Hubble writes the count of lost events as a JSON string (a uint64); a number is read the same way. The window that contains the record is served as partial, with the reason and the count.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lost_event_records_mark_the_window_partial_and_are_counted(bool asText)
    {
        var lost = Line("hubble", "2026-11-10T10:00:30.000Z", 2, new { lost_events = new { source = "HUBBLE_RING_BUFFER", num_events_lost = asText ? (object)"7" : 7, cpu = 1 }, node_name = "n1", time = "2026-11-10T10:00:30.000Z" });
        using var h = Serve([Flow("2026-11-10T10:00:10.000Z", "a-1", "b-1"), lost, Flow("2026-11-10T10:00:50.000Z", "a-1", "b-1")], out var stats, chunk: 2000);
        Assert.Equal(7, stats.LostEvents);
        var partial = Assert.Single(Telemetry(h)["partialWindows"]!.AsArray());
        Assert.Equal(("sourceLoss", "7"), (partial!["reason"]!.GetValue<string>(), partial["count"]!.GetValue<string>()));
        Assert.Contains(Telemetry(h)["series"]!.AsArray(), s => s!["completeness"]!.GetValue<string>() == "Partial(sourceLoss)");
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
        using var a = Serve(inOrder, chunk: 2000);
        using var b = Serve(late, chunk: 2000);
        Assert.Equal(Telemetry(a)["series"]!.ToJsonString(), Telemetry(b)["series"]!.ToJsonString());
        Assert.Contains(b.Windows("master", "flows"), w => w.Value == 15m);
    }

    // ---- evidence: Event Hubs records (diagnostic settings), the Activity Log, warning events ---------------------------------------------

    const string Rid = "/SUBSCRIPTIONS/S/RESOURCEGROUPS/RG/PROVIDERS/MICROSOFT.STORAGE/STORAGEACCOUNTS/ACCT";

    static object ActivityRecord(string op, string result, string time, string corr) => new Dictionary<string, object> { ["category"] = "Administrative", ["operationName"] = op, ["time"] = time, ["resultType"] = result, ["resourceId"] = Rid, ["correlationId"] = corr };

    static string Audit(string id, string verb, string resource, string stage, string name) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["auditID"] = id, ["verb"] = verb, ["stage"] = stage, ["objectRef"] = new { resource, @namespace = "kube-system", name }, ["user"] = new { username = "alice" }, ["responseStatus"] = new { code = 200 }, ["requestReceivedTimestamp"] = "2026-01-01T10:00:05.000000Z",
    });

    /// <summary>Event Hubs records and warning events become evidence elements in the master graph, one element per operation, not only counts.</summary>
    [Fact]
    public void Diagnostic_activity_records_are_evidence_elements_linked_to_their_resource()
    {
        var records = new[] { ActivityRecord("MICROSOFT.STORAGE/STORAGEACCOUNTS/WRITE", "Start", "2026-01-01T10:00:01Z", "c1"), ActivityRecord("MICROSOFT.STORAGE/STORAGEACCOUNTS/WRITE", "Success", "2026-01-01T10:00:02Z", "c1") };
        using var h = Serve([Line("eventhub:sattle-activity", "2026-01-01T10:01:00.000Z", 1, new { records })]);
        var events = Of(h, "ActivityEvent");
        Assert.Equal(["Started", "Succeeded"], events.Select(x => x.Prop("status")).Order());
        Assert.All(events, x => Assert.Equal("microsoft.storage/storageaccounts/write", x.Prop("operation")));      // one spelling, whichever source delivered the event (the diagnostic setting writes upper case)
        Assert.Equal(2, h.Relationships("master", "Concerns").Count);                          // both point at the one resource identity, whatever the id's case
    }

    static object ManagementEvent(string op, string status, string time, string corr, string dataId) => new Dictionary<string, object>
    {
        ["eventTimestamp"] = time, ["resourceId"] = Rid.ToLowerInvariant(), ["status"] = new { value = status }, ["operationName"] = new { value = op }, ["correlationId"] = corr, ["eventDataId"] = dataId, ["caller"] = "operator@example.invalid",
    };

    /// <summary>An Activity Log event is one event however it was delivered: the management API and the diagnostic setting give it the same identity (request, operation, resource, status and its own time), so it is one element with one time and counts once. The same operation of the same request reported again later is another event.</summary>
    [Theory]
    [InlineData("2026-01-01T10:00:02Z", 1, 1)]             // the same event from both sources
    [InlineData("2026-01-01T10:00:09Z", 2, 2)]             // the same operation again, seven seconds later
    public void An_activity_event_delivered_by_both_sources_is_one_event_with_one_time(string managementTime, int events, long changes)
    {
        using var h = Serve(
        [
            Line("eventhub:sattle-activity", "2026-01-01T10:01:00.000Z", 1, new { records = new[] { ActivityRecord("MICROSOFT.STORAGE/STORAGEACCOUNTS/WRITE", "Success", "2026-01-01T10:00:02Z", "c1") } }),
            Line("activity-log", "2026-01-01T10:01:01.000Z", 2, ManagementEvent("Microsoft.Storage/storageAccounts/write", "Succeeded", managementTime, "C1", "d1")),
        ]);
        var found = Of(h, "ActivityEvent");
        Assert.Equal(events, found.Count);
        Assert.All(found, x => Assert.Equal("instant", x.Value("time")!["kind"]!.GetValue<string>()));
        Assert.Contains(found, x => x.Prop("caller") == "operator@example.invalid");
        Assert.Single(h.Relationships("master", "Concerns").Select(r => r["target"].Id()).Distinct());      // the upper-case id of one source and the lower-case id of the other are one resource
        Assert.Equal(changes, (long)h.Windows("master", "changes").Sum(w => w.Value));
    }

    [Fact]
    public void Aks_audit_records_become_audit_events_and_lease_renewals_and_partial_stages_are_left_out()
    {
        var records = new object[]
        {
            new Dictionary<string, object> { ["category"] = "kube-audit-admin", ["properties"] = new { log = Audit("a1", "delete", "pods", "ResponseComplete", "web-1") } },
            new Dictionary<string, object> { ["category"] = "kube-audit-admin", ["properties"] = new { log = Audit("a2", "update", "leases", "ResponseComplete", "lock") } },
            new Dictionary<string, object> { ["category"] = "kube-audit-admin", ["properties"] = new { log = Audit("a3", "create", "pods", "RequestReceived", "web-2") } },
            new Dictionary<string, object> { ["category"] = "cluster-autoscaler", ["properties"] = new { log = "not json at all" } },
        };
        using var h = Serve([Line("eventhub:sattle-aks", "2026-01-01T10:01:00.000Z", 1, new { records })]);
        var audit = Assert.Single(Of(h, "AuditEvent"));
        Assert.Equal(("delete", "web-1", "alice"), (audit.Prop("verb"), audit.Prop("name"), audit.Prop("user")));
    }

    [Fact]
    public void A_pod_warning_event_is_an_evidence_element_about_the_pod()
    {
        static string K(long seq, string at, object obj) => Line("kubernetes-watch", at, seq, new { type = "ADDED", @object = obj });
        var pod = new Dictionary<string, object> { ["kind"] = "Pod", ["metadata"] = new Dictionary<string, object> { ["uid"] = "pu", ["name"] = "web-1", ["namespace"] = "ns", ["resourceVersion"] = "1" } };
        var ev = new Dictionary<string, object> { ["kind"] = "Event", ["metadata"] = new Dictionary<string, object> { ["uid"] = "ev1", ["name"] = "web-1.x", ["namespace"] = "ns", ["resourceVersion"] = "2" }, ["type"] = "Warning", ["reason"] = "BackOff", ["message"] = "Back-off restarting", ["count"] = 3, ["lastTimestamp"] = "2026-01-01T10:00:30Z", ["involvedObject"] = new { kind = "Pod", name = "web-1", @namespace = "ns" } };
        var normal = new Dictionary<string, object>(ev) { ["metadata"] = new Dictionary<string, object> { ["uid"] = "ev2", ["name"] = "web-1.y", ["namespace"] = "ns", ["resourceVersion"] = "3" }, ["type"] = "Normal" };
        using var h = Serve([K(1, "2026-01-01T10:00:00.000Z", pod), K(2, "2026-01-01T10:00:31.000Z", ev), K(3, "2026-01-01T10:00:32.000Z", normal)]);
        var w = Assert.Single(Of(h, "WarningEvent"));
        Assert.Equal(("BackOff", "Back-off restarting"), (w.Prop("reason"), w.Prop("message")));
        var about = Assert.Single(h.Relationships("master", "About"));
        Assert.Equal(w["id"].Id(), about["source"].Id());
        Assert.Equal("web-1", h.Entities("master").Single(x => x["id"].Id() == about["target"].Id()).Prop("name"));
    }

    // ---- authored rows --------------------------------------------------------------------------------------------------------------------

    /// <summary>A record whose value has another JSON type than its adapter reads is rejected and counted; the replay goes on with the records after it (a live space journals a record before it applies it, so an exception here would stop every restart).</summary>
    [Fact]
    public void A_record_with_a_value_of_the_wrong_json_type_is_rejected_and_the_records_after_it_are_applied()
    {
        using var h = Serve(
        [
            Line("authoring", "2026-01-01T10:00:00.000Z", 1, new { kind = "signalRule", id = "r1", type = "pod", meter = "warnings", above = "80", enterAfter = 1, below = 1, exitAfter = 1, severity = 1, invert = false }),
            Line("authoring", "2026-01-01T10:00:01.000Z", 2, new { kind = "capability", capId = "c1", name = "Checkout", owner = "team-a", status = "active" }),
        ], out var stats);
        Assert.Equal(1, stats.Rejected);
        Assert.Contains(stats.RejectionSamples, x => x.Contains("malformed record", StringComparison.Ordinal));
        Assert.Empty(Of(h, "SignalRule"));
        Assert.Single(Of(h, "Capability"));
    }

    /// <summary>The thresholds of an authored rule reach a Threshold as row values, which the checker cannot see: a rule that is no state machine is rejected where it is authored.</summary>
    [Theory]
    [InlineData(5, 1, 1, 1, 1)]
    [InlineData(1, 1, 1, 1, 0)]         // above not greater than below
    [InlineData(5, 0, 1, 1, 0)]         // on at the first window whatever its value
    [InlineData(5, 1, 1, 0, 0)]
    public void An_authored_rule_is_accepted_only_with_above_over_below_and_runs_of_at_least_one_window(int above, int enterAfter, int below, int exitAfter, int rules)
    {
        using var h = Serve([Line("authoring", "2026-01-01T10:00:00.000Z", 1, new { kind = "signalRule", id = "r1", type = "pod", meter = "warnings", above, enterAfter, below, exitAfter, severity = 1, invert = false })], out var stats);
        Assert.Equal(rules, Of(h, "SignalRule").Count);
        Assert.Equal(1 - rules, stats.Rejected);
    }

    [Theory]
    [InlineData("{\"kind\":\"capability\",\"capId\":\"x\",\"name\":\"X\",\"owner\":\"o\"}")]                          // no status
    [InlineData("{\"kind\":\"realizes\",\"capId\":\"x\",\"cluster\":\"c\",\"namespace\":\"n\"}")]                    // no service
    [InlineData("{\"kind\":\"other\",\"capId\":\"x\"}")]                                                          // unknown kind
    [InlineData("{\"kind\":\"capability\",\"name\":\"X\",\"owner\":\"o\",\"status\":\"active\"}")]                    // no capId
    public void An_authoring_record_that_is_incomplete_is_rejected_and_counted(string payload)
    {
        using var h = Serve([Line("authoring", "2026-01-01T10:00:00.000Z", 1, JsonNode.Parse(payload)!)], out var stats, chunk: 2000);
        Assert.Equal(1, stats.Rejected);
        Assert.Empty(h.Entities("master"));
        Assert.Empty(h.Entities("authored"));
    }

    // ---- Prometheus: a range result has one series per label set; each series is a row source of its own ----------------------------------

    const string At = "2026-01-01T10:00:30.000Z";
    static readonly long Ts = DateTimeOffset.Parse(At).ToUnixTimeSeconds();

    static string Node(long seq, string name) =>
        Line("kubernetes-watch", At, seq, new { type = "ADDED", @object = new Dictionary<string, object> { ["kind"] = "Node", ["metadata"] = new Dictionary<string, object> { ["uid"] = "u-" + name, ["name"] = name } } });

    static string Range(long seq, string metric, params (Dictionary<string, string> Labels, string Value)[] series) =>
        Line("prometheus", At, seq, new { data = new { result = series.Select(s => new { metric = s.Labels, values = new object[] { new object[] { Ts, s.Value } } }) } }, new { name = metric });

    static Dictionary<string, string> L(params (string, string)[] labels) => labels.ToDictionary(l => l.Item1, l => l.Item2);

    /// <summary>The adapter keeps the labels of a series, and a rule that reads one meter of a node adds the series of that node.</summary>
    [Theory]
    [InlineData("cilium_forward_rate", "forwardRate")]
    [InlineData("cilium_drop_rate", "dropRate")]
    public void The_series_of_one_node_are_added_and_not_averaged(string metric, string property)
    {
        using var h = Serve([Node(1, "n1"), Range(2, metric, (L(("direction", "egress"), ("instance", "n1")), "10"), (L(("direction", "ingress"), ("instance", "n1")), "30"))]);
        Assert.Equal([40m], h.Windows("master", property).Select(w => w.Value));
        Assert.Equal([40m], h.Windows("master", "reading").Select(w => w.Value));                                     // the node's meter of that metric says the same
    }

    [Fact]
    public void A_series_without_an_instance_label_is_a_sample_and_not_dropped()
    {
        using var h = Serve([Range(1, "container_cpu_rate", (L(("namespace", "demo"), ("pod", "p1")), "0.5"), (L(("namespace", "demo"), ("pod", "p2")), "0.25"))], out var stats);
        Assert.Equal(2, stats.Rows.GetValueOrDefault("metrics"));
        Assert.Equal(0, stats.Rejected);
        Assert.Equal("[]", h.SpaceInfo()["input"]!["dropped"]!.ToJsonString());
    }

    /// <summary>The `instance` of a kube-state-metrics series is the exporter, not a node: such a series is no meter of a node.</summary>
    [Fact]
    public void A_series_whose_instance_is_an_exporter_is_no_meter_of_a_node()
    {
        using var h = Serve([Node(1, "n1"), Range(2, "kube_pod_info", (L(("instance", "ksm.kube-system.svc:8080"), ("job", "kube-state-metrics"), ("namespace", "demo"), ("pod", "p1"), ("node", "n1")), "1"))]);
        Assert.Empty(h.Entities("master", "Meter"));
        Assert.Empty(h.Relationships("master", "MeterOf"));
    }

    // ---- the forwarder's relist and loss markers: a relist is the complete set of one kind, a loss marker is evidence on the windows that contain it

    static Dictionary<string, object> Meta(string name) => new() { ["uid"] = "u-" + name, ["name"] = name, ["namespace"] = "ns", ["resourceVersion"] = "1" };
    static object Obj(string kind, string name) => new Dictionary<string, object> { ["kind"] = kind, ["metadata"] = Meta(name) };
    static object? In(string? cluster) => cluster is null ? null : new { cluster };
    static string Watch(long seq, string at, string kind, string name, string? cluster = null, string type = "ADDED") => Line("kubernetes-watch", at, seq, new { type, @object = Obj(kind, name) }, In(cluster));
    static string Marker(long seq, string at, string ev, string? path = null, object? extra = null, string? cluster = null) =>
        Line("sattle-forwarder", at, seq, new Dictionary<string, object?> { ["at"] = at, ["event"] = ev, ["path"] = path, ["extra"] = extra }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value), In(cluster));
    static string ListItem(long seq, string at, string kind, string path, string name, string? cluster = null) =>
        Line("kubernetes-list", at, seq, new Dictionary<string, object> { ["metadata"] = Meta(name) }, new { list = new { kind = kind + "List" }, path, relist = "start", cluster });

    static List<string?> Names(SpaceHost h, string type) => [.. h.Entities("master", type).Select(x => x.Prop("name")).Order()];

    [Theory, Trait("Category", "Slow")]
    [InlineData("Pod", "/api/v1/pods", "Pod")]
    [InlineData("Service", "/api/v1/services", "Service")]
    [InlineData("Node", "/api/v1/nodes", "Node")]
    [InlineData("Deployment", "/apis/apps/v1/deployments", "Deployment")]
    public void A_relist_replaces_the_known_set_of_its_kind(string kind, string path, string entityType)
    {
        using var h = Serve(
        [
            Watch(1, "2026-01-01T10:00:00.000Z", kind, "a"), Watch(2, "2026-01-01T10:00:00.100Z", kind, "b"), Watch(3, "2026-01-01T10:00:00.200Z", kind, "c"),
            Marker(4, "2026-01-01T10:05:00.000Z", "relist-start", path), ListItem(5, "2026-01-01T10:05:00.100Z", kind, path, "a"), ListItem(6, "2026-01-01T10:05:00.200Z", kind, path, "d"),
            Marker(7, "2026-01-01T10:05:00.300Z", "relist-end", path),
        ]);
        Assert.Equal(["a", "d"], Names(h, entityType));                                         // b and c were deleted while the forwarder was down, d is new
    }

    /// <summary>A relist that no longer holds the Service or the Deployment withdraws the selection between them, with the object.</summary>
    [Theory, Trait("Category", "Slow")]
    [InlineData("Service", "/api/v1/services")]
    [InlineData("Deployment", "/apis/apps/v1/deployments")]
    public void A_relist_without_the_Service_or_the_Deployment_withdraws_their_selection(string kind, string path)
    {
        object Labelled(string k, object spec) => new Dictionary<string, object> { ["kind"] = k, ["metadata"] = Meta("web"), ["spec"] = spec };
        var lines = new List<string>
        {
            Line("kubernetes-watch", "2026-01-01T10:00:00.000Z", 1, new { type = "ADDED", @object = Labelled("Service", new { selector = new { app = "web" } }) }),
            Line("kubernetes-watch", "2026-01-01T10:00:00.100Z", 2, new { type = "ADDED", @object = Labelled("Deployment", new { template = new { metadata = new { labels = new { app = "web" } } } }) }),
            Marker(3, "2026-01-01T10:05:00.000Z", "relist-start", path), Marker(4, "2026-01-01T10:05:00.100Z", "relist-end", path),
        };
        Assert.Single(SelectsAfter(lines.Take(2)));
        using var h = Serve(lines);
        Assert.Empty(Selects(h));
        Assert.Empty(h.Entities("master", kind));
    }

    [Fact, Trait("Category", "Slow")]
    public void A_relist_that_never_ends_deletes_nothing_and_a_relist_of_another_kind_leaves_this_kind_alone()
    {
        using var h = Serve(
        [
            Watch(1, "2026-01-01T10:00:00.000Z", "Pod", "a"), Watch(2, "2026-01-01T10:00:00.100Z", "Pod", "b"), Watch(3, "2026-01-01T10:00:00.200Z", "Service", "s1"),
            Marker(4, "2026-01-01T10:05:00.000Z", "relist-start", "/api/v1/pods"), ListItem(5, "2026-01-01T10:05:00.100Z", "Pod", "/api/v1/pods", "a"),   // crash: no relist-end
            Marker(6, "2026-01-01T10:06:00.000Z", "relist-start", "/api/v1/services"), Marker(7, "2026-01-01T10:06:00.100Z", "relist-end", "/api/v1/services"),
        ]);
        Assert.Equal(["a", "b"], Names(h, "Pod"));                                              // the pod list was cut short
        Assert.Empty(h.Entities("master", "Service"));                                          // an empty complete list of services removes the service
    }

    /// <summary>A capture of several clusters: a full list is the complete set of one kind of one cluster. An object of another cluster is not in it and stays, with its own cluster.</summary>
    [Theory, Trait("Category", "Slow")]
    [InlineData(false)]
    [InlineData(true)]
    public void A_relist_of_one_cluster_leaves_the_objects_of_another_cluster_alone(bool bothRelist)
    {
        const string path = "/api/v1/pods";
        var lines = new List<string>
        {
            Watch(1, "2026-01-01T10:00:00.000Z", "Pod", "a", "c1"), Watch(2, "2026-01-01T10:00:00.100Z", "Pod", "gone", "c1"), Watch(3, "2026-01-01T10:00:00.200Z", "Pod", "b", "c2"),
            Watch(10, "2026-01-01T10:00:00.300Z", "Pod", "later", "c2"), Watch(11, "2026-01-01T10:00:00.400Z", "Pod", "gone2", "c2"),
            Marker(4, "2026-01-01T10:05:00.000Z", "relist-start", path, cluster: "c1"),
        };
        if (bothRelist) lines.Add(Marker(5, "2026-01-01T10:05:00.050Z", "relist-start", path, cluster: "c2"));     // the lists of two clusters overlap in time
        lines.Add(ListItem(6, "2026-01-01T10:05:00.100Z", "Pod", path, "a", "c1"));
        if (bothRelist) lines.AddRange([ListItem(7, "2026-01-01T10:05:00.150Z", "Pod", path, "b", "c2"), ListItem(12, "2026-01-01T10:05:00.160Z", "Pod", path, "later", "c2")]);
        lines.Add(Marker(8, "2026-01-01T10:05:00.300Z", "relist-end", path, cluster: "c1"));
        if (bothRelist) lines.Add(Marker(9, "2026-01-01T10:05:00.350Z", "relist-end", path, cluster: "c2"));
        lines.Add(Watch(13, "2026-01-01T10:06:00.000Z", "Pod", "later", "c2", "DELETED"));                          // the adapter still knows the other cluster's object, so its deletion is seen
        using var h = Serve(lines);
        var pods = h.Entities("master", "Pod").Select(x => x.Prop("cluster") + "/" + x.Prop("name")).Order().ToList();
        Assert.Equal(bothRelist ? ["c1/a", "c2/b"] : ["c1/a", "c2/b", "c2/gone2"], pods);
    }

    /// <summary>A host with a live input, as `sattle serve --live` opens it.</summary>
    static SpaceHost Live()
    {
        var h = SpaceHost.OpenDurable(new InMemoryGraphStore(), Catalog.ReadDirectory(Repo.Definitions), new HostOptions { Projections = Repo.Projections, Cluster = "c" });
        h.StartLive(new ReplayOptions { ChunkRows = 1, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "c", FlushAtEnd = false });
        return h;
    }

    /// <summary>Live input drops an envelope whose key it has seen (at least once delivery). The key of a list record is the object and its version, so an unchanged object has the same key in the next full list: there it still says that the object exists.</summary>
    [Fact, Trait("Category", "Slow")]
    public void An_unchanged_object_of_a_second_relist_stays_in_a_live_run()
    {
        const string path = "/api/v1/pods";
        string[] first = [Marker(1, "2026-01-01T10:00:00.000Z", "relist-start", path), ListItem(2, "2026-01-01T10:00:00.100Z", "Pod", path, "a"), ListItem(3, "2026-01-01T10:00:00.200Z", "Pod", path, "b"), Marker(4, "2026-01-01T10:00:00.300Z", "relist-end", path)];
        string[] second = [Marker(5, "2026-01-01T10:05:00.000Z", "relist-start", path), first[1], Marker(6, "2026-01-01T10:05:00.300Z", "relist-end", path)];      // a is unchanged: the same record, the same key; b is gone
        using var h = Live();
        var n = 0;
        foreach (var line in first.Concat(second)) h.LiveEnvelope("fake", line, $"{{\"n\":{n++}}}");
        Assert.Equal(SpaceHost.LiveResult.Duplicate, h.LiveEnvelope("fake", first[1], $"{{\"n\":{n}}}"));          // outside a relist the same record is a duplicate
        h.LiveFlush();
        Assert.Equal(["a"], Names(h, "Pod"));
    }

    const string ResourceId = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct";
    /// <summary>A Resource Graph row as the forwarder sends it: its key is the query and the resource, whatever the row says.</summary>
    static string ArgRow(long seq, string location) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["v"] = 1, ["source"] = "arg", ["capturedAt"] = $"2026-01-01T10:0{seq}:00.000Z", ["seq"] = seq, ["key"] = "arg/resources/" + ResourceId, ["meta"] = new { query = "resources" },
        ["payload"] = new { id = ResourceId, name = "acct", type = "microsoft.storage/storageaccounts", location, resourceGroup = "rg", subscriptionId = "s" },
    });
    static string ArgChange(long seq, string changeType) => Line("arg", $"2026-01-01T10:0{seq}:00.000Z", seq,
        new { id = ResourceId.ToUpperInvariant() + "/providers/Microsoft.Resources/changes/c" + seq, properties = new { changeType, targetResourceId = ResourceId.ToUpperInvariant() } }, new { query = "resourcechanges" });

    /// <summary>#97: the live stream sends the Resource Graph inventory at every start and each changed resource again, under the key of the resource. The row read last is the resource's row; a delete in `resourcechanges` takes it back.</summary>
    [Theory, Trait("Category", "Slow")]
    [InlineData("same", SpaceHost.LiveResult.Duplicate, new[] { "westeurope" })]
    [InlineData("changed", SpaceHost.LiveResult.Consumed, new[] { "swedencentral" })]
    [InlineData("updateRecord", SpaceHost.LiveResult.Consumed, new[] { "westeurope" })]
    [InlineData("deleteRecord", SpaceHost.LiveResult.Consumed, new string[0])]
    public void A_resource_graph_row_read_again_replaces_its_resource_and_a_delete_removes_it(string next, SpaceHost.LiveResult result, string[] locations)
    {
        using var h = Live();
        h.LiveEnvelope("fake", ArgRow(1, "westeurope"), "{\"n\":0}");
        var line = next switch { "same" => ArgRow(2, "westeurope"), "changed" => ArgRow(2, "swedencentral"), "updateRecord" => ArgChange(2, "Update"), _ => ArgChange(2, "Delete") };
        Assert.Equal(result, h.LiveEnvelope("fake", line, "{\"n\":1}"));
        h.LiveFlush();
        Assert.Equal(locations, h.Entities("master", "AzureResource").Select(x => x.Prop("location")));
    }

    /// <summary>The partial windows the telemetry range read of the master serves, as (start, reason, count).</summary>
    static List<(DateTimeOffset Start, string Reason, string Count)> Partial(SpaceHost h) =>
        [.. Telemetry(h)["partialWindows"]!.AsArray().Select(p => (DateTimeOffset.Parse(p!["windowStart"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture), p["reason"]!.GetValue<string>(), p["count"]!.GetValue<string>()))];

    [Theory, Trait("Category", "Slow")]
    [InlineData("dropped", 7L, "forwarderDropped")]
    [InlineData("hubble-reconnect", 0L, "hubbleReconnect")]
    [InlineData("poll-failed", 0L, "pollFailed")]
    public void A_loss_marker_is_evidence_on_the_telemetry_window_that_contains_it(string ev, long count, string reason)
    {
        var at = "2026-01-01T10:03:30.000Z";
        var marker = Line("sattle-forwarder", at, 2, new Dictionary<string, object> { ["at"] = at, ["event"] = ev, ["count"] = count });
        using var h = Serve([Watch(1, "2026-01-01T10:00:00.000Z", "Pod", "a"), marker, Watch(3, "2026-01-01T10:09:00.000Z", "Pod", "b")]);
        Assert.Equal([(DateTimeOffset.Parse("2026-01-01T10:03:00Z"), reason, count.ToString())], Partial(h));
    }

    [Fact, Trait("Category", "Slow")]
    public void Two_kinds_of_loss_in_one_window_are_both_named()
    {
        var at = "2026-01-01T10:03:30.000Z";
        using var h = Serve(new[] { "dropped", "poll-failed" }.Select((ev, i) => Line("sattle-forwarder", at, i + 1, new Dictionary<string, object> { ["at"] = at, ["event"] = ev, ["count"] = 3 })));
        Assert.Equal("forwarderDropped+pollFailed", Assert.Single(Partial(h)).Reason);
    }

    // ---- the contract check -----------------------------------------------------------------------------------------------------------------

    /// <summary>An adapter delivers rows of the types the catalog declares, field by field: a space whose adapter is missing or whose row contract differs is refused before any record is read.</summary>
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
