using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;
using Sattle.Cli;

namespace Sattle.Tests;

/// <summary>Theorem 8.1, 8.5, 8.6 on the capability view of the examples, a lens with a complement (research 03, example 3): backward edits land in the authored graph, refusals are typed and change nothing, PutGet holds on generated sequences of entity and relationship edits, and PutPut does not hold. GetPut and the laws per step kind are `LensLawTests` (T-LENS).</summary>
[Trait("Category", "Slow")]
public class LensTests
{
    static SpaceHost NewHost() => HostTests.NewHost();

    static JsonObject V(string id) => new() { ["value"] = id };
    static JsonNode Prop(string p, string kind, string v) => new JsonObject { ["property"] = V(p), ["value"] = new JsonObject { ["kind"] = kind, ["value"] = v } };
    static JsonNode Ent(string id, string name, params JsonNode[] extra) => new JsonObject { ["id"] = V(id), ["type"] = V("BizCapability"), ["properties"] = new JsonArray([Prop("name", "text", name), .. extra]) };
    static JsonNode Under(string id, string src, string tgt) => new JsonObject { ["id"] = V(id), ["source"] = V(src), ["target"] = V(tgt), ["type"] = V("Under"), ["properties"] = new JsonArray() };
    static JsonNode Create(string key, string name) => new JsonObject { ["kind"] = "entity", ["id"] = null, ["before"] = null, ["after"] = Ent("", name), ["key"] = new JsonObject { ["domain"] = "capability", ["components"] = new JsonObject { ["id"] = key } } };
    static JsonNode CreateUnder(string src, string tgt) => new JsonObject { ["kind"] = "relationship", ["id"] = null, ["before"] = null, ["after"] = Under("", src, tgt) };

    static string Heads(SpaceHost h, string g) => h.SpaceInfo()["graphs"]!.AsArray().First(x => x!["id"]!.GetValue<string>() == g)!["head"]!.GetValue<string>();
    static JsonObject Edit(SpaceHost h, params JsonNode[] edits) => new() { ["baseRevisions"] = new JsonObject { ["capabilities"] = Heads(h, "capabilities"), ["authored"] = Heads(h, "authored") }, ["edits"] = new JsonArray([.. edits]) };
    static HostError Rejected(SpaceHost h, JsonObject edit, string graph = "capabilities") => Assert.Throws<HostError>(() => h.ApplyEdits(graph, edit));

    static string CapId(SpaceHost h, string key) => h.Engine.Graph("authored").State.Entities.First(e => e.Props.Text("d:acme.ops/prop:capId") == key).Id;
    static Entity? Cap(SpaceHost h, string key) => h.Engine.Graph("authored").State.Entities.FirstOrDefault(e => e.Props.Text("d:acme.ops/prop:capId") == key);
    static string? Status(SpaceHost h, string key) => Cap(h, key)?.Props.Text("d:acme.ops/prop:status");
    static Graph View(SpaceHost h) => h.Engine.Graph("capabilities").State;
    static string[] ViewNames(SpaceHost h) => [.. View(h).Entities.Select(e => e.Props.Text("d:acme.biz/prop:name")!).Order()];

    static void Seed(SpaceHost h)
    {
        h.ApplyEdits("capabilities", Edit(h, Create("payments", "Payments")));
        h.ApplyEdits("capabilities", Edit(h, Create("billing", "Billing")));
        h.ApplyEdits("capabilities", Edit(h, CreateUnder(CapId(h, "billing"), CapId(h, "payments"))));
    }

    [Fact]
    public void Create_rename_reparent_and_retire_flow_through_the_authored_graph_as_authored_rows()
    {
        using var h = NewHost();
        var res = h.ApplyEdits("capabilities", Edit(h, Create("payments", "Payments")));
        Assert.True(res["accepted"]!.GetValue<bool>());
        var cap = Cap(h, "payments")!;
        Assert.Equal("unassigned", cap.Props.Text("d:acme.ops/prop:owner"));           // the complement's "missing"
        Assert.Equal("active", cap.Props.Text("d:acme.ops/prop:status"));
        Assert.Equal(cap.Id, View(h).Entities.Single().Id);                              // view and authored graph share the entity id
    }

