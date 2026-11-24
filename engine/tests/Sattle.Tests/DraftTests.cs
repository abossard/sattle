using System.Text.Json.Nodes;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>PROTOCOL.md v3, drafts (Proposition 4.10, 9.9, 9.10): a draft is edited by the texts of its module documents and projection files, with undo as an inverse edit, is checked against its base, previewed without touching anything live (T-PREVIEW), committed, and every release is listed and restorable. The tests that drive a world are `Slow`.</summary>
public class DraftTests
{
    const string Cardinality = "d:acme.svc/constraint:cardinality-Contains";          // a service is in exactly one group: `max` 1 per target
    /// <summary>The cardinality of `Contains` in the module of the service tree, and the same with two groups allowed per service.</summary>
    const string OneGroup = """{"kind": "cardinality", "type": "Contains", "side": "target", "min": 1, "max": 1}""";
    const string TwoGroups = """{"kind": "cardinality", "type": "Contains", "side": "target", "min": 1, "max": 2}""";
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

    /// <summary>The module document of the service tree with two groups allowed per service.</summary>
    static string WithTwoGroups(SpaceHost h)
    {
        var text = h.Definition("acme.svc");
        Assert.Contains(OneGroup, text);
        return text.Replace(OneGroup, TwoGroups);
    }

    /// <summary>The service tree's text with a threshold that never recovers: below 0.0 for 30 windows keeps the degraded state.</summary>
    static string NeverRecovers(SpaceHost h)
    {
        var text = h.Definition(Deps(h));
        Assert.Contains("\"below\": 0.02, \"exitAfter\": 3", text);
        return text.Replace("\"below\": 0.02, \"exitAfter\": 3", "\"below\": 0.0, \"exitAfter\": 30");
    }

    /// <summary>Code review 2, defect 7, for a module written as a document: a structural patch of it is refused and not recorded, so the draft stays usable; the module changes by its draft text.</summary>
    [Fact]
    public void A_structural_patch_of_a_module_document_is_refused_and_leaves_the_draft_as_it_was()
    {
        var h = Fresh.Value;
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var ent = h.DraftSnapshot(d)["extent"]!["entities"]!.AsArray().First(e => e!["id"]!["value"]!.GetValue<string>() == Cardinality)!;
        JsonNode Edit(JsonNode? before, JsonNode? after) => new JsonObject { ["kind"] = "entity", ["id"] = Cardinality, ["before"] = before?.DeepClone(), ["after"] = after?.DeepClone() };
        var e = Assert.Throws<HostError>(() => h.ApplyDraftPatch(d, new JsonObject { ["edits"] = new JsonArray(Edit(ent, null)) }, 0));
        Assert.Equal(("ModuleText", 422), (e.Code, e.Status));
        Assert.Equal("0", h.DraftInfo(d)["revision"]!.GetValue<string>());
        var check = h.CheckDraft(d);                                                   // the draft still answers, unchanged
        Assert.True(check["ok"]!.GetValue<bool>());
        Assert.False(check["modules"]!["acme.svc"]!["changed"]!.GetValue<bool>());
        h.DiscardDraft(d);
    }

    /// <summary>A module changes in a draft by the text of its document, and undo is the inverse edit applied forward as a new revision, not a rewind: the patches are kept, the committed log is untouched, and an edit against an old revision is stale.</summary>
    [Fact]
    public void A_module_edit_is_a_draft_text_and_undo_is_an_inverse_edit_not_a_rewind()
    {
        var (h, d) = NewDraft(); using var hostGuard = h;
        var rev0 = h.CatalogRevision;
        var before = h.Definition("acme.svc");
        var edited = WithTwoGroups(h);
        var r1 = h.SetDraftText(d, "acme.svc", edited, 0);
        Assert.True(r1["ok"]!.GetValue<bool>(), r1["diagnostics"]!.ToJsonString());
        Assert.Contains(TwoGroups, r1["modules"]!["acme.svc"]!["text"]!.GetValue<string>());
        Assert.True(r1["modules"]!["acme.svc"]!["changed"]!.GetValue<bool>());
        var r2 = h.SetDraftText(d, "acme.svc", before, 1);                     // undo = the inverse edit, applied forward as revision 2
        Assert.Equal("2", r2["revision"]!.GetValue<string>());
        Assert.Equal(before, r2["modules"]!["acme.svc"]!["text"]!.GetValue<string>());
        Assert.False(r2["modules"]!["acme.svc"]!["changed"]!.GetValue<bool>());
        var r3 = h.SetDraftText(d, "acme.svc", edited, 2);                     // redo
        Assert.Contains(TwoGroups, r3["modules"]!["acme.svc"]!["text"]!.GetValue<string>());
        Assert.Equal(rev0, h.CatalogRevision);                                // the committed log is untouched
        Assert.Equal(3, h.DraftPatches(d, 0).Count);
        Assert.Equal("StaleRevision", Assert.Throws<HostError>(() => h.SetDraftText(d, "acme.svc", before, 1)).Code);
        Assert.Equal("3", h.DraftInfo(d)["revision"]!.GetValue<string>());
    }

