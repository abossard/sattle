using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;
using static Sattle.Differential.Logs;

namespace Sattle.Differential;

/// <summary>T-SOUND (Theorem 5.2, O5.1): generated well-typed operator chains (filters, maps with arithmetic that divides by zero and overflows, groups with aggregates, distinct), ending in two create sinks on one key, run on generated rows. No exception; every committed element satisfies C2, C3 and C5, and C4 too while no fault and no conflict was counted; a fault is counted and leaves its value absent without stopping the rows around it; the state is the same under every cut of the log.</summary>
public class TypeSoundnessTests
{
    const string Types = """
        {
          "module": "w", "version": "1.0.0", "imports": [],
          "entities": [{"name": "Out", "properties": [{"name": "k", "type": "Text"}, {"name": "s", "type": "Text"}, {"name": "v", "type": "Decimal", "optional": true}, {"name": "c", "type": "Integer", "optional": true}]}],
          "identities": [{"name": "out", "type": "Out", "components": [{"name": "k", "type": "Text"}]}],
          "rows": [{"name": "R", "fields": [{"name": "k", "type": "Text"}, {"name": "g", "type": "Text"}, {"name": "n", "type": "Integer"}, {"name": "d", "type": "Decimal", "optional": true}, {"name": "t", "type": "Instant"}]}]
        }
        """;
    const string Space = """
        {
          "module": "w.space", "version": "1.0.0", "imports": [{"module": "w", "alias": "base", "version": "1.0.0"}],
          "space": {"name": "s", "sources": [{"name": "a", "adapter": "sattle.authoring", "rows": "base.R"}], "graphs": [{"name": "master", "projection": "base.ingest", "master": true, "inputs": {"a": "a"}}]}
        }
        """;

    sealed record Rel(string Name, Dictionary<string, (string Kind, bool Req)> Cols);

    static string Ref(string column) => $$"""{"ref": "{{column}}"}""";
    static string Lit(string json) => $$"""{"lit": {{json}}}""";
    static string Fn(string name, params string[] args) => $$"""{"fn": "{{name}}", "args": [{{string.Join(", ", args)}}]}""";
    static string Verb(string id, string verb, string fields = "") => $$"""{"id": "{{id}}", "verb": "{{verb}}"{{(fields == "" ? "" : ", " + fields)}}}""";
    static string Wire(string from, string to) => $$"""{"from": "{{from}}", "to": "{{to}}"}""";
    static string Object(IEnumerable<(string Name, string Json)> fields) => "{" + string.Join(", ", fields.Select(f => $"\"{f.Name}\": {f.Json}")) + "}";

