using Sattle.Runtime;

namespace Sattle.Tests;

public class CommitVisibilityTests
{
    [Fact]
    public void Snapshots_read_in_a_commit_callback_contain_only_published_elements()
    {
        var catalog = EstateTests.LoadCatalog(demo: false);
        using var engine = new Engine(catalog, catalog.Spaces.Keys.Single());
        var export = new CanvasExport(engine, catalog);
        var published = engine.Graphs.ToDictionary(g => g.Name, _ => 0);
        engine.Committed += change =>
        {
            if (change.Patch is not { } patch) return;
            foreach (var edit in patch.Edits)
            {
                if (edit.Key.Kind is not ('e' or 'r')) continue;
                if (edit.Before is null) published[change.GraphId]++;
                if (edit.After is null) published[change.GraphId]--;
            }
            foreach (var graph in engine.Graphs)
            {
                var extent = export.Snapshot(graph)["extent"]!;
                var visible = extent["entities"]!.AsArray().Count + extent["relationships"]!.AsArray().Count;
                Assert.True(visible == published[graph.Name],
                    $"During {change.GraphId}'s commit, {graph.Name} exposes {visible} elements, but {published[graph.Name]} have been published.");
            }
        };

        // The first advance creates the report graph's fixed document elements.
        engine.Advance(0);
        Assert.True(published["readinessReport"] > 0);
    }

    sealed class RefusingStore(CommitResult result) : IGraphStore
    {
        public HeadInfo Head(GraphKey graph) => new(0, 0);
        public CommitResult Commit(CommitBatch batch) => result;
        public void Dispose() { }
    }

    /// <summary>A commit the store refuses publishes nothing (#189 F10): the graph keeps its head and the state at that head, and the engine says why.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_commit_the_store_refuses_leaves_the_head_state_unchanged(bool conflict)
    {
        var catalog = EstateTests.LoadCatalog(demo: false);
        CommitResult refusal = conflict ? new CommitResult.Conflict(0) : new CommitResult.Rejected("refused by the test");
        using var engine = new Engine(catalog, catalog.Spaces.Keys.Single(), new EngineOptions { StoreFactory = _ => new RefusingStore(refusal) });
        var export = new CanvasExport(engine, catalog);
        var before = engine.Graphs.ToDictionary(g => g.Name, g => (g.Head, export.Snapshot(g).ToJsonString()));
        Assert.Contains("store did not commit", Assert.Throws<InvalidOperationException>(() => engine.Advance(0)).Message);
        Assert.Equal(before, engine.Graphs.ToDictionary(g => g.Name, g => (g.Head, export.Snapshot(g).ToJsonString())));
    }
}
