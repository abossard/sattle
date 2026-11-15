using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Kql;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>T-LENS (Theorem 8.2, experiment Q3): one lens per step kind over authored items (a filter with a delete that retires, a one-element match, an emit with a literal, a map that drops fields), random source tables and random view edits. Every accepted edit satisfies PutGet, an unchanged element is GetPut, and a rejected edit changes nothing.</summary>
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
        """;

    const string Ingest = """
        graph ingest : t.base;
        input items : CapabilityRow;
        nodes Item on i with (rule = "authored", accept = "create, delete", update = "k, a, d, status") = items | project i = identity("item", capId), k = capId, a = name, d = owner, status;
        edges Under from i to p with (rule = "authored") = items | project i = identity("item", capId), p = identity("item", parentId);
        """;

    const string Filtered = "let shown = graph(\"master\") | graph-match (i) where labels(i) has \"Item\" and i.status != \"gone\" project i;";
    const string Retiring = "let newItem = (k:string) { print k = k, d = \"none\", status = \"live\" };\nlet goneItem = () { print status = \"gone\" };";
    const string RetiringRule = "rule = \"items\", accept = \"create, delete\", update = \"a\", onCreate = \"newItem\", onDelete = \"goneItem\"";

    /// <summary>The lens of each step kind: its lets and its step.</summary>
    static readonly Dictionary<string, string> Lenses = new()
    {
        ["filter"] = $"{Filtered}\n{Retiring}\nnodes V on i with ({RetiringRule}) = shown | project i = i.id, a = i.a;",
        ["match"] = "let shown = graph(\"master\") | graph-match (i) where labels(i) has \"Item\" project i;\nnodes V on i with (rule = \"items\", update = \"a\") = shown | project i = i.id, a = i.a;",
        ["literal"] = $"{Filtered}\n{Retiring}\nnodes V on i with ({RetiringRule}) = shown | project i = i.id, a = i.a, tag = \"seen\";",
        ["map"] = "let shown = graph(\"master\") | graph-match (i) where labels(i) has \"Item\" project i = i.id, a = i.a;\nnodes V on i with (rule = \"items\", update = \"a\") = shown | project i, a;",
    };

    static SourceFile[] Files(string step) =>
    [
        new("t.base.sattle", Base), new("t.base.ingest.kql", Ingest),
        new("t.view.sattle", """
            module t.view @ 1.0.0;
            import t.base @ 1.0.0 sha256:… as base;
            entity V { a: Text; tag?: Text; }
            """),
        new("t.view.v.kql", "graph v : t.view;\ninput master : t.base;\n" + Lenses[step] + "\n"),
        new("t.space.sattle", """
            module t.space @ 1.0.0;
            import t.base @ 1.0.0 sha256:… as base;
            import t.view @ 1.0.0 sha256:… as view;
            space s {
              source items adapter "sattle.authoring" rows base.CapabilityRow;
              master graph master = base.ingest(items: items);
              graph view = view.v(master: master);
            }
            """),
    ];

    static SpaceHost Host(string step, int seed)
    {
        var files = Files(step);
        var h = new SpaceHost(files, new HostOptions { Projections = new KqlSource(), Cluster = "synth" });
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
}