    /// <summary>A random chain over `a`, as gkql verbs and wires, and the relation it ends in.</summary>
    static (List<string> Verbs, List<string> Wires, Rel Last) Chain(Random r)
    {
        List<string> verbs = [Verb("a", "rows", "\"source\": \"a\"")], wires = [];
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
                        var preds = new List<string> { Fn("!=", Ref("g"), Lit("\"g1\"")), Fn("!=", Ref("k"), Lit("\"k0\"")) };
                        preds.AddRange(ints.Select(n => Fn(">", Ref(n), Lit(r.Next(4).ToString()))));
                        preds.AddRange(decs.Select(d => Fn("isnotnull", Ref(d))));
                        verbs.Add(Verb(name, "where", $"\"condition\": {preds[r.Next(preds.Count)]}"));
                        wires.Add(Wire(cur.Name, name));
                        cur = cur with { Name = name };
                        break;
                    }
                case 1:
                    {
                        var cols = new Dictionary<string, (string, bool)> { ["k"] = ("Text", true), ["g"] = ("Text", true) };
                        var exprs = new List<(string, string)> { ("k", Ref("k")), ("g", Ref("g")) };
                        if (ints.Count > 0)
                        {
                            var n = ints[r.Next(ints.Count)];
                            var e = r.Next(3) switch { 0 => Fn("+", Fn("*", Ref(n), Lit("2")), Lit("1")), 1 => Fn("*", Ref(n), Lit("4611686018427387904")), _ => Fn("-", Ref(n), Lit("3")) };   // the second overflows from 2 on
                            exprs.Add(("n", e)); cols["n"] = ("Integer", cur.Cols[n].Req);
                        }
                        if (decs.Count > 0 && ints.Count > 0)
                        {
                            var (d, n) = (decs[r.Next(decs.Count)], ints[r.Next(ints.Count)]);
                            var dv = cur.Cols[d].Req ? Ref(d) : Fn("coalesce", Ref(d), Lit("1.5"));
                            exprs.Add(("d", r.Next(2) == 0 ? Fn("/", dv, Ref(n)) : Fn("iff", Fn(">", Ref(n), Lit("0")), Fn("*", dv, Lit("2.5")), Lit("0.0"))));   // the first divides by zero where n is 0
                            cols["d"] = ("Decimal", true);
                        }
                        verbs.Add(Verb(name, "project", $"\"columns\": {Object(exprs)}"));
                        wires.Add(Wire(cur.Name, name));
                        cur = new Rel(name, cols);
                        break;
                    }
                case 2:
                    {
                        var cols = new Dictionary<string, (string, bool)> { ["k"] = ("Text", true), ["g"] = ("Text", true), ["c"] = ("Integer", true) };
                        var aggs = new List<(string, string)> { ("c", """{"fn": "count"}""") };
                        if (ints.Count > 0) { aggs.Add(("n", Fn("sum", Ref(ints[0])))); cols["n"] = ("Integer", true); }
                        if (decs.Count > 0) { aggs.Add(("d", Fn(r.Next(2) == 0 ? "max" : "avg", Ref(decs[0])))); cols["d"] = ("Decimal", false); }
                        verbs.Add(Verb(name, "summarize", $"\"by\": {Object([("k", Ref("k")), ("g", Ref("g"))])}, \"aggregates\": {Object(aggs)}"));
                        wires.Add(Wire(cur.Name, name));
                        cur = new Rel(name, cols);
                        break;
                    }
                default:
                    {
                        var keep = cur.Cols.Where(c => c.Value.Kind != "Instant").ToDictionary(c => c.Key, c => c.Value);
                        verbs.Add(Verb(name + "p", "project", $"\"columns\": {Object(keep.Keys.Select(c => (c, Ref(c))))}"));
                        verbs.Add(Verb(name, "distinct"));
                        wires.Add(Wire(cur.Name, name + "p")); wires.Add(Wire(name + "p", name));
                        cur = new Rel(name, keep);
                        break;
                    }
            }
        }
        return (verbs, wires, cur);
    }

    /// <summary>The definitions of a chain: two sinks make `Out` from its last relation under one key, so a row may give two values for `s`.</summary>
    static SourceFile[] Module(List<string> chain, List<string> wires, Rel last)
    {
        var sets = new List<(string, string)> { ("k", Ref("k")), ("s", Ref("g")) };
        if (last.Cols.FirstOrDefault(c => c.Value.Kind == "Decimal").Key is { } v) sets.Add(("v", Ref(v)));
        if (last.Cols.FirstOrDefault(c => c.Value.Kind == "Integer").Key is { } c) sets.Add(("c", Ref(c)));
        string Sink(string id, IEnumerable<(string, string)> props) =>
            Verb(id, "nodes", $$"""
                "target": {"nodes": [{"var": "o", "label": "Out", "id": {"identity": "out", "args": [{"ref": "k"}]}, "props": {{Object(props)}}}]}, "rule": "observed"
                """);
        List<string> verbs = [.. chain, Sink("out1", sets), Sink("out2", [("k", Ref("k")), ("s", Fn("concat", Ref("g"), Lit("\"!\"")))])];
        List<string> all = [.. wires, Wire(last.Name, "out1"), Wire(last.Name, "out2")];
        return
        [
            new SourceFile("w.module.gkql.json", Types),
            new SourceFile("w.ingest.gkql.json", $$"""
                {"graph": "ingest", "ontology": "w", "history": 0, "inputs": [{"name": "a", "rows": "w.R"}], "parameters": [],
                 "verbs": [{{string.Join(",\n", verbs)}}],
                 "wires": [{{string.Join(", ", all)}}]}
                """),
            new SourceFile("w.space.module.gkql.json", Space),
        ];
    }

    static readonly SourceSpec A = new("a", (r, now) => Row(("k", Text(r, "k", 4)), ("g", Text(r, "g", 3)), ("n", Value.Integer(r.Next(6))),
        ("d", Pick(r, Value.Null, Value.Decimal(1m), Value.Decimal(2.5m), Value.Decimal(0m))), ("t", Near(r, now))));

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void A_well_typed_projection_never_faults_and_emits_conforming_elements(int seed)
    {
        var r = new Random(seed);
        var (checkedChains, withFaults) = (0, 0);
        for (var i = 0; i < 12; i++)
        {
            var (verbs, wires, last) = Chain(r);
            var files = Module(verbs, wires, last);
            var catalog = new Catalog(EngineRun.Projections);
            var res = catalog.Load(files, updatePins: true);
            Assert.False(res.Diagnostics.Any(d => d.IsError), "the generator wrote a definition that does not check:\n" + files[1].Text + "\n" + string.Join("\n", res.Diagnostics.Where(d => d.IsError)));
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
                var counters = e.Counters();
                var faults = counters.DivisionByZero + counters.Overflow + e.Graphs.Sum(g => g.Out.Counts.PropertyConflicts + g.Out.Counts.ElementConflicts);
                if (counters.DivisionByZero + counters.Overflow > 0) withFaults++;
                var view = catalog.OntologyOf(e.Master.Typed.OutputModule);
                var bad = Conformance.Check(e.Master.State, view).Where(d => d.IsError).ToList();
                Assert.DoesNotContain(bad, d => d.Code is "UnknownType" or "AbstractType" or "UnknownProperty" or "ValueKind" or "EndpointType" or "EventsInGraph");   // C2, C3, C5
                if (faults == 0) Assert.DoesNotContain(bad, d => d.Code == "RequiredProperty");                                                                    // C4
                digests.Add(e.StateDigest());
            }
            Assert.Single(digests);
        }
        Assert.Equal(12, checkedChains);
        Assert.True(withFaults > 0, "no generated chain divided by zero or overflowed");
    }

    /// <summary>A fault is counted, leaves its value absent and does not stop the rows around it: an overflow in one row of a batch of three.</summary>
    [Fact]
    public void A_fault_is_counted_and_leaves_only_its_own_value_absent()
    {
        var catalog = EngineRun.Load(Module([Verb("a", "rows", "\"source\": \"a\""), Verb("x0", "project", $"\"columns\": {Object([("k", Ref("k")), ("g", Ref("g")), ("n", Fn("*", Ref("n"), Lit("4611686018427387904")))])}")], [Wire("a", "x0")], new Rel("x0", new() { ["k"] = ("Text", true), ["g"] = ("Text", true), ["n"] = ("Integer", true) })));
        using var e = new Engine(catalog, catalog.Spaces.Keys.Single());
        Sattle.Reference.RowInput Row_(string k, long n) => new("a", Row(("k", Value.Text(k)), ("g", Value.Text("g")), ("n", Value.Integer(n)), ("d", Value.Null), ("t", Value.Instant(Origin))), 1);
        EngineRun.Ingest(e, [Row_("k0", 1), Row_("k1", 3), Row_("k2", 0)], 1);
        Assert.Equal(1, e.Counters().Overflow);
        var c = e.Master.State.Entities.ToDictionary(x => x.Props.Text("d:w/prop:k")!, x => x.Props.Get("d:w/prop:c"));
        Assert.Equal(["k0", "k1", "k2"], c.Keys.Order());
        Assert.Equal(new IntegerValue(4611686018427387904), c["k0"]); Assert.Null(c["k1"]); Assert.Equal(new IntegerValue(0), c["k2"]);
    }
}
