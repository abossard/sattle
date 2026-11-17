using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>
/// #72: health signals over time written with the forms the KQL profile has, each read by a rule of the workload health on node-a of the estate.
/// A sliding mean (`hop`), a window against the window five minutes before, and the increase of a counter are three node meters added to the ingestion; the workload health reads every node meter through its rules, so it needs no change.
/// The last two compare a series with a copy of its own rows shifted in time (`t + 5m`, `t + 60s`): both copies go into one summary per window, and a window that only one copy reaches is dropped. The shift is a map and the state is the summary's.
/// "No data for N windows" is `telemetry_filled` with a time to live of N windows, shown on the estate by WorkloadTests (scenario E, Hubble silent).
/// </summary>
public class TimeSignalTests
{
    const long Min = 60_000_000_000;
    static readonly DateTimeOffset T0 = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The three meters, added to the ingestion of the examples.</summary>
    const string Meters = """

        // ---- #72: a sliding mean, a window against an earlier one, the increase of a counter; node meters a signal rule can read ----
        // the drop rate of a node over the last five minutes, every minute: the mean of each series, added over the series of the node
        let drops5m = metrics | where metric == "cilium_drop_rate" and isnotempty(instance)
            | summarize n = count(), total = sum(value) by cluster, instance = coalesce(instance, ""), series, windowStart = hop(['time'], 5m, 60s)
            | project cluster, instance, series, windowStart, mean = total / n
            | summarize mean = sum(mean) by cluster, instance, windowStart;
        nodes Meter on m = drops5m | project m = identity("meter", strcat("node/", cluster, "/", instance), "cilium_drop_rate_5m"), windowStart, meterKey = strcat("node/", cluster, "/", instance), meter = "cilium_drop_rate_5m", reading = mean;
        edges MeterOf from m to n with (key = "meterOfNode5m") = drops5m | project m = identity("meter", strcat("node/", cluster, "/", instance), "cilium_drop_rate_5m"), windowStart, n = identity("nodeName", cluster, instance);
        // how much the drop rate of a minute rose against the same minute five minutes before: the rows and a copy of them five minutes later in one summary; a minute without both has no value
        let drops = metrics | where metric == "cilium_drop_rate" and isnotempty(instance) | project cluster, instance = coalesce(instance, ""), series, ['time'], value;
        let dropRise = union (drops | project cluster, instance, series, at = ['time'], value, side = "now"),
                             (drops | project cluster, instance, series, at = ['time'] + 5m, value, side = "before")
            | summarize nNow = countif(side == "now"), now = sum(iff(side == "now", value, 0.0)), nBefore = countif(side == "before"), before = sum(iff(side == "before", value, 0.0))
                by cluster, instance, series, windowStart = bin(at, 60s)
            | where nNow > 0 and nBefore > 0
            | project cluster, instance, series, windowStart, rise = now / nNow - before / nBefore
            | summarize rise = sum(rise) by cluster, instance, windowStart;
        nodes Meter on m = dropRise | project m = identity("meter", strcat("node/", cluster, "/", instance), "cilium_drop_rate_rise5m"), windowStart, meterKey = strcat("node/", cluster, "/", instance), meter = "cilium_drop_rate_rise5m", reading = rise;
        edges MeterOf from m to n with (key = "meterOfNodeRise") = dropRise | project m = identity("meter", strcat("node/", cluster, "/", instance), "cilium_drop_rate_rise5m"), windowStart, n = identity("nodeName", cluster, instance);
        // the increase of a counter in a minute: its highest reading against that of the minute before, the same construction one minute apart; a reading below the one before is a restart, which counts from 0
        let dropCounts = metrics | where metric == "cilium_drop_count_total" and isnotempty(instance) | project cluster, instance = coalesce(instance, ""), series, ['time'], value;
        let dropIncrease = union (dropCounts | project cluster, instance, series, at = ['time'], value, side = "now"),
                                 (dropCounts | project cluster, instance, series, at = ['time'] + 60s, value, side = "before")
            | summarize nNow = countif(side == "now"), now = max(iff(side == "now", value, 0.0)), nBefore = countif(side == "before"), before = max(iff(side == "before", value, 0.0))
                by cluster, instance, series, windowStart = bin(at, 60s)
            | where nNow > 0 and nBefore > 0
            | project cluster, instance, series, windowStart, increase = iff(now >= before, now - before, now)
            | summarize increase = sum(increase) by cluster, instance, windowStart;
        nodes Meter on m = dropIncrease | project m = identity("meter", strcat("node/", cluster, "/", instance), "cilium_drop_count_increase"), windowStart, meterKey = strcat("node/", cluster, "/", instance), meter = "cilium_drop_count_increase", reading = increase;
        edges MeterOf from m to n with (key = "meterOfNodeIncrease") = dropIncrease | project m = identity("meter", strcat("node/", cluster, "/", instance), "cilium_drop_count_increase"), windowStart, n = identity("nodeName", cluster, instance);
        """;

