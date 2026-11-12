using System.Collections.Concurrent;
using Sattle.Core;
using Sattle.Language;
using static Sattle.Differential.Logs;

namespace Sattle.Differential;

/// <summary>A small graph space that exercises one operator kind (or a small composition), with the sources its logs are generated for.</summary>
public sealed record World(string Name, string Kind, IReadOnlyList<SourceFile> Files, IReadOnlyList<SourceSpec> Sources, double AdvanceShare = 0)
{
    /// <summary>Text that some reference result of the default seeds must contain, so a world that never exercises its operator fails.</summary>
    public IReadOnlyList<string> Witnesses { get; init; } = [];
    /// <summary>The telemetry retention of the space (Def. 7.13), for the engine and the reference alike; the default is 48 hours.</summary>
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

    // ---- one ingestion module: rows in, the operator under test, a count per row out ----

    const string R = "rows R { k: Text; g: Text; n: Integer; d?: Decimal; t: Instant; }";
    const string Counted = """
            let o = x |> group by { k: k, g: g } aggregate { c: count(), s: sum(n) };
            emit entity Out from o identity out(k: k, g: g) set { k: k, g: g, c: c, s: s };
        """;
    const string OutType = """
        entity Out { k: Text; g: Text; c: Integer; s: Integer; }
        identity out for Out version "1" { k: Text by exact; g: Text by exact; }
        """;

    static World Rows(string name, string kind, string body, string types = OutType, double advanceShare = 0, IReadOnlyList<SourceSpec>? sources = null, string tail = Counted) => new(name, kind,
        [new SourceFile("w.sattle", $$"""
            module w @ 1.0.0;
            {{types}}
            {{R}}
            projection ingest ingestion output w {
              in a: rows R;
              in b: rows R;
              rule observed readonly "differential" {
            {{body}}
            {{tail}}
              }
            }
            space s {
              source a adapter "sattle.authoring" rows R;
              source b adapter "sattle.authoring" rows R;
              master graph master = ingest(a: a, b: b);
            }
            """)], sources ?? [A, B], advanceShare);

    static World Aggregate(string name, string aggregates, string properties, string sets) => Rows("group " + name, "Group",
        $"    let o = a |> group by {{ g: g }} aggregate {{ {aggregates} }};", $$"""
        entity Agg { g: Text; {{properties}} }
        identity agg for Agg version "1" { g: Text by exact; }
        """, tail: $"    emit entity Agg from o identity agg(g: g) set {{ g: g, {sets} }};");

    static World Windowed(string late) => Rows("window late " + late, "Window, Group over windows", $$"""
            let w = a |> window on t width 60s late {{late}};
            let p = w |> group by { k: k, windowStart: windowStart } aggregate { hits: count(), total: sum(n) };
        """, """
        entity Meter { k: Text; hits?: Integer telemetry delta sum every 60s; total?: Integer telemetry delta sum every 60s; }
        identity meter for Meter version "1" { k: Text by exact; }
        """, 0.25, tail: "    emit entity Meter from p identity meter(k: k) set { k: k, hits: hits, total: total };");

    // ---- a master graph and a view over it ----

    public const string BaseModule = """
        module w.base @ 1.0.0;
        entity Item { k: Text; g: Text; n: Integer; hits?: Integer telemetry delta sum every 60s; }
        relationship Edge (Item) -> (Item);
        identity item for Item version "1" { k: Text by exact; }
        rows R { k: Text; g: Text; n: Integer; d?: Decimal; t: Instant; }
        rows EdgeRow { src: Text; dst: Text; }
        projection ingest ingestion output w.base {
          in a: rows R;
          in b: rows R;
          in edges: rows EdgeRow;
          rule observed readonly "differential" {
            emit entity Item from a identity item(k: k) set { k: k, g: g, n: n };
            emit relationship Edge from edges key edge: item(k: src) -> item(k: dst);
            let w = b |> window on t width 60s late revise;
            let p = w |> group by { k: k, windowStart: windowStart } aggregate { hits: sum(n) };
            emit update entity Item from p identity item(k: k) set { hits: hits };
          }
        }
        """;

