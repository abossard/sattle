using System.Text.Json.Nodes;
using Sattle.Cli;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>An ETag is the digest of the content it names, so it can never name two different contents. A revision number is unique only within one run: two runs with different definitions reach the same catalog revision.</summary>
public class EtagTests
{
    const long Min = 60_000_000_000;
    static Payload Json(JsonNode n) => new(n.ToJsonString(), "application/json");
    static Payload Nd(IEnumerable<JsonNode> n) => new(string.Join("\n", n.Select(x => x.ToJsonString())), "application/x-ndjson");

    static IReadOnlyList<Sattle.Language.SourceFile> WithRole(string from, string to) =>
        [.. Sattle.Language.Catalog.ReadDirectory(Repo.Definitions).Select(f => f.Path.EndsWith("acme.workload.sattle", StringComparison.Ordinal) ? f with { Text = f.Text.Replace(from, to) } : f)];

    [Fact]
    public void Two_hosts_whose_definitions_differ_in_one_value_role_answer_different_ETags_for_the_ontology_at_the_same_catalog_revision()
    {
        using var a = new SpaceHost(WithRole("\"Degraded\": warning", "\"Degraded\": warning"), new HostOptions { Cluster = "synth" });
        using var b = new SpaceHost(WithRole("\"Degraded\": warning", "\"Degraded\": danger"), new HostOptions { Cluster = "synth" });
        Assert.Equal(a.CatalogRevision, b.CatalogRevision);                                     // the old tag, "onto-health-<catalog revision>", was the same for both
        Assert.NotEqual(Etags.Of(Json(a.Ontology("workload"))), Etags.Of(Json(b.Ontology("workload"))));
        Assert.Equal(Etags.Of(Json(a.Ontology("workload"))), Etags.Of(Json(a.Ontology("workload"))));
        Assert.NotEqual(a.Instance, b.Instance);
    }

    [Fact]
    public void The_same_content_has_the_same_ETag_after_a_restart_from_a_store_and_in_another_host_instance()
    {
        var fixture = Path.Combine(Repo.Root, "engine", "tests", "Sattle.Tests", "fixtures", "deploy-health");
        var files = Sattle.Language.Catalog.ReadDirectory(Repo.Definitions);
        var store = new InMemoryGraphStore();
        string[] before;
        string[] Tags(SpaceHost h) => [Etags.Of(Json(h.Ontology("workload"))), Etags.Of(Json(h.Snapshot("deploymentHealth", null))), Etags.Of(Nd(h.Patches("deploymentHealth", 0))), Etags.Of(Json(h.Snapshot("master", 1))), Etags.Of(Json(h.Definitions()))];
        string instance;
        using (var h = SpaceHost.OpenDurable(store, files, new HostOptions { Cluster = "synth" }))
        {
            new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 1, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" }).Run(fixture);
            before = Tags(h); instance = h.Instance;
        }
        using var r = SpaceHost.OpenDurable(store, null, new HostOptions { Cluster = "synth" });
        Assert.NotEqual(instance, r.Instance);
        Assert.Equal(before, Tags(r));
    }

    [Fact]
    public void A_stale_If_None_Match_answers_the_body_and_a_matching_one_answers_304_without_building_it_while_nothing_changed()
    {
        var etags = new Etags(); var built = 0;
        Payload Make(string text) { built++; return new Payload(text, "application/json"); }
        var (body1, tag1, nm1) = etags.Decide("k", "s1", [], () => Make("one"));
        Assert.False(nm1); Assert.Equal("one", body1!.Text); Assert.Equal(1, built);
        var (body2, _, nm2) = etags.Decide("k", "s1", ["\"stale\""], () => Make("one"));
        Assert.False(nm2); Assert.NotNull(body2);                                                // a tag the server does not know is a 200
        built = 0;
        var (body3, _, nm3) = etags.Decide("k", "s1", [tag1], () => Make("one"));
        Assert.True(nm3); Assert.Null(body3); Assert.Equal(0, built);                            // unchanged stamp: 304 without building the body
        var (body4, tag4, nm4) = etags.Decide("k", "s2", [tag1], () => Make("two"));
        Assert.False(nm4); Assert.Equal("two", body4!.Text); Assert.NotEqual(tag1, tag4);        // the stamp moved and the content changed: the old tag is stale
        var (_, tag5, nm5) = etags.Decide("k", "s3", [tag4], () => Make("two"));
        Assert.True(nm5); Assert.Equal(tag4, tag5);                                              // the stamp moved but the bytes are the same: 304 by digest
    }

    [Fact]
    public void The_tag_depends_on_the_media_type_and_on_nothing_but_the_content()
    {
        Assert.NotEqual(Etags.Of(new Payload("x", "application/json")), Etags.Of(new Payload("x", "text/plain")));
        Assert.Equal(Etags.Of(new Payload("x", "text/plain")), Etags.Of(new Payload("x", "text/plain")));
        Assert.Matches("^\"[0-9a-f]{32}\"$", Etags.Of(new Payload("x", "text/plain")));
    }
}
