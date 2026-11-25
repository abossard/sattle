using System.Diagnostics;
using System.Globalization;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Cli;

/// <summary>The benchmarks of the engine on captures: the phases of a replay, latency from source to graph, durable reads, the element view, cold start and restart. Stopwatch and GC counters, no benchmarking library: the method is in PERF.md.</summary>
public static partial class Bench
{
    const long Min = 60_000_000_000;

    public static void GraphTable(Engine e, TextWriter w)
    {
        w.WriteLine("graph | entities | relationships | head revision | windows");
        foreach (var g in e.Graphs) w.WriteLine($"{g.Name} | {g.State.EntityCount:N0} | {g.State.RelCount:N0} | {g.Head:N0} | {e.Windows.ScanGraph(g.Key, null):N0}");
    }
    public static void GcAndCensus(Engine e, TextWriter w, params (string Label, object? Root)[] extra)
    {
        var proc = Process.GetCurrentProcess();
        w.WriteLine($"gc: allocated {GC.GetTotalAllocatedBytes() / 1e9:F2} GB in total, pause {GC.GetTotalPauseDuration().TotalSeconds:F1} s, process cpu {proc.TotalProcessorTime.TotalSeconds:F0} s, collections gen0 {GC.CollectionCount(0):N0} gen1 {GC.CollectionCount(1):N0} gen2 {GC.CollectionCount(2):N0}");
        GraphTable(e, w);
        Census.Run("replay", Census.EngineRoots(e, extra), e.Master.State.EntityCount, e.Master.State.RelCount, w);
    }
    public static void KindTable(Engine e, TextWriter w)
    {
        w.WriteLine("operator kind | calls | ms | MB allocated");
        foreach (var (k, v) in e.KindStats.OrderByDescending(x => x.Value.Ms).Take(14)) w.WriteLine($"{k} | {v.Calls:N0} | {v.Ms:F0} | {v.Bytes / 1e6:F0}");
    }
    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    static double Pct(List<double> xs, double p) { var s = xs.Order().ToList(); return s.Count == 0 ? 0 : s[Math.Min(s.Count - 1, (int)Math.Ceiling(p * s.Count) - 1)]; }

    public static string FindDefs()
    {
        for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d is not null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "engine", "definitions"); if (Directory.Exists(c)) return c;
            c = Path.Combine(d.FullName, "definitions"); if (Directory.Exists(c)) return c;
        }
        throw new InvalidOperationException("definitions not found");
    }

    public static int Run(Program.Options o, Extensions ext)
    {
        if (o.Positional.Contains("phases")) return Phases(o);
        if (o.Positional.Contains("b5")) return SourceToGraph(o);
        if (o.Positional.Contains("b7")) return DurableReads(o, ext);
        if (o.Positional.Contains("b8")) return ElementView(o);
        if (o.Positional.Contains("b10-graphs")) return ColdStart(o);
        if (o.Positional.Contains("b10")) return Restart(o, ext);
        throw new ArgumentException("bench needs one of phases, b5, b7, b8, b10-graphs, b10");
    }
}
