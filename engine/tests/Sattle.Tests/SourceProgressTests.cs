using System.Text.Json;
using System.Text.Json.Nodes;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>
/// Def. 7.14 to 7.16 and Def. 10.9 through the adapters, the replay and a durable host, read from the served graph. A view over the shipped master reads the warning events of each instance with a fill of 0 and a time to live.
/// Every envelope of a source is progress of it, a heartbeat (`{}`) too: while the event source reports, an instance without events has windows of 0 and its signal goes off; when it stops reporting and only another source goes on, the windows of the instance are silent, and three in a row make it silent (#151 D3: silence is silent windows, the fold's `gap`).
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
        {"graph": "quiet", "ontology": "t.quiet", "description": "The warning signal of each instance: on at one window with an event, off after two without, silent after three windows in which its source has not reported.",
         "inputs": [{"name": "master", "ontology": "acme.ops"}], "parameters": [],
         "verbs": [
           {"id": "instances", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "p", "label": "Instance"}]}},
           {"id": "named", "verb": "project", "columns": {"p": {"ref": "p"}, "c": {"ref": "p.name"}}},
           {"id": "warnings", "verb": "series", "graph": "master", "series": {"element": "p", "property": "warnings", "lookback": "10m", "freshness": "3m", "fill": 0.0, "lateness": "3m"}},
           {"id": "signalPolicy", "verb": "extend", "columns": {"above": {"lit": 1}, "below": {"lit": 0}, "enterAfter": {"lit": 1}, "exitAfter": {"lit": 2}, "gap": {"lit": 3}}},
           {"id": "signalFold", "verb": "fold", "fold": {"by": ["c"], "observe": {"n": {"fn": "count", "args": [{"ref": "warnings"}]}, "v": {"fn": "max", "args": [{"ref": "warnings"}]}}, "policy": ["above", "below", "enterAfter", "exitAfter", "gap"], "state": {"state": {"start": {"lit": "off"}, "next": {"fn": "iff", "args": [{"fn": "==", "args": [{"ref": "n"}, {"lit": 0}]}, {"fn": "iff", "args": [{"fn": ">=", "args": [{"fn": "+", "args": [{"ref": "quiet"}, {"lit": 1}]}, {"ref": "gap"}]}, {"lit": "silent"}, {"ref": "state"}]}, {"fn": "iff", "args": [{"fn": "and", "args": [{"fn": "iff", "args": [{"fn": "==", "args": [{"fn": "iff", "args": [{"fn": "==", "args": [{"ref": "state"}, {"lit": "silent"}]}, {"lit": "off"}, {"ref": "state"}]}, {"lit": "off"}]}, {"fn": ">=", "args": [{"fn": "coalesce", "args": [{"ref": "v"}, {"lit": 0}]}, {"ref": "above"}]}, {"fn": "<=", "args": [{"fn": "coalesce", "args": [{"ref": "v"}, {"lit": 0}]}, {"ref": "below"}]}]}, {"fn": "iff", "args": [{"fn": "==", "args": [{"fn": "iff", "args": [{"fn": "==", "args": [{"ref": "state"}, {"lit": "silent"}]}, {"lit": "off"}, {"ref": "state"}]}, {"lit": "off"}]}, {"fn": ">=", "args": [{"fn": "+", "args": [{"ref": "streak"}, {"lit": 1}]}, {"ref": "enterAfter"}]}, {"fn": ">=", "args": [{"fn": "+", "args": [{"ref": "streak"}, {"lit": 1}]}, {"ref": "exitAfter"}]}]}]}, {"fn": "iff", "args": [{"fn": "==", "args": [{"fn": "iff", "args": [{"fn": "==", "args": [{"ref": "state"}, {"lit": "silent"}]}, {"lit": "off"}, {"ref": "state"}]}, {"lit": "off"}]}, {"lit": "on"}, {"lit": "off"}]}, {"fn": "iff", "args": [{"fn": "==", "args": [{"ref": "state"}, {"lit": "silent"}]}, {"lit": "off"}, {"ref": "state"}]}]}]}}, "streak": {"start": {"lit": 0}, "next": {"fn": "iff", "args": [{"fn": "==", "args": [{"ref": "n"}, {"lit": 0}]}, {"lit": 0}, {"fn": "iff", "args": [{"fn": "iff", "args": [{"fn": "==", "args": [{"fn": "iff", "args": [{"fn": "==", "args": [{"ref": "state"}, {"lit": "silent"}]}, {"lit": "off"}, {"ref": "state"}]}, {"lit": "off"}]}, {"fn": ">=", "args": [{"fn": "coalesce", "args": [{"ref": "v"}, {"lit": 0}]}, {"ref": "above"}]}, {"fn": "<=", "args": [{"fn": "coalesce", "args": [{"ref": "v"}, {"lit": 0}]}, {"ref": "below"}]}]}, {"fn": "iff", "args": [{"fn": "iff", "args": [{"fn": "==", "args": [{"fn": "iff", "args": [{"fn": "==", "args": [{"ref": "state"}, {"lit": "silent"}]}, {"lit": "off"}, {"ref": "state"}]}, {"lit": "off"}]}, {"fn": ">=", "args": [{"fn": "+", "args": [{"ref": "streak"}, {"lit": 1}]}, {"ref": "enterAfter"}]}, {"fn": ">=", "args": [{"fn": "+", "args": [{"ref": "streak"}, {"lit": 1}]}, {"ref": "exitAfter"}]}]}, {"lit": 0}, {"fn": "+", "args": [{"ref": "streak"}, {"lit": 1}]}]}, {"lit": 0}]}]}}, "quiet": {"start": {"lit": 0}, "next": {"fn": "iff", "args": [{"fn": "==", "args": [{"ref": "n"}, {"lit": 0}]}, {"fn": "iff", "args": [{"fn": ">=", "args": [{"fn": "+", "args": [{"ref": "quiet"}, {"lit": 1}]}, {"ref": "gap"}]}, {"ref": "gap"}, {"fn": "+", "args": [{"ref": "quiet"}, {"lit": 1}]}]}, {"lit": 0}]}}}, "show": "state"}},
           {"id": "signal", "verb": "summarize", "by": {"c": {"ref": "c"}}, "aggregates": {"state": {"fn": "arg_max", "args": [{"ref": "windowStart"}, {"ref": "to"}]}}},
           {"id": "emit", "verb": "nodes", "target": {"nodes": [{"var": "sig", "label": "InstanceSignal", "id": {"identity": "instanceSignal", "args": [{"ref": "c"}]}, "props": {"k": {"ref": "c"}, "state": {"ref": "state"}}}]}}],
         "wires": [{"from": "instances", "to": "named"}, {"from": "named", "to": "warnings"}, {"from": "warnings", "to": "signalFold"}, {"from": "named", "to": "signalPolicy"}, {"from": "signalPolicy", "to": "signalFold", "port": "policy"}, {"from": "signalFold", "to": "signal"}, {"from": "signal", "to": "emit"}]}
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
    /// <summary>The states of `a` and `b`; an instance without a state has no signal.</summary>
    static Dictionary<string, string?> Expected(string a, string? b) => b is null ? new() { ["a"] = a } : new() { ["a"] = a, ["b"] = b };

    [Theory]
    [InlineData(true, "off", "off")]            // covered silence: the windows after the warning are 0, two of them end the state; the instance without events has windows of 0 too
    [InlineData(false, "silent", null)]         // the source of the events stopped reporting: nothing is filled, `a` is silent after three silent windows; `b` never had a window that is not silent, so it never starts and has no state (D3)
    public void A_covered_silence_is_a_record_and_a_silent_source_is_no_data(bool reporting, string a, string? b)
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
                Assert.Equal(Expected(a, b), States(host));
                digest = host.StateDigest();
            }
            using var again = SpaceHost.OpenDurable(store, null, options);       // the progress of the sources is in the journal: a restart is in the same state
            Assert.Equal(digest, again.StateDigest());
            Assert.Equal(Expected(a, b), States(again));
        }
        finally { Directory.Delete(dir, true); }
    }
}
