// X4: fixed-point and compressed telemetry. Replays the newest capture and an estate workload in-process, reads every closed telemetry
// window as decimal, and reports (1) per property the smallest decimal scale at which every value is an exact i64, (2) bytes per window for
// zigzag-varint deltas and for Gorilla XOR coding of the fixed-point values, (3) ns per appended value.
// usage: dotnet run -c Release -- CAPTURE_DIR DEFINITIONS_DIR OVERLAY_FILE
using System.Diagnostics;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

var capture = args[0]; var defs = args[1]; var overlay = args.Length > 2 ? args[2] : null;
var results = new List<(string Source, string Graph, string Prop, long Element, long Ws, decimal Value)>();

// ---- 1. the capture
{
    var files = Catalog.ReadDirectory(defs).ToList();
    if (overlay is not null && File.Exists(overlay))
    {
        var text = File.ReadAllText(overlay);
        var mod = System.Text.RegularExpressions.Regex.Match(text, @"^module\s+(\S+?)\s*(?:@|;)", System.Text.RegularExpressions.RegexOptions.Multiline).Groups[1].Value;
        var i = files.FindIndex(f => System.Text.RegularExpressions.Regex.Match(f.Text, @"^module\s+(\S+?)\s*(?:@|;)", System.Text.RegularExpressions.RegexOptions.Multiline) is { Success: true } m && m.Groups[1].Value == mod);
        files[i] = files[i] with { Text = text };
    }
    var cat = new Catalog(); var load = cat.Load(files, updatePins: true);
    if (load.HasErrors) throw new InvalidOperationException(string.Join("\n", load.Diagnostics));
    var spaceId = cat.Spaces.Keys.First();
    var cluster = Sattle.Cli.Program.DetectCluster(capture);
    var adapters = AdapterRegistry.Create(cluster);
    var engine = new Engine(cat, spaceId, new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 });
    var replay = new Replay(engine, cat.Spaces[spaceId], adapters, new ReplayOptions { ChunkRows = 2000, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000, FlushAtEnd = true, Speed = 0, Cluster = cluster });
    replay.Run(capture);
    Collect("capture", engine);
    engine.Dispose();
}
// ---- 2. an estate workload (100,000 master nodes, flows and meter readings, 3 windows)
{
    var cat = new Catalog(); var res = cat.Load(Catalog.ReadDirectory(defs), updatePins: true);
    if (res.HasErrors) throw new InvalidOperationException(string.Join("\n", res.Diagnostics));
    using var e = new Engine(cat, cat.Spaces.Keys.First(), new EngineOptions { StoreFactory = _ => new InMemoryGraphStore() });
    int pods = 31250, services = 6250, resources = 62500, pairs = 50000, perWindow = 208000, windows = 3; const long Min = 60_000_000_000;
    new SyntheticWorld(e, pods, services).LoadStructure();
    var it = e.Interner; var rw = new RowWriter(e.RowLayout("resources"), it);
    string Rid(int i) => $"/subscriptions/s{i % 100}/resourceGroups/rg{i % 900}/providers/Microsoft.Compute/virtualMachines/vm{i}";
    for (var i = 0; i < resources; i++)
    {
        rw.Begin(); rw.Text("id", Rid(i)); rw.Text("name", $"vm{i}"); rw.Text("type", "microsoft.compute/virtualmachines"); rw.Text("location", "swedencentral"); rw.Text("resourceGroup", $"rg{i % 900}"); rw.Text("subscriptionId", $"s{i % 100}");
        if ((i + 1) % 50_000 == 0 || i == resources - 1) e.Ingest("resources", rw.Build()!, 100 + i);
    }
    var rr = new RowWriter(e.RowLayout("signalRules"), it); rr.Begin();
    rr.Text("ruleId", "bench-cpu"); rr.Text("type", "microsoft.compute/virtualmachines"); rr.Text("meter", "Percentage CPU"); rr.Dec(rr.Col("above"), 80m); rr.Int(rr.Col("enterAfter"), 2); rr.Dec(rr.Col("below"), 60m); rr.Int(rr.Col("exitAfter"), 2); rr.Int(rr.Col("severity"), 2); rr.Int(rr.Col("invert"), 0);
    e.Ingest("signalRules", rr.Build()!, 90);
    var layout = e.RowLayout("flows"); var rnd = new Random(11);
    var pa = new int[pairs]; var pb = new int[pairs];
    for (var i = 0; i < pairs; i++) { pa[i] = rnd.Next(pods); pb[i] = (pa[i] * 7 + 1 + rnd.Next(5)) % pods; }
    var podCode = Enumerable.Range(0, pods).Select(i => (long)it.Intern($"pod-{i}")).ToArray(); var nsCode = Enumerable.Range(0, 5).Select(i => (long)it.Intern($"ns{i}")).ToArray();
    long cl = it.Intern("synth"), fwd = it.Intern("FORWARDED"), drp = it.Intern("DROPPED");
    int cTime = layout.Index("time"), cCl = layout.Index("cluster"), cVe = layout.Index("verdict"), cSn = layout.Index("srcNamespace"), cSp = layout.Index("srcPod"), cDn = layout.Index("dstNamespace"), cDp = layout.Index("dstPod");
    var t0 = 1_794_300_000_000_000_000L / Min * Min; long seq = 1000;
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
        e.Advance(t0 + (w + 1) * Min + 1);
    }
    Collect("estate", e);
}

