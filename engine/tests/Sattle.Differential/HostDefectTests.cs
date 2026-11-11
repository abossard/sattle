using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Differential;

/// <summary>The defects of the review's list "Correctness, reached today" that sit outside the operators: host, snapshot, drafts, export, refactoring. Each states the correct behaviour and fails until the defect is fixed.</summary>
[Trait("Category", "KnownDefect")]
public class HostDefectTests
{
    static string Definitions
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "engine", "definitions"))) return Path.Combine(dir.FullName, "engine", "definitions");
            throw new DirectoryNotFoundException("engine/definitions");
        }
    }

    static readonly Lazy<(IReadOnlyList<SourceFile> Files, SpaceHost.LoadedDefinitions Loaded)> Shipped = new(() => { var f = Catalog.ReadDirectory(Definitions); return (f, SpaceHost.LoadDefinitions(f)); });
    static SpaceHost ShippedHost() => new(Shipped.Value.Files, new HostOptions { Cluster = "synth" }, Shipped.Value.Loaded);

    static List<JsonNode> Drain(Subscription s) { var l = new List<JsonNode>(); while (s.Messages.Reader.TryRead(out var m)) l.Add(JsonNode.Parse(m)!); return l; }

    /// <summary>Review: Host.cs:542. A subscriber that reads every message is never a slow consumer. `Subscription.Queued` is only incremented, so the 10,001st message closes it.</summary>
    [Fact]
    public void A_subscriber_that_reads_everything_is_not_closed_as_a_slow_consumer()
    {
        using var h = ShippedHost();
        var sub = h.Subscribe(["master"], false, null);
        long received = Drain(sub).Count;
        for (var i = 0; i < 10_100 && !sub.Closed; i++) { h.Finish(); received += Drain(sub).Count; }
        Assert.True(!sub.Closed, $"closed after {received} messages, all of them read");
    }

    /// <summary>Review: Output.cs:295-306 with Host.cs:490. A subscriber learns that a telemetry window went. The host publishes only changes with a new value, so the retraction of a window reaches nobody.</summary>
    [Fact]
    public void A_subscriber_is_told_when_a_telemetry_window_is_retracted()
    {
        const long start = Logs.Origin, minute = Logs.Minute;
        using var h = ShippedHost();
        new SyntheticWorld(h, 4, 2).LoadStructure();
        var sub = h.Subscribe(["master"], false, null);
        Drain(sub);
        h.Ingest("flows", new SyntheticWorld(h, 4, 2).Flows(20, start, minute / 2), 10);
        h.Advance(start + 2 * minute, 11);
        var published = Drain(sub).Count(m => m["type"]!.GetValue<string>() == "telemetry");
        Assert.True(published > 0, "the windows were not published in the first place");
        var retraction = new SyntheticWorld(h, 4, 2).Flows(20, start, minute / 2);
        retraction.W = [.. Enumerable.Range(0, retraction.N).Select(i => -retraction.Weight(i))];
        h.Ingest("flows", retraction, 12);                                                    // inside the late horizon: every window of these flows is retracted
        var after = Drain(sub);
        Assert.True(after.Any(m => m["type"]!.GetValue<string>() == "telemetry"), $"{published} telemetry messages announced the windows, none their retraction; received: {string.Join(", ", after.Select(m => m["type"]!.GetValue<string>()))}");
    }

    sealed class WriterRoot { public List<RowWriter> Writers = []; }

    /// <summary>Review: StateSnapshot.cs:32. A restored row writer writes rows. The reflective snapshot skips every field of type `Layout`, so a writer that went through a seal has none; the host then throws on the first tail entry and replays the whole journal without saying so.</summary>
    [Fact]
    public void A_row_writer_that_went_through_a_snapshot_still_has_its_layout()
    {
        var layout = new Layout(["k", "n"], [false, false]);
        var saved = StateSnapshot.Save(new WriterRoot { Writers = [new RowWriter(layout, new Interner())] });
        var restored = new WriterRoot();                    // as in a host that creates its writers when the first row of a source arrives
        StateSnapshot.Load(saved, restored);
        Assert.NotNull(Assert.Single(restored.Writers).Layout);
    }

    /// <summary>Review: Drafts.cs:283, :126. A draft is compared with its base. Check, preview and refactor compare it with the head, so after another commit an untouched draft reports that commit as its own change (the module reverted).</summary>
    [Fact]
    public void An_untouched_draft_reports_no_change_after_another_commit()
    {
        using var h = ShippedHost();
        var draft = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var text = h.Definition("acme.svc");
        Assert.Contains("above 0.05 for 3", text);
        h.Commit("acme.svc", text.Replace("above 0.05 for 3", "above 0.06 for 3"), h.CatalogRevision);
        var changed = h.CheckDraft(draft)["modules"]!.AsObject().Where(m => m.Value!["changed"]?.GetValue<bool>() == true).Select(m => m.Key);
        Assert.Empty(changed);
    }

    /// <summary>Review: Export.cs:296. The thresholds document states the thresholds that apply. A parameter that is read from the rule row was exported as the literal 0 (above 0, enterAfter 0, below 0, exitAfter 0); it has no one value, so it is null and `byRow` names the column it is read from.</summary>
    [Fact]
    public void An_exported_threshold_never_claims_to_enter_after_zero_windows()
    {
        using var h = ShippedHost();
        var thresholds = h.Meters("treeSignals")["thresholds"]!.AsArray();
        Assert.NotEmpty(thresholds);
        Assert.All(thresholds, t => Assert.True(t!["enterAfter"] is null ? t["byRow"]?["enterAfter"] is not null : t["enterAfter"]!.GetValue<long>() >= 1, t.ToJsonString()));
        Assert.All(h.Meters("serviceTree")["thresholds"]!.AsArray(), t => Assert.True(t!["enterAfter"]!.GetValue<long>() >= 1 && t["byRow"] is null, t.ToJsonString()));
    }

    /// <summary>Review: Refactor.cs:40. An extracted rule keeps every port it reads. The port after `via` of a lift is not collected, so extracting `lifted` from `capabilityView` gives a projection that does not check.</summary>
    [Fact]
    public void Extracting_a_rule_with_a_lift_keeps_the_port_of_its_mapping()
    {
        var files = Shipped.Value.Files;
        static string Module(SourceFile f) => Path.GetFileNameWithoutExtension(f.Path);
        var changed = Refactor.Extract(files.ToDictionary(Module, f => f.Text), "acme.biz", "capabilityView", ["lifted"], "liftedOnly");
        Assert.Contains("acme.biz", changed.Keys);
        var result = new Catalog().Load(files.Select(f => changed.TryGetValue(Module(f), out var t) ? f with { Text = t } : f), updatePins: true);
        var errors = result.Diagnostics.Where(d => d.IsError).Select(d => d.ToString()[(d.ToString().LastIndexOf("acme.", StringComparison.Ordinal) is >= 0 and var i ? i : 0)..]).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors));
    }
}
