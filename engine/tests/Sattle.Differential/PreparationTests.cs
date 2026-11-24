using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Differential;

/// <summary>A populated window creates items, their self-edges and their telemetry in one master patch.
/// Two views read that same patch and publish in order. A late sample revises it, then signed rows withdraw it.</summary>
public class PreparationTests
{
    const int Keys = 8193; // uneven partitions, with enough distinct texts to reuse interner cache slots
    const long Minute = 60_000_000_000;
    static readonly Lazy<Catalog> Catalog = new(() => EngineRun.Load([
        new SourceFile("w.base.sattle", """
            module w.base @ 1.0.0;
            entity Item { k: Text; n: Integer; reading?: Decimal; hits?: Integer telemetry delta sum every 60s; }
            relationship Edge (Item) -> (Item);
            identity item for Item version "1" { k: Text by exact; }
            rows R { k: Text; n: Integer; t: Instant; }
            """),
        Worlds.Kql("w.base", "ingest", """
            graph ingest : w.base;
            input samples : R;
            let closed = samples | summarize n = sum(n) by k, windowStart = bin(t, 60s);
            nodes Item on i with (rule = "observed") = closed | project i = identity("item", k), k, n, windowStart, hits = n;
            extend nodes Item on i with (rule = "observed") = closed | where n > 4 | project i = identity("item", k), reading = n / 2.0;
            edges Edge from i to j with (rule = "observed") = closed | project i = identity("item", k), j = identity("item", k);
            """),
        Worlds.Kql("w.base", "copy", """
            graph copy : w.base;
            input source : w.base;
            let items = graph("source") | graph-match (i) where labels(i) has "Item" project i = i.id, k = i.k, n = i.n, reading = i.reading;
            nodes Item on i = items | project i, k, n, reading;
            extend nodes Item on i = items | join kind=inner (telemetry("source", 3m) | where property == "hits" | project i = id, windowStart, value) on i
                | summarize hits = sum(value) by i, windowStart | project i, windowStart, hits;
            edges Edge from s to d = graph("source") | graph-match (s)-[e]->(d) where labels(e) has "Edge" project s = s.id, d = d.id;
            """),
        Worlds.Space("import w.base @ 1.0.0 sha256:… as base;", """
            source samples adapter "record" rows base.R;
            master graph master = base.ingest(samples: samples);
            graph first = base.copy(source: master);
            graph second = base.copy(source: master);
            """)
    ]));

    static Engine Open(int workers) => new(Catalog.Value, Catalog.Value.Spaces.Keys.Single(),
        new EngineOptions { PreparationWorkers = workers });

    static void Samples(Engine e, int count, long weight, long n, long seq, bool reverse)
    {
        using var w = new RowWriter(e.RowLayout("samples"), e.Interner);
        for (var row = 0; row < count; row++)
        {
            var key = reverse ? count - row - 1 : row;
            w.Begin(weight);
            w.Text("k", $"clé/{key}/日");
            w.Int(w.Col("n"), n + key % 3);
            w.Instant("t", 1);
        }
        e.Ingest("samples", w.Build()!, seq);
    }

    // Compare what a subscriber sees before it returns, including the other view's graph and windows.
    sealed record Observed(string Graph, long Seq, long Lambda, Patch? Patch, string? Inputs, string? Configuration,
        string Windows, string Conflicts, string HeadsAndPositions, string VisibleState);

    static List<Observed> Watch(Engine e)
    {
        var events = new List<Observed>();
        var writer = Environment.CurrentManagedThreadId;
        e.Committed += c =>
        {
            Assert.Equal(writer, Environment.CurrentManagedThreadId);
            var windows = string.Join("\n", c.Telemetry.Select(w => $"{e.Interner.Get(w.Key.Element)}|{e.Interner.Get(w.Key.Prop)}|{w.Key.WindowStart}|{w.Old}|{w.New}"));
            events.Add(new(c.Graph, c.Seq, c.Lambda, c.Patch, c.Patch is { } patch ? string.Join(",", patch.Cause.Inputs) : null, c.Patch?.Cause.Configuration,
                windows, string.Join("\n", c.Conflicts.Select(x => x.Text)), string.Join(",", e.Graphs.Select(g => $"{g.Name}/{g.Head}/{g.TelemetryPosition}")), e.StateDigest()));
        };
        return events;
    }

