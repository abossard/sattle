using System.Collections.Concurrent;
using Sattle.Core;
using Sattle.Language;
using static Sattle.Differential.Logs;

namespace Sattle.Differential;

/// <summary>A small graph space that exercises one operator kind (or a small composition), with the sources its logs are generated for. Its files are gkql documents: modules (types, row types, identities, the space) and one program per projection.</summary>
public sealed record World(string Name, string Kind, IReadOnlyList<SourceFile> Files, IReadOnlyList<SourceSpec> Sources, double AdvanceShare = 0)
{
    /// <summary>Text that some reference result of the default seeds must contain, so a world that never exercises its operator fails.</summary>
    public IReadOnlyList<string> Witnesses { get; init; } = [];
    /// <summary>The window retention of the space (Def. 7.13), for the engine and the reference alike; the default is 48 hours.</summary>
    public long RetentionNs { get; init; } = 48 * 60 * Minute;
    /// <summary>The sources that report progress in the logs of this world (Def. 10.9), and the share of the inputs that are such reports.</summary>
    public IReadOnlyList<string> Reporting { get; init; } = [];
    public double ProgressShare { get; init; }
    /// <summary>The share of the inputs that are loss marks of a source (Def. 10.4), at a time up to four minutes before the frontier.</summary>
    public double LossShare { get; init; }
    public System.Collections.Immutable.ImmutableArray<Sattle.Reference.Input> Log(int seed, int length) => Logs.Generate(Sources, AdvanceShare, seed, length, (Reporting, ProgressShare), LossShare);
    static readonly ConcurrentDictionary<string, Catalog> catalogs = new();
    public Catalog Catalog => catalogs.GetOrAdd(Name, _ => EngineRun.Load(Files));
    public override string ToString() => Name;
}

public static class Worlds
{
    // ---- sources ----

    static readonly SourceSpec A = Bag("a"), B = Bag("b");
    static SourceSpec Bag(string name) => new(name, (r, now) => Row(
        ("k", Text(r, "k", 4)), ("g", Text(r, "g", 3)), ("n", Value.Integer(r.Next(6))),
        ("d", Pick(r, Value.Null, Value.Decimal(1m), Value.Decimal(2.5m), Value.Decimal(2.50m), Value.Decimal(0m))),
        ("t", Near(r, now))));
    static SourceSpec Keyed(string name) => Bag(name) with { Key = f => ((TextValue)f["k"]).S };
    static readonly SourceSpec Edges = new("edges", (r, _) => Row(("src", Text(r, "k", 4)), ("dst", Text(r, "k", 4))), f => ((TextValue)f["src"]).S + ">" + ((TextValue)f["dst"]).S);
    static readonly SourceSpec BagEdges = Edges with { Key = null };

    // ---- files ----

