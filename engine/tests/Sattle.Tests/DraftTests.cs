using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>PROTOCOL.md v3: palette from the kernel, drafts with inverse-patch undo, releases, restore, preview.</summary>
[Trait("Category", "Slow")]
public class DraftTests
{
    const string Cardinality = "d:acme.svc/constraint:cardinality-Contains";          // a service is in exactly one group: `per target 1 .. 1`
    const string FlowsOut = "d:acme.svc/prop:flowsOut";                               // the flows a service or a group sends, a delta telemetry property
    const string Deps = "acme.svc.serviceDeps.kql";                                    // the service tree, with the threshold that gives a dependency its status

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
        var text = h.Definition(Deps);
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
    public void The_palette_is_derived_from_the_kernel_and_marks_what_the_engine_cannot_run()
    {
        var p = Palette.Build();
        var ops = p["operators"]!.AsArray();
        Assert.Equal(18, ops.Count);
        var window = ops.First(o => o!["kind"]!.GetValue<string>() == "Window")!;
        Assert.Equal(["in", "out"], window["ports"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()));
        Assert.Equal(["ignore", "revise", "reject"], window["params"]!.AsArray().First(x => x!["name"]!.GetValue<string>() == "late")!["values"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.True(window["supported"]!.GetValue<bool>());
        Assert.True(ops.First(o => o!["kind"]!.GetValue<string>() == "Values")!["supported"]!.GetValue<bool>());
        Assert.False(ops.First(o => o!["kind"]!.GetValue<string>() == "Delta")!["supported"]!.GetValue<bool>());
        Assert.Contains(p["functions"]!.AsArray(), f => f!["name"]!.GetValue<string>() == "Divide" && f["signature"]!.GetValue<string>().Contains("Decimal"));
        Assert.Contains(p["ruleClasses"]!.AsArray(), c => c!["name"]!.GetValue<string>() == "lens");
        Assert.Contains(p["aggregates"]!.AsArray(), a => a!["name"]!.GetValue<string>() == "countDistinct");
    }

    [Fact]
    public void The_palette_describes_declarations_constraint_kinds_row_types_rules_and_value_roles_from_the_kernel()
    {
        var p = Palette.Build();
        Assert.Equal(["key", "enum", "range", "pattern", "cardinality", "acyclic"], p["declarations"]!.AsArray().First(x => x!["slot"]!.GetValue<string>() == "constraint")!["kinds"]!.AsArray().Select(k => k!["kind"]!.GetValue<string>()));
        Assert.Contains(p["declarations"]!.AsArray(), x => x!["slot"]!.GetValue<string>() == "rows" && x["type"]!.GetValue<string>() == "RowType");
        Assert.Equal(["assert", "review"], p["equivalencePolicies"]!["modes"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains(p["rowTypes"]!["field"]!["params"]!.AsArray(), x => x!["name"]!.GetValue<string>() == "optional");
        Assert.Contains(p["rules"]!["slots"]!.AsArray(), x => x!["name"]!.GetValue<string>() == "createDefault");
        Assert.Equal(["remove", "set"], p["rules"]!["onDelete"]!.AsArray().Select(x => x!.GetValue<string>()).Order());
        Assert.Equal(["ok", "warning", "danger", "unknown", "neutral"], p["valueRoles"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains(p["projectionPorts"]!.AsArray(), x => x!["kind"]!.GetValue<string>() == "param");
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
        var originalDigest = h.Catalog.Modules["acme.svc"].Digest;
        var originalText = h.Definition("acme.svc");
        var (doit, _) = SetProp(h, d, Cardinality, "max", Dec("2"));
        h.ApplyDraftPatch(d, doit, 0);
        var res = h.CommitDraft(d, h.CatalogRevision);
        Assert.Equal("acme.svc", res["modules"]![0]!.GetValue<string>());
        Assert.NotEqual(originalDigest, h.Catalog.Modules["acme.svc"].Digest);
        Assert.Throws<HostError>(() => h.DraftInfo(d));                       // committed drafts are discarded
        var rels = h.Releases("acme.svc").AsArray();
        Assert.Equal(2, rels.Count);
        Assert.Equal(originalDigest, rels[1]!["supersedes"]!.GetValue<string>());
        var diff = h.ReleaseDiff(originalDigest, h.Catalog.Modules["acme.svc"].Digest);
        Assert.Single(diff["edits"]!.AsArray(), e => e!["id"]!["value"]!.GetValue<string>() == Cardinality);
        Assert.Contains("per target 1 .. 2;", h.ReleaseText(h.Catalog.Modules["acme.svc"].Digest));
        Assert.Equal(originalText, h.ReleaseText(originalDigest));
        // restore the first release into a new draft and commit it: the digest comes back
        var d2 = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var restored = h.Restore(d2, originalDigest);
        Assert.True(restored["ok"]!.GetValue<bool>(), restored["diagnostics"]!.ToJsonString());
        h.CommitDraft(d2, h.CatalogRevision);
        Assert.Equal(originalDigest, h.Catalog.Modules["acme.svc"].Digest);
        Assert.Equal(3, h.Releases("acme.svc").AsArray().Count);
    }

    [Fact]
    public void Preview_evaluates_a_draft_with_a_stated_scope_and_touches_nothing_live()
    {
        var (h, d) = NewDraft(); using var hostGuard = h;
        HostTests.Drive(h);
        string Status() => h.Engine.Graph("serviceTree").State.Rels.Single(r => Ids.Name(r.Type) == "DependsOn").Props.Text("d:acme.svc/prop:status")!;
        Assert.Equal("ok", Status());
        Assert.True(h.SetDraftText(d, Deps, NeverRecovers(h), 0)["ok"]!.GetValue<bool>());
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
        Assert.Equal(h.Engine.Identities.Minted.ToString(), idn["live"]!["minted"]!.GetValue<string>());
        // a bounded scope is stated and has no diff against the live outputs
        var small = h.Preview(d, new JsonObject { ["scope"] = new JsonObject { ["maxRows"] = "5" }, ["graphs"] = new JsonArray("serviceTree") });
        Assert.False(small["scope"]!["complete"]!.GetValue<bool>());
        Assert.Null(small["graphs"]![0]!["diff"]);
        Assert.False(small["identity"]!["comparable"]!.GetValue<bool>());
        Assert.Null(small["identity"]!["difference"]);
        Assert.True(long.Parse(small["identity"]!["preview"]!["minted"]!.GetValue<string>()) < h.Engine.Identities.Minted);   // fewer inputs, fewer identities
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
        JsonNode Op(string n) => ops.Single(o => o!["name"]!.GetValue<string>() == n)!;
        JsonNode Col(JsonNode op, string n) => op["columns"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == n)!;
        var s = Col(Op("services_1"), "s");
        Assert.Equal("Entity", s["kind"]!.GetValue<string>());
        Assert.EndsWith("Service", s["type"]!.GetValue<string>());
        Assert.Equal("data", s["role"]!.GetValue<string>());
        Assert.Equal(["service", "pods"], Op("serviceNode_1")["columns"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
        Assert.Equal("Integer", Col(Op("serviceNode_1"), "pods")["kind"]!.GetValue<string>());
        var flows = Col(Op("flows_6"), "flows");
        Assert.Equal("telemetry", flows["role"]!.GetValue<string>());
        Assert.NotNull(flows["period"]);
        Assert.All(ops, o => Assert.StartsWith("d:acme.svc/projection:serviceDeps/op:", o!["op"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("20", false)]
    [InlineData("1", true)]
    public void A_preview_with_rows_returns_the_rows_each_operator_holds(string rowLimit, bool truncated)
    {
        var (h, d) = NewDraft(); using var hostGuard = h;
        HostTests.Drive(h);
        var p = h.Preview(d, new JsonObject { ["graphs"] = new JsonArray("serviceTree"), ["include"] = new JsonArray("counts", "rows"), ["rowLimit"] = rowLimit });
        var ops = p["graphs"]![0]!["operators"]!.AsArray();
        var services = ops.Single(o => o!["name"]!.GetValue<string>() == "services_1")!;
        var live = h.Engine.Graph("serviceTree").State.Entities.Count(e => Ids.Name(e.Type) == "ServiceNode");
        Assert.Equal(live.ToString(), services["rows"]!.GetValue<string>());
        Assert.Equal(services["rows"]!.GetValue<string>(), services["weight"]!.GetValue<string>());       // each service once
        Assert.Equal(truncated, services["truncated"]!.GetValue<bool>());
        var sample = services["sample"]!.AsArray();
        Assert.Equal(Math.Min(live, int.Parse(rowLimit)), sample.Count);
        Assert.Equal(["s", "s.cluster", "s.name", "s.namespace"], services["columns"]!.AsArray().Select(c => c!.GetValue<string>()));          // the typed column and the properties the plan reads from it
        Assert.All(sample, x => Assert.False(string.IsNullOrEmpty(x!["cells"]!["s"]!.GetValue<string>())));
        Assert.All(sample, x => Assert.False(string.IsNullOrEmpty(x!["cells"]!["s.name"]!.GetValue<string>())));
        Assert.All(ops, o => Assert.StartsWith("d:acme.svc/projection:serviceDeps/op:", o!["op"]!.GetValue<string>()));
        Assert.Null(h.Preview(d, new JsonObject { ["graphs"] = new JsonArray("serviceTree") })["graphs"]![0]!["operators"]);
    }

    /// <summary>T-PREVIEW: with no input between the preview and the commit, the complete preview's output of every graph equals the state the commit's activation produces (Proposition 9.10 (b)).</summary>
    [Theory]
    [InlineData("a threshold that never recovers", true)]
    [InlineData("a series that looks back 2h", false)]          // every window of this world is younger than two hours: the outputs stay as they were
    public void A_complete_preview_equals_the_activation_a_commit_performs(string draft, bool changes)
    {
        var (h, d) = NewDraft(); using var hostGuard = h;
        HostTests.Drive(h);
        var text = draft == "a threshold that never recovers" ? NeverRecovers(h) : h.Definition(Deps).Replace("telemetry(\"master\", 24h)", "telemetry(\"master\", 2h)");
        Assert.True(h.SetDraftText(d, Deps, text, 0)["ok"]!.GetValue<bool>());
        var p = h.Preview(d, new JsonObject { ["include"] = new JsonArray("counts") });
        Assert.True(p["scope"]!["complete"]!.GetValue<bool>());
        var previewed = p["graphs"]!.AsArray().Where(g => g!["removed"] is null).ToDictionary(g => g!["id"]!.GetValue<string>(), g => g!["digest"]!.GetValue<string>());
        var windows = p["graphs"]!.AsArray().Where(g => g!["removed"] is null).ToDictionary(g => g!["id"]!.GetValue<string>(), g => g!["counts"]!["telemetryWindows"]!.GetValue<string>());
        var before = h.Engine.Graphs.ToDictionary(g => g.Name, g => g.State.Digest());
        h.CommitDraft(d, h.CatalogRevision);
        var after = h.Engine.Graphs.ToDictionary(g => g.Name, g => g.State.Digest());
        Assert.Equal(after.Keys.Order(), previewed.Keys.Order());
        Assert.All(after, kv => Assert.Equal(kv.Value, previewed[kv.Key]));
        Assert.All(h.Engine.Graphs, g => Assert.Equal(windows[g.Name], ((InMemoryTelemetryStore)h.Engine.Telemetry).Dump(g.Key).Count.ToString()));          // and as many telemetry windows
        Assert.Equal(changes, after.Any(kv => kv.Value != before[kv.Key]));
    }
}
