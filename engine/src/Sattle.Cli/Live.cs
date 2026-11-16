using System.Diagnostics;
using System.Text;
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

    /// <summary>Event Hubs as a live source: one partition, positions by sequence number (`{"partition":"0","sequenceNumber":123}`), Entra auth. Reads in batches with a partition receiver, so a cancellation ends the read at the next batch.</summary>
    sealed class EventHubsSource(string group, string fqns, string hub, Azure.Core.TokenCredential credential, string? from) : ILiveSource
    {
        public string Id => "eventhub:" + hub;
        /// <summary>How far the space is behind the newest event of the partition (events, seconds of enqueued time), once the first batch arrived.</summary>
        public (long Events, double Seconds)? Lag { get; private set; }
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
            var pos = last >= 0 ? EventPosition.FromSequenceNumber(last, isInclusive: false) : from switch { "earliest" => EventPosition.Earliest, _ => EventPosition.Latest };
            Start = $"{hub} partition {partition} from {(last >= 0 ? "sequence number " + last : from ?? "latest")}";
            var receiver = new PartitionReceiver(group, partition, pos, fqns, hub, credential, new PartitionReceiverOptions { PrefetchCount = 1000, TrackLastEnqueuedEventProperties = true });
            try
            {
                while (true)
                {
                    var batch = receiver.ReceiveBatchAsync(500, TimeSpan.FromMilliseconds(500), ct).GetAwaiter().GetResult().ToList();
                    if (batch.Count == 0) { yield return null; continue; }
                    var newest = receiver.ReadLastEnqueuedEventProperties();
                    if (newest.SequenceNumber is { } ns && newest.EnqueuedTime is { } nt) Lag = (ns - batch[^1].SequenceNumber, Math.Max(0, (nt - batch[^1].EnqueuedTime).TotalSeconds));
                    foreach (var e in batch)
                        yield return new LiveEvent(e.EventBody.ToString(), new JsonObject { ["partition"] = partition, ["sequenceNumber"] = e.SequenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) }.ToJsonString());
                }
            }
            finally { receiver.CloseAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(5)); }
        }
    }

    /// <summary>One envelope per line, appended and flushed at once.</summary>
    sealed class FileRecording : ILiveRecording, IDisposable
    {
        readonly StreamWriter w;
        public long Count { get; private set; }
        public FileRecording(string path)
        {
            Count = File.Exists(path) ? File.ReadLines(path).LongCount() : 0;
            w = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        }
        public void Append(string text) { w.WriteLine(text); Count++; }
        public void Dispose() => w.Dispose();
    }

    public static void Run(SpaceHost host, Program.Options o, string cluster)
    {
        var ns = o.Get("eh-namespace") ?? Discover(o.Get("eh-resource-group") ?? throw new ArgumentException("pass --eh-namespace NS or --eh-resource-group GROUP (the group the namespace is found in)"));
        var fqns = ns.Contains('.') ? ns : ns + ".servicebus.windows.net";
        var hub = o.Get("hub") ?? "sattle-ingest";
        var group = o.Get("group") ?? "$Default";
        var outDir = o.Get("live-out") ?? Path.Combine("out", "live-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss"));
        Directory.CreateDirectory(Path.Combine(outDir, "live"));
        var recordPath = Path.Combine(outDir, "live", "stream.ndjson");
        var seconds = long.TryParse(o.Get("seconds"), out var sv) ? sv : o.Duration("seconds", 0) / 1_000_000_000;
        FileRecording? rec = null; var started = DateTimeOffset.UtcNow; string? retrying = null;
        var source = new EventHubsSource(group, fqns, hub, new AzureCliCredential(), o.Get("from"));
        host.LiveStatus = () => new JsonObject
        {
            ["hubs"] = new JsonArray(hub), ["namespace"] = ns, ["position"] = host.SourcePosition("eventhub:" + hub) is { } p ? JsonNode.Parse(p) : null, ["record"] = recordPath, ["events"] = (rec?.Count ?? 0).ToString(),
            ["retrying"] = retrying,
            ["lag"] = source.Lag is { } lag ? new JsonObject { ["events"] = lag.Events.ToString(System.Globalization.CultureInfo.InvariantCulture), ["seconds"] = lag.Seconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) } : null,
        };
        var cts = new CancellationTokenSource();
        if (seconds > 0) cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        // Ctrl-C and SIGTERM stop the input, which then prints the state digest and ends the process
        using var onInt = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGINT, c => { c.Cancel = true; cts.Cancel(); });
        using var onTerm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; cts.Cancel(); });
        using var record = new FileRecording(recordPath); rec = record;
        LiveCounts counts;
        // the ready line comes after the first read, when the link to the hub is open: a forwarder started on it is not missed
        try
        {
            counts = LiveInput.RunRetrying(host, source, Program.ReplayOptionsOf(o, cluster), cts.Token,
                backoff: n => TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(n - 1, 5))),
                failed: (e, wait) => { retrying = e.Message; Console.Error.WriteLine($"live input failed ({e.GetType().Name}: {e.Message}); reading again from the saved position in {wait.TotalSeconds:F0} s"); },
                recording: record, ready: () => { retrying = null; Console.WriteLine("live: " + source.Start); });
        }
        catch (OperationCanceledException) { counts = new LiveCounts(record.Count, 0, 0); host.LiveFlush(); }
        Console.WriteLine($"live stopped after {(DateTimeOffset.UtcNow - started).TotalSeconds:F0} s: {counts.Consumed:N0} events, {counts.Duplicates} duplicate(s), {counts.Rejected} rejected, position {host.SourcePosition("eventhub:" + hub)}");
        Console.WriteLine($"state digest {host.StateDigest()}");
        Console.WriteLine($"replay with: sattle replay {outDir} --no-flush --late-horizon 3 --cluster {cluster}   (add the same --advance-every and --lateness if the live run set them)");
        Environment.Exit(0);   // stopped by --seconds or Ctrl-C: the server goes with the input
    }
}
