namespace Sattle.Differential;

/// <summary>T-INC and batch invariance per operator kind (Theorem 6.1, Theorem 6.2, obligation O6.1): on generated logs the engine's incremental result equals the reference evaluator's batch result after every batch, for every way of cutting the log. A failure prints the smallest log found.</summary>
public class DifferentialTests
{
    public static TheoryData<string> WorldNames => [.. Worlds.All.Select(w => w.Name)];

    [Theory, MemberData(nameof(WorldNames)), Trait("Category", "Differential")]
    public void The_engine_equals_the_reference_however_the_log_is_cut(string world) => Agrees(world, seeds: 12, length: 14, reordered: false);

    [Theory, MemberData(nameof(WorldNames)), Trait("Category", "Differential")]
    public void The_engine_equals_the_reference_when_the_rows_of_a_batch_are_reordered(string world) => Agrees(world, seeds: 12, length: 14, reordered: true);

    [Theory, MemberData(nameof(WorldNames)), Trait("Category", "Slow")]
    public void The_engine_equals_the_reference_on_many_logs_however_they_are_cut(string world) => Agrees(world, seeds: 150, length: 20, reordered: false);

    /// <summary>The comparison is not empty: on the default seeds the reference result of every world holds facts of every graph, and the facts that show its operator at work (a window, a state that switched on, a reach of depth two, a merged identity).</summary>
    [Theory, MemberData(nameof(WorldNames)), Trait("Category", "Differential")]
    public void The_generated_logs_exercise_the_world(string world)
    {
        var w = Worlds.All.Single(x => x.Name == world);
        var results = Enumerable.Range(0, 12).Select(seed => Sattle.Reference.ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), w.Log(seed, 14), new Sattle.Reference.Settings(TelemetryRetentionNs: w.RetentionNs))).ToArray();
        foreach (var graph in results[0].Graphs.Keys) Assert.Contains(results, r => r.Graphs[graph].Lines().Length > 0);
        var lines = results.SelectMany(r => r.Graphs.Values.SelectMany(g => g.Lines())).ToArray();
        Assert.All(w.Witnesses, witness => Assert.Contains(lines, l => l.Contains(witness, StringComparison.Ordinal)));
    }

    static void Agrees(string world, int seeds, int length, bool reordered)
    {
        var report = Differential.Check(Worlds.All.Single(w => w.Name == world), seeds, length, reordered);
        Assert.True(report is null, report);
    }
}
