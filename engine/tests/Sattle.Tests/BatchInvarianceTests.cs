using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Batch invariance (T6) on every capture present, and the premise it rests on for identities: the order in which identity keys get bound does not change the result.</summary>
public class BatchInvarianceTests
{
    static IReadOnlyList<string> Captures()
    {
        return Repo.CaptureDirs;
    }

    /// <summary>One row per (capture, chunk size 1 or 50), each compared with the run at chunk size 100000; a placeholder row when there are no captures (they are gitignored).</summary>
    public static IEnumerable<object[]> CaptureChunks() => Captures().Count == 0 ? [["", 0]] : Captures().SelectMany(d => new[] { 1, 50 }.Select(c => new object[] { Path.GetFileName(d), c }));

    /// <summary>T6 on every capture, with the settings of `sattle replay` (every window revisable, the cluster of the capture): chunk sizes 1 and 50 give the state digest, the graph digests and the integrated output of chunk size 100000, the integrated output of every graph equals its final state, and no identity conflicts. The chunk size 1 mismatch on the first capture was a merge case: a flow naming a pod before the pod row arrived bound the key to a provisional identity that the later row could not join.</summary>
    [Trait("Category", "Capture")]
    [CaptureTheory]
    [MemberData(nameof(CaptureChunks))]
    public void Every_capture_gives_the_same_state_digest_at_chunk_sizes_1_50_and_100000(string capture, int chunk)
    {
        var dir = Path.Combine(Repo.Captures, capture);
        var reference = CaptureRuns.Of(dir, CaptureRuns.Full);
        var r = CaptureRuns.Of(dir, chunk);
        Assert.Equal(reference.StateDigest, r.StateDigest);
        Assert.Equal(0, reference.Conflicts);
        Assert.Equal(0, r.Conflicts);
        Assert.Equal(reference.GraphDigests, r.GraphDigests);
        Assert.Equal(reference.Integrated, r.Integrated);                                           // integrated output equals batch output (Quine invariant)
        Assert.All(r.IntegratedIsFinal.Concat(reference.IntegratedIsFinal), x => Assert.True(x.Value, $"{x.Key}: the integrated output is not the final graph"));
        Assert.True(r.MasterEntities > 100);
    }

