using Sattle.Core;
using Sattle.Kql;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Definitions that the checker accepted although they are wrong, refused although they are right, or answered with an exception instead of a diagnostic (code review, section on the language checker).</summary>
public class CheckerTests
{
    const string Base = """
        module t @ 1.0.0;
        entity Item { k: Text; g: Text; hits?: Integer telemetry delta sum every 60s; level?: Decimal telemetry gauge max every 60s; }
        entity Other { k: Text; }
        identity item for Item version "1" { k: Text by exact; }
        identity other for Other version "1" { k: Text by exact; }
        rows R { k: Text; g: Text; n: Integer; t: Instant; }
        """;

    const string Ingest = """
        graph ingest : t;
        input a : R;
        nodes Item on i with (rule = "observed", description = "test") = a | project i = identity("item", k), k, g;
        nodes Other on o with (rule = "observed") = a | project o = identity("other", g), k = g;
        extend nodes Item on i with (rule = "observed") = a | summarize hits = sum(n) by k, windowStart = bin(t, 60s) | project i = identity("item", k), windowStart, hits;
        """;

    /// <summary>A view over the base module: `types` are extra declarations of the view's module, `body` the steps of its projection `v`, `space` the graphs of the space beside the master.</summary>
    static SourceFile[] View(string body, string types = "", string space = "graph view = v.v(master: master);") =>
    [
        new("t.sattle", Base), new("t.ingest.kql", Ingest), new("v.sattle", $$"""
            module v @ 1.0.0;
            import t @ 1.0.0 sha256:… as t;
            entity Out { k: Text; flag?: Text; owner?: Ref<t.Other>; }
            identity out for Out version "1" { k: Text by exact; }
            {{types}}
            """),
        new("v.v.kql", "graph v : v;\ninput master : t;\n" + body),
        new("s.sattle", $$"""
            module s @ 1.0.0;
            import t @ 1.0.0 sha256:… as t;
            import v @ 1.0.0 sha256:… as v;
            space s {
              source a adapter "sattle.authoring" rows t.R;
              master graph master = t.ingest(a: a);
              {{space}}
            }
            """),
    ];

    const string Items = "graph(\"master\") | graph-match (i) where labels(i) has \"Item\" project i = i.id, k = i.k, g = i.g";
    const string Plain = "nodes Out on o = " + Items + " | project o = identity(\"out\", k), k;\n";
    /// <summary>The windows of the items' hits within three minutes.</summary>
    const string Signals = Items + " | join kind=inner (telemetry(\"master\", 3m) | where property == \"hits\" | project i = id, windowStart, property, value) on i";
    const string Said = ", \"silent\"";
    /// <summary>The windows of `value` from the telemetry call `series`, a threshold with the given no-data state over them, and a sink.</summary>
    static string Quiet(string series, string noData, string value = "hits")
    {
        var nd = series.StartsWith("telemetry_filled", StringComparison.Ordinal) ? ", noData" : "";
        return $"nodes Out on o = {Items} | join kind=inner ({series} | where property == \"{value}\" | project i = id, windowStart, property, value{nd}) on i\n"
            + $"    | project c = k, windowStart, value, above = 3.0, enterAfter = 2, below = 1.0, exitAfter = 2{nd} | invoke hysteresis(\"on\", \"off\", \"off\"{noData})\n    | project o = identity(\"out\", c), k = c, flag = state;\n";
    }

    const string LoadType = "entity Load { k: Text; hits?: Integer telemetry delta sum every 60s; }\nidentity load for Load version \"1\" { k: Text by exact; }";
    /// <summary>Loads per item, rolled up by `keys` and written to the item's Load element.</summary>
    static string Loaded(string keys) =>
        $"nodes Load on l = {Items} | project l = identity(\"load\", k), k;\nextend nodes Load on l = {Signals} | summarize v = sum(value) by {keys}, windowStart | project l = identity(\"load\", c), windowStart, hits = v;\n";

    static (Catalog Catalog, List<Diagnostic> Errors) Load(SourceFile[] files)
    {
        var catalog = new Catalog(new KqlSource());
        return (catalog, [.. catalog.Load(files, updatePins: true).Diagnostics.Where(d => d.IsError)]);
    }

