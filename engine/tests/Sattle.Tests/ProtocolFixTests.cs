using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Protocol mismatches the canvas found in version 3: seq on /space, a patch from restore, named unprintable elements, version bumps, catalog meters.</summary>
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
    public void A_wired_filter_without_a_predicate_is_unprintable_and_names_the_filter()
    {
        var cat = Repo.LoadSattleExamples();
        var op = "d:acme.ops/projection:ingest/op:known";
        var prefix = op + "/predicate:";
        var gone = cat.Graph.Entities.Where(e => e.Id.StartsWith(prefix, StringComparison.Ordinal)).Select(e => e.Id).ToHashSet();
        Assert.NotEmpty(gone);
        var g = cat.Graph.Clone();
        var edits = cat.Graph.Rels.Where(r => gone.Contains(r.Source) || gone.Contains(r.Target)).Select(Edit.Remove).Concat(cat.Graph.Entities.Where(e => gone.Contains(e.Id)).Select(Edit.Remove)).ToList();
        Assert.Null(g.Apply(edits));
        var e = Assert.Throws<UnprintableException>(() => Printer.Print(g, "acme.ops"));
        Assert.Equal(op, e.Element);
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
        using var h = HostTests.NewSattleHost();
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var ent = h.DraftSnapshot(d)["extent"]!["entities"]!.AsArray().First(e => e!["id"]!["value"]!.GetValue<string>() == "d:acme.svc/projection:serviceDeps/op:dependsOn__state0")!;
        JsonNode Edit(JsonNode? before, JsonNode? after) => new JsonObject { ["kind"] = "entity", ["id"] = "d:acme.svc/projection:serviceDeps/op:dependsOn__state0", ["before"] = before?.DeepClone(), ["after"] = after?.DeepClone() };
        var text = h.DraftText(d, "acme.svc");
        var res = h.ApplyDraftPatch(d, new JsonObject { ["edits"] = new JsonArray(Edit(ent, null), Edit(null, ent)) }, 0);
        Assert.True(res["ok"]!.GetValue<bool>());
        Assert.Equal(text, h.DraftText(d, "acme.svc"));
        // a patch that cannot apply is a typed error, never an empty 500
        var bad = Assert.Throws<HostError>(() => h.ApplyDraftPatch(d, new JsonObject { ["edits"] = new JsonArray(Edit(null, ent)) }, 1));
        Assert.Contains(bad.Code, new[] { "GuardFailed", "BadPatch" });
    }

    [Fact]
    public void A_projection_without_its_output_ontology_is_unprintable_and_names_the_projection()
    {
        var cat = Repo.LoadSattleExamples();
        var pid = "d:acme.svc/projection:serviceDeps";
        var g = cat.Graph.Clone();
        Assert.Null(g.Apply(g.Rels.Where(r => r.Source == pid && Ids.Name(r.Type) == "outputOntology").Select(Edit.Remove).ToList()));
        Assert.Equal(pid, Assert.Throws<UnprintableException>(() => Printer.Print(g, "acme.svc")).Element);
    }

    [Fact]
    public void A_match_without_a_pattern_node_is_unprintable_and_names_the_match_operator()
    {
        var cat = Repo.LoadSattleExamples();
        var op = "d:acme.svc/projection:serviceDeps/op:services";
        var gone = cat.Graph.Entities.Where(e => e.Id.StartsWith(op + "/node:", StringComparison.Ordinal)).Select(e => e.Id).ToHashSet();
        Assert.NotEmpty(gone);
        var g = cat.Graph.Clone();
        Assert.Null(g.Apply(cat.Graph.Rels.Where(r => gone.Contains(r.Source) || gone.Contains(r.Target)).Select(Edit.Remove).Concat(cat.Graph.Entities.Where(e => gone.Contains(e.Id)).Select(Edit.Remove)).ToList()));
        Assert.Equal(op, Assert.Throws<UnprintableException>(() => Printer.Print(g, "acme.svc")).Element);
    }
}
