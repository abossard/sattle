using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;
using static Sattle.Differential.Logs;

namespace Sattle.Differential;

/// <summary>T-SOUND (Theorem 5.2, O5.1): generated well-typed operator chains (filters, maps with arithmetic that divides by zero and overflows, groups with aggregates, distinct), ending in two create sinks on one key, run on generated rows. No exception; every committed element satisfies C2, C3 and C5, and C4 too while no fault and no conflict was counted; a fault is counted and leaves its value absent without stopping the rows around it; the state is the same under every cut of the log.</summary>
public class TypeSoundnessTests
{
    const string Head = """
        module w @ 1.0.0;
        entity Out { k: Text; s: Text; v?: Decimal; c?: Integer; }
        identity out for Out version "1" { k: Text by exact; }
        rows R { k: Text; g: Text; n: Integer; d?: Decimal; t: Instant; }
        """;

    sealed record Rel(string Name, Dictionary<string, (string Kind, bool Req)> Cols);

    /// <summary>A random chain over `a`, as statements, and the relation it ends in.</summary>
    static (string Body, Rel Last) Chain(Random r)
    {
        var sb = new System.Text.StringBuilder();
        var cur = new Rel("a", new() { ["k"] = ("Text", true), ["g"] = ("Text", true), ["n"] = ("Integer", true), ["d"] = ("Decimal", false), ["t"] = ("Instant", true) });
        for (var i = 0; i < r.Next(1, 5); i++)
        {
            var name = "x" + i;
            var ints = cur.Cols.Where(c => c.Value.Kind == "Integer").Select(c => c.Key).ToList();
            var decs = cur.Cols.Where(c => c.Value.Kind == "Decimal").Select(c => c.Key).ToList();
            switch (r.Next(4))
            {
                case 0:
                    {
                        var preds = new List<string> { "g != \"g1\"", "k != \"k0\"" };
                        preds.AddRange(ints.Select(n => $"{n} > {r.Next(4)}"));
                        preds.AddRange(decs.Select(d => $"isPresent({d})"));
                        sb.AppendLine($"    let {name} = {cur.Name} |> where {preds[r.Next(preds.Count)]};");
                        cur = cur with { Name = name };
                        break;
                    }
                case 1:
                    {
                        var cols = new Dictionary<string, (string, bool)> { ["k"] = ("Text", true), ["g"] = ("Text", true) };
                        var exprs = new List<string> { "k: k", "g: g" };
                        if (ints.Count > 0)
                        {
                            var n = ints[r.Next(ints.Count)];
                            var (e, req) = r.Next(3) switch { 0 => ($"{n} * 2 + 1", cur.Cols[n].Req), 1 => ($"{n} * 4611686018427387904", cur.Cols[n].Req), _ => ($"{n} - 3", cur.Cols[n].Req) };   // the second overflows from 2 on
                            exprs.Add($"n: {e}"); cols["n"] = ("Integer", req);
                        }
                        if (decs.Count > 0 && ints.Count > 0)
                        {
                            var (d, n) = (decs[r.Next(decs.Count)], ints[r.Next(ints.Count)]);
                            var dv = cur.Cols[d].Req ? d : $"coalesce({d}, 1.5)";
                            exprs.Add(r.Next(2) == 0 ? $"d: {dv} / {n}" : $"d: if({n} > 0, {dv} * 2.5, 0.0)");                                   // the first divides by zero where n is 0
                            cols["d"] = ("Decimal", true);
                        }
                        sb.AppendLine($"    let {name} = {cur.Name} |> select {{ {string.Join(", ", exprs)} }};");
                        cur = new Rel(name, cols);
                        break;
                    }
                case 2:
                    {
                        var cols = new Dictionary<string, (string, bool)> { ["k"] = ("Text", true), ["g"] = ("Text", true), ["c"] = ("Integer", true) };
                        var aggs = new List<string> { "c: count()" };
                        if (ints.Count > 0) { aggs.Add($"n: sum({ints[0]})"); cols["n"] = ("Integer", true); }
                        if (decs.Count > 0) { aggs.Add(r.Next(2) == 0 ? $"d: max({decs[0]})" : $"d: mean({decs[0]})"); cols["d"] = ("Decimal", false); }
                        sb.AppendLine($"    let {name} = {cur.Name} |> group by {{ k: k, g: g }} aggregate {{ {string.Join(", ", aggs)} }};");
                        cur = new Rel(name, cols);
                        break;
                    }
                default:
                    {
                        var keep = cur.Cols.Where(c => c.Value.Kind != "Instant").ToDictionary(c => c.Key, c => c.Value);
                        sb.AppendLine($"    let {name} = {cur.Name} |> select {{ {string.Join(", ", keep.Keys.Select(c => $"{c}: {c}"))} }} |> distinct;");
                        cur = new Rel(name, keep);
                        break;
                    }
            }
        }
        return (sb.ToString(), cur);
    }

