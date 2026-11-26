using System.Text.Json;
using System.Text.Json.Nodes;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>A space of graph documents only (#127): a module and its projection in gkql load into the checked catalog, a shared edge lives exactly as long as one of its contributions (#126), a broken document is reported at the element that breaks it and publishes nothing.</summary>
public class GkqlSpaceTests
{
    /// <summary>Mentions link items: a mention row names two items, the master keeps one node per mention, the view `links` makes both items and one link between them for each mention. Two mentions of the same pair support one link.</summary>
    const string Module = """
        {"module": "pairs", "version": "1.0.0", "imports": [],
         "entities": [{"name": "Item", "properties": [{"name": "name", "type": "Text", "label": true}]},
                      {"name": "Mention", "properties": [{"name": "name", "type": "Text", "label": true}, {"name": "from", "type": "Text"}, {"name": "to", "type": "Text"}]}],
         "relationships": [{"name": "Links", "from": "Item", "to": "Item"}],
         "identities": [{"name": "item", "type": "Item", "components": [{"name": "name", "type": "Text"}]},
                        {"name": "mention", "type": "Mention", "components": [{"name": "name", "type": "Text"}]}],
         "rows": [{"name": "Mention", "fields": [{"name": "name", "type": "Text"}, {"name": "from", "type": "Text"}, {"name": "to", "type": "Text"}]}],
         "space": {"name": "pairs", "sources": [{"name": "mentions", "adapter": "record", "rows": "Mention"}],
                   "graphs": [{"name": "master", "projection": "ingest", "master": true, "inputs": {"mentions": "mentions"}},
                              {"name": "links", "projection": "links", "inputs": {"master": "master"}}]}}
        """;

    const string Ingest = """
        {"graph": "ingest", "ontology": "pairs", "inputs": [{"name": "mentions", "rows": "pairs.Mention"}], "parameters": [],
         "verbs": [
           {"id": "rows", "verb": "rows", "source": "mentions"},
           {"id": "mentions", "verb": "nodes", "rule": "mentions", "target": {"nodes": [
             {"var": "m", "label": "Mention", "id": {"identity": "mention", "args": [{"ref": "name"}]}, "props": {"name": {"ref": "name"}, "from": {"ref": "from"}, "to": {"ref": "to"}}}]}}],
         "wires": [{"from": "rows", "to": "mentions"}]}
        """;

    const string View = """
        {"graph": "links", "ontology": "pairs", "inputs": [{"name": "master", "ontology": "pairs"}], "parameters": [],
         "verbs": [
           {"id": "mentioned", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "m", "label": "Mention"}]}},
           {"id": "starts", "verb": "nodes", "rule": "starts", "target": {"nodes": [
             {"var": "a", "label": "Item", "id": {"identity": "item", "args": [{"ref": "m.from"}]}, "props": {"name": {"ref": "m.from"}}}]}},
           {"id": "ends", "verb": "nodes", "rule": "ends", "target": {"nodes": [
             {"var": "b", "label": "Item", "id": {"identity": "item", "args": [{"ref": "m.to"}]}, "props": {"name": {"ref": "m.to"}}}]}},
           {"id": "links", "verb": "edges", "rule": "links", "target": {
             "nodes": [{"var": "a", "label": "Item", "id": {"identity": "item", "args": [{"ref": "m.from"}]}},
                       {"var": "b", "label": "Item", "id": {"identity": "item", "args": [{"ref": "m.to"}]}}],
             "edges": [{"var": "l", "label": "Links", "from": "a", "to": "b"}]}}],
         "wires": [{"from": "mentioned", "to": "starts"}, {"from": "mentioned", "to": "ends"}, {"from": "mentioned", "to": "links"}]}
        """;

    static List<SourceFile> Space(string module = Module, string view = View, params SourceFile[] more) =>
        [new("pairs.module.gkql.json", module), new("pairs.ingest.gkql.json", Ingest), new("pairs.links.gkql.json", view), .. more];

    static SpaceHost Open(List<SourceFile> files)
    {
        var host = new SpaceHost(files, new HostOptions { SpaceName = "pairs", Projections = Repo.Projections });
        host.StartLive(new ReplayOptions { LatenessNs = 0 });
        return host;
    }

    static int seq;
    static void Send(SpaceHost host, string key, string from, string to, bool deleted = false, bool flush = true)
    {
        var n = Interlocked.Increment(ref seq);
        object payload = deleted ? new { key, deleted = true } : new { key, values = new { name = key, from, to } };
        var line = JsonSerializer.Serialize(new { v = 1, source = "records:Mention", capturedAt = $"2026-11-01T10:00:{n % 60:00}Z", seq = n, key = $"records:Mention/{n}", payload });
        Assert.Equal(SpaceHost.LiveResult.Consumed, host.LiveEnvelope("test", line, n.ToString()));
        if (flush) host.LiveFlush();
    }

    static List<JsonNode> Links(SpaceHost host, string from, string to)
    {
        var extent = host.Snapshot("links", null)["extent"]!;
        var names = extent["entities"]!.AsArray().ToDictionary(e => e!["id"]!["value"]!.GetValue<string>(), e => e!["properties"]![0]!["value"]!["value"]!.GetValue<string>());
        return [.. extent["relationships"]!.AsArray().Select(r => r!).Where(r => names[r["source"]!["value"]!.GetValue<string>()] == from && names[r["target"]!["value"]!.GetValue<string>()] == to)];
    }

    [Fact]
    public void A_space_of_graph_documents_only_loads_into_the_checked_catalog()
    {
        using var host = Open(Space());
        Assert.Equal(["links", "master"], host.Engine.Graphs.Select(g => g.Name).Order(StringComparer.Ordinal));
        var module = host.Definitions().AsArray().Single(m => m!["name"]!.GetValue<string>() == "pairs")!;
        Assert.All(module["projections"]!.AsArray(), p => Assert.Equal("gkql", p!["language"]!.GetValue<string>()));
    }

    static string Mention(SpaceHost host, string name) =>
        host.Snapshot("master", null)["extent"]!["entities"]!.AsArray().Single(e => e!["properties"]!.AsArray().Any(p => p!["value"]!["value"]!.GetValue<string>() == name))!["id"]!["value"]!.GetValue<string>();

    static List<string> Evidence(SpaceHost host, string link) =>
        [.. host.Lineage("links", link)["sources"]!.AsArray().Select(s => s!["element"]!.GetValue<string>()).Distinct().Order(StringComparer.Ordinal)];

    /// <summary>The witness of #126 on gkql: two mentions support one link. The link appears with the first, survives the first withdrawal with the other mention as its evidence, and goes with the last; an unrelated link stays throughout. The patches create it once and delete it once.</summary>
    [Fact]
    public void A_shared_edge_lives_from_its_first_contribution_to_its_last_withdrawal()
    {
        using var host = Open(Space());
        Send(host, "other", "c", "d");
        var head = host.Engine.Graph("links").Head;
        var counts = new List<int> { Links(host, "a", "b").Count };
        Send(host, "first", "a", "b"); counts.Add(Links(host, "a", "b").Count);
        Send(host, "second", "a", "b"); counts.Add(Links(host, "a", "b").Count);
        var id = Links(host, "a", "b").Single()["id"]!["value"]!.GetValue<string>();
        var (first, second) = (Mention(host, "first"), Mention(host, "second"));
        Assert.Equal(new[] { first, second }.Order(StringComparer.Ordinal), Evidence(host, id));
        Send(host, "first", "", "", deleted: true); counts.Add(Links(host, "a", "b").Count);
        Assert.Equal([second], Evidence(host, id));
        Assert.Single(Links(host, "c", "d"));
        Send(host, "second", "", "", deleted: true); counts.Add(Links(host, "a", "b").Count);
        Assert.Equal([0, 1, 1, 1, 0], counts);
        Assert.Single(Links(host, "c", "d"));
        var touches = host.Patches("links", head).SelectMany(p => p["edits"]!.AsArray()).Select(e => e!)
            .Where(e => e["id"]!.ToJsonString().Contains(id, StringComparison.Ordinal)).Select(e => e["before"] is null ? "create" : e["after"] is null ? "delete" : "update").ToList();
        Assert.Equal(["create", "delete"], touches);
    }

    /// <summary>A text field may hold U+001F, the separator of the record adapter's stored values (#189 F05): an update or a deletion still retracts the row as it was sent, so its link goes and nothing else stays.</summary>
    [Theory]
    [InlineData("a\u001fb", "c", true)]
    [InlineData("a\u001fb", "c", false)]
    [InlineData("\u001f", "\u001f\u001f", true)]
    [InlineData("a\u001f", "\u001fb", false)]
    public void A_text_with_the_unit_separator_is_retracted_as_it_was_sent(string from, string to, bool delete)
    {
        using var host = Open(Space());
        Send(host, "m", from, to);
        Assert.Single(Links(host, from, to));
        Send(host, "m", "x", "y", deleted: delete);
        Assert.Empty(Links(host, from, to));
        Assert.Equal(delete ? 0 : 1, Links(host, "x", "y").Count);
    }

    /// <summary>A commit that renames the source of the master activates its new binding for the live input too (#189 F06): a mention sent after the commit reaches the graph, and retracts one sent before it that was still pending at the commit.</summary>
    [Theory]
    [InlineData("feed", false)]
    [InlineData("feed", true)]
    [InlineData("mentions2", true)]
    public void A_renamed_source_feeds_the_live_input_once_activated(string source, bool pendingBefore)
    {
        using var host = Open(Space());
        if (pendingBefore) Send(host, "m", "a", "b", flush: false);
        var renamed = Module.Replace("""[{"name": "mentions",""", $$"""[{"name": "{{source}}",""")
            .Replace("""{"mentions": "mentions"}""", $$"""{"mentions": "{{source}}"}""");
        host.Commit("pairs", renamed, host.CatalogRevision);
        Assert.Equal(source, Assert.Single(host.Space.Sources).Name);
        Send(host, "m", "c", "d");
        Assert.Empty(Links(host, "a", "b"));
        Assert.Single(Links(host, "c", "d"));
    }

    static string Digest(SpaceHost host) => host.Definitions().AsArray().Single(m => m!["name"]!.GetValue<string>() == "pairs")!["digest"]!.GetValue<string>();

    /// <summary>Documents to catalog to documents and back: the texts come out as they went in, the second catalog has the same digest, and the authored verb and rule ids name its operators (`op:ID_n`) and rules.</summary>
    [Fact]
    public void A_round_trip_keeps_the_authored_ids_and_the_meaning()
    {
        using var host = Open(Space());
        using var again = Open([.. host.DefinitionFiles()]);
        foreach (var name in new[] { "pairs", "pairs.ingest.gkql.json", "pairs.links.gkql.json" }) Assert.Equal(host.Definition(name), again.Definition(name));
        Assert.Equal(Digest(host), Digest(again));
        var ids = again.Catalog.Graph.Entities.Select(e => e.Id).Where(id => id.StartsWith("d:pairs/projection:links/", StringComparison.Ordinal)).ToHashSet();
        Assert.All(new[] { "starts", "ends", "links" }, verb => Assert.Contains($"d:pairs/projection:links/op:{verb}_1", ids));
        Assert.All(new[] { "starts", "ends", "links" }, rule => Assert.Contains($"d:pairs/projection:links/rule:{rule}", ids));
    }

    /// <summary>A release is `d:{module}@{version}` with the digest of its facts. A text change with the same facts (here: the module written on one line) makes no revision and no release; a change of the facts makes a release that supersedes the first.</summary>
    [Fact]
    public void A_text_change_with_the_same_facts_makes_no_release()
    {
        using var host = Open(Space());
        var (revision, digest) = (host.CatalogRevision, Digest(host));
        var release = host.Catalog.Graph.Entity("d:pairs@1.0.0")!;
        Assert.Equal(digest, release.Props.Text(Sattle.Core.Ids.KProp("digest")));
        host.Commit("pairs", JsonNode.Parse(Module)!.ToJsonString(), revision);
        Assert.Equal(revision, host.CatalogRevision);
        Assert.Equal(digest, Assert.Single(host.Releases("pairs").AsArray())!["digest"]!.GetValue<string>());
        host.Commit("pairs", Module.Replace("\"label\": true", "\"label\": false"), revision);
        var releases = host.Releases("pairs").AsArray();
        Assert.Equal(2, releases.Count);
        Assert.Equal(digest, releases[1]!["supersedes"]!.GetValue<string>());
        Assert.NotEqual(digest, Digest(host));
    }

    public static TheoryData<string, string, string, string> Broken() => new()
    {
        { "duplicate verb id", Module, View.Replace("\"id\": \"links\"", "\"id\": \"ends\""), "d:pairs/projection:links/verb:ends" },
        { "dangling wire", Module, View.Replace("{\"from\": \"mentioned\", \"to\": \"links\"}", "{\"from\": \"mentioned\", \"to\": \"nowhere\"}"), "d:pairs/projection:links/wire:mentioned>nowhere.in" },
        { "layout key", Module, View.Replace("\"id\": \"starts\",", "\"id\": \"starts\", \"position\": {\"x\": 120, \"y\": 40},"), "d:pairs/projection:links" },
        { "import cycle", Module.Replace("\"imports\": []", "\"imports\": [{\"module\": \"cycle\", \"version\": \"1.0.0\", \"alias\": \"c\"}]"), View, "d:cycle" },
    };

    /// <summary>A broken document is reported at the element that breaks it, in the file that holds it, and the host does not start on a partial catalog.</summary>
    [Theory, MemberData(nameof(Broken))]
    public void A_broken_document_is_reported_at_its_element_and_publishes_nothing(string why, string module, string view, string focus)
    {
        var cycle = new SourceFile("cycle.module.gkql.json", """
            {"module": "cycle", "version": "1.0.0", "imports": [{"module": "pairs", "version": "1.0.0", "alias": "p"}], "entities": [{"name": "Other", "properties": []}]}
            """);
        var result = new Catalog(Repo.Projections).Load(Space(module, view, cycle));
        var error = result.Diagnostics.First(d => d.IsError);
        Assert.True(focus == error.Focus, $"{why}: {error}");
        Assert.NotNull(error.File);
        Assert.Throws<InvalidOperationException>(() => new SpaceHost(Space(module, view, cycle), new HostOptions { SpaceName = "pairs", Projections = Repo.Projections }));
    }

    /// <summary>A commit of two documents where one is broken publishes neither: the catalog revision and both texts stay as they were.</summary>
    [Fact]
    public void A_commit_with_a_broken_document_publishes_no_part_of_it()
    {
        using var host = Open(Space());
        var revision = host.CatalogRevision;
        var module = host.Definition("pairs");
        var view = host.Definition("pairs.links.gkql.json");
        var draft = host.CreateDraft(null)["draft"]!.GetValue<string>();
        Assert.True(host.SetDraftText(draft, "pairs", module.Replace("\"label\": true", "\"label\": false"), 0)["ok"]!.GetValue<bool>());
        Assert.False(host.SetDraftText(draft, "pairs.links.gkql.json", View.Replace("\"to\": \"links\"", "\"to\": \"nowhere\""), 1)["ok"]!.GetValue<bool>());
        Assert.Equal("Invalid", Assert.Throws<HostError>(() => host.CommitDraft(draft, revision)).Code);
        Assert.Equal(revision, host.CatalogRevision);
        Assert.Equal(module, host.Definition("pairs"));
        Assert.Equal(view, host.Definition("pairs.links.gkql.json"));
    }

    /// <summary>The kiln example space, end to end from graph documents (#128): readings make the instrument hot, a threshold edit makes it ready, both survive a restart, and restoring the previous release makes it hot again.
    /// The fold decides a window three windows after it ends (#151 D5), so the instrument is hot from the tick at 10:05, which closes the window from 10:01.</summary>
    [Fact]
    public void A_space_of_graph_documents_runs_through_an_edit_a_restart_and_a_restore()
    {
        var kiln = Path.Combine(Repo.Root, "engine", "examples", "kiln");
        var options = new HostOptions { SpaceName = "kiln", Projections = Repo.Projections };
        const string rating = "workshop.rating.gkql.json";
        static string State(SpaceHost h) => Assert.Single(h.Entities("rating", "Instrument")).Prop("state")!;
        static string Threshold(string text, int enterAfter)
        {
            var program = JsonNode.Parse(text)!;
            program["verbs"]!.AsArray().Single(v => (string?)v!["id"] == "crossingPolicy")!["columns"]!["enterAfter"] = new JsonObject { ["lit"] = enterAfter };
            return program.ToJsonString();
        }
        static string Described(string module)
        {
            var m = JsonNode.Parse(module)!;
            m["entities"]!.AsArray().Single(e => (string?)e!["name"] == "Batch")!["properties"]!.AsArray().Single(p => (string?)p!["name"] == "completion")!["optional"] = true;
            return m.ToJsonString();
        }
        var store = new InMemoryGraphStore();
        string original, originalModule;
        using (var host = SpaceHost.OpenDurable(store, Catalog.ReadDirectory(kiln), options))
        {
            host.StartLive(new ReplayOptions { LatenessNs = 0 });
            var position = 0;
            foreach (var line in File.ReadLines(Path.Combine(kiln, "capture", "kiln.ndjson")))
            {
                Assert.Equal(SpaceHost.LiveResult.Consumed, host.LiveEnvelope("kiln-feed", line, (++position).ToString()));
                host.LiveFlush();
                if ((string?)JsonNode.Parse(line)!["source"] == "records:Clock" && DateTimeOffset.Parse((string)JsonNode.Parse(line)!["capturedAt"]!).Minute == 5) break;
            }
            Assert.Equal("Hot", State(host));
            original = host.Definition(rating);
            originalModule = host.Definition("workshop");
            var edit = host.CreateDraft(null)["draft"]!.GetValue<string>();
            host.SetDraftText(edit, "workshop", Described(originalModule), 0);
            host.SetDraftText(edit, rating, Threshold(original, 4), 1);
            host.CommitDraft(edit, host.CatalogRevision);
            Assert.Equal("Ready", State(host));
            host.Seal();
        }
        using var restarted = RestartTests.Reopen(store, options);
        Assert.Equal("Ready", State(restarted));
        Assert.Equal(Threshold(original, 4), restarted.Definition(rating));
        Assert.Equal(Described(originalModule), restarted.Definition("workshop"));
        var releases = restarted.Releases("workshop").AsArray();
        var previous = releases.First(r => r!["supersedes"] is null)!["digest"]!.GetValue<string>();
        var draft = restarted.CreateDraft(null)["draft"]!.GetValue<string>();
        restarted.Restore(draft, previous);
        restarted.CommitDraft(draft, restarted.CatalogRevision);
        Assert.Equal("Hot", State(restarted));
        Assert.Equal(original, restarted.Definition(rating));
        Assert.Equal(originalModule, restarted.Definition("workshop"));
        Assert.Equal(originalModule, restarted.ReleaseText(previous));
    }
}
