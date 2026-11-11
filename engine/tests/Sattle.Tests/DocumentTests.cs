using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Document projections: a graph of Block entities rendered by one generic renderer. Deterministic, a digest per revision, escaped data.</summary>
public class DocumentTests
{
    const long Min = 60_000_000_000;
    static string Fixture => Path.Combine(Repo.Root, "engine", "tests", "Sattle.Tests", "fixtures", "deploy-health");

    static SpaceHost Replayed()
    {
        var h = HostTests.NewHost();
        new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 1, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" }).Run(Fixture);
        return h;
    }

    [Fact]
    public void The_deployment_report_is_a_markdown_document_with_one_keyed_row_per_deployment()
    {
        using var h = Replayed();
        var doc = h.Document("deploymentReport", null);
        Assert.StartsWith("# Deployment health\n\n- Deployments: 2\n- Unhealthy deployments: 0\n\n| Namespace | Deployment | State | Ready pods |\n| --- | --- | --- | --- |\n", doc.Markdown);
        Assert.Contains("| shop | db | Healthy | 1 |\n| shop | web | Healthy | 1 |\n", doc.Markdown);    // rows in key order; web ends with one ready pod
        Assert.EndsWith("|\n", doc.Markdown);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(doc.Markdown))), doc.Digest);      // computed here, not by the helper under test
        Assert.True(h.SpaceInfo()["graphs"]!.AsArray().First(g => g!["id"]!.GetValue<string>() == "deploymentReport")!["document"]!.GetValue<bool>());
    }

    [Fact]
    public void A_document_is_a_function_of_the_graph_state_and_has_a_digest_per_revision_that_follows_the_health_changes()
    {
        using var a = Replayed(); using var b = Replayed();
        var head = a.Document("deploymentReport", null).Revision;
        Assert.True(head > 3);
        var digests = Enumerable.Range(1, (int)head).Select(r => a.Document("deploymentReport", r).Digest).ToList();
        Assert.Equal(digests, Enumerable.Range(1, (int)head).Select(r => b.Document("deploymentReport", r).Digest));   // same inputs, same bytes at every revision
        Assert.Contains(digests.Zip(digests.Skip(1)), p => p.First != p.Second);                                         // the document changes when a Deployment turns Unhealthy
        Assert.Contains(Enumerable.Range(1, (int)head).Select(r => a.Document("deploymentReport", r).Markdown), m => m.Contains("- Unhealthy deployments: 1") || m.Contains("- Unhealthy deployments: 2"));
        var last = a.Document("deploymentReport", null);
        Assert.Equal(digests[^1], last.Digest);
    }

    [Fact]
    public void Only_graphs_with_the_document_ontology_are_documents()
    {
        using var h = HostTests.NewHost();
        Assert.Equal("NotADocument", Assert.Throws<HostError>(() => h.Document("master", null)).Code);
        Assert.Equal("UnknownRevision", Assert.Throws<HostError>(() => h.Document("deploymentReport", 99)).Code);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a | b", "a \\| b")]
    [InlineData("# heading *bold* [x](y)", "\\# heading \\*bold\\* \\[x\\]\\(y\\)")]
    [InlineData("line1\nline2", "line1 line2")]
    public void Data_text_is_escaped_so_it_cannot_create_document_structure(string data, string expected) => Assert.Equal(expected, Documents.Escape(data));

    [Fact]
    public void Blocks_are_rendered_in_the_order_they_are_given_and_the_graph_order_does_not_matter()
    {
        static Documents.Block B(string id, string order) => new(id, "paragraph", order, Props.Of(("d:sattle.doc/prop:text", Value.Text(id)), ("d:sattle.doc/prop:order", Value.Text(order)), ("d:sattle.doc/prop:kind", Value.Text("paragraph"))));
        var entities = new[] { B("x1", "1"), B("x2", "1"), B("x3", "0") };
        var g1 = new Graph(); var g2 = new Graph();
        Assert.Null(g1.Apply(entities.Select(b => Edit.Create(new Entity(b.Id, "d:sattle.doc/entity:Block", b.Props))).ToList()));
        Assert.Null(g2.Apply(entities.Reverse().Select(b => Edit.Create(new Entity(b.Id, "d:sattle.doc/entity:Block", b.Props))).ToList()));
        Assert.Equal("x3\n\nx1\n\nx2\n", Documents.Markdown(Documents.BlocksOf(g1)));
        Assert.Equal(Documents.Markdown(Documents.BlocksOf(g1)), Documents.Markdown(Documents.BlocksOf(g2)));
    }
}
