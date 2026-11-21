using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

public class GkqlTutorialTests
{
    static List<SourceFile> Definitions([CallerFilePath] string path = "") =>
        Catalog.ReadDirectory(Path.GetFullPath("../../tutorial", Path.GetDirectoryName(path)!));

    [Fact]
    public void The_tutorial_loads_from_graph_documents_and_advertises_its_GKQL_editors()
    {
        var files = Definitions();
        Assert.All(files, f => Assert.EndsWith(".gkql.json", f.Path));
        using var host = new SpaceHost(files, new HostOptions { SpaceName = "azureTutorial", Projections = Sattle.Cli.Program.Projections });
        Assert.Equal(new[] { "colocation", "health", "inventory", "master", "placement", "policy", "report", "signals", "tree", "workloadHealth", "workloads" },
            host.Engine.Graphs.Select(g => g.Name).Order(StringComparer.Ordinal));
        var module = host.Definitions().AsArray().Single(m => m!["name"]!.GetValue<string>() == "tutorial")!;
        Assert.Equal(11, module["projections"]!.AsArray().Count);
        Assert.All(module["projections"]!.AsArray(), p => Assert.Equal("gkql", p!["language"]!.GetValue<string>()));
    }

    [Fact]
    public void Native_module_drafts_check_commit_and_reopen_the_authored_ontology()
    {
        var options = new HostOptions { SpaceName = "azureTutorial", Projections = Sattle.Cli.Program.Projections };
        using var host = new SpaceHost(Definitions(), options);
        var original = host.Definition("tutorial");
        var draft = host.CreateDraft(null)["draft"]!.GetValue<string>();
        var oldHead = host.CatalogRevision;
        var invalid = host.SetDraftText(draft, "tutorial", "{", 0);
        Assert.False(invalid["ok"]!.GetValue<bool>());
        Assert.Equal(original, host.Definition("tutorial"));
        Assert.Equal(oldHead, host.CatalogRevision);

        var module = JsonNode.Parse(original)!;
        var group = module["entities"]!.AsArray().Single(n => n!["name"]!.GetValue<string>() == "Group")!;
        var fields = group["properties"]!.AsArray();
        fields.Single(f => f!["name"]!.GetValue<string>() == "name")!.AsObject().Remove("label");
        fields.Single(f => f!["name"]!.GetValue<string>() == "policy")!["label"] = true;
        var edited = module.ToJsonString();
        var valid = host.SetDraftText(draft, "tutorial", edited, 1);
        Assert.True(valid["ok"]!.GetValue<bool>(), valid["diagnostics"]!.ToJsonString());
        Assert.Equal(edited, host.DraftText(draft, "tutorial"));
        host.CommitDraft(draft, oldHead);
        using var reopened = new SpaceHost(host.DefinitionFiles(), options);
        Assert.Equal(edited, reopened.Definition("tutorial"));
        var ontology = reopened.Ontology("inventory");
        var shape = ontology["entityTypes"]!.AsArray().Single(t => t!["id"]!["value"]!.GetValue<string>().Split('.')[^1] == "Group")!;
        Assert.Equal("policy", shape["label"]!["value"]!.GetValue<string>().Split('.')[^1]);
    }

    [Fact]
    public void A_draft_from_a_previous_engine_cannot_address_a_new_draft()
    {
        var options = new HostOptions { SpaceName = "azureTutorial", Projections = Sattle.Cli.Program.Projections };
        using var first = new SpaceHost(Definitions(), options);
        var stale = first.CreateDraft(null)["draft"]!.GetValue<string>();

        using var restarted = new SpaceHost(first.DefinitionFiles(), options);
        var current = restarted.CreateDraft(null)["draft"]!.GetValue<string>();
        Assert.NotEqual(stale, current);
        Assert.Equal("UnknownDraft", Assert.Throws<HostError>(() => restarted.DraftSnapshot(stale)).Code);
        Assert.Equal("UnknownDraft", Assert.Throws<HostError>(() => restarted.DiscardDraft(stale)).Code);
        Assert.Equal("UnknownDraft", Assert.Throws<HostError>(() => restarted.SetDraftText(stale, "tutorial.inventory.gkql.json", "{", 0)).Code);
        Assert.Equal("0", restarted.DraftInfo(current)["revision"]!.GetValue<string>());
    }