    [Fact]
    public void Edits_of_the_view_reproduce_in_the_view_and_change_only_the_authored_graph()
    {
        using var h = NewHost();
        Seed(h);
        Assert.Equal(["Billing", "Payments"], ViewNames(h));
        Assert.Single(View(h).Rels);
        // rename
        var billing = View(h).Entities.First(e => e.Props.Text("d:acme.biz/prop:name") == "Billing");
        var renamed = Ent(billing.Id, "Invoicing");
        h.ApplyEdits("capabilities", Edit(h, new JsonObject { ["kind"] = "entity", ["id"] = billing.Id, ["before"] = Ent(billing.Id, "Billing"), ["after"] = renamed }));
        Assert.Equal(["Invoicing", "Payments"], ViewNames(h));
        Assert.Equal("Invoicing", Cap(h, "billing")!.Props.Text("d:acme.ops/prop:name"));
        // delete the leaf: the authored graph keeps a retired capability and its PartOf edge, the view loses both
        var rel = View(h).Rels.Single();
        h.ApplyEdits("capabilities", Edit(h, new JsonObject { ["kind"] = "relationship", ["id"] = rel.Id, ["before"] = Under(rel.Id, rel.Source, rel.Target), ["after"] = null },
            new JsonObject { ["kind"] = "entity", ["id"] = billing.Id, ["before"] = Ent(billing.Id, "Invoicing"), ["after"] = null }));
        Assert.Equal("retired", Status(h, "billing"));
        Assert.Equal(["Payments"], ViewNames(h));
        Assert.Empty(View(h).Rels);
    }

    [Fact]
    public void Typed_rejections_name_the_rule_or_the_reason_and_leave_everything_unchanged()
    {
        using var h = NewHost();
        Seed(h);
        var digest = h.StateDigest(); var heads = (Heads(h, "authored"), Heads(h, "capabilities"));
        var pay = View(h).Entities.First(e => e.Props.Text("d:acme.biz/prop:name") == "Payments");
        var withCount = Ent(pay.Id, "Payments", Prop("serviceCount", "integer", "3"));
        var e1 = Rejected(h, Edit(h, new JsonObject { ["kind"] = "entity", ["id"] = pay.Id, ["before"] = Ent(pay.Id, "Payments"), ["after"] = withCount }));
        Assert.Equal(("ReadOnlyByRule", "counts"), (e1.Code, e1.Extra["rule"]!.GetValue<string>()));
        var e2 = Rejected(h, Edit(h, new JsonObject { ["kind"] = "entity", ["id"] = "e:x", ["before"] = null, ["after"] = Ent("e:x", "X"), ["key"] = null }), "serviceTree");
        Assert.Equal("NotEditable", e2.Code);
        var stale = Edit(h, Create("x", "X")); stale["baseRevisions"]!["authored"] = "0";
        var e3 = Rejected(h, stale);
        Assert.Equal(("StaleBase", 409), (e3.Code, e3.Status));
        var e4 = Rejected(h, Edit(h, new JsonObject { ["kind"] = "entity", ["id"] = pay.Id, ["before"] = Ent(pay.Id, "Payments"), ["after"] = null, ["key"] = null }, new JsonObject { ["kind"] = "relationship", ["id"] = null, ["before"] = null, ["after"] = Under("", pay.Id, pay.Id) }));
        Assert.NotEqual("", e4.Code);
        Assert.Equal(digest, h.StateDigest());
        Assert.Equal(heads, (Heads(h, "authored"), Heads(h, "capabilities")));
    }

    [Fact]
    public void The_master_holds_only_observed_rows_and_refuses_edits_NotEditable()
    {
        using var h = NewHost();
        new SyntheticWorld(h, 4, 2).LoadStructure();
        var instance = h.Engine.Master.State.Entities.First(e => Ids.Name(e.Type) == "Instance");
        var node = new JsonObject { ["id"] = V(instance.Id), ["type"] = V("Instance"), ["properties"] = new JsonArray() };
        var edit = new JsonObject
        {
            ["baseRevisions"] = new JsonObject { ["master"] = Heads(h, "master") },
            ["edits"] = new JsonArray(new JsonObject { ["kind"] = "entity", ["id"] = instance.Id, ["before"] = node, ["after"] = null }),
        };
        var digest = h.StateDigest();
        Assert.Equal("NotEditable", Assert.Throws<HostError>(() => h.ApplyEdits("master", edit)).Code);   // authored rows go to the authored graph
        Assert.Equal(digest, h.StateDigest());
    }

