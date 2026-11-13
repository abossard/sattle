namespace Sattle.Runtime;

/// <summary>The default store: the head number of each graph, and nothing else. The graphs are the engine's own, and nothing reads history from a store that is not durable. It takes every commit and counts on from the head the commit names, so an engine that took its state from a snapshot goes on at its head. A space that must survive a restart runs on an `IDurableStore`.</summary>
public sealed class HeadOnlyGraphStore : IGraphStore
{
    readonly Dictionary<GraphKey, long> heads = [];
    readonly object gate = new();

    public HeadInfo Head(GraphKey g) { lock (gate) return new HeadInfo(heads.GetValueOrDefault(g), 0); }

    public CommitResult Commit(CommitBatch batch)
    {
        lock (gate)
        {
            var head = batch.ExpectedHead + batch.Patches.Count;
            heads[batch.Graph] = head;
            return new CommitResult.Committed(head);
        }
    }

    public void Dispose() { }
}
