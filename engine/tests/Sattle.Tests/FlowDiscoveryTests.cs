using System.Text.Json;
using System.Text.Json.Nodes;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

public class FlowDiscoveryTests
{
    const string Time = "2026-11-01T12:00:10Z";
    static string DirectoryOf(string name) => Path.Combine(Repo.Root, "engine", "standard", "flows", name);

    sealed class Space : IDisposable
    {
        public readonly SpaceHost Host = new(Catalog.ReadDirectory(DirectoryOf("definitions")),
            new HostOptions { Projections = Repo.Projections, SpaceName = "flowDiscovery" });
        int seq;

        public Space() => Host.StartLive(new ReplayOptions { LatenessNs = 0 });

        public void Deliver(string row, string key, object values, int minute = 0, bool deleted = false)
        {
            var line = JsonSerializer.Serialize(new { v = 1, source = "records:" + row, seq = ++seq, key,
                capturedAt = $"2026-11-01T12:{minute:D2}:30Z", payload = new { key, values, deleted } });
            Host.LiveEnvelope("local", line, JsonSerializer.Serialize(new { seq }));
            Host.LiveFlush();
        }

        public void Span(string trace, string id, string operation, string? parent = null,
            string source = "tracer", int minute = 0, bool deleted = false) =>
            Deliver("SpanRow", $"{source}/{trace}/{id}", new { source, trace, span = id, operation, parent, time = Time,
                reference = $"{source}/{trace}/{id}" }, minute, deleted);

        public void Close(int minute = 1) =>
            Host.Advance(DateTimeOffset.Parse($"2026-11-01T12:{minute:D2}:00Z").ToUnixTimeMilliseconds() * 1_000_000, ++seq);

        public void Dispose() => Host.Dispose();
    }

    [Fact]
    public void A_parent_forks_only_to_its_identified_children_and_shared_operations_keep_their_identity()
    {
        using var space = new Space();
        void Deliver(int i)
        {
            switch (i)
            {
                case 0: space.Span("submit-42", "web", "submit"); break;
                case 1: space.Span("submit-42", "a", "enqueue", "web"); break;
                case 2: space.Span("submit-42", "b", "enqueue", "web"); break;
                case 3: space.Span("another", "web", "enqueue"); break;
                case 4: space.Span("submit-42", "web", "submit", source: "another-tracer"); break;
            }
        }
        foreach (var i in new[] { 3, 1, 2, 1, 4, 0 }) Deliver(i);
        space.Close();
        var occurrences = space.Host.Entities("flows", "Occurrence");
        Assert.Equal(5, occurrences.Count);
        var parent = occurrences.Single(n => n.Prop("reference") == "tracer/submit-42/web")["id"].Id();
        var childIds = occurrences.Where(n => n.Prop("parent") == "web").Select(n => n["id"].Id()).ToHashSet();
        var edges = space.Host.Relationships("flows", "Handoff");
        Assert.Equal(2, edges.Count);
        Assert.All(edges, e => { Assert.Equal(parent, e["source"].Id()); Assert.Equal("observed", e.Prop("basis")); });
        Assert.True(childIds.SetEquals(edges.Select(e => e["target"].Id())));
        Assert.Single(space.Host.Entities("flows", "OperationPattern"), n => n.Prop("operation") == "enqueue");
        foreach (var edge in edges)
            Assert.True(space.Host.Lineage("flows", edge["id"].Id())["sources"]!.AsArray().Count >= 2);
        var digest = space.Host.Engine.Graph("flows").State.Digest();

        using var replay = new Space();
        replay.Span("submit-42", "web", "submit");
        replay.Span("submit-42", "b", "enqueue", "web");
        replay.Span("submit-42", "a", "enqueue", "web");
        replay.Span("submit-42", "web", "submit", source: "another-tracer");
        replay.Span("another", "web", "enqueue");
        replay.Close();
        Assert.Equal(digest, replay.Host.Engine.Graph("flows").State.Digest());
    }

