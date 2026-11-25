using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>A Rollup aggregates windows, and what it may do depends on the temporality of the value: a delta (counts per window) sums and takes extremes; a gauge (a level) does too; a cumulative (a counter that only grows) does neither until `Delta` makes it a delta. The palette declares this per aggregate (`requires`); the checker must agree with the declaration for every temporality. The matrix covers the aggregates sum, min and max.</summary>
public class RollupTemporalityTests
{
    const string Delta = """{"temporality": "delta", "combine": "sum", "period": "60s"}""", Gauge = """{"temporality": "gauge", "combine": "max", "period": "60s"}""", Cumulative = """{"temporality": "cumulative", "combine": "last", "period": "60s"}""";

    /// <summary>`Item.hits` with the events `TEMPORALITY`, `Load.x` a delta.</summary>
    const string Module = """
        {"module": "t", "version": "1.0.0", "imports": [],
         "entities": [{"name": "Item", "properties": [{"name": "k", "type": "Text"}, {"name": "hits", "type": "Integer", "optional": true, "events": TEMPORALITY}]},
                      {"name": "Load", "properties": [{"name": "k", "type": "Text"}, {"name": "x", "type": "Integer", "optional": true, "events": {"temporality": "delta", "combine": "sum", "period": "60s"}}]}],
         "identities": [{"name": "item", "type": "Item", "components": [{"name": "k", "type": "Text"}]},
                        {"name": "load", "type": "Load", "components": [{"name": "k", "type": "Text"}]}],
         "rows": [{"name": "R", "fields": [{"name": "k", "type": "Text"}]}]}
        """;

    const string Ingest = """
        {"graph": "ingest", "ontology": "t", "inputs": [{"name": "a", "rows": "t.R"}], "parameters": [],
         "verbs": [
           {"id": "a", "verb": "rows", "source": "a"},
           {"id": "observed", "verb": "nodes", "rule": "observed", "target": {"nodes": [{"var": "i", "label": "Item", "id": {"identity": "item", "args": [{"ref": "k"}]}, "props": {"k": {"ref": "k"}}}]}}],
         "wires": [{"from": "a", "to": "observed"}]}
        """;

    /// <summary>The hits of each item rolled up per window by `FN` into the load of the item.</summary>
    const string View = """
        {"graph": "view", "ontology": "t", "inputs": [{"name": "master", "ontology": "t"}], "parameters": [],
         "verbs": [
           {"id": "items", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "i", "label": "Item"}]}},
           {"id": "keyed", "verb": "project", "columns": {"i": {"ref": "i"}, "k": {"ref": "i.k"}}},
           {"id": "load", "verb": "nodes", "target": {"nodes": [{"var": "l", "label": "Load", "id": {"identity": "load", "args": [{"ref": "k"}]}, "props": {"k": {"ref": "k"}}}]}},
           {"id": "hits", "verb": "series", "graph": "master", "series": {"element": "i", "property": "hits", "lookback": "3m"}},
           {"id": "rolled", "verb": "summarize", "by": {"k": {"ref": "k"}, "windowStart": {"ref": "windowStart"}}, "aggregates": {"x": {"fn": "FN", "args": [{"ref": "hits"}]}}},
           {"id": "loadX", "verb": "nodes", "target": {"nodes": [{"var": "l", "label": "Load", "id": {"identity": "load", "args": [{"ref": "k"}]}, "extend": true, "props": {"x": {"ref": "x"}}}]}}],
         "wires": [{"from": "items", "to": "keyed"}, {"from": "keyed", "to": "load"}, {"from": "keyed", "to": "hits"}, {"from": "hits", "to": "rolled"}, {"from": "rolled", "to": "loadX"}]}
        """;

    const string Space = """
        {"module": "s", "version": "1.0.0", "imports": [{"module": "t", "alias": "t", "version": "1.0.0", "digest": null}],
         "space": {"name": "s", "sources": [{"name": "a", "adapter": "record", "rows": "t.R"}],
                   "graphs": [{"name": "master", "projection": "t.ingest", "master": true, "inputs": {"a": "a"}},
                              {"name": "view", "projection": "t.view", "inputs": {"master": "master"}}]}}
        """;

    /// <summary>`Item.hits` declared with the temporality under test, rolled up per item by `fn` in the view `t.view`; the checker's refusals of that aggregate.</summary>
    static IReadOnlyList<Diagnostic> Diagnostics(string temporality, string fn)
    {
        SourceFile[] files =
        [
            new("t.module.gkql.json", Module.Replace("TEMPORALITY", temporality)),
            new("t.ingest.gkql.json", Ingest),
            new("t.view.gkql.json", View.Replace("\"FN\"", "\"" + fn + "\"")),
            new("s.module.gkql.json", Space),
        ];
        return [.. new Catalog(Repo.Projections).Load(files, updatePins: true).Diagnostics.Where(d => d.IsError && d.Code == "Aggregate")];
    }

    public static IEnumerable<object[]> Matrix() =>
        from t in new[] { ("delta", Delta), ("gauge", Gauge), ("cumulative", Cumulative) }
        from fn in new[] { "sum", "min", "max" }
        select new object[] { t.Item1, t.Item2, fn };

    /// <summary>What the palette says is needed for the aggregate to be accepted over a value of this temporality.</summary>
    static bool Allowed(string temporality, string fn)
    {
        var ctx = Support.AggregateContexts[fn].Single(c => c.Operator == "Rollup");
        return temporality switch
        {
            "cumulative" => false,                                       // every Rollup aggregate says "not cumulative" or "gauge"
            "gauge" => true,                                             // `requires` is "not cumulative" or "gauge": both hold
            _ => ctx.Requires != "gauge",
        };
    }

    [Theory, MemberData(nameof(Matrix))]
    public void The_checker_accepts_exactly_what_the_palette_declares_for_each_temporality(string temporality, string declaration, string fn)
    {
        var diags = Diagnostics(declaration, fn);
        Assert.Equal(!Allowed(temporality, fn), diags.Count > 0);
    }

    [Theory]
    [InlineData("sum")] [InlineData("min")] [InlineData("max")]
    public void Over_a_cumulative_value_the_refusal_says_to_apply_Delta_first(string fn)
    {
        var d = Assert.Single(Diagnostics(Cumulative, fn));
        Assert.Contains("Delta", d.Message);
        Assert.Equal("t.view.gkql.json", d.File);
        Assert.True(d.Line > 0);
    }
}
