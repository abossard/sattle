using System.Text.Json;
using System.Text.Json.Nodes;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>
/// Def. 7.14 to 7.16 and Def. 10.9 through the adapters, the replay and a durable host, read from the served graph. A view over the shipped master reads the warning events of each instance with a fill of 0 and a time to live.
/// Every envelope of a source is progress of it, a heartbeat (`{}`) too: while the event source reports, an instance without events has windows of 0 and its signal goes off; when it stops reporting and only another source goes on, the instance is in no data.
/// </summary>
[Trait("Category", "Slow")]
public class SourceProgressTests
{
    const long Min = 60_000_000_000;
    const string View = """
        {"module": "t.quiet", "version": "1.0.0",
         "imports": [{"module": "acme.ops", "alias": "ops", "version": "1.0.0", "digest": null}, {"module": "sattle.infra", "alias": "infra", "version": "1.0.0", "digest": null}],
         "entities": [{"name": "InstanceSignal", "properties": [{"name": "k", "type": "Text", "label": true}, {"name": "state", "type": "Text", "optional": true}]}],
         "identities": [{"name": "instanceSignal", "type": "InstanceSignal", "components": [{"name": "k", "type": "Text"}]}]}
        """;

    const string Quiet = """
        {"graph": "quiet", "ontology": "t.quiet", "description": "The warning signal of each instance: on at one window with an event, off after two without, silent when its source has not reported.",
         "inputs": [{"name": "master", "ontology": "acme.ops"}], "parameters": [],
         "verbs": [
           {"id": "instances", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "p", "label": "Instance"}]}},
           {"id": "named", "verb": "project", "columns": {"p": {"ref": "p"}, "c": {"ref": "p.name"}}},
           {"id": "warnings", "verb": "series", "graph": "master", "series": {"element": "p", "property": "warnings", "lookback": "10m", "freshness": "3m", "fill": 0.0}},
           {"id": "signal", "verb": "hysteresis", "hysteresis": {"by": ["c"], "value": "warnings", "above": 1.0, "enterAfter": 1, "below": 0.0, "exitAfter": 2, "high": "on", "low": "off", "initial": "off", "noData": "silent"}},
           {"id": "emit", "verb": "nodes", "target": {"nodes": [{"var": "sig", "label": "InstanceSignal", "id": {"identity": "instanceSignal", "args": [{"ref": "c"}]}, "props": {"k": {"ref": "c"}, "state": {"ref": "state"}}}]}}],
         "wires": [{"from": "instances", "to": "named"}, {"from": "named", "to": "warnings"}, {"from": "warnings", "to": "signal"}, {"from": "signal", "to": "emit"}]}
        """;

    static List<SourceFile> Files()
    {
        var files = Repo.DefinitionFiles();
        var i = files.FindIndex(f => f.Path.EndsWith("acme.space.module.gkql.json", StringComparison.Ordinal));
        var space = JsonNode.Parse(files[i].Text)!;
        space["imports"]!.AsArray().Add(new JsonObject { ["module"] = "t.quiet", ["alias"] = "quiet", ["version"] = "1.0.0", ["digest"] = null });
        space["space"]!["graphs"]!.AsArray().Add(new JsonObject { ["name"] = "quiet", ["projection"] = "quiet.quiet", ["inputs"] = new JsonObject { ["master"] = "master" } });
        files[i] = files[i] with { Text = space.ToJsonString() };
        files.AddRange([new SourceFile("t.quiet.module.gkql.json", View), new SourceFile("t.quiet.quiet.gkql.json", Quiet)]);
        return files;
    }

    static string Line(string source, string at, long seq, object payload) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["v"] = 1, ["source"] = source, ["capturedAt"] = at, ["seq"] = seq, ["key"] = $"{source}/{seq}", ["payload"] = payload });
    static string At(int minute, int second = 0) => $"2026-01-01T10:{minute:00}:{second:00}.000Z";
    static string Instance(long seq, string at, string name) =>
        Line("records:InstanceRow", at, seq, new { key = "i-" + name, values = new { site = "s", uid = "u-" + name, @namespace = "ns", name } });
    static string Warning(long seq, string at, string instance) => Line("records:EventRow", at, seq, new
    {
        key = "ev-" + seq,
        values = new { eventId = "ev-" + seq, time = at, site = "s", @namespace = "ns", kind = "Instance", name = instance, reason = "BackOff", type = "Warning", count = 1 },
    });
    /// <summary>A heartbeat: progress of the source `rows` and no row.</summary>
    static string Heartbeat(string rows, long seq, string at) => Line("records:" + rows, at, seq, new { });

    static string Capture(IEnumerable<string> lines)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "x"));
        File.WriteAllLines(Path.Combine(dir, "x", "a.ndjson"), lines);
        return dir;
    }

    /// <summary>Two instances at 10:00 and a warning for `a` at 10:00:30. Then one heartbeat a minute until 10:09: of the event source (`reporting`), or of the flows only.</summary>
    static List<string> Lines(bool reporting)
    {
        var lines = new List<string> { Instance(1, At(0), "a"), Instance(2, At(0, 1), "b"), Warning(3, At(0, 30), "a") };
        for (var m = 1; m <= 9; m++) lines.Add(Heartbeat(reporting ? "EventRow" : "FlowRow", 10 + m, At(m, 5)));
        return lines;
    }

    static Dictionary<string, string?> States(SpaceHost h) => h.Entities("quiet", "InstanceSignal").ToDictionary(x => x.Prop("k")!, x => x.Prop("state"));

    [Theory]
    [InlineData(true, "off", "off")]            // covered silence: the windows after the warning are 0, two of them end the state; the instance without events has windows of 0 too
    [InlineData(false, "silent", "silent")]     // the source of the events stopped reporting: nothing is filled, and after the time to live both instances are in no data
    public void A_covered_silence_is_a_record_and_a_silent_source_is_no_data(bool reporting, string a, string b)
    {
        var dir = Capture(Lines(reporting));
        try
        {
            var store = new InMemoryGraphStore();
            var options = new HostOptions { Projections = Repo.Projections};
            string digest;
            using (var host = SpaceHost.OpenDurable(store, Files(), options))
            {
                new Replay(host, host.Space, host.Adapters, new ReplayOptions { ChunkRows = 2, AdvanceEveryNs = Min, LatenessNs = Min}).Run(dir);
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
