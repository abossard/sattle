using System.Text.Json.Nodes;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

public class HealthPolicyTests
{
    static readonly string Example = Path.Combine(Repo.Root, "engine", "examples", "kiln");

    static SpaceHost Open(int enterAfter, int? lateHorizonWindows = 3)
    {
        var files = Catalog.ReadDirectory(Example);
        var i = files.FindIndex(f => f.Path.EndsWith("workshop.rating.gkql.json", StringComparison.Ordinal));
        Assert.True(i >= 0, "The policy can be loaded as an ordinary GKQL definition");
        var policy = JsonNode.Parse(files[i].Text)!;
        var threshold = policy["verbs"]!.AsArray().Single(v => (string?)v!["verb"] == "hysteresis")!;
        threshold["hysteresis"]!["enterAfter"] = enterAfter;
        files[i] = files[i] with { Text = policy.ToJsonString() };
        var host = new SpaceHost(files, new HostOptions { SpaceName = "kiln", Projections = Repo.Projections, LateHorizonWindows = lateHorizonWindows });
        host.StartLive(new ReplayOptions { LatenessNs = 0 });
        return host;
    }

    static IEnumerable<int> Replay(SpaceHost host, bool withoutReadings = false)
    {
        var lines = File.ReadLines(Path.Combine(Example, "capture", "kiln.ndjson"));
        var position = 0;
        foreach (var line in lines)
        {
            var record = JsonNode.Parse(line)!;
            if (withoutReadings && (string?)record["source"] == "records:Reading") continue;
            var result = host.LiveEnvelope("kiln-feed", line, (++position).ToString());
            Assert.Equal(SpaceHost.LiveResult.Consumed, result);
            host.LiveFlush();
            if ((string?)record["source"] == "records:Clock")
                yield return DateTimeOffset.Parse((string)record["capturedAt"]!).Minute;
        }
    }

    [Fact]
    public void Transitions_after_publishing_an_edit_identify_the_new_policy_revision()
    {
        using var host = Open(2);
        var originalRevision = host.CatalogRevision;
        long originalRating = 0;
        foreach (var minute in Replay(host))
        {
            if (minute == 2)
            {
                Assert.Equal("Hot", Assert.Single(host.Entities("rating", "Instrument")).Prop("state"));
                originalRating = host.Engine.Graph("rating").Head;
                const string file = "workshop.rating.gkql.json";
                var source = JsonNode.Parse(host.Definition(file))!;
                source["verbs"]!.AsArray().Single(v => (string?)v!["verb"] == "hysteresis")!["hysteresis"]!["enterAfter"] = 4;
                host.Commit(file, source.ToJsonString(), originalRevision);
                Assert.Equal("Ready", Assert.Single(host.Entities("rating", "Instrument")).Prop("state"));
            }
            if (minute == 4)
            {
                Assert.Equal("Hot", Assert.Single(host.Entities("rating", "Instrument")).Prop("state"));
                var patches = host.Patches("rating", originalRating);
                Assert.True(patches.Count >= 2);
                Assert.All(patches, patch => Assert.Equal((originalRevision + 1).ToString(), (string?)patch["cause"]!["configurationVersion"]));
                return;
            }
        }
        Assert.Fail("The capture must reach the fourth closed window");
    }

    [Fact]
    public void Corrected_reading_moves_the_crossing_window_while_the_instrument_stays_hot()
    {
        using var host = Open(2);
        using var records = Replay(host).GetEnumerator();
        for (var minute = 1; minute <= 3; minute++) Assert.True(records.MoveNext());
        var before = Assert.Single(host.Entities("rating", "Instrument"));
        Assert.Equal("Hot", before.Prop("state"));
        Assert.Equal(1, DateTimeOffset.Parse(before.Prop("changedIn")!).Minute);

        // The second sample of minute zero was high. Its replacement brings that minute's mean below entry.
        const string correction = """
            {"source":"records:Reading","capturedAt":"2026-01-01T10:03:15Z","payload":{"key":"15","values":
              {"probe":"chamber","metric":"kiln temperature","at":"2026-01-01T10:00:15Z","value":0}}}
            """;
        Assert.Equal(SpaceHost.LiveResult.Consumed, host.LiveEnvelope("kiln-feed", correction, "100"));
        host.LiveFlush();
        var after = Assert.Single(host.Entities("rating", "Instrument"));
        Assert.Equal("Hot", after.Prop("state"));
        Assert.Equal(2, DateTimeOffset.Parse(after.Prop("changedIn")!).Minute);
    }

    static (int, int?, bool) Clock(int minute) => (minute, null, false);
    static (int, int?, bool) Reading(int window, int? deliveredAt = null) => (deliveredAt ?? window, window, false);
    static (int, int?, bool) Withdraw(int window, int deliveredAt) => (deliveredAt, window, true);

    static (string State, string Window) ReplayEvidence(params (int DeliveredAt, int? ReadingAt, bool Withdraw)[] records)
    {
        var origin = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        string At(int minute) => origin.AddMinutes(minute).ToString("O");
        using var host = Open(2, lateHorizonWindows: null);
        var position = 0;
        void Send(string source, int minute, object payload)
        {
            var line = System.Text.Json.JsonSerializer.Serialize(new { source, capturedAt = At(minute), payload });
            Assert.Equal(SpaceHost.LiveResult.Consumed, host.LiveEnvelope("kiln-feed", line, (++position).ToString()));
            host.LiveFlush();
        }
        Send("records:Dependency", 0, new { key = "line", values = new { consumer = "line", probe = "chamber", role = "controls firing", metric = "kiln temperature" } });
        foreach (var (minute, readingAt, withdraw) in records)
        {
            if (readingAt is not { } sample)
                Send("records:Clock", minute, new { key = "clock", values = new { tick = At(minute) } });
            else if (withdraw)
                Send("records:Reading", minute, new { key = At(sample), deleted = true });
            else
                Send("records:Reading", minute, new { key = At(sample), values = new { probe = "chamber", metric = "kiln temperature", at = At(sample), value = 70 } });
        }
        var rating = Assert.Single(host.Entities("rating", "Instrument"));
        return (rating.Prop("state")!, rating.Prop("changedIn")!);
    }

