using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The checker on whole definitions (Ch. 2 and 5; Prop. 5.8, Def. 3.14, Def. 7.15): a wrong definition is refused with a diagnostic of a stated code that says what is wrong, a right one loads, and no input is answered with an exception. Definitions in, diagnostics and the definition graph out. The cases come from the code reviews of the checker.</summary>
public class CheckerTests
{
    const string Base = """
        {"module": "t", "version": "1.0.0", "imports": [],
         "entities": [{"name": "Item", "properties": [{"name": "k", "type": "Text"}, {"name": "g", "type": "Text"},
                        {"name": "hits", "type": "Integer", "optional": true, "telemetry": {"temporality": "delta", "combine": "sum", "period": "1m"}},
                        {"name": "level", "type": "Decimal", "optional": true, "telemetry": {"temporality": "gauge", "combine": "max", "period": "1m"}}]},
                      {"name": "Other", "properties": [{"name": "k", "type": "Text"}]}],
         "identities": [{"name": "item", "type": "Item", "components": [{"name": "k", "type": "Text"}]}, {"name": "other", "type": "Other", "components": [{"name": "k", "type": "Text"}]}],
         "rows": [{"name": "R", "fields": [{"name": "k", "type": "Text"}, {"name": "g", "type": "Text"}, {"name": "n", "type": "Integer"}, {"name": "t", "type": "Instant"}]}]}
        """;

    const string Ingest = """
        {"graph": "ingest", "ontology": "t", "inputs": [{"name": "a", "rows": "t.R"}], "parameters": [],
         "verbs": [
           {"id": "a", "verb": "rows", "source": "a"},
           {"id": "items", "verb": "nodes", "rule": "observed", "target": {"nodes": [{"var": "i", "label": "Item", "id": {"identity": "item", "args": [{"ref": "k"}]}, "props": {"k": {"ref": "k"}, "g": {"ref": "g"}}}]}},
           {"id": "others", "verb": "nodes", "rule": "observed", "target": {"nodes": [{"var": "o", "label": "Other", "id": {"identity": "other", "args": [{"ref": "g"}]}, "props": {"k": {"ref": "g"}}}]}},
           {"id": "windows", "verb": "window", "window": {"time": "t", "period": "1m"}},
           {"id": "sums", "verb": "summarize", "by": {"k": {"ref": "k"}, "windowStart": {"ref": "windowStart"}}, "aggregates": {"hits": {"fn": "sum", "args": [{"ref": "n"}]}}},
           {"id": "hits", "verb": "nodes", "rule": "observed", "target": {"nodes": [{"var": "i", "label": "Item", "id": {"identity": "item", "args": [{"ref": "k"}]}, "extend": true, "props": {"hits": {"ref": "hits"}}}]}}],
         "wires": [{"from": "a", "to": "items"}, {"from": "a", "to": "others"}, {"from": "a", "to": "windows"}, {"from": "windows", "to": "sums"}, {"from": "sums", "to": "hits"}],
         "rules": [{"name": "observed", "description": "test"}]}
        """;

    const string OverMaster = """[{"name": "master", "ontology": "t"}]""";
    const string ViewGraph = """{"name": "view", "projection": "v.v", "inputs": {"master": "master"}}""";
    const string TopGraph = """{"name": "top", "projection": "w.w", "inputs": {"g": "view"}}""";

    static string Wire(string from, string to, string port = "in") => $$"""{"from": "{{from}}", "to": "{{to}}", "port": "{{port}}"}""";

    /// <summary>The program `graph` of the module `ontology` over the graphs `inputs`: its verbs, the wires between them, its parameters and its rules.</summary>
    static string Program(string graph, string ontology, string inputs, string verbs, string wires, string parameters = "[]", string rules = "[]") =>
        "{\"graph\": \"" + graph + "\", \"ontology\": \"" + ontology + "\", \"inputs\": " + inputs + ", \"parameters\": " + parameters
        + ", \"verbs\": [" + verbs + "], \"wires\": [" + wires + "], \"rules\": " + rules + "}";

    /// <summary>The space `s` over the source `a`: the master and the graphs `graphs`, in a module that imports `modules`.</summary>
    static string Space(string graphs, params string[] modules) =>
        """{"module": "s", "version": "1.0.0", "imports": [""" + string.Join(", ", modules.Select(m => $$"""{"module": "{{m}}", "alias": "{{m}}", "version": "1.0.0", "digest": null}"""))
        + """], "space": {"name": "s", "sources": [{"name": "a", "adapter": "record", "rows": "t.R"}], "graphs": [{"name": "master", "projection": "t.ingest", "master": true, "inputs": {"a": "a"}}, """
        + graphs + "]}}";