    /// <summary>Two items and hits in the last few windows, so consecutive windows of one key have values.</summary>
    static readonly IReadOnlyList<SourceSpec> Dense =
    [
        new("a", (r, now) => Row(("k", Text(r, "k", 2)), ("g", Value.Text("g0")), ("n", Value.Integer(1)), ("d", Value.Null), ("t", Value.Instant(now))), f => ((TextValue)f["k"]).S),
        new("b", (r, now) => Row(("k", Text(r, "k", 2)), ("g", Value.Text("g0")), ("n", Value.Integer(r.Next(5))), ("d", Value.Null), ("t", Value.Instant(now + r.Next(-6, 2) * 30 * Second)))),
        new("b", (r, now) => Row(("k", Text(r, "k", 2)), ("g", Value.Text("g0")), ("n", Value.Integer(r.Next(5))), ("d", Value.Null), ("t", Value.Instant(now + r.Next(-6, 2) * 30 * Second)))),
    ];

    static World View(string name, string kind, string types, string body, double advanceShare = 0, IReadOnlyList<SourceSpec>? sources = null) => new(name, kind,
        [new SourceFile("w.base.sattle", BaseModule), new SourceFile("w.view.sattle", $$"""
            module w.view @ 1.0.0;
            import w.base @ 1.0.0 sha256:… as base;
            {{types}}
            projection v view forward output w.view {
              in master: graph base;
            {{body}}
            }
            space s {
              source a adapter "sattle.authoring" rows base.R;
              source b adapter "sattle.authoring" rows base.R;
              source edges adapter "sattle.authoring" rows base.EdgeRow;
              master graph master = base.ingest(a: a, b: b, edges: edges);
              graph view = v(master: master);
            }
            """)], sources ?? [Keyed("a"), B, Edges], advanceShare);

    const string Signal = """
        entity Signal { k: Text; state?: Text; v?: Integer telemetry delta sum every 60s; }
        identity signal for Signal version "1" { k: Text by exact; }
        """;

    static string Fixpoint(int max) => $$"""
        module w @ 1.0.0;
        entity Pair { a: Text; b: Text; }
        identity pair for Pair version "1" { a: Text by exact; b: Text by exact; }
        rows EdgeRow { src: Text; dst: Text; }
        projection ingest ingestion output w {
          in edges: rows EdgeRow;
          rule observed readonly "differential" {
            let base = edges |> select { a: src, b: dst };
            let step = base |> select { x: a, y: b };
            let closure = fixpoint base
              via join closure, step on b == x
                |> select { a: a, b: y }
              max {{max}};
            emit entity Pair from closure identity pair(a: a, b: b) set { a: a, b: b };
          }
        }
        space s {
          source edges adapter "sattle.authoring" rows EdgeRow;
          master graph master = ingest(edges: edges);
        }
        """;

    const string Linked = """
        module w @ 1.0.0;
        entity Pod { uid?: Text; name?: Text; g?: Text; }
        relationship Calls (Pod) -> (Pod);
        identity podUid for Pod version "1" { uid: Text by exact; }
        identity podName for Pod version "1" { name: Text by lowerInvariant; }
        equivalence uidIsName between podUid, podName assert threshold 1.0;
        rows PodRow { uid: Text; name: Text; g: Text; }
        rows FlowRow { src: Text; dst: Text; }
        projection ingest ingestion output w {
          in pods: rows PodRow;
          in flows: rows FlowRow;
          rule observed readonly "differential" {
            emit entity Pod from pods identity podUid(uid: uid) set { uid: uid, g: g };
            link uidIsName from pods left podUid(uid: uid) right podName(name: name) confidence 1.0;
            let named = flows |> select { name: src } |> distinct;
            emit entity Pod from named identity podName(name: name) set { name: lowerInvariant(name) };
            emit relationship Calls from flows key calls: podName(name: src) -> podName(name: dst);
          }
        }
        space s {
          source pods adapter "sattle.authoring" rows PodRow;
          source flows adapter "sattle.authoring" rows FlowRow;
          master graph master = ingest(pods: pods, flows: flows);
        }
        """;

