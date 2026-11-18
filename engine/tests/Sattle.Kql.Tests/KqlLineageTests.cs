using System.Text.RegularExpressions;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Kql.Tests;

/// <summary>Lineage of the projections in KQL, the part that is the language's own: each role is the name the KQL text gives a column, or that name with a
/// number when a join renames the right side's copy (`fromPod1`). What an element names is the engine's claim (Theorem 9.7, `LineageTests`).</summary>
public class KqlLineageTests
{
    static readonly string Root = FindRoot();
    static string Definitions => Path.Combine(Root, "engine", "definitions");
    static string Estate => Path.Combine(Root, "engine", "tests", "Sattle.Tests", "fixtures", "estate");

    /// <summary>The estate served with the projections of the `.kql` files.</summary>
    static readonly Lazy<SpaceHost> Host = new(() =>
    {
        var h = new SpaceHost(Catalog.ReadDirectory(Definitions), new HostOptions { Cluster = "synth", LateHorizonWindows = 1_000_000, Projections = new KqlSource() });
        new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 200, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000, Cluster = "synth" }).Run(Estate);
        h.Finish();
        return h;
    });

    /// <summary>The roles the lineage of the elements of a graph names.</summary>
    static SortedSet<string> Roles(SpaceHost h, string graph)
    {
        var extent = h.Snapshot(graph, null)["extent"]!;
        var ids = extent["entities"]!.AsArray().Concat(extent["relationships"]!.AsArray()).Select(e => e!["id"]!["value"]!.GetValue<string>()).ToList();
        Assert.NotEmpty(ids);
        var roles = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
            foreach (var s in h.Lineage(graph, id, int.MaxValue - 1)["sources"]!.AsArray()) roles.Add(s!["role"]!.GetValue<string>());
        Assert.NotEmpty(roles);
        return roles;
    }

    [Theory]
    [InlineData("serviceTree", "acme.svc.serviceDeps.kql")]
    [InlineData("capabilities", "acme.biz.capabilityView.kql")]
    [InlineData("healthTree", "acme.treehealth.healthTree.kql")]
    [InlineData("workloadStructure", "acme.workload.workloadStructure.kql")]
    public void Every_role_of_a_lineage_is_a_column_name_of_the_KQL_text(string graph, string kql)
    {
        var text = File.ReadAllText(Path.Combine(Definitions, kql));
        Assert.All(Roles(Host.Value, graph), r => Assert.Matches($@"\b{Regex.Escape(r.TrimEnd("0123456789".ToCharArray()))}\b", text));
    }

    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "engine", "definitions"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