    /// <summary>A view over the base module: `types` are extra declarations of the view's module (arrays of a module document, appended to its own), `verbs` and `wires` the program of its projection `v`, `graphs` the graphs of the space beside the master.</summary>
    static SourceFile[] View(string verbs, string wires, string types = "{}", string graphs = ViewGraph, string parameters = "[]", string rules = "[]")
    {
        var module = JsonNode.Parse("""
            {"module": "v", "version": "1.0.0", "imports": [{"module": "t", "alias": "t", "version": "1.0.0", "digest": null}],
             "entities": [{"name": "Out", "properties": [{"name": "k", "type": "Text"}, {"name": "flag", "type": "Text", "optional": true}]}],
             "identities": [{"name": "out", "type": "Out", "components": [{"name": "k", "type": "Text"}]}]}
            """)!.AsObject();
        foreach (var (key, more) in JsonNode.Parse(types)!.AsObject().ToList())
        {
            if (module[key] is not JsonArray list) module[key] = list = new JsonArray();
            foreach (var x in more!.AsArray()) list.Add(x!.DeepClone());
        }
        return
        [
            new("t.module.gkql.json", Base), new("t.ingest.gkql.json", Ingest), new("v.module.gkql.json", module.ToJsonString()),
            new("v.v.gkql.json", Program("v", "v", OverMaster, verbs, wires, parameters, rules)),
            new("s.module.gkql.json", Space(graphs, "t", "v")),
        ];
    }

    /// <summary>The space with a graph `top` of the projection `w.w` over the graph `view`, with the module `w` and its program.</summary>
    static SourceFile[] WithTop(SourceFile[] files, string module, string program) =>
    [
        .. files.Where(f => f.Path != "s.module.gkql.json"),
        new("w.module.gkql.json", module), new("w.w.gkql.json", program),
        new("s.module.gkql.json", Space(ViewGraph + ", " + TopGraph, "t", "v", "w")),
    ];

    /// <summary>A lens `top` over the lens view `view`: `view` edits the master's items by `k`, `top` edits the elements of `view` by `k`.</summary>
    static SourceFile[] LensOverLens() => WithTop(
        View("""
            {"id": "items", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "i", "label": "Item"}]}},
            {"id": "outs", "verb": "nodes", "rule": "items", "target": {"nodes": [{"var": "o", "label": "Out", "id": {"ref": "i"}, "props": {"k": {"ref": "i.k"}}}]}}
            """, Wire("items", "outs"), rules: """[{"name": "items", "update": ["k"]}]"""),
        """{"module": "w", "version": "1.0.0", "imports": [{"module": "v", "alias": "v", "version": "1.0.0", "digest": null}], "entities": [{"name": "Top", "properties": [{"name": "k", "type": "Text"}]}]}""",
        Program("w", "w", """[{"name": "g", "ontology": "v"}]""", """
            {"id": "outs", "verb": "graph-match", "graph": "g", "pattern": {"nodes": [{"var": "x", "label": "Out"}]}},
            {"id": "tops", "verb": "nodes", "rule": "tops", "target": {"nodes": [{"var": "y", "label": "Top", "id": {"ref": "x"}, "props": {"k": {"ref": "x.k"}}}]}}
            """, Wire("outs", "tops"), rules: """[{"name": "tops", "update": ["k"]}]"""));

    /// <summary>The items of the master with their keys and groups, in the verbs `items` and `fields`.</summary>
    const string Items = """
        {"id": "items", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "i", "label": "Item"}]}},
        {"id": "fields", "verb": "project", "columns": {"i": {"ref": "i"}, "k": {"ref": "i.k"}, "g": {"ref": "i.g"}}}
        """;
    static readonly string FromItems = Wire("items", "fields");
    const string Out = """{"id": "out", "verb": "nodes", "target": {"nodes": [{"var": "o", "label": "Out", "id": {"identity": "out", "args": [{"ref": "k"}]}, "props": {"k": {"ref": "k"}}}]}}""";
    static SourceFile[] Plain(string types = "{}", string graphs = ViewGraph) => View(Items + ", " + Out, FromItems + ", " + Wire("fields", "out"), types, graphs);
    /// <summary>The windows of the items' hits within three minutes.</summary>
    const string Signals = """{"id": "series", "verb": "series", "graph": "master", "series": {"element": "i", "property": "hits", "lookback": "3m"}}""";
    const string Said = ", \"noData\": \"silent\"";

