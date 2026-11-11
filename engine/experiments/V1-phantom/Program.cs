// V1: does the synthetic estate of `bench estate` contain phantom relationships (review 06, first point)?
// Builds the 100,000-node estate of the baseline twice: as SyntheticWorld.LoadStructure does, and with the optional pod columns
// (nodeName, restarts, ready, deployment) marked absent. Counts master relationships by type and by target, then measures live bytes and window closes.
// usage: dotnet run -c Release -- DEFINITIONS_DIR fix|asis
using System.Diagnostics;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

var defs = args[0]; var fix = args[1] == "fix";
var cat = new Catalog(); var res = cat.Load(Catalog.ReadDirectory(defs), updatePins: true);
if (res.HasErrors) throw new InvalidOperationException(string.Join("\n", res.Diagnostics));
using var e = new Engine(cat, cat.Spaces.Keys.First(), new EngineOptions { StoreFactory = _ => new InMemoryGraphStore() });
int pods = 31250, services = 6250, resources = 62500, pairs = 50000, perWindow = 208000, windows = 3; const long Min = 60_000_000_000;
var it = e.Interner; string cluster = "synth";
string Ns(int s) => $"ns{s % 5}";
{
    var w = new RowWriter(e.RowLayout("pods"), it);
    for (var i = 0; i < pods; i++)
    {
        w.Begin(); w.Text("cluster", cluster); w.Text("uid", $"uid-{i}"); w.Text("namespace", Ns(i % services)); w.Text("name", $"pod-{i}"); w.Text("phase", "Running"); w.Text("teamLabel", null);
        if (fix) { w.Absent(w.Col("nodeName")); w.Absent(w.Col("restarts")); w.Absent(w.Col("ready")); w.Absent(w.Col("deployment")); }
    }
    e.Ingest("pods", w.Build()!, 1);
    var sw = new RowWriter(e.RowLayout("services"), it);
    for (var s = 0; s < services; s++) { sw.Begin(); sw.Text("cluster", cluster); sw.Text("namespace", Ns(s)); sw.Text("name", $"svc-{s}"); }
    e.Ingest("services", sw.Build()!, 2);
    var ew = new RowWriter(e.RowLayout("endpoints"), it);
    for (var i = 0; i < pods; i++) { ew.Begin(); ew.Text("cluster", cluster); ew.Text("namespace", Ns(i % services)); ew.Text("service", $"svc-{i % services}"); ew.Text("address", $"10.0.{i / 250}.{i % 250}"); ew.Text("podUid", $"uid-{i}"); }
    e.Ingest("endpoints", ew.Build()!, 3);
}
string Rid(int i) => $"/subscriptions/s{i % 100}/resourceGroups/rg{i % 900}/providers/Microsoft.Compute/virtualMachines/vm{i}";
{
    var rw = new RowWriter(e.RowLayout("resources"), it);
    for (var i = 0; i < resources; i++)
    {
        rw.Begin(); rw.Text("id", Rid(i)); rw.Text("name", $"vm{i}"); rw.Text("type", "microsoft.compute/virtualmachines"); rw.Text("location", "swedencentral"); rw.Text("resourceGroup", $"rg{i % 900}"); rw.Text("subscriptionId", $"s{i % 100}");
        if ((i + 1) % 50_000 == 0 || i == resources - 1) e.Ingest("resources", rw.Build()!, 100 + i);
    }
    var rr = new RowWriter(e.RowLayout("signalRules"), it); rr.Begin();
    rr.Text("ruleId", "bench-cpu"); rr.Text("type", "microsoft.compute/virtualmachines"); rr.Text("meter", "Percentage CPU"); rr.Dec(rr.Col("above"), 80m); rr.Int(rr.Col("enterAfter"), 2); rr.Dec(rr.Col("below"), 60m); rr.Int(rr.Col("exitAfter"), 2); rr.Int(rr.Col("severity"), 2); rr.Int(rr.Col("invert"), 0);
    e.Ingest("signalRules", rr.Build()!, 90);
}
// ---- what the master holds after the structure
var m = e.Master.State;
var byType = m.Rels.GroupBy(r => Ids.Name(r.Type)).Select(g => (Type: g.Key, N: g.Count())).OrderByDescending(x => x.N).ToList();
Console.WriteLine($"variant {(fix ? "pod columns nodeName, restarts, ready, deployment marked absent" : "as bench estate builds it")}: master {m.EntityCount:N0} entities, {m.RelCount:N0} relationships after the structure and the resources");
Console.WriteLine("relationships by type: " + string.Join(", ", byType.Select(x => $"{x.Type} {x.N:N0}")));
foreach (var t in new[] { "RunsOn", "ControlledBy" })
{
    var rels = m.Rels.Where(r => Ids.Name(r.Type) == t).ToList();
    var targets = rels.Select(r => r.Target).Distinct().ToList();
    var present = targets.Count(id => m.Entity(id) is not null);
    var tgtTypes = targets.Select(id => m.Entity(id) is { } en ? Ids.Name(en.Type) : "absent (no entity in the graph)").GroupBy(x => x).Select(g => $"{g.Key} {g.Count():N0}");
    Console.WriteLine($"{t}: {rels.Count:N0} relationships to {targets.Count:N0} distinct targets, {present:N0} of them entities that exist; target kinds: {string.Join(", ", tgtTypes)}");
}
var readyPods = m.Entities.Where(en => Ids.Name(en.Type) == "Pod").Count(en => en.Props.TryGet("d:sattle.infra/prop:ready", out var v) && v is BooleanValue { B: true });
var podsWithReadyProp = m.Entities.Where(en => Ids.Name(en.Type) == "Pod").Count(en => en.Props.TryGet("d:sattle.infra/prop:ready", out _));
Console.WriteLine($"pods with a ready property: {podsWithReadyProp:N0}, ready true: {readyPods:N0}");
GC.Collect(); Console.WriteLine($"live bytes after structure: {LiveBytes() / 1e6:F0} MB");

