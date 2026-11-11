using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Cli;

/// <summary>B5, B7, B8, B10 of paper Ch. 13: source to graph, durable reads, element view, cold start. Ten runs each, median and spread per figure (13.6).</summary>
static partial class Bench
{
    const int Runs = 10;

    static string Fmt(double v) => v.ToString(v < 10 ? "F3" : v < 1000 ? "F1" : "F0", CultureInfo.InvariantCulture);

    /// <summary>Median, minimum, maximum and the order-statistic interval x2..x9 of ten runs (about 98% for the median).</summary>
    static string Spread(IReadOnlyList<double> v, string unit)
    {
        var s = v.Order().ToArray(); var n = s.Length;
        var med = n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
        return $"median {Fmt(med)} {unit}, min {Fmt(s[0])}, max {Fmt(s[^1])}" + (n >= 10 ? $", interval of the median [{Fmt(s[1])}, {Fmt(s[^2])}]" : "");
    }

    static string LoadAvg()
    {
        try { var p = Process.Start(new ProcessStartInfo("sysctl", "-n vm.loadavg") { RedirectStandardOutput = true })!; var t = p.StandardOutput.ReadToEnd().Trim(' ', '{', '}', '\n'); p.WaitForExit(); return t; }
        catch (Exception) { return "n/a"; }
    }

    static void Environment()
    {
        Console.WriteLine($"environment: {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, {System.Environment.ProcessorCount} logical cores, .NET {System.Environment.Version}, Release={!System.Diagnostics.Debugger.IsAttached}, load average at start {LoadAvg()}");
    }

    /// <summary>Counts engine time spent in logical-time advances (window closes) and whether an ingest produced a master revision.</summary>
    sealed class TimingInput(Engine e) : IEngineInput
    {
        public long AdvTicks; public bool IngestCommitted; public readonly List<double> Closes = [];
        public Interner Interner => e.Interner;
        public Layout RowLayout(string source) => e.RowLayout(source);
        public void Ingest(string source, Batch rows, long seq)
        {
            var master = e.Master; var h = master.Head;
            e.Ingest(source, rows, seq);
            if (master.Head != h) IngestCommitted = true;
        }
        public void Advance(long lambdaNs, long seq)
        {
            var t = Stopwatch.GetTimestamp();
            e.Advance(lambdaNs, seq);
            var d = Stopwatch.GetTimestamp() - t; AdvTicks += d; Closes.Add(Ms(d));
        }
        public void RecordLoss(long timeNs, long count, string reason = "sourceLoss") => e.RecordLoss(timeNs, count, reason);
    }