    /// <summary>The windows of `value` over three minutes with the further series options `series`, a threshold with the given no-data state over them, and a sink.</summary>
    static SourceFile[] Quiet(string series, string noData, string value = "hits") => View(
        Items + ", {\"id\": \"series\", \"verb\": \"series\", \"graph\": \"master\", \"series\": {\"element\": \"i\", \"property\": \"" + value + "\", \"lookback\": \"3m\"" + series + "}}, "
        + "{\"id\": \"state\", \"verb\": \"hysteresis\", \"hysteresis\": {\"by\": [\"k\"], \"value\": \"" + value + "\", \"above\": 3.0, \"enterAfter\": 2, \"below\": 1.0, \"exitAfter\": 2, \"high\": \"on\", \"low\": \"off\", \"initial\": \"off\"" + noData + "}}, "
        + """{"id": "out", "verb": "nodes", "target": {"nodes": [{"var": "o", "label": "Out", "id": {"identity": "out", "args": [{"ref": "k"}]}, "props": {"k": {"ref": "k"}, "flag": {"ref": "state"}}}]}}""",
        string.Join(", ", FromItems, Wire("fields", "series"), Wire("series", "state"), Wire("state", "out")));

    const string LoadType = """
        {"entities": [{"name": "Load", "properties": [{"name": "k", "type": "Text"}, {"name": "hits", "type": "Integer", "optional": true, "telemetry": {"temporality": "delta", "combine": "sum", "period": "1m"}}]}],
         "identities": [{"name": "load", "type": "Load", "components": [{"name": "k", "type": "Text"}]}]}
        """;
    /// <summary>Loads per item, rolled up by `keys` (with the window) and written to the item's Load element.</summary>
    static string Loaded(string keys) => Items + ", " + Signals + ", "
        + """{"id": "loads", "verb": "nodes", "target": {"nodes": [{"var": "l", "label": "Load", "id": {"identity": "load", "args": [{"ref": "k"}]}, "props": {"k": {"ref": "k"}}}]}}, """
        + "{\"id\": \"rolled\", \"verb\": \"summarize\", \"by\": {" + keys + ", \"windowStart\": {\"ref\": \"windowStart\"}}, \"aggregates\": {\"v\": {\"fn\": \"sum\", \"args\": [{\"ref\": \"hits\"}]}}}, "
        + """{"id": "load", "verb": "nodes", "target": {"nodes": [{"var": "l", "label": "Load", "id": {"identity": "load", "args": [{"ref": "c"}]}, "extend": true, "props": {"hits": {"ref": "v"}}}]}}""";
    static readonly string LoadedWires = string.Join(", ", FromItems, Wire("fields", "loads"), Wire("fields", "series"), Wire("series", "rolled"), Wire("rolled", "load"));
    const string ByKey = "\"c\": {\"ref\": \"k\"}";

    static (Catalog Catalog, List<Diagnostic> Errors) Load(SourceFile[] files)
    {
        var catalog = new Catalog(Repo.Projections);
        return (catalog, [.. catalog.Load(files, updatePins: true).Diagnostics.Where(d => d.IsError)]);
    }

    /// <summary>A graph `view` that holds only relationships between items and others of the master, so both ends are references there, and a projection `w` over it whose verbs are `verbs` and wires `wires`.</summary>
    static SourceFile[] OverReferences(string verbs, string wires) => WithTop(
        View(Items + ", " + """
            {"id": "others", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "x", "label": "Other"}]}},
            {"id": "names", "verb": "project", "columns": {"o2": {"ref": "x"}, "ok": {"ref": "x.k"}}},
            {"id": "paired", "verb": "join", "kind": "inner", "on": [{"left": "g", "right": "ok"}]},
            {"id": "pairs", "verb": "edges", "target": {"nodes": [{"var": "s", "label": "Item", "id": {"ref": "i"}}, {"var": "t", "label": "Other", "id": {"ref": "o2"}}], "edges": [{"var": "e", "label": "Paired", "from": "s", "to": "t"}]}}
            """, string.Join(", ", FromItems, Wire("others", "names"), Wire("fields", "paired", "left"), Wire("names", "paired", "right"), Wire("paired", "pairs")),
            """{"relationships": [{"name": "Paired", "from": "t.Item", "to": "t.Other"}]}"""),
        """
        {"module": "w", "version": "1.0.0", "imports": [{"module": "t", "alias": "t", "version": "1.0.0", "digest": null}, {"module": "v", "alias": "v", "version": "1.0.0", "digest": null}],
         "entities": [{"name": "Top", "properties": [{"name": "n", "type": "Integer"}]}], "identities": [{"name": "top", "type": "Top", "components": [{"name": "name", "type": "Text"}]}]}
        """,
        Program("w", "w", """[{"name": "g", "ontology": "v"}]""", verbs, wires));