    static Engine Restore(Engine original, int workers)
    {
        var saved = StateSnapshot.Save(original.StateRootOf());
        var restored = Open(workers);
        var root = restored.StateRootOf();
        StateSnapshot.Load(saved, root);
        restored.AdoptState(root);
        return restored;
    }

    [Theory]
    [InlineData(2, false, false)]
    [InlineData(4, true, true)]
    public void Ordered_patches_and_visible_windows_agree_through_close_revision_and_retraction(int workers, bool reverse, bool restore)
    {
        using var serial = Open(1);
        using var prepared = Open(workers);
        var before = Watch(serial);
        var after = Watch(prepared);
        foreach (var e in new[] { serial, prepared })
        {
            Samples(e, Keys, 1, 3, 10, reverse);
            Assert.Empty(((InMemoryTelemetryStore)e.Telemetry).Dump(e.Master.Key));
            e.Advance(Minute, 11);
            Assert.All(e.Graphs, g => Assert.Equal(Keys, ((InMemoryTelemetryStore)e.Telemetry).WindowCount(g.Key)));
        }
        Assert.Equal(new[] { "master", "first", "second" }, before.Select(x => x.Graph));
        Assert.Equal(Keys * 2, before[0].Patch!.Edits.Length);
        Assert.Equal(before, after);
        Assert.Equal(0, serial.WorkerPreparedRanges);
        Assert.Equal(Environment.ProcessorCount > 1, prepared.WorkerPreparedRanges > 0);

        using var restarted = restore ? Restore(prepared, workers) : null;
        var continuing = restarted ?? prepared;
        var tail = restarted is null ? after : Watch(continuing);
        before.Clear(); tail.Clear();
        var rangesAtClose = continuing.PreparedRanges;
        foreach (var e in new[] { serial, continuing })
        {
            Samples(e, Keys, 1, 5, 12, reverse);   // late data revise all closed windows and the items that carry their sum
            Samples(e, Keys, -1, 5, 13, reverse);
            Samples(e, Keys, -1, 3, 14, reverse);  // every item and edge disappears
        }
        Assert.Contains(before, c => c.Patch?.Edits.Any(x => x.Before != null && x.After != null) == true);
        Assert.Contains(before, c => c.Patch?.Edits.Any(x => x.Before != null && x.After == null) == true);
        Assert.Equal(before, tail);
        Assert.Equal(Environment.ProcessorCount > 1, continuing.PreparedRanges > rangesAtClose);
        Assert.Equal(serial.Interner.Slots, continuing.Interner.Slots);
        static IEnumerable<string> Bindings(Engine e) => e.Identities.Bindings()
            .Select(b => $"{b.Domain.Id}/{b.Id}/{string.Join("/", b.Components)}").Order(StringComparer.Ordinal);
        Assert.Equal(Bindings(serial), Bindings(continuing));
        Assert.All(continuing.Graphs, g =>
        {
            Assert.Equal(0, g.State.EntityCount + g.State.RelCount);
            Assert.Equal(0, ((InMemoryTelemetryStore)continuing.Telemetry).WindowCount(g.Key));
        });
    }

