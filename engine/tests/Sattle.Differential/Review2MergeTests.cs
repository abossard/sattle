using System.Collections.Immutable;
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
    static ImmutableArray<string> ByEngine(World w, IReadOnlyList<Input> log, string graph) => EngineRun.Final(w.Catalog, log, Chunking.OneByOne).Graphs[graph].Lines();
    static ImmutableArray<string> ByReference(World w, IReadOnlyList<Input> log, string graph) => ReferenceEvaluator.Evaluate(w.Catalog, w.Catalog.Spaces.Keys.Single(), log).Graphs[graph].Lines();
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
        var w = new World("a view's series over a pod that a link merges", "Link, Series, Rollup in a view",
        [
            .. Worlds.LinkedLoads.Files.Take(2),        // the types and the ingestion, without the space
            new SourceFile("w.view.sattle", """
                module w.view @ 1.0.0;
                import w @ 1.0.0 sha256:… as base;
                entity Seen { load?: Integer telemetry delta sum every 60s; }
                """),
            Worlds.Kql("w.view", "v", """
                graph v : w.view;
                input master : base;
                nodes Seen on p = graph("master") | graph-match (p) where labels(p) has "Pod" project p = p.id
                    | join kind=inner (telemetry("master", 30m) | where property == "load" | project p = id, windowStart, property, value) on p
                    | summarize load = sum(value) by p, windowStart;
                """),
            Worlds.Space("import w @ 1.0.0 sha256:… as base;\nimport w.view @ 1.0.0 sha256:… as view;", """
                  source pods adapter "sattle.authoring" rows base.PodRow;
                  source flows adapter "sattle.authoring" rows base.FlowRow;
                  source loads adapter "sattle.authoring" rows base.LoadRow;
                  master graph master = base.ingest(pods: pods, flows: flows, loads: loads);
                  graph view = view.v(master: master);
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

    /// <summary>Two rows under the id of the name give the window no value (a conflict); a row under the id of the uid gives it one. After the link the window has none, whichever came first. Before: a link after the rows left the value of the uid's rows, and the conflict under the id that lost.</summary>
    [Fact]
    public void A_window_without_a_value_under_one_id_has_none_after_the_merge()
    {
        Input[] log =
        [
            Load(null, "Web", 5, 10), Load(null, "web", 7, 20),
            Load("u0", null, 3, 30),
            Advance(90),
            PodRow("u0", "Web"),
        ];
        var mismatch = Differential.FirstMismatch(Worlds.LinkedLoads, log, 0, reordered: false);
        Assert.True(mismatch is null, mismatch?.ToString());
    }

    /// <summary>Before: the state digest depended on whether the link came before or after the rows (PROTOCOL 3.22 says it does not).</summary>
    [Fact]
    public void The_state_digest_does_not_depend_on_the_order_of_the_link_and_the_rows()
    {
        string Digest(params Input[] log)
        {
            using var engine = new Engine(Worlds.LinkedLoads.Catalog, Worlds.LinkedLoads.Catalog.Spaces.Keys.Single());
            long seq = 0;
            foreach (var i in log)
                if (i is AdvanceInput a) engine.Advance(a.Frontier, ++seq); else EngineRun.Ingest(engine, [(RowInput)i], ++seq);
            return engine.StateDigest();
        }
        var rows = new Input[] { Load(null, "Web", 5, 10), Load(null, "web", 7, 20), Load("u0", null, 3, 30), Advance(90) };
        Assert.Equal(Digest([.. rows, PodRow("u0", "Web")]), Digest([PodRow("u0", "Web"), .. rows]));
    }

    /// <summary>A view names its elements by the element. A key in a domain that an equivalence policy joins is refused: after the link the row that leaves the view computes the survivor's id and does not find the element it made, which stayed under the id that lost (the review's finding). Named by the element, the view follows the merge like the reference.</summary>
    [Theory]
    [InlineData("identity(\"podName\", name)", true)]
    [InlineData("p", false)]
    public void A_view_names_its_elements_by_the_element_not_by_a_key_of_a_linked_domain(string identity, bool refused)
    {
        var w = Worlds.LinkedWith("a view named by the element of a linked domain", "Link, EmitEntity in a view", "entity Tag { name?: Text; }",
            $"nodes Tag on t = graph(\"master\") | graph-match (p) where labels(p) has \"Pod\" project p = p.id, name = p.name | project t = {identity}, name;");
        if (refused)
        {
            var (code, message) = Assert.Single(new Catalog(EngineRun.Projections).Load([.. w.Files.Where(f => f.Path != "w.space.sattle")], updatePins: true).Diagnostics.Where(d => d.IsError).Select(d => (d.Code, d.Message)).Distinct());     // without the space, which only says that the view did not load
            Assert.Equal("IdentityLinked", code);
            Assert.Contains("keep its id (p.id)", message);
            return;
        }
        Input[] log = [new RowInput("flows", Logs.Row(("src", Value.Text("web")), ("dst", Value.Text("web"))), 1), PodRow("u0", "Web")];
        var (expected, actual) = (ByReference(w, log, "view"), ByEngine(w, log, "view"));
        Assert.True(expected.SequenceEqual(actual), $"{Print(log)}\nexpected:\n{string.Join("\n", expected)}\nengine:\n{string.Join("\n", actual)}");
    }
}
