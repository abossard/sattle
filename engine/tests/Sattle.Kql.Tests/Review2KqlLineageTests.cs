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

    static readonly Lazy<SpaceHost> Host = new(() =>
    {
        var h = new SpaceHost(Catalog.ReadDirectory(Definitions), new HostOptions { Cluster = "synth", LateHorizonWindows = 1_000_000, Projections = new KqlSource() });
        new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 200, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000, Cluster = "synth" }).Run(Estate);
        h.Finish();
        return h;
    });

    /// <summary>HEAD: the count blocks of the two reports name no source elements with the KQL text, the former text named every Workload or Element.
    /// The digest is pinned where the KQL text named the same elements as the former text.</summary>
    [Theory]
    [InlineData("deploymentReport", "sha256:a791c70d7cf8de860594eefe75ca8431c02de82f7704ce70188210026a653f18")]
    [InlineData("healthReport", "sha256:4186f26e6348b63a14e55b076260bcd5cde7b369484bd6ee22b54662741ceda8")]
    public void Every_element_of_a_report_names_the_elements_it_is_made_of(string graph, string pinned) =>
        Assert.Equal(pinned, KqlLineageTests.LineageDigest(Host.Value, graph, out _));

    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "engine", "definitions"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