void Collect(string source, Engine e)
{
    var mem = (InMemoryTelemetryStore)e.Telemetry;
    foreach (var g in e.Graphs)
        foreach (var (el, p, ws, v) in mem.Dump(g.Key)) results.Add((source, g.Name, Short(e.Interner.Get(p)), el, ws, v));
}
static string Short(string p) { var i = Math.Max(p.LastIndexOf("/prop:", StringComparison.Ordinal) + 6, p.LastIndexOf('.') + 1); return i > 0 && i <= p.Length ? p[i..] : p; }

var derived = new HashSet<string> { "cpuPercent", "memoryPercent", "vipAvailability", "serverErrors", "forwardRate", "dropRate", "reading", "dropRatio", "callDropRatio", "flowRate", "signalValue", "value" };
Console.WriteLine($"telemetry values collected: {results.Count:N0} ({string.Join(", ", results.GroupBy(r => r.Source).Select(g => $"{g.Key} {g.Count():N0}"))})");

// ---- 3. exactness per (graph, property)
int ScaleOf(decimal v)
{
    if (v == 0) return 0;
    var bits = decimal.GetBits(v); var scale = (bits[3] >> 16) & 0xFF;
    var lo = (ulong)(uint)bits[0] | ((ulong)(uint)bits[1] << 32); var hi = (uint)bits[2];
    while (scale > 0 && hi == 0 && lo % 10 == 0) { lo /= 10; scale--; }
    return scale;
}
static bool Fits(decimal v, int s) { var x = v * Pow10(s); return x == decimal.Truncate(x) && Math.Abs(x) < 9_200_000_000_000_000_000m; }
static decimal Pow10(int s) { decimal r = 1; for (var i = 0; i < s; i++) r *= 10; return r; }

var groups = results.GroupBy(r => (r.Source, r.Graph, r.Prop)).ToList();
Console.WriteLine("exactness: source | graph | property | class | values | smallest scale s (every value x 10^s an integer in i64) | values that fit no s <= 18");
var scaleOf = new Dictionary<(string, string, string), int>();
foreach (var g in groups.OrderBy(g => g.Key))
{
    var s = 0; var bad = new List<decimal>(); long nBad = 0;
    foreach (var r in g)
    {
        var sc = ScaleOf(r.Value);
        if (sc > 18 || !Fits(r.Value, Math.Min(sc, 18))) { nBad++; if (bad.Count < 3) bad.Add(r.Value); } else s = Math.Max(s, sc);
    }
    scaleOf[g.Key] = s;
    Console.WriteLine($"{g.Key.Source} | {g.Key.Graph} | {g.Key.Prop} | {(derived.Contains(g.Key.Prop) ? "derived" : "input aggregate")} | {g.Count():N0} | {s} | {nBad}{(nBad > 0 ? " e.g. " + string.Join(", ", bad) : "")}");
}
var inputBad = groups.Where(g => !derived.Contains(g.Key.Prop)).Sum(g => g.Count(r => ScaleOf(r.Value) > 18));
Console.WriteLine($"input aggregates with a value that fits no s <= 18: {inputBad}");

