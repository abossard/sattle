// X11: flow pre-aggregation. Reads the Hubble flows of a capture and aggregates them as the engine rule observedCalls does
// (configuration 1), and with the reply filter and workload keys of the concept (configuration 2).
// usage: dotnet run -c Release -- CAPTURE_DIR ENGINE_OUT_DIR   (ENGINE_OUT_DIR = sattle replay CAPTURE --out ...)
using System.Diagnostics;
using System.Text.Json;

var capture = args[0]; var engineOut = args[1];
var file = Path.Combine(capture, "hubble", "flows.ndjson");

// ---- parse once into a struct array with interned strings
var strings = new Dictionary<string, int>(); var names = new List<string>();
int Id(string? s) { if (string.IsNullOrEmpty(s)) return -1; if (!strings.TryGetValue(s, out var i)) { i = names.Count; strings[s] = i; names.Add(s); } return i; }
var flows = new List<Flow>(); long records = 0, lost = 0, noflow = 0;
var parse = Stopwatch.StartNew();
foreach (var line in File.ReadLines(file))
{
    records++;
    using var doc = JsonDocument.Parse(line);
    var payload = doc.RootElement.GetProperty("payload");
    if (payload.TryGetProperty("lost_events", out _)) { lost++; continue; }
    if (!payload.TryGetProperty("flow", out var f)) { noflow++; continue; }
    var t = f.TryGetProperty("time", out var te) ? te.GetString() : payload.TryGetProperty("time", out var te2) ? te2.GetString() : null;
    if (t is null || !f.TryGetProperty("verdict", out var ve)) continue;
    var sec = DateTimeOffset.Parse(t, null, System.Globalization.DateTimeStyles.AssumeUniversal).ToUnixTimeSeconds();
    f.TryGetProperty("source", out var src); f.TryGetProperty("destination", out var dst);
    string? S(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    string? W(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("workloads", out var w) && w.ValueKind == JsonValueKind.Array && w.GetArrayLength() > 0 ? S(w[0], "name") : null;
    var reply = f.TryGetProperty("is_reply", out var rp) && rp.ValueKind == JsonValueKind.True;
    var port = 0;
    if (f.TryGetProperty("l4", out var l4) && l4.ValueKind == JsonValueKind.Object)
        foreach (var proto in l4.EnumerateObject()) if (proto.Value.ValueKind == JsonValueKind.Object && proto.Value.TryGetProperty("destination_port", out var dp)) { port = dp.GetInt32(); break; }
    flows.Add(new Flow { Sec = sec, Dropped = ve.GetString() == "DROPPED", SrcNs = Id(S(src, "namespace")), SrcPod = Id(S(src, "pod_name")), DstNs = Id(S(dst, "namespace")), DstPod = Id(S(dst, "pod_name")), SrcWl = Id(W(src)), DstWl = Id(W(dst)), Port = port, Reply = reply });
}
var parseS = parse.Elapsed.TotalSeconds;
var arr = flows.ToArray();
Console.WriteLine($"records {records:N0}, flows {arr.Length:N0}, lost-event records {lost}, neither {noflow}; parse {parseS:F1} s ({parseS * 1e9 / records:F0} ns per record)");

// ---- configuration 1: the engine rule observedCalls
const long W60 = 60;
Dictionary<(int, int, int, int, long), (long Flows, long Drops)> Config1()
{
    var d = new Dictionary<(int, int, int, int, long), (long, long)>();
    foreach (ref readonly var f in arr.AsSpan())
    {
        if (f.SrcNs < 0 || f.SrcPod < 0 || f.DstNs < 0 || f.DstPod < 0) continue;
        var key = (f.SrcNs, f.SrcPod, f.DstNs, f.DstPod, f.Sec / W60 * W60);
        ref var acc = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(d, key, out _);
        acc.Item1++; if (f.Dropped) acc.Item2++;
    }
    return d;
}
// ---- configuration 2: requests only, workload pair and destination port
Dictionary<(int, int, int, long), long> Config2()
{
    var d = new Dictionary<(int, int, int, long), long>();
    foreach (ref readonly var f in arr.AsSpan())
    {
        if (f.SrcNs < 0 || f.SrcPod < 0 || f.DstNs < 0 || f.DstPod < 0 || f.Reply) continue;
        var key = (f.SrcWl >= 0 ? f.SrcWl : f.SrcPod + 1_000_000, f.DstWl >= 0 ? f.DstWl : f.DstPod + 1_000_000, f.Port, f.Sec / W60 * W60);
        ref var acc = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(d, key, out _);
        acc++;
    }
    return d;
}
var c1 = Config1(); var c2 = Config2();
long known = arr.Count(f => f.SrcNs >= 0 && f.SrcPod >= 0 && f.DstNs >= 0 && f.DstPod >= 0);
long requests = arr.Count(f => f.SrcNs >= 0 && f.SrcPod >= 0 && f.DstNs >= 0 && f.DstPod >= 0 && !f.Reply);
long replies = known - requests;
var wins1 = c1.Keys.GroupBy(k => k.Item5).Select(g => g.Count()).Order().ToArray(); var wins2 = c2.Keys.GroupBy(k => k.Item4).Select(g => g.Count()).Order().ToArray();
Console.WriteLine($"flows with all four names: {known:N0}; requests {requests:N0}, replies {replies:N0}");
Console.WriteLine($"configuration 1 (engine rule): {c1.Count:N0} pair-windows, windows {wins1.Length}, pairs per window median {wins1[wins1.Length / 2]} max {wins1[^1]}, reduction {known / (double)c1.Count:F1}x");
Console.WriteLine($"configuration 2 (requests, workload pair, destination port): {c2.Count:N0} keys, per window median {wins2[wins2.Length / 2]} max {wins2[^1]}, reduction {known / (double)c2.Count:F1}x (against all flows in: {arr.Length / (double)c2.Count:F1}x)");
var wlPairs = c2.Keys.Select(k => (k.Item1, k.Item2)).Distinct().Count();
Console.WriteLine($"distinct workload pairs in configuration 2: {wlPairs}; distinct (workload pair, port): {c2.Keys.Select(k => (k.Item1, k.Item2, k.Item3)).Distinct().Count()}");
// reversed edges: a reply counted by the engine as a Calls edge from the callee to the caller
var reqPairs = new HashSet<(int, int, int, int)>(); var replyOnly = new HashSet<(int, int, int, int)>();
foreach (var f in arr) if (f.SrcNs >= 0 && f.SrcPod >= 0 && f.DstNs >= 0 && f.DstPod >= 0) { if (!f.Reply) reqPairs.Add((f.SrcNs, f.SrcPod, f.DstNs, f.DstPod)); }
foreach (var f in arr) if (f.Reply && f.SrcNs >= 0 && f.SrcPod >= 0 && f.DstNs >= 0 && f.DstPod >= 0 && !reqPairs.Contains((f.SrcNs, f.SrcPod, f.DstNs, f.DstPod))) replyOnly.Add((f.SrcNs, f.SrcPod, f.DstNs, f.DstPod));
var pods1 = c1.Keys.Select(k => (k.Item1, k.Item2, k.Item3, k.Item4)).Distinct().Count();
Console.WriteLine($"configuration 1 pod pairs {pods1}; pod pairs that exist only as replies (reversed edges): {replyOnly.Count}");

// ---- speed without parsing
double Median(List<double> xs) { xs.Sort(); return xs[xs.Count / 2]; }
for (var i = 0; i < 2; i++) { Config1(); Config2(); }
var t1 = new List<double>(); var t2 = new List<double>();
for (var run = 0; run < 5; run++) { var sw = Stopwatch.StartNew(); Config1(); t1.Add(sw.Elapsed.TotalMilliseconds * 1e6 / arr.Length); sw.Restart(); Config2(); t2.Add(sw.Elapsed.TotalMilliseconds * 1e6 / arr.Length); }
Console.WriteLine($"aggregation without parsing, ns per flow (median of 5, in-memory struct array, dictionary of tuples): configuration 1 {Median(t1):F1}, configuration 2 {Median(t2):F1}");

// ---- compare configuration 1 with the engine's Calls telemetry
// engine: relationship id -> (srcNs, srcPod, dstNs, dstPod) from the final snapshot and the references; telemetry per relationship id and window offset
var snap = JsonDocument.Parse(File.ReadAllText(Path.Combine(engineOut, "master.final.snapshot.json"))).RootElement.GetProperty("extent");
var podOf = new Dictionary<string, (string Ns, string Name)>();
foreach (var e in snap.GetProperty("entities").EnumerateArray())
{
    if (e.GetProperty("type").GetProperty("value").GetString() != "Pod") continue;
    string? ns = null, name = null;
    foreach (var p in e.GetProperty("properties").EnumerateArray()) { var pn = p.GetProperty("property").GetProperty("value").GetString(); if (pn == "sattle.infra.namespace") ns = p.GetProperty("value").GetProperty("value").GetString(); if (pn == "sattle.infra.name") name = p.GetProperty("value").GetProperty("value").GetString(); }
    if (ns is not null && name is not null) podOf[e.GetProperty("id").GetProperty("value").GetString()!] = (ns, name);
}
long unresolvedRefs = 0;
foreach (var line in File.ReadLines(Path.Combine(engineOut, "master.references.ndjson")))
{
    using var d = JsonDocument.Parse(line);
    var r = d.RootElement; if (r.GetProperty("entityType").GetString() != "Pod") continue;
    var key = r.GetProperty("identity").GetProperty("key");
    if (key.TryGetProperty("namespace", out var kn) && key.TryGetProperty("name", out var kp)) podOf[r.GetProperty("id").GetProperty("value").GetString()!] = (kn.GetString()!, kp.GetString()!); else unresolvedRefs++;
}
var relPair = new Dictionary<string, (string, string, string, string)>(); long callsRels = 0, callsUnresolved = 0;
foreach (var r in snap.GetProperty("relationships").EnumerateArray())
{
    if (r.GetProperty("type").GetProperty("value").GetString() != "Calls") continue; callsRels++;
    var s = r.GetProperty("source").GetProperty("value").GetString()!; var t = r.GetProperty("target").GetProperty("value").GetString()!;
    if (podOf.TryGetValue(s, out var a) && podOf.TryGetValue(t, out var b)) relPair[r.GetProperty("id").GetProperty("value").GetString()!] = (a.Ns, a.Name, b.Ns, b.Name); else callsUnresolved++;
}
var eng = new Dictionary<(string, string, string, string, long), (double Flows, double Drops)>(); var engWindows = new SortedSet<long>();
var engAllFlows = new SortedDictionary<long, double>(); var engAllDrops = new SortedDictionary<long, double>();
foreach (var line in File.ReadLines(Path.Combine(engineOut, "master.telemetry.ndjson")))
{
    using var d = JsonDocument.Parse(line); var r = d.RootElement;
    var prop = r.GetProperty("property").GetString(); if (prop is not ("sattle.infra.flows" or "sattle.infra.drops")) continue;
    var t = r.GetProperty("t").GetInt64(); engWindows.Add(t);
    var ids = r.GetProperty("ids").EnumerateArray().ToArray(); var vals = r.GetProperty("values").EnumerateArray().ToArray();
    for (var i = 0; i < ids.Length; i++)
    {
        var all = prop == "sattle.infra.flows" ? engAllFlows : engAllDrops; all[t] = all.GetValueOrDefault(t) + vals[i].GetDouble();
        if (!relPair.TryGetValue(ids[i].GetString()!, out var p)) continue;
        var k = (p.Item1, p.Item2, p.Item3, p.Item4, t); var cur = eng.GetValueOrDefault(k);
        eng[k] = prop == "sattle.infra.flows" ? (vals[i].GetDouble(), cur.Drops) : (cur.Flows, vals[i].GetDouble());
    }
}
// align the window offsets: the engine's t is seconds from an origin; the first window with data is the same window
var myWindows = c1.Keys.Select(k => k.Item5).Distinct().Order().ToArray(); var engMin = engWindows.Min; var myMin = myWindows[0];
long mismatches = 0, matched = 0, missingInEngine = 0, missingInMine = 0; var examples = new List<string>();
var mine = new Dictionary<(string, string, string, string, long), (long, long)>();
foreach (var (k, v) in c1) mine[(names[k.Item1], names[k.Item2], names[k.Item3], names[k.Item4], k.Item5 - myMin + engMin)] = v;
foreach (var (k, v) in mine)
{
    if (!eng.TryGetValue(k, out var e)) { if (relPair.Values.Contains((k.Item1, k.Item2, k.Item3, k.Item4))) { missingInEngine++; if (examples.Count < 5) examples.Add($"in capture only: {k} {v}"); } continue; }
    if (e.Flows == v.Item1 && e.Drops == v.Item2) matched++; else { mismatches++; if (examples.Count < 5) examples.Add($"differs: {k} mine {v} engine {e}"); }
}
foreach (var (k, v) in eng) if (!mine.ContainsKey(k)) { missingInMine++; if (examples.Count < 5) examples.Add($"in engine only: {k} {v}"); }
Console.WriteLine($"engine Calls relationships {callsRels:N0}, resolved to pod names {relPair.Count:N0}, unresolved {callsUnresolved:N0}; engine pair-windows {eng.Count:N0}");
Console.WriteLine($"configuration 1 against the engine (windows aligned by the first window, offset {engMin} - {myMin}): equal {matched:N0}, different {mismatches:N0}, only in capture (pair known to the engine) {missingInEngine:N0}, only in engine {missingInMine:N0}");
foreach (var x in examples) Console.WriteLine("  " + x);
var byWinFlows = c1.GroupBy(k => k.Key.Item5 - myMin + engMin).ToDictionary(g => g.Key, g => g.Sum(x => (double)x.Value.Item1)); var byWinDrops = c1.GroupBy(k => k.Key.Item5 - myMin + engMin).ToDictionary(g => g.Key, g => g.Sum(x => (double)x.Value.Item2));
var windowsEqual = engAllFlows.Count(kv => byWinFlows.GetValueOrDefault(kv.Key) == kv.Value && byWinDrops.GetValueOrDefault(kv.Key) == engAllDrops.GetValueOrDefault(kv.Key)); var windowsTotal = engAllFlows.Count;
Console.WriteLine($"per-window totals over all {callsRels} engine Calls relationships (resolved or not) against configuration 1: equal in {windowsEqual} of {windowsTotal} windows; engine total flows {engAllFlows.Values.Sum():N0}, drops {engAllDrops.Values.Sum():N0}; configuration 1 flows {byWinFlows.Values.Sum():N0}, drops {byWinDrops.Values.Sum():N0}");
var sumMine = c1.Values.Sum(v => v.Item1); var sumEng = eng.Values.Sum(v => v.Flows);
Console.WriteLine($"total flows: configuration 1 {sumMine:N0}, engine telemetry (resolved pairs) {sumEng:N0}; drops {c1.Values.Sum(v => v.Item2):N0} vs {eng.Values.Sum(v => v.Drops):N0}");

struct Flow { public long Sec; public bool Dropped, Reply; public int SrcNs, SrcPod, DstNs, DstPod, SrcWl, DstWl, Port; }
