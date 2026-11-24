using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>T-LENS (Theorem 8.2, experiment Q3): one lens per step kind over authored items (a filter with a delete that retires, a one-element match, an emit with a literal, a map that drops fields), random source tables and random view edits. Every accepted edit satisfies PutGet, an unchanged element is GetPut, and a rejected edit changes nothing.</summary>
[Trait("Category", "Slow")]
public class LensLawTests
{
    const string Base = """
        {"module": "t.base", "version": "1.0.0", "imports": [],
         "entities": [{"name": "Item", "properties": [{"name": "k", "type": "Text"}, {"name": "a", "type": "Text"}, {"name": "d", "type": "Text"}, {"name": "status", "type": "Text"}]}],
         "relationships": [{"name": "Under", "from": "Item", "to": "Item"}],
         "identities": [{"name": "item", "type": "Item", "components": [{"name": "k", "type": "Text"}]}],
         "rows": [{"name": "CapabilityRow", "fields": [{"name": "capId", "type": "Text"}, {"name": "name", "type": "Text"}, {"name": "owner", "type": "Text"}, {"name": "status", "type": "Text"}, {"name": "parentId", "type": "Text", "optional": true}]}]}
        """;

    /// <summary>The row shape of the authoring adapter, written back one for one.</summary>
    const string Ingest = """
        {"graph": "ingest", "ontology": "t.base", "inputs": [{"name": "items", "rows": "t.base.CapabilityRow"}], "parameters": [],
         "verbs": [
           {"id": "items", "verb": "rows", "source": "items"},
           {"id": "item", "verb": "nodes", "rule": "authored", "target": {"nodes": [
             {"var": "i", "label": "Item", "id": {"identity": "item", "args": [{"ref": "capId"}]}, "props": {"k": {"ref": "capId"}, "a": {"ref": "name"}, "d": {"ref": "owner"}, "status": {"ref": "status"}}}]}},
           {"id": "under", "verb": "edges", "rule": "authored", "target": {
             "nodes": [{"var": "i", "label": "Item", "id": {"identity": "item", "args": [{"ref": "capId"}]}}, {"var": "p", "label": "Item", "id": {"identity": "item", "args": [{"ref": "parentId"}]}}],
             "edges": [{"var": "u", "label": "Under", "from": "i", "to": "p"}]}}],
         "wires": [{"from": "items", "to": "item"}, {"from": "items", "to": "under"}],
         "rules": [{"name": "authored", "accept": ["create", "delete"], "update": ["k", "a", "d", "status"]}]}
        """;

    const string Shown = """{"id": "shown", "verb": "graph-match", "graph": "master", "pattern": {"nodes": [{"var": "i", "label": "Item"}]}}""";
    const string Live = """{"id": "live", "verb": "where", "condition": {"fn": "!=", "args": [{"ref": "i.status"}, {"lit": "gone"}]}}""";
    const string Kept = """{"id": "kept", "verb": "project", "columns": {"i": {"ref": "i"}, "a": {"ref": "i.a"}}}""";
    /// <summary>A create makes a live item with no owner under the key it names, a delete retires the item.</summary>
    const string Retiring = """{"name": "items", "accept": ["create", "delete"], "update": ["a"], "create": {"key": "item", "set": {"k": {"ref": "key.k"}, "d": {"lit": "none"}, "status": {"lit": "live"}}}, "delete": {"set": {"status": {"lit": "gone"}}}}""";
    const string Updating = """{"name": "items", "update": ["a"]}""";

    static string Sink(string props) => """{"id": "items", "verb": "nodes", "rule": "items", "target": {"nodes": [{"var": "v", "label": "V", "id": {"ref": "i"}, "props": """ + props + "}]}}";

    /// <summary>The program `v`: the verbs in a chain, each wired to the next, and its one rule.</summary>
    static string Program(string rule, params string[] verbs)
    {
        var ids = verbs.Select(v => JsonNode.Parse(v)!["id"]!.GetValue<string>()).ToList();
        var wires = ids.Zip(ids.Skip(1), (from, to) => $$"""{"from": "{{from}}", "to": "{{to}}"}""");
        return """{"graph": "v", "ontology": "t.view", "inputs": [{"name": "master", "ontology": "t.base"}], "parameters": [], "verbs": ["""
            + string.Join(", ", verbs) + """], "wires": [""" + string.Join(", ", wires) + """], "rules": [""" + rule + "]}";
    }

    /// <summary>The lens of each step kind.</summary>
    static readonly Dictionary<string, string> Lenses = new()
    {
        ["filter"] = Program(Retiring, Shown, Live, Sink("""{"a": {"ref": "i.a"}}""")),
        ["match"] = Program(Updating, Shown, Sink("""{"a": {"ref": "i.a"}}""")),
        ["literal"] = Program(Retiring, Shown, Live, Sink("""{"a": {"ref": "i.a"}, "tag": {"lit": "seen"}}""")),
        ["map"] = Program(Updating, Shown, Kept, Sink("""{"a": {"ref": "a"}}""")),
    };

    static SourceFile[] Files(string step) =>
    [
        new("t.base.module.gkql.json", Base), new("t.base.ingest.gkql.json", Ingest),
        new("t.view.module.gkql.json", """
            {"module": "t.view", "version": "1.0.0", "imports": [{"module": "t.base", "alias": "base", "version": "1.0.0", "digest": null}],
             "entities": [{"name": "V", "properties": [{"name": "a", "type": "Text"}, {"name": "tag", "type": "Text", "optional": true}]}]}
            """),
        new("t.view.v.gkql.json", Lenses[step]),
        new("t.space.module.gkql.json", """
            {"module": "t.space", "version": "1.0.0",
             "imports": [{"module": "t.base", "alias": "base", "version": "1.0.0", "digest": null}, {"module": "t.view", "alias": "view", "version": "1.0.0", "digest": null}],
             "space": {"name": "s", "sources": [{"name": "items", "adapter": "record", "rows": "base.CapabilityRow"}],
                       "graphs": [{"name": "master", "projection": "base.ingest", "master": true, "inputs": {"items": "items"}},
                                  {"name": "view", "projection": "view.v", "inputs": {"master": "master"}}]}}
            """),
    ];

    static SpaceHost Host(string step, int seed)
    {
        var files = Files(step);
        var h = new SpaceHost(files, new HostOptions { Projections = Repo.Projections});
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
