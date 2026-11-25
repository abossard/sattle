using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Batch invariance (T6) on every capture present, and the premise it rests on for identities: the order in which identity keys get bound does not change the result.</summary>
public class BatchInvarianceTests
{
    /// <summary>An engine on the example space, for the tests below that feed it rows batch by batch.</summary>
    static Engine NewEngine()
    {
        var cat = Repo.LoadExamples(out var res);
        Assert.Empty(res.Diagnostics);
        return new Engine(cat, cat.Spaces.Keys.Single());
    }

    static string Line(string type, string at, long seq, string key, object values) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?> { ["v"] = 1, ["source"] = "records:" + type, ["capturedAt"] = at, ["seq"] = seq, ["key"] = $"records:{type}/{seq}", ["meta"] = null, ["payload"] = new { key, values } });

    static string Instance(string at, string uid, string name, long seq) => Line("InstanceRow", at, seq, uid,
        new { site = "synth", uid, @namespace = "x", name, phase = "Running" });

    static string Flow(string at, string src, string dst, long seq) => Line("FlowRow", at, seq, $"flow/{seq}",
        new { time = at, site = "synth", verdict = "FORWARDED", srcNamespace = "x", srcInstance = src, dstNamespace = "x", dstInstance = dst });

    static string Capture(IEnumerable<string> lines)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "x"));
        File.WriteAllLines(Path.Combine(dir, "x", "a.ndjson"), lines);
        return dir;
    }

    /// <summary>Instances a-1 and b-1 and 12 flows between them in minute 0, a flow in minute 3 that closes minute 0. The instance rows arrive before the flows, or after minute 0 is closed (the closed window then bound the instance names to provisional identities and the instance rows merge them).</summary>
    static IEnumerable<string> Lines(bool instancesFirst)
    {
        var instances = new[] { Instance(instancesFirst ? "2026-11-10T10:00:01.000Z" : "2026-11-10T10:04:00.000Z", "ua", "a-1", 1), Instance(instancesFirst ? "2026-11-10T10:00:02.000Z" : "2026-11-10T10:04:01.000Z", "ub", "b-1", 2) };
        var flows = Enumerable.Range(0, 12).Select(i => Flow($"2026-11-10T10:00:{10 + i:D2}.000Z", i % 2 == 0 ? "a-1" : "b-1", i % 2 == 0 ? "b-1" : "a-1", 100 + i)).Append(Flow("2026-11-10T10:03:30.000Z", "a-1", "b-1", 200));
        return instancesFirst ? instances.Concat(flows) : flows.Concat(instances);
    }

    /// <summary>The premise of the batch-invariance theorem for identities: binding order is not part of the result. Here the instance row binds a key before the flows that name the instance, or after them (then the flows bind a provisional identity and the instance row merges it); every chunk size.</summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(5)] [InlineData(2000)]
    public void The_result_does_not_depend_on_the_order_in_which_identity_keys_are_bound(int chunk)
    {
        var a = Capture(Lines(instancesFirst: true)); var b = Capture(Lines(instancesFirst: false));
        try
        {
            var opts = new EngineOptions { };
            var first = ReplayTests.Replay(a, chunk, o: opts);
            var second = ReplayTests.Replay(b, chunk, o: opts);
            var i0 = first.Engine.Identities; var i1 = second.Engine.Identities;
            static string Order(IdentityCatalog c) => string.Join(" ", c.Bindings().Select(x => x.Domain.Id + ":" + string.Join(",", x.Components)));       // the keys in the order they were first seen
            Assert.True(Order(i0) != Order(i1), "the keys were first seen in the same order: the test would say nothing");
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

    /// <summary>A host on the example definitions: the late rows are read where it serves them.</summary>
    static SpaceHost NewHost() => HostTests.NewHost();
    static long LateRows(SpaceHost h) => long.Parse(h.Liveness()["counters"]!["lateRows"]!.GetValue<string>());

    static void Resources(IEngineInput e)
    {
        var rw = new RowWriter(e.RowLayout("resources"), e.Interner);
        rw.Begin(); rw.Text("id", Resource); rw.Text("name", "acct"); rw.Text("type", "microsoft.storage/storageaccounts"); rw.Text("location", null); rw.Text("resourceGroup", null); rw.Text("subscriptionId", null); rw.Text("parentId", null);
        e.Ingest("resources", rw.Build()!, 1);
    }

    static void Activity(IEngineInput e, long seq, string id, long offsetNs, int count)
    {
        var rw = new RowWriter(e.RowLayout("activity"), e.Interner);
        for (var i = 0; i < count; i++) { rw.Begin(); rw.Instant("time", T0 + offsetNs + i * 1_000_000_000L); rw.Text("resourceId", id); rw.Text("operation", "op"); rw.Text("status", "Succeeded"); }
        e.Ingest("activity", rw.Build()!, seq);
    }

    static Dictionary<long, decimal> Changes(SpaceHost h) => Changes(h.Engine);
    static Dictionary<long, decimal> Changes(Engine e)
    {
        var changes = new Dictionary<long, decimal>();
        e.Windows.ScanGraph(e.Master.Key, (_, p, ws, v) => { if (e.Interner.Get(p) == "d:sattle.cloud/prop:changes") changes.Add(ws, v); });
        return changes;
    }

    /// <summary>B1: the Activity Log writes one resource under two spellings of its id. The group is keyed by the lowercased id, so every batching gives one aggregate for the element and window; keyed by the raw text it gave two and the window store kept the last one written.</summary>
    [Theory]
    [InlineData("one batch", false, false)]
    [InlineData("two batches, the 3-row spelling first", true, false)]
    [InlineData("two batches, the 2-row spelling first", true, true)]
    public void Two_spellings_of_one_resource_id_count_into_one_window_whatever_the_batching(string name, bool split, bool reverse)
    {
        _ = name;
        using var e = NewEngine();
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

    /// <summary>B2: a row for a window whose state is gone (beyond its input's lateness of 3 minutes) used to create a new window that replaced the value emitted earlier. It is dropped, counted, and changes nothing.</summary>
    [Theory]
    [InlineData(1, 10)] [InlineData(4, 10)] [InlineData(1, 30)] [InlineData(2, 5)]
    public void A_row_for_a_window_beyond_its_lateness_is_dropped_and_counted_and_never_replaces_what_was_emitted(int lateRows, int laterMinutes)
    {
        using var e = NewHost();
        Resources(e);
        Activity(e, 2, Resource, 0, 3);
        e.Advance(T0 + 2 * Min, 10);                                               // the window closes with 3
        Assert.Equal(3m, Changes(e).Single().Value);
        e.Advance(T0 + laterMinutes * Min, 11);                                    // beyond the lateness: the window's state is dropped
        Activity(e, 3, Resource, 5_000_000_000L, lateRows);                     // late rows for it
        e.Advance(T0 + (laterMinutes + 2) * Min, 12);
        Assert.Equal(3m, Changes(e).Single().Value);
        Assert.True(LateRows(e) >= lateRows);
    }

    const string Wide = """
        {"module": "d", "version": "1.0.0", "imports": [],
         "entities": [{"name": "Day", "properties": [{"name": "k", "type": "Text"}, {"name": "hits", "type": "Integer", "optional": true}]}],
         "identities": [{"name": "day", "type": "Day", "components": [{"name": "k", "type": "Text"}]}],
         "rows": [{"name": "R", "fields": [{"name": "k", "type": "Text"}, {"name": "t", "type": "Instant"}]}]}
        """;
    const string WideIngest = """
        {"graph": "ingest", "ontology": "d", "inputs": [{"name": "a", "rows": "d.R", "time": "t", "lateness": "LATENESS"}], "parameters": [],
         "verbs": [
           {"id": "a", "verb": "rows", "source": "a"},
           {"id": "windows", "verb": "window", "window": {"time": "t", "period": "WIDTH"}},
           {"id": "counted", "verb": "summarize", "by": {"k": {"ref": "k"}, "windowStart": {"ref": "windowStart"}}, "aggregates": {"hits": {"fn": "count"}}},
           {"id": "days", "verb": "nodes", "rule": "observed", "target": {"nodes": [
             {"var": "day", "label": "Day", "id": {"identity": "day", "args": [{"ref": "k"}]}, "props": {"k": {"ref": "k"}, "hits": {"ref": "hits"}}}]}}],
         "wires": [{"from": "a", "to": "windows"}, {"from": "windows", "to": "counted"}, {"from": "counted", "to": "days"}],
         "rules": [{"name": "observed", "description": "test"}]}
        """;
    const string WideSpace = """
        {"module": "ds", "version": "1.0.0", "imports": [{"module": "d", "alias": "d", "version": "1.0.0", "digest": null}],
         "space": {"name": "s", "sources": [{"name": "a", "adapter": "record", "rows": "d.R"}],
                   "graphs": [{"name": "master", "projection": "d.ingest", "master": true, "inputs": {"a": "a"}}]}}
        """;

    /// <summary>Code review 2, defect 17: a window of any width takes its row and closes, whatever lateness its input declares up to the retention (48 h).
    /// Before, the width times a late horizon of 1,000,000 windows passed the greatest time from 2 h 33 min on, so a window was never revisable and every row was late (#169: the lateness is the program's, `end + λ` is computed without overflow).</summary>
    [Theory]
    [InlineData("1h", "0s")] [InlineData("3h", "1h")] [InlineData("168h", "1m")]
    [InlineData("1h", "48h")] [InlineData("2h", "48h")] [InlineData("24h", "48h")] [InlineData("168h", "48h")]
    public void A_window_of_any_width_takes_its_rows_and_closes_whatever_the_lateness(string width, string lateness)
    {
        var cat = new Sattle.Language.Catalog(Repo.Projections);
        List<Sattle.Language.SourceFile> files = [new("d.module.gkql.json", Wide), new("d.ingest.gkql.json", WideIngest.Replace("WIDTH", width).Replace("LATENESS", lateness)), new("ds.module.gkql.json", WideSpace)];
        Assert.Empty(cat.Load(files, updatePins: true).Diagnostics.Where(x => x.IsError).Select(x => x.ToString()));
        using var h = new SpaceHost(files, new HostOptions { Projections = Repo.Projections });
        var w = Norm.DurationNs(width); var start = 1_794_300_000_000_000_000 / w * w;
        h.Advance(start + 1, 1);                                 // a replay advances before its first row
        var rw = new RowWriter(h.RowLayout("a"), h.Interner);
        rw.Begin(); rw.Text("k", "a"); rw.Instant("t", start + 2);
        h.Ingest("a", rw.Build()!, 2);
        h.Advance(start + w + 1, 3);                             // the window [start, start + w) closes
        Assert.Equal(0, LateRows(h));
        Assert.Single(h.Engine.Master.State.Entities);
    }

    /// <summary>#169 D1: a lateness beyond the window retention (48 h) is refused at activation: the window would be revisable after it is let go (Def. 7.13).</summary>
    [Theory]
    [InlineData("1h", "49h")] [InlineData("24h", "72h")]
    public void A_lateness_beyond_the_window_retention_is_refused_at_activation(string width, string lateness)
    {
        List<Sattle.Language.SourceFile> files = [new("d.module.gkql.json", Wide), new("d.ingest.gkql.json", WideIngest.Replace("WIDTH", width).Replace("LATENESS", lateness)), new("ds.module.gkql.json", WideSpace)];
        var e = Assert.Throws<InvalidOperationException>(() => new SpaceHost(files, new HostOptions { Projections = Repo.Projections }).Dispose());
        Assert.EndsWith($"keeps a window revisable for {lateness} after its end, and windows are retained for 48h (Def. 7.13)", e.Message);
    }

    /// <summary>The other side of B2: a late row inside its input's lateness revises the window (the lateness is the only thing that decides).</summary>
    [Theory]
    [InlineData(1)] [InlineData(2)]
    public void A_row_for_a_window_inside_its_lateness_revises_it(int lateRows)
    {
        using var e = NewHost();
        Resources(e);
        Activity(e, 2, Resource, 0, 3);
        e.Advance(T0 + 2 * Min, 10);
        Activity(e, 3, Resource, 5_000_000_000L, lateRows);
        e.Advance(T0 + 4 * Min, 11);
        Assert.Equal(3m + lateRows, Changes(e).Single().Value);
        Assert.Equal(0, LateRows(e));
    }
}
