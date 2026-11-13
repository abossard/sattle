using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Kql.Tests;

/// <summary>Lineage of the projections in KQL: an element names the elements it is made of, the same ones as with today's text, and each role is the name
/// the KQL text gives a column.</summary>
public class KqlLineageTests
{
    static readonly string Root = FindRoot();
    static string Definitions => Path.Combine(Root, "engine", "definitions");
    static string Estate => Path.Combine(Root, "engine", "tests", "Sattle.Tests", "fixtures", "estate");

    /// <summary>The estate served with today's text and with every projection that has a `.kql` file loaded from it.</summary>
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

    [Theory]
    [InlineData("serviceTree", "acme.svc.serviceDeps.kql")]
    [InlineData("capabilities", "acme.biz.capabilityView.kql")]
    [InlineData("healthTree", "acme.treehealth.healthTree.kql")]
    [InlineData("workloadStructure", "acme.workload.workloadStructure.kql")]
    public void Every_element_names_the_elements_it_is_made_of_as_with_todays_text_under_the_column_names_of_the_KQL_text(string graph, string kql)
    {
        var (today, k) = Hosts.Value;
        var text = File.ReadAllText(Path.Combine(Definitions, kql));
        var differ = new List<string>(); var roles = new SortedSet<string>(StringComparer.Ordinal);
        var ids = today.Engine.Graph(graph).State.Elements().Select(e => e switch { Entity en => en.Id, Rel r => r.Id, _ => null }).OfType<string>().ToList();
        Assert.NotEmpty(ids);
        foreach (var id in ids)
        {
            var mine = k.Lineage(graph, id, int.MaxValue - 1);
            if (!Named(today.Lineage(graph, id, int.MaxValue - 1)).SetEquals(Named(mine))) differ.Add(id);
            foreach (var s in mine["sources"]!.AsArray()) roles.Add(s!["role"]!.GetValue<string>());
        }
        Assert.True(differ.Count == 0, $"{differ.Count} of {ids.Count} element(s) name other elements, e.g. {string.Join(", ", differ.Take(3))}");
        Assert.All(roles, r => Assert.Matches($@"\b{Regex.Escape(r)}\b", text));
    }

    /// <summary>The rule a step names is the rule its lineage gives (edit stage, first part).</summary>
    [Fact]
    public void A_lifted_capability_edge_is_made_by_the_rule_its_steps_name()
    {
        var k = Hosts.Value.Kql;
        var edge = k.Engine.Graph("capabilities").State.Rels.First(r => Ids.Name(r.Type) == "DependsOn");
        Assert.Equal("lifted", k.Lineage("capabilities", edge.Id)["rule"]!.GetValue<string>());
    }

    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "engine", "definitions"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