    static string Module(string body, Rel last)
    {
        var sets = new List<string> { "k: k", "s: g" };
        if (last.Cols.FirstOrDefault(c => c.Value.Kind == "Decimal").Key is { } v) sets.Add($"v: {v}");
        if (last.Cols.FirstOrDefault(c => c.Value.Kind == "Integer").Key is { } c) sets.Add($"c: {c}");
        return $$"""
            {{Head}}
            projection ingest ingestion output w {
              in a: rows R;
              rule observed readonly "generated" {
            {{body}}
                emit entity Out from {{last.Name}} identity out(k: k) set { {{string.Join(", ", sets)}} };
                emit entity Out from {{last.Name}} identity out(k: k) set { k: k, s: concat(g, "!") };
              }
            }
            space s {
              source a adapter "sattle.authoring" rows R;
              master graph master = ingest(a: a);
            }
            """;
    }

    static readonly SourceSpec A = new("a", (r, now) => Row(("k", Text(r, "k", 4)), ("g", Text(r, "g", 3)), ("n", Value.Integer(r.Next(6))),
        ("d", Pick(r, Value.Null, Value.Decimal(1m), Value.Decimal(2.5m), Value.Decimal(0m))), ("t", Near(r, now))));

    [Theory, Trait("Category", "Differential")]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void A_well_typed_projection_never_faults_and_emits_conforming_elements(int seed)
    {
        var r = new Random(seed);
        var (checkedChains, withFaults) = (0, 0);
        for (var i = 0; i < 12; i++)
        {
            var (body, last) = Chain(r);
            var text = Module(body, last);
            var catalog = new Catalog();
            var res = catalog.Load([new SourceFile("w.sattle", text)], updatePins: true);
            Assert.False(res.Diagnostics.Any(d => d.IsError), "the generator wrote a definition that does not check:\n" + text + "\n" + string.Join("\n", res.Diagnostics.Where(d => d.IsError)));
            checkedChains++;
            var space = catalog.Spaces.Keys.Single();
            var log = Generate([A], 0.2, seed * 100 + i, 25);
            var digests = new HashSet<string>();
            foreach (var cut in new[] { Chunking.OneByOne, Chunking.Largest, Chunking.Random(i) })
            {
                using var e = new Engine(catalog, space);
                var seq = 0L; var batch = new List<Sattle.Reference.RowInput>();
                for (var j = 0; j < log.Length; j++)
                {
                    if (log[j] is Sattle.Reference.AdvanceInput adv) { e.Advance(adv.Frontier, ++seq); continue; }
                    batch.Add((Sattle.Reference.RowInput)log[j]);
                    if (j == log.Length - 1 || log[j + 1] is not Sattle.Reference.RowInput || cut.SplitAfter(j)) { EngineRun.Ingest(e, [.. batch], ++seq); batch.Clear(); }
                }
                var faults = e.Faults.DivisionByZero + e.Faults.Overflow + e.Graphs.Sum(g => g.Out.PropertyConflicts + g.Out.ElementConflicts);
                if (e.Faults.DivisionByZero + e.Faults.Overflow > 0) withFaults++;
                var view = catalog.OntologyOf(e.Master.Typed.OutputModule);
                var bad = Conformance.Check(e.Master.State, view).Where(d => d.IsError).ToList();
                Assert.DoesNotContain(bad, d => d.Code is "UnknownType" or "AbstractType" or "UnknownProperty" or "ValueKind" or "EndpointType" or "TelemetryInGraph");   // C2, C3, C5
                if (faults == 0) Assert.DoesNotContain(bad, d => d.Code == "RequiredProperty");                                                                    // C4
                digests.Add(e.StateDigest());
            }
            Assert.Single(digests);
        }
        Assert.Equal(12, checkedChains);
        Assert.True(withFaults > 0, "no generated chain divided by zero or overflowed");
    }

    /// <summary>A fault is counted, leaves its value absent and does not stop the rows around it: an overflow in one row of a batch of three.</summary>
    [Fact, Trait("Category", "Differential")]
    public void A_fault_is_counted_and_leaves_only_its_own_value_absent()
    {
        var text = Module("    let x0 = a |> select { k: k, g: g, n: n * 4611686018427387904 };\n", new Rel("x0", new() { ["k"] = ("Text", true), ["g"] = ("Text", true), ["n"] = ("Integer", true) }));
        var catalog = new Catalog();
        Assert.DoesNotContain(catalog.Load([new SourceFile("w.sattle", text)], updatePins: true).Diagnostics, d => d.IsError);
        using var e = new Engine(catalog, catalog.Spaces.Keys.Single());
        Sattle.Reference.RowInput Row_(string k, long n) => new("a", Row(("k", Value.Text(k)), ("g", Value.Text("g")), ("n", Value.Integer(n)), ("d", Value.Null), ("t", Value.Instant(Origin))), 1);
        EngineRun.Ingest(e, [Row_("k0", 1), Row_("k1", 3), Row_("k2", 0)], 1);
        Assert.Equal(1, e.Faults.Overflow);
        var c = e.Master.State.Entities.ToDictionary(x => x.Props.Text("d:w/prop:k")!, x => x.Props.Get("d:w/prop:c"));
        Assert.Equal(["k0", "k1", "k2"], c.Keys.Order());
        Assert.Equal(new IntegerValue(4611686018427387904), c["k0"]); Assert.Null(c["k1"]); Assert.Equal(new IntegerValue(0), c["k2"]);
    }
}
