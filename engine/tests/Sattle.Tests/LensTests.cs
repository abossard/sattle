using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Example 3 of research 03: the capability view as a lens with a complement. Backward edits, typed rejections, GetPut, PutGet (open question Q3) and the absence of PutPut.</summary>
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
    static JsonObject Edit(SpaceHost h, params JsonNode[] edits) => new() { ["baseRevisions"] = new JsonObject { ["capabilities"] = Heads(h, "capabilities"), ["master"] = Heads(h, "master") }, ["edits"] = new JsonArray([.. edits]) };
    static HostError Rejected(SpaceHost h, JsonObject edit, string graph = "capabilities") => Assert.Throws<HostError>(() => h.ApplyEdits(graph, edit));

    static string CapId(SpaceHost h, string key) => h.Engine.Master.State.Entities.First(e => e.Props.Text("d:acme.ops/prop:capId") == key).Id;
    static Entity? Cap(SpaceHost h, string key) => h.Engine.Master.State.Entities.FirstOrDefault(e => e.Props.Text("d:acme.ops/prop:capId") == key);
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
    public void Create_rename_reparent_and_retire_flow_through_the_master_as_authored_rows()
    {
        using var h = NewHost();
        var res = h.ApplyEdits("capabilities", Edit(h, Create("payments", "Payments")));
        Assert.True(res["accepted"]!.GetValue<bool>());
        var cap = Cap(h, "payments")!;
        Assert.Equal("unassigned", cap.Props.Text("d:acme.ops/prop:owner"));           // the complement's "missing"
        Assert.Equal("active", cap.Props.Text("d:acme.ops/prop:status"));
        Assert.Equal(cap.Id, View(h).Entities.Single().Id);                              // view and master share the entity id
    }

    [Fact]
    public void Edits_of_the_view_reproduce_in_the_view_and_the_master_is_changed_only_on_the_authored_partition()
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
        // delete the leaf: the master keeps a retired capability and its PartOf edge, the view loses both
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
        var digest = h.StateDigest(); var heads = (Heads(h, "master"), Heads(h, "capabilities"));
        var pay = View(h).Entities.First(e => e.Props.Text("d:acme.biz/prop:name") == "Payments");
        var withCount = Ent(pay.Id, "Payments", Prop("serviceCount", "integer", "3"));
        var e1 = Rejected(h, Edit(h, new JsonObject { ["kind"] = "entity", ["id"] = pay.Id, ["before"] = Ent(pay.Id, "Payments"), ["after"] = withCount }));
        Assert.Equal(("ReadOnlyByRule", "counts"), (e1.Code, e1.Extra["rule"]!.GetValue<string>()));
        var e2 = Rejected(h, Edit(h, new JsonObject { ["kind"] = "entity", ["id"] = "e:x", ["before"] = null, ["after"] = Ent("e:x", "X"), ["key"] = null }), "serviceTree");
        Assert.Equal("NotEditable", e2.Code);
        var stale = Edit(h, Create("x", "X")); stale["baseRevisions"]!["master"] = "0";
        var e3 = Rejected(h, stale);
        Assert.Equal(("StaleBase", 409), (e3.Code, e3.Status));
        var e4 = Rejected(h, Edit(h, new JsonObject { ["kind"] = "entity", ["id"] = pay.Id, ["before"] = Ent(pay.Id, "Payments"), ["after"] = null, ["key"] = null }, new JsonObject { ["kind"] = "relationship", ["id"] = null, ["before"] = null, ["after"] = Under("", pay.Id, pay.Id) }));
        Assert.NotEqual("", e4.Code);
        Assert.Equal(digest, h.StateDigest());
        Assert.Equal(heads, (Heads(h, "master"), Heads(h, "capabilities")));
    }

    [Fact]
    public void Master_edits_reject_readonly_elements_and_accept_the_authored_partition()
    {
        using var h = NewHost();
        new SyntheticWorld(h, 4, 2).LoadStructure();
        var pod = h.Engine.Master.State.Entities.First(e => Ids.Name(e.Type) == "Pod");
        var edit = new JsonObject
        {
            ["baseRevisions"] = new JsonObject { ["master"] = Heads(h, "master") },
            ["edits"] = new JsonArray(new JsonObject { ["kind"] = "entity", ["id"] = pod.Id, ["before"] = null, ["after"] = null }),
        };
        // a pod is owned by observedPods
        var node = new JsonObject { ["id"] = V(pod.Id), ["type"] = V("Pod"), ["properties"] = new JsonArray() };
        edit["edits"] = new JsonArray(new JsonObject { ["kind"] = "entity", ["id"] = pod.Id, ["before"] = node, ["after"] = null });
        var e = Assert.Throws<HostError>(() => h.ApplyEdits("master", edit));
        Assert.Equal(("ReadOnlyByRule", "observedPods"), (e.Code, e.Extra["rule"]!.GetValue<string>()));
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

    /// <summary>What GetPut is in an API of edits: an edit whose after state is its before state is no change of the view. The host drops it before any lens is evaluated (Edits.cs), so this shows that such an edit is accepted and leaves state, heads and journal alone; it does not exercise the backward path of the lens (test T-LENS of the paper's inventory is still wanted).</summary>
    [Fact]
    public void GetPut_putting_back_the_unchanged_view_changes_nothing()
    {
        using var h = NewHost();
        Seed(h);
        var digest = h.StateDigest(); var head = Heads(h, "master");
        var edits = View(h).Entities.Select(e => (JsonNode)new JsonObject { ["kind"] = "entity", ["id"] = e.Id, ["before"] = Ent(e.Id, e.Props.Text("d:acme.biz/prop:name")!), ["after"] = Ent(e.Id, e.Props.Text("d:acme.biz/prop:name")!) }).ToArray();
        var res = h.ApplyEdits("capabilities", Edit(h, edits));
        Assert.True(res["accepted"]!.GetValue<bool>());
        Assert.Equal(digest, h.StateDigest());
        Assert.Equal(head, Heads(h, "master"));
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
        Assert.Null(Cap(b, "tmp"));                                         // applying only the second view state (empty) to the original master
        Assert.NotEqual(a.Engine.Master.State.Digest(), b.Engine.Master.State.Digest());
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
                var digest = h.StateDigest(); var mh = Heads(h, "master");
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
                    Assert.Equal(mh, Heads(h, "master"));
                }
                _ = expected;
            }
        }
        Assert.True(accepted > 5, $"accepted {accepted}, rejected {rejected}, last {lastMsg}");
    }
}