    static readonly string[] PodNames = ["Web", "api", "DB", "cache"];
    static readonly SourceSpec Pods = new("pods", (r, _) => { var i = r.Next(4); return Row(("uid", Value.Text("u" + i)), ("name", Value.Text(PodNames[i])), ("g", Text(r, "g", 2))); }, f => ((TextValue)f["uid"]).S);
    static readonly SourceSpec Flows = new("flows", (r, _) => Row(("src", Value.Text(PodNames[r.Next(4)].ToLowerInvariant())), ("dst", Value.Text(PodNames[r.Next(4)]))));

    /// <summary>Law 7.5 item 3, "rows after the link" (D37): the load of a pod summed per window once by its uid and once by its name, both writing one telemetry property. A link merges the two identities before, between or after the rows; the window is the sum of both groups, as if every row had been written under the survivor.</summary>
    /// <summary>T-B3: a view groups the calls of each pod by the pod it matches. A link in the master merges two identities of one pod; the view's group re-keys through the master's patch, so its count is that of one group over both keys (Theorem 6.1 under identity, the stated way to group by an identity under rule IdOf-in-sinks).</summary>
    public static readonly World LinkedView = new("a view grouped by a pod that a link merges", "Link, Match, Group, EmitEntity in a view", [
        new SourceFile("w.sattle", Linked[..Linked.IndexOf("space s {", StringComparison.Ordinal)]),
        new SourceFile("w.view.sattle", """
            module w.view @ 1.0.0;
            import w @ 1.0.0 sha256:… as base;
            entity Caller { calls: Integer; }
            projection v view forward output w.view {
              in master: graph base;
              let calls = match master (p: base.Pod)-[:base.Calls]->(q: base.Pod) |> group by { p: p } aggregate { c: count() };
              emit entity Caller from calls identity p set { calls: c };
            }
            space s {
              source pods adapter "sattle.authoring" rows base.PodRow;
              source flows adapter "sattle.authoring" rows base.FlowRow;
              master graph master = base.ingest(pods: pods, flows: flows);
              graph view = v(master: master);
            }
            """)], [Pods, Flows]) { Witnesses = ["d:w.view/prop:calls="] };

    public static readonly World LinkedLoads = LinkedLoadsUnder("delta sum");

    /// <summary>The world of `LinkedLoads` with the load property declared `{kind} telemetry {rule} every 60s` (a temporality and a combine rule), each group aggregating `aggregate`.</summary>
    public static World LinkedLoadsUnder(string rule, string aggregate = "sum(n)", string kind = "Integer") => new(rule == "delta sum" && aggregate == "sum(n)" ? "two groups by two keys of one class, one telemetry property" : $"two groups by two keys of one class, one telemetry property ({rule}, {aggregate})", "Link, Window, Group, EmitEntity telemetry", [new SourceFile("w.sattle", Linked
        .Replace("entity Pod { uid?: Text; name?: Text; g?: Text; }", $"entity Pod {{ uid?: Text; name?: Text; g?: Text; load?: {kind} telemetry {rule} every 60s; }}")
        .Replace("rows FlowRow { src: Text; dst: Text; }", "rows FlowRow { src: Text; dst: Text; }\nrows LoadRow { uid?: Text; name?: Text; n: Integer; t: Instant; }")
        .Replace("  in flows: rows FlowRow;", "  in flows: rows FlowRow;\n  in loads: rows LoadRow;")
        .Replace("emit relationship Calls from flows key calls: podName(name: src) -> podName(name: dst);", """
            emit relationship Calls from flows key calls: podName(name: src) -> podName(name: dst);
                let loadWindows = loads |> window on t width 60s late revise;
                let byUid = loadWindows |> where isPresent(uid) |> group by { uid: uid, windowStart: windowStart } aggregate { c: AGGREGATE };
                emit update entity Pod from byUid identity podUid(uid: uid) set { load: c };
                let byName = loadWindows |> where isPresent(name) |> group by { name: name, windowStart: windowStart } aggregate { c: AGGREGATE };
                emit update entity Pod from byName identity podName(name: name) set { load: c };
            """)
        .Replace("  source flows adapter \"sattle.authoring\" rows FlowRow;", "  source flows adapter \"sattle.authoring\" rows FlowRow;\n  source loads adapter \"sattle.authoring\" rows LoadRow;")
        .Replace("master graph master = ingest(pods: pods, flows: flows);", "master graph master = ingest(pods: pods, flows: flows, loads: loads);")
        .Replace("AGGREGATE", aggregate))],
        [Pods, Flows, new("loads", (r, now) => { var i = r.Next(4); var byUid = r.Next(2) == 0; return Row(("uid", byUid ? Value.Text("u" + i) : Value.Null), ("name", byUid ? Value.Null : Value.Text(PodNames[i])), ("n", Value.Integer(r.Next(1, 4))), ("t", Near(r, now))); })], AdvanceShare: 0.2)
    { Witnesses = ["prop:load"] };