    static readonly Lazy<Catalog> WithMeters = new(() =>
    {
        var files = Repo.DefinitionFiles(forCaptures: false);
        var i = files.FindIndex(f => f.Path.EndsWith("acme.ops.ingest.kql", StringComparison.Ordinal));
        files[i] = files[i] with { Text = files[i].Text + Meters };
        var cat = new Catalog(Repo.Projections);
        var res = cat.Load(files, updatePins: true);
        Assert.True(!res.Diagnostics.Any(d => d.IsError), string.Join("\n", res.Diagnostics.Where(d => d.IsError)));
        return cat;
    });

    static string Record(string source, double sec, string key, object? meta, object payload) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        { ["v"] = 1, ["source"] = source, ["capturedAt"] = T0.AddSeconds(sec).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture), ["seq"] = 0, ["key"] = key, ["meta"] = meta, ["payload"] = payload });

    /// <summary>A Prometheus series of node-a sampled every 30 s from 10:00 for 15 minutes: one range query per minute, as the estate's.</summary>
    static IEnumerable<string> Series(string metric, Func<int, double> valueAt) => Enumerable.Range(0, 15).Select(m => Record("prometheus", m * 60 + 62, $"prometheus/{metric}/72-{m}",
        new { name = metric, query = metric, step = "30s" },
        new { status = "success", data = new { resultType = "matrix", result = new[] { new
        {
            metric = new Dictionary<string, string> { ["instance"] = "node-a", ["direction"] = "ingress" },
            values = new[] { 0, 1 }.Select(k => new object[] { T0.ToUnixTimeSeconds() + m * 60 + k * 30.0, valueAt(m * 2 + k).ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToArray(),
        } } } }));

    static string Rule(string id, string meter, double above, double below) => Record("authoring", 0.9, $"authoring/signalRule/{id}", null,
        new { kind = "signalRule", id, type = "node", meter, above, enterAfter = 1, below, exitAfter = 1, severity = 1, invert = false });

    /// <summary>The state of each rule on node-a after the estate with the added series and rules is replayed up to `seconds` after 10:00.</summary>
    static Dictionary<string, string?> SignalsOfNodeA(string[] series, string[] rules, int seconds)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-time-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var f in Directory.EnumerateFiles(EstateTests.EstateDir, "*.ndjson", SearchOption.AllDirectories))
            {
                var target = Path.Combine(dir, Path.GetRelativePath(EstateTests.EstateDir, f)); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target);
            }
            File.WriteAllLines(Path.Combine(dir, "prometheus", "time-72.ndjson"), series);
            File.WriteAllLines(Path.Combine(dir, "authoring", "time-72.ndjson"), rules);
            var cat = WithMeters.Value;
            var space = cat.Spaces.Keys.Single();
            using var engine = new Engine(cat, space, new EngineOptions { Tenant = "lab", LateHorizonWindows = 3 });
            new Replay(engine, cat.Spaces[space], AdapterRegistry.Create("synth"), new ReplayOptions
            {
                ChunkRows = 50, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth", UntilNs = T0.AddSeconds(seconds).ToUnixTimeMilliseconds() * 1_000_000,
            }).Run(dir);
            static string? P(Entity e, string prop) => e.Props.TryGet("d:acme.workload/prop:" + prop, out var v) ? v is TextValue t ? t.S : v.ToString() : null;
            return engine.Graph("workload").State.Entities.Where(e => P(e, "componentKey") == "node/synth/node-a" && P(e, "rule") is not null)
                .ToDictionary(e => P(e, "rule")!, e => P(e, "signalState"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>Each signal on a series of node-a, by sample (every 30 s from 10:00), with the states expected of its rule and of a rule on the plain meter at `seconds` (the frontier lags by a minute).
    /// Sliding mean: a drop rate of 30 in single minutes 6, 8 and 10 is never two minutes in a row, so the estate's `node-drops` rule (two windows above 5) stays off, while the mean over five minutes is 18 (rule above 10: on); one such minute alone gives a mean of 6 (off).
    /// Window against window: a drop rate that jumps from 0 to 30 at minute 7 rose by 30 against five minutes before (rule above 10: on); one that is 30 all along did not rise (off), though the plain rate is high (`node-drops` on).
    /// Counter: a counter that adds 150 every 30 s grows 300 a minute (rule above 100: on); a still counter grows 0 (off); a counter near 99,000 that grows 20 a minute (off), restarts at minute 8 and counts 200 drops in that minute grew 200 in it, not minus 98,960 (on).</summary>
    [Theory]
    [InlineData("sliding mean, one minute high", "cilium_drop_rate", "spike", 780, "drops-5m=off, node-drops=off")]
    [InlineData("sliding mean, high every other minute", "cilium_drop_rate", "flap", 780, "drops-5m=on, node-drops=off")]
    [InlineData("window against window, a jump", "cilium_drop_rate", "jump", 600, "drops-rise=on")]
    [InlineData("window against window, high all along", "cilium_drop_rate", "steady", 600, "drops-rise=off, node-drops=on")]
    [InlineData("counter, growing", "cilium_drop_count_total", "growing", 600, "drops-increase=on")]
    [InlineData("counter, still", "cilium_drop_count_total", "still", 600, "drops-increase=off")]
    [InlineData("counter, restarted", "cilium_drop_count_total", "restart", 600, "drops-increase=on")]
    public void A_signal_over_time_is_written_in_KQL_and_read_by_a_rule(string name, string metric, string shape, int seconds, string expected)
    {
        Func<int, double> valueAt = shape switch
        {
            "spike" => s => s / 2 == 6 ? 30 : 0,
            "flap" => s => s / 2 is 6 or 8 or 10 ? 30 : 0,
            "jump" => s => s / 2 >= 7 ? 30 : 0,
            "steady" => _ => 30,
            "growing" => s => s / 2 >= 5 ? 1000 + 150 * (s - 9) : 1000,
            "still" => _ => 5000,
            "restart" => s => s / 2 < 8 ? 99_000 + 10 * s : 100 * (s - 15),
            _ => throw new ArgumentException(shape),
        };
        var states = SignalsOfNodeA([.. Series(metric, valueAt)],
            [Rule("drops-5m", "cilium_drop_rate_5m", 10, 5), Rule("drops-rise", "cilium_drop_rate_rise5m", 10, 5), Rule("drops-increase", "cilium_drop_count_increase", 100, 10)], seconds);
        foreach (var pair in expected.Split(", "))
        {
            var (rule, state) = (pair[..pair.IndexOf('=')], pair[(pair.IndexOf('=') + 1)..]);
            Assert.True(states.TryGetValue(rule, out var actual) && actual == state, $"{name}: {rule} is {(states.TryGetValue(rule, out var a) ? a : "absent")}, expected {state}; all: {string.Join(", ", states.Select(kv => kv.Key + "=" + kv.Value))}");
        }
    }
}