    /// <summary>A view with one text parameter `only`, bound by the space as `binding` says (Prop. 5.8: a parameter is a constant of the binding; `DeployHealthTests` runs one projection under two bindings).</summary>
    static SourceFile[] Bound(string binding) => View(
        Items + ", " + """{"id": "chosen", "verb": "where", "condition": {"fn": "==", "args": [{"ref": "g"}, {"param": "only"}]}}, """ + Out,
        string.Join(", ", FromItems, Wire("fields", "chosen"), Wire("chosen", "out")),
        graphs: "{\"name\": \"view\", \"projection\": \"v.v\", \"inputs\": {\"master\": \"master\"" + binding + "}}",
        parameters: """[{"name": "only", "kind": "Text"}]""");

    /// <summary>Instances with two keys, a uid and a name, that an equivalence joins, and a view `v` over them whose verbs are `verbs` and wires `wires`.</summary>
    static SourceFile[] OverLinked(string verbs, string wires) =>
    [
        new("l.module.gkql.json", """
            {"module": "l", "version": "1.0.0", "imports": [],
             "entities": [{"name": "Instance", "properties": [{"name": "uid", "type": "Text", "optional": true}, {"name": "name", "type": "Text", "optional": true}]}],
             "identities": [{"name": "instanceUid", "type": "Instance", "components": [{"name": "uid", "type": "Text"}]},
                            {"name": "instanceName", "type": "Instance", "components": [{"name": "name", "type": "Text", "normalize": "lowerInvariant"}]}],
             "equivalences": [{"name": "uidIsName", "left": "instanceUid", "right": "instanceName", "mode": "assert", "threshold": 1.0}],
             "rows": [{"name": "InstanceRow", "fields": [{"name": "uid", "type": "Text"}, {"name": "name", "type": "Text"}]}]}
            """),
        new("l.ingest.gkql.json", """
            {"graph": "ingest", "ontology": "l", "inputs": [{"name": "instances", "rows": "l.InstanceRow"}], "parameters": [],
             "verbs": [
               {"id": "instances", "verb": "rows", "source": "instances"},
               {"id": "observed", "verb": "nodes", "rule": "observed", "target": {"nodes": [{"var": "p", "label": "Instance", "id": {"identity": "instanceUid", "args": [{"ref": "uid"}]}, "props": {"uid": {"ref": "uid"}, "name": {"ref": "name"}}}]}},
               {"id": "linked", "verb": "link", "rule": "observed", "link": {"policy": "uidIsName", "left": {"identity": "instanceUid", "args": [{"ref": "uid"}]}, "right": {"identity": "instanceName", "args": [{"ref": "name"}]}, "confidence": 1.0}}],
             "wires": [{"from": "instances", "to": "observed"}, {"from": "instances", "to": "linked"}]}
            """),
        new("lv.module.gkql.json", """{"module": "lv", "version": "1.0.0", "imports": [{"module": "l", "alias": "base", "version": "1.0.0", "digest": null}], "entities": [{"name": "Tag", "properties": [{"name": "name", "type": "Text", "optional": true}]}]}"""),
        new("lv.v.gkql.json", Program("v", "lv", """[{"name": "master", "ontology": "l"}]""", verbs, wires)),
    ];