    // ---- worlds of the known-defect tests (their inputs are written out, not generated) ----

    const string OneEntity = """
        entity E { k: Text; d?: Decimal; }
        identity e for E version "1" { k: Text by exact; }
        """;
    public static readonly World FilteredEmit = Rows("filtered emit", "Filter, EmitEntity", "    let x = a |> where n > 2;", OneEntity, tail: "    emit entity E from x identity e(k: k) set { k: k };");
    public static readonly World OptionalProperty = Rows("optional property", "EmitEntity", "", OneEntity, tail: "    emit entity E from a identity e(k: k) set { k: k, d: d };");
    public static readonly World TextOrder = Rows("text order", "Filter", "    let x = a |> where k < g;");
    /// <summary>A threshold whose look-back (3 minutes) is shorter than the logs: the state has to outlive the windows that set it.</summary>
    public static readonly World Hysteresis = View("threshold over a short look-back", "Series, Rollup, Threshold", Signal, """
          let m = match master (i: base.Item);
          let s = m |> series master i.hits within 3m |> rollup by { c: i.k } combine { v: sum(hits) };
          let st = s |> threshold v by { c: c } above 3 for 2 then "on" below 1 for 2 then "off" initial "off" as state;
          emit entity Signal from st identity signal(k: c) set { k: c, state: state };
        """, 0.3, Dense) with { Witnesses = ["state=t:on", "state=t:off"] };
    /// <summary>Def. 7.15 and 7.16 on one element: `hits` are filled with 0 where the source of the hits has reported and the item existed, so a covered silence ends a state; an item without a record for three minutes is in no data, which the threshold gives as "silent", and data that return end it.</summary>
    static World Silence(string name, string kind, string series) => View(name, kind, Signal, $$"""
          let m = match master (i: base.Item);
          let s = m |> {{series}};
          let st = s |> threshold v by { c: c } above 3 for 1 then "on" below 1 for 2 then "off" initial "off" nodata then "silent" as state;
          emit entity Signal from st identity signal(k: c) set { k: c, state: state };
        """, 0.3, Dense) with { Reporting = ["b"], ProgressShare = 0.15, Witnesses = ["state=t:on", "state=t:off", "state=t:silent"] } is var w
        ? w with { Files = [.. w.Files.Select(f => f with { Text = f.Text.Replace("source b adapter \"sattle.authoring\"", "source b adapter \"harness.reporting\"", StringComparison.Ordinal) })] } : null!;       // the source of the hits reports: an authored source would not, and its fill is refused
    public static readonly World Fill = Silence("fill and no data", "Series with fill and ttl, Map, Threshold",
        "series master i.hits fill 0 within 5m ttl 3m |> select { c: i.k, windowStart: windowStart, v: hits, noData: noData }");
    public static readonly World TimeToLive = Silence("no data without a fill", "Series with ttl, Map, Threshold",
        "series master i.hits within 5m ttl 2m |> select { c: i.k, windowStart: windowStart, v: hits, noData: noData }");
    public static readonly World RollupAll = Silence("roll-up in no data when all members are", "Series with fill and ttl, Rollup, Threshold",
        "series master i.hits fill 0 within 5m ttl 2m |> rollup by { c: i.g } combine { v: sum(hits) }");
    /// <summary>Def. 7.14: a window in which the source lost events is not covered, so it holds no fill; a loss mark that arrives after the window was filled takes the fill back.</summary>
    public static readonly World FillAroundLoss = Silence("fill around a loss", "Series with fill and ttl, loss marks, Map, Threshold",
        "series master i.hits fill 0 within 5m ttl 3m |> select { c: i.k, windowStart: windowStart, v: hits, noData: noData }") with { LossShare = 0.12 };
    public static readonly World RollupAny = Silence("roll-up in no data when one member is", "Series with ttl, Rollup, Threshold",
        "series master i.hits within 5m ttl 2m |> rollup by { c: i.g } combine { v: sum(hits) } nodata any");

