using System.Collections.Concurrent;
using Sattle.Core;
using Sattle.Language;
using static Sattle.Differential.Logs;

namespace Sattle.Differential;

/// <summary>A small graph space that exercises one operator kind (or a small composition), with the sources its logs are generated for. Its files are modules in `.sattle` text (types, row types, identities, the space) and one `.kql` file per projection.</summary>
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

    // ---- files ----

    /// <summary>The KQL text of the projection `projection` of `module`.</summary>
    public static SourceFile Kql(string module, string projection, string text) => new($"{module}.{projection}.kql", text);

    /// <summary>The space, in a module of its own: a space binds the KQL projections of the modules it imports.</summary>
    public static SourceFile Space(string imports, string body) => new("w.space.sattle", $"module w.space @ 1.0.0;\n{imports}\nspace s {{\n{body}\n}}\n");

    // ---- one ingestion module: rows in, the operator under test, a count per row out ----

    const string R = "rows R { k: Text; g: Text; n: Integer; d?: Decimal; t: Instant; }";
    const string Counted = """nodes Out on o with (rule = "observed") = x | summarize c = count(), s = sum(n) by k, g | project o = identity("out", k, g), k, g, c, s;""";
    const string OutType = """
        entity Out { k: Text; g: Text; c: Integer; s: Integer; }
        identity out for Out version "1" { k: Text by exact; g: Text by exact; }
        """;

    static World Rows(string name, string kind, string body, string types = OutType, double advanceShare = 0, IReadOnlyList<SourceSpec>? sources = null, string tail = Counted) => new(name, kind,
        [
            new SourceFile("w.sattle", $"module w @ 1.0.0;\n{types}\n{R}\n"),
            Kql("w", "ingest", $"graph ingest : w;\ninput a : R;\ninput b : R;\n{body}\n{tail}\n"),
            Space("import w @ 1.0.0 sha256:… as base;", """
                  source a adapter "sattle.authoring" rows base.R;
                  source b adapter "sattle.authoring" rows base.R;
                  master graph master = base.ingest(a: a, b: b);
                """),
        ], sources ?? [A, B], advanceShare);

    /// <summary>A group of the rows of `a` by `g`: `aggregates` is the summarize list, `columns` what the element takes from it (a property by its name, or `property = column`).</summary>
    static World Aggregate(string name, string aggregates, string properties, string columns) => Rows("group " + name, "Group", "", $$"""
        entity Agg { g: Text; {{properties}} }
        identity agg for Agg version "1" { g: Text by exact; }
        """, tail: $"""nodes Agg on o with (rule = "observed") = a | summarize {aggregates} by g | project o = identity("agg", g), g, {columns};""");

    const string MeterType = """
        entity Meter { k: Text; hits?: Integer telemetry delta sum every 60s; total?: Integer telemetry delta sum every 60s; }
        identity meter for Meter version "1" { k: Text by exact; }
        """;
    /// <summary>Rows in windows of their own time. The KQL form has one late policy: a late row revises its window.</summary>
    static readonly World Windowed = Rows("window late revise", "Window, Group over windows", "", MeterType, 0.25,
        tail: """nodes Meter on m with (rule = "observed") = a | summarize hits = count(), total = sum(n) by k, windowStart = bin(t, 60s) | project m = identity("meter", k), k, windowStart, hits, total;""");
    /// <summary>D3 (#101): sliding windows of 3 minutes that move by 60 s; a row counts in the three windows that hold it.</summary>
    static readonly World Hopped = Rows("hop 3m by 60s", "Window with a hop, Group over windows", "", MeterType, 0.25,
        tail: """nodes Meter on m with (rule = "observed") = a | summarize hits = count(), total = sum(n) by k, windowStart = hop(t, 3m, 60s) | project m = identity("meter", k), k, windowStart, hits, total;""");
    /// <summary>D3 (#101): a window over a window: the 60 s windows of the rows, summed in sliding windows of 3 minutes.</summary>
    static readonly World HopOverWindows = Rows("hop over windows", "Group over windows, Window with a hop over its windows", "", MeterType, 0.25,
        tail: """nodes Meter on m with (rule = "observed") = a | summarize hits = count(), total = sum(n) by k, windowStart = bin(t, 60s) | project k, at = windowStart, hits, total | summarize hits = sum(hits), total = sum(total) by k, windowStart = hop(at, 3m, 60s) | project m = identity("meter", k), k, windowStart, hits, total;""");
    /// <summary>D3 (#101): shift: the rows counted in the window 90 s after their own time, so a shifted row crosses window borders.</summary>
    static readonly World Shifted = Rows("shift into the next window", "Shift, Window, Group over windows", "", MeterType, 0.25,
        tail: """nodes Meter on m with (rule = "observed") = a | extend later = t + 90s, earlier = t - 30s | where later > earlier | summarize hits = count(), total = sum(n) by k, windowStart = bin(later, 60s) | project m = identity("meter", k), k, windowStart, hits, total;""");

    // ---- a master graph and a view over it ----

    public const string BaseModule = """
        module w.base @ 1.0.0;
        entity Item { k: Text; g: Text; n: Integer; hits?: Integer telemetry delta sum every 60s; }
        relationship Edge (Item) -> (Item);
        identity item for Item version "1" { k: Text by exact; }
        rows R { k: Text; g: Text; n: Integer; d?: Decimal; t: Instant; }
        rows EdgeRow { src: Text; dst: Text; }
        """;
    /// <summary>The ingestion of `BaseModule`: items from `a`, edges from `edges`, and the hits of an item per window of 60 seconds from `b`.</summary>
    static readonly SourceFile BaseIngest = Kql("w.base", "ingest", """
        graph ingest : w.base;
        input a : R;
        input b : R;
        input edges : EdgeRow;
        nodes Item on i with (rule = "observed") = a | project i = identity("item", k), k, g, n;
        edges Edge from s to d with (rule = "observed") = edges | project s = identity("item", src), d = identity("item", dst);
        extend nodes Item on i with (rule = "observed") = b | summarize hits = sum(n) by k, windowStart = bin(t, 60s) | project i = identity("item", k), windowStart, hits;
        """);

    /// <summary>Two items and hits in the last few windows, so consecutive windows of one key have values.</summary>
    static readonly IReadOnlyList<SourceSpec> Dense =
    [
        new("a", (r, now) => Row(("k", Text(r, "k", 2)), ("g", Value.Text("g0")), ("n", Value.Integer(1)), ("d", Value.Null), ("t", Value.Instant(now))), f => ((TextValue)f["k"]).S),
        new("b", (r, now) => Row(("k", Text(r, "k", 2)), ("g", Value.Text("g0")), ("n", Value.Integer(r.Next(5))), ("d", Value.Null), ("t", Value.Instant(now + r.Next(-6, 2) * 30 * Second)))),
        new("b", (r, now) => Row(("k", Text(r, "k", 2)), ("g", Value.Text("g0")), ("n", Value.Integer(r.Next(5))), ("d", Value.Null), ("t", Value.Instant(now + r.Next(-6, 2) * 30 * Second)))),
    ];

    /// <summary>The space of a view over `BaseModule`: the master and the graph `view` of the projection `v`.</summary>
    static readonly SourceFile ViewSpace = Space("import w.base @ 1.0.0 sha256:… as base;\nimport w.view @ 1.0.0 sha256:… as view;", """
          source a adapter "sattle.authoring" rows base.R;
          source b adapter "sattle.authoring" rows base.R;
          source edges adapter "sattle.authoring" rows base.EdgeRow;
          master graph master = base.ingest(a: a, b: b, edges: edges);
          graph view = view.v(master: master);
        """);

    /// <summary>A view `v` over the master of `BaseModule`: `types` are the view's own, `body` the statements of its KQL text.</summary>
    static World View(string name, string kind, string types, string body, double advanceShare = 0, IReadOnlyList<SourceSpec>? sources = null) => new(name, kind,
        [
            new SourceFile("w.base.sattle", BaseModule), BaseIngest,
            new SourceFile("w.view.sattle", $"module w.view @ 1.0.0;\nimport w.base @ 1.0.0 sha256:… as base;\n{types}\n"),
            Kql("w.view", "v", $"graph v : w.view;\ninput master : base;\n{body}\n"),
            ViewSpace,
        ], sources ?? [Keyed("a"), B, Edges], advanceShare);

    const string Signal = """
        entity Signal { k: Text; state?: Text; v?: Integer telemetry delta sum every 60s; }
        identity signal for Signal version "1" { k: Text by exact; }
        """;

    /// <summary>The items of the master with the windows of their hits: one row per item and window. `key` is what the row is named by, `telemetry` the call that reads the windows.</summary>
    static string Hits(string key, string telemetry, string columns = "windowStart, property, value") => $"""
        graph("master") | graph-match (i) where labels(i) has "Item" project i = i.id, c = i.{key}, n = i.n
            | join kind=inner ({telemetry} | where property == "hits" | project i = id, {columns}) on i
        """;
    /// <summary>The state of a signal from rows with a value per window: on after `enterAfter` windows at or above `above`, off after `exitAfter` windows at or below `below`.</summary>
    static string State(string above, string enterAfter, string below, string exitAfter, string value = "v", string states = "\"on\", \"off\", \"off\"", string more = "") =>
        $"    | project c, windowStart, value = {value}, above = {above}, enterAfter = {enterAfter}, below = {below}, exitAfter = {exitAfter}{more}\n    | invoke hysteresis({states})\n    | project s = identity(\"signal\", c), k = c, state;";

    // ---- pods with two keys: a link merges two identities of one pod ----

    const string LinkedTypes = """
        module w @ 1.0.0;
        entity Pod { uid?: Text; name?: Text; g?: Text; }
        relationship Calls (Pod) -> (Pod);
        identity podUid for Pod version "1" { uid: Text by exact; }
        identity podName for Pod version "1" { name: Text by lowerInvariant; }
        equivalence uidIsName between podUid, podName assert threshold 1.0;
        rows PodRow { uid: Text; name: Text; g: Text; }
        rows FlowRow { src: Text; dst: Text; }
        """;
    const string LinkedIngest = """
        graph ingest : w;
        input pods : PodRow;
        input flows : FlowRow;
        nodes Pod on p with (rule = "observed") = pods | project p = identity("podUid", uid), uid, g;
        link uidIsName from byUid to byName with (confidence = 1.0, rule = "observed") = pods | project byUid = identity("podUid", uid), byName = identity("podName", name);
        nodes Pod on p with (rule = "observed") = flows | project name = src | distinct * | project p = identity("podName", name), name = tolower(name);
        edges Calls from s to d with (rule = "observed") = flows | project s = identity("podName", src), d = identity("podName", dst);
        """;
    const string LinkedSources = """
          source pods adapter "sattle.authoring" rows base.PodRow;
          source flows adapter "sattle.authoring" rows base.FlowRow;
        """;
    /// <summary>The master of the linked pods: pods by their uid, pods named by flows, the calls between them, and the link of a pod's uid to its name.</summary>
    static readonly IReadOnlyList<SourceFile> Linked = [new SourceFile("w.sattle", LinkedTypes), Kql("w", "ingest", LinkedIngest)];
    static readonly SourceFile LinkedSpace = Space("import w @ 1.0.0 sha256:… as base;", LinkedSources + "  master graph master = base.ingest(pods: pods, flows: flows);");

    /// <summary>A view `v` over the master of the linked pods.</summary>
    public static World LinkedWith(string name, string kind, string types, string body) => new(name, kind,
        [
            .. Linked,
            new SourceFile("w.view.sattle", $"module w.view @ 1.0.0;\nimport w @ 1.0.0 sha256:… as base;\n{types}\n"),
            Kql("w.view", "v", $"graph v : w.view;\ninput master : base;\n{body}\n"),
            Space("import w @ 1.0.0 sha256:… as base;\nimport w.view @ 1.0.0 sha256:… as view;", LinkedSources + "  master graph master = base.ingest(pods: pods, flows: flows);\n  graph view = view.v(master: master);"),
        ], [Pods, Flows]);

    static readonly string[] PodNames = ["Web", "api", "DB", "cache"];
    static readonly SourceSpec Pods = new("pods", (r, _) => { var i = r.Next(4); return Row(("uid", Value.Text("u" + i)), ("name", Value.Text(PodNames[i])), ("g", Text(r, "g", 2))); }, f => ((TextValue)f["uid"]).S);
    static readonly SourceSpec Flows = new("flows", (r, _) => Row(("src", Value.Text(PodNames[r.Next(4)].ToLowerInvariant())), ("dst", Value.Text(PodNames[r.Next(4)]))));

    /// <summary>T-B3: a view groups the calls of each pod by the pod it matches. A link in the master merges two identities of one pod; the view's group re-keys through the master's patch, so its count is that of one group over both keys (Theorem 6.1 under identity, the stated way to group by an identity under rule IdOf-in-sinks).</summary>
    public static readonly World LinkedView = LinkedWith("a view grouped by a pod that a link merges", "Link, Match, Group, EmitEntity in a view", "entity Caller { calls: Integer; }", """
        nodes Caller on p = graph("master") | graph-match (p)-[c]->(q) where labels(c) has "Calls" project p = p.id | summarize calls = count() by p;
        """) with { Witnesses = ["d:w.view/prop:calls="] };

    /// <summary>A view that names everything by the pod it matches, over the same master: every pod counts zero and every call one (a union, one side of which carries the name read from the pod), a group by the pod, two joins on the pod, and sinks whose ids and ends are the pod. A link merges two identities of one pod; every step re-keys through the master's patch.</summary>
    public static readonly World LinkedUnionJoin = LinkedWith("a view that unions, groups and joins by a pod that a link merges", "Link, Match, Union, Group, Join, EmitEntity, EmitRelationship in a view", """
        entity Node { name?: Text; calls?: Integer; state?: Text; }
        relationship Talks (Node) -> (Node) { state?: Text; }
        """, """
        let pods = graph("master") | graph-match (p) where labels(p) has "Pod" project p = p.id, name = p.name;
        let status = union (pods | project p, n = 0), (graph("master") | graph-match (p)-[c]->(q) where labels(c) has "Calls" project p = p.id, n = 1)
            | summarize c = sum(n) by p | extend state = iff(c > 0, "busy", "idle");
        nodes Node on p = pods | project p, name;
        extend nodes Node on p = status | project p, calls = c, state;
        edges Talks from a to b = graph("master") | graph-match (a)-[k]->(b) where labels(k) has "Calls" and a.id != b.id project a = a.id, b = b.id
            | join kind=inner (status | project ps = p, ss = state) on $left.a == $right.ps
            | join kind=inner (status | project pt = p, ts = state) on $left.b == $right.pt
            | project a, b, state = iff(ss == "busy" and ts == "busy", "busy", "idle");
        """) with { Witnesses = ["d:w.view/prop:calls=", "d:w.view/prop:name=", "rel "] };

    public static readonly World LinkedLoads = LinkedLoadsUnder("delta sum");

    /// <summary>A view reads the load windows of the pods through a series. A link merges two identities of one pod and moves their windows; the view has the survivor's window in the step of the merge (code review 2, defect 22).</summary>
    public static readonly World LinkedSeries = new("a view's series over a pod that a link merges", "Link, Series, Rollup in a view",
        [
            .. LinkedLoads.Files.Take(2),        // the types and the ingestion, without the space
            new SourceFile("w.view.sattle", """
                module w.view @ 1.0.0;
                import w @ 1.0.0 sha256:… as base;
                entity Seen { load?: Integer telemetry delta sum every 60s; }
                """),
            Kql("w.view", "v", """
                graph v : w.view;
                input master : base;
                nodes Seen on p = graph("master") | graph-match (p) where labels(p) has "Pod" project p = p.id
                    | join kind=inner (telemetry("master", 30m) | where property == "load" | project p = id, windowStart, property, value) on p
                    | summarize load = sum(value) by p, windowStart;
                """),
            Space("import w @ 1.0.0 sha256:… as base;\nimport w.view @ 1.0.0 sha256:… as view;", """
                  source pods adapter "sattle.authoring" rows base.PodRow;
                  source flows adapter "sattle.authoring" rows base.FlowRow;
                  source loads adapter "sattle.authoring" rows base.LoadRow;
                  master graph master = base.ingest(pods: pods, flows: flows, loads: loads);
                  graph view = view.v(master: master);
                """),
        ], LinkedLoads.Sources, LinkedLoads.AdvanceShare) { Witnesses = ["d:w.view/prop:load "] };

    /// <summary>A view names its elements by the element it matches, so it follows a merge of the master (a key of a linked domain is refused there, `IdentityLinked`).</summary>
    public static readonly World LinkedNamed = LinkedWith("a view named by the element of a linked domain", "Link, Match, EmitEntity in a view", "entity Tag { name?: Text; }",
        """nodes Tag on t = graph("master") | graph-match (p) where labels(p) has "Pod" project p = p.id, name = p.name | project t = p, name;""") with { Witnesses = ["d:w.view/prop:name="] };

    /// <summary>Law 7.5 item 3, "rows after the link" (D37): the load of a pod aggregated per window once by its uid and once by its name, both writing one telemetry property. A link merges the two identities before, between or after the rows; the window is what both groups give together, as if every row had been written under the survivor.
    /// The load property is declared `{kind} telemetry {rule} every 60s` (a temporality and a combine rule); `aggregate` is the aggregate of each group in KQL: `sum(n)`, `avg(n)`, or `arg_max(t, n)` for the n of the latest row.</summary>
    public static World LinkedLoadsUnder(string rule, string aggregate = "sum(n)", string kind = "Integer")
    {
        var (summarize, value) = aggregate.StartsWith("arg_max(", StringComparison.Ordinal) ? (aggregate, "n") : ("c = " + aggregate, "c");
        return new(rule == "delta sum" && aggregate == "sum(n)" ? "two groups by two keys of one class, one telemetry property" : $"two groups by two keys of one class, one telemetry property ({rule}, {aggregate})", "Link, Window, Group, EmitEntity telemetry",
        [
            new SourceFile("w.sattle", LinkedTypes.Replace("entity Pod { uid?: Text; name?: Text; g?: Text; }", $"entity Pod {{ uid?: Text; name?: Text; g?: Text; load?: {kind} telemetry {rule} every 60s; }}") + "rows LoadRow { uid?: Text; name?: Text; n: Integer; t: Instant; }\n"),
            Kql("w", "ingest", LinkedIngest.Replace("input flows : FlowRow;", "input flows : FlowRow;\ninput loads : LoadRow;") + "\n" + $"""
                extend nodes Pod on p with (rule = "observed") = loads | where isnotempty(uid) | summarize {summarize} by uid, windowStart = bin(t, 60s) | project p = identity("podUid", uid), windowStart, load = {value};
                extend nodes Pod on p with (rule = "observed") = loads | where isnotempty(name) | summarize {summarize} by name, windowStart = bin(t, 60s) | project p = identity("podName", name), windowStart, load = {value};
                """),
            Space("import w @ 1.0.0 sha256:… as base;", LinkedSources + "  source loads adapter \"sattle.authoring\" rows base.LoadRow;\n  master graph master = base.ingest(pods: pods, flows: flows, loads: loads);"),
        ],
        [Pods, Flows, new("loads", (r, now) => { var i = r.Next(4); var byUid = r.Next(2) == 0; return Row(("uid", byUid ? Value.Text("u" + i) : Value.Null), ("name", byUid ? Value.Null : Value.Text(PodNames[i])), ("n", Value.Integer(r.Next(1, 4))), ("t", Near(r, now))); })], AdvanceShare: 0.2)
        { Witnesses = ["prop:load"] };
    }

    // ---- worlds outside `All`: their inputs are written out, not generated ----

    const string OneEntity = """
        entity E { k: Text; d?: Decimal; }
        identity e for E version "1" { k: Text by exact; }
        """;
    public static readonly World FilteredEmit = Rows("filtered emit", "Filter, EmitEntity", "let x = a | where n > 2;", OneEntity, tail: """nodes E on e with (rule = "observed") = x | project e = identity("e", k), k;""");
    public static readonly World OptionalProperty = Rows("optional property", "EmitEntity", "", OneEntity, tail: """nodes E on e with (rule = "observed") = a | project e = identity("e", k), k, d;""") with { Witnesses = ["prop:d="] };
    /// <summary>A threshold whose look-back (3 minutes) is shorter than the logs: the state has to outlive the windows that set it.</summary>
    public static readonly World Hysteresis = View("threshold over a short look-back", "Series, Rollup, Threshold", Signal,
        "nodes Signal on s = " + Hits("k", "telemetry(\"master\", 3m)") + "\n    | summarize v = sum(value) by c, windowStart\n" + State("3.0", "2", "1.0", "2"), 0.3, Dense) with { Witnesses = ["state=t:on", "state=t:off"] };
    /// <summary>T-B4 (premise B4 of Theorem 6.2): a threshold over a roll-up by group, whose members change group (a keyed item row is replaced with another group, so the item leaves one key and joins another), with windows that leave the retained range of three minutes (the look-back, the least the engine accepts). Every advance is its own step in the engine and in the reference, so an advance and a structure change never share one; the harness cuts the rows between them every way.</summary>
    public static readonly World MembersLeave = View("threshold over a roll-up whose members leave", "Series, Rollup, Threshold, structure change", Signal,
        "nodes Signal on s = " + Hits("g", "telemetry(\"master\", 3m)") + "\n    | summarize v = sum(value) by c, windowStart\n" + State("2.0", "1", "1.0", "2"), 0.3, [
            new("a", (r, now) => Row(("k", Text(r, "k", 3)), ("g", Text(r, "g", 2)), ("n", Value.Integer(1)), ("d", Value.Null), ("t", Value.Instant(now))), f => ((TextValue)f["k"]).S),
            Dense[1], Dense[2],
        ]) with { RetentionNs = 3 * Minute, Witnesses = ["state=t:on", "state=t:off"] };
    /// <summary>Def. 7.15 and 7.16 on one element: `hits` are filled with 0 where the source of the hits has reported and the item existed, so a covered silence ends a state; an item without a record for three minutes is in no data, which the threshold gives as "silent", and data that return end it.</summary>
    static World Silence(string name, string kind, string rows, bool rolledUp = false) => View(name, kind, Signal,
        "nodes Signal on s = " + rows + "\n" + State("3.0", "1", "1.0", "2", rolledUp ? "v" : "value", "\"on\", \"off\", \"off\", \"silent\"", rolledUp ? "" : ", noData"), 0.3, Dense) with { Reporting = ["b"], ProgressShare = 0.15, Witnesses = ["state=t:on", "state=t:off", "state=t:silent"] } is var w
        ? w with { Files = [.. w.Files.Select(f => f with { Text = f.Text.Replace("source b adapter \"sattle.authoring\"", "source b adapter \"harness.reporting\"", StringComparison.Ordinal) })] } : null!;       // the source of the hits reports: an authored source would not, and its fill is refused
    const string WithNoData = "windowStart, property, value, noData";
    public static readonly World Fill = Silence("fill and no data", "Series with fill and ttl, Map, Threshold", Hits("k", "telemetry_filled(\"master\", 5m, 3m, 0.0)", WithNoData));
    /// <summary>Def. 7.14: a window in which the source lost events is not covered, so it holds no fill; a loss mark that arrives after the window was filled takes the fill back.</summary>
    public static readonly World FillAroundLoss = Silence("fill around a loss", "Series with fill and ttl, loss marks, Map, Threshold", Hits("k", "telemetry_filled(\"master\", 5m, 3m, 0.0)", WithNoData)) with { LossShare = 0.12 };
    /// <summary>#111: fill and no data over hop windows: the hits of an item in windows of 3 minutes every 60 s, read with a fill and a time to live, with gaps and loss marks. A filled window is one the source covered to its end, so a fill is neither early nor doubled.</summary>
    public static readonly World FillOverHop = Fill with
    {
        Name = "fill and no data over hop windows", Kind = "Window with a hop, Series with fill and ttl, loss marks, Threshold", LossShare = 0.12,
        Files = [.. Fill.Files.Select(f => f == BaseIngest ? f with { Text = f.Text.Replace("bin(t, 60s)", "hop(t, 3m, 60s)", StringComparison.Ordinal) } : f)],
    };
    public static readonly World TimeToLive = Silence("no data without a fill", "Series with ttl, Map, Threshold", Hits("k", "telemetry_filled(\"master\", 5m, 2m, real(null))", WithNoData));
    public static readonly World RollupAll = Silence("roll-up in no data when all members are", "Series with fill and ttl, Rollup, Threshold",
        Hits("g", "telemetry_filled(\"master\", 5m, 2m, 0.0)", WithNoData) + "\n    | summarize v = sum(value) by c, windowStart", rolledUp: true);

    /// <summary>A threshold whose `above` and `enterAfter` come from the row (the item's `n`), with a constant `below` of 1: an item with n below 2 gives parameters that are no state machine, and a revised item changes the parameters of every window of its key.</summary>
    public static readonly World RowParameters = View("threshold with parameters of the row", "Series, Map, Threshold", Signal,
        "nodes Signal on s = " + Hits("k", "telemetry(\"master\", 3m)") + "\n" + State("toreal(n)", "n - 1", "1.0", "2", value: "value"), 0.3,
        [
            new("a", (r, now) => Row(("k", Text(r, "k", 2)), ("g", Value.Text("g0")), ("n", Value.Integer(r.Next(4))), ("d", Value.Null), ("t", Value.Instant(now))), f => ((TextValue)f["k"]).S),
            .. Dense.Skip(1),
        ]) with { Witnesses = ["state=t:on", "state=t:off"] };
    public static readonly World SharedWindow = View("shared window", "Series, Rollup, EmitEntity", """
        entity Signal { k: Text; v?: Integer telemetry delta sum every 60s; }
        identity signal for Signal version "1" { k: Text by lowerInvariant; }
        """, "nodes Signal on s = " + Hits("k", "telemetry(\"master\", 30m)") + "\n    | summarize v = sum(value) by c, windowStart\n    | project s = identity(\"signal\", c), k = tolower(c), windowStart, v;", 0.3,
        [   // items whose keys differ only in the case of a letter: two items, one signal, so two rows support one window of the signal
            new("a", (r, now) => Row(("k", Pick(r, Value.Text("k0"), Value.Text("K0"), Value.Text("k1"), Value.Text("K1"))), ("g", Value.Text("g0")), ("n", Value.Integer(1)), ("d", Value.Null), ("t", Value.Instant(now))), f => ((TextValue)f["k"]).S),
            new("b", (r, now) => Row(("k", Pick(r, Value.Text("k0"), Value.Text("K0"), Value.Text("k1"), Value.Text("K1"))), ("g", Value.Text("g0")), ("n", Value.Integer(r.Next(5))), ("d", Value.Null), ("t", Value.Instant(now + r.Next(-6, 2) * 30 * Second)))),
        ]) with { Witnesses = ["d:w.view/prop:v "] };

    const string GroupsTypes = """
        module w @ 1.0.0;
        entity Item { k: Text label; g: Text; }
        entity Group { name: Text label; }
        relationship Edge (Item) -> (Item) kind communication;
        relationship Belongs (Item) -> (Group) kind composition;
        relationship Talks (Group) -> (Group) kind communication;
        relationship Needs (Group) -> (Group) kind dependency;
        relationship Seen (Item) -> (Group);
        identity item for Item version "1" { k: Text by exact; }
        identity group for Group version "1" { name: Text by exact; }
        rows ItemRow { k: Text; g: Text; }
        rows EdgeRow { src: Text; dst: Text; }
        """;
    /// <summary>The edges between items as edges between the groups the items belong to, in the graph `graph`: each end of an edge is joined with the group of its item, and an edge inside one group is left out.</summary>
    static string Lifted(string graph) => $"""
        let images = graph("{graph}") | graph-match (m)-[bl]->(i) where labels(bl) has "Belongs" project m = m.id, i = i.id;
        edges Talks from sg to tg = graph("master") | graph-match (s)-[e]->(t) where labels(e) has "Edge" project s = s.id, t = t.id
            | join kind=inner (images | project sm = m, sg = i) on $left.s == $right.sm
            | join kind=inner (images | project tm = m, tg = i) on $left.t == $right.tm
            | where sg != tg
            | project sg, tg;
        """;
    static readonly IReadOnlyList<SourceSpec> ItemsAndEdges = [new("items", (r, _) => Row(("k", Text(r, "k", 5)), ("g", Text(r, "g", 3))), f => ((TextValue)f["k"]).S), BagEdges];
    const string ItemSources = """
          source items adapter "sattle.authoring" rows base.ItemRow;
          source edges adapter "sattle.authoring" rows base.EdgeRow;
          master graph master = base.ingest(items: items, edges: edges);
        """;

    /// <summary>The shapes of the workload model: item edges lifted to the groups the items belong to (communication), and a dependency between groups derived from the same edges by joins with a table of the groups that count and with the groups themselves (Match, Values, Join, Distinct, EmitRelationship).</summary>
    public static readonly World LiftedDependency = new("lifted and derived dependency", "Match, Values, Join, Distinct, EmitRelationship",
        [
            new SourceFile("w.sattle", GroupsTypes),
            Kql("w", "ingest", """
                graph ingest : w;
                input items : ItemRow;
                input edges : EdgeRow;
                nodes Item on i with (rule = "observed") = items | project i = identity("item", k), k, g;
                nodes Group on grp with (rule = "observed") = items | project grp = identity("group", g), name = g;
                edges Belongs from i to grp with (rule = "observed") = items | project i = identity("item", k), grp = identity("group", g);
                edges Edge from s to d with (rule = "observed") = edges | project s = identity("item", src), d = identity("item", dst);
                """),
            Kql("w", "groups", $"""
                graph groups : w;
                input master : w;
                let everyGroup = graph("master") | graph-match (g) where labels(g) has "Group" project g = g.id, name = g.name;
                nodes Group on g = everyGroup | project g, name;
                {Lifted("master")}
                let reached = graph("master") | graph-match (a)-[e]->(b), (a)-[ba]->(ga), (b)-[bb]->(gb) where labels(e) has "Edge" and labels(ba) has "Belongs" and labels(bb) has "Belongs" project gfrom = ga.name, gto = gb.name;
                let counting = datatable(counted:string) ["g0", "g1"];
                edges Needs from s to t = reached
                    | join kind=inner (counting) on $left.gto == $right.counted
                    | where gfrom != gto | project gfrom, gto | distinct *
                    | join kind=inner (everyGroup | project s = g, sn = name) on $left.gfrom == $right.sn
                    | join kind=inner (everyGroup | project t = g, tn = name) on $left.gto == $right.tn
                    | project s, t;
                """),
            Space("import w @ 1.0.0 sha256:… as base;", ItemSources + "  graph view = base.groups(master: master);"),
        ], ItemsAndEdges) { Witnesses = ["Talks", "Needs"] };

    /// <summary>References: the graph `groups` holds the groups and which item belongs to which, and no item; the items are entities of the master. A pattern over `groups` binds the item end of a `Belongs` by its id, and the master's edges are mapped through those `Belongs` by two joins, whose match over `groups` binds the same way.</summary>
    public static readonly World References = new("ends of relationships that are entities of another graph", "Match and Join over references",
        [
            new SourceFile("w.sattle", GroupsTypes),
            Kql("w", "ingest", """
                graph ingest : w;
                input items : ItemRow;
                input edges : EdgeRow;
                nodes Item on i with (rule = "observed") = items | project i = identity("item", k), k, g;
                edges Edge from s to d with (rule = "observed") = edges | project s = identity("item", src), d = identity("item", dst);
                """),
            Kql("w", "membership", """
                graph membership : w;
                input master : w;
                let items = graph("master") | graph-match (i) where labels(i) has "Item" project i = i.id, g = i.g;
                nodes Group on grp = items | project g | distinct * | project grp = identity("group", g), name = g;
                edges Belongs from i to grp = items | project i, grp = identity("group", g);
                """),
            Kql("w", "talk", $"""
                graph talk : w;
                input master : w;
                input groups : w;
                nodes Group on g = graph("groups") | graph-match (g) where labels(g) has "Group" project g = g.id, name = g.name;
                {Lifted("groups")}
                edges Seen from i to g = graph("groups") | graph-match (i)-[bl]->(g) where labels(bl) has "Belongs" and g.name != "g0" project i = i.id, g = g.id;
                """),
            Space("import w @ 1.0.0 sha256:… as base;", ItemSources + "  graph groups = base.membership(master: master);\n  graph view = base.talk(master: master, groups: groups);"),
        ], ItemsAndEdges) { Witnesses = ["Talks", "Seen"] };

    public static readonly World TwoKeyRules = new("two key rules", "EmitRelationship, Def. 6.4",
        [
            new SourceFile("w.sattle", """
                module w @ 1.0.0;
                entity Item { k?: Text; }
                relationship Edge (Item) -> (Item);
                identity item for Item version "1" { k: Text by exact; }
                rows EdgeRow { src: Text; dst: Text; }
                """),
            Kql("w", "ingest", """
                graph ingest : w;
                input edges : EdgeRow;
                input more : EdgeRow;
                edges Edge from s to d with (rule = "observed", key = "first") = edges | project s = identity("item", src), d = identity("item", dst);
                edges Edge from s to d with (rule = "observed", key = "second") = more | project s = identity("item", src), d = identity("item", dst);
                """),
            Space("import w @ 1.0.0 sha256:… as base;", """
                  source edges adapter "sattle.authoring" rows base.EdgeRow;
                  source more adapter "sattle.authoring" rows base.EdgeRow;
                  master graph master = base.ingest(edges: edges, more: more);
                """),
        ], [BagEdges, BagEdges with { Name = "more" }]) { Witnesses = ["rel "] };

    /// <summary>The share rule of acme.workload (a deployment by the share of its pods, no data ranked between healthy and degraded), with the statements of the model: pods are rows of `a` (key `k`, deployment `g`, severity `n` capped at 3), retracted and changed by the log.</summary>
    public static readonly World ShareRule = Rows("share rule of a deployment", "Map, Group, Map, EmitEntity", """
        let byShare = a | project dk = g, pk2 = k, own = iff(n > 3, 3, n)
            | summarize n = count(), bad = sum(iff(own >= 3, 1, 0)), weak = sum(iff(own >= 2, 1, 0)), silent = sum(iff(own >= 1, 1, 0)) by dk
            | project k = dk, own = iff(bad * 2 >= n, 3, iff(weak * 2 >= n, 2, iff(silent * 2 >= n, 1, 0)));
        """, """
        entity Deployment { k: Text; own: Integer; }
        identity deployment for Deployment version "1" { k: Text by exact; }
        """, tail: """nodes Deployment on dep with (rule = "observed") = byShare | project dep = identity("deployment", k), k, own;""") with { Witnesses = ["prop:own=n:0", "prop:own=n:1", "prop:own=n:2", "prop:own=n:3"] };

    /// <summary>#112: a world whose code numbers depend on how the log is cut. A `where` drops items, so the texts only they carry (their tag, a key or group seen first in a dropped row) are freed at the end of their step, and texts that come later take the freed numbers, the smallest first. A traverse walks its starts in code order: an output that depended on the numbers would differ between cuts and from the reference.</summary>
    public static readonly World TraverseAfterWhere = new World("traverse over items a where thins", "Filter, Map, Traverse",
        [
            new SourceFile("w.base.sattle", BaseModule),
            Kql("w.base", "ingest", """
                graph ingest : w.base;
                input a : R;
                input b : R;
                input edges : EdgeRow;
                nodes Item on i with (rule = "observed") = a | extend tag = strcat(k, "-", g) | where n > 2 | project i = identity("item", k), k, g, n;
                edges Edge from s to d with (rule = "observed") = edges | project s = identity("item", src), d = identity("item", dst);
                """),
            new SourceFile("w.view.sattle", """
                module w.view @ 1.0.0;
                import w.base @ 1.0.0 sha256:… as base;
                entity Reach { a: Text; b: Text; g: Text; }
                identity reach for Reach version "1" { a: Text by exact; b: Text by exact; }
                """),
            Kql("w.view", "v", """
                graph v : w.view;
                input master : base;
                nodes Reach on r = graph("master") | graph-match (i)-[p*0..2]->(d) where labels(i) has "Item" and all(p, labels(p) has "Edge") project a = i.k, b = d.k, g = d.g
                    | project r = identity("reach", a, b), a, b, g;
                """),
            ViewSpace,
        ], [Keyed("a"), Edges]) with { Witnesses = ["Reach"] };

    public static readonly IReadOnlyList<World> All =
    [
        TraverseAfterWhere,
        Rows("filter", "Filter", """let x = a | where n > 2 and g != "g1";"""),
        Rows("map", "Map", """let x = a | project k = strcat(k, "/", g), g = tolower(g), n = n * 2 + 1;"""),
        Rows("union", "Union", "let x = union a, b;"),
        Rows("distinct", "Distinct", "let x = union a, b | distinct *;"),
        Rows("distinct over a decimal", "Distinct", "let x = union a, b | project k = g, g, n = 1, d | distinct *;"),
        Rows("join", "Join", """
            let x = a | project k, g, n
                | join kind=inner (b | project rk = k, rg = g, rn = n) on $left.g == $right.rg
                | where n <= rn
                | project k = strcat(k, "+", rk), g, n = n + rn;
            """) with { Witnesses = ["+k"] },
        Rows("join on a decimal key", "Join", """
            let x = a | project k, g, n, d = coalesce(d, 0.0)
                | join kind=inner (b | project rk = k, rd = coalesce(d, 0.0)) on $left.d == $right.rd
                | project k = strcat(k, "+", rk), g, n;
            """),
        Rows("values", "Values", """
            let labels = datatable(label:string, tk:string) ["L1", "g1", "L2", "g2"];
            let x = a | join kind=inner (labels) on $left.g == $right.tk | project k, g = label, n;
            """) with { Witnesses = ["g=t:L1"] },
        Rows("retain", "Retain", "let x = a | where t > ago(2m);", advanceShare: 0.25),
        Aggregate("count", "v = count()", "v: Integer;", "v"),
        Aggregate("countDistinct", "v = count_distinct(k)", "v: Integer;", "v"),
        Aggregate("sum", "v = sum(n), w = sum(d)", "v: Integer; w?: Decimal;", "v, w"),
        Aggregate("min and max", "lo = min(n), hi = max(n), top = max(d)", "lo?: Integer; hi?: Integer; top?: Decimal;", "lo, hi, top") with { Witnesses = ["hi=n:", "top=n:"] },
        Aggregate("mean", "v = avg(n), w = avg(d)", "v?: Decimal; w?: Decimal;", "v, w"),
        Aggregate("last of one row", "arg_max(n, k, d)", "v?: Text; w?: Decimal; x?: Integer;", "v = k, w = d, x = n") with { Witnesses = ["v=t:k", "prop:x=n:"] },
        Aggregate("countDistinct over a decimal", "v = count_distinct(d)", "v: Integer;", "v"),
        Rows("group by a decimal key", "Group", "let x = a | project k = g, g, n, d = coalesce(d, 0.0) | summarize n = sum(n) by k, g, d | project k, g, n;"),
        Aggregate("count with countDistinct", "c = count(), v = count_distinct(k)", "c: Integer; v: Integer;", "c, v"),
        Aggregate("extremes with countDistinct", "lo = min(n), v = count_distinct(k), hi = max(n)", "lo?: Integer; v: Integer; hi?: Integer;", "lo, v, hi") with { Witnesses = ["lo=n:", "hi=n:"] },
        Aggregate("countif", "v = countif(n > 2), total = count()", "v: Integer; total: Integer;", "v, total"),
        Windowed with { Witnesses = ["window "] },
        Windowed with { Name = "window late revise, retention 2 minutes", Kind = "Window, telemetry retention", RetentionNs = 2 * Minute, Witnesses = ["window "] },
        Hopped with { Witnesses = ["window "] },
        HopOverWindows with { Witnesses = ["window "] },
        Shifted with { Witnesses = ["window "] },
        new World("link", "Link, EmitRelationship", [.. Linked, LinkedSpace], [Pods, Flows]) with { Witnesses = ["prop:name=t:web,d:w/prop:uid=t:u0", "rel "] },
        LinkedLoads,
        LinkedSeries,
        LinkedView,
        LinkedUnionJoin,
        LinkedNamed,
        References,
        ShareRule,
        MembersLeave,
        View("match nodes", "Match, EmitEntity", """
            entity Copy { k: Text; g: Text; n: Integer; }
            identity copy for Copy version "1" { k: Text by exact; }
            """, """
            nodes Copy on c = graph("master") | graph-match (i) where labels(i) has "Item" project k = i.k, g = i.g, n = i.n | project c = identity("copy", k), k, g, n;
            """),
        View("match edges", "Match", """
            entity Pair { a: Text; b: Text; g: Text; }
            identity pair for Pair version "1" { a: Text by exact; b: Text by exact; }
            """, """
            nodes Pair on p = graph("master") | graph-match (x)-[e]->(y) where labels(e) has "Edge" project a = x.k, b = y.k, g = y.g | project p = identity("pair", a, b), a, b, g;
            """) with { Witnesses = ["Pair"] },
        View("traverse", "Traverse", """
            entity Reach { a: Text; b: Text; g: Text; }
            identity reach for Reach version "1" { a: Text by exact; b: Text by exact; }
            """, """
            nodes Reach on r = graph("master") | graph-match (i)-[p*0..2]->(d) where labels(i) has "Item" and all(p, labels(p) has "Edge") project a = i.k, b = d.k, g = d.g
                | project r = identity("reach", a, b), a, b, g;
            """) with { Witnesses = ["Reach"] },
        View("traverse either way", "Traverse", """
            entity Reach { a: Text; b: Text; g: Text; }
            identity reach for Reach version "1" { a: Text by exact; b: Text by exact; }
            """, """
            nodes Reach on r = graph("master") | graph-match (i)-[p*0..2]-(d) where labels(i) has "Item" and all(p, labels(p) has "Edge") project a = i.k, b = d.k, g = d.g
                | project r = identity("reach", a, b), a, b, g;
            """) with { Witnesses = ["Reach"] },
        View("emit update", "EmitEntity update", """
            entity Copy { k: Text; g?: Text; degree?: Integer; }
            identity copy for Copy version "1" { k: Text by exact; }
            """, """
            nodes Copy on c = graph("master") | graph-match (i) where labels(i) has "Item" project k = i.k | project c = identity("copy", k), k;
            extend nodes Copy on c = graph("master") | graph-match (x)-[r]->(y) where labels(r) has "Edge" project k = x.k | summarize degree = count() by k | project c = identity("copy", k), degree;
            """) with { Witnesses = ["degree=n:"] },
        View("series and rollup", "Series, Rollup", Signal,
            "nodes Signal on s = " + Hits("g", "telemetry(\"master\", 3m)") + "\n    | summarize v = sum(value) by c, windowStart\n    | project s = identity(\"signal\", c), k = c, windowStart, v;", 0.25) with { Witnesses = ["d:w.view/prop:v"] },
        Hysteresis, RowParameters, SharedWindow, Fill, FillAroundLoss, FillOverHop, TimeToLive, RollupAll, TwoKeyRules, LiftedDependency, OptionalProperty,
        Rows("conflicting fragments", "EmitEntity, Def. 6.4", "", """
            entity E { k: Text; g?: Text; n?: Integer; }
            identity e for E version "1" { k: Text by exact; }
            """, tail: """
            nodes E on e with (rule = "observed") = a | project e = identity("e", k), k, g;
            extend nodes E on e with (rule = "observed") = b | project e = identity("e", k), n, g;
            """) with { Witnesses = ["prop:g=", "prop:n="] },
        View("threshold", "Series, Rollup, Threshold", Signal,
            "nodes Signal on s = " + Hits("k", "telemetry(\"master\", 30m)") + "\n    | summarize v = sum(value) by c, windowStart\n" + State("2.0", "2", "1.0", "2"), 0.3, Dense) with { Witnesses = ["state=t:on", "state=t:off"] },
    ];
}
