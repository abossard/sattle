using System.Text.RegularExpressions;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Kql.Tests;

/// <summary>Lineage of the projections in KQL: what each element names is pinned by a digest, taken when the KQL text named every element
/// the projections' former text named and beyond that only relationships. Each role is the name the KQL text gives a column, or that name with a
/// number when a join renames the right side's copy (`fromPod1`).</summary>
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

    /// <summary>The digest of every element of a graph with the sources its lineage names, by kind, graph and element.</summary>
    internal static string LineageDigest(SpaceHost h, string graph, out SortedSet<string> roles)
    {
        var ids = h.Engine.Graph(graph).State.Elements().Select(e => e switch { Entity en => en.Id, Rel r => r.Id, _ => null }).OfType<string>().Order(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(ids);
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var lines = ids.Select(id =>
        {
            var sources = h.Lineage(graph, id, int.MaxValue - 1)["sources"]!.AsArray();
            foreach (var s in sources) found.Add(s!["role"]!.GetValue<string>());
            return id + "\t" + string.Join(",", sources.Select(s => s!["kind"]!.GetValue<string>() + ":" + s["graph"]!.GetValue<string>() + "/" + s["element"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        });
        var digest = Digests.Of(string.Join("\n", lines));
        roles = found;
        return digest;
    }

    [Theory]
    [InlineData("serviceTree", "acme.svc.serviceDeps.kql", "sha256:47c68a129a57d6e6514d4a5e9e53cf17498f8608f487da53023974d2f41f6087")]
    [InlineData("capabilities", "acme.biz.capabilityView.kql", "sha256:afe1c2100ce3f61453de58e193e0083cf5ecb880d38e69e45fedb6eef62183a0")]
    [InlineData("healthTree", "acme.treehealth.healthTree.kql", "sha256:539861486c14affda672b4bd83e1345d94fc68549963eb9a19a16d25102d7440")]
    [InlineData("workloadStructure", "acme.workload.workloadStructure.kql", "sha256:8ed7d36e532e2d778489cae26a6f78c583a0710e7b4667c3eb013e099ff2d5c7")]
    public void Every_element_names_the_pinned_sources_under_the_column_names_of_the_KQL_text(string graph, string kql, string pinned)
    {
        var text = File.ReadAllText(Path.Combine(Definitions, kql));
        Assert.Equal(pinned, LineageDigest(Host.Value, graph, out var roles));
        Assert.All(roles, r => Assert.Matches($@"\b{Regex.Escape(r.TrimEnd("0123456789".ToCharArray()))}\b", text));
    }

    /// <summary>The rule a step names is the rule its lineage gives (edit stage, first part).</summary>
    [Fact]
    public void A_lifted_capability_edge_is_made_by_the_rule_its_steps_name()
    {
        var k = Host.Value;
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