    [Fact]
    public void GKQL_exports_the_authored_tree_count_labels_verbatim()
    {
        var files = Definitions();
        var index = files.FindIndex(f => Path.GetFileName(f.Path) == "tutorial.module.gkql.json");
        var module = JsonNode.Parse(files[index].Text)!;
        var properties = module["entities"]!.AsArray().Single(e => e!["name"]!.GetValue<string>() == "Group")!["properties"]!.AsArray();
        properties.Add(new JsonObject { ["name"] = "reviewReadyCount", ["type"] = "Integer", ["optional"] = true, ["treeCount"] = "Accepted for review" });
        properties.Add(new JsonObject { ["name"] = "reviewHoldCount", ["type"] = "Integer", ["optional"] = true, ["treeCount"] = "red" });
        files[index] = files[index] with { Text = module.ToJsonString() };
        using var host = new SpaceHost(files, new HostOptions { SpaceName = "azureTutorial", Projections = Sattle.Cli.Program.Projections });
        var group = host.Ontology("inventory")["entityTypes"]!.AsArray().Single(e => e!["id"]!["value"]!.GetValue<string>().Split('.')[^1] == "Group")!;
        var counts = group["treeState"]!["counts"]!;
        Assert.Equal("reviewReadyCount", counts["Accepted for review"]!.GetValue<string>());
        Assert.Equal("reviewHoldCount", counts["red"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("startswith", "ORDERS-", "Orders-Api", "Pre-Orders")]
    [InlineData("endswith", "-API", "Orders-Api", "Api-Orders")]
    [InlineData("contains", "-PAY-", "Orders-Pay-Edge", "Orders-Payment")]
    public void Text_predicates_filter_GKQL_rows_using_ordinal_ignore_case(string predicate, string term, string hit, string miss)
    {
        var files = Definitions();
        var index = files.FindIndex(f => Path.GetFileName(f.Path) == "tutorial.inventory.gkql.json");
        var program = JsonNode.Parse(files[index].Text)!;
        var where = program["verbs"]!.AsArray().Single(v => v!["verb"]!.GetValue<string>() == "where")!;
        where["condition"] = new JsonObject { ["fn"] = predicate, ["args"] = new JsonArray(new JsonObject { ["ref"] = "r.name" }, new JsonObject { ["lit"] = term }) };
        files[index] = files[index] with { Text = program.ToJsonString() };
        using var host = new SpaceHost(files, new HostOptions { SpaceName = "azureTutorial", Projections = Sattle.Cli.Program.Projections });
        host.StartLive(new ReplayOptions { LatenessNs = 0 });
        void Send(string name, string type, string id, int seq)
        {
            var line = JsonSerializer.Serialize(new { v = 1, source = "arg", capturedAt = "2026-11-01T10:00:00Z", seq, key = id,
                meta = new { query = "resources" }, payload = new { name, type, id, subscriptionId = "sub", resourceGroup = "rg", location = "test" } });
            host.LiveEnvelope("local", line, JsonSerializer.Serialize(new { seq }));
        }
        const string group = "/subscriptions/sub/resourcegroups/rg";
        Send("rg", "microsoft.resources/subscriptions/resourcegroups", group, 1);
        Send(hit, "example/apps", group + "/providers/example/apps/" + hit, 2);
        Send(miss, "example/apps", group + "/providers/example/apps/" + miss, 3);
        host.LiveFlush();
        Assert.Equal(hit, Assert.Single(host.Entities("inventory", "Cluster")).Prop("name"));
    }

    [Fact]
    public void Resource_Graph_updates_and_deletes_retract_network_evidence()
    {
        using var host = new SpaceHost(Definitions(), new HostOptions { SpaceName = "azureTutorial", Projections = Sattle.Cli.Program.Projections });
        host.StartLive(new ReplayOptions { AdvanceEveryNs = 60_000_000_000, LatenessNs = 0 });
        const string nic = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Network/networkInterfaces/nic";
        string Network(string id, string value, int minute) => JsonSerializer.Serialize(new
        {
            v = 1, source = "arg", seq = minute, key = "arg/resources/nic", capturedAt = $"2026-01-01T10:0{minute}:00Z",
            meta = new { query = "resources" },
            payload = new
            {
                id, name = "nic", type = "microsoft.network/networkinterfaces",
                properties = new
                {
                    ipConfigurations = new[] { new { properties = new
                    {
                        privateIPAddress = value,
                        privateLinkConnectionProperties = new { fqdns = new[] { "storage.example.invalid" } },
                    } } },
                },
            },
        });
        string? Address() => Assert.Single(host.Entities("master", "IpAddress")).Prop("address");
        var master = host.Engine.Graph("master").State;
        Assert.Equal(SpaceHost.LiveResult.Consumed, host.LiveEnvelope("local", Network(nic, "192.0.2.1", 1), "{\"n\":1}"));
        host.LiveFlush();
        Assert.Equal("192.0.2.1", Address());
        Assert.Single(master.Rels, r => Sattle.Core.Ids.Name(r.Type) == "ConfiguredAddress");

        Assert.Equal(SpaceHost.LiveResult.Duplicate, host.LiveEnvelope("local", Network(nic, "192.0.2.1", 2), "{\"n\":2}"));
        Assert.Equal(SpaceHost.LiveResult.Consumed, host.LiveEnvelope("local", Network(nic, "192.0.2.2", 3), "{\"n\":3}"));
        Assert.Equal(SpaceHost.LiveResult.Consumed, host.LiveEnvelope("local", Network(nic.ToUpperInvariant(), "192.0.2.2", 4), "{\"n\":4}"));
        host.LiveFlush();
        Assert.Equal("192.0.2.2", Address());
        Assert.Single(master.Rels, r => Sattle.Core.Ids.Name(r.Type) == "ConfiguredAddress");

        var delete = JsonSerializer.Serialize(new
        {
            v = 1, source = "arg", seq = 5, key = "arg/changes/5", capturedAt = "2026-01-01T10:05:00Z",
            meta = new { query = "resourcechanges" },
            payload = new { id = nic + "/changes/5", properties = new { changeType = "Delete", targetResourceId = nic } },
        });
        Assert.Equal(SpaceHost.LiveResult.Consumed, host.LiveEnvelope("local", delete, "{\"n\":5}"));
        host.LiveFlush();
        Assert.Empty(master.Rels);
        Assert.Empty(master.Entities);
    }
}