    public static TheoryData<string, SourceFile[], string, string> Refused => new()
    {
        { "the condition of an if is telemetry, so its result is: it is no value of a data property", View("nodes Out on o = " + Signals + " | project o = identity(\"out\", k), k, flag = iff(value > 2, \"hot\", \"cold\");\n"), "RoleInterference", "telemetry value" },
        { "two columns of one name in a select", View("let m = " + Items + " | project k, k = g;\nnodes Out on o = m | project o = identity(\"out\", k), k;\n"), "Kusto", "A column with the name 'k' is already declared" },
        { "a key and an aggregate of one name", View("nodes Out on o = " + Items + " | summarize k = count() by k | project o = identity(\"out\", k), k;\n"), "Kusto", "A column with the name 'k' is already declared" },
        { "a key and the state of a threshold of one name", View("nodes Out on o = " + Signals + " | project state = k, windowStart, value, above = 3.0, enterAfter = 2, below = 1.0, exitAfter = 2 | invoke hysteresis(\"on\", \"off\", \"off\")\n    | project o = identity(\"out\", state), k = state;\n"), "DuplicateColumn", "'state'" },
        { "two declarations of one id: two key constraints would be one key over both properties", View(Plain, "constraint key Out (k);\nconstraint key Out (flag);"), "Duplicate", "constraint 'key-Out'" },
        { "two labels on one type", View(Plain, "entity Named { k: Text label; title: Text label; }"), "Label", "second" },
        { "a label that is no text", View(Plain, "entity Named { k: Text; rank: Integer label; }"), "Label", "must be a Text property" },
        { "a relationship kind that is none of the four", View(Plain, "entity Named { k: Text; }\nrelationship Uses (Named) -> (Named) kind friendship;"), "Syntax", "relationship kind" },
        { "a fill on a gauge", View(Quiet("telemetry_filled(\"master\", 3m, 2m, 0.0)", Said, "level")), "Series", "takes no fill" },
        { "a fill of an Integer property that is no integer", View(Quiet("telemetry_filled(\"master\", 3m, 2m, 0.5)", Said)), "Series", "must be an integer" },
        { "a time to live below one window", View(Quiet("telemetry_filled(\"master\", 3m, 30s, real(null))", Said)), "Series", "at least one window" },
        { "a time to live above the look-back", View(Quiet("telemetry_filled(\"master\", 3m, 4m, real(null))", Said)), "Series", "at most the look-back less one window" },
        { "a time to live as long as the look-back", View(Quiet("telemetry_filled(\"master\", 3m, 3m, real(null))", Said)), "Series", "at most the look-back less one window" },
        { "a threshold over a series with a time to live that does not say what no data gives", View(Quiet("telemetry_filled(\"master\", 3m, 2m, real(null))", "")), "Threshold", "what no data gives" },
        { "a no-data state without a time to live upstream", View(Quiet("telemetry(\"master\", 3m)", Said)), "Threshold", "needs telemetry with a time to live" },
        { "a telemetry roll-up keyed finer than the element it writes (premise B1)", View(Loaded("c = k, g"), LoadType), "B1", "grouped by 'g'" },
        { "a reference to another entity type than the property names", View("nodes Out on o = " + Items + " | project o = identity(\"out\", k), k, owner = i;\n"), "Set", "owner expects" },
    };

    [Theory, MemberData(nameof(Refused))]
    public void A_wrong_definition_is_refused_with_a_diagnostic(string what, SourceFile[] files, string code, string says)
    {
        var (_, errors) = Load(files);
        Assert.True(errors.Any(e => e.Code == code && e.Message.Contains(says, StringComparison.Ordinal)), $"{what}: expected {code} saying \"{says}\", found {(errors.Count == 0 ? "no error" : string.Join("; ", errors.Select(e => e.ToString())))}");
    }

    /// <summary>Def. 7.15: a window can only be known to be covered when a source feeds the property. A fill of a property that nothing writes is refused when the space is activated.</summary>
    [Fact]
    public void A_fill_of_a_property_that_no_source_feeds_is_refused_at_activation()
    {
        var files = View("nodes Beat on b = " + Items + " | project b = identity(\"beat\", k), k;\n", """
            entity Beat { k: Text; beats?: Integer telemetry delta sum every 60s; }
            identity beat for Beat version "1" { k: Text by exact; }
            """, "graph view = v.v(master: master);\n  graph second = v.read(view: view);");
        files = [.. files, new("v.read.kql", """
            graph read : v;
            input view : v;
            nodes Out on o = graph("view") | graph-match (b) where labels(b) has "Beat" project b = b.id, k = b.k
                | join kind=inner (telemetry_filled("view", 3m, 2m, 0) | where property == "beats" | project b = id, windowStart, property, value, noData) on b
                | project c = k, windowStart, value, above = 3.0, enterAfter = 2, below = 1.0, exitAfter = 2, noData | invoke hysteresis("on", "off", "off", "silent")
                | project o = identity("out", c), k = c, flag = state;
            """)];
        var (catalog, errors) = Load(files);
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => e.ToString())));
        var refused = Assert.Throws<InvalidOperationException>(() => new Engine(catalog, catalog.Spaces.Keys.Single()));
        Assert.Contains("no source feeds", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Def. 7.15: authored rows come when someone writes them, so their source does not report and nothing it feeds is ever covered. A fill of such a property is refused when the space is activated; the same definitions over a source that reports are accepted.</summary>
    [Theory]
    [InlineData("sattle.authoring", "authored data records no progress")]
    [InlineData("test.reporting", null)]
    public void A_fill_of_a_property_that_an_authored_source_feeds_is_refused_at_activation(string adapter, string? refusal)
    {
        var files = View(Quiet("telemetry_filled(\"master\", 3m, 2m, 0)", Said));
        files = [.. files.Select(f => f.Path == "s.sattle" ? f with { Text = f.Text.Replace("source a adapter \"sattle.authoring\"", $"source a adapter \"{adapter}\"") } : f)];
        var (catalog, errors) = Load(files);
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => e.ToString())));
        if (refusal is null) { using var engine = new Engine(catalog, catalog.Spaces.Keys.Single()); Assert.True(engine.WantsProgress); }
        else Assert.Contains(refusal, Assert.Throws<InvalidOperationException>(() => new Engine(catalog, catalog.Spaces.Keys.Single())).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_graphs_of_one_projection_both_hold_its_output()
    {
        var (catalog, errors) = Load(View(Plain, space: "graph first = v.v(master: master);\n  graph second = v.v(master: master);"));
        Assert.Empty(errors);
        using var engine = new Engine(catalog, catalog.Spaces.Keys.Single());
        var w = new RowWriter(engine.RowLayout("a"), engine.Interner);
        w.Begin(1); w.Text("k", "k0"); w.Text("g", "g0"); w.Int(w.Col("n"), 1); w.Instant("t", 0);
        engine.Ingest("a", w.Build()!, 1);
        Assert.Equal(["first", "master", "second"], engine.Graphs.Select(g => g.Name).Order());
        Assert.All(engine.Graphs.Where(g => g.Name != "master"), g => Assert.Single(g.State.Entities));
    }
}
