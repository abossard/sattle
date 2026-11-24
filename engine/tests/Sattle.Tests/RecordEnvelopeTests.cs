using Sattle.Core;
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
    public void A_record_without_a_key_is_a_heartbeat_or_a_loss_mark(string payload, int rejections, long lost, string? reason)
    {
        var line = $$"""{"v":1,"source":"records:Reading","capturedAt":"{{At}}","seq":1,"key":"k","payload":{{payload}}}""";
        using var e = CaptureReader.Parse(line, "a.ndjson", 1)!;
        var sink = new Sink();
        new RecordAdapter(null, null).Consume(e, sink);
        Assert.Equal(rejections, sink.Rejections.Count);
        if (lost < 0) { Assert.Empty(sink.Losses); return; }
        var (when, count, why) = Assert.Single(sink.Losses);
        Assert.Equal((lost, reason), (count, why));
        Assert.Equal(payload.Contains("time") ? AtNs - 60_000_000_000 : AtNs, when);
    }
}