// ---- flows and readings, 3 windows
var layout = e.RowLayout("flows"); var rnd = new Random(11);
var pa = new int[pairs]; var pb = new int[pairs];
for (var i = 0; i < pairs; i++) { pa[i] = rnd.Next(pods); pb[i] = (pa[i] * 7 + 1 + rnd.Next(5)) % pods; }
var podCode = Enumerable.Range(0, pods).Select(i => (long)it.Intern($"pod-{i}")).ToArray(); var nsCode = Enumerable.Range(0, 5).Select(i => (long)it.Intern($"ns{i}")).ToArray();
long cl = it.Intern("synth"), fwd = it.Intern("FORWARDED"), drp = it.Intern("DROPPED");
int cTime = layout.Index("time"), cCl = layout.Index("cluster"), cVe = layout.Index("verdict"), cSn = layout.Index("srcNamespace"), cSp = layout.Index("srcPod"), cDn = layout.Index("dstNamespace"), cDp = layout.Index("dstPod");
var t0 = 1_794_300_000_000_000_000L / Min * Min; long seq = 1000; var closes = new List<double>();
for (var w = 0; w < windows; w++)
{
    var left = perWindow;
    while (left > 0)
    {
        var n = Math.Min(50_000, left); var b = new Batch(layout, n) { N = n };
        for (var i = 0; i < n; i++)
        {
            var k = rnd.Next(pairs); var a = pa[k]; var d = pb[k];
            b.Cols[cTime].L[i] = t0 + w * Min + rnd.NextInt64(Min - 2_000_000_000); b.Cols[cCl].L[i] = cl; b.Cols[cVe].L[i] = rnd.NextDouble() < 0.02 ? drp : fwd;
            b.Cols[cSn].L[i] = nsCode[(a % services) % 5]; b.Cols[cSp].L[i] = podCode[a]; b.Cols[cDn].L[i] = nsCode[(d % services) % 5]; b.Cols[cDp].L[i] = podCode[d];
        }
        e.Ingest("flows", b, ++seq); left -= n;
    }
    var mw = new RowWriter(e.RowLayout("resourceMetrics"), it);
    for (var i = 0; i < resources; i++)
    {
        mw.Begin(); mw.Instant("time", t0 + w * Min + 1_000_000_000L + i % 50_000); mw.Text("resourceId", Rid(i)); mw.Text("metric", "Percentage CPU"); mw.Dec(mw.Col("value"), i < resources / 100 && w >= 1 ? 95m : 20m + i % 30);
        if ((i + 1) % 50_000 == 0 || i == resources - 1) e.Ingest("resourceMetrics", mw.Build()!, ++seq);
    }
    var sw2 = Stopwatch.StartNew(); e.Advance(t0 + (w + 1) * Min + 1); closes.Add(sw2.Elapsed.TotalMilliseconds);
}
Console.WriteLine($"window closes (ms): {string.Join(", ", closes.Select(c => c.ToString("F0")))}; master relationships after the windows {e.Master.State.RelCount:N0}");
Console.WriteLine($"live bytes at the end: {LiveBytes() / 1e6:F0} MB; derived graphs: {string.Join(", ", e.Graphs.Where(g => g.State.EntityCount + g.State.RelCount > 0).Select(g => $"{g.Name} {g.State.EntityCount:N0}e/{g.State.RelCount:N0}r"))}");

static long LiveBytes()
{
    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
    GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); GC.Collect(2, GCCollectionMode.Forced, true, true);
    return GC.GetGCMemoryInfo().PromotedBytes;
}