    [Fact]
    public void A_failing_commit_subscriber_sees_the_same_published_prefix()
    {
        static (List<Observed>, string) UntilFailure(int workers)
        {
            using var e = Open(workers);
            var seen = Watch(e);
            var failed = new InvalidOperationException("subscriber failed");
            e.Committed += c => { if (c.Graph == "first") throw failed; };
            Samples(e, Keys, 1, 3, 20, false);
            Assert.Same(failed, Assert.Throws<InvalidOperationException>(() => e.Advance(Minute, 21)));
            Assert.Equal(new[] { "master", "first" }, seen.Select(c => c.Graph));
            var unpublished = e.Graph("second");
            Assert.Equal(0, unpublished.Head);
            Assert.Equal(0, unpublished.State.EntityCount);
            Assert.Equal(0, ((InMemoryTelemetryStore)e.Telemetry).WindowCount(unpublished.Key));
            return (seen, e.StateDigest());
        }
        var serial = UntilFailure(1);
        var prepared = UntilFailure(4);
        Assert.Equal(serial.Item1, prepared.Item1);
        Assert.Equal(serial.Item2, prepared.Item2);
    }

    [Theory]
    [InlineData(511, false)] // 1,022 edits
    [InlineData(512, true)]  // 1,024 edits
    public void First_close_at_the_parallel_cutoff_matches_the_serial_close(int keys, bool eligible)
    {
        using var serial = Open(1);
        using var parallel = Open(4);
        var expected = Watch(serial);
        var actual = Watch(parallel);
        var snapshots = new List<string>();
        foreach (var e in new[] { serial, parallel })
        {
            var canvas = new CanvasExport(e, Catalog.Value);
            e.Committed += change =>
            {
                if (change.Graph != "first") return;
                var pending = canvas.Snapshot(e.Graph("second"));
                Assert.Empty(pending["extent"]!["entities"]!.AsArray());
                Assert.Empty(pending["extent"]!["relationships"]!.AsArray());
                snapshots.Add(canvas.Snapshot(e.Graph("first")).ToJsonString());
            };
            Samples(e, keys, 1, 3, 1, false);
            e.Advance(Minute, 2);
        }
        Assert.Equal(expected, actual);
        Assert.Equal(2, snapshots.Count);
        Assert.Equal(snapshots[0], snapshots[1]);
        Assert.Equal(keys * 2, expected[0].Patch!.Edits.Length);
        Assert.Equal(eligible && Environment.ProcessorCount > 1, parallel.PreparedRanges > 0);
    }

    // The center of a star and every ordered pair of its outgoing edges, weighted by the center's n.
    static readonly Lazy<Catalog> Fanout = new(() => EngineRun.Load([
        new SourceFile("w.base.sattle", Worlds.BaseModule),
        Worlds.Kql("w.base", "ingest", """
            graph ingest : w.base;
            input nodes : R;
            input links : EdgeRow;
            nodes Item on i with (rule = "observed") = nodes | project i = identity("item", k), k, g, n;
            edges Edge from s to d with (rule = "observed") = links | project s = identity("item", src), d = identity("item", dst);
            """),
        Worlds.Kql("w.base", "count", """
            graph count : w.base;
            input source : w.base;
            nodes Item on h = graph("source") | graph-match (h)-[a]->(l), (h)-[b]->(r)
                where labels(a) has "Edge" and labels(b) has "Edge" project h = h.id, k = h.k, n = h.n
                | summarize n = sum(n) by h, k | project h, k, g = k, n;
            """),
        Worlds.Space("import w.base @ 1.0.0 sha256:… as base;", """
            source nodes adapter "record" rows base.R;
            source links adapter "record" rows base.EdgeRow;
            master graph master = base.ingest(nodes: nodes, links: links);
            graph counts = base.count(source: master);
            """)
    ]));

    static Engine Forks(int workers) => new(Fanout.Value, Fanout.Value.Spaces.Keys.Single(), new EngineOptions { PreparationWorkers = workers });

    static void Nodes(Engine e, int count, bool replace = false)
    {
        using var w = new RowWriter(e.RowLayout("nodes"), e.Interner);
        for (var i = 0; i < count; i++)
            for (var n = 1; n <= (replace ? 2 : 1); n++)
            {
                w.Begin(replace && n == 1 ? -1 : 1);
                w.Text("k", $"k{i}"); w.Text("g", "group"); w.Int(w.Col("n"), n); w.Instant("t", 0);
            }
        e.Ingest("nodes", w.Build()!, replace ? 3 : 1);
    }

