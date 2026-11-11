using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>`serve --live` records every envelope it consumed, in arrival order, to `<live-out>/live/stream.ndjson`. Replaying that directory with the live settings (`--no-flush --late-horizon 3`) reproduces the state digest of the live run, whatever the arrival order and wherever the live input flushed its chunks.</summary>
[Trait("Category", "Slow")]
public class LiveReproducibilityTests
{
    const long Min = 60_000_000_000;

    /// <summary>The envelope lines of a capture, in capture-time order, then moved around: every line may arrive up to `distance` places away from its time position (a hub delivers per partition, sources are not synchronized).</summary>
    public static List<string> Arrival(string dir, int seed, int distance)
    {
        var lines = new List<(long Ns, string Text)>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.ndjson", SearchOption.AllDirectories).Where(f => !Path.GetRelativePath(dir, f).StartsWith("eventhub", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            foreach (var text in File.ReadLines(f))
                if (CaptureReader.Parse(text, f, 0) is { } e) lines.Add((e.CapturedNs, text));
        var ordered = lines.OrderBy(l => l.Ns).Select(l => l.Text).ToList();
        var rnd = new Random(seed);
        for (var i = 0; i < ordered.Count; i++) { var j = Math.Min(ordered.Count - 1, i + rnd.Next(distance + 1)); (ordered[i], ordered[j]) = (ordered[j], ordered[i]); }
        return ordered;
    }

    /// <summary>What Live.Run does with each event: record it, process it, flush the pending chunk now and then (here at random points).</summary>
    static string LiveRun(IReadOnlyList<string> arrival, string cluster, int seed, out string digest)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "live"));
        File.WriteAllLines(Path.Combine(dir, "live", "stream.ndjson"), arrival);
        var files = Catalog.ReadDirectory(Repo.Definitions);
        using var host = new SpaceHost(files, new HostOptions { Cluster = cluster }) { Mode = "live" };
        var replay = new Replay(host, host.Space, host.Adapters, new ReplayOptions { ChunkRows = 2000, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = cluster });
        var rnd = new Random(seed);
        foreach (var text in arrival)
        {
            replay.Process(CaptureReader.Parse(text, "eventhub:sattle-ingest", 0)!, pace: false);
            if (rnd.Next(7) == 0) replay.FlushPending();
        }
        replay.FlushPending();
        digest = host.StateDigest();
        return dir;
    }

    static string Replayed(string dir, string cluster, int horizon)
    {
        var cat = Repo.LoadExamples(out _);
        var space = cat.Spaces.Keys.Single();
        var adapters = AdapterRegistry.Create(cluster);
        using var engine = new Engine(cat, space, new EngineOptions { Tenant = "lab", LateHorizonWindows = horizon });
        new Replay(engine, cat.Spaces[space], adapters, new ReplayOptions { ChunkRows = 2000, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = cluster, FlushAtEnd = false }).Run(dir);
        return engine.StateDigest();
    }

    [Theory]
    [InlineData(1, 0)] [InlineData(2, 6)] [InlineData(3, 40)]
    public void Replaying_the_live_recording_with_the_live_settings_reproduces_the_live_digest_on_the_fixtures(int seed, int distance)
    {
        var arrival = Arrival(Repo.Fixtures, seed, distance);
        var dir = LiveRun(arrival, Repo.ClusterOf(Repo.Fixtures), seed, out var live);
        try { Assert.Equal(live, Replayed(dir, Repo.ClusterOf(Repo.Fixtures), horizon: 3)); }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_recording_is_read_in_arrival_order_not_in_capture_time_order()
    {
        // Arrival order is part of the input: the recording is one file and is read in file order.
        var arrival = Arrival(Repo.Fixtures, 3, 40);
        var dir = LiveRun(arrival, Repo.ClusterOf(Repo.Fixtures), 3, out _);
        try
        {
            string Payload(string line) { using var d = System.Text.Json.JsonDocument.Parse(line); return d.RootElement.GetProperty("payload").GetRawText(); }
            Assert.Equal(arrival.Select(Payload), CaptureReader.Read(dir).Select(e => e.Payload.GetRawText()));
            Assert.NotEqual(arrival.OrderBy(l => CaptureReader.Parse(l, "", 0)!.CapturedNs).ToList(), arrival);   // the arrival really differs from capture-time order
        }
        finally { Directory.Delete(dir, true); }
    }

    [Trait("Category", "Capture")]
    [CaptureFact]
    public void Replaying_the_live_recording_reproduces_the_live_digest_on_the_newest_capture_when_present()
    {
        var c = Path.Combine(Repo.Root, "lab", "captures");
        var cap = Directory.EnumerateDirectories(c).Order(StringComparer.Ordinal).Last();
        var cluster = Sattle.Cli.Program.DetectCluster(cap);
        var arrival = Arrival(cap, 5, 25);
        var dir = LiveRun(arrival, cluster, 5, out var live);
        try { Assert.Equal(live, Replayed(dir, cluster, horizon: 3)); }
        finally { Directory.Delete(dir, true); }
    }
}
