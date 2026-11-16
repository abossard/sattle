using System.Text.Json.Nodes;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Protocol mismatches the canvas found in version 3: seq on /space, a patch from restore, version bumps, catalog meters.</summary>
public class ProtocolFixTests
{
    [Fact]
    public void Space_carries_the_event_sequence()
    {
        using var h = HostTests.NewHost();
        var before = long.Parse(h.SpaceInfo()["seq"]!.GetValue<string>());
        HostTests.Drive(h, 2);
        Assert.True(long.Parse(h.SpaceInfo()["seq"]!.GetValue<string>()) > before);
        Assert.Equal(h.Seq.ToString(), h.SpaceInfo()["seq"]!.GetValue<string>());
    }

    [Fact]
    public void Restore_returns_the_patch_it_applied()
    {
        using var h = HostTests.NewHost();
        var digest = h.Catalog.Modules["acme.svc"].Digest;
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var snap = h.DraftSnapshot(d);
        var res = h.Restore(d, digest);                          // already equal: an empty patch, still returned
        Assert.NotNull(res["patch"]);
        Assert.Empty(res["patch"]!["edits"]!.AsArray());
        Assert.NotNull(snap);
    }

    [Fact]
    public void A_version_bump_commits_with_the_import_versions_and_pins_of_its_dependents_rewritten()
    {
        using var h = HostTests.NewHost();
        var text = h.Definition("acme.svc").Replace("module acme.svc @ 1.0.0;", "module acme.svc @ 1.1.0;");
        Assert.Contains("@ 1.1.0", text);
        h.Commit("acme.svc", text, h.CatalogRevision);
        Assert.Equal("1.1.0", h.Catalog.Modules["acme.svc"].Version);
        Assert.Contains("import acme.svc @ 1.1.0", h.Definition("acme.space"));
        Assert.Contains("import acme.svc @ 1.1.0 sha256:" + h.Catalog.Modules["acme.svc"].Digest[7..], h.Definition("acme.space"));
    }

    [Fact]
    public void The_catalog_has_empty_meters()
    {
        using var h = HostTests.NewHost();
        Assert.Empty(h.Meters("catalog")["meters"]!.AsArray());
    }

    [Fact]
    public void A_patch_that_removes_and_recreates_an_element_is_one_edit_and_changes_nothing()
    {
        using var h = HostTests.NewHost();
        const string Id = "d:acme.svc/constraint:cardinality-Contains";
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var ent = h.DraftSnapshot(d)["extent"]!["entities"]!.AsArray().First(e => e!["id"]!["value"]!.GetValue<string>() == Id)!;
        JsonNode Edit(JsonNode? before, JsonNode? after) => new JsonObject { ["kind"] = "entity", ["id"] = Id, ["before"] = before?.DeepClone(), ["after"] = after?.DeepClone() };
        var res = h.ApplyDraftPatch(d, new JsonObject { ["edits"] = new JsonArray(Edit(ent, null), Edit(null, ent)) }, 0);
        Assert.True(res["ok"]!.GetValue<bool>());
        Assert.False(res["modules"]!["acme.svc"]!["changed"]!.GetValue<bool>());
        Assert.Equal(h.Definition("acme.svc"), res["modules"]!["acme.svc"]!["text"]!.GetValue<string>());
        // a patch that cannot apply is a typed error, never an empty 500
        var bad = Assert.Throws<HostError>(() => h.ApplyDraftPatch(d, new JsonObject { ["edits"] = new JsonArray(Edit(null, ent)) }, 1));
        Assert.Contains(bad.Code, new[] { "GuardFailed", "BadPatch" });
    }
}
