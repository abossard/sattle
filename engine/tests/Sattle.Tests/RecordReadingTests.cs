using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Records are read once, as UTF-8 bytes, into one token index per record, and the adapters write their texts straight from it into the rows (#45).</summary>
public class RecordReadingTests
{
    static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string Line(long seq, object payload, string source = "hubble") =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["v"] = 1, ["source"] = source, ["capturedAt"] = "2026-01-01T10:00:00.000Z", ["seq"] = seq, ["key"] = $"{source}/{seq}", ["payload"] = payload }, Relaxed);

    static string Flow(long seq, string pod = "api-1", string name = "db.example.com") =>
        Line(seq, new { flow = new { time = "2026-01-01T10:00:00.123456789Z", verdict = "FORWARDED", source = new { @namespace = "shop", pod_name = pod }, destination = new { @namespace = "data", pod_name = "db-0" }, IP = new { destination = "10.0.0.9" }, destination_names = new[] { name } } });

    static string File(params byte[][] parts)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-reading-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        System.IO.File.WriteAllBytes(Path.Combine(dir, "a.ndjson"), [.. parts.SelectMany(p => p)]);
        return dir;
    }

    /// <summary>Rows into tables of their own, with a text table of their own.</summary>
    sealed class Rows(ISourceAdapter adapter) : IRowSink
    {
        public readonly Interner Texts = new();
        readonly Dictionary<string, RowWriter> writers = [];
        public RowWriter Begin(string rowType)
        {
            if (writers.TryGetValue(rowType, out var w)) return w;
            var c = adapter.Contracts.First(x => x.RowType == rowType);
            return writers[rowType] = new RowWriter(new Layout([.. c.Fields.Select(f => f.Name)], [.. c.Fields.Select(f => f.Kind == "Decimal")]), Texts);
        }
        public void Rejected(string source, string reason) => throw new InvalidOperationException(reason);
        public void SourceLoss(long timeNs, long count, string reason = "sourceLoss") { }
        readonly Dictionary<string, Batch> built = [];
        /// <summary>A text of the first row of a type.</summary>
        public string? Text(string rowType, string column)
        {
            var c = GetColumn(rowType, column);
            return c.Present(0) ? Texts.Get((int)c.L[0]) : null;
        }

        public bool? Flag(string rowType, string column)
        {
            var c = GetColumn(rowType, column);
            return c.Present(0) ? c.L[0] != 0 : null;
        }

        Column GetColumn(string rowType, string column)
        {
            if (!built.TryGetValue(rowType, out var b)) built[rowType] = b = writers[rowType].Build()!;
            return b.Cols[b.Layout.Index(column)];
        }
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("False", false)]
    [InlineData("Unknown", null)]
    [InlineData(null, null)]
    public void A_pod_reports_readiness_only_when_the_source_knows_it(string? status, bool? ready)
    {
        var adapter = new KubernetesAdapter("test");
        var rows = new Rows(adapter);
        var payload = new { type = "ADDED", @object = new {
            kind = "Pod", metadata = new { uid = "p1", @namespace = "default", name = "api" },
            status = new { conditions = status is null ? [] : new[] { new { type = "Ready", status } } }
        } };
        using var envelope = CaptureReader.Parse(Line(1, payload, "kubernetes-watch"), "", 1)!;
        adapter.Consume(envelope, rows);
        Assert.Equal(ready, rows.Flag("PodRow", "ready"));
    }

    public static TheoryData<string, string> Separators => new()
    {
        { "lf", "\n" }, { "crlf", "\r\n" }, { "bom", "\n" }, { "no final newline", "\n" }, { "empty lines", "\n\n\n" }, { "a line longer than the buffer", "\n" },
    };

    [Theory]
    [MemberData(nameof(Separators))]
    public void A_capture_file_gives_the_envelopes_of_its_lines(string shape, string separator)
    {
        string[] lines = [Flow(1), Flow(2, pod: shape == "a line longer than the buffer" ? new string('p', 200_000) : "api-2"), Flow(3)];
        var text = string.Join(separator, lines) + (shape == "no final newline" ? "" : separator);
        var dir = File(shape == "bom" ? [0xEF, 0xBB, 0xBF] : [], Encoding.UTF8.GetBytes(text));
        var read = CaptureReader.Read(dir).Select(e => (e.Source, e.CapturedNs, e.Seq, e.Payload.GetRawText())).ToList();
        Assert.Equal(lines.Select((l, i) => { using var e = CaptureReader.Parse(l, "", i + 1)!; return (e.Source, e.CapturedNs, e.Seq, e.Payload.GetRawText()); }), read);
    }

    [Theory]
    [InlineData("api-7f9c", "api-7f9c")]
    [InlineData("café-0", "café-0")]                    // UTF-8 beyond ASCII, written unescaped
    [InlineData("say \"hi\"", "say \"hi\"")]            // escaped in the record
    [InlineData("", null)]                              // empty is absent
    public void A_text_of_a_record_is_written_as_its_value(string pod, string? row)
    {
        var hubble = new HubbleAdapter("c1"); var rows = new Rows(hubble);
        using var e = CaptureReader.Parse(Flow(1, pod: pod), "", 1)!;
        hubble.Consume(e, rows);
        Assert.Equal(row, rows.Text("FlowRow", "srcPod"));
    }

    [Fact]
    public void A_text_longer_than_the_stack_buffer_is_written_whole()
    {
        var hubble = new HubbleAdapter("c1"); var rows = new Rows(hubble); var pod = new string('p', 300);
        using var e = CaptureReader.Parse(Flow(1, pod: pod), "", 1)!;
        hubble.Consume(e, rows);
        Assert.Equal(pod, rows.Text("FlowRow", "srcPod"));
    }

    [Theory]
    [InlineData("Db.Example.COM:5432")]
    [InlineData("https://u@Api.example.net/x?y")]
    [InlineData(" lower.example.org ")]
    [InlineData("not a host!")]
    [InlineData("é.example")]
    public void The_host_of_a_name_is_the_one_of_its_text(string name)
    {
        var hubble = new HubbleAdapter("c1"); var rows = new Rows(hubble);
        using var e = CaptureReader.Parse(Flow(1, name: name), "", 1)!;
        hubble.Consume(e, rows);
        Assert.Equal(KubernetesAdapter.HostOf(name), rows.Text("FlowRow", "dstName"));
    }

    [Fact]
    public void An_audit_event_inside_a_string_is_read_with_its_escapes()
    {
        var audit = JsonSerializer.Serialize(new { stage = "ResponseComplete", auditID = "a-1", verb = "get", requestReceivedTimestamp = "2026-01-01T10:00:00Z", objectRef = new { resource = "pods", @namespace = "shop", name = "café \"x\"" }, user = new { username = "system:admin" } }, Relaxed);
        var aks = new AksAuditAdapter(); var rows = new Rows(aks);
        using var e = CaptureReader.Parse(Line(1, new { records = new[] { new { category = "kube-audit-admin", properties = new { log = audit } } } }, "eventhub:sattle-aks"), "", 1)!;
        aks.Consume(e, rows);
        Assert.Equal("café \"x\"", rows.Text("AuditRow", "name"));
        Assert.Equal("system:admin", rows.Text("AuditRow", "user"));
    }

    /// <summary>Before: each line was a string, copied to UTF-8, indexed into arrays left to the collector, and every value read was a string of its own: 3,227 B per record for this flow of about 400 B.</summary>
    [Fact]
    public void Reading_and_consuming_a_flow_of_known_texts_allocates_only_the_envelope()
    {
        const int n = 400;
        var dir = File(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, n).Select(i => Flow(i) + "\n"))));
        var hubble = new HubbleAdapter("c1"); var rows = new Rows(hubble);
        long before = 0; var i = 0;
        foreach (var e in CaptureReader.Read(dir))
        {
            if (++i == n / 2) before = GC.GetAllocatedBytesForCurrentThread();       // the texts are interned and the buffers grown by then
            hubble.Consume(e, rows); rows.Begin("FlowRow").Truncate(0);
        }
        var perRecord = (GC.GetAllocatedBytesForCurrentThread() - before) / (n / 2.0);
        Assert.True(perRecord < 1024, $"{perRecord:F0} B per record");
    }
}
