using System.Diagnostics;
using System.Globalization;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Cli;

/// <summary>Benchmarks for open question Q14 and the targets of research 02 section 5.1. Stopwatch and GC counters, no benchmarking library: the method is in PERF.md.</summary>
static partial class Bench
{
    const long Min = 60_000_000_000;

    public static void GraphTable(Engine e, TextWriter w)
    {
        w.WriteLine("graph | entities | relationships | head revision | telemetry windows");
        foreach (var g in e.Graphs) w.WriteLine($"{g.Name} | {g.State.EntityCount:N0} | {g.State.RelCount:N0} | {g.Head:N0} | {(e.Telemetry is InMemoryTelemetryStore m ? m.Dump(g.Key).Count : 0):N0}");
    }
    public static void GcAndCensus(Engine e, TextWriter w, params (string Label, object? Root)[] extra)
    {
        var proc = Process.GetCurrentProcess();
        w.WriteLine($"gc: allocated {GC.GetTotalAllocatedBytes() / 1e9:F2} GB in total, pause {GC.GetTotalPauseDuration().TotalSeconds:F1} s, process cpu {proc.TotalProcessorTime.TotalSeconds:F0} s, collections gen0 {GC.CollectionCount(0):N0} gen1 {GC.CollectionCount(1):N0} gen2 {GC.CollectionCount(2):N0}");
        GraphTable(e, w);
        Census.Run("replay", Census.EngineRoots(e, extra), e.Master.State.EntityCount, e.Master.State.RelCount, w);
    }
    static void GcStats(Process proc, Stopwatch wall, long rows, TextWriter w)
    {
        var pause = GC.GetTotalPauseDuration();
        w.WriteLine($"gc: allocated {GC.GetTotalAllocatedBytes() / 1e9:F1} GB in total ({GC.GetTotalAllocatedBytes() / (double)Math.Max(1, rows):F0} bytes per ingested flow row incl. structure), pause {pause.TotalSeconds:F1} s of {wall.Elapsed.TotalSeconds:F0} s wall ({100 * pause.TotalSeconds / wall.Elapsed.TotalSeconds:F1} %), collections gen0 {GC.CollectionCount(0):N0} gen1 {GC.CollectionCount(1):N0} gen2 {GC.CollectionCount(2):N0}; process user {proc.UserProcessorTime.TotalSeconds:F0} s, total cpu {proc.TotalProcessorTime.TotalSeconds:F0} s");
    }
    public static void KindTable(Engine e, TextWriter w)
    {
        w.WriteLine("operator kind | calls | ms | MB allocated");
        foreach (var (k, v) in e.KindStats.OrderByDescending(x => x.Value.Ms).Take(14)) w.WriteLine($"{k} | {v.Calls:N0} | {v.Ms:F0} | {v.Bytes / 1e6:F0}");
    }
    static long WorkingSet(Process p) { p.Refresh(); return p.WorkingSet64; }
    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    static double Pct(List<double> xs, double p) { var s = xs.Order().ToList(); return s.Count == 0 ? 0 : s[Math.Min(s.Count - 1, (int)Math.Ceiling(p * s.Count) - 1)]; }

    /// <summary>`lookBack`: every look-back and retention of the definitions (`24h`, and the default look-back of a lift) becomes this duration, so that a run of a few dozen windows reaches the steady state that the shipped 24 h reach after 1,440.</summary>
    static Engine NewEngine(IGraphStore? store = null, string? lookBack = null)
    {
        var cat = new Catalog();
        var files = Catalog.ReadDirectory(FindDefs());
        if (lookBack is not null)
            files = [.. files.Select(f => f with { Text = System.Text.RegularExpressions.Regex.Replace(f.Text.Replace("24h", lookBack), @"(?ms)^(\s*lift\b(?:(?!\bwithin\b)[^;])*);", "$1 within " + lookBack + ";") })];
        var res = cat.Load(files, updatePins: lookBack is not null);
        if (res.HasErrors) throw new InvalidOperationException(string.Join("\n", res.Diagnostics));
        // the shipped ratio: telemetry is retained for 48 h and read 24 h back, so a shortened look-back comes with twice its length of retention
        var retention = lookBack is null ? new EngineOptions().TelemetryRetentionNs : 2 * Lexer.DurationNs(lookBack);
        return new Engine(cat, cat.Spaces.Keys.First(), new EngineOptions { StoreFactory = _ => store ?? new InMemoryGraphStore(), TelemetryRetentionNs = retention });
    }