    public static TheoryData<string, SourceFile[], string, string> Refused => new()
    {
        { "a union of element columns of two types", View("""
            {"id": "items", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "i", "label": "Item"}]}},
            {"id": "a", "verb": "project", "columns": {"x": {"ref": "i"}}},
            {"id": "others", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "o", "label": "Other"}]}},
            {"id": "b", "verb": "project", "columns": {"x": {"ref": "o"}}},
            {"id": "both", "verb": "union"},
            {"id": "out", "verb": "nodes", "target": {"nodes": [{"var": "n", "label": "Out", "id": {"identity": "out", "args": [{"lit": "k"}]}, "props": {"k": {"lit": "k"}}}]}}
            """, string.Join(", ", Wire("items", "a"), Wire("others", "b"), Wire("a", "both"), Wire("b", "both"), Wire("both", "out"))), "Union", "union inputs must have the same columns" },
        { "a row of a table of values with another type than its column", View("""
            {"id": "names", "verb": "values", "values": {"columns": {"tk": "Text", "label": "Text"}, "rows": [{"tk": "g1", "label": "L1"}, {"tk": "g2", "label": 2}]}},
            {"id": "out", "verb": "nodes", "target": {"nodes": [{"var": "o", "label": "Out", "id": {"identity": "out", "args": [{"ref": "tk"}]}, "props": {"k": {"ref": "tk"}, "flag": {"ref": "label"}}}]}}
            """, Wire("names", "out")), "Program", "column label needs a Text, Integer, Decimal or Boolean value of its declared type Text" },
        { "a property read of a reference: the graph holds the relationship, not its ends (Def. 3.14)", OverReferences("""
            {"id": "pairs", "verb": "graph-match", "graph": "g", "pattern": {"nodes": [{"var": "i", "label": "Item"}, {"var": "o", "label": "Other"}], "edges": [{"var": "p", "label": "Paired", "from": "i", "to": "o"}]}},
            {"id": "counts", "verb": "summarize", "by": {"k": {"ref": "i.g"}}, "aggregates": {"n": {"fn": "count"}}},
            {"id": "tops", "verb": "nodes", "target": {"nodes": [{"var": "x", "label": "Top", "id": {"identity": "top", "args": [{"ref": "k"}]}, "props": {"n": {"ref": "n"}}}]}}
            """, string.Join(", ", Wire("pairs", "counts"), Wire("counts", "tops"))), "S4", "graph view holds no Item" },
        { "a parameter the space does not bind (Prop. 5.8)", Bound(""), "S2", "parameter only of v is not bound" },
        { "a parameter bound with a constant of another kind (Prop. 5.8)", Bound(", \"only\": {\"lit\": 5}"), "S2", "parameter only is Text, the binding gives Integer" },
        { "a binding of a parameter the projection does not have (Prop. 5.8)", Bound(", \"only\": {\"lit\": \"a\"}, \"other\": {\"lit\": 1}"), "UnknownPort", "has no port 'other'" },
        { "a view that names its elements by a key of a domain an equivalence joins: after a link the row that leaves would compute the survivor's id", OverLinked("""
            {"id": "instances", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "p", "label": "Instance"}]}},
            {"id": "names", "verb": "project", "columns": {"p": {"ref": "p"}, "name": {"ref": "p.name"}}},
            {"id": "tags", "verb": "nodes", "target": {"nodes": [{"var": "t", "label": "Tag", "id": {"identity": "instanceName", "args": [{"ref": "name"}]}, "props": {"name": {"ref": "name"}}}]}}
            """, string.Join(", ", Wire("instances", "names"), Wire("names", "tags"))), "IdentityLinked", "keep its id (p.id)" },
        { "the condition of an if is telemetry, so its result is: it is no value of a data property", View(Items + ", " + Signals + ", " + """
            {"id": "out", "verb": "nodes", "target": {"nodes": [{"var": "o", "label": "Out", "id": {"identity": "out", "args": [{"ref": "k"}]}, "props": {"k": {"ref": "k"}, "flag": {"fn": "iff", "args": [{"fn": ">", "args": [{"ref": "hits"}, {"lit": 2}]}, {"lit": "hot"}, {"lit": "cold"}]}}}]}}
            """, string.Join(", ", FromItems, Wire("fields", "series"), Wire("series", "out"))), "RoleInterference", "telemetry value" },
        { "a key and the state of a threshold of one name", View("""
            {"id": "items", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "i", "label": "Item"}]}},
            {"id": "keys", "verb": "project", "columns": {"i": {"ref": "i"}, "state": {"ref": "i.k"}}},
            {"id": "series", "verb": "series", "graph": "master", "series": {"element": "i", "property": "hits", "lookback": "3m"}},
            {"id": "level", "verb": "hysteresis", "hysteresis": {"by": ["state"], "value": "hits", "above": 3.0, "enterAfter": 2, "below": 1.0, "exitAfter": 2, "high": "on", "low": "off", "initial": "off"}},
            {"id": "out", "verb": "nodes", "target": {"nodes": [{"var": "o", "label": "Out", "id": {"identity": "out", "args": [{"ref": "state"}]}, "props": {"k": {"ref": "state"}}}]}}
            """, string.Join(", ", Wire("items", "keys"), Wire("keys", "series"), Wire("series", "level"), Wire("level", "out"))), "DuplicateColumn", "'state'" },
        { "two labels on one type", Plain("""{"entities": [{"name": "Named", "properties": [{"name": "k", "type": "Text", "label": true}, {"name": "title", "type": "Text", "label": true}]}]}"""), "Label", "second" },
        { "a label that is no text", Plain("""{"entities": [{"name": "Named", "properties": [{"name": "k", "type": "Text"}, {"name": "rank", "type": "Integer", "label": true}]}]}"""), "Label", "must be a Text property" },
        { "a fill on a gauge", Quiet(", \"freshness\": \"2m\", \"fill\": 0.0", Said, "level"), "Series", "takes no fill" },
        { "a fill of an Integer property that is no integer", Quiet(", \"freshness\": \"2m\", \"fill\": 0.5", Said), "Series", "must be an integer" },
        { "a time to live below one window", Quiet(", \"freshness\": \"30s\"", Said), "Series", "at least one window" },
        { "a time to live above the look-back", Quiet(", \"freshness\": \"4m\"", Said), "Series", "at most the look-back less one window" },
        { "a time to live as long as the look-back", Quiet(", \"freshness\": \"3m\"", Said), "Series", "at most the look-back less one window" },
        { "a threshold over a series with a time to live that does not say what no data gives", Quiet(", \"freshness\": \"2m\"", ""), "Threshold", "what no data gives" },
        { "a no-data state without a time to live upstream", Quiet("", Said), "Threshold", "needs telemetry with a time to live" },
        { "a telemetry roll-up keyed finer than the element it writes (premise B1)", View(Loaded(ByKey + ", \"g\": {\"ref\": \"g\"}"), LoadedWires, LoadType), "B1", "grouped by 'g'" },
        { "a lens over a lens view: the engine runs one lens over a bijective rule (Ch. 8)", LensOverLens(), "Unsupported", "rule tops: a lens over the lens view view is later work" },
        { "a lens rule whose rows pass through a join: a join is not a lens step", View(Items + ", " + """
            {"id": "others", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "x", "label": "Other"}]}},
            {"id": "names", "verb": "project", "columns": {"gx": {"ref": "x.k"}}},
            {"id": "paired", "verb": "join", "kind": "inner", "on": [{"left": "g", "right": "gx"}]},
            {"id": "outs", "verb": "nodes", "rule": "items", "target": {"nodes": [{"var": "o", "label": "Out", "id": {"ref": "i"}, "props": {"k": {"ref": "k"}}}]}}
            """, string.Join(", ", FromItems, Wire("others", "names"), Wire("fields", "paired", "left"), Wire("names", "paired", "right"), Wire("paired", "outs")),
            rules: """[{"name": "items", "update": ["k"]}]"""), "DirectionOverclaim", "(Join) is many-to-one or lossy" },
    };

