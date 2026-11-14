using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Kql.Tests;

/// <summary>Lineage of the projections in KQL: an element names the elements it is made of, every one today's text names, and each role is the name
/// the KQL text gives a column, or that name with a number when a join renames the right side's copy (`fromPod1`). The KQL text names the edges its
/// patterns walk, so it may name more: only relationships.</summary>
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

    static SortedSet<string> Named(JsonNode lineage, string? kind = null) => new(lineage["sources"]!.AsArray()
        .Where(s => kind is null || s!["kind"]!.GetValue<string>() == kind)
        .Select(s => s!["graph"]!.GetValue<string>() + "/" + s["element"]!.GetValue<string>()), StringComparer.Ordinal);

    [Theory]
    [InlineData("serviceTree", "acme.svc.serviceDeps.kql")]
    [InlineData("capabilities", "acme.biz.capabilityView.kql")]
    [InlineData("healthTree", "acme.treehealth.healthTree.kql")]
    [InlineData("workloadStructure", "acme.workload.workloadStructure.kql")]
    public void Every_element_names_what_todays_text_names_and_beyond_that_only_relationships_under_the_column_names_of_the_KQL_text(string graph, string kql)
    {
        var (today, k) = Hosts.Value;
        var text = File.ReadAllText(Path.Combine(Definitions, kql));
        var differ = new List<string>(); var roles = new SortedSet<string>(StringComparer.Ordinal);
        var ids = today.Engine.Graph(graph).State.Elements().Select(e => e switch { Entity en => en.Id, Rel r => r.Id, _ => null }).OfType<string>().ToList();
        Assert.NotEmpty(ids);
        foreach (var id in ids)
        {
            var mine = k.Lineage(graph, id, int.MaxValue - 1);
            var named = Named(today.Lineage(graph, id, int.MaxValue - 1));
            if (!named.IsSubsetOf(Named(mine)) || !Named(mine, "entity").IsSubsetOf(named)) differ.Add(id);
            foreach (var s in mine["sources"]!.AsArray()) roles.Add(s!["role"]!.GetValue<string>());
        }
        Assert.True(differ.Count == 0, $"{differ.Count} of {ids.Count} element(s) miss an element today's text names, or name another entity, e.g. {string.Join(", ", differ.Take(3))}");
        Assert.All(roles, r => Assert.Matches($@"\b{Regex.Escape(r.TrimEnd("0123456789".ToCharArray()))}\b", text));
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
