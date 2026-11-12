using System.Collections.Immutable;
using Sattle.Core;
using Sattle.Language;
using Sattle.Reference;
using Sattle.Runtime;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Sattle.Differential;

/// <summary>How a log is cut into the engine's batches: the positions where a batch ends although the next row has the same source, and whether the rows of a batch are fed in reverse order. Rows of different sources, advances and progress reports always end a batch.</summary>
public sealed record Chunking(string Name, Func<int, bool> SplitAfter, bool Reverse = false)
{
    public static readonly Chunking OneByOne = new("one row per batch", _ => true);
    public static readonly Chunking Largest = new("largest batches", _ => false);
    public static readonly Chunking Reversed = new("largest batches, rows reversed inside each batch", _ => false, true);
    public static Chunking SplitAt(int position) => new($"largest batches, split after input {position}", i => i == position);
    public static Chunking Random(int seed) { var cuts = new Random(seed); var set = Enumerable.Range(0, 4096).Where(_ => cuts.Next(3) == 0).ToHashSet(); return new($"random batches (seed {seed})", set.Contains); }
    public override string ToString() => Name;
}

/// <summary>Runs the engine on a log and reads its state back as facts. Actions only: the calculations are in Sattle.Reference.</summary>
public static class EngineRun
{
    public static Catalog Load(IEnumerable<SourceFile> files)
    {
        var catalog = new Catalog();
        var result = catalog.Load(files, updatePins: true);
        var errors = result.Diagnostics.Where(d => d.IsError).Select(d => d.ToString()).ToArray();
        return errors.Length == 0 ? catalog : throw new InvalidOperationException("definitions do not check:\n" + string.Join("\n", errors));
    }

    /// <summary>The facts of every graph after the whole log.</summary>
    public static SpaceFacts Final(Catalog catalog, IReadOnlyList<Input> log, Chunking chunking) => States(catalog, log, chunking).Last().Facts;

    /// <summary>The facts after each batch, with the number of inputs consumed so far.</summary>
    public static IEnumerable<(int Consumed, SpaceFacts Facts)> States(Catalog catalog, IReadOnlyList<Input> log, Chunking chunking, long? retentionNs = null)
    {
        using var engine = new Engine(catalog, catalog.Spaces.Keys.Single(), retentionNs is { } age ? new EngineOptions { TelemetryRetentionNs = age } : null);
        yield return (0, FactsOf(engine));
        long seq = 0;
        foreach (var batch in Batches(log, chunking))
        {
            if (batch[0].Input is AdvanceInput a) engine.Advance(a.Frontier, ++seq);
            else if (batch[0].Input is ProgressInput p) engine.Progress(p.Source, p.Until, ++seq);
            else Ingest(engine, [.. (chunking.Reverse ? batch.Reverse() : batch).Select(x => (RowInput)x.Input)], ++seq);
            yield return (batch[^1].Index + 1, FactsOf(engine));
        }
    }

    static IEnumerable<ImmutableArray<(int Index, Input Input)>> Batches(IReadOnlyList<Input> log, Chunking chunking)
    {
        var current = ImmutableArray.CreateBuilder<(int, Input)>();
        for (var i = 0; i < log.Count; i++)
        {
            current.Add((i, log[i]));
            var ends = i == log.Count - 1 || log[i] is not RowInput || log[i + 1] is not RowInput || ((RowInput)log[i]).Source != ((RowInput)log[i + 1]).Source || chunking.SplitAfter(i);
            if (ends) { yield return current.ToImmutable(); current.Clear(); }
        }
    }

    public static void Ingest(Engine engine, ImmutableArray<RowInput> rows, long seq)
    {
        var source = rows[0].Source;
        var port = engine.Master.PortSources.First(p => p.Value == source).Key;
        var columns = engine.Master.Typed.Port(port).Rows!.Cols;
        var writer = new RowWriter(engine.RowLayout(source), engine.Interner);
        foreach (var row in rows)
        {
            writer.Begin(row.Weight);
            for (var c = 0; c < columns.Count; c++)
                switch (row.Fields.GetValueOrDefault(columns[c].Name, Value.Null))
                {
                    case NullValue: writer.Absent(c); break;
                    case TextValue t: writer.Text(c, t.S); break;
                    case IntegerValue n: writer.Int(c, (long)n.N); break;
                    case DecimalValue d: writer.Dec(c, d.Q); break;
                    case InstantValue t: writer.Instant(c, t.Ns); break;
                    case BooleanValue b: writer.Int(c, b.B ? 1 : 0); break;
                    case var other: throw new InvalidOperationException($"no column form for {other.Kind}");
                }
        }
        engine.Ingest(source, writer.Build()!, seq);
    }

    public static SpaceFacts FactsOf(Engine engine)
    {
        static ImmutableSortedDictionary<string, Value> Props(Props p) => p.Items.Where(kv => kv.Value is not NullValue).ToImmutableSortedDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var store = (InMemoryTelemetryStore)engine.Telemetry;
        var graphs = engine.Graphs.ToImmutableSortedDictionary(g => g.Name, g => new GraphFacts(
            g.State.Entities.ToImmutableSortedDictionary(e => e.Id, e => new EntityFact(e.Id, e.Type, Props(e.Props)), StringComparer.Ordinal),
            g.State.Rels.ToImmutableSortedDictionary(r => r.Id, r => new RelFact(r.Id, r.Type, r.Source, r.Target, Props(r.Props)), StringComparer.Ordinal),
            store.Dump(g.Key).ToImmutableSortedDictionary(w => new WindowId(engine.Interner.Get(w.Element), engine.Interner.Get(w.Prop), w.Ws), w => w.Value),
            [.. g.Out.Conflicts.Select(c => c.Text)]), StringComparer.Ordinal);
        return new SpaceFacts(graphs, engine.LogicalTime == long.MinValue ? null : engine.LogicalTime);
    }
}
