using Sattle.Core;

namespace Sattle.Runtime;

/// <summary>
/// The default store (S1, step a): the head revision of each graph, its frontier, and a tail of the last commits capped by their number of edits (`tailEdits`; a commit larger than the cap empties the tail). No second copy of a graph, no element versions, no adjacency history.
/// Reads of the head come from the graph the engine attaches (its own state, already at the head when it commits); without an attached graph the store keeps one head copy itself (the conformance suite, a store used alone).
/// Declared limit: history reaches back over the tail only. `Read`, `ScanAdjacency`, `Seal` and `OpenSnapshot` at a revision before the tail answer `Unavailable` (reads) or throw `GapException`; `Follow` before the tail throws `GapException`, which the port already defines.
/// `ReadResult.Present.From` is the revision at which the element took that state when the change is in the tail, else the start of the tail.
/// </summary>
public sealed class HeadOnlyGraphStore(int tailEdits = 4096) : IGraphStore, IJournalStore
{
    readonly InMemoryJournal journal = new();
    public long JournalCount => journal.JournalCount;
    public void JournalAppend(string body) => journal.JournalAppend(body);
    public IEnumerable<string> JournalRead(long after = 0) => journal.JournalRead(after);
    public void SealPut(long position, string meta, byte[] data) => journal.SealPut(position, meta, data);
    public SealRecord? SealLatest() => journal.SealLatest();

    sealed class G
    {
        public long Head;
        /// <summary>The last commits, oldest first; `Tail[0]` goes from revision `Head - Tail.Count`.</summary>
        public readonly Queue<CommittedPatch> Tail = new();
        public long TailEdits;
        /// <summary>The engine's own graph (attached), or the store's head copy when nothing is attached.</summary>
        public Graph? Attached;
        public Graph? Own;
        public Graph State => Attached ?? (Own ??= new Graph());
        public long TailStart => Head - Tail.Count;
        public void ClearTail() { Tail.Clear(); TailEdits = 0; }
    }
    readonly Dictionary<GraphKey, G> graphs = [];
    readonly object gate = new();
    G Get(GraphKey k) { if (!graphs.TryGetValue(k, out var g)) graphs[k] = g = new G(); return g; }

    /// <summary>The engine hands over the graph it keeps at the head: reads come from it, and the store keeps no copy. The engine applies a patch to it before it commits the patch.</summary>
    public void Attach(GraphKey key, Graph state) { lock (gate) { var g = Get(key); g.Attached = state; g.Own = null; } }

    /// <summary>After a restore from a seal: the graph is at `head`, and nothing before it is in the tail.</summary>
    public void Resume(GraphKey key, long head) { lock (gate) { var g = Get(key); g.Head = head; g.ClearTail(); } }

    public HeadInfo Head(GraphKey g) { lock (gate) { var h = Get(g).Head; return new HeadInfo(h, h); } }

    public CommitResult Commit(CommitBatch b)
    {
        lock (gate)
        {
            var g = Get(b.Graph);
            if (g.Head != b.ExpectedHead) return new CommitResult.Conflict(g.Head);
            var effective = new List<List<Edit>>();
            for (var i = 0; i < b.Patches.Count; i++)
            {
                var p = b.Patches[i];
                var e = p.Edits.Where(x => x.Effective).ToList();
                if (p.From != g.Head + i || p.To != p.From + 1 || e.Count == 0) return new CommitResult.Rejected($"patch {i} has revisions {p.From}->{p.To}, expected {g.Head + i}->{g.Head + i + 1}, or is empty");
                effective.Add(e);
            }
            if (g.Attached is null)
            {
                // the store's own head copy: guards and uniqueness are checked here, all patches or none
                var applied = new List<List<Edit>>();
                foreach (var e in effective)
                {
                    if (g.State.Apply(e) is { } err) { for (var j = applied.Count - 1; j >= 0; j--) g.State.Apply([.. applied[j].Select(x => x.Inverted())]); return new CommitResult.Rejected(err); }
                    applied.Add(e);
                }
            }
            for (var i = 0; i < b.Patches.Count; i++)
            {
                var p = b.Patches[i];
                if (effective[i].Count > tailEdits) { g.ClearTail(); continue; }          // too large to keep: history starts after it
                g.Tail.Enqueue(new CommittedPatch(new Patch(p.Graph, p.From, p.To, effective[i], p.Cause), b.Frontiers[i]));
                g.TailEdits += effective[i].Count;
                while (g.TailEdits > tailEdits) g.TailEdits -= g.Tail.Dequeue().Patch.Edits.Length;
            }
            g.Head += b.Patches.Count;
            return new CommitResult.Committed(g.Head);
        }
    }