    static string FindDefs()
    {
        for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d is not null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "engine", "definitions"); if (Directory.Exists(c)) return c;
            c = Path.Combine(d.FullName, "definitions"); if (Directory.Exists(c)) return c;
        }
        throw new InvalidOperationException("definitions not found");
    }

    /// <summary>The "large estate" of research 02 section 2.3: 150,000 pods, 30,000 services, 300,000 Azure resources, about 240,000 active pod pairs per 60 s window.</summary>
    public static int Estate(Program.Options o)
    {
        var pods = int.TryParse(o.Get("pods"), out var p) ? p : 150_000;
        var services = int.TryParse(o.Get("services"), out var sv) ? sv : 30_000;
        var resources = int.TryParse(o.Get("resources"), out var rs) ? rs : 300_000;
        var pairs = int.TryParse(o.Get("pairs"), out var pr) ? pr : 240_000;
        var perWindow = int.TryParse(o.Get("per-window"), out var pw) ? pw : 1_000_000;
        var windows = int.TryParse(o.Get("windows"), out var wn) ? wn : 4;
        var lookBack = o.Get("look-back");
        // the window from which nothing grows any more: telemetry is retained for twice the look-back, and nothing a window adds stays longer than that
        var full = lookBack is null ? int.MaxValue : (int)(2 * Lexer.DurationNs(lookBack) / Min);
        Console.WriteLine($"# estate: {pods:N0} pods, {services:N0} services, {resources:N0} Azure resources, {pairs:N0} active pod pairs, {perWindow:N0} flow rows per 60 s window, {windows} windows{(lookBack is null ? "" : $", look-back and evidence retention {lookBack}, telemetry retention twice that (steady after {full} windows)")}");
        var proc = Process.GetCurrentProcess(); var wall = Stopwatch.StartNew();
        using var e = NewEngine(lookBack: lookBack);
        var world = new SyntheticWorld(e, pods, services);
        var sw = Stopwatch.StartNew();
        e.TrackAllocations = o.Flags.Contains("trace-alloc");
        world.LoadStructure();
        if (e.TrackAllocations) { foreach (var (op, ms) in e.OperatorMillis.OrderByDescending(x => x.Value).Take(8)) Console.WriteLine($"  structure time {op}: {ms:F0} ms"); Console.WriteLine("structure load:"); KindTable(e, Console.Out); e.OperatorMillis.Clear(); e.OperatorAllocations.Clear(); e.KindStats.Clear(); }
        Console.WriteLine($"dangling relationships of the master (an end that is not an entity of the graph): {e.Master.State.Rels.Count(r => !e.Master.State.HasEntity(r.Source) || !e.Master.State.HasEntity(r.Target)):N0}");
        Console.WriteLine($"structure: {sw.Elapsed.TotalSeconds:F1} s; master revision {e.Master.Head}, {e.Master.State.EntityCount:N0} entities, {e.Master.State.RelCount:N0} relationships; serviceTree {e.Graph("serviceTree").State.EntityCount:N0} entities, {e.Graph("serviceTree").State.RelCount:N0} relationships");
        sw.Restart();
        e.TrackAllocations = o.Flags.Contains("trace-alloc");
        var it = e.Interner; var rw = new RowWriter(e.RowLayout("resources"), it);
        for (var i = 0; i < resources; i++)
        {
            rw.Begin(); rw.Text("id", $"/subscriptions/s{i % 100}/resourceGroups/rg{i % 900}/providers/Microsoft.Compute/virtualMachines/vm{i}"); rw.Text("name", $"vm{i}"); rw.Text("type", "microsoft.compute/virtualmachines");
            rw.Text("location", "swedencentral"); rw.Text("resourceGroup", $"rg{i % 900}"); rw.Text("subscriptionId", $"s{i % 100}");
            if ((i + 1) % 50_000 == 0 || i == resources - 1) e.Ingest("resources", rw.Build()!, 100 + i);
        }
        Console.WriteLine($"resources: {sw.Elapsed.TotalSeconds:F1} s; master {e.Master.State.EntityCount:N0} entities");
        if (e.TrackAllocations) foreach (var (op, bytes) in e.OperatorAllocations.OrderByDescending(x => x.Value).Take(6)) Console.WriteLine($"  resources alloc {op}: {bytes / 1e6:F0} MB");
        if (e.TrackAllocations) { foreach (var (op, ms) in e.OperatorMillis.OrderByDescending(x => x.Value).Take(10)) Console.WriteLine($"  resources time {op}: {ms:F0} ms"); Console.WriteLine("resources load:"); KindTable(e, Console.Out); e.KindStats.Clear(); }
        var tree = o.Get("no-tree") is null && !o.Flags.Contains("no-tree");
        var hot = Math.Max(1, resources / 100);
        if (tree)
        {
            // the signal rule is data: CPU above 80 for 2 windows is unhealthy, below 60 for 2 windows clears it
            var rr = new RowWriter(e.RowLayout("signalRules"), it); rr.Begin();
            rr.Text("ruleId", "bench-cpu"); rr.Text("type", "microsoft.compute/virtualmachines"); rr.Text("meter", "Percentage CPU");
            rr.Dec(rr.Col("above"), 80m); rr.Int(rr.Col("enterAfter"), 2); rr.Dec(rr.Col("below"), 60m); rr.Int(rr.Col("exitAfter"), 2); rr.Int(rr.Col("severity"), 2); rr.Int(rr.Col("invert"), 0);
            e.Ingest("signalRules", rr.Build()!, 90);
        }
        GC.Collect(); var heap = GC.GetTotalMemory(true);
        Console.WriteLine($"live bytes after structure (promoted by a forced compacting collection): {Census.LiveBytes() / 1e6:F0} MB");
        Console.WriteLine($"managed heap after structure: {heap / 1e6:F0} MB; working set {WorkingSet(proc) / 1e6:F0} MB (engine graphs, in-memory stores and identity catalog included)");
        // flows over a fixed set of pod pairs
        var layout = e.RowLayout("flows"); var rnd = new Random(11);
        var pa = new int[pairs]; var pb = new int[pairs];
        for (var i = 0; i < pairs; i++) { pa[i] = rnd.Next(pods); pb[i] = (pa[i] * 7 + 1 + rnd.Next(5)) % pods; }
        var podCode = Enumerable.Range(0, pods).Select(i => (long)it.Intern($"pod-{i}")).ToArray();
        var nsCode = Enumerable.Range(0, 5).Select(i => (long)it.Intern($"ns{i}")).ToArray();
        long cl = it.Intern("synth"), fwd = it.Intern("FORWARDED"), drp = it.Intern("DROPPED");
        int cTime = layout.Index("time"), cCl = layout.Index("cluster"), cVe = layout.Index("verdict"), cSn = layout.Index("srcNamespace"), cSp = layout.Index("srcPod"), cDn = layout.Index("dstNamespace"), cDp = layout.Index("dstPod");
        var t0 = 1_794_300_000_000_000_000L / Min * Min; long seq = 1000; long rows = 0;
        var close = new List<double>(); var ingestS = 0.0; long liveAtFull = 0;
        var set = new[] { cTime, cCl, cVe, cSn, cSp, cDn, cDp };
        for (var w = 0; w < windows; w++)
        {
            var left = perWindow;
            var t = Stopwatch.GetTimestamp();
            while (left > 0)
            {
                var n = Math.Min(50_000, left);
                var b = new Batch(layout, n) { N = n };
                for (var c = 0; c < b.Cols.Length; c++) if (!set.Contains(c)) b.Cols[c].P = new bool[n];        // the columns this generator does not write are absent, not the value with code 0
                for (var i = 0; i < n; i++)
                {
                    var k = rnd.Next(pairs); var a = pa[k]; var d = pb[k];
                    b.Cols[cTime].L[i] = t0 + w * Min + rnd.NextInt64(Min - 2_000_000_000); b.Cols[cCl].L[i] = cl; b.Cols[cVe].L[i] = rnd.NextDouble() < 0.02 ? drp : fwd;
                    b.Cols[cSn].L[i] = nsCode[(a % services) % 5]; b.Cols[cSp].L[i] = podCode[a]; b.Cols[cDn].L[i] = nsCode[(d % services) % 5]; b.Cols[cDp].L[i] = podCode[d];
                }
                e.Ingest("flows", b, ++seq); left -= n; rows += n;
            }
            if (tree)
            {
                // one CPU reading per resource and window; the first 1 % of the resources run hot from the second window on
                var mw = new RowWriter(e.RowLayout("resourceMetrics"), it);
                for (var i = 0; i < resources; i++)
                {
                    mw.Begin(); mw.Instant("time", t0 + w * Min + 1_000_000_000L + i % 50_000);
                    mw.Text("resourceId", $"/subscriptions/s{i % 100}/resourceGroups/rg{i % 900}/providers/Microsoft.Compute/virtualMachines/vm{i}"); mw.Text("metric", "Percentage CPU");
                    mw.Dec(mw.Col("value"), i < hot && w >= 1 ? 95m : 20m + i % 30);
                    if ((i + 1) % 50_000 == 0 || i == resources - 1) e.Ingest("resourceMetrics", mw.Build()!, ++seq);
                }
            }
            ingestS += (Stopwatch.GetTimestamp() - t) / (double)Stopwatch.Frequency;
            var tc = Stopwatch.GetTimestamp();
            e.Advance(t0 + (w + 1) * Min + 1);
            close.Add(Ms(Stopwatch.GetTimestamp() - tc));
            if (e.TrackAllocations && w < 2) { Console.WriteLine($"window {w} (ingest of the flows, the readings and the close):"); KindTable(e, Console.Out); e.KindStats.Clear(); }
            Console.WriteLine($"window {w}: close {close[^1]:F0} ms; master {e.Master.State.RelCount:N0} relationships, serviceTree {e.Graph("serviceTree").State.RelCount:N0} relationships (revision {e.Graph("serviceTree").Head})");
            if (w + 1 == full)
            {
                liveAtFull = Census.LiveBytes(); Console.WriteLine($"  retention full after window {w}: live bytes {liveAtFull / 1e6:F0} MB");
                // a second census point: what differs from the census at the end is what a window adds once nothing should grow any more
                if (o.Flags.Contains("census")) Census.Run($"estate when the retention is full (window {w})", Census.EngineRoots(e), e.Master.State.EntityCount, e.Master.State.RelCount, Console.Out);
            }
            if (tree) Console.WriteLine($"  tree: treeStructure {e.Graph("treeStructure").State.EntityCount:N0} elements, {e.Graph("treeStructure").State.RelCount:N0} Contains; treeSignals {e.Graph("treeSignals").State.EntityCount:N0} signals; healthTree {e.Graph("healthTree").State.EntityCount:N0} entities, {e.Graph("healthTree").State.RelCount:N0} relationships; heap {GC.GetTotalMemory(false) / 1e6:F0} MB, working set {WorkingSet(proc) / 1e6:F0} MB");
        }
        GC.Collect(); heap = GC.GetTotalMemory(true);
        var tel = ((InMemoryTelemetryStore)e.Telemetry).Dump(e.Master.Key).Count + ((InMemoryTelemetryStore)e.Telemetry).Dump(e.Graph("serviceTree").Key).Count;
        Console.WriteLine($"flows: {rows:N0} rows, generation included: {rows / ingestS / 1e6:F2} M rows/s; window close p50 {Pct(close, .5):F0} ms, max {close.Max():F0} ms");
        Console.WriteLine($"dangling relationships of the master at the end: {e.Master.State.Rels.Count(r => !e.Master.State.HasEntity(r.Source) || !e.Master.State.HasEntity(r.Target)):N0} of {e.Master.State.RelCount:N0}");
        var liveAtEnd = Census.LiveBytes();
        Console.WriteLine($"live bytes at the end (promoted): {liveAtEnd / 1e6:F0} MB");
        if (windows > full)
            Console.WriteLine($"steady state (windows {full} to {windows - 1}, look-back and retention full): window close p50 {Pct([.. close.Skip(full)], .5):F0} ms, max {close.Skip(full).Max():F0} ms; live bytes {liveAtFull / 1e6:F0} MB when the retention filled, {liveAtEnd / 1e6:F0} MB at the end ({(liveAtEnd - liveAtFull) * 100.0 / liveAtFull:+0.0;-0.0} %)");
        Console.WriteLine($"end state: managed heap {heap / 1e6:F0} MB, working set {WorkingSet(proc) / 1e6:F0} MB, telemetry windows {tel:N0}");
        if (e.TrackAllocations) { Console.WriteLine("flow windows (ingest and closes):"); KindTable(e, Console.Out); }
        GcStats(proc, wall, rows + resources * (tree ? windows : 0), Console.Out);
        if (o.Flags.Contains("census")) GraphTable(e, Console.Out);
        if (o.Flags.Contains("census")) Census.Run($"estate {pods} pods {services} services {resources} resources {(tree ? "with readings" : "no readings")}", Census.EngineRoots(e), e.Master.State.EntityCount, e.Master.State.RelCount, Console.Out);
        return 0;
    }

    public static int Run(Program.Options o)
    {
        if (o.Positional.Contains("estate")) return Estate(o);
        if (o.Positional.Contains("phases")) return Phases(o);
        if (o.Positional.Contains("b5")) return SourceToGraph(o);
        if (o.Positional.Contains("b7")) return DurableReads(o);
        if (o.Positional.Contains("b8")) return ElementView(o);
        if (o.Positional.Contains("b10-graphs")) return ColdStart(o);
        if (o.Positional.Contains("b10")) return Restart(o);
        var rows = long.TryParse(o.Get("rows"), out var r) ? r : 10_000_000;
        var pods = int.TryParse(o.Get("pods"), out var p) ? p : 1000;
        var services = int.TryParse(o.Get("services"), out var s) ? s : 100;
        var perWindow = int.TryParse(o.Get("per-window"), out var pw) ? pw : 100_000;
        Console.WriteLine($"# Q14: {rows:N0} synthetic flow rows, {pods} pods, {services} services, {perWindow:N0} rows per 60 s window, 5 peers per pod");
        using var e = NewEngine();
        e.TrackAllocations = o.Flags.Contains("trace-alloc") || o.Get("trace-alloc") is not null;
        var world = new SyntheticWorld(e, pods, services);
        var sw = Stopwatch.StartNew();
        world.LoadStructure();
        Console.WriteLine($"structure loaded in {sw.ElapsedMilliseconds} ms: master revision {e.Master.Head}, {e.Master.State.EntityCount} entities, {e.Master.State.RelCount} relationships");

        // pre-built row pool: codes are interned up front, so the timed loop measures the plan, not the generator
        var layout = e.RowLayout("flows"); var it = e.Interner;
        var pool = new List<Batch>(); var rnd = new Random(7);
        var peers = Enumerable.Range(0, pods).Select(a => Enumerable.Range(0, 5).Select(k => (a * 13 + 1 + k * 7) % pods).ToArray()).ToArray();
        var podCode = Enumerable.Range(0, pods).Select(i => (long)it.Intern($"pod-{i}")).ToArray();
        var nsCode = Enumerable.Range(0, 5).Select(i => (long)it.Intern($"ns{i}")).ToArray();
        long cl = it.Intern("synth"), fwd = it.Intern("FORWARDED"), drp = it.Intern("DROPPED");
        const int batchRows = 50_000;
        int cTime = layout.Index("time"), cCl = layout.Index("cluster"), cVe = layout.Index("verdict"), cSn = layout.Index("srcNamespace"), cSp = layout.Index("srcPod"), cDn = layout.Index("dstNamespace"), cDp = layout.Index("dstPod");
        for (var k = 0; k < 4; k++)
        {
            var b = new Batch(layout, batchRows) { N = batchRows };
            for (var i = 0; i < batchRows; i++)
            {
                var a = rnd.Next(pods); var d = peers[a][rnd.Next(5)];
                b.Cols[cCl].L[i] = cl; b.Cols[cVe].L[i] = rnd.NextDouble() < 0.02 ? drp : fwd;
                b.Cols[cSn].L[i] = nsCode[(a % services) % 5]; b.Cols[cSp].L[i] = podCode[a]; b.Cols[cDn].L[i] = nsCode[(d % services) % 5]; b.Cols[cDp].L[i] = podCode[d];
            }
            pool.Add(b);
        }
        var t0 = 1_794_300_000_000_000_000L / Min * Min;
        var ingestTicks = 0L; long ingested = 0; long alloc = 0; var ingestOps = new Dictionary<string, long>();
        var closeMs = new List<double>(); var closeAlloc = new List<long>();
        var windows = (int)Math.Ceiling(rows / (double)perWindow);
        GC.Collect(); GC.WaitForPendingFinalizers();
        var gcBefore = GC.CollectionCount(0);
        for (var w = 0; w < windows; w++)
        {
            var remaining = Math.Min(perWindow, rows - ingested);
            var inWindow = 0L;
            while (inWindow < remaining)
            {
                var b = pool[(int)((ingested / batchRows) % pool.Count)];
                var n = (int)Math.Min(batchRows, remaining - inWindow);
                var span = Min - 2 * 1_000_000_000L;
                for (var i = 0; i < n; i++) b.Cols[cTime].L[i] = t0 + w * Min + (long)(i * (double)span / n);
                b.N = n;
                e.OperatorAllocations.Clear();
                var a0 = GC.GetAllocatedBytesForCurrentThread(); var t = Stopwatch.GetTimestamp();
                e.Ingest("flows", b, ++ingested == 0 ? 0 : ingested);
                foreach (var (op, bytes) in e.OperatorAllocations) ingestOps[op] = ingestOps.GetValueOrDefault(op) + bytes;
                ingestTicks += Stopwatch.GetTimestamp() - t; alloc += GC.GetAllocatedBytesForCurrentThread() - a0;
                ingested += n - 1; inWindow += n;
            }
            var a1 = GC.GetAllocatedBytesForCurrentThread(); var tc = Stopwatch.GetTimestamp();
            e.Advance(t0 + (w + 1) * Min + 1);
            closeMs.Add(Ms(Stopwatch.GetTimestamp() - tc)); closeAlloc.Add(GC.GetAllocatedBytesForCurrentThread() - a1);
        }
        var seconds = ingestTicks / (double)Stopwatch.Frequency;
        Console.WriteLine($"ingest: {ingested:N0} rows in {seconds:F2} s of Ingest calls = {ingested / seconds / 1e6:F2} M rows/s; allocated {alloc / (double)ingested:F3} bytes/row; gen0 collections {GC.CollectionCount(0) - gcBefore}");
        var dump = ((InMemoryTelemetryStore)e.Telemetry).Dump(e.Master.Key).Count;
        Console.WriteLine($"after the run: master revision {e.Master.Head}, {e.Master.State.RelCount} Calls/Backs relationships, {dump:N0} master telemetry windows, serviceTree revision {e.Graph("serviceTree").Head}, {e.Graph("serviceTree").State.RelCount} relationships");
        Console.WriteLine($"window close (Advance: group emit, identity lookups, telemetry append, Series, Rollup, Threshold, derived commit): n={closeMs.Count}, p50 {Pct(closeMs, .5):F2} ms, p99 {Pct(closeMs, .99):F2} ms, max {closeMs.Max():F2} ms; allocated per close p50 {Pct(closeAlloc.Select(x => (double)x).ToList(), .5) / 1e6:F2} MB");

        if (e.TrackAllocations)
            foreach (var (op, bytes) in ingestOps.OrderByDescending(x => x.Value).Take(6)) Console.WriteLine($"  ingest alloc {op}: {bytes / 1e6:F2} MB ({bytes / (double)ingested:F3} bytes/row)");
        HandFused(pool, layout, pods);
        TelemetryAppend();
        Commit(e);
        Reads(e, pods);
        Memory();
        return 0;
    }

    /// <summary>A hand-written fused loop for the same query (filter, window, group count and sum): the ceiling a compiled delegate could reach on this layout.</summary>
    static void HandFused(List<Batch> pool, Layout layout, int pods)
    {
        var cSp = layout.Index("srcPod"); var cDp = layout.Index("dstPod"); var cVe = layout.Index("verdict"); var cTime = layout.Index("time");
        var table = new Dictionary<(long, long, long), (long Count, long Drops)>(pods * 8);
        long drop = pool[0].Cols[cVe].L.Max();
        var sw = Stopwatch.StartNew(); long n = 0;
        for (var rep = 0; rep < 40; rep++)
            foreach (var b in pool)
            {
                var sp = b.Cols[cSp].L; var dp = b.Cols[cDp].L; var ve = b.Cols[cVe].L; var tm = b.Cols[cTime].L;
                for (var i = 0; i < b.N; i++)
                {
                    var ws = tm[i] - tm[i] % Min;
                    ref var acc = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(table, (sp[i], dp[i], ws), out _);
                    acc.Count++; if (ve[i] == drop) acc.Drops++;
                }
                n += b.N;
            }
        Console.WriteLine($"hand-fused reference loop (Dictionary accumulators, same query): {n / sw.Elapsed.TotalSeconds / 1e6:F2} M rows/s ({table.Count} groups)");
    }

    /// <summary>H2: Append into the in-memory ring, 1,000 elements x 2 properties x windows.</summary>
    static void TelemetryAppend()
    {
        var store = new InMemoryTelemetryStore(); var key = new GraphKey("t", "g");
        const int per = 20_000; var batch = new Sample[per]; long total = 0;
        var sw = Stopwatch.StartNew();
        for (var w = 0; w < 100; w++)
        {
            for (var i = 0; i < per; i++) batch[i] = new Sample(i / 2 % 10_000, i % 2, w * Min, Min, i, w);
            store.Append(key, batch); total += per;
        }
        Console.WriteLine($"telemetry append (ring): {total / sw.Elapsed.TotalSeconds / 1e6:F2} M samples/s over {total:N0} samples, 10,000 series x 2 properties x 100 windows");
    }

    /// <summary>Commit latency through the port, in memory and on SQLite, with 50 edits per patch.</summary>
    static void Commit(Engine _)
    {
        foreach (var (name, make) in new (string, Func<IGraphStore>)[] { ("memory", () => new InMemoryGraphStore()), ("sqlite", () => new SqliteGraphStore(Path.Combine(Path.GetTempPath(), "sattle-bench-" + Guid.NewGuid().ToString("N") + ".db"))) })
        {
            using var store = make();
            var key = new GraphKey("t", "g"); var lat = new List<double>(); long head = 0;
            for (var i = 0; i < 2000; i++)
            {
                var edits = Enumerable.Range(0, 50).Select(k => Edit.Create(new Entity($"e{i}-{k}", "T", Props.Of(("p", Value.Integer(k)))))).ToList();
                var t = Stopwatch.GetTimestamp();
                var res = store.Commit(new CommitBatch(key, head, [new Patch("g", head, head + 1, edits)], [Frontier.Empty], []));
                lat.Add(Ms(Stopwatch.GetTimestamp() - t));
                head = ((CommitResult.Committed)res).Head;
            }
            Console.WriteLine($"commit {name}: 2,000 patches of 50 edits: p50 {Pct(lat, .5):F3} ms, p99 {Pct(lat, .99):F3} ms, max {lat.Max():F3} ms");
        }
    }

    /// <summary>H6: point read and one-hop adjacency scan on the hot graph.</summary>
    static void Reads(Engine e, int pods)
    {
        var g = e.Master.State;
        var ids = g.Entities.Select(x => x.Id).ToArray();
        var rels = g.Rels.Select(r => r.Source).Distinct().ToArray();
        long sink = 0; const int n = 5_000_000;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < n; i++) sink += g.Entity(ids[i % ids.Length]) is null ? 0 : 1;
        var point = sw.Elapsed.TotalMilliseconds * 1e6 / n;
        sw.Restart(); long nb = 0; const int m = 1_000_000;
        for (var i = 0; i < m; i++) foreach (var r in g.OutRels(rels[i % rels.Length])) nb++;
        var scan = sw.Elapsed.TotalMilliseconds * 1e6 / m;
        Console.WriteLine($"hot reads ({g.EntityCount} entities, {g.RelCount} relationships): point read {point:F1} ns, one-hop scan {scan:F1} ns per scan at {nb / (double)m:F1} neighbours on average ({sink > 0})");
        _ = pods;
    }

    /// <summary>H9: managed heap for 750,000 entities and 2.5 M relationships in the hot graph (no telemetry).</summary>
    static void Memory()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetTotalMemory(true);
        var g = new Graph(); var sw = Stopwatch.StartNew();
        var edits = new List<Edit>(100_000);
        for (var i = 0; i < 750_000; i++)
        {
            edits.Add(Edit.Create(new Entity("e:" + i.ToString("x8"), "d:m/entity:Pod", Props.Of(("d:m/prop:name", Value.Text("pod-" + i)), ("d:m/prop:namespace", Value.Text("ns" + i % 50))))));
            if (edits.Count == 100_000) { g.Apply(edits); edits.Clear(); }
        }
        g.Apply(edits); edits.Clear();
        var rnd = new Random(3);
        for (var i = 0; i < 2_500_000; i++)
        {
            var a = rnd.Next(750_000); var b = rnd.Next(750_000);
            edits.Add(Edit.Create(new Rel("r:" + i.ToString("x8"), "e:" + a.ToString("x8"), "e:" + b.ToString("x8"), i % 3 == 0 ? "d:m/rel:Backs" : "d:m/rel:Calls", Props.Empty)));
            if (edits.Count == 100_000) { var err = g.Apply(edits); if (err is not null) edits.RemoveAll(x => g.Check([x]) is not null); g.Apply(edits); edits.Clear(); }
        }
        g.Apply(edits.Where(x => g.Check([x]) is null).ToList());
        var after = GC.GetTotalMemory(true);
        Console.WriteLine($"memory: {g.EntityCount:N0} entities and {g.RelCount:N0} relationships built in {sw.Elapsed.TotalSeconds:F1} s: managed heap {(after - before) / 1e6:F0} MB");
        GC.KeepAlive(g);
    }
}