    /// <summary>A threshold whose `above` and `enterAfter` come from the row (the item's `n`), with a constant `below` of 1: an item with n below 2 gives parameters that are no state machine, and a revised item changes the parameters of every window of its key.</summary>
    public static readonly World RowParameters = View("threshold with parameters of the row", "Series, Map, Threshold", Signal, """
          let m = match master (i: base.Item);
          let s = m |> series master i.hits within 3m |> select { c: i.k, windowStart: windowStart, v: hits, hi: i.n, run: i.n - 1 };
          let st = s |> threshold v by { c: c } above hi for run then "on" below 1 for 2 then "off" initial "off" as state;
          emit entity Signal from st identity signal(k: c) set { k: c, state: state };
        """, 0.3,
        [
            new("a", (r, now) => Row(("k", Text(r, "k", 2)), ("g", Value.Text("g0")), ("n", Value.Integer(r.Next(4))), ("d", Value.Null), ("t", Value.Instant(now))), f => ((TextValue)f["k"]).S),
            .. Dense.Skip(1),
        ]) with { Witnesses = ["state=t:on", "state=t:off"] };
    public static readonly World SharedWindow = View("shared window", "Series, Rollup, EmitEntity", """
        entity Signal { k: Text; v?: Integer telemetry delta sum every 60s; }
        identity signal for Signal version "1" { k: Text by lowerInvariant; }
        """, """
          let m = match master (i: base.Item);
          let s = m |> series master i.hits within 30m |> rollup by { c: i.k } combine { v: sum(hits) };
          emit entity Signal from s identity signal(k: c) set { k: lowerInvariant(c), v: v };
        """);

    /// <summary>The shapes of the workload model: item edges lifted to the groups the items belong to (communication), and a dependency between groups derived from the same edges by joins with a table of the groups that count and with the groups themselves (Match, Values, Join, Distinct, EmitRelationship).</summary>
    public static readonly World LiftedDependency = new("lifted and derived dependency", "Lift, Match, Values, Join, Distinct, EmitRelationship", [new SourceFile("w.sattle", """
        module w @ 1.0.0;
        entity Item { k: Text label; g: Text; }
        entity Group { name: Text label; }
        relationship Edge (Item) -> (Item) kind communication;
        relationship Belongs (Item) -> (Group) kind composition;
        relationship Talks (Group) -> (Group) kind communication;
        relationship Needs (Group) -> (Group) kind dependency;
        identity item for Item version "1" { k: Text by exact; }
        identity group for Group version "1" { name: Text by exact; }
        rows ItemRow { k: Text; g: Text; }
        rows EdgeRow { src: Text; dst: Text; }
        projection ingest ingestion output w {
          in items: rows ItemRow;
          in edges: rows EdgeRow;
          rule observed readonly "differential" {
            emit entity Item from items identity item(k: k) set { k: k, g: g };
            emit entity Group from items identity group(name: g) set { name: g };
            emit relationship Belongs from items key belongs: item(k: k) -> group(name: g);
            emit relationship Edge from edges key edge: item(k: src) -> item(k: dst);
          }
        }
        projection groups view forward output w {
          in master: graph w;
          let all = match master (g: Group);
          emit entity Group from all identity g set { name: g.name };
          lift talks as Talks from master Edge via master Belongs images each;
          let reached = match master (a: Item)-[:Edge]->(b: Item), (a)-[:Belongs]->(ga: Group), (b)-[:Belongs]->(gb: Group) |> select { from: ga.name, to: gb.name };
          let counting = values { counted: "g0" }, { counted: "g1" };
          let needed = join reached, counting on to == counted;
          let pairs = needed |> where from != to |> select { from: from, to: to } |> distinct;
          let sources = all |> select { s: g, sn: g.name };
          let targets = all |> select { t: g, tn: g.name };
          let fromSource = join pairs, sources on from == sn;
          let both = join fromSource, targets on to == tn;
          emit relationship Needs from both key needs: s -> t;
        }
        space s {
          source items adapter "sattle.authoring" rows ItemRow;
          source edges adapter "sattle.authoring" rows EdgeRow;
          master graph master = ingest(items: items, edges: edges);
          graph view = groups(master: master);
        }
        """)], [new("items", (r, _) => Row(("k", Text(r, "k", 5)), ("g", Text(r, "g", 3))), f => ((TextValue)f["k"]).S), BagEdges]) { Witnesses = ["Talks", "Needs"] };

