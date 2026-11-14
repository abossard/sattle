using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Kql.Tests;

/// <summary>Code review 2, verifier v-kql: the two reports, which the lineage test of KqlLineageTests does not cover.</summary>
public class Review2KqlLineageTests
{
    static readonly string Root = FindRoot();
    static string Definitions => Path.Combine(Root, "engine", "definitions");
    static string Estate => Path.Combine(Root, "engine", "tests", "Sattle.Tests", "fixtures", "estate");

    static readonly Lazy<(SpaceHost Today, SpaceHost Kql)> Hosts = new(() =>
    {
        var files = Catalog.ReadDirectory(Definitions).ToList();
        var put = KqlFiles.Of(files, KqlFiles.ReadDirectory(Definitions));
        Assert.True(put.Ok, string.Join("\n", put.Diagnostics));
        return (Serve(files), Serve(put.Files));
    });

    static SpaceHost Serve(List<SourceFile> files)
    {
        var h = new SpaceHost(files, new HostOptions { Cluster = "synth", LateHorizonWindows = 1_000_000 });
        new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 200, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000, Cluster = "synth" }).Run(Estate);
        h.Finish();
        return h;
    }

    static SortedSet<string> Named(JsonNode lineage) => new(lineage["sources"]!.AsArray().Select(s => s!["graph"]!.GetValue<string>() + "/" + s["element"]!.GetValue<string>()), StringComparer.Ordinal);

    /// <summary>HEAD: the count blocks of the two reports name no source elements with the KQL text, today's text names every Workload or Element.</summary>
    [Theory]
    [InlineData("deploymentReport")]
    [InlineData("healthReport")]
    public void Every_element_of_a_report_names_the_elements_it_is_made_of_as_with_todays_text(string graph)
    {
        var (today, k) = Hosts.Value;
        var differ = new List<string>();
        var ids = today.Engine.Graph(graph).State.Elements().Select(e => e switch { Entity en => en.Id, Rel r => r.Id, _ => null }).OfType<string>().ToList();
        Assert.NotEmpty(ids);
        foreach (var id in ids)
            if (!Named(today.Lineage(graph, id, int.MaxValue - 1)).SetEquals(Named(k.Lineage(graph, id, int.MaxValue - 1)))) differ.Add(id);
        Assert.True(differ.Count == 0, $"{differ.Count} of {ids.Count} element(s) name other elements, e.g. {string.Join(", ", differ.Take(3))}");
    }

    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "engine", "definitions"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
