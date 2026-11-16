using System.Runtime.CompilerServices;
using System.Text.Json;
using Sattle.Core;
using Sattle.Kql;
using Sattle.Language;
using Sattle.Runtime;
using Xunit.Abstractions;

namespace PictureC;

/// <summary>The health model written as program graphs in picture C (the model with resource states, and one level of roll-up stacked three times) replays to the state of its KQL twins.</summary>
public class HealthModelTests(ITestOutputHelper output)
{
    const long Min = 60_000_000_000;
    static readonly string[] Programs = ["acme.healthmodel.modelStates", "acme.healthmodel.rollup"];
    static readonly string[] Levels = ["modelStates", "rollup1", "rollup2", "rollup3"];
    static string Here([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;
    static string Model => Path.Combine(Here(), "healthmodel");
    static string Root => Path.GetFullPath(Path.Combine(Here(), "..", "..", ".."));

    [Theory]
    [InlineData("estate", 0)]
    [InlineData("estate", 2)]
    [InlineData("deploy-health", 0)]
    [InlineData("deploy-health", 1)]
    public void The_health_model_programs_replay_to_the_state_digest_of_their_KQL_twins(string fixture, int minReady)
    {
        using var kql = Replay(fixture, minReady, program: false);
        using var graph = Replay(fixture, minReady, program: true);
        var top = kql.Graph("rollup3").State;
        static string? Prop(Entity e, string name) => e.Props.Items.FirstOrDefault(p => Ids.Name(p.Key) == name).Value is TextValue t ? t.S : null;
        output.WriteLine($"{fixture} minReady {minReady}: " + string.Join(", ", top.Entities.Select(e => $"{Prop(e, "key")} {Prop(e, "state")}").Order(StringComparer.Ordinal)));
        foreach (var g in Levels) output.WriteLine($"{g,-12} KQL {kql.Graph(g).State.Digest()}  program {graph.Graph(g).State.Digest()}");
        output.WriteLine($"state digest KQL {kql.StateDigest()}  program {graph.StateDigest()}");
        Assert.Equal(9, top.EntityCount);
        foreach (var g in Levels) Assert.Equal(kql.Graph(g).State.Digest(), graph.Graph(g).State.Digest());
        Assert.Equal(kql.StateDigest(), graph.StateDigest());
    }

    /// <summary>The service stores impact on the child entity and none on a relationship. In the example storefront and api are each asked for Standard by one parent and
    /// Limited by another; each keeps Standard and gets one proxy for Limited between it and the parent that asks for Limited. The service's rule (impact read from the child entity)
    /// over the service form gives every authored entity the state that the roll-up with impact on the link gives it.</summary>
    [Theory]
    [InlineData("estate", 0)]
    [InlineData("estate", 2)]
    [InlineData("deploy-health", 0)]
    [InlineData("deploy-health", 1)]
    public void Impact_per_link_is_kept_in_the_service_form_by_one_proxy_per_other_impact(string fixture, int minReady)
    {
        using var engine = Replay(fixture, minReady, program: false);
        var (proxies, edges) = ServiceForm(engine);
        Assert.Equal(["api--limited Limited", "storefront--limited Limited"], proxies);
        Assert.Subset(edges, new HashSet<string> { "browse -> storefront", "order -> storefront--limited", "storefront--limited -> storefront", "orders -> api", "storefront -> api--limited", "api--limited -> api" });
        Assert.DoesNotContain("order -> storefront", edges);
        Assert.DoesNotContain("storefront -> api", edges);
        AssertServiceRuleGivesTheRollup(engine, fixture, minReady);
    }

    /// <summary>reporting is asked for Limited by browse and Suppressed by shop, no parent asks for Standard; web is asked for Standard by storefront, Limited by order and Suppressed by shop.
    /// Each keeps Standard and gets one proxy for each impact other than Standard, and the service's rule still gives every authored entity the state of the roll-up.</summary>
    [Theory]
    [InlineData("estate", 0)]
    [InlineData("deploy-health", 1)]
    public void Limited_and_Suppressed_on_one_child_and_all_three_impacts_on_one_child_get_one_proxy_per_impact_other_than_Standard(string fixture, int minReady)
    {
        using var engine = Replay(fixture, minReady, program: false,
            Link(30, "browse", "reporting", "Limited"), Link(31, "order", "web", "Limited"), Link(32, "shop", "web", "Suppressed"));
        var (proxies, edges) = ServiceForm(engine);
        Assert.Equal(["api--limited Limited", "reporting--limited Limited", "reporting--suppressed Suppressed", "storefront--limited Limited", "web--limited Limited", "web--suppressed Suppressed"], proxies);
        Assert.Subset(edges, new HashSet<string> {
            "browse -> reporting--limited", "shop -> reporting--suppressed", "reporting--limited -> reporting", "reporting--suppressed -> reporting",
            "storefront -> web", "order -> web--limited", "shop -> web--suppressed", "web--limited -> web", "web--suppressed -> web" });
        Assert.DoesNotContain(edges, e => e is "browse -> reporting" or "shop -> reporting" or "order -> web" or "shop -> web");
        AssertServiceRuleGivesTheRollup(engine, fixture, minReady);
    }

    /// <summary>The real fields an authored entity may carry reach its entity in the service form.</summary>
    [Fact]
    public void The_real_fields_of_an_authored_entity_reach_the_service_form()
    {
        using var engine = Replay("deploy-health", 0, program: false);
        var by = engine.Graph("service").State.Entities.ToDictionary(e => Text(e, "name")!);
        long Int(string k, string f) => (long)((IntegerValue)Prop(by[k], f)!).N;
        Assert.Equal((99L, 400L, 0L, "Sev3", "Sev1"), (Int("shop", "healthObjective"), Int("shop", "canvasX"), Int("shop", "canvasY"), Text(by["shop"], "degradedAlert"), Text(by["shop"], "unhealthyAlert")));
        Assert.Equal(new BooleanValue(true), Prop(by["database"], "resourceHealth"));
        Assert.Null(Prop(by["shop"], "resourceHealth"));
    }

    /// <summary>An authored key that ends like a proxy name would name the same service entity as a proxy, so its entity and its links are refused.</summary>
    [Theory]
    [InlineData("web--limited")]
    [InlineData("Api--Suppressed")]
    public void An_authored_key_that_ends_like_a_proxy_name_is_refused(string key)
    {
        using var engine = Replay("deploy-health", 0, program: false,
            Row(30, $"entity/{key}", $$"""{"kind":"entity","key":"{{key}}","name":"x","role":"component","ignoreUnknown":true}"""), Link(31, "shop", key, "Standard"));
        Assert.DoesNotContain(engine.Graph("model").State.Entities, e => Text(e, "key") == key);
        Assert.Equal(9, engine.Graph("model").State.Entities.Count());
        Assert.Equal(10, engine.Graph("model").State.Rels.Count());
        Assert.Equal(11, engine.Graph("service").State.Entities.Count());
    }

    /// <summary>The proxies as "name impact" and the relationships as "parent -> child", by service name; every name matches the service's name pattern.</summary>
    static (List<string> Proxies, HashSet<string> Edges) ServiceForm(Engine engine)
    {
        var service = engine.Graph("service").State;
        var ents = service.Entities.ToDictionary(e => e.Id);
        Assert.All(ents.Values, e => Assert.Matches("^[a-zA-Z0-9][a-zA-Z0-9-]{1,258}[a-zA-Z0-9]$", Text(e, "name")));
        foreach (var k in service.Entities.Where(e => Prop(e, "proxy") is not BooleanValue { B: true }).Select(e => Text(e, "name")))
            if (service.Entities.Any(e => Text(e, "name")!.StartsWith(k + "--", StringComparison.Ordinal))) Assert.Equal("Standard", Text(service.Entities.Single(e => Text(e, "name") == k), "impact"));
        return ([.. service.Entities.Where(e => Prop(e, "proxy") is BooleanValue { B: true }).Select(e => $"{Text(e, "name")} {Text(e, "impact")}").Order(StringComparer.Ordinal)],
            service.Rels.Select(r => $"{Text(ents[r.Source], "name")} -> {Text(ents[r.Target], "name")}").ToHashSet());
    }

    /// <summary>The service's rule over the service form: a parent is the worst over its children, each through its own impact; an entity without children keeps its own state
    /// (a resource's from deployment health). It gives every authored entity the state that the roll-up with impact on the link gives it.</summary>
    void AssertServiceRuleGivesTheRollup(Engine engine, string fixture, int minReady)
    {
        var service = engine.Graph("service").State;
        var ents = service.Entities.ToDictionary(e => e.Id);
        var own = engine.Graph("modelStates").State.Entities.ToDictionary(e => Text(e, "key")!, e => Text(e, "state") ?? "Unknown");
        var kids = service.Rels.ToLookup(r => r.Source, r => r.Target);
        string State(string id)
        {
            if (!kids[id].Any()) return own[Text(ents[id], "name")!];
            var ignore = Prop(ents[id], "ignoreUnknown") is BooleanValue { B: true };
            var worst = kids[id].Select(k => Counted(Text(ents[k], "impact")!, State(k), ignore)).Max();
            return worst switch { 3 => "Unhealthy", 2 => "Degraded", 0 => "Healthy", _ => "Unknown" };
        }
        var rolled = engine.Graph("rollup3").State.Entities.ToDictionary(e => Text(e, "key")!, e => Text(e, "state"));
        var got = rolled.Keys.ToDictionary(k => k, k => State(service.Entities.Single(e => Text(e, "name") == k).Id));
        output.WriteLine($"{fixture} minReady {minReady}: " + string.Join(", ", got.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}")));
        Assert.Equal(9, rolled.Count);
        foreach (var (k, v) in rolled) Assert.Equal((k, v), (k, got[k]));
    }

    /// <summary>The roll-up's severity of a child for its parent: Unhealthy 3, Degraded 2, Healthy 0, Unknown 1 or not counted (-1) where the parent ignores Unknown; Limited one level milder.
    /// Suppressed reduces the child's impact to none: the child is not counted, and it does not make the parent healthy.</summary>
    static int Counted(string impact, string state, bool ignoreUnknown)
    {
        var v = state switch { "Unhealthy" => 3, "Degraded" => 2, "Healthy" => 0, _ => ignoreUnknown ? -1 : 1 };
        return impact switch { "Suppressed" => -1, "Limited" => v switch { 3 => 2, 2 => 0, _ => v }, _ => v };
    }

    static string Row(int seq, string key, string payload) =>
        $$"""{"v":1,"source":"healthmodel","capturedAt":"2026-01-01T10:00:00.700Z","seq":{{seq}},"key":"healthmodel/{{key}}","meta":null,"payload":{{payload}}}""";
    static string Link(int seq, string parent, string child, string impact) =>
        Row(seq, $"link/{parent}/{child}", $$"""{"kind":"link","parent":"{{parent}}","child":"{{child}}","impact":"{{impact}}"}""");

    static Value? Prop(Entity e, string name) => e.Props.Items.FirstOrDefault(p => Ids.Name(p.Key) == name).Value;
    static string? Text(Entity e, string name) => Prop(e, name) is TextValue t ? t.S : null;

    /// <summary>Replays the fixture and the authored model, with extra authored rows appended, with the settings of `sattle replay`; the model with resource states and the roll-up read from KQL text or from program graphs.</summary>
    static Engine Replay(string fixture, int minReady, bool program, params string[] extra)
    {
        var src = Path.Combine(Root, "engine", "tests", "Sattle.Tests", "fixtures", fixture);
        var dir = Directory.CreateTempSubdirectory("x12-healthmodel-").FullName;
        try
        {
            foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(Path.Combine(Model, "fixture"), "*", SearchOption.AllDirectories)))
            {
                var to = Path.Combine(dir, Path.GetRelativePath(f.StartsWith(src, StringComparison.Ordinal) ? src : Path.Combine(Model, "fixture"), f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to);
            }
            File.AppendAllLines(Path.Combine(dir, "healthmodel", "model.ndjson"), extra);
            var files = Catalog.ReadDirectory(Path.Combine(Root, "engine", "definitions")).ToList();
            if (fixture == "estate") files = [.. files.Select(f => f.Path.EndsWith("acme.space.sattle", StringComparison.Ordinal) ? f with { Text = File.ReadAllText(Path.Combine(dir, "space", "acme.space.sattle")) } : f)];
            files = [.. files.Select(f => f.Path.EndsWith("acme.space.sattle", StringComparison.Ordinal) ? f with { Text = WithModel(f.Text, minReady) } : f)];
            files.AddRange(Directory.EnumerateFiles(Model).Where(f => !f.EndsWith(ProgramSource.Extension, StringComparison.Ordinal) && !(program && Programs.Any(p => f.EndsWith(p + ".kql", StringComparison.Ordinal))))
                .Select(f => new SourceFile(f, File.ReadAllText(f))));
            if (program) files.AddRange(Programs.Select(p => Path.Combine(Model, p + ProgramSource.Extension)).Select(f => new SourceFile(f, File.ReadAllText(f))));
            var cat = new Catalog(program ? new ProgramSource(new KqlSource()) : new KqlSource());
            var res = cat.Load(files, updatePins: true);
            Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
            var space = cat.Spaces.Keys.Single();
            var engine = new Engine(cat, space, new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 });
            new Replay(engine, cat.Spaces[space], [.. AdapterRegistry.Create("synth"), new ModelAdapter()], new ReplayOptions { ChunkRows = 50, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" }).Run(dir);
            return engine;
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>The space with the authored model as two sources, the model with resource states on deployment health, the roll-up stacked three times, and the model in the form the service stores it.</summary>
    static string WithModel(string space, int minReady)
    {
        var text = space.Replace("dh.deploymentHealth(master: master)", $"dh.deploymentHealth(master: master, minReady: {minReady})");
        var import = text.IndexOf("\nspace ", StringComparison.Ordinal);
        text = text[..import] + "\nimport acme.healthmodel @ 1.0.0 sha256:" + new string('0', 64) + " as hm;\n" + text[import..];
        var end = text.LastIndexOf('}');
        return text[..end] + """
              source modelEntities adapter "x12.healthmodel" rows hm.ModelEntityRow;
              source modelLinks adapter "x12.healthmodel" rows hm.ModelLinkRow;
              graph model = hm.model(entities: modelEntities, links: modelLinks);
              graph modelStates = hm.modelStates(model: model, health: deploymentHealth);
              graph rollup1 = hm.rollup(model: modelStates);
              graph rollup2 = hm.rollup(model: rollup1);
              graph rollup3 = hm.rollup(model: rollup2);
              graph service = hm.service(model: model);
            }
            """;
    }
}

/// <summary>Authored rows of the health model, envelopes of source `healthmodel`: payload `{ "kind": "entity", "key", "name", "role", "ignoreUnknown", "cluster"?, "namespace"?, "deployment"?, "healthObjective"?, "canvasX"?, "canvasY"?, "degradedAlert"?, "unhealthyAlert"?, "resourceHealth"? }` or `{ "kind": "link", "parent", "child", "impact" }`.</summary>
public sealed class ModelAdapter : ISourceAdapter
{
    public string Id => "x12.healthmodel";
    public IReadOnlyList<RowContract> Contracts { get; } =
    [
        new("ModelEntityRow", [RowDef.F("key", "Text"), RowDef.F("name", "Text"), RowDef.F("role", "Text"), RowDef.F("ignoreUnknown", "Boolean"),
            RowDef.F("cluster", "Text", true), RowDef.F("namespace", "Text", true), RowDef.F("deployment", "Text", true),
            RowDef.F("healthObjective", "Integer", true), RowDef.F("canvasX", "Integer", true), RowDef.F("canvasY", "Integer", true),
            RowDef.F("degradedAlert", "Text", true), RowDef.F("unhealthyAlert", "Text", true), RowDef.F("resourceHealth", "Boolean", true)]),
        new("ModelLinkRow", [RowDef.F("parent", "Text"), RowDef.F("child", "Text"), RowDef.F("impact", "Text")]),
    ];
    public bool Handles(string s) => s == "healthmodel";

    static bool ProxyLike(string? key) => key is not null && (key.EndsWith("--limited", StringComparison.OrdinalIgnoreCase) || key.EndsWith("--suppressed", StringComparison.OrdinalIgnoreCase));
    static string? S(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Consume(Envelope e, IRowSink sink)
    {
        var p = e.Payload;
        if (ProxyLike(S(p, "key")) || ProxyLike(S(p, "parent")) || ProxyLike(S(p, "child")))
            sink.Rejected("healthmodel", "a key that ends like a proxy name of the service form (--limited or --suppressed)");
        else if (S(p, "kind") == "entity" && S(p, "key") is { } key && S(p, "name") is { } name && S(p, "role") is "root" or "flow" or "component" or "resource"
            && p.TryGetProperty("ignoreUnknown", out var ig) && ig.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            var w = sink.Begin("ModelEntityRow"); w.Begin(1);
            w.Text("key", key); w.Text("name", name); w.Text("role", S(p, "role")); w.Int(w.Col("ignoreUnknown"), ig.ValueKind == JsonValueKind.True ? 1 : 0);
            w.Text("cluster", S(p, "cluster")); w.Text("namespace", S(p, "namespace")); w.Text("deployment", S(p, "deployment"));
            foreach (var f in new[] { "healthObjective", "canvasX", "canvasY" }) if (p.TryGetProperty(f, out var n) && n.TryGetInt64(out var v)) w.Int(w.Col(f), v);
            w.Text("degradedAlert", S(p, "degradedAlert")); w.Text("unhealthyAlert", S(p, "unhealthyAlert"));
            if (p.TryGetProperty("resourceHealth", out var rh) && rh.ValueKind is JsonValueKind.True or JsonValueKind.False) w.Int(w.Col("resourceHealth"), rh.ValueKind == JsonValueKind.True ? 1 : 0);
        }
        else if (S(p, "kind") == "link" && S(p, "parent") is { } parent && S(p, "child") is { } child && S(p, "impact") is "Standard" or "Limited" or "Suppressed")
        {
            var w = sink.Begin("ModelLinkRow"); w.Begin(1);
            w.Text("parent", parent); w.Text("child", child); w.Text("impact", S(p, "impact"));
        }
        else sink.Rejected("healthmodel", "an entity without key, name, a known role or ignoreUnknown, or a link without parent, child or a known impact");
    }
}
