using System.Diagnostics;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Cli;

static partial class Bench
{
    /// <summary>The engine behind a stopwatch and an allocation counter, so the time of a replay splits into reading and parsing the capture, the adapters (rows from records), and the engine (ingest and window advance).</summary>
    sealed class TimedInput(Engine e, Process process) : IEngineInput
    {
        public long Ticks, Alloc, Calls, IngestRows, Cpu; public long AdvanceTicks, AdvanceAlloc, Advances, AdvanceCpu;
        public Interner Interner => e.Interner;
        public Layout RowLayout(string source) => e.RowLayout(source);
        public bool WantsProgress => e.WantsProgress;
        /// <summary>A window close: the progress the sources made since the last advance, then the advance (`Replay.RecordProgress`, `Engine.Advance`). Both end in committed graphs, so each close is timed from its first progress record to the end of its advance.</summary>
        public readonly List<Close> Closes = [];
        /// <summary>Operator measurements during the source progress records and advances that close windows.</summary>
        public readonly Dictionary<string, (double Ms, long Bytes, long Calls)> AdvanceKinds = [];
        long startTicks, startCpu, startAlloc, startTotalAlloc, startPreparedRanges, startWorkerRanges, changes;
        TimeSpan startProcessCpu, startPause;
        bool closing, committed;
        Dictionary<string, double>? graphMs;
        Dictionary<string, (double Ms, long Bytes, long Calls)>? kinds;

        void Count(CommitEvent ce) { committed = true; changes += ce.Telemetry.Count; }

        // Replay sends all source progress records immediately before their advance.
        void BeginClose()
        {
            if (closing) return;
            closing = true;
            if (e.TrackAllocations)
            {
                graphMs = e.Graphs.ToDictionary(g => g.Name, g => GraphMillis(e, g.Name));
                kinds = new(e.KindStats);
            }
            changes = 0;
            committed = false;
            e.Committed += Count;
            startPreparedRanges = e.PreparedRanges;
            startWorkerRanges = e.WorkerPreparedRanges;
            process.Refresh();
            startProcessCpu = process.TotalProcessorTime;
            startTotalAlloc = GC.GetTotalAllocatedBytes(precise: true);
            startPause = GC.GetTotalPauseDuration();
            startAlloc = GC.GetAllocatedBytesForCurrentThread();
            startCpu = ThreadCpuNs();
            startTicks = Stopwatch.GetTimestamp();
        }

        public void Progress(string source, long untilNs, long seq)
        {
            BeginClose();
            e.Progress(source, untilNs, seq);
        }
        public void Ingest(string source, Batch rows, long seq)
        {
            Calls++; IngestRows += rows.Rows;
            if (!e.TrackAllocations) { e.Ingest(source, rows, seq); return; }
            var a = GC.GetAllocatedBytesForCurrentThread(); var t = Stopwatch.GetTimestamp(); var c = ThreadCpuNs();
            e.Ingest(source, rows, seq);
            Ticks += Stopwatch.GetTimestamp() - t; Alloc += GC.GetAllocatedBytesForCurrentThread() - a; Cpu += ThreadCpuNs() - c;
        }
        public void Advance(long lambdaNs, long seq) => Advance(lambdaNs, seq, endOfCapture: false);