    [Fact]
    public void Invalid_structure_is_reported_by_definition_element_id()
    {
        var (h, d) = NewDraft(); using var hostGuard = h;
        var bad = h.Definition("acme.svc").Replace("\"temporality\": \"delta\"", "\"temporality\": \"bogus\"");
        Assert.NotEqual(h.Definition("acme.svc"), bad);
        var r = h.SetDraftText(d, "acme.svc", bad, 0);
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Contains(r["diagnostics"]!.AsArray(), x => x!["severity"]!.GetValue<string>() == "error" && x["module"]?.GetValue<string>() == "acme.svc");
    }

    [Fact]
    public void Committing_a_draft_activates_it_and_every_release_is_listed_diffed_and_restorable()
    {
        var (h, d) = NewDraft(); using var hostGuard = h;
        string Digest() => h.Definitions().AsArray().Single(m => m!["name"]!.GetValue<string>() == "acme.svc")!["digest"]!.GetValue<string>();
        var originalDigest = Digest();
        var originalText = h.Definition("acme.svc");
        h.SetDraftText(d, "acme.svc", WithTwoGroups(h), 0);
        var res = h.CommitDraft(d, h.CatalogRevision);
        Assert.Contains(res["modules"]!.AsArray(), m => m!.GetValue<string>() == "acme.svc");
        Assert.NotEqual(originalDigest, Digest());
        Assert.Throws<HostError>(() => h.DraftInfo(d));                       // committed drafts are discarded
        var rels = h.Releases("acme.svc").AsArray();
        Assert.Equal(2, rels.Count);
        Assert.Equal(originalDigest, rels[1]!["supersedes"]!.GetValue<string>());
        var diff = h.ReleaseDiff(originalDigest, Digest());
        Assert.Single(diff["edits"]!.AsArray(), e => e!["id"]!["value"]!.GetValue<string>() == Cardinality);
        Assert.Contains(TwoGroups, h.ReleaseText(Digest()));
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
        h.SetDraftText(d, "acme.svc", WithTwoGroups(h), 0);
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
        var node = ops.First(o => o!["columns"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).SequenceEqual(["service", "instances"]));
        Assert.Equal("Integer", Col(node, "instances")!["kind"]!.GetValue<string>());
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
        string[] columns = ["s", "s.name", "s.namespace", "s.site"];                              // the typed column and the properties the plan reads from it
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
        var text = draft == "a threshold that never recovers" ? NeverRecovers(h) : h.Definition(Deps(h)).Replace("\"lookback\": \"24h\"", "\"lookback\": \"2h\"");
        Assert.True(h.SetDraftText(d, Deps(h), text, 0)["ok"]!.GetValue<bool>());
        var p = h.Preview(d, new JsonObject { ["include"] = new JsonArray("counts") });
        Assert.True(p["scope"]!["complete"]!.GetValue<bool>());
        var previewed = p["graphs"]!.AsArray().Where(g => g!["removed"] is null).ToDictionary(g => g!["id"]!.GetValue<string>(), g => g!["digest"]!.GetValue<string>());
        var windows = p["graphs"]!.AsArray().Where(g => g!["removed"] is null).ToDictionary(g => g!["id"]!.GetValue<string>(), g => g!["counts"]!["telemetryWindows"]!.GetValue<string>());
        Dictionary<string, string> Served() => h.Liveness()["graphs"]!.AsArray().Where(g => g!["name"]!.GetValue<string>() != "catalog").ToDictionary(g => g!["name"]!.GetValue<string>(), g => g!["digest"]!.GetValue<string>());
        var before = Served();
        h.CommitDraft(d, h.CatalogRevision);
        var after = Served();
        Assert.Equal(after.Keys.Order(), previewed.Keys.Order());
        Assert.All(after, kv => Assert.Equal(kv.Value, previewed[kv.Key]));
        Assert.All(after.Keys, g => Assert.Equal(windows[g], h.Telemetry(g, null, null, null, null, batches: true).AsArray().Sum(b => b!["ids"]!.AsArray().Count).ToString()));          // and as many telemetry windows
        Assert.Equal(changes, after.Any(kv => kv.Value != before[kv.Key]));
    }

