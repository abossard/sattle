using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Identity;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Primitives;
using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Cli;

/// <summary>Live input: reads the envelope stream from an Event Hub (Entra auth, no keys), checkpoints by delivery key, feeds the same adapters as replay, and records every consumed envelope so the stream can be replayed to the same state digest.</summary>
static class Live
{
    static string Discover(string rg)
    {
        var psi = new ProcessStartInfo("az", $"eventhubs namespace list -g {rg} --query [0].name -o tsv") { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var name = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit();
        return name.Length > 0 ? name : throw new ArgumentException($"no Event Hubs namespace found in resource group {rg}; pass --eh-namespace");
    }

    /// <summary>Event Hubs as a live source: one partition, positions by sequence number (`{"partition":"0","sequenceNumber":123}`), Entra auth. Reads in batches with a partition receiver (`LiveInput.Batches`), so a cancellation ends the read at the next batch, and the lag is known once a batch was processed. With no position it starts at the newest event (`--from latest`, the default) or the oldest the hub keeps (`--from earliest`).</summary>
    sealed class EventHubsSource(string group, string fqns, string hub, Azure.Core.TokenCredential credential, bool earliest) : ILiveSource
    {
        public string Id => "eventhub:" + hub;
        /// <summary>How far the space is behind the newest event of the partition, after the last batch it processed.</summary>
        public LiveLag? Lag { get; private set; }
        /// <summary>Where the last read started, for the line that says the input is ready.</summary>
        public string Start { get; private set; } = "";
        public IEnumerable<LiveEvent?> Read(string? after, CancellationToken ct)
        {
            string[] ids;
            var client = new EventHubConsumerClient(group, fqns, hub, credential);
            try { ids = client.GetPartitionIdsAsync(ct).GetAwaiter().GetResult(); } finally { client.CloseAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(5)); }
            if (ids.Length != 1) Console.Error.WriteLine($"warning: hub has {ids.Length} partitions; only a single partition gives a total order, reading partition {ids[0]}");
            var partition = ids[0]; long last = -1;
            if (after is not null && JsonNode.Parse(after) is { } p) { partition = p["partition"]!.GetValue<string>(); last = long.Parse(p["sequenceNumber"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture); }
            var pos = last >= 0 ? EventPosition.FromSequenceNumber(last, isInclusive: false) : earliest ? EventPosition.Earliest : EventPosition.Latest;
            Start = $"{hub} partition {partition} from {(last >= 0 ? "sequence number " + last : earliest ? "earliest" : "latest")}";
            var receiver = new PartitionReceiver(group, partition, pos, fqns, hub, credential, new PartitionReceiverOptions { PrefetchCount = 1000, TrackLastEnqueuedEventProperties = true });
            (IReadOnlyList<LiveEvent>, LiveLag?) Receive()
            {
                var batch = receiver.ReceiveBatchAsync(500, TimeSpan.FromMilliseconds(500), ct).GetAwaiter().GetResult().ToList();
                var newest = receiver.ReadLastEnqueuedEventProperties();
                var lag = batch.Count > 0 && newest.SequenceNumber is { } ns && newest.EnqueuedTime is { } nt ? new LiveLag(ns - batch[^1].SequenceNumber, Math.Max(0, (nt - batch[^1].EnqueuedTime).TotalSeconds)) : null;
                return ([.. batch.Select(e => new LiveEvent(e.EventBody.ToString(), new JsonObject { ["partition"] = partition, ["sequenceNumber"] = e.SequenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) }.ToJsonString()))], lag);
            }
            try { foreach (var ev in LiveInput.Batches(Receive, lag => Lag = lag)) yield return ev; }
            finally { receiver.CloseAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(5)); }
        }
    }

    /// <summary>The live input of `serve --live`, run under the stop state machine (`LiveInput.RunUntilStopped`): Ctrl-C, SIGTERM or `--seconds` stop it, and its end stops the server. It prints the counts, the position and the state digest when it stopped; the server waits for that before the process ends.</summary>
    public static void Run(SpaceHost host, Program.Options o, Microsoft.Extensions.Hosting.IHostApplicationLifetime lifetime)
    {
        var hub = o.Get("hub") ?? "sattle-ingest";
        var outDir = o.Get("live-out") ?? Path.Combine("out", "live-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss"));
        var seconds = long.TryParse(o.Get("seconds"), out var sv) ? sv : o.Duration("seconds", 0) / 1_000_000_000;
        var started = DateTimeOffset.UtcNow;
        using var deadline = new CancellationTokenSource();
        if (seconds > 0) deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        var (counts, reason) = LiveInput.RunUntilStopped(lifetime, deadline.Token, ct =>
        {
            var inputPath = o.Get("follow");
            var ns = inputPath is null ? o.Get("eh-namespace") ?? Discover(o.Get("eh-resource-group") ?? throw new ArgumentException("pass --eh-namespace NS, --eh-resource-group GROUP, or --follow FILE")) : null;
            Directory.CreateDirectory(Path.Combine(outDir, "live"));
            var recordPath = Path.Combine(outDir, "live", "stream.ndjson");
            using var record = new FileRecording(recordPath);
            var bus = ns is null ? null : new EventHubsSource(o.Get("group") ?? "$Default", ns.Contains('.') ? ns : ns + ".servicebus.windows.net", hub, new AzureCliCredential(), o.Get("from") == "earliest");
            ILiveSource source = (ILiveSource?)bus ?? new AppendFileSource(inputPath!);
            string? retrying = null;
            host.LiveStatus = () => new JsonObject
            {
                ["hubs"] = bus is null ? new JsonArray() : new JsonArray(hub), ["namespace"] = ns, ["file"] = inputPath, ["position"] = host.SourcePosition(source.Id) is { } p ? JsonNode.Parse(p) : null, ["record"] = recordPath, ["events"] = record.Count.ToString(),
                ["retrying"] = retrying,
                ["lag"] = bus?.Lag is { } lag ? new JsonObject { ["events"] = lag.Events.ToString(System.Globalization.CultureInfo.InvariantCulture), ["seconds"] = lag.Seconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) } : null,
            };
            // the ready line comes after the first read, when the link to the hub is open: a forwarder started on it is not missed
            return LiveInput.Run(host, source, Program.ReplayOptionsOf(o), ct, record,
                ready: () => { retrying = null; Console.WriteLine("live: " + (bus?.Start ?? "following " + inputPath)); },
                retry: (e, n) =>
                {
                    var wait = TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(n - 1, 5)));
                    retrying = e.Message; Console.Error.WriteLine($"live input failed ({e.GetType().Name}: {e.Message}); reading again from the saved position in {wait.TotalSeconds:F0} s");
                    return wait;
                });
        });
        Console.WriteLine($"live stopped after {(DateTimeOffset.UtcNow - started).TotalSeconds:F0} s ({reason}): {counts.Consumed:N0} events, {counts.Duplicates} duplicate(s), {counts.Rejected} rejected, position {host.SourcePosition("eventhub:" + hub)}");
        Console.WriteLine($"state digest {host.StateDigest()}");
        Console.WriteLine($"replay with: sattle replay {outDir} --no-flush --late-horizon 3   (add the same --advance-every and --lateness if the live run set them)");
    }
}