// ---- 4. compression per series (window order), fixed-point at the property's scale (derived values at min(scale, 18): values with more digits are rounded for the size figure only)
static ulong Zig(long v) => (ulong)((v << 1) ^ (v >> 63));
static int VarLen(ulong v) { var n = 1; while (v >= 0x80) { v >>= 7; n++; } return n; }
long[] FixedSeries(List<(long Ws, decimal Value)> s, int scale) { var mx = s.Max(x => Math.Abs(x.Value)); while (scale > 0 && mx * Pow10(scale) >= 9_000_000_000_000_000_000m) scale--; var m = Pow10(scale); return s.Select(x => (long)Math.Round(x.Value * m, MidpointRounding.AwayFromZero)).ToArray(); }

// Gorilla bit accounting for one series of i64 (as 64-bit patterns)
static long GorillaBits(long[] v)
{
    if (v.Length == 0) return 0; long bits = 64; int pl = 99, pt = 0;
    for (var i = 1; i < v.Length; i++)
    {
        var x = (ulong)v[i] ^ (ulong)v[i - 1];
        if (x == 0) { bits += 1; continue; }
        var lz = System.Numerics.BitOperations.LeadingZeroCount(x); var tz = System.Numerics.BitOperations.TrailingZeroCount(x); lz = Math.Min(lz, 31);
        if (pl != 99 && lz >= pl && tz >= pt) bits += 2 + (64 - pl - pt); else { bits += 2 + 5 + 6 + (64 - lz - tz); pl = lz; pt = tz; }
    }
    return bits;
}
var perProp = new Dictionary<(string, string), (long Windows, long VarBytes, long GorBytes, long Series)>();
long totW = 0, totVar = 0, totGor = 0; long inW = 0, inVar = 0, inGor = 0;
var allSeries = new List<long[]>();
foreach (var g in groups)
{
    var series = g.GroupBy(r => r.Element).Select(x => x.OrderBy(r => r.Ws).Select(r => (r.Ws, r.Value)).ToList()).ToList();
    long w = 0, vb = 0, gb = 0;
    foreach (var s in series)
    {
        var fx = FixedSeries(s, Math.Min(scaleOf[g.Key], 18)); allSeries.Add(fx);
        // varint deltas: first value absolute, then zigzag deltas; windows missing between consecutive points: one gap record (varint) per gap
        long bytes = VarLen(Zig(fx[0]));
        for (var i = 1; i < fx.Length; i++) { bytes += VarLen(Zig(fx[i] - fx[i - 1])); if (s[i].Ws - s[i - 1].Ws != 60_000_000_000L) bytes += 2; }
        vb += bytes; gb += (GorillaBits(fx) + 7) / 8; for (var i = 1; i < fx.Length; i++) if (s[i].Ws - s[i - 1].Ws != 60_000_000_000L) gb += 2; w += fx.Length;
    }
    var key = (g.Key.Source, g.Key.Prop); var cur = perProp.GetValueOrDefault(key);
    perProp[key] = (cur.Windows + w, cur.VarBytes + vb, cur.GorBytes + gb, cur.Series + series.Count);
    totW += w; totVar += vb; totGor += gb; if (!derived.Contains(g.Key.Prop)) { inW += w; inVar += vb; inGor += gb; }
}
Console.WriteLine("compression: source | property | series | windows | zigzag-varint delta bytes per window | Gorilla XOR bytes per window (decimal: 16)");
foreach (var (k, v) in perProp.OrderBy(x => x.Key)) Console.WriteLine($"{k.Item1} | {k.Item2} | {v.Series:N0} | {v.Windows:N0} | {v.VarBytes / (double)v.Windows:F2} | {v.GorBytes / (double)v.Windows:F2}");
Console.WriteLine($"all values: {totW:N0} windows, varint {totVar / (double)totW:F2} B, Gorilla {totGor / (double)totW:F2} B per window (decimal 16 B, i64 8 B uncompressed)");
Console.WriteLine($"input aggregates only: {inW:N0} windows, varint {inVar / (double)Math.Max(1, inW):F2} B, Gorilla {inGor / (double)Math.Max(1, inW):F2} B per window");
foreach (var src in new[] { "capture", "estate" })
{
    var sub = perProp.Where(x => x.Key.Item1 == src).ToList(); var w = sub.Sum(x => x.Value.Windows);
    Console.WriteLine($"{src}: {w:N0} windows, varint {sub.Sum(x => x.Value.VarBytes) / (double)Math.Max(1, w):F2} B, Gorilla {sub.Sum(x => x.Value.GorBytes) / (double)Math.Max(1, w):F2} B per window");
}

