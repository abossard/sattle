using System.Runtime.CompilerServices;
using Sattle.Core;
using Sattle.Kql;
using Sattle.Language;
using Sattle.Runtime;
using Xunit.Abstractions;

namespace PictureC;

/// <summary>Deployment health written as a program graph in picture C and lowered to the engine's operators replays to the state of its KQL twin.</summary>
public class PictureCTests(ITestOutputHelper output)
{
    const long Min = 60_000_000_000;
    const string Program = "acme.deployhealth.deploymentHealth.gkql.json", Twin = "acme.deployhealth.deploymentHealth.kql";
    static string Here([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;
    static string Root => Path.GetFullPath(Path.Combine(Here(), "..", "..", ".."));
    static string Lab => Environment.GetEnvironmentVariable("SATTLE_LAB") is { Length: > 0 } folder ? folder : Path.Combine(Root, "lab");
    static string FixtureDir(string fixture) => fixture == "lab/fixtures" ? Path.Combine(Lab, "fixtures") : Path.Combine(Root, "engine", "tests", "Sattle.Tests", "fixtures", fixture);

    /// <summary>The synthetic estate, the deploy-health fixture at two bounds, and the scrubbed lab fixtures when they are generated locally.</summary>
    public static TheoryData<string, int, bool> Fixtures()
    {
        var d = new TheoryData<string, int, bool> { { "estate", 0, true }, { "deploy-health", 0, true }, { "deploy-health", 1, true } };
        if (File.Exists(Path.Combine(FixtureDir("lab/fixtures"), "kubernetes", "list.ndjson"))) d.Add("lab/fixtures", 0, false);
        return d;
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void The_program_graph_replays_to_the_state_digest_of_its_KQL_twin(string fixture, int minReady, bool talks)
    {
        using var kql = Replay(fixture, minReady, program: false);
        using var graph = Replay(fixture, minReady, program: true);
        var (k, g) = (kql.Graph("deploymentHealth").State, graph.Graph("deploymentHealth").State);
        output.WriteLine($"{fixture} minReady {minReady}: {k.EntityCount} Workload, {k.Rels.Count(r => Ids.Name(r.Type) == "Talks")} Talks");
        output.WriteLine($"deploymentHealth  KQL {k.Digest()}  program {g.Digest()}");
        output.WriteLine($"state digest      KQL {kql.StateDigest()}  program {graph.StateDigest()}");
        Assert.True(k.EntityCount > 0);
        Assert.Equal(talks, k.Rels.Any(r => Ids.Name(r.Type) == "Talks"));
        Assert.Equal(k.Digest(), g.Digest());
        Assert.Equal(kql.StateDigest(), graph.StateDigest());
    }

    /// <summary>Replays a fixture with the settings of `sattle replay`, deployment health read from its KQL text or from the program graph.</summary>
    static Engine Replay(string fixture, int minReady, bool program)
    {
        var dir = FixtureDir(fixture);
        var files = Catalog.ReadDirectory(Path.Combine(Root, "engine", "definitions")).ToList();
        if (fixture == "estate") files = [.. files.Select(f => f.Path.EndsWith("acme.space.sattle", StringComparison.Ordinal) ? f with { Text = File.ReadAllText(Path.Combine(dir, "space", "acme.space.sattle")) } : f)];
        if (Sattle.Cli.Program.ArgumentsOf(dir) is { } arguments) files = Sattle.Cli.Program.WithArguments(files, arguments);
        files = [.. files.Select(f => f.Path.EndsWith("acme.space.sattle", StringComparison.Ordinal) ? f with { Text = f.Text.Replace("dh.deploymentHealth(master: master)", $"dh.deploymentHealth(master: master, minReady: {minReady})") } : f)];
        if (program) files = [.. files.Where(f => !f.Path.EndsWith(Twin, StringComparison.Ordinal)), new SourceFile(Path.Combine(Here(), Program), File.ReadAllText(Path.Combine(Here(), Program)))];
        var cat = new Catalog(program ? new ProgramSource(new KqlSource()) : new KqlSource());
        var res = cat.Load(files, updatePins: true);
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
        var space = cat.Spaces.Keys.Single();
        var cluster = fixture == "lab/fixtures" ? Sattle.Cli.Program.DetectCluster(dir) : "synth";
        var engine = new Engine(cat, space, new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 });
        new Replay(engine, cat.Spaces[space], AdapterRegistry.Create(cluster), new ReplayOptions { ChunkRows = 50, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = cluster }).Run(dir);
        return engine;
    }
}
