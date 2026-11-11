using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Evidence retention is a declaration in the projection (`|> retain on time for 24h`): the master keeps an event while the logical frontier is less than its time plus the age, and not longer. The retained state is a function of the frontier, so it does not depend on the chunk size.</summary>
public class RetentionTests
{
    const long Min = 60_000_000_000;
    static readonly string[] EvidenceTypes = ["ActivityEvent", "WarningEvent", "AuditEvent"];

    static Catalog Catalog(string age, bool captures = false)
    {
        var files = Repo.DefinitionFiles(captures).Select(f => f.Path.EndsWith("acme.ops.sattle", StringComparison.Ordinal) ? f with { Text = f.Text.Replace("retain on time for 24h", $"retain on time for {age}") } : f).ToList();
        var cat = new Catalog();
        var res = cat.Load(files, updatePins: true);
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
        return cat;
    }

    public sealed record Result(Engine Engine, long Frontier);

    static Result Run(Catalog cat, string dir, int chunk, string cluster)
    {
        var space = cat.Spaces.Keys.Single();
        var engine = new Engine(cat, space, new EngineOptions { Tenant = "lab", LateHorizonWindows = 3 });
        var adapters = AdapterRegistry.Create(cluster);
        var stats = new Replay(engine, cat.Spaces[space], adapters, new ReplayOptions { ChunkRows = chunk, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = cluster }).Run(dir);
        return new Result(engine, stats.LastCaptureNs / Min * Min + Min + Min);   // the advance of FlushAtEnd
    }

    /// <summary>The evidence entities of the master: id, type name, event time.</summary>
    static List<(string Id, string Type, long Time)> Evidence(Engine e) =>
        [.. e.Master.State.Entities.Where(x => EvidenceTypes.Contains(Ids.Name(x.Type))).Select(x => (x.Id, Ids.Name(x.Type), ((InstantValue)x.Props.Items.First(k => k.Key.EndsWith("/prop:time", StringComparison.Ordinal)).Value).Ns))];

    static readonly Lazy<Result> Unbounded = new(() => Run(Catalog("24h"), Repo.Fixtures, 2000, Repo.ClusterOf(Repo.Fixtures)));

    /// <summary>An age, in milliseconds, that expires the oldest `q` of the evidence of the fixtures and keeps the rest (the evidence is older than the capture clock suggests, so a fixed age would keep nothing).</summary>
    static string AgeFor(double q)
    {
        var times = Evidence(Unbounded.Value.Engine).Select(x => x.Time).Order().ToList();
        var cut = times[(int)(times.Count * q)];
        return ((Unbounded.Value.Frontier - cut) / 1_000_000 + 1) + "ms";
    }

    [Theory]
    [InlineData(0.25)] [InlineData(0.5)] [InlineData(0.9)]
    public void The_master_keeps_exactly_the_evidence_younger_than_the_age_at_the_frontier_on_the_fixtures(double q)
    {
        var age = AgeFor(q);
        var all = Unbounded.Value;
        var kept = Run(Catalog(age), Repo.Fixtures, 2000, Repo.ClusterOf(Repo.Fixtures));
        var ageNs = Lexer.DurationNs(age);
        var universe = Evidence(all.Engine); var retained = Evidence(kept.Engine);
        var expected = universe.Where(x => x.Time + ageNs > kept.Frontier).Select(x => x.Id).Order().ToList();
        Assert.Equal(expected, retained.Select(x => x.Id).Order().ToList());
        Assert.NotEmpty(retained);
        Assert.True(retained.Count < universe.Count, $"the age {age} expired nothing ({universe.Count} evidence elements): the test would say nothing");
        // what is not evidence is unchanged
        Assert.Equal(all.Engine.Master.State.Entities.Count(x => !EvidenceTypes.Contains(Ids.Name(x.Type))), kept.Engine.Master.State.Entities.Count(x => !EvidenceTypes.Contains(Ids.Name(x.Type))));
        // the relationships of an evidence element go with it: every relationship that starts at an evidence element starts at a retained one
        var gone = universe.Select(x => x.Id).Except(retained.Select(x => x.Id)).ToHashSet();
        Assert.DoesNotContain(kept.Engine.Master.State.Rels, r => gone.Contains(r.Source));
    }

    [Theory]
    [InlineData(1)] [InlineData(7)] [InlineData(50)] [InlineData(100000)]
    public void Retained_state_and_digests_do_not_depend_on_the_chunk_size_on_the_fixtures(int chunk)
    {
        var cat = Catalog(AgeFor(0.5));
        var reference = Run(cat, Repo.Fixtures, 2000, Repo.ClusterOf(Repo.Fixtures)); var r = Run(cat, Repo.Fixtures, chunk, Repo.ClusterOf(Repo.Fixtures));
        Assert.Equal(reference.Engine.StateDigest(), r.Engine.StateDigest());
        Assert.Equal(Evidence(reference.Engine).Select(x => x.Id).Order(), Evidence(r.Engine).Select(x => x.Id).Order());
    }

    static IEnumerable<string> CaptureDirs()
    {
        var c = Path.Combine(Repo.Root, "lab", "captures");
        return Directory.Exists(c) ? Directory.EnumerateDirectories(c).Order(StringComparer.Ordinal) : [];
    }

    public static IEnumerable<object[]> CaptureChunks() => CaptureDirs().Any() ? CaptureDirs().SelectMany(d => new[] { 1, 50, 100000 }.Select(c => new object[] { Path.GetFileName(d), c })) : [["", 0]];

    /// <summary>On the real captures: with an age that expires the older half of the evidence, what is left is exactly the evidence younger than the age at the frontier, and the digest is the same at chunk sizes 1, 50 and 100000.</summary>
    [Trait("Category", "Capture")]
    [CaptureTheory]
    [MemberData(nameof(CaptureChunks))]
    public void Retention_is_batch_invariant_and_bounds_the_master_on_every_capture(string capture, int chunk)
    {
        var dir = Path.Combine(Repo.Root, "lab", "captures", capture);
        var cluster = Sattle.Cli.Program.DetectCluster(dir);
        var unbounded = Shared(Catalog("24h", captures: true), dir, 100000, cluster, capture + "/24h");
        var universe = Evidence(unbounded.Engine);
        var cut = universe.Select(x => x.Time).Order().ToList()[universe.Count / 2];
        var age = ((unbounded.Frontier - cut) / 1_000_000 + 1) + "ms";
        var cat = Catalog(age, captures: true);
        var reference = Shared(cat, dir, 2000, cluster, capture + "/" + age); var r = Shared(cat, dir, chunk, cluster, capture + "/" + age);       // a chunk size that is none of the rows, so no row compares a run with itself
        Assert.Equal(reference.Engine.StateDigest(), r.Engine.StateDigest());
        var kept = Evidence(r.Engine);
        Assert.Equal(universe.Where(x => x.Time + Lexer.DurationNs(age) > r.Frontier).Select(x => x.Id).Order(), kept.Select(x => x.Id).Order());
        Assert.True(kept.Count > 0 && kept.Count < universe.Count);
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), Lazy<Result>> Cache = new();
    static Result Shared(Catalog cat, string dir, int chunk, string cluster, string key) => Cache.GetOrAdd((key, chunk), _ => new Lazy<Result>(() => Run(cat, dir, chunk, cluster))).Value;
}
