using System.Diagnostics;
using System.Text.Json.Nodes;
using Azure.Identity;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Primitives;
using Sattle.Cli;

namespace Sattle.Lab;

/// <summary>Event Hubs as a live source: one partition, positions by sequence number (`{"partition":"0","sequenceNumber":123}`), Entra auth (no keys). Reads in batches with a partition receiver (`LiveInput.Batches`), so a cancellation ends the read at the next batch, and the lag is known once a batch was processed. With no position it starts at the newest event (`--from latest`, the default) or the oldest the hub keeps (`--from earliest`).</summary>
public sealed class EventHubsSource(string group, string fqns, string hub, Azure.Core.TokenCredential credential, bool earliest) : ILiveSource
{
    /// <summary>The source of `serve --live` from `--eh-namespace NS` or `--eh-resource-group GROUP` (the first namespace there), `--hub` (`sattle-ingest`), `--group` (`$Default`) and `--from`.</summary>
    public static EventHubsSource FromOptions(Program.Options o)
    {
        var ns = o.Get("eh-namespace") ?? Discover(o.Get("eh-resource-group") ?? throw new ArgumentException("pass --eh-namespace NS, --eh-resource-group GROUP, or --follow FILE"));
        return new EventHubsSource(o.Get("group") ?? "$Default", ns.Contains('.') ? ns : ns + ".servicebus.windows.net", o.Get("hub") ?? "sattle-ingest", new AzureCliCredential(), o.Get("from") == "earliest");
    }

    static string Discover(string rg)
    {
        var psi = new ProcessStartInfo("az", $"eventhubs namespace list -g {rg} --query [0].name -o tsv") { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var name = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit();
        return name.Length > 0 ? name : throw new ArgumentException($"no Event Hubs namespace found in resource group {rg}; pass --eh-namespace");
    }

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