    public static readonly World TwoKeyRules = new("two key rules", "EmitRelationship, Def. 6.4", [new SourceFile("w.sattle", """
        module w @ 1.0.0;
        entity Item { k?: Text; }
        relationship Edge (Item) -> (Item);
        identity item for Item version "1" { k: Text by exact; }
        rows EdgeRow { src: Text; dst: Text; }
        projection ingest ingestion output w {
          in edges: rows EdgeRow;
          in more: rows EdgeRow;
          rule observed readonly "differential" {
            emit relationship Edge from edges key first: item(k: src) -> item(k: dst);
            emit relationship Edge from more key second: item(k: src) -> item(k: dst);
          }
        }
        space s {
          source edges adapter "sattle.authoring" rows EdgeRow;
          source more adapter "sattle.authoring" rows EdgeRow;
          master graph master = ingest(edges: edges, more: more);
        }
        """)], [BagEdges, BagEdges with { Name = "more" }]) { Witnesses = ["rel "] };

    /// <summary>The share rule of acme.workload (a deployment by the share of its pods, no data ranked between healthy and degraded), with the statements of the model: pods are rows of `a` (key `k`, deployment `g`, severity `n` capped at 3), retracted and changed by the log.</summary>
    public static readonly World ShareRule = Rows("share rule of a deployment", "Map, Group, Map, EmitEntity",
        """
            let podsOfDeployment = a |> select { dk: g, pk2: k, own: if(n > 3, 3, n) };
            let shares = podsOfDeployment |> group by { dk: dk } aggregate { n: count(), bad: sum(if(own >= 3, 1, 0)), weak: sum(if(own >= 2, 1, 0)), silent: sum(if(own >= 1, 1, 0)) };
            let byShare = shares |> select { k: dk, own: if(bad * 2 >= n, 3, if(weak * 2 >= n, 2, if(silent * 2 >= n, 1, 0))) };
        """, """
        entity Deployment { k: Text; own: Integer; }
        identity deployment for Deployment version "1" { k: Text by exact; }
        """, tail: "    emit entity Deployment from byShare identity deployment(k: k) set { k: k, own: own };") with { Witnesses = ["prop:own=n:0", "prop:own=n:1", "prop:own=n:2", "prop:own=n:3"] };

