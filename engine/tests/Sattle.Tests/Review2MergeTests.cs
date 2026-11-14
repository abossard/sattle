using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>Code review 2, defect 22, on the estate of the tests: merged identities and the telemetry windows they move.</summary>
public class Review2MergeTests
{
    const long Min = 60_000_000_000;

    static Engine RunEstate(Action<Engine>? before = null)
    {
        var cat = EstateTests.LoadCatalog(false);
        var space = cat.Spaces.Keys.Single();
        var engine = new Engine(cat, space, new EngineOptions { Tenant = "lab", LateHorizonWindows = 3 });
        before?.Invoke(engine);
        new Replay(engine, cat.Spaces[space], AdapterRegistry.Create("synth"), new ReplayOptions { ChunkRows = 50, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" }).Run(EstateTests.EstateDir);
        return engine;
    }

    /// <summary>Before: in a step of the master that merged identities, the moved windows were not in the commit's telemetry and stayed unread in the store's log (32 changes on this estate).</summary>
    [Fact]
    public void On_the_estate_every_step_that_merges_identities_hands_on_the_windows_it_moved()
    {
        var late = new List<string>();
        long merges = 0, withWindows = 0, steps = 0;
        using var e = RunEstate(en => en.Committed += ev =>
        {
            if (ev.Graph != "master") return;
            var now = en.Identities.Merges;
            if (now > merges)
            {
                steps++;
                var unread = en.Telemetry.ClosedWindows(en.Master.Key, en.Master.TelemetryPosition, out _);
                if (unread.Count > 0 && ev.Telemetry.Count == 0) { withWindows++; late.Add($"seq {ev.Seq}: {unread.Count} window changes unread"); }
            }
            merges = now;
        });
        Assert.True(steps > 0);
        Assert.True(late.Count == 0, $"{withWindows} of {steps} merge steps left window changes unread: " + string.Join("; ", late.Take(5)));
    }
}
