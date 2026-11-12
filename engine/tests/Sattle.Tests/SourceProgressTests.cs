using System.Text.Json;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>
/// Def. 7.14 to 7.16 and Def. 10.9 through the adapters, the replay and a durable host. A view over the shipped master reads the warning events of each pod with a fill of 0 and a time to live.
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
        /// The warning signal of each pod: on at one window with an event, off after two without, silent when its source has not reported.
        projection quiet view forward output t.quiet {
          in master: graph ops;
          let s = match master (p: infra.Pod) |> series master p.warnings fill 0 within 10m ttl 3m |> select { c: p.name, windowStart: windowStart, v: warnings, noData: noData }
            |> threshold v by { c: c } above 1 for 1 then "on" below 0 for 2 then "off" initial "off" nodata then "silent" as state;
          emit entity PodSignal from s identity podSignal(k: c) set { k: c, state: state };
        }
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
        files.Add(new SourceFile("t.quiet.sattle", View));
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

    static Dictionary<string, string?> States(Engine e) =>
        e.Graph("quiet").State.Entities.ToDictionary(x => x.Props.Text("d:t.quiet/prop:k")!, x => x.Props.Text("d:t.quiet/prop:state"));

    [Theory]
    [InlineData(true, "off", "off")]            // covered silence: the windows after the warning are 0, two of them end the state; the pod without events has windows of 0 too
    [InlineData(false, "silent", "silent")]     // the source of the events stopped reporting: nothing is filled, and after the time to live both pods are in no data
    public void A_covered_silence_is_a_record_and_a_silent_source_is_no_data(bool reporting, string a, string b)
    {
        var dir = Capture(Lines(reporting));
        try
        {
            var store = new InMemoryGraphStore();
            var options = new HostOptions { Cluster = "synth" };
            string digest;
            using (var host = SpaceHost.OpenDurable(store, Files(), options))
            {
                new Replay(host, host.Space, host.Adapters, new ReplayOptions { ChunkRows = 2, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" }).Run(dir);
                Assert.Equal(new Dictionary<string, string?> { ["a"] = a, ["b"] = b }, States(host.Engine));
                digest = host.StateDigest();
            }
            using var again = SpaceHost.OpenDurable(store, null, options);       // the progress of the sources is in the journal: a restart is in the same state
            Assert.Equal(digest, again.StateDigest());
            Assert.Equal(new Dictionary<string, string?> { ["a"] = a, ["b"] = b }, States(again.Engine));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>What a filling Series carries from one step to the next (since when an element has had a row, how far its reads are covered, which elements are in no data) and the progress of the sources are part of a state snapshot: an engine restored in the middle of a capture goes on like the one that was not.</summary>
    [Theory]
    [InlineData(true, 6)] [InlineData(false, 6)] [InlineData(false, 9)]
    public void A_snapshot_in_the_middle_of_a_capture_carries_the_state_of_a_filling_series(bool reporting, int cut)
    {
        var cat = new Catalog();
        Assert.DoesNotContain(cat.Load(Files(), updatePins: true).Diagnostics, d => d.IsError);
        var space = cat.Spaces.Keys.Single();
        var lines = Lines(reporting);
        void Feed(Engine e, IEnumerable<string> part, bool end)
        {
            var dir = Capture(part);
            try { new Replay(e, cat.Spaces[space], AdapterRegistry.Create("synth"), new ReplayOptions { ChunkRows = 2, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth", FlushAtEnd = end }).Run(dir); }
            finally { Directory.Delete(dir, true); }
        }
        var options = new EngineOptions { StoreFactory = _ => new SnapshotTests.AcceptingStore() };       // the engine state is under test here, not the log
        using var original = new Engine(cat, space, options);
        Feed(original, lines.Take(cut), false);
        var snapshot = StateSnapshot.Save(original.StateRootOf());
        using var restored = new Engine(cat, space, options);
        var root = restored.StateRootOf(); StateSnapshot.Load(snapshot, root); restored.AdoptState(root);
        Assert.Equal(original.StateDigest(), restored.StateDigest());
        Feed(original, lines.Skip(cut), true); Feed(restored, lines.Skip(cut), true);
        Assert.Equal(original.StateDigest(), restored.StateDigest());
        Assert.Equal(States(original), States(restored));
        Assert.Contains(reporting ? "off" : "silent", States(restored).Values);
    }

    [Theory]
    [InlineData(1)] [InlineData(3)] [InlineData(100000)]
    public void The_state_with_fills_and_no_data_does_not_depend_on_the_chunk_size(int chunk)
    {
        var cat = new Catalog();
        Assert.DoesNotContain(cat.Load(Files(), updatePins: true).Diagnostics, d => d.IsError);
        string Digest(bool reporting, int rows)
        {
            var dir = Capture(Lines(reporting));
            try
            {
                var space = cat.Spaces.Keys.Single();
                using var engine = new Engine(cat, space);
                new Replay(engine, cat.Spaces[space], AdapterRegistry.Create("synth"), new ReplayOptions { ChunkRows = rows, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" }).Run(dir);
                return engine.StateDigest();
            }
            finally { Directory.Delete(dir, true); }
        }
        Assert.Equal(Digest(true, 2), Digest(true, chunk));
        Assert.Equal(Digest(false, 2), Digest(false, chunk));
    }
}