        public void Advance(long lambdaNs, long seq, bool endOfCapture)
        {
            BeginClose();
            e.Advance(lambdaNs, seq);
            long dt = Stopwatch.GetTimestamp() - startTicks, dc = ThreadCpuNs() - startCpu, da = GC.GetAllocatedBytesForCurrentThread() - startAlloc;
            var pause = GC.GetTotalPauseDuration() - startPause;
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - startTotalAlloc;
            process.Refresh();
            var processCpu = process.TotalProcessorTime - startProcessCpu;
            e.Committed -= Count;
            closing = false;
            AdvanceTicks += dt; AdvanceAlloc += da; AdvanceCpu += dc; Advances++;
            var series = e.Telemetry is InMemoryTelemetryStore mem ? e.Graphs.Sum(g => (long)mem.SeriesCount(g.Key)) : 0;
            var top = graphMs is null ? [] : e.Graphs.Select(g => (g.Name, Ms: GraphMillis(e, g.Name) - graphMs[g.Name])).OrderByDescending(x => x.Ms).Take(4).ToList();
            double? matchMs = kinds is null ? null : e.KindStats.GetValueOrDefault("MatchOp").Ms - kinds.GetValueOrDefault("MatchOp").Ms;
            Closes.Add(new(lambdaNs, Ms(dt), dc / 1e6, processCpu.TotalMilliseconds, allocated / 1e6, pause.TotalMilliseconds, changes, series,
                e.PreparedRanges - startPreparedRanges, e.WorkerPreparedRanges - startWorkerRanges, endOfCapture && !committed, matchMs, top));
            if (kinds is null) return;
            foreach (var (k, v) in e.KindStats)
            {
                var b = kinds.GetValueOrDefault(k); var s = AdvanceKinds.GetValueOrDefault(k);
                AdvanceKinds[k] = (s.Ms + v.Ms - b.Ms, s.Bytes + v.Bytes - b.Bytes, s.Calls + v.Calls - b.Calls);
            }
        }
        public void RecordLoss(long timeNs, long count, string reason = "sourceLoss") => e.RecordLoss(timeNs, count, reason);
        static double GraphMillis(Engine e, string graph) => e.OperatorMillis.Where(x => x.Key.StartsWith(graph + "/", StringComparison.Ordinal)).Sum(x => x.Value);
    }
    /// <summary>One close includes the progress records that precede its advance and the resulting commits.</summary>
    sealed record Close(long Lambda, double Ms, double CpuMs, double ProcessCpuMs, double AllocMb, double PauseMs, long WindowChanges, long Series, long PreparedRanges, long WorkerRanges, bool EmptyFinalAdvance, double? MatchMs, List<(string Name, double Ms)> Top);

    static void PrintCloses(List<Close> closes, TextWriter w)
    {
        w.WriteLine("window close (progress records and advance, to committed graphs) | frontier | wall ms | writer CPU ms | process CPU ms | allocated MB (all threads) | GC pause ms | telemetry window changes | series held | prepared ranges (total / off writer) | longest graphs, ms");
        for (var i = 0; i < closes.Count; i++)
        {
            var c = closes[i];
            w.WriteLine($"close {i} | {DateTimeOffset.FromUnixTimeMilliseconds(c.Lambda / 1_000_000):HH:mm:ss} | {c.Ms:F0} | {c.CpuMs:F0} | {c.ProcessCpuMs:F0} | {c.AllocMb:F0} | {c.PauseMs:F0} | {c.WindowChanges:N0} | {c.Series:N0} | {c.PreparedRanges:N0} / {c.WorkerRanges:N0} | {string.Join(", ", c.Top.Select(x => $"{x.Name} {x.Ms:F0}"))}");
        }
        void Stats(string label, IEnumerable<Close> set)
        {
            var ms = set.Select(x => x.Ms).Order().ToArray();
            if (ms.Length == 0) { w.WriteLine($"window close {label}: 0 closes"); return; }
            double P(double q) => ms[Math.Min(ms.Length - 1, (int)Math.Ceiling(q * ms.Length) - 1)];      // nearest rank
            w.WriteLine($"window close {label}: {ms.Length} {(ms.Length == 1 ? "close" : "closes")}, p50 {P(0.5):F0} ms, p99 {P(0.99):F0} ms, max {ms[^1]:F0} ms; series held at the last {set.Last().Series:N0}");
        }
        Stats("all", closes);
        // Source-driven quiet windows still count. A synthetic final flush belongs to the sample only if it committed a change.
        var sample = closes.Where(c => !c.EmptyFinalAdvance).ToList();
        var first = sample.FindIndex(c => c.WindowChanges > 0 || c.Series > 0);
        Stats("before telemetry", sample.Take(first < 0 ? sample.Count : first));
        Stats("first populated", first < 0 ? [] : [sample[first]]);
        Stats("steady (after first populated)", first < 0 ? [] : sample.Skip(first + 1));
        if (sample.Count != closes.Count) Stats("empty end-of-capture advance", closes.Where(c => c.EmptyFinalAdvance));
        if (first >= 0 && sample[first].MatchMs is { } matchMs)
            w.WriteLine($"graph match operators during first populated close: {matchMs:F0} ms wall");
    }