    static string Line(string source, string at, long seq, object payload) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?> { ["v"] = 1, ["source"] = source, ["capturedAt"] = at, ["seq"] = seq, ["key"] = $"{source}/{seq}", ["meta"] = null, ["payload"] = payload });

    static string Pod(string at, string uid, string name, long seq) => Line("kubernetes-watch", at, seq,
        new Dictionary<string, object> { ["type"] = "ADDED", ["object"] = new Dictionary<string, object> { ["kind"] = "Pod", ["metadata"] = new Dictionary<string, object> { ["uid"] = uid, ["name"] = name, ["namespace"] = "x", ["resourceVersion"] = seq.ToString() }, ["status"] = new { phase = "Running" } } });

    static string Flow(string at, string src, string dst, long seq) => Line("hubble", at, seq,
        new { flow = new { time = at, verdict = "FORWARDED", source = new { @namespace = "x", cluster_name = "synth", pod_name = src }, destination = new { @namespace = "x", cluster_name = "synth", pod_name = dst } }, node_name = "n1", time = at });

    static string Capture(IEnumerable<string> lines)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "x"));
        File.WriteAllLines(Path.Combine(dir, "x", "a.ndjson"), lines);
        return dir;
    }

    /// <summary>Pods a-1 and b-1 and 12 flows between them in minute 0, a flow in minute 3 that closes minute 0. The pod rows arrive before the flows, or after minute 0 is closed (the closed window then bound the pod names to provisional identities and the pod rows merge them).</summary>
    static IEnumerable<string> Lines(bool podsFirst)
    {
        var pods = new[] { Pod(podsFirst ? "2026-11-10T10:00:01.000Z" : "2026-11-10T10:04:00.000Z", "ua", "a-1", 1), Pod(podsFirst ? "2026-11-10T10:00:02.000Z" : "2026-11-10T10:04:01.000Z", "ub", "b-1", 2) };
        var flows = Enumerable.Range(0, 12).Select(i => Flow($"2026-11-10T10:00:{10 + i:D2}.000Z", i % 2 == 0 ? "a-1" : "b-1", i % 2 == 0 ? "b-1" : "a-1", 100 + i)).Append(Flow("2026-11-10T10:03:30.000Z", "a-1", "b-1", 200));
        return podsFirst ? pods.Concat(flows) : flows.Concat(pods);
    }

    /// <summary>The premise of the batch-invariance theorem for identities: binding order is not part of the result. Here the pod row binds a key before the flows that name the pod, or after them (then the flows bind a provisional identity and the pod row merges it); every chunk size.</summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(5)] [InlineData(2000)]
    public void The_result_does_not_depend_on_the_order_in_which_identity_keys_are_bound(int chunk)
    {
        var a = Capture(Lines(podsFirst: true)); var b = Capture(Lines(podsFirst: false));
        try
        {
            var opts = new EngineOptions { LateHorizonWindows = 1_000_000 };
            var first = ReplayTests.Replay(a, chunk, "synth", o: opts);
            var second = ReplayTests.Replay(b, chunk, "synth", o: opts);
            var i0 = first.Engine.Identities; var i1 = second.Engine.Identities;
            Assert.True(i1.Merges > i0.Merges, $"the order of the bindings did not change (merged {i0.Merges} against {i1.Merges}): the test would say nothing");
            Assert.Equal(0, i1.Conflicts);
            Assert.Equal(2, second.Engine.Master.State.EntityCount);
            string Dump(Graph g) => string.Join("\n", g.Elements().OrderBy(e => e.Key.ToString(), StringComparer.Ordinal).Select(ElementJson.Encode));
            Assert.Equal(Dump(first.Engine.Master.State), Dump(second.Engine.Master.State));
            Assert.Equal(first.Engine.Master.State.Digest(), second.Engine.Master.State.Digest());
            foreach (var g in first.Engine.Graphs) Assert.Equal(g.State.Digest(), second.Engine.Graph(g.Name).State.Digest());
            Assert.Equal(first.Engine.StateDigest(), second.Engine.StateDigest());
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
    }

    // ---- the two causes of the chunk-size-50 digest difference of turn 5, as unit tests (the capture digests above cover them only in aggregate) ----

    const long Min = 60_000_000_000;
    static readonly long T0 = 1_794_300_000_000_000_000 / Min * Min;
    const string Resource = "/subscriptions/s/resourcegroups/rg/providers/microsoft.storage/storageaccounts/acct";

    static void Resources(Engine e)
    {
        var rw = new RowWriter(e.RowLayout("resources"), e.Interner);
        rw.Begin(); rw.Text("id", Resource); rw.Text("name", "acct"); rw.Text("type", "microsoft.storage/storageaccounts"); rw.Text("location", null); rw.Text("resourceGroup", null); rw.Text("subscriptionId", null); rw.Text("parentId", null);
        e.Ingest("resources", rw.Build()!, 1);
    }

    static void Activity(Engine e, long seq, string id, long offsetNs, int count)
    {
        var rw = new RowWriter(e.RowLayout("activity"), e.Interner);
        for (var i = 0; i < count; i++) { rw.Begin(); rw.Instant("time", T0 + offsetNs + i * 1_000_000_000L); rw.Text("resourceId", id); rw.Text("operation", "op"); rw.Text("status", "Succeeded"); }
        e.Ingest("activity", rw.Build()!, seq);
    }

    static Dictionary<long, decimal> Changes(Engine e) =>
        ((InMemoryTelemetryStore)e.Telemetry).Dump(e.Master.Key).Where(x => e.Interner.Get(x.Prop) == "d:sattle.azure/prop:changes").ToDictionary(x => x.Ws, x => x.Value);

    /// <summary>B1: the Activity Log writes one resource under two spellings of its id. The group is keyed by the lowercased id, so every batching gives one aggregate for the element and window; keyed by the raw text it gave two and the telemetry store kept the last one written.</summary>
    [Theory]
    [InlineData("one batch", false, false)]
    [InlineData("two batches, the 3-row spelling first", true, false)]
    [InlineData("two batches, the 2-row spelling first", true, true)]
    public void Two_spellings_of_one_resource_id_count_into_one_window_whatever_the_batching(string name, bool split, bool reverse)
    {
        _ = name;
        using var e = EngineTests.NewEngine();
        Resources(e);
        var upper = Resource.Replace("resourcegroups", "resourceGroups");
        if (!split)
        {
            var rw = new RowWriter(e.RowLayout("activity"), e.Interner);
            for (var i = 0; i < 5; i++) { rw.Begin(); rw.Instant("time", T0 + i * 1_000_000_000L); rw.Text("resourceId", i < 3 ? Resource : upper); rw.Text("operation", "op"); rw.Text("status", "Succeeded"); }
            e.Ingest("activity", rw.Build()!, 2);
        }
        else if (!reverse) { Activity(e, 2, Resource, 0, 3); Activity(e, 3, upper, 5_000_000_000L, 2); }
        else { Activity(e, 2, upper, 5_000_000_000L, 2); Activity(e, 3, Resource, 0, 3); }
        e.Advance(T0 + 3 * Min);
        Assert.Equal(5m, Changes(e).Single().Value);                           // one window of 5, not 3 or 2
    }

    /// <summary>B2: a row for a window whose state is gone (beyond the late horizon of three widths) used to create a new window that replaced the value emitted earlier. It is dropped, counted, and changes nothing.</summary>
    [Theory]
    [InlineData(1, 10)] [InlineData(4, 10)] [InlineData(1, 30)] [InlineData(2, 5)]
    public void A_row_for_a_window_beyond_the_late_horizon_is_dropped_and_counted_and_never_replaces_what_was_emitted(int lateRows, int laterMinutes)
    {
        using var e = EngineTests.NewEngine();
        Resources(e);
        Activity(e, 2, Resource, 0, 3);
        e.Advance(T0 + 2 * Min);                                               // the window closes with 3
        Assert.Equal(3m, Changes(e).Single().Value);
        e.Advance(T0 + laterMinutes * Min);                                    // beyond the horizon: the window's state is dropped
        Activity(e, 3, Resource, 5_000_000_000L, lateRows);                     // late rows for it
        e.Advance(T0 + (laterMinutes + 2) * Min);
        Assert.Equal(3m, Changes(e).Single().Value);
        Assert.True(e.Master.Cx.LateDropped >= lateRows);
    }

    const string Wide = """
        module d @ 1.0.0;
        entity Day { k: Text; hits?: Integer; }
        identity day for Day version "1" { k: Text by exact; }
        rows R { k: Text; t: Instant; }
        projection ingest ingestion output d {
          in a: rows R;
          rule observed readonly "test" {
            let w = a |> window on t width WIDTH late revise;
            let p = w |> group by { k: k, windowStart: windowStart } aggregate { hits: count() };
            emit entity Day from p identity day(k: k) set { k: k, hits: hits };
          }
        }
        """;
    const string WideSpace = """
        module ds @ 1.0.0;
        import d @ 1.0.0 sha256:… as d;
        space s {
          source a adapter "sattle.authoring" rows d.R;
          master graph master = d.ingest(a: a);
        }
        """;

    /// <summary>Code review 2, defect 17: a window of any width takes its row and closes, with no late horizon (a replay) and with one of 1,000,000 windows. Before, the width times 1,000,000 passed the greatest time from 2 h 33 min on, so every row counted as too late and was dropped.</summary>
    [Theory]
    [InlineData("1h", null)] [InlineData("3h", null)] [InlineData("168h", null)]
    [InlineData("1h", 1_000_000)] [InlineData("2h", 1_000_000)] [InlineData("3h", 1_000_000)] [InlineData("24h", 1_000_000)] [InlineData("168h", 1_000_000)]
    [InlineData("168h", int.MaxValue)]
    public void A_window_of_any_width_takes_its_rows_and_closes_whatever_the_late_horizon(string width, int? horizon)
    {
        var cat = new Sattle.Language.Catalog();
        Assert.Empty(cat.Load([new Sattle.Language.SourceFile("d.sattle", Wide.Replace("WIDTH", width)), new Sattle.Language.SourceFile("ds.sattle", WideSpace)], updatePins: true).Diagnostics.Where(x => x.IsError).Select(x => x.ToString()));
        using var e = new Engine(cat, cat.Spaces.Keys.Single(), new EngineOptions { LateHorizonWindows = horizon });
        var w = Sattle.Language.Lexer.DurationNs(width); var start = 1_794_300_000_000_000_000 / w * w;
        e.Advance(start + 1);                                    // a replay advances before its first row
        var rw = new RowWriter(e.RowLayout("a"), e.Interner);
        rw.Begin(); rw.Text("k", "a"); rw.Instant("t", start + 2);
        e.Ingest("a", rw.Build()!, 1);
        e.Advance(start + w + 1);                                // the window [start, start + w) closes
        Assert.Equal(0, e.Master.Cx.LateDropped);
        Assert.Single(e.Master.State.Entities);
    }

    /// <summary>The other side of B2: a late row inside the horizon revises the window (the horizon is the only thing that decides).</summary>
    [Theory]
    [InlineData(1)] [InlineData(2)]
    public void A_row_for_a_window_inside_the_late_horizon_revises_it(int lateRows)
    {
        using var e = EngineTests.NewEngine();
        Resources(e);
        Activity(e, 2, Resource, 0, 3);
        e.Advance(T0 + 2 * Min);
        Activity(e, 3, Resource, 5_000_000_000L, lateRows);
        e.Advance(T0 + 4 * Min);
        Assert.Equal(3m + lateRows, Changes(e).Single().Value);
        Assert.Equal(0, e.Master.Cx.LateDropped);
    }
}
