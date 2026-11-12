using Sattle.Core;
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
        projection ingest ingestion output t {
          in a: rows R;
          rule observed readonly "test" {
            emit entity Item from a identity item(k: k) set { k: k, g: g };
            emit entity Other from a identity other(k: g) set { k: g };
            let w = a |> window on t width 60s late revise;
            let p = w |> group by { k: k, windowStart: windowStart } aggregate { hits: sum(n) };
            emit update entity Item from p identity item(k: k) set { hits: hits };
          }
        }
        """;

    /// <summary>A view over the base module: `types` are extra declarations of the view's module, `body` the statements of its projection, `space` the graphs of the space beside the master.</summary>
    static SourceFile[] View(string body, string types = "", string space = "graph view = v(master: master);") =>
    [
        new("t.sattle", Base), new("v.sattle", $$"""
            module v @ 1.0.0;
            import t @ 1.0.0 sha256:… as t;
            entity Out { k: Text; flag?: Text; owner?: Ref<t.Other>; }
            identity out for Out version "1" { k: Text by exact; }
            {{types}}
            projection v view forward output v {
              in master: graph t;
            {{body}}
            }
            space s {
              source a adapter "sattle.authoring" rows t.R;
              master graph master = t.ingest(a: a);
              {{space}}
            }
            """),
    ];

    const string Plain = "  let m = match master (i: t.Item);\n  emit entity Out from m identity out(k: i.k) set { k: i.k };";
    const string Signals = "  let s = match master (i: t.Item) |> series master i.hits within 3m";
    const string Said = " nodata then \"silent\"";
    /// <summary>A series with the given reads and clauses, mapped to a key and a value with the no-data column, a threshold with the given no-data clause, and a sink.</summary>
    static string Quiet(string series, string noData, string value = "hits") =>
        $"  let s = match master (i: t.Item) |> series master {series} |> select {{ c: i.k, windowStart: windowStart, v: {value}, noData: noData }}\n    |> threshold v by {{ c: c }} above 3 for 2 then \"on\" below 1 for 2 then \"off\" initial \"off\"{noData} as state;\n  emit entity Out from s identity out(k: c) set {{ k: c, flag: state }};";

    const string LoadType = "entity Load { k: Text; hits?: Integer telemetry delta sum every 60s; }\nidentity load for Load version \"1\" { k: Text by exact; }";
    /// <summary>Loads per item, rolled up by `keys` and written to the item's Load element.</summary>
    static string Loaded(string keys) =>
        $"  let m = match master (i: t.Item);\n  emit entity Load from m identity load(k: i.k) set {{ k: i.k }};\n{Signals} |> rollup by {{ {keys} }} combine {{ v: sum(hits) }};\n  emit update entity Load from s identity load(k: c) set {{ hits: v }};";

    static (Catalog Catalog, List<Diagnostic> Errors) Load(SourceFile[] files)
    {
        var catalog = new Catalog();
        return (catalog, [.. catalog.Load(files, updatePins: true).Diagnostics.Where(d => d.IsError)]);
    }

    public static TheoryData<string, SourceFile[], string, string> Refused => new()
    {
        { "the condition of an if is telemetry, so its result is: it is no value of a data property", View(Signals + " |> select { k: i.k, flag: if(hits > 2, \"hot\", \"cold\") };\n  emit entity Out from s identity out(k: k) set { k: k, flag: flag };"), "RoleInterference", "telemetry value" },
        { "null is no value of a required property", View("  let m = match master (i: t.Item);\n  emit entity Out from m identity out(k: i.k) set { k: null };"), "Set", "optional value" },
        { "steps after the bound of a fixpoint", View("  let m = match master (i: t.Item) |> select { a: i.k, b: i.g };\n  let c = fixpoint m via join c, m on b == a |> select { a: a, b: b } max 3 |> where a != b;\n  emit entity Out from c identity out(k: a) set { k: a };"), "Fixpoint", "steps after" },
        { "two columns of one name in a select", View("  let m = match master (i: t.Item) |> select { k: i.k, k: i.g };\n  emit entity Out from m identity out(k: k) set { k: k };"), "Duplicate", "assign 'k'" },
        { "a key and an aggregate of one name", View("  let m = match master (i: t.Item) |> group by { k: i.k } aggregate { k: count() };\n  emit entity Out from m identity out(k: k) set { k: k };"), "DuplicateColumn", "'k'" },
        { "a key and the state of a threshold of one name", View(Signals + " |> select { state: i.k, windowStart: windowStart, v: hits }\n    |> threshold v by { state: state } above 3 for 2 then \"on\" below 1 for 2 then \"off\" initial \"off\" as state;\n  emit entity Out from s identity out(k: state) set { k: state };"), "DuplicateColumn", "'state'" },
        { "two declarations of one id: two key constraints would be one key over both properties", View(Plain, "constraint key Out (k);\nconstraint key Out (flag);"), "Duplicate", "constraint 'key-Out'" },
        { "two labels on one type", View(Plain, "entity Named { k: Text label; title: Text label; }"), "Label", "second" },
        { "a label that is no text", View(Plain, "entity Named { k: Text; rank: Integer label; }"), "Label", "must be a Text property" },
        { "a relationship kind that is none of the four", View(Plain, "entity Named { k: Text; }\nrelationship Uses (Named) -> (Named) kind friendship;"), "Syntax", "relationship kind" },
        { "a fill without a time to live", View(Quiet("i.hits fill 0 within 3m", Said)), "Series", "time to live" },
        { "a fill on a gauge", View(Quiet("i.level fill 0 within 3m ttl 2m", Said)), "Series", "takes no fill" },
        { "a fill of an Integer property that is no integer", View(Quiet("i.hits fill 0.5 within 3m ttl 2m", Said)), "Series", "must be an integer" },
        { "a time to live below one window", View(Quiet("i.hits within 3m ttl 30s", Said)), "Series", "at least one window" },
        { "a time to live above the look-back", View(Quiet("i.hits within 3m ttl 4m", Said)), "Series", "at most `within` less one window" },
        { "a time to live as long as the look-back", View(Quiet("i.hits within 3m ttl 3m", Said)), "Series", "at most `within` less one window" },
        { "a threshold over a series with a time to live that does not say what no data gives", View(Quiet("i.hits within 3m ttl 2m", "")), "Threshold", "what no data gives" },
        { "a no-data clause without a time to live upstream", View(Signals + " |> select { c: i.k, windowStart: windowStart, v: hits }\n    |> threshold v by { c: c } above 3 for 2 then \"on\" below 1 for 2 then \"off\" initial \"off\" nodata ignore as state;\n  emit entity Out from s identity out(k: c) set { k: c, flag: state };"), "Threshold", "needs a series with a time to live" },
        { "a roll-up that names no data without a time to live upstream", View(Signals + " |> rollup by { c: i.g } combine { v: sum(hits) } nodata any\n    |> threshold v by { c: c } above 3 for 2 then \"on\" below 1 for 2 then \"off\" initial \"off\" as state;\n  emit entity Out from s identity out(k: c) set { k: c, flag: state };"), "Rollup", "needs a series with a time to live" },
        { "a telemetry roll-up keyed finer than the element it writes (premise B1)", View(Loaded("c: i.k, g: i.g"), LoadType), "B1", "grouped by 'g'" },
        { "a reference to another entity type than the property names", View("  let m = match master (i: t.Item);\n  emit entity Out from m identity out(k: i.k) set { k: i.k, owner: i };"), "Set", "owner expects" },
    };

    [Theory, MemberData(nameof(Refused))]
    public void A_wrong_definition_is_refused_with_a_diagnostic(string what, SourceFile[] files, string code, string says)
    {
        var (_, errors) = Load(files);
        Assert.True(errors.Any(e => e.Code == code && e.Message.Contains(says, StringComparison.Ordinal)), $"{what}: expected {code} saying \"{says}\", found {(errors.Count == 0 ? "no error" : string.Join("; ", errors.Select(e => e.ToString())))}");
    }

    public static TheoryData<string, SourceFile[]> Accepted => new()
    {
        { "a telemetry roll-up keyed by the identity of the element it writes", View(Loaded("c: i.k"), LoadType) },
        { "a list of entities and a list of any value", View(Plain, """
            entity Bag { k: Text; members?: List<Entity>; anything?: List<Any>; }
            identity bag for Bag version "1" { k: Text by exact; }
            rows BagRow { k: Text; members?: List<Entity>; anything?: List<Any>; }
            projection bags ingestion output v {
              in b: rows BagRow;
              rule observed readonly "test" { emit entity Bag from b identity bag(k: k) set { k: k, members: members, anything: anything }; }
            }
            """) },
        { "a reference to the entity type the property names", View("  let m = match master (o: t.Other);\n  emit entity Out from m identity out(k: o.k) set { k: o.k, owner: o };") },
        { "a label, and a relationship with a kind that is also a hierarchy", View(Plain, "entity Named { k: Text; title?: Text label; }\nrelationship Under (Named) -> (Named) hierarchy kind composition;\nrelationship Uses (Named) -> (Named) kind dependency { weight?: Integer; }") },
        { "a fill and a time to live, and a threshold that names no data", View(Quiet("i.hits fill 0 within 3m ttl 2m", Said)) },
        { "a time to live without a fill, on a gauge, and a threshold that ignores no data", View(Quiet("i.level within 3m ttl 2m", " nodata ignore", "level")) },
        { "a roll-up in no data when one member is", View("  let s = match master (i: t.Item) |> series master i.hits fill 0 within 3m ttl 2m |> rollup by { c: i.g } combine { v: sum(hits) } nodata any\n    |> threshold v by { c: c } above 3 for 2 then \"on\" below 1 for 2 then \"off\" initial \"off\" nodata then \"silent\" as state;\n  emit entity Out from s identity out(k: c) set { k: c, flag: state };") },
        { "one projection for two graphs of a space", View(Plain, space: "graph first = v(master: master);\n  graph second = v(master: master);") },
        { "a threshold whose parameter is a negated column", View(Signals + " |> select { c: i.k, windowStart: windowStart, v: hits, low: 0 - 1 }\n    |> threshold v by { c: c } above -low for 2 then \"on\" below low for 2 then \"off\" initial \"off\" as state;\n  emit entity Out from s identity out(k: c) set { k: c, flag: state };") },
        { "a text with every escape", View("  let m = match master (i: t.Item) |> select { k: i.k, flag: \"quote \\\" backslash \\\\ tab \\t newline \\n bell \u0007 form \f back \b\" };\n  emit entity Out from m identity out(k: k) set { k: k, flag: flag };") },
    };

    [Theory, MemberData(nameof(Accepted))]
    public void A_right_definition_checks_and_its_printed_form_checks_to_the_same_module(string what, SourceFile[] files)
    {
        var (catalog, errors) = Load(files);
        Assert.True(errors.Count == 0, $"{what}: {string.Join("; ", errors.Select(e => e.ToString()))}");
        var printed = Printer.Print(catalog.Graph, "v");
        var (again, errorsAgain) = Load([files[0], new SourceFile("v.sattle", printed)]);
        Assert.True(errorsAgain.Count == 0, $"{what}, printed:\n{printed}\n{string.Join("; ", errorsAgain.Select(e => e.ToString()))}");
        Assert.Equal(catalog.Modules["v"].Digest, again.Modules["v"].Digest);
    }

    /// <summary>Def. 7.15: a window can only be known to be covered when a source feeds the property. A fill of a property that nothing writes is refused when the space is activated.</summary>
    [Fact]
    public void A_fill_of_a_property_that_no_source_feeds_is_refused_at_activation()
    {
        var files = View("  let m = match master (i: t.Item);\n  emit entity Beat from m identity beat(k: i.k) set { k: i.k };", """
            entity Beat { k: Text; beats?: Integer telemetry delta sum every 60s; }
            identity beat for Beat version "1" { k: Text by exact; }
            projection read view forward output v {
              in view: graph v;
              let s = match view (b: Beat) |> series view b.beats fill 0 within 3m ttl 2m |> select { c: b.k, windowStart: windowStart, v: beats, noData: noData }
                |> threshold v by { c: c } above 3 for 2 then "on" below 1 for 2 then "off" initial "off" nodata then "silent" as state;
              emit entity Out from s identity out(k: c) set { k: c, flag: state };
            }
            """, "graph view = v(master: master);\n  graph second = read(view: view);");
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
        var files = View(Quiet("i.hits fill 0 within 3m ttl 2m", Said));
        files = [files[0], files[1] with { Text = files[1].Text.Replace("source a adapter \"sattle.authoring\"", $"source a adapter \"{adapter}\"") }];
        var (catalog, errors) = Load(files);
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => e.ToString())));
        if (refusal is null) { using var engine = new Engine(catalog, catalog.Spaces.Keys.Single()); Assert.True(engine.WantsProgress); }
        else Assert.Contains(refusal, Assert.Throws<InvalidOperationException>(() => new Engine(catalog, catalog.Spaces.Keys.Single())).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_graphs_of_one_projection_both_hold_its_output()
    {
        var (catalog, errors) = Load(View(Plain, space: "graph first = v(master: master);\n  graph second = v(master: master);"));
        Assert.Empty(errors);
        using var engine = new Engine(catalog, catalog.Spaces.Keys.Single());
        var w = new RowWriter(engine.RowLayout("a"), engine.Interner);
        w.Begin(1); w.Text("k", "k0"); w.Text("g", "g0"); w.Int(w.Col("n"), 1); w.Instant("t", 0);
        engine.Ingest("a", w.Build()!, 1);
        Assert.Equal(["first", "master", "second"], engine.Graphs.Select(g => g.Name).Order());
        Assert.All(engine.Graphs.Where(g => g.Name != "master"), g => Assert.Single(g.State.Entities));
    }
}