    static void Links(Engine e, int count, bool star)
    {
        using var w = new RowWriter(e.RowLayout("links"), e.Interner);
        for (var i = 0; i < count; i++)
        {
            w.Begin(); w.Text("src", star ? "k0" : $"k{i}"); w.Text("dst", $"k{i}");
        }
        e.Ingest("links", w.Build()!, 2);
    }

    [Theory]
    [InlineData(8191, false)]
    [InlineData(8192, true)]
    public void Source_batches_use_the_larger_preparation_cutoff(int keys, bool eligible)
    {
        using var e = Forks(4);
        Nodes(e, keys);
        Links(e, 1, star: true);
        var before = e.PreparedRanges;
        Nodes(e, keys, replace: true);
        var updated = Assert.Single(e.Graph("counts").State.Entities);
        Assert.Equal(Value.Integer(2), updated.Props.Get("d:w.base/prop:n"));
        Assert.Equal(eligible && Environment.ProcessorCount > 1, e.PreparedRanges > before);
    }

    [Fact]
    public void A_range_that_exceeds_the_workspace_limit_replays_its_signed_walks_once()
    {
        const int degree = 257;
        using var serial = Forks(1);
        using var parallel = Forks(4);
        var expected = Watch(serial);
        var actual = Watch(parallel);
        foreach (var e in new[] { serial, parallel })
        {
            Nodes(e, Keys);
            Links(e, degree, star: true);
            Nodes(e, Keys, replace: true); // one edited center retracts and re-emits 257² walks
            var center = Assert.Single(e.Graph("counts").State.Entities);
            Assert.Equal(Value.Integer(2 * degree * degree), center.Props.Get("d:w.base/prop:n"));
        }
        Assert.Equal(expected, actual);
        Assert.Equal(Environment.ProcessorCount > 1, parallel.ReplayedPreparationRanges > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Malformed_graph_values_raise_the_first_match_error_in_canonical_order(bool overflowFirst)
    {
        // A graph's Integer permits arbitrary precision. Match writes a signed 64-bit column. Inject
        // two distinguishable bad values into different ranges of the committed graph before a view reads it.
        static Exception Failure(int workers, bool overflowFirst)
        {
            using var e = Forks(workers);
            Nodes(e, Keys);
            var events = new List<string>();
            e.Committed += change =>
            {
                events.Add(change.Graph);
                if (change.Graph != "master") return;
                var edits = change.Patch!.Edits;
                void Corrupt(Rel edge, bool overflow)
                {
                    var current = e.Master.State.Entity(edge.Source)!;
                    var value = overflow ? Value.Integer((System.Numerics.BigInteger)long.MaxValue + 1) : Value.List([]);
                    var props = Props.Of(current.Props.Items.Select(kv => kv.Key == "d:w.base/prop:n" ? KeyValuePair.Create(kv.Key, value) : kv));
                    Assert.Null(e.Master.State.Apply([Edit.Change(current, current with { Props = props })]));
                }
                Corrupt((Rel)edits[0].After!, overflowFirst);
                Corrupt((Rel)edits[^1].After!, !overflowFirst);
            };
            var failure = Assert.ThrowsAny<Exception>(() => Links(e, Keys, star: false));
            Assert.Equal(new[] { "master" }, events);
            Assert.Equal(0, e.Graph("counts").Head);
            return failure;
        }
        var serial = Failure(1, overflowFirst);
        var parallel = Failure(4, overflowFirst);
        Assert.Equal(overflowFirst ? typeof(OverflowException) : typeof(InvalidOperationException), serial.GetType());
        Assert.Equal(serial.GetType(), parallel.GetType());
        Assert.Equal(serial.Message, parallel.Message);
    }
}