    [Theory, MemberData(nameof(Refused))]
    public void A_wrong_definition_is_refused_with_a_diagnostic(string what, SourceFile[] files, string code, string says)
    {
        var (_, errors) = Load(files);
        Assert.True(errors.Any(e => e.Code == code && e.Message.Contains(says, StringComparison.Ordinal)), $"{what}: expected {code} saying \"{says}\", found {(errors.Count == 0 ? "no error" : string.Join("; ", errors.Select(e => e.ToString())))}");
    }

    /// <summary>A view with a window (in the base), a roll-up, the last aggregates of one row and a join of two row sets: each a form that gkql writes.</summary>
    static readonly Lazy<Graph> Written = new(() =>
    {
        var (catalog, errors) = Load(View(Loaded(ByKey) + ", " + """
            {"id": "groups", "verb": "summarize", "by": {"g": {"ref": "g"}}, "aggregates": {"n": {"fn": "count"}}},
            {"id": "top", "verb": "summarize", "by": {"c": {"ref": "g"}}, "aggregates": {"most": {"fn": "arg_max", "args": [{"ref": "n"}, {"ref": "g"}]}}},
            {"id": "outs", "verb": "nodes", "target": {"nodes": [{"var": "o", "label": "Out", "id": {"identity": "out", "args": [{"ref": "c"}]}, "props": {"k": {"ref": "c"}}}]}},
            {"id": "others", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "x", "label": "Other"}]}},
            {"id": "names", "verb": "project", "columns": {"gx": {"ref": "x.k"}, "ok": {"ref": "x.k"}}},
            {"id": "paired", "verb": "join", "kind": "inner", "on": [{"left": "g", "right": "gx"}]},
            {"id": "flags", "verb": "nodes", "target": {"nodes": [{"var": "o", "label": "Out", "id": {"identity": "out", "args": [{"ref": "k"}]}, "extend": true, "props": {"flag": {"ref": "ok"}}}]}}
            """, string.Join(", ", LoadedWires, Wire("fields", "groups"), Wire("groups", "top"), Wire("top", "outs"), Wire("others", "names"),
                Wire("fields", "paired", "left"), Wire("names", "paired", "right"), Wire("paired", "flags")), LoadType));
        Assert.Empty(errors);
        return catalog.Graph;
    });