    [Fact]
    public void A_missing_parent_resolves_then_retracts_and_a_late_arrival_is_accounted_for()
    {
        using var space = new Space();
        space.Span("submit-42", "child", "enqueue", "web");
        space.Close();
        JsonNode Child() => Assert.Single(space.Host.Entities("flows", "Occurrence"), n => n.Prop("span") == "child");
        Assert.Equal("unresolved", Child().Prop("parentStatus"));
        Assert.Equal("tracer/submit-42/child", Child().Prop("reference"));
        Assert.Empty(space.Host.Relationships("flows", "Handoff"));
        space.Span("submit-42", "web", "submit", minute: 1);
        Assert.Equal("resolved", Child().Prop("parentStatus"));
        Assert.Single(space.Host.Relationships("flows", "Handoff"));
        space.Span("submit-42", "web", "submit", minute: 2, deleted: true);
        Assert.Empty(space.Host.Relationships("flows", "Handoff"));
        Assert.Equal("unresolved", Child().Prop("parentStatus"));

        space.Span("submit-42", "web", "submit", minute: 7);
        Assert.True(long.Parse(space.Host.Liveness()["counters"]!["lateRows"]!.GetValue<string>()) > 0);
        Assert.Empty(space.Host.Relationships("flows", "Handoff"));
        Assert.Equal("unresolved", Child().Prop("parentStatus"));
    }

    [Fact]
    public void Counts_keep_their_grain_and_accepting_a_rule_keeps_both_origins()
    {
        using var space = new Space();
        void Count(string reference, string operation, int count, string? parentOperation = null) =>
            space.Deliver("OperationCountRow", reference, new { source = "tracer", reference, operation, count, parentOperation, time = Time });
        void Claim(string basis, string revision, string reference) =>
            space.Deliver("FlowClaimRow", reference, new { source = "tracer", fromOperation = "checkout", toOperation = "notify",
                basis, revision, reference, evidence = "tracer/summary/19" });
        Count("tracer/summary/19", "checkout", 9);
        Count("tracer/summary/19", "checkout", 9);
        Count("tracer/pair/1", "pay", 3, "checkout");
        Count("tracer/pair/2", "pay", 4, "checkout");
        Claim("inferred", "route-rule/v3", "suggestion/19");
        Claim("authored", "journey/v4", "journey/19");
        Claim("observed", "untrusted", "suggestion/forged");
        space.Close();
        Assert.Empty(space.Host.Entities("flows", "Occurrence"));
        Assert.Empty(space.Host.Relationships("flows", "Handoff"));
        var checkout = space.Host.Entities("flows", "OperationPattern").Single(n => n.Prop("operation") == "checkout");
        Assert.Equal("9", checkout.Prop("reportedCount"));
        var counted = Assert.Single(space.Host.Relationships("flows", "CountedHandoff"));
        Assert.Equal("observed", counted.Prop("basis"));
        Assert.Equal("7", counted.Prop("count"));
        var sources = space.Host.Lineage("flows", counted["id"].Id())["sources"]!.AsArray();
        Assert.Equal(2, sources.Count);
        foreach (var (type, basis, revision, reference) in new[] {
            ("InferredHandoff", "inferred", "route-rule/v3", "suggestion/19"), ("AuthoredHandoff", "authored", "journey/v4", "journey/19") })
        {
            var edge = Assert.Single(space.Host.Relationships("flows", type));
            Assert.Equal(basis, edge.Prop("basis"));
            var source = Assert.Single(space.Host.Lineage("flows", edge["id"].Id())["sources"]!.AsArray())!;
            var claim = space.Host.Entities("master", "FlowClaim").Single(n => n["id"].Id() == source["element"]!.GetValue<string>());
            Assert.Equal(revision, claim.Prop("revision"));
            Assert.Equal(reference, claim.Prop("reference"));
            Assert.Equal("tracer/summary/19", claim.Prop("evidence"));
        }
    }
}