    sealed class Rig : IDisposable
    {
        public readonly Catalog Cat = new(); public readonly string Space; public readonly Engine Engine; public readonly TimingInput Input; public readonly Replay Replay;
        public Rig(string cluster, Func<string, IGraphStore> factory, int chunkRows)
        {
            var res = Cat.Load(Catalog.ReadDirectory(FindDefs()));
            if (res.HasErrors) throw new InvalidOperationException(string.Join("\n", res.Diagnostics));
            Space = Cat.Spaces.Keys.First();
            var adapters = AdapterRegistry.Create(cluster);
            Engine = new Engine(Cat, Space, new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000, StoreFactory = factory });
            Input = new TimingInput(Engine);
            var ro = Program.ReplayOptionsOf(Program.Options.Parse([]), cluster);
            Replay = new Replay(Input, Cat.Spaces[Space], adapters, new ReplayOptions { ChunkRows = chunkRows, AdvanceEveryNs = ro.AdvanceEveryNs, LatenessNs = ro.LatenessNs, FlushAtEnd = true, Speed = 0, Cluster = cluster });
        }
        public void Dispose() => Engine.Dispose();
    }

    static string Capture(Program.Options o) => Program.FindCapture(o.Get("capture") ?? "latest");

    // ---- B5 / H5: engine receipt to master revision ----

    /// <summary>One record at a time: adapter parse, row building, ingest, master commit. The time spent in window closes that the record triggered is taken out (H4 and H12 cover closes) and reported on its own.</summary>
    public static int SourceToGraph(Program.Options o)
    {
        var capture = Capture(o); var cluster = Program.DetectCluster(capture);
        var kind = o.Get("store") ?? "memory"; var runs = int.TryParse(o.Get("runs"), out var rn) ? rn : Runs;
        Console.WriteLine($"# B5 source to graph: capture {Path.GetFileName(capture.TrimEnd('/'))}, store {kind}, {runs} runs, one record per ingest call");
        Environment();
        var p50 = new List<double>(); var p99 = new List<double>(); var max = new List<double>(); var c50 = new List<double>(); var c99 = new List<double>(); var cmax = new List<double>(); var cl99 = new List<double>(); var clmax = new List<double>();
        for (var run = 0; run < runs; run++)
        {
            var path = Path.Combine(Path.GetTempPath(), "sattle-b5-" + Guid.NewGuid().ToString("N") + ".db");
            using var shared = kind == "sqlite" ? new SqliteGraphStore(path) : null;
            using var rig = new Rig(cluster, _ => shared is not null ? new Shared(shared) : new InMemoryGraphStore(), 2000);
            var all = new List<double>(300_000); var committing = new List<double>(); var master = rig.Engine.Master; var wall = Stopwatch.StartNew();
            foreach (var env in CaptureReader.Read(capture))
            {
                rig.Input.IngestCommitted = false; var adv0 = rig.Input.AdvTicks; var t0 = Stopwatch.GetTimestamp();
                if (!rig.Replay.Process(env, false)) break;
                rig.Replay.FlushPending();
                var dt = Ms(Stopwatch.GetTimestamp() - t0 - (rig.Input.AdvTicks - adv0));
                all.Add(dt); if (rig.Input.IngestCommitted) committing.Add(dt);
            }
            var closes = rig.Input.Closes;
            p50.Add(Pct(all, .5)); p99.Add(Pct(all, .99)); max.Add(all.Max());
            c50.Add(Pct(committing, .5)); c99.Add(Pct(committing, .99)); cmax.Add(committing.Max());
            cl99.Add(Pct(closes, .99)); clmax.Add(closes.Max());
            Console.WriteLine($"run {run + 1}: {all.Count:N0} records in {wall.Elapsed.TotalSeconds:F1} s, master revision {master.Head:N0}, {committing.Count:N0} records made a master revision; all records p50 {Pct(all, .5):F3} ms p99 {Pct(all, .99):F3} ms max {all.Max():F1} ms; committing records p50 {Pct(committing, .5):F3} ms p99 {Pct(committing, .99):F3} ms max {committing.Max():F1} ms; {closes.Count} window closes p99 {Pct(closes, .99):F0} ms max {closes.Max():F0} ms; load {LoadAvg()}");
            if (File.Exists(path)) { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f); }
        }
        Console.WriteLine($"H5 all records, p99:           {Spread(p99, "ms")}  (target p99 <= 2000 ms)");
        Console.WriteLine($"H5 all records, p50:           {Spread(p50, "ms")}");
        Console.WriteLine($"H5 all records, max:           {Spread(max, "ms")}");
        Console.WriteLine($"H5 committing records, p99:    {Spread(c99, "ms")}");
        Console.WriteLine($"H5 committing records, p50:    {Spread(c50, "ms")}");
        Console.WriteLine($"H5 committing records, max:    {Spread(cmax, "ms")}");
        Console.WriteLine($"window close (excluded above): p99 {Spread(cl99, "ms")}; max {Spread(clmax, "ms")}");
        return 0;
    }

    /// <summary>The SQLite store is shared by every graph of a space; the engine disposes what its factory made, so a wrapper keeps the owner in charge of disposal.</summary>
    sealed class Shared(IGraphStore s) : IGraphStore
    {
        public HeadInfo Head(GraphKey g) => s.Head(g);
        public CommitResult Commit(CommitBatch b) => s.Commit(b);
        public IReadOnlyList<ReadResult> Read(GraphKey g, long r, IReadOnlyList<ElementKey> k) => s.Read(g, r, k);
        public AdjacencyPage ScanAdjacency(GraphKey g, long r, string e, Direction d, string? t, int n, string? c) => s.ScanAdjacency(g, r, e, d, t, n, c);
        public IEnumerable<CommittedPatch> Follow(GraphKey g, long a) => s.Follow(g, a);
        public SealInfo Seal(GraphKey g, long r) => s.Seal(g, r);
        public IEnumerable<Element> OpenSnapshot(GraphKey g, long r) => s.OpenSnapshot(g, r);
        public void Dispose() { }
    }

    // ---- B7 / H7: durable reads ----

    static (double P50, double P99, double Max) Latencies(int n, Action<int> op)
    {
        var l = new List<double>(n);
        for (var i = 0; i < n; i++) { var t = Stopwatch.GetTimestamp(); op(i); l.Add(Ms(Stopwatch.GetTimestamp() - t)); }
        return (Pct(l, .5), Pct(l, .99), l.Max());
    }

    public static int DurableReads(Program.Options o)
    {
        const int entities = 2000, hubs = 20, fan = 100, point = 1000, scans = 200;
        var endpoint = o.Get("cosmos"); var runs = int.TryParse(o.Get("runs"), out var rn) ? rn : Runs;
        Console.WriteLine($"# B7 durable reads: {entities:N0} entities, {hubs} hubs with {fan} outgoing relationships, per run {point:N0} point reads and {scans} one-hop scans of {fan} neighbours, {runs} runs");
        Environment();
        var stores = new List<(string, Func<IGraphStore>)> { ("memory", () => new InMemoryGraphStore()), ("sqlite", () => new SqliteGraphStore(Path.Combine(Path.GetTempPath(), "sattle-b7-" + Guid.NewGuid().ToString("N") + ".db"))) };
        if (endpoint is not null) stores.Add(("cosmos-emulator", () => CosmosGraphStore.Open(endpoint, "sattle-b7", true)));
        foreach (var (name, make) in stores)
        {
            using var store = make(); var key = new GraphKey("t", "state"); long head = 0;
            var load = Stopwatch.StartNew(); long commits = 0;
            void Commit(List<Edit> edits) { var res = store.Commit(new CommitBatch(key, head, [new Patch("state", head, head + 1, edits)], [Frontier.Empty], [])); head = ((CommitResult.Committed)res).Head; commits++; }
            for (var i = 0; i < entities; i += 50) Commit(Enumerable.Range(i, 50).Select(k => Edit.Create(new Entity($"e{k:D5}", "T", Props.Of(("p", Value.Integer(k)))))).ToList());
            for (var h = 0; h < hubs; h++)
                for (var k = 0; k < fan; k += 50)
                    Commit(Enumerable.Range(k, 50).Select(j => Edit.Create(new Rel($"r{h:D2}-{j:D3}", $"e{h:D5}", $"e{hubs + h * 99 + j:D5}", "R", Props.Empty))).ToList());
            Console.WriteLine($"{name}: loaded in {load.Elapsed.TotalSeconds:F1} s, {commits} commits, revision {head}");
            var rnd = new Random(11);
            for (var i = 0; i < 100; i++) store.Read(key, head, [ElementKey.Entity($"e{rnd.Next(entities):D5}")]);   // warm-up
            var pp50 = new List<double>(); var pp99 = new List<double>(); var pmax = new List<double>(); var sp50 = new List<double>(); var sp99 = new List<double>(); var smax = new List<double>();
            for (var run = 0; run < runs; run++)
            {
                var pr = Latencies(point, _ => { if (store.Read(key, head, [ElementKey.Entity($"e{rnd.Next(entities):D5}")])[0] is not ReadResult.Present) throw new InvalidOperationException("missing"); });
                var sc = Latencies(scans, i => { if (store.ScanAdjacency(key, head, $"e{i % hubs:D5}", Direction.Out, null, fan, null).Entries.Count != fan) throw new InvalidOperationException("short scan"); });
                pp50.Add(pr.P50); pp99.Add(pr.P99); pmax.Add(pr.Max); sp50.Add(sc.P50); sp99.Add(sc.P99); smax.Add(sc.Max);
                Console.WriteLine($"  run {run + 1}: point read p50 {pr.P50:F3} ms p99 {pr.P99:F3} ms max {pr.Max:F2} ms; scan of {fan} p50 {sc.P50:F3} ms p99 {sc.P99:F3} ms max {sc.Max:F2} ms; load {LoadAvg()}");
            }
            Console.WriteLine($"H7 {name} point read p99: {Spread(pp99, "ms")}  (target p99 <= 10 ms)");
            Console.WriteLine($"H7 {name} point read p50: {Spread(pp50, "ms")}");
            Console.WriteLine($"H7 {name} one-hop scan of {fan}, p99: {Spread(sp99, "ms")}  (target <= 20 ms)");
            Console.WriteLine($"H7 {name} one-hop scan of {fan}, p50: {Spread(sp50, "ms")}");
            if (store is CosmosGraphStore c) foreach (var (kind, (calls, ru)) in c.Charges.OrderBy(x => x.Key)) Console.WriteLine($"  cosmos {kind}: {calls:N0} request(s), reported charge {ru:N1} (the emulator reports 1 per request, no RU accounting)");
        }
        return 0;
    }

    // ---- B8 / H8: element view ----

    public static int ElementView(Program.Options o)
    {
        const int n = 10_000, windows = 60, props = 4; var runs = int.TryParse(o.Get("runs"), out var rn) ? rn : Runs;
        Console.WriteLine($"# B8 element view: structure at head joined with {windows} windows (1 h at 60 s) of {props} series for {n:N0} elements, hot plane only, {runs} runs");
        Environment();
        var g = new Graph(); var edits = new List<Edit>(n);
        for (var i = 0; i < n; i++) edits.Add(Edit.Create(new Entity($"e{i:D6}", "d:m/entity:Pod", Props.Of(("d:m/prop:name", Value.Text("pod-" + i)), ("d:m/prop:namespace", Value.Text("ns" + i % 50))))));
        g.Apply(edits);
        var ts = new InMemoryTelemetryStore(); var key = new GraphKey("t", "g"); var batch = new Sample[n * props];
        for (var w = 0; w < windows; w++)
        {
            for (var i = 0; i < batch.Length; i++) batch[i] = new Sample(i / props, i % props, w * Min, Min, (i * 7 + w) % 1000, w);
            ts.Append(key, batch);
        }
        var elems = Enumerable.Range(0, n).ToArray(); var pids = Enumerable.Range(0, props).ToArray(); var ids = Enumerable.Range(0, n).Select(i => $"e{i:D6}").ToArray();
        long sink = 0;
        void View(int[] es, string[] idsOf)
        {
            foreach (var id in idsOf) sink += g.Entity(id) is null ? 0 : 1;
            sink += ts.ScanWindow(key, es, pids, 0, windows * Min, (_, _, _, v) => { if (v < 0) sink++; });
        }
        for (var i = 0; i < 3; i++) View(elems, ids);   // warm-up
        var full = new List<double>(); var one = new List<double>();
        for (var run = 0; run < runs; run++)
        {
            var g0 = GC.CollectionCount(0); var g1 = GC.CollectionCount(1); var g2 = GC.CollectionCount(2); var pause0 = GC.GetTotalPauseDuration(); var alloc0 = GC.GetAllocatedBytesForCurrentThread();
            var t = Stopwatch.GetTimestamp(); View(elems, ids); var ms = Ms(Stopwatch.GetTimestamp() - t); full.Add(ms);
            var gcInfo = $"allocated {(GC.GetAllocatedBytesForCurrentThread() - alloc0) / 1e6:F0} MB, collections gen0 {GC.CollectionCount(0) - g0} gen1 {GC.CollectionCount(1) - g1} gen2 {GC.CollectionCount(2) - g2}, GC pause {(GC.GetTotalPauseDuration() - pause0).TotalMilliseconds:F1} ms";
            var l = new List<double>(); var rnd = new Random(run);
            for (var i = 0; i < 2000; i++) { var e = rnd.Next(n); var t1 = Stopwatch.GetTimestamp(); View([e], [ids[e]]); l.Add(Ms(Stopwatch.GetTimestamp() - t1)); }
            one.Add(Pct(l, .99));
            Console.WriteLine($"run {run + 1}: view of {n:N0} elements {ms:F1} ms ({gcInfo}); single element view p50 {Pct(l, .5):F4} ms p99 {Pct(l, .99):F4} ms; load {LoadAvg()}");
        }
        Console.WriteLine($"H8 view of {n:N0} elements x {props} series x {windows} windows ({n * props * windows:N0} points): {Spread(full, "ms")}  (target <= 100 ms; {sink > 0})");
        Console.WriteLine($"H8 single element view, p99 over 2,000 views: {Spread(one, "ms")}");
        GC.KeepAlive(g);
        return 0;
    }

    // ---- B10 / H10: cold start ----

    public static int ColdStart(Program.Options o)
    {
        var capture = Capture(o); var cluster = Program.DetectCluster(capture); var runs = int.TryParse(o.Get("runs"), out var rn) ? rn : Runs;
        Console.WriteLine($"# B10 cold start: capture {Path.GetFileName(capture.TrimEnd('/'))}, {runs} runs");
        Environment();
        var path = Path.Combine(Path.GetTempPath(), "sattle-b10-" + Guid.NewGuid().ToString("N") + ".db");
        List<(GraphKey Key, long Head)> graphs;
        double wallLoad;
        using (var rig = new Rig(cluster, _ => new SqliteGraphStore(path), 2000))
        {
            var sw = Stopwatch.StartNew(); rig.Replay.Run(capture); wallLoad = sw.Elapsed.TotalSeconds;
            graphs = rig.Engine.Graphs.Select(x => (x.Key, x.Head)).ToList();
        }
        Console.WriteLine($"log written: {graphs.Count} graphs, {graphs.Sum(x => x.Head):N0} revisions in total ({string.Join(", ", graphs.Select(x => $"{x.Key.Graph}={x.Head}"))}), SQLite file {new FileInfo(path).Length / 1e6:F1} MB, capture replayed in {wallLoad:F1} s");
        var snap = new List<double>(); var follow = new List<double>(); long snapElems = 0, patches = 0, edits = 0;
        for (var run = 0; run < runs; run++)
        {
            double s = 0, f = 0;
            using (var store = new SqliteGraphStore(path))
                foreach (var (key, head) in graphs)
                {
                    var t = Stopwatch.GetTimestamp();
                    var g = new Graph(); var els = store.OpenSnapshot(key, head).ToList();
                    var err = g.Apply(els.OfType<Entity>().Select(Edit.Create).ToList()) ?? g.Apply(els.OfType<Rel>().Select(Edit.Create).ToList());
                    if (err is not null) throw new InvalidOperationException(err);
                    s += Ms(Stopwatch.GetTimestamp() - t); snapElems += els.Count;
                    t = Stopwatch.GetTimestamp();
                    var g2 = new Graph();
                    foreach (var cp in store.Follow(key, 0)) { var e2 = g2.Apply(cp.Patch.Edits); if (e2 is not null) throw new InvalidOperationException(e2); patches++; edits += cp.Patch.Edits.Length; }
                    f += Ms(Stopwatch.GetTimestamp() - t);
                    if (g2.EntityCount != g.EntityCount || g2.RelCount != g.RelCount) throw new InvalidOperationException("snapshot and log replay differ in size");
                }
            snap.Add(s); follow.Add(f);
            Console.WriteLine($"run {run + 1}: all {graphs.Count} graphs, load from snapshot at head {s:F0} ms, replay of the whole log from revision 0 {f:F0} ms; load {LoadAvg()}");
        }
        Console.WriteLine($"cold start of the hot graphs from SQLite, snapshot at head: {Spread(snap, "ms")}");
        Console.WriteLine($"cold start of the hot graphs from SQLite, replay of the whole log ({patches / runs:N0} patches, {edits / runs:N0} edits, {snapElems / runs:N0} elements at head): {Spread(follow, "ms")}");
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f);

        var rebuild = new List<double>();
        for (var run = 0; run < runs; run++)
        {
            using var rig = new Rig(cluster, _ => new InMemoryGraphStore(), 2000);
            var sw = Stopwatch.StartNew(); rig.Replay.Run(capture); rebuild.Add(sw.Elapsed.TotalSeconds);
            Console.WriteLine($"run {run + 1}: whole engine rebuilt from the inputs of the capture in {sw.Elapsed.TotalSeconds:F1} s; load {LoadAvg()}");
        }
        Console.WriteLine($"cold start of the whole engine by replaying the retained inputs (memory store): {Spread(rebuild, "s")}");
        return 0;
    }

    /// <summary>H10: the space is built from the newest capture repeated with shifted time until its logical span reaches `--hours` (default 1), journaled in a SQLite store; then the process is "restarted" `--runs` times: a fresh host replays the journal, recomputes every commit and compares it with the stored one.</summary>
    public static int Restart(Program.Options o)
    {
        var capture = Capture(o); var cluster = Program.DetectCluster(capture); var runs = int.TryParse(o.Get("runs"), out var rn) ? rn : Runs;
        var hours = double.TryParse(o.Get("hours"), NumberStyles.Float, CultureInfo.InvariantCulture, out var hh) ? hh : 1.0;
        var path = o.Get("db") ?? Path.Combine(Path.GetTempPath(), "sattle-b10-" + Guid.NewGuid().ToString("N") + ".db");
        var files = Catalog.ReadDirectory(FindDefs());
        var sealEvery = long.TryParse(o.Get("seal-every"), out var se) ? se : 0;
        var opts = new Sattle.Runtime.HostOptions { Cluster = cluster, SealEvery = sealEvery };
        var restartOpts = opts with { UseSeal = !o.Flags.Contains("no-seal") };
        var cosmos = o.Get("cosmos"); var dbName = "b10" + Guid.NewGuid().ToString("N")[..10];
        IGraphStore Open() => cosmos is null ? new SqliteGraphStore(path) : CosmosGraphStore.Open(cosmos, dbName, false);
        Console.WriteLine($"# B10 cold start by restart: capture {Path.GetFileName(capture.TrimEnd('/'))} repeated to {hours} h of logical time, {(cosmos is null ? "SQLite" : "Cosmos emulator")} journal and log, {runs} restarts");
        Environment();
        if (cosmos is not null || !File.Exists(path))
        {
            using var store = Open(); using var host = SpaceHost.OpenDurable(store, files, opts);
            var ro = Program.ReplayOptionsOf(Program.Options.Parse([]), cluster);
            var replay = new Replay(host, host.Space, host.Adapters, new ReplayOptions { ChunkRows = 2000, AdvanceEveryNs = ro.AdvanceEveryNs, LatenessNs = ro.LatenessNs, FlushAtEnd = true, Speed = 0, Cluster = cluster });
            long first = 0, last = 0, span = 0; var wall = Stopwatch.StartNew();
            for (var pass = 0; ; pass++)
            {
                var offset = pass == 0 ? 0 : pass * ((span + 120_000_000_000) / Min + 1) * Min;
                long pf = 0, pl = 0;
                foreach (var env in CaptureReader.Read(capture))
                {
                    if (pf == 0) pf = env.CapturedNs; pl = env.CapturedNs;
                    // A later pass is later history only for telemetry: its samples are replayed with their event times moved forward by the offset, like the capture clock.
                    // Structure (Kubernetes, Resource Graph, authoring) and evidence (Activity Log, audit, the Event Hubs copies) are loaded once: the same rows again would be the same objects and events a second time, not history.
                    if (pass > 0 && env.Source is not ("hubble" or "prometheus" or "azure-metrics" or "vnet-flow-log")) continue;
                    replay.TimeShiftNs = offset;
                    replay.Process(env with { CapturedNs = env.CapturedNs + offset, Seq = env.Seq + pass * 1_000_000_000L }, false);
                }
                replay.FlushPending();
                if (pass == 0) { first = pf; last = pl; span = last - first; }
                var logical = (pl + offset - first) / 1e9;
                Console.WriteLine($"pass {pass + 1}: logical span {logical / 3600:F2} h, {host.InputRows:N0} rows, journal {((IJournalStore)store).JournalCount:N0} entries, late rows dropped {host.Engine.Graphs.Sum(g => g.Cx.LateDropped):N0} (rows older than the late horizon inside their own pass are dropped in every pass alike), master {host.Engine.Master.State.EntityCount:N0} entities and {host.Engine.Master.State.RelCount:N0} relationships, of them evidence kept by `retain` ({string.Join(", ", host.Engine.Master.State.Entities.Where(x => Sattle.Core.Ids.Name(x.Type) is "ActivityEvent" or "WarningEvent" or "AuditEvent").GroupBy(x => Sattle.Core.Ids.Name(x.Type)).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count():N0}"))}; they age out as the passes move the frontier), wall {wall.Elapsed.TotalSeconds:F0} s");
                if (logical >= hours * 3600) break;
            }
            host.Finish();
            if (sealEvery > 0) Console.WriteLine($"seals: {host.SealsTaken} taken, {host.SealMilliseconds / Math.Max(1, host.SealsTaken)} ms each on average, the last {host.LastSealBytes / 1e6:F1} MB (compressed)");
            var revs = host.Engine.Graphs.Sum(g => g.Head);
            Console.WriteLine($"built: {revs:N0} revisions in {host.Engine.Graphs.Count} graphs, journal {((IJournalStore)store).JournalCount:N0} entries, state digest {host.StateDigest()}{(cosmos is null ? $", store file {new FileInfo(path).Length / 1e6:F0} MB" : "")}");
        }
        var secs = new List<double>(); string? digest = null;
        for (var run = 0; run < runs; run++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers();
            var sw = Stopwatch.StartNew();
            using var store = Open();
            using var host = SpaceHost.OpenDurable(store, null, restartOpts);
            var t = sw.Elapsed.TotalSeconds; secs.Add(t);
            digest ??= host.StateDigest(); if (digest != host.StateDigest()) throw new InvalidOperationException("restart digests differ");
            Console.WriteLine($"restart {run + 1}: {t:F1} s ({(host.RestoredFromSeal ? $"seal at entry {host.SealPosition}, {host.TailReplayed:N0} entries replayed after it" : "journal replayed from the start")}{(host.RestoredFromSeal ? $", state loaded in {host.SealLoadMilliseconds} ms, tail replayed in {host.TailMilliseconds} ms" : "")}), {host.Recomputed:N0} revisions recomputed and compared with the stored log ({host.SealedRevisions:N0} came from the seal), sequence {host.Seq}, digest {host.StateDigest()[..19]}; load {LoadAvg()}");
        }
        Console.WriteLine($"H10 restart from the store (journal replay with verification of every commit), history of {hours} h: {Spread(secs, "s")}  (target at most 60 s)");
        if (cosmos is not null) { using var drop = CosmosGraphStore.Open(cosmos, dbName, true); }
        if (o.Get("db") is null && cosmos is null) foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f);
        return 0;
    }
}
