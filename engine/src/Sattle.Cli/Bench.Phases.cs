using System.Diagnostics;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Cli;

static partial class Bench
{
    /// <summary>The engine behind a stopwatch and an allocation counter, so the time of a replay splits into reading and parsing the capture, the adapters (rows from records), and the engine (ingest and window advance).</summary>
    sealed class TimedInput(Engine e) : IEngineInput
    {
        public long Ticks, Alloc, Calls, IngestRows, Cpu; public long AdvanceTicks, AdvanceAlloc, Advances, AdvanceCpu;
        public Interner Interner => e.Interner;
        public Layout RowLayout(string source) => e.RowLayout(source);
        public bool WantsProgress => e.WantsProgress;
        public void Progress(string source, long untilNs, long seq) => e.Progress(source, untilNs, seq);
        public void Ingest(string source, Batch rows, long seq)
        {
            var a = GC.GetAllocatedBytesForCurrentThread(); var t = Stopwatch.GetTimestamp(); var c = ThreadCpuNs();
            e.Ingest(source, rows, seq);
            Ticks += Stopwatch.GetTimestamp() - t; Alloc += GC.GetAllocatedBytesForCurrentThread() - a; Cpu += ThreadCpuNs() - c; Calls++; IngestRows += rows.Rows;
        }
        public void Advance(long lambdaNs, long seq)
        {
            var a = GC.GetAllocatedBytesForCurrentThread(); var t = Stopwatch.GetTimestamp(); var c = ThreadCpuNs();
            e.Advance(lambdaNs, seq);
            AdvanceTicks += Stopwatch.GetTimestamp() - t; AdvanceAlloc += GC.GetAllocatedBytesForCurrentThread() - a; AdvanceCpu += ThreadCpuNs() - c; Advances++;
        }
        public void RecordLoss(long timeNs, long count, string reason = "sourceLoss") => e.RecordLoss(timeNs, count, reason);
    }