    /// <summary>Times a replay and its window closes. With --timing-only, skip the per-record, per-ingest and per-operator counters. With --census, also measure what each structure retains.</summary>
    public static int Phases(Program.Options o)
    {
        var options = new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 };
        if (o.Get("graph-workers") is { } count)
        {
            if (!int.TryParse(count, out int workers) || workers < 1)
                throw new ArgumentException("--graph-workers requires a positive integer");
            options = options with { PreparationWorkers = workers };
        }
        var capture = Capture(o);
        var cat = new Catalog(Program.Projections); var setup = Stopwatch.StartNew();
        var load = cat.Load(Program.ReadDefinitions(o), updatePins: true); var loadMs = setup.Elapsed.TotalMilliseconds;
        if (load.HasErrors) throw new InvalidOperationException(string.Join("\n", load.Diagnostics));
        var spaceId = cat.Spaces.Keys.First();
        var adapters = AdapterRegistry.Create(cat.Spaces[spaceId], cat);
        var engine = new Engine(cat, spaceId, options);
        Console.WriteLine($"setup: catalog load (parse, check, lower, type every module) {loadMs:F0} ms, engine construction (plans for {engine.Graphs.Count} graphs) {setup.Elapsed.TotalMilliseconds - loadMs:F0} ms");
        var detailed = !o.Flags.Contains("timing-only");
        engine.TrackAllocations = detailed;
        if (o.Flags.Contains("export")) new CanvasExport(engine, cat).Attach();
        using var proc = Process.GetCurrentProcess();
        var input = new TimedInput(engine, proc);
        var replay = new Replay(input, cat.Spaces[spaceId], adapters, Program.ReplayOptionsOf(o));
        long readTicks = 0, readAlloc = 0, procTicks = 0, procAlloc = 0, records = 0, readCpu = 0, procCpu = 0;
        var loadAverage = LoadAverage();
        GC.Collect();
        var allocStart = GC.GetTotalAllocatedBytes(precise: true);
        var pauseStart = GC.GetTotalPauseDuration();
        var cpuStart = proc.TotalProcessorTime;
        var threadStart = ThreadCpuNs();
        var wall = Stopwatch.StartNew();
        if (!detailed)
        {
            foreach (var envelope in CaptureReader.Read(capture)) { replay.Process(envelope, false); records++; }
        }
        else
        {
            using var en = CaptureReader.Read(capture).GetEnumerator();
            while (true)
            {
                var a = GC.GetAllocatedBytesForCurrentThread(); var t = Stopwatch.GetTimestamp(); var c = ThreadCpuNs();
                if (!en.MoveNext()) break;
                var c1 = ThreadCpuNs();
                readTicks += Stopwatch.GetTimestamp() - t; readAlloc += GC.GetAllocatedBytesForCurrentThread() - a; readCpu += c1 - c;
                a = GC.GetAllocatedBytesForCurrentThread(); t = Stopwatch.GetTimestamp();
                replay.Process(en.Current, false); records++;
                procTicks += Stopwatch.GetTimestamp() - t; procAlloc += GC.GetAllocatedBytesForCurrentThread() - a; procCpu += ThreadCpuNs() - c1;
            }
        }
        var t1 = Stopwatch.GetTimestamp(); var cf = ThreadCpuNs(); var af = GC.GetAllocatedBytesForCurrentThread();
        replay.FlushPending(); input.Advance(replay.LogicalTime + 2 * Min, 999_999_999, endOfCapture: true);
        procTicks += Stopwatch.GetTimestamp() - t1; procCpu += ThreadCpuNs() - cf; procAlloc += GC.GetAllocatedBytesForCurrentThread() - af;
        var total = wall.Elapsed; var allocTotal = GC.GetTotalAllocatedBytes(precise: true) - allocStart; var pause = GC.GetTotalPauseDuration() - pauseStart;
        var writerCpu = ThreadCpuNs() - threadStart;
        proc.Refresh();
        var processCpu = proc.TotalProcessorTime - cpuStart;
        var peak = PeakResidentBytes();
        Console.WriteLine($"graph preparation: worker limit {options.PreparationWorkers}, ranges prepared {engine.PreparedRanges:N0}, off writer {engine.WorkerPreparedRanges:N0}, ranges replayed on writer {engine.ReplayedPreparationRanges:N0}");
        Console.WriteLine($"# phases of the replay of {Path.GetFileName(capture.TrimEnd('/'))}: {records:N0} records, {input.IngestRows:N0} rows in {input.Calls:N0} ingests, {input.Advances:N0} advances, canvas export {(o.Flags.Contains("export") ? "attached" : "not attached")}; detailed counters {(detailed ? "on" : "off")}; load average at start {loadAverage}");
        if (detailed)
        {
            var adapterTicks = procTicks - input.Ticks - input.AdvanceTicks;
            Console.WriteLine("phase | wall seconds | share | writer CPU seconds | allocated MB (writer) | bytes per record (writer) | bytes per row (writer)");
            void Row(string n, double s, long cpuNs, long alloc) => Console.WriteLine($"{n} | {s:F2} | {100 * s / total.TotalSeconds:F0} % | {cpuNs / 1e9:F2} | {alloc / 1e6:F0} | {alloc / (double)Math.Max(1, records):F0} | {alloc / (double)Math.Max(1, input.IngestRows):F0}");
            Row("read and parse the capture (JSON lines to envelopes)", Ms(readTicks) / 1000, readCpu, readAlloc);
            Row("adapters (records to rows, chunking), without the engine", Ms(adapterTicks) / 1000, procCpu - input.Cpu - input.AdvanceCpu, procAlloc - input.Alloc - input.AdvanceAlloc);
            Row("engine ingest (operators, sinks, commit)", Ms(input.Ticks) / 1000, input.Cpu, input.Alloc);
            Row("engine advance (progress, window closes, derived commits)", Ms(input.AdvanceTicks) / 1000, input.AdvanceCpu, input.AdvanceAlloc);
            Console.WriteLine($"unattributed wall {total.TotalSeconds - (Ms(readTicks) + Ms(procTicks)) / 1000:F2} s");
        }
        Console.WriteLine($"cpu: writer {writerCpu / 1e9:F2} s ({writerCpu / 1000.0 / Math.Max(1, records):F2} us/record), process {processCpu.TotalSeconds:F2} s ({processCpu.TotalMicroseconds / Math.Max(1, records):F2} us/record); peak resident set during replay {peak / 1e6:F0} MB");
        Console.WriteLine($"total wall {total.TotalSeconds:F2} s, allocated (all threads) {allocTotal / 1e6:F0} MB ({allocTotal / (double)Math.Max(1, records):F0} bytes per record, {allocTotal / (double)Math.Max(1, input.IngestRows):F0} per row), GC pause {pause.TotalSeconds:F2} s ({100 * pause.TotalSeconds / total.TotalSeconds:F0} %)");
        PrintCloses(input.Closes, Console.Out);
        if (detailed) PrintOperatorTimings(engine, input);
        string? digest = null;
        if (!detailed)
        {
            // Measure the end state before printing diagnostics for each graph.
            digest = engine.StateDigest();
            Console.WriteLine($"peak resident set through state digest {PeakResidentBytes() / 1e6:F0} MB");
            if (!o.Flags.Contains("census"))
                Console.WriteLine($"live managed bytes after forced compaction {Census.LiveBytes():N0}");
        }
        GraphTable(engine, Console.Out);
        Console.WriteLine($"state digest {digest ?? engine.StateDigest()}");
        if (o.Flags.Contains("census"))
        {
            // the unit of the budgets: master nodes are the pods, Services and Azure resources of the master
            var structure = engine.Master.State.Entities.LongCount(x => Ids.Name(x.Type) is "Pod" or "Service" or "AzureResource");
            Census.Run($"after the replay, per master node = per pod, Service or Azure resource ({structure:N0})", Census.EngineRoots(engine), structure, engine.Master.State.RelCount, Console.Out);
        }
        engine.Dispose();
        return 0;
    }

    static void PrintOperatorTimings(Engine engine, TimedInput input)
    {
        Console.WriteLine("in the progress records and advances: operator kind or part of a step | calls | ms | MB allocated (writer)");
        foreach (var (k, v) in input.AdvanceKinds.OrderByDescending(x => x.Value.Ms).Take(24)) Console.WriteLine($"{k} | {v.Calls:N0} | {v.Ms:F0} | {v.Bytes / 1e6:F0}");
        Console.WriteLine("operator kind or part of a step | calls | ms | MB allocated (writer)");
        foreach (var (k, v) in engine.KindStats.OrderByDescending(x => x.Value.Bytes).Take(24)) Console.WriteLine($"{k} | {v.Calls:N0} | {v.Ms:F0} | {v.Bytes / 1e6:F0}");
        Console.WriteLine("per graph (operators and commit), ms:");
        foreach (var gname in engine.Graphs.Select(g => g.Name))
            Console.WriteLine($"{gname} | operators {engine.OperatorMillis.Where(x => x.Key.StartsWith(gname + "/", StringComparison.Ordinal) && !x.Key.EndsWith("(commit)", StringComparison.Ordinal)).Sum(x => x.Value):F0} | commit {engine.OperatorMillis.GetValueOrDefault(gname + "/(commit)"):F0}");
    }

    [System.Runtime.InteropServices.DllImport("libc")] static extern int clock_gettime(int clock, [System.Runtime.InteropServices.Out] long[] ts);
    [System.Runtime.InteropServices.DllImport("libc")] static extern int getrusage(int who, [System.Runtime.InteropServices.Out] long[] usage);

    /// <summary>CPU time of the calling thread in ns (`CLOCK_THREAD_CPUTIME_ID`: 16 on macOS, 3 on Linux).</summary>
    [ThreadStatic] static long[]? timespec;
    static long ThreadCpuNs() { var ts = timespec ??= new long[2]; return clock_gettime(OperatingSystem.IsMacOS() ? 16 : 3, ts) == 0 ? ts[0] * 1_000_000_000 + ts[1] : 0; }

    /// <summary>The largest resident set of the process so far (`ru_maxrss`: bytes on macOS, KB on Linux).</summary>
    static long PeakResidentBytes()
    {
        var u = new long[18];          // two timevals, then ru_maxrss
        return getrusage(0, u) == 0 ? (OperatingSystem.IsMacOS() ? u[4] : u[4] * 1024) : 0;
    }

    static string LoadAverage()
    {
        try { return File.ReadAllText("/proc/loadavg").Trim(); } catch { }
        try
        {
            var info = new ProcessStartInfo("uptime") { RedirectStandardOutput = true };
            info.Environment["LC_ALL"] = "C";
            using var p = Process.Start(info)!;
            var line = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 ? line[(line.LastIndexOf(':') + 1)..].Trim() : "n/a";
        }
        catch { return "n/a"; }
    }
}
