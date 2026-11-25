using Sattle.Cli;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Serving a recorded capture is a replay: `sattle serve CAPTURE` ends in the state `sattle replay CAPTURE` computes. Neither has a late horizon of the host: a window stays revisable for the lateness its program declares (#169).</summary>
public class ServeReplayTests
{
    static string Estate => EstateTests.EstateDir;

    /// <summary>#169 A1: the command line sets no late horizon. A replay given the former option `--late-horizon` ends in the state of a replay without it.</summary>
    [Theory]
    [InlineData("1")] [InlineData("3")] [InlineData("1000000")]
    public void A_replay_ends_in_one_state_whatever_late_horizon_its_command_line_names(string horizon) =>
        Assert.Equal(Digests(Estate).Replayed, Digests(Estate, "--late-horizon", horizon).Replayed);

    /// <summary>The digest `sattle replay` ends in, and the digest of a host built the way `sattle serve` builds it, for the same capture.</summary>
    static (string Replayed, string Served) Digests(string dir, params string[] more)
    {
        var r = Program.Options.Parse(["replay", dir, .. more]);
        var cat = Repo.CatalogFor(dir, out var res); Assert.Empty(res.Diagnostics);
        var space = cat.Spaces.Keys.Single();
        var engine = new Engine(cat, space, new EngineOptions { Tenant = "lab" });
        new Replay(engine, cat.Spaces[space], AdapterRegistry.Create(cat.Spaces[space], cat), Program.ReplayOptionsOf(r)).Run(dir);
        return (engine.StateDigest(), Served(dir));
    }

    /// <summary>The digest of a host built the way `sattle serve` builds it, after it served `dir`.</summary>
    static string Served(string dir)
    {
        var o = Program.Options.Parse(["serve", dir]);
        using var host = new SpaceHost(Repo.DefinitionFilesFor(dir), Program.HostOptionsFor(o, null));
        new Replay(host, host.Space, host.Adapters, Program.ReplayOptionsOf(o)).Run(dir);
        host.Finish();
        return host.StateDigest();
    }

    [Fact]
    public void Serving_the_estate_gives_the_state_digest_of_replaying_it()
    {
        var (replayed, served) = Digests(Estate);
        Assert.Equal(replayed, served);
        Assert.Equal(replayed, Scenarios.Of(Estate, Scenarios.Full).StateDigest);                   // the shared scenario runs have the settings of `sattle replay`
    }

    /// <summary>Code review 2, defect 5: `serve CAPTURE --write-definitions` is refused when a definition is not the text of its file, here the space replaced by an overlay. A commit writes every definition back, so the overlay would be written into the definitions folder. Temporary folders, invented values.</summary>
    [Fact]
    public void Serve_refuses_to_write_definitions_back_when_one_is_not_the_text_of_its_file()
    {
        string Temp(string name) { var d = Path.Combine(Path.GetTempPath(), $"sattle-{name}-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
        var defs = Temp("defs"); var cap = Temp("cap");
        try
        {
            foreach (var f in Directory.EnumerateFiles(Repo.Definitions, "*.gkql.json")) File.Copy(f, Path.Combine(defs, Path.GetFileName(f)));
            var spaceFile = Path.Combine(defs, "acme.space.module.gkql.json"); var space = File.ReadAllText(spaceFile);
            var overlay = Path.Combine(cap, "overlay.module.gkql.json");
            var other = space.Replace("\"projection\": \"dh.readiness\", \"master\": false, \"inputs\": {\"master\": \"master\"}", "\"projection\": \"dh.readiness\", \"master\": false, \"inputs\": {\"master\": \"master\", \"minReady\": {\"lit\": 1}}");
            Assert.NotEqual(space, other);
            File.WriteAllText(overlay, other);
            string[] args = [cap, "--defs", defs, "--write-definitions", "--overlay", overlay];
            Assert.Equal(["acme.space.module.gkql.json"], Program.NotAsOnDisk(Program.ReadDefinitions(Program.Options.Parse(args))));
            Assert.Empty(Program.NotAsOnDisk(Program.ReadDefinitions(Program.Options.Parse(["--defs", defs]))));
            Assert.Equal(2, Program.Main(["serve", .. args]));                                 // refused before a host exists
            Assert.Equal(space, File.ReadAllText(spaceFile));
        }
        finally { Directory.Delete(defs, true); Directory.Delete(cap, true); }
    }

    /// <summary>A stored run restarts in its state, from a seal or from the journal alone; neither records a late horizon (#169).</summary>
    [Theory]
    [InlineData(50)] [InlineData(0)]
    public void A_stored_run_restarts_in_its_state_from_a_seal_or_the_journal(long sealEvery)
    {
        var store = Path.Combine(Path.GetTempPath(), "sattle-horizon-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            string digest;
            using (var g = new SqliteGraphStore(store))
            using (var h = SpaceHost.OpenDurable(g, Repo.DefinitionFilesFor(Estate), new HostOptions { Projections = Repo.Projections, SealEvery = sealEvery }))
            {
                new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 100, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000}).Run(Estate);
                h.Finish(); digest = h.StateDigest();
            }
            using var g2 = new SqliteGraphStore(store);
            using var again = SpaceHost.OpenDurable(g2, null, new HostOptions { Projections = Repo.Projections, VerifyJournal = true });
            Assert.NotNull(again.Restart);
            Assert.Equal(digest, again.StateDigest());
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(store) + "*")) File.Delete(f); }
    }
}