    // ---- a draft against its base and the head ----

    /// <summary>Restoring a release of a module written as a document gives the draft the texts the release was made from: a release equal to the draft changes nothing, and the draft's check is returned.</summary>
    [Fact]
    public void Restore_of_a_release_equal_to_the_draft_changes_nothing()
    {
        var h = Fresh.Value;
        var digest = h.Definitions().AsArray().Single(m => m!["name"]!.GetValue<string>() == "acme.svc")!["digest"]!.GetValue<string>();
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var res = h.Restore(d, digest);                          // already equal: no edit, still the check
        Assert.True(res["ok"]!.GetValue<bool>(), res["diagnostics"]!.ToJsonString());
        Assert.Equal("0", res["revision"]!.GetValue<string>());
        Assert.False(res["modules"]!["acme.svc"]!["changed"]!.GetValue<bool>());
        h.DiscardDraft(d);
    }

    /// <summary>Review: Drafts.cs:283, :126. A draft is compared with its base, not with the head: after another commit an untouched draft reports no change of its own.</summary>
    [Fact]
    public void An_untouched_draft_reports_no_change_after_another_commit()
    {
        using var h = HostTests.NewHost();
        var draft = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var text = h.Definition(Deps(h));
        Assert.Contains("\"above\": 0.05", text);
        h.Commit(Deps(h), text.Replace("\"above\": 0.05", "\"above\": 0.06"), h.CatalogRevision);
        var changed = h.CheckDraft(draft)["modules"]!.AsObject().Where(m => m.Value!["changed"]?.GetValue<bool>() == true).Select(m => m.Key);
        Assert.Empty(changed);
    }

    // ---- a draft edits the text of a projection file ----

    /// <summary>The service tree without the services of one namespace: an edit of the projection's text that changes its graph.</summary>
    static string WithoutShop(SpaceHost h)
    {
        var program = JsonNode.Parse(h.Definition(Deps(h)))!;
        var sink = program["verbs"]!.AsArray().Single(v => v!["target"]?["edges"]?.AsArray().Any(e => e!["label"]!.GetValue<string>() == "Contains") == true)!["id"]!.GetValue<string>();
        var wire = program["wires"]!.AsArray().Single(w => w!["to"]!.GetValue<string>() == sink)!;
        program["verbs"]!.AsArray().Add(JsonNode.Parse("""{"id": "outsideShop", "verb": "where", "condition": {"fn": "!=", "args": [{"ref": "namespace"}, {"lit": "shop"}]}}"""));
        program["wires"]!.AsArray().Add(new JsonObject { ["from"] = "outsideShop", ["to"] = sink });
        wire["to"] = "outsideShop";
        return program.ToJsonString();
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
        var wrong = h.Definition(file).Replace("\"name\": {\"ref\": \"s.name\"}", "\"name\": {\"ref\": \"s.title\"}");
        Assert.NotEqual(h.Definition(file), wrong);
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var check = h.SetDraftText(d, file, wrong, 0);
        Assert.False(check["ok"]!.GetValue<bool>());
        Assert.Contains(check["diagnostics"]!.AsArray(), x => x!["file"]!.GetValue<string>() == file && x["line"]!.GetValue<int>() > 0);
        Assert.Equal((wrong, "1"), (h.DraftText(d, file), h.DraftInfo(d)["revision"]!.GetValue<string>()));
        Assert.Equal("Invalid", Assert.Throws<HostError>(() => h.CommitDraft(d, h.CatalogRevision)).Code);
        h.DiscardDraft(d);
    }

