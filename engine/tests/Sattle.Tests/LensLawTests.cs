using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>T-LENS (Theorem 8.2, experiment Q3): one lens per step kind over authored items (a filter with a delete that retires, a one-element match, an emit with a literal, a map that drops fields), random source tables and random view edits. Every accepted edit satisfies PutGet, an unchanged element is GetPut, a rejected edit changes nothing, and PutPut holds where the rule declares it. A rule over a step with a complement that accepts delete cannot declare putput.</summary>
[Trait("Category", "Slow")]
public class LensLawTests
{
    const string Base = """
        module t.base @ 1.0.0;
        entity Item { k: Text; a: Text; d: Text; status: Text; }
        relationship Under (Item) -> (Item);
        identity item for Item version "1" { k: Text by exact; }
        /// the row shape of the authoring adapter
        rows CapabilityRow { capId: Text; name: Text; owner: Text; status: Text; parentId?: Text; }
        projection ingest ingestion output t.base {
          in items: rows CapabilityRow;
          rule authored bijective {
            emit entity Item from items identity item(k: capId) set { k: capId, a: name, d: owner, status: status };
            emit relationship Under from items key under: item(k: capId) -> item(k: parentId);
          }
        }
        """;

    const string Filtered = "let shown = match master (i: base.Item) |> where i.status != \"gone\";";
    const string Retiring = "accept create, delete, update(a);\n    on create key base.item default { k: key.k, d: \"none\", status: \"live\" };\n    on delete set { status: \"gone\" };";

    /// <summary>The lens of each step kind: its statements, the type it emits, and whether it declares putput.</summary>
    static readonly Dictionary<string, (string Rule, string Body, string Policy)> Lenses = new()
    {
        ["filter"] = ("lens", Filtered + "\n    emit entity V from shown identity i set { a: i.a };", Retiring),
        ["match"] = ("lens putput", "let shown = match master (i: base.Item);\n    emit entity V from shown identity i set { a: i.a };", "accept update(a);"),
        ["literal"] = ("lens", Filtered + "\n    emit entity V from shown identity i set { a: i.a, tag: \"seen\" };", Retiring),
        ["map"] = ("lens", "let shown = match master (i: base.Item) |> select { i: i, a: i.a };\n    emit entity V from shown identity i set { a: a };", "accept update(a);"),
    };

    static SourceFile[] Files(string step, string? rule = null) =>
    [
        new("t.base.sattle", Base), new("t.view.sattle", $$"""
            module t.view @ 1.0.0;
            import t.base @ 1.0.0 sha256:… as base;
            entity V { a: Text; tag?: Text; }
            projection v view output t.view {
              in master: graph base;
              rule items {{rule ?? Lenses[step].Rule}} {
                {{Lenses[step].Body}}
                {{Lenses[step].Policy}}
              }
            }
            space s {
              source items adapter "sattle.authoring" rows base.CapabilityRow;
              master graph master = base.ingest(items: items);
              graph view = v(master: master);
            }
            """),
    ];

    static SpaceHost Host(string step, int seed)
    {
        var files = Files(step);
        var loaded = SpaceHost.LoadDefinitions(files);
        var h = new SpaceHost(files, new HostOptions { Cluster = "synth" }, loaded);
        var rnd = new Random(seed);
        var rw = new RowWriter(h.RowLayout("items"), h.Interner);
        for (var i = 0; i < rnd.Next(3, 7); i++) { rw.Begin(); rw.Text("capId", "k" + i); rw.Text("name", "a" + rnd.Next(10)); rw.Text("owner", "d" + rnd.Next(10)); rw.Text("status", rnd.Next(4) == 0 ? "gone" : "live"); }
        h.Ingest("items", rw.Build()!, 1);
        return h;
    }

    static JsonObject V(string id) => new() { ["value"] = id };
    static JsonNode Ent(string id, string a, bool tag) => new JsonObject { ["id"] = V(id), ["type"] = V("V"), ["properties"] = new JsonArray([
        new JsonObject { ["property"] = V("a"), ["value"] = new JsonObject { ["kind"] = "text", ["value"] = a } },
        .. tag ? new JsonNode[] { new JsonObject { ["property"] = V("tag"), ["value"] = new JsonObject { ["kind"] = "text", ["value"] = "seen" } } } : []]) };
    static string Head(SpaceHost h, string g) => h.SpaceInfo()["graphs"]!.AsArray().First(x => x!["id"]!.GetValue<string>() == g)!["head"]!.GetValue<string>();
    static JsonObject Edit(SpaceHost h, JsonNode edit) => new() { ["baseRevisions"] = new JsonObject { ["view"] = Head(h, "view"), ["master"] = Head(h, "master") }, ["edits"] = new JsonArray(edit) };
    static string A(Entity e) => e.Props.Text("d:t.view/prop:a")!;

