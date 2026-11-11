using System.Text.Json;
using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Event Hubs records (diagnostic settings) and warning events become evidence elements in the master graph, one element per operation, not only counts.</summary>
public class EvidenceAdapterTests
{
    static string Line(string source, string at, long seq, object payload) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["v"] = 1, ["source"] = source, ["capturedAt"] = at, ["seq"] = seq, ["key"] = $"{source}/{seq}", ["payload"] = payload });

    static string Capture(IEnumerable<string> lines)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "x"));
        File.WriteAllLines(Path.Combine(dir, "x", "a.ndjson"), lines);
        return dir;
    }

    const string Rid = "/SUBSCRIPTIONS/S/RESOURCEGROUPS/RG/PROVIDERS/MICROSOFT.STORAGE/STORAGEACCOUNTS/ACCT";

    static object ActivityRecord(string op, string result, string time, string corr) => new Dictionary<string, object> { ["category"] = "Administrative", ["operationName"] = op, ["time"] = time, ["resultType"] = result, ["resourceId"] = Rid, ["correlationId"] = corr };

    static string Audit(string id, string verb, string resource, string stage, string name) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["auditID"] = id, ["verb"] = verb, ["stage"] = stage, ["objectRef"] = new { resource, @namespace = "kube-system", name }, ["user"] = new { username = "alice" }, ["responseStatus"] = new { code = 200 }, ["requestReceivedTimestamp"] = "2026-01-01T10:00:05.000000Z",
    });

    static IEnumerable<Entity> Of(Engine e, string type) => e.Master.State.Entities.Where(x => Ids.Name(x.Type) == type);

    [Fact]
    public void Diagnostic_activity_records_are_evidence_elements_linked_to_their_resource()
    {
        var records = new[] { ActivityRecord("MICROSOFT.STORAGE/STORAGEACCOUNTS/WRITE", "Start", "2026-01-01T10:00:01Z", "c1"), ActivityRecord("MICROSOFT.STORAGE/STORAGEACCOUNTS/WRITE", "Success", "2026-01-01T10:00:02Z", "c1") };
        var e = ReplayTests.Replay(Capture([Line("eventhub:sattle-activity", "2026-01-01T10:01:00.000Z", 1, new { records })]), chunk: 1).Engine;
        var events = Of(e, "ActivityEvent").OrderBy(x => x.Props.Text("d:sattle.azure/prop:status")).ToList();
        Assert.Equal(["Started", "Succeeded"], events.Select(x => x.Props.Text("d:sattle.azure/prop:status")));
        Assert.All(events, x => Assert.Equal("microsoft.storage/storageaccounts/write", x.Props.Text("d:sattle.azure/prop:operation")));      // one spelling, whichever source delivered the event (the diagnostic setting writes upper case)
        Assert.Equal(2, e.Master.State.Rels.Count(r => Ids.Name(r.Type) == "Concerns"));       // both point at the one resource identity, whatever the id's case
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
        var e = ReplayTests.Replay(Capture(
        [
            Line("eventhub:sattle-activity", "2026-01-01T10:01:00.000Z", 1, new { records = new[] { ActivityRecord("MICROSOFT.STORAGE/STORAGEACCOUNTS/WRITE", "Success", "2026-01-01T10:00:02Z", "c1") } }),
            Line("activity-log", "2026-01-01T10:01:01.000Z", 2, ManagementEvent("Microsoft.Storage/storageAccounts/write", "Succeeded", managementTime, "C1", "d1")),
        ]), chunk: 1).Engine;
        var found = Of(e, "ActivityEvent").ToList();
        Assert.Equal(events, found.Count);
        Assert.All(found, x => Assert.IsType<InstantValue>(x.Props.Get("d:sattle.azure/prop:time")));
        Assert.Contains(found, x => x.Props.Text("d:sattle.azure/prop:caller") == "operator@example.invalid");
        Assert.Single(e.Master.State.Rels.Where(r => Ids.Name(r.Type) == "Concerns").Select(r => r.Target).Distinct());      // the upper-case id of one source and the lower-case id of the other are one resource
        var windows = ((InMemoryTelemetryStore)e.Telemetry).Dump(e.Master.Key).Where(w => e.Interner.Get(w.Prop).EndsWith("/prop:changes", StringComparison.Ordinal)).ToList();
        Assert.Equal(changes, (long)windows.Sum(w => w.Value));
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
        var e = ReplayTests.Replay(Capture([Line("eventhub:sattle-aks", "2026-01-01T10:01:00.000Z", 1, new { records })]), chunk: 1).Engine;
        var audit = Of(e, "AuditEvent").Single();
        Assert.Equal("delete", audit.Props.Text("d:sattle.infra/prop:verb")); Assert.Equal("web-1", audit.Props.Text("d:sattle.infra/prop:name")); Assert.Equal("alice", audit.Props.Text("d:sattle.infra/prop:user"));
    }

    [Fact]
    public void A_pod_warning_event_is_an_evidence_element_about_the_pod()
    {
        static string K(long seq, string at, object obj) => Line("kubernetes-watch", at, seq, new { type = "ADDED", @object = obj });
        var pod = new Dictionary<string, object> { ["kind"] = "Pod", ["metadata"] = new Dictionary<string, object> { ["uid"] = "pu", ["name"] = "web-1", ["namespace"] = "ns", ["resourceVersion"] = "1" } };
        var ev = new Dictionary<string, object> { ["kind"] = "Event", ["metadata"] = new Dictionary<string, object> { ["uid"] = "ev1", ["name"] = "web-1.x", ["namespace"] = "ns", ["resourceVersion"] = "2" }, ["type"] = "Warning", ["reason"] = "BackOff", ["message"] = "Back-off restarting", ["count"] = 3, ["lastTimestamp"] = "2026-01-01T10:00:30Z", ["involvedObject"] = new { kind = "Pod", name = "web-1", @namespace = "ns" } };
        var normal = new Dictionary<string, object>(ev) { ["metadata"] = new Dictionary<string, object> { ["uid"] = "ev2", ["name"] = "web-1.y", ["namespace"] = "ns", ["resourceVersion"] = "3" }, ["type"] = "Normal" };
        var e = ReplayTests.Replay(Capture([K(1, "2026-01-01T10:00:00.000Z", pod), K(2, "2026-01-01T10:00:31.000Z", ev), K(3, "2026-01-01T10:00:32.000Z", normal)]), chunk: 1).Engine;
        var w = Of(e, "WarningEvent").Single();
        Assert.Equal("BackOff", w.Props.Text("d:sattle.infra/prop:reason")); Assert.Equal("Back-off restarting", w.Props.Text("d:sattle.infra/prop:message"));
        var about = e.Master.State.Rels.Single(r => Ids.Name(r.Type) == "About");
        Assert.Equal(w.Id, about.Source);
        Assert.Equal("web-1", e.Master.State.Entity(about.Target)!.Props.Text("d:sattle.infra/prop:name"));
    }
}
