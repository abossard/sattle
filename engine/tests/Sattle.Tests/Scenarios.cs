using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The replays of the scenarios, shared by the whole test assembly. A scenario is a recorded input (a tracked fixture, `lab/fixtures`, a real capture) replayed with the settings of `sattle replay` (the site its records carry, every window revisable, tenant lab, its catalog with the overlay): one run per (scenario, chunk size). The run at chunk 100000 keeps its engine for the tests that read graphs, windows or references; a run at another chunk size keeps only its summary and is disposed. The state digests of the tracked scenarios are pinned in one file.</summary>
public static class Scenarios
{
    /// <summary>The folder of a tracked scenario: `estate`, `estate-wide` or `deploy-health`.</summary>
    public static string Tracked(string name) => Path.Combine(Repo.Root, "engine", "tests", "Sattle.Tests", "fixtures", name);

    static readonly object PinLock = new();
    static string PinFile => Path.Combine(Repo.Root, "engine", "tests", "Sattle.Tests", "fixtures", "pinned-digests.json");

    /// <summary>The state digest of the scenario in `dir` is the pinned one (Theorem 6.3: a replay is a function of its input). A second projection language is one more definitions folder that must reach the same pins.
    /// To pin again after a change of the engine or the definitions that is meant to change the state, run the test with SATTLE_PIN=1: it writes the digest to the file instead of comparing.</summary>
    public static void AssertPinned(string dir, string stateDigest)
    {
        var name = Path.GetFileName(dir);
        var pins = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(PinFile))!.AsObject();
        if (Environment.GetEnvironmentVariable("SATTLE_PIN") == "1")
        {
            lock (PinLock)
            {
                pins = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(PinFile))!.AsObject();
                pins[name] = stateDigest;
                var sorted = new System.Text.Json.Nodes.JsonObject(pins.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
                File.WriteAllText(PinFile, sorted.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
            }
            return;
        }
        Assert.True(pins[name] is not null, $"no pinned state digest for the scenario {name}; SATTLE_PIN=1 writes it");
        Assert.True(pins[name]!.GetValue<string>() == stateDigest, $"the scenario {name} ends in {stateDigest}, pinned is {pins[name]}; if the change is meant, SATTLE_PIN=1 pins again");
    }

    public const int Full = 100000;

    /// <summary>At most four scenario replays run at a time. The machine has 10 cores; a replay uses about one core plus the collector's, and holds 0.7 to 0.9 GB live while it runs. Four leaves the other cores to the rest of the suite and keeps the live peak of the capture replays near 4 GB; without a cap up to ten ran at once and slowed each other.</summary>
    static readonly SemaphoreSlim Gate = new(4);

    /// <summary>Runs `replay` when a slot is free. Every replay of a scenario goes through here.</summary>
    public static T Gated<T>(Func<T> replay)
    {
        Gate.Wait();
        try { return replay(); }
        finally { Gate.Release(); }
    }

    /// <summary>What the tests read from a run at any chunk size: the state digest, per graph its digest, the digest of its integrated output (the sum of the signed deltas of every patch) and whether that equals the final graph, and the identity counters.</summary>
    public sealed record Summary(string StateDigest, SortedDictionary<string, string> GraphDigests, SortedDictionary<string, string> Integrated, SortedDictionary<string, bool> IntegratedIsFinal, long Conflicts, long Merges, int MasterEntities);

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<ReplayTests.Run>> Runs = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), Lazy<Summary>> Summaries = new();

    static ReplayTests.Run Replay(string dir, int chunk) =>
        ReplayTests.Replay(dir, chunk, o: new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 });

    /// <summary>The run of `dir` at chunk 100000, with its engine.</summary>
    public static ReplayTests.Run Run(string dir) => Runs.GetOrAdd(dir, d => new Lazy<ReplayTests.Run>(() => Gated(() => Replay(d, Full)))).Value;

    /// <summary>One more replay of `dir` at chunk 100000, the caller's own: what a test of determinism compares the shared run with.</summary>
    public static ReplayTests.Run Again(string dir) => Gated(() => Replay(dir, Full));

    /// <summary>The summary of the run of `dir` at `chunk`.</summary>
    public static Summary Of(string dir, int chunk) => Summaries.GetOrAdd((dir, chunk), k => new Lazy<Summary>(() =>
    {
        if (k.Item2 == Full) return Summarize(Run(k.Item1));
        return Gated(() =>
        {
            var r = Replay(k.Item1, k.Item2);
            using (r.Engine) return Summarize(r);
        });
    })).Value;

    static Summary Summarize(ReplayTests.Run r)
    {
        var digests = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var integrated = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var isFinal = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var g in r.Engine.Graphs)
        {
            var sum = new Delta();
            foreach (var p in r.Export.PatchesOf(g.Name)) foreach (var (f, x) in p.Delta().Weights) sum.Add(f, x);
            digests[g.Name] = g.State.Digest();
            integrated[g.Name] = Digests.Of(sum.Encode());
            isFinal[g.Name] = sum.Equals(Delta.Chi(g.State));
        }
        var ids = r.Engine.Identities;
        return new Summary(r.Engine.StateDigest(), digests, integrated, isFinal, ids.Conflicts, ids.Merges, r.Engine.Master.State.EntityCount);
    }
}