    [Theory]
    [InlineData("filter", 1)] [InlineData("filter", 2)] [InlineData("filter", 3)]
    [InlineData("match", 1)] [InlineData("match", 2)]
    [InlineData("literal", 1)] [InlineData("literal", 2)]
    [InlineData("map", 1)]
    public void Lens_steps_satisfy_their_laws_on_generated_edits(string step, int seed)
    {
        using var h = Host(step, seed);
        var rnd = new Random(seed * 31);
        var tag = step == "literal";
        var (accepted, rejected) = (0, 0);
        for (var n = 0; n < 16; n++)
        {
            var view = h.Engine.Graph("view").State.Entities.OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
            var digest = h.StateDigest(); var heads = (Head(h, "master"), Head(h, "view"));
            JsonObject edit; Action<SpaceHost> putGet;
            switch (rnd.Next(4))
            {
                case 0 when view.Count > 0:          // GetPut: putting an element back unchanged changes nothing
                    {
                        var e = view[rnd.Next(view.Count)];
                        h.ApplyEdits("view", Edit(h, new JsonObject { ["kind"] = "entity", ["id"] = e.Id, ["before"] = Ent(e.Id, A(e), tag), ["after"] = Ent(e.Id, A(e), tag) }));
                        Assert.Equal(digest, h.StateDigest()); Assert.Equal(heads, (Head(h, "master"), Head(h, "view")));
                        continue;
                    }
                case 1 when view.Count > 0:
                    {
                        var e = view[rnd.Next(view.Count)]; var a = "a" + rnd.Next(100);
                        edit = Edit(h, new JsonObject { ["kind"] = "entity", ["id"] = e.Id, ["before"] = Ent(e.Id, A(e), tag), ["after"] = Ent(e.Id, a, tag) });
                        putGet = x => Assert.Equal(a, A(x.Engine.Graph("view").State.Entity(e.Id)!));
                        break;
                    }
                case 2 when view.Count > 0:
                    {
                        var e = view[rnd.Next(view.Count)];
                        edit = Edit(h, new JsonObject { ["kind"] = "entity", ["id"] = e.Id, ["before"] = Ent(e.Id, A(e), tag), ["after"] = null });
                        putGet = x => Assert.Null(x.Engine.Graph("view").State.Entity(e.Id));
                        break;
                    }
                default:
                    {
                        var key = "n" + rnd.Next(5); var a = "a" + rnd.Next(100);
                        edit = Edit(h, new JsonObject { ["kind"] = "entity", ["id"] = null, ["before"] = null, ["after"] = Ent("", a, tag), ["key"] = new JsonObject { ["domain"] = "item", ["components"] = new JsonObject { ["k"] = key } } });
                        var before = view.Count;
                        putGet = x => Assert.Contains(x.Engine.Graph("view").State.Entities, v => A(v) == a && x.Engine.Master.State.Entity(v.Id)?.Props.Text("d:t.base/prop:k") == key);
                        break;
                    }
            }
            try { h.ApplyEdits("view", edit); accepted++; putGet(h); }
            catch (HostError e)
            {
                rejected++;
                Assert.Equal(digest, h.StateDigest());                  // atomic: nothing changed
                Assert.Equal(heads, (Head(h, "master"), Head(h, "view")));
                if (step == "map") Assert.True(e.Code is "PutGetViolation" or "NotAccepted", $"{e.Code}: {e.Message}");
            }
        }
        // the map renames the field it keeps, which the inverse of the emit does not follow: its updates are refused by the staged check, never applied wrong
        if (step == "map") Assert.Equal(0, accepted); else Assert.True(accepted > 0, $"{step}: no edit accepted, {rejected} rejected");
    }

    /// <summary>PutPut where the rule declares it: two updates one after the other leave the master as the second update alone does.</summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void PutPut_holds_where_the_rule_declares_it(int seed)
    {
        using var twice = Host("match", seed); using var once = Host("match", seed);
        Assert.True(twice.Engine.Graph("view").Typed.Rules.Single().PutPut);
        var e = twice.Engine.Graph("view").State.Entities.OrderBy(x => x.Id, StringComparer.Ordinal).First();
        var (a0, a1, a2) = (A(e), A(e) + "x", A(e) + "y");
        twice.ApplyEdits("view", Edit(twice, new JsonObject { ["kind"] = "entity", ["id"] = e.Id, ["before"] = Ent(e.Id, a0, false), ["after"] = Ent(e.Id, a1, false) }));
        twice.ApplyEdits("view", Edit(twice, new JsonObject { ["kind"] = "entity", ["id"] = e.Id, ["before"] = Ent(e.Id, a1, false), ["after"] = Ent(e.Id, a2, false) }));
        once.ApplyEdits("view", Edit(once, new JsonObject { ["kind"] = "entity", ["id"] = e.Id, ["before"] = Ent(e.Id, a0, false), ["after"] = Ent(e.Id, a2, false) }));
        Assert.Equal(once.Engine.Master.State.Digest(), twice.Engine.Master.State.Digest());
        Assert.Equal(once.Engine.Graph("view").State.Digest(), twice.Engine.Graph("view").State.Digest());
    }

    [Theory]
    [InlineData("filter", true)]          // the view leaves d and status out and accepts delete
    [InlineData("match", false)]          // no delete accepted
    public void A_lens_with_a_complement_that_accepts_delete_cannot_declare_putput(string step, bool refused)
    {
        var res = new Catalog().Load(Files(step, "lens putput"), updatePins: true);
        Assert.Equal(refused, res.Diagnostics.Any(d => d.IsError && d.Code == "PutPut" && d.Message.Contains("d, status", StringComparison.Ordinal)));
        if (!refused) Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
    }
}
