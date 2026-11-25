using System.Text.Json.Nodes;
using Sattle.Runtime;

namespace Sattle.Cli;

/// <summary>Live input: reads the envelope stream of a live source (`--follow FILE`, or the source a host outside the engine adds by `Extensions.LiveSource`), checkpoints by its position, feeds the same adapters as replay, and records every consumed envelope so the stream can be replayed to the same state digest.</summary>
static class Live
{
    /// <summary>The live input of `serve --live`, run under the stop state machine (`LiveInput.RunUntilStopped`): Ctrl-C, SIGTERM or `--seconds` stop it, and its end stops the server. It prints the counts, the position and the state digest when it stopped; the server waits for that before the process ends.</summary>
    public static void Run(SpaceHost host, Program.Options o, Extensions ext, Microsoft.Extensions.Hosting.IHostApplicationLifetime lifetime)
    {
        var outDir = o.Get("live-out") ?? Path.Combine("out", "live-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss"));
        var seconds = long.TryParse(o.Get("seconds"), out var sv) ? sv : o.Duration("seconds", 0) / 1_000_000_000;
        var started = DateTimeOffset.UtcNow;
        using var deadline = new CancellationTokenSource();
        if (seconds > 0) deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        ILiveSource? source = null;
        var (counts, reason) = LiveInput.RunUntilStopped(lifetime, deadline.Token, ct =>
        {
            var inputPath = o.Get("follow");
            source = inputPath is not null ? new AppendFileSource(inputPath)
                : ext.LiveSource?.Invoke(o) ?? throw new ArgumentException("pass --follow FILE");
            Directory.CreateDirectory(Path.Combine(outDir, "live"));
            var recordPath = Path.Combine(outDir, "live", "stream.ndjson");
            using var record = new FileRecording(recordPath);
            string? retrying = null;
            host.LiveStatus = () => new JsonObject
            {
                ["source"] = source.Id, ["position"] = host.SourcePosition(source.Id) is { } p ? JsonNode.Parse(p) : null, ["record"] = recordPath, ["events"] = record.Count.ToString(),
                ["retrying"] = retrying,
                ["lag"] = source.Lag is { } lag ? new JsonObject { ["events"] = lag.Events.ToString(System.Globalization.CultureInfo.InvariantCulture), ["seconds"] = lag.Seconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) } : null,
            };
            // the ready line comes after the first read, when the link to the source is open: a forwarder started on it is not missed
            return LiveInput.Run(host, source, Program.ReplayOptionsOf(o), ct, record,
                ready: () => { retrying = null; Console.WriteLine("live: " + source.Start); },
                retry: (e, n) =>
                {
                    var wait = TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(n - 1, 5)));
                    retrying = e.Message; Console.Error.WriteLine($"live input failed ({e.GetType().Name}: {e.Message}); reading again from the saved position in {wait.TotalSeconds:F0} s");
                    return wait;
                });
        });
        Console.WriteLine($"live stopped after {(DateTimeOffset.UtcNow - started).TotalSeconds:F0} s ({reason}): {counts.Consumed:N0} events, {counts.Duplicates} duplicate(s), {counts.Rejected} rejected, position {(source is null ? null : host.SourcePosition(source.Id))}");
        Console.WriteLine($"state digest {host.StateDigest()}");
        Console.WriteLine($"replay with: sattle replay {outDir} --no-flush   (add the same --advance-every and --lateness if the live run set them)");
    }
}
