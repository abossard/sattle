using System.Collections.Immutable;
using Sattle.Reference;

namespace Sattle.Differential;

/// <summary>Where the engine and the reference differ: the chunking, the input after which they differ, the graph, and the facts only one of them has.</summary>
public sealed record Mismatch(string Chunking, int AfterInput, string Graph, ImmutableArray<string> OnlyReference, ImmutableArray<string> OnlyEngine)
{
    public override string ToString() =>
        $"chunking: {Chunking}\nafter input {AfterInput - 1}, graph {Graph}\n  reference only:\n{Indent(OnlyReference)}\n  engine only:\n{Indent(OnlyEngine)}";
    static string Indent(ImmutableArray<string> lines) => lines.Length == 0 ? "    (nothing)" : string.Join("\n", lines.Select(l => "    " + l));
}

/// <summary>Theorem 6.1 and Theorem 6.2 as a check: after every batch of every chunking, the engine's graphs and telemetry equal the reference's batch result on the inputs consumed so far.</summary>
public static class Differential
{
    /// <summary>Every way the log is cut: one row per batch, the largest batches, random cuts and a cut at every position. With `reordered`, the largest batches with their rows in reverse order instead (a retraction can then precede its insertion inside a batch).</summary>
    public static IEnumerable<Chunking> Chunkings(int inputs, int seed, bool reordered) => reordered
        ? [Chunking.Reversed]
        : [Chunking.OneByOne, Chunking.Largest, Chunking.Random(seed), .. Enumerable.Range(0, Math.Max(inputs - 1, 0)).Select(Chunking.SplitAt)];

    public static Mismatch? FirstMismatch(World world, IReadOnlyList<Input> log, int seed, bool reordered)
    {
        var reference = ReferenceEvaluator.Prefixes(world.Catalog, world.Catalog.Spaces.Keys.Single(), log, new Settings(TelemetryRetentionNs: world.RetentionNs)).ToArray();
        foreach (var chunking in Chunkings(log.Count, seed, reordered))
        {
            try
            {
                foreach (var (consumed, engine) in EngineRun.States(world.Catalog, log, chunking, world.RetentionNs))
                    foreach (var (graph, expected) in reference[consumed].Graphs)
                    {
                        // the elements and windows, and the conflicts that hold (Def. 6.4): both are part of what a graph is after an input
                        static ImmutableArray<string> All(GraphFacts g) => [.. g.Lines(), .. g.Conflicts.Select(c => "conflict " + c)];
                        var (want, got) = (All(expected), All(engine.Graphs[graph]));
                        if (!want.SequenceEqual(got)) return new Mismatch(chunking.Name, consumed, graph, [.. want.Except(got)], [.. got.Except(want)]);
                    }
            }
            catch (Exception e) when (e is not Undefined)
            {
                return new Mismatch(chunking.Name, log.Count, "(engine threw)", [], [e.GetType().Name + ": " + e.Message.Split('\n')[0]]);
            }
        }
        return null;
    }

    /// <summary>A smaller log with a mismatch: inputs are removed one at a time while the log stays well formed and still differs.</summary>
    public static ImmutableArray<Input> Shrink(World world, ImmutableArray<Input> log, int seed, bool reordered)
    {
        for (var removed = true; removed;)
        {
            removed = false;
            for (var i = log.Length - 1; i >= 0; i--)
            {
                var candidate = log.RemoveAt(i);
                if (!Logs.IsWellFormed(world.Sources, candidate) || FirstMismatch(world, candidate, seed, reordered) is null) continue;
                log = candidate; removed = true;
            }
        }
        return log;
    }

    /// <summary>Null when every seed agrees; otherwise the report of the first seed that differs, with its smallest log.</summary>
    public static string? Check(World world, int seeds, int length, bool reordered)
    {
        for (var seed = 0; seed < seeds; seed++)
        {
            var log = Logs.Generate(world.Sources, world.AdvanceShare, seed, length);
            if (FirstMismatch(world, log, seed, reordered) is null) continue;
            var small = Shrink(world, log, seed, reordered);
            return $"world '{world.Name}' ({world.Kind}), seed {seed}, {log.Length} inputs shrunk to {small.Length}:\n{Logs.Print(small)}\n{FirstMismatch(world, small, seed, reordered)}";
        }
        return null;
    }
}
