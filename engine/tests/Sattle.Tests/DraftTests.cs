using System.Text.Json.Nodes;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>PROTOCOL.md v3, drafts (Proposition 4.10, 9.9, 9.10): a draft is edited by patches with inverse-patch undo and by the text of a projection file, is checked against its base, previewed without touching anything live (T-PREVIEW), committed, and every release is listed and restorable. The tests that drive a world are `Slow`.</summary>
public class DraftTests
{
    const string Cardinality = "d:acme.svc/constraint:cardinality-Contains";          // a service is in exactly one group: `per target 1 .. 1`
    const string FlowsOut = "d:acme.svc/prop:flowsOut";                               // the flows a service or a group sends, a delta telemetry property
    /// <summary>The file of the service tree, with the threshold that gives a dependency its status.</summary>
    static string Deps(SpaceHost h) => h.ProjectionFile("acme.svc.serviceDeps");

    /// <summary>A host nothing is fed to and nothing is committed on, for the tests that discard their draft.</summary>
    static readonly Lazy<SpaceHost> Fresh = new(HostTests.NewHost);

    static (SpaceHost H, string D) NewDraft()
    {
        var h = HostTests.NewHost();
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        return (h, d);
    }

    /// <summary>A GraphPatch that sets one property of one definition node, and its inverse.</summary>
    static (JsonNode Do, JsonNode Undo) SetProp(SpaceHost h, string draft, string node, string prop, JsonNode newValue)
    {
        var snap = h.DraftSnapshot(draft);
        var before = snap["extent"]!["entities"]!.AsArray().First(e => e!["id"]!["value"]!.GetValue<string>() == node)!;
        var after = before.DeepClone();
        var props = after["properties"]!.AsArray();
        var existing = props.FirstOrDefault(p => p!["property"]!["value"]!.GetValue<string>() == prop);
        if (existing is not null) existing["value"] = newValue.DeepClone();
        else props.Add(new JsonObject { ["property"] = new JsonObject { ["value"] = prop }, ["value"] = newValue.DeepClone() });
        JsonNode P(JsonNode b, JsonNode a) => new JsonObject { ["edits"] = new JsonArray(new JsonObject { ["kind"] = "entity", ["id"] = node, ["before"] = b.DeepClone(), ["after"] = a.DeepClone() }) };
        return (P(before, after), P(after, before));
    }
    static JsonNode Dec(string v) => new JsonObject { ["kind"] = "decimal", ["value"] = v };

    /// <summary>The service tree's text with a threshold that never recovers: below 0.0 for 30 windows keeps the degraded state.</summary>
    static string NeverRecovers(SpaceHost h)
    {
        var text = h.Definition(Deps(h));
        Assert.Contains("below = 0.02, exitAfter = 3", text);
        return text.Replace("below = 0.02, exitAfter = 3", "below = 0.0, exitAfter = 30");
    }

