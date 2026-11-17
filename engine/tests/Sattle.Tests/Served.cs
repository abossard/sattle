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

    public static List<JsonNode> Entities(this SpaceHost h, string graph, string? type = null) => Of(h, graph, "entities", type);
    public static List<JsonNode> Relationships(this SpaceHost h, string graph, string? type = null) => Of(h, graph, "relationships", type);

    static List<JsonNode> Of(SpaceHost h, string graph, string kind, string? type) =>
        [.. h.Snapshot(graph, null)["extent"]![kind]!.AsArray().Select(x => x!).Where(x => type is null || Names(x["type"].Id(), type))];

    /// <summary>The definition file that holds the text of a projection (`module.name`), as `/definitions` names it: an engine test names no language and no file extension.</summary>
    public static string ProjectionFile(this SpaceHost h, string projection) =>
        h.Definitions().AsArray().SelectMany(m => m!["projections"]!.AsArray()).Single(p => p!["projection"]!.GetValue<string>() == projection)!["file"]!.GetValue<string>();

    /// <summary>The value of a property of a served element as text, null when the element has none.</summary>
    public static string? Prop(this JsonNode element, string property) =>
        element["properties"]!.AsArray().FirstOrDefault(p => Names(p!["property"].Id(), property))?["value"]!["value"]!.ToString();
}
