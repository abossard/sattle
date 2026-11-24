using System.Text.Json.Nodes;
using Sattle.Core;

namespace Sattle.Runtime;

/// <summary>Lineage of the elements of a space: which elements of the graph below an element is made of (forward), and which elements of the graphs above a lower element is part of (reverse). Both read the rows that hold an element up (`LineageLayer`), so a lifted edge names every lower edge it stands for and a lifted node every lower node, not the last row. An element emitted from an aggregate (`Group`, `Rollup`) has only the elements its group key names. Used by the host for the routes and by `replay --out` for the static files, so both give the same answers.</summary>
public sealed class LineageReader(Engine engine)
{
    public const int DefaultLimit = 100;

    sealed record Source(string Role, bool IsRel, string GraphId, string Element);
    sealed record Part(string GraphId, string Element, bool IsRel, string Role, string? Sink, string? Rule);

    static string Rev(long r) => r.ToString(System.Globalization.CultureInfo.InvariantCulture);
    Interner It => engine.Interner;

    GraphRuntime GraphOf(string g) => engine.Graphs.FirstOrDefault(x => x.Name == g) ?? throw new HostError("UnknownGraph", $"no graph {g}", 404);

    string UpstreamName(GraphRuntime rt, string port) => rt.PortSources.GetValueOrDefault(port) ?? port;

    /// <summary>The input elements of one emitted element: the complete set when its rows are known, else the group-key elements of its last row.</summary>
    (List<Source> Sources, bool Complete, long Contributions) SourcesOf(GraphRuntime rt, ElementLineage? l)
    {
        if (l is null) return ([], false, 0);
        var list = l.Sources.Select(s => new Source(s.Role, s.IsRel, UpstreamName(rt, s.GraphPort), It.Get(s.Handle)));
        if (l.FromRows) return ([.. list.Distinct().OrderBy(s => s.Role, StringComparer.Ordinal).ThenBy(s => s.Element, StringComparer.Ordinal)], true, l.Contributions);
        var last = list.ToList();
        return (last, l.Total == 0 ? rt.Ingests : last.Count > 0, last.Count);
    }

    static JsonNode SourceJson(Source s) => new JsonObject { ["graph"] = s.GraphId, ["element"] = s.Element, ["kind"] = s.IsRel ? "relationship" : "entity", ["role"] = s.Role };

    /// <summary>`GET /graphs/{g}/lineage`: where the element comes from. `limit` sources per page after the cursor `after` (the `next` of the previous page).</summary>
    public JsonNode Forward(string g, string element, int limit = DefaultLimit, string? after = null) => ForwardOf(GraphOf(g), element, limit, after);

    JsonNode ForwardOf(GraphRuntime rt, string element, int limit, string? after)
    {
        var isRel = element.StartsWith("r:", StringComparison.Ordinal);
        if (rt.State.Get(isRel ? ElementKey.Rel(element) : ElementKey.Entity(element)) is null || !It.TryFind(element, out var h)) throw new HostError("UnknownElement", $"{element} is not in {rt.Name}", 404);
        var l = rt.Out.Lineage.Of(h);
        var info = l?.Sink is null ? null : l;
        var typed = rt.Typed;
        var ops = new List<string>(); var inputRows = new List<string>();
        if (info is not null)
        {
            var cur = typed.Ops.First(o => o.Name == info.Sink);
            var seen = new HashSet<string>();
            void Walk(Sattle.Language.TOp o)
            {
                if (!seen.Add(o.Name)) return;
                foreach (var (_, src) in o.Inputs)
                    if (src.IsPort) { if (typed.Port(src.Name).Kind == "rows") inputRows.Add(Ids.Name(typed.Port(src.Name).RowTypeId!)); }
                    else Walk(typed.Ops.First(x => x.Name == src.Name));
                ops.Add(o.Name);
            }
            Walk(cur);
        }
        var (all, complete, contributions) = SourcesOf(rt, l);
        var page = all.Where(s => after is null || string.CompareOrdinal(Cursor(s), after) > 0).Take(Math.Max(limit, 1) + 1).ToList();
        var more = page.Count > Math.Max(limit, 1);
        if (more) page.RemoveAt(page.Count - 1);
        var rule = info?.Rule;
        var cls = rule is null ? null : typed.Rules.FirstOrDefault(r => r.Name == rule)?.Class;
        return new JsonObject
        {
            ["graph"] = rt.Name, ["element"] = element, ["kind"] = isRel ? "relationship" : "entity", ["projection"] = typed.Name, ["sink"] = info?.Sink, ["rule"] = rule,
            ["direction"] = cls ?? (typed.Forward ? "forward" : null), ["operators"] = new JsonArray([.. ops.Select(o => (JsonNode)o)]), ["sources"] = new JsonArray([.. page.Select(SourceJson)]),
            ["sourceCount"] = Rev(all.Count), ["contributions"] = Rev(contributions), ["complete"] = info is not null && complete,
            ["next"] = more ? Cursor(page[^1]) : null,
            ["inputs"] = new JsonArray([.. inputRows.Distinct().Select(x => (JsonNode)x)]), ["groupKey"] = null,
        };
    }

    static string Cursor(Source s) => s.Role + "\u001f" + s.Element;

    // ---- reverse ----

    (long Version, Dictionary<string, Dictionary<string, List<Part>>> ByGraph, Dictionary<string, HashSet<string>> Incomplete) reverse = (-1, [], []);