    /// <summary>The state of the graph at `revision`: the head with the tail's patches after it undone. Null when the revision is before the tail.</summary>
    Graph? StateAt(G g, long revision)
    {
        if (revision == g.Head) return g.State;
        if (revision < g.TailStart) return null;
        var gr = g.State.Clone();
        foreach (var c in g.Tail.Reverse().TakeWhile(c => c.Patch.From >= revision))
            if (gr.Apply([.. c.Patch.Edits.Select(e => e.Inverted())]) is { } err) throw new InvalidOperationException(err);
        return gr;
    }

    public IReadOnlyList<ReadResult> Read(GraphKey gk, long revision, IReadOnlyList<ElementKey> keys)
    {
        lock (gate)
        {
            var g = Get(gk);
            if (revision > g.Head || revision < g.TailStart) return [.. keys.Select(_ => (ReadResult)new ReadResult.Unavailable())];
            return [.. keys.Select(k =>
            {
                // walk the tail from the head back to the revision: the state the key had there, and the interval of that state
                var state = g.State.Get(k); long? to = null; long from = g.TailStart;
                foreach (var c in g.Tail.Reverse())
                {
                    var edit = c.Patch.Edits.FirstOrDefault(e => e.Key == k);
                    if (edit is null) continue;
                    if (c.Patch.From >= revision) { state = edit.Before; to = c.Patch.To; }
                    else { from = c.Patch.To; break; }
                }
                return state is null ? (ReadResult)new ReadResult.Absent() : new ReadResult.Present(state, from, to);
            })];
        }
    }

    public AdjacencyPage ScanAdjacency(GraphKey gk, long revision, string endpoint, Direction dir, string? type, int pageSize, string? continuation)
    {
        lock (gate)
        {
            var g = Get(gk);
            if (revision > g.Head || revision < g.TailStart) throw new GapException(g.TailStart);
            // the relationships at the endpoint now, then the tail undone back to the revision for those that touch the endpoint
            var rels = (dir == Direction.Out ? g.State.OutEdges(endpoint) : g.State.InEdges(endpoint)).ToDictionary(r => r.Id, r => r, StringComparer.Ordinal);
            foreach (var c in g.Tail.Reverse().TakeWhile(c => c.Patch.From >= revision))
                foreach (var e in c.Patch.Edits.Where(e => e.Key.Kind == 'r'))
                {
                    if (e.After is Rel a && (dir == Direction.Out ? a.Source : a.Target) == endpoint) rels.Remove(a.Id);
                    if (e.Before is Rel b && (dir == Direction.Out ? b.Source : b.Target) == endpoint) rels[b.Id] = b;
                }
            var all = rels.Values.Where(r => type is null || r.Type == type).OrderBy(r => r.Id, StringComparer.Ordinal)
                .Where(r => continuation is null || string.CompareOrdinal(r.Id, continuation) > 0).Take(pageSize + 1).ToList();
            var page = all.Take(pageSize).Select(r => new AdjEntry(r.Id, dir == Direction.Out ? r.Target : r.Source, r.Type, dir)).ToList();
            return new AdjacencyPage(page, all.Count > pageSize ? page[^1].RelId : null);
        }
    }

    public IEnumerable<CommittedPatch> Follow(GraphKey gk, long after)
    {
        lock (gate)
        {
            var g = Get(gk);
            if (after < g.TailStart) throw new GapException(g.TailStart);
            return [.. g.Tail.Where(c => c.Patch.From >= after)];
        }
    }

    public SealInfo Seal(GraphKey gk, long revision)
    {
        lock (gate)
        {
            var g = Get(gk);
            var gr = StateAt(g, revision) ?? throw new GapException(g.TailStart);
            var frontier = revision == 0 ? Frontier.Empty : g.Tail.FirstOrDefault(c => c.Patch.To == revision)?.Frontier ?? Frontier.Empty;
            return new SealInfo(revision, gr.Digest(), gr.Count, frontier);
        }
    }

    public IEnumerable<Element> OpenSnapshot(GraphKey gk, long revision)
    {
        lock (gate)
        {
            var g = Get(gk);
            var gr = StateAt(g, revision) ?? throw new GapException(g.TailStart);
            return [.. gr.Elements().OrderBy(e => e.Key.ToString(), StringComparer.Ordinal)];
        }
    }

    public void Dispose() { }
}
