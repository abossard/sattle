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
                if (edit.Before is null) published[change.Graph]++;
                if (edit.After is null) published[change.Graph]--;
            }
            foreach (var graph in engine.Graphs)
            {
                var extent = export.Snapshot(graph)["extent"]!;
                var visible = extent["entities"]!.AsArray().Count + extent["relationships"]!.AsArray().Count;
                Assert.True(visible == published[graph.Name],
                    $"During {change.Graph}'s commit, {graph.Name} exposes {visible} elements, but {published[graph.Name]} have been published.");
            }
        };

        // The first advance creates both report graphs' fixed document elements.
        engine.Advance(0);
        Assert.True(published.Values.Count(count => count > 0) >= 2);
    }
}