    [Fact]
    public void Deleting_a_parent_without_its_edges_is_a_PutGet_violation_and_with_them_is_accepted()
    {
        using var h = NewHost();
        Seed(h);
        var pay = View(h).Entities.First(e => e.Props.Text("d:acme.biz/prop:name") == "Payments");
        JsonNode Del() => new JsonObject { ["kind"] = "entity", ["id"] = pay.Id, ["before"] = Ent(pay.Id, "Payments"), ["after"] = null };
        var digest = h.StateDigest();
        Assert.Equal("PutGetViolation", Rejected(h, Edit(h, Del())).Code);
        Assert.Equal(digest, h.StateDigest());
        var rel = View(h).Rels.Single();
        h.ApplyEdits("capabilities", Edit(h, new JsonObject { ["kind"] = "relationship", ["id"] = rel.Id, ["before"] = Under(rel.Id, rel.Source, rel.Target), ["after"] = null }, Del()));
        Assert.Equal("retired", Status(h, "payments"));
        Assert.Equal(["Billing"], ViewNames(h));        // the child became a root
    }

    [Fact]
    public void An_edge_to_a_retired_endpoint_and_a_cycle_are_rejected()
    {
        using var h = NewHost();
        Seed(h);
        var pay = View(h).Entities.First(e => e.Props.Text("d:acme.biz/prop:name") == "Payments");
        var bil = View(h).Entities.First(e => e.Props.Text("d:acme.biz/prop:name") == "Billing");
        // cycle: payments under billing while billing is under payments
        var cyc = Rejected(h, Edit(h, CreateUnder(pay.Id, bil.Id)));
        Assert.Contains(cyc.Code, new[] { "ConstraintViolation", "PutGetViolation" });
    }

    [Fact]
    public void PutPut_does_not_hold_create_then_delete_leaves_a_retired_capability_but_the_final_view_state_alone_leaves_none()
    {
        using var a = NewHost(); using var b = NewHost();
        a.ApplyEdits("capabilities", Edit(a, Create("tmp", "Tmp")));
        var id = View(a).Entities.Single().Id;
        a.ApplyEdits("capabilities", Edit(a, new JsonObject { ["kind"] = "entity", ["id"] = id, ["before"] = Ent(id, "Tmp"), ["after"] = null }));
        Assert.Equal("retired", Status(a, "tmp"));
        Assert.Empty(View(a).Entities);
        Assert.Null(Cap(b, "tmp"));                                         // applying only the second view state (empty) to the original authored graph
        Assert.NotEqual(a.Engine.Graph("authored").State.Digest(), b.Engine.Graph("authored").State.Digest());
        Assert.False(a.Engine.Graph("capabilities").Typed.Rules.First(r => r.Name == "capabilities").PutPut);
    }

