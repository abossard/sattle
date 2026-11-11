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
        public long Ticks, Alloc, Calls, IngestRows; public long AdvanceTicks, AdvanceAlloc, Advances;
        public Interner Interner => e.Interner;
        public Layout RowLayout(string source) => e.RowLayout(source);
        public void Ingest(string source, Batch rows, long seq)
        {
            var a = GC.GetAllocatedBytesForCurrentThread(); var t = Stopwatch.GetTimestamp();
            e.Ingest(source, rows, seq);
            Ticks += Stopwatch.GetTimestamp() - t; Alloc += GC.GetAllocatedBytesForCurrentThread() - a; Calls++; IngestRows += rows.Rows;
        }
        public void Advance(long lambdaNs, long seq)
        {
            var a = GC.GetAllocatedBytesForCurrentThread(); var t = Stopwatch.GetTimestamp();
            e.Advance(lambdaNs, seq);
            AdvanceTicks += Stopwatch.GetTimestamp() - t; AdvanceAlloc += GC.GetAllocatedBytesForCurrentThread() - a; Advances++;
        }
        public void RecordLoss(long timeNs, long count, string reason = "sourceLoss") => e.RecordLoss(timeNs, count, reason);
    }

    /// <summary>`sattle bench phases [--capture DIR] [--overlay FILE] [--export]`: where the time and the allocations of a capture replay go, by phase.</summary>
    public static int Phases(Program.Options o)
    {
        var capture = Capture(o); var cluster = Program.DetectCluster(capture);
        var cat = new Catalog(); var setup = Stopwatch.StartNew();
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
        long readTicks = 0, readAlloc = 0, procTicks = 0, procAlloc = 0, records = 0;
        var wall = Stopwatch.StartNew();
        GC.Collect(); var allocStart = GC.GetAllocatedBytesForCurrentThread(); var pauseStart = GC.GetTotalPauseDuration();
        using (var en = CaptureReader.Read(capture).GetEnumerator())
            while (true)
            {
                var a = GC.GetAllocatedBytesForCurrentThread(); var t = Stopwatch.GetTimestamp();
                if (!en.MoveNext()) break;
                readTicks += Stopwatch.GetTimestamp() - t; readAlloc += GC.GetAllocatedBytesForCurrentThread() - a;
                a = GC.GetAllocatedBytesForCurrentThread(); t = Stopwatch.GetTimestamp();
                replay.Process(en.Current, false); records++;
                procTicks += Stopwatch.GetTimestamp() - t; procAlloc += GC.GetAllocatedBytesForCurrentThread() - a;
            }
        var t1 = Stopwatch.GetTimestamp(); replay.FlushPending(); input.Advance(replay.LogicalTime + 2 * Min, 999_999_999); procTicks += Stopwatch.GetTimestamp() - t1;
        var total = wall.Elapsed; var allocTotal = GC.GetAllocatedBytesForCurrentThread() - allocStart; var pause = GC.GetTotalPauseDuration() - pauseStart;
        var engineTicks = input.Ticks + input.AdvanceTicks; var adapterTicks = procTicks - engineTicks;
        Console.WriteLine($"# phases of the replay of {Path.GetFileName(capture.TrimEnd('/'))}: {records:N0} records, {input.IngestRows:N0} rows in {input.Calls:N0} ingests, {input.Advances:N0} advances, canvas export {(o.Flags.Contains("export") ? "attached" : "not attached")}; load average at start {LoadAverage()}");
        Console.WriteLine("phase | seconds | share | allocated MB | bytes per record");
        void Row(string n, double s, long alloc) => Console.WriteLine($"{n} | {s:F2} | {100 * s / total.TotalSeconds:F0} % | {alloc / 1e6:F0} | {alloc / (double)Math.Max(1, records):F0}");
        Row("read and parse the capture (JSON lines to envelopes)", Ms(readTicks) / 1000, readAlloc);
        Row("adapters (records to rows, chunking), without the engine", Ms(adapterTicks) / 1000, procAlloc - input.Alloc - input.AdvanceAlloc);
        Row("engine ingest (operators, sinks, commit)", Ms(input.Ticks) / 1000, input.Alloc);
        Row("engine advance (window closes, derived commits)", Ms(input.AdvanceTicks) / 1000, input.AdvanceAlloc);
        Console.WriteLine($"total wall {total.TotalSeconds:F2} s, allocated {allocTotal / 1e6:F0} MB ({allocTotal / (double)Math.Max(1, records):F0} bytes per record, {allocTotal / (double)Math.Max(1, input.IngestRows):F0} per row), GC pause {pause.TotalSeconds:F2} s ({100 * pause.TotalSeconds / total.TotalSeconds:F0} %), unexplained {total.TotalSeconds - (Ms(readTicks) + Ms(procTicks)) / 1000:F2} s");
        Console.WriteLine("operator kind | calls | ms | MB allocated");
        foreach (var (k, v) in engine.KindStats.OrderByDescending(x => x.Value.Ms).Take(16)) Console.WriteLine($"{k} | {v.Calls:N0} | {v.Ms:F0} | {v.Bytes / 1e6:F0}");
        Console.WriteLine("per graph (operators and commit), ms:");
        foreach (var gname in engine.Graphs.Select(g => g.Name))
            Console.WriteLine($"{gname} | operators {engine.OperatorMillis.Where(x => x.Key.StartsWith(gname + "/", StringComparison.Ordinal) && !x.Key.EndsWith("(commit)", StringComparison.Ordinal)).Sum(x => x.Value):F0} | commit {engine.OperatorMillis.GetValueOrDefault(gname + "/(commit)"):F0}");
        engine.Dispose();
        return 0;
    }

    static string LoadAverage()
    {
        try { return File.ReadAllText("/proc/loadavg"); } catch { }
        try { var psi = new ProcessStartInfo("sysctl", "-n vm.loadavg") { RedirectStandardOutput = true }; using var p = Process.Start(psi)!; var s = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(); return s; } catch { return "n/a"; }
    }
}
