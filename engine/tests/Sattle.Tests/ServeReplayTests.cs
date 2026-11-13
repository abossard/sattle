using Sattle.Cli;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Serving a recorded capture is a replay: `sattle serve CAPTURE` ends in the state `sattle replay CAPTURE` computes. Only a live source needs the short late horizon.</summary>
public class ServeReplayTests
{
    static string Estate => EstateTests.EstateDir;

    [Theory]
    [InlineData(new[] { "replay", "X" }, null)] [InlineData(new[] { "serve", "X" }, null)] [InlineData(new[] { "serve", "X", "--live" }, 3)]
    [InlineData(new[] { "serve", "X", "--late-horizon", "7" }, 7)] [InlineData(new[] { "replay", "X", "--late-horizon", "3" }, 3)]
    public void There_is_no_late_horizon_for_a_recorded_capture_served_or_replayed_and_3_for_a_live_source(string[] args, int? expected) =>
        Assert.Equal(expected, Program.LateHorizonFor(Program.Options.Parse(args)));

    /// <summary>The digest `sattle replay` ends in, and the digest of a host built the way `sattle serve` builds it, for the same capture.</summary>
    static (string Replayed, string Served) Digests(string dir, string cluster)
    {
        var r = Program.Options.Parse(["replay", dir, "--cluster", cluster]);
        var cat = Repo.CatalogFor(dir, out var res); Assert.Empty(res.Diagnostics);
        var space = cat.Spaces.Keys.Single();
        var engine = new Engine(cat, space, new EngineOptions { Tenant = "lab", LateHorizonWindows = Program.LateHorizonFor(r) });
        new Replay(engine, cat.Spaces[space], AdapterRegistry.Create(cluster), Program.ReplayOptionsOf(r, cluster)).Run(dir);
        return (engine.StateDigest(), Served(dir, cluster));
    }

    /// <summary>The digest of a host built the way `sattle serve` builds it, after it served `dir`.</summary>
    static string Served(string dir, string cluster)
    {
        var o = Program.Options.Parse(["serve", dir, "--cluster", cluster]);
        using var host = new SpaceHost(Repo.DefinitionFilesFor(dir), Program.HostOptionsFor(o, cluster, null));
        new Replay(host, host.Space, host.Adapters, Program.ReplayOptionsOf(o, cluster)).Run(dir);
        host.Finish();
        return host.StateDigest();
    }

    [Fact]
    public void Serving_the_estate_gives_the_state_digest_of_replaying_it()
    {
        var (replayed, served) = Digests(Estate, "synth");
        Assert.Equal(replayed, served);
    }

    [Trait("Category", "Fixtures")]
    [FixturesFact]
    public void Serving_lab_fixtures_gives_the_state_digest_of_replaying_them()
    {
        var (replayed, served) = Digests(Repo.Fixtures, Repo.ClusterOf(Repo.Fixtures));
        Assert.Equal(replayed, served);
    }

    [Trait("Category", "Capture")]
    [CaptureFact]
    public void Serving_the_newest_real_capture_gives_the_state_digest_of_replaying_it()
    {
        var dir = Repo.CaptureDirs.LastOrDefault();
        Assert.NotNull(dir);
        var replayed = CaptureRuns.Of(dir, CaptureRuns.Full).StateDigest;                           // the shared run has the settings of `sattle replay`; its chunk size does not change the digest (BatchInvarianceTests)
        Assert.Equal(replayed, CaptureRuns.Gated(() => Served(dir, Repo.ClusterOf(dir))));
    }

    /// <summary>Code review 2, defect 5: `serve CAPTURE --write-definitions` is refused when a definition is not the text of its file, here the space bound with the capture's arguments file or replaced by an overlay. A commit writes every definition back, so the capture's values would be written into the definitions folder. Temporary folders, invented values.</summary>
    [Theory]
    [InlineData("arguments")] [InlineData("overlay")]
    public void Serve_refuses_to_write_definitions_back_when_one_is_not_the_text_of_its_file(string bound)
    {
        string Temp(string name) { var d = Path.Combine(Path.GetTempPath(), $"sattle-{name}-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
        var defs = Temp("defs"); var cap = Temp("cap");
        try
        {
            foreach (var f in Directory.EnumerateFiles(Repo.Definitions, "*.sattle")) File.Copy(f, Path.Combine(defs, Path.GetFileName(f)));
            var spaceFile = Path.Combine(defs, "acme.space.sattle"); var space = File.ReadAllText(spaceFile);
            var overlay = Path.Combine(cap, "overlay.sattle");
            var other = Program.WithArguments(space, new Dictionary<string, string> { ["workloadNamespace"] = "other" });
            Assert.NotEqual(space, other);
            if (bound == "arguments") File.WriteAllText(Path.Combine(cap, Program.SpaceArguments), "{ \"workloadNamespace\": \"other\" }");
            else File.WriteAllText(overlay, other);
            string[] args = bound == "arguments" ? [cap, "--defs", defs, "--write-definitions"] : [cap, "--defs", defs, "--write-definitions", "--overlay", overlay];
            Assert.Equal(["acme.space.sattle"], Program.NotAsOnDisk(Program.ReadDefinitions(Program.Options.Parse(args))));
            Assert.Empty(Program.NotAsOnDisk(Program.ReadDefinitions(Program.Options.Parse(["--defs", defs]))));
            Assert.Equal(2, Program.Main(["serve", .. args]));                                 // refused before a host exists
            Assert.Equal(space, File.ReadAllText(spaceFile));
        }
        finally { Directory.Delete(defs, true); Directory.Delete(cap, true); }
    }

    [Theory]
    [InlineData(1_000_000)] [InlineData(7)] [InlineData(null)]
    public void A_stored_run_restarts_with_the_late_horizon_it_ran_with(int? horizon)
    {
        var store = Path.Combine(Path.GetTempPath(), "sattle-horizon-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            string digest;
            using (var g = new SqliteGraphStore(store))
            using (var h = SpaceHost.OpenDurable(g, Repo.DefinitionFilesFor(Estate), new HostOptions { Cluster = "synth", LateHorizonWindows = horizon, SealEvery = 50 }))
            {
                new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 100, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000, Cluster = "synth" }).Run(Estate);
                h.Finish(); digest = h.StateDigest();
            }
            using var g2 = new SqliteGraphStore(store);
            using var again = SpaceHost.OpenDurable(g2, null, new HostOptions { Cluster = "synth", VerifyJournal = true });
            Assert.True(again.Restored);
            Assert.Equal(horizon, again.LateHorizonWindows);
            Assert.Equal(digest, again.StateDigest());
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(store) + "*")) File.Delete(f); }
    }
}
