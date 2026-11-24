using System.Text.Json.Nodes;
using Sattle.Runtime;
using Sattle.Cli;

namespace Sattle.Tests;

/// <summary>Proposition 12.10 on the `ETag` header the server sends: a tag is the digest of the content it names, so it never names two contents. A revision number is unique only within one run: two runs with different definitions reach the same catalog revision and answer different tags; the same content has the same tag after a restart; `If-None-Match` answers 304 exactly while the content is the same.</summary>
public class EtagTests
{
    const long Min = 60_000_000_000;
    static HostOptions Options => new() { Projections = Repo.Projections};

    static async Task<(int Status, string Tag, string Body)> Get(HttpClient http, string path, string? ifNoneMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        var response = await http.SendAsync(request);
        return ((int)response.StatusCode, response.Headers.ETag!.ToString(), await response.Content.ReadAsStringAsync());
    }

    static IReadOnlyList<Sattle.Language.SourceFile> WithRole(string role) =>
        [.. Sattle.Language.Catalog.ReadDirectory(Repo.Definitions).Select(f => f.Path.EndsWith("acme.workload.sattle", StringComparison.Ordinal) ? f with { Text = f.Text.Replace("\"Degraded\": warning", "\"Degraded\": " + role) } : f)];

    [Fact]
    public async Task Two_hosts_whose_definitions_differ_in_one_value_role_answer_different_ETags_for_the_ontology_at_the_same_catalog_revision()
    {
        static Task<(string Revision, string Tag, string Again)> Ask(SpaceHost host) => ServerTests.Serving(host, async http =>
            (JsonNode.Parse((await Get(http, "/space")).Body)!["catalogRevision"]!.GetValue<string>(), (await Get(http, "/graphs/workload/ontology")).Tag, (await Get(http, "/graphs/workload/ontology")).Tag));
        using var a = new SpaceHost(WithRole("warning"), Options);
        using var b = new SpaceHost(WithRole("danger"), Options);
        var (first, second) = (await Ask(a), await Ask(b));
        Assert.Equal(first.Revision, second.Revision);                                           // a tag made of the catalog revision would be the same for both
        Assert.NotEqual(first.Tag, second.Tag);
        Assert.Equal(first.Tag, first.Again);
        Assert.Matches("^\"[0-9a-f]{32}\"$", first.Tag);
    }

    [Fact]
    public async Task The_same_content_has_the_same_ETag_after_a_restart_from_a_store_and_in_another_host_instance()
    {
        var fixture = Path.Combine(Repo.Root, "engine", "tests", "Sattle.Tests", "fixtures", "deploy-health");
        string[] paths = ["/graphs/workload/ontology", "/graphs/deploymentHealth/snapshot", "/graphs/deploymentHealth/patches", "/graphs/master/snapshot?revision=1", "/definitions",
            "/graphs/deploymentReport/document", "/graphs/deploymentReport/document?format=json"];
        static Task<string[]> Tags(SpaceHost host, string[] paths) => ServerTests.Serving(host, async http => { var tags = new List<string>(); foreach (var p in paths) { var (status, tag, _) = await Get(http, p); Assert.Equal(200, status); tags.Add(tag); } return tags.ToArray(); });
        var store = new InMemoryGraphStore();
        string[] before;
        using (var h = SpaceHost.OpenDurable(store, Sattle.Language.Catalog.ReadDirectory(Repo.Definitions), Options))
        {
            new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 1, AdvanceEveryNs = Min, LatenessNs = Min}).Run(fixture);
            before = await Tags(h, paths);
        }
        using var restarted = SpaceHost.OpenDurable(store, null, Options);
        Assert.Equal(before, await Tags(restarted, paths));
        Assert.Equal(paths.Length, before.Distinct().Count());                                   // different contents, and one document in two formats, have different tags
    }

    [Fact]
    public async Task A_matching_If_None_Match_answers_304_without_a_body_while_the_content_is_the_same_and_the_body_with_a_new_tag_once_it_changed()
    {
        using var host = HostTests.NewHost();
        new SyntheticWorld(host, 6, 3).LoadStructure();
        const string Tree = "/graphs/serviceTree/snapshot", Ontology = "/graphs/serviceTree/ontology";
        await ServerTests.Serving(host, async http =>
        {
            var (first, ontology) = (await Get(http, Tree), await Get(http, Ontology));
            Assert.Equal(200, first.Status);
            var stale = await Get(http, Tree, "\"stale\"");
            Assert.Equal((200, first.Body, first.Tag), (stale.Status, stale.Body, stale.Tag));                          // a tag the server does not know is a 200
            Assert.Equal((304, first.Tag, ""), await Get(http, Tree, first.Tag));                                       // unchanged: 304 without the body
            for (var window = 0; window < 4; window++) HostTests.Flows(host, window);                                   // the service tree changes, its ontology does not
            var changed = await Get(http, Tree, first.Tag);
            Assert.Equal(200, changed.Status);
            Assert.NotEqual(first.Tag, changed.Tag);                                                                    // the content changed: the old tag is stale
            Assert.NotEqual(first.Body, changed.Body);
            Assert.Equal((304, ontology.Tag, ""), await Get(http, Ontology, ontology.Tag));                             // the host moved on and the bytes are the same: 304 by digest
            return 0;
        });
    }
}