    static Entity First(Graph g, string type, Func<Entity, bool>? where = null) => g.Entities.First(e => Ids.Name(e.Type) == type && (where?.Invoke(e) ?? true));
    static List<Edit> Set(Entity e, Props props) => [Edit.Change(e, e with { Props = props })];

    /// <summary>The forms no gkql program writes (#81) and what refuses each when its facts come another way: the kernel refuses a late policy other than revise and a no-data rule on a roll-up, the checker a last without its row mark and a join condition that is no key.</summary>
    public static TheoryData<string, Func<Graph, List<Edit>>, bool, string, string> Unwritable => new()
    {
        { "a window that ignores late rows", g => { var w = First(g, "Window"); return Set(w, w.Props.With(Ids.KProp("late"), Value.Text("ignore"))); }, true, "Enum", "late = \"ignore\" is not one of revise" },
        { "a roll-up that gives no data when one member has none", g => { var r = First(g, "Rollup"); return Set(r, r.Props.With(Ids.KProp("nodata"), Value.Text("any"))); }, true, "UnknownProperty", "nodata is not declared for Rollup" },
        { "a last without its row mark", g => { var a = First(g, "Aggregate", e => e.Props.Text(Ids.KProp("fn")) == "last"); return Set(a, a.Props.Without(Ids.KProp("row"))); }, false, "Unsupported", "last needs a row mark" },
        { "a join condition that reads the left rows only", g =>
            {
                var join = First(g, "Join");
                return [.. g.Entities.Where(e => Ids.Name(e.Type) == "FieldRef" && e.Id.StartsWith(join.Id + "/", StringComparison.Ordinal)).SelectMany(f => Set(f, f.Props.With(Ids.KProp("path"), Value.Text("k"))))];
            }, false, "Join", "a join condition is equalities between a left and a right expression" },
    };

    [Theory, MemberData(nameof(Unwritable))]
    public void A_form_that_gkql_cannot_write_is_refused_when_its_facts_come_another_way(string what, Func<Graph, List<Edit>> edit, bool kernel, string code, string says)
    {
        var g = Written.Value.Clone();
        Assert.Null(g.Apply(edit(g)));
        var found = kernel ? Conformance.Check(g, OntologyView.Build(g, [Ids.KernelModule])) : new ProjectionChecker(g).Check("d:v/projection:v").Diagnostics;
        Assert.True(found.Any(d => d.IsError && d.Code == code && d.Message.Contains(says, StringComparison.Ordinal)), $"{what}: expected {code} saying \"{says}\", found {string.Join("; ", found.Select(d => d.ToString()))}");
    }