    /// <summary>For every graph, the elements of the other graphs that name one of its elements as a source: graph, element id, then the parts.</summary>
    (Dictionary<string, Dictionary<string, List<Part>>> ByGraph, Dictionary<string, HashSet<string>> Incomplete) ReverseIndex()
    {
        var version = engine.Graphs.Sum(g => g.Head) * 1_000_003 + engine.Graphs.Sum(g => g.Out.Lineage.Size);
        if (reverse.Version == version) return (reverse.ByGraph, reverse.Incomplete);
        var by = new Dictionary<string, Dictionary<string, List<Part>>>(StringComparer.Ordinal);
        var incomplete = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var rt in engine.Graphs)
        {
            foreach (var h in rt.Out.Lineage.Elements)
            {
                var info = rt.Out.Lineage.Of(h)!;
                var elem = It.Get(h); var isRel = elem.StartsWith("r:", StringComparison.Ordinal);
                if (rt.State.Get(isRel ? ElementKey.Rel(elem) : ElementKey.Entity(elem)) is null) continue;
                var (sources, complete, _) = SourcesOf(rt, info);
                foreach (var s in sources)
                {
                    if (!by.TryGetValue(s.GraphId, out var byElem)) by[s.GraphId] = byElem = new Dictionary<string, List<Part>>(StringComparer.Ordinal);
                    if (!byElem.TryGetValue(s.Element, out var parts)) byElem[s.Element] = parts = [];
                    parts.Add(new Part(rt.Name, elem, isRel, s.Role, info.Sink, info.Rule));
                    if (!complete) { if (!incomplete.TryGetValue(s.GraphId, out var set)) incomplete[s.GraphId] = set = []; set.Add(rt.Name); }
                }
            }
        }
        reverse = (version, by, incomplete);
        return (by, incomplete);
    }

    /// <summary>`GET /graphs/{g}/lineage/reverse`: the elements of the graphs above that the element of `g` is part of. `incompleteIn` names the graphs that read `g` through an aggregate, where only the group-key elements are known.</summary>
    public JsonNode Reverse(string g, string element, int limit = DefaultLimit, string? after = null)
    {
        var rt = GraphOf(g);
        var isRel = element.StartsWith("r:", StringComparison.Ordinal);
        if (rt.State.Get(isRel ? ElementKey.Rel(element) : ElementKey.Entity(element)) is null) throw new HostError("UnknownElement", $"{element} is not in {g}", 404);
        return ReverseOf(rt, element, isRel, limit, after);
    }

    JsonNode ReverseOf(GraphRuntime rt, string element, bool isRel, int limit, string? after)
    {
        var (by, incomplete) = ReverseIndex();
        var parts = by.GetValueOrDefault(rt.Name)?.GetValueOrDefault(element) ?? [];
        var ordered = parts.OrderBy(p => p.GraphId, StringComparer.Ordinal).ThenBy(p => p.Element, StringComparer.Ordinal).ThenBy(p => p.Role, StringComparer.Ordinal).Select(p => (Key: p.GraphId + "\u001f" + p.Element + "\u001f" + p.Role, P: p)).ToList();
        var page = ordered.Where(x => after is null || string.CompareOrdinal(x.Key, after) > 0).Take(Math.Max(limit, 1) + 1).ToList();
        var more = page.Count > Math.Max(limit, 1);
        if (more) page.RemoveAt(page.Count - 1);
        return new JsonObject
        {
            ["graph"] = rt.Name, ["element"] = element, ["kind"] = isRel ? "relationship" : "entity",
            ["partOf"] = new JsonArray([.. page.Select(x => (JsonNode)new JsonObject { ["graph"] = x.P.GraphId, ["element"] = x.P.Element, ["kind"] = x.P.IsRel ? "relationship" : "entity", ["role"] = x.P.Role, ["sink"] = x.P.Sink, ["rule"] = x.P.Rule })]),
            ["count"] = Rev(ordered.Count), ["next"] = more ? page[^1].Key : null,
            ["incompleteIn"] = new JsonArray([.. (incomplete.GetValueOrDefault(rt.Name) ?? []).Order(StringComparer.Ordinal).Select(x => (JsonNode)x)]),
        };
    }

    // ---- everything, for the static files ----

    /// <summary>The forward lineage of every element of a graph, complete (no paging), in id order.</summary>
    public IEnumerable<JsonNode> AllForward(string g)
    {
        var rt = GraphOf(g);
        foreach (var id in rt.State.Entities.Select(e => e.Id).Concat(rt.State.Rels.Select(r => r.Id)).Order(StringComparer.Ordinal))
            yield return ForwardOf(rt, id, int.MaxValue - 1, null);
    }

    /// <summary>The reverse lineage of every element of a graph that is part of something, complete, in id order.</summary>
    public IEnumerable<JsonNode> AllReverse(string g)
    {
        var rt = GraphOf(g);
        var (by, _) = ReverseIndex();
        var byElem = by.GetValueOrDefault(g);
        if (byElem is null) yield break;
        foreach (var id in byElem.Keys.Order(StringComparer.Ordinal))
            if (rt.State.Get(id.StartsWith("r:", StringComparison.Ordinal) ? ElementKey.Rel(id) : ElementKey.Entity(id)) is not null)
                yield return ReverseOf(rt, id, id.StartsWith("r:", StringComparison.Ordinal), int.MaxValue - 1, null);
    }
}
