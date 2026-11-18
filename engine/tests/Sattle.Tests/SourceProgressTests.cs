using System.Text.Json;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>
/// Def. 7.14 to 7.16 and Def. 10.9 through the adapters, the replay and a durable host, read from the served graph. A view over the shipped master reads the warning events of each pod with a fill of 0 and a time to live.
/// Every envelope of the Kubernetes adapter is progress of its sources: while it reports, a pod without events has windows of 0 and its signal goes off; when it stops reporting and only another source goes on, the pod is in no data.
/// </summary>
[Trait("Category", "Slow")]
public class SourceProgressTests
{
    const long Min = 60_000_000_000;
    const string View = """
        module t.quiet @ 1.0.0;
        import acme.ops @ 1.0.0 sha256:… as ops;
        import sattle.infra @ 1.0.0 sha256:… as infra;
        entity PodSignal { k: Text label; state?: Text; }
        identity podSignal for PodSignal version "1" { k: Text by exact; }
        """;

    const string Quiet = """
        graph quiet : t.quiet with (description = "The warning signal of each pod: on at one window with an event, off after two without, silent when its source has not reported.");
        input master : acme.ops;

        let s = graph("master") | graph-match (p) where labels(p) has "Pod" project p = p.id, c = p.name
            | join kind=inner (telemetry_filled("master", 10m, 3m, 0.0) | where property == "warnings" | project p = id, windowStart, property, value, noData) on p
            | project c, windowStart, value, above = 1.0, enterAfter = 1, below = 0.0, exitAfter = 2, noData
            | invoke hysteresis("on", "off", "off", "silent");
        nodes PodSignal on sig = s | project sig = identity("podSignal", c), k = c, state;
        """;

    static List<SourceFile> Files()
    {
        var files = Repo.DefinitionFiles(false);
        var space = files.FindIndex(f => f.Path.EndsWith("acme.space.sattle", StringComparison.Ordinal));
        var text = files[space].Text;
        var firstImport = text.IndexOf("import ", StringComparison.Ordinal);
        text = text.Insert(firstImport, "import t.quiet @ 1.0.0 sha256:… as quiet;\n");
        text = text[..text.LastIndexOf('}')] + "  graph quiet = quiet.quiet(master: master);\n}\n";
        files[space] = files[space] with { Text = text };
        files.AddRange([new SourceFile("t.quiet.sattle", View), new SourceFile("t.quiet.quiet.kql", Quiet)]);
        return files;
    }

    static string Line(string source, string at, long seq, object payload) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["v"] = 1, ["source"] = source, ["capturedAt"] = at, ["seq"] = seq, ["key"] = $"{source}/{seq}", ["payload"] = payload });
    static string At(int minute, int second = 0) => $"2026-01-01T10:{minute:00}:{second:00}.000Z";
    static string Pod(long seq, string at, string name) =>
        Line("kubernetes-watch", at, seq, new { type = "ADDED", @object = new Dictionary<string, object> { ["kind"] = "Pod", ["metadata"] = new Dictionary<string, object> { ["uid"] = "u-" + name, ["name"] = name, ["namespace"] = "ns" } } });
    static string Warning(long seq, string at, string pod) => Line("kubernetes-watch", at, seq, new
    {
        type = "ADDED",
        @object = new Dictionary<string, object>
        {
            ["kind"] = "Event", ["metadata"] = new Dictionary<string, object> { ["uid"] = "ev-" + seq, ["name"] = "ev-" + seq, ["namespace"] = "ns" }, ["count"] = 1, ["lastTimestamp"] = at,
            ["involvedObject"] = new { kind = "Pod", @namespace = "ns", name = pod }, ["reason"] = "BackOff", ["type"] = "Warning",
        },
    });
    static string Flow(long seq, string at) => Line("hubble", at, seq, new { flow = new { time = at, verdict = "FORWARDED" } });

    static string Capture(IEnumerable<string> lines)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "x"));
        File.WriteAllLines(Path.Combine(dir, "x", "a.ndjson"), lines);
        return dir;
    }

    /// <summary>Two pods at 10:00 and a warning for `a` at 10:00:30. Then one envelope a minute until 10:09: of the Kubernetes adapter (`reporting`), or of Hubble only.</summary>
    static List<string> Lines(bool reporting)
    {
        var lines = new List<string> { Pod(1, At(0), "a"), Pod(2, At(0, 1), "b"), Warning(3, At(0, 30), "a") };
        for (var m = 1; m <= 9; m++) lines.Add(reporting ? Pod(10 + m, At(m, 5), "b") : Flow(10 + m, At(m, 5)));
        return lines;
    }

    static Dictionary<string, string?> States(SpaceHost h) => h.Entities("quiet", "PodSignal").ToDictionary(x => x.Prop("k")!, x => x.Prop("state"));

    [Theory]
    [InlineData(true, "off", "off")]            // covered silence: the windows after the warning are 0, two of them end the state; the pod without events has windows of 0 too
    [InlineData(false, "silent", "silent")]     // the source of the events stopped reporting: nothing is filled, and after the time to live both pods are in no data
    public void A_covered_silence_is_a_record_and_a_silent_source_is_no_data(bool reporting, string a, string b)
    {
        var dir = Capture(Lines(reporting));
        try
        {
            var store = new InMemoryGraphStore();
            var options = new HostOptions { Projections = Repo.Projections, Cluster = "synth" };
            string digest;
            using (var host = SpaceHost.OpenDurable(store, Files(), options))
            {
                new Replay(host, host.Space, host.Adapters, new ReplayOptions { ChunkRows = 2, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" }).Run(dir);
                Assert.Equal(new Dictionary<string, string?> { ["a"] = a, ["b"] = b }, States(host));
                digest = host.StateDigest();
            }
            using var again = SpaceHost.OpenDurable(store, null, options);       // the progress of the sources is in the journal: a restart is in the same state
            Assert.Equal(digest, again.StateDigest());
            Assert.Equal(new Dictionary<string, string?> { ["a"] = a, ["b"] = b }, States(again));
        }
        finally { Directory.Delete(dir, true); }
    }
}