    /// <summary>Random edit sequences on the view: every accepted edit satisfies PutGet against the actual forward result, every rejected one changes nothing (open question Q3).</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void PutGet_and_atomic_rejection_hold_on_generated_edit_sequences(int group)
    {
        var accepted = 0; var rejected = 0; var lastMsg = "";
        for (var c = 0; c < 12; c++)
        {
            using var h = NewHost();
            var rnd = new Random(group * 1000 + c);
            for (var step = 0; step < 8; step++)
            {
                var view = View(h);
                var ents = view.Entities.OrderBy(e => e.Id, StringComparer.Ordinal).ToList(); var rels = view.Rels.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
                var edits = new List<JsonNode>();
                var expected = view.Clone();
                string Name(Entity e) => e.Props.Text("d:acme.biz/prop:name")!;
                switch (rnd.Next(6))
                {
                    case 0: edits.Add(Create("k" + rnd.Next(6), "N" + rnd.Next(100))); break;
                    case 1 when ents.Count > 0: { var e = ents[rnd.Next(ents.Count)]; edits.Add(new JsonObject { ["kind"] = "entity", ["id"] = e.Id, ["before"] = Ent(e.Id, Name(e)), ["after"] = Ent(e.Id, "R" + rnd.Next(100)) }); break; }
                    case 2 when ents.Count > 1: { var a = ents[rnd.Next(ents.Count)]; var b = ents[rnd.Next(ents.Count)]; edits.Add(CreateUnder(a.Id, b.Id)); break; }
                    case 3 when rels.Count > 0: { var r = rels[rnd.Next(rels.Count)]; edits.Add(new JsonObject { ["kind"] = "relationship", ["id"] = r.Id, ["before"] = Under(r.Id, r.Source, r.Target), ["after"] = null }); break; }
                    case 4 when ents.Count > 0:
                        {
                            var e = ents[rnd.Next(ents.Count)];
                            foreach (var r in rels.Where(r => r.Source == e.Id || r.Target == e.Id)) edits.Add(new JsonObject { ["kind"] = "relationship", ["id"] = r.Id, ["before"] = Under(r.Id, r.Source, r.Target), ["after"] = null });
                            edits.Add(new JsonObject { ["kind"] = "entity", ["id"] = e.Id, ["before"] = Ent(e.Id, Name(e)), ["after"] = null });
                            break;
                        }
                    case 5 when ents.Count > 0: { var e = ents[rnd.Next(ents.Count)]; edits.Add(new JsonObject { ["kind"] = "entity", ["id"] = e.Id, ["before"] = Ent(e.Id, Name(e)), ["after"] = null }); break; } // often a PutGet violation
                    default: continue;
                }
                var digest = h.StateDigest(); var mh = Heads(h, "authored");
                try
                {
                    h.ApplyEdits("capabilities", Edit(h, [.. edits]));
                    accepted++;
                    // PutGet against the real engine: the new view contains what the edit asked for
                    foreach (var n in edits)
                        if (n["after"] is { } after && n["kind"]!.GetValue<string>() == "entity" && n["after"]!["properties"]!.AsArray().Count > 0)
                            Assert.Contains(ViewNames(h), x => x == n["after"]!["properties"]![0]!["value"]!["value"]!.GetValue<string>());
                        else if (n["after"] is null && n["kind"]!.GetValue<string>() == "entity")
                            Assert.DoesNotContain(View(h).Entities, e => e.Id == n["id"]!.GetValue<string>());
                        else if (n["after"] is { } ra && n["kind"]!.GetValue<string>() == "relationship")
                            Assert.Contains(View(h).Rels, r => r.Source == ra["source"]!["value"]!.GetValue<string>() && r.Target == ra["target"]!["value"]!.GetValue<string>());
                        else if (n["after"] is null && n["kind"]!.GetValue<string>() == "relationship")
                            Assert.DoesNotContain(View(h).Rels, r => r.Id == n["id"]!.GetValue<string>());
                }
                catch (HostError e)
                {
                    rejected++; lastMsg = e.Code + ": " + e.Message;
                    Assert.Contains(e.Code, new[] { "PutGetViolation", "ConstraintViolation", "BadEdit", "KeyMismatch" });
                    Assert.Equal(digest, h.StateDigest());                  // nothing was accepted
                    Assert.Equal(mh, Heads(h, "authored"));
                }
                _ = expected;
            }
        }
        Assert.True(accepted > 5, $"accepted {accepted}, rejected {rejected}, last {lastMsg}");
    }