    /// <summary>The gkql documents of the world `name`: its modules, programs and space, in `worlds/` beside the tests, one folder per world.</summary>
    public static IReadOnlyList<SourceFile> Files(string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "worlds", System.Text.RegularExpressions.Regex.Replace(name, "[^A-Za-z0-9]+", "-").Trim('-'));
        return [.. Directory.GetFiles(dir).Order(StringComparer.Ordinal).Select(f => new SourceFile(Path.GetFileName(f), File.ReadAllText(f)))];
    }

    // ---- one ingestion module: rows of R in, the operator under test, a count per row out ----

    static World Rows(string name, string kind, double advanceShare = 0, IReadOnlyList<SourceSpec>? sources = null) => new(name, kind, Files(name), sources ?? [A, B], advanceShare);

    /// <summary>A group of the rows of `a` by `g` into an element `Agg`.</summary>
    static World Aggregate(string name) => Rows("group " + name, "Group");
    /// <summary>#159: percentiles of nearest rank of the integers `n` (p 0, 50, 95, 100) and the decimals `d` (p 50, 99.9) of a group.</summary>
    public static readonly World Percentile = Aggregate("percentile") with { Witnesses = ["p95=n:", "high=n:"] };

    /// <summary>#159: two fixpoints over the same rows. `ranked`: the worst rank (0 to 3) of an item k0 to k4 and of what it depends on, an edge marked Limited
    /// lowering a rank of 2 or 3 by one and an edge marked None passing 0; a rank of 4 is outside the order, a missing one is no value. `worst`: the worst state
    /// (ok, warn, down) of a decimal key, where 2.5 and 2.50 are one key and gone is outside the order. `loaded`: the greatest decimal (1, 2.5, 3) of an item and
    /// what it depends on, 2.50 being 2.5 and 0 outside the order. Edges make chains, diamonds, cycles and self-loops.</summary>
    static readonly SourceSpec FixpointEdges = new("edges", (r, _) => Row(("src", Text(r, "k", 5)), ("dst", Text(r, "k", 5)),
        ("impact", Pick(r, Value.Text("Limited"), Value.Text("None"), Value.Text("Other"), Value.Null)), ("ds", Decimals(r)), ("dd", Decimals(r))));
    static Value Decimals(Random r) => Pick(r, Value.Decimal(1m), Value.Decimal(2.5m), Value.Decimal(2.50m), Value.Decimal(3m), Value.Decimal(0m));
    public static readonly World Fixpoint = Rows("fixpoint", "Fixpoint", 0,
    [
        new("nodes", (r, _) => Row(("k", Text(r, "k", 5)), ("rank", Pick(r, Value.Integer(0), Value.Integer(1), Value.Integer(2), Value.Integer(3), Value.Integer(4), Value.Null)),
            ("d", Decimals(r)), ("s", Pick(r, Value.Text("ok"), Value.Text("warn"), Value.Text("down"), Value.Text("gone"), Value.Null)))),
        FixpointEdges, FixpointEdges,
    ]) with { Witnesses = ["Rank", "Worst", "Load"] };

    /// <summary>#159 A6: the roll-up of a dependency tree of three levels (a0, b0 b1, c0 to c3, d0 to d3; edges from one level to the next, a node with
    /// several parents) written twice: unrolled to three levels by joins and summaries, as the mirror of the lab writes it, and as one fixpoint. Every node
    /// has a state, 0 unless a row of `own` says more; an edge marked Limited lowers a rank of 2 or 3 by one. `Unrolled` and `Fixed` hold the two results.</summary>
    static readonly Value[][] Levels = [[Value.Text("a0")], [Value.Text("b0"), Value.Text("b1")], [.. Enumerable.Range(0, 4).Select(i => Value.Text("c" + i))], [.. Enumerable.Range(0, 4).Select(i => Value.Text("d" + i))]];
    public static readonly World RollupOfThreeLevels = Rows("rollup of three levels", "Join, Map, Union, Group, Fixpoint", 0,
    [
        new("own", (r, _) => Row(("key", Pick(r, [.. Levels.SelectMany(l => l)])), ("rank", Value.Integer(r.Next(4))))),
        new("deps", (r, _) => { var l = r.Next(3); return Row(("parentKey", Pick(r, Levels[l])), ("childKey", Pick(r, Levels[l + 1])), ("impact", Pick(r, Value.Text("Limited"), Value.Text("Full")))); }),
    ]) with { Witnesses = ["Unrolled", "Fixed"] };

    /// <summary>#159 A7 (Ch. 6, Def. 6.1, Cor. 6.5): rows of `a` (lateness 1 minute) joined on `k` with rows of `b` (lateness 4 minutes), counted and summed in
    /// windows of a minute of the time of `a`. A row late for its own input is left out; a window stays revisable for 4 minutes after its end.</summary>
    public static readonly World LatenessPerInput = Rows("lateness per input", "Join, Window, Group over windows, lateness per input", 0.25) with { Witnesses = ["window "] };
    /// <summary>Rows in windows of their own time. A late row revises its window.</summary>
    public static readonly World Windowed = Rows("window late revise", "Window, Group over windows", 0.25);
    /// <summary>D3 (#101): sliding windows of 3 minutes that move by 60 s; a row counts in the three windows that hold it.</summary>
    static readonly World Hopped = Rows("hop 3m by 60s", "Window with a hop, Group over windows", 0.25);
    /// <summary>D3 (#101): a window over a window: the 60 s windows of the rows, summed in sliding windows of 3 minutes.</summary>
    static readonly World HopOverWindows = Rows("hop over windows", "Group over windows, Window with a hop over its windows", 0.25);
    /// <summary>D3 (#101): shift: the rows counted in the window 90 s after their own time, so a shifted row crosses window borders.</summary>
    static readonly World Shifted = Rows("shift into the next window", "Shift, Window, Group over windows", 0.25);

    // ---- a master graph of items (rows of `a`), their edges and their hits per window of 60 seconds (rows of `b`), and a view `v` over it ----

    /// <summary>Two items and hits in the last few windows, so consecutive windows of one key have values.</summary>
    static readonly IReadOnlyList<SourceSpec> Dense =
    [
        new("a", (r, now) => Row(("k", Text(r, "k", 2)), ("g", Value.Text("g0")), ("n", Value.Integer(1)), ("d", Value.Null), ("t", Value.Instant(now))), f => ((TextValue)f["k"]).S),
        new("b", (r, now) => Row(("k", Text(r, "k", 2)), ("g", Value.Text("g0")), ("n", Value.Integer(r.Next(5))), ("d", Value.Null), ("t", Value.Instant(now + r.Next(-6, 2) * 30 * Second)))),
        new("b", (r, now) => Row(("k", Text(r, "k", 2)), ("g", Value.Text("g0")), ("n", Value.Integer(r.Next(5))), ("d", Value.Null), ("t", Value.Instant(now + r.Next(-6, 2) * 30 * Second)))),
    ];

    static World View(string name, string kind, double advanceShare = 0, IReadOnlyList<SourceSpec>? sources = null) => new(name, kind, Files(name), sources ?? [Keyed("a"), B, Edges], advanceShare);

    // ---- instances with two keys: a link merges two identities of one instance ----

    static readonly string[] InstanceNames = ["Web", "api", "DB", "cache"];
    static readonly SourceSpec Instances = new("instances", (r, _) => { var i = r.Next(4); return Row(("uid", Value.Text("u" + i)), ("name", Value.Text(InstanceNames[i])), ("g", Text(r, "g", 2))); }, f => ((TextValue)f["uid"]).S);
    /// <summary>Instances whose name a later row of the same uid may change: the row that replaces it withdraws the old link, so a class can split (Ch. 2, Def. 2.5).</summary>
    static readonly SourceSpec Renamed = new("instances", (r, _) => Row(("uid", Value.Text("u" + r.Next(4))), ("name", Value.Text(InstanceNames[r.Next(4)])), ("g", Text(r, "g", 2))), f => ((TextValue)f["uid"]).S);
    static readonly SourceSpec Flows = new("flows", (r, _) => Row(("src", Value.Text(InstanceNames[r.Next(4)].ToLowerInvariant())), ("dst", Value.Text(InstanceNames[r.Next(4)]))));

    /// <summary>A view over the master of the linked instances: instances by their uid, instances named by flows, the calls between them, and the link of an instance's uid to its name.</summary>
    static World LinkedWith(string name, string kind) => new(name, kind, Files(name), [Instances, Flows]);

    /// <summary>T-B3: a view groups the calls of each instance by the instance it matches. A link in the master merges two identities of one instance; the view's group re-keys through the master's patch, so its count is that of one group over both keys (Theorem 6.1 under identity, the stated way to group by an identity under rule IdOf-in-sinks).</summary>
    public static readonly World LinkedView = LinkedWith("a view grouped by an instance that a link merges", "Link, Match, Group, EmitEntity in a view") with { Witnesses = ["d:w.view/prop:calls="] };

    /// <summary>A view that names everything by the instance it matches, over the same master: every instance counts zero and every call one (a union, one side of which carries the name read from the instance), a group by the instance, two joins on the instance, and sinks whose ids and ends are the instance. A link merges two identities of one instance; every step re-keys through the master's patch.</summary>
    public static readonly World LinkedUnionJoin = LinkedWith("a view that unions, groups and joins by an instance that a link merges", "Link, Match, Union, Group, Join, EmitEntity, EmitRelationship in a view") with { Witnesses = ["d:w.view/prop:calls=", "d:w.view/prop:name=", "rel "] };

    public static readonly World LinkedLoads = LinkedLoadsUnder("delta sum");
    /// <summary>The instances and calls of `link`, with names that change: a withdrawn link splits a class, and what was written under a key of the part without the old id moves to the part's id.</summary>
    public static readonly World LinkWithdrawn = new World("a link withdrawn", "Link withdrawn, EmitRelationship", Files("link"), [Renamed, Flows]) with { Witnesses = ["rel "] };

    /// <summary>A view reads the load windows of the instances through a series. A link merges two identities of one instance and moves their windows; the view has the survivor's window in the step of the merge (code review 2, defect 22).</summary>
    public static readonly World LinkedSeries = new("a view's series over an instance that a link merges", "Link, Series, Rollup in a view", Files("a view's series over an instance that a link merges"), LinkedLoads.Sources, LinkedLoads.AdvanceShare) { Witnesses = ["d:w.view/prop:load "] };

    /// <summary>A view names its elements by the element it matches, so it follows a merge of the master (a key of a linked domain is refused there, `IdentityLinked`).</summary>
    public static readonly World LinkedNamed = LinkedWith("a view named by the element of a linked domain", "Link, Match, EmitEntity in a view") with { Witnesses = ["d:w.view/prop:name="] };

    const string LoadsWorld = "two groups by two keys of one class, one event property";
    /// <summary>Law 7.5 item 3, "rows after the link" (D37): the load of an instance aggregated per window once by its uid and once by its name, both writing one event property. A link merges the two identities before, between or after the rows; the window is what both groups give together, as if every row had been written under the survivor.
    /// The load property is declared `{kind}` with the temporality and combine rule `rule`; `aggregate` is the aggregate of each group: `sum` or `avg` of n, or `arg_max` for the n of the latest row.</summary>
    public static World LinkedLoadsUnder(string rule, string aggregate = "sum", string kind = "Integer")
    {
        var (temporality, combine) = (rule.Split(' ')[0], rule.Split(' ')[1]);
        var call = aggregate == "arg_max" ? """{"fn": "arg_max", "args": [{"ref": "t"}, {"ref": "n"}]}""" : $$"""{"fn": "{{aggregate}}", "args": [{"ref": "n"}]}""";
        var files = Files(LoadsWorld).Select(f => f with
        {
            Text = f.Text
                .Replace("""{"name": "load", "type": "Integer", "optional": true, "events": {"temporality": "delta", "combine": "sum", "period": "1m"}}""",
                    $$$"""{"name": "load", "type": "{{{kind}}}", "optional": true, "events": {"temporality": "{{{temporality}}}", "combine": "{{{combine}}}", "period": "1m"}}""", StringComparison.Ordinal)
                .Replace("""{"c": {"fn": "sum", "args": [{"ref": "n"}]}}""", "{\"c\": " + call + "}", StringComparison.Ordinal),
        }).ToList();
        return new(rule == "delta sum" && aggregate == "sum" ? LoadsWorld : $"{LoadsWorld} ({rule}, {aggregate})", "Link, Window, Group, EmitEntity windows", files,
        [Instances, Flows, new("loads", (r, now) => { var i = r.Next(4); var byUid = r.Next(2) == 0; return Row(("uid", byUid ? Value.Text("u" + i) : Value.Null), ("name", byUid ? Value.Null : Value.Text(InstanceNames[i])), ("n", Value.Integer(r.Next(1, 4))), ("t", Near(r, now))); })], AdvanceShare: 0.2)
        { Witnesses = ["prop:load"] };
    }

    // ---- worlds outside `All`: their inputs are written out, not generated ----

    public static readonly World FilteredEmit = Rows("filtered emit", "Filter, EmitEntity");
    public static readonly World OptionalProperty = Rows("optional property", "EmitEntity") with { Witnesses = ["prop:d="] };
    /// <summary>A fold whose look-back (10 minutes) is shorter than the logs: a window leaves the series six windows after it closes (a window and its late horizon of three take four), and the fold forgets it (#151 D6): the key starts again at its first window still inside the look-back.</summary>
    /// <summary>#159 G2 and #151 D1: a fold by the group `g` of the items, straight over their series, so two items of one group give two rows for one key and window.</summary>
    public static readonly World SharedKeyThreshold = View("threshold by a key two items share", "Series, Fold", 0.3, Dense) with { Witnesses = ["state=t:off"] };
    public static readonly World Hysteresis = View("threshold over a short look-back", "Series, Rollup, Fold", 0.45, Dense) with { Witnesses = ["state=t:on", "state=t:off"] };
    /// <summary>T-B4 (premise B4 of Theorem 6.2): a fold over a roll-up by group, whose members change group (a keyed item row is replaced with another group, so the item leaves one key and joins another), with windows that leave the retained range of five minutes (the look-back; a window and its late horizon of three take four). Every advance is its own step in the engine and in the reference, so an advance and a structure change never share one; the harness cuts the rows between them every way.</summary>
    public static readonly World MembersLeave = View("threshold over a roll-up whose members leave", "Series, Rollup, Fold, structure change", 0.3, [
            new("a", (r, now) => Row(("k", Text(r, "k", 3)), ("g", Text(r, "g", 2)), ("n", Value.Integer(1)), ("d", Value.Null), ("t", Value.Instant(now))), f => ((TextValue)f["k"]).S),
            Dense[1], Dense[2],
        ]) with { RetentionNs = 5 * Minute, Witnesses = ["state=t:on", "state=t:off"] };
    /// <summary>Def. 7.15 on one element and the fold's silence (#151 D3): `hits` are filled with 0 where the source of the hits has reported and the item existed, so a covered silence ends a state; a window without a record or a fill is silent, `gap` of them in a row (the policy: 3 for a time to live of three minutes, 2 for two) make the key "silent", and data that return leave it. The source of the hits reports (adapter harness.reporting): an authored source would not, and its fill is refused.</summary>
    static World Silence(string name, string kind) => View(name, kind, 0.3, Dense) with { Reporting = ["b"], ProgressShare = 0.15, Witnesses = ["state=t:on", "state=t:off", "state=t:silent"] };
    public static readonly World Fill = Silence("fill and no data", "Series with fill and ttl, Map, Fold");
    /// <summary>Def. 7.14: a window in which the source lost events is not covered, so it holds no fill; a loss mark that arrives after the window was filled takes the fill back.</summary>
    public static readonly World FillAroundLoss = Silence("fill around a loss", "Series with fill and ttl, loss marks, Map, Fold") with { LossShare = 0.12 };
    /// <summary>#111: fill and no data over hop windows: the hits of an item in windows of 3 minutes every 60 s, read with a fill and a time to live, with gaps and loss marks. A filled window is one the source covered to its end, so a fill is neither early nor doubled.</summary>
    public static readonly World FillOverHop = Silence("fill and no data over hop windows", "Window with a hop, Series with fill and ttl, loss marks, Fold") with { LossShare = 0.12 };
    public static readonly World TimeToLive = Silence("no data without a fill", "Series with ttl, Map, Fold") with { AdvanceShare = 0.35 };
    public static readonly World RollupAll = Silence("roll-up in no data when all members are", "Series with fill and ttl, Rollup, Fold");

    /// <summary>#151 D4: a fold by the group of the items whose policy rows are the items, `above` the item's `n` and `enterAfter` n - 1, with a constant `below` of 1: a revised item is a new policy row, which holds from the windows that close after it entered, and two items of one group are two rows even with one policy. The signal shows the `since` of its newest transition.</summary>
    public static readonly World RowParameters = View("threshold with parameters of the row", "Series, Map, Fold", 0.45,
        [
            new("a", (r, now) => Row(("k", Text(r, "k", 2)), ("g", Value.Text("g0")), ("n", Value.Integer(r.Next(4))), ("d", Value.Null), ("t", Value.Instant(now))), f => ((TextValue)f["k"]).S),
            .. Dense.Skip(1),
        ]) with { Witnesses = ["state=t:on", "state=t:off"] };
    /// <summary>Items whose keys differ only in the case of a letter: two items, one signal (its key normalized to lower case), so two rows support one window of the signal.</summary>
    public static readonly World SharedWindow = View("shared window", "Series, Rollup, EmitEntity", 0.3,
        [
            new("a", (r, now) => Row(("k", Pick(r, Value.Text("k0"), Value.Text("K0"), Value.Text("k1"), Value.Text("K1"))), ("g", Value.Text("g0")), ("n", Value.Integer(1)), ("d", Value.Null), ("t", Value.Instant(now))), f => ((TextValue)f["k"]).S),
            new("b", (r, now) => Row(("k", Pick(r, Value.Text("k0"), Value.Text("K0"), Value.Text("k1"), Value.Text("K1"))), ("g", Value.Text("g0")), ("n", Value.Integer(r.Next(5))), ("d", Value.Null), ("t", Value.Instant(now + r.Next(-6, 2) * 30 * Second)))),
        ]) with { Witnesses = ["d:w.view/prop:v "] };

    // ---- items in groups: edges between items lifted to the groups ----

    static readonly IReadOnlyList<SourceSpec> ItemsAndEdges = [new("items", (r, _) => Row(("k", Text(r, "k", 5)), ("g", Text(r, "g", 3))), f => ((TextValue)f["k"]).S), BagEdges];

    /// <summary>The shapes of a dependency model: item edges lifted to the groups the items belong to (communication), and a dependency between groups derived from the same edges by joins with a table of the groups that count and with the groups themselves (Match, Values, Join, Distinct, EmitRelationship).</summary>
    public static readonly World LiftedDependency = new("lifted and derived dependency", "Match, Values, Join, Distinct, EmitRelationship", Files("lifted and derived dependency"), ItemsAndEdges) { Witnesses = ["Talks", "Needs"] };

    /// <summary>References: the graph `groups` holds the groups and which item belongs to which, and no item; the items are entities of the master. A pattern over `groups` binds the item end of a `Belongs` by its id, and the master's edges are mapped through those `Belongs` by two joins, whose match over `groups` binds the same way.</summary>
    public static readonly World References = new("ends of relationships that are entities of another graph", "Match and Join over references", Files("ends of relationships that are entities of another graph"), ItemsAndEdges) { Witnesses = ["Talks", "Seen"] };

    /// <summary>Two sinks of one relationship type between the same ends, under two keys (first, second): two relationships.</summary>
    public static readonly World TwoKeyRules = new("two key rules", "EmitRelationship, Def. 6.4", Files("two key rules"), [BagEdges, BagEdges with { Name = "more" }]) { Witnesses = ["rel "] };

    /// <summary>A share rule (a unit by the share of its members, no data ranked between good and degraded): members are rows of `a` (key `k`, unit `g`, severity `n` capped at 3), retracted and changed by the log.</summary>
    public static readonly World ShareRule = Rows("share rule of a unit", "Map, Group, Map, EmitEntity") with { Witnesses = ["prop:own=n:0", "prop:own=n:1", "prop:own=n:2", "prop:own=n:3"] };

    /// <summary>#112: a world whose code numbers depend on how the log is cut. A `where` drops items, so the texts only they carry (their tag, a key or group seen first in a dropped row) are freed at the end of their step, and texts that come later take the freed numbers, the smallest first. A traverse walks its starts in code order: an output that depended on the numbers would differ between cuts and from the reference.</summary>
    public static readonly World TraverseAfterWhere = View("traverse over items a where thins", "Filter, Map, Traverse", 0, [Keyed("a"), Edges]) with { Witnesses = ["Reach"] };

    // ---- traversals: the reach of each item over its edges, one `Reach` per pair, named by both keys ----

    /// <summary>Items k0 to k4 and the edges between them: room for a chain of four edges and a short cut over it.</summary>
    static readonly IReadOnlyList<SourceSpec> FiveItems =
    [
        new("a", (r, now) => Row(("k", Text(r, "k", 5)), ("g", Text(r, "g", 3)), ("n", Value.Integer(r.Next(6))), ("d", Value.Null), ("t", Near(r, now))), f => ((TextValue)f["k"]).S),
        FiveEdges("edges"), FiveEdges("edges"),
    ];
    static SourceSpec FiveEdges(string name) => new(name, (r, _) => Row(("src", Text(r, "k", 5)), ("dst", Text(r, "k", 5))), f => ((TextValue)f["src"]).S + ">" + ((TextValue)f["dst"]).S);

    /// <summary>#113: a traverse from depth 1 to 3. An added edge lowers depths from its far end, possibly several hops on, and a cycle back to the start does not reach the start (its depth is 0, below the minimum). Two added edges of one patch, or an added and a lost edge, meet in one step when a cut puts them in one batch.</summary>
    public static readonly World TraverseOneToThree = View("traverse from depth 1 to 3", "Traverse", 0.1, FiveItems) with { Witnesses = ["Reach"] };
    /// <summary>#113: a traverse against the direction of the edges: an item reaches the items with edges into it.</summary>
    public static readonly World TraverseIn = View("traverse in", "Traverse", 0.1, FiveItems) with { Witnesses = ["Reach"] };
    /// <summary>#115: a join on the start of a traverse, which the engine runs below the traverse (`PlanCompiler.JoinsBelowTraverses`). The other side are the out-edges of the items whose `n` is above 3, so an item's rows add and remove its start rows, one per out-edge (a start joined twice is walked twice), and the edges change both the walks and the other side.</summary>
    public static readonly World JoinOnTraverseStart = View("join on the start of a traverse", "Traverse, Map, Join", 0.1, FiveItems) with { Witnesses = ["Reach"] };
    /// <summary>#115: the same with the traverse on the right of the join, from depth 1.</summary>
    public static readonly World JoinOnTraverseStartRight = View("join on the start of a traverse on its right", "Traverse, Map, Join", 0.1, FiveItems) with { Witnesses = ["Reach"] };
    /// <summary>#113: a traverse over `Edge` follows `Fast`, a sub-type of `Edge`, and not `Other`, a relationship type of its own between the same items.</summary>
    public static readonly World TraverseSubtype = new("traverse over a sub-type, not over another type", "Traverse", Files("traverse over a sub-type, not over another type"),
        [FiveItems[0], FiveEdges("edges"), FiveEdges("fast"), FiveEdges("other")], 0.1) { Witnesses = ["rel ", "Reach"] };

    public static readonly IReadOnlyList<World> All =
    [
        TraverseAfterWhere,
        Rows("filter", "Filter"),
        Rows("map", "Map"),
        Rows("union", "Union"),
        Rows("distinct", "Distinct"),
        Rows("distinct over a decimal", "Distinct"),
        Rows("join", "Join") with { Witnesses = ["+k"] },
        Rows("self join", "Join") with { Witnesses = ["+k"] },
        Rows("two joins on one input", "Join") with { Witnesses = ["+k", "-k"] },
        Rows("join on a decimal key", "Join"),
        Rows("values", "Values") with { Witnesses = ["g=t:L1"] },
        Rows("retain", "Retain", advanceShare: 0.25),
        Aggregate("count"),
        Aggregate("countDistinct"),
        Aggregate("sum"),
        Aggregate("min and max") with { Witnesses = ["hi=n:", "top=n:"] },
        Percentile,
        Fixpoint,
        RollupOfThreeLevels,
        Aggregate("mean"),
        Aggregate("last of one row") with { Witnesses = ["v=t:k", "prop:x=n:"] },
        Aggregate("first of one row") with { Witnesses = ["v=t:k", "prop:x=n:"] },
        Aggregate("countDistinct over a decimal"),
        Rows("group by a decimal key", "Group"),
        Aggregate("count with countDistinct"),
        Aggregate("extremes with countDistinct") with { Witnesses = ["lo=n:", "hi=n:"] },
        Aggregate("countif"),
        Windowed with { Witnesses = ["window "] },
        LatenessPerInput,
        Windowed with { Name = "window late revise, retention 2 minutes", Kind = "Window, window retention", RetentionNs = 2 * Minute, Witnesses = ["window "],
            Files = [.. Windowed.Files.Select(f => f with { Text = f.Text.Replace("\"lateness\": \"3m\"", "\"lateness\": \"1m\"", StringComparison.Ordinal) })] },   // a lateness within the retention (#169 D1)
        Hopped with { Witnesses = ["window "] },
        HopOverWindows with { Witnesses = ["window "] },
        Shifted with { Witnesses = ["window "] },
        new World("link", "Link, EmitRelationship", Files("link"), [Instances, Flows]) with { Witnesses = ["prop:name=t:web,d:w/prop:uid=t:u0", "rel "] },
        LinkedLoads,
        LinkWithdrawn,
        LinkedLoads with { Name = LoadsWorld + " (a link withdrawn)", Sources = [Renamed, .. LinkedLoads.Sources.Skip(1)] },
        LinkedSeries,
        LinkedView,
        LinkedUnionJoin,
        LinkedNamed,
        References,
        ShareRule,
        MembersLeave,
        View("match nodes", "Match, EmitEntity"),
        View("match edges", "Match") with { Witnesses = ["Pair"] },
        View("traverse", "Traverse") with { Witnesses = ["Reach"] },
        View("traverse either way", "Traverse") with { Witnesses = ["Reach"] },
        TraverseOneToThree, TraverseIn, TraverseSubtype, JoinOnTraverseStart, JoinOnTraverseStartRight,
        View("emit update", "EmitEntity update") with { Witnesses = ["degree=n:"] },
        View("series and rollup", "Series, Rollup", 0.25) with { Witnesses = ["d:w.view/prop:v"] },
        Hysteresis, SharedKeyThreshold, RowParameters, SharedWindow, Fill, FillAroundLoss, FillOverHop, TimeToLive, RollupAll, TwoKeyRules, LiftedDependency, OptionalProperty,
        Rows("conflicting fragments", "EmitEntity, Def. 6.4") with { Witnesses = ["prop:g=", "prop:n="] },
        View("threshold", "Series, Rollup, Fold", 0.45, Dense) with { Witnesses = ["state=t:on", "state=t:off"] },
    ];
}
