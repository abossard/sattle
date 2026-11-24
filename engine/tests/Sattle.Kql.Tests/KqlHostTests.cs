using System.Text.Json.Nodes;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Kql.Tests;

/// <summary>What is KQL's own on a served space: an error of a `.kql` text is placed in that file with Kusto's code, and the operators of a preview carry ids the outline of the KQL text shows. How a host handles projection files of any language is `ProjectionFileTests` and `DraftTests` of the engine tests.</summary>
public class KqlHostTests
{
    static readonly string Definitions = Path.Combine(FindRoot(), "engine", "definitions");

    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "engine", "definitions"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    static HostOptions Options => new() { Projections = new KqlSource() };

    internal static readonly Lazy<SpaceHost> Served = new(() => new SpaceHost(Files, Options));

    static List<SourceFile> Files => Catalog.ReadDirectory(Definitions);
    internal static string Text(string file) => File.ReadAllText(Path.Combine(Definitions, file));
    /// <summary>A check of a `.kql` file that does not lower names the file and the line of the error.</summary>
    [Fact]
    public void A_check_of_a_KQL_file_places_its_errors_in_that_file()
    {
        var text = Text("acme.svc.serviceDeps.kql");
        var line = text[..text.IndexOf("name = s.name", StringComparison.Ordinal)].Count(c => c == '\n') + 1;
        var check = Served.Value.Check("acme.svc.serviceDeps.kql", text.Replace("name = s.name", "name = s.title"));
        Assert.False(check["ok"]!.GetValue<bool>());
        Assert.Contains(check["diagnostics"]!.AsArray(), d => d!["file"]!.GetValue<string>() == "acme.svc.serviceDeps.kql" && d["line"]!.GetValue<int>() == line);
        // a column the KQL text does not know is Kusto's finding, with a place in the text
        var unknown = Served.Value.Check("acme.svc.serviceDeps.kql", text.Replace("drops / flows", "drops / nosuch"))["diagnostics"]!.AsArray().First(d => d!["code"]!.GetValue<string>() == "Kusto")!;
        Assert.True(unknown["line"]!.GetValue<int>() > 0 && unknown["column"]!.GetValue<int>() > 0);
    }

    /// <summary>Each operator of a preview's rows is named by an id the outline of its projection's text shows (on an operator, a step's sink or a
    /// function's call), so an editor puts the rows after each operator under its card (research 11, section 2 e).</summary>
    [Fact]
    public void The_operators_of_a_preview_are_named_by_ids_the_outline_shows()
    {
        using var h = new SpaceHost(Files, Options);
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var ops = h.Preview(d, new JsonObject { ["include"] = new JsonArray("rows"), ["rowLimit"] = "1" })["graphs"]!.AsArray()
            .SelectMany(g => g!["operators"]?.AsArray() ?? []).Select(o => o!["op"]!.GetValue<string>()).ToList();
        var projections = ops.GroupBy(op => op[..op.IndexOf("/op:", StringComparison.Ordinal)]).ToList();
        Assert.Equal(Directory.EnumerateFiles(Definitions, "*.kql").Count(), projections.Count);
        foreach (var projection in projections)
        {
            var (module, name) = (projection.Key[2..projection.Key.IndexOf('/', StringComparison.Ordinal)], projection.Key[(projection.Key.LastIndexOf(':') + 1)..]);
            var shown = new HashSet<string>(StringComparer.Ordinal);
            void Walk(JsonNode? n)
            {
                if (n is JsonArray a) foreach (var x in a) Walk(x);
                if (n is not JsonObject o) return;
                foreach (var (k, v) in o)
                    if (k is "engine" or "expands") foreach (var id in v!.AsArray()) shown.Add(id!.GetValue<string>());
                    else if (k == "sink" && v is not null) shown.Add(v.GetValue<string>());
                    else Walk(v);
            }
            Walk(KqlAuthoring.Analyse(h.Catalog, $"{module}.{name}", Text($"{module}.{name}.kql"))["statements"]);
            Assert.Subset(shown, projection.ToHashSet(StringComparer.Ordinal));
        }
    }
}
