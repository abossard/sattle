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

    /// <summary>Replays the fixture and the authored model with the settings of `sattle replay`; the model with resource states and the roll-up read from KQL text or from program graphs.</summary>
    static Engine Replay(string fixture, int minReady, bool program)
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

    /// <summary>The space with the authored model as two sources, the model with resource states on deployment health, and the roll-up stacked three times.</summary>
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
            }
            """;
    }
}

/// <summary>Authored rows of the health model, envelopes of source `healthmodel`: payload `{ "kind": "entity", "key", "name", "role", "ignoreUnknown", "cluster"?, "namespace"?, "deployment"? }` or `{ "kind": "link", "parent", "child", "impact" }`.</summary>
public sealed class ModelAdapter : ISourceAdapter
{
    public string Id => "x12.healthmodel";
    public IReadOnlyList<RowContract> Contracts { get; } =
    [
        new("ModelEntityRow", [RowDef.F("key", "Text"), RowDef.F("name", "Text"), RowDef.F("role", "Text"), RowDef.F("ignoreUnknown", "Boolean"),
            RowDef.F("cluster", "Text", true), RowDef.F("namespace", "Text", true), RowDef.F("deployment", "Text", true)]),
        new("ModelLinkRow", [RowDef.F("parent", "Text"), RowDef.F("child", "Text"), RowDef.F("impact", "Text")]),
    ];
    public bool Handles(string s) => s == "healthmodel";

    static string? S(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Consume(Envelope e, IRowSink sink)
    {
        var p = e.Payload;
        if (S(p, "kind") == "entity" && S(p, "key") is { } key && S(p, "name") is { } name && S(p, "role") is "root" or "flow" or "component" or "resource"
            && p.TryGetProperty("ignoreUnknown", out var ig) && ig.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            var w = sink.Begin("ModelEntityRow"); w.Begin(1);
            w.Text("key", key); w.Text("name", name); w.Text("role", S(p, "role")); w.Int(w.Col("ignoreUnknown"), ig.ValueKind == JsonValueKind.True ? 1 : 0);
            w.Text("cluster", S(p, "cluster")); w.Text("namespace", S(p, "namespace")); w.Text("deployment", S(p, "deployment"));
        }
        else if (S(p, "kind") == "link" && S(p, "parent") is { } parent && S(p, "child") is { } child && S(p, "impact") is "Standard" or "Limited" or "Suppressed")
        {
            var w = sink.Begin("ModelLinkRow"); w.Begin(1);
            w.Text("parent", parent); w.Text("child", child); w.Text("impact", S(p, "impact"));
        }
        else sink.Rejected("healthmodel", "an entity without key, name, a known role or ignoreUnknown, or a link without parent, child or a known impact");
    }
}
