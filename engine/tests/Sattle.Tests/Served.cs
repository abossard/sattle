using System.Text.Json.Nodes;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Reads of what a host serves, for tests that assert on answers of the protocol and on nothing inside the engine: the elements of a graph's snapshot by type, and a property of a served element.</summary>
static class Served
{
    /// <summary>The id an answer wraps as `{ "value": id }`.</summary>
    public static string Id(this JsonNode? wrapped) => wrapped!["value"]!.GetValue<string>();

    /// <summary>True when a served type or property name is `name`: plain, or with its module before it where two modules of the graph use the name (`acme.svc.DependsOn`).</summary>
    static bool Names(string served, string name) => served == name || served.EndsWith("." + name, StringComparison.Ordinal);

    /// <summary>True when a served element has the type `type`.</summary>
    public static bool Is(this JsonNode element, string type) => Names(element["type"].Id(), type);

    public static List<JsonNode> Entities(this SpaceHost h, string graph, string? type = null) => Of(h.Snapshot(graph, null), "entities", type);
    public static List<JsonNode> Relationships(this SpaceHost h, string graph, string? type = null) => Of(h.Snapshot(graph, null), "relationships", type);

    /// <summary>The same reads on the replay of a scenario. The run of `sattle replay` is no host; its export writes the snapshot a host serves (the files of `replay --out`), so a test reads both the same way.</summary>
    public static List<JsonNode> Entities(this ReplayTests.Run r, string graph, string? type = null) => Of(Snapshot(r, graph), "entities", type);
    public static List<JsonNode> Relationships(this ReplayTests.Run r, string graph, string? type = null) => Of(Snapshot(r, graph), "relationships", type);

    /// <summary>The patches of a graph in a replay, as a host serves them.</summary>
    public static List<JsonNode> Patches(this ReplayTests.Run r, string graph) => r.Read(x => r.Export.PatchesOf(graph).Select(p => x.Patch(p)).ToList());

    /// <summary>A read of what the export of a replay writes. A shared run is read by several test classes at once, and an export keeps the names it has written in tables that take one writer: every such read goes through here, one at a time.</summary>
    public static T Read<T>(this ReplayTests.Run r, Func<CanvasExport, T> read) { lock (r.Export) return read(r.Export); }

    static JsonNode Snapshot(ReplayTests.Run r, string graph) => r.Read(x => x.SnapshotOf(r.Engine.Graph(graph).State, new JsonObject()));

    static List<JsonNode> Of(JsonNode snapshot, string kind, string? type) =>
        [.. snapshot["extent"]![kind]!.AsArray().Select(x => x!).Where(x => type is null || x.Is(type))];

    /// <summary>The definition file that holds the text of a projection (`module.name`), as `/definitions` names it: an engine test names no language and no file extension.</summary>
    public static string ProjectionFile(this SpaceHost h, string projection) =>
        h.Definitions().AsArray().SelectMany(m => m!["projections"]!.AsArray()).Single(p => p!["projection"]!.GetValue<string>() == projection)!["file"]!.GetValue<string>();

    /// <summary>The value of a property of a served element as it is served (`{ "kind", "value" }`), null when the element has none.</summary>
    public static JsonNode? Value(this JsonNode element, string property) =>
        element["properties"]!.AsArray().FirstOrDefault(p => Names(p!["property"].Id(), property))?["value"];

    /// <summary>The value of a property of a served element as text, null when the element has none.</summary>
    public static string? Prop(this JsonNode element, string property) => element.Value(property)?["value"]?.ToString();

    /// <summary>The windows of a property of a graph that the range read of a host serves: per window and element, the value. `Window` names the window (its start) and is the same text for every property of the graph.</summary>
    public static List<(string Window, string Element, decimal Value)> Windows(this SpaceHost h, string graph, string property) =>
        [.. h.Windows(graph, null, null, null, null, batches: false)["series"]!.AsArray().Where(s => Names(s!["property"]!.GetValue<string>(), property))
            .SelectMany(s => s!["points"]!.AsArray().Select(p => (p!["windowStart"]!.GetValue<string>(), s["element"]!.GetValue<string>(), decimal.Parse(p["valueText"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture))))];

    /// <summary>The same on the replay of a scenario, from the window batches its export writes: `Window` is the number of the window, counted from the first window of the graph.</summary>
    public static List<(string Window, string Element, decimal Value)> Windows(this ReplayTests.Run r, string graph, string property) =>
        [.. r.Read(x => x.WindowBatches(r.Engine.Graph(graph)).ToList()).Where(b => Names(b["property"]!.GetValue<string>(), property))
            .SelectMany(b => b["ids"]!.AsArray().Zip(b["values"]!.AsArray(), (id, v) => (b["tick"]!.ToString(), id!.GetValue<string>(), (decimal)v!.GetValue<double>())))];
}