// ---- 5. append speed: encode every value of every series with each coder, single thread, after warm-up
static long GorillaAppend(long[][] series)
{
    long total = 0; var buf = new byte[1 << 16];
    foreach (var v in series)
    {
        var bw = new BitWriter(buf); bw.Write((ulong)v[0], 64); int pl = 99, pt = 0;
        for (var i = 1; i < v.Length; i++)
        {
            var x = (ulong)v[i] ^ (ulong)v[i - 1];
            if (x == 0) { bw.Write(0, 1); continue; }
            var lz = Math.Min(System.Numerics.BitOperations.LeadingZeroCount(x), 31); var tz = System.Numerics.BitOperations.TrailingZeroCount(x);
            if (pl != 99 && lz >= pl && tz >= pt) { bw.Write(2, 2); bw.Write(x >> pt, 64 - pl - pt); }
            else { bw.Write(3, 2); bw.Write((ulong)lz, 5); bw.Write((ulong)(64 - lz - tz - 1), 6); bw.Write(x >> tz, 64 - lz - tz); pl = lz; pt = tz; }
        }
        total += bw.Bits;
    }
    return total;
}
static long VarintAppend(long[][] series)
{
    long total = 0; var buf = new byte[1 << 16];
    foreach (var v in series)
    {
        var n = 0; var prev = 0L;
        foreach (var x in v) { var z = (ulong)((x - prev) << 1 ^ (x - prev) >> 63); prev = x; while (z >= 0x80) { buf[n++ & 0xFFFF] = (byte)(z | 0x80); z >>= 7; } buf[n++ & 0xFFFF] = (byte)z; }
        total += n;
    }
    return total;
}
var arrays = allSeries.ToArray(); var values = arrays.Sum(a => (long)a.Length);
for (var i = 0; i < 3; i++) { GorillaAppend(arrays); VarintAppend(arrays); }
var tg = new List<double>(); var tv = new List<double>();
for (var run = 0; run < 5; run++) { var sw = Stopwatch.StartNew(); GorillaAppend(arrays); tg.Add(sw.Elapsed.TotalMilliseconds * 1e6 / values); sw.Restart(); VarintAppend(arrays); tv.Add(sw.Elapsed.TotalMilliseconds * 1e6 / values); }
tg.Sort(); tv.Sort();
Console.WriteLine($"append speed over {values:N0} values in {arrays.Length:N0} series (median of 5, single thread): Gorilla {tg[2]:F1} ns per value, zigzag varint {tv[2]:F1} ns per value");

sealed class BitWriter(byte[] buf)
{
    public long Bits; ulong acc; int cnt, pos;
    public void Write(ulong v, int n)
    {
        while (n > 0)
        {
            var k = Math.Min(n, 32); n -= k;
            var part = (v >> n) & ((1UL << k) - 1);
            acc = (acc << k) | part; cnt += k; Bits += k;
            while (cnt >= 8) { buf[pos++ & 0xFFFF] = (byte)(acc >> (cnt - 8)); cnt -= 8; }
            acc &= (1UL << cnt) - 1;
        }
    }
}