    /// <summary>A module written as a document is read in a draft by its name, as the text of its document; a draft cannot remove a module.</summary>
    [Fact]
    public void A_draft_text_of_a_module_is_its_document_and_cannot_be_removed()
    {
        var h = Fresh.Value;
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        Assert.Equal(h.Definition("acme.svc"), h.DraftText(d, "acme.svc"));
        var e = Assert.Throws<HostError>(() => h.SetDraftText(d, "acme.svc", null, 0));
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
        var (elsewhere, revision) = (h.ProjectionFile("acme.readiness.readiness"), h.CatalogRevision);
        const string MinReady = "\"minReady\", \"kind\": \"Integer\", \"default\": ";
        Assert.Contains(MinReady + "0", h.Definition(elsewhere));
        h.Commit(elsewhere, h.Definition(elsewhere).Replace(MinReady + "0", MinReady + "1"), revision);
        Assert.Equal(revision + 1, h.CatalogRevision);
        var check = h.SetDraftText(d, Deps(h), WithoutShop(h), 0);
        Assert.True(check["ok"]!.GetValue<bool>(), check["diagnostics"]!.ToJsonString());
        var ids = h.DraftPatches(d, 0).SelectMany(p => p["edits"]!.AsArray()).Select(e => e!["id"]!["value"]!.GetValue<string>()).ToList();
        Assert.NotEmpty(ids);
        Assert.DoesNotContain(ids, id => id.StartsWith("d:acme.readiness", StringComparison.Ordinal));
    }

    /// <summary>The space module of the draft without the graph `name`.</summary>
    static string SpaceWithout(SpaceHost h, string draft, string name)
    {
        var space = JsonNode.Parse(h.DraftText(draft, "acme.space"))!;
        var graphs = space["space"]!["graphs"]!.AsArray();
        graphs.Remove(graphs.Single(g => g!["name"]!.GetValue<string>() == name));
        return space.ToJsonString();
    }

    /// <summary>#36: a draft removes a projection file (a null text) and the space's graph it produces (an edit of the space module). The removal alone does not check, since the space still binds the projection; with the graph gone too, the commit drops the graph and the file.</summary>
    [Fact]
    public void A_draft_removes_a_projection_file_and_the_commit_drops_its_graph()
    {
        using var h = HostTests.NewHost();
        var file = h.ProjectionFile("acme.report.readinessReport");
        string[] Graphs() => [.. h.SpaceInfo()["graphs"]!.AsArray().Select(g => g!["id"]!.GetValue<string>())];
        Assert.Contains("readinessReport", Graphs());
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var alone = h.SetDraftText(d, file, null, 0);
        Assert.False(alone["ok"]!.GetValue<bool>());
        Assert.Contains(alone["diagnostics"]!.AsArray(), x => x!["module"]?.GetValue<string>() == "acme.space");
        Assert.Equal("UnknownModule", Assert.Throws<HostError>(() => h.DraftText(d, file)).Code);
        var check = h.SetDraftText(d, "acme.space", SpaceWithout(h, d, "readinessReport"), 1);
        Assert.True(check["ok"]!.GetValue<bool>(), check["diagnostics"]!.ToJsonString());
        Assert.DoesNotContain("\"readinessReport\"", check["modules"]!["acme.space"]!["text"]!.GetValue<string>());
        Assert.Equal((null, true), (check["modules"]![file]!["text"], check["modules"]![file]!["changed"]!.GetValue<bool>()));
        Assert.Contains("readinessReport", Graphs());                    // the draft changes nothing live
        var res = h.CommitDraft(d, h.CatalogRevision);
        Assert.Contains(res["modules"]!.AsArray(), m => m!.GetValue<string>() == file);
        Assert.DoesNotContain("readinessReport", Graphs());
        Assert.Equal("UnknownModule", Assert.Throws<HostError>(() => h.Definition(file)).Code);
        Assert.DoesNotContain(h.DefinitionFiles(), f => Path.GetFileName(f.Path) == file);
    }

    /// <summary>A projection written in a file produces a second graph of the space: a draft of the space module that binds it again checks, and changes the space module only.</summary>
    [Fact]
    public void A_draft_of_the_space_adding_a_graph_produced_by_a_projection_written_in_a_file_is_accepted()
    {
        var h = Fresh.Value;
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var space = JsonNode.Parse(h.DraftText(d, "acme.space"))!;
        space["space"]!["graphs"]!.AsArray().Add(JsonNode.Parse("""{"name": "serviceTreeTwo", "projection": "svc.serviceDeps", "master": false, "inputs": {"master": "master"}}"""));
        var check = h.SetDraftText(d, "acme.space", space.ToJsonString(), 0);
        Assert.True(check["ok"]!.GetValue<bool>(), check["diagnostics"]!.ToJsonString());
        Assert.Contains("\"serviceTreeTwo\"", check["modules"]!["acme.space"]!["text"]!.GetValue<string>());
        Assert.Equal(["acme.space"], check["modules"]!.AsObject().Where(m => m.Value!["changed"]!.GetValue<bool>()).Select(m => m.Key));
        h.DiscardDraft(d);
    }
}