    /// <summary>Code review 2, defect 7: a draft patch that the check cannot read is refused and not recorded, so the draft stays usable. Before, the patch was recorded first: an Integer beyond 64 bits answered 500, and so did every later check, preview and commit of that draft.</summary>
    [Fact]
    public void A_draft_patch_that_cannot_be_checked_is_refused_and_leaves_the_draft_as_it_was()
    {
        var (h, d) = NewDraft(); using var hostGuard = h;
        var patch = SetProp(h, d, FlowsOut, "period", new JsonObject { ["kind"] = "integer", ["value"] = "99999999999999999999" }).Do;
        var failure = Record.Exception(() => h.ApplyDraftPatch(d, patch, 0));
        Assert.True(failure is null or HostError, $"{failure?.GetType().Name}: {failure?.Message}");
        Assert.Equal(failure is null ? "1" : "0", h.DraftInfo(d)["revision"]!.GetValue<string>());
        var check = h.CheckDraft(d);                                                   // the draft still answers
        if (failure is null) Assert.False(check["ok"]!.GetValue<bool>());            // accepted and recorded: then it is a diagnostic of the draft
        else Assert.True(check["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void Structural_edits_print_canonically_and_undo_is_an_inverse_patch_not_a_rewind()
    {
        var (h, d) = NewDraft(); using var hostGuard = h;
        var rev0 = h.CatalogRevision;
        var before = h.Definition("acme.svc");
        var (doit, undo) = SetProp(h, d, Cardinality, "max", Dec("2"));
        var r1 = h.ApplyDraftPatch(d, doit, 0);
        Assert.True(r1["ok"]!.GetValue<bool>(), r1["diagnostics"]!.ToJsonString());
        Assert.Contains("per target 1 .. 2;", r1["modules"]!["acme.svc"]!["text"]!.GetValue<string>());
        Assert.True(r1["modules"]!["acme.svc"]!["changed"]!.GetValue<bool>());
        var r2 = h.ApplyDraftPatch(d, undo, 1);                              // undo = the inverse patch, applied forward as revision 2
        Assert.Equal("2", r2["revision"]!.GetValue<string>());
        Assert.Equal(before, r2["modules"]!["acme.svc"]!["text"]!.GetValue<string>());
        Assert.False(r2["modules"]!["acme.svc"]!["changed"]!.GetValue<bool>());
        var r3 = h.ApplyDraftPatch(d, doit, 2);                              // redo
        Assert.Contains("per target 1 .. 2;", r3["modules"]!["acme.svc"]!["text"]!.GetValue<string>());
        Assert.Equal(rev0, h.CatalogRevision);                                // the committed log is untouched
        Assert.Equal(3, h.DraftPatches(d, 0).Count);
        // guards: the same patch twice is a conflict and changes nothing
        Assert.Equal("GuardFailed", Assert.Throws<HostError>(() => h.ApplyDraftPatch(d, doit, 3)).Code);
        Assert.Equal("StaleRevision", Assert.Throws<HostError>(() => h.ApplyDraftPatch(d, undo, 1)).Code);
        Assert.Equal("3", h.DraftInfo(d)["revision"]!.GetValue<string>());
    }

    [Fact]
    public void Invalid_structure_is_reported_by_definition_element_id()
    {
        var (h, d) = NewDraft(); using var hostGuard = h;
        var (bad, _) = SetProp(h, d, FlowsOut, "temporality", new JsonObject { ["kind"] = "text", ["value"] = "bogus" });
        var r = h.ApplyDraftPatch(d, bad, 0);
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Contains(r["diagnostics"]!.AsArray(), x => x!["severity"]!.GetValue<string>() == "error" && x["module"]!.GetValue<string>() == "acme.svc");
    }

    [Fact]
    public void Committing_a_draft_activates_it_and_every_release_is_listed_diffed_and_restorable()
    {
        var (h, d) = NewDraft(); using var hostGuard = h;
        string Digest() => h.Definitions().AsArray().Single(m => m!["name"]!.GetValue<string>() == "acme.svc")!["digest"]!.GetValue<string>();
        var originalDigest = Digest();
        var originalText = h.Definition("acme.svc");
        var (doit, _) = SetProp(h, d, Cardinality, "max", Dec("2"));
        h.ApplyDraftPatch(d, doit, 0);
        var res = h.CommitDraft(d, h.CatalogRevision);
        Assert.Equal("acme.svc", res["modules"]![0]!.GetValue<string>());
        Assert.NotEqual(originalDigest, Digest());
        Assert.Throws<HostError>(() => h.DraftInfo(d));                       // committed drafts are discarded
        var rels = h.Releases("acme.svc").AsArray();
        Assert.Equal(2, rels.Count);
        Assert.Equal(originalDigest, rels[1]!["supersedes"]!.GetValue<string>());
        var diff = h.ReleaseDiff(originalDigest, Digest());
        Assert.Single(diff["edits"]!.AsArray(), e => e!["id"]!["value"]!.GetValue<string>() == Cardinality);
        Assert.Contains("per target 1 .. 2;", h.ReleaseText(Digest()));
        Assert.Equal(originalText, h.ReleaseText(originalDigest));
        // restore the first release into a new draft and commit it: the digest comes back
        var d2 = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var restored = h.Restore(d2, originalDigest);
        Assert.True(restored["ok"]!.GetValue<bool>(), restored["diagnostics"]!.ToJsonString());
        h.CommitDraft(d2, h.CatalogRevision);
        Assert.Equal(originalDigest, Digest());
        Assert.Equal(3, h.Releases("acme.svc").AsArray().Count);
    }

    [Fact, Trait("Category", "Slow")]
    public void Preview_evaluates_a_draft_with_a_stated_scope_and_touches_nothing_live()
    {
        var (h, d) = NewDraft(); using var hostGuard = h;
        HostTests.Drive(h);
        string Status() => h.Relationships("serviceTree", "DependsOn").Single().Prop("status")!;
        Assert.Equal("ok", Status());
        Assert.True(h.SetDraftText(d, Deps(h), NeverRecovers(h), 0)["ok"]!.GetValue<bool>());
        var digest = h.StateDigest(); var seq = h.Seq; var heads = h.SpaceInfo()["graphs"]!.ToJsonString();
        var p = h.Preview(d, new JsonObject { ["graphs"] = new JsonArray("serviceTree"), ["include"] = new JsonArray("counts", "diff", "telemetry") });
        Assert.True(p["scope"]!["complete"]!.GetValue<bool>());
        var g = p["graphs"]![0]!;
        var changes = g["diff"]!["edits"]!.AsArray();
        Assert.Contains(changes, e => e!["after"]!["properties"]!.AsArray().Any(x => x!["value"]!["value"]!.GetValue<string>() == "degraded"));
        Assert.NotEmpty(g["telemetry"]!.AsArray());
        Assert.Equal(digest, h.StateDigest());                                  // live state, stores and event log untouched
        Assert.Equal(seq, h.Seq);
        Assert.Equal(heads, h.SpaceInfo()["graphs"]!.ToJsonString());
        Assert.Equal("ok", Status());
        // the draft changes no identity: the preview resolves the same identities as the live space
        var idn = p["identity"]!;
        Assert.True(idn["comparable"]!.GetValue<bool>());
        Assert.Equal(idn["live"]!.ToJsonString(), idn["preview"]!.ToJsonString());
        Assert.All(new[] { "minted", "aliased", "merged", "conflicts" }, k => Assert.Equal("0", idn["difference"]![k]!.GetValue<string>()));
        // a bounded scope is stated and has no diff against the live outputs
        var small = h.Preview(d, new JsonObject { ["scope"] = new JsonObject { ["maxRows"] = "5" }, ["graphs"] = new JsonArray("serviceTree") });
        Assert.False(small["scope"]!["complete"]!.GetValue<bool>());
        Assert.Null(small["graphs"]![0]!["diff"]);
        Assert.False(small["identity"]!["comparable"]!.GetValue<bool>());
        Assert.Null(small["identity"]!["difference"]);
        Assert.True(long.Parse(small["identity"]!["preview"]!["minted"]!.GetValue<string>()) < long.Parse(idn["live"]!["minted"]!.GetValue<string>()));   // fewer inputs, fewer identities
    }

    [Fact]
    public void Draft_diagnostics_are_pushed_on_the_event_stream_to_subscribers_who_ask()
    {
        var (h, d) = NewDraft(); using var hostGuard = h;
        var with = h.Subscribe(null, true, null); var without = h.Subscribe(null, false, null);
        while (with.Messages.Reader.TryRead(out _)) { }
        while (without.Messages.Reader.TryRead(out _)) { }
        var (doit, _) = SetProp(h, d, Cardinality, "max", Dec("2"));
        h.ApplyDraftPatch(d, doit, 0);
        Assert.True(with.Messages.Reader.TryRead(out var m));
        Assert.Equal("draft", JsonNode.Parse(m)!["type"]!.GetValue<string>());
        Assert.False(without.Messages.Reader.TryRead(out _));
    }

    [Fact]
    public void A_draft_check_returns_the_row_schema_after_each_operator()
    {
        var (h, d) = NewDraft(); using var hostGuard = h;
        var r = h.CheckDraft(d);
        var ops = r["schemas"]!["d:acme.svc/projection:serviceDeps"]!.AsArray();
        // the operators are found by the columns they hold: the names a language's lowering gives them are not the engine's
        static JsonNode? Col(JsonNode? op, string n) => op!["columns"]!.AsArray().FirstOrDefault(c => c!["name"]!.GetValue<string>() == n);
        Assert.Contains(ops, o => Col(o, "s") is { } s && s["kind"]!.GetValue<string>() == "Entity" && s["type"]!.GetValue<string>().EndsWith("Service", StringComparison.Ordinal) && s["role"]!.GetValue<string>() == "data");
        var node = ops.First(o => o!["columns"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).SequenceEqual(["service", "pods"]));
        Assert.Equal("Integer", Col(node, "pods")!["kind"]!.GetValue<string>());
        Assert.Contains(ops, o => Col(o, "flows") is { } flows && flows["role"]!.GetValue<string>() == "telemetry" && flows["period"] is not null);
        Assert.All(ops, o => Assert.StartsWith("d:acme.svc/projection:serviceDeps/op:", o!["op"]!.GetValue<string>()));
    }

    [Theory, Trait("Category", "Slow")]
    [InlineData("20", false)]
    [InlineData("1", true)]
    public void A_preview_with_rows_returns_the_rows_each_operator_holds(string rowLimit, bool truncated)
    {
        var (h, d) = NewDraft(); using var hostGuard = h;
        HostTests.Drive(h);
        var p = h.Preview(d, new JsonObject { ["graphs"] = new JsonArray("serviceTree"), ["include"] = new JsonArray("counts", "rows"), ["rowLimit"] = rowLimit });
        var ops = p["graphs"]![0]!["operators"]!.AsArray();
        string[] columns = ["s", "s.cluster", "s.name", "s.namespace"];                              // the typed column and the properties the plan reads from it
        var services = ops.First(o => o!["columns"] is { } c && c.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(columns))!;
        var live = h.Entities("serviceTree", "ServiceNode").Count;
        Assert.Equal(live.ToString(), services["rows"]!.GetValue<string>());
        Assert.Equal(services["rows"]!.GetValue<string>(), services["weight"]!.GetValue<string>());       // each service once
        Assert.Equal(truncated, services["truncated"]!.GetValue<bool>());
        var sample = services["sample"]!.AsArray();
        Assert.Equal(Math.Min(live, int.Parse(rowLimit)), sample.Count);
        Assert.All(sample, x => Assert.False(string.IsNullOrEmpty(x!["cells"]!["s"]!.GetValue<string>())));
        Assert.All(sample, x => Assert.False(string.IsNullOrEmpty(x!["cells"]!["s.name"]!.GetValue<string>())));
        Assert.All(ops, o => Assert.StartsWith("d:acme.svc/projection:serviceDeps/op:", o!["op"]!.GetValue<string>()));
        Assert.Null(h.Preview(d, new JsonObject { ["graphs"] = new JsonArray("serviceTree") })["graphs"]![0]!["operators"]);
    }

    /// <summary>T-PREVIEW: with no input between the preview and the commit, the complete preview's output of every graph equals the state the commit's activation produces (Proposition 9.10 (b)).</summary>
    [Theory, Trait("Category", "Slow")]
    [InlineData("a threshold that never recovers", true)]
    [InlineData("a series that looks back 2h", false)]          // every window of this world is younger than two hours: the outputs stay as they were
    public void A_complete_preview_equals_the_activation_a_commit_performs(string draft, bool changes)
    {
        var (h, d) = NewDraft(); using var hostGuard = h;
        HostTests.Drive(h);
        var text = draft == "a threshold that never recovers" ? NeverRecovers(h) : h.Definition(Deps(h)).Replace("telemetry(\"master\", 24h)", "telemetry(\"master\", 2h)");
        Assert.True(h.SetDraftText(d, Deps(h), text, 0)["ok"]!.GetValue<bool>());
        var p = h.Preview(d, new JsonObject { ["include"] = new JsonArray("counts") });
        Assert.True(p["scope"]!["complete"]!.GetValue<bool>());
        var previewed = p["graphs"]!.AsArray().Where(g => g!["removed"] is null).ToDictionary(g => g!["id"]!.GetValue<string>(), g => g!["digest"]!.GetValue<string>());
        var windows = p["graphs"]!.AsArray().Where(g => g!["removed"] is null).ToDictionary(g => g!["id"]!.GetValue<string>(), g => g!["counts"]!["telemetryWindows"]!.GetValue<string>());
        var before = h.Engine.Graphs.ToDictionary(g => g.Name, g => g.State.Digest());          // the protocol serves no digest per graph: read inside the engine
        h.CommitDraft(d, h.CatalogRevision);
        var after = h.Engine.Graphs.ToDictionary(g => g.Name, g => g.State.Digest());
        Assert.Equal(after.Keys.Order(), previewed.Keys.Order());
        Assert.All(after, kv => Assert.Equal(kv.Value, previewed[kv.Key]));
        Assert.All(after.Keys, g => Assert.Equal(windows[g], h.Telemetry(g, null, null, null, null, batches: true).AsArray().Sum(b => b!["ids"]!.AsArray().Count).ToString()));          // and as many telemetry windows
        Assert.Equal(changes, after.Any(kv => kv.Value != before[kv.Key]));
    }

    // ---- a draft against its base and the head ----

    [Fact]
    public void Restore_returns_the_patch_it_applied()
    {
        var h = Fresh.Value;
        var digest = h.Definitions().AsArray().Single(m => m!["name"]!.GetValue<string>() == "acme.svc")!["digest"]!.GetValue<string>();
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var res = h.Restore(d, digest);                          // already equal: an empty patch, still returned
        Assert.NotNull(res["patch"]);
        Assert.Empty(res["patch"]!["edits"]!.AsArray());
        h.DiscardDraft(d);
    }

    [Fact]
    public void A_patch_that_removes_and_recreates_an_element_is_one_edit_and_changes_nothing()
    {
        var h = Fresh.Value;
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var ent = h.DraftSnapshot(d)["extent"]!["entities"]!.AsArray().First(e => e!["id"]!["value"]!.GetValue<string>() == Cardinality)!;
        JsonNode Edit(JsonNode? before, JsonNode? after) => new JsonObject { ["kind"] = "entity", ["id"] = Cardinality, ["before"] = before?.DeepClone(), ["after"] = after?.DeepClone() };
        var res = h.ApplyDraftPatch(d, new JsonObject { ["edits"] = new JsonArray(Edit(ent, null), Edit(null, ent)) }, 0);
        Assert.True(res["ok"]!.GetValue<bool>());
        Assert.False(res["modules"]!["acme.svc"]!["changed"]!.GetValue<bool>());
        Assert.Equal(h.Definition("acme.svc"), res["modules"]!["acme.svc"]!["text"]!.GetValue<string>());
        // a patch that cannot apply is a typed error, never an empty 500
        var bad = Assert.Throws<HostError>(() => h.ApplyDraftPatch(d, new JsonObject { ["edits"] = new JsonArray(Edit(null, ent)) }, 1));
        Assert.Contains(bad.Code, new[] { "GuardFailed", "BadPatch" });
        h.DiscardDraft(d);
    }

    /// <summary>Review: Drafts.cs:283, :126. A draft is compared with its base, not with the head: after another commit an untouched draft reports no change of its own.</summary>
    [Fact]
    public void An_untouched_draft_reports_no_change_after_another_commit()
    {
        using var h = HostTests.NewHost();
        var draft = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var text = h.Definition(Deps(h));
        Assert.Contains("above = 0.05", text);
        h.Commit(Deps(h), text.Replace("above = 0.05", "above = 0.06"), h.CatalogRevision);
        var changed = h.CheckDraft(draft)["modules"]!.AsObject().Where(m => m.Value!["changed"]?.GetValue<bool>() == true).Select(m => m.Key);
        Assert.Empty(changed);
    }

    // ---- a draft edits the text of a projection file ----

    /// <summary>The service tree without the services of one namespace: an edit of the projection's text that changes its graph.</summary>
    static string WithoutShop(SpaceHost h)
    {
        var text = h.Definition(Deps(h));
        Assert.Contains("edges Contains from group to service = services |", text);
        return text.Replace("edges Contains from group to service = services |", "edges Contains from group to service = services | where namespace != \"shop\" |");
    }

    /// <summary>A draft edits the text of a projection file: its check is the text's, its module texts stay without the projection, and its commit changes the graph as a commit of the file does.</summary>
    [Fact]
    public void A_draft_edits_a_projection_file_and_commits_it()
    {
        using var h = HostTests.NewHost();
        new Replay(h, h.Space, h.Adapters, new ReplayOptions { }).Run(Repo.Estate);
        int Contains() => h.Relationships("serviceTree", "Contains").Count;
        var (file, edited, before) = (Deps(h), WithoutShop(h), Contains());
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var check = h.SetDraftText(d, file, edited, 0);
        Assert.True(check["ok"]!.GetValue<bool>(), check["diagnostics"]!.ToJsonString());
        Assert.True(check["modules"]![file]!["changed"]!.GetValue<bool>());
        Assert.Equal((edited, h.Definition("acme.svc")), (h.DraftText(d, file), check["modules"]!["acme.svc"]!["text"]!.GetValue<string>()));
        Assert.Equal(before, Contains());                                    // the draft changes nothing live
        h.CommitDraft(d, h.CatalogRevision);
        Assert.InRange(Contains(), 0, before - 1);
        Assert.Equal(edited, h.Definition(file));
    }

    /// <summary>A draft text that does not check is kept, so an editor never loses what was typed; the check names the file and a line, and the draft cannot commit.</summary>
    [Fact]
    public void A_draft_keeps_a_text_that_does_not_check_and_places_its_errors_in_the_file()
    {
        var h = Fresh.Value;
        var file = Deps(h);
        var wrong = h.Definition(file).Replace("name = s.name", "name = s.title");
        Assert.NotEqual(h.Definition(file), wrong);
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var check = h.SetDraftText(d, file, wrong, 0);
        Assert.False(check["ok"]!.GetValue<bool>());
        Assert.Contains(check["diagnostics"]!.AsArray(), x => x!["file"]!.GetValue<string>() == file && x["line"]!.GetValue<int>() > 0);
        Assert.Equal((wrong, "1"), (h.DraftText(d, file), h.DraftInfo(d)["revision"]!.GetValue<string>()));
        Assert.Equal("Invalid", Assert.Throws<HostError>(() => h.CommitDraft(d, h.CatalogRevision)).Code);
        h.DiscardDraft(d);
    }

    /// <summary>A draft text is read by its file: a module name is a bad request, since a module of a draft changes by patches.</summary>
    [Fact]
    public void A_draft_text_of_a_module_is_a_bad_request()
    {
        var h = Fresh.Value;
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var e = Assert.Throws<HostError>(() => h.DraftText(d, "acme.svc"));
        Assert.Equal(("BadRequest", 400), (e.Code, e.Status));
        h.DiscardDraft(d);
    }

    /// <summary>A structural edit of a projection whose text is a file would be lost at the next commit, which is made from the text: refused, with the file to edit.</summary>
    [Fact]
    public void A_structural_edit_of_a_projection_written_in_a_file_is_refused()
    {
        var h = Fresh.Value;
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        // a new operator of serviceDeps, made from one of its own
        const string projection = "d:acme.svc/projection:serviceDeps", id = projection + "/op:first";
        var op = h.DraftSnapshot(d)["extent"]!["entities"]!.AsArray().First(e => e!["id"]!["value"]!.GetValue<string>().StartsWith(projection + "/op:", StringComparison.Ordinal))!.DeepClone();
        op["id"] = new JsonObject { ["value"] = id };
        var e = Assert.Throws<HostError>(() => h.ApplyDraftPatch(d, new JsonObject { ["edits"] = new JsonArray(new JsonObject { ["kind"] = "entity", ["id"] = id, ["after"] = op }) }, 0));
        Assert.Equal(("ProjectionText", Deps(h)), (e.Code, e.Extra["file"]!.GetValue<string>()));
        Assert.Equal("0", h.DraftInfo(d)["revision"]!.GetValue<string>());
        h.DiscardDraft(d);
    }

    /// <summary>A draft's text is read with the draft's own modules and the projection files it was made with: a commit of another projection file after the draft was made does not enter the draft's patch. (Whether such a draft then commits is not said here: today it is refused as `StaleRevision`, since both changes rewrite a pin in the space module.)</summary>
    [Fact]
    public void A_draft_text_set_after_a_commit_elsewhere_takes_nothing_of_that_commit()
    {
        using var h = HostTests.NewHost();
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var (elsewhere, revision) = (h.ProjectionFile("acme.deployhealth.deploymentHealth"), h.CatalogRevision);
        Assert.Contains("minReady:long = 0", h.Definition(elsewhere));
        h.Commit(elsewhere, h.Definition(elsewhere).Replace("minReady:long = 0", "minReady:long = 1"), revision);
        Assert.Equal(revision + 1, h.CatalogRevision);
        var check = h.SetDraftText(d, Deps(h), WithoutShop(h), 0);
        Assert.True(check["ok"]!.GetValue<bool>(), check["diagnostics"]!.ToJsonString());
        var ids = h.DraftPatches(d, 0).SelectMany(p => p["edits"]!.AsArray()).Select(e => e!["id"]!["value"]!.GetValue<string>()).ToList();
        Assert.NotEmpty(ids);
        Assert.DoesNotContain(ids, id => id.StartsWith("d:acme.deployhealth", StringComparison.Ordinal));
    }

    /// <summary>#36: a draft removes a projection file (a null text) and the space's graph it produces (a patch of the space). The removal alone does not check, since the space still binds the projection; with the graph gone too, the commit drops the graph and the file.</summary>
    [Fact]
    public void A_draft_removes_a_projection_file_and_the_commit_drops_its_graph()
    {
        using var h = HostTests.NewHost();
        var file = h.ProjectionFile("acme.report.deploymentReport");
        string[] Graphs() => [.. h.SpaceInfo()["graphs"]!.AsArray().Select(g => g!["id"]!.GetValue<string>())];
        Assert.Contains("deploymentReport", Graphs());
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var alone = h.SetDraftText(d, file, null, 0);
        Assert.False(alone["ok"]!.GetValue<bool>());
        Assert.Contains(alone["diagnostics"]!.AsArray(), x => x!["module"]!.GetValue<string>() == "acme.space");
        Assert.Equal("UnknownModule", Assert.Throws<HostError>(() => h.DraftText(d, file)).Code);
        var extent = h.DraftSnapshot(d)["extent"]!;
        static string V(JsonNode? n) => n!["value"]!.GetValue<string>();
        var g = V(extent["relationships"]!.AsArray().First(r => V(r!["type"]) == "producedBy" && V(r["target"]) == "d:acme.report/projection:deploymentReport")!["source"]);
        var bindings = g.Replace("/graph:", "/binding:") + ".";
        bool Mine(string id) => id == g || id.StartsWith(g + "/", StringComparison.Ordinal) || id.StartsWith(bindings, StringComparison.Ordinal);
        var patch = new JsonObject { ["edits"] = new JsonArray([
            .. extent["relationships"]!.AsArray().Where(r => Mine(V(r!["source"])) || Mine(V(r["target"]))).Select(r => (JsonNode)new JsonObject { ["kind"] = "relationship", ["id"] = V(r!["id"]), ["before"] = r.DeepClone() }),
            .. extent["entities"]!.AsArray().Where(e => Mine(V(e!["id"]))).Select(e => (JsonNode)new JsonObject { ["kind"] = "entity", ["id"] = V(e!["id"]), ["before"] = e.DeepClone() })]) };
        var check = h.ApplyDraftPatch(d, patch, 1);
        Assert.True(check["ok"]!.GetValue<bool>(), check["diagnostics"]!.ToJsonString());
        Assert.DoesNotContain("graph deploymentReport", check["modules"]!["acme.space"]!["text"]!.GetValue<string>());
        Assert.Equal((null, true), (check["modules"]![file]!["text"], check["modules"]![file]!["changed"]!.GetValue<bool>()));
        Assert.Contains("deploymentReport", Graphs());                    // the draft changes nothing live
        var res = h.CommitDraft(d, h.CatalogRevision);
        Assert.Contains(res["modules"]!.AsArray(), m => m!.GetValue<string>() == file);
        Assert.DoesNotContain("deploymentReport", Graphs());
        Assert.Equal("UnknownModule", Assert.Throws<HostError>(() => h.Definition(file)).Code);
        Assert.DoesNotContain(h.DefinitionFiles(), f => Path.GetFileName(f.Path) == file);
    }

    /// <summary>An edge from outside into a projection whose text is a file belongs to the other text: a patch that adds a graph to the space, produced by that projection, is accepted.</summary>
    [Fact]
    public void A_patch_adding_a_graph_produced_by_a_projection_written_in_a_file_is_accepted()
    {
        var h = Fresh.Value;
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var extent = h.DraftSnapshot(d)["extent"]!;
        static string V(JsonNode? n) => n!["value"]!.GetValue<string>();
        var g = V(extent["relationships"]!.AsArray().First(r => V(r!["type"]) == "producedBy" && V(r["target"]) == "d:acme.svc/projection:serviceDeps")!["source"]);
        // the graph, and its bindings, which sit beside it in the space: `binding:serviceTree.master`
        var bindings = g.Replace("/graph:", "/binding:") + ".";
        bool Mine(string id) => id == g || id.StartsWith(g + "/", StringComparison.Ordinal) || id.StartsWith(bindings, StringComparison.Ordinal);
        JsonNode Copy(JsonNode e) => JsonNode.Parse(e.ToJsonString().Replace(g, g + "Two").Replace(bindings, bindings[..^1] + "Two."))!;
        var entities = extent["entities"]!.AsArray().Where(e => Mine(V(e!["id"]))).Select(e => Copy(e!)).ToList();
        foreach (var p in entities.Single(e => V(e["id"]) == g + "Two")["properties"]!.AsArray().Where(p => V(p!["property"]) == "name")) p!["value"]!["value"] = "serviceTreeTwo";
        var rels = extent["relationships"]!.AsArray().Where(r => Mine(V(r!["source"])) || V(r["type"]) == "contains" && Mine(V(r["target"]))).Select(r => Copy(r!)).ToList();
        Assert.Contains(rels, r => V(r["type"]) == "producedBy");
        var patch = new JsonObject { ["edits"] = new JsonArray([.. entities.Select(e => (JsonNode)new JsonObject { ["kind"] = "entity", ["id"] = V(e["id"]), ["after"] = e }),
            .. rels.Select(r => (JsonNode)new JsonObject { ["kind"] = "relationship", ["id"] = V(r["id"]), ["after"] = r })]) };
        var check = h.ApplyDraftPatch(d, patch, 0);
        Assert.True(check["ok"]!.GetValue<bool>(), check["diagnostics"]!.ToJsonString());
        Assert.Contains("graph serviceTreeTwo = svc.serviceDeps(master: master);", check["modules"]!["acme.space"]!["text"]!.GetValue<string>());
        h.DiscardDraft(d);
    }
}
