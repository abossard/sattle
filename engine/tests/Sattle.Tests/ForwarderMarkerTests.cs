using System.Text.Json;
using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The forwarder's relist and loss markers (lab/CAPTURE-FORMAT.md): a relist is the complete set of one kind, a loss marker is evidence on the windows that contain it.</summary>
[Trait("Category", "Slow")]
public class ForwarderMarkerTests
{
    static string Line(string source, string at, long seq, object payload, object? meta = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["v"] = 1, ["source"] = source, ["capturedAt"] = at, ["seq"] = seq, ["key"] = $"{source}/{seq}", ["meta"] = meta, ["payload"] = payload });

    static string Capture(IEnumerable<string> lines)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-markers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "x"));
        File.WriteAllLines(Path.Combine(dir, "x", "a.ndjson"), lines);
        return dir;
    }

    static Dictionary<string, object> Meta(string name) => new() { ["uid"] = "u-" + name, ["name"] = name, ["namespace"] = "ns", ["resourceVersion"] = "1" };
    static object Obj(string kind, string name) => new Dictionary<string, object> { ["kind"] = kind, ["metadata"] = Meta(name) };
    static object? In(string? cluster) => cluster is null ? null : new { cluster };
    static string Watch(long seq, string at, string kind, string name, string? cluster = null, string type = "ADDED") => Line("kubernetes-watch", at, seq, new { type, @object = Obj(kind, name) }, In(cluster));
    static string Marker(long seq, string at, string ev, string? path = null, object? extra = null, string? cluster = null) =>
        Line("sattle-forwarder", at, seq, new Dictionary<string, object?> { ["at"] = at, ["event"] = ev, ["path"] = path, ["extra"] = extra }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value), In(cluster));
    static string ListItem(long seq, string at, string kind, string path, string name, string? cluster = null) =>
        Line("kubernetes-list", at, seq, new Dictionary<string, object> { ["metadata"] = Meta(name) }, new { list = new { kind = kind + "List" }, path, relist = "start", cluster });

    static int Count(Engine e, string type) => e.Master.State.Entities.Count(x => Ids.Name(x.Type) == type);

    [Theory]
    [InlineData("Pod", "/api/v1/pods", "Pod")]
    [InlineData("Service", "/api/v1/services", "Service")]
    [InlineData("Node", "/api/v1/nodes", "Node")]
    [InlineData("Deployment", "/apis/apps/v1/deployments", "Deployment")]
    public void A_relist_replaces_the_known_set_of_its_kind(string kind, string path, string entityType)
    {
        var lines = new List<string>
        {
            Watch(1, "2026-01-01T10:00:00.000Z", kind, "a"), Watch(2, "2026-01-01T10:00:00.100Z", kind, "b"), Watch(3, "2026-01-01T10:00:00.200Z", kind, "c"),
            Marker(4, "2026-01-01T10:05:00.000Z", "relist-start", path), ListItem(5, "2026-01-01T10:05:00.100Z", kind, path, "a"), ListItem(6, "2026-01-01T10:05:00.200Z", kind, path, "d"),
            Marker(7, "2026-01-01T10:05:00.300Z", "relist-end", path),
        };
        var r = ReplayTests.Replay(Capture(lines), chunk: 1);
        var names = r.Engine.Master.State.Entities.Where(x => Ids.Name(x.Type) == entityType).Select(x => x.Props.Text("d:sattle.infra/prop:name")).Order().ToList();
        Assert.Equal(["a", "d"], names);                                                        // b and c were deleted while the forwarder was down, d is new
    }

    [Fact]
    public void A_relist_that_never_ends_deletes_nothing_and_a_relist_of_another_kind_leaves_this_kind_alone()
    {
        var lines = new List<string>
        {
            Watch(1, "2026-01-01T10:00:00.000Z", "Pod", "a"), Watch(2, "2026-01-01T10:00:00.100Z", "Pod", "b"), Watch(3, "2026-01-01T10:00:00.200Z", "Service", "s1"),
            Marker(4, "2026-01-01T10:05:00.000Z", "relist-start", "/api/v1/pods"), ListItem(5, "2026-01-01T10:05:00.100Z", "Pod", "/api/v1/pods", "a"),   // crash: no relist-end
            Marker(6, "2026-01-01T10:06:00.000Z", "relist-start", "/api/v1/services"), Marker(7, "2026-01-01T10:06:00.100Z", "relist-end", "/api/v1/services"),
        };
        var e = ReplayTests.Replay(Capture(lines), chunk: 1).Engine;
        Assert.Equal(2, Count(e, "Pod"));                                                       // the pod list was cut short
        Assert.Equal(0, Count(e, "Service"));                                                   // an empty complete list of services removes the service
    }

    /// <summary>A capture of several clusters: a full list is the complete set of one kind of one cluster. An object of another cluster is not in it and stays, with its own cluster.</summary>
    [Theory]
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
        var e = ReplayTests.Replay(Capture(lines), chunk: 1).Engine;
        var pods = e.Master.State.Entities.Where(x => Ids.Name(x.Type) == "Pod").Select(x => x.Props.Text("d:sattle.infra/prop:cluster") + "/" + x.Props.Text("d:sattle.infra/prop:name")).Order().ToList();
        Assert.Equal(bothRelist ? ["c1/a", "c2/b"] : ["c1/a", "c2/b", "c2/gone2"], pods);
    }

    /// <summary>Live input drops an envelope whose key it has seen (at least once delivery). The key of a list record is the object and its version, so an unchanged object has the same key in the next full list: there it still says that the object exists.</summary>
    [Fact]
    public void An_unchanged_object_of_a_second_relist_stays_in_a_live_run()
    {
        const string path = "/api/v1/pods";
        string[] first = [Marker(1, "2026-01-01T10:00:00.000Z", "relist-start", path), ListItem(2, "2026-01-01T10:00:00.100Z", "Pod", path, "a"), ListItem(3, "2026-01-01T10:00:00.200Z", "Pod", path, "b"), Marker(4, "2026-01-01T10:00:00.300Z", "relist-end", path)];
        string[] second = [Marker(5, "2026-01-01T10:05:00.000Z", "relist-start", path), first[1], Marker(6, "2026-01-01T10:05:00.300Z", "relist-end", path)];      // a is unchanged: the same record, the same key; b is gone
        using var h = SpaceHost.OpenDurable(new InMemoryGraphStore(), Sattle.Language.Catalog.ReadDirectory(Repo.Definitions), new HostOptions { Cluster = "c" });
        h.StartLive(new ReplayOptions { ChunkRows = 1, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000, Cluster = "c", FlushAtEnd = false });
        var n = 0;
        foreach (var line in first.Concat(second)) h.LiveEnvelope("fake", line, $"{{\"n\":{n++}}}");
        Assert.Equal(SpaceHost.LiveResult.Duplicate, h.LiveEnvelope("fake", first[1], $"{{\"n\":{n}}}"));          // outside a relist the same record is a duplicate
        h.LiveFlush();
        Assert.Equal(["a"], h.Engine.Master.State.Entities.Where(x => Ids.Name(x.Type) == "Pod").Select(x => x.Props.Text("d:sattle.infra/prop:name")).Order());
    }

    [Theory]
    [InlineData("dropped", 7L, "forwarderDropped")]
    [InlineData("hubble-reconnect", 0L, "hubbleReconnect")]
    [InlineData("poll-failed", 0L, "pollFailed")]
    public void A_loss_marker_is_evidence_on_the_telemetry_window_that_contains_it(string ev, long count, string reason)
    {
        var at = "2026-01-01T10:03:30.000Z";
        var marker = Line("sattle-forwarder", at, 2, new Dictionary<string, object> { ["at"] = at, ["event"] = ev, ["count"] = count });
        var e = ReplayTests.Replay(Capture([Watch(1, "2026-01-01T10:00:00.000Z", "Pod", "a"), marker, Watch(3, "2026-01-01T10:09:00.000Z", "Pod", "b")]), chunk: 1).Engine;
        var partial = ((InMemoryTelemetryStore)e.Telemetry).PartialWindows(e.Master.Key);
        var ws = DateTimeOffset.Parse(at).ToUnixTimeMilliseconds() * 1_000_000L / 60_000_000_000L * 60_000_000_000L;
        Assert.Equal((reason, count), partial[ws]);
        Assert.Single(partial);
    }

    [Fact]
    public void Two_kinds_of_loss_in_one_window_are_both_named()
    {
        var at = "2026-01-01T10:03:30.000Z";
        var lines = new[] { "dropped", "poll-failed" }.Select((ev, i) => Line("sattle-forwarder", at, i + 1, new Dictionary<string, object> { ["at"] = at, ["event"] = ev, ["count"] = 3 }));
        var e = ReplayTests.Replay(Capture(lines), chunk: 1).Engine;
        Assert.Equal("forwarderDropped+pollFailed", ((InMemoryTelemetryStore)e.Telemetry).PartialWindows(e.Master.Key).Values.Single().Reason);
    }
}
