using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The forms of a record envelope that carry no row: a heartbeat and a loss mark (lab/CAPTURE-FORMAT.md, keyed records).</summary>
public class RecordEnvelopeTests
{
    sealed class Sink : IRowSink
    {
        public readonly List<string> Rejections = [];
        public readonly List<(long At, long Count, string Reason)> Losses = [];
        public RowWriter Begin(string rowType) => throw new InvalidOperationException($"a row of {rowType}");
        public void Rejected(string source, string reason) => Rejections.Add(reason);
        public void SourceLoss(long timeNs, long count, string reason = "sourceLoss") => Losses.Add((timeNs, count, reason));
    }

    const string At = "2026-01-01T10:00:00.000Z";
    /// <summary>The kiln space: its sources read the row types `Reading`, `Dependency`, `TaskEvent` and `Clock` through the record adapter.</summary>
    static string Kiln => Path.Combine(Repo.Root, "engine", "examples", "kiln");
    static readonly Lazy<Catalog> KilnCatalog = new(() => SpaceHost.LoadDefinitions(Catalog.ReadDirectory(Kiln), Repo.Projections).Catalog);
    static HostOptions KilnOptions => new() { SpaceName = "kiln", Projections = Repo.Projections };
    static readonly long AtNs = Norm.TryParseInstant(At, out var ns) ? ns : throw new InvalidOperationException();

    [Theory]
    [InlineData("{}", 0, -1L, null)]                                                                      // a heartbeat: progress, no row
    [InlineData("""{"lost":3}""", 0, 3L, "sourceLoss")]                                                   // a loss at the capture time
    [InlineData("""{"lost":0,"reason":"pollFailed"}""", 0, 0L, "pollFailed")]                             // a loss of unknown size
    [InlineData("""{"lost":2,"time":"2026-01-01T09:59:00.000Z"}""", 0, 2L, "sourceLoss")]                 // a loss at its own time
    [InlineData("""{"lost":-1}""", 1, -1L, null)]
    [InlineData("""{"lost":"3"}""", 1, -1L, null)]
    [InlineData("""{"lost":1,"time":"yesterday"}""", 1, -1L, null)]
    [InlineData("""{"values":{"a":1}}""", 1, -1L, null)]                                                  // values without a key
    [InlineData("""{"key":"","values":{}}""", 1, -1L, null)]
    [InlineData("""{"lost":3}""", 1, -1L, null, "Undeclared")]                                          // a loss of a row type the space does not read
    [InlineData("{}", 0, -1L, null, "Undeclared")]                                                       // a heartbeat of a row type the space does not read: progress of no source
    public void A_record_without_a_key_is_a_heartbeat_or_a_loss_mark(string payload, int rejections, long lost, string? reason, string type = "Reading")
    {
        var line = $$"""{"v":1,"source":"records:{{type}}","capturedAt":"{{At}}","seq":1,"key":"k","payload":{{payload}}}""";
        using var e = CaptureReader.Parse(line, "a.ndjson", 1)!;
        var sink = new Sink();
        new RecordAdapter(KilnCatalog.Value, KilnCatalog.Value.Spaces.Values.Single()).Consume(e, sink);
        Assert.Equal(rejections, sink.Rejections.Count);
        if (lost < 0) { Assert.Empty(sink.Losses); return; }
        var (when, count, why) = Assert.Single(sink.Losses);
        Assert.Equal((lost, reason), (count, why));
        Assert.Equal(payload.Contains("time") ? AtNs - 60_000_000_000 : AtNs, when);
    }

    /// <summary>A loss mark delivered twice is one loss: it has no record key that a newer delivery could replace.</summary>
    [Fact]
    public void A_loss_mark_delivered_again_is_a_duplicate()
    {
        using var host = new SpaceHost(Catalog.ReadDirectory(Kiln), KilnOptions);
        host.StartLive(new ReplayOptions { LatenessNs = 0 });
        var line = $$$"""{"v":1,"source":"records:Reading","capturedAt":"{{{At}}}","seq":1,"key":"loss-1","payload":{"lost":3}}""";
        Assert.Equal(SpaceHost.LiveResult.Consumed, host.LiveEnvelope("feed", line, "1"));
        Assert.Equal(SpaceHost.LiveResult.Duplicate, host.LiveEnvelope("feed", line, "2"));
    }

    /// <summary>Envelopes of a source no adapter reads (an `authoring` record of the old capture format) are counted as skipped; a capture of nothing else is refused, not replayed to an empty graph.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_envelope_no_adapter_reads_is_skipped_and_a_capture_of_only_those_is_refused(bool withRecord)
    {
        var dir = Directory.CreateTempSubdirectory("sattle-skip-").FullName;
        try
        {
            string[] lines = [
                $$$"""{"v":1,"source":"authoring","capturedAt":"{{{At}}}","seq":1,"key":"a/1","payload":{"kind":"capability","capId":"c"}}""",
                .. withRecord ? [$$$$"""{"v":1,"source":"records:Clock","capturedAt":"{{{{At}}}}","seq":2,"key":"c/1","payload":{"key":"t1","values":{"tick":"1"}}}"""] : Array.Empty<string>()];
            File.WriteAllLines(Path.Combine(dir, "capture.ndjson"), lines);
            using var host = new SpaceHost(Catalog.ReadDirectory(Kiln), KilnOptions);
            var replay = new Replay(host, host.Space, host.Adapters, new ReplayOptions { LatenessNs = 0 });
            if (!withRecord) { Assert.Contains("authoring", Assert.Throws<InvalidOperationException>(() => replay.Run(dir)).Message); return; }
            Assert.Equal(1, replay.Run(dir).Skipped);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A record names its row type, not its source: two sources of one row type through the record adapter are refused, since the second would never get a row.</summary>
    [Fact]
    public void Two_sources_of_one_row_type_are_refused()
    {
        var files = Catalog.ReadDirectory(Kiln);
        var i = files.FindIndex(f => f.Path.EndsWith("workshop.module.gkql.json", StringComparison.Ordinal));
        var module = JsonNode.Parse(files[i].Text)!;
        module["space"]!["sources"]!.AsArray().Add(new JsonObject { ["name"] = "moreReadings", ["adapter"] = "record", ["rows"] = "Reading" });
        files[i] = files[i] with { Text = module.ToJsonString() };
        var refused = Assert.Throws<InvalidOperationException>(() => new SpaceHost(files, KilnOptions));
        Assert.Contains("both read rows of type Reading", refused.Message);
    }
}
