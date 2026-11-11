using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Identity;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
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

    /// <summary>Event Hubs as a live source: one partition, positions by sequence number (`{"partition":"0","sequenceNumber":123}`), Entra auth.</summary>
    sealed class EventHubsSource(EventHubConsumerClient client, string hub, string? from) : ILiveSource
    {
        public string Id => "eventhub:" + hub;
        public IEnumerable<LiveEvent?> Read(string? after, CancellationToken ct)
        {
            var ids = client.GetPartitionIdsAsync(ct).GetAwaiter().GetResult();
            if (ids.Length != 1) Console.Error.WriteLine($"warning: hub has {ids.Length} partitions; only a single partition gives a total order, reading partition {ids[0]}");
            var partition = ids[0]; long last = -1;
            if (after is not null && JsonNode.Parse(after) is { } p) { partition = p["partition"]!.GetValue<string>(); last = long.Parse(p["sequenceNumber"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture); }
            var pos = last >= 0 ? EventPosition.FromSequenceNumber(last, isInclusive: false) : from switch { "earliest" => EventPosition.Earliest, _ => EventPosition.Latest };
            Console.WriteLine($"live: {hub} partition {partition} from {(last >= 0 ? "sequence number " + last : from ?? "latest")}");
            foreach (var pe in client.ReadEventsFromPartitionAsync(partition, pos, new ReadEventOptions { MaximumWaitTime = TimeSpan.FromMilliseconds(500) }, ct).ToBlockingEnumerable(ct))
            {
                if (pe.Data is null) { yield return null; continue; }
                yield return new LiveEvent(pe.Data.EventBody.ToString(), new JsonObject { ["partition"] = partition, ["sequenceNumber"] = pe.Data.SequenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) }.ToJsonString());
            }
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
        host.Mode = "live";
        FileRecording? rec = null; var started = DateTimeOffset.UtcNow;
        host.LiveStatus = () => new JsonObject
        {
            ["hubs"] = new JsonArray(hub), ["namespace"] = ns, ["position"] = host.SourcePosition("eventhub:" + hub) is { } p ? JsonNode.Parse(p) : null, ["record"] = recordPath, ["events"] = (rec?.Count ?? 0).ToString(),
            ["lag"] = new JsonObject { ["events"] = (rec?.Count ?? 0).ToString() },
        };
        var client = new EventHubConsumerClient(group, fqns, hub, new AzureCliCredential());
        var cts = new CancellationTokenSource();
        if (seconds > 0) cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        using var record = new FileRecording(recordPath); rec = record;
        var source = new EventHubsSource(client, hub, o.Get("from"));
        LiveCounts counts;
        try { counts = LiveInput.Run(host, source, Program.ReplayOptionsOf(o, cluster), cts.Token, record); }
        catch (OperationCanceledException) { counts = new LiveCounts(record.Count, 0, 0); host.LiveFlush(); }
        Console.WriteLine($"live stopped after {(DateTimeOffset.UtcNow - started).TotalSeconds:F0} s: {counts.Consumed:N0} events, {counts.Duplicates} duplicate(s), {counts.Rejected} rejected, position {host.SourcePosition("eventhub:" + hub)}");
        Console.WriteLine($"state digest {host.StateDigest()}");
        Console.WriteLine($"replay with: sattle replay {outDir} --no-flush --late-horizon 3 --cluster {cluster}   (add the same --advance-every and --lateness if the live run set them)");
        if (seconds > 0) Environment.Exit(0);
    }
}