    public static readonly IReadOnlyList<World> All =
    [
        Rows("filter", "Filter", "    let x = a |> where n > 2 and g != \"g1\";"),
        Rows("map", "Map", "    let x = a |> select { k: concat(k, \"/\", g), g: lowerInvariant(g), n: n * 2 + 1 };"),
        Rows("union", "Union", "    let x = union a, b;"),
        Rows("distinct", "Distinct", "    let u = union a, b;\n    let x = u |> distinct;"),
        Rows("distinct over a decimal", "Distinct", "    let u = union a, b;\n    let p = u |> select { k: g, g: g, n: 1, d: d };\n    let x = p |> distinct;"),
        Rows("join", "Join", """
                let l = a |> select { k: k, g: g, n: n };
                let r = b |> select { rk: k, rg: g, rn: n };
                let j = join l, r on g == rg and n <= rn;
                let x = j |> select { k: concat(k, "+", rk), g: g, n: n + rn };
            """) with { Witnesses = ["+k"] },
        Rows("join on a decimal key", "Join", """
                let l = a |> select { k: k, g: g, n: n, d: coalesce(d, 0.0) };
                let r = b |> select { rk: k, rd: coalesce(d, 0.0) };
                let j = join l, r on d == rd;
                let x = j |> select { k: concat(k, "+", rk), g: g, n: n };
            """),
        Rows("values", "Values", """
                let v = values { label: "L1", tk: "g1" }, { label: "L2", tk: "g2" };
                let j = join a, v on g == tk;
                let x = j |> select { k: k, g: label, n: n };
            """) with { Witnesses = ["g=t:L1"] },
        Rows("retain", "Retain", "    let x = a |> retain on t for 2m;", advanceShare: 0.25),
        Aggregate("count", "v: count()", "v: Integer;", "v: v"),
        Aggregate("countDistinct", "v: countDistinct(k)", "v: Integer;", "v: v"),
        Aggregate("sum", "v: sum(n), w: sum(d)", "v: Integer; w?: Decimal;", "v: v, w: w"),
        Aggregate("min and max", "lo: min(n), hi: max(n), top: max(d)", "lo?: Integer; hi?: Integer; top?: Decimal;", "lo: lo, hi: hi, top: top") with { Witnesses = ["hi=n:", "top=n:"] },
        Aggregate("mean", "v: mean(n), w: weightedMean(n, d)", "v?: Decimal; w?: Decimal;", "v: v, w: w"),
        Aggregate("any and all", "some: any(n > 2), every: all(n > 2)", "some?: Boolean; every?: Boolean;", "some: some, every: every") with { Witnesses = ["some=b:1", "every=b:0"] },
        Aggregate("last", "v: last(k, n)", "v?: Text;", "v: v") with { Witnesses = ["v=t:k"] },
        Aggregate("countDistinct over a decimal", "v: countDistinct(d)", "v: Integer;", "v: v"),
        Rows("group by a decimal key", "Group", "    let p = a |> select { k: g, g: g, n: n, d: coalesce(d, 0.0) };\n    let q = p |> group by { k: k, g: g, d: d } aggregate { n: sum(n) };\n    let x = q |> select { k: k, g: g, n: n };"),
        Aggregate("count with countDistinct", "c: count(), v: countDistinct(k)", "c: Integer; v: Integer;", "c: c, v: v"),
        Windowed("revise") with { Witnesses = ["window "] },
        Rows("two windows of one width, two late policies", "Window, Group over windows", """
                let w1 = a |> window on t width 60s late revise;
                let p1 = w1 |> group by { k: k, windowStart: windowStart } aggregate { hits: count() };
                let w2 = b |> window on t width 60s late ignore;
                let p2 = w2 |> group by { k: k, windowStart: windowStart } aggregate { total: count() };
            """, """
            entity Meter { k: Text; hits?: Integer telemetry delta sum every 60s; total?: Integer telemetry delta sum every 60s; }
            identity meter for Meter version "1" { k: Text by exact; }
            """, 0.25, tail: "    emit entity Meter from p1 identity meter(k: k) set { k: k, hits: hits };\n    emit entity Meter from p2 identity meter(k: k) set { k: k, total: total };") with { Witnesses = ["prop:hits", "prop:total"] },
        Windowed("revise") with { Name = "window late revise, retention 2 minutes", Kind = "Window, telemetry retention", RetentionNs = 2 * Minute, Witnesses = ["window "] }, Windowed("ignore") with { Witnesses = ["window "] }, Windowed("reject") with { Witnesses = ["window "] },
        new("fixpoint max 2", "Fixpoint", [new SourceFile("w.sattle", Fixpoint(2))], [BagEdges]),
        new World("fixpoint max 8", "Fixpoint", [new SourceFile("w.sattle", Fixpoint(8))], [BagEdges]) with { Witnesses = ["Pair"] },
        new World("link", "Link, EmitRelationship", [new SourceFile("w.sattle", Linked)], [Pods, Flows]) with { Witnesses = ["prop:name=t:web,d:w/prop:uid=t:u0", "rel "] },
        LinkedLoads,
        LinkedView,
        ShareRule,
        View("match nodes", "Match, EmitEntity", """
            entity Copy { k: Text; g: Text; n: Integer; }
            identity copy for Copy version "1" { k: Text by exact; }
            """, """
              let m = match master (i: base.Item);
              emit entity Copy from m identity copy(k: i.k) set { k: i.k, g: i.g, n: i.n };
            """),
        View("match edges", "Match", """
            entity Pair { a: Text; b: Text; g: Text; }
            identity pair for Pair version "1" { a: Text by exact; b: Text by exact; }
            """, """
              let m = match master (x: base.Item)-[e: base.Edge]->(y: base.Item);
              emit entity Pair from m identity pair(a: x.k, b: y.k) set { a: x.k, b: y.k, g: y.g };
            """) with { Witnesses = ["Pair"] },
        View("traverse", "Traverse", """
            entity Reach { a: Text; b: Text; depth: Integer; g: Text; }
            identity reach for Reach version "1" { a: Text by exact; b: Text by exact; }
            """, """
              let m = match master (i: base.Item);
              let r = m |> traverse master via base.Edge out depth 0 .. 2 from i as d;
              emit entity Reach from r identity reach(a: i.k, b: d.k) set { a: i.k, b: d.k, depth: d_depth, g: d.g };
            """) with { Witnesses = ["depth=n:2", "depth=n:1"] },
        View("emit update", "EmitEntity update", """
            entity Copy { k: Text; g?: Text; degree?: Integer; }
            identity copy for Copy version "1" { k: Text by exact; }
            """, """
              let m = match master (i: base.Item);
              emit entity Copy from m identity copy(k: i.k) set { k: i.k };
              let e = match master (x: base.Item)-[r: base.Edge]->(y: base.Item);
              let degree = e |> group by { k: x.k } aggregate { degree: count() };
              emit update entity Copy from degree identity copy(k: k) set { degree: degree };
            """) with { Witnesses = ["degree=n:"] },
        View("series and rollup", "Series, Rollup", Signal, """
              let m = match master (i: base.Item);
              let s = m |> series master i.hits within 3m |> rollup by { c: i.g } combine { v: sum(hits) };
              emit entity Signal from s identity signal(k: c) set { k: c, v: v };
            """, 0.25) with { Witnesses = ["d:w.view/prop:v"] },
        Hysteresis, RowParameters, Fill, FillAroundLoss, TimeToLive, RollupAll, RollupAny, TwoKeyRules, LiftedDependency,
        Rows("conflicting fragments", "EmitEntity, Def. 6.4", "", """
            entity E { k: Text; g?: Text; n?: Integer; }
            identity e for E version "1" { k: Text by exact; }
            """, tail: "    emit entity E from a identity e(k: k) set { k: k, g: g };\n    emit update entity E from b identity e(k: k) set { n: n, g: g };") with { Witnesses = ["prop:g=", "prop:n="] },
        View("threshold", "Series, Rollup, Threshold", Signal, """
              let m = match master (i: base.Item);
              let s = m |> series master i.hits within 30m |> rollup by { c: i.k } combine { v: sum(hits) };
              let st = s |> threshold v by { c: c } above 2 for 2 then "on" below 1 for 2 then "off" initial "off" as state;
              emit entity Signal from st identity signal(k: c) set { k: c, state: state };
            """, 0.3, Dense) with { Witnesses = ["state=t:on", "state=t:off"] },
    ];
}