    /// <summary>`sattle bench phases [--capture DIR] [--overlay FILE] [--export] [--census]`: where the time and the allocations of a capture replay go, by phase (wall and this thread's CPU), the peak resident set, and with `--census` the bytes retained per structure and per master node (pods, Services and Azure resources).</summary>
    public static int Phases(Program.Options o)
    {
        var capture = Capture(o); var cluster = Program.DetectCluster(capture);
        var cat = new Catalog(Program.Projections); var setup = Stopwatch.StartNew();
        var load = cat.Load(Program.ReadDefinitions(o), updatePins: true); var loadMs = setup.Elapsed.TotalMilliseconds;
        if (load.HasErrors) throw new InvalidOperationException(string.Join("\n", load.Diagnostics));
        var spaceId = cat.Spaces.Keys.First();
        var adapters = AdapterRegistry.Create(cluster);
        var engine = new Engine(cat, spaceId, new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 });
        Console.WriteLine($"setup: catalog load (parse, check, lower, type every module) {loadMs:F0} ms, engine construction (plans for {engine.Graphs.Count} graphs) {setup.Elapsed.TotalMilliseconds - loadMs:F0} ms");
        engine.TrackAllocations = true;
        if (o.Flags.Contains("export")) new CanvasExport(engine, cat).Attach();
        var input = new TimedInput(engine);
        var replay = new Replay(input, cat.Spaces[spaceId], adapters, Program.ReplayOptionsOf(o, cluster));
        long readTicks = 0, readAlloc = 0, procTicks = 0, procAlloc = 0, records = 0, readCpu = 0, procCpu = 0;
        var wall = Stopwatch.StartNew();
        GC.Collect(); var allocStart = GC.GetAllocatedBytesForCurrentThread(); var pauseStart = GC.GetTotalPauseDuration();
        var proc = Process.GetCurrentProcess(); var cpuStart = proc.TotalProcessorTime; var threadStart = ThreadCpuNs();
        using (var en = CaptureReader.Read(capture).GetEnumerator())
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
        var t1 = Stopwatch.GetTimestamp(); var cf = ThreadCpuNs(); var af = GC.GetAllocatedBytesForCurrentThread();
        replay.FlushPending(); input.Advance(replay.LogicalTime + 2 * Min, 999_999_999);
        procTicks += Stopwatch.GetTimestamp() - t1; procCpu += ThreadCpuNs() - cf; procAlloc += GC.GetAllocatedBytesForCurrentThread() - af;
        var total = wall.Elapsed; var allocTotal = GC.GetAllocatedBytesForCurrentThread() - allocStart; var pause = GC.GetTotalPauseDuration() - pauseStart;
        var engineTicks = input.Ticks + input.AdvanceTicks; var adapterTicks = procTicks - engineTicks;
        Console.WriteLine($"# phases of the replay of {Path.GetFileName(capture.TrimEnd('/'))}: {records:N0} records, {input.IngestRows:N0} rows in {input.Calls:N0} ingests, {input.Advances:N0} advances, canvas export {(o.Flags.Contains("export") ? "attached" : "not attached")}; load average at start {LoadAverage()}");
        Console.WriteLine("phase | wall seconds | share | thread CPU seconds | allocated MB | bytes per record | bytes per row");
        void Row(string n, double s, long cpuNs, long alloc) => Console.WriteLine($"{n} | {s:F2} | {100 * s / total.TotalSeconds:F0} % | {cpuNs / 1e9:F2} | {alloc / 1e6:F0} | {alloc / (double)Math.Max(1, records):F0} | {alloc / (double)Math.Max(1, input.IngestRows):F0}");
        Row("read and parse the capture (JSON lines to envelopes)", Ms(readTicks) / 1000, readCpu, readAlloc);
        Row("adapters (records to rows, chunking), without the engine", Ms(adapterTicks) / 1000, procCpu - input.Cpu - input.AdvanceCpu, procAlloc - input.Alloc - input.AdvanceAlloc);
        Row("engine ingest (operators, sinks, commit)", Ms(input.Ticks) / 1000, input.Cpu, input.Alloc);
        Row("engine advance (window closes, derived commits)", Ms(input.AdvanceTicks) / 1000, input.AdvanceCpu, input.AdvanceAlloc);
        proc.Refresh();
        Console.WriteLine($"cpu: this thread {(ThreadCpuNs() - threadStart) / 1e9:F1} s, the process {(proc.TotalProcessorTime - cpuStart).TotalSeconds:F1} s (the rest is the collector and the runtime's other threads); peak resident set {PeakResidentBytes() / 1e6:F0} MB");
        Console.WriteLine($"total wall {total.TotalSeconds:F2} s, allocated {allocTotal / 1e6:F0} MB ({allocTotal / (double)Math.Max(1, records):F0} bytes per record, {allocTotal / (double)Math.Max(1, input.IngestRows):F0} per row), GC pause {pause.TotalSeconds:F2} s ({100 * pause.TotalSeconds / total.TotalSeconds:F0} %), unexplained {total.TotalSeconds - (Ms(readTicks) + Ms(procTicks)) / 1000:F2} s");
        Console.WriteLine("operator kind or part of a step | calls | ms | MB allocated");
        foreach (var (k, v) in engine.KindStats.OrderByDescending(x => x.Value.Bytes).Take(24)) Console.WriteLine($"{k} | {v.Calls:N0} | {v.Ms:F0} | {v.Bytes / 1e6:F0}");
        Console.WriteLine("per graph (operators and commit), ms:");
        foreach (var gname in engine.Graphs.Select(g => g.Name))
            Console.WriteLine($"{gname} | operators {engine.OperatorMillis.Where(x => x.Key.StartsWith(gname + "/", StringComparison.Ordinal) && !x.Key.EndsWith("(commit)", StringComparison.Ordinal)).Sum(x => x.Value):F0} | commit {engine.OperatorMillis.GetValueOrDefault(gname + "/(commit)"):F0}");
        if (o.Flags.Contains("census"))
        {
            // the unit of the budgets: master nodes are the pods, Services and Azure resources of the master
            var structure = engine.Master.State.Entities.LongCount(x => Ids.Name(x.Type) is "Pod" or "Service" or "AzureResource");
            GraphTable(engine, Console.Out);
            Census.Run($"after the replay, per master node = per pod, Service or Azure resource ({structure:N0})", Census.EngineRoots(engine), structure, engine.Master.State.RelCount, Console.Out);
        }
        engine.Dispose();
        return 0;
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
        try { return File.ReadAllText("/proc/loadavg"); } catch { }
        try { var psi = new ProcessStartInfo("sysctl", "-n vm.loadavg") { RedirectStandardOutput = true }; using var p = Process.Start(psi)!; var s = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(); return s; } catch { return "n/a"; }
    }
}
