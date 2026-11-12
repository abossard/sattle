using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The host keeps a declared number of patches per graph and events of the stream (`RetainPatches`, `RetainEvents`). Revision numbers, heads, state and digests do not change; what is older than the retention answers `Gap` (410) with the first revision still available, and a subscriber that resumes before the oldest event gets `gap` and snapshots.</summary>
[Trait("Category", "Slow")]
public class RetentionOfHistoryTests
{
    const long Min = 60_000_000_000;
    static SpaceHost Fed(int patches, int events)
    {
        var h = new SpaceHost(EstateTests.Files(demo: false), new HostOptions { Cluster = "synth", RetainPatches = patches, RetainEvents = events });
        new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 1, AdvanceEveryNs = Min, LatenessNs = Min, Cluster = "synth" }).Run(EstateTests.EstateDir);
        return h;
    }

    static readonly Lazy<SpaceHost> Unbounded = new(() => Fed(0, 0));

    [Theory]
    [InlineData(5, 40)] [InlineData(12, 100)] [InlineData(1000, 100_000)]
    public void Heads_state_and_sequence_do_not_depend_on_the_retention(int patches, int events)
    {
        using var h = Fed(patches, events);
        var all = Unbounded.Value;
        Assert.Equal(all.StateDigest(), h.StateDigest());
        Assert.Equal(all.Seq, h.Seq);
        Assert.Equal(all.SpaceInfo()["graphs"]!.ToJsonString(), h.SpaceInfo()["graphs"]!.ToJsonString());
    }

    [Theory]
    [InlineData("master", 5)] [InlineData("workload", 8)]
    public void Revisions_inside_the_retention_read_exactly_as_without_a_cap_and_older_ones_answer_Gap(string graph, int keep)
    {
        using var h = Fed(keep, 100_000);
        var all = Unbounded.Value;
        var head = h.Head(graph);
        Assert.Equal(all.Head(graph), head);
        var kept = h.Patches(graph, head - keep);                            // the retained ones are the last `keep` (and up to 10 percent more)
        Assert.True(kept.Count >= keep && kept.Count <= keep + Math.Max(1, keep / 10) + 1, $"{kept.Count} kept");
        var first = head - kept.Count;                                                         // the oldest revision whose successor is retained
        Assert.Equal(all.Snapshot(graph, first).ToJsonString(), h.Snapshot(graph, first).ToJsonString());
        Assert.Equal(all.Snapshot(graph, head - 1).ToJsonString(), h.Snapshot(graph, head - 1).ToJsonString());
        Assert.Equal(all.Patches(graph, first).Select(p => p.ToJsonString()), h.Patches(graph, first).Select(p => p.ToJsonString()));
        var gap = Assert.Throws<HostError>(() => h.Patches(graph, 0));
        Assert.Equal(("Gap", 410), (gap.Code, gap.Status));
        Assert.Equal(first.ToString(), gap.Extra["lastAvailable"]!.GetValue<string>());
        Assert.Equal(410, Assert.Throws<HostError>(() => h.Snapshot(graph, 0)).Status);
    }

    [Fact]
    public void A_subscriber_that_resumes_before_the_oldest_retained_event_gets_gap_and_snapshots_and_one_that_resumes_inside_gets_exactly_the_missed_events()
    {
        using var h = Fed(2_000, 60);
        Assert.True(h.Seq > 100);
        var old = h.Subscribe(null, false, 3);
        var first = JsonNode.Parse(Next(old))!;
        Assert.Equal("gap", first["type"]!.GetValue<string>());
        Assert.True(long.Parse(first["firstSeq"]!.GetValue<string>()) > 4);
        Assert.Contains("snapshot", string.Join("", Drain(old)));
        h.Unsubscribe(old);
        var recent = h.Subscribe(null, false, h.Seq - 5);
        var events = Drain(recent).Select(x => JsonNode.Parse(x)!["seq"]!.GetValue<string>()).ToList();
        Assert.Equal(Enumerable.Range(1, 5).Select(i => (h.Seq - 5 + i).ToString()), events);
        h.Unsubscribe(recent);
    }

    static string Next(Subscription s) { Assert.True(s.Messages.Reader.TryRead(out var m)); return m!; }
    static List<string> Drain(Subscription s) { var l = new List<string>(); while (s.Messages.Reader.TryRead(out var m)) l.Add(m); return l; }
}