    [Fact]
    public void The_first_silent_window_follows_the_latest_supported_reading()
    {
        var onTime = ReplayEvidence(Reading(0), Clock(1), Reading(2), Clock(3), Clock(6), Clock(8));
        Assert.Equal("No reading", onTime.State);
        Assert.Equal(5, DateTimeOffset.Parse(onTime.Window).Minute);
        var corrected = ReplayEvidence(Reading(0), Clock(1), Clock(4), Reading(2, deliveredAt: 7), Clock(8));
        Assert.Equal(onTime, corrected);
        var jump = ReplayEvidence(Reading(0), Clock(1), Reading(2), Clock(3), Clock(8));
        Assert.Equal(onTime, jump);
    }

    [Fact]
    public void Withdrawing_the_first_reading_after_silence_moves_resumption_to_the_next_supported_window()
    {
        var supported = ReplayEvidence(Reading(0), Clock(1), Clock(4), Reading(7), Clock(8));
        Assert.Equal("Ready", supported.State);
        Assert.Equal(7, DateTimeOffset.Parse(supported.Window).Minute);
        var backfilled = ReplayEvidence(Reading(0), Clock(1), Clock(4), Reading(7), Clock(8), Reading(6, deliveredAt: 8));
        Assert.Equal("Ready", backfilled.State);
        Assert.Equal(6, DateTimeOffset.Parse(backfilled.Window).Minute);
        var corrected = ReplayEvidence(Reading(0), Clock(1), Clock(4), Reading(6), Reading(7), Clock(8), Withdraw(6, deliveredAt: 8));
        Assert.Equal(supported, corrected);

        var chronological = ReplayEvidence(Reading(0), Clock(1), Clock(4), Clock(10), Reading(8, deliveredAt: 10), Reading(9, deliveredAt: 10));
        var reversed = ReplayEvidence(Reading(0), Clock(1), Clock(4), Clock(10), Reading(9, deliveredAt: 10), Reading(8, deliveredAt: 10));
        Assert.Equal(("Ready", "2026-01-01T10:08:00Z"), chronological);
        Assert.Equal(chronological, reversed);
    }

    [Fact]
    public void A_bound_instrument_that_never_reported_reaches_no_data()
    {
        using var host = Open(2);
        Assert.Equal(11, Replay(host, withoutReadings: true).Last());
        Assert.Empty(host.Windows("master", "temperature"));
        var sample = Assert.Single(host.Entities("rating", "Instrument"));
        Assert.Equal("No reading", sample.Prop("state"));
        Assert.All(host.Relationships("impact", "Uses"), edge => Assert.Equal(sample["id"]!.Id(), edge["target"]!.Id()));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    public void The_edited_policy_replays_thresholds_silence_impact_and_task_evidence(int streak, int degradeMinute)
    {
        using var first = Open(streak);
        using var second = Open(streak);
        using var replica = Replay(second).GetEnumerator();
        string? lastState = null, lastWindow = null;
        foreach (var minute in Replay(first))
        {
            Assert.True(replica.MoveNext());
            Assert.Equal(minute, replica.Current);
            var sample = Assert.Single(first.Entities("rating", "Instrument"));
            var state = sample.Prop("state");
            Assert.Equal(minute < degradeMinute || minute is >= 7 and <= 9 ? "Ready"
                : minute < 7 ? "Hot" : "No reading", state);
            var window = DateTimeOffset.Parse(sample.Prop("changedIn")!);
            if (state != lastState)
            {
                Assert.Equal(minute - 1, window.Minute);
                lastWindow = sample.Prop("changedIn");
            }
            else Assert.Equal(lastWindow, sample.Prop("changedIn"));
            lastState = state;

            var links = first.Relationships("impact", "Uses");
            Assert.Equal(2, links.Count);
            Assert.Equal(sample["id"]!.Id(), Assert.Single(links.Select(r => r["target"]!.Id()).Distinct()));
            Assert.All(links, edge => {
                Assert.Equal(state, edge.Prop("observed"));
                Assert.Equal(sample.Prop("changedIn"), edge.Prop("changedIn"));
            });
            var burners = first.Entities("impact", "Consumer").ToDictionary(e => e.Prop("name")!, e => e.Prop("state"));
            Assert.Equal(state, burners["firing line"]);
            Assert.Equal("Ready", burners["display"]);

            var task = first.Entities("progress", "Batch").Single(e => e.Prop("name") == "batch-A");
            Assert.Equal("Accepted", task.Prop("submission"));
            Assert.Equal(minute < 6 ? "Unknown" : "Finished", task.Prop("completion"));
            Assert.Equal(first.Snapshot("rating", null).ToJsonString(), second.Snapshot("rating", null).ToJsonString());
        }
        Assert.False(replica.MoveNext());
        Assert.Equal("No reading", lastState);
        foreach (var graph in new[] { "rating", "impact", "progress" })
        {
            var history = first.Patches(graph, 0);
            Assert.NotEmpty(history);
            Assert.All(history, p => Assert.Equal(first.CatalogRevision.ToString(), (string?)p["cause"]!["configurationVersion"]));
            Assert.Equal(history.Select(p => p.ToJsonString()), second.Patches(graph, 0).Select(p => p.ToJsonString()));
        }
    }
}
