using System.Text.Json;
using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>A Prometheus range result has one series per label set. Each series is a row source of its own: the adapter keeps its labels, and a rule that reads one meter of a node adds the series of that node.</summary>
public class PrometheusAdapterTests
{
    const string At = "2026-01-01T10:00:30.000Z";
    static readonly long Ts = DateTimeOffset.Parse(At).ToUnixTimeSeconds();

    static string Line(string source, long seq, object payload, object? meta = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["v"] = 1, ["source"] = source, ["capturedAt"] = At, ["seq"] = seq, ["key"] = $"{source}/{seq}", ["meta"] = meta, ["payload"] = payload });

    static string Node(long seq, string name) =>
        Line("kubernetes-watch", seq, new { type = "ADDED", @object = new Dictionary<string, object> { ["kind"] = "Node", ["metadata"] = new Dictionary<string, object> { ["uid"] = "u-" + name, ["name"] = name } } });

    static string Range(long seq, string metric, params (Dictionary<string, string> Labels, string Value)[] series) =>
        Line("prometheus", seq, new { data = new { result = series.Select(s => new { metric = s.Labels, values = new object[] { new object[] { Ts, s.Value } } }) } }, new { name = metric });

    static string Capture(params string[] lines)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-prometheus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "x"));
        File.WriteAllLines(Path.Combine(dir, "x", "a.ndjson"), lines);
        return dir;
    }

    static Dictionary<string, string> L(params (string, string)[] labels) => labels.ToDictionary(l => l.Item1, l => l.Item2);

    static IEnumerable<decimal> Windows(Engine e, string property) =>
        ((InMemoryTelemetryStore)e.Telemetry).Dump(e.Master.Key).Where(w => Ids.Name(e.Interner.Get(w.Prop)) == property).Select(w => w.Value);

    [Theory]
    [InlineData("cilium_forward_rate", "forwardRate")]
    [InlineData("cilium_drop_rate", "dropRate")]
    public void The_series_of_one_node_are_added_and_not_averaged(string metric, string property)
    {
        var e = ReplayTests.Replay(Capture(Node(1, "n1"),
            Range(2, metric, (L(("direction", "egress"), ("instance", "n1")), "10"), (L(("direction", "ingress"), ("instance", "n1")), "30"))), chunk: 1).Engine;
        Assert.Equal([40m], Windows(e, property));
        Assert.Equal([40m], Windows(e, "reading"));                                              // the node's meter of that metric says the same
    }

    [Fact]
    public void A_series_without_an_instance_label_is_a_sample_and_not_dropped()
    {
        var r = ReplayTests.Replay(Capture(Range(1, "container_cpu_rate", (L(("namespace", "demo"), ("pod", "p1")), "0.5"), (L(("namespace", "demo"), ("pod", "p2")), "0.25"))), chunk: 1);
        Assert.Equal(2, r.Adapters.OfType<PrometheusAdapter>().Single().Samples);
        Assert.Equal(0, r.Stats.Rejected);
    }

    /// <summary>The `instance` of a kube-state-metrics series is the exporter, not a node: such a series is no meter of a node.</summary>
    [Fact]
    public void A_series_whose_instance_is_an_exporter_is_no_meter_of_a_node()
    {
        var e = ReplayTests.Replay(Capture(Node(1, "n1"),
            Range(2, "kube_pod_info", (L(("instance", "ksm.kube-system.svc:8080"), ("job", "kube-state-metrics"), ("namespace", "demo"), ("pod", "p1"), ("node", "n1")), "1"))), chunk: 1).Engine;
        Assert.DoesNotContain(e.Master.State.Entities, x => Ids.Name(x.Type) == "Meter");
        Assert.DoesNotContain(e.Master.State.Rels, x => Ids.Name(x.Type) == "MeterOf");
    }
}