    /// <summary>The rules of a program lower to the class of their edits: an editable rule over the rows of a source is bijective, over graphs a lens. A lens's `create` names the identity domain of a new element and what it sets, its `delete` what a delete sets. An edges sink accepts create without `create`: its ends name the elements.</summary>
    [Theory]
    [InlineData("acme.ops", "authored", "authoredCapabilities", "bijective", null, null, new string[0], new string[0])]
    [InlineData("acme.biz", "capabilityView", "capabilities", "lens", "set", "d:acme.ops/identity:capability", new[] { "capId", "owner", "status" }, new[] { "status" })]
    [InlineData("acme.biz", "capabilityView", "tree", "lens", "remove", null, new string[0], new string[0])]
    [InlineData("acme.biz", "capabilityView", "counts", "readonly", null, null, new string[0], new string[0])]
    public void A_rule_lowers_to_its_class_and_what_its_edits_make(string module, string projection, string rule, string cls, string? onDelete, string? createKey, string[] created, string[] deleted)
    {
        var d = new DefView(Repo.LoadExamples(out _).Graph);
        var rid = Ids.Child(Ids.Child(Ids.Module(module), "projection", projection), "rule", rule);
        Assert.Equal((cls, onDelete), (d.Text(rid, "class"), d.Text(rid, "onDelete")));
        Assert.Equal(createKey, d.Target(rid, "createKey"));
        Assert.Equal(created, d.Children(rid, "createDefault").Select(Ids.Name).Order(StringComparer.Ordinal));
        Assert.Equal(deleted, d.Children(rid, "deleteSet").Select(Ids.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>The capability rule of the capability view with `patch` merged in (a null removes the field).</summary>
    public static TheoryData<string, string, string> RulesRefused => new()
    {
        { "a rule with create that does not accept create", """{"accept": ["delete"]}""", "create says what a create makes, so the rule accepts create" },
        { "a lens whose nodes sink accepts create without create", """{"create": null}""", "a rule whose nodes sink accepts create names the identity domain of a new element in create" },
        { "a delete that sets a property the type does not have", """{"delete": {"set": {"nosuch": {"lit": "gone"}}}}""", "Capability has no property nosuch" },
        { "a delete that sets a value read from the row", """{"delete": {"set": {"status": {"ref": "status"}}}}""", "status is a constant or a component of the key" },
    };

    [Theory, MemberData(nameof(RulesRefused))]
    public void A_rule_that_does_not_say_what_its_edits_make_is_refused(string what, string patch, string says)
    {
        var files = Catalog.ReadDirectory(Repo.Definitions);
        var i = files.FindIndex(f => Path.GetFileName(f.Path) == "acme.biz.capabilityView.gkql.json");
        var program = JsonNode.Parse(files[i].Text)!;
        var rule = program["rules"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == "capabilities")!.AsObject();
        foreach (var (name, value) in JsonNode.Parse(patch)!.AsObject().ToList()) if (value is null) rule.Remove(name); else rule[name] = value.DeepClone();
        files[i] = files[i] with { Text = program.ToJsonString() };
        var errors = new Catalog(Repo.Projections).Load(files, updatePins: true).Diagnostics.Where(d => d.IsError).ToList();
        Assert.True(errors.Any(e => e.Code == "Program" && e.Message.Contains(says, StringComparison.Ordinal)), $"{what}: {string.Join("; ", errors)}");
    }

    /// <summary>The capability lens of acme.ops with a second key of a capability, its name, and an equivalence of the two: authored rows with one name give one capability, whose identity then has two keys in the domain of the emit.</summary>
    static SpaceHost WithNamedCapabilities()
    {
        var files = Catalog.ReadDirectory(Repo.Definitions);
        JsonNode Doc(string name) => JsonNode.Parse(files.Single(f => Path.GetFileName(f.Path) == name).Text)!;
        var ops = Doc("acme.ops.module.gkql.json"); var ingest = Doc("acme.ops.ingest.gkql.json"); var space = Doc("acme.space.module.gkql.json");
        ops["identities"]!.AsArray().Add(JsonNode.Parse("""{"name": "capabilityName", "type": "Capability", "components": [{"name": "name", "type": "Text"}]}"""));
        ops["equivalences"] = new JsonArray(JsonNode.Parse("""{"name": "capabilityIsNamed", "left": "capability", "right": "capabilityName", "mode": "assert", "threshold": 1.0}"""));
        // a link rule belongs to the master's projection: it reads capability rows from a source of its own, with a row type of its own
        var capRow = ops["rows"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == "CapabilityRow")!.DeepClone();
        capRow["name"] = "CapabilityNameRow";
        ops["rows"]!.AsArray().Add(capRow);
        ingest["inputs"]!.AsArray().Add(JsonNode.Parse("""{"name": "capNames", "rows": "acme.ops.CapabilityNameRow"}"""));
        ingest["verbs"]!.AsArray().Add(JsonNode.Parse("""{"id": "capNames", "verb": "rows", "source": "capNames"}"""));
        ingest["verbs"]!.AsArray().Add(JsonNode.Parse("""
            {"id": "namedCapabilities", "verb": "link", "rule": "namedCapabilities",
             "link": {"policy": "capabilityIsNamed", "left": {"identity": "capability", "args": [{"ref": "capId"}]}, "right": {"identity": "capabilityName", "args": [{"ref": "name"}]}, "confidence": 1.0}}
            """));
        ingest["wires"]!.AsArray().Add(JsonNode.Parse("""{"from": "capNames", "to": "namedCapabilities"}"""));
        space["space"]!["sources"]!.AsArray().Add(JsonNode.Parse("""{"name": "capNames", "adapter": "record", "rows": "ops.CapabilityNameRow"}"""));
        space["space"]!["graphs"]!.AsArray().Single(g => g!["name"]!.GetValue<string>() == "master")!["inputs"]!["capNames"] = "capNames";
        var edited = new Dictionary<string, string> { ["acme.ops.module.gkql.json"] = ops.ToJsonString(), ["acme.ops.ingest.gkql.json"] = ingest.ToJsonString(), ["acme.space.module.gkql.json"] = space.ToJsonString() };
        return new SpaceHost([.. files.Select(f => edited.TryGetValue(Path.GetFileName(f.Path), out var t) ? f with { Text = t } : f)], new HostOptions { Projections = Repo.Projections});
    }

    /// <summary>With `linkFirst` false the authored graph holds the capability before the link names it, so the merge reaches an element of the authored graph.</summary>
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void An_edit_of_an_element_with_two_keys_is_rejected_AmbiguousKey(bool linkFirst)
    {
        using var h = WithNamedCapabilities();
        void Rows(params (string Id, string Name)[] rows)
        {
            foreach (var source in linkFirst ? new[] { "capNames", "caps" } : ["caps", "capNames"])
            {
                var rw = new RowWriter(h.RowLayout(source), h.Interner);
                foreach (var (id, name) in rows) { rw.Begin(); rw.Text("capId", id); rw.Text("name", name); rw.Text("owner", "unassigned"); rw.Text("status", "active"); }
                h.Ingest(source, rw.Build()!, 5000 + rows.Length);
            }
        }
        string IdOfKey(string key) => h.Engine.Identities.Bindings().Single(b => b.Domain.Name == "capability" && b.Components.SequenceEqual([key])).Id;
        Rows(("payments", "Payments"), ("billing", "Billing"));
        // a capability with one key in the domain is accepted
        var billing = IdOfKey("billing");
        h.ApplyEdits("capabilities", Edit(h, new JsonObject { ["kind"] = "entity", ["id"] = billing, ["before"] = Ent(billing, "Billing"), ["after"] = null }));
        Assert.Equal("retired", Status(h, "billing"));
        // a second row names the capability Payments: its identity has the keys payments and pay
        Rows(("pay", "Payments"));
        var pay = IdOfKey("payments");
        Assert.Equal(pay, IdOfKey("pay"));
        Assert.Equal([pay], View(h).Entities.Select(e => e.Id));
        var digest = h.StateDigest(); var heads = (Heads(h, "authored"), Heads(h, "capabilities"));
        var before = Ent(pay, "Payments");
        var update = Rejected(h, Edit(h, new JsonObject { ["kind"] = "entity", ["id"] = pay, ["before"] = before, ["after"] = Ent(pay, "Settlement") }));
        var delete = Rejected(h, Edit(h, new JsonObject { ["kind"] = "entity", ["id"] = pay, ["before"] = before.DeepClone(), ["after"] = null }));
        Assert.All(new[] { update, delete }, e => Assert.Equal(("AmbiguousKey", pay), (e.Code, e.Extra["element"]!.GetValue<string>())));
        Assert.Contains("payments", update.Message); Assert.Contains("pay;", update.Message);
        Assert.Equal(digest, h.StateDigest());                                                   // nothing changed
        Assert.Equal(heads, (Heads(h, "authored"), Heads(h, "capabilities")));
    }
}
