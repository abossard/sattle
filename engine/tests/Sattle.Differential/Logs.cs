using System.Collections.Immutable;
using Sattle.Core;
using Sattle.Reference;

namespace Sattle.Differential;

using Fields = ImmutableSortedDictionary<string, Value>;

/// <summary>How rows of one source are generated (a source listed twice gets twice the share of the inputs). A keyed source holds at most one row per key, as an adapter keeps one row per object: a new row for a held key is a retraction of the old row and an insertion.</summary>
public sealed record SourceSpec(string Name, Func<Random, long, Fields> Row, Func<Fields, string>? Key = null, int MaxWeight = 2);

/// <summary>Seeded input logs: insertions, retractions of held rows (whole or part of the weight), weights above one, logical-time advances, and row times before, inside and after the frontier. Pure: the same seed gives the same log.</summary>
public static class Logs
{
    public const long Second = 1_000_000_000, Minute = 60 * Second;
    /// <summary>A whole minute, so windows of 60 s start on it.</summary>
    public const long Origin = 1_800_000_000 * Second;

    public static Fields Row(params (string Name, Value Value)[] fields) => fields.ToImmutableSortedDictionary(f => f.Name, f => f.Value, StringComparer.Ordinal);
    public static Value Pick(Random r, params Value[] values) => values[r.Next(values.Length)];
    public static Value Text(Random r, string prefix, int n) => Value.Text(prefix + r.Next(n));
    /// <summary>A time around the frontier: up to six minutes late, up to two minutes ahead, in steps of 20 s.</summary>
    public static Value Near(Random r, long now) => Value.Instant(now + r.Next(-18, 7) * 20 * Second);

    /// <summary>`progress`: the sources that report their progress (Def. 10.9) and the share of the inputs that are such reports. A report lies up to two minutes behind or one minute ahead of the frontier, so a source can lag and catch up; with a share of zero the log has none, and the random choices are those of a log without them.</summary>
    public static ImmutableArray<Input> Generate(IReadOnlyList<SourceSpec> sources, double advanceShare, int seed, int length, (IReadOnlyList<string> Sources, double Share)? progress = null, double lossShare = 0)
    {
        var r = new Random(seed);
        var log = ImmutableArray.CreateBuilder<Input>();
        var held = sources.Select(s => s.Name).Distinct().ToDictionary(n => n, _ => new List<(Fields Fields, long Weight)>());
        var now = Origin;
        while (log.Count < length)
        {
            if (r.NextDouble() < advanceShare) { now += r.Next(1, 5) * 30 * Second; log.Add(new AdvanceInput(now)); continue; }
            if (lossShare > 0 && r.NextDouble() < lossShare) { log.Add(new LossInput(now + r.Next(-12, 1) * 20 * Second)); continue; }
            if (progress is { Share: > 0 } reports && r.NextDouble() < reports.Share) { log.Add(new ProgressInput(reports.Sources[r.Next(reports.Sources.Count)], now + r.Next(-4, 3) * 30 * Second)); continue; }
            var s = sources[r.Next(sources.Count)];
            var rows = held[s.Name];
            if (s.Key is { } key)
            {
                var fresh = s.Row(r, now);
                var old = rows.FindIndex(x => key(x.Fields) == key(fresh));
                if (old >= 0) { log.Add(new RowInput(s.Name, rows[old].Fields, -1)); rows.RemoveAt(old); if (r.Next(10) < 3) continue; }
                log.Add(new RowInput(s.Name, fresh, 1)); rows.Add((fresh, 1));
            }
            else if (rows.Count > 0 && r.Next(10) < 3)
            {
                var i = r.Next(rows.Count); var w = r.Next(1, (int)rows[i].Weight + 1);
                log.Add(new RowInput(s.Name, rows[i].Fields, -w));
                if (rows[i].Weight == w) rows.RemoveAt(i); else rows[i] = (rows[i].Fields, rows[i].Weight - w);
            }
            else
            {
                var fresh = s.Row(r, now); var w = r.Next(1, s.MaxWeight + 1);
                var i = rows.FindIndex(x => Canon.Of([.. x.Fields.Values]) == Canon.Of([.. fresh.Values]) && x.Fields.Keys.SequenceEqual(fresh.Keys));
                log.Add(new RowInput(s.Name, fresh, w));
                if (i >= 0) rows[i] = (rows[i].Fields, rows[i].Weight + w); else rows.Add((fresh, w));
            }
        }
        return log.ToImmutable();
    }

    /// <summary>Whether no prefix of the log holds a row with negative weight, and no keyed source two rows of one key: the inputs an adapter can produce.</summary>
    public static bool IsWellFormed(IReadOnlyList<SourceSpec> sources, IEnumerable<Input> log)
    {
        var held = new Dictionary<string, long>(StringComparer.Ordinal);
        var perKey = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in log.OfType<RowInput>())
        {
            var id = row.Source + "\u001e" + string.Join('\u001f', row.Fields.Select(f => f.Key + "=" + Canon.Of(f.Value)));
            if ((held[id] = held.GetValueOrDefault(id) + row.Weight) < 0) return false;
            if (sources.First(s => s.Name == row.Source).Key is { } key)
            {
                var k = row.Source + "\u001e" + key(row.Fields);
                if ((perKey[k] = perKey.GetValueOrDefault(k) + row.Weight) is < 0 or > 1) return false;
            }
        }
        return true;
    }

    public static string Print(IEnumerable<Input> log) => string.Join("\n", log.Select((x, i) => $"  {i,2}: " + x switch
    {
        AdvanceInput a => $"advance to {(a.Frontier - Origin) / Second:+0;-0}s",
        ProgressInput p => $"progress of {p.Source} to {(p.Until - Origin) / Second:+0;-0}s",
        LossInput l => $"loss at {(l.Time - Origin) / Second:+0;-0}s",
        RowInput row => $"{row.Source} {(row.Weight > 0 ? "+" : "")}{row.Weight} {{ {string.Join(", ", row.Fields.Where(f => f.Value is not NullValue).Select(f => f.Key + ": " + Show(f.Value)))} }}",
        _ => x.ToString(),
    }));

    static string Show(Value v) => v switch { InstantValue t => $"{(t.Ns - Origin) / Second:+0;-0}s", TextValue t => "\"" + t.S + "\"", DecimalValue d => d.Q.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m", IntegerValue n => n.N.ToString(), BooleanValue b => b.B ? "true" : "false", _ => Canon.Of(v) };
}
