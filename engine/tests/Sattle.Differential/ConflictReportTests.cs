using Sattle.Reference;
using Sattle.Runtime;
namespace Sattle.Differential;

/// <summary>Def. 6.4, conflicts reported with the commit: a commit reports the conflicts that appeared, changed or ended with it. Folding the reports of every commit in order gives, after each commit, exactly the conflicts the graph holds, and at the end the conflicts of the reference evaluator.</summary>
public class ConflictReportTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void The_conflicts_a_commit_reports_add_up_to_the_conflicts_the_graph_holds(int seed)
    {
        var w = Worlds.All.Single(x => x.Name == "conflicting fragments");
        var log = w.Log(seed, 24);
        using var engine = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single());
        // a property or an element conflict is about `<kind> <element>[ <property>]`; an ended one names that subject
        var folded = new Dictionary<string, string>(StringComparer.Ordinal);
        engine.Committed += e =>
        {
            if (e.Graph != "master") return;
            foreach (var c in e.Conflicts)
            {
                if (c.Kind == "resolved") { Assert.True(folded.Remove(c.Element), $"seed {seed}: {c.Element} ended without having been reported"); continue; }
                folded[c.Property is { } p ? $"{c.Kind} {c.Element} {p}" : $"{c.Kind} {c.Element}"] = c.Text;
            }
        };
        long seq = 0;
        foreach (var row in log.Cast<RowInput>())
        {
            EngineRun.Ingest(engine, [row], ++seq);
            Assert.Equal(engine.Master.Out.Conflicts.Select(c => c.Text).Order(StringComparer.Ordinal), folded.Values.Order(StringComparer.Ordinal));
        }
        var reference = ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log).Graphs["master"].Conflicts;
        Assert.Equal(reference.Order(StringComparer.Ordinal), folded.Values.Order(StringComparer.Ordinal));
    }

    /// <summary>The generated logs reach both ends: some commit reports a conflict that appears, and some commit one that ends.</summary>
    [Fact]
    public void The_default_logs_report_conflicts_that_appear_and_that_end()
    {
        var w = Worlds.All.Single(x => x.Name == "conflicting fragments");
        var (appeared, ended) = (0, 0);
        foreach (var seed in Enumerable.Range(0, 6))
        {
            using var engine = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single());
            engine.Committed += e => { foreach (var c in e.Conflicts) if (c.Kind == "resolved") ended++; else appeared++; };
            long seq = 0;
            foreach (var row in w.Log(seed, 24).Cast<RowInput>()) EngineRun.Ingest(engine, [row], ++seq);
        }
        Assert.True(appeared > 0 && ended > 0, $"appeared {appeared}, ended {ended}");
    }
}
