using Sattle.Core;
using Sattle.Language;
using Sattle.Reference;
using Sattle.Runtime;
using static Sattle.Differential.Logs;

namespace Sattle.Differential;

/// <summary>Code review 2, defect 22: what a merge of two identities does to telemetry windows reaches the graphs above in the step of the merge.</summary>
public class Review2MergeTests
{
    static AdvanceInput Advance(long seconds) => new(Origin + seconds * Second);
    static RowInput Load(string? uid, string? name, long n, long seconds) => new("loads", Logs.Row(("uid", uid is null ? Value.Null : Value.Text(uid)), ("name", name is null ? Value.Null : Value.Text(name)), ("n", Value.Integer(n)), ("t", Value.Instant(Origin + seconds * Second))), 1);
    static RowInput PodRow(string uid, string name) => new("pods", Logs.Row(("uid", Value.Text(uid)), ("name", Value.Text(name)), ("g", Value.Text("g0"))), 1);

    /// <summary>Before: the two window changes of the move stayed in the telemetry log of the merge step; a step without a sample of its own never read them.</summary>
    [Fact]
    public void The_windows_a_merge_moves_are_handed_on_in_the_step_of_the_merge()
    {
        var w = Worlds.LinkedLoads;
        using var engine = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single());
        EngineRun.Ingest(engine, [Load("u0", null, 2, 10)], 1);
        EngineRun.Ingest(engine, [Load(null, "Web", 3, 10)], 2);
        engine.Advance(Origin + 120 * Second, 3);
        var events = new List<CommitEvent>();
        engine.Committed += events.Add;
        EngineRun.Ingest(engine, [PodRow("u0", "Web")], 4);
        Assert.Equal(1, engine.Identities.Merges);
        Assert.Contains(events, e => e.Telemetry.Count > 0);
    }

    /// <summary>Before: the view held no window of the survivor after the merge step; the reference has it at once.</summary>
    [Fact]
    public void A_view_reads_the_windows_of_a_merged_pod_in_the_step_of_the_merge()
    {
        var master = Worlds.LinkedLoads.Files[0].Text;
        var w = new World("a view's series over a pod that a link merges", "Link, Series, Rollup in a view",
        [
            new SourceFile("w.sattle", master[..master.IndexOf("space s {", StringComparison.Ordinal)]),
            new SourceFile("w.view.sattle", """
                module w.view @ 1.0.0;
                import w @ 1.0.0 sha256:… as base;
                entity Seen { load?: Integer telemetry delta sum every 60s; }
                projection v view forward output w.view {
                  in master: graph base;
                  let m = match master (p: base.Pod);
                  let s = m |> series master p.load within 30m |> rollup by { p: p } combine { v: sum(load) };
                  emit entity Seen from s identity p set { load: v };
                }
                space s {
                  source pods adapter "sattle.authoring" rows base.PodRow;
                  source flows adapter "sattle.authoring" rows base.FlowRow;
                  source loads adapter "sattle.authoring" rows base.LoadRow;
                  master graph master = base.ingest(pods: pods, flows: flows, loads: loads);
                  graph view = v(master: master);
                }
                """),
        ], []);
        Input[] log =
        [
            new RowInput("flows", Logs.Row(("src", Value.Text("web")), ("dst", Value.Text("web"))), 1),
            Load(null, "Web", 2, 20),
            Advance(90),
            PodRow("u0", "Web"),
        ];
        var mismatch = Differential.FirstMismatch(w, log, 0, reordered: false);
        Assert.True(mismatch is null, mismatch?.ToString());
    }
}