    /// <summary>Def. 7.15: a window can only be known to be covered when a source feeds the property. A fill of a property that nothing writes is refused when the space is activated.</summary>
    [Fact]
    public void A_fill_of_a_property_that_no_source_feeds_is_refused_at_activation()
    {
        var files = View(Items + ", " + """
            {"id": "beats", "verb": "nodes", "target": {"nodes": [{"var": "b", "label": "Beat", "id": {"identity": "beat", "args": [{"ref": "k"}]}, "props": {"k": {"ref": "k"}}}]}}
            """, string.Join(", ", FromItems, Wire("fields", "beats")), """
            {"entities": [{"name": "Beat", "properties": [{"name": "k", "type": "Text"}, {"name": "beats", "type": "Integer", "optional": true, "telemetry": {"temporality": "delta", "combine": "sum", "period": "1m"}}]}],
             "identities": [{"name": "beat", "type": "Beat", "components": [{"name": "k", "type": "Text"}]}]}
            """, ViewGraph + """, {"name": "second", "projection": "v.read", "inputs": {"view": "view"}}""");
        files = [.. files, new("v.read.gkql.json", Program("read", "v", """[{"name": "view", "ontology": "v"}]""", """
            {"id": "beats", "verb": "graph-match", "graph": "view", "pattern": {"nodes": [{"var": "b", "label": "Beat"}]}},
            {"id": "keys", "verb": "project", "columns": {"b": {"ref": "b"}, "k": {"ref": "b.k"}}},
            {"id": "series", "verb": "series", "graph": "view", "series": {"element": "b", "property": "beats", "lookback": "3m", "freshness": "2m", "fill": 0}},
            {"id": "state", "verb": "hysteresis", "hysteresis": {"by": ["k"], "value": "beats", "above": 3.0, "enterAfter": 2, "below": 1.0, "exitAfter": 2, "high": "on", "low": "off", "initial": "off", "noData": "silent"}},
            {"id": "out", "verb": "nodes", "target": {"nodes": [{"var": "o", "label": "Out", "id": {"identity": "out", "args": [{"ref": "k"}]}, "props": {"k": {"ref": "k"}, "flag": {"ref": "state"}}}]}}
            """, string.Join(", ", Wire("beats", "keys"), Wire("keys", "series"), Wire("series", "state"), Wire("state", "out"))))];
        var (catalog, errors) = Load(files);
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => e.ToString())));
        var refused = Assert.Throws<InvalidOperationException>(() => new Engine(catalog, catalog.Spaces.Keys.Single()));
        Assert.Contains("no source feeds", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A Match reads the properties its own projection uses. One checker serves every projection of a module and keeps its demand set; before, a Match also read what a same-named operator of an earlier projection demanded.</summary>
    [Fact]
    public void A_match_reads_only_the_properties_its_own_projection_uses()
    {
        const string Copies = """{"id": "items", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "i", "label": "Item"}]}}, """;
        var wires = string.Join(", ", Wire("items", "copies"), Wire("copies", "out"));
        var (catalog, errors) = Load([.. View(Copies + """{"id": "copies", "verb": "project", "columns": {"k": {"ref": "i.k"}, "g": {"ref": "i.g"}}}, """
                + """{"id": "out", "verb": "nodes", "target": {"nodes": [{"var": "o", "label": "Out", "id": {"identity": "out", "args": [{"ref": "k"}]}, "props": {"k": {"ref": "k"}, "flag": {"ref": "g"}}}]}}""", wires),
            new SourceFile("v.second.gkql.json", Program("second", "v", OverMaster, Copies + """{"id": "copies", "verb": "project", "columns": {"k": {"ref": "i.k"}}}, """ + Out, wires))]);
        Assert.Empty(errors);
        var second = catalog.Projections.Values.Single(p => p.Name == "second").Ops.OfType<TMatch>().Single();
        Assert.Equal(["k"], second.Reads.Select(r => Ids.Name(r.PropId)));
    }

    [Fact]
    public void Two_graphs_of_one_projection_both_hold_its_output()
    {
        var (catalog, errors) = Load(Plain(graphs: """{"name": "first", "projection": "v.v", "inputs": {"master": "master"}}, {"name": "second", "projection": "v.v", "inputs": {"master": "master"}}"""));
        Assert.Empty(errors);
        using var engine = new Engine(catalog, catalog.Spaces.Keys.Single());
        var w = new RowWriter(engine.RowLayout("a"), engine.Interner);
        w.Begin(1); w.Text("k", "k0"); w.Text("g", "g0"); w.Int(w.Col("n"), 1); w.Instant("t", 0);
        engine.Ingest("a", w.Build()!, 1);
        Assert.Equal(["first", "master", "second"], engine.Graphs.Select(g => g.Name).Order());
        Assert.All(engine.Graphs.Where(g => g.Name != "master"), g => Assert.Single(g.State.Entities));
    }
}
