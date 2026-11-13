using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>PROTOCOL.md v3: palette from the kernel, drafts with inverse-patch undo, releases, restore, preview.</summary>
[Trait("Category", "Slow")]
public class DraftTests
{
    const string Health = "d:acme.svc/projection:serviceDeps/op:dependsOn/state:status";          // the state clause of the lift statement of the service tree

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

    /// <summary>Code review 2, defect 7: a draft patch that the check cannot read is refused and not recorded, so the draft stays usable. Before, the patch was recorded first: an Integer beyond 64 bits answered 500, and so did every later check, preview and commit of that draft. Also the gap of defect 1: a patch that builds an expression in a circle is refused by name instead of overflowing the stack.</summary>
    [Theory]
    [InlineData("an integer beyond 64 bits")] [InlineData("an expression that contains itself")]
    public void A_draft_patch_that_cannot_be_checked_is_refused_and_leaves_the_draft_as_it_was(string what)
    {
        var (h, d) = NewDraft(); using var hostGuard = h;
        JsonNode patch;
        if (what == "an integer beyond 64 bits") patch = SetProp(h, d, "d:acme.svc/projection:serviceDeps/op:dependsOn/state:status", "exitAfter", new JsonObject { ["kind"] = "integer", ["value"] = "99999999999999999999" }).Do;
        else
        {
            // a `contains` edge from a call back to itself, with the slot of one of its own arguments
            var rels = h.DraftSnapshot(d)["extent"]!["relationships"]!.AsArray();
            var arg = rels.First(r => r!["type"]!["value"]!.GetValue<string>() == "contains" && r["id"]!["value"]!.GetValue<string>().Contains("acme.svc") && r["properties"]!.AsArray().Any(p => p!["property"]!["value"]!.GetValue<string>() == "slot" && p["value"]!["value"]!.GetValue<string>() == "arg"))!;
            var loop = arg.DeepClone(); var call = arg["source"]!["value"]!.GetValue<string>();
            loop["id"] = new JsonObject { ["value"] = arg["id"]!["value"]!.GetValue<string>() + "-loop" }; loop["target"] = new JsonObject { ["value"] = call };
            patch = new JsonObject { ["edits"] = new JsonArray(new JsonObject { ["kind"] = "relationship", ["id"] = loop["id"]!["value"]!.GetValue<string>(), ["after"] = loop }) };
        }
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
        Assert.Equal(20, ops.Count);
        var window = ops.First(o => o!["kind"]!.GetValue<string>() == "Window")!;
        Assert.Equal(["in", "out"], window["ports"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()));
        Assert.Equal(["ignore", "revise", "reject"], window["params"]!.AsArray().First(x => x!["name"]!.GetValue<string>() == "late")!["values"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.True(window["supported"]!.GetValue<bool>());
        Assert.True(ops.First(o => o!["kind"]!.GetValue<string>() == "Fixpoint")!["supported"]!.GetValue<bool>());
        Assert.True(ops.First(o => o!["kind"]!.GetValue<string>() == "Values")!["supported"]!.GetValue<bool>());
        Assert.False(ops.First(o => o!["kind"]!.GetValue<string>() == "Delta")!["supported"]!.GetValue<bool>());
        Assert.Contains(p["functions"]!.AsArray(), f => f!["name"]!.GetValue<string>() == "Divide" && f["signature"]!.GetValue<string>().Contains("Decimal"));
        Assert.Contains(p["ruleClasses"]!.AsArray(), c => c!["name"]!.GetValue<string>() == "lens");
        Assert.Contains(p["aggregates"]!.AsArray(), a => a!["name"]!.GetValue<string>() == "weightedMean");
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
    public void The_palette_describes_the_Lift_operator_with_its_ports_parameters_slots_and_the_state_clause()
    {
        var p = Palette.Build();
        var lift = p["operators"]!.AsArray().Single(o => o!["kind"]!.GetValue<string>() == "Lift")!;
        Assert.True(lift["supported"]!.GetValue<bool>());
        Assert.Equal("lift", lift["role"]!.GetValue<string>());
        Assert.Equal(["edges:in:graph", "map:in:graph", "out:out:rows"], lift["ports"]!.AsArray().Select(x => $"{x!["name"]}:{x["direction"]}:{x["kind"]}"));
        var ps = lift["params"]!.AsArray();
        Assert.Equal(["within", "images", "selfLoops"], ps.Select(x => x!["name"]!.GetValue<string>()));
        Assert.Equal("duration", ps.Single(x => x!["name"]!.GetValue<string>() == "within")!["quantity"]!.GetValue<string>());
        Assert.Equal(["reject", "each"], ps.Single(x => x!["name"]!.GetValue<string>() == "images")!["values"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.False(ps.Single(x => x!["name"]!.GetValue<string>() == "images")!["required"]!.GetValue<bool>());          // no images clause: the mapping must be a function
        Assert.Equal(["carry", "derive", "keep", "state", "when"], lift["slots"]!.AsArray().Where(x => x!["name"]!.GetValue<string>() != "port").Select(x => x!["name"]!.GetValue<string>()).Order());
        Assert.Contains("Match", lift["strategy"]!.GetValue<string>());
        Assert.Contains(p["functions"]!.AsArray(), f => f!["name"]!.GetValue<string>() == "EntityId");
        // defaults, the child types of the slots with their parameters, and what each slot holds
        Assert.Equal("86400000000000", ps.Single(x => x!["name"]!.GetValue<string>() == "within")!["default"]!.GetValue<string>());
        Assert.Equal("drop", ps.Single(x => x!["name"]!.GetValue<string>() == "selfLoops")!["default"]!.GetValue<string>());
        JsonNode Slot(string n) => lift["slots"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == n)!;
        var state = Slot("state")["childTypes"]!.AsArray().Single()!;
        Assert.Equal("LiftState", state["type"]!.GetValue<string>());
        Assert.Equal(["target", "of", "above", "enterAfter", "high", "below", "exitAfter", "low", "initial"], state["params"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()));
        Assert.Equal("count", state["params"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "enterAfter")!["quantity"]!.GetValue<string>());
        Assert.Equal("fieldRef", Slot("carry")["value"]!["kind"]!.GetValue<string>());
        Assert.Equal(["src", "dst"], Slot("keep")["value"]!["roots"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("expr", Slot("derive")["value"]!["kind"]!.GetValue<string>());
        Assert.Equal("expr", Slot("when")["value"]!["kind"]!.GetValue<string>());
        Assert.Equal(["target"], Slot("carry")["childTypes"]!.AsArray().Single()!["params"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()));
    }

    [Fact]
    public void A_skeleton_patch_creates_a_Lift_with_its_three_ports_and_the_checker_asks_for_the_wires_and_the_types()
    {
        var (h, d) = NewDraft();
        using var hostGuard = h;
        var patch = h.Skeleton(new JsonObject { ["kind"] = "Lift", ["container"] = "d:acme.svc/projection:serviceDeps", ["name"] = "l2", ["params"] = new JsonObject { ["within"] = "3600000000000" }, ["draft"] = d });
        Assert.Equal(4, patch["edits"]!.AsArray().Count(e => e!["kind"]!.GetValue<string>() == "entity"));          // the operator and its ports edges, map and out
        var res = h.ApplyDraftPatch(d, patch, 0);
        Assert.False(res["ok"]!.GetValue<bool>());                                                                   // unwired ports and no relationship types: not a statement yet
        Assert.True(res["diagnostics"]!.AsArray().Any(x => x!["element"] is { } el && el.GetValue<string>().StartsWith("d:acme.svc/projection:serviceDeps/op:l2")), res["diagnostics"]!.ToJsonString());
    }

    [Fact]
    public void A_draft_edit_of_a_lift_clause_prints_back_as_the_clause_and_a_preview_shows_what_it_changes()
    {
        var (h, d) = NewDraft();
        using var hostGuard = h;
        var lift = "d:acme.svc/projection:serviceDeps/op:dependsOn";
        var (keep, _) = SetProp(h, d, lift, "selfLoops", new JsonObject { ["kind"] = "text", ["value"] = "keep" });
        var r = h.ApplyDraftPatch(d, keep, 0);
        Assert.True(r["ok"]!.GetValue<bool>(), r["diagnostics"]!.ToJsonString());
        Assert.Contains("\n    selfloops keep;", r["modules"]!["acme.svc"]!["text"]!.GetValue<string>());                // printed as the clause, the other defaults stay unsaid
        var (within, _) = SetProp(h, d, lift, "within", new JsonObject { ["kind"] = "integer", ["value"] = "7200000000000" });
        var r2 = h.ApplyDraftPatch(d, within, 1);
        Assert.True(r2["ok"]!.GetValue<bool>(), r2["diagnostics"]!.ToJsonString());
        Assert.Contains("within 2h", r2["modules"]!["acme.svc"]!["text"]!.GetValue<string>());
        // an edit that makes the statement unliftable is a typed diagnostic that names the statement, not an engine fault
        var (bad, _) = SetProp(h, d, lift, "images", new JsonObject { ["kind"] = "text", ["value"] = "reject" });
        var rb = h.ApplyDraftPatch(d, bad, 2);
        Assert.True(rb["ok"]!.GetValue<bool>());                                                                      // `images reject` is a legal clause
        var preview = h.Preview(d, new JsonObject { ["graphs"] = new JsonArray("serviceTree"), ["include"] = new JsonArray("counts", "diff") });
        Assert.NotNull(preview["graphs"]);
    }

    [Fact]
    public void A_skeleton_patch_creates_an_operator_with_its_kernel_fixed_ports()
    {
        var (h, d) = NewDraft();
        using var hostGuard = h;
        var patch = h.Skeleton(new JsonObject { ["kind"] = "Window", ["container"] = "d:acme.svc/projection:serviceDeps", ["name"] = "w1", ["params"] = new JsonObject { ["width"] = "60000000000" }, ["draft"] = d });
        Assert.Equal(3, patch["edits"]!.AsArray().Count(e => e!["kind"]!.GetValue<string>() == "entity"));       // the operator and its two ports
        var res = h.ApplyDraftPatch(d, patch, 0);
        // a Window with unwired ports is a W1 error, reported by element id
        Assert.False(res["ok"]!.GetValue<bool>());
        Assert.Contains(res["diagnostics"]!.AsArray(), x => x!["element"] is { } el && el.GetValue<string>().StartsWith("d:acme.svc/projection:serviceDeps/op:w1"));
    }

    [Fact]
    public void Structural_edits_print_canonically_and_undo_is_an_inverse_patch_not_a_rewind()
    {
        var (h, d) = NewDraft(); using var hostGuard = h;
        var rev0 = h.CatalogRevision;
        var before = h.DraftText(d, "acme.svc");
        var (doit, undo) = SetProp(h, d, Health, "above", Dec("0.5"));
        var r1 = h.ApplyDraftPatch(d, doit, 0);
        Assert.True(r1["ok"]!.GetValue<bool>(), r1["diagnostics"]!.ToJsonString());
        Assert.Contains("above 0.5 ", r1["modules"]!["acme.svc"]!["text"]!.GetValue<string>());
        Assert.True(r1["modules"]!["acme.svc"]!["changed"]!.GetValue<bool>());
        var r2 = h.ApplyDraftPatch(d, undo, 1);                              // undo = the inverse patch, applied forward as revision 2
        Assert.Equal("2", r2["revision"]!.GetValue<string>());
        Assert.Equal(before, h.DraftText(d, "acme.svc"));
        Assert.False(r2["modules"]!["acme.svc"]!["changed"]!.GetValue<bool>());
        var r3 = h.ApplyDraftPatch(d, doit, 2);                              // redo
        Assert.Contains("above 0.5 ", r3["modules"]!["acme.svc"]!["text"]!.GetValue<string>());
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
        var (bad, _) = SetProp(h, d, "d:acme.ops/projection:ingest/op:windowed", "late", new JsonObject { ["kind"] = "text", ["value"] = "bogus" });
        var r = h.ApplyDraftPatch(d, bad, 0);
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Contains(r["diagnostics"]!.AsArray(), x => x!["severity"]!.GetValue<string>() == "error" && x["module"]!.GetValue<string>() == "acme.ops");
    }

    [Fact]
    public void Committing_a_draft_activates_it_and_every_release_is_listed_diffed_and_restorable()
    {
        var (h, d) = NewDraft(); using var hostGuard = h;
        var originalDigest = h.Catalog.Modules["acme.svc"].Digest;
        var originalText = h.Definition("acme.svc");
        var (doit, _) = SetProp(h, d, Health, "above", Dec("0.5"));
        h.ApplyDraftPatch(d, doit, 0);
        var res = h.CommitDraft(d, h.CatalogRevision);
        Assert.Equal("acme.svc", res["modules"]![0]!.GetValue<string>());
        Assert.NotEqual(originalDigest, h.Catalog.Modules["acme.svc"].Digest);
        Assert.Throws<HostError>(() => h.DraftInfo(d));                       // committed drafts are discarded
        var rels = h.Releases("acme.svc").AsArray();
        Assert.Equal(2, rels.Count);
        Assert.Equal(originalDigest, rels[1]!["supersedes"]!.GetValue<string>());
        var diff = h.ReleaseDiff(originalDigest, h.Catalog.Modules["acme.svc"].Digest);
        Assert.Single(diff["edits"]!.AsArray(), e => e!["id"]!["value"]!.GetValue<string>() == Health);
        Assert.Contains("above 0.5 ", h.ReleaseText(h.Catalog.Modules["acme.svc"].Digest));
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
        // never recover: below 0.0 for 30 windows keeps the degraded state
        var text = h.DraftText(d, "acme.svc").Replace("below 0.02 for 3", "below 0.0 for 30");
        var (doit, _) = SetProp(h, d, Health, "exitAfter", new JsonObject { ["kind"] = "integer", ["value"] = "30" });
        h.ApplyDraftPatch(d, doit, 0);
        var (doit2, _) = SetProp(h, d, Health, "below", Dec("0.0"));
        h.ApplyDraftPatch(d, doit2, 1);
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
        _ = text;
    }

    [Fact]
    public void Draft_diagnostics_are_pushed_on_the_event_stream_to_subscribers_who_ask()
    {
        var (h, d) = NewDraft(); using var hostGuard = h;
        var with = h.Subscribe(null, true, null); var without = h.Subscribe(null, false, null);
        while (with.Messages.Reader.TryRead(out _)) { }
        while (without.Messages.Reader.TryRead(out _)) { }
        var (doit, _) = SetProp(h, d, Health, "above", Dec("0.5"));
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
        var s = Col(Op("services"), "s");
        Assert.Equal("Entity", s["kind"]!.GetValue<string>());
        Assert.EndsWith("Service", s["type"]!.GetValue<string>());
        Assert.Equal("data", s["role"]!.GetValue<string>());
        Assert.Equal(["s", "pods"], Op("backed")["columns"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
        Assert.Equal("Integer", Col(Op("backed"), "pods")["kind"]!.GetValue<string>());
        var flows = Col(Op("dependsOn"), "flows");
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
        var services = ops.Single(o => o!["name"]!.GetValue<string>() == "services")!;
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
    [InlineData("a lift that keeps self-loops and looks back 2h", false)]          // no self-loop in this world: the outputs stay as they were
    public void A_complete_preview_equals_the_activation_a_commit_performs(string draft, bool changes)
    {
        var (h, d) = NewDraft(); using var hostGuard = h;
        HostTests.Drive(h);
        const string Lift = "d:acme.svc/projection:serviceDeps/op:dependsOn";
        var edits = draft switch
        {
            "a threshold that never recovers" => new[] { SetProp(h, d, Health, "exitAfter", new JsonObject { ["kind"] = "integer", ["value"] = "30" }).Do },
            _ => [SetProp(h, d, Lift, "selfLoops", new JsonObject { ["kind"] = "text", ["value"] = "keep" }).Do],
        };
        var rev = 0L;
        foreach (var e in edits) Assert.True(h.ApplyDraftPatch(d, e, rev++)["ok"]!.GetValue<bool>());
        if (draft != "a threshold that never recovers")
            Assert.True(h.ApplyDraftPatch(d, SetProp(h, d, Lift, "within", new JsonObject { ["kind"] = "integer", ["value"] = "7200000000000" }).Do, rev++)["ok"]!.GetValue<bool>());
        else
            Assert.True(h.ApplyDraftPatch(d, SetProp(h, d, Health, "below", Dec("0.0")).Do, rev++)["ok"]!.GetValue<bool>());
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
